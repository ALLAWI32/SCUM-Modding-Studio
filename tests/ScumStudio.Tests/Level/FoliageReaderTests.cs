using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Tests.Level;

/// <summary>
/// SCUM paints foliage with its own native components (FoliageInstancedTree/Bush/Grass). They must be read with their
/// instances: before the fix only 3,252 of the 9,309 instances of this tile were found (trees and bushes missing).
/// </summary>
public sealed class FoliageReaderTests
{
    [MapSliceFact]
    public void ScumFoliageComponentsKeepTheirInstances()
    {
        using var catalog = MapSlice.Open();
        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), MapSlice.MapsPath + "Landscape_A_0_4c");
        Assert.Equal(9309, doc.InstanceCount);
        var bushes = doc.Actors.SelectMany(a => a.Components).Where(c => c.ClassName == "FoliageInstancedBush").ToList();
        Assert.NotEmpty(bushes);
        Assert.All(bushes, c => Assert.True(c.IsInstanced && c.Instances.Count > 0, c.Name));

        // Foliage carries the foliage type's cull distance (InstanceEndCullDistance), which every instance placement inherits.
        var culled = doc.Actors.SelectMany(a => a.Components).Where(c => c.IsInstanced && c.InstanceEndCullDistance > 0).ToList();
        Assert.NotEmpty(culled);
        var placements = ScumStudio.Viewport.LevelScenePreparer.CollectPlacements(doc, 0);
        Assert.Contains(placements, p => p.Instance is not null && p.CullDistance > 0f);
        Assert.All(placements.Where(p => p.Instance is not null), p =>
            Assert.Equal(p.Actor.Components.Single(c => c.ExportIndex == p.Instance!.ComponentExportIndex).InstanceEndCullDistance, p.CullDistance));
    }
}

/// <summary>Helper volumes (fake lit-window boxes, environment descriptions) are not drawn: they hid buildings as white blocks.</summary>
public sealed class HelperMeshTests
{
    [MapSliceFact]
    public void WindowLightBoxesAndEnvironmentVolumesAreNotDrawn()
    {
        using var catalog = MapSlice.Open();
        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), MapSlice.MapsPath + "A_0_Outpost_Ext_Hospital");
        Assert.Contains(doc.Actors.SelectMany(a => a.Components), c => c.StaticMeshPath?.Contains("/WindowLights/", StringComparison.Ordinal) == true);
        var placements = ScumStudio.Viewport.LevelScenePreparer.CollectPlacements(doc, 0);
        Assert.NotEmpty(placements);
        Assert.DoesNotContain(placements, p => p.MeshPath.Contains("/WindowLights/", StringComparison.Ordinal));
        Assert.DoesNotContain(placements, p => p.Component?.ClassName == "EnvironmentDescriptionComponent");
    }
}

/// <summary>The game's spawn places are drawn as coloured pins (owner: show the default spawn places and let me move them).</summary>
public sealed class SpawnPinTests
{
    [Fact]
    public void APinPointsDownAtItsPlaceInItsKindsColour()
    {
        var asset = ScumStudio.Viewport.SpawnMarkers.Asset(ScumStudio.Viewport.SpawnKind.Sentry);

        Assert.True(ScumStudio.Viewport.SpawnMarkers.IsMarker(asset.MeshPath));
        Assert.Equal(0f, asset.Mesh.Bounds.Min.Z);
        Assert.True(asset.Mesh.Bounds.Max.Z > 100f);
        var colour = ScumStudio.Viewport.SpawnMarkers.Color(ScumStudio.Viewport.SpawnKind.Sentry);
        Assert.Equal(colour with { W = ScumStudio.Viewport.SpawnMarkers.StandInAlpha }, asset.MaterialTints[asset.Mesh.Sections[0].MaterialName]); // a stand-in: half transparent
        Assert.Null(ScumStudio.Viewport.SpawnMarkers.AssetFor("/Game/Some/Mesh.Mesh"));
    }

    [MapSliceFact]
    public void TheOutpostCarShopShowsWhereItsVehiclesAppear()
    {
        using var catalog = MapSlice.Open();
        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), MapSlice.MapsPath + "A_0_Outpost");
        var placements = ScumStudio.Viewport.LevelScenePreparer.CollectPlacements(doc, 0);
        var pins = placements.Where(p => p.MeshPath == ScumStudio.Viewport.SpawnMarkers.MeshKey(ScumStudio.Viewport.SpawnKind.Vehicle)).ToList();

        Assert.NotEmpty(pins);
        // Inside the shop's Blueprint: a small pin that picks its box as a part of the shop (Discord igor: the click picked nothing).
        Assert.All(pins, p => Assert.Equal(0.7f, p.World.Scale3D.X));
        Assert.All(pins, p => Assert.Equal(ScumStudio.Viewport.LevelScenePreparer.SelectableIdOf(0, p.Actor), p.SelectableId));
        Assert.All(pins, p => Assert.Equal(ScumStudio.Viewport.InstanceKey.Part, p.InstanceKey!.Value.InstanceIndex));
    }
}
