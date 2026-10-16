using System.Globalization;
using Microsoft.Data.Sqlite;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Saves;
using ScumStudio.Level.World;

namespace ScumStudio.Tests.Level.Saves;

/// <summary>
/// D02 ("phantom fridges", defolt_0999, O Piqueno): a fixed item the studio places is a world item spawner, and the game
/// saves the item it spawned in SCUM.db (<c>entity</c> + <c>item_entity</c>, linked to the spawner by
/// <c>item_entity_spawner.name</c> = <c>Level:PersistentLevel.Actor.Component</c>). Clean saved items removes exactly the
/// items of the project's removed or reverted spawners from a save built with the game's real schema, after a backup.
/// </summary>
public sealed class SavedItemCleanerTests : IDisposable
{
    internal const string Level = "/Game/ConZ_Files/Maps/The_Island/Z_4_Prvo_Selo_08";
    internal const string Fridge = "/Game/ConZ_Files/Items/Kitchen/Refrigerator.Refrigerator_C";
    private static readonly FVector A = new(441871.1f, -832910.8f, 2415.5f);
    private static readonly FVector B = new(442100f, -832910f, 2415f);
    private static readonly FVector C = new(442400f, -833000f, 2415f);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-save-" + Guid.NewGuid().ToString("N"));

    public SavedItemCleanerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>The project placed three fridges and reverted them; a fourth is still placed.</summary>
    internal static IReadOnlyList<SpawnerTarget> Targets =>
    [
        new(Level, "Refrigerator_Added", Fridge, A),
        new(Level, "Refrigerator_Added2", Fridge, B),
        new(Level, "Refrigerator_Added3", Fridge, C),
        new(Level, "Refrigerator_Added4", Fridge, new FVector(1, 2, 3)) { Placed = true },
    ];

    [Fact]
    public void TheGameNamesASpawnedItemAfterItsClassAndASpawnerAfterItsLevelAndActor()
    {
        Assert.Equal("Refrigerator_ES", SavedItemCleaner.EntityClassOf(Fridge));
        Assert.Equal("Refrigerator_Unusable_ES", SavedItemCleaner.EntityClassOf("/Game/ConZ_Files/Items/Kitchen/Refrigerator_Unusable.Refrigerator_Unusable_C"));
        Assert.Equal("Z_4_Prvo_Selo_08:PersistentLevel.Refrigerator_Added.", SavedItemCleaner.SpawnerPrefix(Level, "Refrigerator_Added"));
    }

    [Fact]
    public void FindsOnlyTheItemsOfRemovedSpawners()
    {
        var save = SyntheticSave.Create(_dir);

        var found = SavedItemCleaner.Find(save, Targets, area: null, runningGames: []);

        // 100 and 102 are linked to the removed spawners by name, 103 lies where Refrigerator_Added3 stood (its record is gone).
        // Kept: 104 (a stock fridge next to it), 105 (in a player's inventory), 107 (part of a base), 108 (20 m away),
        // 109 (another class), 110 (the spawner the project still places), 200 (the player).
        Assert.Equal([100L, 102L, 103L], found.Select(f => f.Id).Order());
        Assert.Equal(SavedItemMatch.Spawned, found.Single(f => f.Id == 100).Match);
        Assert.Equal("Refrigerator_Added", found.Single(f => f.Id == 100).Target);
        Assert.Equal(1, found.Single(f => f.Id == 100).Contents); // the food stored in it
        Assert.Equal(SavedItemMatch.Near, found.Single(f => f.Id == 103).Match);
        Assert.Equal("Refrigerator_Added3", found.Single(f => f.Id == 103).Target);
        Assert.False(File.Exists(save + "-wal") || File.Exists(save + "-shm"), "reading leaves nothing next to the save");

        // An area picked on the Map adds the loose fridge 20 m away, and nothing of another class.
        var withArea = SavedItemCleaner.Find(save, Targets, new SaveArea(C.X + 2000, C.Y, 500), runningGames: []);
        Assert.Equal([100L, 102L, 103L, 108L], withArea.Select(f => f.Id).Order());
        Assert.Equal(SavedItemMatch.InArea, withArea.Single(f => f.Id == 108).Match);

        // Around the spawner the project still places: its own fridge (110, linked) stays, a loose copy (112) is listed.
        Assert.Equal([100L, 102L, 103L, 112L], SavedItemCleaner.Find(save, Targets, new SaveArea(1, 2, 500), runningGames: []).Select(f => f.Id).Order());
    }

