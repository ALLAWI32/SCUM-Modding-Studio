using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Rendering;

/// <summary>
/// Impostors of trees and bushes: a mesh pictured from <c>views</c> headings around its up axis into one atlas row
/// (<see cref="Bake"/>), which <see cref="GpuMesh.SetImpostor"/> draws as a single card per instance past the last cooked
/// LOD, so a forest a few kilometres away costs a few thousand quads instead of millions of triangles.
/// </summary>
public static class ImpostorBaker
{
    /// <summary>Headings pictured (every 45°).</summary>
    public const int DefaultViews = 8;

    /// <summary>Texels of one picture, square.</summary>
    public const int DefaultCell = 128;

    /// <summary>Bump when the pictures change, so cached atlases are baked again.</summary>
    public const int Version = 1;

    /// <summary>Material name of the card's section.</summary>
    public const string Material = "#impostor";

    /// <summary>Opacity clip of the card: an atlas texel is leaf or gap.</summary>
    public const float AlphaCutoff = 0.5f;

    /// <summary>Camera elevation of the pictures, degrees: trees are mostly seen from a little above.</summary>
    public const float Elevation = 10f;

    // A narrow field of view from far away: nearly orthographic, so a picture matches a card at the bounds centre.
    private const float FieldOfView = 5f;

    /// <summary>
    /// Half the side of the square a picture covers (and the card spans) around the bounds centre, in mesh units: the
    /// widest the bounds can look from <see cref="Elevation"/> at any heading, plus 5 %.
    /// </summary>
    public static float CardHalfSize(BoundingBox bounds)
    {
        var e = bounds.Extent;
        var across = MathF.Sqrt((e.X * e.X) + (e.Z * e.Z));
        var (sin, cos) = MathF.SinCos(Elevation * UeMath.DegreesToRadians);
        return MathF.Max(MathF.Max(across, (e.Y * cos) + (across * sin)), 1f) * 1.05f;
    }

    /// <summary>
    /// The screen size below which the card takes over from the last cooked LOD (<see cref="LodMath.ChooseLod"/>): half the
    /// last threshold, at most 0.04 (a tree about 30 pixels tall on a 720 pixel view).
    /// </summary>
    public static float ScreenSizeFor(ReadOnlySpan<float> lodScreenSizes) =>
        lodScreenSizes.IsEmpty ? 0.04f : MathF.Min(lodScreenSizes[^1] * 0.5f, 0.04f);

    /// <summary>
    /// Pictures the uploaded mesh <paramref name="handle"/> from <paramref name="views"/> headings (heading k looks from
    /// azimuth k·360°/views, measured from +X toward +Z around +Y, as the card's shader picks it) at <see cref="Elevation"/>,
    /// unlit (the card is lit where it stands), into one RGBA row of <paramref name="views"/> cells of
    /// <paramref name="cell"/>² texels, top row first; alpha is 0 where the mesh is not, and those texels take the cell's
    /// mean colour so its mips get no dark fringe. The renderer's settings, time and overlay are left as they were.
    /// </summary>
    public static byte[] Bake(SceneRenderer renderer, MeshHandle handle, int views = DefaultViews, int cell = DefaultCell)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfLessThan(views, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(cell, 4);

        // The overlay (gizmo lines, brush rings) and the frame's settings belong to the view: stashed, not drawn into the atlas.
        var (settings, time) = (renderer.Settings, renderer.Time);
        OverlayLine[] lines = [.. renderer.Overlay];
        OverlayTriangle[] triangles = [.. renderer.OverlayTriangles];
        renderer.Overlay.Clear();
        renderer.OverlayTriangles.Clear();
        try
        {
            renderer.Settings = new RenderSettings
            {
                ClearColor = Vector4.Zero,
                ShowGrid = false,
                Lighting = false,
                CullPixelSize = 0f,
                LodBias = 1000f, // always the finest LOD
                ReverseZ = settings.ReverseZ,
                UseSceneEnvironment = false,
            };
            renderer.Time = 0f;

            var scene = new Scene();
            scene.Add(handle, Matrix4x4.Identity);
            var bounds = handle.Bounds;
            var half = CardHalfSize(bounds);
            var distance = half / MathF.Tan(FieldOfView * 0.5f * UeMath.DegreesToRadians);
            var camera = new FlyCamera { FieldOfView = FieldOfView };
            camera.SetClipRange(MathF.Max(distance - (half * 3f), distance * 0.01f), distance + (half * 3f));
            var atlas = new byte[views * cell * cell * 4];
            using var target = renderer.CreateTarget(cell, cell);
            for (var k = 0; k < views; k++)
            {
                camera.Orbit(bounds.Center, (k * 360f / views) + 180f, -Elevation, distance);
                renderer.Render(target, scene, camera);
                var picture = target.ReadColorRgba();
                for (var y = 0; y < cell; y++)
                {
                    picture.AsSpan(y * cell * 4, cell * 4).CopyTo(atlas.AsSpan(((y * views * cell) + (k * cell)) * 4));
                }
            }

            FillGaps(atlas, views * cell, cell, cell);
            return atlas;
        }
        finally
        {
            renderer.Settings = settings;
            renderer.Time = time;
            renderer.Overlay.Clear();
            renderer.Overlay.AddRange(lines);
            renderer.OverlayTriangles.Clear();
            renderer.OverlayTriangles.AddRange(triangles);
        }
    }

    /// <summary>Uploads an atlas made by <see cref="Bake"/> (sRGB, mipmapped, clamped); the caller owns it.</summary>
    public static GpuTexture Upload(SceneRenderer renderer, byte[] atlas, int views = DefaultViews, int cell = DefaultCell)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(atlas);
        return renderer.CreateTexture(views * cell, cell, atlas, srgb: true, TextureWrap.ClampToEdge);
    }

    /// <summary>Gives every transparent texel of each <paramref name="cellWidth"/>-wide cell the mean colour of the cell's opaque ones.</summary>
    internal static void FillGaps(byte[] rgba, int width, int height, int cellWidth)
    {
        for (var x0 = 0; x0 < width; x0 += cellWidth)
        {
            var (r, g, b, n) = (0L, 0L, 0L, 0L);
            for (var y = 0; y < height; y++)
            {
                for (var o = ((y * width) + x0) * 4; o < ((y * width) + x0 + cellWidth) * 4; o += 4)
                {
                    if (rgba[o + 3] != 0)
                    {
                        (r, g, b, n) = (r + rgba[o], g + rgba[o + 1], b + rgba[o + 2], n + 1);
                    }
                }
            }

            var mean = n == 0 ? (R: (byte)0, G: (byte)0, B: (byte)0) : (R: (byte)(r / n), G: (byte)(g / n), B: (byte)(b / n));
            for (var y = 0; y < height; y++)
            {
                for (var o = ((y * width) + x0) * 4; o < ((y * width) + x0 + cellWidth) * 4; o += 4)
                {
                    if (rgba[o + 3] == 0)
                    {
                        (rgba[o], rgba[o + 1], rgba[o + 2]) = mean;
                    }
                }
            }
        }
    }
}
