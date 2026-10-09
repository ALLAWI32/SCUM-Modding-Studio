using System.Numerics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using Xunit;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// Discord (seviscache): "When rotating the view to look at the sky, the sun attempts to reposition itself, creating the
/// effect of jumping." The sky shader turns each pixel into a view direction by unprojecting it; the sun's disc is about a
/// degree wide, so a direction off by a few hundredths of a degree that changes as the view turns makes it jump.
/// </summary>
public sealed class SkyDirectionTests(ITestOutputHelper output)
{
    /// <summary>The sky's direction for the pixel at <paramref name="ndc"/>, as the shader computes it (float maths).</summary>
    private static Vector3 Unproject(Matrix4x4 inverse, Vector2 ndc)
    {
        static Vector3 At(Matrix4x4 m, Vector2 xy, float z)
        {
            var p = Vector4.Transform(new Vector4(xy, z, 1f), m);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }

        return Vector3.Normalize(At(inverse, ndc, 1f) - At(inverse, ndc, -1f));
    }

    /// <summary>The exact direction (double maths) through the pixel at <paramref name="ndc"/>.</summary>
    private static (double X, double Y, double Z) Exact(FlyCamera camera, float aspect, Vector2 ndc)
    {
        var t = Math.Tan(camera.FieldOfView * Math.PI / 360.0);
        var yaw = camera.Yaw * Math.PI / 180.0;
        var pitch = camera.Pitch * Math.PI / 180.0;
        (double X, double Y, double Z) f = (Math.Cos(pitch) * Math.Cos(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Sin(yaw));
        (double X, double Y, double Z) r = (-Math.Sin(yaw), 0.0, Math.Cos(yaw));
        (double X, double Y, double Z) u = ((r.Y * f.Z) - (r.Z * f.Y), (r.Z * f.X) - (r.X * f.Z), (r.X * f.Y) - (r.Y * f.X));
        var (x, y) = (ndc.X * t * aspect, ndc.Y * t);
        var d = (X: f.X + (x * r.X) + (y * u.X), Y: f.Y + (x * r.Y) + (y * u.Y), Z: f.Z + (x * r.Z) + (y * u.Z));
        var length = Math.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));
        return (d.X / length, d.Y / length, d.Z / length);
    }

    /// <summary>The angle between the two, by atan2 of the cross and dot products (acos of a float dot rounds to ~0.03°).</summary>
    private static double ErrorDegrees(Vector3 v, (double X, double Y, double Z) e)
    {
        var (cx, cy, cz) = (((double)v.Y * e.Z) - ((double)v.Z * e.Y), ((double)v.Z * e.X) - ((double)v.X * e.Z), ((double)v.X * e.Y) - ((double)v.Y * e.X));
        return Math.Atan2(Math.Sqrt((cx * cx) + (cy * cy) + (cz * cz)), (v.X * e.X) + (v.Y * e.Y) + (v.Z * e.Z)) * 180.0 / Math.PI;
    }

    [Fact]
    public void TheSkyDirectionStaysExactWhileTheViewTurnsFarOutOnTheIsland()
    {
        // The B_2 airfield, about 2.6 km out, with the Map's clip range (10 cm to 30 km), looking up toward the sun.
        var camera = new FlyCamera { Position = new Vector3(-262870f, 36663f, -27545f) };
        camera.SetClipRange(10f, 3_000_000f);
        const float Aspect = 16f / 9f;
        double worstOld = 0, worstNew = 0;
        for (var step = 0; step < 400; step++)
        {
            // Small turns as a mouse makes them, around the sun, over the whole screen
            camera.Yaw = 60f + (step * 0.037f);
            camera.Pitch = 20f + ((step % 17) * 0.11f);
            var ndc = new Vector2(((step % 9) - 4) / 5f, ((step % 7) - 3) / 4f);
            var exact = Exact(camera, Aspect, ndc);
            Matrix4x4.Invert(camera.GetViewProjection(Aspect), out var full);
            worstOld = Math.Max(worstOld, ErrorDegrees(Unproject(full, ndc), exact));
            worstNew = Math.Max(worstNew, ErrorDegrees(Unproject(SceneRenderer.SkyUnprojection(camera, Aspect), ndc), exact));
        }

        // Before the fix the sky used the full view-projection: 0.15° off, changing as the view turned (measured 2026-10-09)
        output.WriteLine($"worst direction error: full view-projection {worstOld:0.#####}°, sky unprojection {worstNew:0.#####}°");
        Assert.True(worstNew < 0.002, $"the sky's direction is off by {worstNew:0.#####}° (the sun's disc is about 1° wide)");
    }
}
