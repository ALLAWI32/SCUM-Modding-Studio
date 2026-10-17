using System.Numerics;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Textures;
using ScumStudio.Pak;
using ScumStudio.Pak.Writing;
using ScumStudio.Viewport.Decals;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Report (3.4.0, project "ServerMods"): "I have only cloned 2 vehicles and added stickers. It won't export": the package
/// check stopped the export because <c>BP_Prisoner</c> imports <c>PP_LowLightVision_Inst</c>, which neither the game nor the
/// mod has. On the real game files (<c>SCUM_PAKS</c>, key from this PC's store; nothing of the game is written into the
/// repository): two vehicle clones with stickers export both roles (the server role cooked from the client paks: this PC
/// has no SCUMServer) and never stage the prisoner. With an imported mod whose <c>BP_Prisoner</c> is a cook from before
/// the game's update that removed <c>PP_LowLightVision_Inst</c>, and game files that had not read that mod (as after a
/// re-read that failed), 3.4.0 stopped with the report's two lines; now the export packs, says which mod is out of date,
/// and the same import in a package the export writes itself still stops it. How the out-of-date mod behaves in a running
/// game is not checked here.
/// </summary>
public sealed class VehicleStickersExportRealTests(ITestOutputHelper output)
{
    private const string Prisoner = "/Game/ConZ_Files/Characters/Prisoner/Blueprints/BP_Prisoner";
    private const string LowLight = "/Game/ConZ_Files/Materials/Postprocessing/PP_LowLightVision_Inst";
    private const string NightVision = "/Game/ConZ_Files/Materials/Postprocessing/MI_NightVision";

