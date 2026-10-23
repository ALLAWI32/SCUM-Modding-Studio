using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using ScumStudio.Tests.App;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// r39/r40 (the clones diagnosis: "a clone of Weapon_M16A4_Premium is not built at all"): in 3.5.1, 22 of the game's 2,841
/// items, weapons and vehicles could be cloned on their page and were then left out of the pak at export ("not created").
/// Three causes, each fixed: a numbered member renamed to <c>_v&lt;n&gt;</c> changed its FName number, so data the studio
/// cannot read (the M16A4 Premium's skin mesh, the Trench Gun's) refused the copy; material names in another case than the
/// material's own export (the Scout knife's MI_Scout_MEt); and a numbered object of the template's class (the Log Shed's
/// LogShed_1_C_1). Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class PremiumCloneRealTests(ITestOutputHelper output)
{
    private const string Folder = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/";
    private const string Skin = "/Game/ConZ_Files/Models/Weapons/Ranged_Weapons/Automatic_Rifles/M16/M16_Skin/";

    [PaksFact]
    public async Task TheM16A4PremiumIsClonedOnTheWeaponsPageAndExportedWithItsOwnSkin()
    {
        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Premium");
        using var page = new WeaponsPageViewModel(ctx.Services);
        await page.LoadCompletion;
        var game = ctx.Services.Workspace.Catalog!;

        Assert.True(await page.SelectAsync("Weapon_M16A4_Premium"));
        page.CloneName = "M16Gold";
        await page.CreateCloneAsync();
        Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.IsError);
        Assert.Equal(("M16Gold", "#SpawnItem M16Gold"), (page.SelectedItem!.Name, page.SpawnCommandText));
        var clone = Assert.Single(project.State.AssetClones);
        output.WriteLine(string.Join("\n", clone.Packages.Select(p => $"{p.Old} -> {p.New}")));

        // The skin's material keeps the game's number: MI_M16_Apocalypso_2 is copied as M16Gold_MI_M16_Apocalypso_2.
        Assert.Contains(clone.Packages, p => p.Old == Skin + "Materials/MI_M16_Apocalypso_2" && p.New == Skin + "Materials/M16Gold_MI_M16_Apocalypso_2");

        var result = await new ProjectExporter().ExportAsync(project, game, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        Assert.DoesNotContain(result.Warnings, w => w.Contains("M16Gold", StringComparison.Ordinal));
        Assert.True(result.Validation is null || result.Validation.Passed, string.Join('\n', result.Validation?.Problems ?? []));
        var shipped = result.Assets.Select(a => a.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, copy) in clone.Packages.Select(p => (p.Old, p.New)))
        {
            Assert.Contains(copy, shipped);
        }

        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);

        // The skin mesh's copy wears the material's copy, under the cooker's split name, and nothing of the game's skin.
        var mesh = ModdableAssets.ReadPackage(written, Skin + "Meshes/M16Gold_SK_M16_Apocalypso");
        var material = Assert.Single(mesh.Imports, i => mesh.ResolveName(i.ClassName) == "MaterialInstanceConstant" && i.ObjectName.Format(mesh.Names).Contains("Apocalypso", StringComparison.Ordinal));
        Assert.Equal(("M16Gold_MI_M16_Apocalypso", 3), (mesh.Names[material.ObjectName.Index], material.ObjectName.Number));
        Assert.DoesNotContain(mesh.Imports, i => i.ObjectName.Format(mesh.Names) == Skin + "Materials/MI_M16_Apocalypso_2");
        Assert.True(written.TryLoadObject<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh>(Skin + "Meshes/M16Gold_SK_M16_Apocalypso.M16Gold_SK_M16_Apocalypso", out _));
        Assert.True(written.PackageExists(Skin + "Materials/M16Gold_MI_M16_Apocalypso_2"));

        // Spawnable under its new name (the game hides its template from #SpawnItem: r39).
        var registry = AssetRegistryFile.Load(Path.Combine(result.StagingDirectory, "SCUM", "AssetRegistry.bin"));
        var record = Assert.Single(registry.Assets, a => a.ObjectPath == Folder + "M16Gold.M16Gold_C");
        Assert.DoesNotContain(registry.GetTags(record), t => t.Key == RegistryCloner.HiddenFromAdminCommandsTag);
        var cdo = TunableReader.Read(ModdableAssets.ReadPackage(written, Folder + "M16Gold")).Single(t => t.Path == AssetModBuilder.VisibleToAdminCommands && t.Export.StartsWith("Default__", StringComparison.Ordinal));
        Assert.True(TunableValue.ParseBool(cdo.Value));
        Assert.False(written.PackageExists(Folder + "Weapon_M16A4_Premium"));
    }

    /// <summary>
    /// A clone recorded by 3.5.1 keeps its package names in the project's journal, numbered parts as <c>_v&lt;n&gt;</c>
    /// (<c>M16Gold_MI_M16_Apocalypso_v2</c>). The M16A4 Premium's and the Trench Gun's build now too (the chance bytes and the
    /// material's slot name no longer refuse them), so such a project exports them without making them again. The SF19's
    /// does not (its numbered mesh is named in data the studio cannot read): such a clone is removed and made again.
    /// </summary>
    [PaksFact]
    public void AClonePlannedBy351WithVNamesBuildsForThePremiumAndTheTrenchGun()
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions
        {
            AesKey = AesKeyText.FromEnvironmentOrStore(),
            PakFileFilter = OwnPaks.Filter(OwnPaks.ProjectNames("OldPlans")),
        });
        foreach (var (template, name, builds) in new[] { (Folder + "Weapon_M16A4_Premium", "M16Gold", true), (Folder + "Weapon_Trench_Gun", "RiotGun", true), (Folder + "Weapon_SF19", "SF20", false) })
        {
            var plan = CloneFamilyPlanner.PlanItem(catalog, template, name);
            var old = As351(plan);
            Assert.Contains(old.Packages, p => p.Value.Contains("_v", StringComparison.Ordinal));
            var why = AssetModBuilder.WhyNotBuilt(catalog, old);
            output.WriteLine($"{name} as 3.5.1 planned it: {why ?? "builds"}");
            Assert.Equal(builds, why is null);
            if (why is not null)
            {
                // The export says what to do; the refusal when a clone is made does not (there is nothing to remove yet).
                Assert.DoesNotContain(AssetModBuilder.RemakeHint, why, StringComparison.Ordinal);
                var export = AssetModBuilder.Build(catalog, new AssetModRequest([old], new Dictionary<string, IReadOnlyList<ScumStudio.Modding.Tuning.TunableEdit>>()) { WriteRegistry = false, SellClones = false });
                Assert.Contains(export.Warnings, w => w.Contains(old.NewPrimary, StringComparison.OrdinalIgnoreCase) && w.EndsWith(AssetModBuilder.RemakeHint, StringComparison.Ordinal));
            }

            Assert.Null(AssetModBuilder.WhyNotBuilt(catalog, plan)); // planned now, it builds
        }
    }

    /// <summary>
    /// The clones 3.5.1 did not build, its own planner and builder on the same game files: 22 of the 2,823 items and weapons
    /// (the r40 sweep before any change, <c>art\r40-items\04-sweep-before.log</c>: "built 2801, failed 22") and 3 of the 18
    /// vehicles (the same probe on the 3.5.1 code, commit e16f9aa9: <c>art\r40-items\22-probe-351-vehicles.log</c>, "built 15").
    /// </summary>
    private static readonly HashSet<string> FailedIn351 = new(StringComparer.Ordinal)
    {
        "1H_Scout_White", "2H_Tang_Dao_Black_Dragon", "Weapon_AS_Val", "Weapon_DEagle_50", "Weapon_Hunter85_V2", "Weapon_M16A4_Premium",
        "Weapon_SF19", "Weapon_Trench_Gun", "Weapon_VSS_VZ", "Weapon_Parts_Trench_Gun", "Magazine_AS_Val", "Cal_22", "Cal_38",
        "Baseball_Cap_11", "LogShed_1", "LogShed_2", "FishingLinePiece_1", "FishingLine_1", "Cruiser_Wheel_155_66_R17",
        "Cruiser_Wheel_188_53_R17", "Improvised_Motorcycle_Wheel", "Mosin_Bayonet",
        "BPC_Cruiser", "BPC_Dirtbike", "BPC_Tractor",
    };

    /// <summary>
    /// Every item, weapon, magazine, ammunition, projectile and vehicle of the game builds as a clone (3.5.1: 25 did not). And
    /// a project made with 3.5.1 keeps its clones: each clone with a numbered part, planned as 3.5.1 planned it (the part
    /// named <c>_v&lt;n&gt;</c>, as the journal holds it), builds now whenever 3.5.1 built it (r40 review).
    /// </summary>
    [PaksFact]
    public void EveryItemWeaponAndVehicleOfTheGameBuildsAsAClone()
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions
        {
            AesKey = AesKeyText.FromEnvironmentOrStore(),
            PakFileFilter = OwnPaks.Filter(OwnPaks.ProjectNames("CloneSweep")), // never this PC's installed studio paks
        });
        var assets = ModdableAssets.Find(catalog);
        Assert.True(assets.Count > 2800, $"{assets.Count} assets");
        var failed = new List<string>();
        var oldPlans = 0;
        var oldFailed = new List<string>();
        var oldLost = new List<string>();
        foreach (var asset in assets)
        {
            var plan = asset.Kind == ModdableKind.Vehicle
                ? CloneFamilyPlanner.PlanVehicle(catalog, asset.PackagePath, "Zq" + ModdableAssets.VehicleToken(asset.Name) + "Sw")
                : CloneFamilyPlanner.PlanItem(catalog, asset.PackagePath, "Zq" + asset.Name.Replace("-", string.Empty, StringComparison.Ordinal) + "Sw");
            if (AssetModBuilder.WhyNotBuilt(catalog, plan) is { } why)
            {
                failed.Add($"{asset.Kind} {asset.Name}: {why}");
            }

            var old = As351(plan);
            if (old.Packages.SequenceEqual(plan.Packages))
            {
                continue;
            }

            oldPlans++;
            if (AssetModBuilder.WhyNotBuilt(catalog, old) is { } oldWhy)
            {
                (FailedIn351.Contains(asset.Name) ? oldFailed : oldLost).Add($"{asset.Kind} {asset.Name}: {oldWhy}");
            }
        }

        output.WriteLine($"{assets.Count - failed.Count} of {assets.Count} build");
        output.WriteLine($"as 3.5.1 planned them: {oldPlans} clones with a numbered part, {oldPlans - oldFailed.Count - oldLost.Count} build; not built (3.5.1 did not build them either):");
        oldFailed.ForEach(output.WriteLine);
        Assert.True(failed.Count == 0, string.Join('\n', failed));
        Assert.True(oldPlans >= 3, $"{oldPlans} clones with a numbered part"); // the M16A4 Premium's, the Trench Gun's and the SF19's at least
        Assert.True(oldLost.Count == 0, "3.5.1 built these, now they are not:\n" + string.Join('\n', oldLost));
    }

    /// <summary>
    /// <paramref name="plan"/> as 3.5.1 made it (as an existing project's journal holds it): a numbered part (FName number n,
    /// <c>…_MI_M16_Apocalypso_2</c>) named <c>…_v&lt;n-1&gt;</c> (<c>…_MI_M16_Apocalypso_v2</c>).
    /// </summary>
    private static ClonePlan As351(ClonePlan plan) => plan with
    {
        Packages = [.. plan.Packages.Select(p =>
        {
            var (text, number) = ScumStudio.Formats.FNameRef.Split(PackageMap.Leaf(p.Value));
            return number == 0 ? p : new KeyValuePair<string, string>(p.Key, $"{PackageMap.Folder(p.Value)}/{text}_v{number - 1}");
        })],
    };
}
