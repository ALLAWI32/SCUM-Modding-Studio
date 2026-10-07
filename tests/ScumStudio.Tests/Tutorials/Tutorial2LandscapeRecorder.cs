using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Tests.App;

namespace ScumStudio.Tests.Tutorials;

/// <summary>
/// Records tutorial 2 "Landscape looks, trees and fit to ground": the real app headless over the real game files, one PNG
/// per step and <c>steps.json</c> (file, caption, narration, click) into <c>SCUMSTUDIO_TUTORIAL_OUT</c>. Runs only with
/// that folder and <c>SCUM_PAKS</c>. Each frame's 3D view is <see cref="TutorialCapture"/>'s offscreen render of the page's
/// scene with its edits applied; the renderer draws the game's own ground textures, so a ground look shows as the menu's
/// choice and its History row, and the narration says what changes in the game. The project and the export live in a
/// neutral folder that is deleted afterwards, so no owner path is in a picture.
/// </summary>
public sealed class Tutorial2LandscapeRecorder
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    private const string Root = @"C:\SCUM Mods";

    [AvaloniaFact]
    public async Task RecordLandscapeTutorial()
    {
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_TUTORIAL_OUT") is not { Length: > 0 } output
            || Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, output);
        var ownsRoot = !Directory.Exists(Root);
        var projects = Path.Combine(Root, "Projects");
        var export = Path.Combine(Root, "Export");
        Directory.CreateDirectory(projects);
        Directory.CreateDirectory(export);
        var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        var rec = new TutorialCapture(window, ctx.Services, output);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await ctx.Services.Projects.CreateAsync(projects, "Tutorial 2");
            var catalog = ctx.Services.Workspace.Catalog!;

            // 1. The farm with the landscape tiles around its biggest building (the tree shows the farm's cell open).
            map.IsWorldMode = false; // as picking a level in the tree does
            await map.LoadLevelsAsync([Farm]);
            var meshes = map.PreparedScene!.Meshes;
            var biggestName = map.AllActors
                .Where(a => a.Actor.Kind == ActorKind.StaticMeshActor && a.Actor.StaticMeshPath is { } m && m.Contains("/Buildings/", StringComparison.OrdinalIgnoreCase) && meshes.ContainsKey(m))
                .MaxBy(a => meshes[a.Actor.StaticMeshPath!].Mesh.Bounds.Size.X * meshes[a.Actor.StaticMeshPath!].Mesh.Bounds.Size.Y)!.Name;
            var at = map.AllActors.First(a => a.Name == biggestName).Actor.WorldTransform.Translation;
            var world = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog, null);
            var tiles = MapPageViewModel.LevelsAround(world, at, 2000f).Where(p => p.Contains("/Landscape_", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.NotEmpty(tiles);
            await map.LoadLevelsAsync([Farm, .. tiles]);
            Assert.NotNull(map.PreparedScene!.HeightField);
            meshes = map.PreparedScene.Meshes;
            var cell = map.Nodes.FirstOrDefault(n => n.Title == "Cell A_3");
            var leaf = cell?.Children.SelectMany(c => c.Children).FirstOrDefault(n => n.Package?.Name == "A_3_Farm_01");
            if (cell is not null && leaf is not null)
            {
                cell.IsExpanded = true;
                cell.Children.First(g => g.Children.Contains(leaf)).IsExpanded = true;
            }

            HeadlessUi.Pump();
            var leafItem = leaf is null ? null : HeadlessUi.Find<TreeViewItem>(window).FirstOrDefault(t => ReferenceEquals(t.DataContext, leaf));
            leafItem?.BringIntoView();
            HeadlessUi.Pump();
            rec.Map = map;
            var biggest = map.AllActors.First(a => a.Name == biggestName);
            rec.FrameOn(biggest.SelectableId, marginCm: 2500f, pitch: -40f, shift: -0.3f); // the farm right of centre: the menus open over the left half
            rec.Shot("Load the farm with the landscape tiles under it",
                "Open the Map page and load A_3_Farm_01 together with the Landscape tiles around it. With the ground loaded, the app knows the terrain under every object.",
                leafItem ?? HeadlessUi.FindNamed<Control>(window, "WorldTree"));

            // 2. The Landscape menu.
            var landscape = HeadlessUi.FindNamed<Button>(window, "LandscapeButton")!;
            landscape.Flyout!.ShowAt(landscape);
            HeadlessUi.Pump();
            rec.Shot("Landscape: tree swaps and Fit to ground",
                "The Landscape button opens one menu: a tree swap for the whole island, and Fit to ground for the selected objects.",
                landscape);

            // 3. Trees: every oak drawn as a pine.
            var oak = map.Trees.First(t => t.Name.Contains("Oak", StringComparison.OrdinalIgnoreCase));
            var pine = map.Trees.First(t => t.Name.Contains("Pine", StringComparison.OrdinalIgnoreCase));
            map.TreeToSwap.Selected = oak;
            map.TreeSwapWith.Selected = pine;
            map.SwapTreeCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.Single(map.TreeSwaps);
            rec.Shot("Trees: draw one tree of the game as another",
                $"Pick the tree to replace on the left and the tree to draw instead on the right, then press Swap. Every {oak.Name} on the island becomes a {pine.Name}; the swap is listed under the pickers and in History, and Put back removes it.",
                HeadlessUi.Find<Button>(window).FirstOrDefault(b => ReferenceEquals(b.Command, map.SwapTreeCommand)));
            landscape.Flyout.Hide();
            HeadlessUi.Pump();

            // 7-8. Fit to ground: a house lifted and tilted, then laid on the slope.
            var house = map.AllActors.FirstOrDefault(a => a.Actor.Kind == ActorKind.Blueprint && a.Actor.Root is not null
                            && a.Name.Contains("house", StringComparison.OrdinalIgnoreCase)
                            && a.Actor.Components.Any(c => c.StaticMeshPath is { } m && !c.IsInstanced && c.IsVisible && meshes.ContainsKey(m)))
                        ?? biggest;
            var root = house.Actor.Root!.Relative;
            ctx.Services.Projects.Apply(EditOpFactory.SetTransform(house.Level, house.Actor,
                root with { Location = root.Location + new FVector(0f, 0f, 150f), Rotation = root.Rotation with { Pitch = root.Rotation.Pitch + 12f } }, ctx.Services.Projects.Current!.State));
            map.RefreshEdits();
            map.SelectedActorId = house.SelectableId;
            HeadlessUi.Pump();
            Assert.Same(house, map.SelectedActor);
            var shape = HeadlessUi.FindNamed<Button>(window, "ShapeButton")!;
            shape.Flyout!.ShowAt(shape);
            HeadlessUi.Pump();
            rec.FrameOn(house.SelectableId, marginCm: 500f, pitch: -30f, shift: -0.3f); // right of centre, out from under the Shape panel
            var before = map.ActorTransforms[house.SelectableId].Translation;
            rec.Shot("A house lifted and tilted floats over the slope",
                "This house was moved 1.5 m up and tilted 12 degrees, so it hangs over the hillside. Select it and open Shape: a building cannot bend, but it has Fit to ground.",
                HeadlessUi.FindNamed<Control>(window, "FitToGroundButton"));
            map.FitToGroundCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.NotEqual(before, map.ActorTransforms[house.SelectableId].Translation);
            shape.Flyout.Hide();
            HeadlessUi.Pump();
            rec.FrameOn(house.SelectableId, marginCm: 500f, pitch: -30f);
            rec.Shot("Fit to ground lays the house on the slope",
                "Fit to ground puts its four bottom corners on the terrain and tilts the house to the slope. Properties shows the new location and rotation, and History gets one row.",
                HeadlessUi.FindNamed<Control>(window, "HistoryList"));

            // 9-10. Multi-select: the three objects nearest the house lifted 3 m, then all set down at once.
            var houseAt = house.Actor.WorldTransform.Translation;
            var candidates = map.AllActors
                .Where(a => a.Actor.Kind == ActorKind.StaticMeshActor && a.SelectableId != house.SelectableId && a.Actor.Root is not null
                            && a.Actor.StaticMeshPath is { } m && meshes.ContainsKey(m) && !m.Contains("/Buildings/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var firstAt = candidates.MinBy(a => FVector.Distance(a.Actor.WorldTransform.Translation, houseAt))!.Actor.WorldTransform.Translation;
            var three = candidates.OrderBy(a => FVector.Distance(a.Actor.WorldTransform.Translation, firstAt)).Take(3).ToList(); // a cluster, so one close shot shows all three
            Assert.Equal(3, three.Count);
            var state = ctx.Services.Projects.Current!.State;
            ctx.Services.Projects.Apply(new BatchOp("Lift three objects", three.Select(a =>
            {
                var relative = a.Actor.Root!.Relative;
                return (EditOp)EditOpFactory.SetTransform(a.Level, a.Actor, relative with { Location = relative.Location + new FVector(0f, 0f, 300f) }, state);
            }).ToList()));
            map.RefreshEdits();
            map.SelectedActorId = three[0].SelectableId;
            map.ToggleGroup(three[1].SelectableId, null);
            map.ToggleGroup(three[2].SelectableId, null);
            Assert.True(map.HasGroup);
            HeadlessUi.Pump();
            var centre = three.Aggregate(FVector.Zero, (sum, a) => sum + a.Actor.WorldTransform.Translation) / 3f;
            var radius = (three.Max(a => FVector.Distance(a.Actor.WorldTransform.Translation, centre)) * 0.5f) + 200f; // FrameAt fits the cube's sphere: the view is ~7 radii wide
            rec.FrameAt(centre, radius, pitch: -35f);
            rec.Shot("Multi-select: three objects floating 3 m up",
                "Ctrl+click adds objects to the selection; here three objects next to the house were lifted 3 metres. All of them light up together.",
                EntityRow(window, three[2]));
            var rows = ctx.Services.Projects.History.Count;
            map.FitToGroundCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.Equal(rows + 1, ctx.Services.Projects.History.Count);
            rec.Shot("One Fit to ground sets all three down",
                "Press Fit to ground once and every selected object is set on the ground, as one History row that Ctrl+Z undoes together.",
                HeadlessUi.FindNamed<Control>(window, "HistoryList"));

            // 11. Export the client pak.
            var page = (ProjectsPageViewModel)vm.NavigateTo("projects")!;
            page.NewProjectFolder = projects;
            page.ExportFolder = export;
            page.ExportServer = false;
            HeadlessUi.Pump();
            await page.ExportCommand.ExecuteAsync(null);
            HeadlessUi.Pump();
            var pak = Assert.Single(page.LastExport).PakPath;
            Assert.True(pak is not null && File.Exists(pak), "no pak written");
            var pakName = Path.GetFileName(pak);
            rec.Map = null;
            rec.Shot("Export mod: one pak with every change",
                $"On the Projects page press Export mod. The app builds {pakName} with its .sig file; copy both into SCUM\\Content\\Paks. The tree swap and the moved objects are in it.",
                HeadlessUi.FindNamed<Control>(window, "ExportButton"));
            rec.WriteSteps();
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, null);
            ctx.Dispose();
            string[] folders = ownsRoot ? [Root] : [projects, export];
            foreach (var folder in folders)
            {
                TryDelete(folder);
            }
        }
    }

    private static Control? EntityRow(Window window, ActorItemViewModel item)
    {
        var list = HeadlessUi.FindNamed<ListBox>(window, "EntityList");
        list?.ScrollIntoView(item);
        HeadlessUi.Pump();
        return (Control?)HeadlessUi.Find<ListBoxItem>(window).FirstOrDefault(i => ReferenceEquals(i.DataContext, item)) ?? list;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // a file still open: the folder is neutral and holds nothing secret
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
