using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Tests.Level;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// A copy's own spawn point pins (igor8802: "a duplicated sentry's patrol route cannot be changed"): the pins the project
/// gives a copy replace the ones it took from its source, and they go with the copy when it moves, as its other nodes do.
/// One synthetic sentry, no game files.
/// </summary>
public sealed class ClonePinsTests
{
    private const uint CopyId = 0x00F0_0000;

    [GlFact]
    public void ACopysOwnPinsReplaceItsSourcesAndMoveWithIt()
    {
        using var temp = new LevelTempDirectory();
        SyntheticSpawnerLevel.WriteContent(temp.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), SyntheticSpawnerLevel.LevelPath);
        var prepared = new LevelScenePreparer(catalog).Prepare([document], new LevelSceneOptions { IncludeInstances = false, TextureSize = 64 });
        using var harness = GlHarness.Create(64, 64);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);
        var sentry = document.Actors.Single(a => a.Name == SyntheticSpawnerLevel.Sentry);
        var sentryId = prepared.IdOf(0, sentry);

        // The copy 10 m along X takes the source's three patrol pins; then its own route (first point 50 cm along Y).
        var at = sentry.WorldTransform with { Translation = sentry.WorldTransform.Translation + new FVector(1000f, 0f, 0f) };
        Assert.True(level.AddClone(CopyId, sentryId, at, "Copy") > 0);
        Assert.Equal(3, Pins().Count);
        var route = sentry.PatrolPoints.Select((p, i) => i == 0 ? p + new FVector(0f, 50f, 0f) : p).ToList();
        var own = LevelScenePreparer.PinPlacements(sentry with { WorldTransform = at, PatrolPoints = route }, CopyId, 0).Where(p => p.SpawnPoint is not null).ToList();
        level.ReplacePins(CopyId, own);
        Assert.Equal(3, Pins().Count);
        var first = own.Single(p => p.SpawnPoint!.Value.Index == 0).World.Translation;
        Assert.True(FVector.Distance(PinAt(0), first) < 0.01f);

        // Moved (a drag preview, then the copy re-placed): its own pins go along, and they are the copy's placements.
        var up = new FVector(0f, 0f, 300f);
        level.SetCloneTransform(CopyId, at with { Translation = at.Translation + up });
        Assert.True(FVector.Distance(PinAt(0), first + up) < 0.01f);
        level.AddClone(CopyId, sentryId, at with { Translation = at.Translation + up + up }, "Copy");
        Assert.True(FVector.Distance(PinAt(0), first + up + up) < 0.01f);
        var placed = level.Find(CopyId).Where(p => p.SpawnPoint is not null).ToList();
        Assert.Equal(3, placed.Count);
        Assert.True(FVector.Distance(placed.Single(p => p.SpawnPoint!.Value.Index == 0).World.Translation, first + up + up) < 0.01f);

        List<SceneNode> Pins() => level.Scene.Nodes.Where(n => n.SelectableId == CopyId && n.Tag is ScenePlacement { SpawnPoint: not null }).ToList();

        FVector PinAt(int index) =>
            UeToGl.ToUePoint(Pins().Single(n => ((ScenePlacement)n.Tag!).SpawnPoint!.Value.Index == index).LocalTransform.Translation);
    }
}
