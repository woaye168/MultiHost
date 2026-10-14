namespace MultiHost;

/// <summary>
/// 全局日志服务：内存环形缓冲 + 订阅事件。
/// 主窗口、内嵌控制器、日志窗口共用，日志窗口独立显示。
/// </summary>
public static class LogService
{
    private const int MaxEntries = 500;
    private static readonly List<string> _entries = new();
    private static readonly object _lock = new();

    /// <summary>新日志条目事件（订阅者需自行切到 UI 线程）</summary>
    public static event Action<string>? EntryAdded;

    /// <summary>历史日志（最多保留 500 条）</summary>
    public static IReadOnlyList<string> Entries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    /// <summary>追加一条带时间戳的日志</summary>
    public static void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_lock)
        {
            _entries.Add(line);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        EntryAdded?.Invoke(line);
    }
}
