using System.Numerics;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Level;

/// <summary>
/// SCUM's roads are landscape spline segments: <c>SplineMeshComponent</c>s owned by the landscape streaming proxy. The
/// reader must carry their bend (<see cref="ComponentRecord.SplineMesh"/>) so the viewer can draw a continuous road
/// instead of straight slices.
/// </summary>
public sealed class SplineMeshReaderTests
{
    [MapSliceFact]
    public void LandscapeSplineSegmentsCarryTheirSplineParameters()
    {
        using var catalog = MapSlice.Open();
        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), MapSlice.MapsPath + "Landscape_A_0_3");
        var segments = doc.Actors.SelectMany(a => a.Components).Where(c => c.ClassName == "SplineMeshComponent").ToList();
        Assert.NotEmpty(segments);
        Assert.All(segments, c =>
        {
            Assert.NotNull(c.SplineMesh);
            Assert.NotNull(c.StaticMeshPath);
            Assert.True(c.IsStaticMeshComponent, c.Name);
            Assert.NotEqual(c.SplineMesh!.StartPos, c.SplineMesh.EndPos);
            Assert.False(c.SplineMesh.StartTangent.IsNearlyZero(), c.Name);
            Assert.Equal(SplineMeshAxis.X, c.SplineMesh.ForwardAxis);
            Assert.Equal(FVector.Up, c.SplineMesh.SplineUpDir);
        });

        // Roads leave the scale at the engine default (not stored); river banks store 1.212 — both must come through.
        Assert.Contains(segments, c => c.StaticMeshPath!.Contains("/Road/", StringComparison.Ordinal) && c.SplineMesh!.StartScale == Vector2.One);
        Assert.Contains(segments, c => c.SplineMesh!.StartScale != Vector2.One && c.SplineMesh.StartScale == c.SplineMesh.EndScale);

        // Every segment becomes a placement that still names the real mesh, with the bend attached to its component.
        var placements = LevelScenePreparer.CollectPlacements(doc, 0);
        var roads = placements.Where(p => p.Component?.SplineMesh is not null).ToList();
        Assert.Equal(segments.Count, roads.Count);
        Assert.All(roads, p => Assert.DoesNotContain(SplineMeshPlacements.KeyMarker, p.MeshPath));
    }
}
