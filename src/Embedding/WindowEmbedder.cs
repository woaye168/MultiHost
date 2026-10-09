using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiHost;

public record CaptureResult(IntPtr Hwnd, string Strategy)
{
    public bool Found => Hwnd != IntPtr.Zero;
}

public static class WindowEmbedder
{
    public static async Task<CaptureResult> CaptureMainWindowAsync(
        InstanceConfig cfg, Process? proc, CancellationToken ct,
        int timeoutMs = 20000, int? knownPid = null, Func<int, bool>? owns = null)
    {
        ct.ThrowIfCancellationRequested();
        int? processId = knownPid ?? proc?.Id;
        if (owns == null && processId == null) return new(IntPtr.Zero, "None");
        Func<int, bool> isOwned = owns ?? (pid => pid == processId);
        string strategy = owns != null ? "OwnedJob" : "ExactProcess";
        var watch = Stopwatch.StartNew();
        IntPtr previous = IntPtr.Zero;
        (int Width, int Height) previousSize = default;
        int stable = 0;
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var result = await Task.Run(() => FindWindow(isOwned, strategy), ct);
            if (result.Found && WindowNative.GetClientRect(result.Hwnd, out var rect) &&
                rect.Right > rect.Left && rect.Bottom > rect.Top)
            {
                var size = (rect.Right - rect.Left, rect.Bottom - rect.Top);
                stable = result.Hwnd == previous && size == previousSize ? stable + 1 : 1;
                previousSize = size;
                if (stable >= 3) return result;
            }
            else stable = 0;
            previous = result.Hwnd;
            await Task.Delay(200, ct);
        }
        return new(IntPtr.Zero, "None");
    }

    private static CaptureResult FindWindow(Func<int, bool> owns, string strategy)
    {
        IntPtr best = IntPtr.Zero;
        long largestArea = 0;
        foreach (var (hwnd, pid, _) in Win32Api.EnumTopWindows())
        {
            if (!owns((int)pid) || (Win32Api.GetWindowLong(hwnd, Win32Api.GWL_STYLE) & Win32Api.WS_CHILD) != 0 ||
                !WindowNative.GetClientRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) continue;
            // A generic application can choose any initial size; prefer its largest visible window.
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > largestArea)
            {
                largestArea = area;
                best = hwnd;
            }
        }
        return new(best, best == IntPtr.Zero ? "None" : strategy);
    }

    public static long Embed(IntPtr child, IntPtr parent)
    {
        if (!Win32Api.IsWindow(child) || !Win32Api.IsWindow(parent)) throw new InvalidOperationException("窗口句柄失效");
        long original = Win32Api.GetWindowLong(child, Win32Api.GWL_STYLE);
        var oldParent = Win32Api.GetParent(child);
        long style = (original & ~(Win32Api.WS_CAPTION | Win32Api.WS_SYSMENU | Win32Api.WS_THICKFRAME |
            Win32Api.WS_MINIMIZEBOX | Win32Api.WS_MAXIMIZEBOX | Win32Api.WS_POPUP)) | Win32Api.WS_CHILD | Win32Api.WS_VISIBLE;
        try
        {
            Win32Api.SetLastError(0);
            if (Win32Api.SetWindowLong(child, Win32Api.GWL_STYLE, style) == 0) ThrowIfError("SetWindowLong");
            Win32Api.SetLastError(0);
            if (Win32Api.SetParent(child, parent) == IntPtr.Zero) ThrowIfError("SetParent");
            if (Win32Api.GetParent(child) != parent) throw new InvalidOperationException("内嵌父窗口验证失败");
            if (!Win32Api.SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
                Win32Api.SWP_NOSIZE | Win32Api.SWP_NOMOVE | Win32Api.SWP_NOZORDER | Win32Api.SWP_FRAMECHANGED)) ThrowIfError("SetWindowPos");
            return original;
        }
        catch
        {
            Win32Api.SetParent(child, oldParent);
            Win32Api.SetWindowLong(child, Win32Api.GWL_STYLE, original);
            throw;
        }
    }
    public static void Unembed(IntPtr child, long style)
    {
        if (!Win32Api.IsWindow(child)) return;
        Win32Api.SetLastError(0);
        if (Win32Api.SetParent(child, IntPtr.Zero) == IntPtr.Zero) ThrowIfError("SetParent");
        Win32Api.SetLastError(0);
        if (Win32Api.SetWindowLong(child, Win32Api.GWL_STYLE, style) == 0) ThrowIfError("SetWindowLong");
        Win32Api.SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
            Win32Api.SWP_NOSIZE | Win32Api.SWP_NOMOVE | Win32Api.SWP_NOZORDER | Win32Api.SWP_FRAMECHANGED);
    }
    public static bool IsEmbedded(IntPtr hwnd) => Win32Api.IsWindow(hwnd) && Win32Api.GetParent(hwnd) != IntPtr.Zero &&
        (Win32Api.GetWindowLong(hwnd, Win32Api.GWL_STYLE) & Win32Api.WS_CHILD) != 0;
    private static void ThrowIfError(string api)
    {
        int error = Marshal.GetLastWin32Error();
        if (error != 0) throw new Win32Exception(error, api);
    }
}
