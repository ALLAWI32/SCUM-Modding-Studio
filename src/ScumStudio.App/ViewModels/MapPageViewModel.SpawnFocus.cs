using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Show on the Map from the Spawns page (owner: "a button that switches to the Map page with the camera on that exact place
/// and the place selected"): the spawn pins are switched on (and the place's kind in the legend), the place's sector loads
/// when it is not shown yet (with the whole island streaming, the camera going there loads it), the camera looks at the
/// place and the place's actor is selected, so the gizmo moves, turns, copies and deletes it like any object.
/// </summary>
public sealed partial class MapPageViewModel
{
    // How long the place may take to come in once the camera is there (a sector or the streamed levels around it).
    private static readonly TimeSpan SpawnFocusWait = TimeSpan.FromSeconds(120);

    /// <summary>The last Show on the Map (tests wait for it).</summary>
    internal Task SpawnFocus { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Puts the camera on <paramref name="place"/> (as the project has it) and selects it once it is loaded. The places come
    /// in over the loaded sectors where the game has them (<paramref name="home"/>; a new place where it was made), so a
    /// place moved into another sector loads with its home sector.
    /// </summary>
    public void ShowSpawnPlace(SpawnPlace place, FVector home)
    {
        ArgumentNullException.ThrowIfNull(place);
        SpawnFocus = ShowSpawnPlaceAsync(place, home);
    }

    private async Task ShowSpawnPlaceAsync(SpawnPlace place, FVector home)
    {
        var reference = new ActorRef(SpawnPlaces.StaticDataPath, place.ActorName);
        var at = place.Transform.Translation;
        ShowSpawns = true;
        var layer = place.Kind switch
        {
            SpawnPlaceKind.Vehicle => "Vehicles",
            SpawnPlaceKind.Zone => "Zones",
            SpawnPlaceKind.Animal => "Animals",
            _ => "Zombies",
        };
        foreach (var hidden in SpawnLayers.Where(l => l.Key == layer && !l.IsVisible))
        {
            hidden.IsVisible = true;
        }

        WorldStatus = Loc.F("Map.SpawnFocus.Going", place.ActorName);
        _services.Logger.LogInformation("Map: showing spawn place {Place} at {X:0}, {Y:0}, {Z:0}.", place.ActorName, at.X, at.Y, at.Z);
        await _loadTask.ConfigureAwait(true); // the island's index

        // The page was just switched to: its view comes up with the next layout and only then listens for the camera.
        for (var i = 0; i < 100 && (FrameSectorRequested is null || FrameSelectionRequested is null); i++)
        {
            await Task.Delay(50).ConfigureAwait(true);
        }

        if (World is null)
        {
            WorldStatus = Loc.F("Map.SpawnFocus.NotLoaded", place.ActorName); // no game files: nothing to show it in
            return;
        }

        if (ActorOf(reference) is null && World is { } world && (CellAt(world, home.X, home.Y) ?? CellAt(world, at.X, at.Y)) is { } cell && Cells.Contains(cell)
            && !(LoadMode == MapLoadMode.Island && IsWorldMode))
        {
            await (LevelLoadCompletion = LoadSectorAsync(cell)).ConfigureAwait(true);
        }

        // The camera looks at the place from 15 m (with the island streaming, this is what loads it).
        FrameSectorRequested?.Invoke(new CameraShot(at + (new FVector(-0.54f, -0.54f, 0.64f) * 1500f), at));
        var deadline = DateTime.UtcNow + SpawnFocusWait;
        while (ActorOf(reference) is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250).ConfigureAwait(true);
        }

        if (ActorOf(reference) is not { } item)
        {
            WorldStatus = Loc.F("Map.SpawnFocus.NotLoaded", place.ActorName);
            return;
        }

        SelectedActor = item;
        FrameSelectionRequested?.Invoke(this, EventArgs.Empty);
        WorldStatus = Loc.F("Map.SpawnFocus.Done", place.ActorName);
    }
}
