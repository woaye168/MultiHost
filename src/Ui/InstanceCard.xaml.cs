using System.Windows;

namespace MultiHost;

public partial class InstanceCard : System.Windows.Controls.UserControl
{
    public InstanceItem Item => (InstanceItem)DataContext;
    public EmbedSlot Slot { get; }
    public event Action<string>? StartRequested;
    public event Action<string>? StopRequested;
    public event Action<string>? ConfigureRequested;
    public event Action<string>? PopOutRequested;
    public event Action<string>? ReEmbedRequested;

    public InstanceCard(InstanceItem item, EmbedSlot slot)
    {
        InitializeComponent();
        DataContext = item;
        Slot = slot;
        WindowContent.Content = slot;
    }

    public void RefreshState(bool running, bool busy, bool attached, bool queued)
    {
        StartBtn.IsEnabled = !running && !busy && !queued;
        StopBtn.IsEnabled = running || busy || queued;
        ActivateBtn.IsEnabled = running && Win32Api.IsWindow(Slot.LastHwnd);
        PopOutBtn.IsEnabled = running && attached;
        ReEmbedBtn.IsEnabled = running && !attached && !busy;
    }

    public void SetEmbeddedResolution(int width, int height)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        // Settings describe native pixels; WPF measures in device-independent units.
        WindowContent.Width = width / dpi.DpiScaleX;
        WindowContent.Height = height / dpi.DpiScaleY;
        Width = WindowContent.Width + 16; // border, content inset and card spacing
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke(Item.Config.FolderName);
    private void StopBtn_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke(Item.Config.FolderName);
    private void ConfigureBtn_Click(object sender, RoutedEventArgs e) => ConfigureRequested?.Invoke(Item.Config.FolderName);
    private void PopOutBtn_Click(object sender, RoutedEventArgs e) => PopOutRequested?.Invoke(Item.Config.FolderName);
    private void ReEmbedBtn_Click(object sender, RoutedEventArgs e) => ReEmbedRequested?.Invoke(Item.Config.FolderName);
    private void ActivateBtn_Click(object sender, RoutedEventArgs e) => Slot.ActivateChild();
}
