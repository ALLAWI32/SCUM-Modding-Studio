using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
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

    [Fact]
    public async Task AWolfsWagenCloneMovesItsDoorAndGetsANewPartWithoutTouchingTheStockCar()
    {
        if (Open() is not { } catalog)
        {
            return;
        }

        using (catalog)
        {
            const string meshes = "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/";
            var plan = CloneFamilyPlanner.PlanVehicle(catalog, "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen", "PartsReal");
            var map = plan.Packages.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var mesh = map[meshes + "SK_WolfsWagen"]; // the clone's own copy of the car's mesh (it wears paint)
            var door = Assert.Single(VehicleSockets.Read(ModdableAssets.ReadPackage(catalog, meshes + "SK_WolfsWagen_Skeleton")), s => s.Name == "s_Door_FrontLeft");
            var custom = VehicleAttachmentEdits.CustomName(Attachments.TrimEnd('/'), "PartsReal", catalog.PackageExists);
            var socket = VehicleAttachmentEdits.CustomSocket(custom);
            var part = new ClonePlan(Attachments + "BPC_WolfsWagen_RoofRack", custom, [new(Attachments + "BPC_WolfsWagen_RoofRack", custom)]);
            var body = map[Attachments + "BPC_WolfsWagen_Body_Top"];
            var roof = VehicleAttachmentEdits.SlotsOf(ModdableAssets.ReadPackage(catalog, Attachments + "BPC_WolfsWagen_Body_Top"));
            var donorSlot = roof.Select((s, i) => (s, i)).First(x => x.s.Contains(Class("BPC_WolfsWagen_RoofRack"))).i;
            var presetNode = Assert.Single(VehicleParts.Read(ModdableAssets.ReadPackage(catalog, Preset), _ => null), t => t.ExportClass == Class("BPC_WolfsWagen_Body_Top") && t.Path == "Children[0]").Export;
            var meshEdits = VehicleSockets.Override("SK_PartsReal", door, door.Location + new System.Numerics.Vector3(10, 0, 0), door.Rotation, door.Scale)
                .Concat([new("SK_PartsReal", VehicleSockets.Path(socket, VehicleSockets.Bone), "Root"), new("SK_PartsReal", VehicleSockets.Path(socket, VehicleSockets.Location), "-30, 0, 160")])
                .ToList();
            var result = AssetModBuilder.Build(catalog, new AssetModRequest([plan, part], new Dictionary<string, IReadOnlyList<TunableEdit>>
            {
                [mesh] = meshEdits,
                [custom] = [new("Default__" + PackageMap.Leaf(custom) + "_C", VehicleAttachmentEdits.SocketPath, socket)],
                [body] = [new("Default__BPC_PartsReal_Body_Top_C", VehicleAttachmentEdits.SlotPath(roof.Count), VehicleAttachmentEdits.SlotValue(VehicleAttachmentEdits.ClassPath(custom), donorSlot))],
                [map[Preset]] = [new(presetNode, $"Children[{roof.Count}]", VehicleAttachmentEdits.ClassPath(custom))],
            }));
            output.WriteLine(string.Join('\n', result.Warnings));
            Assert.DoesNotContain(result.Warnings, w => w.Contains("edits skipped", StringComparison.Ordinal));
            Assert.All(result.Packages.Where(p => p.PackagePath != DataTableEdits.TradeableTable), p => Assert.False(catalog.PackageExists(p.PackagePath), p.PackagePath));
            Assert.NotNull(result.AssetRegistry);

            // CUE4Parse reads the clone's mesh whole and resolves both sockets on it.
            var built = Assert.Single(result.Packages, p => p.PackagePath == mesh);
            var dir = Path.Combine(Path.GetTempPath(), "scumstudio-parts-" + Guid.NewGuid().ToString("N"));
            try
            {
                var basePath = Path.Combine(dir, built.HeaderFilePath())[..^".uasset".Length];
                await built.Bytes.WriteAsync(basePath);
                if (built.UBulk is { } ubulk)
                {
                    await File.WriteAllBytesAsync(basePath + ".ubulk", ubulk);
                }

                using var loose = AssetCatalog.OpenLoose(dir);
                var read = loose.LoadObject<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh>(mesh + "." + mesh[(mesh.LastIndexOf('/') + 1)..]);
                var names = (read.GetOrDefault<FPackageIndex[]>("Sockets") ?? []).Select(s => s.Load<UObject>()!.GetOrDefault<FName>("SocketName").Text).ToList();
                Assert.Equal(new[] { "s_Door_FrontLeft", socket }.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
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
