using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiHost;

internal static class SessionNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? reserved, desktop, title;
        public int x, y, width, height, charsX, charsY, fill, flags;
        public short show, reservedSize;
        public IntPtr reservedPtr, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long user, kernel, periodUser, periodKernel;
        public uint faults, total, active, terminated;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int type, out Accounting info, int length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    internal static Process Launch(InstanceConfig cfg, IntPtr job)
    {
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcess(cfg.ExeFullPath, new StringBuilder($"\"{cfg.ExeFullPath}\" {cfg.BuildArguments()}"),
            IntPtr.Zero, IntPtr.Zero, false, 4, IntPtr.Zero, Path.GetDirectoryName(cfg.ExeFullPath)!, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Process? process = null;
        try
        {
            if (!Win32Api.AssignProcessToJobObject(job, info.process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "指派实例 Job 失败");
            // Retain a process handle before execution starts, including for short-lived launchers.
            process = Process.GetProcessById(info.pid);
            _ = process.SafeHandle;
            if (ResumeThread(info.thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "恢复实例进程失败");
            return process;
        }
        catch { process?.Dispose(); TerminateProcess(info.process, 1); throw; }
        finally { CloseHandle(info.thread); CloseHandle(info.process); }
    }

    internal static bool IsAlive(IntPtr job)
    {
        if (!QueryInformationJobObject(job, 1, out var info, Marshal.SizeOf<Accounting>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.active > 0;
    }

    internal static bool Owns(IntPtr job, int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return IsProcessInJob(process.Handle, job, out var owned) && owned;
        }
        catch { return false; }
    }
}
