using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;

namespace ScumStudio.Tests.Level;

/// <summary>
/// D02 ("phantom fridges", defolt_0999, O Piqueno): a fridge placed with Add object is a world item spawner, and the server
/// saves the fridge it spawned, so taking the spawner out of the mod leaves the fridge. A pak cannot remove it; the export
/// at least lists the spawners it no longer has, also those an earlier export placed and the project since reverted.
/// </summary>
public sealed class ItemSpawnerExportTests : IDisposable
{
    private const string Level = "/Game/ConZ_Files/Maps/The_Island/Z_4_Prvo_Selo_08";
    private const string Fridge = "/Game/ConZ_Files/Items/Kitchen/Refrigerator.Refrigerator_C";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-spawners-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void ASpawnerAnEarlierExportPlacedIsListedWhenItIsGone()
    {
        Directory.CreateDirectory(_dir);
        var placed = new EditState();
        placed.Apply(new AddBlueprintActorOp(Level, "Refrigerator_Added", PlacedBlueprintIndex.WorldItemSpawnerClass, new ActorRef(Level, "WorldItemSpawner_5"), TransformValue.Identity) { Item = Fridge });
        var warnings = new List<string>();

        Assert.Empty(ProjectExporter.ItemSpawnersGone(placed, _dir, new Dictionary<string, LevelDocument?>(), warnings));
        Assert.Contains("Refrigerator_Added", File.ReadAllText(Path.Combine(_dir, ProjectExporter.ItemSpawnersFileName)), StringComparison.Ordinal);

        // The project reverted the fridge: the next export names it (its fridge stays on the server).
        var gone = ProjectExporter.ItemSpawnersGone(new EditState(), _dir, new Dictionary<string, LevelDocument?>(), warnings);
        var line = Assert.Single(gone);
        Assert.Contains("Refrigerator_Added", line, StringComparison.Ordinal);
        Assert.Contains(Fridge, line, StringComparison.Ordinal);
        Assert.Contains("earlier export", line, StringComparison.Ordinal);
        Assert.Empty(warnings);

        // Once listed, it is not listed again.
        Assert.Empty(ProjectExporter.ItemSpawnersGone(new EditState(), _dir, new Dictionary<string, LevelDocument?>(), warnings));
    }

    [Fact]
    public void ASpawnerIsTheSpawnerActorOrAnActorWithASpawnerComponent()
    {
        Assert.True(ProjectExporter.IsItemSpawner(Actor(PlacedBlueprintIndex.WorldItemSpawnerClass)));
        Assert.True(ProjectExporter.IsItemSpawner(Actor("/Game/Buildings/BP_Kitchen.BP_Kitchen_C",
            new ComponentRecord(7, "WorldItemSpawner", "WorldItemSpawnerComponent", true, null, FTransform.Identity, FTransform.Identity, null, []) { ClassPath = "/Script/SCUM.WorldItemSpawnerComponent" })));
        Assert.False(ProjectExporter.IsItemSpawner(Actor("/Script/Engine.StaticMeshActor")));
    }

    private static ActorRecord Actor(string classPath, params ComponentRecord[] components) =>
        new(5, "Fridge_1", classPath, null, components, FTransform.Identity, ActorKind.Other, null, []);
}
