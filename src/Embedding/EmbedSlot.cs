using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using Panel = System.Windows.Forms.Panel;

namespace MultiHost;

public class EmbedSlot : Grid
{
    public event Action<string>? ReEmbedRequested;
    private readonly WindowsFormsHost _host;
    private readonly Panel _panel;
    private readonly TextBlock _message;
    private readonly DispatcherTimer _resize;
    private Window? _popup;
    private WindowsFormsHost? _popupHost;
    private Panel? _popupPanel;
    private IntPtr _child;
    private bool _inputAttached;
    private List<uint> _inputThreads = [];
    private long _style;
    private (int Width, int Height) _original;
    private bool _hasOriginal;
    private Win32Api.RECT _originalBounds;
    private bool _disposed;
    public string? InstanceKey { get; private set; }
    public IntPtr LastHwnd => _child;
    public IntPtr ExpectedParent => _popupPanel?.Handle ?? _panel.Handle;
    public bool IsAttached => Win32Api.IsWindow(_child) && _popup == null && Win32Api.GetParent(_child) == _panel.Handle;
    public bool IsPoppedOut => _popup != null;
    public bool FillMode { get; private set; } = true;

    public EmbedSlot()
    {
        ClipToBounds = true;
        _panel = new Panel { BackColor = System.Drawing.Color.Black, Dock = System.Windows.Forms.DockStyle.Fill };
        _host = new ClippedWindowsFormsHost { Child = _panel, Visibility = Visibility.Collapsed };
        Children.Add(_host);
        _message = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12)
        };
        Children.Add(_message);
        _resize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _resize.Tick += (_, _) => { _resize.Stop(); FitChild(); };
        _panel.Resize += (_, _) => { _resize.Stop(); _resize.Start(); };
    }
    public void ShowStopped(string key) => ShowMessage(key, "已停止");
    public void ShowExternal(string key) => ShowMessage(key, "已在独立窗口运行，点击收回可内嵌");
    public void ShowWaiting(string key) => ShowMessage(key, "等待实例窗口");
    public void ShowFailed(string key, string? reason = null) => ShowMessage(key, reason ?? "未能内嵌，可点击收回重试；详情见日志");
    private void ShowMessage(string key, string message)
    {
        InstanceKey = key;
        _message.Text = message;
        _host.Visibility = Visibility.Collapsed;
        _message.Visibility = Visibility.Visible;
    }

    public void Attach(string key, IntPtr hwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_child == hwnd && IsAttached)
        {
            SetFill(true);
            return;
        }
        bool first = hwnd != _child;
        if (first)
        {
            if (_child != IntPtr.Zero) ResetContent();
            // 已有预捕获的原始尺寸（嵌入前调整客户区会改变当前尺寸，需用最初捕获值作弹出/还原基准）。
            if (!_hasOriginal) CaptureOriginal(hwnd);
        }
        long style = WindowEmbedder.Embed(hwnd, _panel.Handle);
        MergeInputThreads(key, hwnd);
        if (first) _style = style;
        _child = hwnd;
        InstanceKey = key;
        FillMode = true;
        ClosePopup();
        _host.Visibility = Visibility.Visible;
        _message.Visibility = Visibility.Collapsed;
        UpdateLayout();
        FitChild();
        // 部分应用（内嵌浏览器/引擎）会异步创建属于其他线程的子窗口，延迟补合并输入队列。
        ScheduleInputMerge(key, hwnd, 1500);
        ScheduleInputMerge(key, hwnd, 5000);
    }
    /// <summary>捕获应用原始客户区尺寸与窗口位置（弹出/还原基准），只记一次。</summary>
    public void CaptureOriginal(IntPtr hwnd)
    {
        if (!WindowNative.GetClientRect(hwnd, out var rect)) throw new InvalidOperationException("无法读取原始客户区");
        if (!Win32Api.GetWindowRect(hwnd, out _originalBounds)) throw new InvalidOperationException("无法读取原始窗口位置");
        _original = (rect.Right - rect.Left, rect.Bottom - rect.Top);
        _hasOriginal = true;
    }
    private void ScheduleInputMerge(string key, IntPtr hwnd, int delayMs)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_disposed || _child != hwnd || !Win32Api.IsWindow(hwnd)) return;
            MergeInputThreads(key, hwnd);
        };
        timer.Start();
    }
    public void Detach()
    {
        if (!IsAttached) return;
        _resize.Stop();
        _popupPanel = new Panel { Dock = System.Windows.Forms.DockStyle.Fill, BackColor = System.Drawing.Color.Black };
        _popupHost = new WindowsFormsHost { Child = _popupPanel };
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        _popupHost.Width = _original.Width / dpi.DpiScaleX;
        _popupHost.Height = _original.Height / dpi.DpiScaleY;
        _popup = new Window { Title = InstanceKey, SizeToContent = SizeToContent.WidthAndHeight, Content = _popupHost };
        _popup.Closing += OnPopupClosing;
        _popup.Show();
        WindowEmbedder.Embed(_child, _popupPanel.Handle);
        MoveChildAligningClient(0, 0, _original.Width, _original.Height);
        _popup.SizeToContent = SizeToContent.Manual;
        _popupHost.Width = double.NaN;
        _popupHost.Height = double.NaN;
        _popupPanel.Resize += (_, _) =>
        {
            if (_popupPanel is not { } panel || !Win32Api.IsWindow(_child)) return;
            var size = panel.ClientSize;
            if (size.Width > 0 && size.Height > 0) MoveChildAligningClient(0, 0, size.Width, size.Height);
        };
        _host.Visibility = Visibility.Collapsed;
        _message.Visibility = Visibility.Visible;
        _message.Text = "已弹出，关闭独立宿主可收回";
    }
    private void OnPopupClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        if (InstanceKey is { } key) _ = Dispatcher.BeginInvoke(new Action(() => ReEmbedRequested?.Invoke(key)));
    }
    public void ClosePopout() => _popup?.Close();
    private void ClosePopup()
    {
        if (_popup == null) return;
        var popup = _popup;
        var host = _popupHost;
        _popup = null; _popupHost = null; _popupPanel = null;
        popup.Closing -= OnPopupClosing;
        try { popup.Close(); }
        finally { host?.Dispose(); }
    }
    public void ResetContent()
    {
        _resize.Stop();
        try
        {
            WindowNative.DetachInputDeep(_inputThreads);
            _inputThreads = [];
            _inputAttached = false;
            if (Win32Api.IsWindow(_child))
            {
                WindowEmbedder.Unembed(_child, _style);
                if (!Win32Api.MoveWindow(_child, _originalBounds.Left, _originalBounds.Top,
                    _originalBounds.Right - _originalBounds.Left, _originalBounds.Bottom - _originalBounds.Top, true))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "还原窗口位置失败");
            }
        }
        catch (System.ComponentModel.Win32Exception) when (!Win32Api.IsWindow(_child))
        {
            // Applications may destroy their own window while its original style is restored.
        }
        finally
        {
            _child = IntPtr.Zero;
            _style = 0;
            _original = default;
            _hasOriginal = false;
            _originalBounds = default;
            FillMode = true;
            try { ClosePopup(); }
            finally { ShowStopped(InstanceKey ?? ""); }
        }
    }
    public void ResetOnly()
    {
        if (_disposed) return;
        try { ResetContent(); }
        finally
        {
            _disposed = true;
            InstanceKey = null;
            _host.Dispose();
        }
    }
    public void SetFill(bool fill)
    {
        FillMode = fill;
        FitChild();
    }
    private void FitChild()
    {
        if (!IsAttached) return;
        var size = _panel.ClientSize;
        int width = FillMode ? size.Width : _original.Width;
        int height = FillMode ? size.Height : _original.Height;
        if (width <= 0 || height <= 0) return;
        int x = Math.Max(0, (size.Width - width) / 2), y = Math.Max(0, (size.Height - height) / 2);
        MoveChildAligningClient(x, y, width, height);
    }
    // 内嵌保留标题栏/边框，用 AdjustWindowRectEx 求出客户区相对窗口原点的偏移，把窗口整体上移/左移，
    // 让客户区正好对齐宿主可视区（标题栏被裁在边界外）。这样游戏画面和点击命中都对齐。
    private void MoveChildAligningClient(int x, int y, int width, int height)
    {
        long style = Win32Api.GetWindowLong(_child, Win32Api.GWL_STYLE);
        uint exStyle = unchecked((uint)Win32Api.GetWindowLong(_child, Win32Api.GWL_EXSTYLE));
        var rect = new Win32Api.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        if (Win32Api.AdjustWindowRectEx(ref rect, unchecked((uint)style), false, exStyle))
        {
            x = rect.Left; y = rect.Top;
            width = rect.Right - rect.Left; height = rect.Bottom - rect.Top;
        }
        if (Win32Api.MoveWindow(_child, x, y, width, height, true))
            NotifyChildResized();
        else
            LogService.Log("设置内嵌窗口尺寸失败");
    }
    // SDL 等引擎在外部程序直接 MoveWindow 时不一定触发其内部尺寸重算（auto_scale 仍按旧逻辑分辨率
    // 做命中判定，导致点击错位）。补发完整的 sizing 消息序列，模拟用户拖动边框，强制引擎重算。
    private void NotifyChildResized()
    {
        if (!Win32Api.IsWindow(_child)) return;
        Win32Api.SendMessage(_child, Win32Api.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
        WindowNative.GetClientRect(_child, out var c);
        int w = c.Right - c.Left, h = c.Bottom - c.Top;
        Win32Api.SendMessage(_child, Win32Api.WM_SIZE, (IntPtr)Win32Api.SIZE_RESTORED, (IntPtr)(w | (h << 16)));
        Win32Api.SendMessage(_child, Win32Api.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
    }
    public void ActivateChild()
    {
        if (!Win32Api.IsWindow(_child)) return;
        // 应用可能在内嵌后新建其他线程的窗口（如内嵌浏览器），激活时补充合并。
        MergeInputThreads(InstanceKey ?? "", _child);
        // 键盘焦点：把主窗口提到前台，再激活并聚焦到内嵌窗口，并显式通知它获得焦点。
        // 跨进程需保持输入队列合并（leaveAttached=true），否则 SDL 等引擎收不到键盘。
        var root = Win32Api.GetAncestor(_child, Win32Api.GA_ROOT);
        Win32Api.SetForegroundWindow(root);
        Win32Api.SetActiveWindow(_child);
        if (!WindowNative.Focus(_child, leaveAttached: true)) LogService.Log($"{InstanceKey} 焦点验证失败");
        Win32Api.SendMessage(_child, 0x0007 /*WM_SETFOCUS*/, IntPtr.Zero, IntPtr.Zero);
    }
    private void MergeInputThreads(string key, IntPtr hwnd)
    {
        var merged = WindowNative.AttachInputDeep(hwnd);
        foreach (uint thread in merged)
            if (!_inputThreads.Contains(thread)) _inputThreads.Add(thread);
        _inputAttached = _inputThreads.Count > 0 || WindowNative.AttachInput(hwnd);
        if (!_inputAttached) LogService.Log($"{key} 输入队列合并失败，内嵌窗口可能无法交互");
        else if (merged.Count > 1) LogService.Log($"{key} 已合并 {merged.Count} 个窗口线程的输入队列");
    }
    public bool InputAttached => _inputAttached;
    public bool HasOriginal => _hasOriginal;
    public object Snapshot()
    {
        WindowNative.GetClientRect(_child, out var rect);
        Win32Api.GetWindowThreadProcessId(_child, out var pid);
        return new { Hwnd = _child.ToInt64(), Pid = pid, Parent = Win32Api.GetParent(_child).ToInt64(),
            ExpectedParent = ExpectedParent.ToInt64(), Embedded = IsAttached, PoppedOut = IsPoppedOut,
            InputAttached = _inputAttached,
            Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top,
            OriginalWidth = _original.Width, OriginalHeight = _original.Height,
            HostWidth = _panel.ClientSize.Width, HostHeight = _panel.ClientSize.Height, FillMode };
    }

    // HWND children do not participate in WPF clipping; constrain their native region
    // so scrolling cards cannot cover the toolbar or adjacent cards.
    private sealed class ClippedWindowsFormsHost : WindowsFormsHost
    {
        private (int Left, int Top, int Right, int Bottom)? _lastClip;

        public ClippedWindowsFormsHost() => LayoutUpdated += (_, _) => UpdateClip();

        protected override void OnWindowPositionChanged(Rect bounds)
        {
            base.OnWindowPositionChanged(bounds);
            UpdateClip();
        }

        private void UpdateClip()
        {
            if (Handle == IntPtr.Zero || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return;
            var visible = new Rect(0, 0, ActualWidth, ActualHeight);
            for (DependencyObject? parent = System.Windows.Media.VisualTreeHelper.GetParent(this); parent != null;
                 parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
            {
                if (parent is not FrameworkElement element || (!element.ClipToBounds && element is not ScrollContentPresenter)) continue;
                var inverse = TransformToAncestor(element).Inverse;
                if (inverse != null) visible.Intersect(inverse.TransformBounds(new Rect(element.RenderSize)));
            }
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var clip = visible.IsEmpty ? (0, 0, 0, 0) :
                ((int)Math.Ceiling(visible.Left * dpi.DpiScaleX), (int)Math.Ceiling(visible.Top * dpi.DpiScaleY),
                 (int)Math.Floor(visible.Right * dpi.DpiScaleX), (int)Math.Floor(visible.Bottom * dpi.DpiScaleY));
            if (_lastClip == clip) return;
            var region = CreateRectRgn(clip.Item1, clip.Item2, clip.Item3, clip.Item4);
            if (region == IntPtr.Zero) return;
            if (SetWindowRgn(Handle, region, true) == 0) DeleteObject(region);
            else _lastClip = clip;
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr value);
    }
}
