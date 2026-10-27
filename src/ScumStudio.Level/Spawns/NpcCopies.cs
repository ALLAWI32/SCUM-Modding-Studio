using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Level.Spawns;

/// <summary>An NPC's own copy: the clone of its whole chain, the NPC kind that names the copy and the value edits that make it the only one spawned.</summary>
/// <param name="Chain">One <see cref="CloneAssetOp"/> of kind <see cref="ZoneCopies.Kind"/>: the zone settings first, then the encounter, the preset, the character and its common data.</param>
/// <param name="Type">The NPC kind as the copy names it: its encounter, character and zone settings are the copies.</param>
/// <param name="Tunes">Edits of the copies' values, recorded after the clone (the other presets and characters weigh 0).</param>
public sealed record NpcCopy(CloneAssetOp Chain, NpcGuardType Type, IReadOnlyList<EditOp> Tunes);

/// <summary>
/// "Own copy of this NPC" (owner: "can we copy an NPC and place it without touching the others?"). A game NPC is a chain
/// of shared packages: the zone settings weigh encounters, an encounter weighs character presets, a preset names character
/// Blueprints and a Blueprint points at its common data (health, weapons, loot). Editing one of them changes every place and
/// every NPC that shares it. A copy renames the chain together (the studio's <see cref="PackageMap"/> follows the references
/// between its members, so the copies point at each other and nothing else points at them): the zone settings, the
/// encounter, the one preset that holds the chosen character, the character and its common data. The other presets and
/// characters of the copied encounter and preset keep their stock packages but weigh 0, so the copy spawns only the chosen
/// NPC. The result is one <see cref="CloneAssetOp"/> of kind <see cref="ZoneCopies.Kind"/> (a zone copy that also carries its
/// NPC), which the exporter builds with the other clones.
/// </summary>
public static class NpcCopies
{
    /// <summary>The text the copies' names end in (<c>BP_Guard_Lvl_3_Own</c>); a letter follows when it is taken (<c>_OwnB</c>).</summary>
    public const string SuffixStem = "_Own";

    /// <summary>
    /// The copy of one NPC of <paramref name="type"/>. Reads the encounter, the preset and the character Blueprint, and checks
    /// the new names against every package of the game files: call it on a worker.
    /// </summary>
    /// <param name="catalog">The game files.</param>
    /// <param name="type">The NPC kind (encounter and zone settings).</param>
    /// <param name="character">The character to copy, by Blueprint name (<c>BP_Guard_Lvl_3</c>).</param>
    /// <param name="planned">Packages the project already creates, which the new names may not take.</param>
    /// <exception cref="ArgumentException">The kind stores no presets of its own, no preset has the character, or a package cannot be read.</exception>
    public static NpcCopy Plan(AssetCatalog catalog, NpcGuardType type, string character, IEnumerable<string>? planned = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(character);

        var presets = SpawnChoices.StoredPresets(catalog, type.EncounterClass);
        if (presets.Count == 0)
        {
            throw new ArgumentException($"{type.Name} draws its character presets from a parent class, so a copy of it cannot spawn only {character}.", nameof(type));
        }

        // The preset that holds the character, and the character's Blueprint as that preset names it.
        string? blueprint = null;
        var chosen = presets.FirstOrDefault(p =>
        {
            blueprint = SpawnChoices.PresetCharacters(catalog, p.Preset).FirstOrDefault(c => SpawnChoices.ClassName(c).Equals(character, StringComparison.OrdinalIgnoreCase));
            return blueprint is not null;
        }) ?? throw new ArgumentException($"No character preset of {type.Name} has {character}.", nameof(character));

        var members = new List<string>
        {
            type.ZonePackage,
            Package(type.EncounterClass),
            Package(chosen.Preset),
            Package(blueprint!),
        };
        if (CommonDataOf(catalog, Package(blueprint!)) is { } commonData && !members.Contains(commonData, StringComparer.OrdinalIgnoreCase))
        {
            members.Add(commonData);
        }

        var taken = ZoneCopies.Taken(catalog, planned);
        var suffix = SuggestSuffix(members.Select(Leaf), taken);
        var pairs = members.Select(m => new PackagePair(m, m[..(m.LastIndexOf('/') + 1)] + Leaf(m) + suffix)).ToList();
        var clone = new CloneAssetOp(ZoneCopies.Kind, type.ZonePackage, pairs[0].New, pairs);

        // The copies' names, as the references inside the copies read them.
        var map = new PackageMap(pairs.Select(p => new KeyValuePair<string, string>(p.Old, p.New)));
        var newEncounter = map.Map(Package(type.EncounterClass))!;
        var newType = new NpcGuardType($"{newEncounter}.{Leaf(newEncounter)}_C", [Leaf(Package(blueprint!)) + suffix], pairs[0].New);

        // Only the chosen preset keeps its weight in the copied encounter (a stock weight of 0 becomes 100: it was off).
        var tunes = new List<EditOp>();
        var encounterExport = map.RemapObject(chosen.Export);
        foreach (var preset in presets)
        {
            var weight = SameObject(preset.Preset, chosen.Preset) ? (preset.Weight > 0f ? preset.Weight : 100f) : 0f;
            if (weight != preset.Weight)
            {
                tunes.Add(new SetAssetValueOp(newEncounter, encounterExport, preset.Path, nameof(TunableKind.Float), TunableValue.Format(preset.Weight), TunableValue.Format(weight)));
            }
        }

        // The same inside the copied preset: other characters of it weigh 0.
        if (SpawnChoices.ForPreset(catalog, Package(chosen.Preset)) is { } characters)
        {
            var presetPackage = map.Map(Package(chosen.Preset))!;
            var presetExport = map.RemapObject(characters.Export);
            foreach (var row in characters.Entries.Where(e => e.Path is not null))
            {
                var mine = row.Name.Equals(character, StringComparison.OrdinalIgnoreCase);
                var weight = mine ? (row.Weight > 0f ? row.Weight : 1f) : 0f;
                if (weight != row.Weight)
                {
                    tunes.Add(new SetAssetValueOp(presetPackage, presetExport, row.Path!, nameof(TunableKind.Float), TunableValue.Format(row.Weight), TunableValue.Format(weight)));
                }
            }
        }

        return new NpcCopy(clone, newType, tunes);
    }

