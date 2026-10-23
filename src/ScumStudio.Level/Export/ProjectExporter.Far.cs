using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Level.Export;

public sealed partial class ProjectExporter
{
    /// <summary>
    /// <paramref name="state"/> with the placed meshes that stand out of their level's reach moved into the terrain tile under
    /// them (<see cref="FarPlacement"/>), against this cook's own tiles. The levels read for added instances are left in
    /// <paramref name="documents"/> (null when unreadable) for the rewrite. Nothing moves when nothing was placed. A moved mesh
    /// keeps its name unless the tile already uses it; the server's export (with <paramref name="client"/>, the client's game
    /// files) also avoids the names the client's tile uses, so both cooks give it the same name where the client's tile has
    /// one the server's lacks.
    /// </summary>
    private (EditState State, IReadOnlyList<FarPlacement.Move> Moves) PlaceFar(EditState state, AssetCatalog catalog, AssetCatalog? client,
        Cue4ParseLevelReader reader, Dictionary<string, LevelDocument?> documents, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!state.AddedActors.Values.OfType<AddStaticMeshActorOp>().Any() && state.AddedInstances.Count == 0)
        {
            return (state, []);
        }

        LevelDocument? Document(string level)
        {
            if (!documents.TryGetValue(level, out var document))
            {
                try
                {
                    document = catalog.TryGetPackageFile(level, out _) ? LevelDocument.Load(reader, level, cancellationToken) : null;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
                {
                    document = null; // its added instances stay instances; the rewrite reports what it cannot read
                }

                documents[level] = document;
            }

            return document;
        }

        IEnumerable<string> Names(string level) =>
            client is null || ReferenceEquals(client, catalog) ? NamesIn(catalog, level) : [.. NamesIn(catalog, level), .. NamesIn(client, level)];

        static IReadOnlyList<string> NamesIn(AssetCatalog files, string level)
        {
            try
            {
                return files.TryGetPackageFile(level, out _) ? files.GetExports(level).Select(e => e.Name).ToList() : [];
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
            {
                return [];
            }
        }

        try
        {
            var (moved, moves) = FarPlacement.Apply(state, FarPlacement.TilesOf(catalog), level => FarPlacement.TileInfoOf(catalog, level), Document, Names);
            foreach (var move in moves)
            {
                _logger.LogInformation("{Line}", move.Describe());
            }

            return (moved, moves);
        }
        catch (ArgumentException ex)
        {
            warnings.Add($"Placed objects far from their level stay in it ({ex.Message}); the game draws them only near that level.");
            return (state, []);
        }
    }
}
