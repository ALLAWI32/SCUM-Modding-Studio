using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Viewport;

/// <summary>
/// Meshes the game files carry in a form the views cannot draw (a LOD with indices but no vertices, an index past the
/// vertex list, a section past the indices): they are left out where they are loaded, with one warning per mesh for the
/// whole session. Drawn, such a mesh threw in every frame of the map view and the view stopped streaming (pheidian, 3.5.1
/// over SCUM 0.6 files: "Mesh 'SK_SentryRobot_01' is invalid: Index 0 is out of range for 0 vertices").
/// </summary>
public static class BrokenMeshes
{
    private static readonly ConcurrentDictionary<string, byte> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The LODs of <paramref name="chain"/> that can be drawn, with their screen sizes (finest first), and the problems of
    /// the first one left out (null when none was).
    /// </summary>
    public static (IReadOnlyList<MeshData> Lods, IReadOnlyList<float> ScreenSizes, string? Problem) Usable(MeshLodChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var lods = new List<MeshData>(chain.Lods.Count);
        var sizes = new List<float>(chain.Lods.Count);
        string? problem = null;
        for (var i = 0; i < chain.Lods.Count; i++)
        {
            if (ProblemOf(chain.Lods[i]) is { } wrong)
            {
                problem ??= $"LOD {i}: {wrong}";
                continue;
            }

            lods.Add(chain.Lods[i]);
            sizes.Add(i < chain.ScreenSizes.Count ? chain.ScreenSizes[i] : 1f);
        }

        return (lods, sizes, problem);
    }

    /// <summary>
    /// Why <paramref name="mesh"/> cannot be drawn (<see cref="MeshData.Validate"/>, or no vertices at all: nothing to draw),
    /// or null when it can.
    /// </summary>
    public static string? ProblemOf(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var problems = mesh.Validate();
        if (problems.Count > 0)
        {
            return string.Join(" ", problems);
        }

        return mesh.VertexCount == 0 ? "no vertices." : null;
    }

    /// <summary>Logs that <paramref name="meshPath"/> is not drawn because of <paramref name="problem"/>, once per mesh and session.</summary>
    public static void Warn(ILogger logger, string meshPath, string problem)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (Warned.TryAdd(meshPath, 0))
        {
            logger.LogWarning("Mesh {Mesh} is not drawn: the game files hold it in a form the map view cannot draw ({Problem}).", meshPath, problem);
        }
    }

    /// <summary>True when a warning was logged for <paramref name="meshPath"/> (tests).</summary>
    internal static bool WasWarned(string meshPath) => Warned.ContainsKey(meshPath);
}
