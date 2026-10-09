using System.Numerics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using Xunit;

namespace ScumStudio.Tests.Rendering;

[Collection(GlCollection.Name)]
public sealed class SkyAndShadowTests
{
    [GlFact]
    public void Sky_IsAGradientFromAHazyHorizonToABlueZenith_AndOnlyBehindTheFirstScene()
    {
        using var gl = GlHarness.Create(64, 64);
        gl.Renderer.Settings = new RenderSettings { Sky = true, Exposure = 1f, ShowGrid = false, LightDirection = new Vector3(0f, -0.5f, 1f) };
        var camera = new FlyCamera();
        camera.Orbit(Vector3.Zero, 0f, 0f, 100f); // level: the horizon runs through the middle row

        gl.Renderer.Render(gl.Target, new Scene(), camera);
        var rgba = gl.Target.ReadColorRgba();
        var top = gl.Pixel(rgba, 32, 1);
        var horizon = gl.Pixel(rgba, 32, 31);
        Assert.True(top[2] > top[0] + 60, $"top {top[0]},{top[1]},{top[2]} is not sky blue");
        Assert.True(horizon[0] > top[0] + 30, $"horizon {horizon[0]},{horizon[1]},{horizon[2]} is not paler than the top");

        // A scene drawn over the first one (clear: false) keeps what is there: no second sky over the backdrop.
        gl.Renderer.Settings = gl.Renderer.Settings with { SkyHorizonColor = Vector3.Zero, SkyZenithColor = Vector3.Zero };
        gl.Renderer.Render(gl.Target, new Scene(), camera, clear: false);
        Assert.Equal(top, gl.Pixel(gl.Target.ReadColorRgba(), 32, 1));
    }

