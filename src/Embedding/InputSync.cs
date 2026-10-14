using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MultiHost;

/// <summary>一条待广播的 Win32 输入消息（鼠标左键），lParam 按消息类型编码。</summary>
public readonly record struct SyncedInput(uint Msg, IntPtr WParam, IntPtr LParam);

/// <summary>
/// 输入同步：用全局低级鼠标钩子（WH_MOUSE_LL）拦截鼠标左键点击。
/// 钩子安装在主程序 UI 线程，回调随该线程消息循环执行。
/// 同步开启且左键按下落在某个内嵌窗口（WindowFromPoint 命中）时，
/// 吞掉原始事件（回调返回非 0）并广播到所有已内嵌窗口的相同相对位置；
/// 否则 CallNextHookEx 放行，完全不干预。
/// 鼠标用 SendInput 物理注入（SDL/bgfx 等 Raw Input 轮询的游戏不认 PostMessage 软件消息）：
/// 逐窗口 SetCursorPos 到对应屏幕点后注入左键按下/抬起，最后光标移回原位。
/// SendInput 是硬件级输入，会再次触发低级钩子，因此注入期间置 _injecting 标志让钩子直接放行防递归。
/// </summary>
public static class InputSync
{
    private static IntPtr _mouseHook;
    // 物理注入节奏：光标到位等待、按下/抬起间隔、窗口间间隔。游戏引擎对过快跳动的光标会做抖动合并，需放慢。
    private const int ClickSettleMs = 200;
    private const int DownUpGapMs = 100;
    private const int BetweenWindowsMs = 100;
    // 委托必须保留引用，否则被 GC 回收后钩子回调会崩溃。
    private static Win32Api.HookProc? _mouseProc;
    private static Func<IReadOnlyList<IntPtr>>? _targetsProvider;
    // SendInput 注入期间为 true：注入的输入会再次触发低级钩子，钩子回调必须直接放行，否则无限递归。
    private static bool _injecting;

    /// <summary>同步是否已开启（钩子已安装）。</summary>
    public static bool Enabled { get; private set; }

    /// <summary>放行回调，单测可注入假实现避免真实调用 Win32。</summary>
    internal static Func<IntPtr, int, IntPtr, IntPtr, IntPtr> CallNextHook { get; set; } = Win32Api.CallNextHookEx;

    /// <summary>移动光标到屏幕绝对坐标，单测可注入假实现。</summary>
    internal static Action<int, int> SetCursor { get; set; } = (x, y) => Win32Api.SetCursorPos(x, y);

    /// <summary>取当前光标屏幕坐标，单测可注入假实现。</summary>
    internal static Func<(int X, int Y)> GetCursor { get; set; } = () =>
    {
        Win32Api.GetCursorPos(out var pt);
        return (pt.X, pt.Y);
    };

    /// <summary>客户区坐标转屏幕坐标（失败返回 null），单测可注入假实现。</summary>
    internal static Func<IntPtr, int, int, (int X, int Y)?> ClientToScreenPoint { get; set; } = (hwnd, x, y) =>
    {
        var pt = new Win32Api.POINT { X = x, Y = y };
        return Win32Api.ClientToScreen(hwnd, ref pt) ? (pt.X, pt.Y) : null;
    };

    /// <summary>物理注入一次鼠标动作（SendInput，在当前光标位置生效），单测可注入假实现。</summary>
    internal static Action<uint, int> SendMouse { get; set; } = Win32Api.SendMouseInput;

