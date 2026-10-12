using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using ScumStudio.App.Controls;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;
using static ScumStudio.Tests.Viewport.StreamingCacheTests;

namespace ScumStudio.Tests.App;

/// <summary>
/// F010: while a new scene is staged the view still draws and picks the old one, but the page already reads ids in the new
/// scene's numbering. An id from the old scene reaches the page only while it names the same actor there; otherwise a
/// click, Delete or drag edited an unrelated stock actor. Headless (no GL): the pick itself needs GL, its delivery is the seam.
/// </summary>
public sealed class ViewportSceneSwitchTests
{
    private static readonly FTransform Start = new(new FRotator(0, 30, 0), new FVector(0, 0, 0), new FVector(1, 1, 1));

    /// <summary>An actor of the level in slot 0 (export 4).</summary>
    private const uint ActorInSlot0 = (0u << 20) | 5u;

    [AvaloniaFact]
    public void ADragThatEndsInAnotherSceneDoesNotMoveAnUnrelatedActor()
    {
        var (a, b, d) = (NamedLevel("A"), NamedLevel("B"), NamedLevel("D"));

        // D took A's slot 0 while the arrow was held: the id now names one of D's actors.
        Assert.Empty(DragAcross(SceneOf([a], 0), SceneOf([d], 0)));

        // A kept its slot: the drag lands.
        var kept = Assert.Single(DragAcross(SceneOf([a], 0), SceneOf([b, a], 1, 0)));
        Assert.Equal(ActorInSlot0, kept.Id);
    }

    [AvaloniaFact]
    public void APickFromTheDrawnSceneIsDroppedWhenItsLevelLeft()
    {
        var (a, b, d) = (NamedLevel("A"), NamedLevel("B"), NamedLevel("D"));
        var drawn = SceneOf([a], 0);
        var viewport = new LevelViewport { Scene = SceneOf([d], 0), SelectedId = 9 };
        var (picked, toggled) = (new List<uint>(), new List<uint>());
        viewport.ActorPicked += (_, id) => picked.Add(id);
        viewport.PickToggled += (_, pick) => toggled.Add(pick.Id);

        viewport.DeliverPick(drawn, ActorInSlot0, null, toggle: false);
        viewport.DeliverPick(drawn, ActorInSlot0, null, toggle: true);
        Assert.Empty(picked);
        Assert.Empty(toggled);
        Assert.Equal(9u, viewport.SelectedId);

        // A level both scenes have: its picks go through, and a click on nothing still clears the selection.
        viewport.Scene = SceneOf([b, a], 1, 0);
        viewport.DeliverPick(drawn, ActorInSlot0, null, toggle: false);
        viewport.DeliverPick(drawn, ActorInSlot0, null, toggle: true);
        Assert.Equal([ActorInSlot0], picked);
        Assert.Equal([ActorInSlot0], toggled);
        Assert.Equal(ActorInSlot0, viewport.SelectedId);
        viewport.DeliverPick(drawn, 0, null, toggle: false);
        Assert.Equal(0u, viewport.SelectedId);
    }

    /// <summary>Selects the actor in slot 0 of <paramref name="before"/>, drags its X arrow, shows <paramref name="after"/>, releases.</summary>
    private static List<TransformDragEventArgs> DragAcross(PreparedLevelScene before, PreparedLevelScene after)
    {
        var viewport = new LevelViewport { Scene = before, SelectedId = ActorInSlot0, SelectedRootWorld = Start };
        var window = new Window { Width = 900, Height = 700, Content = viewport };
        window.Show();
        HeadlessUi.Pump();
        viewport.Camera.Position = new Vector3(600f, 500f, 900f);
        viewport.Camera.Yaw = -124f;
        viewport.Camera.Pitch = -25f;
        var drags = new List<TransformDragEventArgs>();
        viewport.TransformDragged += (_, e) => drags.Add(e);

        var at = viewport.HandleScreenPoint(GizmoAxis.X)!.Value;
        var centre = viewport.HandleScreenPoint(GizmoAxis.ScaleUniform)!.Value;
        var along = at - centre;
        var length = Math.Sqrt((along.X * along.X) + (along.Y * along.Y));
        var to = at + new Avalonia.Vector(along.X / length * 40, along.Y / length * 40);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(to);
        Assert.NotNull(viewport.DragPreviewRoot);
        viewport.Scene = after;
        window.MouseUp(to, MouseButton.Left);
        window.Close();
        return drags;
    }
}
