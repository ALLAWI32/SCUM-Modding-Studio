using System.Text.Json;
using ScumStudio.Core.Settings;
using ScumStudio.Tests.Core;

namespace ScumStudio.Tests.App;

/// <summary>Settings › Smooth camera and Steady foliage in motion (owner: the Map should move like the game): on by default, kept, and on for an older settings file.</summary>
public sealed class MotionSettingsTests
{
    [Fact]
    public void SmoothCameraAndSteadyFoliageAreOnByDefaultAndKept()
    {
        var defaults = new UiPreferences();
        Assert.True(defaults.SmoothCamera);
        Assert.True(defaults.SteadyFoliage);

        // A settings file from 3.4.0 has neither: both on.
        var older = JsonSerializer.Deserialize<UiPreferences>("{\"antiAliasedFoliage\": true}", SettingsStore.JsonOptions)!;
        Assert.True(older.SmoothCamera && older.SteadyFoliage);

        using var temp = new TempFolder();
        var store = new SettingsStore(temp.Path);
        store.Save(new AppSettings { Ui = new UiPreferences { SmoothCamera = false, SteadyFoliage = false } });
        var loaded = new SettingsStore(temp.Path).Load().Ui;
        Assert.False(loaded.SmoothCamera);
        Assert.False(loaded.SteadyFoliage);
    }
}
