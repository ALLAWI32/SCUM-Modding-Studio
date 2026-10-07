using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (FiveM-editor style): "select a road piece, a wall, a tree or a building and replace it with another of the same
/// family, fitted to its length, height and curve". A road piece of the farm's landscape tile and a long piece of the farm
/// are replaced from their family lists, exported, and undone. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class ReplaceRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";

    private readonly ITestOutputHelper _output;

    public ReplaceRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ARoadPieceAndALongPieceAreReplacedFittedExportedAndUndone()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        var catalog = ctx.Services.Workspace.Catalog!;
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Replace");
        await map.LoadLevelsAsync([Farm]);

        // With the landscape tiles around the farm: the roads are spline pieces of a tile's landscape proxy.
        var at = map.AllActors.First(a => a.Actor.Kind == ActorKind.StaticMeshActor).Actor.WorldTransform.Translation;
        var world = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog, null);
        var tiles = MapPageViewModel.LevelsAround(world, at, 4000f).Where(p => p.Contains("/Landscape_", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(tiles);
        await map.LoadLevelsAsync([Farm, .. tiles]);
        var scene = map.PreparedScene!;
        var state = project.State;

        // 1. A road piece: its family is the roads; the piece keeps its curve (no scale), the component draws the other mesh.
        var (roadItem, road) = map.AllActors
            .SelectMany(a => a.Actor.Components.Where(c => c.SplineMesh is not null && !c.IsSynthesized && c.StaticMeshPath is not null).Select(c => (Item: a, Piece: c)))
            .OrderBy(p => p.Piece.StaticMeshPath!.Contains("Road", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First();
        map.SelectedActorId = roadItem.SelectableId;
        map.SelectedInstanceKey = InstanceKey.Of(roadItem.SelectableId, road.Name, InstanceKey.Segment);
        map.RefreshReplaceCandidates();
        _output.WriteLine($"{roadItem.Name}.{road.Name} ({road.StaticMeshPath}): {map.ReplaceCaption}");
        Assert.True(map.ReplacePicker.Items.Count > 1, map.ReplaceCaption);
        Assert.True(map.ReplacePicker.Items[0].IsCurrent);
        Assert.True(EditOpFactory.SameObject(road.StaticMeshPath!, map.ReplacePicker.Items[0].Choice.ObjectPath));
        var otherRoad = map.ReplacePicker.Items[1];
        map.ReplacePicker.Search = otherRoad.Name[^3..];
        Assert.Contains(otherRoad, map.ReplacePicker.Items);
        Assert.Contains(map.ReplacePicker.Items, c => c.IsCurrent); // the current one is always listed
        map.ReplacePicker.Search = string.Empty;
        Assert.False(map.ReplaceCommand.CanExecute(null));
        map.ReplacePicker.Selected = otherRoad;
        Assert.True(map.ReplaceCommand.CanExecute(null));
        map.ReplaceCommand.Execute(null);
        Assert.True(await map.ReplaceCompletion);
        Assert.Single(ctx.Services.Projects.History);
        Assert.Equal(otherRoad.Choice.ObjectPath, state.GetMeshOverride(roadItem.Reference, road.Name));
        Assert.Null(state.GetTransformOverride(roadItem.Reference, road.Name)); // the curve fits the new mesh: nothing to scale
        Assert.Contains(InstanceKey.Of(roadItem.SelectableId, road.Name, InstanceKey.Segment), map.PartMeshes.Keys);

        // 2. A long piece of the farm (a fence, a wall): replaced in place, scaled along its length to the old length.
        var (pieceItem, candidates) = map.AllActors
            .Where(a => a.Actor.Kind == ActorKind.StaticMeshActor && !a.IsAdded && !a.IsDeleted && a.Actor.StaticMeshPath is { } m && a.Actor.Root is { IsSynthesized: false }
                        && scene.Meshes.TryGetValue(m, out var asset) && asset.Mesh.Bounds.Size is { } size && MathF.Max(size.X, size.Y) >= 6f * MathF.Min(size.X, size.Y))
            .Select(a => (Item: a, Family: ReplaceFamilies.Candidates(a.Actor.StaticMeshPath!, AssetDumper.Packages)))
            .First(p => p.Family.Count > 1);
        map.SelectedInstanceKey = null;
        map.SelectedActor = pieceItem;
        map.RefreshReplaceCandidates();
        var oldMesh = pieceItem.Actor.StaticMeshPath!;
        var other = map.ReplacePicker.Items.First(c => !c.IsCurrent);
        _output.WriteLine($"{pieceItem.Name} ({oldMesh}) -> {other.Choice.ObjectPath}: {map.ReplaceCaption}");
        map.ReplacePicker.Selected = other;
        map.ReplaceCommand.Execute(null);
        Assert.True(await map.ReplaceCompletion);
        Assert.Equal(2, ctx.Services.Projects.History.Count);
        Assert.Equal(other.Choice.ObjectPath, state.GetMeshOverride(pieceItem.Reference));
        Assert.Contains(pieceItem.SelectableId, map.ReplacedMeshes.Keys);

        var oldBounds = scene.Meshes[oldMesh].Mesh.Bounds;
        var oldScale = pieceItem.Actor.Root!.Relative.Scale;
        var newBounds = new BendSupport(catalog).Describe(other.Choice.ObjectPath)!.Bounds;
        var expected = ReplaceFamilies.FitScale(oldBounds, oldScale, newBounds, isLong: true);
        var fitted = state.GetTransformOverride(pieceItem.Reference)?.Scale ?? oldScale;
        Assert.True(fitted.Equals(expected, 0.001f), $"{fitted} vs {expected}");
        var alongY = oldBounds.Size.Y * MathF.Abs(oldScale.Y) > oldBounds.Size.X * MathF.Abs(oldScale.X);
        var oldLength = alongY ? oldBounds.Size.Y * MathF.Abs(oldScale.Y) : oldBounds.Size.X * MathF.Abs(oldScale.X);
        var newLength = alongY ? newBounds.Size.Y * MathF.Abs(fitted.Y) : newBounds.Size.X * MathF.Abs(fitted.X);
        _output.WriteLine($"length {oldLength:0} cm -> {newLength:0} cm, scale {oldScale} -> {fitted}");
        Assert.InRange(newLength, oldLength - 1f, oldLength + 1f);

        // 3. The export: each component's StaticMesh import is the new mesh, the piece's scale as fitted.
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        _output.WriteLine(string.Join(Environment.NewLine, result.Warnings));
        var written = Written(result, pieceItem.Level.PackagePath, pieceItem.Name, pieceItem.Actor.Root.Name);
        Assert.Equal(other.Choice.ObjectPath, written.Mesh);
        Assert.True(written.Scale.Equals(fitted, 0.001f), $"{written.Scale} vs {fitted}");
        Assert.Equal(otherRoad.Choice.ObjectPath, Written(result, roadItem.Level.PackagePath, roadItem.Name, road.Name).Mesh);

        // 4. Undo takes both back.
        ctx.Services.Projects.Undo();
        ctx.Services.Projects.Undo();
        map.RefreshEdits();
        Assert.Null(state.GetMeshOverride(pieceItem.Reference));
        Assert.Null(state.GetMeshOverride(roadItem.Reference, road.Name));
        Assert.True(state.IsEmpty, state.Describe());
        Assert.Empty(map.ReplacedMeshes);
        Assert.Empty(map.PartMeshes);
    }

    /// <summary>The mesh and scale a component of a written level has (parsed from the staged .umap/.uexp).</summary>
    private static (string Mesh, FVector Scale) Written(ExportResult result, string level, string actor, string component)
    {
        var exported = result.Levels.Single(l => string.Equals(l.PackagePath, level, StringComparison.OrdinalIgnoreCase));
        var file = Path.Combine([result.StagingDirectory, .. exported.VirtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        var package = CookedPackage.Parse(File.ReadAllBytes(file), File.ReadAllBytes(Path.ChangeExtension(file, ".uexp")), null, level);
        var levelIndex = LevelPackageEditor.FindLevelExport(package);
        var actorIndex = Enumerable.Range(0, package.Exports.Count).Single(i => package.Exports[i].OuterIndex == levelIndex + 1 && package.ResolveName(package.Exports[i].ObjectName) == actor);
        var componentIndex = Enumerable.Range(0, package.Exports.Count).Single(i => package.Exports[i].OuterIndex == actorIndex + 1 && package.ResolveName(package.Exports[i].ObjectName) == component);
        var props = package.ReadProperties(componentIndex);
        var mesh = Assert.IsType<ObjectValue>(props.Find("StaticMesh")!.Value);
        var scale = props.Find("RelativeScale3D")?.Value is VectorValue v ? new FVector(v.X, v.Y, v.Z) : FVector.One;
        return (package.GetFullPath(mesh.Index), scale);
    }
}

/// <summary>
/// The Replacer as the owner sees it (screenshots with <c>SCUMSTUDIO_SCREENSHOTS</c>): off without a selection; two cards,
/// the selection's picture and name on the left, the pick on the right; the right card drops the family list down
/// (pictures made on workers as the menu opens, a search box, the current one first and marked); a pick fills the card.
/// </summary>
public sealed class ReplaceMenuRealTests
{
    private readonly ITestOutputHelper _output;

    public ReplaceMenuRealTests(ITestOutputHelper output) => _output = output;

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheReplacerShowsTwoCardsAndTheFamilyWithPictures()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "ReplaceMenu");
            await map.LoadLevelsAsync(["/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01"]);
            HeadlessUi.Pump();

            var button = HeadlessUi.FindNamed<Button>(window, "ReplaceButton")!;
            Assert.False(button.IsEnabled); // nothing selected
            var item = map.AllActors.First(a => a.Actor.Kind == ActorKind.StaticMeshActor && a.Actor.StaticMeshPath is { } m && ReplaceFamilies.Candidates(m, AssetDumper.Packages).Count > 1);
            map.SelectedActor = item;
            HeadlessUi.Pump();
            Assert.True(button.IsEnabled);

            // Two cards: the selection (its picture and name) and the picking card, empty until a pick.
            button.Flyout!.ShowAt(button);
            HeadlessUi.Pump();
            var picker = map.ReplacePicker;
            Assert.True(picker.Items.Count > 1, map.ReplaceCaption);
            Assert.True(picker.Items[0].IsCurrent);
            Assert.Same(picker.Items[0], map.ReplaceCurrent);
            Assert.Null(picker.Selected);
            Assert.False(map.ReplaceCommand.CanExecute(null));
            var panel = Assert.IsType<StackPanel>(((Flyout)button.Flyout).Content);
            var from = HeadlessUi.FindNamed<Border>(panel, "ReplaceFromCard")!;
            Assert.Contains(from.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == map.ReplaceCurrent!.Name);
            var card = HeadlessUi.FindNamed<ToggleButton>(panel, "PickerCard")!;
            var scroll = panel.FindAncestorOfType<ScrollViewer>()!;
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"the cards are clipped: {scroll.Extent.Width} > {scroll.Viewport.Width}");

            // The pictures of the first rows are asked for as the menu opens and made off the UI thread.
            var first = picker.All.Take(ObjectPicker.PrefetchCount).ToList();
            var made = HeadlessUi.PumpUntil(() => first.Count(c => c.Thumbnail is not null) >= Math.Min(first.Count, 8), TimeSpan.FromSeconds(120));
            _output.WriteLine($"{map.ReplaceCaption}; pictures {first.Count(c => c.Thumbnail is not null)}/{first.Count} after the menu opened");
            Assert.True(made, "the first rows' pictures were not made");
            Assert.NotNull(map.ReplaceCurrent!.Thumbnail);

            // The right card drops the list down: the whole family, the current one first with its "now" badge.
            card.IsChecked = true;
            HeadlessUi.Pump();
            Assert.True(picker.IsOpen);
            var popup = HeadlessUi.Find<Popup>(panel).Single();
            Assert.True(popup.IsOpen);
            var listPanel = Assert.IsAssignableFrom<Visual>(popup.Child);
            var list = HeadlessUi.FindNamed<ItemsControl>(listPanel, "PickerList")!;
            Assert.Equal(picker.Items.Count, list.ItemCount);
            var rows = HeadlessUi.Find<Button>(list).ToList();
            Assert.True(rows.Count > 3, $"{rows.Count} rows realized");
            var currentRow = rows.Single(r => ReferenceEquals(r.CommandParameter, picker.Items[0]));
            Assert.Contains(currentRow.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("badge") && b.IsEffectivelyVisible);
            Assert.DoesNotContain(rows.Where(r => !ReferenceEquals(r, currentRow)).SelectMany(r => r.GetVisualDescendants().OfType<Border>()), b => b.Classes.Contains("badge") && b.IsEffectivelyVisible);
            var search = HeadlessUi.FindNamed<TextBox>(listPanel, "PickerSearchBox")!;
            search.Text = "zz-nothing-like-this";
            HeadlessUi.Pump();
            Assert.Single(picker.Items); // the current one stays
            search.Text = string.Empty;
            HeadlessUi.Pump();
            Assert.Equal(picker.All.Count, picker.Items.Count);
            HeadlessUi.SaveScreenshot(window, "map-replace-list");

            // A pick fills the right card, closes the list and turns Replace on.
            var other = picker.Items[1];
            var row = HeadlessUi.Find<Button>(list).Single(r => ReferenceEquals(r.CommandParameter, other));
            row.Command!.Execute(row.CommandParameter);
            HeadlessUi.Pump();
            Assert.Same(other, picker.Selected);
            Assert.False(picker.IsOpen);
            Assert.False(popup.IsOpen);
            Assert.True(map.ReplaceCommand.CanExecute(null));
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == other.Name && t.IsEffectivelyVisible);
            HeadlessUi.PumpUntil(() => other.Thumbnail is not null, TimeSpan.FromSeconds(60));
            HeadlessUi.SaveScreenshot(window, "map-replace-menu");

            button.Flyout.Hide();
            HeadlessUi.Pump();
            Assert.False(picker.IsOpen);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
