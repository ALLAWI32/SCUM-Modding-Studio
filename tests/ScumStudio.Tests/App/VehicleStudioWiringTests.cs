using System.Numerics;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Mathematics;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Vehicles studio's 3D view wired to its tabs (owner: "decals I click on the car, parts I drag like Blender"): a click
/// on the car places the armed decal once, a drag moves it as one step, Escape puts it back; the Parts tab turns the
/// gizmo's drag into the socket change of the part; "Add part…" gives the view a surface click.
/// </summary>
public sealed class VehicleStudioWiringTests
{
    [Fact]
    public async Task AClickPlacesTheArmedDecalOnceAndADragIsOneStep()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        page.StudioTab = StudioTab.Decals;
        Assert.Equal(PreviewTool.Surface, page.StudioTool);

        void Surface(float x, bool drag = false, bool release = false, bool cancel = false)
        {
            var ray = DecalsViewModelTests.Ray(x);
            page.OnStudioSurface(ray.PartIndex, ray.Origin, ray.Direction, drag, release, cancel);
        }

        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "flames-1"));
        Surface(0);
        Surface(0, release: true);
        Assert.Single(page.Decals.Layers);
        Assert.Null(page.ArmedDecal); // one click, one decal

        // A second click picks the decal under the cursor; its drag is one undo step.
        Surface(0);
        Surface(10, drag: true);
        Surface(20, drag: true);
        Surface(20, release: true);
        Assert.Single(page.Decals.Layers);
        Assert.Equal(20f, page.Decals.Layers.Single().Position.X, 2);
        Assert.False(page.DecalsDragging);
        page.UndoDecalCommand.Execute(null);
        Assert.Equal(0f, page.Decals.Layers.Single().Position.X, 2);

        // Escape during a drag puts the decal back and records nothing.
        Surface(0);
        Surface(15, drag: true);
        Surface(15, cancel: true);
        Assert.Equal(0f, page.Decals.Layers.Single().Position.X, 2);
        Assert.False(page.DecalsDragging);
        page.UndoDecalCommand.Execute(null);
        Assert.Empty(page.Decals.Layers); // the next step back is the placement

        // Another tab ignores the clicks.
        page.StudioTab = StudioTab.Materials;
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "flames-1"));
        Surface(0);
        Assert.Empty(page.Decals.Layers);
    }

    [Fact]
    public void ThePartsTabGivesTheGizmoItsModeAndAddPartASurfaceClick()
    {
        using var ctx = AppTestContext.Create();
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.StudioTab = StudioTab.Parts;
        Assert.Equal(PreviewTool.Gizmo, page.StudioTool);
        page.SetPartToolCommand.Execute(PartTool.Rotate);
        Assert.Equal(PreviewGizmoMode.Rotate, page.StudioGizmoMode);
        page.SetPartToolCommand.Execute(PartTool.Scale);
        Assert.Equal(PreviewGizmoMode.Scale, page.StudioGizmoMode);

        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        page.IsAddingPart = true;
        Assert.Equal(PreviewTool.Surface, page.StudioTool);
        Assert.Contains(nameof(ModulePageViewModel.StudioTool), changed);
        page.IsAddingPart = false;
        Assert.Equal(PreviewTool.Gizmo, page.StudioTool);
        Assert.Empty(page.StudioPartOverrides);
    }

    [Fact]
    public void AGizmoDragBecomesTheSocketChangeOfThePart()
    {
        var start = new FTransform(new FRotator(0, 10, 0), new FVector(100, 50, 30), FVector.One);
        var now = new FTransform(new FRotator(0, 40, 0), new FVector(110, 50, 35), new FVector(2, 2, 2));

        var delta = ModulePageViewModel.GizmoDelta(start, now);

        Assert.Equal(10f, delta.Translation.X, 3);
        Assert.Equal(0f, delta.Translation.Y, 3);
        Assert.Equal(5f, delta.Translation.Z, 3);
        Assert.Equal(30f, delta.Rotator().Yaw, 2);
        Assert.Equal(2f, delta.Scale3D.X, 3);
        var turned = (delta.Rotation * start.Rotation).Rotator();
        Assert.Equal(40f, turned.Yaw, 2); // delta after start gives where the gizmo left it

        // The socket the part hangs on takes the same change (PartsStudio.Apply).
        var socket = new VehicleSocket("s_Door", "Root", new Vector3(87.7f, -86.9f, 49.7f), new Vector3(0, 10, 0), Vector3.One, false);
        var (location, rotation, scale) = PartsStudio.Apply(socket, delta);
        Assert.Equal(97.7f, location.X, 3);
        Assert.Equal(-86.9f, location.Y, 3);
        Assert.Equal(54.7f, location.Z, 3);
        Assert.Equal(40f, rotation.Y, 2);
        Assert.Equal(2f, scale.Z, 3);

        // A cancelled drag (back at its start) changes nothing.
        var none = ModulePageViewModel.GizmoDelta(start, start);
        Assert.Equal(FVector.Zero, none.Translation);
        Assert.Equal(0f, none.Rotator().Yaw, 3);
        Assert.Equal(FVector.One, none.Scale3D);
    }
}
