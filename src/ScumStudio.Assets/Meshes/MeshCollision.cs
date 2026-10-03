using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.PhysicsEngine;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Assets.Meshes;

/// <summary>One simple collision shape of a mesh as a box in the mesh's own space (cm): its middle, turn and full size.</summary>
/// <param name="Center">Middle of the box.</param>
/// <param name="Rotation">Turn of the box.</param>
/// <param name="Size">Full size along the box's own X, Y and Z.</param>
public sealed record CollisionBox(FVector Center, FRotator Rotation, FVector Size);

/// <summary>
/// A static mesh's simple collision (its <c>BodySetup</c>'s <c>AggGeom</c>) as boxes: boxes as they are, convex hulls,
/// spheres and capsules by the box around them. Enough to give a bent piece collision the game can build without cooking
/// (boxes need no cooked data).
/// </summary>
/// <param name="Boxes">Every shape as a box.</param>
/// <param name="Convex">How many convex hulls the mesh has.</param>
/// <param name="BoxElements">How many boxes.</param>
/// <param name="Spheres">How many spheres.</param>
/// <param name="Capsules">How many capsules.</param>
/// <param name="TraceFlag">The body's <c>CollisionTraceFlag</c> (e.g. <c>CTF_UseComplexAsSimple</c>), or empty for the default.</param>
/// <param name="BodySetupGuid">
/// The body's <c>BodySetupGuid</c> (A, B, C, D as the engine stores them). A spline mesh component whose
/// <c>CachedMeshBodySetupGuid</c> differs rebuilds its collision from the mesh when it loads, replacing its own.
/// </param>
public sealed record MeshCollisionInfo(IReadOnlyList<CollisionBox> Boxes, int Convex, int BoxElements, int Spheres, int Capsules, string TraceFlag,
    (uint A, uint B, uint C, uint D) BodySetupGuid = default);

/// <summary>Reads a static mesh's simple collision (see <see cref="MeshCollisionInfo"/>).</summary>
public static class MeshCollision
{
    /// <summary>The mesh's simple collision, or null when it has no body setup.</summary>
    public static MeshCollisionInfo? Read(UStaticMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.BodySetup is null || !mesh.BodySetup.TryLoad<UBodySetup>(out var setup) || setup is null)
        {
            return null;
        }

        var geom = setup.AggGeom;
        var boxes = new List<CollisionBox>();
        foreach (var b in geom?.BoxElems ?? [])
        {
            boxes.Add(new CollisionBox(V(b.Center), new FRotator(b.Rotation.Pitch, b.Rotation.Yaw, b.Rotation.Roll), new FVector(b.X, b.Y, b.Z)));
        }

        foreach (var c in geom?.ConvexElems ?? [])
        {
            if (c.VertexData is not { Length: > 0 } vertices)
            {
                continue;
            }

            var t = c.Transform;
            var transform = new FTransform(
                new FQuat(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W),
                V(t.Translation),
                t.Scale3D.X == 0f && t.Scale3D.Y == 0f && t.Scale3D.Z == 0f ? FVector.One : V(t.Scale3D));
            var (min, max) = (new FVector(float.MaxValue), new FVector(float.MinValue));
            foreach (var v in vertices)
            {
                var p = transform.TransformPosition(V(v));
                (min, max) = (FVector.Min(min, p), FVector.Max(max, p));
            }

            boxes.Add(new CollisionBox((min + max) * 0.5f, FRotator.Zero, max - min));
        }

        foreach (var s in geom?.SphereElems ?? [])
        {
            boxes.Add(new CollisionBox(V(s.Center), FRotator.Zero, new FVector(s.Radius * 2f)));
        }

        foreach (var s in geom?.SphylElems ?? [])
        {
            var d = s.Radius * 2f;
            boxes.Add(new CollisionBox(V(s.Center), new FRotator(s.Rotation.Pitch, s.Rotation.Yaw, s.Rotation.Roll), new FVector(d, d, s.Length + d)));
        }

        var flag = setup.Properties.FirstOrDefault(p => p.Name.Text == "CollisionTraceFlag")?.Tag?.GenericValue?.ToString() ?? string.Empty;
        var g = setup.BodySetupGuid;
        return new MeshCollisionInfo(boxes, geom?.ConvexElems?.Length ?? 0, geom?.BoxElems?.Length ?? 0, geom?.SphereElems?.Length ?? 0, geom?.SphylElems?.Length ?? 0, flag,
            (g.A, g.B, g.C, g.D));
    }

    private static FVector V(CUE4Parse.UE4.Objects.Core.Math.FVector v) => new(v.X, v.Y, v.Z);
}
