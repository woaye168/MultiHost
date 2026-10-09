using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Xunit;

namespace MultiHost.Tests;

[Collection("Process workspace")]
public class ProcessWindowTests
{
    [Fact]
    public async Task CaptureAcceptsOwnedWindowWithArbitraryClientSize()
    {
        using var window = await TestWindow.ShowAsync("Generic application", new Size(413, 271));
        var result = await WindowEmbedder.CaptureMainWindowAsync(new() { Width = 800, Height = 360 },
            null, CancellationToken.None, 1800, owns: pid => pid == Environment.ProcessId);

        Assert.Equal(window.Handle, result.Hwnd);
        Assert.Equal("OwnedJob", result.Strategy);
    }

    [Fact]
    public async Task CaptureAcceptsTitlelessOwnedWindow()
    {
        using var window = await TestWindow.ShowAsync("", new Size(413, 271));
        var result = await WindowEmbedder.CaptureMainWindowAsync(new() { Width = 413, Height = 271 },
            null, CancellationToken.None, 1800, owns: pid => pid == Environment.ProcessId);

        Assert.Equal(window.Handle, result.Hwnd);
    }

    [Fact]
    public async Task CaptureUsesExplicitProcessIdentityWithoutDirectoryHeuristic()
    {
        using var window = await TestWindow.ShowAsync("Known process", new Size(413, 271));
        var result = await WindowEmbedder.CaptureMainWindowAsync(new() { FolderName = "missing", Width = 413, Height = 271 },
            null, CancellationToken.None, 1800, knownPid: Environment.ProcessId);

        Assert.Equal(window.Handle, result.Hwnd);
    }

    [Fact]
    public async Task CaptureNeverAdoptsSameDirectoryWindowWithoutProcessIdentity()
    {
        using var window = await TestWindow.ShowAsync("External application", new Size(413, 271));
        using var self = Process.GetCurrentProcess();
        var config = new InstanceConfig { FolderName = Path.GetDirectoryName(self.MainModule!.FileName)!, Width = 413, Height = 271 };

        var result = await WindowEmbedder.CaptureMainWindowAsync(config, null, CancellationToken.None, 900);

        Assert.False(result.Found);
    }

    [Fact]
    public async Task CaptureDoesNotFallBackToPidWhenOwnershipIsRejected()
    {
        using var window = await TestWindow.ShowAsync("Unowned application", new Size(413, 271));
        var result = await WindowEmbedder.CaptureMainWindowAsync(new() { Width = 413, Height = 271 },
            null, CancellationToken.None, 900, knownPid: Environment.ProcessId, owns: _ => false);

        Assert.False(result.Found);
    }

    [Fact]
    public async Task CaptureCancellationIsObservedBeforeEnumeration()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowEmbedder.CaptureMainWindowAsync(
            new(), null, cancel.Token, 1800, owns: _ => true));
    }

    private sealed class TestWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly Form _form;
        public IntPtr Handle { get; }

        private TestWindow(Thread thread, Form form, IntPtr handle)
        {
            _thread = thread;
            _form = form;
            Handle = handle;
        }

        internal static async Task<TestWindow> ShowAsync(string title, Size size)
        {
            var ready = new TaskCompletionSource<(Form Form, IntPtr Handle)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                using var form = new Form
                {
                    Text = title,
                    ClientSize = size,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-30000, -30000)
                };
                form.Shown += (_, _) => ready.TrySetResult((form, form.Handle));
                try { System.Windows.Forms.Application.Run(form); }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            var value = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new(thread, value.Form, value.Handle);
        }

        public void Dispose()
        {
            _form.BeginInvoke(new Action(_form.Close));
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "Test window did not close.");
        }
    }
}
