using ScumStudio.App.Services;
using ScumStudio.Modding.Catalog;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Vehicles module: every vehicle Blueprint of the game files (cars, planes, boats, bikes) with its stored handling
/// values (mass, wheels, suspension, engine RPM, gears, push force …), entity setup and attachments; tune them or clone
/// the whole vehicle family under a new name that <c>#SpawnVehicle</c> finds.
/// </summary>
public sealed class VehiclesPageViewModel : ModulePageViewModel
{
    private static readonly HashSet<string> KeyNames = new(StringComparer.Ordinal)
    {
        "ChassisMass", "IdleRPM", "MaxRPM", "Radius", "Width", "Mass", "DampingRate", "MaxBrakeTorque", "MaxHandBrakeTorque",
        "MaxSteer", "Ratio", "ReverseGearRatio", "SwitchTime", "NaturalFrequency", "SpringDamperRatio", "MaxCompression",
        "MaxDroop", "_maxPushForce", "_minPushForce", "_maxPushForcePerPusher", "_maxLinearVelocityWhenPushing",
        "_inWaterDestructionTimeInSeconds", "_explosionDamageRatio", "_shouldVehicleBurnWhenDestroyed", "Caption", "Description",
        "_displayName", "Weight", "MaxTorque", "MaxPower", "_maxHealth", "_health", "MaxHealth", "Health", "FuelCapacity",
        "_fuelCapacity", "_maxFuel", "TopSpeed", "MaxSpeed", "bAutoReverse", "bAutoBrake", "StopThreshold",
        "BasePurchasePrice", "BaseSalePrice", "RequiredFamePoints", "CanBePurchasedByPlayer", "CanBeSoldByPlayer", "MaxAmountPurchasedAtOnce",
    };

    /// <summary>Creates the page.</summary>
    public VehiclesPageViewModel(AppServices services, Action? openSetup = null)
        : base(services, openSetup, "vehicles", "Vehicles", "Tune handling or clone a vehicle under a new name", [ModdableKind.Vehicle])
    {
    }

    /// <inheritdoc />
    protected override IReadOnlySet<string> KeyStats => KeyNames;

    /// <inheritdoc />
    protected override IReadOnlyList<ModuleFilter> BuildFilters(IReadOnlyList<ModuleItemViewModel> items) =>
        new[] { new ModuleFilter(Localization.Loc.T("Module.Filter.All"), null, null) }
            .Concat(items.Select(i => i.Asset.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(c => new ModuleFilter(c, ModdableKind.Vehicle, c)))
            .ToList();
}

/// <summary>
/// Weapons module: weapons, magazines, ammunition and projectiles with their stored values (damage per shot, rate of
/// fire, range, magazine capacity, muzzle velocity, penetration, weight, inventory size, spawn zones …); tune them or
/// clone an item under a new name that <c>#SpawnItem</c> finds.
/// </summary>
public sealed class WeaponsPageViewModel : ModulePageViewModel
{
    private static readonly HashSet<string> KeyNames = new(StringComparer.Ordinal)
    {
        "DamagePerShot", "MaxRange", "ROF", "MaxLoadedAmmo", "EventMaxAmmo", "BurstShotsCount", "WeaponFiringStateType",
        "WeaponCategory", "ZeroRangeStep", "ZeroRangeMax", "_capacity", "MaxAmmoCount", "MuzzleVelocity", "InitialDamage",
        "InitialDamageInGameEvent", "PenetrationFactor", "Caption", "Description", "Weight", "GridInventoryRowSpan",
        "GridInventoryColumnSpan", "_rarity", "_noiseLevel", "_damageOverTime", "Multiplier", "AddImpulseOnHit",
        "PitchMin", "PitchMax", "YawMin", "YawMax", "IsCarriedWithTwoHands", "_spawnRotationRandomization",
        "Urban", "Rural", "Industrial", "Police", "MilitaryMedium", "MilitaryAdvanced", "Sport", "Market", "GasStation",
        "Damage", "Energy", "SharpnessSlash", "SharpnessPierce", "CombatAnimationPlayRateModifier", "DamageOnUse", "MaxHealth",
        "BasePurchasePrice", "BaseSalePrice", "RequiredFamePoints", "CanBePurchasedByPlayer", "CanBeSoldByPlayer", "MaxAmountPurchasedAtOnce",
    };

    /// <summary>Creates the page.</summary>
    public WeaponsPageViewModel(AppServices services, Action? openSetup = null)
        : base(services, openSetup, "weapons", "Weapons", "Tune weapons, magazines and ammunition or clone them under a new name",
            [ModdableKind.Weapon, ModdableKind.Magazine, ModdableKind.Ammo, ModdableKind.Projectile])
    {
    }

    /// <inheritdoc />
    protected override IReadOnlySet<string> KeyStats => KeyNames;

