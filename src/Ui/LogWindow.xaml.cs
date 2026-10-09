using System.Windows;

namespace MultiHost;

/// <summary>
/// 独立日志窗口：订阅 LogService，实时滚动显示。
/// </summary>
public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        // 先回填历史日志
        LogText.Text = string.Join(Environment.NewLine, LogService.Entries);
        LogText.ScrollToEnd();
        LogService.EntryAdded += OnEntryAdded;
        Closed += (_, _) => LogService.EntryAdded -= OnEntryAdded;
    }

    private void OnEntryAdded(string line)
    {
        // LogService 回调可能在非 UI 线程
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnEntryAdded(line));
            return;
        }
        LogText.AppendText(line + Environment.NewLine);
        LogText.ScrollToEnd();
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e) => LogText.Clear();

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (LogText.Text.Length > 0)
            System.Windows.Clipboard.SetText(LogText.Text);
    }
}
