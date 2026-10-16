using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using ScumStudio.Tests.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// D01 (jenni_1129, m7ammed): a cloned vehicle could be spawned, but once the game was closed and started again it
/// crashed while loading the saved car. Of everything a saved car is rebuilt from, the one thing the clone lacked was its
/// item container's entity setup: it copied the container (the vehicle's <c>_itemContainerClass</c>) but not
/// <c>X_Item_Container_ES</c>, which names the container only by its <c>Actor</c> soft class, so nothing imports it and the
/// family walk never reached it. Every stock vehicle, and the owner's own clones that survive a restart
/// (<c>clone_vehicle.py</c>, <c>clone_car.py</c>, <c>clone_plane.py</c>), ship one.
/// </summary>
public sealed class VehicleCloneReloadTests(ITestOutputHelper output)
{
    private const string Folder = "/Game/V/Car/";
    private const string Car = Folder + "BPC_Car";

    [Fact]
    public void AVehicleCloneTakesItsItemContainersSetup()
    {
        using var temp = new TempDirectory();
        WriteGame(temp.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var plan = CloneFamilyPlanner.PlanVehicle(catalog, Car, "RedCar", new VehicleCloneOptions { IncludePaint = false });
        var map = plan.Packages.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Folder + "RedCar_ES", map[Folder + "Car_ES"]);
        Assert.Equal(Folder + "RedCar_Item_Container", map[Folder + "Car_Item_Container"]);
        Assert.Equal(Folder + "RedCar_Item_Container_ES", map[Folder + "Car_Item_Container_ES"]);
        Assert.Equal(Folder + "RedCar_ES", plan.Packages.Select(p => p.Value).First(p => p.EndsWith("_ES", StringComparison.Ordinal))); // the vehicle's setup stays first
        Assert.False(map.ContainsKey(Folder + "Car_Tires_ES")); // a twin that does not name Car_Tires_C is not its setup
        Assert.Equal(Folder + "Car_Item_Container_ES", CloneFamilyPlanner.EntitySetupOf(catalog, Folder + "Car_Item_Container"));
        Assert.Null(CloneFamilyPlanner.EntitySetupOf(catalog, Folder + "Car_Tires"));
    }

    [Fact]
    public void AnExportCompletesAPlanRecordedWithoutTheContainersSetup()
    {
        using var temp = new TempDirectory();
        var registry = WriteGame(temp.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var plan = CloneFamilyPlanner.PlanVehicle(catalog, Car, "RedCar", new VehicleCloneOptions { IncludePaint = false });

        // What earlier versions recorded in the journal (Jenni's seven Ragers): the family without the container's setup.
        var recorded = plan with { Packages = plan.Packages.Where(p => !p.Key.EndsWith("_Item_Container_ES", StringComparison.Ordinal)).ToList() };
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([recorded], new Dictionary<string, IReadOnlyList<TunableEdit>>())
        {
            BaseRegistry = registry.Save(),
            SellClones = false, // no trade table in this game
        });
        Assert.Empty(result.Warnings);
        AssertReloadable(result, plan, output);

        var setup = Assert.Single(result.Packages, p => p.PackagePath == Folder + "RedCar_Item_Container_ES");
        var names = CookedPackage.Parse(setup.Bytes.UAsset, setup.Bytes.UExp, null, setup.PackagePath).Names;
        Assert.Contains(Folder + "RedCar_Item_Container.RedCar_Item_Container_C", names);
        Assert.Contains(result.Registered, r => r.ObjectPath == Folder + "RedCar_Item_Container_ES.RedCar_Item_Container_ES_C" && r.PrimaryAssetType == "LWObjectSetup");
        Assert.Contains(result.Registered, r => r.ObjectPath == Folder + "RedCar_Item_Container_ES.RedCar_Item_Container_ES" && r.PrimaryAssetType == "LWObjectSetup");
    }

    [Fact]
    public void ASetupWhoseNewPathIsTakenIsLeftOutWithAWarning()
    {
        using var temp = new TempDirectory();
        WriteGame(temp.Path);
        SyntheticGame.Write(temp.Path, Folder + "RedCar_Item_Container_ES", SyntheticGame.Package(Folder + "RedCar_Item_Container_ES"));
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var recorded = new ClonePlan(Car, Folder + "BPC_RedCar", [new(Car, Folder + "BPC_RedCar"), new(Folder + "Car_Item_Container", Folder + "RedCar_Item_Container")]);

        var warnings = new List<string>();
        var completed = CloneFamilyPlanner.WithEntitySetups(catalog, recorded, warnings);
        Assert.Same(recorded, completed);
        Assert.Contains("RedCar_Item_Container_ES exists already", Assert.Single(warnings), StringComparison.Ordinal);
    }

    /// <summary>
    /// Real game files (<c>SCUM_PAKS</c>, key from this PC's store): <c>BPC_Rager</c> cloned as <c>RedRager</c>, the
    /// family 3.2 recorded completed by the export, and what loading a saved car needs checked on the result.
    /// </summary>
    [Fact]
    public void ARagerCloneShipsASetupForEveryItemItCopies()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        const string rager = "/Game/ConZ_Files/Vehicles/Car/Rager/";
        var plan = CloneFamilyPlanner.PlanVehicle(catalog, rager + "BPC_Rager", "RedRager");
        Assert.Contains(plan.Packages, p => p.Key == rager + "Rager_Item_Container_ES" && p.Value == rager + "RedRager_Item_Container_ES");

