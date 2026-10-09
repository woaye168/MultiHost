using System.IO;
using Xunit;

namespace MultiHost.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "MultiHost-config-tests-" + Guid.NewGuid().ToString("N"));

    private string Workspace => Path.Combine(_temporary, "workspace");
    private string Settings => Path.Combine(_temporary, "user-settings");

    private string WriteFile(string relative, string content = "")
    {
        var path = Path.Combine(Workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void NewInstanceDoesNotInjectApplicationSpecificArguments()
    {
        Assert.Empty(new InstanceConfig().BuildArguments());
    }

    [Fact]
    public void GenericArgumentsArePassedWithoutSizeFlags()
    {
        var config = new InstanceConfig { ExtraArgs = "--profile \"my profile\" --verbose" };
        Assert.Equal("--profile \"my profile\" --verbose", config.BuildArguments());
    }

    [Fact]
    public void ApplicationSpecificFolderNamesAreNotReserved()
    {
        ProcessManager.ValidateFolder("星火对战平台PC_线上_251125");
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("bin/../../escape.exe")]
    [InlineData("C:\\escape.exe")]
    [InlineData("bin\\game.exe:evil.exe")]
    [InlineData("bin/CON.exe")]
    [InlineData("bin./game.exe")]
    [InlineData("\\\\server\\share\\game.exe")]
    public void EscapingOrAmbiguousExecutablePathsAreRejected(string executable)
    {
        var config = new InstanceConfig { FolderName = "one", ExeFileName = executable };
        Assert.Throws<ArgumentException>(config.Validate);
    }

    [Fact]
    public void NestedExecutableAndQuotedArgumentsAreValid()
    {
        new InstanceConfig { FolderName = "one", ExeFileName = "bin/application.exe", ExtraArgs = "--profile \"my profile\"" }.Validate();
    }

    [Fact]
    public void WorkspaceChoicePersistsAndEnvironmentOverrideDoesNotChangeSavedChoice()
    {
        var configuration = new WorkspaceConfiguration(Settings);
        Assert.Equal(Path.Combine(Settings, "Workspace"), configuration.WorkspaceRoot);
        configuration.ConfigureWorkspace(Workspace);
        Assert.Equal(Workspace, new WorkspaceConfiguration(Settings).WorkspaceRoot);
        var overridePath = Path.Combine(_temporary, "isolated");
        Assert.Equal(overridePath, new WorkspaceConfiguration(Settings, overridePath).WorkspaceRoot);
        Assert.Equal(Workspace, new WorkspaceConfiguration(Settings).WorkspaceRoot);
    }

    [Fact]
    public void NonPersistentWorkspaceDoesNotWriteUserSettings()
    {
        var configuration = new WorkspaceConfiguration(Settings);
        configuration.ConfigureWorkspace(Workspace, persist: false);
        Assert.False(Directory.Exists(Settings));
        Assert.Equal(Workspace, configuration.WorkspaceRoot);
    }

    [Fact]
    public void EnvironmentOverrideKeepsWorkspaceChangesIsolatedFromUserSettings()
    {
        var configuration = new WorkspaceConfiguration(Settings, Workspace);
        var second = Path.Combine(_temporary, "another-workspace");
        configuration.ConfigureWorkspace(second);
        Assert.Equal(second, configuration.WorkspaceRoot);
        Assert.False(Directory.Exists(Settings));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("C:\\")]
    [InlineData("")]
    public void UnsafeWorkspacePathsAreRejectedWithoutChangingSelection(string path)
    {
        var configuration = new WorkspaceConfiguration(Settings);
        var original = configuration.WorkspaceRoot;
        Assert.Throws<ArgumentException>(() => configuration.ConfigureWorkspace(path));
        Assert.Equal(original, configuration.WorkspaceRoot);
    }

    [Fact]
    public void CorruptWorkspaceCannotReplaceCurrentWorkspace()
    {
        var configuration = new WorkspaceConfiguration(Settings);
        var original = configuration.WorkspaceRoot;
        WriteFile("instances.json", "[broken");
        Assert.Throws<InvalidDataException>(() => configuration.ConfigureWorkspace(Workspace));
        Assert.Equal(original, configuration.WorkspaceRoot);
        Assert.False(Directory.Exists(Settings));
    }

    [Fact]
    public void CorruptUserSettingsRemainIntactAndProvideStartupWarning()
    {
        Directory.CreateDirectory(Settings);
        var file = Path.Combine(Settings, "settings.json");
        File.WriteAllText(file, "broken");
        var configuration = new WorkspaceConfiguration(Settings);
        Assert.Equal(Path.Combine(Settings, "Workspace"), configuration.WorkspaceRoot);
        Assert.Contains(file, configuration.StartupWarning);
        Assert.Throws<InvalidDataException>(() => configuration.ConfigureWorkspace(Workspace));
        Assert.Equal("broken", File.ReadAllText(file));
    }

    [Fact]
    public void TemplateChoiceIsPerWorkspaceAndPreservesThemeSettings()
    {
        WriteFile("shell_settings.json", "{\"Theme\":\"Dark\",\"BatchIntervalSeconds\":3}");
        var configuration = new WorkspaceConfiguration(Settings, Workspace);
        var template = Path.Combine(_temporary, "source");
        Directory.CreateDirectory(template);
        configuration.ConfigureTemplate(template);
        Assert.Equal(template, new WorkspaceConfiguration(Settings, Workspace).TemplateDirectory);
        Assert.Contains("\"Theme\": \"Dark\"", File.ReadAllText(Path.Combine(Workspace, "shell_settings.json")));
        configuration.ConfigureWorkspace(Path.Combine(_temporary, "second"), false);
        Assert.Equal(Path.Combine(_temporary, "second", "template"), configuration.TemplateDirectory);
    }

    [Fact]
    public void TemplateCannotContainWorkspace()
    {
        var configuration = new WorkspaceConfiguration(Settings, Workspace);
        Assert.Throws<ArgumentException>(() => configuration.ConfigureTemplate(_temporary));
    }

    [Fact]
    public void CustomTemplateFolderIsNotDiscoveredAsAnInstance()
    {
        WriteFile("source/app.exe");
        new WorkspaceConfiguration(Settings, Workspace).ConfigureTemplate(Path.Combine(Workspace, "source"));
        Assert.Empty(InstanceStore.Scan([], Workspace));
    }

    [Fact]
    public void MissingInstancesAndConfiguredExecutablesRemainVisible()
    {
        WriteFile("one/helper.exe");
        var stored = new List<InstanceConfig>
        {
            new() { FolderName = "one", ExeFileName = "bin/application.exe" },
            new() { FolderName = "missing", ExeFileName = "application.exe" }
        };
        var found = InstanceStore.Scan(stored, Workspace);
        Assert.Equal(2, found.Count);
        Assert.Contains(stored[0], found);
        Assert.Contains(stored[1], found);
        Assert.Equal("bin/application.exe", found.Single(c => c.FolderName == "one").ExeFileName);
    }

    [Fact]
    public void DiscoveryPrefersRootExecutableThenAlphabeticalOrder()
    {
        WriteFile("one/z.exe");
        WriteFile("one/bin/a-helper.exe");
        WriteFile("one/a.exe");
        Assert.Equal("a.exe", Assert.Single(InstanceStore.Scan([], Workspace)).ExeFileName);
        Assert.Equal(new[] { "a.exe", "z.exe", Path.Combine("bin", "a-helper.exe") }, InstanceStore.ScanTemplateExes(Path.Combine(Workspace, "one")));
    }

    [Fact]
    public void CorruptInstanceConfigurationIsReportedAndNeverOverwritten()
    {
        var file = WriteFile("instances.json", "[{broken");
        Assert.Contains(file, Assert.Throws<InvalidDataException>(() => InstanceStore.Load(Workspace)).Message);
        Assert.Throws<InvalidDataException>(() => InstanceStore.Save([], Workspace));
        Assert.Equal("[{broken", File.ReadAllText(file));
    }

    [Fact]
    public void NullDisplayNameIsReportedBeforeItCanBreakFiltering()
    {
        WriteFile("instances.json", "[{\"FolderName\":\"one\",\"ExeFileName\":\"app.exe\",\"Remark\":null}]");
        Assert.Throws<InvalidDataException>(() => InstanceStore.Load(Workspace));
    }

    [Fact]
    public void LegacyConfigurationLoadsWithoutInjectingArgumentsAndSavesOnlyPortableData()
    {
        WriteFile("instances.json", "[{\"FolderName\":\"one\",\"ExeFileName\":\"bin/app.exe\",\"ExtraArgs\":\"--profile old\",\"FolderFullPath\":\"D:/obsolete/one\"}]");
        var config = Assert.Single(InstanceStore.Load(Workspace));
        Assert.Equal("--profile old", config.BuildArguments());
        InstanceStore.Save([config], Workspace);
        var json = File.ReadAllText(Path.Combine(Workspace, "instances.json"));
        Assert.DoesNotContain("FolderFullPath", json);
        Assert.DoesNotContain("ExeFullPath", json);
        Assert.Single(InstanceStore.Load(Workspace));
        Assert.Empty(Directory.GetFiles(Workspace, "*.tmp"));
    }

    [Fact]
    public void ThemeAndBatchUpdatesPreserveTemplateAndOtherSettings()
    {
        WriteFile("shell_settings.json", "{\"Theme\":\"Dark\",\"BatchIntervalSeconds\":3,\"CustomSetting\":\"keep\"}");
        var store = new ShellSettingsStore(Workspace);
        Assert.Equal(AppThemeMode.Dark, store.Load().Theme);
        store.Save(AppThemeMode.Light, 7);
        Assert.Equal(new ShellSettings(AppThemeMode.Light, 7), store.Load());
        Assert.Contains("keep", File.ReadAllText(Path.Combine(Workspace, "shell_settings.json")));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{\"Theme\":7}")]
    [InlineData("{\"Theme\":\"Unknown\"}")]
    [InlineData("{\"BatchIntervalSeconds\":\"fast\"}")]
    public void InvalidShellSettingsCannotBeSilentlyReplaced(string json)
    {
        var file = WriteFile("shell_settings.json", json);
        var store = new ShellSettingsStore(Workspace);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(AppThemeMode.Light, 1));
        Assert.Equal(json, File.ReadAllText(file));
        var configuration = new WorkspaceConfiguration(Settings);
        Assert.Throws<InvalidDataException>(() => configuration.ConfigureWorkspace(Workspace));
        Assert.Equal(Path.Combine(Settings, "Workspace"), configuration.WorkspaceRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporary)) Directory.Delete(_temporary, recursive: true);
    }
}
