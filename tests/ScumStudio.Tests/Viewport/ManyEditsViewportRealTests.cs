using System.Diagnostics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner: "with about 980 edits the app freezes for seconds when I move objects". After every edit the viewport draws
/// each moved actor at its place and each copy where it is: with a thousand of them in real sublevels that must stay
/// quick. Real game files (<c>SCUM_PAKS</c>) and OpenGL.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class ManyEditsViewportRealTests
{
    private static readonly string[] Levels =
    [
        "/Game/ConZ_Files/Maps/The_Island/A_4_Airfield",
        "/Game/ConZ_Files/Maps/The_Island/Landscape_A_4_1c",
        "/Game/ConZ_Files/Maps/The_Island/Landscape_A_4_1b",
        "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01",
        "/Game/ConZ_Files/Maps/The_Island/C_3_Church",
    ];

    private readonly ITestOutputHelper _output;

    public ManyEditsViewportRealTests(ITestOutputHelper output) => _output = output;

    [GlFact]
    public void AThousandMovedAndCopiedActorsRedrawQuick()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var reader = new Cue4ParseLevelReader(catalog);
        var prepared = new LevelScenePreparer(catalog).Prepare(Levels.Select(l => LevelDocument.Load(reader, l)).ToList(), new LevelSceneOptions { TextureSize = 64 });
        using var harness = GlHarness.Create(64, 64);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 1_000_000f);
        var nodes = level.Scene.Nodes.Count();
        var actors = level.PlacementsById.Where(p => p.Key != 0 && p.Value.Count <= 20).Select(p => p.Key).Take(1000).ToList();
        var away = new FVector(0f, 500f, 0f);
        FTransform At(uint id, int step) => level.PlacementsById[id][0].Actor.WorldTransform is var w ? w with { Translation = w.Translation + (away * step) } : default;
        harness.Renderer.Render(harness.Target, level.Scene, camera);

        // Every moved actor is put where it is after each edit.
        var time = Stopwatch.StartNew();
        foreach (var id in actors)
        {
            level.SetActorTransform(id, At(id, 1));
        }

        var moved = time.ElapsedMilliseconds;

        // A copy of each: added, moved together (a drag of the whole set), then removed (undo).
        const uint copies = 3_000_000;
        time.Restart();
        for (var i = 0; i < actors.Count; i++)
        {
            level.AddClone(copies + (uint)i, actors[i], At(actors[i], 2), "Copy");
        }

        var added = time.ElapsedMilliseconds;
        time.Restart();
        for (var i = 0; i < actors.Count; i++)
        {
            level.AddClone(copies + (uint)i, actors[i], At(actors[i], 3), "Copy");
        }

        var again = time.ElapsedMilliseconds;
        time.Restart();
        harness.Renderer.Render(harness.Target, level.Scene, camera);
        var frame = time.ElapsedMilliseconds;
        Assert.Equal(At(actors[0], 3).Translation.Y, level.Find(copies)[0].World.Translation.Y, 1f);

        time.Restart();
        for (var i = 0; i < actors.Count; i++)
        {
            level.RemoveClone(copies + (uint)i);
        }

        var removed = time.ElapsedMilliseconds;
        Assert.Empty(level.CloneIds);
        Assert.Equal(nodes, level.Scene.Nodes.Count());

        _output.WriteLine($"{nodes:N0} nodes, {actors.Count} actors: moved {moved} ms, copies added {added} ms, moved again {again} ms, frame {frame} ms, removed {removed} ms");
        Assert.InRange(moved, 0, 300);
        Assert.InRange(added, 0, 500);
        Assert.InRange(again, 0, 300);
        Assert.InRange(removed, 0, 300);
    }
}
