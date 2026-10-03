using System.Text.Json;
using ScumStudio.Core.Settings;

namespace ScumStudio.Tests.Core;

public sealed class SettingsStoreTests
{
    private static AppSettings Sample() => new AppSettings
    {
        GamePaksFolder = Path.Combine("Games", "SCUM", "SCUM", "Content", "Paks"),
        ServerPaksFolder = Path.Combine("SCUMServer", "server", "SCUM", "Content", "Paks"),
        ClientModsOutputFolder = Path.Combine("Mods", "Client"),
        ServerModsOutputFolder = Path.Combine("Mods", "Server"),
        RepakExecutablePath = Path.Combine("Tools", "repak.exe"),
        Ui = new UiPreferences
        {
            Theme = UiTheme.Light,
            MainWindow = new WindowPlacement(10, 20, 1600, 900, IsMaximized: true),
            CameraSpeed = 3500f,
            FieldOfViewDegrees = 80f,
            InvertMouseY = true,
            ShowGrid = false,
            ShowStatistics = true,
            TranslationSnap = 25f,
            RotationSnapDegrees = 5f,
        },
    }.WithRecentProject("second.scumproj").WithRecentProject("first.scumproj");

    [Fact]
    public void RoundTripPreservesEverything()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.Path);
        var settings = Sample();

        store.Save(settings);
        var loaded = new SettingsStore(temp.Path).Load();

        Assert.Equal(settings, loaded);
        Assert.Equal(AppSettings.CurrentVersion, loaded.Version);
        Assert.Equal(["first.scumproj", "second.scumproj"], loaded.RecentProjects);
        Assert.Equal("first.scumproj", loaded.LastProjectPath);

        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"gamePaksFolder\"", json);
        Assert.Contains("\"theme\": \"light\"", json);
    }

    [Fact]
    public async Task AsyncRoundTripAndOverwrite()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(Path.Combine(temp.Path, "nested", "home"));
        await store.SaveAsync(Sample());
        var changed = Sample() with { GamePaksFolder = "other", Ui = new UiPreferences { Theme = UiTheme.System } };
        await store.SaveAsync(changed);

        Assert.Equal(changed, await store.LoadAsync());

        // Only the settings file is left behind: temporary files were renamed or removed.
        Assert.Equal([SettingsStore.FileName], Directory.GetFiles(store.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(Path.Combine(temp.Path, "does-not-exist"));
        var settings = store.Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
        Assert.Null(settings.GamePaksFolder);
        Assert.Empty(settings.RecentProjects);
        Assert.Equal(UiTheme.Dark, settings.Ui.Theme);
        Assert.False(Directory.Exists(store.Directory));
    }

    [Fact]
    public void CorruptFileIsBackedUpAndDefaultsReturned()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, "{ this is not json");
        using var log = new CapturedLog();

        var settings = new SettingsStore(temp.Path, log.Logger).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.False(File.Exists(store.FilePath));
        Assert.Single(Directory.GetFiles(temp.Path, SettingsStore.FileName + ".corrupt-*"));
        Assert.Contains("WRN", log.Text);

        File.WriteAllText(store.FilePath, "[1, 2, 3]");
        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public void HandEditedJsonWithCommentsLoads()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, """
            {
              // written by hand
              "version": 1,
              "GamePaksFolder": "D:/SCUM/SCUM/Content/Paks",
              "recentProjects": ["a", "", null],
              "ui": { "theme": "Light", "cameraSpeed": "1234.5" },
            }
            """);

        var settings = store.Load();
        Assert.Equal("D:/SCUM/SCUM/Content/Paks", settings.GamePaksFolder);
        Assert.Equal(["a"], settings.RecentProjects);
        Assert.Equal(UiTheme.Light, settings.Ui.Theme);
        Assert.Equal(1234.5f, settings.Ui.CameraSpeed);
        Assert.True(settings.Ui.ShowGrid);
    }

    [Fact]
    public void VersionZeroIsMigrated()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, """{ "PaksFolder": "old-paks", "modsFolder": "old-mods", "serverPaks": "srv" }""");
        using var log = new CapturedLog();

        var settings = new SettingsStore(temp.Path, log.Logger).Load();

        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
        Assert.Equal("old-paks", settings.GamePaksFolder);
        Assert.Equal("old-mods", settings.ClientModsOutputFolder);
        Assert.Equal("srv", settings.ServerPaksFolder);
        Assert.True(settings.AdditionalProperties is null || settings.AdditionalProperties.Count == 0);
        Assert.Contains("Migrated settings.json from version 0 to 1", log.Text);
    }

    [Fact]
    public void NewerVersionKeepsUnknownProperties()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, """
            { "version": 99, "gamePaksFolder": "paks", "futureFeature": { "enabled": true, "level": 3 } }
            """);

        var settings = store.Load();
        Assert.Equal(99, settings.Version);
        Assert.Equal("paks", settings.GamePaksFolder);
        Assert.NotNull(settings.AdditionalProperties);
        Assert.True(settings.AdditionalProperties!.ContainsKey("futureFeature"));

        store.Save(settings with { LastProjectPath = "p" });
        using var document = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.Equal(99, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("futureFeature").GetProperty("level").GetInt32());
        Assert.Equal(settings with { LastProjectPath = "p" }, store.Load());
    }

    [Fact]
    public void UpdateAppliesChange()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.Path);
        store.Update(s => s with { GamePaksFolder = "x" });
        var updated = store.Update(s => s.WithRecentProject("proj"));
        Assert.Equal("x", updated.GamePaksFolder);
        Assert.Equal(updated, store.Load());
    }

    [Fact]
    public void RecentProjectsAreDeduplicatedAndCapped()
    {
        var settings = new AppSettings();
        for (var i = 0; i < AppSettings.MaxRecentProjects + 5; i++)
        {
            settings = settings.WithRecentProject($"p{i}");
        }

        settings = settings.WithRecentProject("p3");
        Assert.Equal(AppSettings.MaxRecentProjects, settings.RecentProjects.Count);
        Assert.Equal("p3", settings.RecentProjects[0]);
        Assert.Single(settings.RecentProjects, p => p == "p3");
        Assert.Equal("p3", settings.LastProjectPath);
    }

    [Fact]
    public void EqualityComparesListContent()
    {
        var a = new AppSettings { RecentProjects = ["x", "y"] };
        var b = new AppSettings { RecentProjects = new List<string> { "x", "y" } };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { RecentProjects = ["y", "x"] });
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StudioHomeEnvironmentCollection
{
    public const string Name = "SCUMSTUDIO_HOME environment";
}

[Collection(StudioHomeEnvironmentCollection.Name)]
public sealed class StudioHomeTests
{
    [Fact]
    public void EnvironmentVariableOverridesHome()
    {
        using var temp = new TempFolder();
        var previous = Environment.GetEnvironmentVariable(StudioHome.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(StudioHome.EnvironmentVariable, temp.Path);
            Assert.Equal(Path.GetFullPath(temp.Path), StudioHome.GetDirectory());

            var store = new SettingsStore();
            Assert.Equal(Path.Combine(Path.GetFullPath(temp.Path), SettingsStore.FileName), store.FilePath);
            store.Save(new AppSettings { GamePaksFolder = "env" });
            Assert.True(File.Exists(Path.Combine(temp.Path, SettingsStore.FileName)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StudioHome.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void DefaultHomeIsUnderLocalApplicationData()
    {
        var previous = Environment.GetEnvironmentVariable(StudioHome.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(StudioHome.EnvironmentVariable, null);
            var home = StudioHome.GetDirectory();
            Assert.True(Path.IsPathFullyQualified(home));
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                Assert.Equal(Path.Combine(localAppData, StudioHome.FolderName), home);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(StudioHome.EnvironmentVariable, previous);
        }
    }
}