    [Fact]
    public void RemovesThemAfterABackupAndTouchesNothingElse()
    {
        var save = SyntheticSave.Create(_dir);
        var original = File.ReadAllBytes(save);
        var before = SaveDump.Read(save);
        var components = SaveDump.ComponentsOf(save, [100, 101, 102, 103]);

        var removal = SavedItemCleaner.Remove(save, Targets, area: null, [100, 102, 103, 104, 107, 111, 200], new DateTime(2026, 10, 16, 18, 5, 1), runningGames: []);

        // Asked for too and never removed: 104 (a stock spawner's fridge), 107 (part of a base), 111 (a vehicle), 200 (the player).
        Assert.Equal(3, removal.Items);
        Assert.Equal(1, removal.Contents);
        Assert.Equal(save + ".studio-backup-20261016-180501", removal.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(removal.BackupPath));
        Assert.Equal(removal.BackupPath, SavedItemCleaner.LatestBackup(save));
        Assert.False(File.Exists(save + "-wal") && new FileInfo(save + "-wal").Length > 0, "the save is closed cleanly");

        SaveDump.AssertOnlyRemoved(before, SaveDump.Read(save), [100, 101, 102, 103], components);
        Assert.Empty(SavedItemCleaner.Find(save, Targets, area: null, runningGames: []));

        SavedItemCleaner.Restore(save, removal.BackupPath, runningGames: []);
        Assert.Equal(original, File.ReadAllBytes(save));
    }

