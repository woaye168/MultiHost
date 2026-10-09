using System.IO;
using Xunit;

namespace MultiHost.Tests;

public sealed class InstanceCreationConfigurationTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "MultiHost-create-tests-" + Guid.NewGuid().ToString("N"));
    private static InstanceConfig NewConfig() => new() { FolderName = "new", ExeFileName = "bin/app.exe", Remark = "Selected app" };

    private void CompleteCopy()
    {
        var folder = Path.Combine(_workspace, "new", "bin");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "app.exe"), "application bytes");
    }

    [Fact]
    public async Task ConfigurationEditsDuringCopyArePreserved()
    {
        InstanceStore.Save([new() { FolderName = "existing", ExeFileName = "old.exe", ExtraArgs = "--old" }], _workspace);
        var service = new InstanceCreationService(_workspace, (_, _, _) =>
        {
            var changed = InstanceStore.Load(_workspace);
            changed[0].ExtraArgs = "--new";
            changed[0].Remark = "Changed during copy";
            InstanceStore.Save(changed, _workspace);
            CompleteCopy();
            return Task.CompletedTask;
        });

        await service.CreateAsync(NewConfig());

        var configs = InstanceStore.Load(_workspace);
        Assert.Equal(2, configs.Count);
        Assert.Equal("--new", configs.Single(c => c.FolderName == "existing").ExtraArgs);
        Assert.Equal("Changed during copy", configs.Single(c => c.FolderName == "existing").Remark);
        Assert.Equal("bin/app.exe", configs.Single(c => c.FolderName == "new").ExeFileName);
    }

    [Fact]
    public async Task FailedSavePreservesCopiedFilesAndReportsRecoveryPath()
    {
        var service = new InstanceCreationService(_workspace, (_, _, _) =>
        {
            CompleteCopy();
            File.WriteAllText(Path.Combine(_workspace, "instances.json"), "broken during copy");
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(NewConfig()));

        Assert.Contains(Path.Combine(_workspace, "new"), error.Message);
        Assert.Contains("bin/app.exe", error.Message);
        Assert.True(File.Exists(Path.Combine(_workspace, "new", "bin", "app.exe")));
        Assert.Equal("broken during copy", File.ReadAllText(Path.Combine(_workspace, "instances.json")));
    }

    [Fact]
    public async Task ConcurrentConfigurationCannotBeReplacedByCopiedInstance()
    {
        var service = new InstanceCreationService(_workspace, (_, _, _) =>
        {
            CompleteCopy();
            InstanceStore.Save([new() { FolderName = "new", ExeFileName = "external.exe", Remark = "External entry" }], _workspace);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(NewConfig()));
        Assert.Equal("External entry", Assert.Single(InstanceStore.Load(_workspace)).Remark);
        Assert.True(File.Exists(Path.Combine(_workspace, "new", "bin", "app.exe")));
    }

    [Fact]
    public async Task CorruptInitialConfigurationPreventsCopy()
    {
        Directory.CreateDirectory(_workspace);
        File.WriteAllText(Path.Combine(_workspace, "instances.json"), "broken");
        var called = false;
        var service = new InstanceCreationService(_workspace, (_, _, _) => { called = true; return Task.CompletedTask; });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(NewConfig()));
        Assert.False(called);
    }

    [Fact]
    public async Task ExistingUnconfiguredFolderCannotBeOverwritten()
    {
        CompleteCopy();
        var called = false;
        var service = new InstanceCreationService(_workspace, (_, _, _) => { called = true; return Task.CompletedTask; });

        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(NewConfig()));
        Assert.False(called);
    }

    [Fact]
    public async Task MissingCopiedExecutableIsNotPersistedAsSuccessfulCreation()
    {
        var service = new InstanceCreationService(_workspace, (_, _, _) =>
        {
            Directory.CreateDirectory(Path.Combine(_workspace, "new"));
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(NewConfig()));
        Assert.Empty(InstanceStore.Load(_workspace));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }
}
