using System.Numerics;
using ScumStudio.App.Controls;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Rendering.Context;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Renders a Weapons-page preview model offscreen, as the 3D view draws it (studio light, the default view), to a PNG in
/// <c>WEAPON_SHOTS</c> (the before/after pictures of the weapon studio work); nothing without that variable or without GL.
/// </summary>
internal static class WeaponShots
{
    /// <summary>The folder the pictures go to, or null when none are asked for.</summary>
    public static string? Folder => Environment.GetEnvironmentVariable("WEAPON_SHOTS") is { Length: > 0 } folder && folder != "0" ? folder : null;

    /// <summary>Renders <paramref name="model"/> to <paramref name="name"/>.png and returns the RGBA pixels, or null when no picture is asked for or GL is missing.</summary>
    public static async Task<byte[]?> SaveAsync(PreviewModel model, string name, int width = 1280, int height = 720, float yaw = PreviewScene.DefaultViewYaw, float pitch = -12f)
    {
        if (Folder is not { } folder || GlTestEnvironment.SkipReason is not null)
        {
            return null;
        }

        byte[] rgba;
        using (var gl = GlHarness.Create(width, height))
        {
            using var scene = PreviewScene.Upload(gl.Renderer, model);
            var camera = new FlyCamera();
            var bounds = scene.Bounds;
            var distance = camera.Frame(bounds, (float)width / height, yaw, pitch);
            var radius = MathF.Max(bounds.Extent.Length(), 1f);
            var keyLight = Vector3.Normalize(camera.Forward - (camera.Up * 0.8f) + (camera.Right * 0.35f));
            gl.Renderer.Settings = MeshPreview.SettingsFor(PreviewLight.Studio, gl.Renderer.Settings, MathF.Max(radius, distance * 0.5f), keyLight) with
            {
                ShowGrid = true,
                GridHeight = bounds.Min.Y,
                GridCellSize = MeshPreview.GridCellFor(MathF.Max(bounds.Size.X, MathF.Max(bounds.Size.Y, bounds.Size.Z))),
                GridFadeDistance = MathF.Max(distance * 3f, radius * 6f),
            };
            gl.Renderer.Render(gl.Target, scene.Scene, camera);
            rgba = gl.Target.ReadColorRgba();
        }

        Directory.CreateDirectory(folder);
        await ImageExport.SavePngAsync(rgba, width, height, Path.Combine(folder, name + ".png"));
        return rgba;
    }
}
