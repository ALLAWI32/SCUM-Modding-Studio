using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner (2026-10-26): the game's encounters never spawned armed NPCs on the server, a spawned guard Blueprint works, so a set
/// of guard posts becomes a server Lua mod. The set is a journal edit (undo, saved with the project) and its Lua carries
/// every post and the wake-up distance the set implies.
/// </summary>
public sealed class GuardSpawnsTests
{
    private static GuardSpawnSet Dungeon(params GuardPost[] posts) => new("Dungeon", posts.Length == 0
        ? [new GuardPost(new FVector(0f, 0f, 100f), 3), new GuardPost(new FVector(1000f, 0f, 100f), 5)]
        : posts);

    [Fact]
    public void ASetIsAJournalEditThatUndoesAndReplays()
    {
        var made = new SetGuardSpawnsOp("Dungeon", null, Dungeon());
        var state = new EditState();

        state.Apply(made);

        Assert.Equal(Dungeon(), state.GetGuardSet("dungeon")); // names are not case sensitive
        Assert.False(state.IsEmpty);
        var more = Dungeon(new GuardPost(new FVector(5f, 5f, 5f), 4));
        state.Apply(new SetGuardSpawnsOp("Dungeon", Dungeon(), more));
        Assert.Equal(more, state.GetGuardSet("Dungeon"));

        state.Apply(new SetGuardSpawnsOp("Dungeon", Dungeon(), more).Inverse());
        Assert.Equal(Dungeon(), state.GetGuardSet("Dungeon"));
        state.Apply(made.Inverse());
        Assert.True(state.IsEmpty);

        Assert.Equal(Dungeon(), EditState.Replay([made]).GuardSets["Dungeon"]);
        Assert.Empty(made.GetTouchedLevels()); // not in a pak: the export has nothing of it
    }

    [Fact]
    public void ABadSetOrAStaleEditIsRefused()
    {
        var state = EditState.Replay([new SetGuardSpawnsOp("Dungeon", null, Dungeon())]);

        Assert.Contains("out of date", state.Validate(new SetGuardSpawnsOp("Dungeon", null, Dungeon())), StringComparison.Ordinal); // it exists already
        Assert.NotNull(state.Validate(new SetGuardSpawnsOp("Other", Dungeon(), null))); // nothing to remove
        Assert.NotNull(state.Validate(new SetGuardSpawnsOp("bad name!", null, Dungeon() with { Name = "bad name!" })));
        Assert.NotNull(state.Validate(new SetGuardSpawnsOp("Other", null, Dungeon()))); // the set carries another name
        Assert.NotNull(state.Validate(new SetGuardSpawnsOp("Other", null, new GuardSpawnSet("Other", []))));
        Assert.NotNull(state.Validate(new SetGuardSpawnsOp("Other", null, new GuardSpawnSet("Other", [new GuardPost(FVector.Zero, 2)]))));
        Assert.NotNull(state.Validate(new SetGuardSpawnsOp("Other", null, new GuardSpawnSet("Other", [new GuardPost(FVector.Zero, 6)]))));
        Assert.NotNull(state.Validate(new SetGuardSpawnsOp("Other", null, new GuardSpawnSet("Other", [new GuardPost(new FVector(float.NaN, 0f, 0f), 3)]))));
        Assert.NotNull(state.Validate(new SetGuardSpawnsOp("Other", null, new GuardSpawnSet("Other", Enumerable.Repeat(new GuardPost(FVector.Zero, 3), GuardSpawns.MaxPosts + 1).ToList()))));
        Assert.NotNull(state.Validate(new SetGuardSpawnsOp("Other", null, new GuardSpawnSet("Other", [new GuardPost(FVector.Zero, 3)], IdleSeconds: 1))));
        Assert.Null(state.Validate(new SetGuardSpawnsOp("Other", null, new GuardSpawnSet("Other", [new GuardPost(FVector.Zero, 3)]))));
    }

    [Fact]
    public void TheWakeDistanceIsTheFarthestPostPlusTheReach()
    {
        var set = new GuardSpawnSet("A", [new GuardPost(new FVector(0f, 0f, 0f), 3), new GuardPost(new FVector(600f, 800f, 50f), 3)], Reach: 1000f);

        Assert.Equal((300f, 400f), set.Center);
        Assert.Equal(1500f, set.WakeRadius, 0.5f); // 500 from the middle to either post, and the reach
    }

    [Fact]
    public void TheLuaCarriesEveryPostAndTheSettings()
    {
        var set = new GuardSpawnSet("Dungeon", [new GuardPost(new FVector(456300.5f, -234600f, 8301f), 4), new GuardPost(new FVector(456400f, -234500f, 8301.25f), 5)], 2000f, 120, 600);
        var lua = GuardSpawns.Generate([set, new GuardSpawnSet("Base-2", [new GuardPost(new FVector(10f, 20f, 30f), 3)])]);

        Assert.Contains("name = \"Dungeon\", cx = 456350.25, cy = -234550, radius = ", lua, StringComparison.Ordinal);
        Assert.Contains("idle = 120, respawn = 600, posts = {", lua, StringComparison.Ordinal);
        Assert.Contains("{ 456300.5, -234600, 8301, 4 },", lua, StringComparison.Ordinal);
        Assert.Contains("{ 456400, -234500, 8301.25, 5 },", lua, StringComparison.Ordinal);
        Assert.Contains("name = \"Base-2\"", lua, StringComparison.Ordinal);
        Assert.Contains("idle = 90, respawn = 300", lua, StringComparison.Ordinal); // the defaults
        Assert.True(lua.IndexOf("\"Base-2\"", StringComparison.Ordinal) < lua.IndexOf("\"Dungeon\"", StringComparison.Ordinal), "sets in name order");
        Assert.DoesNotContain("--[[SETS]]", lua, StringComparison.Ordinal);
        Assert.Contains("SpawnActor", lua, StringComparison.Ordinal);
        Assert.Contains("LoadAsset(GUARD_FOLDER .. name .. \".\" .. name)", lua, StringComparison.Ordinal); // the full object path loads the class
    }

    [Fact]
    public void NamesAreChecked()
    {
        Assert.True(GuardSpawns.IsValidName("Dungeon-2_a"));
        Assert.False(GuardSpawns.IsValidName("has space"));
        Assert.False(GuardSpawns.IsValidName("\"; os.execute(\"x\")")); // nothing a Lua file could be broken with
        Assert.False(GuardSpawns.IsValidName(new string('a', 25)));
        Assert.Equal("Guards", GuardSpawns.NewName([]));
        Assert.Equal("Guards3", GuardSpawns.NewName(["Guards", "guards2"]));
    }
}
