using System.IO;
using Xunit;

namespace MultiHost.Tests;

[Collection("Process workspace")]
public sealed class EmbedResolutionSettingsTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "MultiHost-resolution-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsFile => Path.Combine(_workspace, "shell_settings.json");

    [Fact]
    public void NewAndExistingWorkspacesDefaultTo800By360()
    {
        var store = new ShellSettingsStore(_workspace);
        Assert.Equal(800, store.Load().EmbedWidth);
        Assert.Equal(360, store.Load().EmbedHeight);
        WriteSettings("{\"Theme\":\"Dark\",\"BatchIntervalSeconds\":3}");
        Assert.Equal(800, store.Load().EmbedWidth);
        Assert.Equal(360, store.Load().EmbedHeight);
    }

    [Theory]
    [InlineData(800, 360)]
    [InlineData(1120, 540)]
    [InlineData(1024, 768)]
    public void ResolutionPersistsAndOtherSettingsRemainIntact(int width, int height)
    {
        WriteSettings("{\"Theme\":\"Dark\",\"BatchIntervalSeconds\":3,\"TemplateDirectory\":\"D:/source\",\"CustomSetting\":\"keep\"}");
        var store = new ShellSettingsStore(_workspace);
        store.SaveEmbedResolution(width, height);
        var settings = new ShellSettingsStore(_workspace).Load();
        Assert.Equal(width, settings.EmbedWidth);
        Assert.Equal(height, settings.EmbedHeight);
        Assert.Equal(AppThemeMode.Dark, settings.Theme);
        Assert.Equal(3, settings.BatchIntervalSeconds);
        Assert.Contains("D:/source", File.ReadAllText(SettingsFile));
        Assert.Contains("keep", File.ReadAllText(SettingsFile));
        store.Save(AppThemeMode.Light, 7);
        Assert.Equal(width, store.Load().EmbedWidth);
        Assert.Equal(height, store.Load().EmbedHeight);
    }

    [Theory]
    [InlineData(0, 360)]
    [InlineData(800, -1)]
    [InlineData(16385, 360)]
    [InlineData(800, 16385)]
    public void InvalidResolutionNeverChangesSavedSettings(int width, int height)
    {
        const string original = "{\"Theme\":\"Dark\"}";
        WriteSettings(original);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShellSettingsStore(_workspace).SaveEmbedResolution(width, height));
        Assert.Equal(original, File.ReadAllText(SettingsFile));
    }

    [Theory]
    [InlineData("{\"EmbedWidth\":0}")]
    [InlineData("{\"EmbedHeight\":16385}")]
    [InlineData("{\"EmbedWidth\":\"bad\"}")]
    public void MalformedSavedResolutionIsPreserved(string json)
    {
        WriteSettings(json);
        var store = new ShellSettingsStore(_workspace);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.SaveEmbedResolution(800, 360));
        Assert.Equal(json, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void ActiveResolutionLoadsPerWorkspaceAndInvalidUpdatesKeepCurrentValue()
    {
        var originalWorkspace = AppPaths.WorkspaceRoot;
        try
        {
            AppPaths.ConfigureWorkspace(_workspace, persist: false);
            ThemeManager.Initialize();
            Assert.Equal(800, ThemeManager.EmbedWidth);
            Assert.Equal(360, ThemeManager.EmbedHeight);
            ThemeManager.SetEmbedResolution(1120, 540);
            Assert.Equal(1120, ThemeManager.EmbedWidth);
            Assert.Equal(540, ThemeManager.EmbedHeight);
            ThemeManager.SetBatchInterval(3);
            ThemeManager.Apply(AppThemeMode.Dark);
            ThemeManager.Initialize();
            Assert.Equal(1120, ThemeManager.EmbedWidth);
            Assert.Equal(540, ThemeManager.EmbedHeight);
            Assert.Throws<ArgumentOutOfRangeException>(() => ThemeManager.SetEmbedResolution(0, 360));
            Assert.Equal(1120, ThemeManager.EmbedWidth);
            Assert.Equal(540, ThemeManager.EmbedHeight);
            AppPaths.ConfigureWorkspace(Path.Combine(_workspace, "second"), persist: false);
            ThemeManager.Initialize();
            Assert.Equal(800, ThemeManager.EmbedWidth);
            Assert.Equal(360, ThemeManager.EmbedHeight);
        }
        finally
        {
            AppPaths.ConfigureWorkspace(originalWorkspace, persist: false);
            ThemeManager.Initialize();
        }
    }

    private void WriteSettings(string content)
    {
        Directory.CreateDirectory(_workspace);
        File.WriteAllText(SettingsFile, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }
}
