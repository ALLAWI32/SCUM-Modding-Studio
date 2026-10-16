using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;

namespace ScumStudio.Level.Saves;

/// <summary>
/// An item spawner whose saved items should go: one the project placed and no longer has (reverted, undone or deleted),
/// or a game spawner it deleted.
/// </summary>
/// <param name="Level">Level package (<c>/Game/ConZ_Files/Maps/The_Island/Z_4_Prvo_Selo_08</c>).</param>
/// <param name="Actor">The spawner actor's name in that level.</param>
/// <param name="Item">Class path of the item it spawned (<c>/Game/X/Refrigerator.Refrigerator_C</c>), or null when unknown.</param>
/// <param name="Location">Where it stood (world cm), or null when unknown.</param>
public sealed record SpawnerTarget(string Level, string Actor, string? Item, FVector? Location)
{
    /// <summary>
    /// A spawner the project still places: its own item is never touched, its item class is only searched in the area
    /// picked on the Map (copies no spawner owns any more, if a spawner ever spawned again).
    /// </summary>
    public bool Placed { get; init; }
}

/// <summary>A circle on the map, world cm (picked on the Map: around the selected object).</summary>
public sealed record SaveArea(double X, double Y, double Radius);

/// <summary>Why a saved item matches a <see cref="SpawnerTarget"/>.</summary>
public enum SavedItemMatch
{
    /// <summary>The save itself links it to the spawner (<c>item_entity_spawner</c> named after the spawner).</summary>
    Spawned,

    /// <summary>Not linked to any spawner, same item class, next to where the spawner stood.</summary>
    Near,

    /// <summary>Not linked to any spawner, an item class of the targets, in the area picked on the Map.</summary>
    InArea,
}

/// <summary>A loose saved item that a removed spawner left (an <c>entity</c> row with an <c>item_entity</c> row).</summary>
/// <param name="Id">Its <c>entity.id</c>.</param>
/// <param name="Class">Its <c>entity.class</c> (<c>Refrigerator_ES</c>).</param>
/// <param name="X">World cm.</param>
/// <param name="Y">World cm.</param>
/// <param name="Z">World cm.</param>
/// <param name="Spawner">The save's spawner record that spawned it (<c>Level:PersistentLevel.Actor.Component</c>), or null.</param>
/// <param name="Match">Why it matches.</param>
/// <param name="Target">The actor name of the spawner it belongs to (empty for an area match).</param>
/// <param name="Contents">How many items are stored inside it (they go with it).</param>
public sealed record SavedItem(long Id, string Class, double X, double Y, double Z, string? Spawner, SavedItemMatch Match, string Target, int Contents);

/// <summary>Why a save cannot be read or changed now.</summary>
public enum SaveBusy
{
    /// <summary>It can be used.</summary>
    None,

    /// <summary>The file does not exist.</summary>
    Missing,

    /// <summary>The game or a dedicated server runs on this PC.</summary>
    GameRunning,

    /// <summary>Another program (the game, a server, a database viewer) has the file open.</summary>
    OpenElsewhere,

    /// <summary>A write-ahead log or journal with content lies next to it: the last writer did not close it cleanly.</summary>
    NotClosedCleanly,
}

/// <summary>The result of <see cref="SavedItemCleaner.Check"/>: <see cref="Detail"/> names the process or file.</summary>
public sealed record SaveCheck(SaveBusy Busy, string Detail)
{
    /// <summary>True when the save can be used.</summary>
    public bool Ok => Busy == SaveBusy.None;
}

/// <summary>Thrown when a save cannot be used now (see <see cref="Check"/>); nothing was changed.</summary>
public sealed class SaveBusyException(SaveCheck check) : InvalidOperationException($"The save cannot be used now ({check.Busy}: {check.Detail}).")
{
    /// <summary>Why.</summary>
    public SaveCheck Check { get; } = check;
}

/// <summary>What <see cref="SavedItemCleaner.Remove"/> did.</summary>
/// <param name="BackupPath">The copy of the save made before anything changed.</param>
/// <param name="Items">Items removed.</param>
/// <param name="Contents">Items that were stored inside them (removed with them).</param>
public sealed record SavedItemRemoval(string BackupPath, int Items, int Contents);

