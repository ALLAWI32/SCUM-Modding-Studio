using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering.Resources;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// pheidian (3.5.1 over SCUM 0.6 files): "Mesh 'SK_SentryRobot_01' is invalid: Index 0 is out of range for 0 vertices",
/// in every frame of the map view, and the streaming stopped. A mesh that cannot be drawn is left out where it is loaded
/// (<see cref="BrokenMeshes"/>), with one warning per mesh.
/// </summary>
public sealed class BrokenMeshesTests
{
    private static MeshData Triangle(string name) => MeshData.Create(name, [0f, 0f, 0f, 100f, 0f, 0f, 0f, 100f, 0f], [0u, 1u, 2u]);

    [Fact]
    public void AMeshWithIndicesButNoVerticesIsNothingToDraw()
    {
        var empty = MeshData.Create("SK_SentryRobot_01", [], [0u, 1u, 2u]);

        // What the frame threw before: the upload validates the vertex data.
        var thrown = Assert.Throws<ArgumentException>(() => PreparedMesh.From(empty));
        Assert.Contains("Index 0 is out of range for 0 vertices", thrown.Message, StringComparison.Ordinal);

        var (lods, sizes, problem) = BrokenMeshes.Usable(new MeshLodChain([empty], [1f]));
        Assert.Empty(lods);
        Assert.Empty(sizes);
        Assert.Contains("Index 0 is out of range for 0 vertices", problem, StringComparison.Ordinal);
        Assert.NotNull(BrokenMeshes.ProblemOf(MeshData.Create("Nothing", [], [])));
        Assert.Null(BrokenMeshes.ProblemOf(Triangle("Fine")));
    }

    [Fact]
    public void ABrokenLodIsLeftOutAndTheOthersStay()
    {
        var broken = MeshData.Create("SM_Rock", [], [0u, 1u, 2u]);
        var whole = Triangle("SM_Rock");
        var coarse = Triangle("SM_Rock");

        var (lods, sizes, problem) = BrokenMeshes.Usable(new MeshLodChain([broken, whole, coarse], [1f, 0.4f, 0.1f]));

        Assert.Equal(new[] { whole, coarse }, lods);
        Assert.Equal(new[] { 0.4f, 0.1f }, sizes);
        Assert.StartsWith("LOD 0:", problem, StringComparison.Ordinal);
        var packed = PreparedMesh.FromLods(lods, sizes.ToArray()); // uploads without throwing
        Assert.Equal(2, packed.Lods.Length);

        var fine = BrokenMeshes.Usable(new MeshLodChain([whole], [1f]));
        Assert.Null(fine.Problem);
        Assert.Single(fine.Lods);
    }

    [Fact]
    public void EachBrokenMeshIsReportedOnce()
    {
        var logger = new CountingLogger();
        var path = $"/Game/Test/SK_Broken_{Guid.NewGuid():N}.SK_Broken";

        for (var frame = 0; frame < 3; frame++)
        {
            BrokenMeshes.Warn(logger, path, "Index 0 is out of range for 0 vertices.");
        }

        BrokenMeshes.Warn(logger, path + "_Other", "no vertices.");
        Assert.Equal(2, logger.Warnings);
        Assert.True(BrokenMeshes.WasWarned(path));
    }

    private sealed class CountingLogger : ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
