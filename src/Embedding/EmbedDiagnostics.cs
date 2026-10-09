using System.IO;
using System.Windows;
using System.Windows.Forms.Integration;

namespace MultiHost;

/// <summary>
/// 内嵌交互诊断：导出子窗口后代树（句柄/类名/线程/屏幕矩形）并截取内嵌区域屏幕截图，
/// 用于比对“看到的画面”与“实际接收输入的窗口”是否错位。
/// </summary>
public static class EmbedDiagnostics
{
    public static object Capture(EmbedSlot slot, string folder)
    {
        var child = slot.LastHwnd;
        var descendants = new List<object>();
        if (Win32Api.IsWindow(child))
        {
            EnumChildWindows(child, (hwnd, lparam) =>
            {
                var cls = new System.Text.StringBuilder(128);
                GetClassName(hwnd, cls, cls.Capacity);
                Win32Api.GetWindowThreadProcessId(hwnd, out var pid);
                Win32Api.GetWindowRect(hwnd, out var rect);
                descendants.Add(new
                {
                    Hwnd = hwnd.ToInt64(), Class = cls.ToString(), Pid = pid,
                    Tid = Win32Api.GetWindowThreadProcessId(hwnd, out uint _),
                    Visible = Win32Api.IsWindowVisible(hwnd),
                    Rect = new[] { rect.Left, rect.Top, rect.Right, rect.Bottom }
                });
                return true;
            }, IntPtr.Zero);
        }
        Win32Api.GetWindowRect(child, out var childRect);
        string? screenshot = null;
        try { screenshot = SaveScreenshot(slot, folder); }
        catch (Exception ex) { LogService.Log($"诊断截图失败: {ex.Message}"); }
        return new
        {
            Folder = folder,
            ChildRect = new[] { childRect.Left, childRect.Top, childRect.Right, childRect.Bottom },
            slot.InputAttached,
            Descendants = descendants,
            Screenshot = screenshot
        };
    }

    private static string SaveScreenshot(EmbedSlot slot, string folder)
    {
        var host = slot.Children.OfType<WindowsFormsHost>().FirstOrDefault();
        if (host == null) throw new InvalidOperationException("实例未内嵌");
        var topLeft = host.PointToScreen(new System.Windows.Point(0, 0));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(host);
        int x = (int)topLeft.X, y = (int)topLeft.Y;
        int width = (int)(host.ActualWidth * dpi.DpiScaleX), height = (int)(host.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("内嵌区域不可见");
        var file = Path.Combine(AppPaths.WorkspaceRoot, $"diag-{folder}-{DateTime.Now:HHmmss}.png");
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(x, y, 0, 0, bitmap.Size);
        bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        return file;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, Win32Api.EnumWindowsProc callback, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int capacity);

    /// <summary>对内嵌窗口做尺寸探测：在管理员 GUI 线程里改尺寸并读回客户区，定位游戏何时响应 WM_SIZE。</summary>
    public static async Task<object> ProbeAsync(EmbedSlot slot)
    {
        var child = slot.LastHwnd;
        var steps = new List<object>();
        void Dump(string tag)
        {
            WindowNative.GetClientRect(child, out var c);
            Win32Api.GetWindowRect(child, out var w);
            steps.Add(new { Step = tag, Client = $"{c.Right - c.Left}x{c.Bottom - c.Top}", Window = $"{w.Right - w.Left}x{w.Bottom - w.Top}", Pos = $"{w.Left},{w.Top}" });
        }
        Dump("内嵌当前");
        // 直接 MoveWindow 改客户区到内嵌分辨率（不经 FitChild 的 AdjustWindowRectEx）
        Win32Api.MoveWindow(child, 0, 0, 800, 360, true);
        await Task.Delay(500);
        Dump("MoveWindow(800x360裸)");
        // 用 SetWindowPos 改
        Win32Api.SetWindowPos(child, IntPtr.Zero, 0, 0, 900, 500, Win32Api.SWP_NOZORDER);
        await Task.Delay(500);
        Dump("SetWindowPos(900x500)");
        // 还原 fill
        slot.SetFill(true);
        await Task.Delay(300);
        Dump("SetFill还原");
        return new { Steps = steps, InputAttached = slot.InputAttached, FillMode = slot.FillMode };
    }

    /// <summary>连续读取内嵌窗口客户区，检测是否抖动（尺寸来回变化）。</summary>
    public static async Task<object> ProbeFluctuateAsync(EmbedSlot slot)
    {
        var child = slot.LastHwnd;
        var samples = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            WindowNative.GetClientRect(child, out var c);
            Win32Api.GetWindowRect(child, out var w);
            samples.Add($"{c.Right - c.Left}x{c.Bottom - c.Top}/win{w.Right - w.Left}x{w.Bottom - w.Top}");
            await Task.Delay(150);
        }
        return new { Samples = samples, Distinct = samples.Distinct().Count(), Note = "Distinct>1 说明窗口尺寸在抖动" };
    }

