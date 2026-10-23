using System.Text.RegularExpressions;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// An ingredient group of the project (igor8802: "create, change, and view groups of ingredients"; redriver6155: "maybe let
/// us create new groups?"): a new ingredient tag <see cref="Tag"/> that every member item gets on export, the way the game's
/// own groups work (<c>CI_Group_Rags</c>: each item that counts as rags lists the tag in its entity setup's
/// <c>CraftingIngredientInstances</c>). A recipe of the project asks for the tag, and any member is accepted.
/// </summary>
/// <param name="Name">What the crafting menu shows for it.</param>
/// <param name="Tag">Its tag, <c>CI_Group_</c> + the name (<see cref="CraftGroups.TagFor"/>), fixed when it is made.</param>
/// <param name="Members">The items (package paths) that count as it.</param>
public sealed record CraftGroup(string Name, string Tag, IReadOnlyList<string> Members)
{
    /// <summary>True when <see cref="Members"/> holds <paramref name="item"/> (any case).</summary>
    public bool Has(string item) => Members.Contains(item, StringComparer.OrdinalIgnoreCase);

    /// <summary>The same name, tag and members in the same order (package paths in any case).</summary>
    public bool Equals(CraftGroup? other) =>
        other is not null && Name == other.Name && Tag == other.Tag && Members.SequenceEqual(other.Members, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Tag, Members.Count);
}

/// <summary>Names and uses of the project's ingredient groups (<see cref="CraftGroup"/>).</summary>
public static partial class CraftGroups
{
    /// <summary>The start of every group tag, the game's own groups' (<c>CI_Group_Rags</c>).</summary>
    public const string Prefix = "CI_Group_";

    /// <summary>Longest group name kept in the tag (the tag stays a short asset name).</summary>
    public const int MaxNameInTag = 40;

    /// <summary>
    /// The tag of a group named <paramref name="name"/>: <see cref="Prefix"/> + its Latin letters and digits (other runs as
    /// '_'), "Custom" when none is left, never ending in '_' and a number (the engine would read an FName number: <c>_G</c> is
    /// added), and with <c>_v2</c>, <c>_v3</c> … when <paramref name="taken"/> has it already (in any case: the game's tags and
    /// the project's other groups).
    /// </summary>
    public static string TagFor(string name, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(taken);
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var latin = NotLatin().Replace(name.Trim(), "_").Trim('_');
        latin = latin.Length > MaxNameInTag ? latin[..MaxNameInTag].TrimEnd('_') : latin;
        var tag = Prefix + (latin.Length > 0 ? latin : "Custom");
        tag = EndsInNumber().IsMatch(tag) ? tag + "_G" : tag;
        var unique = tag;
        for (var n = 2; used.Contains(unique); n++)
        {
            unique = $"{tag}_v{n}";
        }

        return unique;
    }

    /// <summary>True for a tag <see cref="TagFor"/> can give: <see cref="Prefix"/>, then a valid asset name.</summary>
    public static bool IsValidTag(string? tag) =>
        tag is not null && tag.StartsWith(Prefix, StringComparison.Ordinal) && tag.Length > Prefix.Length && CloneFamilyPlanner.IsValidName(tag) && !EndsInNumber().IsMatch(tag);

    /// <summary>
    /// The craftables whose recipe asks for the group <paramref name="tag"/> (an ingredient of that tag, not an item): a group
    /// in use is never deleted (its recipes would ask for a tag nothing ships).
    /// </summary>
    public static IReadOnlyList<string> UsedBy(IEnumerable<Craftable> craftables, string tag)
    {
        ArgumentNullException.ThrowIfNull(craftables);
        ArgumentNullException.ThrowIfNull(tag);
        return craftables.Where(c => c.Ingredients.Any(i => i.Item is null && string.Equals(i.Tag, tag, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name).ToList();
    }

    [GeneratedRegex("[^A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotLatin();

    [GeneratedRegex("_[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EndsInNumber();
}
