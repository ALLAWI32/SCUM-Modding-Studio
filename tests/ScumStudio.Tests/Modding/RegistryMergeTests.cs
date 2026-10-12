using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak.Writing;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The registry a pak ships is the game's stock one plus additions (P0 #1, #21; N004, F005, F040, N012, F066, F122): never
/// a mod pak's or an imported mod's registry as a whole, and a mod's records only for packages that ship.
/// </summary>
public sealed class RegistryMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-regmerge-" + Guid.NewGuid().ToString("N"));

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
    public void StockPlusShippedModRecordsPlusOurs() =>
        MergesStockShippedModRecordsAndOurs(SyntheticGame.StockRegistry(), SyntheticGame.Stock2, SyntheticGame.Gen + ".BP_Gen", (SyntheticGame.Gen, "/Game/T/BP_Ours"));

    [FixturesFact]
    public void StockPlusShippedModRecordsPlusOursOnTheGamesRegistry() =>
        MergesStockShippedModRecordsAndOurs(AssetRegistryFile.Load(FixturePaths.StockAssetRegistry),
            "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/SK_WolfsWagen.SK_WolfsWagen",
            "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen.BPC_WolfsWagen",
            ("/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen", "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_Hunter"));

    private static void MergesStockShippedModRecordsAndOurs(AssetRegistryFile stock, string dropped, string templatePath, (string Old, string New) clone)
    {
        // The mod's registry: an older stock (one record fewer), a record of a package it ships and one of a package it does not.
        var stockBytes = stock.Save();
        var mod = AssetRegistryFile.Parse(stockBytes);
        Assert.Equal(1, mod.Remove(a => a.ObjectPath == dropped));
        var template = mod.Assets.Single(a => a.ObjectPath == templatePath);
        foreach (var leaf in new[] { "BP_Shipped", "BP_Gone" })
        {
            mod.AddAsset(template, $"/Game/Mods/{leaf}.{leaf}", "/Game/Mods", "/Game/Mods/" + leaf, leaf, SyntheticGame.PrimaryTags("Vehicle", leaf));
        }

        var warnings = new List<string>();
        var merged = RegistryMerge.Build(AssetRegistryFile.Parse(stockBytes), [("ModA", AssetRegistryFile.Parse(mod.Save()))], p => p == "/Game/Mods/BP_Shipped", warnings);
        var ours = RegistryCloner.Register(merged, new PackageMap([new(clone.Old, clone.New)]));

        Assert.NotEmpty(ours);
        ExportSafety.AssertRegistrySuperset(stock, AssetRegistryFile.Parse(merged.Save()), ours.Select(r => r.ObjectPath).Append("/Game/Mods/BP_Shipped.BP_Shipped"));
        var warning = Assert.Single(warnings);
        Assert.StartsWith("ModA: 1 record(s)", warning, StringComparison.Ordinal);
        Assert.Contains("/Game/Mods/BP_Gone.BP_Gone", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadStockIgnoresPPaks()
    {
        var stock = SyntheticGame.StockRegistry();
        var mod = SyntheticGame.Registry(new SyntheticGame.Record("/Game/Mods/BP_Mod.BP_Mod", "Blueprint", SyntheticGame.PrimaryTags("Item", "BP_Mod")));
        var paks = await WritePaksAsync(stock, mod);

        using var catalog = AssetCatalog.OpenPaks(paks);
        Assert.Equal(mod.Save(), catalog.Provider.Files[AssetModBuilder.AssetRegistryPath].Read()); // what the game reads: the highest pak's
        Assert.Equal(stock.Save(), RegistryMerge.ReadStock(catalog));

        using var loose = AssetCatalog.OpenLoose(Path.Combine(_dir, "game"));
        Assert.Null(RegistryMerge.ReadStock(loose));
        var warnings = new List<string>();
        Assert.Equal(stock.Save(), RegistryMerge.ReadBase(loose, warnings));
        Assert.Contains("not the game's stock registry", Assert.Single(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModPaksRegistryIsNotTheBase()
    {
        // An installed mod pak whose registry lacks a stock record (an older game build) and adds its own.
        var stock = SyntheticGame.StockRegistry();
        var mod = AssetRegistryFile.Parse(stock.Save());
        mod.Remove(a => a.ObjectPath == SyntheticGame.Stock2);
        mod.AddAsset(mod.Assets.First(a => a.AssetClass == "Blueprint"), "/Game/Mods/BP_Mod.BP_Mod", "/Game/Mods", "/Game/Mods/BP_Mod", "BP_Mod", SyntheticGame.PrimaryTags("Item", "BP_Mod"));
        var paks = await WritePaksAsync(stock, AssetRegistryFile.Parse(mod.Save()));

        using var catalog = AssetCatalog.OpenPaks(paks);
        var plan = new ClonePlan(SyntheticGame.Gen, "/Game/T/BP_Ours", [new(SyntheticGame.Gen, "/Game/T/BP_Ours"), new(SyntheticGame.GenEs, "/Game/T/BP_Ours_ES")]);
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>()));

        Assert.NotNull(result.AssetRegistry);
        ExportSafety.AssertRegistrySuperset(stock, AssetRegistryFile.Parse(result.AssetRegistry), result.Registered.Select(r => r.ObjectPath));
        Assert.Contains(result.Warnings, w => w.StartsWith("pakchunk900-OtherMod_P.pak ships an AssetRegistry.bin of its own", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAStockRegistryAModPaksIsNotTheBase()
    {
        // The game's paks have no registry (a server folder without one): the only one there is a mod pak's, and nothing is
        // registered rather than shipping that mod's registry as the game's.
        var mod = SyntheticGame.Registry(new SyntheticGame.Record("/Game/Mods/BP_Mod.BP_Mod", "Blueprint", SyntheticGame.PrimaryTags("Item", "BP_Mod")));
        var paks = await WritePaksAsync(null, mod);

        using var catalog = AssetCatalog.OpenPaks(paks);
        var warnings = new List<string>();
        Assert.Null(RegistryMerge.ReadBase(catalog, warnings));
        Assert.Contains("pakchunk900-OtherMod_P.pak", Assert.Single(warnings), StringComparison.Ordinal);

        var plan = new ClonePlan(SyntheticGame.Gen, "/Game/T/BP_Ours", [new(SyntheticGame.Gen, "/Game/T/BP_Ours")]);
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>()));
        Assert.Null(result.AssetRegistry);
        Assert.Empty(result.Registered);
        Assert.Contains(result.Warnings, w => w.Contains("pakchunk900-OtherMod_P.pak", StringComparison.Ordinal));
    }

    // Paks/pakchunk0-WindowsNoEditor.pak: the synthetic game with stock (none when null); Paks/pakchunk900-OtherMod_P.pak: only modRegistry.
    private async Task<string> WritePaksAsync(AssetRegistryFile? stock, AssetRegistryFile modRegistry)
    {
        var game = Path.Combine(_dir, "game");
        SyntheticGame.WriteGame(game);
        if (stock is null)
        {
            File.Delete(Path.Combine(game, "SCUM", "AssetRegistry.bin"));
        }
        else
        {
            SyntheticGame.Write(game, stock);
        }

        var other = Path.Combine(_dir, "othermod");
        SyntheticGame.Write(other, "/Game/Mods/BP_Mod", SyntheticGame.Package("/Game/Mods/BP_Mod"));
        SyntheticGame.Write(other, modRegistry);
        var paks = Path.Combine(_dir, "Paks");
        Directory.CreateDirectory(paks);
        await new PakWriter().WriteFromDirectoryAsync(game, Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"));
        await new PakWriter().WriteFromDirectoryAsync(other, Path.Combine(paks, "pakchunk900-OtherMod_P.pak"));
        return paks;
    }
}
