using System.Numerics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// r37 (owner, 3.4.0: brown patches with dark ragged rims on the ground): the close-up ground grain fades with the share
/// of the ground its four layers cover, instead of staying at full strength until their weight reaches zero and stopping
/// there in a ragged line around a patch of a fifth layer.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class TerrainDetailFadeTests(ITestOutputHelper output)
{
    [GlFact]
    public void TheGrainFadesWhereItsLayersCoverLittle()
    {
        using var gl = GlHarness.Create(320, 320);
        gl.UseUnlit();
        var full = GrainShare(gl, [255, 255]);
        var ramp = GrainShare(gl, [255, 0]); // one layer from all of the ground (left quarter) to none (right quarter)
        output.WriteLine($"grain over {full:P0} of the image with full cover, {ramp:P0} with the cover going 1 -> 0 across");

        Assert.True(full > 0.4, $"the grain shows over {full:P0} only");
        // Full strength down to weight zero kept the grain over three quarters of the plane; fading with the cover, about half.
        Assert.True(ramp < full * 0.65, $"grain over {ramp:P0} with the cover ramp against {full:P0}");
    }

    /// <summary>
    /// r37 verifier (B_4 airfield's flat brown smudge): where the four layers leave the ground to a fifth (a dry grass patch
    /// in a component with six layers on its land), that ground takes the fifth layer's grain instead of none.
    /// </summary>
    [GlFact]
    public void TheRestOfTheGroundTakesTheFifthLayersGrain()
    {
        using var gl = GlHarness.Create(320, 320);
        gl.UseUnlit();
        var full = GrainShare(gl, [255, 255]);
        var ramp = GrainShare(gl, [255, 0]);
        var rest = GrainShare(gl, [255, 0], withRest: true); // the same ramp, the rest of the ground to a fifth layer
        output.WriteLine($"grain over {full:P0} with full cover, {ramp:P0} with the cover going 1 -> 0, {rest:P0} with a fifth layer for the rest");

        Assert.True(rest > full * 0.9, $"grain over {rest:P0} with a fifth layer against {full:P0} with full cover");
        Assert.True(ramp < full * 0.65, $"without one: grain over {ramp:P0}");
    }

    /// <summary>The share of the image with the layer's black-and-white grain, its weight over the ground left to right <paramref name="weights"/>.</summary>
    private static double GrainShare(GlHarness gl, byte[] weights, bool withRest = false)
    {
        var r = gl.Renderer;
        using var baked = r.CreateTexture(4, 4, Enumerable.Repeat((byte)128, 4 * 4 * 4).ToArray());
        var checker = new byte[8 * 8 * 4];
        for (var i = 0; i < 64; i++)
        {
            var v = ((i % 8) / 4 + (i / 32)) % 2 == 0 ? (byte)0 : (byte)255;
            (checker[i * 4], checker[(i * 4) + 1], checker[(i * 4) + 2], checker[(i * 4) + 3]) = (v, v, v, 255);
        }

        using var layer = r.CreateTexture(8, 8, checker);
        var rgbaWeights = weights.SelectMany(w => new[] { w, (byte)0, (byte)0, (byte)0 }).ToArray();
        using var weightTexture = r.CreateTexture(weights.Length, 1, rgbaWeights, srgb: false, TextureWrap.ClampToEdge);
        var ground = r.AddMesh(PreparedMesh.From(PrimitiveMeshes.Plane(2000f), MeshSpace.Gl), baked);
        r.SetMeshTerrainDetail(ground, new TerrainDetailTextures(weightTexture, [layer, null, null, null], new Vector4(1f / 100f, 0f, 0f, 0f),
            [new Vector3(0.5f), Vector3.Zero, Vector3.Zero, Vector3.Zero], [null, null, null, null],
            withRest ? layer : null, 1f / 100f, new Vector3(0.5f)));
        var scene = new Scene();
        scene.Add(ground, Matrix4x4.Identity, 1, "ground");
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 10_000f);
        camera.Orbit(Vector3.Zero, 0f, -89.9f, 1800f);
        r.Render(gl.Target, scene, camera);
        var image = gl.Target.ReadColorRgba();
        r.RemoveMesh(ground);

        int Luma(int x, int y) => gl.Pixel(image, x, y)[1];
        var grain = 0;
        var w = gl.Target.Width;
        var h = gl.Target.Height;
        for (var y = 0; y < h - 4; y++)
        {
            for (var x = 0; x < w - 4; x++)
            {
                grain += Math.Abs(Luma(x, y) - Luma(x + 4, y)) > 30 || Math.Abs(Luma(x, y) - Luma(x, y + 4)) > 30 ? 1 : 0;
            }
        }

        return grain / (double)((w - 4) * (h - 4));
    }
}
