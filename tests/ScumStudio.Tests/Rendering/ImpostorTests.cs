using System.Diagnostics;
using System.Numerics;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Viewport;
using Xunit;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// Owner: "the game is very smooth, our app is not: an inland view over a dense forest towards the mountains stutters".
/// Trees past their last cooked LOD drew the whole LOD mesh up to the game's cull distance; now each tree type is pictured
/// from eight headings once and drawn there as one card. Synthetic trees: 2,000 / 600 / 150 triangles of masked leaf
/// cards around a trunk.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class ImpostorTests(ITestOutputHelper output)
{
    private const float Cutoff = 0.3333f;
    private const int LeafSize = 64;
    private static readonly float[] TreeLods = [1f, 0.3f, 0.1f];

    [GlFact]
    public void ForestBeyondLastLodDrawsCards()
    {
        // 20,000 trees on 3 x 3 km seen from its west edge, 1280 x 720 with 4x MSAA, Ultra's LOD bias and pixel cull, the
        // game's cull distance at 2.8 km. Before: every tree past 160 m draws its 150-triangle LOD; after: two triangles.
        using var gl = GlHarness.Create(64, 64);
        using var target = gl.Renderer.CreateTarget(1280, 720, samples: 4);
        var (tree, _) = Tree(gl);
        gl.Renderer.Settings = Lit() with { LodBias = 1.25f, CullPixelSize = 2f };
        var camera = new FlyCamera { Position = new Vector3(-1000f, 300f, 150_000f), Yaw = 0f, Pitch = -2f, FieldOfView = 60f };
        camera.SetClipRange(10f, 1_000_000f);
        var cardDistance = tree.Bounds.Extent.Length() / (ImpostorBaker.ScreenSizeFor(TreeLods) / 1.25f * MathF.Tan(30f * UeMath.DegreesToRadians));
        var (forest, far) = (new Scene(), new Scene());
        var random = new Random(11);
        for (var i = 0; i < 20_000; i++)
        {
            var at = new Vector3(random.NextSingle() * 300_000f, 0f, random.NextSingle() * 300_000f);
            var model = Matrix4x4.CreateScale(0.8f + (random.NextSingle() * 0.4f)) * Matrix4x4.CreateRotationY(random.NextSingle() * MathF.Tau) * Matrix4x4.CreateTranslation(at);
            forest.Add(tree, model, (uint)(i + 1)).MaxDrawDistance = 280_000f;
            if (Vector3.Distance(at, camera.Position) > cardDistance * 1.3f)
            {
                far.Add(tree, model, (uint)(i + 1)).MaxDrawDistance = 280_000f;
            }
        }

        var before = (Forest: Frame(forest), Far: Frame(far));
        Impostor(gl, tree);
        var after = (Forest: Frame(forest), Far: Frame(far));
        output.WriteLine($"card from {cardDistance / 100f:0} m; forest: {before.Forest.Stats.Instances:N0} trees drawn, {before.Forest.Stats.Triangles:N0} triangles in {before.Forest.Ms:0.0} ms before, {after.Forest.Stats.Triangles:N0} in {after.Forest.Ms:0.0} ms after (CPU + glFinish, median of 5)");
        output.WriteLine($"past the last LOD: {after.Far.Stats.Instances:N0} trees, {before.Far.Stats.Triangles:N0} triangles before, {after.Far.Stats.Triangles:N0} after");
        Assert.Equal(before.Forest.Stats.Instances, after.Forest.Stats.Instances);
        Assert.Equal(before.Far.Stats.Instances, after.Far.Stats.Instances);
        Assert.True(after.Far.Stats.Instances > 10_000, $"{after.Far.Stats.Instances} far trees drawn");
        Assert.True(after.Far.Stats.Triangles <= 4L * after.Far.Stats.Instances, $"{after.Far.Stats.Triangles:N0} triangles for {after.Far.Stats.Instances:N0} far trees");
        Assert.True(after.Forest.Stats.Triangles * 4 < before.Forest.Stats.Triangles, $"{after.Forest.Stats.Triangles:N0} triangles after, {before.Forest.Stats.Triangles:N0} before");

        (RenderStats Stats, double Ms) Frame(Scene scene)
        {
            var times = new List<double>();
            var stats = default(RenderStats);
            for (var i = 0; i < 7; i++)
            {
                gl.Context.Gl.Finish();
                var start = Stopwatch.GetTimestamp();
                stats = gl.Renderer.Render(target, scene, camera);
                gl.Context.Gl.Finish();
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            return (stats, times.Skip(2).Order().ElementAt(2));
        }
    }

    [GlFact]
    public void ImpostorMatchesTheMeshFromItsBakeAngles()
    {
        // The tree 1 km away at each baked heading of its own (a 1.5° view, so it fills the frame), drawn as LOD 0 and as its
        // card. It is turned by 40°, and its branch sticks out on one side: a card picked for the wrong heading shows the
        // branch where the mesh has none.
        using var gl = GlHarness.Create(256, 256);
        gl.UseUnlit();
        var (tree, _) = Tree(gl);
        Impostor(gl, tree);
        const float turn = 0.7f;
        var model = Matrix4x4.CreateRotationY(turn) * Matrix4x4.CreateTranslation(3000f, 0f, -2000f);
        var scene = new Scene();
        scene.Add(tree, model, 1);
        var unlit = gl.Renderer.Settings;
        for (var k = 0; k < ImpostorBaker.DefaultViews; k++)
        {
            var camera = Distant(tree, (k * 45f) - (turn / UeMath.DegreesToRadians), ImpostorBaker.Elevation, Vector3.Transform(tree.Bounds.Center, model) - tree.Bounds.Center);
            var mesh = Shot(gl, scene, camera, unlit with { LodBias = 1000f });
            Assert.Equal(TreeTriangles(0), gl.Renderer.LastStats.Triangles);
            var card = Shot(gl, scene, camera, unlit with { LodBias = 1e-6f });
            Assert.Equal(2L, gl.Renderer.LastStats.Triangles);
            var litMesh = Shot(gl, scene, camera, Lit() with { LodBias = 1000f });
            var litCard = Shot(gl, scene, camera, Lit() with { LodBias = 1e-6f });
            var difference = Vector3.Abs(mesh.Mean - card.Mean) * 255f;
            var overlap = Overlap(mesh.Mask, card.Mask);
            var control = Overlap(mesh.Mask, Shot(gl, scene, Distant(tree, (k * 45f) + 45f - (turn / UeMath.DegreesToRadians), ImpostorBaker.Elevation, Vector3.Transform(tree.Bounds.Center, model) - tree.Bounds.Center), unlit with { LodBias = 1000f }).Mask);
            output.WriteLine($"heading {k * 45,3}°: covered {mesh.Covered} px (mesh) vs {card.Covered} (card), overlap {overlap:P0} (the mesh 45° on: {control:P0}), mean colour off by {difference.X:0.0}/{difference.Y:0.0}/{difference.Z:0.0}; lit by the sun {Vector3.Abs(litMesh.Mean - litCard.Mean).Length() * 255f:0.0} ({Luma(litMesh.Mean) * 255f:0} vs {Luma(litCard.Mean) * 255f:0})");
            Assert.True(MathF.Max(difference.X, MathF.Max(difference.Y, difference.Z)) <= 8f, $"heading {k * 45}°: colour off by {difference}");
            Assert.True(Math.Abs(card.Covered - mesh.Covered) <= 0.15 * mesh.Covered, $"heading {k * 45}°: {card.Covered} px covered by the card, {mesh.Covered} by the mesh");
            Assert.True(overlap >= 0.6 && overlap > control, $"heading {k * 45}°: the card and the mesh overlap by {overlap:P0}, the mesh and itself 45° on by {control:P0}");
            Assert.True(MathF.Abs(Luma(litMesh.Mean) - Luma(litCard.Mean)) * 255f <= 10f, $"heading {k * 45}°: in sunlight the card is {Luma(litCard.Mean) * 255f:0}, the mesh {Luma(litMesh.Mean) * 255f:0}");
        }

        static float Luma(Vector3 c) => (0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z);
        static double Overlap(bool[] a, bool[] b) => a.Zip(b).Count(p => p.First && p.Second) / (double)Math.Max(a.Zip(b).Count(p => p.First || p.Second), 1);
    }

    [GlFact]
    public void CardFacesTheCameraAllAround()
    {
        using var gl = GlHarness.Create(256, 256);
        gl.UseUnlit();
        var (tree, _) = Tree(gl);
        Impostor(gl, tree);
        var scene = new Scene();
        scene.Add(tree, Matrix4x4.CreateRotationY(0.4f) * Matrix4x4.CreateTranslation(5000f, 0f, -3000f), 1);
        var settings = gl.Renderer.Settings with { LodBias = 1e-6f };
        var around = new List<int>();
        for (var heading = 0; heading < 360; heading += 10)
        {
            around.Add(Shot(gl, scene, Distant(tree, heading, ImpostorBaker.Elevation, new Vector3(5000f, 0f, -3000f)), settings).Covered);
            Assert.Equal(2L, gl.Renderer.LastStats.Triangles);
        }

        var above = new[] { 30f, 60f, 80f, 89f }.Select(e => Shot(gl, scene, Distant(tree, 20f, e, new Vector3(5000f, 0f, -3000f)), settings).Covered).ToList();
        output.WriteLine($"covered around: {around.Min()}..{around.Max()} px; from 30/60/80/89° up: {string.Join(", ", above)}");
        Assert.True(around.Min() >= 0.5 * around.Max(), $"the card thins to {around.Min()} px of {around.Max()}");
        Assert.True(above.Min() >= 0.25 * around.Max(), $"seen from above the card thins to {above.Min()} px of {around.Max()}");
    }

    [GlFact]
    public void BakeLeavesOverlayAndSettingsAlone()
    {
        // Baking happens between two frames of the Map: last frame's gizmo lines and brush ring, the grid, the sky and the
        // shadows must not end up in the pictures, and the next frame must find its settings and overlay as they were.
        using var gl = GlHarness.Create(64, 64);
        var (tree, _) = Tree(gl);
        var magenta = new Vector4(1f, 0f, 1f, 1f);
        var settings = Lit() with { ShowGrid = true, Sky = true, Shadows = true, ClearColor = magenta };
        gl.Renderer.Settings = settings;
        gl.Renderer.Time = 123.5f;
        gl.Renderer.Overlay.Add(new OverlayLine(new Vector3(-3000f, 700f, 0f), new Vector3(3000f, 700f, 0f), magenta));
        gl.Renderer.Overlay.Add(new OverlayLine(new Vector3(0f, 700f, -3000f), new Vector3(0f, 700f, 3000f), magenta));
        gl.Renderer.OverlayTriangles.Add(new OverlayTriangle(new Vector3(-2000f, 0f, 0f), new Vector3(2000f, 0f, 0f), new Vector3(0f, 2000f, 0f), magenta));
        OverlayLine[] lines = [.. gl.Renderer.Overlay];
        OverlayTriangle[] triangles = [.. gl.Renderer.OverlayTriangles];

        ImpostorBaker.Bake(gl.Renderer, tree); // warm-up: the shaders and the batcher
        var start = Stopwatch.GetTimestamp();
        var atlas = ImpostorBaker.Bake(gl.Renderer, tree);
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        var pink = 0;
        var covered = 0;
        for (var o = 0; o < atlas.Length; o += 4)
        {
            pink += atlas[o] > 160 && atlas[o + 1] < 90 && atlas[o + 2] > 160 ? 1 : 0;
            covered += atlas[o + 3] > 0 ? 1 : 0;
        }

        output.WriteLine($"baked in {ms:0.0} ms: {covered} covered texels of {atlas.Length / 4}, {pink} overlay-coloured");
        Assert.Equal(0, pink);
        Assert.InRange(covered, atlas.Length / 4 / 20, atlas.Length / 4 * 9 / 10);
        Assert.Same(settings, gl.Renderer.Settings);
        Assert.Equal(123.5f, gl.Renderer.Time);
        Assert.Equal(lines, gl.Renderer.Overlay);
        Assert.Equal(triangles, gl.Renderer.OverlayTriangles);
    }

    [GlFact]
    public void CardsPickOnlyWhereLeavesAre()
    {
        // A click on the card's empty corner (inside the quad, outside the crown) goes through to the wall behind it.
        using var gl = GlHarness.Create(256, 256);
        gl.UseUnlit();
        var (tree, _) = Tree(gl);
        Impostor(gl, tree);
        var camera = Distant(tree, 30f, ImpostorBaker.Elevation);
        var scene = new Scene();
        scene.Add(tree, Matrix4x4.Identity, 1, "tree");
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(8000f), MeshSpace.Gl), Matrix4x4.CreateTranslation(tree.Bounds.Center + (camera.Forward * 20_000f)), 2, "wall");
        gl.Renderer.Settings = gl.Renderer.Settings with { LodBias = 1e-6f };
        gl.Renderer.Render(gl.Target, scene, camera);
        Assert.Equal(2L + 12L, gl.Renderer.LastStats.Triangles); // the card and the wall

        var half = ImpostorBaker.CardHalfSize(tree.Bounds);
        var corner = FoliageAntiAliasingTests.ToPixel(camera, tree.Bounds.Center + (((camera.Up * 0.9f) - (camera.Right * 0.9f)) * half), gl.Target);
        var crown = FoliageAntiAliasingTests.ToPixel(camera, new Vector3(0f, 850f, 0f), gl.Target);
        Assert.Equal(2u, gl.Renderer.Pick(gl.Target, scene, camera, corner.X, corner.Y)?.InstanceId);
        Assert.Equal(1u, gl.Renderer.Pick(gl.Target, scene, camera, crown.X, crown.Y, radius: 2)?.InstanceId);
    }

    [GlFact]
    public void StageGivesInstancedTreesTheirImpostorAndKeepsItOnDisk()
    {
        // The Map's GPU cache: the tree foliage instances draw gets a card, the same tree as a plain mesh actor too (one mesh),
        // a rock does not; Trim frees the atlas with its mesh; the next session loads the pictures instead of baking them.
        var folder = Path.Combine(Path.GetTempPath(), "ScumStudioTests", "impostors-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var gl = GlHarness.Create(64, 64);
            var scene = TreeScene();
            var cache = new GpuMeshCache();
            cache.Impostors.Directory = folder;
            for (var i = 0; !cache.Stage(gl.Renderer, scene, 1000.0); i++)
            {
                Assert.True(i < 100_000, "staging never finished");
                Thread.Yield(); // the workers pack the meshes and find the trees
            }

            Assert.True(cache.TryGetMesh(TreePath, out var treeHandle));
            Assert.True(cache.TryGetMesh(RockPath, out var rockHandle));
            var mesh = gl.Renderer.Meshes[treeHandle.Id];
            Assert.Equal(3, mesh.ImpostorLod);
            Assert.Equal(-1, gl.Renderer.Meshes[rockHandle.Id].ImpostorLod);
            Assert.Equal((1, 1), (cache.ImpostorCount, cache.Impostors.Baked));
            Assert.Single(Directory.GetFiles(folder, "*.imp"));
            var atlas = mesh.Lods[mesh.ImpostorLod].Sections[0].Texture!;

            cache.Begin();
            cache.Begin();
            cache.Trim(gl.Renderer);
            Assert.Equal(0, cache.ImpostorCount);
            Assert.Throws<ObjectDisposedException>(() => atlas.Bind());

            var next = new GpuMeshCache();
            next.Impostors.Directory = folder;
            var again = TreeScene();
            while (!next.Stage(gl.Renderer, again, 1000.0))
            {
                Thread.Yield();
            }

            Assert.Equal((1, 0), (next.ImpostorCount, next.Impostors.Baked));
            next.Clear();
            cache.Clear();
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void CardTakesOverBelowTheLastLod()
    {
        Assert.Equal(0.04f, ImpostorBaker.ScreenSizeFor([1f, 0.3f, 0.1f]));
        Assert.Equal(0.025f, ImpostorBaker.ScreenSizeFor([1f, 0.3f, 0.05f]));
        float[] withCard = [1f, 0.3f, 0.1f, ImpostorBaker.ScreenSizeFor([1f, 0.3f, 0.1f])];
        Assert.Equal(2, LodMath.ChooseLod(withCard, 0.05f));
        Assert.Equal(3, LodMath.ChooseLod(withCard, 0.039f));

        // The card covers the bounds from any heading at the bake's elevation.
        var bounds = new BoundingBox(new Vector3(-400f, 0f, -300f), new Vector3(400f, 1200f, 300f));
        var half = ImpostorBaker.CardHalfSize(bounds);
        Assert.True(half >= 500f && half >= 600f * MathF.Cos(ImpostorBaker.Elevation * UeMath.DegreesToRadians), $"half size {half}");
    }

    private const string TreePath = "/Game/Foliage/Tree_01.Tree_01";
    private const string RockPath = "/Game/Props/Rock_01.Rock_01";

    private static long TreeTriangles(int lod) => lod switch { 0 => 2000, 1 => 600, _ => 150 };

    private static RenderSettings Lit() => new()
    {
        SkyColor = new Vector3(0.35f, 0.4f, 0.45f),
        GroundColor = new Vector3(0.15f, 0.13f, 0.1f),
        LightColor = new Vector3(0.9f, 0.85f, 0.8f),
        LightDirection = Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.3f)),
        ShowGrid = false,
        ClearColor = Vector4.Zero,
    };

    /// <summary>The tree uploaded with its leaf texture (owned by the harness's renderer for the test's life).</summary>
    private static (MeshHandle Handle, GpuTexture Leaves) Tree(GlHarness gl)
    {
        var leaves = gl.Renderer.CreateTexture(LeafSize, LeafSize, LeafTexture());
        var prepared = PreparedMesh.FromLods([TreeLod(1000, 1), TreeLod(300, 2), TreeLod(75, 3)], TreeLods, MeshSpace.Gl);
        return (gl.Renderer.AddMesh(prepared, leaves, materialAlphaCutoffs: new Dictionary<string, float> { [string.Empty] = Cutoff }), leaves);
    }

    private static void Impostor(GlHarness gl, MeshHandle tree)
    {
        var atlas = ImpostorBaker.Upload(gl.Renderer, ImpostorBaker.Bake(gl.Renderer, tree));
        gl.Renderer.Meshes[tree.Id].SetImpostor(atlas, ImpostorBaker.DefaultViews, ImpostorBaker.ScreenSizeFor(TreeLods));
    }

    /// <summary>A 1.5° camera 1 km from the tree's bounds centre (plus <paramref name="offset"/>), at a heading and elevation.</summary>
    private static FlyCamera Distant(MeshHandle tree, float heading, float elevation, Vector3 offset = default)
    {
        var camera = new FlyCamera { FieldOfView = 1.5f };
        camera.SetClipRange(50_000f, 200_000f);
        camera.Orbit(tree.Bounds.Center + offset, heading + 180f, -elevation, 100_000f);
        return camera;
    }

    /// <summary>Renders and returns the covered pixel count, the mean stored colour (0..1) of the covered pixels and which they are.</summary>
    private static (int Covered, Vector3 Mean, bool[] Mask) Shot(GlHarness gl, Scene scene, FlyCamera camera, RenderSettings settings)
    {
        gl.Renderer.Settings = settings;
        gl.Renderer.Render(gl.Target, scene, camera);
        var image = gl.Target.ReadColorRgba();
        var (sum, covered, mask) = (Vector3.Zero, 0, new bool[image.Length / 4]);
        for (var o = 0; o < image.Length; o += 4)
        {
            if (image[o + 3] > 0)
            {
                sum += new Vector3(image[o], image[o + 1], image[o + 2]) / 255f;
                covered++;
                mask[o / 4] = true;
            }
        }

        return (covered, covered == 0 ? Vector3.Zero : sum / covered, mask);
    }

    /// <summary>
    /// One LOD of the tree (GL space, Y up; <paramref name="unreal"/> = UE space, Z up): a trunk of two crossed bark cards, a
    /// branch out to +X and <paramref name="quads"/> - 3 leaf cards in a crown 8.5 m up, larger the fewer they are, so every
    /// LOD covers alike.
    /// </summary>
    internal static MeshData TreeLod(int quads, int seed, bool unreal = false)
    {
        var random = new Random(seed);
        var (positions, normals, uvs, indices) = (new List<float>(), new List<float>(), new List<float>(), new List<uint>());
        Card(new Vector3(0f, 400f, 0f), new Vector3(30f, 0f, 0f), new Vector3(0f, 400f, 0f), Vector3.UnitZ, 0.9f, 1f);
        Card(new Vector3(0f, 400f, 0f), new Vector3(0f, 0f, 30f), new Vector3(0f, 400f, 0f), Vector3.UnitX, 0.9f, 1f);
        Card(new Vector3(350f, 380f, 0f), new Vector3(350f, 0f, 0f), new Vector3(0f, 40f, 0f), Vector3.UnitZ, 0.9f, 1f);
        var size = 60f * MathF.Sqrt(1000f / quads);
        var crown = new Vector3(0f, 850f, 0f);
        for (var q = 3; q < quads; q++)
        {
            Vector3 p;
            do
            {
                p = new Vector3((random.NextSingle() * 2f) - 1f, (random.NextSingle() * 2f) - 1f, (random.NextSingle() * 2f) - 1f);
            }
            while (p.LengthSquared() > 1f);

            var at = crown + (p * new Vector3(400f, 350f, 400f));
            var axis = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) + new Vector3(1e-3f));
            var side = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var normal = Vector3.Normalize(at - crown + new Vector3(0f, 1f, 0f));
            Card(at, side * size, Vector3.Cross(side, axis) * size, normal, 0f, 0.85f);
        }

        return MeshData.Create($"Tree{quads}", [.. positions], [.. indices], [.. normals], [.. uvs]);

        void Card(Vector3 centre, Vector3 u, Vector3 v, Vector3 n, float v0, float v1)
        {
            var first = (uint)(positions.Count / 3);
            foreach (var (corner, uv) in new[] { (-u - v, new Vector2(0f, v1)), (u - v, new Vector2(1f, v1)), (u + v, new Vector2(1f, v0)), (-u + v, new Vector2(0f, v0)) })
            {
                var c = centre + corner;
                positions.AddRange(unreal ? new[] { c.X, c.Z, c.Y } : new[] { c.X, c.Y, c.Z });
                normals.AddRange(unreal ? new[] { n.X, n.Z, n.Y } : new[] { n.X, n.Y, n.Z });
                uvs.AddRange([uv.X, uv.Y]);
            }

            indices.AddRange(unreal ? new[] { first, first + 2, first + 1, first, first + 3, first + 2 } : new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
        }
    }

    /// <summary>Leaves in 4 x 4 texel clumps (half of them) in shades of green above, opaque bark in the bottom eighth.</summary>
    private static byte[] LeafTexture()
    {
        var rgba = new byte[LeafSize * LeafSize * 4];
        for (var y = 0; y < LeafSize; y++)
        {
            for (var x = 0; x < LeafSize; x++)
            {
                var o = ((y * LeafSize) + x) * 4;
                var clump = (((x >> 2) * 73_856_093) ^ ((y >> 2) * 19_349_663)) & 0xff;
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = y >= LeafSize * 7 / 8
                    ? ((byte)95, (byte)62, (byte)40, (byte)255)
                    : ((byte)(40 + (clump % 30)), (byte)(95 + (clump % 50)), (byte)(30 + (clump % 20)), (byte)(clump < 128 ? 255 : 0));
            }
        }

        return rgba;
    }

    /// <summary>The tree as a foliage instance and as a plain mesh actor, and a rock mesh actor (UE space).</summary>
    private static PreparedLevelScene TreeScene()
    {
        var tree = new PreparedMeshAsset(TreePath, TreeLod(1000, 1, unreal: true), null)
        {
            Lods = [TreeLod(1000, 1, unreal: true), TreeLod(300, 2, unreal: true), TreeLod(75, 3, unreal: true)],
            LodScreenSizes = TreeLods,
        };
        var rock = new PreparedMeshAsset(RockPath, PrimitiveMeshes.Cube(200f), null);
        var instance = new ActorInstance(1, "FoliageInstancedStaticMeshComponent_0", 0, TreePath, FTransform.Identity, FTransform.Identity) { EndCullDistance = 50_000 };
        var foliage = new ActorRecord(0, "InstancedFoliageActor_1", "/Script/Foliage.InstancedFoliageActor", null, [], FTransform.Identity, ActorKind.Other, null, []);
        ScenePlacement[] placements =
        [
            new(TreePath, UeToGl.ModelMatrix(FTransform.Identity), FTransform.Identity, 1, "Tree", 0, foliage, null, instance) { CullDistance = 50_000f },
            new(TreePath, UeToGl.ModelMatrix(FTransform.Identity), FTransform.Identity, 2, "TreeActor", 0, foliage, null, null),
            new(RockPath, UeToGl.ModelMatrix(FTransform.Identity), FTransform.Identity, 3, "Rock", 0, foliage, null, null),
        ];
        return new PreparedLevelScene([], placements, new Dictionary<string, PreparedMeshAsset>(StringComparer.OrdinalIgnoreCase) { [TreePath] = tree, [RockPath] = rock },
            new Dictionary<string, TextureImage>(), [], [], [], TimeSpan.Zero);
    }
}
