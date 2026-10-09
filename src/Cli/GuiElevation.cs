using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace MultiHost;

public sealed record ElevationResult(bool ContinueStartup, int ExitCode = 0, string? Error = null);

/// <summary>在创建窗口或持有单实例锁之前提升 GUI；CLI 保持调用者权限。</summary>
public static class GuiElevation
{
    private const string WorkspaceOption = "--workspace-override";

    public static string? ReadWorkspaceOverride(string[] args)
    {
        var index = Array.IndexOf(args, WorkspaceOption);
        if (index < 0) return null;
        if (index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
            throw new ArgumentException("工作区参数必须为绝对目录路径");
        return args[index + 1];
    }

    public static ElevationResult Ensure(string[] args, bool isAdministrator, string executable,
        string? workspaceOverride, Func<ProcessStartInfo, bool>? launch = null)
    {
        if (isAdministrator || args.FirstOrDefault()?.Equals("cli", StringComparison.OrdinalIgnoreCase) == true)
            return new(true);

        try
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            foreach (var argument in args) info.ArgumentList.Add(argument);
            if (ReadWorkspaceOverride(args) == null && !string.IsNullOrWhiteSpace(workspaceOverride))
            {
                info.ArgumentList.Add(WorkspaceOption);
                info.ArgumentList.Add(workspaceOverride);
            }
            if (!(launch ?? Launch)(info)) return new(false, 1, "未能启动管理员进程");
            return new(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, 1223); // 用户取消系统 UAC 后退出，不再提示或重试。
        }
        catch (Exception ex)
        {
            return new(false, 1, ex.Message);
        }
    }

    private static bool Launch(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        return process != null;
    }
}
