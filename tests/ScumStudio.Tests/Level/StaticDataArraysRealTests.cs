using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "players: a first-spawn place: when a player creates his character for the first time the server puts him at a
/// chosen place, after that he spawns normally". Real game files only (<c>SCUM_PAKS</c>, key from this PC's store): lists
/// every top-level property of the island's <c>LevelStaticData</c> and the spawn, start, prisoner and respawn properties of
/// the game mode, game state and spawn screen assets, so a run on the owner's PC shows whether a first-spawn location is
/// data a pak can carry. Writes them to the test output; asserts only the five spawn place arrays the editor knows.
/// </summary>
public sealed class StaticDataArraysRealTests(ITestOutputHelper output)
{
    // From the pak index (Desktop\SCUM_Mod_Build\pak_full_index.txt); the FTUE plugin's paths are its mount point's.
    private static readonly string[] PlayerSpawnPackages =
    [
        "/Game/ConZ_Files/Blueprints/GameModes/BP_ConZGameMode",
        "/Game/ConZ_Files/Blueprints/GameModes/BP_CharacterCreationMenuGameMode",
        "/Game/ConZ_Files/Blueprints/BP_ConZGameState",
        "/FTUE/Gameplay/Blueprints/GameModes/BP_ConZGameMode_FTUE",
        "/FTUE/Gameplay/Blueprints/GameModes/BP_ConZGameState_FTUE",
        "/Game/ConZ_Files/UI/SpawnScreen/RespawnSettings",
        "/Game/ConZ_Files/AdminCommands/ArgumentCompletion/RespawnOption",
        "/Game/ConZ_Files/Items/Tags/Misc/SaveSpawnLocationTag",
    ];

    private static readonly string[] SpawnWords = ["Spawn", "Start", "Prisoner", "Respawn", "Location", "FirstTime", "NewCharacter"];

    /// <summary>
    /// What a new spawn place takes from the stock place it is laid out from (reviewer: "a tagged point's tags, a hunting
    /// area's other parameters, any id"): every member of the first entry of each list, how many entries store each member,
    /// and that no entry holds a member the editor does not know (one it would copy into a new place unseen).
    /// </summary>
    [Fact]
    public void EveryMemberOfTheSpawnPlaceListsIsListed()
    {
        using var catalog = Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;
        if (catalog is null)
        {
            return; // not asked for
        }

        var data = ModdableAssets.ReadPackage(catalog, SpawnPlaces.StaticDataPath);
        var block = data.ReadProperties(SpawnPlaces.StaticDataExport(data));
        foreach (var kind in Enum.GetValues<SpawnPlaceKind>())
        {
            var items = ((ArrayValue)block.Find(SpawnPlaces.ArrayOf(kind))!.Value).Items.OfType<StructValue>().ToList();
            foreach (var line in PropertyFormatter.Format(items[0].Properties))
            {
                output.WriteLine($"{SpawnPlaces.ArrayOf(kind)}[0] {line}");
            }

            foreach (var (member, count) in items.SelectMany(i => Members(i, string.Empty)).GroupBy(m => m).Select(g => (g.Key, g.Count())))
            {
                output.WriteLine($"{SpawnPlaces.ArrayOf(kind)}: {member} stored by {count} of {items.Count}");
            }

            Assert.All(items, i => Assert.Empty(SpawnPlacesEditor.UnknownMembers(kind, i)));
        }

        static IEnumerable<string> Members(StructValue item, string prefix) =>
            item.Properties.SelectMany(p => p.Value is StructValue inner ? Members(inner, $"{prefix}{p.Name}.").Prepend(prefix + p.Name) : new[] { prefix + p.Name });
    }

    [Fact]
    public void TheStaticDataAndThePlayerSpawnAssetsAreListed()
    {
        using var catalog = Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;
        if (catalog is null)
        {
            return; // not asked for
        }

        var data = ModdableAssets.ReadPackage(catalog, SpawnPlaces.StaticDataPath);
        var block = data.ReadProperties(SpawnPlaces.StaticDataExport(data));
        foreach (var tag in block.Properties)
        {
            output.WriteLine($"LevelStaticData.{tag.Name}: {tag.Type}{(tag.Value is ArrayValue a ? $"<{a.StructName ?? a.InnerType}>[{a.Items.Count}]" : string.Empty)}");
        }

        Assert.All(Enum.GetValues<SpawnPlaceKind>(), k => Assert.IsType<ArrayValue>(block.Find(SpawnPlaces.ArrayOf(k))?.Value));

        foreach (var path in PlayerSpawnPackages)
        {
            if (!catalog.TryGetPackageFile(path, out _))
            {
                output.WriteLine($"{path}: not in these game files");
                continue;
            }

            var package = ModdableAssets.ReadPackage(catalog, path);
            for (var i = 0; i < package.Exports.Count; i++)
            {
                var name = package.ResolveName(package.Exports[i].ObjectName);
                PropertyBlock properties;
                try
                {
                    properties = package.ReadProperties(i);
                }
                catch (Exception ex) when (ex is FormatException or EndOfStreamException or NotSupportedException or InvalidDataException)
                {
                    output.WriteLine($"{path}:{name} ({package.GetExportClassName(i)}): not readable ({ex.Message})");
                    continue;
                }

                foreach (var line in PropertyFormatter.Format(properties.Properties.Where(p => SpawnWords.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList()))
                {
                    output.WriteLine($"{path}:{name} ({package.GetExportClassName(i)}) {line}");
                }
            }
        }
    }
}