    /// <summary>
    /// <paramref name="stock"/>, the weights of the stock zone settings, as the copy <paramref name="clone"/> reads them: the
    /// copy's package and export, and each entry named after its copied encounter (the entries of the encounters that were
    /// not copied keep their names).
    /// </summary>
    /// <param name="stock">The stock zone settings' encounter list.</param>
    /// <param name="clone">The copy (<see cref="NpcCopy.Chain"/>).</param>
    public static SpawnWeightList Rows(SpawnWeightList stock, CloneAssetOp clone)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(clone);
        var map = new PackageMap(clone.Packages.Select(p => new KeyValuePair<string, string>(p.Old, p.New)));
        return stock with
        {
            Package = clone.NewPrimary,
            Export = map.RemapObject(stock.Export),
            Entries = stock.Entries.Select(e => e with { Name = map.RemapObject(e.Name) }).ToList(),
        };
    }

    /// <summary>
    /// The suffix that makes every name of <paramref name="leaves"/> free: <c>_Own</c>, else <c>_OwnB</c>, <c>_OwnC</c>, …
    /// (<c>_OwnAA</c> after <c>_OwnZ</c>). It always ends in a letter, so no name ends in <c>_&lt;number&gt;</c>, which the game reads as an FName number.
    /// </summary>
    /// <param name="leaves">The names (package leaves) that get the suffix.</param>
    /// <param name="taken">Every name that exists (<see cref="ZoneCopies.Taken"/>).</param>
    public static string SuggestSuffix(IEnumerable<string> leaves, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        ArgumentNullException.ThrowIfNull(taken);
        var names = leaves.ToList();
        for (var n = 0; ; n++)
        {
            var letters = string.Empty;
            for (var k = n; k > 0; k = (k - 1) / 26)
            {
                letters = (char)('A' + ((k - 1) % 26)) + letters;
            }

            // _Own, then _OwnB ... (A is skipped: _Own is the first)
            if (letters == "A")
            {
                continue;
            }

            var suffix = SuffixStem + letters;
            if (names.All(l => !taken.Contains(l + suffix)))
            {
                return suffix;
            }
        }
    }

    /// <summary>The common data (<c>_armedNPCBaseCommonData</c>) the character Blueprint's own defaults point at, or null when it stores none (its parent's are used).</summary>
    private static string? CommonDataOf(AssetCatalog catalog, string blueprintPackage)
    {
        var package = ModdableAssets.ReadPackage(catalog, blueprintPackage);
        var keys = TunableReader.ExportKeys(package);
        var cdo = Array.FindIndex(keys, k => k.StartsWith("Default__", StringComparison.Ordinal));
        if (cdo < 0 || package.ReadProperties(cdo).Find("_armedNPCBaseCommonData")?.Value is not ObjectValue { Index: not 0 } reference)
        {
            return null;
        }

        var path = package.GetFullPath(reference.Index);
        return path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ? Package(path) : null;
    }

    private static string Package(string objectPath)
    {
        var dot = objectPath.IndexOf('.', objectPath.LastIndexOf('/') + 1);
        return dot < 0 ? objectPath : objectPath[..dot];
    }

    private static string Leaf(string packagePath) => packagePath[(packagePath.LastIndexOf('/') + 1)..];

    private static bool SameObject(string a, string b) => string.Equals(Package(a), Package(b), StringComparison.OrdinalIgnoreCase);
}
