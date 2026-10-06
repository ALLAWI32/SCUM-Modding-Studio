using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Pak;
using ScumStudio.Tests.App;

namespace ScumStudio.Tests.Tutorials;

/// <summary>
/// Records the "Getting started: your first map mod" tutorial: one PNG per step plus <c>steps.json</c> (caption, narration,
/// click point) in <c>SCUMSTUDIO_TUTORIAL_OUT</c>. Real game files only (<c>SCUM_PAKS</c>); does nothing otherwise.
/// The headless platform gives the GL viewport no context: <see cref="TutorialCapture"/> renders each frame's 3D view offscreen
/// from the same prepared scene with the page's edits applied and pastes it into the window render.
/// </summary>
public sealed class Tutorial1GettingStartedRecorder
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";

    // Neutral folders for the pictures (the temp data folder is under the owner's profile); created here, deleted at the end.
    private const string NeutralRoot = @"C:\SCUM Mods";
    private const string ProjectsFolder = NeutralRoot + @"\Projects";
    private const string ExportFolder = NeutralRoot + @"\Export";

    [AvaloniaFact]
    public async Task RecordGettingStarted()
    {
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_TUTORIAL_OUT") is not { Length: > 0 } output
            || Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        Directory.CreateDirectory(output);
        var rootExisted = Directory.Exists(NeutralRoot);
        foreach (var folder in new[] { ProjectsFolder, ExportFolder })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true); // a crashed run
            }

            Directory.CreateDirectory(folder);
        }

        Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, output);
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks, ClientModsOutputFolder = ExportFolder });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        var rec = new TutorialCapture(window, ctx.Services, output);
        try
        {
            // 1-2: Settings, the game Paks folder, connect.
            vm.NavigateTo("settings");
            HeadlessUi.Pump();
            var paksBox = HeadlessUi.Find<TextBox>(window).First(t => t.Watermark?.Contains("steamapps", StringComparison.Ordinal) == true);
            rec.Shot("Settings: put your game Paks folder here", "Open Settings and paste the path of your game's Paks folder, the one inside SCUM\\Content. Click Save folders.", paksBox);
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            HeadlessUi.Pump();
            var connect = HeadlessUi.Find<Button>(window).FirstOrDefault(b => ReferenceEquals(b.Command, vm.ConnectCommand));
            rec.Shot("Connected: the status bar shows your game paks", "The app reads the game files. The status bar at the bottom shows the paks and the key; the round button there reconnects any time.", connect);

            // 3: Map page overview.
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            HeadlessUi.Pump();
            var cell = map.Nodes.FirstOrDefault(n => n.Title == "Cell A_3");
            if (cell is not null)
            {
                cell.IsExpanded = true;
            }

            HeadlessUi.Pump();
            rec.Map = map;
            rec.Shot("Map page: World tree, 3D view, Entities, Properties", "This is the Map page. The World tree on the left lists the island's cells and their levels, the 3D view is in the middle, the Entities list is below it and Properties and History are on the right.", null);

            // 4: A project.
            await ctx.Services.Projects.CreateAsync(ProjectsFolder, "My First Mod");
            HeadlessUi.Pump();
            rec.Shot("New project: every edit is saved in it", "Press New project in the header and give it a name. Everything you change is written to the project, so you can undo, close the app and carry on later.", HeadlessUi.FindNamed<Control>(window, "ProjectName"));

            // 5: Open the farm level from the tree.
            var leaf = cell?.Children.SelectMany(c => c.Children).FirstOrDefault(n => n.Package?.Name == "A_3_Farm_01");
            if (leaf is not null)
            {
                cell!.Children.First(g => g.Children.Contains(leaf)).IsExpanded = true;
                map.SelectedNode = leaf;
                await map.LevelLoadCompletion;
            }
            else
            {
                await map.LoadLevelsAsync([Farm]);
            }

            HeadlessUi.Pump();
            Assert.NotNull(map.PreparedScene);
            string[] liked = ["Crate", "Barrel", "Box", "Pallet", "Hay", "Tractor", "Bench", "Table", "Cart"];
            var drawn = map.AllActors.Where(a => a.ClassName == "StaticMeshActor" && a.HasMesh && a.Actor.StaticMeshPath is { } p && map.PreparedScene!.Meshes.ContainsKey(p)).ToList();
            var target = liked.Select(l => drawn.FirstOrDefault(a => a.Name.Contains(l, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(a => a is not null) ?? drawn.First();
            var victim = liked.Select(l => drawn.FirstOrDefault(a => a != target && a.Name.Contains(l, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(a => a is not null)
                ?? drawn.First(a => a != target);
            var leafItem = leaf is null ? null : HeadlessUi.Find<TreeViewItem>(window).FirstOrDefault(t => ReferenceEquals(t.DataContext, leaf));
            leafItem?.BringIntoView();
            HeadlessUi.Pump();
            rec.FrameOn(target.SelectableId, marginCm: 4000f, pitch: -35f); // the farm around the object we edit next
            rec.Shot("Open a level: click A_3_Farm_01 in the World tree", "Open a cell and click a level to load it. The farm appears in the 3D view and its objects fill the Entities list. Right-click the view and drag to look around, W A S D to fly.", leafItem ?? HeadlessUi.FindNamed<Control>(window, "WorldTree"));

            // 6: Select an object.
            map.SelectedActorId = target.SelectableId;
            HeadlessUi.Pump();
            Assert.Same(target, map.SelectedActor);
            rec.FrameOn(target.SelectableId, marginCm: 700f, pitch: -45f);
            rec.Shot($"Select an object: {target.Name}", "Click an object in the 3D view or in the Entities list. It lights up, and Properties shows its location, rotation and scale.", EntityRow(window, target));

            // 7: Move it 6 m along X through the Properties panel.
            var parts = map.EditLocation.Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            map.EditLocation = string.Create(CultureInfo.InvariantCulture, $"{parts[0] + 600:0.##}, {parts[1]:0.##}, {parts[2]:0.##}");
            map.ApplyTransformCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.Contains(target.SelectableId, map.ActorTransforms.Keys);
            var moved = ctx.Services.Projects.History[0].Summary;
            rec.Shot("Move it: new location, Apply", "Type a new location and press Apply, or drag the gizmo's arrows in the 3D view and turn it with the ring. Snap joins pieces end to end and Local axes follows the object's own front. History shows the move.", HeadlessUi.FindNamed<Control>(window, "ApplyTransformButton"));

            // 8-9: Select another object, delete it.
            map.SelectedActorId = victim.SelectableId;
            HeadlessUi.Pump();
            Assert.Same(victim, map.SelectedActor);
            rec.FrameOn(victim.SelectableId, marginCm: 700f, pitch: -45f);
            rec.Shot($"Pick another object: {victim.Name}", "Now pick something you want gone. Copy and Duplicate in Properties make more of an object; Delete in the Entities bar removes it.", EntityRow(window, victim));
            map.DeleteSelectedCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.Contains(victim.SelectableId, map.HiddenActorIds);
            rec.Shot($"Delete: {victim.Name} is gone from the level", "Press Delete, or the Delete key. The object disappears from the view, its row in Entities goes grey, and History gets a new line.", HeadlessUi.FindNamed<Control>(window, "DeleteButton"));

            // 10: Undo.
            map.UndoCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.DoesNotContain(victim.SelectableId, map.HiddenActorIds);
            rec.Shot("Undo (Ctrl+Z): the object is back", "Changed your mind? Ctrl+Z or the undo arrow in History brings it back. The line stays in History marked undone, and redo puts it back.", HeadlessUi.FindNamed<Control>(window, "UndoButton"));

            // 11: Export the mod pak.
            var projects = (ProjectsPageViewModel)vm.NavigateTo("projects")!;
            projects.NewProjectFolder = ProjectsFolder; // not Documents\ScumStudio Projects of the owner
            projects.ExportFolder = ExportFolder;
            projects.ExportServer = false;
            HeadlessUi.Pump();
            await projects.ExportCommand.ExecuteAsync(null);
            HeadlessUi.Pump();
            var pak = Assert.Single(projects.LastExport).PakPath;
            Assert.NotNull(pak);
            Assert.True(File.Exists(pak), "no pak written");
            var pakName = Path.GetFileName(pak);
            rec.Map = null;
            rec.Shot($"Export mod: {pakName}", $"On the Projects page press Export mod. The app builds {pakName} with its .sig file. Copy both into SCUM\\Content\\Paks to play it, or into your server's server_mods folder for a dedicated server.", HeadlessUi.FindNamed<Control>(window, "ExportButton"));

            Assert.StartsWith("Transform ", moved, StringComparison.Ordinal);
            rec.WriteSteps();
        }
        finally
        {
            window.Close();
            vm.Dispose();
            ctx.Services.Projects.Close();
            Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, null);
            Directory.Delete(rootExisted ? ProjectsFolder : NeutralRoot, recursive: true);
            if (rootExisted)
            {
                Directory.Delete(ExportFolder, recursive: true);
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
}
