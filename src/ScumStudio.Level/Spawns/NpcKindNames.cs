using System.Globalization;

namespace ScumStudio.Level.Spawns;

/// <summary>How dangerous the zone settings an NPC kind comes from are (the game's <c>LTZ</c> / <c>MTZ</c> / <c>HTZ</c> prefixes).</summary>
public enum NpcThreat
{
    /// <summary>The name does not say.</summary>
    Unknown,

    /// <summary>Low threat zone (<c>LTZ_</c>).</summary>
    Low,

    /// <summary>Medium threat zone (<c>MTZ_</c>).</summary>
    Medium,

    /// <summary>High threat zone (<c>HTZ_</c>).</summary>
    High,
}

/// <summary>
/// Readable names for the NPC wizard: the game names its NPC encounters <c>MTZ_POI_Military_Base_NPC_Encounter</c> and its
/// characters <c>BP_Guard_Lvl_3</c>; the user is shown "Military Base" (medium threat) and "Guard Lvl 3". Only the names are
/// tidied, nothing is looked up, so a name the rules do not know is shown as the game spells it.
/// </summary>
public static class NpcKindNames
{
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase) { "POI", "NPC", "NPCs", "Encounter", "BP", "C" };

    /// <summary>The threat level in an encounter's name (<c>MTZ_POI_Military_Base_NPC_Encounter</c> is medium).</summary>
    /// <param name="encounterName">The encounter class's name.</param>
    public static NpcThreat ThreatOf(string encounterName)
    {
        ArgumentNullException.ThrowIfNull(encounterName);
        return Tokens(encounterName).FirstOrDefault()?.ToUpperInvariant() switch
        {
            "LTZ" => NpcThreat.Low,
            "MTZ" => NpcThreat.Medium,
            "HTZ" => NpcThreat.High,
            _ => NpcThreat.Unknown,
        };
    }

    /// <summary>The place an encounter belongs to, without the threat prefix and the filler words (<c>Military Base</c>); the whole name, readable, when nothing is left.</summary>
    /// <param name="encounterName">The encounter class's name.</param>
    public static string PlaceOf(string encounterName)
    {
        ArgumentNullException.ThrowIfNull(encounterName);
        var tokens = Tokens(encounterName).ToList();
        if (tokens.Count > 0 && ThreatOf(encounterName) != NpcThreat.Unknown)
        {
            tokens.RemoveAt(0);
        }

        var words = tokens.Where(t => !Dropped.Contains(t)).Select(Words).ToList();
        return words.Count == 0 ? Words(encounterName) : string.Join(' ', words);
    }

    /// <summary>A character Blueprint's name tidied (<c>BP_Guard_Lvl_3</c> is <c>Guard Lvl 3</c>, <c>Drifter_Class_01</c> is <c>Drifter Class 1</c>).</summary>
    /// <param name="character">The character Blueprint's name.</param>
    public static string CharacterOf(string character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var words = Tokens(character).Where(t => !Dropped.Contains(t)).Select(Words).ToList();
        return words.Count == 0 ? Words(character) : string.Join(' ', words);
    }

    private static IEnumerable<string> Tokens(string name) => name.Split('_', StringSplitOptions.RemoveEmptyEntries);

    // One token as a word: a number loses its leading zeros ("01" is "1"); a word keeps its capitals ("Military", "AI").
    private static string Words(string token) =>
        int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number.ToString(CultureInfo.InvariantCulture) : token.Replace('_', ' ');
}
