using System.Diagnostics;
using System.Windows.Controls.Primitives;

namespace MultiHost;

public sealed class EmbedController
{
    private readonly UniformGrid? _grid;
    private readonly ProcessManager _manager;
    private readonly Func<string, InstanceConfig?> _resolve;
    private readonly Dictionary<string, EmbedSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    public EmbedController(ProcessManager manager, Func<string, InstanceConfig?> resolve)
    { _manager = manager; _resolve = resolve; }
    public EmbedController(UniformGrid grid, ProcessManager manager, Func<string, InstanceConfig?> resolve)
        : this(manager, resolve) { _grid = grid; }
    public bool IsAttached(string key) => _slots.TryGetValue(key, out var slot) && slot.IsAttached;
    public bool HasSlot(string key) => _slots.ContainsKey(key);
    public EmbedSlot? GetSlot(string key) => _slots.GetValueOrDefault(key);
    public void Synchronize(IEnumerable<InstanceConfig> instances)
    {
        var keys = instances.Select(cfg => cfg.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _slots.Keys.Where(key => !keys.Contains(key)).ToArray()) RemoveSlot(key);
        foreach (var key in keys) EnsureSlot(key);
    }
    private EmbedSlot EnsureSlot(string key)
    {
        if (_slots.TryGetValue(key, out var existing)) return existing;
        var slot = new EmbedSlot();
        slot.ReEmbedRequested += async k =>
        {
            try { await ReEmbedAsync(k); }
            catch (Exception ex) { LogService.Log($"收回失败 {k}: {ex.Message}"); }
        };
        slot.ShowStopped(key);
        _slots[key] = slot;
        _grid?.Children.Add(slot);
        return slot;
    }
    public async Task EmbedAsync(InstanceConfig cfg, Process? proc, int timeoutMs = 60000)
    {
        string key = cfg.FolderName;
        if (IsAttached(key)) return;
        Cancel(key);
        var token = new CancellationTokenSource();
        _pending[key] = token;
        var slot = EnsureSlot(key);
        slot.ShowWaiting(key);
        try
        {
            var capture = await WindowEmbedder.CaptureMainWindowAsync(cfg, proc, token.Token, timeoutMs,
                owns: pid => _manager.Owns(key, pid));
            token.Token.ThrowIfCancellationRequested();
            if (!_slots.TryGetValue(key, out var current) || current != slot) return;
            if (!capture.Found) throw new TimeoutException("未找到可内嵌的实例窗口；程序可能没有图形界面，或尚未创建窗口。可关闭自动内嵌后启动，或稍后点击收回重试。");
            Win32Api.GetWindowThreadProcessId(capture.Hwnd, out var pid);
            if (!_manager.Owns(key, (int)pid)) throw new InvalidOperationException("窗口不再属于实例会话");
            if (_slots.Any(p => p.Key != key && p.Value.LastHwnd == capture.Hwnd))
                throw new InvalidOperationException("窗口已被其他槽位占用");
            slot.Attach(key, capture.Hwnd);
            LogService.Log($"已内嵌 {key} HWND={capture.Hwnd}，归属={capture.Strategy}，输入合并={slot.InputAttached}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_pending.GetValueOrDefault(key) == token) slot.ShowFailed(key, ex.Message);
            LogService.Log($"内嵌失败 {key}: {ex.Message}");
            throw;
        }
        finally
        {
            if (_pending.GetValueOrDefault(key) == token) _pending.Remove(key);
            token.Dispose();
        }
    }
    private void Cancel(string key)
    {
        if (_pending.Remove(key, out var token)) token.Cancel();
    }
    public void PopOut(string key)
    {
        Cancel(key);
        _slots.GetValueOrDefault(key)?.Detach();
    }
    public async Task ReEmbedAsync(string key)
    {
        var cfg = _resolve(key);
        if (cfg == null || !_manager.IsRunning(key)) return;
        if (_slots.TryGetValue(key, out var slot) && Win32Api.IsWindow(slot.LastHwnd))
        {
            Win32Api.GetWindowThreadProcessId(slot.LastHwnd, out var pid);
            if (_manager.Owns(key, (int)pid)) { Cancel(key); slot.Attach(key, slot.LastHwnd); return; }
        }
        await EmbedAsync(cfg, _manager.GetProcess(key));
    }
    public void ReleaseSlot(string key, bool restoreWindow)
    {
        Cancel(key);
        _slots.GetValueOrDefault(key)?.ResetContent();
    }
    private void RemoveSlot(string key)
    {
        Cancel(key);
        if (!_slots.Remove(key, out var slot)) return;
        try { slot.ResetOnly(); }
        finally { _grid?.Children.Remove(slot); }
    }
    public void ReleaseAll()
    {
        var errors = new List<Exception>();
        foreach (var key in _slots.Keys.ToArray())
        {
            try { RemoveSlot(key); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("部分实例窗口还原失败", errors);
    }
}
