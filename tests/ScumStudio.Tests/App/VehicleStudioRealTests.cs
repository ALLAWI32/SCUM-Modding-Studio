using System.Numerics;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-17), on the Vehicles 3D tab of a Rager clone: "adding a part and removing a part does not work",
/// "Replace texture seems to do nothing", "I name the new vehicle; Rager_1, Rager_2". On the real game files
/// (<c>SCUM_PAKS</c>, key from this PC's store), through the page's view model as the buttons drive it: what the 3D view
/// is given, what the journal holds and what the exported mod carries. How the game spawns the result only the game shows.
/// </summary>
[Collection(ScumStudio.Tests.Rendering.GlCollection.Name)]
public sealed class VehicleStudioRealTests(ITestOutputHelper output)
{
    [AvaloniaPaksFact]
    public async Task ARemovedPartLeavesTheViewAndTheSpawnAndAnAddedPartAppearsInBoth()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder! });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Rager parts");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));

            // The name box offers the game's name and a number; "Rager_1" typed is kept clear of the FName split.
            Assert.Equal("Rager1", vehicles.CloneName);
            Assert.Contains("#SpawnVehicle BPC_Rager1", vehicles.ClonePreview, StringComparison.Ordinal);
            vehicles.CloneName = "Rager_1";
            Assert.Null(vehicles.CloneNameError);
            Assert.Equal("Rager_v1", vehicles.EffectiveCloneName);
            Assert.NotNull(vehicles.CloneNameNote);
            Assert.Contains("#SpawnVehicle BPC_Rager_v1", vehicles.ClonePreview, StringComparison.Ordinal);
            vehicles.CloneName = "RagerParts";
            await vehicles.CreateCloneAsync();
            Assert.Equal("BPC_RagerParts", vehicles.SelectedItem!.Name);
            Assert.Equal(string.Empty, vehicles.CloneName); // a clone offers no name of its own
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.Preview is not null && vehicles.HasPaints, TimeSpan.FromSeconds(60)));
            vehicles.StudioTab = StudioTab.Parts;
            var model = vehicles.Preview!;
            var stockParts = model.Parts.Count;
            output.WriteLine(string.Join("\n", model.Parts.Select((p, i) => $"{i}: {p.Name} [{p.Attachment}]").Where(l => l.Contains("BPC_", StringComparison.Ordinal)).Take(40)));

            // Remove a door: one journal step; the 3D view hides it; Ctrl+Z shows it again.
            var door = model.Parts.ToList().FindIndex(p => p.Attachment.Contains("Door", StringComparison.OrdinalIgnoreCase));
            Assert.True(door >= 0, "the Rager has a door part");
            vehicles.OnStudioPick(door);
            output.WriteLine($"picked {vehicles.StudioPartName}: '{vehicles.StudioPartNote}', editable {vehicles.CanEditPart}");
            var history = ctx.Services.Projects.Current!.History.Count;
            vehicles.RemovePartCommand.Execute(null);
            output.WriteLine($"after Remove: '{vehicles.StudioPartNote}'");
            Assert.Equal(history + 1, ctx.Services.Projects.Current!.History.Count);
            await vehicles.StudioPartsCompletion;
            Assert.True(vehicles.StudioPartOverrides.TryGetValue(door, out var removed) && removed.Hidden, "the removed door is hidden in the 3D view");
            ctx.Services.Projects.Undo();
            await vehicles.StudioPartsCompletion;
            Assert.False(vehicles.StudioPartOverrides.ContainsKey(door));
            ctx.Services.Projects.Redo();
            await vehicles.StudioPartsCompletion;
            Assert.True(vehicles.StudioPartOverrides[door].Hidden);

            // Add a part: pick a model, arm "Add part…", click the roof.
            vehicles.PartMeshSearch = "Jerrycan";
            var mesh = vehicles.PartMeshChoices.FirstOrDefault(p => p.Contains("/SM_", StringComparison.OrdinalIgnoreCase)) ?? vehicles.PartMeshChoices.First();
            output.WriteLine($"adding {mesh}");
            vehicles.IsAddingPart = true;
            var top = Top(model);
            var ray = new SurfaceRay(-1, top + new Vector3(0, 0, 300), -Vector3.UnitZ);
            Assert.NotNull(MeshRay.Hit(model, ray));
            vehicles.OnStudioSurface(-1, ray.Origin, ray.Direction, false, false, false);
            Assert.Equal(history + 1, ctx.Services.Projects.Current!.History.Count); // no model chosen: nothing added, and the panel says why
            Assert.False(string.IsNullOrEmpty(vehicles.StudioPartNote));
            vehicles.SelectedPartMesh = mesh;
            vehicles.IsAddingPart = true;
            vehicles.OnStudioSurface(-1, ray.Origin, ray.Direction, false, false, false);
            output.WriteLine($"after Add: '{vehicles.StudioPartNote}'");
            Assert.Equal(history + 2, ctx.Services.Projects.Current!.History.Count);
            var state = ctx.Services.Projects.Current!.State;
            var added = state.AssetClones.Single(c => c.Kind == VehicleAttachmentEdits.PartKind).NewPrimary;
            output.WriteLine($"added {added}");
            await vehicles.StudioPartsCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.Preview!.Parts.Any(p => p.Attachment == added), TimeSpan.FromSeconds(30)), "the added part is drawn");
            var drawn = vehicles.Preview!.Parts.Where(p => p.Attachment == added).ToList();
            Assert.All(drawn, p => Assert.InRange(p.Transform.Translation.Z, top.Z - 60, top.Z + 60));
            Assert.Equal("BPC_RagerParts", vehicles.SelectedItem!.Name); // the list did not reload under the user

            // The mod: the door is in no spawn preset of the clone, the added part is, and its package is written.
            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            Assert.True(written.PackageExists(added));
            var clone = state.FindCloneOf(vehicles.SelectedItem.PackagePath)!;
            var presets = clone.Packages.Select(p => p.New).Where(p => p.Contains("/SpawningPresets/", StringComparison.OrdinalIgnoreCase) && written.PackageExists(p)).ToList();
            Assert.NotEmpty(presets);
            var spawned = presets.SelectMany(p => VehicleParts.Spawned(ModdableAssets.ReadPackage(written, p))).ToList();
            Assert.Contains(VehicleAttachmentEdits.ClassPath(added), spawned);
            var doorClone = clone.Packages.Single(p => string.Equals(p.Old, model.Parts[door].Attachment, StringComparison.OrdinalIgnoreCase)).New;
            Assert.DoesNotContain(VehicleAttachmentEdits.ClassPath(doorClone), spawned);

            // Remove on the added part takes it off whole (Ctrl+Z puts it back).
            vehicles.OnStudioPick(vehicles.Preview!.Parts.ToList().FindIndex(p => p.Attachment == added));
            vehicles.RemovePartCommand.Execute(null);
            Assert.DoesNotContain(state.AssetClones, c => c.NewPrimary == added);
            await vehicles.StudioPartsCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.Preview!.Parts.Count == stockParts, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaPaksFact]
    public async Task AReplacedTextureShowsOnTheCarAtOnceAndCtrlZTakesItOff()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder! });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Rager textures");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "RagerTex";
            await vehicles.CreateCloneAsync();
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.Preview is not null && vehicles.HasPaints, TimeSpan.FromSeconds(60)));
            vehicles.StudioTab = StudioTab.Textures;
            var outer = vehicles.Paints.Single(p => p.MaterialPath.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));
            vehicles.TextureMaterial = vehicles.TextureMaterials.First(m => m.MaterialPath == outer.MaterialPath);
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.TextureSlots.Count > 0 && vehicles.TextureSlots.All(s => s.HasPicture), TimeSpan.FromSeconds(30)));
            output.WriteLine(string.Join("\n", vehicles.TextureSlots.Select(s => $"{s.Label} | {s.Status} | {s.Info}")));
            Assert.Contains(vehicles.TextureSlots, s => s.Label.StartsWith("Colour", StringComparison.Ordinal));
            Assert.False(string.IsNullOrEmpty(vehicles.TextureMaterialParts));
            var colour = vehicles.TextureSlots.First(s => s.Parameter == "Diffuse Map");
            var stock = colour.TexturePath;

            // A red picture in the colour slot: the body is drawn with it at once.
            var rgba = new byte[256 * 256 * 4];
            for (var i = 0; i < rgba.Length; i += 4)
            {
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (220, 20, 20, 255);
            }

            var red = new TextureImage("red.png", 256, 256, rgba, "PF_R8G8B8A8", 0, 1, 1, true, false);
            Assert.True(await vehicles.ReplaceTextureAsync(outer.MaterialPath, "Diffuse Map", red));
            await vehicles.ReplacedTexturesCompletion;
            bool BodyIsRed()
            {
                var model = vehicles.Preview!;
                var body = model.Parts.Where(p => p.Material == outer.MaterialPath).ToList();
                return body.Count > 0 && body.All(p => p.TexturePath is { } key && key != stock && model.Textures.TryGetValue(key, out var t)
                    && t.Rgba.Length >= 4 && t.Rgba[0] > 150 && t.Rgba[1] < 80);
            }

            Assert.True(HeadlessUi.PumpUntil(BodyIsRed, TimeSpan.FromSeconds(30)), "the replaced texture is drawn on the car");
            output.WriteLine("replaced: the body is drawn with " + string.Join(", ", vehicles.Preview!.Parts.Where(p => p.Material == outer.MaterialPath).Select(p => p.TexturePath).Distinct()));
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.TextureSlots.First(s => s.Parameter == "Diffuse Map").IsReplaced, TimeSpan.FromSeconds(30)));

            // Ctrl+Z: the game's texture again, in the view and in the panel.
            ctx.Services.Projects.Undo();
            await vehicles.ReplacedTexturesCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => !BodyIsRed(), TimeSpan.FromSeconds(30)), "Ctrl+Z takes the picture off the car");
            var after = vehicles.Preview!.Parts.Where(p => p.Material == outer.MaterialPath).Select(p => p.TexturePath ?? string.Empty).Distinct().ToList();
            output.WriteLine("after Ctrl+Z the body is drawn with " + string.Join(", ", after));
            Assert.DoesNotContain(after, k => k.StartsWith("#replaced/", StringComparison.Ordinal));
            Assert.True(HeadlessUi.PumpUntil(() => !vehicles.TextureSlots.First(s => s.Parameter == "Diffuse Map").IsReplaced, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaPaksFact]
    public async Task ShotsOfAReplacedTextureAndADecalStuckAcrossTheSide()
    {
        // Pictures for a look (SCUMSTUDIO_SCREENSHOTS): a checker put in the Rager clone's body colour slot, then a flame decal
        // 4.5 m wide stuck to the surface across the right side (doors, fenders, bed: each takes its piece).
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder! });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Rager shots");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "RagerShot";
            await vehicles.CreateCloneAsync();
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.Preview is not null && vehicles.HasPaints, TimeSpan.FromSeconds(60)));
            var outer = vehicles.Paints.Single(p => p.MaterialPath.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));
            var rgba = new byte[512 * 512 * 4];
            for (var i = 0; i < rgba.Length; i += 4)
            {
                var (x, y) = (i / 4 % 512 / 32, i / 4 / 512 / 32);
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (x + y) % 2 == 0 ? ((byte)230, (byte)200, (byte)20, (byte)255) : ((byte)25, (byte)25, (byte)30, (byte)255);
            }

            Assert.True(await vehicles.ReplaceTextureAsync(outer.MaterialPath, "Diffuse Map", new TextureImage("checker.png", 512, 512, rgba, "PF_R8G8B8A8", 0, 1, 1, true, false)));
            await vehicles.ReplacedTexturesCompletion;
            HeadlessUi.PumpUntil(() => false, TimeSpan.FromSeconds(1));
            await ScumStudio.Tests.Viewport.VehicleStudioShotsRealTests.ShotAsync(vehicles.Preview!, "rager-texture-replaced.png");
            ctx.Services.Projects.Undo();
            await vehicles.ReplacedTexturesCompletion;

            vehicles.StudioTab = StudioTab.Decals;
            var model = vehicles.Preview!;
            var top = Top(model);
            var ray = Enumerable.Range(0, 40).Select(k => new SurfaceRay(-1, new Vector3(top.X + ((k % 5) - 2) * 30, 400, top.Z - 40 - (k / 5 * 12)), -Vector3.UnitY))
                .First(r => MeshRay.Hit(model, r) is { } hit && model.Parts[hit.PartIndex].Material == outer.MaterialPath);
            vehicles.ArmDecalCommand.Execute(vehicles.DecalDesigns.Single(d => d.Design.Id == "flames-1"));
            Assert.True(vehicles.PlaceDecal(ray));
            vehicles.SelectedDecal!.Wrap = true;
            vehicles.SelectedDecal!.Size = 450;
            Assert.True(HeadlessUi.PumpUntil(() => !vehicles.DecalPreviewBusy, TimeSpan.FromSeconds(60)));
            HeadlessUi.PumpUntil(() => false, TimeSpan.FromSeconds(2));
            Assert.True(HeadlessUi.PumpUntil(() => !vehicles.DecalPreviewBusy, TimeSpan.FromSeconds(60)));
            var materials = vehicles.Preview!.Parts.Where(p => p.TexturePath?.StartsWith("#paint/", StringComparison.Ordinal) == true).Select(p => p.Material).Distinct().ToList();
            output.WriteLine("decalled materials: " + string.Join(", ", materials));
            await ScumStudio.Tests.Viewport.VehicleStudioShotsRealTests.ShotAsync(vehicles.Preview!, "rager-wrap-decal.png", -60f);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>The middle of the vehicle's top (UE space): above its centre, at its highest point.</summary>
    private static Vector3 Top(PreviewModel model)
    {
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var part in model.Parts)
        {
            for (var i = 0; i < part.Mesh.Positions.Length; i += 3)
            {
                var p = part.Transform.TransformPosition(new ScumStudio.Core.Mathematics.FVector(part.Mesh.Positions[i], part.Mesh.Positions[i + 1], part.Mesh.Positions[i + 2]));
                var v = new Vector3(p.X, p.Y, p.Z);
                (min, max) = (Vector3.Min(min, v), Vector3.Max(max, v));
            }
        }

        return new Vector3((min.X + max.X) / 2, (min.Y + max.Y) / 2, max.Z);
    }
}