        var recorded = plan with { Packages = plan.Packages.Where(p => !p.Key.EndsWith("_Item_Container_ES", StringComparison.Ordinal)).ToList() };
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([recorded], new Dictionary<string, IReadOnlyList<TunableEdit>>()));
        Assert.Empty(result.Warnings);
        AssertReloadable(result, plan, output);

        var setup = Assert.Single(result.Packages, p => p.PackagePath == rager + "RedRager_Item_Container_ES");
        var names = CookedPackage.Parse(setup.Bytes.UAsset, setup.Bytes.UExp, null, setup.PackagePath).Names;
        Assert.Contains(rager + "RedRager_Item_Container.RedRager_Item_Container_C", names);
        var vehicleSetup = result.Packages.Single(p => p.PackagePath == rager + "RedRager_ES");
        Assert.Contains(rager + "BPC_RedRager.BPC_RedRager_C", CookedPackage.Parse(vehicleSetup.Bytes.UAsset, vehicleSetup.Bytes.UExp, null, vehicleSetup.PackagePath).Names);
    }

    /// <summary>
    /// What a saved car needs to load again: <c>Vehicle:BPC_&lt;Token&gt;</c> and its <c>_C</c> record, a registered entity
    /// setup naming the class of every item the clone copies (the vehicle's own and its item container's), and no copied
    /// package still naming a class of the stock family it was copied from.
    /// </summary>
    private static void AssertReloadable(AssetModBuildResult result, ClonePlan plan, ITestOutputHelper output)
    {
        Assert.NotNull(result.AssetRegistry);
        var registry = AssetRegistryFile.Parse(result.AssetRegistry);
        var types = registry.Assets.ToDictionary(
            a => a.ObjectPath,
            a => registry.GetTags(a).FirstOrDefault(t => t.Key == "PrimaryAssetType").Value?.Text,
            StringComparer.OrdinalIgnoreCase);
        var clones = result.Packages.Where(p => p.IsClone)
            .ToDictionary(p => p.PackagePath, p => CookedPackage.Parse(p.Bytes.UAsset, p.Bytes.UExp, p.UBulk, p.PackagePath).Names, StringComparer.OrdinalIgnoreCase);
        string Class(string package) => $"{package}.{PackageMap.Leaf(package)}_C";

        var vehicle = plan.NewPrimary;
        Assert.Equal("Vehicle", types.GetValueOrDefault($"{vehicle}.{PackageMap.Leaf(vehicle)}"));
        Assert.Equal("Vehicle", types.GetValueOrDefault(Class(vehicle)));

        var setups = clones.Where(c => types.GetValueOrDefault(Class(c.Key)) == "LWObjectSetup").ToList();
        foreach (var item in clones.Keys.Where(p => types.GetValueOrDefault(Class(p)) is "Item" or "Vehicle"))
        {
            output.WriteLine($"{item}: setup {setups.FirstOrDefault(s => s.Value.Contains(Class(item), StringComparer.OrdinalIgnoreCase)).Key ?? "(none)"}");
            Assert.Contains(setups, s => s.Value.Contains(Class(item), StringComparer.OrdinalIgnoreCase));
        }

        var stock = plan.Packages.Select(p => PackageMap.Normalize(p.Key))
            .SelectMany(p => new[] { p, $"{p}.{PackageMap.Leaf(p)}", Class(p) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (package, names) in clones)
        {
            Assert.False(names.Any(stock.Contains), $"{package} still names {names.FirstOrDefault(stock.Contains)}");
        }
    }

    /// <summary>
    /// A small car family: the vehicle imports its item container and tyres; its setup, the container's setup and a tyre
    /// twin that is not a setup (<c>Car_Tires_ES</c> does not name <c>Car_Tires_C</c>) sit beside them.
    /// </summary>
    private static AssetRegistryFile WriteGame(string root)
    {
        SyntheticGame.Write(root, Car, SyntheticGame.Package(Car, [Folder + "Car_Item_Container", Folder + "Car_Tires"], ["BPC_Car_C", "Default__BPC_Car_C", "Car_Item_Container_C"]));
        SyntheticGame.Write(root, Folder + "Car_ES", SyntheticGame.Package(Folder + "Car_ES", [], [Car + ".BPC_Car_C", "Car_ES_C"]));
        SyntheticGame.Write(root, Folder + "Car_Item_Container", SyntheticGame.Package(Folder + "Car_Item_Container", [], ["Car_Item_Container_C"]));
        SyntheticGame.Write(root, Folder + "Car_Item_Container_ES",
            SyntheticGame.Package(Folder + "Car_Item_Container_ES", [], [Folder + "Car_Item_Container.Car_Item_Container_C", "Car_Item_Container_ES_C"]));
        SyntheticGame.Write(root, Folder + "Car_Tires", SyntheticGame.Package(Folder + "Car_Tires"));
        SyntheticGame.Write(root, Folder + "Car_Tires_ES", SyntheticGame.Package(Folder + "Car_Tires_ES"));

        var records = new List<SyntheticGame.Record>();
        foreach (var (package, type) in new[] { (Car, "Vehicle"), (Folder + "Car_ES", "LWObjectSetup"), (Folder + "Car_Item_Container", "Item"), (Folder + "Car_Item_Container_ES", "LWObjectSetup") })
        {
            var leaf = PackageMap.Leaf(package);
            records.Add(new SyntheticGame.Record($"{package}.{leaf}", "Blueprint", SyntheticGame.PrimaryTags(type, leaf)));
            records.Add(new SyntheticGame.Record($"{package}.{leaf}_C", "BlueprintGeneratedClass", SyntheticGame.PrimaryTags(type, leaf)));
        }

        var registry = SyntheticGame.Registry([.. records]);
        SyntheticGame.Write(root, registry);
        return registry;
    }
}
