using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace MultiHost.Tests;

[Collection("Process workspace")]
public sealed class DialogTests
{
    [Fact]
    public void EditingInstanceConfigurationProducesIndependentValidatedCandidate()
    {
        var original = new InstanceConfig
        {
            FolderName = "one", Remark = "Original", ExeFileName = "original.exe",
            ExtraArgs = "--old", EmbedWindow = true, LastExitAbnormal = true,
        };

        var candidate = InstanceConfigWindow.BuildCandidate(original, " New name ", " bin/app.exe ",
            "--profile \"new profile\"");

        Assert.NotSame(original, candidate);
        Assert.Equal("one", candidate.FolderName);
        Assert.Equal("New name", candidate.Remark);
        Assert.Equal("bin/app.exe", candidate.ExeFileName);
        Assert.Equal("--profile \"new profile\"", candidate.BuildArguments());
        Assert.True(candidate.EmbedWindow);
        Assert.True(candidate.LastExitAbnormal);
        Assert.Equal("Original", original.Remark);
        Assert.Equal("original.exe", original.ExeFileName);
        Assert.Equal("--old", original.ExtraArgs);
        Assert.True(original.EmbedWindow);
    }

    [Theory]
    [InlineData("", "app.exe")]
    [InlineData("Name", "../outside.exe")]
    public void InvalidConfigurationEditsLeaveOriginalUnchanged(string name, string exe)
    {
        var original = new InstanceConfig { FolderName = "one", Remark = "Original", ExeFileName = "original.exe" };
        Assert.Throws<ArgumentException>(() => InstanceConfigWindow.BuildCandidate(original, name, exe, "--new"));
        Assert.Equal("Original", original.Remark);
        Assert.Equal("original.exe", original.ExeFileName);
        Assert.Empty(original.ExtraArgs);
    }

    [Fact]
    public Task FailedConfigSaveKeepsUserInputAndOriginalConfiguration() => OnUiThreadAsync(() =>
    {
        var original = new InstanceConfig { FolderName = "one", Remark = "Original", ExeFileName = "app.exe" };
        var attempts = 0;
        var dialog = new InstanceConfigWindow(original, candidate =>
        {
            attempts++;
            Assert.Equal("Changed", candidate.Remark);
            throw new IOException("Configuration is read-only");
        });
        try
        {
            ((TextBox)dialog.FindName("InstanceRemarkBox")).Text = "Changed";
            ((Button)dialog.FindName("SaveCfgBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, attempts);
            Assert.Equal("Changed", ((TextBox)dialog.FindName("InstanceRemarkBox")).Text);
            Assert.Equal("Original", original.Remark);
            Assert.Contains("read-only", ((TextBlock)dialog.FindName("FeedbackText")).Text);
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task CreateFailureRestoresInputsForRetry() => WithTemplateAsync(() =>
    {
        var dialog = new CreateInstanceWindow((_, _, _) => Task.FromException(new IOException("copy failed")), () => { });
        try
        {
            SetCreateInput(dialog);
            ((Button)dialog.FindName("CreateBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(((Button)dialog.FindName("CreateBtn")).IsEnabled);
            Assert.True(((StackPanel)dialog.FindName("CreateForm")).IsEnabled);
            Assert.Equal("Name", ((TextBox)dialog.FindName("RemarkBox")).Text);
            Assert.Equal("new-instance", ((TextBox)dialog.FindName("FolderBox")).Text);
            Assert.Contains("copy failed", ((TextBlock)dialog.FindName("FeedbackText")).Text);
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ClosingDuringCopyWaitsForCancellationAndDoesNotDuplicateRequests() => WithTemplateAsync(async () =>
    {
        var copying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCount = 0;
        var dialog = new CreateInstanceWindow((_, _, _) => copying.Task, () => cancellationCount++);
        dialog.Closed += (_, _) => closed.TrySetResult();
        try
        {
            SetCreateInput(dialog);
            ((Button)dialog.FindName("CreateBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(((Button)dialog.FindName("CreateBtn")).IsEnabled);
            dialog.ReportProgress("bin/app.exe");
            Assert.Contains("bin/app.exe", ((TextBlock)dialog.FindName("FeedbackText")).Text);
            dialog.Close();
            dialog.Close();
            Assert.Equal(1, cancellationCount);
            Assert.False(closed.Task.IsCompleted);
            dialog.ReportProgress("late-progress.exe");
            Assert.DoesNotContain("late-progress", ((TextBlock)dialog.FindName("FeedbackText")).Text);
            copying.TrySetCanceled();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { copying.TrySetCanceled(); }
    });

    private static void SetCreateInput(CreateInstanceWindow dialog)
    {
        ((TextBox)dialog.FindName("RemarkBox")).Text = "Name";
        ((TextBox)dialog.FindName("FolderBox")).Text = "new-instance";
        ((ComboBox)dialog.FindName("ExeCombo")).SelectedIndex = 0;
    }

    private static async Task WithTemplateAsync(Func<Task> action)
    {
        var original = AppPaths.WorkspaceRoot;
        var workspace = Path.Combine(Path.GetTempPath(), "MultiHost-dialog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "template"));
        File.WriteAllText(Path.Combine(workspace, "template", "app.exe"), "fixture");
        try
        {
            AppPaths.ConfigureWorkspace(workspace, persist: false);
            await OnUiThreadAsync(action);
        }
        finally
        {
            AppPaths.ConfigureWorkspace(original, persist: false);
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static async Task OnUiThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
