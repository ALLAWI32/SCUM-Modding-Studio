using ScumStudio.App.Localization;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Step 2 of the Craftables page (owner 2026-10-17: "unclear where the item will show up"): one sentence that says in which
/// menu of the game players find it, and under which section.
/// </summary>
public sealed partial class CraftableRowViewModel
{
    /// <summary>The section the game puts it in when none is chosen ("Furniture › Chairs"), set by <see cref="RefreshSections"/>.</summary>
    internal string DefaultSection { get; private set; } = "…";

    /// <summary>The section it shows in: the chosen one, else the game's default for it.</summary>
    public string SectionName => Category is null ? DefaultSection : SelectedSection?.Label ?? Category;

    /// <summary>
    /// "Players find it in the base BUILDING menu, section «Furniture › Chairs» …", or the crafting menu, or both.
    /// </summary>
    public string WhereText => Use switch
    {
        CraftUse.Craft => Loc.F("Craftables.Where.InCraft", SectionName),
        CraftUse.Both => Loc.F("Craftables.Where.InBuild", SectionName) + " " + Loc.T("Craftables.Where.AlsoCraft"),
        _ => Loc.F("Craftables.Where.InBuild", SectionName),
    };
}