    [Fact]
    public void RefusesASaveTheGameOrTheServerStillHasOpen()
    {
        var save = SyntheticSave.Create(_dir);

        Assert.Equal(SaveBusy.GameRunning, SavedItemCleaner.Check(save, ["SCUMServer"]).Busy);
        Assert.Equal(SaveBusy.Missing, SavedItemCleaner.Check(Path.Combine(_dir, "none.db"), []).Busy);
        using (new FileStream(save, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Equal(SaveBusy.OpenElsewhere, SavedItemCleaner.Check(save, []).Busy);
            var refused = Assert.Throws<SaveBusyException>(() => SavedItemCleaner.Remove(save, Targets, null, [100], DateTime.Now, runningGames: []));
            Assert.Equal(SaveBusy.OpenElsewhere, refused.Check.Busy);
        }

        File.WriteAllBytes(save + "-wal", [1, 2, 3]);
        Assert.Equal(SaveBusy.NotClosedCleanly, SavedItemCleaner.Check(save, []).Busy);
        Assert.Throws<SaveBusyException>(() => SavedItemCleaner.Find(save, Targets, null, runningGames: []));
        Assert.Null(SavedItemCleaner.LatestBackup(save)); // nothing was written
        File.Delete(save + "-wal");
        Assert.True(SavedItemCleaner.Check(save, []).Ok);
    }

    [Fact]
    public void TheTargetsAreEverySpawnerTheProjectPlacedAndNoLongerHasAndTheGameSpawnersItDeleted()
    {
        var source = new ActorRef(Level, "WorldItemSpawner_5");
        var placed = new AddBlueprintActorOp(Level, "Refrigerator_Added", PlacedBlueprintIndex.WorldItemSpawnerClass, source, new TransformValue(A, FRotator.Zero, FVector.One)) { Item = Fridge };
        var second = placed with { NewName = "Refrigerator_Added2", Transform = TransformValue.At(B.X, B.Y, B.Z) };
        var kept = placed with { NewName = "Refrigerator_Added4", Transform = TransformValue.At(1, 2, 3) };
        var rock = new AddStaticMeshActorOp(Level, "SM_Rock_Added", "/Game/X/SM_Rock.SM_Rock", TransformValue.Identity);
        var kitchen = new ActorRef(Level, "BP_Kitchen_0");
        var state = new EditState();
        foreach (var op in new EditOp[] { kept, rock, new DeleteActorOp(kitchen) })
        {
            state.Apply(op);
        }

        var spawnerRecord = new ComponentRecord(7, "WIS_Stove", "WorldItemSpawnerComponent", true, null, FTransform.Identity, FTransform.Identity, null, [])
        {
            ClassPath = "/Script/SCUM.WorldItemSpawnerComponent",
            SpawnMarkers = [new SpawnMarker(FTransform.Identity, string.Empty, 1f, 1, 1) { ItemClassPath = "/Game/Items/Stove.Stove_C" }],
        };
        var kitchenActor = new ActorRecord(3, "BP_Kitchen_0", "/Game/B/BP_Kitchen.BP_Kitchen_C", null, [spawnerRecord],
            new FTransform(FQuat.Identity, new FVector(10, 20, 30), FVector.One), ActorKind.Blueprint, null, []);

        // Placed, then reverted (undone): the history keeps it, the state no longer has it.
        var history = new EditOp[] { new BatchOp("Prefab", [placed, rock]), second, kept, rock, new DeleteActorOp(kitchen) };
        var targets = SavedItemCleaner.TargetsOf(history, state, a => a == kitchen ? kitchenActor : null);

        Assert.Equal(
        [
            new SpawnerTarget(Level, "Refrigerator_Added", Fridge, A),
            new SpawnerTarget(Level, "Refrigerator_Added2", Fridge, B),
            new SpawnerTarget(Level, "Refrigerator_Added4", Fridge, new FVector(1, 2, 3)) { Placed = true },
            new SpawnerTarget(Level, "BP_Kitchen_0", "/Game/Items/Stove.Stove_C", new FVector(10, 20, 30)),
        ], targets);
    }
}

/// <summary>A save built from the game's real schema (<c>ScumDbSchema.sql</c>) holding a few hand-made rows.</summary>
internal static class SyntheticSave
{
    internal static string Create(string directory)
    {
        var path = Path.Combine(directory, "SCUM.db");
        // Rows go in any order, as in a real save (the provider turns foreign keys on by default).
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = false }.ToString()))
        {
            connection.Open();
            Execute(connection, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Level", "Saves", "ScumDbSchema.sql")));
            Execute(connection, "PRAGMA user_version = 73; PRAGMA journal_mode = WAL;");
            Execute(connection, """
                INSERT INTO map(id, name) VALUES (4, 'The_Island');
                INSERT INTO entity_system(id, map_id, user_profile_id, timestamp) VALUES (4, 4, NULL, 194.2);
                INSERT INTO item_entity_spawner(id, name, entity_system_id, evaluation_time, num_evaluations) VALUES
                    (1, 'Z_4_Prvo_Selo_08:PersistentLevel.Refrigerator_Added.SpawnerComponent', 4, 1.0, 0),
                    (2, 'Z_4_Prvo_Selo_08:PersistentLevel.Refrigerator_Added2.SpawnerComponent', 4, 1.0, 0),
                    (3, 'Z_4_Prvo_Selo_08:PersistentLevel.BP_House_0.WIS_Refrigerator_02', 4, 1.0, 0),
                    (4, 'Z_4_Prvo_Selo_08:PersistentLevel.Refrigerator_Added4.SpawnerComponent', 4, 1.0, 0);
                INSERT INTO entity(id, entity_system_id, class, owning_entity_id, parent_entity_id, location_x, location_y, location_z, flags, reason) VALUES
                    (200, 4, 'BP_Prisoner_ES', NULL, NULL, 441000, -832000, 2400, 32, ''),
                    (100, 4, 'Refrigerator_ES', NULL, NULL, 441871, -832911, 2416, 0, 'UWorldItemSpawnerComponent::TryCreateItemTimerCallback'),
                    (101, 4, 'CannedSardine_ES', 100, 100, 0, 0, 0, 0, ''),
                    (102, 4, 'Refrigerator_ES', NULL, NULL, 442100, -832910, 2416, 0, 'UWorldItemSpawnerComponent::TryCreateItemTimerCallback'),
                    (103, 4, 'Refrigerator_ES', NULL, NULL, 442450, -833020, 2420, 0, 'UWorldItemSpawnerComponent::TryCreateItemTimerCallback'),
                    (104, 4, 'Refrigerator_ES', NULL, NULL, 442400, -833120, 2415, 0, 'UWorldItemSpawnerComponent::TryCreateItemTimerCallback'),
                    (105, 4, 'Refrigerator_ES', 200, 200, 0, 0, 0, 0, ''),
                    (107, 4, 'Refrigerator_ES', NULL, NULL, 442350, -832950, 2415, 0, ''),
                    (108, 4, 'Refrigerator_ES', NULL, NULL, 444400, -833000, 2415, 0, 'UWorldItemSpawnerComponent::TryCreateItemTimerCallback'),
                    (109, 4, 'Jeans_02_08_ES', NULL, NULL, 444450, -833000, 2415, 0, 'AItemSpawningManager::SpawnItemOnSpawner'),
                    (110, 4, 'Refrigerator_ES', NULL, NULL, 1, 2, 3, 0, 'UWorldItemSpawnerComponent::TryCreateItemTimerCallback'),
                    (112, 4, 'Refrigerator_ES', NULL, NULL, 101, 2, 3, 0, 'UWorldItemSpawnerComponent::TryCreateItemTimerCallback');
                INSERT INTO item_entity(entity_id, health, xml) VALUES (100, 100, '<Item/>'), (101, 90, '<Item/>'), (102, 100, '<Item/>'), (103, 100, '<Item/>'),
                    (104, 100, '<Item/>'), (105, 100, '<Item/>'), (107, 100, '<Item/>'), (108, 100, '<Item/>'), (109, 60, '<Item/>'), (110, 100, '<Item/>'), (112, 100, '<Item/>');
                INSERT INTO item_entity_spawner_entry(item_entity_spawner_id, idx, entity_id) VALUES (1, 0, 100), (2, 0, 102), (3, 0, 104), (4, 0, 110);
                INSERT INTO virtualized_item(item_entity_id, expiration_time, item_name, can_expire, save_time, bounds_size) VALUES
                    (100, 1791781504, 'Refrigerator_C_2147417278', 0, 1791781443, 200.0), (102, 1791781504, 'Refrigerator_C_2147417274', 0, 1791781443, 200.0),
                    (104, 1791781504, 'Refrigerator_C_2147417270', 0, 1791781443, 200.0);
                INSERT INTO food_item_entity(entity_id, is_opened) VALUES (101, 0);
                INSERT INTO entity_component(id, entity_id, name, class, flags) VALUES (500, 100, 'Evaluation', 'ItemSpawnerEvaluationResultEntityComponent', 0),
                    (501, 104, 'Evaluation', 'ItemSpawnerEvaluationResultEntityComponent', 0);
                INSERT INTO item_spawner_evaluation_result_entity_component(entity_component_id, usage_ratio, health_ratio, stack_ratio) VALUES (500, 1, 1, 0), (501, 1, 1, 0);
                INSERT INTO prisoner_entity(entity_id, prisoner_id) VALUES (200, NULL);
                INSERT INTO base_element_item(element_id, item_entity_id) VALUES (9, 107);
                INSERT INTO entity(id, entity_system_id, class, owning_entity_id, parent_entity_id, location_x, location_y, location_z, flags, reason) VALUES (111, 4, 'WheelBarrow_Metal_ES', NULL, NULL, 442400, -833000, 2415, 0, '');
                INSERT INTO vehicle_entity(entity_id, item_container_entity_id) VALUES (111, NULL);
                INSERT INTO entity_decay_manager_entry(entity_id, timestamp, damage_per_day) VALUES (104, 1.0, 0.5);
                """);
        }

        return path;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>Every row of every table of a save, to prove what a clean changed and that nothing else changed.</summary>
internal static class SaveDump
{
    /// <summary>Table → rows (column=value pairs in column order), in rowid order.</summary>
    internal static Dictionary<string, List<Dictionary<string, string>>> Read(string path)
    {
        var dump = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
        using var connection = Open(path);
        foreach (var table in Strings(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, string>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i) is byte[] blob ? Convert.ToHexString(blob) : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
                }

                rows.Add(row);
            }

            dump[table] = rows;
        }

        return dump;
    }

    /// <summary>The <c>entity_component</c> ids of <paramref name="entities"/>.</summary>
    internal static HashSet<string> ComponentsOf(string path, IEnumerable<long> entities)
    {
        using var connection = Open(path);
        return Strings(connection, $"SELECT id FROM entity_component WHERE entity_id IN ({string.Join(',', entities)})").ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// <paramref name="after"/> is <paramref name="before"/> without the rows of <paramref name="gone"/> (entities) and of
    /// their <paramref name="components"/>, the spawners' links to them set to NULL: every other table and row unchanged.
    /// </summary>
    internal static void AssertOnlyRemoved(
        Dictionary<string, List<Dictionary<string, string>>> before, Dictionary<string, List<Dictionary<string, string>>> after, IReadOnlyCollection<long> gone, ISet<string> components)
    {
        var ids = gone.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        var changed = new List<string>();
        foreach (var (table, rows) in before)
        {
            var expected = new List<string>();
            foreach (var row in rows)
            {
                if ((table == "entity" && ids.Contains(row["id"]))
                    || (table == "entity_component" && components.Contains(row["id"]))
                    || (table != "item_entity_spawner_entry" && row.Any(c => c.Key is "entity_id" or "item_entity_id" && ids.Contains(c.Value)))
                    || row.Any(c => c.Key == "entity_component_id" && components.Contains(c.Value)))
                {
                    continue;
                }

                var copy = new Dictionary<string, string>(row, StringComparer.Ordinal);
                if (table == "item_entity_spawner_entry" && ids.Contains(row["entity_id"]))
                {
                    copy["entity_id"] = "NULL";
                }

                expected.Add(Text(copy));
            }

            var actual = after[table].Select(Text).ToList();
            Assert.True(expected.SequenceEqual(actual), $"{table}: expected {expected.Count} rows, found {actual.Count}\n{string.Join('\n', actual.Except(expected).Concat(expected.Except(actual)).Take(10))}");
            if (rows.Count != actual.Count || !rows.Select(Text).SequenceEqual(actual))
            {
                changed.Add(table);
            }
        }

        Assert.DoesNotContain(changed, t => t is not ("entity" or "item_entity" or "virtualized_item" or "item_entity_spawner_entry" or "entity_component" or "food_item_entity"
            or "item_spawner_evaluation_result_entity_component" or "entity_decay_manager_entry" or "entity_inventory_component_entry"));
    }

    private static string Text(Dictionary<string, string> row) => string.Join('|', row.Select(c => c.Key + "=" + c.Value));

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static List<string> Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }

        return list;
    }
}
