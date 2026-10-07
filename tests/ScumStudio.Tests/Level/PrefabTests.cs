using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.Level;

/// <summary>A prefab file is plain, indented JSON that names its objects, meshes and positions, and reads back exactly.</summary>
public sealed class PrefabTests
{
    private static Prefab Sample() => new()
    {
        Name = "Farm corner",
        Created = new DateTimeOffset(2026, 10, 6, 12, 30, 0, TimeSpan.Zero),
        AppVersion = "0.2.7",
        Pivot = new FVector(100f, 200f, 30f),
        Parts =
        [
            new PrefabPart { Kind = PrefabPartKind.StockActor, Level = Outpost, Actor = "BP_House_C_3", Class = "/Game/X/BP_House.BP_House_C", Transform = new TransformValue(FVector.Zero, new FRotator(0f, 90f, 0f), FVector.One) },
            new PrefabPart { Kind = PrefabPartKind.StaticMesh, Mesh = "/Game/X/SM_Crate.SM_Crate", Collision = "BlockAll", Transform = TransformValue.At(300f, 0f, 0f), Bend = new BendValue(12f, 20f) },
            new PrefabPart { Kind = PrefabPartKind.Blueprint, Level = Outpost, Actor = "WorldItemSpawner_5", Class = "/Script/SCUM.WorldItemSpawner", Item = "/Game/X/Chest.Chest_C", Transform = TransformValue.At(-250f, 40f, 0f) },
            new PrefabPart { Kind = PrefabPartKind.FoliageInstance, Level = Outpost, Actor = "InstancedFoliageActor_0", Component = "HISM_Oak", Mesh = "/Game/F/SM_Oak.SM_Oak", Collision = "SCUM_TreeStump", Transform = new TransformValue(new FVector(0f, 500f, 0f), FRotator.Zero, new FVector(1.2f, 1.2f, 1.2f)) },
        ],
    };

    [Fact]
    public void RoundTripsThroughReadableJson()
    {
        var prefab = Sample();
        var json = prefab.ToJson();
        Assert.Contains("\"format\": \"scumstudio.prefab/1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"stockActor\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"foliageInstance\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mesh\": \"/Game/X/SM_Crate.SM_Crate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"location\": [300,0,0]", json, StringComparison.Ordinal);
        Assert.Contains("\"rotation\": [0,90,0]", json, StringComparison.Ordinal);
        Assert.Contains("\"pivot\": [100,200,30]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"item\": null", json, StringComparison.Ordinal); // nulls are left out
        Assert.True(json.Split('\n').Length > 30, "indented, one value per line");

        var back = Prefab.FromJson(json);
        Assert.Equal(prefab.Name, back.Name);
        Assert.Equal(prefab.Created, back.Created);
        Assert.Equal(prefab.AppVersion, back.AppVersion);
        Assert.Equal(prefab.Pivot, back.Pivot);
        Assert.Equal(prefab.Parts, back.Parts);
        Assert.Equal("BP_House", back.Parts[0].DisplayName);
        Assert.Equal("WorldItemSpawner", back.Parts[2].DisplayName);

        using var temp = new LevelTempDirectory();
        var path = temp.Combine("lib", "Farm corner" + Prefab.Extension);
        prefab.Save(path);
        Assert.Equal(json, File.ReadAllText(path));
        Assert.Equal(prefab.Parts, Prefab.Load(path).Parts);
    }

    [Fact]
    public void RefusesOtherFilesAndEmptyPrefabs()
    {
        Assert.Throws<InvalidDataException>(() => Prefab.FromJson("not json at all"));
        Assert.Throws<InvalidDataException>(() => Prefab.FromJson("{\"hello\":1}")); // some other JSON
        Assert.Throws<InvalidDataException>(() => Prefab.FromJson("{\"format\":\"other/1\",\"parts\":[{\"kind\":\"staticMesh\"}]}"));
        Assert.Throws<InvalidDataException>(() => Prefab.FromJson((Sample() with { Parts = [] }).ToJson()));
        Assert.Throws<InvalidDataException>(() => Prefab.FromJson("{\"format\":\"scumstudio.prefab/1\",\"parts\":null}"));
        Assert.Throws<InvalidDataException>(() => Prefab.FromJson("{\"format\":\"scumstudio.prefab/1\",\"parts\":[null]}"));
        Assert.Equal("a_b_c", Prefab.SafeFileName("a/b:c"));
        Assert.Equal("Prefab", Prefab.SafeFileName("  "));
    }
}