    [GlFact]
    public void Shadows_DarkenTheGroundUnderABox_AndCanBeTurnedOff()
    {
        using var gl = GlHarness.Create(64, 64);
        var scene = new Scene();
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Plane(4000f), MeshSpace.Gl), Matrix4x4.Identity, 1, "ground");
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(200f), MeshSpace.Gl), Matrix4x4.CreateTranslation(0f, 300f, 0f), 2, "box");
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 10_000f);
        camera.Orbit(new Vector3(300f, 0f, 0f), 0f, -89f, 1500f); // straight down at the box's shadow (sun from 45° in -X)

        float Centre(bool shadows)
        {
            gl.Renderer.Settings = new RenderSettings
            {
                SkyColor = new Vector3(0.2f),
                GroundColor = new Vector3(0.2f),
                LightColor = Vector3.One,
                LightDirection = new Vector3(1f, -1f, 0f),
                ShowGrid = false,
                Shadows = shadows,
                ShadowDistance = 2000f,
            };
            gl.Renderer.Render(gl.Target, scene, camera);
            return gl.Pixel(gl.Target.ReadColorRgba(), 32, 32)[1];
        }

        var lit = Centre(shadows: false);
        var shaded = Centre(shadows: true);
        Assert.True(shaded < lit - 40, $"ground in the box's shadow {shaded} vs lit {lit}");

        // Picking ignores the shadow pass: the ground under the centre pixel is still the ground.
        Assert.Equal(1u, gl.Renderer.Pick(gl.Target, scene, camera, 32, 32)?.InstanceId);
    }

    [GlFact]
    public void NormalMap_TiltsTheSurfaceTowardsPlusU_LikeUnrealsTangentSpace()
    {
        using var gl = GlHarness.Create(64, 64);
        using var white = gl.Renderer.CreateTexture(1, 1, [255, 255, 255, 255]);
        using var tilted = gl.Renderer.CreateTexture(1, 1, [217, 128, 255, 255], srgb: false); // x = +0.7: leaning towards +U
        var plane = gl.Renderer.AddMesh(PrimitiveMeshes.Plane(400f), MeshSpace.Gl, 1f, white); // u runs along +X
        var scene = new Scene();
        scene.Add(plane, Matrix4x4.Identity, 1, "plane");
        var camera = new FlyCamera();
        camera.Orbit(Vector3.Zero, 0f, -89f, 300f);

        int Centre(Vector3 lightDirection)
        {
            gl.Renderer.Settings = new RenderSettings { SkyColor = Vector3.Zero, GroundColor = Vector3.Zero, LightColor = Vector3.One, LightDirection = lightDirection, ShowGrid = false };
            gl.Renderer.Render(gl.Target, scene, camera);
            return gl.Pixel(gl.Target.ReadColorRgba(), 32, 32)[0];
        }

        var fromPlusX = new Vector3(-1f, -1f, 0f);
        var fromMinusX = new Vector3(1f, -1f, 0f);
        Assert.InRange(Math.Abs(Centre(fromPlusX) - Centre(fromMinusX)), 0, 2); // flat: the same either way

        gl.Renderer.Meshes[plane.Id].NormalMap = tilted;
        var lit = Centre(fromPlusX);
        var away = Centre(fromMinusX);
        Assert.True(lit > away + 100, $"lit from +U {lit}, from -U {away}");
    }

    [GlFact]
    public void Water_ShowsTheGroundInTheShallows_AndIsOpaqueWhereDeep()
    {
        // Owner: "flying above the sea I see empty ground that is just blue". The water now covers what is under it by
        // its thickness (from a copy of the depth buffer), instead of a fixed 75 % tint over a bright seabed.
        using var gl = GlHarness.Create(64, 64);
        using var white = gl.Renderer.CreateTexture(1, 1, [255, 255, 255, 255]);
        var ground = gl.Renderer.AddMesh(PrimitiveMeshes.Plane(4000f), MeshSpace.Gl, 1f, white);
        var water = gl.Renderer.AddMesh(PrimitiveMeshes.Plane(4000f), MeshSpace.Gl);
        gl.Renderer.Meshes[water.Id].Water = true;
        var scene = new Scene();
        var bed = scene.Add(ground, Matrix4x4.CreateTranslation(0f, -20f, 0f), 1, "seabed");
        scene.Add(water, Matrix4x4.Identity, 0, "sea").Tint = new Vector4(0.035f, 0.12f, 0.17f, 0.75f);
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 100_000f);
        camera.Orbit(Vector3.Zero, 0f, -89f, 300f);
        gl.Renderer.Settings = new RenderSettings
        {
            SkyColor = new Vector3(0.5f),
            GroundColor = new Vector3(0.5f),
            LightColor = Vector3.One,
            LightDirection = new Vector3(0.3f, -1f, 0.2f),
            ShowGrid = false,
        };

        byte[] Centre()
        {
            gl.Renderer.Render(gl.Target, scene, camera);
            return gl.Pixel(gl.Target.ReadColorRgba(), 32, 32);
        }

        var shallow = Centre(); // 20 cm of water over white sand
        bed.LocalTransform = Matrix4x4.CreateTranslation(0f, -5000f, 0f);
        var deep = Centre(); // 50 m
        Assert.True(shallow[0] > 150 && shallow[1] > 150, $"shallow {shallow[0]},{shallow[1]},{shallow[2]}: the sand should show");
        Assert.True(deep[0] < 90 && deep[2] > deep[0] + 50, $"deep {deep[0]},{deep[1]},{deep[2]}: should be opaque blue");
    }

    /// <summary>
    /// Discord feature request: "toggle lighting on or off" and "toggle the ocean shader animation". Lighting off draws a
    /// surface in its own colour (a white ground stays white in the box's shadow: no shadow pass, no shade); a still sea
    /// looks the same at any time.
    /// </summary>
    [Fact]
    public void LightingAndTheSeasWavesCanBeTurnedOff()
    {
        using var gl = GlHarness.Create(64, 64);
        using var white = gl.Renderer.CreateTexture(1, 1, [200, 200, 200, 255]);
        var scene = new Scene();
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Plane(4000f), MeshSpace.Gl, 1f, white), Matrix4x4.Identity, 1, "ground");
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(200f), MeshSpace.Gl), Matrix4x4.CreateTranslation(0f, 300f, 0f), 2, "box");
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 10_000f);
        camera.Orbit(new Vector3(300f, 0f, 0f), 0f, -89f, 1500f); // straight down at the box's shadow (sun from 45° in -X)
        var light = new RenderSettings
        {
            SkyColor = new Vector3(0.2f),
            GroundColor = new Vector3(0.2f),
            LightColor = Vector3.One,
            LightDirection = new Vector3(1f, -1f, 0f),
            ShowGrid = false,
            Shadows = true,
            ShadowDistance = 2000f,
        };

        (byte Shadow, byte Open) Ground(RenderSettings settings)
        {
            gl.Renderer.Settings = settings;
            gl.Renderer.Render(gl.Target, scene, camera);
            var rgba = gl.Target.ReadColorRgba();
            return (gl.Pixel(rgba, 32, 32)[1], gl.Pixel(rgba, 60, 60)[1]); // in the box's shadow, and out in the open
        }

        var lit = Ground(light);
        var flat = Ground(light with { Lighting = false });
        Assert.True(lit.Shadow < lit.Open - 40, $"lit: the ground in the box's shadow {lit.Shadow} vs in the open {lit.Open}");
        Assert.InRange(flat.Shadow - flat.Open, -2, 2); // lighting off: no shadow, the ground in its own colour everywhere
        Assert.True(flat.Shadow > lit.Shadow + 40, $"lighting off {flat.Shadow} vs the lit shadow {lit.Shadow}");

        // The sea: waves move with the time, a still sea does not.
        var water = gl.Renderer.AddMesh(PrimitiveMeshes.Plane(4000f), MeshSpace.Gl);
        gl.Renderer.Meshes[water.Id].Water = true;
        var sea = new Scene();
        sea.Add(water, Matrix4x4.Identity, 0, "sea").Tint = new Vector4(0.035f, 0.12f, 0.17f, 0.75f);
        camera.Orbit(Vector3.Zero, 30f, -20f, 800f);
        byte[] Frame(bool animate, float time)
        {
            gl.Renderer.Settings = light with { Shadows = false, AnimateWater = animate };
            gl.Renderer.Time = time;
            gl.Renderer.Render(gl.Target, sea, camera);
            return gl.Target.ReadColorRgba();
        }

        Assert.False(Frame(animate: true, 0f).AsSpan().SequenceEqual(Frame(animate: true, 1.7f)), "animated waves should move");
        Assert.True(Frame(animate: false, 0f).AsSpan().SequenceEqual(Frame(animate: false, 1.7f)), "a still sea should not move");
    }
}