    /// <summary>
    /// Owner: "the page opens on the dildo; make the AKM the default and the top, firearms first, sorted sensibly". Guns by
    /// family (assault rifles first, the AKM and AK-47 at their top), then melee and spare parts; a clone right after its gun.
    /// </summary>
    protected override IEnumerable<ModuleItemViewModel> Order(IEnumerable<ModuleItemViewModel> items) =>
        items.Select(i => (Item: i, Rank: WeaponFamilies.Rank(TemplateLeaf(i), i.Asset.Category)))
            .OrderBy(e => e.Item.Asset.Kind)
            .ThenBy(e => e.Item.Asset.Kind == ModdableKind.Weapon ? e.Rank.Family : WeaponFamily.Other)
            .ThenBy(e => e.Item.Asset.Kind == ModdableKind.Weapon ? e.Rank.Order : 0)
            .ThenBy(e => e.Item.Asset.Kind == ModdableKind.Weapon ? string.Empty : e.Item.Asset.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => TemplateLeaf(e.Item), StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Item.IsClone)
            .ThenBy(e => e.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Item);

    /// <summary>A weapon's family ("Assault rifle") under its name; other kinds keep their folder.</summary>
    protected override string? GroupLabel(ModuleItemViewModel item) => item.Asset.Kind == ModdableKind.Weapon
        ? Localization.Loc.T(FamilyKey(WeaponFamilies.FamilyOf(TemplateLeaf(item), item.Asset.Category)))
        : null;

    private static string TemplateLeaf(ModuleItemViewModel item) => ModuleItemViewModel.ShortName(item.CloneOf ?? item.PackagePath);

    /// <summary>The string key of a family's name (<c>Weapons.Family.AssaultRifle</c>).</summary>
    internal static string FamilyKey(WeaponFamily family) => "Weapons.Family." + family;

    /// <inheritdoc />
    protected override IReadOnlyList<ModuleFilter> BuildFilters(IReadOnlyList<ModuleItemViewModel> items)
    {
        var filters = new List<ModuleFilter> { new(Localization.Loc.T("Module.Filter.All"), null, null) };
        foreach (var (kind, label) in new[]
                 {
                     (ModdableKind.Weapon, Localization.Loc.T("Module.Filter.Weapons")), (ModdableKind.Magazine, Localization.Loc.T("Module.Filter.Magazines")),
                     (ModdableKind.Ammo, Localization.Loc.T("Module.Kind.Ammo")), (ModdableKind.Projectile, Localization.Loc.T("Module.Filter.Projectiles")),
                 })
        {
            if (items.Any(i => i.Asset.Kind == kind))
            {
                filters.Add(new ModuleFilter(label, kind, null));
            }

            if (kind == ModdableKind.Weapon && items.Any(i => i.Asset.Category == ModdableAssets.MeleeCategory))
            {
                filters.Add(new ModuleFilter(Localization.Loc.T("Module.Filter.Melee"), kind, ModdableAssets.MeleeCategory));
            }
        }

        return filters;
    }
}

/// <summary>
/// Items module (CwHedgehog: "duplicate some in-game items, such as copper coins; at the moment only weapons can be
/// duplicated"): every other inventory item of the game files (coins, medicine, food, resources, clothes, weapon
/// attachments …) with its stored values (name, description, weight, stack or uses, inventory size, spawn rarity, trade
/// row …); tune them or clone one under a new name that <c>#SpawnItem</c> finds, with its own class, entity setup and
/// registry records, exactly as the Weapons page clones a weapon.
/// </summary>
public sealed class ItemsPageViewModel : ModulePageViewModel
{
    private static readonly HashSet<string> KeyNames = new(StringComparer.Ordinal)
    {
        "Caption", "Description", "Weight", "MaxQuantity", "GridInventoryRowSpan", "GridInventoryColumnSpan", "MaxHealth",
        "ShelfLife", "_rarity", "_isIgnoredBySpawners", "_noiseLevel", "_damageOverTime",
        "Urban", "Rural", "Industrial", "Police", "MilitaryBasic", "MilitaryMedium", "MilitaryAdvanced", "MilitaryWW2", "Sport", "Market", "GasStation",
        "BasePurchasePrice", "BaseSalePrice", "RequiredFamePoints", "CanBePurchasedByPlayer", "CanBeSoldByPlayer", "MaxAmountPurchasedAtOnce",
    };

    /// <summary>Creates the page.</summary>
    public ItemsPageViewModel(AppServices services, Action? openSetup = null)
        : base(services, openSetup, "items", "Items", "Tune any inventory item or clone it under a new name", [ModdableKind.Item])
    {
    }

    /// <inheritdoc />
    protected override IReadOnlySet<string> KeyStats => KeyNames;

    /// <inheritdoc />
    protected override IReadOnlyList<ModuleFilter> BuildFilters(IReadOnlyList<ModuleItemViewModel> items) =>
        new[] { new ModuleFilter(Localization.Loc.T("Module.Filter.All"), null, null) }
            .Concat(items.Select(i => i.Asset.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(c => new ModuleFilter(c, ModdableKind.Item, c)))
            .ToList();
}
