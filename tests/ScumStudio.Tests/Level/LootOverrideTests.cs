using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.Spawns;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Loot that the studio changes is the loot the game gives (Discord, redriver6155, 3.5: "default and custom loot merged.
/// it keeps both. that means all new and removed items will spawn"), and placed objects can have no loot inside
/// (defolt_0999: "disable loot in the objects you add"). A searchable mesh asset carries its own examine entry, which the
/// game reads too; a changed component therefore draws a loot-free copy of its mesh (see <see cref="MeshLoot"/>).
/// </summary>
public sealed class LootOverrideTests
{
    private const string Engine = "/Script/Engine";
    private const string Level = "/Game/ConZ_Files/Maps/The_Island/A_0_Synth_Loot";
    private const string DeskMesh = "/Game/X/SM_Desk.SM_Desk";
    private const string ShelfMesh = "/Game/X/SM_Shelf.SM_Shelf";
    private const string LockerMesh = "/Game/X/SM_Locker.SM_Locker";
    private const string CrateMesh = "/Game/X/SM_Crate.SM_Crate";
    private const string DeskPreset = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Desk.Examine_Desk_C";
    private const string ToolDeskPreset = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Tool_Desk.Examine_Tool_Desk_C";
    private const string LockerPreset = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Locker.Examine_Locker_C";
    private const string CratePreset = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Crate.Examine_Crate_C";
    private const string WeaponPreset = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Weapon.Examine_Weapon_C";

    private static readonly TransformValue Somewhere = new(new FVector(100, 200, 0), FRotator.Zero, FVector.One);

    /// <summary>
    /// Mesh assets as the game has them: the desk, locker and crate meshes carry an examine entry of their own (the desk's
    /// differs from the one the building's desk component stores), the shelf none.
    /// </summary>
    private static MeshLoot GameMeshes() => new(mesh => mesh switch
    {
        DeskMesh => new MeshLootInfo(true, [ToolDeskPreset], true),
        LockerMesh => new MeshLootInfo(true, [LockerPreset], true),
        CrateMesh => new MeshLootInfo(true, [CratePreset], true),
        _ => MeshLootInfo.None,
    });

    /// <summary>
    /// A building (desk with its own preset, a shelf, an item spawner with two loot points, a locker slot whose stored child
    /// actor is a locker) and a crate of a mesh actor whose loot is only its mesh's.
    /// </summary>
    private static LevelDocument Document()
    {
        var b = new FakeLevelBuilder(Level);
        var building = b.Actor("Building_1", "/Game/X/BP_Building.BP_Building_C", blueprint: true);
        var root = b.Component(building, "DefaultSceneRoot");
        b.Root(building, root);
        var desk = b.Component(building, "Desk", "StaticMeshComponent", attachTo: root, mesh: DeskMesh);
        var shelf = b.Component(building, "Shelf", "StaticMeshComponent", attachTo: root, mesh: ShelfMesh);
        var spawner = b.Component(building, "ItemSpawner", "ItemSpawnerComponent", attachTo: root);
        var slot = b.Component(building, "LockerSlot", "ChildActorComponent", attachTo: root);
        var locker = b.Actor("Locker_CAT", "/Game/X/BPLockpick_Locker.BPLockpick_Locker_C", blueprint: true);
        var lockerMesh = b.Component(locker, "SM_Locker", "StaticMeshComponent", mesh: LockerMesh);
        b.Root(locker, lockerMesh);
        var crate = b.Actor("Crate_1", "/Script/Engine.StaticMeshActor");
        var crateMesh = b.Component(crate, "StaticMeshComponent0", "StaticMeshComponent", mesh: CrateMesh);
        b.Root(crate, crateMesh);
        var data = b.Build();
        return LevelDocument.FromData(data with
        {
            Exports = data.Exports.Select(e => e.Index switch
            {
                _ when e.Index == desk => e with { IsStaticMeshComponent = true, LootPresets = [DeskPreset] },
                _ when e.Index == shelf || e.Index == crateMesh => e with { IsStaticMeshComponent = true },
                _ when e.Index == lockerMesh => e with { IsStaticMeshComponent = true, LootPresets = [LockerPreset] },
                _ when e.Index == spawner => e with
                {
                    SpawnMarkersFromTemplate = true,
                    SpawnMarkers = [new SpawnMarker(FTransform.Identity, "World_Shelf", 30, 1, 1), new SpawnMarker(FTransform.Identity, "World_Shelf", 30, 1, 1)],
                },
                _ when e.Index == slot => e with { ChildActorClassPath = "/Game/X/BPLockpick_Locker.BPLockpick_Locker_C", ChildActor = locker },
                _ when e.Index == locker => e with { ParentComponent = slot },
                _ => e,
            }).ToList(),
        });
    }

    [Fact]
    public void NoLootIsAnEditOfPlacedObjectsOnlyWithUndoAndAJournalLine()
    {
        var state = new EditState();
        var stock = new ActorRef(Level, "Building_1");
        Assert.Contains("not an object the project placed", state.Validate(new SetNoLootOp(stock, false, true)));

        var add = new AddStaticMeshActorOp(Level, "SM_Crate_Added", CrateMesh, Somewhere);
        state.Apply(add);
        var on = new SetNoLootOp(add.Created, false, true);
        Assert.Null(state.Validate(on));
        state.Apply(on);
        Assert.True(state.IsNoLoot(add.Created));
        Assert.Contains("no-loot ", state.Describe());
        Assert.Contains("out of date", state.Validate(on)); // already on
        state.Apply(on.Inverse());
        Assert.False(state.IsNoLoot(add.Created));

        var line = on.ToJson();
        Assert.Contains("\"op\":\"setNoLoot\"", line);
        Assert.Equal(on, EditOp.FromJson(line));
        var renamed = new ActorRef(Level, "SM_Crate_Shared");
        Assert.Equal(on with { Target = renamed }, EditOpRemap.Rename(on, new Dictionary<ActorRef, ActorRef> { [add.Created] = renamed }, new Dictionary<InstanceRef, InstanceRef>(), new Dictionary<string, string>()));
        Assert.Contains(typeof(SetNoLootOp), EditOpRemap.KnownTypes);

        // Placed and set in one step (the Add object box's default), then removed with the object.
        var batch = new BatchOp("Add", [new AddStaticMeshActorOp(Level, "SM_Crate_2", CrateMesh, Somewhere), new SetNoLootOp(new ActorRef(Level, "SM_Crate_2"), false, true)]);
        Assert.Null(state.Validate(batch));
        state.Apply(batch);
        Assert.True(state.IsNoLoot(new ActorRef(Level, "SM_Crate_2")));
        state.Apply(batch.Inverse());
        Assert.Empty(state.NoLootActors);
    }

    [Fact]
    public void ACopyOfAnObjectWithoutLootHasNoneEither()
    {
        var state = new EditState();
        var source = new AddStaticMeshActorOp(Level, "SM_Crate_Added", CrateMesh, Somewhere);
        state.Apply(source);
        state.Apply(new SetNoLootOp(source.Created, false, true));
        var edits = state.EditsOf(source.Created);
        Assert.True(edits.NoLoot);
        Assert.False(edits.IsEmpty);

        var copy = new ActorRef(Level, "SM_Crate_Copy");
        var ops = edits.OpsOn(copy, rootMeshAndLootBaked: true);
        Assert.Contains(new SetNoLootOp(copy, false, true), ops);
        state.Apply(new BatchOp("Duplicate", [new AddStaticMeshActorOp(Level, copy.Actor, CrateMesh, Somewhere), .. ops]));
        Assert.True(state.IsNoLoot(copy));
    }

    [Fact]
    public void APlacedBuildingWithNoLootInsideEmptiesItsPartsItsLockersAndItsLootPoints()
    {
        var state = new EditState();
        var duplicate = new DuplicateActorOp(new ActorRef(Level, "Building_1"), "Building_1_Copy", Somewhere);
        state.Apply(duplicate);
        state.Apply(new SetNoLootOp(duplicate.Created, false, true));
        var meshes = GameMeshes();
        var warnings = new List<string>();

        var request = ProjectExporter.PlanLevel(state, Level, Document(), warnings, null, null, meshLoot: meshes);

        // The desk and the locker carried by the building: no entries, a loot-free copy of their meshes (both carry loot).
        Assert.Equal(new[] { "Desk", "LockerSlot/SM_Locker" }, request.Loot.Where(l => l.Actor == "Building_1_Copy").Select(l => l.Component).Order());
        Assert.All(request.Loot, l => Assert.Equal(new LootSetting(false, []), l.Loot));
        var desk = Assert.Single(request.Meshes, m => m.Component == "Desk");
        Assert.Equal("/Game/X/SM_Desk_NoLoot.SM_Desk_NoLoot", desk.StaticMesh);
        Assert.True(desk.IsLootFreeCopy);
        Assert.Equal("/Game/X/SM_Locker_NoLoot.SM_Locker_NoLoot", Assert.Single(request.Meshes, m => m.Component == "LockerSlot/SM_Locker").StaticMesh);
        Assert.DoesNotContain(request.Meshes, m => m.Component == "Shelf"); // no loot of its own, none in its mesh: untouched
        Assert.Equal(new[] { DeskMesh, LockerMesh }, meshes.Requested.Order());

        // The item spawner's two loot points go.
        var points = Assert.Single(request.SpawnPoints);
        Assert.Equal(("Building_1_Copy", "ItemSpawner", SpawnPointArrays.SpawnerMarkers), (points.Actor, points.Component, points.Array));
        Assert.Empty(points.Points);
        Assert.Contains(warnings, w => w.Contains("no loot inside: 2 searchable part(s) emptied, 2 loot point(s) taken out", StringComparison.Ordinal));

        // The original keeps its loot.
        Assert.DoesNotContain(request.Loot, l => l.Actor == "Building_1");
    }

    [Fact]
    public void ChangedLootReplacesTheLootItsMeshCarries()
    {
        var crate = new ActorRef(Level, "Crate_1");
        var building = new ActorRef(Level, "Building_1");
        var stockCrate = new LootSetting(true, [CratePreset]);

        // redriver's case: the crate's only loot is its mesh's; another preset instead must not give both.
        var replaced = new EditState();
        replaced.Apply(new SetLootOp(crate, null, stockCrate, new LootSetting(true, [WeaponPreset])));
        var meshes = GameMeshes();
        var request = ProjectExporter.PlanLevel(replaced, Level, Document(), [], null, null, meshLoot: meshes);
        var loot = Assert.Single(request.Loot);
        Assert.Equal(("Crate_1", "StaticMeshComponent0", new LootSetting(true, [WeaponPreset])), (loot.Actor, loot.Component, loot.Loot));
        Assert.Equal("/Game/X/SM_Crate_NoLoot.SM_Crate_NoLoot", Assert.Single(request.Meshes).StaticMesh);

        // Decoration: no entries and the loot-free copy (else the mesh's own loot would still be found).
        var decoration = new EditState();
        decoration.Apply(new SetLootOp(crate, null, stockCrate, new LootSetting(false, [])));
        request = ProjectExporter.PlanLevel(decoration, Level, Document(), [], null, null, meshLoot: GameMeshes());
        Assert.Equal(new LootSetting(false, []), Assert.Single(request.Loot).Loot);
        Assert.True(Assert.Single(request.Meshes).IsLootFreeCopy);

        // Exactly the mesh's loot on a component that stores another preset: its entries go, the stock mesh stays.
        var asMesh = new EditState();
        asMesh.Apply(new SetLootOp(building, "Desk", new LootSetting(true, [DeskPreset]), new LootSetting(true, [ToolDeskPreset])));
        request = ProjectExporter.PlanLevel(asMesh, Level, Document(), [], null, null, meshLoot: GameMeshes());
        Assert.Equal(("Desk", new LootSetting(false, [])), (Assert.Single(request.Loot).Component, request.Loot[0].Loot));
        Assert.Empty(request.Meshes);

        // Without the meshes' loot (a plan made without game files) the component's entries are written as before.
        request = ProjectExporter.PlanLevel(replaced, Level, Document(), []);
        Assert.Equal(new LootSetting(true, [WeaponPreset]), Assert.Single(request.Loot).Loot);
        Assert.Empty(request.Meshes);
    }

    [Fact]
    public void AModelWhoseCopyCannotBeBuiltKeepsItsMeshWithAWarning()
    {
        var state = new EditState();
        state.Apply(new SetLootOp(new ActorRef(Level, "Crate_1"), null, new LootSetting(true, [CratePreset]), new LootSetting(true, [WeaponPreset])));
        var meshes = new MeshLoot(m => m == CrateMesh ? new MeshLootInfo(true, [CratePreset], true) : MeshLootInfo.None, _ => throw new InvalidOperationException("not a static mesh"));
        var warnings = new List<string>();

        var request = ProjectExporter.PlanLevel(state, Level, Document(), warnings, null, null, meshLoot: meshes);

        Assert.Equal(new LootSetting(true, [WeaponPreset]), Assert.Single(request.Loot).Loot);
        Assert.Empty(request.Meshes); // never a level pointing at a copy the export does not have
        Assert.Contains(warnings, w => w.Contains("not a static mesh", StringComparison.Ordinal) && w.Contains("may spawn as well", StringComparison.Ordinal));
        Assert.Empty(meshes.Built);
    }

    [Fact]
    public void AnAddedMeshWithNoLootInsideDrawsTheLootFreeCopy()
    {
        var state = new EditState();
        var add = new AddStaticMeshActorOp(Level, "SM_Crate_Added", CrateMesh, Somewhere) { Loot = [CratePreset] };
        state.Apply(add);
        state.Apply(new SetNoLootOp(add.Created, false, true));

        var request = ProjectExporter.PlanLevel(state, Level, Document(), [], null, null, meshLoot: GameMeshes());

        Assert.Equal(CrateMesh, Assert.Single(request.StaticMeshAdds).StaticMesh); // created as placed (far models, collision checks)
        var loot = Assert.Single(request.Loot);
        Assert.Equal(("SM_Crate_Added", "StaticMeshComponent0", new LootSetting(false, [])), (loot.Actor, loot.Component, loot.Loot));
        var mesh = Assert.Single(request.Meshes);
        Assert.Equal(("SM_Crate_Added", "StaticMeshComponent0", "/Game/X/SM_Crate_NoLoot.SM_Crate_NoLoot"), (mesh.Actor, mesh.Component, mesh.StaticMesh));
    }

    [Fact]
    public void LootFreeCopiesAreNamedAfterTheirMesh()
    {
        Assert.Equal("/Game/X/SM_Crate_NoLoot.SM_Crate_NoLoot", MeshLoot.CopyPath(CrateMesh));
        Assert.Equal("/Game/X/SM_Crate_NoLoot", MeshLoot.CopyPath("/Game/X/SM_Crate"));
        Assert.True(MeshLoot.IsCopy(MeshLoot.CopyPath(CrateMesh)));
        Assert.False(MeshLoot.IsCopy(CrateMesh));
        Assert.Equal(CrateMesh, MeshLoot.StockPath(MeshLoot.CopyPath(CrateMesh)));
        Assert.Equal(CrateMesh, MeshLoot.StockPath(CrateMesh));

        var meshes = GameMeshes();
        var same = meshes.Settle(CrateMesh, [CratePreset]); // exactly the mesh's: nothing on the component, the stock mesh
        Assert.Empty(same.Entries);
        Assert.Null(same.Mesh);
        Assert.Equal("/Game/X/SM_Crate_NoLoot.SM_Crate_NoLoot", meshes.Settle(CrateMesh, [CratePreset, WeaponPreset]).Mesh);
        Assert.Null(meshes.Settle(ShelfMesh, []).Mesh); // a mesh without loot of its own needs no copy
        Assert.Equal(new[] { WeaponPreset }, meshes.Settle(ShelfMesh, [WeaponPreset]).Entries);
        Assert.NotNull(new MeshLoot(_ => new MeshLootInfo(true, [], false)).Settle(CrateMesh, [CratePreset]).Mesh); // loot not fully known: copied
    }

    [Fact]
    public async Task TheCopysLockerIsFoundThroughItsChildActorComponent()
    {
        // Building_1 stores a locker as a child actor (as the police station stores its weapon lockers) and an item spawner.
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_Tile", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var conz = p.PackageImport("/Script/ConZ");
        var examineClass = p.Import("/Script/CoreUObject", "Class", conz, "ExamineAssetData");
        var examineDefault = p.Import("/Script/ConZ", "ExamineAssetData", conz, "Default__ExamineAssetData");
        var preset = p.ObjectImport("/Game/Loot/Examine_Locker", Engine, "BlueprintGeneratedClass", "Examine_Locker_C");
        var lockerMesh = p.ObjectImport("/Game/X/SM_Locker", Engine, "StaticMesh", "SM_Locker");
        var building = p.Export("Building_1", p.ScriptClass(Engine, "Actor"), level);
        var root = p.Export("DefaultSceneRoot", p.ScriptClass(Engine, "SceneComponent"), building);
        var slot = p.Export("LockerSlot", p.ScriptClass(Engine, "ChildActorComponent"), building);
        var spawner = p.Export("ItemSpawner", p.ScriptClass("/Script/SCUM", "ItemSpawnerComponent"), building);
        var locker = p.Export("Locker_CAT", p.ScriptClass(Engine, "Actor"), level);
        var mesh = p.Export("SM_Locker", p.ScriptClass(Engine, "StaticMeshComponent"), locker);
        var entry = p.Export("ExamineAssetData_0", examineClass, mesh, template: examineDefault);
        p.SetPayload(world, p.Properties(native: w => { w.I32(level); w.I32(0); w.I32(0); }));
        p.SetPayload(level, p.Properties(native: w => CopyExportTests.LevelTail(w, [building, locker])));
        p.SetPayload(building, p.Properties(t => t.Object("RootComponent", root)));
        p.SetPayload(root, p.Properties(t => t.Vector("RelativeLocation", 500, 0, 0)));
        p.SetPayload(slot, p.Properties(t =>
        {
            t.Object("AttachParent", root);
            t.Object("ChildActor", locker);
        }));
        p.SetPayload(spawner, p.Properties(t =>
        {
            t.Object("AttachParent", root);
            t.StructArray("SpawnerMarkers", "ItemSpawnerMarker", m => m.Float("Probability", 30), m => m.Float("Probability", 40));
        }));
        p.SetPayload(locker, p.Properties(t =>
        {
            t.Object("RootComponent", mesh);
            t.Object("ParentComponent", slot);
        }));
        p.SetPayload(mesh, p.Properties(t =>
        {
            t.Object("StaticMesh", lockerMesh);
            t.ObjectArray("AssetUserData", entry);
            t.Object("AttachParent", slot);
        }, w => w.I32(0)));
        p.SetPayload(entry, p.Properties(t => t.TaggedStruct("SpawnerPreset", "ItemSpawnerPresetWithOverrides", s => s.Object("Preset", preset))));
        var built = p.Build();
        var package = CookedPackage.Parse(built.UAsset, built.UExp);

        // The copy's child actor takes a name of its own; the patches name it by the slot that spawns it.
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Copies = [new ActorCopy("Building_1", "Building_2", null)],
            Loot = [new LootPatch("Building_2", "LockerSlot/SM_Locker", new LootSetting(false, []))],
            Meshes = [new MeshPatch("Building_2", "LockerSlot/SM_Locker", "/Game/X/SM_Locker_NoLoot.SM_Locker_NoLoot") { IsLootFreeCopy = true }],
            SpawnPoints = [new SpawnPointsPatch("Building_2", "ItemSpawner", SpawnPointArrays.SpawnerMarkers, [])],
        });

        Assert.Empty(report.Warnings);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var copy = CopyExportTests.Child(edited, level, "Building_2");
        var copySlot = CopyExportTests.Child(edited, copy + 1, "LockerSlot");
        var copyLocker = ((ObjectValue)edited.ReadProperties(copySlot).Find("ChildActor")!.Value).Index;
        Assert.True(copyLocker > package.Exports.Count, "the copy's own locker");
        var copyMesh = CopyExportTests.Child(edited, copyLocker, "SM_Locker");
        var copied = edited.ReadProperties(copyMesh);
        Assert.Empty(((ArrayValue)copied.Find("AssetUserData")!.Value).Items);
        Assert.Equal("/Game/X/SM_Locker_NoLoot.SM_Locker_NoLoot", edited.GetFullPath(((ObjectValue)copied.Find("StaticMesh")!.Value).Index));
        var copySpawner = CopyExportTests.Child(edited, copy + 1, "ItemSpawner");
        Assert.Empty(((ArrayValue)edited.ReadProperties(copySpawner).Find("SpawnerMarkers")!.Value).Items);

        // The building it was copied from keeps its locker's loot and its loot points.
        var original = edited.ReadProperties(mesh - 1);
        Assert.Single(((ArrayValue)original.Find("AssetUserData")!.Value).Items);
        Assert.Equal("/Game/X/SM_Locker.SM_Locker", edited.GetFullPath(((ObjectValue)original.Find("StaticMesh")!.Value).Index));
        Assert.Equal(2, ((ArrayValue)edited.ReadProperties(spawner - 1).Find("SpawnerMarkers")!.Value).Items.Count);

        // The level loads in CUE4Parse as the game's reader would take it.
        using var temp = new LevelTempDirectory();
        var basePath = temp.Combine("SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "A_0_Tile");
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        await bytes.WriteAsync(basePath, ".umap");
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), "/Game/ConZ_Files/Maps/The_Island/A_0_Tile");
        Assert.NotNull(document.FindActor("Building_2"));
    }
}
