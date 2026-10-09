using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace MultiHost.Tests;

public class GuiElevationTests
{
    [Fact]
    public void StandardGuiRequestsElevationBeforeContinuing()
    {
        ProcessStartInfo? requested = null;
        var result = GuiElevation.Ensure([], false, @"C:\应用 目录\MultiHost.exe", null,
            info => { requested = info; return true; });

        Assert.False(result.ContinueStartup);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(requested);
        Assert.Equal("runas", requested.Verb);
        Assert.True(requested.UseShellExecute);
        Assert.Equal(@"C:\应用 目录\MultiHost.exe", requested.FileName);
        Assert.Equal(@"C:\应用 目录", requested.WorkingDirectory);
        Assert.Empty(requested.ArgumentList);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AdministratorAndCliNeverRequestElevation(bool administrator, bool cli)
    {
        var result = GuiElevation.Ensure(cli ? ["CLI", "status"] : [], administrator,
            @"C:\MultiHost.exe", null, _ => throw new InvalidOperationException("Unexpected elevation"));
        Assert.True(result.ContinueStartup);
    }

    [Fact]
    public void ElevationPreservesExplicitWorkspaceWithoutCreatingDefaultOverride()
    {
        ProcessStartInfo? requested = null;
        GuiElevation.Ensure([], false, @"C:\MultiHost.exe", @"D:\我的工作区\实例",
            info => { requested = info; return true; });
        Assert.Equal(new[] { "--workspace-override", @"D:\我的工作区\实例" }, requested!.ArgumentList);
        Assert.Equal(@"D:\我的工作区\实例", GuiElevation.ReadWorkspaceOverride(requested.ArgumentList.ToArray()));
    }

    [Fact]
    public void AlreadyForwardedWorkspaceIsNotDuplicated()
    {
        ProcessStartInfo? requested = null;
        string[] args = ["--workspace-override", @"D:\test space"];
        GuiElevation.Ensure(args, false, @"C:\MultiHost.exe", @"D:\test space",
            info => { requested = info; return true; });
        Assert.Equal(args, requested!.ArgumentList);
    }

    [Fact]
    public void CancellingWindowsPromptDoesNotContinueOrRetry()
    {
        var calls = 0;
        var result = GuiElevation.Ensure([], false, @"C:\MultiHost.exe", null, _ =>
        {
            calls++;
            throw new Win32Exception(1223);
        });
        Assert.Equal(1, calls);
        Assert.False(result.ContinueStartup);
        Assert.Equal(1223, result.ExitCode);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FailedLaunchDoesNotFallThroughToUnelevatedGui()
    {
        var result = GuiElevation.Ensure([], false, @"C:\MultiHost.exe", null, _ => false);
        Assert.False(result.ContinueStartup);
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("--workspace-override")]
    [InlineData("--workspace-override", "relative")]
    public void InvalidForwardedWorkspaceIsRejected(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => GuiElevation.ReadWorkspaceOverride(args));
    }
}
