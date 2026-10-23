using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding;

/// <summary>
/// A clone is made to be spawned under its new name (Discord 2026-10-18, hexter_98's thread: "'ak47_gold' is not allowed to be
/// spawned"; found while checking why). 246 of the game's items (Weapon_M16A4_Premium and the other Premium weapons,
/// Cal_84_AT4_HEAT, KitchenStove, Bear_Pow_01 …) store <c>_visibleToAdminCommands = false</c> in their class defaults; the cook
/// turns it into the registry tag <c>HiddenFromAdminCommands</c>, and <c>#SpawnItem</c> / <c>#SpawnVehicle</c> refuse every
/// record that carries it. The export copied every tag of the template, so a clone of such an item was refused however well
/// it was installed, and nothing said why. Now the clone's primary package says true (its class defaults; listed among the
/// applied values of the export) and <see cref="Registry.RegistryCloner"/> leaves the tag off its records; the template and
/// the clone's other packages (its entity setup) stay as the game has them. A DLC item's clone may still fail the game's own
/// DLC check.
/// </summary>
public static partial class AssetModBuilder
{
    /// <summary>
    /// The flag of an item's, weapon's or vehicle's class defaults that the game cooks into the registry tag
    /// <see cref="Registry.RegistryCloner.HiddenFromAdminCommandsTag"/>: false hides the asset from the spawn commands.
    /// </summary>
    public const string VisibleToAdminCommands = "_visibleToAdminCommands";

    /// <summary>
    /// Sets <see cref="VisibleToAdminCommands"/> to true in the class defaults of the clone's primary package when its template
    /// stores it false. Returns what was applied (package, key, old, new) for the report; a flag that cannot be set leaves a
    /// warning and the clone as it was copied.
    /// </summary>
    private static List<(string Package, string Key, string Old, string New)> MakeVisibleToAdminCommands(ClonePlan plan,
        List<(string New, CookedPackage Package, PackageBytes Bytes)> copies, ICollection<string> warnings)
    {
        var primary = PackageMap.Normalize(plan.NewPrimary);
        for (var i = 0; i < copies.Count; i++)
        {
            var (newPath, package, _) = copies[i];
            if (!string.Equals(PackageMap.Normalize(newPath), primary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var hidden = TunableReader.Read(package)
                    .Where(t => t.Path == VisibleToAdminCommands && t.Kind == TunableKind.Bool
                        && t.Export.StartsWith("Default__", StringComparison.Ordinal) && !TunableValue.ParseBool(t.Value))
                    .ToList();
                if (hidden.Count == 0)
                {
                    return [];
                }

                var result = TunablePatcher.Apply(package, hidden.Select(t => new TunableEdit(t.Export, t.Path, "true")), isClone: true);
                copies[i] = (newPath, CookedPackage.Parse(result.Bytes.UAsset, result.Bytes.UExp, package.UBulk, newPath), result.Bytes);
                return result.Applied.Select(a => (newPath, a.Key, a.Old, a.New)).ToList();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or InvalidDataException or IOException)
            {
                warnings.Add($"{plan.NewPrimary}: the game hides its template from the spawn commands and the clone's {VisibleToAdminCommands} could not be set ({ex.Message}); #SpawnItem may refuse it.");
                return [];
            }
        }

        return [];
    }
}
