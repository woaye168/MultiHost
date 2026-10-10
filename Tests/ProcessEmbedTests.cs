using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Windows.Forms.Integration;
using Xunit;
using Size = System.Drawing.Size;
using Point = System.Drawing.Point;

namespace MultiHost.Tests;

[Collection("Process workspace")]
public sealed class ProcessEmbedTests
{
    [Fact]
    public Task ActivatingEmbeddedWindowPreservesFocusedInputControl() => OnUiThreadAsync(async () =>
    {
        using var child = CreateForm();
        using var input = new System.Windows.Forms.TextBox();
        child.Controls.Add(input);
        child.Show();
        var slot = new EmbedSlot();
        var window = new Window { Content = slot, Width = 600, Height = 500, ShowInTaskbar = false };
        try
        {
            window.Show();
            slot.Attach("fixture", child.Handle);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            input.Focus();
            Assert.Equal(input.Handle, GetFocus());

            slot.ActivateChild();

            Assert.Equal(input.Handle, GetFocus());
        }
        finally { slot.ResetOnly(); window.Close(); }
    });

    [Fact]
    public Task EmbeddedWindowStaysInteractiveAfterModalDialog() => OnUiThreadAsync(async () =>
    {
        using var child = CreateForm();
        using var input = new System.Windows.Forms.TextBox();
        child.Controls.Add(input);
        child.Show();
        var slot = new EmbedSlot();
        var window = new Window { Content = slot, Width = 600, Height = 500, ShowInTaskbar = false };
        try
        {
            window.Show();
            slot.Attach("fixture", child.Handle);
            var dialog = new Window { Owner = window, Width = 200, Height = 100, ShowInTaskbar = false };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(dialog.Close);
            dialog.ShowDialog();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(IsWindowEnabled(child.Handle));
            Assert.True(input.Focus());
            Assert.Equal(input.Handle, GetFocus());
        }
        finally { slot.ResetOnly(); window.Close(); }
    });

