using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using Xunit;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// The mesh vertex shader turns normals with the cofactor matrix instead of inverting every instance's matrix per vertex
/// (a forest is vertex-bound). Flat-shaded faces under a stretch of 4:1:1, a mirror and a turn are lit exactly as the
/// inverse-transpose computed on the CPU says, and a mirrored instance keeps its outside lit.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class NormalMatrixParityTests(ITestOutputHelper output)
{
    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.3f, -0.5f, -0.8f));

    [GlFact]
    public void StretchedMirroredAndTurnedFacesShadeLikeTheInverseTranspose()
    {
        using var gl = GlHarness.Create(512, 256);
        gl.Renderer.Settings = new RenderSettings
        {
            SkyColor = Vector3.Zero,
            GroundColor = Vector3.Zero,
            LightColor = Vector3.One,
            LightDirection = Light,
            ShowGrid = false,
            ClearColor = Vector4.Zero,
        };

        var (mesh, faces) = FacetedBall();
        var handle = gl.Renderer.AddMesh(PreparedMesh.From(mesh, MeshSpace.Gl));
        Matrix4x4[] models =
        [
            Matrix4x4.CreateScale(4f, 1f, 1f) * Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(-1300f, 0f, 0f),
            Matrix4x4.CreateScale(-1.5f, 1f, 1f) * Matrix4x4.CreateRotationX(0.3f) * Matrix4x4.CreateTranslation(600f, 0f, 0f),
            Matrix4x4.CreateRotationZ(0.7f) * Matrix4x4.CreateRotationY(-0.4f) * Matrix4x4.CreateTranslation(1700f, 0f, 0f),
        ];
        var scene = new Scene();
        for (var i = 0; i < models.Length; i++)
        {
            scene.Add(handle, models[i], (uint)(i + 1));
        }

        var camera = new FlyCamera { FieldOfView = 40f };
        camera.SetClipRange(10f, 20_000f);
        camera.Orbit(Vector3.Zero, 90f, -20f, 4000f); // looking along +Z, a little down
        gl.Renderer.Render(gl.Target, scene, camera);
        var image = gl.Target.ReadColorRgba();

        var (checkedFaces, worst) = (0, 0);
        for (var i = 0; i < models.Length; i++)
        {
            Assert.True(Matrix4x4.Invert(models[i], out var inverse));
            var normalMatrix = Matrix4x4.Transpose(inverse);
            foreach (var (centroid, normal) in faces)
            {
                var world = Vector3.Transform(centroid, models[i]);
                var n = Vector3.Normalize(Vector3.TransformNormal(normal, normalMatrix));
                if (Vector3.Dot(n, Vector3.Normalize(camera.Position - world)) < 0.35f)
                {
                    continue; // seen edge-on or from behind
                }

                var (x, y) = FoliageAntiAliasingTests.ToPixel(camera, world, gl.Target);
                var expected = (int)MathF.Round(Srgb(MathF.Max(Vector3.Dot(n, -Light), 0f)) * 255f);
                var actual = gl.Pixel(image, x, y)[1];
                worst = Math.Max(worst, Math.Abs(actual - expected));
                checkedFaces++;
                Assert.True(Math.Abs(actual - expected) <= 1, $"instance {i}, face normal {normal}: stored {actual}, the inverse-transpose gives {expected}");
            }
        }

        output.WriteLine($"{checkedFaces} faces checked, largest difference {worst}/255");
        Assert.True(checkedFaces >= 9, $"only {checkedFaces} faces faced the camera");
    }

    private static float Srgb(float c) => c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;

    /// <summary>
    /// A flat-shaded ball of 24 faces (radius 300, every triangle its own vertices and normal: the octahedron's faces split
    /// in three), and each face's centroid and outward normal.
    /// </summary>
    private static (MeshData Mesh, List<(Vector3 Centroid, Vector3 Normal)> Faces) FacetedBall()
    {
        Vector3[] tips = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        var (positions, normals, indices, faces) = (new List<float>(), new List<float>(), new List<uint>(), new List<(Vector3, Vector3)>());
        foreach (var sx in new[] { 1, -1 })
        {
            foreach (var sy in new[] { 1, -1 })
            {
                foreach (var sz in new[] { 1, -1 })
                {
                    var (a, b, c) = (tips[sx > 0 ? 0 : 1] * 300f, tips[sy > 0 ? 2 : 3] * 300f, tips[sz > 0 ? 4 : 5] * 300f);
                    var mid = Vector3.Normalize(new Vector3(sx, sy, sz)) * 300f * 0.8f;
                    foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                    {
                        var n = Vector3.Normalize(Vector3.Cross(q - p, mid - p));
                        n = Vector3.Dot(n, mid) < 0f ? -n : n;
                        var first = (uint)(positions.Count / 3);
                        foreach (var v in new[] { p, q, mid })
                        {
                            positions.AddRange([v.X, v.Y, v.Z]);
                            normals.AddRange([n.X, n.Y, n.Z]);
                        }

                        // Wound counter-clockwise seen from outside, as the renderer's front faces are.
                        var outward = Vector3.Dot(Vector3.Cross(q - p, mid - p), n) > 0f;
                        indices.AddRange(outward ? new[] { first, first + 1, first + 2 } : new[] { first, first + 2, first + 1 });
                        faces.Add(((p + q + mid) / 3f, n));
                    }
                }
            }
        }

        return (MeshData.Create("FacetedBall", [.. positions], [.. indices], [.. normals]), faces);
    }
}
