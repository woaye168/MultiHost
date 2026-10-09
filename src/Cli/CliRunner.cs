using System.Runtime.InteropServices;
using System.Text.Json;

namespace MultiHost;

public static class CliRunner
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint pid);
    public static async Task<int> RunAsync(string[] args)
    {
        AttachConsole(uint.MaxValue);
        CommandReply reply;
        if (args.Length == 0 || args[0] is "help" or "--help")
            reply = new(0, new { Usage = "MultiHost.exe cli <command> [args]，操作正在运行的同一 GUI",
                Commands = new[] { "list", "status", "workspace [absolute-path]", "template [absolute-path]", "start <folder...>", "stop <folder>", "stopall", "embed <folder>", "popout <folder>", "close-popout <folder>", "check-embed <folder>", "diag <folder>（导出内嵌窗口树与截图）", "probe <folder>（尺寸探测）", "fit <folder> <fill|original>", "theme [system|light|dark]", "resolution [width height]", "create <name> <folder> [relative-exe]", "config <folder> <width> <height> <arguments> <embed> [include-size-arguments] [relative-exe] [name]", "logs", "refresh", "autopilot <folder1> <folder2>", "shutdown" },
                Notes = "参数默认原样传递，不附加宽高标志。GUI 启动时自动提权；CLI 请在同一用户的管理员终端中运行。MULTIHOST_WORKSPACE 可指定独立工作区。" });
        else
        {
            try { reply = args[0] == "autopilot" ? await CliAcceptance.RunAsync(args.Skip(1).ToArray()) : await GuiPipe.SendAsync(args); }
            catch (UnauthorizedAccessException ex) { reply = new(5, Error: $"CLI 与 GUI 需相同用户和权限等级。{ex.Message}"); }
            catch (Exception ex) { reply = new(4, Error: $"无法连接 GUI，请先启动多开管理器。{ex.Message}"); }
        }
        Console.WriteLine(JsonSerializer.Serialize(reply));
        return reply.Code;
    }
}

