using System.Diagnostics;
using System.IO;
using Xunit;

namespace MultiHost.Tests;

[CollectionDefinition("Process workspace", DisableParallelization = true)]
public sealed class ProcessWorkspaceCollection;

[Collection("Process workspace")]
public sealed class ProcessSessionTests : IDisposable
{
    private readonly string _originalWorkspace = AppPaths.WorkspaceRoot;
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "MultiHost-process-tests-" + Guid.NewGuid().ToString("N"));

    public ProcessSessionTests()
    {
        Directory.CreateDirectory(_workspace);
        AppPaths.ConfigureWorkspace(_workspace, persist: false);
    }

    [Fact]
    public async Task GenericCommandReceivesArgumentsAndInstanceWorkingDirectory()
    {
        var config = CreateCommand("working", "/d /c cd > working.txt & echo generic-value > argument.txt");
        using var manager = new ProcessManager();
        var process = manager.Start(config);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(config.FolderFullPath, File.ReadAllText(Path.Combine(config.FolderFullPath, "working.txt")).Trim());
        Assert.Equal("generic-value", File.ReadAllText(Path.Combine(config.FolderFullPath, "argument.txt")).Trim());
        manager.Refresh();
        Assert.Empty(manager.RunningFolders);
    }

    [Fact]
    public async Task NestedExecutableUsesItsOwnDirectoryForRelativeResources()
    {
        var config = CreateCommand("nested", "/d /c type resource.txt > output.txt & cd > working.txt");
        string programDirectory = Path.Combine(config.FolderFullPath, "bin");
        Directory.CreateDirectory(programDirectory);
        File.Move(config.ExeFullPath, Path.Combine(programDirectory, "command.exe"));
        File.WriteAllText(Path.Combine(programDirectory, "resource.txt"), "application-local-resource");
        config.ExeFileName = "bin\\command.exe";
        using var manager = new ProcessManager();

        var process = manager.Start(config);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("application-local-resource", File.ReadAllText(Path.Combine(programDirectory, "output.txt")));
        Assert.Equal(programDirectory, File.ReadAllText(Path.Combine(programDirectory, "working.txt")).Trim());
        manager.Refresh();
        Assert.Empty(manager.RunningFolders);
    }

    [Fact]
    public void StartRejectsExecutableThroughDirectoryJunction()
    {
        var config = CreateCommand("linked", "/d /c exit");
        string outside = Path.Combine(_workspace, "outside");
        Directory.CreateDirectory(outside);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(outside, "command.exe"));
        string junction = Path.Combine(config.FolderFullPath, "linked-bin");
        using var link = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /c mklink /J \"{junction}\" \"{outside}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Assert.True(link.WaitForExit(5000));
        Assert.Equal(0, link.ExitCode);
        config.ExeFileName = "linked-bin\\command.exe";

        using var manager = new ProcessManager();
        try
        {
            Assert.Throws<IOException>(() => manager.Start(config));
            Assert.Empty(manager.RunningFolders);
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void StartRejectsTraversalEvenWhenItNormalizesInsideInstance()
    {
        var config = CreateCommand("traversal", "/d /c exit");
        config.ExeFileName = "folder\\..\\command.exe";
        using var manager = new ProcessManager();

        Assert.Throws<ArgumentException>(() => manager.Start(config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownTerminatesOwnedChildAfterLauncherExitsAndPreservesExternalProcess(bool disposeManager)
    {
        var config = CreateCommand("job", "/d /c start \"\" /b powershell.exe -NoProfile -NonInteractive -Command \"$PID | Set-Content child.pid; Start-Sleep -Seconds 120\"");
        using var external = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 120\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        using var manager = new ProcessManager();
        try
        {
            var launcher = manager.Start(config);
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var pidFile = Path.Combine(config.FolderFullPath, "child.pid");
            await WaitUntilAsync(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));
            Assert.True(manager.IsRunning(config.FolderName));
            Assert.True(manager.Owns(config.FolderName, child.Id));
            Assert.False(manager.Owns(config.FolderName, external.Id));

            if (disposeManager) manager.Dispose();
            else await manager.StopAsync(config.FolderName);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(external.HasExited);
            Assert.Empty(manager.RunningFolders);
        }
        finally
        {
            if (!external.HasExited) external.Kill(entireProcessTree: true);
            await external.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task FailedLaunchLeavesNoSessionAndAllowsCorrectedExecutableToStart()
    {
        var config = CreateCommand("retry", "/d /c echo recovered > result.txt");
        File.WriteAllText(config.ExeFullPath, "not an executable");
        using var manager = new ProcessManager();

        Assert.Throws<System.ComponentModel.Win32Exception>(() => manager.Start(config));
        Assert.Empty(manager.RunningFolders);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), config.ExeFullPath, overwrite: true);

        var process = manager.Start(config);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("recovered", File.ReadAllText(Path.Combine(config.FolderFullPath, "result.txt")).Trim());
        manager.Refresh();
        Assert.Empty(manager.RunningFolders);
    }

    private InstanceConfig CreateCommand(string folder, string arguments)
    {
        string directory = Path.Combine(_workspace, folder);
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(directory, "command.exe"));
        return new InstanceConfig { FolderName = folder, ExeFileName = "command.exe", ExtraArgs = arguments, EmbedWindow = false };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }

    public void Dispose()
    {
        AppPaths.ConfigureWorkspace(_originalWorkspace, persist: false);
        Directory.Delete(_workspace, recursive: true);
    }
}
