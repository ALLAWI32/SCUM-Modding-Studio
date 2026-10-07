using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Pak;
using ScumStudio.Tests.App;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Tutorials;

/// <summary>
/// Records tutorial 3, "Spawners, placing Blueprints and reporting a problem": the real app headless over the real game
/// files, one PNG per step and <c>steps.json</c> (file, caption, narration, click). Runs only with
/// <c>SCUMSTUDIO_TUTORIAL_OUT</c> (the folder) and <c>SCUM_PAKS</c>. The 3D view of every frame comes from
/// <see cref="TutorialCapture"/> (offscreen render of the page's scene, pins included). Nothing leaves the PC: the report
/// stays typed and the update card shows a made-up next version with this version's changelog. The project lives in a
/// neutral folder that is deleted afterwards, so no owner path is in a picture.
/// </summary>
public sealed class Tutorial3SpawnsRecorder
{
    private const string Maps = "/Game/ConZ_Files/Maps/The_Island/";
    private const string FallbackBlueprint = "/Game/ConZ_Files/Models/Objects/Indoor/Furniture/Water_Tank_Office/BP_Water_Tank_Office_01";
    private const string Root = @"C:\SCUM Mods";

    private readonly List<string> _skipped = [];

    [AvaloniaFact]
    public async Task RecordSpawnersBlueprintsAndReport()
    {
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_TUTORIAL_OUT") is not { Length: > 0 } outDir
            || Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, outDir);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReportService.WebhookVariable)))
        {
            // Shows the "Report a problem" box in a build without the embedded address; SendReport is never executed here.
            Environment.SetEnvironmentVariable(ReportService.WebhookVariable, "https://discord.com/api/webhooks/0/tutorial-never-sent");
        }

        var ownsRoot = !Directory.Exists(Root);
        var projects = Path.Combine(Root, "Projects");
        Directory.CreateDirectory(projects);
        var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        var rec = new TutorialCapture(window, ctx.Services, outDir);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await ctx.Services.Projects.CreateAsync(projects, "Tutorial 3");
            // The airfield: hangar loot points and vehicle spawn points; the outpost: traders and a car shop's vehicle box.
            map.IsWorldMode = false; // as picking a level in the tree does: the whole-island view streams the levels around the camera over anything loaded
            await map.LoadLevelsAsync([Maps + "A_4_Airfield", Maps + "A_0_Outpost"]);
            var placements = map.PreparedScene!.Placements;
            map.ShowSpawns = true;
            HeadlessUi.Pump();
            rec.Map = map;

            // A loot point out in the open (not inside another actor's mesh box, so the camera sees it, unlike a shelf under a
            // roof) with the most loot points around it: the camera looks at that spot for the first four steps.
            var meshes = map.PreparedScene.Meshes;
            var rooms = placements.Where(p => !SpawnMarkers.IsMarker(p.MeshPath) && meshes.ContainsKey(p.MeshPath))
                .Select(p => (p.SelectableId, Box: LevelSceneUploader.TransformBounds(meshes[p.MeshPath].Mesh.Bounds, p.GlModel)))
                .Where(r => r.Box.Size.Y > 250f) // GL Y is up: something tall enough to hide what is in it
                .ToList();
            bool Outdoors(ScenePlacement pin)
            {
                var at = UeToGl.Point(pin.World.Translation) + new Vector3(0f, 50f, 0f);
                return !rooms.Any(r => r.SelectableId != pin.SelectableId && r.Box.Contains(at));
            }

            var lootPins = placements.Where(p => SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.Loot && p.InstanceKey is { IsLootPoint: true }).ToList();
            var open = lootPins.Where(Outdoors).ToList();
            var lootPin = open.MaxBy(p => open.Count(o => FVector.Distance(o.World.Translation, p.World.Translation) < 800f))
                ?? lootPins.FirstOrDefault() ?? placements.First(p => SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.Loot);
            var lootOwner = map.AllActors.First(a => a.SelectableId == lootPin.SelectableId);
            rec.FrameAt(lootPin.World.Translation, radiusCm: 600f, pitch: -45f, shift: 0.3f); // left of centre: the legend flyout covers the middle

            // 1. Spawns on, the legend open.
            var legend = (Button)Named(window, "SpawnLegendButton")!;
            legend.Flyout!.ShowAt(legend);
            HeadlessUi.Pump();
            rec.Shot("Spawns on: every spawner is a coloured pin",
                "Turn on Spawns and every spawner in the level shows up as a coloured pin in the 3D view; the gold crates at this barn are its loot points. Open Legend to see what each colour means.",
                Named(window, "SpawnsButton"));

            // 2-3. The loot layer off and on.
            var loot = map.SpawnLayers.Single(l => l.Key == "Loot");
            var lootBox = window.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => ReferenceEquals(c.DataContext, loot));
            loot.IsVisible = false;
            HeadlessUi.Pump();
            rec.Shot("Untick Loot points: the gold pins disappear",
                "Gold is loot points, blue is vehicle spawn points, green is zombie spawn points, teal is traders, light blue is car shop vehicles, orange is effects like fire. Untick Loot points and only the gold crates disappear; everything else stays.",
                lootBox);
            loot.IsVisible = true;
            HeadlessUi.Pump();
            rec.Shot("Tick it again and the gold pins are back",
                "Tick Loot points again and the gold pins come back. The app remembers which kinds you switched off.",
                lootBox);
            legend.Flyout.Hide();
            HeadlessUi.Pump();

            // 4. A loot pin: what spawns there (legend closed, so the pin goes back to the centre).
            rec.FrameAt(lootPin.World.Translation, radiusCm: 600f, pitch: -45f);
            map.SelectedInstanceKey = lootPin.InstanceKey;
            map.SelectedActorId = lootPin.SelectableId;
            HeadlessUi.PumpUntil(() => map.SpawnInfo.Count > 0, TimeSpan.FromSeconds(90));
            HeadlessUi.PumpUntil(() => map.SpawnInfo.Any(r => r.IsItem), TimeSpan.FromSeconds(30)); // the item rows follow the header
            Named(window, "SpawnInfoPanel")?.BringIntoView(); // the list sits below the actor's properties
            HeadlessUi.Pump();
            rec.Shot("Click a gold pin: the panel lists what spawns there",
                "Click a gold pin and it lights up; the panel on the right lists what can spawn at that point and how rare each item is.",
                Named(window, "SpawnInfoPanel"));

            // 5. Delete a spawner: the vehicle spawn point nearest the loot (so buildings are in the picture), else any actor
            // that only spawns, else the loot pin's owner.
            var spawner = map.AllActors.Where(a => SpawnMarkers.KindOf(a.Actor) == SpawnKind.VehiclePlace) // a VehicleSpawnPlace actor: a world vehicle spawn point
                    .MinBy(a => FVector.Distance(a.Actor.WorldTransform.Translation, lootPin.World.Translation))
                ?? map.AllActors.FirstOrDefault(a => SpawnMarkers.KindOf(a.Actor) is not null)
                ?? lootOwner;
            var spawnerKind = SpawnMarkers.KindOf(spawner.Actor) switch
            {
                SpawnKind.Vehicle or SpawnKind.VehiclePlace => "vehicle spawn point",
                SpawnKind.Zombie => "zombie spawn point",
                SpawnKind.Trader => "trader",
                { } other => other.ToString().ToLowerInvariant() + " spawner",
                null => "spawner",
            };
            map.SelectedInstanceKey = null;
            map.SelectedActorId = spawner.SelectableId;
            HeadlessUi.Pump();
            rec.FrameOn(spawner.SelectableId, marginCm: 800f, pitch: -45f);
            if (map.DeleteSelectedCommand.CanExecute(null))
            {
                map.DeleteSelectedCommand.Execute(null);
                HeadlessUi.Pump();
                rec.Shot($"Delete a {spawnerKind}: its pin is gone, History keeps the step",
                    $"Click a spawner pin, here a {spawnerKind}, and press Delete. The pin disappears from the 3D view, its row in Entities goes grey and the edit shows in History, where Undo brings it back.",
                    Named(window, "HistoryList"));
            }
            else
            {
                _skipped.Add($"delete: {spawner.Name} ({spawner.ClassName}) cannot be deleted");
            }

            // 6. Move a spawn part: the car shop's vehicle box, else any fixed-item spawner inside a building.
            var parts = placements.Where(p => p.Spawner is not null && p.InstanceKey is { InstanceIndex: InstanceKey.Part }
                && SpawnMarkers.IsMarker(p.MeshPath) && !map.HiddenActorIds.Contains(p.SelectableId)).ToList();
            var part = parts.FirstOrDefault(p => SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.VehiclePlace) ?? parts.FirstOrDefault();
            ActorItemViewModel? owner = null;
            if (part is not null && map.AllActors.FirstOrDefault(a => a.SelectableId == part.SelectableId) is { } partOwner
                && partOwner.Actor.FindComponent(part.Spawner!) is { } component)
            {
                owner = partOwner;
                var isCarShop = SpawnMarkers.KindOfMesh(part.MeshPath) == SpawnKind.VehiclePlace;
                map.SelectedInstanceKey = part.InstanceKey;
                map.SelectedActorId = part.SelectableId;
                HeadlessUi.Pump();
                rec.FrameAt(part.World.Translation, radiusCm: 700f, pitch: -45f);
                var moved = component.Relative.Location + new FVector(100f, 0f, 0f);
                map.EditLocation = string.Create(CultureInfo.InvariantCulture, $"{moved.X}, {moved.Y}, {moved.Z}");
                if (map.ApplyTransformCommand.CanExecute(null))
                {
                    map.ApplyTransformCommand.Execute(null);
                    HeadlessUi.Pump();
                    Named(window, "ApplyTransformButton")?.BringIntoView(); // the panel was left scrolled to the loot list
                    HeadlessUi.Pump();
                    rec.Shot(isCarShop ? "Move the car shop's vehicle box: new location, then Apply" : "Move a spawn part: new location, then Apply",
                        (isCarShop ? "The box where a car shop puts its vehicles is a part of the shop: " : "A machine or vehicle spot inside a building is a part of it: ")
                        + "click its pin, change the location in Properties and press Apply. Here it moved one metre along X; the pin moves with it and History has the step.",
                        Named(window, "ApplyTransformButton"));
                }
                else
                {
                    _skipped.Add($"move a spawn part: Apply is off for {partOwner.Name}/{part.Spawner}");
                }
            }
            else
            {
                _skipped.Add("move a spawn part: the level has no fixed-item spawner or vehicle box part");
            }

            // 7-8. Place a Blueprint from the Add object box, six metres from the last building (headless there is no camera to aim).
            var anchorActor = owner ?? lootOwner;
            var anchor = anchorActor.Actor.WorldTransform.Translation + new FVector(600f, 0f, 0f);
            map.AimPointProvider = () => anchor;
            var lamp = AssetDumper.Packages
                .Where(p => p.ClassName == "Blueprint" && p.PackagePath.Contains("/Street_Lamp/", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.PackagePath).FirstOrDefault() ?? FallbackBlueprint;
            map.SelectedInstanceKey = null;
            map.SelectedActorId = 0;
            rec.FrameAt(anchor, radiusCm: 700f, pitch: -30f);
            var addButton = (Button)Named(window, "AddObjectButton")!;
            addButton.Flyout!.ShowAt(addButton);
            map.AddObjectText = lamp;
            HeadlessUi.Pump();
            rec.Shot("Add object: type a Blueprint name or path",
                "Open Add object, type the name of any Blueprint, like a street lamp, and press Enter. Place in map on the Assets page does the same.",
                Named(window, "AddObjectBox"));
            map.AddObjectFromTextCommand.Execute(null);
            var placed = await map.AddCompletion;
            if (!placed && lamp != FallbackBlueprint)
            {
                _skipped.Add($"place: {lamp} was not found placed on the island; placed {FallbackBlueprint} instead");
                map.AddObject(FallbackBlueprint);
                placed = await map.AddCompletion;
            }

            addButton.Flyout.Hide();
            HeadlessUi.Pump();
            if (placed)
            {
                var placedId = map.SelectedActorId;
                var what = map.SelectedActor?.Name ?? "the object";
                HeadlessUi.PumpUntil(() => map.Clones.FirstOrDefault(c => c.Id == placedId) is { } clone && MeshesLoaded(map, clone), TimeSpan.FromSeconds(90));
                rec.FrameOn(placedId, marginCm: 250f, pitch: -25f);
                rec.Shot($"Placed: {what} stands in the 3D view, selected in Entities",
                    "The object appears where the camera looks, selected in the 3D view and in the Entities list, and History has the step. Export mod puts it in the game.",
                    Named(window, "EntityList"));
            }
            else
            {
                _skipped.Add("place: no Blueprint could be placed");
            }

            // 9. Support card with a typed report (never sent).
            vm.OpenSupportCommand.Execute(null);
            HeadlessUi.Pump();
            vm.ReportText = "The loot spawner I deleted in the camp still spawns items after I exported the mod.";
            HeadlessUi.Pump();
            rec.Shot("Support: write what went wrong, press Send",
                "Found a bug or have an idea? Open Support, pick Bug, Idea or Question, write it in your own language and press Send. It reaches the developer in English.",
                Named(window, "ReportBox"));
            vm.CloseSupportCommand.Execute(null);
            HeadlessUi.Pump();

            // 10-12. The Update button, the card with the release notes, What's new after the restart.
            var notes = ChangelogNotes();
            var current = Updater.Current;
            var next = new Version(current.Major, current.Minor, current.Build + 1);
            vm.Update = new ReleaseInfo(next, "v" + next.ToString(3), notes, null, 0, "https://github.com/" + Updater.Repository + "/releases");
            var updateButton = Named(window, "UpdateButton")!;
            updateButton.IsVisible = true; // gated on the GitHub check, which headless tests never wire; text and colour are the real bindings
            HeadlessUi.Pump();
            rec.Shot("A new version lights up the Update button",
                "When a new version is out, the Update button at the top lights up with its number.",
                updateButton);
            vm.OpenUpdateCommand.Execute(null);
            HeadlessUi.Pump();
            rec.Shot("Read the release notes, then press Install",
                "Click it to read what changed, then press Install. The app downloads the update, swaps its files and restarts itself.",
                Named(window, "InstallUpdateButton"));
            vm.CloseUpdateCommand.Execute(null);
            vm.WhatsNew = MainWindowViewModel.ReleaseNotes(notes);
            HeadlessUi.Pump();
            rec.Shot("After the restart: What's new",
                "After the restart the same notes show once as What's new. Press Got it and you are back to modding.",
                Named(window, "WhatsNewClose"));
        }
        finally
        {
            rec.WriteSteps();
            File.WriteAllLines(Path.Combine(outDir, "skipped.txt"), _skipped);
            window.Close();
            vm.Dispose();
            ctx.Dispose();
            Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, null);
            TryDelete(ownsRoot ? Root : projects);
        }
    }

    /// <summary>True when every mesh the clone draws is in the prepared scene or already loaded as an extra mesh.</summary>
    private static bool MeshesLoaded(MapPageViewModel map, ActorClone clone)
    {
        var meshes = (clone.Placements ?? []).Select(p => p.MeshPath).Concat(clone.MeshPath is { } m ? [m] : []);
        return meshes.All(path => SpawnMarkers.IsMarker(path) || map.PreparedScene!.Meshes.ContainsKey(path)
            || map.ExtraMeshes.Any(e => string.Equals(e.Asset.MeshPath, path, StringComparison.OrdinalIgnoreCase)));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // a file still open: the folder is neutral and empty of anything secret
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Control? Named(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);

    /// <summary>This version's section of the repository's CHANGELOG.md without its version line (as the update card shows notes).</summary>
    private static string ChangelogNotes([CallerFilePath] string thisFile = "")
    {
        var file = Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "CHANGELOG.md");
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file);
            var start = text.IndexOf("## [", StringComparison.Ordinal);
            if (start >= 0)
            {
                var end = text.IndexOf("\n## [", start + 4, StringComparison.Ordinal);
                var section = end < 0 ? text[start..] : text[start..end];
                section = System.Text.RegularExpressions.Regex.Replace(section, @"\r?\n[ \t]+(?![-*] )", " "); // hard-wrapped bullets back on one line
                return section[(section.IndexOf('\n') + 1)..].Trim();
            }
        }

        return "### New in this release\n- Spawn pins you can click, delete and move\n- Place any Blueprint from Assets or the Add object box\n- Report a problem from the Support card";
    }
}
