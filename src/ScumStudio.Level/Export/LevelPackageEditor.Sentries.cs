using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Level.Export;

/// <summary>
/// Sentries. The game spawns a sentry robot only from a <c>SentrySpawner2</c> listed in the <c>_sentrySpawners</c> of its
/// level's guarded zone manager (<c>BP_GuardedZoneManager_C</c>, one per level with sentries; the global manager switches
/// a zone's spawners on and off). Every stock spawner is listed (D_0_Military_Barracks 8 of 8, A_4_Military_Base 4 of 4,
/// A_4_Military_Base_Underground 5 of 5, A_4_Military_Harbour 5 of 5), and the manager's create-before-serialize
/// dependencies name them. A copied spawner listed nowhere never spawns its robot (igor8802, Discord), so each new spawner
/// joins the manager that lists its source, else the level's own manager, else a copy of its source level's manager made
/// for it where it stands.
/// </summary>
public static partial class LevelPackageEditor
{
    private const string SentrySpawnerClass = "SentrySpawner2";
    private const string SentrySpawnersProperty = "_sentrySpawners";

    /// <summary>Lists the request's new sentry spawners in a guarded zone manager; returns the managers created for them.</summary>
    private static List<int> JoinSentrySpawners(
        CookedPackage package, int levelIndex, LevelEditRequest request, List<ExportEntry> exports, List<ReadOnlyMemory<byte>> data,
        List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, List<int> preload, List<string> added, List<string> warnings)
    {
        var levelPackageIndex = levelIndex + 1;
        var spawners = request.Copies.Select(c => (c.NewName, Source: package, c.SourceActor, c.RootTransform))
            .Concat(request.ForeignCopies.Select(c => (c.NewName, c.Source, c.SourceActor, c.RootTransform)))
            .Where(c => added.Contains(c.NewName) && IsSentrySpawner(c.Source, c.SourceActor))
            .DistinctBy(c => c.NewName)
            .Select(c => (c.NewName, c.Source, c.RootTransform, Index: FindLevelActor(exports, names, levelPackageIndex, c.NewName), Manager: ManagerListing(c.Source, c.SourceActor)))
            .Where(c => c.Index > 0)
            .ToList();
        var created = new List<int>();
        if (spawners.Count == 0)
        {
            return created;
        }

        var levelManager = ManagerListing(package, null);
        foreach (var group in spawners.GroupBy(s => ReferenceEquals(s.Source, package) && s.Manager >= 0 ? s.Manager : levelManager))
        {
            var listed = group.Select(s => s.Index).ToList();
            if (group.Key >= 0)
            {
                var payload = data[group.Key].ToArray();
                var tag = PropertyReader.ReadPayload(package, payload, group.Key).Find(SentrySpawnersProperty)!; // ManagerListing read it
                data[group.Key] = WriteObjectArray(payload, tag, ((ArrayValue)tag.Value).Items.OfType<ObjectValue>().Select(o => o.Index).Concat(listed).ToList());
                AddCreateBeforeSerialize(exports, preload, group.Key, listed);
                continue;
            }

            // No manager in this level: the source level's manager comes along, listing only the new spawners.
            foreach (var bySource in group.GroupBy(s => (s.Source, s.Manager)))
            {
                var first = bySource.First();
                if (first.Manager < 0)
                {
                    warnings.Add($"'{first.NewName}': no guarded zone manager in the level or the source level lists sentries; the sentry will not spawn.");
                    continue;
                }

                var manager = first.Source.ResolveName(first.Source.Exports[first.Manager].ObjectName);
                var copy = new ForeignActorCopy(first.Source, manager, $"{manager}_{first.NewName}", first.RootTransform) { SentrySpawners = bySource.Select(s => s.NewName).ToList() };
                var made = ImportActors(package, levelIndex, [copy], exports, data, imports, names, wide, addedNames, preload, added, warnings);
                if (made.Count > 0)
                {
                    AddCreateBeforeSerialize(exports, preload, made[0] - 1, bySource.Select(s => s.Index).ToList());
                    created.AddRange(made);
                }
            }
        }

        return created;
    }

    private static bool IsSentrySpawner(CookedPackage source, string actor)
    {
        try
        {
            var index = FindExport(source, actor, FindLevelExport(source) + 1);
            return index >= 0 && source.GetExportClassName(index) == SentrySpawnerClass;
        }
        catch (InvalidDataException)
        {
            return false; // not a level: the copy reports it
        }
    }

    /// <summary>
    /// Export index of the guarded zone manager of <paramref name="package"/> whose <c>_sentrySpawners</c> lists
    /// <paramref name="spawner"/> (null: the first manager with a list), or -1.
    /// </summary>
    private static int ManagerListing(CookedPackage package, string? spawner)
    {
        var levelPackageIndex = FindLevelExport(package) + 1;
        var spawnerIndex = spawner is null ? 0 : FindExport(package, spawner, levelPackageIndex) + 1;
        for (var i = 0; i < package.Exports.Count; i++)
        {
            var className = package.GetExportClassName(i);
            if (package.Exports[i].OuterIndex != levelPackageIndex || !className.Contains("GuardedZoneManager", StringComparison.Ordinal) || className.Contains("Global", StringComparison.Ordinal))
            {
                continue;
            }

            if (package.ReadProperties(i).Find(SentrySpawnersProperty)?.Value is ArrayValue list
                && (spawner is null || list.Items.OfType<ObjectValue>().Any(o => o.Index == spawnerIndex)))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Re-emits the four preload-dependency groups of an export at the end of <paramref name="preload"/> with
    /// <paramref name="extra"/> added to its create-before-serialize group: the objects it references exist before it is
    /// read (the event-driven loader resolves a reference to an export not yet created as null).
    /// </summary>
    private static void AddCreateBeforeSerialize(List<ExportEntry> exports, List<int> preload, int exportIndex, IReadOnlyList<int> extra)
    {
        var entry = exports[exportIndex];
        var before = entry.SerializationBeforeSerializationDependencies + entry.CreateBeforeSerializationDependencies;
        var total = before + entry.SerializationBeforeCreateDependencies + entry.CreateBeforeCreateDependencies;
        var groups = entry.FirstExportDependency >= 0 ? preload.GetRange(entry.FirstExportDependency, total) : new List<int>();
        groups.InsertRange(before, extra);
        exports[exportIndex] = entry with { FirstExportDependency = preload.Count, CreateBeforeSerializationDependencies = entry.CreateBeforeSerializationDependencies + extra.Count };
        preload.AddRange(groups);
    }
}
