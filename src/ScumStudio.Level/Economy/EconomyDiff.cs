using ScumStudio.Level.Editing;

namespace ScumStudio.Level.Economy;

/// <summary>
/// What changed between two economies (the summary after an import, owner #25: "a diff summary after import"), and the
/// journal steps that turn one into the other (<see cref="Steps"/>, a code edit applied as the fewest single changes).
/// </summary>
/// <param name="SettingsChanged">Settings added, removed or with another value.</param>
/// <param name="TradersAdded">Sections only the new file has.</param>
/// <param name="TradersRemoved">Sections only the old file has.</param>
/// <param name="TradersChanged">Sections both have, written differently.</param>
/// <param name="ItemsAdded">Entries only the new file has (whole new sections included).</param>
/// <param name="ItemsRemoved">Entries only the old file has (whole removed sections included).</param>
/// <param name="ItemsChanged">Entries both have, written differently.</param>
/// <param name="ChangedTraders">Every section added, removed or changed, in the new file's order (removed ones last).</param>
public sealed record EconomyDiff(
    int SettingsChanged, int TradersAdded, int TradersRemoved, int TradersChanged, int ItemsAdded, int ItemsRemoved, int ItemsChanged,
    IReadOnlyList<string> ChangedTraders)
{
    /// <summary>A code edit with more single changes than this is one whole-file step.</summary>
    public const int MaxSteps = 200;

    /// <summary>A section with more changed entries than this is one whole-section step.</summary>
    public const int MaxSectionSteps = 50;

    /// <summary>True when nothing changed.</summary>
    public bool IsEmpty => SettingsChanged + TradersAdded + TradersRemoved + TradersChanged == 0;

    /// <summary>Compares two economies: settings by key, sections by name, entries by <c>tradeable-code</c>.</summary>
    public static EconomyDiff Compare(EconomyOverride before, EconomyOverride after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var (oldSettings, newSettings) = (SettingMap(before), SettingMap(after));
        var settings = oldSettings.Keys.Union(newSettings.Keys).Count(k => oldSettings.GetValueOrDefault(k) != newSettings.GetValueOrDefault(k));

        var oldSections = before.Sections.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var newNames = after.Sections.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0, removed = 0, changed = 0, itemsAdded = 0, itemsRemoved = 0, itemsChanged = 0;
        var names = new List<string>();
        foreach (var section in after.Sections)
        {
            if (!oldSections.TryGetValue(section.Name, out var old))
            {
                added++;
                itemsAdded += section.Entries.Count;
                names.Add(section.Name);
                continue;
            }

            if (old.Text == section.Text)
            {
                continue;
            }

            changed++;
            names.Add(section.Name);
            var (was, now) = (EntryMap(old), EntryMap(section));
            itemsAdded += now.Keys.Count(k => !was.ContainsKey(k));
            itemsRemoved += was.Keys.Count(k => !now.ContainsKey(k));
            itemsChanged += now.Count(e => was.TryGetValue(e.Key, out var text) && text != e.Value);
        }

        foreach (var section in before.Sections.Where(s => !newNames.Contains(s.Name)))
        {
            removed++;
            itemsRemoved += section.Entries.Count;
            names.Add(section.Name);
        }

        return new EconomyDiff(settings, added, removed, changed, itemsAdded, itemsRemoved, itemsChanged, names);
    }

    /// <summary>
    /// The journal steps that turn <paramref name="before"/> into <paramref name="after"/>, applied in order with
    /// <see cref="EconomyOverride.Apply"/>: one per setting, per added or removed section and per entry (by code and
    /// index). A section whose entries were reordered, or with more than <see cref="MaxSectionSteps"/> changes, is one step;
    /// a changed order of settings or sections, a changed frame, or more than <see cref="MaxSteps"/> steps is one
    /// <see cref="ReplaceEconomyOp"/>. The steps are checked on a copy: they always give <paramref name="after"/>'s text.
    /// Empty when the two are the same.
    /// </summary>
    public static IReadOnlyList<EconomyOp> Steps(EconomyOverride before, EconomyOverride after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var target = after.ToJson();
        var source = before.ToJson();
        if (target == source)
        {
            return [];
        }

        EconomyOp[] whole = [new ReplaceEconomyOp(source, target)];
        var steps = new List<EconomyOp>();
        if (!SettingSteps(before, after, steps) || !SectionSteps(before, after, steps) || steps.Count > MaxSteps)
        {
            return whole;
        }

        var probe = before.Clone();
        foreach (var step in steps)
        {
            probe.Apply(step);
        }

        return probe.ToJson() == target ? steps : whole;
    }

    private static bool SettingSteps(EconomyOverride before, EconomyOverride after, List<EconomyOp> steps)
    {
        var (was, now) = (before.SettingMembers, after.SettingMembers);
        var (wasKeys, nowKeys) = (was.Select(m => m.Key).ToList(), now.Select(m => m.Key).ToList());
        if (wasKeys.Distinct().Count() != wasKeys.Count || nowKeys.Distinct().Count() != nowKeys.Count
            || !wasKeys.Where(nowKeys.Contains).SequenceEqual(nowKeys.Where(wasKeys.Contains)))
        {
            return false;
        }

        for (var i = was.Count - 1; i >= 0; i--)
        {
            if (!nowKeys.Contains(was[i].Key))
            {
                steps.Add(new SetEconomySettingOp(was[i].Key, i, was[i].Raw, null));
            }
        }

        var kept = wasKeys.Where(nowKeys.Contains).ToList();
        foreach (var member in now)
        {
            if (kept.IndexOf(member.Key) is var i and >= 0 && was[wasKeys.IndexOf(member.Key)].Raw != member.Raw)
            {
                steps.Add(new SetEconomySettingOp(member.Key, i, was[wasKeys.IndexOf(member.Key)].Raw, member.Raw));
            }
        }

        for (var i = 0; i < now.Count; i++)
        {
            if (!wasKeys.Contains(now[i].Key))
            {
                steps.Add(new SetEconomySettingOp(now[i].Key, i, null, now[i].Raw));
            }
        }

        return true;
    }

    private static bool SectionSteps(EconomyOverride before, EconomyOverride after, List<EconomyOp> steps)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var (was, now) = (before.Sections, after.Sections);
        var wasNames = was.Select(s => s.Name).ToHashSet(comparer);
        var nowNames = now.Select(s => s.Name).ToHashSet(comparer);
        var kept = was.Where(s => nowNames.Contains(s.Name)).ToList();
        if (!kept.Select(s => s.Name).SequenceEqual(now.Where(s => wasNames.Contains(s.Name)).Select(s => s.Name), comparer))
        {
            return false;
        }

        for (var i = was.Count - 1; i >= 0; i--)
        {
            if (!nowNames.Contains(was[i].Name))
            {
                steps.Add(new SetEconomySectionOp(was[i].Name, i, was[i].Text, null));
            }
        }

        for (var i = 0; i < kept.Count; i++)
        {
            var old = kept[i];
            var section = now.First(s => comparer.Equals(s.Name, old.Name));
            if (old.Text != section.Text)
            {
                steps.AddRange(EntrySteps(old, section) ?? [new SetEconomySectionOp(old.Name, i, old.Text, section.Text)]);
            }
        }

        for (var i = 0; i < now.Count; i++)
        {
            if (!wasNames.Contains(now[i].Name))
            {
                steps.Add(new SetEconomySectionOp(now[i].Name, i, null, now[i].Text));
            }
        }

        return true;
    }

    /// <summary>One step per entry added, removed or changed; null when a whole-section step is the better one.</summary>
    private static List<EconomyOp>? EntrySteps(EconomyOverride.Section before, EconomyOverride.Section after)
    {
        var (was, now) = (before.Entries, after.Entries);
        if (before.Raw is not null || after.Raw is not null || was.Any(e => e.Code is null) || now.Any(e => e.Code is null))
        {
            return null;
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        var (wasCodes, nowCodes) = (was.Select(e => e.Code!).ToList(), now.Select(e => e.Code!).ToList());
        var (wasSet, nowSet) = (wasCodes.ToHashSet(comparer), nowCodes.ToHashSet(comparer));
        if (wasSet.Count != wasCodes.Count || nowSet.Count != nowCodes.Count
            || !wasCodes.Where(nowSet.Contains).SequenceEqual(nowCodes.Where(wasSet.Contains), comparer))
        {
            return null;
        }

        var steps = new List<EconomyOp>();
        for (var i = was.Count - 1; i >= 0; i--)
        {
            if (!nowSet.Contains(wasCodes[i]))
            {
                steps.Add(new SetEconomyEntryOp(before.Name, wasCodes[i], i, was[i].Text, null));
            }
        }

        var kept = was.Where(e => nowSet.Contains(e.Code!)).ToList();
        for (var i = 0; i < kept.Count; i++)
        {
            var entry = now[nowCodes.FindIndex(c => comparer.Equals(c, kept[i].Code))];
            if (entry.Text != kept[i].Text)
            {
                steps.Add(new SetEconomyEntryOp(before.Name, kept[i].Code!, i, kept[i].Text, entry.Text));
            }
        }

        for (var i = 0; i < now.Count; i++)
        {
            if (!wasSet.Contains(nowCodes[i]))
            {
                steps.Add(new SetEconomyEntryOp(before.Name, nowCodes[i], i, null, now[i].Text));
            }
        }

        return steps.Count <= MaxSectionSteps ? steps : null;
    }

    private static Dictionary<string, string> SettingMap(EconomyOverride economy) =>
        economy.SettingMembers.GroupBy(m => m.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => string.Join("\n", g.Select(m => m.Raw)), StringComparer.Ordinal);

    /// <summary>A section's entries by code (an entry without one by its text), each code's entries written as one.</summary>
    private static Dictionary<string, string> EntryMap(EconomyOverride.Section section) =>
        section.Entries.GroupBy(e => e.Code ?? "\0" + e.Text, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join("\n", g.Select(e => e.Text)), StringComparer.OrdinalIgnoreCase);
}
