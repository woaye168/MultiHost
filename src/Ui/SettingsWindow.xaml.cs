using System.IO;
using System.Windows;
using System.Windows.Controls;
using Brushes = System.Windows.Media.Brushes;

namespace MultiHost;

public partial class SettingsWindow : Window
{
    private readonly Action<string> _changeWorkspace;
    private readonly Action<string> _changeTemplate;
    private readonly Action<int, int> _changeEmbedResolution;
    private bool _loading = true;

    public SettingsWindow(Action<string> changeWorkspace, Action<string> changeTemplate,
        Action<int, int>? changeEmbedResolution = null)
    {
        _changeWorkspace = changeWorkspace;
        _changeTemplate = changeTemplate;
        _changeEmbedResolution = changeEmbedResolution ?? ThemeManager.SetEmbedResolution;
        InitializeComponent();
        RefreshValues();
    }

    public void RefreshValues()
    {
        _loading = true;
        try
        {
            WorkspaceBox.Text = AppPaths.WorkspaceRoot;
            TemplateBox.Text = AppPaths.TemplateDir;
            ThemeCombo.SelectedIndex = (int)ThemeManager.Mode;
            BatchIntervalBox.Text = ThemeManager.BatchIntervalSeconds.ToString();
            EmbedWidthBox.Text = ThemeManager.EmbedWidth.ToString();
            EmbedHeightBox.Text = ThemeManager.EmbedHeight.ToString();
            EmbedResolutionCombo.SelectedIndex = ResolutionPreset(ThemeManager.EmbedWidth, ThemeManager.EmbedHeight);
        }
        finally { _loading = false; }
    }

    private void BrowseDirectory(System.Windows.Controls.TextBox box, string title) => Run(() =>
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (Directory.Exists(box.Text)) picker.InitialDirectory = box.Text;
        if (picker.ShowDialog(this) == true) box.Text = picker.FolderName;
    });

    private void BrowseWorkspaceBtn_Click(object sender, RoutedEventArgs e) => BrowseDirectory(WorkspaceBox, "选择实例工作区");
    private void BrowseTemplateBtn_Click(object sender, RoutedEventArgs e) => BrowseDirectory(TemplateBox, "选择应用模板目录");

    private void ApplyWorkspaceBtn_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        _changeWorkspace(WorkspaceBox.Text.Trim());
        RefreshValues();
        ShowFeedback("已切换工作区。");
    });

    private void ApplyTemplateBtn_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        _changeTemplate(TemplateBox.Text.Trim());
        RefreshValues();
        ShowFeedback("已应用模板目录。");
    });

    private void ApplyBatchIntervalBtn_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!int.TryParse(BatchIntervalBox.Text, out var seconds) || seconds is < 0 or > 30)
            throw new ArgumentException("批量启动间隔必须是 0 至 30 秒的整数");
        ThemeManager.SetBatchInterval(seconds);
        ShowFeedback("已保存批量启动间隔。");
    });

    private void ApplyEmbedResolutionBtn_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!int.TryParse(EmbedWidthBox.Text, out var width) || !int.TryParse(EmbedHeightBox.Text, out var height))
            throw new ArgumentException("内嵌窗口宽度和高度必须为整数");
        ShellSettingsStore.ValidateEmbedResolution(width, height);
        _changeEmbedResolution(width, height);
        ShowFeedback($"已应用内嵌分辨率：{width} × {height}。");
    });

    private void EmbedResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EmbedResolutionCombo.SelectedIndex is < 0 or > 1) return;
        _loading = true;
        try
        {
            var preset = EmbedResolutionCombo.SelectedIndex;
            EmbedWidthBox.Text = preset == 0 ? "800" : "1120";
            EmbedHeightBox.Text = preset == 0 ? "360" : "540";
        }
        finally { _loading = false; }
    }

    private void EmbedResolutionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            EmbedResolutionCombo.SelectedIndex = int.TryParse(EmbedWidthBox.Text, out var width) &&
                int.TryParse(EmbedHeightBox.Text, out var height) ? ResolutionPreset(width, height) : 2;
        }
        finally { _loading = false; }
    }

    private static int ResolutionPreset(int width, int height) => (width, height) switch
    {
        (800, 360) => 0,
        (1120, 540) => 1,
        _ => 2,
    };

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<AppThemeMode>(item.Tag as string, out var mode)) return;
        try
        {
            ThemeManager.Apply(mode);
            ShowFeedback("已应用主题。");
        }
        catch (Exception ex)
        {
            _loading = true;
            try { ThemeCombo.SelectedIndex = (int)ThemeManager.Mode; }
            finally { _loading = false; }
            ShowFeedback(ex.Message, true);
        }
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowFeedback(ex.Message, true); }
    }

    private void ShowFeedback(string message, bool error = false)
    {
        FeedbackText.Text = message;
        FeedbackText.Visibility = Visibility.Visible;
        if (error) FeedbackText.Foreground = Brushes.IndianRed;
        else FeedbackText.ClearValue(TextBlock.ForegroundProperty);
    }
}
