using System.Runtime.InteropServices;
namespace MultiHost;

internal static class WindowNative
{
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out Win32Api.RECT rect);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, Win32Api.EnumWindowsProc callback, IntPtr lParam);

    // 跨进程内嵌的子窗口必须与宿主 GUI 线程共享输入队列，否则真实鼠标输入不会投递到子进程。
    internal static bool AttachInput(IntPtr hwnd)
    {
        if (!Win32Api.IsWindow(hwnd)) return false;
        uint target = Win32Api.GetWindowThreadProcessId(hwnd, out _), current = GetCurrentThreadId();
        return target == current || AttachThreadInput(current, target, true);
    }
    internal static void DetachInput(IntPtr hwnd)
    {
        uint target = Win32Api.GetWindowThreadProcessId(hwnd, out _), current = GetCurrentThreadId();
        if (target != 0 && target != current) AttachThreadInput(current, target, false);
    }

    // 复杂应用（内嵌浏览器/游戏引擎等）的后代窗口可能属于其他进程的线程，
    // 需要逐个线程合并输入队列，否则这些子窗口收不到真实鼠标输入。
    internal static List<uint> AttachInputDeep(IntPtr hwnd)
    {
        var threads = new HashSet<uint>();
        if (Win32Api.IsWindow(hwnd))
        {
            threads.Add(Win32Api.GetWindowThreadProcessId(hwnd, out _));
            EnumChildWindows(hwnd, (child, _) =>
            {
                threads.Add(Win32Api.GetWindowThreadProcessId(child, out uint _));
                return true;
            }, IntPtr.Zero);
        }
        uint current = GetCurrentThreadId();
        var attached = new List<uint>();
        foreach (uint thread in threads)
            if (thread != current && AttachThreadInput(current, thread, true)) attached.Add(thread);
        return attached;
    }
    internal static void DetachInputDeep(IEnumerable<uint> threads)
    {
        uint current = GetCurrentThreadId();
        foreach (uint thread in threads)
            if (thread != current) AttachThreadInput(current, thread, false);
    }
    internal static bool Focus(IntPtr hwnd, bool leaveAttached = false)
    {
        if (!Win32Api.IsWindow(hwnd)) return false;
        uint target = Win32Api.GetWindowThreadProcessId(hwnd, out _), current = GetCurrentThreadId();
        bool attached = target != current && AttachThreadInput(current, target, true);
        if (target != current && !attached) return false;
        try
        {
            Win32Api.SetForegroundWindow(Win32Api.GetAncestor(hwnd, Win32Api.GA_ROOT));
            Win32Api.SetFocus(hwnd);
            return GetFocus() == hwnd;
        }
        finally { if (attached && !leaveAttached) AttachThreadInput(current, target, false); }
    }
}
