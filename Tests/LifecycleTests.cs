using System.Diagnostics;
using System.IO;
using Xunit;

namespace MultiHost.Tests;

public class LifecycleTests
{
    [Fact]
    public void CheckedItemsWinOverFocusedRow()
    {
        var a = new InstanceItem { Config = new() { FolderName = "a" }, IsSelected = true };
        var b = new InstanceItem { Config = new() { FolderName = "b" }, IsSelected = true };
        var c = new InstanceItem { Config = new() { FolderName = "c" } };
        Assert.Equal(new[] { a, b }, MainWindow.SelectTargets(new[] { a, b, c }, c));
    }
    [Theory]
    [InlineData("..")] [InlineData(".")] [InlineData("template")] [InlineData("exe_template")]
    [InlineData("../escape")] [InlineData("x.")]
    [InlineData("CON")] [InlineData("NUL.txt")] [InlineData("COM1")] [InlineData("LPT9.log")]
    public void UnsafeFoldersAreRejected(string folder) => Assert.Throws<ArgumentException>(() => ProcessManager.ValidateFolder(folder));

    [Fact]
    public void DirectoryBoundaryAllowsChildrenOnly()
    {
        Assert.True(ProcessManager.IsWithin(@"C:\root\a\Win\game.exe", @"C:\root\a"));
        Assert.False(ProcessManager.IsWithin(@"C:\root\ab\game.exe", @"C:\root\a"));
        Assert.False(ProcessManager.IsWithin(null, @"C:\root\a"));
    }
    [Fact]
    public void StopUnownedFolderDoesNotKillCurrentProcess()
    {
        using var manager = new ProcessManager();
        manager.Stop("not-owned");
        Assert.False(Process.GetCurrentProcess().HasExited);
        Assert.Empty(manager.RunningFolders);
    }
}