    [AvaloniaPaksFact]
    public async Task TwoVehicleClonesWithStickersExportBothRolesWithoutThePrisoner()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder! });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "ServerMods");
            var catalog = ctx.Services.Workspace.Catalog!;
            Assert.False(catalog.PackageExists(LowLight), "the game of 2026-10-17 no longer has the low-light post process");

            await CloneWithStickersAsync(vm, ["BPC_Rager", "BPC_Laika", "BPC_WolfsWagen", "BPC_Kinglet_Duster"]);
            Assert.Contains(project.State.AssetValueOverrides, o => TextureWrites.IsEdit(o.Path));

            foreach (var result in await ExportBothRolesAsync(ctx, project))
            {
                output.WriteLine($"{result.Role}: {result.Validation?.Summary}; {result.Assets.Count} asset package(s)");
                Assert.DoesNotContain(result.Assets, a => a.PackagePath.Contains("/Characters/Prisoner/", StringComparison.OrdinalIgnoreCase));
                Assert.False(File.Exists(Path.Combine(result.StagingDirectory, "SCUM", "Content", "ConZ_Files", "Characters", "Prisoner", "Blueprints", "BP_Prisoner.uasset")));
                Assert.True(result.Validation!.Passed, string.Join('\n', result.Validation.Problems));
            }
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>
    /// Review 3.5.0 (blocker, the same report on a real dedicated server, <c>SCUM_SERVER_PAKS</c>): the server's game files
    /// keep every texture without pixels (a dedicated server never draws), so writing the stickers' pixels into the server's
    /// copy failed, the sticker textures were left out of the server pak, and the package check refused it for the cloned
    /// materials that import them ("Server pak not built ... 8 problem(s)"). Now the app's export with the server pak and the
    /// Vehicles page's own export write a server pak for every client pak, each passing the check, with the sticker textures
    /// written as the server's own pixelless copies.
    /// </summary>
    [AvaloniaPaksFact]
    public async Task TwoVehicleClonesWithStickersGetTheirServerPaksOnARealServer()
    {
        if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is not { Length: > 0 } server || !Directory.Exists(server))
        {
            return; // needs a dedicated server's paks
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder!, ServerPaksFolder = server });
        var warnings = new List<string>();
        ctx.Services.Notifications.Toasts.CollectionChanged += (_, e) =>
            warnings.AddRange((e.NewItems ?? Array.Empty<object>()).OfType<ToastViewModel>().Where(t => t.IsWarning || t.IsError).Select(t => $"{t.Title}: {t.Message}"));
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "ServerMods");
            await CloneWithStickersAsync(vm, ["BPC_Rager", "BPC_Laika"]);
            var stickers = project.State.AssetValueOverrides.Where(o => TextureWrites.IsEdit(o.Path)).Select(o => o.Package).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.NotEmpty(stickers);

            var whole = await ModExportService.ExportAsync(ctx.Services, new ModExportRequest(project, ctx.Combine("out"), null, IncludeServer: true), ProgressSink.Null);
            var page = await ModExportService.ExportModuleAsync(ctx.Services, project, ExportModule.Vehicles, ctx.Combine("pages"), includeServer: true, ProgressSink.Null);
            foreach (var r in whole.Concat(page))
            {
                output.WriteLine($"{r.Role} {Path.GetFileName(r.PakPath)}: {r.Validation?.Summary ?? "not checked"}");
            }

            foreach (var w in warnings)
            {
                output.WriteLine("warning: " + w);
            }

            foreach (var results in new[] { whole, page })
            {
                var clientPaks = results.Where(r => r.Role == ProjectSourceRole.Client).Select(r => Path.GetFileName(r.PakPath)).ToList();
                Assert.Equal(clientPaks, results.Where(r => r.Role == ProjectSourceRole.Server).Select(r => Path.GetFileName(r.PakPath)));
                Assert.All(results, r => Assert.True(r.Validation is { Passed: true } && File.Exists(r.PakPath), $"{r.Role} {Path.GetFileName(r.PakPath)}: {string.Join('\n', r.Validation?.Problems ?? ["not checked"])}"));
            }

            Assert.DoesNotContain(warnings, w => w.Contains("Server pak", StringComparison.OrdinalIgnoreCase) || w.Contains("texture not written", StringComparison.Ordinal));

            // The server's sticker textures: its own copies, without pixels; the client's have them.
            var serverMain = whole.First(r => r.Role == ProjectSourceRole.Server);
            var clientMain = whole.First(r => r.Role == ProjectSourceRole.Client);
            Assert.DoesNotContain(serverMain.Warnings, w => w.Contains("texture not written", StringComparison.Ordinal));
            foreach (var texture in stickers)
            {
                CookedPackage Staged(ExportResult result)
                {
                    var asset = result.Assets.Single(a => string.Equals(a.PackagePath, texture, StringComparison.OrdinalIgnoreCase));
                    var file = Path.Combine([result.StagingDirectory, .. asset.VirtualPath[..asset.VirtualPath.LastIndexOf('.')].Split('/')]);
                    var ubulk = File.Exists(file + ".ubulk") ? File.ReadAllBytes(file + ".ubulk") : null;
                    return CookedPackage.Parse(File.ReadAllBytes(file + ".uasset"), File.ReadAllBytes(file + ".uexp"), ubulk, texture);
                }

                Assert.True(TextureWriter.IsPixelless(Staged(serverMain)), texture);
                Assert.False(TextureWriter.IsPixelless(Staged(clientMain)), texture);
            }
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaPaksFact]
    public async Task AnImportedModWithAnOutdatedPrisonerStillExportsAndIsNamed()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder! });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "ServerMods");
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.Current is not null, TimeSpan.FromSeconds(30)));

            // Someone's mod pak with the prisoner as the game cooked it before its update: the night vision import is the
            // low-light post process the game no longer ships (the rest is the game's own prisoner).
            var catalog = ctx.Services.Workspace.Catalog!;
            var pak = await OutdatedPrisonerModAsync(catalog, ctx.Combine("mod-staging"), ctx.Combine("pakchunk99-PrisonerTweaks_P.pak"));

            // Not this mod: the same prisoner written by an export (not someone's mod) is a broken import the check still stops.
            var check = ModPackageValidator.Validate(ctx.Combine("mod-staging"), catalog);
            Assert.Equal(2, check.Problems.Count);
            Assert.All(check.Problems, p => Assert.Contains($"{Prisoner}: imports {LowLight}", p, StringComparison.Ordinal));
            Assert.All(check.Problems, p => Assert.Contains($"neither the game nor the mod has the package {LowLight} (the game would get null there).", p, StringComparison.Ordinal));

            // Imported into the project while the open game files do not read it (as after a re-read that failed or was
            // cancelled: the workspace kept the game files it had), then the two cars and their stickers.
            var key = AesKeyText.FromEnvironmentOrStore();
            var (folder, _) = await ProjectMods.ImportAsync(project.DirectoryPath, pak, key);
            Assert.DoesNotContain(folder, ctx.Services.Workspace.ModFolders);
            await CloneWithStickersAsync(vm, ["BPC_Rager", "BPC_Laika"]);

            // 3.4.0 stopped here with the report's two lines (BP_Prisoner imports PP_LowLightVision_Inst[.PP_LowLightVision_Inst]).
            var results = await ExportBothRolesAsync(ctx, project);
            Assert.Contains(folder, ctx.Services.Workspace.ModFolders);
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Notifications.Toasts.Any(t => t.Title == "An imported mod is out of date"), TimeSpan.FromSeconds(10)));
            foreach (var result in results)
            {
                output.WriteLine($"{result.Role}: {result.Validation?.Summary}");
                foreach (var warning in result.Warnings)
                {
                    output.WriteLine("  " + warning);
                }

                Assert.True(result.Validation!.Passed, string.Join('\n', result.Validation.Problems));
                var staged = Path.Combine(result.StagingDirectory, "SCUM", "Content", "ConZ_Files", "Characters", "Prisoner", "Blueprints", "BP_Prisoner.uasset");
                Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(folder, "SCUM", "Content", "ConZ_Files", "Characters", "Prisoner", "Blueprints", "BP_Prisoner.uasset")), await File.ReadAllBytesAsync(staged));
                Assert.Single(result.Warnings, w => w.Contains("PrisonerTweaks", StringComparison.Ordinal) && w.Contains(LowLight, StringComparison.Ordinal));
            }
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>The game's prisoner with its night vision import renamed to the removed low-light post process, packed as a mod.</summary>
    private static async Task<string> OutdatedPrisonerModAsync(AssetCatalog catalog, string staging, string pakPath)
    {
        Assert.True(catalog.TryGetPackageFile(Prisoner, out var file));
        var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(catalog, file);
        var stock = CookedPackage.Parse(uasset, uexp, ubulk, Prisoner);
        Assert.Contains(stock.Names, n => string.Equals(n, NightVision, StringComparison.Ordinal));
        var old = PackageCloner.Clone(stock, Prisoner, new PackageMap([new(Prisoner, Prisoner), new(NightVision, LowLight)]));
        Assert.Contains(CookedPackage.Parse(old.Bytes.UAsset, old.Bytes.UExp, null, Prisoner).Names, n => string.Equals(n, LowLight, StringComparison.Ordinal));
        await old.Bytes.WriteAsync(Path.Combine(staging, "SCUM", "Content", "ConZ_Files", "Characters", "Prisoner", "Blueprints", "BP_Prisoner"));
        await new PakWriter().WriteFromDirectoryAsync(staging, pakPath, null, CancellationToken.None);
        return pakPath;
    }

    /// <summary>Clones each vehicle on the Vehicles page and applies a sticker on its painted body, as a user does.</summary>
    private async Task CloneWithStickersAsync(MainWindowViewModel vm, IReadOnlyList<string> templates)
    {
        var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
        await vehicles.LoadCompletion;
        output.WriteLine("vehicles: " + string.Join(", ", vehicles.AllItems.Select(i => i.Name)));
        foreach (var template in templates)
        {
            Assert.True(await vehicles.SelectAsync(template), template);
            var name = template[4..] + "Stk";
            vehicles.CloneName = name;
            await vehicles.CreateCloneAsync();
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints && vehicles.Preview is not null && vehicles.Decals.CloneLeaf == "BPC_" + name, TimeSpan.FromSeconds(90)), template);
            vehicles.StudioTab = StudioTab.Decals;
            vehicles.ArmDecalCommand.Execute(vehicles.DecalDesigns[0]);
            var model = vehicles.Preview!;
            var placed = false;
            string[] painted = ["Outer", "Body", "Outside", "Paint", "Fuselage", "Wing", "Exterior"];
            var rays = Enumerable.Range(0, 9).SelectMany(zi => Enumerable.Range(0, 25).Select(xi => new SurfaceRay(-1, new Vector3(-300 + (xi * 25), 800, 40 + (zi * 20)), -Vector3.UnitY)))
                .Concat(Enumerable.Range(0, 25).SelectMany(yi => Enumerable.Range(0, 25).Select(xi => new SurfaceRay(-1, new Vector3(-300 + (xi * 25), -300 + (yi * 25), 800), -Vector3.UnitZ))));
            foreach (var ray in rays)
            {
                if (!placed && MeshRay.Hit(model, ray) is { } hit && model.Parts[hit.PartIndex].Material is var material
                    && painted.Any(p => material.Contains(p, StringComparison.OrdinalIgnoreCase)) && vehicles.PlaceDecal(ray))
                {
                    placed = true;
                    output.WriteLine($"{template}: sticker on {model.Parts[hit.PartIndex].Name} ({material})");
                }
            }

            Assert.True(placed, $"{template}: no painted body found for the sticker ({string.Join(", ", model.Parts.Select(p => p.Material).Distinct().Take(12))})");
            await vehicles.ApplyDecalsCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// The client pak as the app exports it, then the server role cooked from the client paks (the cook from a real dedicated
    /// server's files: <see cref="TwoVehicleClonesWithStickersGetTheirServerPaksOnARealServer"/>).
    /// </summary>
    private static async Task<IReadOnlyList<ExportResult>> ExportBothRolesAsync(AppTestContext ctx, ScumStudio.Level.Projects.Project project)
    {
        var client = await ModExportService.ExportAsync(ctx.Services, new ModExportRequest(project, ctx.Combine("out"), null, false), ProgressSink.Null);
        var catalog = ctx.Services.Workspace.Catalog!;
        var server = await new ProjectExporter().ExportAsync(project, catalog,
            new ExportOptions { OutputDirectory = ctx.Combine("out-server"), WritePak = false, Mods = ProjectMods.Folders(project.DirectoryPath), ClientCatalog = catalog }, ProjectSourceRole.Server);
        return [client[0], server];
    }
}
