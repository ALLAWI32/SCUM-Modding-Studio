using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak.Reading;
using ScumStudio.Pak.Writing;
using ScumStudio.Tests.Modding;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Hektor's map pak (found by another session): zlib-compressed, a GameFeature plugin level next to SCUM/Content, 210 MB of
/// JSON dumps and pictures, 5.8 GB in all. The import keeps what the game loads, the export can leave a big mod out, and
/// plugin content survives the pak writer.
/// </summary>
public sealed class ProjectModsTests : IDisposable
{
    private const string Plugin = "SCUM/Plugins/GameFeatures/WoodlandHunterPack/Content/World/Maps/The_Island/C_2_Outpost_HuntersGrotto.umap";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-mods-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AZlibModWithAPluginLevelIsImportedWithoutJunkAndCanStayOutOfTheExport()
    {
        // The mod as Hektor's tools packed it.
        var staging = Path.Combine(_dir, "hektor");
        var files = new Dictionary<string, byte[]>
        {
            ["SCUM/Content/ConZ_Files/Maps/The_Island/Landscape_A_0_1.umap"] = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 7)).ToArray(),
            ["SCUM/Content/ConZ_Files/Maps/The_Island/Landscape_A_0_1.uexp"] = Enumerable.Range(0, 90_000).Select(i => (byte)(i % 13)).ToArray(),
            [Plugin] = [1, 2, 3, 4, 5],
            ["SCUM/Content/WaterSplines.json"] = "{\"dump\": true}"u8.ToArray(),
            ["SCUM/Content/Preview.png"] = [137, 80, 78, 71],
        };
        foreach (var (path, data) in files)
        {
            var file = Path.Combine(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, data);
        }

        var pak = Path.Combine(_dir, "pakchunk90-DesertMap.pak");
        await new PakWriter(new PakWriterOptions { Compression = PakCompression.Zlib, OnlyGameContent = false }).WriteFromDirectoryAsync(staging, pak, null, CancellationToken.None);

        // Imported: the packages, inflated, the plugin level too; no dumps or pictures.
        var project = Path.Combine(_dir, "project");
        var (folder, count) = await ProjectMods.ImportAsync(project, pak, null);
        Assert.Equal("DesertMap", Path.GetFileName(folder));
        Assert.Equal(3, count);
        foreach (var path in files.Keys.Where(ProjectMods.IsGameFile))
        {
            Assert.Equal(files[path], await File.ReadAllBytesAsync(Path.Combine(folder, path)));
        }

        Assert.False(File.Exists(Path.Combine(folder, "SCUM/Content/WaterSplines.json")));
        Assert.False(File.Exists(Path.Combine(folder, "SCUM/Content/Preview.png")));

        // Carried into the export (plugin level included) and packed with it, unless it is to stay its own pak.
        var export = Path.Combine(_dir, "export");
        Assert.True(ProjectMods.IsCarried(folder));
        Assert.Equal(3, ProjectMods.CarryInto([folder], export));
        var ours = Path.Combine(_dir, "ours.pak");
        await new PakWriter(new PakWriterOptions()).WriteFromDirectoryAsync(export, ours, null, CancellationToken.None);
        using (var written = PakFileSource.OpenFile(ours))
        {
            Assert.True(written.Exists(Plugin));
        }

        ProjectMods.SetCarried(folder, false);
        Assert.False(ProjectMods.IsCarried(folder));
        Assert.Equal(0, ProjectMods.CarryInto([folder], Path.Combine(_dir, "export2")));
        Assert.Equal([folder], ProjectMods.Folders(project)); // still read over the game
        ProjectMods.SetCarried(folder, true);
        Assert.True(ProjectMods.IsCarried(folder));
    }

    [Fact]
    public void CarryIntoNeverCopiesTheRegistry()
    {
        // F005/N012: a mod's AssetRegistry.bin shipped as it is would replace the game's (stock records lost, old ones kept).
        var mod = Path.Combine(_dir, "project", ProjectMods.FolderName, "ModA");
        SyntheticGame.Write(mod, "/Game/Mods/BP_ModCar", SyntheticGame.Package("/Game/Mods/BP_ModCar"));
        SyntheticGame.Write(mod, SyntheticGame.StockRegistry());
        var staging = Path.Combine(_dir, "staging");

        Assert.Equal(2, ProjectMods.CarryInto([mod], staging));
        Assert.True(File.Exists(Path.Combine(staging, "SCUM", "Content", "Mods", "BP_ModCar.uasset")));
        Assert.False(File.Exists(Path.Combine(staging, "SCUM", "AssetRegistry.bin")));
        Assert.Equal([("ModA", Path.Combine(mod, "SCUM", "AssetRegistry.bin"))], ProjectMods.Registries([mod]));
    }

    [Fact]
    public void FoldersSkipImporting()
    {
        // F012: an import that failed half-way is neither read over the game nor carried into the pak.
        var project = Path.Combine(_dir, "project");
        SyntheticGame.Write(Path.Combine(project, ProjectMods.FolderName, "Done"), "/Game/Mods/BP_A", SyntheticGame.Package("/Game/Mods/BP_A"));
        SyntheticGame.Write(Path.Combine(project, ProjectMods.FolderName, "Half" + ProjectMods.ImportingSuffix), "/Game/Mods/BP_B", SyntheticGame.Package("/Game/Mods/BP_B"));

        Assert.Equal([Path.Combine(project, ProjectMods.FolderName, "Done")], ProjectMods.Folders(project));
    }

    [Fact]
    public async Task ACarriedModsRegistryIsMergedIntoStockPlusOurs()
    {
        // The game as paks (stock registry in pakchunk0), an imported mod read over it with an older registry of its own
        // (one stock record fewer, its car, a record of a package it does not have), and a project that clones a Blueprint.
        var game = Path.Combine(_dir, "game");
        var stock = SyntheticGame.WriteGame(game);
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        var paks = Path.Combine(_dir, "Paks");
        Directory.CreateDirectory(paks);
        await new PakWriter().WriteFromDirectoryAsync(game, Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"));

        var mod = Path.Combine(_dir, "project", ProjectMods.FolderName, "ModA");
        SyntheticGame.Write(mod, "/Game/Mods/BP_ModCar", SyntheticGame.Package("/Game/Mods/BP_ModCar"));
        var modRegistry = AssetRegistryFile.Parse(stock.Save());
        modRegistry.Remove(a => a.ObjectPath == SyntheticGame.Stock2);
        var template = modRegistry.Assets.First(a => a.AssetClass == "Blueprint");
        foreach (var leaf in new[] { "BP_ModCar", "BP_ModGone" })
        {
            modRegistry.AddAsset(template, $"/Game/Mods/{leaf}.{leaf}", "/Game/Mods", "/Game/Mods/" + leaf, leaf, SyntheticGame.PrimaryTags("Vehicle", leaf));
        }

        SyntheticGame.Write(mod, modRegistry);
        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { LooseOverlays = [mod] });
        using var project = Project.Create(Path.Combine(_dir, "project", "Merge.ssproj"), "Merge");
        project.Apply(new CloneAssetOp("item", SyntheticGame.Gen, "/Game/T/BP_Ours",
            [new PackagePair(SyntheticGame.Gen, "/Game/T/BP_Ours"), new PackagePair(SyntheticGame.GenEs, "/Game/T/BP_Ours_ES")]));

        var result = await new ProjectExporter().ExportAsync(project, catalog,
            new ExportOptions { OutputDirectory = Path.Combine(_dir, "out"), WritePak = false, Mods = [mod] });

        var shipped = AssetRegistryFile.Load(Path.Combine(result.StagingDirectory, "SCUM", "AssetRegistry.bin"));
        ExportSafety.AssertRegistrySuperset(stock, shipped, result.Registered.Select(r => r.ObjectPath).Append("/Game/Mods/BP_ModCar.BP_ModCar"));
        Assert.Contains("/Game/T/BP_Ours.BP_Ours_C", result.Registered.Select(r => r.ObjectPath));
        Assert.Contains(result.Warnings, w => w.StartsWith("ModA: 1 record(s)", StringComparison.Ordinal) && w.Contains("BP_ModGone", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(result.StagingDirectory, "SCUM", "Content", "Mods", "BP_ModCar.uasset")));
    }
}