    // 三种尺寸修改方式对照：看哪种能触发游戏渲染器重建（日志出现 Resize Display）。
    public static async Task<object> ProbeResizeMethodAsync(EmbedSlot slot, string method)
    {
        var child = slot.LastHwnd;
        WindowNative.GetClientRect(child, out var c);
        int w = c.Right - c.Left, h = c.Bottom - c.Top;
        int nw = w == 800 ? 820 : 800, nh = h == 360 ? 380 : 360; // 换个尺寸触发变化
        string note = method switch
        {
            "movewindow" => DoMove(child, nw, nh),
            "setwindowpos" => DoSetPos(child, nw, nh),
            "minmax" => await DoMinMax(child),
            _ => "unknown"
        };
        await Task.Delay(600);
        WindowNative.GetClientRect(child, out var c2);
        return new { Method = method, Before = $"{w}x{h}", After = $"{c2.Right - c2.Left}x{c2.Bottom - c2.Top}", Note = note + "；看日志有无 Resize Display" };
    }
    private static string DoMove(IntPtr h, int w, int ht)
        => Win32Api.MoveWindow(h, 0, 0, w, ht, true) ? "MoveWindow已发" : "MoveWindow失败";
    private static string DoSetPos(IntPtr h, int w, int ht)
        => Win32Api.SetWindowPos(h, IntPtr.Zero, 0, 0, w, ht, Win32Api.SWP_NOZORDER | Win32Api.SWP_FRAMECHANGED) ? "SetWindowPos+FRAMECHANGED已发" : "SetWindowPos失败";
    private static async Task<string> DoMinMax(IntPtr h)
    {
        Win32Api.SendMessage(h, 0x0112 /*WM_SYSCOMMAND*/, (IntPtr)0xF020 /*SC_MINIMIZE*/, IntPtr.Zero);
        await Task.Delay(400);
        Win32Api.SendMessage(h, 0x0112, (IntPtr)0xF120 /*SC_RESTORE*/, IntPtr.Zero);
        return "最小化→还原已发";
    }

    /// <summary>对内嵌窗口显式发 WM_SIZE，验证游戏是否据此重算逻辑分辨率。</summary>
    public static async Task<object> ProbeWmSizeAsync(EmbedSlot slot)
    {
        var child = slot.LastHwnd;
        WindowNative.GetClientRect(child, out var c);
        int w = c.Right - c.Left, h = c.Bottom - c.Top;
        var before = $"{w}x{h}";
        // 显式发 WM_SIZE(SIZE_RESTORED, 客户区宽高)
        Win32Api.SendMessage(child, Win32Api.WM_SIZE, (IntPtr)Win32Api.SIZE_RESTORED, (IntPtr)(w | (h << 16)));
        await Task.Delay(800);
        WindowNative.GetClientRect(child, out var c2);
        return new { SentClient = before, AfterClient = $"{c2.Right - c2.Left}x{c2.Bottom - c2.Top}", Note = "看游戏日志是否刷出对应分辨率" };
    }
}
