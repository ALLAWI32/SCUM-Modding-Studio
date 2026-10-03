using Avalonia.VisualTree;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "the spawner does not tell me what spawns here, and the yellow, blue and pink circles I did not understand".
/// Real game files only (<c>SCUM_PAKS</c>, key from this PC's store): a hangar loot pin is picked on its own and lists its
/// items with their rarity, the whole hangar lists its preset, a blue pin its vehicles, and the legend hides a kind.
/// </summary>
public sealed class SpawnInfoRealTests
{
    [Fact]
    public async Task ALootPinListsItsItemsAVehiclePinItsVehiclesAndTheLegendHidesAKind()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        await map.LoadLevelsAsync(["/Game/ConZ_Files/Maps/The_Island/A_4_Airfield"]);
        map.ShowSpawns = true;

        // A gold pin on a hangar shelf picks as itself (the hangar's id, its own key), not as nothing.
        var hangar = map.AllActors.Single(a => a.Name == "BP_Airplane_Hangar2_2");
        var pin = map.PreparedScene!.Placements.First(p => p.MeshPath == SpawnMarkers.MeshKey(SpawnKind.Loot) && ReferenceEquals(p.Actor, hangar.Actor));
        Assert.Equal(hangar.SelectableId, pin.SelectableId);
        var key = pin.InstanceKey!.Value;
        Assert.True(key.IsLootPoint);

        map.SelectedInstanceKey = key;
        map.SelectedActorId = hangar.SelectableId;
        Assert.True(map.IsLootPointSelected);
        Assert.Null(map.SelectedRootWorld); // no gizmo: dragging the pin must not carry the hangar off
        Assert.False(map.DeleteSelectedCommand.CanExecute(null)); // Delete must not delete the hangar
        Assert.False(map.DuplicateSelectedCommand.CanExecute(null));
        Assert.Contains(map.ActorProperties, r => r.Name == "Loot point");
        await Until(() => map.SpawnInfo.Count > 0);
        Assert.Contains(map.SpawnInfo, r => r.IsHeader && r.Text.Contains("World_Shelf", StringComparison.Ordinal) && r.Detail!.StartsWith("20 %", StringComparison.Ordinal));
        Assert.Contains(map.SpawnInfo, r => r.IsGroup && r.Text.EndsWith("Airfield", StringComparison.Ordinal) && r.Rarity == "Uncommon");
        Assert.Contains(map.SpawnInfo, r => r.IsItem && r.Text == "Car Battery" && r.Rarity == "Rare" && r.Detail == "Tools");

        // The whole hangar: its 25 points, one preset.
        map.SelectedInstanceKey = null;
        await Until(() => map.SpawnInfo.Any(r => r.IsHeader && r.Text.Contains("(25 ", StringComparison.Ordinal)));
        Assert.True(map.DeleteSelectedCommand.CanExecute(null));

        // A blue pin (a world vehicle point at the airfield): the vehicles of its group.
        var place = map.AllActors.First(a => SpawnMarkers.KindOf(a.Actor) == SpawnKind.VehiclePlace);
        map.SelectedActorId = place.SelectableId;
        await Until(() => map.SpawnInfo.Count > 1);
        Assert.Contains(map.SpawnInfo, r => r.IsHeader && r.Text.Contains(place.Actor.ClassPath, StringComparison.Ordinal));
        Assert.Contains(map.SpawnInfo, r => r.IsItem);

        // The legend: every kind has a colour; the circles start off; switching loot off hides the hangar's pins, not the hangar.
        Assert.Equal(11, map.SpawnLayers.Count);
        Assert.False(map.SpawnLayers.Single(l => l.Key == "Zones").IsVisible);
        Assert.True(map.SpawnLayers.Single(l => l.Key == "Vehicles").IsVisible);
        map.SpawnLayers.Single(l => l.Key == "Loot").IsVisible = false;
        Assert.Contains(key, map.HiddenInstanceKeys);
        Assert.DoesNotContain(hangar.SelectableId, map.HiddenActorIds);
        Assert.Contains("Loot", ctx.Services.UiState.Current.HiddenSpawnLayers);
    }

    /// <summary>The panel and the legend as the owner sees them (screenshots with <c>SCUMSTUDIO_SCREENSHOTS</c>).</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheLootListAndTheLegendFitThePanel()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await map.LoadLevelsAsync(["/Game/ConZ_Files/Maps/The_Island/A_4_Airfield"]);
            var hangar = map.AllActors.Single(a => a.Name == "BP_Airplane_Hangar2_2");
            var pin = map.PreparedScene!.Placements.First(p => p.MeshPath == SpawnMarkers.MeshKey(SpawnKind.Loot) && ReferenceEquals(p.Actor, hangar.Actor));
            map.SelectedInstanceKey = pin.InstanceKey;
            map.SelectedActorId = hangar.SelectableId;
            Assert.True(HeadlessUi.PumpUntil(() => map.SpawnInfo.Count > 0, TimeSpan.FromSeconds(60)));
            HeadlessUi.SaveScreenshot(window, "map-loot-info");

            var legend = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "SpawnLegendButton");
            legend.Flyout!.ShowAt(legend);
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "map-spawn-legend");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 600 && !done(); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(done());
    }
}
