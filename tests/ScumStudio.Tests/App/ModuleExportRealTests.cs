using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Registry;
using ScumStudio.Pak;
using ScumStudio.Pak.Reading;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Real game files: each page as its own mod (owner 2026-10-17). The game reads one <c>AssetRegistry.bin</c>, the highest
/// pak's; a Vehicles pak, a Weapons pak and a Craftables pak installed together keep every clone registered through the
/// Registry pak, each page pak passes the package check, holds only its page's packages and works alone. And the whole
/// project's Craftables pak, whose registry keeps the project pak's clones, passes the check again.
/// </summary>
public sealed class ModuleExportRealTests(ITestOutputHelper output)
{
    private const string Rager = "/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager";
    private const string Ak47 = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";
    private const string Table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
    private const string Name = "Modular";

    [PaksFact]
    public async Task PagePaksInstalledTogetherKeepEveryCloneRegistered()
    {
        using var catalog = OpenGame();
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Modular.ssproj"), Name);
        var rager = Clone(project, "vehicle", Rager, CloneFamilyPlanner.PlanVehicle(catalog, Rager, "ModRager"));
        var ak = Clone(project, "weapon", Ak47, CloneFamilyPlanner.PlanItem(catalog, Ak47, "Weapon_AK47_Mod"));

        // Review 3.5.0: the Weapons page lightens the game's AK-47, and the craftable asks for one, giving it a tag (its entity
        // setup is in the Craftables pak too).
        project.Apply(new SetAssetValueOp(Ak47 + "_ES", "Default__Weapon_AK47_ES_C", "Weight", "Float", "3500", "123.5"));
        var craftables = Craftables(catalog, project, [new CraftIngredient(string.Empty, 1, Item: Ak47, AddTag: true)]);

        var options = new ExportOptions { OutputDirectory = temp.Combine("out"), ProjectDirectory = project.DirectoryPath, LeaveOutEconomy = true };
        var exporter = new ProjectExporter();
        var paks = new Dictionary<ExportModule, ExportResult>();
        foreach (var module in new[] { ExportModule.Vehicles, ExportModule.Weapons, ExportModule.Craftables })
        {
            var own = options with { Module = module, Elsewhere = ModuleExport.Elsewhere(project.State, module, Name, options.PakChunkIndex) };
            var result = module == ExportModule.Craftables
                ? await CraftablesExporter.ExportAsync(craftables, Name, catalog, own, ProjectSourceRole.Client)
                : await exporter.ExportAsync(ModuleExport.StateOf(project.State, module), Name, catalog, own, ProjectSourceRole.Client);
            Assert.NotNull(result);
            paks[module] = result;
            Assert.Empty(ModuleExport.HiddenBy(ProjectExporter.ExportFolder(options.OutputDirectory, Name, pages: true, ProjectSourceRole.Client), Name, module, catalog.ProjectName));

            // Every page pak: its own name and chunk, a .sig, and the package check passed.
            Assert.Equal(ModuleExport.PakFileName(Name, module, options.PakChunkIndex), Path.GetFileName(result.PakPath));
            Assert.True(File.Exists(result.PakPath) && File.Exists(result.SigPath), module.ToString());
            Assert.True(result.Validation is { Passed: true }, string.Join('\n', result.Validation?.Problems ?? ["not checked"]));

            // The Registry pak is written again after every page export, and checked like every pak; the first page pak alone
            // carries its own records, so none is written for it (owner, 3.5.0: one page gave a second 48 MB pak).
            var registryPak = await ModuleExport.WriteRegistryPakAsync(Name, catalog, own, ProjectSourceRole.Client, state: project.State);
            if (paks.Count == 1)
            {
                Assert.Null(registryPak);
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(result.PakPath)!, ModuleExport.RegistryPakFileName(Name, options.PakChunkIndex))));
                continue;
            }

            Assert.NotNull(registryPak);
            Assert.True(registryPak.Validation is { Passed: true }, string.Join('\n', registryPak.Validation?.Problems ?? ["not checked"]));
            Assert.True(File.Exists(registryPak.PakPath) && File.Exists(registryPak.SigPath));
        }

        // Each page pak holds only its page's packages, and its own registry carries its own records (it works alone). They sit
        // in the project's "- pages" folder with nothing else: the paks, their signatures, the reports (and the hidden work folder).
        var client = ProjectExporter.ExportFolder(options.OutputDirectory, Name, pages: true, ProjectSourceRole.Client);
        Assert.All(Directory.GetFileSystemEntries(client), entry => Assert.True(
            Path.GetExtension(entry) is ".pak" or ".sig" or ".md" || Path.GetFileName(entry) == ProjectExporter.WorkFolderName, entry));
        Assert.True(new DirectoryInfo(Path.Combine(client, ProjectExporter.WorkFolderName)).Attributes.HasFlag(FileAttributes.Hidden));
        Assert.False(Directory.Exists(Path.Combine(options.OutputDirectory, ProjectExporter.ClientFolderName)), "nothing loose under the output folder");
        var contents = paks.ToDictionary(p => p.Key, p => ModuleExport.ReadPak(p.Value.PakPath!, catalog.ProjectName));
        Assert.Contains(rager.NewPrimary, contents[ExportModule.Vehicles].Packages);
        Assert.DoesNotContain(ak.NewPrimary, contents[ExportModule.Vehicles].Packages);
        Assert.Contains(ak.NewPrimary, contents[ExportModule.Weapons].Packages);
        Assert.DoesNotContain(rager.NewPrimary, contents[ExportModule.Weapons].Packages);
        Assert.DoesNotContain(contents[ExportModule.Craftables].Packages, p => p.Equals(rager.NewPrimary, StringComparison.OrdinalIgnoreCase) || p.Equals(ak.NewPrimary, StringComparison.OrdinalIgnoreCase));
        foreach (var (module, result) in paks)
        {
            Assert.NotEmpty(result.Registered);
            var own = contents[module].Registry!.Assets.Select(a => a.ObjectPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(result.Registered, r => Assert.Contains(r.ObjectPath, own));
        }

        // Installed together: the registry the game reads holds every record of the game and every clone of every page pak.
        var stock = AssetRegistryFile.Parse(RegistryMerge.ReadStock(catalog)!).Assets.Select(a => a.ObjectPath).ToList();
        var everyClone = paks.Values.SelectMany(r => r.Registered).Select(r => r.ObjectPath).ToList();
        var installed = Directory.GetFiles(client, "*.pak");
        Assert.Equal(4, installed.Length);
        var (winner, read) = InstalledRegistry(installed, catalog.ProjectName);
        Assert.Equal(ModuleExport.RegistryPakFileName(Name, options.PakChunkIndex), winner);
        var records = read.Assets.Select(a => a.ObjectPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(everyClone, path => Assert.Contains(path, records));
        var lost = stock.Where(path => !records.Contains(path)).ToList();
        Assert.True(lost.Count == 0, $"{lost.Count} record(s) of the game lost, e.g. {string.Join(", ", lost.Take(3))}");

        // Without the Registry pak the highest page pak's registry wins, and the other pages' clones are not in it: what the
        // Registry pak is for.
        var (alone, lonely) = InstalledRegistry(installed.Where(p => !p.EndsWith(winner, StringComparison.OrdinalIgnoreCase)), catalog.ProjectName);
        Assert.Equal(ModuleExport.PakFileName(Name, ExportModule.Craftables, options.PakChunkIndex), alone);
        Assert.DoesNotContain(paks[ExportModule.Vehicles].Registered[0].ObjectPath, lonely.Assets.Select(a => a.ObjectPath), StringComparer.OrdinalIgnoreCase);

        // Review 3.5.0: the Vehicles and the Weapons pak each sell their clone in their own copy of the traders' table; the copy
        // the game reads (the highest pak's, the Registry pak's) sells both.
        const string trade = ScumStudio.Modding.Tuning.DataTableEdits.TradeableTable;
        Assert.All(new[] { ExportModule.Vehicles, ExportModule.Weapons }, m => Assert.Contains(trade, contents[m].Packages));
        var (seller, table) = InstalledPackage(installed, catalog.ProjectName, trade);
        Assert.Equal(winner, seller);
        var rows = ScumStudio.Formats.Properties.DataTableRows.Read(table, Enumerable.Range(0, table.Exports.Count).First(i => table.GetExportClassName(i) == "DataTable"))
            .Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(PackageMap.Leaf(rager.NewPrimary) + "_C", rows);
        Assert.Contains(PackageMap.Leaf(ak.NewPrimary) + "_C", rows);

        // The Craftables pak's copy of the AK-47's entity setup (its tag) is built on the Weapons pak's: the lighter gun stays.
        var (setupPak, setup) = InstalledPackage(installed, catalog.ProjectName, Ak47 + "_ES");
        Assert.Equal(ModuleExport.PakFileName(Name, ExportModule.Craftables, options.PakChunkIndex), setupPak);
        var weights = ScumStudio.Modding.Tuning.TunableReader.Read(setup).Where(v => v.Path == "Weight").Select(v => v.Value).ToList();
        Assert.True(weights.Contains("123.5"), "weights: " + string.Join(", ", weights));

        // The Weapons page exported again after it: the Craftables pak above now hides its newer copy, and the export says so.
        var again = await exporter.ExportAsync(ModuleExport.StateOf(project.State, ExportModule.Weapons), Name, catalog,
            options with { Module = ExportModule.Weapons, Elsewhere = ModuleExport.Elsewhere(project.State, ExportModule.Weapons, Name, options.PakChunkIndex) }, ProjectSourceRole.Client);
        Assert.True(again.Validation is { Passed: true });
        Assert.Contains(ModuleExport.HiddenBy(client, Name, ExportModule.Weapons, catalog.ProjectName),
            h => h.Above == ExportModule.Craftables && h.Example.Equals(Ak47 + "_ES", StringComparison.OrdinalIgnoreCase));

        // The reports' Install section names the Registry pak.
        Assert.Contains(winner, await File.ReadAllTextAsync(Path.Combine(client, ModuleExport.ReportFileName(ExportModule.Vehicles))), StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole project's export: the Craftables pak's registry keeps the project pak's records (its clones), and the package
    /// check named them missing, so a project with a vehicle clone and a craftable could not be exported (3.4.0). The check now
    /// finds their packages in the project pak and names that pak.
    /// </summary>
    [PaksFact]
    public async Task TheWholeProjectsCraftablesPakPassesTheCheckWithTheProjectsClones()
    {
        using var catalog = OpenGame();
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Whole.ssproj"), "Whole");
        Clone(project, "vehicle", Rager, CloneFamilyPlanner.PlanVehicle(catalog, Rager, "WholeRager"));
        var craftables = Craftables(catalog, project);
        var options = new ExportOptions { OutputDirectory = temp.Combine("out"), ProjectDirectory = project.DirectoryPath, LeaveOutEconomy = true };
        var main = await new ProjectExporter().ExportAsync(project, catalog, options, ProjectSourceRole.Client);
        Assert.True(main.Validation is { Passed: true });

        var refused = await Assert.ThrowsAsync<ModValidationException>(() =>
            CraftablesExporter.ExportAsync(craftables, "Whole", catalog, options, ProjectSourceRole.Client, CraftablesExporter.StagedRegistry(main)));
        Assert.Contains(refused.Result.Problems, p => p.Contains("WholeRager", StringComparison.Ordinal));

        var crafted = await CraftablesExporter.ExportAsync(craftables, "Whole", catalog, options with { Elsewhere = CraftablesExporter.ProjectPakPackages(main, catalog) },
            ProjectSourceRole.Client, CraftablesExporter.StagedRegistry(main));
        Assert.NotNull(crafted);
        Assert.True(crafted.Validation is { Passed: true }, string.Join('\n', crafted.Validation?.Problems ?? []));
        Assert.Equal([Path.GetFileName(main.PakPath)!], crafted.Validation!.Needs);
        Assert.True(File.Exists(crafted.PakPath) && File.Exists(crafted.SigPath));
    }

    /// <summary>The Vehicles page's Export button as the app runs it: the page pak and the Registry pak, both checked, with the install rule.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheVehiclesPagesExportButtonWritesItsOwnPak()
    {
        if (RealPaks.Folder is not { } paks)
        {
            return; // needs the real game files
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks, ClientModsOutputFolder = ctx.Combine("exports") });
        await ctx.Services.Workspace.ConnectAsync(ScumStudio.Core.Abstractions.ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Garage");
        Clone(project, "vehicle", Rager, CloneFamilyPlanner.PlanVehicle(ctx.Services.Workspace.Catalog!, Rager, "GarageRager"));
        Assert.Null(ModExportService.ModuleBlocker(ctx.Services, project, ExportModule.Vehicles));
        Assert.NotNull(ModExportService.ModuleBlocker(ctx.Services, project, ExportModule.Weapons)); // nothing on that page

        var (results, _) = await ModExportService.QuickExportModuleAsync(ctx.Services, project, ExportModule.Vehicles, ctx.Combine("unused"), ScumStudio.Core.Abstractions.ProgressSink.Null);
        // One page pak alone needs no Registry pak (its own registry carries its records); it sits in the project's "- pages" folder.
        Assert.Equal(["pakchunk912-Garage-Vehicles_P.pak"], results.Select(r => Path.GetFileName(r.PakPath)));
        Assert.All(results, r => Assert.True(r.Validation is { Passed: true } && File.Exists(r.SigPath), r.ModName));
        Assert.Equal(ProjectExporter.ExportFolder(ctx.Combine("exports"), "Garage", pages: true, ProjectSourceRole.Client), Path.GetDirectoryName(results[0].PakPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(results[0].PakPath)!, "pakchunk919-Garage-Registry_P.pak")));
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(results[0].PakPath)!), f => f.EndsWith("EconomyOverride.json", StringComparison.OrdinalIgnoreCase));

        var text = ModuleExportViewModel.Describe(results, []);
        Assert.Contains("pakchunk912-Garage-Vehicles_P.pak", text, StringComparison.Ordinal);
        Assert.Contains(ScumStudio.App.Localization.Loc.T("Export.Module.Install"), text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every page with a pak exported on its own as the app's Export buttons run it (Spawns, Map, Vehicles, Weapons, Items,
    /// Craftables, each followed by the Registry pak), then the whole project in one export (its pak and its Craftables pak):
    /// every pak written passes the package check and has its .sig.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task EveryPagesPakAndTheWholeExportPassTheCheck()
    {
        if (RealPaks.Folder is not { } paks)
        {
            return; // needs the real game files
        }

        await ExportEveryPageAsync(paks, null);
    }

    /// <summary>
    /// The same project with a dedicated server's paks (<c>SCUM_SERVER_PAKS</c>, a server's <c>SCUM\Content\Paks</c>): the
    /// whole export and every page's export write a server pak for every client pak, and every pak of both roles passes the
    /// package check. The app only warns about a server pak the check refuses (the client paks are written by then), so the
    /// test names each role's paks and lists the warnings.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task EveryPagesPakAndTheWholeExportPassTheCheckOnTheServerToo()
    {
        if (RealPaks.Folder is not { } paks || Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is not { Length: > 0 } server || !Directory.Exists(server))
        {
            return; // needs the real game files and a dedicated server's paks
        }

        await ExportEveryPageAsync(paks, server);
    }

    private async Task ExportEveryPageAsync(string paks, string? serverPaks)
    {
        const string farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
        const string coins = "/Game/ConZ_Files/Items/Equipment/Active_Items/Copper_Coins";
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks, ServerPaksFolder = serverPaks });
        var warnings = new List<string>();
        ctx.Services.Notifications.Toasts.CollectionChanged += (_, e) =>
            warnings.AddRange((e.NewItems ?? Array.Empty<object>()).OfType<ScumStudio.App.ViewModels.ToastViewModel>()
                .Where(t => t.IsWarning || t.IsError).Select(t => $"{t.Title}: {t.Message}"));
        await ctx.Services.Workspace.ConnectAsync(ScumStudio.Core.Abstractions.ProgressSink.Null);
        var catalog = ctx.Services.Workspace.Catalog!;
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "AllPages");

        // Map: an object of the farm deleted and another one duplicated 5 m away.
        var objects = ScumStudio.Level.Model.LevelDocument.Load(new ScumStudio.Level.Reading.Cue4ParseLevelReader(catalog), farm).Actors
            .Where(a => a.Kind == ScumStudio.Level.Model.ActorKind.StaticMeshActor && a.Root is not null).Take(2).ToList();
        project.Apply(new DeleteActorOp(new ActorRef(farm, objects[0].Name)));
        var copied = objects[1].Root!.Relative;
        project.Apply(new DuplicateActorOp(new ActorRef(farm, objects[1].Name), objects[1].Name + "_Copy",
            copied with { Location = copied.Location + new ScumStudio.Core.Mathematics.FVector(500, 0, 0) }));

        // Spawns: a vehicle spawn point of the island moved 10 m east.
        var car = ScumStudio.Level.Spawns.SpawnPlaces.ReadFrom(catalog).Where(p => p.Kind == ScumStudio.Level.Spawns.SpawnPlaceKind.Vehicle).ElementAt(100);
        var at = car.Transform.Translation;
        project.Apply(ScumStudio.Level.Spawns.SpawnPlaceMoves.Move(car, project.State, at.X + 1000f, at.Y, at.Z)!);

        // Vehicles, Weapons, Items: a clone each; Craftables: the armoury table.
        Clone(project, "vehicle", Rager, CloneFamilyPlanner.PlanVehicle(catalog, Rager, "AllRager"));
        Clone(project, "weapon", Ak47, CloneFamilyPlanner.PlanItem(catalog, Ak47, "Weapon_AK47_All"));
        Clone(project, "item", coins, CloneFamilyPlanner.PlanItem(catalog, coins, "All_Coins"));
        Craftables(catalog, project);

        // The whole project in one export, then every page on its own; a refused pak is listed, not the end of the run.
        var written = new List<ExportResult>();
        var refused = new List<string>();
        async Task Export(string what, Func<Task<ModExportResults>> export)
        {
            try
            {
                var results = await export();
                written.AddRange(results);
                output.WriteLine($"{what}: {string.Join(", ", results.Select(r => Path.GetFileName(r.PakPath)))}");
                if (results.ServerRefused is { } server)
                {
                    refused.Add($"{what} (server): {server.Result.Problems.Count} problem(s), e.g. {string.Join(" | ", server.Result.Problems.Take(3))}");
                }
            }
            catch (ModValidationException ex)
            {
                refused.Add($"{what}: {ex.Result.Problems.Count} problem(s), e.g. {string.Join(" | ", ex.Result.Problems.Take(3))}");
            }
        }

        var withServer = serverPaks is not null;
        await Export("whole project", () => ModExportService.ExportAsync(ctx.Services, new ModExportRequest(project, ctx.Combine("whole"), null, IncludeServer: withServer),
            ScumStudio.Core.Abstractions.ProgressSink.Null));
        foreach (var module in ModuleExport.PakModules)
        {
            Assert.Null(ModExportService.ModuleBlocker(ctx.Services, project, module));
            await Export(module.ToString(), () => ModExportService.ExportModuleAsync(ctx.Services, project, module, ctx.Combine("pages"), includeServer: withServer,
                ScumStudio.Core.Abstractions.ProgressSink.Null));
        }

        foreach (var r in written)
        {
            output.WriteLine($"{r.Role} {Path.GetFileName(r.PakPath)}: {r.Validation?.Summary ?? "not checked"}");
        }

        foreach (var w in warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.True(refused.Count == 0, string.Join('\n', refused));
        if (withServer)
        {
            // Every client pak has its server pak (same name, in the server folder): none was left out with a warning.
            var clientPaks = written.Where(r => r.Role == ProjectSourceRole.Client).Select(r => Path.GetFileName(r.PakPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var serverWritten = written.Where(r => r.Role == ProjectSourceRole.Server).Select(r => Path.GetFileName(r.PakPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = clientPaks.Where(p => !serverWritten.Contains(p)).ToList();
            Assert.True(missing.Count == 0, $"no server pak for {string.Join(", ", missing)}; warnings: {string.Join(" | ", warnings)}");
            Assert.Contains(written, r => r.Role == ProjectSourceRole.Server && r.Validation is { Passed: true });
        }

        var paksWritten = written.Select(r => Path.GetFileName(r.PakPath)).ToList();
        Assert.Contains("pakchunk900-AllPages_P.pak", paksWritten);
        Assert.Contains("pakchunk901-AllPagesCraftables_P.pak", paksWritten);
        Assert.All(ModuleExport.PakModules, m => Assert.Contains(ModuleExport.PakFileName("AllPages", m, ProjectExporter.DefaultPakChunkIndex), paksWritten));
        Assert.Contains(ModuleExport.RegistryPakFileName("AllPages", ProjectExporter.DefaultPakChunkIndex), paksWritten);
        Assert.All(written, r => Assert.True(r.Validation is { Passed: true } && File.Exists(r.PakPath) && File.Exists(r.SigPath),
            $"{Path.GetFileName(r.PakPath)}: {string.Join('\n', r.Validation?.Problems ?? ["not checked"])}"));
    }

    private static AssetCatalog OpenGame() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions
    {
        AesKey = AesKeyText.FromEnvironmentOrStore(),
        PakFileFilter = OwnPaks.Filter(OwnPaks.ProjectNames(Name)), // never this PC's installed studio paks
    });

    private static ClonePlan Clone(Project project, string kind, string template, ClonePlan plan)
    {
        project.Apply(new CloneAssetOp(kind, template, plan.NewPrimary, plan.Packages.Select(p => new PackagePair(p.Key, p.Value)).ToList()));
        return plan;
    }

    /// <summary>The armoury table as a craftable of the project, as the page adds it (with <paramref name="ingredients"/>: those).</summary>
    private static CraftablesFile Craftables(AssetCatalog catalog, Project project, IReadOnlyList<CraftIngredient>? ingredients = null)
    {
        var table = CraftablesPageViewModel.Describe(catalog, Table)!;
        table = ingredients is null ? table : table with { Ingredients = [.. ingredients] };
        var token = CraftablesFile.NewTokenFor("Module Table", []);
        new CraftablesFile { Items = [table with { Name = "Module Table", Token = token, Exported = false }] }.Save(project.DirectoryPath);
        return CraftablesFile.Load(project.DirectoryPath);
    }

    /// <summary>
    /// The <c>AssetRegistry.bin</c> the game reads with <paramref name="paks"/> installed in one folder, and that pak's name.
    /// UE 4.27 (<c>FPakPlatformFile::MountAllPakFiles</c>) mounts the paks it finds sorted by name in descending order, all
    /// <c>*_P.pak</c> of one folder get the same read order, and a file is read from the first mounted pak that has it: the pak
    /// whose name sorts last wins (the rule <see cref="RegistryMerge.ReadStock"/> follows too). CUE4Parse's own pick differs
    /// for equal read orders (it took the lowest page pak here), so the rule is applied directly.
    /// </summary>
    /// <summary>The copy of <paramref name="package"/> the game reads with <paramref name="paks"/> installed (the rule of <see cref="InstalledRegistry"/>).</summary>
    private static (string Pak, ScumStudio.Formats.Packages.CookedPackage Package) InstalledPackage(IEnumerable<string> paks, string projectName, string package)
    {
        foreach (var pak in paks.OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            using var source = PakFileSource.OpenFile(pak, new PakFileSourceOptions { ProjectName = projectName });
            var header = source.EnumerateFiles().FirstOrDefault(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                && string.Equals(AssetPaths.ToPackagePath(f, projectName), package, StringComparison.OrdinalIgnoreCase));
            if (header is not null && source.TryGetBytes(header, out var uasset) && source.TryGetBytes(header[..^".uasset".Length] + ".uexp", out var uexp))
            {
                return (Path.GetFileName(pak), ScumStudio.Formats.Packages.CookedPackage.Parse(uasset, uexp, null, package));
            }
        }

        throw new InvalidOperationException($"No installed pak has {package}.");
    }

    private static (string Pak, AssetRegistryFile Registry) InstalledRegistry(IEnumerable<string> paks, string projectName)
    {
        foreach (var pak in paks.OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            if (ModuleExport.ReadPak(pak, projectName).Registry is { } registry)
            {
                return (Path.GetFileName(pak), registry);
            }
        }

        throw new InvalidOperationException("No installed pak has an AssetRegistry.bin.");
    }
}
