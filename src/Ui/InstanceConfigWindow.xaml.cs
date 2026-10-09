using System.Windows;

namespace MultiHost;

public partial class InstanceConfigWindow : Window
{
    private readonly InstanceConfig _original;
    private readonly Action<InstanceConfig> _save;

    public InstanceConfigWindow(InstanceConfig config, Action<InstanceConfig> save)
    {
        _save = save;
        _original = new InstanceConfig
        {
            FolderName = config.FolderName, Remark = config.Remark, ExeFileName = config.ExeFileName,
            ExtraArgs = config.ExtraArgs, EmbedWindow = config.EmbedWindow,
            LastExitAbnormal = config.LastExitAbnormal,
        };
        InitializeComponent();
        FolderHint.Text = config.FolderName;
        InstanceRemarkBox.Text = _original.Remark;
        InstanceExeBox.Text = _original.ExeFileName;
        ExtraArgsBox.Text = _original.ExtraArgs;
        try
        {
            InstanceExeBox.ItemsSource = InstanceStore.ScanTemplateExes(_original.FolderFullPath);
            InstanceExeBox.Text = _original.ExeFileName;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    public static InstanceConfig BuildCandidate(InstanceConfig original, string remark, string exe, string args)
    {
        if (string.IsNullOrWhiteSpace(remark)) throw new ArgumentException("显示名称不能为空");
        var candidate = new InstanceConfig
        {
            FolderName = original.FolderName, Remark = remark.Trim(), ExeFileName = exe.Trim(),
            ExtraArgs = args, EmbedWindow = original.EmbedWindow, LastExitAbnormal = original.LastExitAbnormal,
        };
        candidate.Validate();
        return candidate;
    }

    private void SaveCfgBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = BuildCandidate(_original, InstanceRemarkBox.Text, InstanceExeBox.Text, ExtraArgsBox.Text);
            _save(candidate);
            Close();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ShowError(string message)
    {
        FeedbackText.Text = message;
        FeedbackText.Visibility = Visibility.Visible;
    }
}