    /// <summary>
    /// 开启/关闭输入同步。必须在 UI 线程调用：低级钩子回调在安装线程的消息循环上下文执行。
    /// targetsProvider 每次回调时重新取当前已内嵌的子窗口句柄列表。
    /// </summary>
    public static void SetEnabled(bool enabled, Func<IReadOnlyList<IntPtr>> targetsProvider)
    {
        if (!enabled)
        {
            Shutdown();
            return;
        }
        if (Enabled)
        {
            _targetsProvider = targetsProvider;
            return;
        }
        IntPtr module = Win32Api.GetModuleHandle(null);
        _mouseProc = MouseHookProc;
        _mouseHook = Win32Api.SetWindowsHookEx(Win32Api.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseProc = null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "安装全局鼠标钩子失败");
        }
        _targetsProvider = targetsProvider;
        Enabled = true;
    }

    /// <summary>卸载钩子（关闭同步或主窗口关闭时调用）。</summary>
    public static void Shutdown()
    {
        Enabled = false;
        _targetsProvider = null;
        if (_mouseHook != IntPtr.Zero)
        {
            Win32Api.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _mouseProc = null;
    }

    internal static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 自己 SendInput 注入的输入会再次触发钩子，必须直接放行，否则广播→捕获→再广播无限递归。
        if (_injecting) return CallNextHook(_mouseHook, nCode, wParam, lParam);
        if (Enabled && nCode == Win32Api.HC_ACTION &&
            TryHandleMouse((uint)wParam, Marshal.PtrToStructure<Win32Api.MSLLHOOKSTRUCT>(lParam)))
            return (IntPtr)1; // 吞掉原始事件，改由广播投递
        return CallNextHook(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 鼠标输入处理：只处理左键。按下命中某个内嵌窗口（或其子控件）时，换算成该窗口客户区的相对坐标，
    /// 广播到所有内嵌窗口的相同相对位置并吞掉原事件。回调必须快速返回。
    /// </summary>
    internal static bool TryHandleMouse(uint msg, Win32Api.MSLLHOOKSTRUCT data)
    {
        // 只拦截左键按下和抬起；抬起(UP)不广播——按下时已对每个窗口注入完整点击(down+up)，抬起事件直接吞掉。
        // 其余鼠标消息（右键/中键/滚轮/移动）一律放行。
        if (msg is Win32Api.WM_LBUTTONUP)
        {
            // 抬起：只有命中内嵌窗口才吞掉（配对的 down 已注入完整点击），否则放行。
            var upTargets = _targetsProvider?.Invoke();
            return upTargets is { Count: > 0 } && FindTarget(upTargets, Win32Api.WindowFromPoint(data.pt)) != IntPtr.Zero;
        }
        if (msg is not Win32Api.WM_LBUTTONDOWN) return false;
        var targets = _targetsProvider?.Invoke();
        if (targets is not { Count: > 0 }) return false;
        IntPtr target = FindTarget(targets, Win32Api.WindowFromPoint(data.pt));
        if (target == IntPtr.Zero) return false;
        var pt = data.pt;
        if (!Win32Api.ScreenToClient(target, ref pt)) return false;
        Broadcast(targets, new SyncedInput(msg, (IntPtr)Win32Api.MK_LBUTTON, MakeLParam(pt.X, pt.Y)));
        LogService.Log($"[sync] 鼠标左键 相对位置=({pt.X},{pt.Y}) 已广播 {targets.Count} 个窗口");
        return true;
    }

    /// <summary>沿父窗口链向上找命中窗口属于哪个内嵌子窗口（游戏可能在内嵌窗口内再建子控件）。</summary>
    internal static IntPtr FindTarget(IReadOnlyList<IntPtr> targets, IntPtr hit, Func<IntPtr, IntPtr>? getParent = null)
    {
        getParent ??= Win32Api.GetParent;
        for (IntPtr hwnd = hit; hwnd != IntPtr.Zero; hwnd = getParent(hwnd))
            if (targets.Contains(hwnd)) return hwnd;
        return IntPtr.Zero;
    }

    /// <summary>拦截层（WPF，DIP 单位）坐标换算为子窗口客户区像素坐标。两者尺寸一致，仅差 DPI 缩放。</summary>
    public static (int X, int Y) ToClientPoint(System.Windows.Point dip, double scaleX, double scaleY)
        => ((int)Math.Round(dip.X * scaleX), (int)Math.Round(dip.Y * scaleY));

    /// <summary>打包客户区坐标为 lParam（低位 x，高位 y，按有符号 16 位处理）。</summary>
    public static IntPtr MakeLParam(int x, int y) => (IntPtr)((x & 0xFFFF) | (y << 16));

    /// <summary>把同一条左键点击广播到所有已内嵌窗口的相同客户区位置（含来源窗口自身，保证行为一致）。</summary>
    public static void Broadcast(IEnumerable<EmbedSlot> slots, SyncedInput input)
        => Broadcast(slots.Where(s => s.IsAttached).Select(s => s.LastHwnd), input);

    /// <summary>
    /// 把同一条左键点击广播到所有给定窗口的相同客户区位置（SendInput 物理注入，
    /// Raw Input 轮询的游戏不认 PostMessage 软件消息）。非左键按下消息忽略。
    /// </summary>
    public static void Broadcast(IEnumerable<IntPtr> hwnds, SyncedInput input)
    {
        if (input.Msg is not Win32Api.WM_LBUTTONDOWN) return;
        InjectMouseBroadcast(hwnds, input);
    }

    /// <summary>
    /// 鼠标物理广播：对每个窗口把光标 SetCursorPos 到其对应屏幕点，SendInput 注入左键按下/抬起，
    /// 全部点完后光标移回原位。原始事件已被钩子吞掉，物理光标仍在用户点击处，广播结束必须还原。
    /// </summary>
    private static void InjectMouseBroadcast(IEnumerable<IntPtr> hwnds, SyncedInput input)
    {
        // 按下消息映射为 down+up 一对（完整点击）；UP 消息不进入这里（在 TryHandleMouse 已吞掉）。
        int rx = (short)(input.LParam.ToInt64() & 0xFFFF);
        int ry = (short)(input.LParam.ToInt64() >> 16);
        var origin = GetCursor();
        _injecting = true; // SendInput 会再次触发低级钩子，注入期间钩子必须直接放行防递归
        try
        {
            int injected = 0, skipped = 0;
            foreach (IntPtr hwnd in hwnds)
            {
                var screen = ClientToScreenPoint(hwnd, rx, ry);
                if (screen is null) { skipped++; LogService.Log($"[sync] 窗口 {hwnd} 坐标换算失败，跳过"); continue; }
                SetCursor(screen.Value.X, screen.Value.Y);
                Thread.Sleep(ClickSettleMs); // 光标到位后给游戏时间感知光标位置
                SendMouse(Win32Api.MOUSEEVENTF_LEFTDOWN, 0);
                Thread.Sleep(DownUpGapMs); // 按下与抬起间隔，确保游戏注册到按下
                SendMouse(Win32Api.MOUSEEVENTF_LEFTUP, 0);
                injected++;
                LogService.Log($"[sync] 窗口 {hwnd} 注入于屏幕 ({screen.Value.X},{screen.Value.Y})");
                Thread.Sleep(BetweenWindowsMs); // 窗口间间隔，避免快速跳动被游戏当抖动忽略
            }
            LogService.Log($"[sync] 本次广播：注入 {injected} 个，跳过 {skipped} 个");
        }
        finally
        {
            SetCursor(origin.X, origin.Y); // 光标移回用户原点击位置
            _injecting = false;
        }
    }
}
