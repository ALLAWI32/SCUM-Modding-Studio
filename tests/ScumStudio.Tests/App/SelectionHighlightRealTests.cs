using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-09): "sometimes a selected object shows no orange, only its numbers". A copy re-added (or a piece re-bent,
/// an instance given its own node) after the viewport lit the selection came back unlit. New nodes count in
/// <see cref="LevelScene.NodesBuilt"/>, and the viewport lights the selection again whenever that count changes.
/// Real game files and OpenGL.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class SelectionHighlightRealTests
{
    [GlFact]
    public void ACopyAddedAfterTheSelectionWasLitIsCountedAndLitAgain()
    {
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return;
        }

        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), SpawnPartsRealTests.Maps + "A_0_Outpost");
        var prepared = new LevelScenePreparer(catalog).Prepare([document], new LevelSceneOptions { TextureSize = 64 });
        using var harness = GlHarness.Create(64, 64);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);
        var source = document.Actors.Select(a => (Actor: a, Id: LevelScenePreparer.SelectableIdOf(0, a)))
            .First(p => p.Actor.Kind == ActorKind.StaticMeshActor && level.Scene.Nodes.Any(n => n.SelectableId == p.Id));
        const uint copy = 0x7000_0001;

        // The copy is selected while its nodes do not exist yet (the viewport lit the selection, then re-added the copies).
        level.SetSelection([copy]);
        var before = level.NodesBuilt;
        Assert.True(level.AddClone(copy, source.Id, source.Actor.WorldTransform, source.Actor.Name + "_Copy") > 0);
        var nodes = level.Scene.Nodes.Where(n => n.SelectableId == copy).ToList();
        Assert.NotEmpty(nodes);
        Assert.True(level.NodesBuilt > before, "new nodes are counted");
        Assert.All(nodes, n => Assert.False(n.Selected));

        // What the viewport does when the count changed: light the selection again.
        level.SetSelection([copy]);
        Assert.All(nodes, n => Assert.True(n.Selected));
    }
}
