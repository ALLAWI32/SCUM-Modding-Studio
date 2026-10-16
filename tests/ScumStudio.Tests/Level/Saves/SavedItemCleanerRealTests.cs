using Microsoft.Data.Sqlite;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Saves;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level.Saves;

/// <summary>
/// D02 on real data (each does nothing without it): <c>SCUM_SAVE_DB</c> = a COPY of a real SCUM.db (the test copies it
/// again and works on that), <c>SCUM_PAKS</c> = the game's Paks (key from this PC's store).
/// </summary>
public sealed class SavedItemCleanerRealTests(ITestOutputHelper output)
{
    private const string FarmLevel = "/Game/ConZ_Files/Maps/The_Island/B_2_Farm_01";
    private const string FarmHouse = "BP_CB_05_B_INT_0"; // its "WorldItemSpawner - Refridgerator" and "- Stove" spawned one item each in the sandbox save

    [Fact]
    public void OnACopyOfARealSaveOnlyTheRemovedSpawnersItemsGoAndEveryOtherRowStays()
    {
        if (Environment.GetEnvironmentVariable("SCUM_SAVE_DB") is not { Length: > 0 } source || !File.Exists(source))
        {
            return; // needs a copy of a real save
        }

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-realsave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var save = Path.Combine(dir, "SCUM.db");
            File.Copy(source, save);
            var linked = Linked(save, SavedItemCleaner.SpawnerPrefix(FarmLevel, FarmHouse));
            if (linked.Count == 0)
            {
                output.WriteLine("This save has no item spawned by " + FarmHouse + ": nothing to check.");
                return;
            }

            // As if the project had deleted the farm house: the items its spawners made go, the fridge 25 m away stays.
            IReadOnlyList<SpawnerTarget> targets = [new(FarmLevel, FarmHouse, "/Game/ConZ_Files/Items/Kitchen/Refrigerator_Unusable.Refrigerator_Unusable_C", new FVector(-266776f, -156247f, 32276f))];
            var found = SavedItemCleaner.Find(save, targets, area: null, runningGames: []);
            foreach (var item in found)
            {
                output.WriteLine($"{item.Id} {item.Class} ({item.X:0}, {item.Y:0}, {item.Z:0}) {item.Match} {item.Spawner} contents {item.Contents}");
            }

            Assert.Equal(linked.Order(), found.Select(f => f.Id).Order());
            Assert.All(found, f => Assert.Equal(SavedItemMatch.Spawned, f.Match));

            var gone = WithContents(save, linked);
            var components = SaveDump.ComponentsOf(save, gone);
            var before = SaveDump.Read(save);
            var removal = SavedItemCleaner.Remove(save, targets, area: null, found.Select(f => f.Id).ToList(), DateTime.Now, runningGames: []);
            output.WriteLine($"Removed {removal.Items} item(s), {removal.Contents} inside; backup {Path.GetFileName(removal.BackupPath)}");

            Assert.Equal(found.Count, removal.Items);
            SaveDump.AssertOnlyRemoved(before, SaveDump.Read(save), gone, components);
            Assert.Empty(SavedItemCleaner.Find(save, targets, area: null, runningGames: []));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(removal.BackupPath));

            SavedItemCleaner.Restore(save, removal.BackupPath, runningGames: []);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(save));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// What could stop a spawner from piling items up: the stock world item spawners placed in The_Island's levels set no
    /// respawn, count, once-only or "not saved" value (the class has only <c>_item</c> and <c>_chanceToBeUsable</c>, SDK 1.3.3).
    /// </summary>
    [Fact]
    public void StockWorldItemSpawnersSetNoRespawnCountOrSaveValue()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var spawners = 0;
        var levels = 0;
        foreach (var file in catalog.PackageFiles
            .Where(f => f.Contains("/Maps/The_Island/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            var path = AssetPaths.ToPackagePath(file);
            if (!catalog.TryGetPackageFile(path, out var game))
            {
                continue;
            }

            var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(catalog, game);
            var package = CookedPackage.Parse(uasset, uexp, ubulk, path);
            levels++;
            for (var i = 0; i < package.Exports.Count; i++)
            {
                if (package.GetExportClassName(i) == "WorldItemSpawnerComponent")
                {
                    spawners++;
                    foreach (var tag in package.ReadProperties(i).Properties)
                    {
                        names[tag.Name] = names.GetValueOrDefault(tag.Name) + 1;
                    }
                }
            }

            if (spawners >= 400)
            {
                break;
            }
        }

        output.WriteLine($"{spawners} WorldItemSpawnerComponent(s) in {levels} level(s); their stored values:");
        foreach (var (name, count) in names.OrderByDescending(n => n.Value))
        {
            output.WriteLine($"  {name}: {count}");
        }

        Assert.True(spawners > 0);
        Assert.DoesNotContain(names.Keys, n => new[] { "Respawn", "Max", "Once", "Save", "Cooldown", "Count", "Limit" }.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<long> Linked(string save, string prefix) => Ids(save,
        "SELECT se.entity_id FROM item_entity_spawner s JOIN item_entity_spawner_entry se ON se.item_entity_spawner_id = s.id "
        + $"WHERE se.entity_id IS NOT NULL AND substr(s.name, 1, {prefix.Length}) = '{prefix.Replace("'", "''", StringComparison.Ordinal)}'");

    private static List<long> WithContents(string save, IEnumerable<long> ids) => Ids(save,
        $"WITH RECURSIVE tree(id) AS (SELECT id FROM entity WHERE id IN ({string.Join(',', ids)}) UNION SELECT e.id FROM entity e JOIN tree ON e.owning_entity_id = tree.id) SELECT id FROM tree");

    private static List<long> Ids(string save, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = save, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }
}
