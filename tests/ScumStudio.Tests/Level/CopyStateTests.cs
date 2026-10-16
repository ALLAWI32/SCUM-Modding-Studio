using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using static ScumStudio.Tests.Level.EditOpTests;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>What a copy carries along: the create op's own data and every per-actor edit of its source.</summary>
public sealed class CopyStateTests
{
    private const string Painted = "/Game/ConZ_Files/Materials/MI_Painted.MI_Painted";

    [Fact]
    public void AddStaticMeshActorOpEqualsAfterJsonRoundTrip()
    {
        // F215: a remove read back from the journal carries new lists; it must still match the add it undoes.
        var add = new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Somewhere) { Loot = [], OverrideMaterials = [null, Painted], CollisionProfile = "BlockAll" };
        var state = new EditState();
        state.Apply(add);

        var read = (AddStaticMeshActorOp)EditOp.FromJson(add.ToJson());
        Assert.Equal(add, read);
        Assert.Equal(add.GetHashCode(), read.GetHashCode());
        Assert.Equal([null, Painted], read.OverrideMaterials);
        Assert.Null(state.Validate(new RemoveAddedActorOp(add.Created, read)));
        Assert.Null(state.Validate(EditOp.FromJson(add.Inverse().ToJson())));

        // Different materials are a different add; an entry written before materials were carried still loads.
        Assert.NotEqual(add, read with { OverrideMaterials = [Painted, null] });
        Assert.NotEqual(add, read with { OverrideMaterials = null });
        var old = (AddStaticMeshActorOp)EditOp.FromJson(add.ToJson().Replace(",\"overrideMaterials\":[null,\"" + Painted + "\"]", string.Empty, StringComparison.Ordinal));
        Assert.Null(old.OverrideMaterials);
        Assert.Equal(add with { OverrideMaterials = null }, old);
    }

    [Fact]
    public void DuplicateOfDeletedSourceValidates()
    {
        // F203: a copy of a copy is a copy of the original; deleting the original must not stop it.
        var source = new ActorRef(Outpost, "StaticMeshActor_12");
        var state = new EditState();
        state.Apply(new DuplicateActorOp(source, "StaticMeshActor_12_Copy", Somewhere));
        state.Apply(new DeleteActorOp(source));

        var again = new DuplicateActorOp(source, "StaticMeshActor_12_Copy_Copy", Elsewhere);
        Assert.Null(state.Validate(again));
        state.Apply(again);
        Assert.NotNull(state.Validate(again)); // the name is taken now
    }

    [Fact]
    public void ACopyOfACopyIsExportedAfterTheOriginalIsDeleted()
    {
        // F203 (review): both copies are made from the original's stored exports before those become inert objects; no copy
        // may keep a reference to one of them (a building's stored door comes along as a copy of its own).
        foreach (var (bytes, level, actor) in new[]
                 {
                     (SyntheticLevels.BuildLevel(), SyntheticLevels.LevelPath, "StaticMeshActor_1"),
                     (SyntheticLevels.BuildChildActorLevel("A_0_Building"), "/Game/ConZ_Files/Maps/The_Island/A_0_Building", "Building_1"),
                 })
        {
            var source = new ActorRef(level, actor);
            var state = new EditState();
            state.Apply(new DuplicateActorOp(source, actor + "_Copy", Somewhere));
            state.Apply(new DeleteActorOp(source));
            state.Apply(new DuplicateActorOp(source, actor + "_Copy_Copy", Elsewhere)); // Duplicate of the copy: a copy of the original
            var warnings = new List<string>();
            var request = ProjectExporter.PlanLevel(state, level, null, warnings);
            var package = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
            var (edited, report) = LevelPackageEditor.Apply(package, request);

            Assert.True(warnings.Count + report.Warnings.Count == 0, string.Join(" | ", warnings.Concat(report.Warnings)));
            Assert.Equal([actor], report.RemovedActors);
            Assert.Equal([actor + "_Copy", actor + "_Copy_Copy"], report.AddedActors);
            var after = CookedPackage.Parse(edited.UAsset, edited.UExp);
            var listed = LevelPackageEditor.ReadActorList(after).Select(a => a.Name).ToList();
            Assert.DoesNotContain(actor, listed);
            Assert.Contains(actor + "_Copy", listed);
            Assert.Contains(actor + "_Copy_Copy", listed);

            var inert = Enumerable.Range(0, package.Exports.Count)
                .Where(i => after.GetExportClassName(i) == "Object" && package.GetExportClassName(i) != "Object")
                .Select(i => i + 1)
                .ToHashSet();
            Assert.NotEmpty(inert);
            for (var i = package.Exports.Count; i < after.Exports.Count; i++)
            {
                Assert.DoesNotContain(after.ReadProperties(i).Properties.SelectMany(t => References(t.Value)), inert.Contains);
            }
        }
    }

    [Fact]
    public void ACopySetBackToItsStockLootKeepsThatSetting()
    {
        // F204 (review): a copy with no loot of its own gets its source's (export and panel). A copy set back to its stock
        // loot dropped its setting (it equalled the stock), so its source's came back.
        var source = new ActorRef(Outpost, "Locker_2");
        var copy = new ActorRef(Outpost, "Locker_2_Copy");
        var stock = new LootSetting(true, ["/Game/Items/Examine_Locker.Examine_Locker_C"]);
        var none = new LootSetting(false, []);
        var state = new EditState();
        state.Apply(new SetLootOp(source, null, stock, none));
        state.Apply(new BatchOp("Duplicate", [new DuplicateActorOp(source, copy.Actor, Somewhere), .. state.EditsOf(source).OpsOn(copy, false)]));
        Assert.Equal(none, state.GetLoot(copy));
        var before = state.Describe();

        var reset = new SetLootOp(copy, null, none, stock);
        state.Apply(reset);
        Assert.Equal(stock, state.GetLoot(copy));
        Assert.Equal(stock, state.EditsOf(copy).Loot.Single().Current); // and a copy of it carries that
        state.Apply(reset.Inverse());
        Assert.Equal(before, state.Describe());

        // Without a loot setting on the source, back to stock is no setting at all (as for any actor).
        state.Apply(new SetLootOp(source, null, none, stock));
        Assert.Null(state.GetLoot(source));
        state.Apply(reset);
        Assert.Null(state.GetLoot(copy));
    }

    [Fact]
    public void EditsOfReplaysEverySettingOnACopy()
    {
        const string boulder = "/Game/ConZ_Files/Models/Rocks/SM_Boulder.SM_Boulder";
        var source = new ActorRef(Outpost, "BP_Sentry_3");
        var copy = new ActorRef(Outpost, "BP_Sentry_3_Copy");
        var bend = new BendValue(45f, Sway1: 120f, Start: new SplineEnd(new FVector(30f, 0f, 0f), Grow: 0.25f), Legs: 300f);
        var stock = new LootSetting(true, ["/Game/Items/Examine_Locker.Examine_Locker_C"]);
        var patrol = new SpawnPoint[] { new(0, TransformValue.Identity), new(1, Somewhere) };
        var moved = new SpawnPoint[] { new(0, Elsewhere), new(1, Somewhere), new(1, Elsewhere) };
        var fence = new TransformValue(new FVector(10f, 0f, 0f), default, FVector.One);
        var tree = new InstanceRef(Outpost, source.Actor, "Trees", 2);

        var state = new EditState();
        state.Apply(new BatchOp("Edits",
        [
            new BendActorOp(source, 0f, bend.Degrees, NewSway1: bend.Sway1, NewStart: bend.Start, NewLegs: bend.Legs),
            new SetTransformOp(source, fence, fence with { Scale = FVector.Zero }, "Fence"),
            new ReplaceMeshOp(source, null, Rock, boulder),
            new SwaySegmentOp(source, "Road_0", 0f, 0f, 50f, -20f),
            new SetSpawnPointsOp(source, null, "PatrolPoints", patrol, moved),
            new SetLootOp(source, "Locker", stock, new LootSetting(false, [])),
            new SetClearGrassOp(source, null, false),
            new DeleteInstanceOp(tree),
            new SetInstanceTransformOp(tree with { Index = 1 }, Somewhere, Elsewhere),
            new AddInstanceOp(tree with { Index = 9 }, Somewhere),
            new SetInstanceTransformOp(tree with { Index = 9 }, Somewhere, Elsewhere),
        ]));
        var before = state.Describe();

        var edits = state.EditsOf(source);
        Assert.False(edits.IsEmpty);
        Assert.True(state.EditsOf(copy).IsEmpty);
        Assert.Equal([source], state.EditsOf([source, copy]).Keys);

        var batch = new BatchOp("Duplicate", [new DuplicateActorOp(source, copy.Actor, Somewhere), .. edits.OpsOn(copy, rootMeshAndLootBaked: false)]);
        Assert.Null(state.Validate(batch));
        Assert.Equal(batch, EditOp.FromJson(batch.ToJson())); // the journal keeps it exactly
        state.Apply(batch);

        Assert.True(state.GetBendValue(copy).IsNearly(bend));
        Assert.Equal(FVector.Zero, state.GetTransformOverride(copy, "Fence")!.Value.Scale);
        Assert.Equal(boulder, state.GetMeshOverride(copy));
        Assert.True(state.GetSegmentShape(copy, "Road_0").IsNearly(state.GetSegmentShape(source, "Road_0")));
        Assert.Equal(moved, state.GetSpawnPoints(copy, null, "PatrolPoints"));
        Assert.Equal(new LootSetting(false, []), state.GetLoot(copy, "Locker"));
        Assert.False(state.GetClearGrass(copy));
        Assert.True(state.IsDeleted(tree with { Actor = copy.Actor }));
        Assert.Equal(Elsewhere, state.GetInstanceOverride(tree with { Actor = copy.Actor, Index = 1 }));
        Assert.Equal(Elsewhere, state.GetAddedInstanceTransform(tree with { Actor = copy.Actor, Index = 9 }));
        Assert.Equal(edits.OpsOn(new ActorRef(Outpost, "X"), false), state.EditsOf(copy).OpsOn(new ActorRef(Outpost, "X"), false));

        // Undo gives back the state before, and nothing of the copy stays.
        state.Apply(batch.Inverse());
        Assert.Equal(before, state.Describe());
        Assert.Null(state.GetLoot(copy, "Locker"));
        Assert.Null(state.GetClearGrass(copy));

        // A mesh actor copy already carries the root's mesh and loot in its create op, and has no other part to edit.
        var baked = edits.OpsOn(copy, rootMeshAndLootBaked: true);
        Assert.Equal([typeof(BendActorOp), typeof(SetClearGrassOp)], baked.Select(o => o.GetType()));
    }

    private static IEnumerable<int> References(PropertyValue value) => value switch
    {
        ObjectValue o => [o.Index],
        StructValue s => s.Properties.SelectMany(t => References(t.Value)),
        ArrayValue a => a.Items.SelectMany(References),
        _ => [],
    };
}
