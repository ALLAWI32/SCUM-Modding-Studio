using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Spawns;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Indoor loot points (seviscache, P1 #5): a building's item spawner takes its <c>SpawnerMarkers</c> from its Blueprint's
/// template, so the level stores none. Moved in the studio, the template's array is written onto the level's component.
/// </summary>
public sealed class TemplateMarkersExportTests
{
    private const string Engine = "/Script/Engine";
    private const string ConZ = "/Script/ConZ";
    private const string Shed = "/Game/ConZ_Files/Blueprints/BP_Shed";
    private const string ShedBase = "/Game/ConZ_Files/Blueprints/BP_ShedBase";
    private const string Preset = "/Game/Loot/Examine_Shelf.Examine_Shelf_C";

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the Blueprint's template stores none either: its own template (the parent Blueprint's) does
    public async Task MovingATemplateLootPointWritesTheTemplatesArrayOntoTheComponent(bool inherited)
    {
        var packages = new Dictionary<string, CookedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            [Shed] = Blueprint("BP_Shed", inherited ? null : Markers, inherited ? ShedBase : null),
            [ShedBase] = Blueprint("BP_ShedBase", Markers, null),
        };
        var level = Level();
        var lootIndex = CopyExportTests.Child(level, CopyExportTests.Child(level, LevelPackageEditor.FindLevelExport(level) + 1, "Shed_1") + 1, "Loot");
        var moved = new TransformValue(new(222, 333, 44), new(0, 90, 0), new(1, 1, 1));
        IReadOnlyList<SpawnPoint> points = [new SpawnPoint(0, TransformValue.At(10, 0, 50)), new SpawnPoint(1, moved), new SpawnPoint(2, TransformValue.At(30, 0, 50))];
        var patch = new SpawnPointsPatch("Shed_1", "Loot", SpawnPointArrays.SpawnerMarkers, points);

        // Without the Blueprint (or before this fix): refused as before.
        var (_, refused) = LevelPackageEditor.Apply(level, new LevelEditRequest { SpawnPoints = [patch] });
        Assert.Contains(refused.Warnings, w => w.Contains("array is not stored", StringComparison.Ordinal));

        var (bytes, report) = LevelPackageEditor.Apply(level, new LevelEditRequest { SpawnPoints = [patch], Packages = path => packages.GetValueOrDefault(path) });

        Assert.Empty(report.Warnings);
        Assert.Equal(["Shed_1.Loot.SpawnerMarkers (3 point(s), from its template)"], report.PatchedTransforms);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        for (var i = 0; i < level.Exports.Count; i++)
        {
            if (i != lootIndex)
            {
                Assert.Equal(level.GetExportBytes(i), edited.GetExportBytes(i));
            }
        }

        var component = edited.ReadProperties(lootIndex);
        Assert.NotNull(component.Find("RelativeLocation")); // what it stored stays
        var markers = Assert.IsType<ArrayValue>(component.Find(SpawnPointArrays.SpawnerMarkers)!.Value).Items.Cast<StructValue>().ToList();
        Assert.Equal(3, markers.Count);
        Assert.Equal([(10f, 0f, 50f), (222f, 333f, 44f), (30f, 0f, 50f)], markers.Select(m => Translation(m)).ToList());
        var rotation = Assert.IsType<QuatValue>(((StructValue)markers[1].Find("Transform")!.Value).Find("Rotation")!.Value);
        Assert.Equal(moved.ToTransform().Rotation.Z, rotation.Z, 1e-5f);
        foreach (var marker in markers)
        {
            var preset = Assert.IsType<ObjectValue>(((StructValue)marker.Find("SpawnerPreset")!.Value).Find("Preset")!.Value);
            Assert.True(preset.Index < 0, "the preset class is an import of the level");
            Assert.Equal(Preset, edited.GetFullPath(preset.Index));
            Assert.Contains(preset.Index, CopyExportTests.CreateBeforeSerialize(edited, lootIndex));
        }

        // The level reader takes the points from the component now, without the Blueprint at hand.
        using var temp = new LevelTempDirectory();
        var basePath = temp.Combine("SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "A_0_Tile");
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        await bytes.WriteAsync(basePath, ".umap");
        using var catalog = ScumStudio.Assets.Catalog.AssetCatalog.OpenLoose(temp.Path);
        var read = ScumStudio.Level.Model.LevelDocument.Load(new ScumStudio.Level.Reading.Cue4ParseLevelReader(catalog), "/Game/ConZ_Files/Maps/The_Island/A_0_Tile")
            .FindActor("Shed_1")!.FindComponent("Loot")!.SpawnMarkers;
        Assert.Equal(3, read.Count);
        Assert.True(FVector.Distance(read[1].Local.Translation, new FVector(222, 333, 44)) < 1e-3f, read[1].Local.Translation.ToString());
        Assert.All(read, m => Assert.Equal(Preset, m.PresetPath));
    }

    private static (float, float, float) Translation(StructValue marker) =>
        ((StructValue)marker.Find("Transform")!.Value).Find("Translation")!.Value is VectorValue v ? (v.X, v.Y, v.Z) : (float.NaN, 0, 0);

    /// <summary>Three loot points of the shelf preset, 10, 20 and 30 cm along X, 50 cm up.</summary>
    private static void Markers(SyntheticLevelPackage p, SyntheticLevelPackage.TagWriter t)
    {
        var preset = p.ObjectImport("/Game/Loot/Examine_Shelf", Engine, "BlueprintGeneratedClass", "Examine_Shelf_C");
        t.StructArray(SpawnPointArrays.SpawnerMarkers, "ItemSpawnerMarker", Marker(10), Marker(20), Marker(30));

        Action<SyntheticLevelPackage.TagWriter> Marker(float x) => e =>
        {
            e.TaggedStruct("Transform", "Transform", s =>
            {
                s.Quat("Rotation", 0, 0, 0, 1);
                s.Vector("Translation", x, 0, 50);
                s.Vector("Scale3D", 1, 1, 1);
            });
            e.TaggedStruct("SpawnerPreset", "ItemSpawnerPresetWithOverrides", s =>
            {
                s.Object("Preset", preset);
                s.Float("Probability", 75);
            });
        };
    }

    /// <summary>A Blueprint package: its class and the item spawner component's template (with the markers, or pointing at a parent's).</summary>
    private static CookedPackage Blueprint(string name, Action<SyntheticLevelPackage, SyntheticLevelPackage.TagWriter>? markers, string? parent)
    {
        var p = new SyntheticLevelPackage();
        var parentTemplate = parent is null ? 0
            : p.Import(ConZ, "ItemSpawnerComponent", p.ObjectImport(parent, Engine, "BlueprintGeneratedClass", parent[(parent.LastIndexOf('/') + 1)..] + "_C"), "Loot_GEN_VARIABLE");
        var cls = p.Export(name + "_C", p.ScriptClass(Engine, "BlueprintGeneratedClass"), 0);
        var template = p.Export("Loot_GEN_VARIABLE", p.ScriptClass(ConZ, "ItemSpawnerComponent"), cls, template: parentTemplate);
        p.SetPayload(cls, p.Properties());
        p.SetPayload(template, p.Properties(t => markers?.Invoke(p, t), w => w.I32(0)));
        var built = p.Build();
        return CookedPackage.Parse(built.UAsset, built.UExp);
    }

    /// <summary>A level with BP_Shed's actor: its loot component stores only its place; the markers are its template's.</summary>
    private static CookedPackage Level()
    {
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_Tile", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var bpClass = p.ObjectImport(Shed, Engine, "BlueprintGeneratedClass", "BP_Shed_C");
        var lootTemplate = p.Import(ConZ, "ItemSpawnerComponent", bpClass, "Loot_GEN_VARIABLE");
        var shed = p.Export("Shed_1", bpClass, level);
        var root = p.Export("DefaultSceneRoot", p.ScriptClass(Engine, "SceneComponent"), shed);
        var loot = p.Export("Loot", p.ScriptClass(ConZ, "ItemSpawnerComponent"), shed, template: lootTemplate);
        p.SetPayload(world, p.Properties(native: w => { w.I32(level); w.I32(0); w.I32(0); }));
        p.SetPayload(level, p.Properties(native: w => CopyExportTests.LevelTail(w, [shed])));
        p.SetPayload(shed, p.Properties(t => t.Object("RootComponent", root)));
        p.SetPayload(root, p.Properties(t => t.Vector("RelativeLocation", 1000, 0, 0)));
        p.SetPayload(loot, p.Properties(t =>
        {
            t.Object("AttachParent", root);
            t.Vector("RelativeLocation", 0, 0, 10);
        }, w => w.I32(0)));
        var built = p.Build();
        return CookedPackage.Parse(built.UAsset, built.UExp);
    }
}
