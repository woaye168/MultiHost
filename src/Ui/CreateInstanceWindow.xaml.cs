using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Brushes = System.Windows.Media.Brushes;

namespace MultiHost;

public partial class CreateInstanceWindow : Window
{
    private readonly Func<string, string, string, Task> _create;
    private readonly Action _cancel;
    private bool _busy;
    private bool _closeAfterCancellation;
    private bool _cancellationRequested;

    public CreateInstanceWindow(Func<string, string, string, Task> create, Action cancel)
    {
        _create = create;
        _cancel = cancel;
        InitializeComponent();
        RefreshValues();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    public void RefreshValues()
    {
        if (_busy) return;
        try
        {
            var selected = ExeCombo.SelectedItem as string;
            var exes = InstanceStore.ScanTemplateExes();
            ExeCombo.ItemsSource = exes;
            ExeCombo.SelectedItem = exes.FirstOrDefault(exe => string.Equals(exe, selected, StringComparison.OrdinalIgnoreCase))
                ?? exes.FirstOrDefault();
            TemplatePath.Text = AppPaths.TemplateDir;
            TemplateHint.Text = exes.Count == 0
                ? "模板中没有可执行程序。请先在「设置」选择包含 .exe 的应用目录。"
                : $"模板中有 {exes.Count} 个程序，请确认要启动的入口。";
            CreateBtn.IsEnabled = exes.Count > 0;
        }
        catch (Exception ex)
        {
            CreateBtn.IsEnabled = false;
            ShowFeedback(ex.Message, true);
        }
    }

    public void ReportProgress(string file)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportProgress(file));
            return;
        }
        if (_busy && !_cancellationRequested) ShowFeedback("正在复制：" + file);
    }

    private async void CreateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        bool created = false;
        try
        {
            var remark = RemarkBox.Text.Trim();
            var folder = FolderBox.Text.Trim();
            var exe = ExeCombo.SelectedItem as string ?? "";
            if (string.IsNullOrWhiteSpace(remark)) throw new ArgumentException("显示名称不能为空");
            new InstanceConfig { Remark = remark, FolderName = folder, ExeFileName = exe }.Validate();
            if (!InstanceStore.ScanTemplateExes().Contains(exe, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("所选程序已不在模板中，请刷新后重新选择。");
            _busy = true;
            _cancellationRequested = false;
            CreateForm.IsEnabled = CreateBtn.IsEnabled = false;
            CloseBtn.Visibility = Visibility.Collapsed;
            CancelCreateBtn.Visibility = Visibility.Visible;
            CancelCreateBtn.IsEnabled = true;
            ShowFeedback("正在复制模板，请稍候……");
            await _create(remark, folder, exe);
            created = true;
        }
        catch (OperationCanceledException) { ShowFeedback("复制已取消，可修改后重新创建。"); }
        catch (Exception ex) { ShowFeedback(ex.Message, true); }
        finally
        {
            _busy = false;
            CreateForm.IsEnabled = true;
            CreateBtn.IsEnabled = ExeCombo.Items.Count > 0;
            CloseBtn.Visibility = Visibility.Visible;
            CancelCreateBtn.Visibility = Visibility.Collapsed;
            if (created || _closeAfterCancellation) Close();
        }
    }

    private void RequestCancellation()
    {
        if (_cancellationRequested) return;
        try
        {
            _cancel();
            _cancellationRequested = true;
            CancelCreateBtn.IsEnabled = false;
            ShowFeedback("正在取消复制，请稍候……");
        }
        catch (Exception ex) { ShowFeedback(ex.Message, true); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            _closeAfterCancellation = true;
            RequestCancellation();
        }
        base.OnClosing(e);
    }

    private void ShowFeedback(string message, bool error = false)
    {
        FeedbackText.Text = message;
        FeedbackText.Visibility = Visibility.Visible;
        if (error) FeedbackText.Foreground = Brushes.IndianRed;
        else FeedbackText.ClearValue(TextBlock.ForegroundProperty);
    }

    private void RefreshTemplateBtn_Click(object sender, RoutedEventArgs e) => RefreshValues();
    private void CancelCreateBtn_Click(object sender, RoutedEventArgs e) => RequestCancellation();
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