/// <summary>
/// "Clean saved items" (D02, "phantom fridges"): a fixed item the studio places is a world item spawner, and the game saves
/// the item it spawned in SCUM.db (single player <c>%LOCALAPPDATA%\SCUM\Saved\SaveFiles</c>, a server's
/// <c>SCUM\Saved\SaveFiles</c>): an <c>entity</c> row (class <c>&lt;Item&gt;_ES</c>, location, owner) with an
/// <c>item_entity</c> row, linked to the spawner by <c>item_entity_spawner</c> (<c>name</c> =
/// <c>Level:PersistentLevel.Actor.Component</c>) and <c>item_entity_spawner_entry</c>. Removing the spawner from the mod
/// leaves that item; this finds the items of the project's removed spawners and removes only those, after a backup.
/// </summary>
/// <remarks>
/// Only loose world items are ever touched: an <c>item_entity</c> with no owner and no parent (not in an inventory or a
/// container), not a vehicle, not part of a base, not a character. Removal deletes the <c>entity</c> row with foreign keys on,
/// so the game's own <c>ON DELETE</c> rules take what belongs to it (its item rows, components, the items stored inside it)
/// and set the spawner's link to NULL. Reading opens the file immutable (nothing is written next to it).
/// </remarks>
public static class SavedItemCleaner
{
    /// <summary>How far (cm, on the map) an unlinked item may lie from where its spawner stood.</summary>
    public const double DefaultRadius = 200;

    /// <summary>How far (cm) above or below the spawner an unlinked item may lie.</summary>
    public const double HeightTolerance = 300;

    /// <summary>Part of a backup's file name: <c>SCUM.db.studio-backup-20261016-180501</c>.</summary>
    public const string BackupInfix = ".studio-backup-";

    /// <summary>Processes that keep a save open: the game and the dedicated server.</summary>
    public static readonly IReadOnlyList<string> GameProcesses = ["SCUM", "SCUM-Win64-Shipping", "SCUMServer", "SCUMServer-Win64-Shipping"];

    private const string LooseItems = """
        FROM entity e JOIN item_entity i ON i.entity_id = e.id
        LEFT JOIN item_entity_spawner_entry se ON se.entity_id = e.id
        LEFT JOIN item_entity_spawner s ON s.id = se.item_entity_spawner_id
        WHERE e.owning_entity_id IS NULL AND e.parent_entity_id IS NULL
          AND NOT EXISTS (SELECT 1 FROM vehicle_entity v WHERE v.entity_id = e.id OR v.item_container_entity_id = e.id)
          AND NOT EXISTS (SELECT 1 FROM base_element_item b WHERE b.item_entity_id = e.id)
          AND NOT EXISTS (SELECT 1 FROM prisoner_entity p WHERE p.entity_id = e.id)
        """;

    private static readonly string[] RequiredTables =
        ["entity", "item_entity", "item_entity_spawner", "item_entity_spawner_entry", "vehicle_entity", "base_element_item", "prisoner_entity"];

