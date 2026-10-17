namespace ScumStudio.Modding.Catalog;

/// <summary>The kind of gun a SCUM weapon is, in the order the Weapons page lists them (firearms first).</summary>
public enum WeaponFamily
{
    /// <summary>AKM, AK-47, M16, SCAR-L …</summary>
    AssaultRifle,

    /// <summary>RPK-74, M249.</summary>
    MachineGun,

    /// <summary>MP5, UMP-45, MAC-10, Tommy gun.</summary>
    SubmachineGun,

    /// <summary>SVD, VSS, SCAR DMR, M82A1, AWM, AWP.</summary>
    SniperRifle,

    /// <summary>Mosin, Kar98k, M1 Garand, SKS, hunting rifles.</summary>
    Rifle,

    /// <summary>590A1, M1887, DT11B, SDASS, trench gun.</summary>
    Shotgun,

    /// <summary>Pistols and revolvers.</summary>
    Handgun,

    /// <summary>Bows and crossbows.</summary>
    Bow,

    /// <summary>RPG-7, AT4, grenade launchers, the flare gun.</summary>
    Launcher,

    /// <summary>The improvised (crafted) guns.</summary>
    Improvised,

    /// <summary>A firearm this list does not know (a new one in a game update).</summary>
    OtherFirearm,

    /// <summary>Knives, axes, bats (<c>New_Melee</c>).</summary>
    Melee,

    /// <summary>Spare weapon parts and anything else of the weapons folder.</summary>
    Other,
}

/// <summary>
/// Owner: "the first weapon the page opens is the dildo; make the AKM / AK-47 the default and the top of the list, firearms
/// first, sorted sensibly". Sorts the Weapons page by <see cref="WeaponFamily"/>, the well-known guns of each family first
/// (AKM, AK-47 …), then by name; a clone sits right after the gun it was made from.
/// </summary>
public static class WeaponFamilies
{
    /// <summary>Name tokens (after <c>Weapon_</c>) of each family, the best-known first: a name starting with a token belongs to it.</summary>
    private static readonly (WeaponFamily Family, string[] Tokens)[] Known =
    [
        (WeaponFamily.Improvised, ["Improvised", "Impro_"]),
        (WeaponFamily.AssaultRifle, ["AKM", "AK47", "AK15", "AKS_74U", "M16A4", "MK18", "SCAR_L", "AS_Val", "VHS"]),
        (WeaponFamily.MachineGun, ["RPK", "M249"]),
        (WeaponFamily.SubmachineGun, ["MP5", "UMP", "MAC10", "TommyGun"]),
        (WeaponFamily.SniperRifle, ["SVD", "VSS", "SCAR_DMR", "M82A1", "AWM", "AWP"]),
        (WeaponFamily.Rifle, ["MosinNagant", "98k", "M1_Garand", "SKS", "Hunter85", "Krueger"]),
        (WeaponFamily.Shotgun, ["590A11", "M1887", "DT11B", "SDASS", "Trench_Gun"]),
        (WeaponFamily.Handgun, ["M1911", "M9", "DEagle", "Deagle", "Block21", "BayRifferBlock21", "HS9", "SF19", "Judge44", "PeaceKeeper38", "Serpent357", "Viper_M357"]),
        (WeaponFamily.Bow, ["CarbonHunter", "CarbonBear", "BlackHawk_Crossbow", "Crossbow"]),
        (WeaponFamily.Launcher, ["RPG7", "AT4", "ImprovisedGrenadeLauncher", "FlareGun"]),
    ];

    /// <summary>The family of a weapon package leaf (<c>Weapon_AKM</c>, <c>1H_Dildo</c>) in category <paramref name="category"/>.</summary>
    public static WeaponFamily FamilyOf(string leaf, string category) => Rank(leaf, category).Family;

    /// <summary>
    /// The list position of a weapon: its family, then the place of its token in the family (the AKM first of the assault
    /// rifles), then a new gun the list does not know. Sort by this, then by name.
    /// </summary>
    public static (WeaponFamily Family, int Order) Rank(string leaf, string category)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        if (string.Equals(category, ModdableAssets.MeleeCategory, StringComparison.OrdinalIgnoreCase))
        {
            return (WeaponFamily.Melee, 0);
        }

        if (!leaf.StartsWith("Weapon_", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("Weapon_Parts", StringComparison.OrdinalIgnoreCase))
        {
            return (WeaponFamily.Other, 0);
        }

        var name = leaf["Weapon_".Length..];
        // The longest matching token wins (ImprovisedGrenadeLauncher is a launcher, Improvised_Rifle an improvised gun).
        (WeaponFamily Family, int Order, int Length) best = (WeaponFamily.OtherFirearm, 0, 0);
        foreach (var (family, tokens) in Known)
        {
            for (var i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Length > best.Length && name.StartsWith(tokens[i], StringComparison.OrdinalIgnoreCase))
                {
                    best = (family, i, tokens[i].Length);
                }
            }
        }

        return (best.Family, best.Order);
    }
}
