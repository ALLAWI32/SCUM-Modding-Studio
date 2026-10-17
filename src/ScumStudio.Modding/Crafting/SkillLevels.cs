namespace ScumStudio.Modding.Crafting;

/// <summary>
/// The amount of one ingredient at each of the player's skill levels (CwHedgehog: "adjust the materials required for each
/// level of Engineering/Survival"). A recipe slot stores it as <c>Amount</c>, an <c>FPerSkillLevelIntegerData</c> of five
/// ints (SCUM 1.3.3 SDK; every one of the game's 3,004 recipe slots stores all five), and the game asks the player for the
/// one of their level in the recipe's <c>RelevantSkill</c> (Engineering for buildings, Survival, Demolition, Rifles or
/// Medical for items).
/// </summary>
/// <param name="NoSkill">Without the skill.</param>
/// <param name="Basic">Basic level.</param>
/// <param name="Medium">Medium level.</param>
/// <param name="Advanced">Advanced level.</param>
/// <param name="AboveAdvanced">Above advanced level.</param>
public readonly record struct LevelAmounts(int NoSkill, int Basic, int Medium, int Advanced, int AboveAdvanced)
{
    /// <summary>The level names as the game stores them, lowest first (<c>ESkillLevel</c>).</summary>
    public static IReadOnlyList<string> Names { get; } = ["NoSkill", "Basic", "Medium", "Advanced", "AboveAdvanced"];

    /// <summary>The amount at level <paramref name="level"/> (0 = no skill … 4 = above advanced).</summary>
    public int this[int level] => level switch
    {
        0 => NoSkill,
        1 => Basic,
        2 => Medium,
        3 => Advanced,
        4 => AboveAdvanced,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>The five amounts, lowest level first.</summary>
    public int[] ToArray() => [NoSkill, Basic, Medium, Advanced, AboveAdvanced];

    /// <summary>The amounts of <paramref name="values"/> (five, lowest level first).</summary>
    public static LevelAmounts From(IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Count == 5 ? new(values[0], values[1], values[2], values[3], values[4]) : throw new ArgumentException("Five amounts, one per skill level.", nameof(values));
    }

    /// <summary>What the studio writes for <paramref name="amount"/> without skill when no level is set (<see cref="RecipeIngredients.PerSkill"/>).</summary>
    public static LevelAmounts Auto(int amount) => From(RecipeIngredients.PerSkill(Math.Max(1, amount)));

    /// <summary>Each amount within 1 and <paramref name="max"/>: a slot never asks for nothing (no recipe of the game does).</summary>
    public LevelAmounts Clamped(int max = 9999) =>
        new(Math.Clamp(NoSkill, 1, max), Math.Clamp(Basic, 1, max), Math.Clamp(Medium, 1, max), Math.Clamp(Advanced, 1, max), Math.Clamp(AboveAdvanced, 1, max));

    /// <inheritdoc />
    public override string ToString() => string.Join('/', ToArray());
}

/// <summary>One ingredient slot of a recipe as the game stores it.</summary>
/// <param name="Tags">The tags it accepts (<c>AllowedTypes</c>, one or more <c>CI_*</c> names).</param>
/// <param name="IsTool">A tool (used, not consumed).</param>
/// <param name="Amount">The amount per skill level.</param>
public sealed record RecipeSlot(IReadOnlyList<string> Tags, bool IsTool, LevelAmounts Amount)
{
    /// <summary>The tags as one text (<c>CI_Group_Logs|CI_Group_Logs_Small</c>), how an override names the slot.</summary>
    public string TagText => string.Join('|', Tags);
}

/// <summary>The amounts per skill level of one slot of a game recipe the user overrides.</summary>
/// <param name="Tags">The slot's tags as the game stored them when it was set (<see cref="RecipeSlot.TagText"/>): a game update that changes the slot is found.</param>
/// <param name="Amount">The amounts written.</param>
public sealed record StockSlotAmounts(string Tags, LevelAmounts Amount);

/// <summary>
/// A recipe of the game whose amounts per skill level the user changed (one entry of <see cref="CraftablesFile.RecipeOverrides"/>).
/// The Craftables pak ships the game's recipe at its own path with only those amounts changed: another mod that changes
/// the same recipe conflicts with it (the pak loaded last wins), and a game update that changes the recipe needs a new export.
/// </summary>
public sealed record StockRecipeOverride
{
    /// <summary>The game's recipe (package path, <c>/Game/ConZ_Files/Items/Crafting/Recipes/…/CR_*</c>).</summary>
    public required string Recipe { get; init; }

    /// <summary>Every slot of the recipe, in the game's order.</summary>
    public IReadOnlyList<StockSlotAmounts> Slots { get; init; } = [];
}
