namespace ScumStudio.Level.Spawns;

/// <summary>The share of one character among everything a zone spawns.</summary>
/// <param name="Character">The character (<c>BP_Zombie_Civilian_Fat_Male</c>, <c>BP_Drifter_01</c>), or the encounter or preset when its own list is unknown.</param>
/// <param name="Kind">What it is (from its preset's list).</param>
/// <param name="Percent">Its share, 0–100.</param>
public sealed record SpawnChance(string Character, SpawnCategory Kind, double Percent);

/// <summary>
/// The real percentages behind a zone's weights (owner: "the percentage of every zombie"): the game draws an encounter by
/// its weight in the zone's settings, a preset by its weight in the encounter, a character by its weight in the preset, so
/// a character's share is P(encounter) × P(preset | encounter) × P(character | preset), summed over every way to reach it.
/// A weight of 0 turns its branch off; a list whose weights are all 0 spawns nothing.
/// </summary>
public static class SpawnChances
{
    /// <summary>
    /// The share of every character of <paramref name="lists"/> (the lists of one zone, <see cref="SpawnChoices.ForZone"/>,
    /// or of one preset), largest first, summing to 100; empty when nothing can spawn. The list of the lowest level is
    /// the draw; an entry without a list of its own counts under its own name.
    /// </summary>
    public static IReadOnlyList<SpawnChance> Effective(IReadOnlyList<SpawnWeightList> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        if (lists.Count == 0)
        {
            return [];
        }

        var root = lists.MinBy(l => l.Level)!;
        var byEntry = new Dictionary<(int Level, string Name), SpawnWeightList>();
        foreach (var list in lists.Where(l => l.Level > root.Level))
        {
            byEntry.TryAdd((list.Level, list.Of), list);
        }

        var shares = new Dictionary<(string, SpawnCategory), double>();
        Walk(root, 1d, 0);
        var total = shares.Values.Sum();
        return total <= 0d
            ? []
            : shares.Where(s => s.Value > 0d)
                .Select(s => new SpawnChance(s.Key.Item1, s.Key.Item2, s.Value / total * 100d))
                .OrderByDescending(s => s.Percent).ThenBy(s => s.Character, StringComparer.OrdinalIgnoreCase)
                .ToList();

        void Walk(SpawnWeightList list, double chance, int depth)
        {
            var sum = list.Entries.Sum(e => Math.Max(e.Weight, 0f));
            if (sum <= 0f || depth > 4)
            {
                return; // all off
            }

            foreach (var entry in list.Entries.Where(e => e.Weight > 0f))
            {
                var share = chance * entry.Weight / sum;
                if (byEntry.TryGetValue((list.Level + 1, entry.Name), out var below))
                {
                    Walk(below, share, depth + 1);
                }
                else
                {
                    var key = (entry.Name, list.Category);
                    shares[key] = shares.GetValueOrDefault(key) + share;
                }
            }
        }
    }
}
