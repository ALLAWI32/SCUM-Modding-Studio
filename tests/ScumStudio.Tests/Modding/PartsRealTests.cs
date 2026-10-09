using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Owner (2026-10-09): "more control over vehicles: attachments and parts". A vehicle spawn preset's slots read as
/// <see cref="TunableKind.Part"/> values (a part removed, armour put on an empty door slot), a weapon's sockets as
/// <see cref="TunableKind.Mount"/> values (a magazine type allowed, the bayonet taken away); the exported packages read
/// back with ours and with the game's reader (CUE4Parse). Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class PartsRealTests(ITestOutputHelper output)
{
    private const string Preset = "/Game/ConZ_Files/Vehicles/SpawningPresets/ManualSpawn/WolfsWagenManualSpawnPreset";
    private const string Attachments = "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/Attachments/";
    private const string Ak47 = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";
    private const string Sockets = "/Game/ConZ_Files/Items/Weapons/AttachmentSockets/";

    private static string Class(string leaf) => Attachments + leaf + "." + leaf + "_C";

    private static string Mount(string leaf) => Sockets + leaf + "." + leaf + "_C";

    [Fact]
    public async Task AVehiclePresetLosesThePartsTakenAwayAndWearsTheArmourPutOn()
    {
        if (Open() is not { } catalog)
        {
            return;
        }

        using (catalog)
        {
            CookedPackage? Load(string p) => catalog.PackageExists(p) ? ModdableAssets.ReadPackage(catalog, p) : null;
            var preset = ModdableAssets.ReadPackage(catalog, Preset);
            var slots = VehicleParts.Read(preset, Load);
            foreach (var s in slots.Take(40))
            {
                output.WriteLine($"{s.Group} | {s.Name} | {s.Key} = {VehicleParts.Label(s.Value)} [{string.Join(", ", s.Choices.Select(VehicleParts.Label))}]");
            }

            // The chassis has 24 slots (dashboard … brake lights); the radio sits in the dashboard; the front-left door's
            // own slot takes heavy or light armour and spawns empty.
            Assert.Equal(24, slots.Count(s => s.Group == "Chassis"));
            var radio = Assert.Single(slots, s => s.Value == Class("BPC_WolfsWagen_Radio"));
            var armour = Assert.Single(slots, s => s.Group == "Door FrontLeft");
            Assert.Equal(string.Empty, armour.Value);
            Assert.Equal("ArmorHeavy / ArmorLight", armour.Name);
            Assert.Contains(Class("BPC_WolfsWagen_Door_ArmorHeavy_FrontLeft"), armour.Choices);
            Assert.Contains(Class("BPC_WolfsWagen_Door_ArmorLight_FrontLeft"), armour.Choices);
            Assert.Contains(string.Empty, armour.Choices);

            var result = AssetModBuilder.Build(catalog, new AssetModRequest([], new Dictionary<string, IReadOnlyList<TunableEdit>>
            {
                [Preset] = [new(radio.Export, radio.Path, string.Empty), new(armour.Export, armour.Path, Class("BPC_WolfsWagen_Door_ArmorHeavy_FrontLeft"))],
            }));
            Assert.Empty(result.Warnings);
            var built = Assert.Single(result.Packages, p => p.PackagePath == Preset);

            // Ours: the slots read back as edited, every other slot as it was.
            var after = VehicleParts.Read(Parsed(built), Load).ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(string.Empty, after[radio.Key]);
            Assert.Equal(Class("BPC_WolfsWagen_Door_ArmorHeavy_FrontLeft"), after[armour.Key]);
            Assert.Equal(slots.Count, after.Count); // the new armour node and the radio have no slots of their own
            foreach (var s in slots.Where(s => s.Key != radio.Key && s.Key != armour.Key))
            {
                Assert.Equal(s.Value, after[s.Key]);
            }

            Assert.DoesNotContain(Class("BPC_WolfsWagen_Radio"), VehicleParts.Spawned(Parsed(built)));
            Assert.Contains(Class("BPC_WolfsWagen_Door_ArmorHeavy_FrontLeft"), VehicleParts.Spawned(Parsed(built)));

            // The game's reader: walk the node tree from RootNode.
            var dir = Path.Combine(Path.GetTempPath(), "scumstudio-parts-" + Guid.NewGuid().ToString("N"));
            try
            {
                await built.Bytes.WriteAsync(Path.Combine(dir, built.HeaderFilePath())[..^".uasset".Length]);
                using var loose = AssetCatalog.OpenLoose(dir);
                var root = loose.LoadObject<UObject>(Preset + ".WolfsWagenManualSpawnPreset");
                var spawned = new List<string>();
                void Walk(UObject node)
                {
                    spawned.Add(node.GetOrDefault<FSoftObjectPath>("AttachmentClass").AssetPathName.Text);
                    foreach (var child in node.GetOrDefault<FPackageIndex[]>("Children") ?? [])
                    {
                        if (!child.IsNull && child.Load<UObject>() is { } c)
                        {
                            Walk(c);
                        }
                    }
                }

                Walk(root.GetOrDefault<FPackageIndex>("RootNode").Load<UObject>()!);
                Assert.Contains(Class("BPC_WolfsWagen_Door_ArmorHeavy_FrontLeft"), spawned);
                Assert.DoesNotContain(Class("BPC_WolfsWagen_Radio"), spawned);
                Assert.Contains(Class("BPC_WolfsWagen_Dashboard"), spawned);
                Assert.Equal(VehicleParts.Spawned(Parsed(built)).Order(), spawned.Order());
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task AWeaponTakesTheMagazineAllowedAndLosesTheBayonet()
    {
        if (Open() is not { } catalog)
        {
            return;
        }

        using (catalog)
        {
            var index = WeaponMounts.Index(catalog);
            Assert.Contains(Ak47.Replace("Ranged_Weapons/Weapon_AK47", "Weapon_Clips/Magazine_AK47"), index[Mount("BP_MountTypeWeaponMagazine76239")]);
            var weapon = ModdableAssets.ReadPackage(catalog, Ak47);
            var rows = WeaponMounts.Read(weapon, index);
            foreach (var r in rows)
            {
                output.WriteLine($"{r.Group} | {r.Name} | {r.Value}");
            }

            var ak47Mag = Assert.Single(rows, r => r.Path.EndsWith("#" + Mount("BP_MountTypeWeaponMagazine76239"), StringComparison.OrdinalIgnoreCase));
            Assert.Equal("true", ak47Mag.Value);
            var magazines = rows.Where(r => r.Group == ak47Mag.Group).ToList();
            var ak15Mag = Assert.Single(magazines, r => r.Path.EndsWith("#" + Mount("BP_MountTypeWeaponMagazine_AK15_762_39"), StringComparison.OrdinalIgnoreCase));
            Assert.Equal("false", ak15Mag.Value);
            var bayonet = Assert.Single(rows, r => r.Path.EndsWith("#" + Mount("BP_MountTypeWeapon_M70_Bayonet"), StringComparison.OrdinalIgnoreCase));
            Assert.Equal("true", bayonet.Value);

            var result = AssetModBuilder.Build(catalog, new AssetModRequest([], new Dictionary<string, IReadOnlyList<TunableEdit>>
            {
                [Ak47] = [new(ak15Mag.Export, ak15Mag.Path, "true"), new(bayonet.Export, bayonet.Path, "false")],
            }));
            Assert.Empty(result.Warnings);
            var built = Assert.Single(result.Packages, p => p.PackagePath == Ak47);
            var after = WeaponMounts.Read(Parsed(built), index).ToDictionary(r => r.Key, r => r.Value);
            Assert.Equal("true", after[ak15Mag.Key]);
            Assert.Equal("false", after.GetValueOrDefault(bayonet.Key, "false")); // no bayonet left: the bayonet kinds are no longer offered
            Assert.Equal("true", after[ak47Mag.Key]);
            Assert.All(rows.Where(r => r.Key != ak15Mag.Key && r.Key != bayonet.Key), r => Assert.Equal(r.Value, after.GetValueOrDefault(r.Key, "false")));

            // Every other stored value is untouched (the CDO only grew by one socket item and lost one).
            var stock = TunableReader.Read(weapon).ToDictionary(t => t.Key, t => t.Value);
            var edited = TunableReader.Read(Parsed(built)).ToDictionary(t => t.Key, t => t.Value);
            Assert.Equal(stock["Default__Weapon_AK47_C|_damageOverTime"], edited["Default__Weapon_AK47_C|_damageOverTime"]);

            var dir = Path.Combine(Path.GetTempPath(), "scumstudio-mounts-" + Guid.NewGuid().ToString("N"));
            try
            {
                await built.Bytes.WriteAsync(Path.Combine(dir, built.HeaderFilePath())[..^".uasset".Length]);
                using var loose = AssetCatalog.OpenLoose(dir);
                var package = loose.LoadPackage(Ak47);
                var cdo = package.GetExports().Single(e => e.Name == "Default__Weapon_AK47_C");
                var mounts = (cdo.GetOrDefault<FStructFallback[]>(WeaponMounts.Property) ?? [])
                    .SelectMany(s => s.GetOrDefault<FStructFallback[]>("Items") ?? [])
                    .Select(i => i.GetOrDefault<FPackageIndex>("MountType").Name)
                    .ToList();
                output.WriteLine(string.Join(", ", mounts));
                Assert.Contains("BP_MountTypeWeaponMagazine_AK15_762_39_C", mounts);
                Assert.Contains("BP_MountTypeWeaponMagazine76239_C", mounts);
                Assert.DoesNotContain("BP_MountTypeWeapon_M70_Bayonet_C", mounts);
                Assert.Equal(1.12f, cdo.GetOrDefault<float>("_damageOverTime"));
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    private static CookedPackage Parsed(BuiltAssetPackage built) => CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, built.UBulk, built.PackagePath);

    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;
}
