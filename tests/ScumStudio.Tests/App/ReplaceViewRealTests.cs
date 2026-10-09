using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Pak;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord (igor8802), reproduced by the owner on B_2_Airport_03: "The model does not change visually when replaced." The
/// journal had every Replace, the 3D view kept the old hangar: the Map page never handed the replaced meshes to its
/// viewport. Through the app's own window: a hangar's part, a mesh actor, a foliage-like instance and a multi-selection
/// are replaced, the viewport's scene draws each with the new mesh, undo puts the old ones back, redo the new ones, and
/// the export writes them. Real game files (<c>SCUM_PAKS</c>) and OpenGL.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class ReplaceViewRealTests
{
    private const string Airport = "/Game/ConZ_Files/Maps/The_Island/B_2_Airport_03";
    private const string Garages = "/Game/ConZ_Files/Maps/The_Island/B_2_Airport_02";

    private readonly ITestOutputHelper _output;

    public ReplaceViewRealTests(ITestOutputHelper output) => _output = output;

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task EveryReplaceIsDrawnAtOnceUndoneRedoneAndExported()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks) || GlTestEnvironment.SkipReason is not null)
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
            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "ReplaceView");
            await map.LoadLevelsAsync([Airport, Garages]);
            HeadlessUi.Pump();
            var viewport = HeadlessUi.FindNamed<LevelViewport>(window, "Viewport3d")!;
            using var harness = GlHarness.Create(64, 64);
            using var level = LevelSceneUploader.Upload(harness.Renderer, map.PreparedScene!);

            // What the view draws once it took the page's edits (as LevelViewport does each frame): the meshes it was handed, then the swaps.
            string?[] Drawn(Func<ScenePlacement, bool> which)
            {
                HeadlessUi.Pump();
                foreach (var extra in viewport.ExtraMeshes ?? [])
                {
                    if (!level.HasMesh(extra.Asset.MeshPath))
                    {
                        level.AddMesh(extra);
                    }
                }

                level.SetMeshes(viewport.ReplacedMeshes, viewport.PartMeshes);
                return level.Scene.Nodes.Where(n => n.Tag is ScenePlacement p && which(p)).Select(n => n.Mesh?.Name).ToArray();
            }

            // The name the renderer gives a mesh's handle, once the page has the mesh (in the scene, or loaded for the view).
            string NameOf(string mesh)
            {
                Assert.True(HeadlessUi.PumpUntil(() => map.PreparedScene!.Meshes.ContainsKey(mesh) || map.ExtraMeshes.Any(e => string.Equals(e.Asset.MeshPath, mesh, StringComparison.OrdinalIgnoreCase)),
                    TimeSpan.FromSeconds(120)), $"{mesh} was never loaded");
                var asset = map.PreparedScene!.Meshes.GetValueOrDefault(mesh) ?? map.ExtraMeshes.First(e => string.Equals(e.Asset.MeshPath, mesh, StringComparison.OrdinalIgnoreCase)).Asset;
                return asset.Lods[0].Name;
            }

            async Task<string> ReplaceAsync(Func<ReplaceCandidate, bool>? also = null)
            {
                map.RefreshReplaceCandidates();
                var pick = map.ReplacePicker.Items.First(c => !c.IsCurrent && !c.Choice.IsBlueprint && (also?.Invoke(c) ?? true));
                map.ReplacePicker.Selected = pick;
                Assert.True(map.ReplaceCommand.CanExecute(null), map.ReplaceCaption);
                map.ReplaceCommand.Execute(null);
                Assert.True(await map.ReplaceCompletion);
                _output.WriteLine($"{map.ReplaceCaption} -> {pick.Choice.ObjectPath}");
                return pick.Choice.ObjectPath;
            }

            // (a) The owner's hangar: one part of the Blueprint building (a click in part mode).
            var hangar = map.AllActors.First(a => a.Actor.ClassName.StartsWith("BP_Military_Hangar_RW_Ruined_CHILD_02", StringComparison.Ordinal));
            var part = hangar.Actor.Components.First(c => c.StaticMeshPath is not null && c.IsVisible && !c.IsSynthesized && c.ExportIndex != hangar.Actor.RootComponent);
            var partKey = InstanceKey.Of(hangar.SelectableId, part.Name, InstanceKey.Part);
            bool IsPart(ScenePlacement p) => p.InstanceKey == partKey;
            var oldPart = Assert.Single(Drawn(IsPart));
            map.SelectedActorId = hangar.SelectableId;
            map.SelectedInstanceKey = partKey;
            var partMesh = await ReplaceAsync();
            var newPart = NameOf(partMesh);
            Assert.NotEqual(oldPart, newPart);
            Assert.Equal(newPart, Assert.Single(Drawn(IsPart)));

            // (b) A plain mesh actor of the airport.
            var crate = map.AllActors.First(a => a.Level.PackagePath == Airport && a.Actor.Kind == ActorKind.StaticMeshActor && !a.IsAdded && a.Actor.StaticMeshPath is { } m
                && !m.Contains("Metal_Sheet", StringComparison.Ordinal) && ReplaceFamilies.Candidates(m, AssetDumper.Packages).Count > 1);
            bool IsCrate(ScenePlacement p) => p.SelectableId == crate.SelectableId && p.InstanceKey is null;
            var oldCrate = Drawn(IsCrate);
            Assert.NotEmpty(oldCrate);
            map.SelectedInstanceKey = null;
            map.SelectedActor = crate;
            var crateMesh = await ReplaceAsync();
            var newCrate = NameOf(crateMesh);
            Assert.All(Drawn(IsCrate), n => Assert.Equal(newCrate, n));

            // (c) One instance of a garage's instanced mesh (the airport has none): it leaves and a mesh actor stands where it stood.
            var instance = map.PreparedScene!.Placements.First(p => p.Instance is { } i && i.StaticMeshPath is { } m
                && ReplaceFamilies.Candidates(m, AssetDumper.Packages).Count > 1);
            map.SelectedActorId = instance.SelectableId;
            map.SelectedInstanceKey = instance.InstanceKey;
            var instanceMesh = await ReplaceAsync();
            HeadlessUi.Pump();
            Assert.Contains(instance.InstanceKey!.Value, viewport.HiddenInstances!);
            Assert.Contains(viewport.Clones!, c => c.MeshPath == instanceMesh);

            // (d) A multi-selection of two metal sheets: both become a third.
            var sheets = map.AllActors.Where(a => a.Level.PackagePath == Airport && !a.IsAdded && a.Actor.StaticMeshPath is { } m && m.Contains("Metal_Sheet", StringComparison.Ordinal))
                .DistinctBy(a => a.Actor.StaticMeshPath).Take(2).ToList();
            Assert.Equal(2, sheets.Count);
            bool IsSheet(ScenePlacement p) => sheets.Any(s => s.SelectableId == p.SelectableId) && p.InstanceKey is null;
            var oldSheets = Drawn(IsSheet);
            map.SelectedInstanceKey = null;
            map.SelectedActor = sheets[0];
            map.ToggleGroup(sheets[1].SelectableId, null);
            Assert.True(map.HasGroup);
            var sheetMesh = await ReplaceAsync(c => sheets.All(s => !string.Equals(s.Actor.StaticMeshPath, c.Choice.ObjectPath, StringComparison.OrdinalIgnoreCase)));
            var newSheet = NameOf(sheetMesh);
            Assert.All(Drawn(IsSheet), n => Assert.Equal(newSheet, n));
            Assert.Equal(4, project.Journal.Applied.Count);

            // Undo takes every one back in the view, redo brings them again.
            for (var i = 0; i < 4; i++)
            {
                map.UndoCommand.Execute(null);
            }

            Assert.Equal(oldPart, Assert.Single(Drawn(IsPart)));
            Assert.Equal(oldCrate, Drawn(IsCrate));
            Assert.Equal(oldSheets, Drawn(IsSheet));
            Assert.False(viewport.HiddenInstances?.Contains(instance.InstanceKey!.Value) == true, "the instance is back");
            for (var i = 0; i < 4; i++)
            {
                map.RedoCommand.Execute(null);
            }

            Assert.Equal(newPart, Assert.Single(Drawn(IsPart)));
            Assert.All(Drawn(IsCrate), n => Assert.Equal(newCrate, n));
            Assert.All(Drawn(IsSheet), n => Assert.Equal(newSheet, n));

            // The export carries the new meshes, so the game shows them too.
            var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            _output.WriteLine(string.Join(Environment.NewLine, result.Warnings));
            Assert.Equal(partMesh, ReplaceRealTests.Written(result, Airport, hangar.Name, part.Name).Mesh);
            Assert.Equal(crateMesh, ReplaceRealTests.Written(result, Airport, crate.Name, crate.Actor.Root!.Name).Mesh);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
