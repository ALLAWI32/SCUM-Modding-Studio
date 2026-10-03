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
