using System.Diagnostics;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "with about 980 edits the app freezes for seconds when I move objects". A project with a thousand edits in four
/// real sublevels: one move, a move of the whole set and an undo must stay quick. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class ManyEditsRealTests
{
    private static readonly string[] Levels =
    [
        "/Game/ConZ_Files/Maps/The_Island/A_4_Airfield",
        "/Game/ConZ_Files/Maps/The_Island/Landscape_A_4_1c",
        "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01",
        "/Game/ConZ_Files/Maps/The_Island/C_3_Church",
    ];

    private readonly ITestOutputHelper _output;

    public ManyEditsRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AThousandEditsKeepMovingQuick()
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
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Many");
        await map.LoadLevelsAsync(Levels);

        // Up to 500 moved and 500 copied objects (journaled straight into the project, then one refresh).
        var movable = map.AllActors.Where(a => a.Actor.Kind is ActorKind.StaticMeshActor or ActorKind.Blueprint && a.Actor.Root is not null).Take(500).ToList();
        Assert.True(movable.Count > 300, movable.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var item in movable)
        {
            var at = item.Actor.Root!.Relative;
            project.Apply(EditOpFactory.SetTransform(item.Level, item.Actor, at with { Location = at.Location + new FVector(100f, 0f, 0f) }, project.State));
            project.Apply(EditOpFactory.Duplicate(item.Level, item.Actor, null, project.State));
        }

        ctx.Services.Projects.Refresh();
        var edits = project.History.Count;
        Assert.Equal(movable.Count * 2, edits);
        Assert.Equal(movable.Count, map.Clones.Count);

        // One object moved.
        var one = movable[0];
        var time = Stopwatch.StartNew();
        map.SelectedActor = one;
        map.ApplyDraggedTransform(one.SelectableId, map.SelectedRootWorld!.Value with { Translation = map.SelectedRootWorld.Value.Translation + new FVector(0f, 200f, 0f) });
        var single = time.ElapsedMilliseconds;

        // The whole set (moved originals and their copies) dragged by one of them.
        foreach (var item in map.AllActors.Where(a => a.IsAdded || movable.Contains(a)).Skip(1))
        {
            map.ToggleGroup(item.SelectableId, null);
        }

        Assert.True(map.HasGroup);
        time.Restart();
        map.ApplyDraggedTransform(one.SelectableId, map.SelectedRootWorld!.Value with { Translation = map.SelectedRootWorld.Value.Translation + new FVector(0f, 200f, 0f) });
        var group = time.ElapsedMilliseconds;
        Assert.Equal(edits + 2, project.History.Count);

        time.Restart();
        ctx.Services.Projects.Undo();
        var undo = time.ElapsedMilliseconds;

        _output.WriteLine($"{map.AllActors.Count} actors, {project.History.Count} edits: one move {single} ms, set of {map.KindSelectionIds.Count} moved {group} ms, undo {undo} ms");
        Assert.InRange(single, 0, 400);
        Assert.InRange(group, 0, 2000);
        Assert.InRange(undo, 0, 400);
    }
}
