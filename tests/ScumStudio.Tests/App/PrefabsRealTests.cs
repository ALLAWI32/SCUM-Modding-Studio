using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "select a whole building or a group, Save, name it; Place puts it where the camera aims; Export gives a
/// readable file; Import loads one; History right-click Go to flies to the edit's object". A farm building with two
/// props on the real game files (<c>SCUM_PAKS</c>), through the view model and the library service, no dialogs.
/// </summary>
public sealed class PrefabsRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";

    private readonly ITestOutputHelper _output;

    public PrefabsRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ABuildingWithPropsIsSavedPlacedUndoneSharedAndFoundAgain()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Prefabs");
        await map.LoadLevelsAsync([Farm]);
        var project = ctx.Services.Projects.Current!;
        var catalog = ctx.Services.Workspace.Catalog!;

        // A building and the two props nearest to it, as a multi-selection.
        var house = map.AllActors.First(a => a.Actor.Kind == ActorKind.Blueprint && a.Name.Contains("House", StringComparison.OrdinalIgnoreCase));
        var props = map.AllActors
            .Where(a => a.Actor.Kind == ActorKind.StaticMeshActor && a.Actor.StaticMeshPath is not null && !a.IsAdded)
            .OrderBy(a => (a.Actor.WorldTransform.Translation - house.Actor.WorldTransform.Translation).Size())
            .Take(2).ToList();
        Assert.False(map.SavePrefabCommand.CanExecute(null));
        map.SelectedActor = house;
        map.ToggleGroup(props[0].SelectableId, null);
        map.ToggleGroup(props[1].SelectableId, null);
        Assert.True(map.HasGroup);
        Assert.True(map.SavePrefabCommand.CanExecute(null));
        map.PrefabName = "Farmhouse";
        map.SavePrefabCommand.Execute(null);

        // The file: plain JSON naming the building, the props' meshes and their places relative to the centre on the ground.
        var entry = Assert.Single(ctx.Services.Prefabs.List());
        Assert.Equal("Farmhouse", entry.Name);
        var json = File.ReadAllText(entry.Path);
        _output.WriteLine(json);
        Assert.Contains("\"kind\": \"stockActor\"", json, StringComparison.Ordinal);
        Assert.Contains("\"actor\": \"" + house.Name + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mesh\": \"" + props[0].Actor.StaticMeshPath + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"level\": \"" + Farm + "\"", json, StringComparison.Ordinal);
        Assert.Equal(3, entry.Prefab.Parts.Count);
        var members = new[] { house, props[0], props[1] };
        var worlds = members.Select(a => a.Actor.WorldTransform.Translation).ToList();
        var pivot = entry.Prefab.Pivot;
        Assert.InRange(pivot.X, worlds.Average(w => w.X) - 1f, worlds.Average(w => w.X) + 1f);
        Assert.InRange(pivot.Y, worlds.Average(w => w.Y) - 1f, worlds.Average(w => w.Y) + 1f);
        Assert.InRange(pivot.Z, worlds.Min(w => w.Z) - 1f, worlds.Min(w => w.Z) + 1f);
        for (var i = 0; i < 3; i++)
        {
            Assert.True((entry.Prefab.Parts[i].Transform.Location + pivot - worlds[i]).Size() < 1f, $"part {i}: {entry.Prefab.Parts[i].Transform} vs {worlds[i]}");
        }

        // Placed 60 m away: one history row, three new objects in the saved layout, the copies selected.
        var row = Assert.Single(map.Prefabs);
        Assert.Equal("Farmhouse", row.Name);
        var aim = house.Actor.WorldTransform.Translation + new FVector(6000f, 3000f, 0f);
        map.AimPointProvider = () => aim;
        row.Place.Execute(null);
        var placed = Assert.Single(ctx.Services.Projects.History);
        Assert.Contains("Place prefab Farmhouse", placed.Summary, StringComparison.Ordinal);
        var batch = Assert.IsType<BatchOp>(placed.Item.Op);
        Assert.Equal(3, batch.Ops.Count);
        Assert.IsType<DuplicateActorOp>(batch.Ops[0]); // the building stays a copy of the stored one, with its components
        var added = map.AllActors.Where(a => a.IsAdded).ToList();
        Assert.Equal(3, added.Count);
        foreach (var part in entry.Prefab.Parts)
        {
            var expected = part.Transform.Location + aim;
            Assert.Contains(added, a => (a.Actor.WorldTransform.Translation - expected).Size() < 1f);
        }

        Assert.True(map.HasGroup);
        Assert.Equal(3, map.GroupWorlds.Count);
        Assert.True(map.SelectedActor?.IsAdded);

        // History: Go to the placement selects its first object and asks the view to frame it.
        var framed = 0;
        map.FrameSelectionRequested += (_, _) => framed++;
        map.SelectedActor = null;
        Assert.True(await map.ShowEditAsync(placed, frame: true));
        Assert.True(map.SelectedActor?.IsAdded);
        Assert.Equal(batch.Ops[0].GetPrimaryTarget()!.Actor, map.SelectedActor!.Name);
        Assert.Equal(1, framed);

        // Undo takes them all out; the row then leads nowhere.
        map.UndoCommand.Execute(null);
        Assert.DoesNotContain(map.AllActors, a => a.IsAdded);
        Assert.Empty(project.State.AddedActors);
        Assert.False(await map.ShowEditAsync(ctx.Services.Projects.History[0], frame: true));
        Assert.Equal(1, framed);

        // Shared: Export is the same file; Import brings it back under a free name and the Map's list follows.
        var shared = ctx.Combine("shared", "Farmhouse.ssprefab");
        ctx.Services.Prefabs.Export(entry, shared);
        Assert.Equal(json, File.ReadAllText(shared));
        var imported = ctx.Services.Prefabs.Import(shared, p => PrefabLibrary.Knows(catalog, p));
        Assert.Equal("Farmhouse 2", imported.Name);
        Assert.Equal(2, map.Prefabs.Count);

        // A file that is not a prefab, and one whose objects are not in this game, are refused.
        var text = ctx.Combine("shared", "notes.ssprefab");
        File.WriteAllText(text, "hello");
        Assert.Throws<InvalidDataException>(() => ctx.Services.Prefabs.Import(text));
        var alien = ctx.Combine("shared", "Alien.ssprefab");
        (entry.Prefab with { Parts = [new PrefabPart { Kind = PrefabPartKind.StaticMesh, Mesh = "/Game/Nope/SM_Nope.SM_Nope" }] }).Save(alien);
        Assert.Throws<InvalidDataException>(() => ctx.Services.Prefabs.Import(alien, p => PrefabLibrary.Knows(catalog, p)));
        Assert.Null(ctx.Services.Prefabs.ImportFile(alien, catalog, ctx.Services.Notifications));
        Assert.Equal(2, map.Prefabs.Count);

        // A prefab from another game version: the part the game no longer has is skipped and said so.
        var mixed = ctx.Combine("shared", "Mixed.ssprefab");
        (entry.Prefab with { Parts = [entry.Prefab.Parts[1], new PrefabPart { Kind = PrefabPartKind.StaticMesh, Mesh = "/Game/Nope/SM_Gone.SM_Gone" }] }).Save(mixed);
        ctx.Services.Prefabs.Import(mixed, p => PrefabLibrary.Knows(catalog, p));
        ctx.Services.Notifications.DismissAll();
        map.Prefabs.Single(r => r.Name == "Mixed").Place.Execute(null);
        Assert.Single(map.AllActors, a => a.IsAdded);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Message.Contains("SM_Gone", StringComparison.Ordinal));
        map.UndoCommand.Execute(null);

        // A moved tree: Select picks that one instance; a ground look has no place.
        var tile = map.AllActors.First(a => a.Actor.InstanceTransforms.Count > 0 && !a.IsAdded);
        var tree = tile.Actor.InstanceTransforms[0];
        var lifted = TransformValue.FromTransform(tree.LocalTransform);
        ctx.Services.Projects.Apply(EditOpFactory.SetInstanceTransform(tile.Level, tile.Actor, tree.ComponentName, tree.InstanceIndex, lifted with { Location = lifted.Location + new FVector(0f, 0f, 100f) }, project.State));
        Assert.True(await map.ShowEditAsync(ctx.Services.Projects.History[0], frame: false));
        Assert.Equal(tile, map.SelectedActor);
        Assert.Equal(InstanceKey.Of(tile.SelectableId, tree.ComponentName, tree.InstanceIndex), map.SelectedInstanceKey);
        Assert.Equal(1, framed);
        map.GroundLookOptions.Single(o => o.Look == GroundLook.Snow).Choose.Execute(null);
        Assert.False(await map.ShowEditAsync(ctx.Services.Projects.History[0], frame: true));
        Assert.Equal(1, framed);
    }
}
