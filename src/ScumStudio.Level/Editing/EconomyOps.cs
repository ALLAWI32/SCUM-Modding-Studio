using ScumStudio.Level.Model;

namespace ScumStudio.Level.Editing;

/// <summary>
/// An edit of the project's <c>EconomyOverride.json</c> (owner, 2026-10-12: "one journal op per change so Ctrl+Z works").
/// It carries the exact text before and after (null = absent), so its inverse swaps the two; it changes no level and no
/// asset (<see cref="EditState"/> accepts it as it is), the app's <c>ProjectEconomy</c> applies it to the file when it is
/// journaled, undone or redone (<see cref="Economy.EconomyOverride.Apply"/>).
/// </summary>
public abstract record EconomyOp : EditOp
{
    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => null;

    /// <summary>A value's text for a summary: a JSON string without its quotes, cut to 40 characters.</summary>
    protected static string Brief(string? text)
    {
        if (text is null)
        {
            return "(none)";
        }

        var value = text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
        return value.Length <= 40 ? value : value[..37] + "...";
    }
}

/// <summary>Sets, adds or removes one entry of a trader's section.</summary>
/// <param name="Trader">The section (the trader's name).</param>
/// <param name="Code">The entry's <c>tradeable-code</c>.</param>
/// <param name="Index">Where the entry is in the section (where it goes when added).</param>
/// <param name="Old">The entry's compact JSON object before; null when it is added.</param>
/// <param name="New">The entry's compact JSON object after; null when it is removed.</param>
public sealed record SetEconomyEntryOp(string Trader, string Code, int Index, string? Old, string? New) : EconomyOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => this with { Old = New, New = Old };

    /// <inheritdoc />
    public override string Describe() =>
        New is null ? $"Economy: {Trader} remove {Code}"
        : Old is null ? $"Economy: {Trader} add {Code}"
        : $"Economy: {Trader} {Code} {Economy.EconomyOverride.DescribeChange(Old, New)}";
}

/// <summary>Sets, adds or removes one setting of <c>economy-override</c> (<c>traders-unlimited-stock</c> …).</summary>
/// <param name="Key">The setting.</param>
/// <param name="Index">Where it is among the settings (where it goes when added).</param>
/// <param name="Old">Its raw JSON value before; null when it is added.</param>
/// <param name="New">Its raw JSON value after; null when it is removed.</param>
public sealed record SetEconomySettingOp(string Key, int Index, string? Old, string? New) : EconomyOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => this with { Old = New, New = Old };

    /// <inheritdoc />
    public override string Describe() => $"Economy: {Key} {Brief(Old)} -> {Brief(New)}";
}

/// <summary>Sets, adds or removes a trader's whole section (a reset, a reordered list, a pasted section).</summary>
/// <param name="Trader">The section (the trader's name).</param>
/// <param name="Index">Where the section is among the traders (where it goes when added).</param>
/// <param name="Old">The section's JSON array before; null when it is added.</param>
/// <param name="New">The section's JSON array after; null when it is removed.</param>
public sealed record SetEconomySectionOp(string Trader, int Index, string? Old, string? New) : EconomyOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => this with { Old = New, New = Old };

    /// <inheritdoc />
    public override string Describe() =>
        New is null ? $"Economy: remove {Trader}" : Old is null ? $"Economy: add {Trader}" : $"Economy: {Trader} (whole section)";
}

/// <summary>Replaces the whole file (an import, or a code edit that changed too much for single steps).</summary>
/// <param name="Old">The file before; null = none (the game's defaults).</param>
/// <param name="New">The file after; null = none.</param>
public sealed record ReplaceEconomyOp(string? Old, string? New) : EconomyOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => new ReplaceEconomyOp(New, Old);

    /// <inheritdoc />
    public override string Describe() => $"Economy: whole file ({New?.Length ?? 0} characters)";
}
