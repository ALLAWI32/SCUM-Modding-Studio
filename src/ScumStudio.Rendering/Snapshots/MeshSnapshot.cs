using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Rendering.Snapshots;

/// <summary>Camera and image settings of <see cref="MeshSnapshot.Render"/>.</summary>
public sealed record SnapshotOptions
{
    /// <summary>Image width in pixels.</summary>
    public int Width { get; init; } = 1280;

    /// <summary>Image height in pixels.</summary>
    public int Height { get; init; } = 720;

    /// <summary>Camera heading in degrees (UE yaw of the view direction; -135 looks at the front-right of a UE asset).</summary>
    public float Yaw { get; init; } = -135f;

    /// <summary>Camera pitch in degrees (negative looks down).</summary>
    public float Pitch { get; init; } = -20f;

    /// <summary>Distance from the bounds centre; null fits the bounds into the view.</summary>
    public float? Distance { get; init; }

    /// <summary>Draw the ground grid under the mesh.</summary>
    public bool ShowGrid { get; init; } = true;

    /// <summary>Tint (linear RGBA) applied to the mesh.</summary>
    public Vector4 Tint { get; init; } = new(0.55f, 0.55f, 0.55f, 1f);
}

/// <summary>A rendered image (top-down RGBA8) plus what was drawn.</summary>
/// <param name="Rgba">Pixels, <c>Width * Height * 4</c> bytes, top row first.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Stats">Frame statistics.</param>
/// <param name="Bounds">Bounds of the mesh in the renderer's GL world.</param>
/// <param name="Distance">Camera distance used.</param>
public sealed record SnapshotResult(byte[] Rgba, int Width, int Height, RenderStats Stats, BoundingBox Bounds, float Distance);

/// <summary>Renders a single mesh to an image (CLI <c>render mesh/obj</c>, asset thumbnails).</summary>
public static class MeshSnapshot
{
    /// <summary>
    /// Renders <paramref name="mesh"/> on <paramref name="context"/> (made current) and reads the image back.
    /// </summary>
    /// <param name="context">GL context.</param>
    /// <param name="mesh">The mesh.</param>
    /// <param name="space">Vertex coordinate system.</param>
    /// <param name="options">Camera and image settings.</param>
    /// <param name="texture">Optional albedo texture: (width, height, top-down RGBA8, sRGB).</param>
    public static SnapshotResult Render(
        IGlContext context,
        MeshData mesh,
        MeshSpace space,
        SnapshotOptions? options = null,
        (int Width, int Height, byte[] Rgba, bool Srgb)? texture = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new SnapshotOptions();
        context.MakeCurrent();
        using var renderer = new SceneRenderer(context);
        GpuTexture? gpuTexture = null;
        try
        {
            if (texture is { } t)
            {
                gpuTexture = renderer.CreateTexture(t.Width, t.Height, t.Rgba, t.Srgb);
            }

            var handle = renderer.AddMesh(mesh, space, 1f, gpuTexture);
            var scene = new Scene();
            var node = scene.Add(handle, Matrix4x4.Identity, 1);
            node.Tint = options.Tint;

            var bounds = handle.Bounds;
            var radius = MathF.Max(bounds.Extent.Length(), 1f);
            var camera = new FlyCamera();
            var aspect = (float)options.Width / options.Height;
            var distance = camera.Frame(bounds, aspect, options.Yaw, options.Pitch);
            if (options.Distance is { } d && d > 0f)
            {
                distance = d;
                camera.FitClipRange(d, radius);
                camera.Orbit(bounds.Center, options.Yaw, options.Pitch, d);
            }

            var cell = MathF.Pow(10f, MathF.Floor(MathF.Log10(radius)) - 1f);
            renderer.Settings = renderer.Settings with
            {
                ShowGrid = options.ShowGrid,
                GridHeight = bounds.IsEmpty ? 0f : bounds.Min.Y,
                GridCellSize = cell,
                GridFadeDistance = MathF.Max(distance * 3f, radius * 6f),
            };

            using var target = renderer.CreateTarget(options.Width, options.Height);
            var stats = renderer.Render(target, scene, camera);
            var rgba = target.ReadColorRgba();
            return new SnapshotResult(rgba, options.Width, options.Height, stats, bounds, distance);
        }
        finally
        {
            gpuTexture?.Dispose();
        }
    }
}
