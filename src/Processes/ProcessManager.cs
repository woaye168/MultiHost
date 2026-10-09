using System.Diagnostics;
using System.IO;

namespace MultiHost;

public sealed class ProcessManager : IDisposable
{
    private sealed record Session(Process Process, IntPtr Job);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;
    public event Action<string>? InstanceExited;
    public ProcessManager(bool useJob = true) { }

    public IReadOnlyCollection<string> RunningFolders { get { lock (_gate) return _sessions.Keys.ToArray(); } }
    public Process? GetProcess(string key) { lock (_gate) return _sessions.TryGetValue(key, out var s) ? s.Process : null; }
    public bool IsRunning(string key)
    {
        lock (_gate) return _sessions.TryGetValue(key, out var s) && SessionNative.IsAlive(s.Job);
    }
    public bool Owns(string key, int pid)
    {
        lock (_gate) return _sessions.TryGetValue(key, out var s) && SessionNative.Owns(s.Job, pid);
    }
    public void Refresh()
    {
        var exited = new List<string>();
        lock (_gate)
        {
            foreach (var (key, session) in _sessions.ToArray())
            {
                if (SessionNative.IsAlive(session.Job)) continue;
                _sessions.Remove(key);
                session.Process.Dispose();
                SessionNative.CloseHandle(session.Job);
                exited.Add(key);
            }
        }
        foreach (var key in exited) InstanceExited?.Invoke(key);
    }

    public Process Start(InstanceConfig cfg)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.ContainsKey(cfg.FolderName)) throw new InvalidOperationException("实例会话已存在");
            cfg.Validate();
            if (!IsWithin(cfg.ExeFullPath, cfg.FolderFullPath) || !File.Exists(cfg.ExeFullPath))
                throw new FileNotFoundException("实例可执行文件无效", cfg.ExeFullPath);
            RejectLinkedInstancePath(cfg.ExeFullPath);
            var existing = FindInstanceProcesses(cfg.FolderFullPath, "");
            try { if (existing.Count > 0) throw new InvalidOperationException("实例已由外部运行，拒绝接管或重复启动"); }
            finally { foreach (var p in existing) p.Dispose(); }
            var job = Win32Api.CreateKillOnCloseJob();
            if (job == IntPtr.Zero) throw new InvalidOperationException("创建实例 Job 失败");
            try
            {
                var process = SessionNative.Launch(cfg, job);
                _sessions.Add(cfg.FolderName, new Session(process, job));
                return process;
            }
            catch { SessionNative.CloseHandle(job); throw; }
        }
    }

    public Task StopAsync(string key) => Task.Run(() => Stop(key));
    public void Stop(string key, int gracefulMs = 3000)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var s)) return;
            if (!SessionNative.TerminateJobObject(s.Job, 0))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            _sessions.Remove(key);
            SessionNative.CloseHandle(s.Job);
            s.Process.Dispose();
        }
    }
    public void StopAll() { foreach (var key in RunningFolders) Stop(key); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopAll();
            _disposed = true;
        }
    }

    public static bool IsWithin(string? file, string folder) => file != null &&
        Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void RejectLinkedInstancePath(string executable)
    {
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.WorkspaceRoot));
        var current = Path.GetFullPath(executable);
        while (!current.Equals(workspace, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("实例程序路径不能经过文件链接或目录链接: " + current);
            current = Path.GetDirectoryName(current)
                ?? throw new IOException("实例程序不在工作区中");
        }
    }

    public static List<Process> FindInstanceProcesses(string folderPath, string exeFileName)
    {
        var result = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            bool owned = false;
            try { owned = IsWithin(p.MainModule?.FileName, folderPath); }
            catch { }
            if (owned) result.Add(p); else p.Dispose();
        }
        return result;
    }

    public static void ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder is "." or ".." ||
            folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            folder.EndsWith('.') || folder.EndsWith(' ') || AppPaths.ExcludedFolderNames.Contains(folder) ||
            folder.StartsWith(".copy-", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(folder.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("实例文件夹名无效或为保留目录");
    }

    public async Task CopyTemplateAsync(string folderName, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateFolder(folderName);
        string source = AppPaths.TemplateDir, target = Path.Combine(AppPaths.WorkspaceRoot, folderName);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("实例目录已存在");
        string staging = Path.Combine(AppPaths.WorkspaceRoot, ".copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CopyDirectoryAsync(source, staging, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
    private static async Task CopyDirectoryAsync(string source, string target, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("模板不能包含目录链接");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("模板不能包含文件链接");
            var name = Path.GetFileName(file);
            await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(Path.Combine(target, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, cancellationToken);
            progress?.Report(name);
        }
        foreach (var directory in Directory.GetDirectories(source))
            await CopyDirectoryAsync(directory, Path.Combine(target, Path.GetFileName(directory)), progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
