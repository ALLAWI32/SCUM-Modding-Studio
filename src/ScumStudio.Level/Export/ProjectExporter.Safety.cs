using System.Text;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.World;

namespace ScumStudio.Level.Export;

/// <summary>
/// What keeps an export safe for servers and players' worlds: the server cook's sound-event stubs (D12), the install
/// note (every player needs the client pak, the server the server pak: D13, D19) and the item spawners an export no
/// longer has (D02: the server keeps what they spawned).
/// </summary>
public sealed partial class ProjectExporter
{
    /// <summary>File in the export folder's hidden work folder listing the item spawners the last export placed (one <c>level|actor|item</c> per line).</summary>
    public const string ItemSpawnersFileName = "item-spawners.txt";

    /// <summary>What an item spawner leaves behind (the report's note).</summary>
    public const string ItemSpawnerNote =
        "Items these spawners already spawned stay in players' worlds: the game saves every spawned item, and a pak cannot remove it. "
        + "Remove them with Map → Clean saved items: stop the server (or quit the game), choose its SCUM.db, Find, Remove ticked, start the server (a backup is made first).";

    /// <summary>Where a cook's files go (the report's install note): everything in the mod's folder, nothing else with it.</summary>
    public static string InstallNote(ProjectSourceRole role) => role == ProjectSourceRole.Server
        ? "SERVER files. Copy every .pak and .sig in this folder (the project's pak and the Craftables pak alike) into the dedicated server's SCUM\\Content\\Paks\\~mods (make the ~mods folder when it is missing) "
            + "and start the server with -fileopenlog (without it the server refuses every mod pak and stops). Every player needs the CLIENT folder of the same export "
            + "in the game's SCUM\\Content\\Paks\\~mods. Never install this project's page paks (the \"- pages\" folder) together with these. Restart the server and the game after copying."
        : "CLIENT files. Copy every .pak and .sig in this folder (the project's pak and the Craftables pak alike) into the game's SCUM\\Content\\Paks\\~mods on every player's PC (make the ~mods folder when it is missing). "
            + "A server with this mod needs the SERVER folder of the same export in its SCUM\\Content\\Paks\\~mods, started with -fileopenlog (without it the server refuses every mod pak and stops). "
            + "Never install this project's page paks (the \"- pages\" folder) together with these. Restart the game and the server after copying.";

    /// <summary>
    /// The stub pass of one cook's staging: the server's events become its 12-byte stubs (<see cref="ServerEventStubs"/>);
    /// a client pak that would carry such stubs (a mod imported from a server's mods folder) gets a warning.
    /// </summary>
    /// <exception cref="ServerEventStubException">Server cook: events to replace and no stub.</exception>
    internal static async Task<IReadOnlyList<string>> StubEventsAsync(
        string staging, AssetCatalog catalog, ExportOptions options, ProjectSourceRole role, List<string> warnings, CancellationToken cancellationToken)
    {
        if (role == ProjectSourceRole.Server)
        {
            return await Export.ServerEventStubs.ApplyAsync(staging, () => options.ServerEventStubs?.Invoke() ?? Export.ServerEventStubs.FromCatalog(catalog),
                catalog.ProjectName, cancellationToken).ConfigureAwait(false);
        }

        var stubs = Export.ServerEventStubs.FindEvents(staging, catalog.ProjectName).Where(e => e.Size == Pak.Server.AkAudioEventStubBuilder.StubExportSize).ToList();
        if (stubs.Count > 0)
        {
            warnings.Add($"The client pak carries {stubs.Count} server-only sound event stub(s) (e.g. {stubs[0].GamePath}): the imported mod was taken from a server's mods folder, "
                + "so those sounds are silent for players. Import the mod's client pak instead.");
        }

        return [];
    }

    /// <summary>
    /// The item spawners gone from this export (see <see cref="ExportResult.RemovedItemSpawners"/>): the game's that it
    /// deletes, and those an earlier export into <paramref name="roleDirectory"/> placed that this one no longer has. The
    /// list of placed ones is written for the next export.
    /// </summary>
    internal static List<string> ItemSpawnersGone(EditState state, string roleDirectory, IReadOnlyDictionary<string, LevelDocument?> documents, List<string> warnings)
    {
        var gone = ItemSpawnersGone(state, roleDirectory, documents, warnings, out var placed);
        WriteItemSpawners(roleDirectory, placed, warnings);
        return gone;
    }

    /// <summary>
    /// <see cref="ItemSpawnersGone(EditState, string, IReadOnlyDictionary{string, LevelDocument?}, List{string})"/> without writing the
    /// list: <paramref name="placed"/> is what this export places, for <see cref="WriteItemSpawners"/> once its pak is written (an
    /// export the package check refuses leaves the list of the last export that wrote a pak).
    /// </summary>
    internal static List<string> ItemSpawnersGone(EditState state, string roleDirectory, IReadOnlyDictionary<string, LevelDocument?> documents, List<string> warnings,
        out IReadOnlyList<string> placed)
    {
        var gone = new List<string>();
        foreach (var deleted in state.DeletedActors.OrderBy(a => a.Level, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Actor, StringComparer.OrdinalIgnoreCase))
        {
            if (documents.GetValueOrDefault(deleted.Level)?.FindActor(deleted.Actor) is { } actor && IsItemSpawner(actor))
            {
                gone.Add($"{deleted.Level}: {deleted.Actor}{ItemOf(actor)}");
            }
        }

        placed = state.AddedActors.Values.OfType<AddBlueprintActorOp>()
            .Where(b => b.Item is not null || string.Equals(b.ClassPath, PlacedBlueprintIndex.WorldItemSpawnerClass, StringComparison.Ordinal))
            .Select(b => string.Join('|', b.Level, b.NewName, b.Item ?? string.Empty))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var file = Path.Combine(roleDirectory, ItemSpawnersFileName);
        try
        {
            if (File.Exists(file))
            {
                var now = placed.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(file).Where(l => l.Length > 0 && !now.Contains(l)))
                {
                    var parts = line.Split('|');
                    gone.Add(parts.Length == 3 ? $"{parts[0]}: {parts[1]}{(parts[2].Length > 0 ? $" ({parts[2]})" : string.Empty)} (placed by an earlier export)" : line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The list of placed item spawners ({file}) could not be read: {ex.Message}");
        }

        return gone;
    }

    /// <summary>Writes <see cref="ItemSpawnersFileName"/> in <paramref name="roleDirectory"/>: the item spawners this export placed.</summary>
    internal static void WriteItemSpawners(string roleDirectory, IReadOnlyList<string> placed, List<string> warnings)
    {
        var file = Path.Combine(roleDirectory, ItemSpawnersFileName);
        try
        {
            File.WriteAllLines(file, placed, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The list of placed item spawners ({file}) could not be written: {ex.Message}");
        }
    }

    /// <summary>A world item spawner (<c>/Script/SCUM.WorldItemSpawner</c>) or an actor with a spawner component of its own.</summary>
    public static bool IsItemSpawner(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return string.Equals(actor.ClassPath, PlacedBlueprintIndex.WorldItemSpawnerClass, StringComparison.Ordinal)
            || actor.Components.Any(c => !c.IsSynthesized && c.ClassPath.EndsWith(".WorldItemSpawnerComponent", StringComparison.Ordinal));
    }

    private static string ItemOf(ActorRecord actor) =>
        actor.Components.SelectMany(c => c.SpawnMarkers).Select(m => m.ItemClassPath).FirstOrDefault(i => i is not null) is { } item ? $" ({item})" : string.Empty;
}
