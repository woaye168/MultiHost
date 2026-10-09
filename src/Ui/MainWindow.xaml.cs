using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MultiHost;

public class InstanceItem : INotifyPropertyChanged
{
    public required InstanceConfig Config { get; init; }
    private bool _selected;
    public bool IsSelected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    private string _status = "已停止";
    public string StatusText { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(StatusText))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshConfig() => PropertyChanged?.Invoke(this, new(nameof(Config)));
}

public partial class MainWindow : Window
{
    private readonly ProcessManager _processManager = new();
    private readonly EmbedController _embedController;
    private readonly ObservableCollection<InstanceItem> _instances = new();
    private readonly Dictionary<string, InstanceCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<InstanceItem> Instances => _instances;
    private readonly DispatcherTimer _statusTimer;
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stopRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queuedStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private LogWindow? _logWindow;
    private CreateInstanceWindow? _createWindow;
    private SettingsWindow? _settingsWindow;
    private GuiPipe? _pipe;
    private CancellationTokenSource? _createCancellation;
    private CancellationTokenSource? _batchDelay;
    private bool _closing;
    private bool _confirmedShutdown;
    private bool _loadedSelection;
    private int _batchOperations;

    public MainWindow(string? startupWarning = null)
    {
        InitializeComponent();
        // 恢复上次的主窗口尺寸（持久化在工作区设置里）。
        var savedSize = new ShellSettingsStore(AppPaths.WorkspaceRoot).Load();
        if (savedSize.WindowWidth > 0 && savedSize.WindowHeight > 0)
        {
            Width = savedSize.WindowWidth;
            Height = savedSize.WindowHeight;
        }
        DpiChanged += (_, _) => UpdateCardLayout();
        CardsScroll.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) UpdateCardLayout(); };
        _embedController = new(_processManager, FindConfig);
        _processManager.InstanceExited += key =>
        {
            _embedController.ReleaseSlot(key, false);
            Log($"{key} 会话全部进程已退出");
        };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RunUi(RefreshStatuses);
        Loaded += (_, _) =>
        {
            RunUi(ReloadInstances);
            RunUi(RefreshSettings);
            if ((startupWarning ?? AppPaths.StartupWarning) is { } warning) ShowStatus(warning, true);
            _statusTimer.Start();
            _pipe = new GuiPipe(this);
        };
    }

    private InstanceConfig? FindConfig(string key) => _instances.FirstOrDefault(i => i.Config.FolderName.Equals(key, StringComparison.OrdinalIgnoreCase))?.Config;

    // 勾选状态持久化到工作区设置，下次启动恢复。
    private void PersistSelection()
    {
        if (!_loadedSelection) return;
        try { new ShellSettingsStore(AppPaths.WorkspaceRoot).SaveSelectedFolders(_instances.Where(i => i.IsSelected).Select(i => i.Config.FolderName)); } catch { }
    }

    private void ReloadInstances()
    {
        var configs = InstanceStore.Scan(InstanceStore.Load());
        // 首次加载用持久化的勾选列表；之后刷新保留当前勾选。
        var checkedKeys = _loadedSelection
            ? _instances.Where(i => i.IsSelected).Select(i => i.Config.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new ShellSettingsStore(AppPaths.WorkspaceRoot).LoadSelectedFolders().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _loadedSelection = true;
        _embedController.Synchronize(configs);
        var keys = configs.Select(c => c.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _cards.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            InstanceGrid.Children.Remove(_cards[key]);
            _cards.Remove(key);
        }
        _instances.Clear();
        foreach (var cfg in configs)
        {
            var item = new InstanceItem { Config = cfg, IsSelected = checkedKeys.Contains(cfg.FolderName) };
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(InstanceItem.IsSelected)) PersistSelection(); };
            _instances.Add(item);
            if (_cards.TryGetValue(cfg.FolderName, out var card)) card.DataContext = item;
            else
            {
                card = new InstanceCard(item, _embedController.GetSlot(cfg.FolderName)!);
                card.StartRequested += async key => await GuardAsync(() => StartInstanceAsync(FindConfig(key)!));
                card.StopRequested += async key => await GuardAsync(() => StopInstanceAsync(key));
                card.ConfigureRequested += key => RunUi(() => OpenInstanceConfiguration(key));
                card.PopOutRequested += key => RunUi(() => { _embedController.PopOut(key); RefreshStatuses(); });
                card.ReEmbedRequested += async key => await GuardAsync(async () => { await _embedController.ReEmbedAsync(key); RefreshStatuses(); });
                _cards.Add(cfg.FolderName, card);
                InstanceGrid.Children.Add(card);
            }
        }
        UpdateCardLayout();
        RefreshStatuses();
    }

    private void RefreshStatuses()
    {
        _processManager.Refresh();
        foreach (var item in _instances)
        {
            var key = item.Config.FolderName;
            item.StatusText = _stopRequested.Contains(key) ? "正在停止…" : _busy.Contains(key) ? "正在启动…" : _queuedStarts.Contains(key) ? "等待启动…" : _processManager.IsRunning(key)
                ? (_embedController.IsAttached(key) ? "运行中 · 已内嵌" : "运行中 · 独立窗口")
                : !File.Exists(item.Config.ExeFullPath) ? "程序不存在，请检查启动配置" : "已停止";
            if (_errors.TryGetValue(key, out var error)) item.StatusText += " · " + error;
            _cards.GetValueOrDefault(key)?.RefreshState(_processManager.IsRunning(key), _busy.Contains(key), _embedController.IsAttached(key), _queuedStarts.Contains(key));
        }
        EmptyInstancesHint.Visibility = _instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CardsScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardLayout();

    private void UpdateCardLayout()
    {
        if (InstanceGrid == null || CardsScroll == null) return;
        foreach (var card in _cards.Values) card.SetEmbeddedResolution(ThemeManager.EmbedWidth, ThemeManager.EmbedHeight);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var cardWidth = ThemeManager.EmbedWidth / dpi.DpiScaleX + 16;
        var viewport = CardsScroll.ViewportWidth > 0 ? CardsScroll.ViewportWidth : CardsScroll.ActualWidth;
        InstanceGrid.Width = Math.Max(cardWidth, viewport);
    }

    private void ChangeEmbedResolution(int width, int height)
    {
        ThemeManager.SetEmbedResolution(width, height);
        UpdateCardLayout();
        InstanceGrid.UpdateLayout();
        foreach (var card in _cards.Values) card.Slot.SetFill(true);
        ShowStatus($"内嵌分辨率已设为 {width} × {height}");
    }

    private async Task StartInstanceAsync(InstanceConfig cfg)
    {
        if (_closing) throw new InvalidOperationException("应用正在关闭");
        if (_processManager.IsRunning(cfg.FolderName)) return;
        if (!_busy.Add(cfg.FolderName)) throw new InvalidOperationException("实例正在操作中");
        cfg = new InstanceConfig
        {
            FolderName = cfg.FolderName, Remark = cfg.Remark, ExeFileName = cfg.ExeFileName,
            ExtraArgs = cfg.ExtraArgs, EmbedWindow = cfg.EmbedWindow,
        };
        _errors.Remove(cfg.FolderName);
        RefreshStatuses();
        try
        {
            var proc = await Task.Run(() => _processManager.Start(cfg));
            if (_closing || _stopRequested.Contains(cfg.FolderName)) { await _processManager.StopAsync(cfg.FolderName); return; }
            ShowStatus($"已启动 {cfg.Remark}（{cfg.FolderName}）");
            if (cfg.EmbedWindow) await _embedController.EmbedAsync(cfg, proc);
            else _embedController.GetSlot(cfg.FolderName)?.ShowExternal(cfg.FolderName);
        }
        catch (Exception ex)
        {
            _errors[cfg.FolderName] = ex.Message;
            throw;
        }
        finally
        {
            _busy.Remove(cfg.FolderName);
            _stopRequested.Remove(cfg.FolderName);
            if (!_closing) RefreshStatuses();
        }
    }

    private async Task StopInstanceAsync(string key)
    {
        _queuedStarts.Remove(key);
        if (_queuedStarts.Count == 0) _batchDelay?.Cancel();
        if (_busy.Contains(key)) _stopRequested.Add(key);
        try { _embedController.ReleaseSlot(key, false); }
        finally { await _processManager.StopAsync(key); }
        _errors.Remove(key);
        ShowStatus($"已停止 {key}");
        RefreshStatuses();
    }

    public static List<InstanceItem> SelectTargets(IEnumerable<InstanceItem> items, InstanceItem? selected)
    {
        var checkedItems = items.Where(i => i.IsSelected).ToList();
        return checkedItems.Count > 0 ? checkedItems : selected == null ? new() : new() { selected };
    }

    private async Task RunTargetsAsync(IEnumerable<InstanceItem> items, bool start)
    {
        var targets = items.ToArray();
        if (targets.Length == 0) { ShowStatus("请先勾选要操作的实例。", true); return; }
        if (start && _batchOperations > 0) { ShowStatus("批量操作尚未完成，请等待完成后重试。", true); return; }
        if (start) foreach (var item in targets) _queuedStarts.Add(item.Config.FolderName);
        if (start) _batchDelay = new CancellationTokenSource();
        _batchOperations++;
        RefreshStatuses();
        try
        {
            var tasks = new List<Task>();
            for (var i = 0; i < targets.Length && !_closing; i++)
            {
                var item = targets[i];
                if (start && !_queuedStarts.Remove(item.Config.FolderName)) continue;
                tasks.Add(GuardAsync(() => start ? StartInstanceAsync(item.Config) : StopInstanceAsync(item.Config.FolderName)));
                if (start && i < targets.Length - 1 && ThemeManager.BatchIntervalSeconds > 0)
                    try { await Task.Delay(ThemeManager.BatchIntervalSeconds * 1000, _batchDelay!.Token); }
                    catch (OperationCanceledException) { }
            }
            await Task.WhenAll(tasks);
        }
        finally
        {
            if (start) foreach (var item in targets) _queuedStarts.Remove(item.Config.FolderName);
            if (start) { _batchDelay?.Dispose(); _batchDelay = null; }
            _batchOperations--;
            if (!_closing) RefreshStatuses();
        }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { ShowStatus("操作已取消"); }
        catch (Exception ex) { ShowStatus(ex.Message, true); }
    }

    private void RunUi(Action action)
    {
        try { action(); } catch (Exception ex) { ShowStatus(ex.Message, true); }
    }

    private void ShowStatus(string message, bool error = false)
    {
        StatusText.Text = (error ? "操作失败：" : "") + message;
        if (error) StatusText.Foreground = System.Windows.Media.Brushes.IndianRed;
        else StatusText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        Log(message);
    }

    private async void BatchStartBtn_Click(object sender, RoutedEventArgs e) => await RunTargetsAsync(_instances.Where(i => i.IsSelected), true);
    private async void BatchStopBtn_Click(object sender, RoutedEventArgs e) => await RunTargetsAsync(_instances.Where(i => i.IsSelected), false);

    private void OpenInstanceConfiguration(string key)
    {
        var workspace = AppPaths.WorkspaceRoot;
        var cfg = FindConfig(key) ?? throw new ArgumentException("实例不存在");
        var dialog = new InstanceConfigWindow(cfg, candidate =>
        {
            if (!string.Equals(workspace, AppPaths.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区已切换，请关闭配置窗口后重新打开");
            var current = FindConfig(key) ?? throw new InvalidOperationException("实例已不存在，请关闭配置窗口后刷新");
            SaveConfig(current, candidate.ExtraArgs, candidate.ExeFileName, candidate.Remark);
            ShowStatus("配置已保存，将在下次启动时生效。");
        }) { Owner = this };
        dialog.ShowDialog();
    }

    private async Task CreateInstanceAsync(string remark, string folder, string exe)
    {
        if (_createCancellation != null) throw new InvalidOperationException("正在复制模板，请等待完成或取消");
        if (string.IsNullOrWhiteSpace(remark)) throw new ArgumentException("显示名称不能为空");
        var cfg = new InstanceConfig { Remark = remark.Trim(), FolderName = folder.Trim(), ExeFileName = exe };
        cfg.Validate();
        if (!InstanceStore.ScanTemplateExes().Contains(exe, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("请选择模板中的程序");
        var configs = InstanceStore.Scan(InstanceStore.Load());
        if (configs.Any(c => c.FolderName.Equals(cfg.FolderName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("实例文件夹名已存在，请使用新的名称");
        _createCancellation = new();
        try
        {
            await InstanceCreationService.CreateAsync(cfg, _processManager, new Progress<string>(file =>
            {
                if (_createCancellation != null && !_closing)
                {
                    StatusText.Text = $"正在复制 {cfg.FolderName}：{file}";
                    _createWindow?.ReportProgress(file);
                }
            }), _createCancellation.Token);
            ReloadInstances();
            ShowStatus($"已创建 {cfg.Remark}。可检查启动配置后启动。");
        }
        finally
        {
            _createCancellation.Dispose();
            _createCancellation = null;
        }
    }

    private void CreateBtn_Click(object sender, RoutedEventArgs e) => RunUi(() =>
    {
        _createWindow = new CreateInstanceWindow(CreateInstanceAsync, () => _createCancellation?.Cancel()) { Owner = this };
        try { _createWindow.ShowDialog(); }
        finally { _createWindow = null; }
    });

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => RunUi(() =>
    {
        _settingsWindow = new SettingsWindow(ChangeWorkspace, ChangeTemplate, ChangeEmbedResolution) { Owner = this };
        try { _settingsWindow.ShowDialog(); }
        finally
        {
            _settingsWindow = null;
            ReloadInstances();
            RefreshSettings();
        }
    });

    private void SaveConfig(InstanceConfig cfg, string extra, string? exe = null, string? remark = null)
    {
        var candidate = new InstanceConfig
        {
            FolderName = cfg.FolderName, Remark = remark?.Trim() ?? cfg.Remark,
            ExeFileName = exe?.Trim() ?? cfg.ExeFileName, ExtraArgs = extra,
            EmbedWindow = cfg.EmbedWindow, LastExitAbnormal = cfg.LastExitAbnormal,
        };
        if (string.IsNullOrWhiteSpace(candidate.Remark)) throw new ArgumentException("显示名称不能为空");
        candidate.Validate();
        InstanceStore.Save(_instances.Select(i => ReferenceEquals(i.Config, cfg) ? candidate : i.Config));
        cfg.ExtraArgs = candidate.ExtraArgs;
        cfg.ExeFileName = candidate.ExeFileName; cfg.Remark = candidate.Remark;
        foreach (var item in _instances) item.RefreshConfig();
        RefreshStatuses();
    }

    private void EnsureWorkspaceIdle()
    {
        if (_busy.Count > 0 || _batchOperations > 0 || _createCancellation != null || _processManager.RunningFolders.Count > 0)
            throw new InvalidOperationException("请先停止所有实例，并等待当前操作完成，再更改工作区或模板");
    }

    private void ChangeWorkspace(string path)
    {
        EnsureWorkspaceIdle();
        // Explicit test workspaces must never overwrite the user's persisted workspace choice.
        AppPaths.ConfigureWorkspace(path, persist: string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIHOST_WORKSPACE")));
        _embedController.ReleaseAll();
        _errors.Clear();
        _instances.Clear();
        _cards.Clear();
        InstanceGrid.Children.Clear();
        ThemeManager.Initialize();
        ReloadInstances();
        RefreshSettings();
        ShowStatus("已切换工作区：" + AppPaths.WorkspaceRoot);
    }

    private void ChangeTemplate(string path)
    {
        EnsureWorkspaceIdle();
        AppPaths.ConfigureTemplate(path);
        RefreshSettings();
        ReloadInstances();
        ShowStatus("已应用模板目录");
    }

    private void RefreshSettings()
    {
        WorkspaceSummary.Text = AppPaths.WorkspaceRoot;
        WorkspaceSummary.ToolTip = AppPaths.WorkspaceRoot;
        _settingsWindow?.RefreshValues();
        _createWindow?.RefreshValues();
    }

    private void OpenLogBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is { IsLoaded: true }) { _logWindow.Activate(); return; }
        _logWindow = new LogWindow { Owner = this };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }
    private static void Log(string message) => LogService.Log(message);
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_confirmedShutdown && (_processManager.RunningFolders.Count > 0 || _busy.Count > 0 || _createCancellation != null || _batchOperations > 0))
        {
            if (System.Windows.MessageBox.Show(this, "退出将停止本次启动的所有实例，并取消正在进行的操作。是否退出？", "退出 MultiHost",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
        }
        _closing = true;
        // 持久化主窗口尺寸（仅正常窗口状态，避免记下最大化/最小化的尺寸）。
        if (WindowState == WindowState.Normal && ActualWidth > 0 && ActualHeight > 0)
            try { new ShellSettingsStore(AppPaths.WorkspaceRoot).SaveWindowSize(ActualWidth, ActualHeight); } catch { }
        _queuedStarts.Clear();
        _batchDelay?.Cancel();
        _createCancellation?.Cancel();
        _pipe?.Dispose();
        _statusTimer.Stop();
        try { _embedController.ReleaseAll(); }
        finally { _processManager.Dispose(); }
        _logWindow?.Close();
        base.OnClosing(e);
    }
}

