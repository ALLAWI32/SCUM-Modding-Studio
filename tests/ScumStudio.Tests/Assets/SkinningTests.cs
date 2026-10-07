using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// CPU skinning for the posed spawn stand-ins (owner: traders "not with open arms"): a pose moves the vertices of the
/// bones it names, blends shared vertices, leaves the rest in the bind pose. Synthetic bones and weights.
/// </summary>
public sealed class SkinningTests
{
    // root at the origin, a "pelvis" 10 up, a "spine" 10 above that.
    private static readonly SkinBone[] Bones =
    [
        new("root", -1, FTransform.Identity),
        new("pelvis", 0, new FTransform(new FVector(0f, 0f, 10f))),
        new("spine", 1, new FTransform(new FVector(0f, 0f, 10f))),
    ];

    [Fact]
    public void BindWorldsComposeUpTheChain()
    {
        var worlds = Skinning.BindWorlds(Bones);
        Assert.Equal(new FVector(0f, 0f, 10f), worlds[1].Translation);
        Assert.Equal(new FVector(0f, 0f, 20f), worlds[2].Translation);
    }

    [Fact]
    public void APoseTakesRotationsFromTheAnimationAndLengthsFromTheMesh()
    {
        // The animation lifts the pelvis to 5 (its height counts) and would stretch the spine to 30 (ignored: the
        // mesh's bone length stays), turning the spine a quarter about X.
        var quarter = FQuat.MakeFromEuler(new FVector(90f, 0f, 0f));
        var locals = new Dictionary<string, FTransform>
        {
            ["pelvis"] = new(FQuat.Identity, new FVector(0f, 0f, 5f), FVector.One),
            ["spine"] = new(quarter, new FVector(0f, 0f, 30f), FVector.One),
        };

        var pose = Skinning.Pose(Bones, locals);

        Assert.Equal(new FVector(0f, 0f, 5f), pose["pelvis"].Translation);
        Assert.Equal(new FVector(0f, 0f, 15f), pose["spine"].Translation);
        Assert.Equal(90f, MathF.Abs(pose["spine"].Rotation.Rotator().Roll), 0.01f);
        Assert.Equal(FTransform.Identity, pose["root"]);
    }

    [Fact]
    public void SkinnedVerticesFollowTheirBonesAndBlend()
    {
        // v0 on the spine's tip, fully the spine's; v1 the same point shared half with the pelvis; v2 on the root.
        float[] positions = [0f, 0f, 30f, 0f, 0f, 30f, 5f, 0f, 0f];
        float[] normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f];
        var mesh = MeshData.Create("rig", positions, [0u, 1u, 2u], normals);
        var weights = new SkinWeights(Bones, [0, 1, 3, 4], [2, 1, 2, 0], [1f, 0.5f, 0.5f, 1f]);
        var quarter = FQuat.MakeFromEuler(new FVector(90f, 0f, 0f));
        // The spine bends a quarter at its base (bind world: 20 up), the pelvis stays.
        var pose = new Dictionary<string, FTransform> { ["spine"] = new(quarter, new FVector(0f, 0f, 20f), FVector.One) };

        var skinned = Skinning.Skin(mesh, weights, pose);

        Assert.NotSame(mesh, skinned);
        var v0 = new FVector(skinned.Positions[0], skinned.Positions[1], skinned.Positions[2]);
        Assert.Equal(20f, v0.Z, 1e-3f); // the tip swung into the XY plane of the joint
        Assert.Equal(10f, MathF.Abs(v0.Y), 1e-3f);
        Assert.Equal(0f, v0.X, 1e-3f);
        var v1 = new FVector(skinned.Positions[3], skinned.Positions[4], skinned.Positions[5]);
        Assert.Equal((v0.Z + 30f) / 2f, v1.Z, 1e-3f); // halfway between the bind place and the swung one
        Assert.Equal(v0.Y / 2f, v1.Y, 1e-3f);
        Assert.Equal(new FVector(5f, 0f, 0f), new FVector(skinned.Positions[6], skinned.Positions[7], skinned.Positions[8])); // the root did not move
        Assert.Equal(0f, skinned.Normals[2], 1e-4f); // v0's normal turned with the spine
        Assert.Equal(1f, skinned.Normals[8], 1e-4f);
        Assert.Equal(25f, skinned.Bounds.Max.Z, 1e-3f); // the half-blended v1 is the highest point now
        Assert.Equal(30f, mesh.Positions[2]); // the input is untouched
    }

    [Fact]
    public void APoseWithoutTheMeshsBonesLeavesItAlone()
    {
        var mesh = MeshData.Create("rig", [0f, 0f, 30f, 0f, 0f, 30f, 5f, 0f, 0f], [0u, 1u, 2u]);
        var weights = new SkinWeights(Bones, [0, 1, 2, 3], [2, 1, 0], [1f, 1f, 1f]);
        Assert.Same(mesh, Skinning.Skin(mesh, weights, new Dictionary<string, FTransform> { ["head"] = FTransform.Identity }));
        Assert.Same(mesh, Skinning.Skin(mesh, new SkinWeights(Bones, [0, 1], [0], [1f]), new Dictionary<string, FTransform> { ["root"] = FTransform.Identity })); // weights for another vertex count
    }
}
