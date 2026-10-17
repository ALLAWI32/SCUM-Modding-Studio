namespace ScumStudio.Level.Editing;

public sealed partial class EditState
{
    /// <summary>
    /// A copy of this state with only the edits of the levels <paramref name="level"/> keeps (by package path), the vehicle and
    /// item edits (clones by their new primary package, value overrides and replacements by their package) <paramref name="asset"/>
    /// keeps, and the terrain holes when <paramref name="holes"/> is true: one page's part of a project, exported as a mod of its
    /// own (owner 2026-10-17: "each part as its own mod"). <paramref name="value"/>, when given, picks the value overrides
    /// one by one instead of by their package (a table whose rows belong to several pages).
    /// </summary>
    public EditState Where(Func<string, bool> level, Func<string, bool> asset, bool holes, Func<AssetValueOverride, bool>? value = null)
    {
        value ??= v => asset(v.Package);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(asset);
        var copy = Clone();
        copy._deletedActors.RemoveWhere(a => !level(a.Level));
        copy._deletedInstances.RemoveWhere(i => !level(i.Level));
        copy._deletedComponents.RemoveWhere(k => !level(k.Actor.Level));
        Drop(copy._transforms, k => k.Actor.Level);
        Drop(copy._instanceTransforms, k => k.Level);
        Drop(copy._added, k => k.Level);
        Drop(copy._addedInstances, k => k.Level);
        Drop(copy._bends, k => k.Level);
        Drop(copy._segmentSways, k => k.Actor.Level);
        Drop(copy._spawnPoints, k => k.Actor.Level);
        Drop(copy._meshes, k => k.Actor.Level);
        Drop(copy._loot, k => k.Actor.Level);
        Drop(copy._grass, k => k.Level);
        Drop(copy._choices, k => k.Actor.Level);
        copy._newPlaces = copy._newPlaces.Where(p => level(p.Created.Level)).ToArray();
        if (!holes)
        {
            copy._holes.Clear();
        }

        foreach (var key in copy._clones.Keys.Where(k => !asset(k)).ToList())
        {
            copy._clones.Remove(key);
        }

        foreach (var key in copy._values.Where(v => !value(v.Value)).Select(v => v.Key).ToList())
        {
            copy._values.Remove(key);
        }

        foreach (var key in copy._replacements.Keys.Where(k => !asset(k)).ToList())
        {
            copy._replacements.Remove(key);
        }

        return copy;

        void Drop<TKey, TValue>(Dictionary<TKey, TValue> entries, Func<TKey, string> levelOf)
            where TKey : notnull
        {
            foreach (var key in entries.Keys.Where(k => !level(levelOf(k))).ToList())
            {
                entries.Remove(key);
            }
        }
    }
}
