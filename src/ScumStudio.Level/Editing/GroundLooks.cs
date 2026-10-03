namespace ScumStudio.Level.Editing;

/// <summary>How the island's ground looks (owner: "make the map snowy, a desert, autumn, or all grass").</summary>
public enum GroundLook
{
    /// <summary>The game's own ground.</summary>
    Game,

    /// <summary>Snow on every soft ground; snow plants instead of grass.</summary>
    Snow,

    /// <summary>Sand on every soft ground; dry plants instead of grass.</summary>
    Desert,

    /// <summary>Dry grass instead of green; dry plants.</summary>
    Autumn,

    /// <summary>Green grass on every soft ground (fields, forest floor, soil, beaches).</summary>
    Grass,
}

/// <summary>
/// The ground looks as <see cref="ReplaceAssetOp"/>s: the landscape material draws fixed textures (no parameters a mod
/// could change), so a look puts another ground texture of the game in place of each soft-ground texture, and another
/// grass type in place of the grass that grows on it. Roads (asphalt, gravel, crushed rock), rocks, cliffs, rivers and
/// the sea floor keep theirs. Faraway ground comes from the game's pre-baked view and keeps its colour.
/// </summary>
public static class GroundLooks
{
    private const string Textures = "/Game/ConZ_Files/Landscape/LandscapeTextures/";
    private const string FarTextures = "/Game/ConZ_Files/Landscape/Textures/";
    private const string Grass = "/Game/ConZ_Files/Landscape/Procedural_Generation/";

    private static readonly string[] GreenGround =
    [
        Textures + "T_Grass_Continental_D", Textures + "T_Continental_Forest_D", Textures + "T_Pine_Forest_D", Textures + "T_PineForestGround_D",
        Textures + "T_Coastal_Grass_D", FarTextures + "T_Grass_Continental_Far_D",
    ];

    private static readonly string[] BareGround =
    [
        Textures + "T_Field_D", Textures + "T_RockySoil_D", Textures + "T_ForestGroundRocks_D", FarTextures + "T_Field_Distant_D",
    ];

    private static readonly string[] Beach = [Textures + "T_Beach_Sand_D", Textures + "T_Beach_Pebbles_D"];

    private static readonly string[] GreenGrassTypes =
    [
        Grass + "Grass_Continental_LandscapeGrassType", Grass + "Grass_Continental_01_Tall", Grass + "Field_01_LandscapeGrassType",
        Grass + "Forest_Continental_01_LandscapeGrassType", Grass + "Forest_Continental_02_LandscapeGrassType", Grass + "Forest_Continental_03_LandscapeGrassType",
        Grass + "Forest_Continental_Pine_LandscapeGrassType", Grass + "Forest_Continental_Pine_DeadPlants_LandscapeGrassType",
        Grass + "Coastal_Forest_LandscapeGrassType", Grass + "Default_Height_Slope_LandscapeGrassType", Grass + "Gravel_03_Grass_01_LandscapeGrassType",
    ];

    private static readonly string[] DryGrassTypes = [Grass + "Coastal_Dry_LandscapeGrassType", Grass + "Grass_Continental_Tall_DryLandscapeGrassType"];

    /// <summary>Every package a look may replace (switching looks puts the ones the new look leaves back).</summary>
    public static IEnumerable<string> Targets => GreenGround.Concat(BareGround).Concat(Beach).Concat(GreenGrassTypes).Concat(DryGrassTypes);

    /// <summary>Package → the game package drawn in its place for <paramref name="look"/> (empty for <see cref="GroundLook.Game"/>).</summary>
    public static IReadOnlyDictionary<string, string> Replacements(GroundLook look)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Put(IEnumerable<string> targets, string with)
        {
            foreach (var target in targets.Where(t => !string.Equals(t, with, StringComparison.OrdinalIgnoreCase)))
            {
                map[target] = with;
            }
        }

        switch (look)
        {
            case GroundLook.Snow:
                Put(GreenGround.Concat(BareGround).Concat(Beach), "/Game/ConZ_Files/Materials/Snow/T_GroundSnow_03_D");
                Put(GreenGrassTypes.Concat(DryGrassTypes), Grass + "Snow_LandscapeGrassType");
                break;
            case GroundLook.Desert:
                Put(GreenGround.Concat(BareGround), Textures + "T_Beach_Sand_D");
                Put(GreenGrassTypes.Concat(DryGrassTypes), Grass + "Coastal_Dry_LandscapeGrassType");
                break;
            case GroundLook.Autumn:
                Put(GreenGround, Textures + "T_Coastal_Grass_D");
                Put(GreenGrassTypes, Grass + "Grass_Continental_Tall_DryLandscapeGrassType");
                break;
            case GroundLook.Grass:
                Put(BareGround.Concat(Beach).Append(Textures + "T_Coastal_Grass_D"), Textures + "T_Grass_Continental_D");
                break;
        }

        return map;
    }

    /// <summary>The look <paramref name="state"/> has (the one whose replacements it holds exactly), or null when it is mixed.</summary>
    public static GroundLook? Current(EditState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var targets = Targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var held = state.AssetReplacements.Where(r => targets.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var look in Enum.GetValues<GroundLook>())
        {
            var wanted = Replacements(look);
            if (wanted.Count == held.Count && wanted.All(w => held.TryGetValue(w.Key, out var v) && string.Equals(v, w.Value, StringComparison.OrdinalIgnoreCase)))
            {
                return look;
            }
        }

        return null;
    }

    /// <summary>
    /// The edits that give <paramref name="state"/> the look <paramref name="look"/>: every look target set to the look's
    /// replacement or back to the game's own; only packages <paramref name="exists"/> knows (a game update may drop one).
    /// </summary>
    public static IReadOnlyList<ReplaceAssetOp> Edits(EditState state, GroundLook look, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(exists);
        var wanted = Replacements(look);
        var ops = new List<ReplaceAssetOp>();
        foreach (var target in Targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var with = wanted.GetValueOrDefault(target);
            var now = state.GetReplacement(target);
            if (string.Equals(now, with, StringComparison.OrdinalIgnoreCase) || (with is not null && (!exists(target) || !exists(with))))
            {
                continue;
            }

            ops.Add(new ReplaceAssetOp(target, now, with));
        }

        return ops;
    }
}
