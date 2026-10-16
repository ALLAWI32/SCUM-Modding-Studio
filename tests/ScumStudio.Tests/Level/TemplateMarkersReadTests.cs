using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.Spawns;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Level;

/// <summary>
/// seviscache: "indoor loot points show no gizmo when selected". A house's loot spawner component is stored in the level
/// without its markers (the Blueprint template has them); the reader now says so, and its points are editable points of
/// their own like a spawner group's, not read-only pins of the building (audit F275).
/// </summary>
public sealed class TemplateMarkersReadTests
{
    [Fact]
    public void TheReaderTellsMarkersThatComeFromTheTemplate()
    {
        var level = Load();
        var house = level.Actors.Single(a => a.Name == SyntheticSpawnerLevel.House).FindComponent(SyntheticSpawnerLevel.HouseLoot)!;
        Assert.Equal(SyntheticSpawnerLevel.HouseMarkers, house.SpawnMarkers.Select(m => m.Local.Translation));
        Assert.True(house.SpawnMarkersFromTemplate);
        Assert.DoesNotContain(SpawnPointArrays.SpawnerMarkers, house.PropertyNames);

        var group = level.Actors.Single(a => a.Name == SyntheticSpawnerLevel.Group).FindComponent(SyntheticSpawnerLevel.GroupLoot)!;
        Assert.Equal(3, group.SpawnMarkers.Count);
        Assert.False(group.SpawnMarkersFromTemplate); // stored on the export
        Assert.Contains("\"markersFromTemplate\": true", level.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplateArrayOfAStoredComponentIsAnEditableArrayInTheReadersOrder()
    {
        var house = Load().Actors.Single(a => a.Name == SyntheticSpawnerLevel.House);
        var array = Assert.Single(SpawnPointArrays.Of(house));
        Assert.Equal((SyntheticSpawnerLevel.HouseLoot, SpawnPointArrays.SpawnerMarkers), (array.Component, array.Array));
        Assert.Equal([0, 1, 2], array.Points.Select(p => p.Source));
        Assert.Equal(SyntheticSpawnerLevel.HouseMarkers, array.Points.Select(p => p.Local.Location));
        Assert.Same(array.Component, SpawnPointArrays.Find(house, SyntheticSpawnerLevel.HouseLoot)?.Component);

        // The pins are drawn from a changed list the same way as a stored one's.
        var moved = SpawnPointArrays.WithPoints(house, array.Component, [array.Points[2] with { Local = TransformValue.At(0, 0, 77) }]);
        var marker = Assert.Single(moved.FindComponent(SyntheticSpawnerLevel.HouseLoot)!.SpawnMarkers);
        Assert.Equal(new FVector(0, 0, 77), marker.Local.Translation);

        // A component the level does not store (a Blueprint construction part) stays read-only.
        var synthesized = house with { Components = [.. house.Components.Select(c => c.Name == SyntheticSpawnerLevel.HouseLoot ? c with { IsSynthesized = true, ExportIndex = -5 } : c)] };
        Assert.Empty(SpawnPointArrays.Of(synthesized));
    }

    [Fact]
    public void TemplateLootPointsArePointsWithAGizmoNotReadOnlyMarkers()
    {
        var house = Load().Actors.Single(a => a.Name == SyntheticSpawnerLevel.House);
        var pins = SpawnMarkers.PinsOf(house).Where(p => p.Kind == SpawnKind.Loot).ToList();
        Assert.Equal(3, pins.Count);
        Assert.All(pins, p => Assert.Null(p.Marker));
        Assert.Equal([0, 1, 2], pins.Select(p => p.Point!.Value.Index));
        Assert.All(pins, p => Assert.Equal(SyntheticSpawnerLevel.HouseLoot, p.Point!.Value.Key));
        var root = SyntheticSpawnerLevel.HouseAt + SyntheticSpawnerLevel.HouseLootAt;
        Assert.True(FVector.Distance(pins[1].Pin.Translation, root + SyntheticSpawnerLevel.HouseMarkers[1]) < 0.01f);
    }

    private static LevelDocument Load()
    {
        using var temp = new LevelTempDirectory();
        SyntheticSpawnerLevel.WriteContent(temp.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        return LevelDocument.Load(new Cue4ParseLevelReader(catalog), SyntheticSpawnerLevel.LevelPath);
    }
}
