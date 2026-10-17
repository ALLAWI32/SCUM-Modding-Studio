namespace ScumStudio.Modding.Crafting;

/// <summary>Where a craftable goes once made.</summary>
public enum CraftUse
{
    /// <summary>Placed in the world like base building (a placeable recipe).</summary>
    Build,

    /// <summary>Goes to the bag (an item recipe).</summary>
    Craft,

    /// <summary>Both: built in the world and crafted into the bag.</summary>
    Both,
}

/// <summary>
/// A <see cref="CraftKind.Element"/> craftable: the game's base element (or placeable Blueprint) whose whole family is
/// cloned, and the values its levels change (<c>UBaseBuildingComponent</c>, SDK <c>SCUM_classes.hpp</c>).
/// </summary>
public sealed record ElementSettings
{
    /// <summary>The base element package the family is cloned from (<c>/Game/…/BaseElements/BP_Bed_Improvised</c>).</summary>
    public required string Template { get; init; }

    /// <summary>
    /// Values per level: index 0 is the element, 1 and on its upgrades in the game's chain (<c>_upgradeRecipe</c>), each a
    /// property of <see cref="Editable"/> and its value as text; a missing key keeps the game's value.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Levels { get; init; } = [];

    /// <summary>What the item the element spawns holds, and which item that is (a chest's size, the fridge), or null.</summary>
    public StorageSettings? Storage { get; init; }

    /// <summary>The element values the page offers (<c>UBaseBuildingComponent</c> properties).</summary>
    public static IReadOnlyList<string> Editable { get; } =
    [
        "_healthPoints",
        "_hoursToDecay",
        "_hoursToDecayOutsideFlagArea",
        "_canBeDamaged",
        "_explosionResistance",
        "_canBePlacedWithoutFlag",
        "_canBePlacedInBuilding",
        "_canBePlacedInRestrictedArea",
        "_canBePlacedOnOtherBaseElements",
        "_maxInstancesPerFlagArea",
        "_maxInstancesPerSquad",
        "_isUsedForRespawn",
    ];
}

/// <summary>
/// The container item an <see cref="CraftKind.Element"/> craftable spawns (<see cref="ItemStorage"/>): the item (null = its
/// element's own, a chest's chest) and the storage values its copy changes.
/// </summary>
public sealed record StorageSettings
{
    /// <summary>
    /// A container item of the game the element spawns instead of its own (package path; the fridge on the large cabinet),
    /// or null for its own.
    /// </summary>
    public string? Item { get; init; }

    /// <summary>Changed values of <see cref="ItemStorage.Editable"/> (<c>NumRows</c> …) as text; a missing key keeps the game's.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}
