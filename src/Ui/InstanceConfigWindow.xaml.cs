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
            Width = config.Width, Height = config.Height, ExtraArgs = config.ExtraArgs,
            EmbedWindow = config.EmbedWindow, IncludeSizeArguments = config.IncludeSizeArguments,
            LastExitAbnormal = config.LastExitAbnormal,
        };
        InitializeComponent();
        FolderHint.Text = config.FolderName;
        InstanceRemarkBox.Text = _original.Remark;
        InstanceExeBox.Text = _original.ExeFileName;
        WidthBox.Text = _original.Width.ToString();
        HeightBox.Text = _original.Height.ToString();
        ExtraArgsBox.Text = _original.ExtraArgs;
        EmbedCheck.IsChecked = _original.EmbedWindow;
        SizeArgsCheck.IsChecked = _original.IncludeSizeArguments;
        try
        {
            InstanceExeBox.ItemsSource = InstanceStore.ScanTemplateExes(_original.FolderFullPath);
            InstanceExeBox.Text = _original.ExeFileName;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    public static InstanceConfig BuildCandidate(InstanceConfig original, string remark, string exe,
        string widthText, string heightText, string args, bool embed, bool includeSize)
    {
        if (string.IsNullOrWhiteSpace(remark)) throw new ArgumentException("显示名称不能为空");
        if (!int.TryParse(widthText, out var width) || !int.TryParse(heightText, out var height))
            throw new ArgumentException("宽度和高度必须为整数");
        var candidate = new InstanceConfig
        {
            FolderName = original.FolderName, Remark = remark.Trim(), ExeFileName = exe.Trim(),
            Width = width, Height = height, ExtraArgs = args, EmbedWindow = embed,
            IncludeSizeArguments = includeSize, LastExitAbnormal = original.LastExitAbnormal,
        };
        candidate.Validate();
        return candidate;
    }

    private void SaveCfgBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = BuildCandidate(_original, InstanceRemarkBox.Text, InstanceExeBox.Text,
                WidthBox.Text, HeightBox.Text, ExtraArgsBox.Text,
                EmbedCheck.IsChecked == true, SizeArgsCheck.IsChecked == true);
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
