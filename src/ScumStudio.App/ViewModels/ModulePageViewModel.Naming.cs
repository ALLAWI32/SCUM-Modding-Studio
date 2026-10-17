using ScumStudio.Modding.Cloning;

namespace ScumStudio.App.ViewModels;

/// <content>
/// Owner (2026-10-17): "I name the new vehicle in the right panel; by default the game's name and a number, Rager_1,
/// Rager_2 …; it gets its own spawn name so the stock one and mine both exist". Selecting a stock vehicle fills the clone
/// name box with the next free numbered name (<see cref="CloneFamilyPlanner.NumberedName"/>: <c>Rager1</c>, <c>Rager2</c> …)
/// unless the user typed one; a typed name ending in <c>_&lt;number&gt;</c> is saved as <c>_v&lt;number&gt;</c>
/// (<see cref="EffectiveCloneName"/>, the FName rule of <see cref="CloneFamilyPlanner.SafeName"/>), and the panel shows the
/// <c>#SpawnVehicle BPC_&lt;name&gt;</c> it will answer to before the clone is made.
/// </content>
public abstract partial class ModulePageViewModel
{
    /// <summary>The name last put in the box by the page (replaced on the next selection unless the user changed it).</summary>
    private string _suggestedCloneName = string.Empty;

    /// <summary>Fills the clone name box for <paramref name="item"/> (Vehicles page): its next free numbered name, or empty for a clone.</summary>
    private void SuggestCloneName(ModuleItemViewModel? item)
    {
        if (!IsVehicleModule || (CloneName.Trim().Length > 0 && CloneName != _suggestedCloneName))
        {
            return; // a name the user typed stays
        }

        _suggestedCloneName = item is { IsClone: false } ? NextCloneName(item) : string.Empty;
        CloneName = _suggestedCloneName;
    }

    /// <summary>The first of <c>Rager1</c>, <c>Rager2</c> … that no game package and no clone of the project is called (with <c>BPC_</c>).</summary>
    internal string NextCloneName(ModuleItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var leaf = PackageMap.Leaf(item.PackagePath);
        var stem = leaf.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) ? leaf[4..] : leaf;
        var folder = PackageMap.Folder(item.PackagePath);
        var clones = (_services.Projects.Current?.State.AssetClones ?? []).Select(c => PackageMap.Leaf(c.NewPrimary)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
        {
            var name = CloneFamilyPlanner.NumberedName(stem, n);
            var primary = "BPC_" + name;
            if (!clones.Contains(primary) && _catalog?.PackageExists(folder + "/" + primary) != true && CloneFamilyPlanner.IsValidName(name))
            {
                return name;
            }

            if (n > 999)
            {
                return string.Empty; // never in practice: leave the box to the user
            }
        }
    }
}
