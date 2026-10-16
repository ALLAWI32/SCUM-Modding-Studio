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
    /// <summary>File in the role folder listing the item spawners the last export placed (one <c>level|actor|item</c> per line).</summary>
    public const string ItemSpawnersFileName = "item-spawners.txt";

    /// <summary>What an item spawner leaves behind (the report's note).</summary>
    public const string ItemSpawnerNote =
        "Items these spawners already spawned stay in players' worlds: the game saves every spawned item, and a pak cannot remove it. "
        + "Remove them with Map → Clean saved items: stop the server (or quit the game), choose its SCUM.db, Find, Remove ticked, start the server (a backup is made first).";

    /// <summary>Where a cook's paks go (the report's install note).</summary>
    public static string InstallNote(ProjectSourceRole role) => role == ProjectSourceRole.Server
        ? "This is the SERVER pak: it goes into the dedicated server's mods folder. Every player needs the CLIENT pak of the same export in the game's Paks\\~mods, "
            + "and the Craftables paks go the same way (client pak on every player, server pak on the server). Restart the server and the game after copying."
        : "This is the CLIENT pak: every player needs it in the game's Paks\\~mods. A server needs the SERVER pak of the same export in its mods folder, "
            + "and the Craftables paks go the same way (client pak on every player, server pak on the server). Restart the game and the server after copying.";

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
        var gone = new List<string>();
        foreach (var deleted in state.DeletedActors.OrderBy(a => a.Level, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Actor, StringComparer.OrdinalIgnoreCase))
        {
            if (documents.GetValueOrDefault(deleted.Level)?.FindActor(deleted.Actor) is { } actor && IsItemSpawner(actor))
            {
                gone.Add($"{deleted.Level}: {deleted.Actor}{ItemOf(actor)}");
            }
        }

        var placed = state.AddedActors.Values.OfType<AddBlueprintActorOp>()
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

            File.WriteAllLines(file, placed, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The list of placed item spawners ({file}) could not be read or written: {ex.Message}");
        }

        return gone;
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