    /// <summary>The <c>entity.class</c> the game saves an item of <paramref name="itemClassPath"/> under (<c>.../Refrigerator.Refrigerator_C</c> → <c>Refrigerator_ES</c>).</summary>
    public static string EntityClassOf(string itemClassPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemClassPath);
        var name = itemClassPath[(itemClassPath.LastIndexOf('/') + 1)..];
        name = name[(name.IndexOf('.') + 1)..];
        return (name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name) + "_ES";
    }

    /// <summary>The start of the save's record name of every spawner of <paramref name="actor"/> (<c>Z_4_Prvo_Selo_08:PersistentLevel.Refrigerator_Added.</c>).</summary>
    public static string SpawnerPrefix(string level, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return $"{level[(level.TrimEnd('/').LastIndexOf('/') + 1)..]}:PersistentLevel.{actor}.";
    }

    /// <summary>
    /// The project's item spawners that are gone: every spawner its history ever placed (Add object, prefabs, pastes,
    /// copies; undone, reverted or deleted since) that the current state no longer has, and the game's spawners it deleted.
    /// </summary>
    /// <param name="history">Every recorded edit, applied or not (the journal keeps undone ones).</param>
    /// <param name="state">The current state.</param>
    /// <param name="findActor">The game's actor (pristine level), or null when unknown.</param>
    public static IReadOnlyList<SpawnerTarget> TargetsOf(IEnumerable<EditOp> history, EditState state, Func<ActorRef, ActorRecord?> findActor)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(findActor);
        var targets = new List<SpawnerTarget>();
        void Add(SpawnerTarget target)
        {
            if (!targets.Contains(target))
            {
                targets.Add(target);
            }
        }

        var ops = history.SelectMany(Flatten).ToList();
        foreach (var op in ops)
        {
            switch (op)
            {
                case AddBlueprintActorOp add when !state.IsAdded(add.Created):
                    var source = findActor(add.Source);
                    if (add.Item is not null || string.Equals(add.ClassPath, PlacedBlueprintIndex.WorldItemSpawnerClass, StringComparison.Ordinal)
                        || (source is not null && ProjectExporter.IsItemSpawner(source)))
                    {
                        Add(new SpawnerTarget(add.Level, add.NewName, add.Item ?? (source is null ? null : ItemOf(source)), add.Transform.Location));
                    }

                    break;
                case DuplicateActorOp duplicate when !state.IsAdded(duplicate.Created) && findActor(duplicate.Source) is { } copied && ProjectExporter.IsItemSpawner(copied):
                    Add(new SpawnerTarget(duplicate.Source.Level, duplicate.NewName, ItemOf(copied), duplicate.Transform.Location));
                    break;
            }
        }

        // The spawners still placed: only their item classes, for the area (copies no spawner owns any more).
        foreach (var add in ops.OfType<AddBlueprintActorOp>().Where(a => state.IsAdded(a.Created)))
        {
            if ((add.Item ?? (findActor(add.Source) is { } source && ProjectExporter.IsItemSpawner(source) ? ItemOf(source) : null)) is { } item)
            {
                Add(new SpawnerTarget(add.Level, add.NewName, item, add.Transform.Location) { Placed = true });
            }
        }

        foreach (var deleted in state.DeletedActors.OrderBy(a => a.Level, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Actor, StringComparer.OrdinalIgnoreCase))
        {
            if (findActor(deleted) is { } actor && ProjectExporter.IsItemSpawner(actor))
            {
                Add(new SpawnerTarget(deleted.Level, deleted.Actor, ItemOf(actor), actor.WorldTransform.Translation));
            }
        }

        return targets;
    }

    /// <summary>
    /// Whether <paramref name="path"/> can be read or changed now: it exists, neither the game nor a server runs on this PC
    /// (<paramref name="runningGames"/>, null = look at this PC's processes), no other program has it open, and no
    /// write-ahead log or journal with content lies next to it.
    /// </summary>
    public static SaveCheck Check(string path, IEnumerable<string>? runningGames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return new SaveCheck(SaveBusy.Missing, path);
        }

        if ((runningGames ?? RunningGames()).FirstOrDefault() is { } game)
        {
            return new SaveCheck(SaveBusy.GameRunning, game);
        }

        foreach (var suffix in new[] { "-wal", "-journal" })
        {
            var side = new FileInfo(path + suffix);
            if (side.Exists && side.Length > 0)
            {
                return new SaveCheck(SaveBusy.NotClosedCleanly, side.Name);
            }
        }

        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            return new SaveCheck(SaveBusy.OpenElsewhere, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SaveCheck(SaveBusy.OpenElsewhere, ex.Message);
        }

        return new SaveCheck(SaveBusy.None, string.Empty);
    }

    /// <summary>The loose saved items of <paramref name="targets"/> (and, with <paramref name="area"/>, of their item classes there).</summary>
    /// <exception cref="SaveBusyException">The save cannot be used now.</exception>
    /// <exception cref="InvalidDataException">The file is not a SCUM save.</exception>
    public static IReadOnlyList<SavedItem> Find(
        string path, IReadOnlyList<SpawnerTarget> targets, SaveArea? area, double radius = DefaultRadius, IEnumerable<string>? runningGames = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var check = Check(path, runningGames);
        if (!check.Ok)
        {
            throw new SaveBusyException(check);
        }

        using var connection = Open(path, write: false);
        return Match(connection, null, targets, area, radius);
    }

    /// <summary>
    /// Removes the items of <paramref name="ids"/> that still match <paramref name="targets"/> (found again inside the same
    /// transaction: anything else asked for is left alone), after copying the save to
    /// <c>&lt;save&gt;.studio-backup-&lt;yyyyMMdd-HHmmss&gt;</c> next to it.
    /// </summary>
    /// <exception cref="SaveBusyException">The save cannot be used now; nothing was written.</exception>
    public static SavedItemRemoval Remove(
        string path, IReadOnlyList<SpawnerTarget> targets, SaveArea? area, IReadOnlyCollection<long> ids, DateTime now, double radius = DefaultRadius,
        IEnumerable<string>? runningGames = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(ids);
        var check = Check(path, runningGames);
        if (!check.Ok)
        {
            throw new SaveBusyException(check);
        }

        var backup = path + BackupInfix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        File.Copy(path, backup, overwrite: false);
        var items = 0;
        var contents = 0;
        using (var connection = Open(path, write: true))
        {
            Execute(connection, null, "PRAGMA foreign_keys = ON;"); // the game's ON DELETE rules take what belongs to the item
            using var transaction = connection.BeginTransaction();
            var wanted = ids.ToHashSet();
            foreach (var item in Match(connection, transaction, targets, area, radius).Where(i => wanted.Contains(i.Id)))
            {
                contents += item.Contents;
                items += Execute(connection, transaction, "DELETE FROM entity WHERE id = $id;", item.Id);
            }

            transaction.Commit();
        }

        return new SavedItemRemoval(backup, items, contents);
    }

    /// <summary>Puts <paramref name="backupPath"/> back as <paramref name="path"/> (the save must not be in use).</summary>
    /// <exception cref="SaveBusyException">The save is in use; nothing was written.</exception>
    public static void Restore(string path, string backupPath, IEnumerable<string>? runningGames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var check = Check(path, runningGames);
        if (check.Busy is not (SaveBusy.None or SaveBusy.Missing))
        {
            throw new SaveBusyException(check);
        }

        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("The backup does not exist.", backupPath);
        }

        foreach (var side in new[] { path + "-wal", path + "-shm" })
        {
            File.Delete(side); // empty leftovers of a reader: they belong to the file being replaced
        }

        File.Copy(backupPath, path, overwrite: true);
    }

    /// <summary>The newest backup Clean saved items made of <paramref name="path"/>, or null.</summary>
    public static string? LatestBackup(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return directory is null || !Directory.Exists(directory)
            ? null
            : Directory.EnumerateFiles(directory, Path.GetFileName(path) + BackupInfix + "*").Order(StringComparer.Ordinal).LastOrDefault();
    }

    private static IEnumerable<EditOp> Flatten(EditOp op) => op is BatchOp batch ? batch.Ops.SelectMany(Flatten) : [op];

    private static string? ItemOf(ActorRecord actor) =>
        actor.Components.SelectMany(c => c.SpawnMarkers).Select(m => m.ItemClassPath).FirstOrDefault(i => i is not null);

    private static List<string> RunningGames()
    {
        var running = new List<string>();
        foreach (var name in GameProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0)
            {
                running.Add(name);
            }

            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return running;
    }

    /// <summary>Reading opens the file immutable: no lock and no <c>-wal</c>/<c>-shm</c> next to the player's save.</summary>
    private static SqliteConnection Open(string path, bool write)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = write ? Path.GetFullPath(path) : new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1",
            Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false, // closed for real on dispose: the game finds the file free and the log folded back in
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        if (RequiredTables.FirstOrDefault(t => !tables.Contains(t)) is { } missing)
        {
            connection.Dispose();
            throw new InvalidDataException($"{path} is not a SCUM save (it has no {missing} table).");
        }

        return connection;
    }

    private static List<SavedItem> Match(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<SpawnerTarget> targets, SaveArea? area, double radius)
    {
        var classes = targets.Where(t => t.Item is not null).Select(t => EntityClassOf(t.Item!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<SavedItem>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT e.id, e.class, e.location_x, e.location_y, e.location_z, s.name " + LooseItems + " ORDER BY e.id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var (id, cls, x, y, z) = (reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4));
                var spawner = reader.IsDBNull(5) ? null : reader.GetString(5);
                if (spawner is not null)
                {
                    // Linked: only to a removed spawner; an item of any other spawner (the game's, one still placed) stays.
                    if (targets.FirstOrDefault(t => !t.Placed && spawner.StartsWith(SpawnerPrefix(t.Level, t.Actor), StringComparison.OrdinalIgnoreCase)) is { } owner)
                    {
                        found.Add(new SavedItem(id, cls, x, y, z, spawner, SavedItemMatch.Spawned, owner.Actor, 0));
                    }

                    continue;
                }

                if (targets.FirstOrDefault(t => t is { Placed: false, Item: not null, Location: { } at } && string.Equals(EntityClassOf(t.Item), cls, StringComparison.OrdinalIgnoreCase)
                        && Flat(x - at.X, y - at.Y) <= radius && Math.Abs(z - at.Z) <= HeightTolerance) is { } near)
                {
                    found.Add(new SavedItem(id, cls, x, y, z, null, SavedItemMatch.Near, near.Actor, 0));
                }
                else if (area is not null && classes.Contains(cls) && Flat(x - area.X, y - area.Y) <= area.Radius)
                {
                    found.Add(new SavedItem(id, cls, x, y, z, null, SavedItemMatch.InArea, string.Empty, 0));
                }
            }
        }

        return found.Select(item => item with { Contents = Execute(connection, transaction, """
            WITH RECURSIVE inside(id) AS (SELECT id FROM entity WHERE owning_entity_id = $id UNION SELECT e.id FROM entity e JOIN inside ON e.owning_entity_id = inside.id)
            SELECT count(*) FROM inside;
            """, item.Id, scalar: true) }).ToList();
    }

    private static double Flat(double dx, double dy) => Math.Sqrt((dx * dx) + (dy * dy));

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, long? id = null, bool scalar = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (id is { } value)
        {
            command.Parameters.AddWithValue("$id", value);
        }

        return scalar ? Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) : command.ExecuteNonQuery();
    }
}