    [Fact]
    public Task ScrolledNativeWindowDoesNotCoverToolbar() => OnUiThreadAsync(async () =>
    {
        using var child = CreateForm();
        child.Show();
        var slot = new EmbedSlot();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        grid.Children.Add(new TextBlock { Text = "Toolbar" });
        var footer = new TextBlock { Text = "Footer" };
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(slot);
        slot.Height = 400;
        content.Children.Add(new Border { Height = 400 });
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        var window = new Window { Content = grid, Width = 600, Height = 500, ShowActivated = false, ShowInTaskbar = false, Topmost = true };
        try
        {
            window.Show();
            slot.Attach("fixture", child.Handle);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var bottom = footer.PointToScreen(new System.Windows.Point(40, 20));
            Assert.NotEqual(child.Handle, WindowFromPoint(new NativePoint { X = (int)bottom.X, Y = (int)bottom.Y }));
            scroll.ScrollToVerticalOffset(200);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var point = grid.PointToScreen(new System.Windows.Point(40, 40));
            var top = WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y });
            Assert.NotEqual(child.Handle, top);
            scroll.ScrollToVerticalOffset(0);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var visible = scroll.PointToScreen(new System.Windows.Point(40, 40));
            Assert.Equal(child.Handle, WindowFromPoint(new NativePoint { X = (int)visible.X, Y = (int)visible.Y }));

            slot.Detach();
            slot.Attach("fixture", child.Handle);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(child.Handle, WindowFromPoint(new NativePoint { X = (int)visible.X, Y = (int)visible.Y }));
        }
        finally { slot.ResetOnly(); window.Close(); }
    });

    [Fact]
    public Task SynchronizeCreatesStoppedPlaceholdersAndPreservesExistingNativeHosts() => OnUiThreadAsync(() =>
    {
        using var manager = new ProcessManager();
        var controller = new EmbedController(manager, _ => null);
        try
        {
            controller.Synchronize([new() { FolderName = "first" }, new() { FolderName = "second" }]);
            var first = Assert.IsType<EmbedSlot>(controller.GetSlot("first"));
            var second = Assert.IsType<EmbedSlot>(controller.GetSlot("second"));
            var removedPanel = Assert.Single(second.Children.OfType<WindowsFormsHost>()).Child!;
            Assert.Equal("已停止", Assert.Single(first.Children.OfType<TextBlock>()).Text);
            Assert.False(manager.IsRunning("first"));
            Assert.False(manager.IsRunning("second"));
            second.ShowExternal("second");
            Assert.Equal("已在独立窗口运行，点击收回可内嵌", Assert.Single(second.Children.OfType<TextBlock>()).Text);
            Assert.False(second.IsAttached);
            Assert.False(second.IsPoppedOut);

            controller.Synchronize([new() { FolderName = "FIRST" }, new() { FolderName = "third" }]);

            Assert.Same(first, controller.GetSlot("first"));
            Assert.False(controller.HasSlot("second"));
            Assert.True(removedPanel.IsDisposed);
            Assert.NotNull(controller.GetSlot("third"));
        }
        finally { controller.ReleaseAll(); }
        Assert.False(controller.HasSlot("first"));
        controller.Synchronize([new() { FolderName = "next-workspace" }]);
        Assert.NotNull(controller.GetSlot("next-workspace"));
        controller.ReleaseAll();
        return Task.CompletedTask;
    });

    [Fact]
    public Task StoppingDuringCaptureKeepsPlaceholderAndCancelsPendingAttach() => OnUiThreadAsync(async () =>
    {
        using var manager = new ProcessManager();
        var controller = new EmbedController(manager, _ => null);
        try
        {
            var capture = controller.EmbedAsync(new() { FolderName = "pending" }, null, 10000);
            var slot = Assert.IsType<EmbedSlot>(controller.GetSlot("pending"));
            controller.ReleaseSlot("pending", true);
            await capture.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Same(slot, controller.GetSlot("pending"));
            Assert.Equal(IntPtr.Zero, slot.LastHwnd);
            Assert.Equal("已停止", Assert.Single(slot.Children.OfType<TextBlock>()).Text);
        }
        finally { controller.ReleaseAll(); }
    });

    [Fact]
    public Task AttachFillsNativeHostAndFollowsItsClientSize() => OnUiThreadAsync(async () =>
    {
        using var child = CreateForm();
        child.Show();
        var slot = new EmbedSlot();
        var panel = Assert.Single(slot.Children.OfType<WindowsFormsHost>()).Child!;
        try
        {
            panel.ClientSize = new Size(287, 163);
            slot.Attach("fixture", child.Handle);
            Assert.True(slot.FillMode);
            Assert.True(GetClientRect(child.Handle, out var first));
            Assert.Equal(panel.ClientSize, new Size(first.Right - first.Left, first.Bottom - first.Top));

            panel.ClientSize = new Size(319, 187);
            await Task.Delay(150);
            Assert.True(GetClientRect(child.Handle, out var resized));
            Assert.Equal(panel.ClientSize, new Size(resized.Right - resized.Left, resized.Bottom - resized.Top));
        }
        finally { slot.ResetOnly(); }
    });

    [Fact]
    public Task PopoutUsesOriginalSizeAndReembeddingReturnsToFill() => OnUiThreadAsync(async () =>
    {
        using var child = CreateForm();
        child.Show();
        var original = child.ClientSize;
        var slot = new EmbedSlot();
        var panel = Assert.Single(slot.Children.OfType<WindowsFormsHost>()).Child!;
        try
        {
            panel.ClientSize = new Size(287, 163);
            slot.Attach("fixture", child.Handle);
            slot.SetFill(false);
            slot.Detach();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(slot.IsAttached);
            Assert.True(GetClientRect(child.Handle, out var popped));
            Assert.Equal(original, new Size(popped.Right - popped.Left, popped.Bottom - popped.Top));

            slot.Attach("fixture", child.Handle);
            Assert.True(slot.IsAttached);
            Assert.True(slot.FillMode);
            Assert.True(GetClientRect(child.Handle, out var embedded));
            Assert.Equal(panel.ClientSize, new Size(embedded.Right - embedded.Left, embedded.Bottom - embedded.Top));
        }
        finally { slot.ResetOnly(); }
    });

    [Fact]
    public Task CaptureFailureReachesCallerAndLeavesRetryableSlot() => OnUiThreadAsync(async () =>
    {
        using var manager = new ProcessManager();
        var grid = new UniformGrid();
        var controller = new EmbedController(grid, manager, _ => null);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => controller.EmbedAsync(new() { FolderName = "no-session" }, null, 100));
            Assert.True(controller.HasSlot("no-session"));
            Assert.False(controller.IsAttached("no-session"));
        }
        finally { controller.ReleaseAll(); }
    });

    [Fact]
    public Task ReusedSlotShowsWaitingAndFailureMessages() => OnUiThreadAsync(() =>
    {
        using var child = CreateForm();
        child.Show();
        var slot = new EmbedSlot();
        try
        {
            slot.Attach("fixture", child.Handle);
            var message = Assert.Single(slot.Children.OfType<TextBlock>());
            Assert.Equal(Visibility.Collapsed, message.Visibility);

            slot.ShowWaiting("fixture");
            Assert.Equal(Visibility.Visible, message.Visibility);
            slot.ShowFailed("fixture");
            Assert.Equal(Visibility.Visible, message.Visibility);
        }
        finally { slot.ResetOnly(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task HookPassesInputThroughWhenSyncDisabled()
    {
        var sentinel = (IntPtr)12345;
        var previous = InputSync.CallNextHook;
        InputSync.CallNextHook = (_, _, _, _) => sentinel;
        try
        {
            Assert.False(InputSync.Enabled);
            // 同步关闭时钩子必须 CallNextHookEx 放行，不吞事件。
            Assert.Equal(sentinel, InputSync.MouseHookProc(Win32Api.HC_ACTION, (IntPtr)Win32Api.WM_MOUSEMOVE, IntPtr.Zero));
        }
        finally { InputSync.CallNextHook = previous; }
        return Task.CompletedTask;
    }

    [Fact]
    public Task FindTargetWalksParentChainToEmbeddedWindow()
    {
        var targets = new List<IntPtr> { (IntPtr)0x100, (IntPtr)0x200 };
        IntPtr FakeGetParent(IntPtr hwnd) => hwnd == (IntPtr)0x101 ? (IntPtr)0x100 : IntPtr.Zero;
        // 命中内嵌窗口的子控件时沿父链找到内嵌窗口；未命中任何内嵌窗口返回 Zero（放行）。
        Assert.Equal((IntPtr)0x100, InputSync.FindTarget(targets, (IntPtr)0x101, FakeGetParent));
        Assert.Equal((IntPtr)0x200, InputSync.FindTarget(targets, (IntPtr)0x200, FakeGetParent));
        Assert.Equal(IntPtr.Zero, InputSync.FindTarget(targets, (IntPtr)0x999, FakeGetParent));
        return Task.CompletedTask;
    }

    [Fact]
    public Task ScreenToClientMapsScreenPointToRelativeClientCoordinates()
    {
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Width = 400, Height = 300 };
        form.Show();
        // 屏幕坐标 → 客户区相对坐标，同步广播依赖这个换算把命中点映射为各窗口内相同的相对位置。
        var screen = form.PointToScreen(new Point(40, 50));
        var pt = new Win32Api.POINT { X = screen.X, Y = screen.Y };
        Assert.True(Win32Api.ScreenToClient(form.Handle, ref pt));
        Assert.Equal((40, 50), (pt.X, pt.Y));
        return Task.CompletedTask;
    }

    [Fact]
    public Task InputSyncConvertsDipToClientPixelsAndPacksLParam()
    {
        var (x, y) = InputSync.ToClientPoint(new System.Windows.Point(10.4, 20.6), 1.5, 1.5);
        Assert.Equal((16, 31), (x, y));
        long packed = InputSync.MakeLParam(16, 31).ToInt64();
        Assert.Equal(16, (short)(packed & 0xFFFF));
        Assert.Equal(31, (short)(packed >> 16));
        long negative = InputSync.MakeLParam(-5, -7).ToInt64();
        Assert.Equal(-5, (short)(negative & 0xFFFF));
        Assert.Equal(-7, (short)(negative >> 16));
        return Task.CompletedTask;
    }

    [Fact]
    public Task BroadcastInjectsMouseAtEachWindowScreenPoint()
    {
        // 鼠标广播改为物理注入：逐窗口 SetCursorPos 到「客户区相对坐标换算出的该窗口屏幕点」，
        // 再 SendInput 注入动作，全部窗口点完后光标移回原位。
        var cursorMoves = new List<(int X, int Y)>();
        var injections = new List<(uint Flags, int Data)>();
        var previous = (InputSync.SetCursor, InputSync.GetCursor, InputSync.ClientToScreenPoint, InputSync.SendMouse);
        InputSync.SetCursor = (x, y) => cursorMoves.Add((x, y));
        InputSync.GetCursor = () => (1000, 500); // 用户原点击位置
        // 窗口 A 的客户区 (10,20) 在屏幕 (110,120)，窗口 B 在 (210,220)。
        InputSync.ClientToScreenPoint = (hwnd, x, y) => hwnd == (IntPtr)0xA ? (110, 120) : (210, 220);
        InputSync.SendMouse = (flags, data) => injections.Add((flags, data));
        try
        {
            InputSync.Broadcast([(IntPtr)0xA, (IntPtr)0xB],
                new SyncedInput(Win32Api.WM_LBUTTONDOWN, (IntPtr)Win32Api.MK_LBUTTON, InputSync.MakeLParam(10, 20)));

            Assert.Equal(new[] { (110, 120), (210, 220), (1000, 500) }, cursorMoves); // 末位为还原到原点击位置
            // 按下事件对每个窗口注入完整点击（down+up 一对）。
            Assert.Equal(new[] {
                (Win32Api.MOUSEEVENTF_LEFTDOWN, 0), (Win32Api.MOUSEEVENTF_LEFTUP, 0),
                (Win32Api.MOUSEEVENTF_LEFTDOWN, 0), (Win32Api.MOUSEEVENTF_LEFTUP, 0) }, injections);
        }
        finally
        {
            (InputSync.SetCursor, InputSync.GetCursor, InputSync.ClientToScreenPoint, InputSync.SendMouse) = previous;
        }
        return Task.CompletedTask;
    }

    [Fact]
    public Task HookPassesThroughWhileInjecting()
    {
        // SendInput 是硬件级输入会再次触发低级钩子：注入期间钩子必须直接放行（CallNextHookEx），
        // 否则广播的点击被再次捕获再广播，无限递归。
        var sentinel = (IntPtr)12345;
        var previous = (InputSync.CallNextHook, InputSync.SetCursor, InputSync.GetCursor, InputSync.ClientToScreenPoint, InputSync.SendMouse);
        InputSync.CallNextHook = (_, _, _, _) => sentinel;
        InputSync.SetCursor = (_, _) => { };
        InputSync.GetCursor = () => (0, 0);
        InputSync.ClientToScreenPoint = (_, _, _) => (50, 60);
        IntPtr nestedResult = IntPtr.Zero;
        InputSync.SendMouse = (_, _) =>
            // 模拟注入触发的递归钩子回调：此时 _injecting 为 true，必须放行。
            nestedResult = InputSync.MouseHookProc(Win32Api.HC_ACTION, (IntPtr)Win32Api.WM_LBUTTONDOWN, IntPtr.Zero);
        try
        {
            InputSync.Broadcast([(IntPtr)0xA],
                new SyncedInput(Win32Api.WM_LBUTTONDOWN, (IntPtr)Win32Api.MK_LBUTTON, InputSync.MakeLParam(10, 20)));

            Assert.Equal(sentinel, nestedResult);
        }
        finally
        {
            (InputSync.CallNextHook, InputSync.SetCursor, InputSync.GetCursor, InputSync.ClientToScreenPoint, InputSync.SendMouse) = previous;
        }
        return Task.CompletedTask;
    }

    [Fact]
    public Task ReleaseRestoresOriginalBoundsStyleAndOwner() => OnUiThreadAsync(() =>
    {
        using var owner = CreateForm();
        using var child = CreateForm();
        owner.Show();
        child.Show(owner);
        Assert.True(Win32Api.GetWindowRect(child.Handle, out var originalBounds));
        long originalStyle = Win32Api.GetWindowLong(child.Handle, Win32Api.GWL_STYLE);
        IntPtr originalOwner = Win32Api.GetParent(child.Handle);
        var slot = new EmbedSlot();
        slot.Attach("fixture", child.Handle);
        Win32Api.MoveWindow(child.Handle, 0, 0, 123, 99, true);

        slot.ResetOnly();

        Assert.True(Win32Api.IsWindow(child.Handle));
        Assert.Equal(originalStyle, Win32Api.GetWindowLong(child.Handle, Win32Api.GWL_STYLE));
        Assert.Equal(originalOwner, Win32Api.GetParent(child.Handle));
        Assert.True(Win32Api.GetWindowRect(child.Handle, out var actual));
        Assert.Equal((originalBounds.Left, originalBounds.Top, originalBounds.Right, originalBounds.Bottom),
            (actual.Left, actual.Top, actual.Right, actual.Bottom));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ReleaseKeepsReusablePlaceholderWhenWindowClosesDuringRestoration() => OnUiThreadAsync(async () =>
    {
        using var manager = new ProcessManager();
        var grid = new UniformGrid();
        var controller = new EmbedController(grid, manager, _ => null);
        await Assert.ThrowsAsync<TimeoutException>(() => controller.EmbedAsync(new() { FolderName = "closing" }, null, 1));
        var slot = Assert.IsType<EmbedSlot>(controller.GetSlot("closing"));
        using var child = new ClosingOnRestoreForm { Text = "Self closing fixture", ShowInTaskbar = false };
        child.Show();
        slot.Attach("closing", child.Handle);
        var host = Assert.Single(slot.Children.OfType<WindowsFormsHost>());
        var panel = host.Child!;
        child.CloseWhenRestored = true;

        controller.ReleaseSlot("closing", true);

        Assert.True(controller.HasSlot("closing"));
        Assert.Same(slot, controller.GetSlot("closing"));
        Assert.Single(grid.Children);
        Assert.Equal(IntPtr.Zero, slot.LastHwnd);
        Assert.False(panel.IsDisposed);
        Assert.True(child.IsDisposed);
        Assert.Equal("已停止", Assert.Single(slot.Children.OfType<TextBlock>()).Text);

        using var restarted = CreateForm();
        restarted.Show();
        slot.Attach("closing", restarted.Handle);
        Assert.True(slot.IsAttached);
        controller.ReleaseAll();
        Assert.Empty(grid.Children);
        Assert.True(panel.IsDisposed);
    });

    private sealed class ClosingOnRestoreForm : Form
    {
        public bool CloseWhenRestored;

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (CloseWhenRestored && message.Msg == 0x007D &&
                (Win32Api.GetWindowLong(Handle, Win32Api.GWL_STYLE) & Win32Api.WS_CHILD) == 0)
            {
                CloseWhenRestored = false;
                Close();
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Win32Api.RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    private static Form CreateForm() => new()
    {
        Text = "Local embedding fixture",
        ClientSize = new Size(413, 271),
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-30000, -30000)
    };

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
