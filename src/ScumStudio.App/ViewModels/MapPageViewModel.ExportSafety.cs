using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// What the Map says before an edit damages a level or players' worlds: a volume is not pasted into another level (its brush
/// shape cannot go there yet; lights, fire, smoke, Niagara effects and sounds are copied since D15 was fixed), and placing
/// or deleting an item spawner warns that the server keeps what it already spawned (D02, "phantom fridges").
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>
    /// The reason, in the user's language, the Map does not paste <paramref name="item"/> into <paramref name="target"/>; null
    /// when it can be pasted there.
    /// </summary>
    internal static string? CopyRefusedText(ActorItemViewModel item, LevelDocument target) =>
        LevelPackageEditor.RefusedCopy(item.Actor, item.Level, !string.Equals(target.PackagePath, item.Level.PackagePath, StringComparison.OrdinalIgnoreCase)) is { } refusal
            ? Localization.Loc.F("Map.CopyRefused.OtherLevel", item.Name, refusal.Member)
            : null;

    /// <summary>True when <paramref name="op"/> places an item spawner (a fixed item placed by Add object, a prefab or a paste, or a copy of a spawner).</summary>
    internal bool PlacesItemSpawner(EditOp op) => op switch
    {
        AddBlueprintActorOp blueprint => blueprint.Item is not null || string.Equals(blueprint.ClassPath, PlacedBlueprintIndex.WorldItemSpawnerClass, StringComparison.Ordinal),
        DuplicateActorOp duplicate => PristineOf(duplicate.Source) is { } source && ProjectExporter.IsItemSpawner(source.Actor),
        _ => false,
    };

    /// <summary>Warns when <paramref name="ops"/> place item spawners: the server saves what they spawn, deleting them later leaves it.</summary>
    private void WarnIfItemSpawners(IEnumerable<EditOp> ops)
    {
        var names = ops.Where(PlacesItemSpawner).Select(op => op.GetPrimaryTarget()?.Actor).OfType<string>().ToList();
        if (names.Count > 0)
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.ItemSpawner.Title"), Localization.Loc.F("Map.ItemSpawner.Placed", string.Join(", ", names.Take(4))));
        }
    }

    /// <summary>Warns when deleting <paramref name="item"/> deletes an item spawner: the items it spawned stay.</summary>
    private void WarnIfItemSpawnerDeleted(ActorItemViewModel item)
    {
        if (ProjectExporter.IsItemSpawner(item.Actor))
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.ItemSpawner.Title"), Localization.Loc.F("Map.ItemSpawner.Deleted", item.Name));
        }
    }
}
