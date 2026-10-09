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
}
