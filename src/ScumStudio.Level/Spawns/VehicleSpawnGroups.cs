using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;

namespace ScumStudio.Level.Spawns;

/// <summary>A vehicle the game may put on a world spawn point of a group, and the preset that says so.</summary>
public sealed record GroupVehicle(string Vehicle, string Preset, string VehicleClass);

/// <summary>
/// Which vehicles appear on the world vehicle spawn points of each group (owner: "click a blue pin: what spawns here?"):
/// every <c>VehiclePreset</c> under <see cref="Folder"/> names its vehicle class and the level spawn groups it uses
/// (<c>RagerSpawnPreset</c>: <c>VehicleLevelSpawnGroup.City</c>, <c>.Default</c>).
/// </summary>
public sealed class VehicleSpawnGroups
{
    /// <summary>Folder of the vehicle spawn presets.</summary>
    public const string Folder = "/Game/ConZ_Files/Vehicles/SpawningPresets/";

    private const string GroupPrefix = "VehicleLevelSpawnGroup.";

    private readonly Dictionary<string, List<GroupVehicle>> _groups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Groups that have at least one vehicle.</summary>
    public IReadOnlyCollection<string> Groups => _groups.Keys;

    /// <summary>The vehicles of <paramref name="group"/> (<c>City</c> or <c>VehicleLevelSpawnGroup.City</c>), by name; empty for an unknown group.</summary>
    public IReadOnlyList<GroupVehicle> VehiclesOf(string group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var key = group.StartsWith(GroupPrefix, StringComparison.Ordinal) ? group[GroupPrefix.Length..] : group;
        return _groups.TryGetValue(key, out var list) ? list : [];
    }

    /// <summary>Reads every vehicle preset of <paramref name="catalog"/>.</summary>
    public static VehicleSpawnGroups Read(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var result = new VehicleSpawnGroups();
        var folder = AssetPaths.ToFilePathWithoutExtension(Folder, catalog.ProjectName);
        foreach (var file in catalog.PackageFiles.Where(f => f.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
        {
            var path = AssetPaths.ToPackagePath(file, catalog.ProjectName);
            if (!catalog.TryLoadObject<UObject>(path, out var preset) || preset.ExportType != "VehiclePreset"
                || !preset.TryGetValue(out FSoftObjectPath vehicleClass, "VehicleClass"))
            {
                continue;
            }

            var classPath = vehicleClass.AssetPathName.Text;
            var name = classPath[(classPath.LastIndexOf('.') + 1)..];
            name = name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;
            name = name.StartsWith("BPC_", StringComparison.Ordinal) ? name[4..] : name;
            foreach (var tag in GroupTags(preset))
            {
                var group = tag.StartsWith(GroupPrefix, StringComparison.Ordinal) ? tag[GroupPrefix.Length..] : tag;
                if (!result._groups.TryGetValue(group, out var list))
                {
                    result._groups[group] = list = [];
                }

                list.Add(new GroupVehicle(name, preset.Name, classPath));
            }
        }

        foreach (var list in result._groups.Values)
        {
            list.Sort((a, b) => string.Compare(a.Vehicle, b.Vehicle, StringComparison.OrdinalIgnoreCase));
        }

        return result;
    }

    private static IEnumerable<string> GroupTags(UObject preset) =>
        preset.TryGetValue(out FGameplayTagContainer container, "VehicleLevelSpawnGroups") ? container.GameplayTags.Select(t => t.TagName.Text)
        : preset.TryGetValue(out FGameplayTag[] tags, "VehicleLevelSpawnGroups") ? tags.Select(t => t.TagName.Text)
        : [];
}
