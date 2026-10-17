using System.Diagnostics;
using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Resources;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using Xunit;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// What the Map's GPU cache keeps, frees and sends while levels stream: synthetic scenes shown the way the viewport does it
/// (Stage until done, Upload or Update, Trim), each printing its measurement.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class GpuMeshCacheLifetimeTests
{
    private const uint AddedId = 0x8000_0001;
    private const uint CopyId = 0x8000_0002;

    // One BC1 block: both end colours pure red (RGB565 0xF800), every texel index 0.
    private static readonly byte[] RedBlock = [0x00, 0xF8, 0x00, 0xF8, 0, 0, 0, 0];

    // Pure green (0x07E0), and the purple-blue of a normal map (128, 128, 255: 0x841F).
    private static readonly byte[] GreenBlock = [0xE0, 0x07, 0xE0, 0x07, 0, 0, 0, 0];
    private static readonly byte[] PurpleBlock = [0x1F, 0x84, 0x1F, 0x84, 0, 0, 0, 0];

    private readonly ITestOutputHelper _output;

    public GpuMeshCacheLifetimeTests(ITestOutputHelper output) => _output = output;

    [GlFact]
    public void OwnedMeshesKeepBorrowedTexturesAlive()
    {
        using var gl = GlHarness.Create(32, 32);
        gl.UseUnlit();
        var cache = new GpuMeshCache();
        var red = new Dictionary<string, TextureImage> { ["/Game/T"] = Bc1(16, Bc1Mips(16)) };
        var level = Show(gl, cache, null, Scene(red, new PreparedMeshAsset("/Game/M1", Quad("M1"), "/Game/T")));
        var camera = TopView();

        // An object added from a level that is not loaded (its mesh arrives later and draws the cached T), and a copy of a
        // loaded object drawn bent (its bent copy draws T too, its straight mesh waits for the bend to be undone).
        level.AddMesh(new ExtraMesh(new PreparedMeshAsset("/Game/M2", Quad("M2"), "/Game/T"), red));
        Assert.Equal(1, level.AddClone(AddedId, 0, FTransform.Identity, "Added", "/Game/M2"));
        Assert.Equal(1, level.AddClone(CopyId, 0, FTransform.Identity, "Copy", placements: [Placement("/Game/M1")]));
        var copy = level.Scene.Nodes.Single(n => n.SelectableId == CopyId);
        var straight = copy.Mesh!;
        level.SetBend(CopyId, [new SplineMeshParams { StartPos = new FVector(-1000, 0, 0), EndPos = new FVector(1000, 0, 0), StartTangent = new FVector(2000, 0, 0), EndTangent = new FVector(2000, 0, 0) }]);
        Assert.NotSame(straight, copy.Mesh);

        // The camera flies on: three streaming steps whose levels draw neither M1 nor T, each trimmed as the viewport does.
        level = FlyOn(gl, cache, level, "U");
        _output.WriteLine($"after 3 streaming steps without M1 and T: the copy's straight mesh still on the GPU {gl.Renderer.Meshes.ContainsKey(straight.Id)}");
        var stats = gl.Renderer.Render(gl.Target, level.Scene, camera); // threw "Cannot access a disposed object" (GpuTexture)
        Assert.True(stats.Instances >= 2, $"{stats.Instances} drawn");
        AssertRed(gl);

        // Unbent, the copy draws its straight mesh again: it stayed on the GPU while the bent copy was drawn.
        level.SetBend(CopyId, null);
        Assert.Same(straight, copy.Mesh);
        Assert.True(gl.Renderer.Meshes.ContainsKey(straight.Id));
        gl.Renderer.Render(gl.Target, level.Scene, camera);
        AssertRed(gl);
        level.Dispose();
    }

    [GlFact]
    public void TrimFreesABorrowedTextureOnceNothingDrawsIt()
    {
        using var gl = GlHarness.Create(32, 32);
        var cache = new GpuMeshCache();
        var image = Bc1(16, Bc1Mips(16));
        var level = Show(gl, cache, null, Scene(new Dictionary<string, TextureImage> { ["/Game/T"] = image }, new PreparedMeshAsset("/Game/M1", Quad("M1"), "/Game/T")));
        var texture = cache.Texture(gl.Renderer, "/Game/T", image);
        Assert.Equal(1, level.AddClone(CopyId, 0, FTransform.Identity, "Copy", placements: [Placement("/Game/M1")]));
        var straight = level.Scene.Nodes.Single(n => n.SelectableId == CopyId).Mesh!;
        level.SetBend(CopyId, [new SplineMeshParams { StartPos = new FVector(-1000, 0, 0), EndPos = new FVector(1000, 0, 0), StartTangent = new FVector(2000, 0, 0), EndTangent = new FVector(2000, 0, 0) }]);
        level = FlyOn(gl, cache, level, "U");
        var keptWhileBent = !Disposed(texture);

        // The bend undone and the copy deleted: nothing draws M1 or T any more, so the cache frees both as for any level.
        level.SetBend(CopyId, null);
        level.RemoveClone(CopyId);
        level = FlyOn(gl, cache, level, "V");
        var freed = Disposed(texture);
        var meshFreed = !gl.Renderer.Meshes.ContainsKey(straight.Id);
        _output.WriteLine($"T while the bent copy is drawn: kept {keptWhileBent}; {GpuMeshCache.KeepUploads + 1} streaming steps after the undo and delete: T freed {freed}, M1 freed {meshFreed}, cached meshes {cache.MeshCount}");

        Assert.True(keptWhileBent);
        Assert.True(freed, "the borrowed texture stayed in video memory after its last user went");
        Assert.True(meshFreed);
        Assert.Equal(GpuMeshCache.KeepUploads, cache.MeshCount); // the last steps' meshes, as an unused upload survives
        level.Dispose();
    }

    [GlFact]
    public void RaisedTextureQualitySharpensWithinTheStagingBudget()
    {
        using var gl = GlHarness.Create(32, 32);
        var (mips256, mips512, mips1024) = (Bc1Mips(256), Bc1Mips(512), Bc1Mips(1024));

        // Balanced (256) to Ultra (1024) for 200 textures at the viewport's budget while the camera moves (4 ms a frame).
        var cache = new GpuMeshCache();
        var paths = Enumerable.Range(0, 200).Select(i => $"/Game/T_{i}").ToList();
        var level = Show(gl, cache, null, Scene(paths.ToDictionary(p => p, _ => Bc1(256, mips256))));
        var copies = paths.Select(p => cache.Texture(gl.Renderer, p, Bc1(256, mips256))).ToList();
        var ultra = Scene(paths.ToDictionary(p => p, _ => Bc1(1024, mips1024)));
        var (frames, worst, total, staged) = (0, 0.0, 0.0, false);
        while (!staged && frames < 10_000)
        {
            var clock = Stopwatch.StartNew();
            staged = cache.Stage(gl.Renderer, ultra, 4.0);
            worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds);
            total += clock.Elapsed.TotalMilliseconds;
            frames++;
        }

        var sharp = copies.Count(c => c.Width == 1024);
        level.Update(ultra);
        var switchMs = level.UploadTimings!.TexturesMs;
        _output.WriteLine($"200 textures 256 -> 1024 at 4 ms a frame: {frames} staging frame(s), worst {worst:0.0} ms, total {total:0.0} ms, sharp before the switch {sharp}/200; switch frame textures {switchMs:0.00} ms");
        level.Dispose();

        // A few textures, the smallest budget: each frame sends what fits and the scene switches only once all are sharp.
        var small = new GpuMeshCache();
        var few = paths.Take(4).ToList();
        var shown = Show(gl, small, null, Scene(few.ToDictionary(p => p, _ => Bc1(256, mips256))));
        var fewCopies = few.Select(p => small.Texture(gl.Renderer, p, Bc1(256, mips256))).ToList();
        var sharper = Scene(few.ToDictionary(p => p, _ => Bc1(512, mips512)));
        var first = small.Stage(gl.Renderer, sharper, 0.001);
        var calls = 1;
        for (var done = first; !done && calls < 10_000; calls++)
        {
            done = small.Stage(gl.Renderer, sharper, 0.001);
        }

        var sharpFew = fewCopies.Count(c => c.Width == 512);
        _output.WriteLine($"4 textures 256 -> 512 at a 0.001 ms budget: first Stage {first}, {calls} call(s), sharp before the switch {sharpFew}/4");

        Assert.True(staged);
        Assert.True(frames > 1, "Stage sent nothing: the switch frame sharpened every texture");
        Assert.Equal(200, sharp);
        if (!Ci.On) Assert.True(switchMs < total / 4, $"switch {switchMs:0.00} ms, staging {total:0.0} ms");
        Assert.False(first);
        Assert.Equal(4, sharpFew);
        shown.Update(sharper);
        small.Trim(gl.Renderer);
        for (var i = 0; i < few.Count; i++)
        {
            Assert.Same(fewCopies[i], small.Texture(gl.Renderer, few[i], Bc1(256, mips256))); // never back to the blurrier one
            Assert.Equal(512, fewCopies[i].Width);
        }

        shown.Dispose();
    }

    [GlFact]
    public void AMeshStagedForASceneNeverShownKeepsItsTexturesAlive()
    {
        using var gl = GlHarness.Create(32, 32);
        gl.UseUnlit();
        var cache = new GpuMeshCache();
        var red = new Dictionary<string, TextureImage> { ["/Game/T"] = Bc1(16, Bc1Mips(16)) };
        var level = Show(gl, cache, null, Scene(red, new PreparedMeshAsset("/Game/M1", Quad("M1"), "/Game/T")));
        level = Show(gl, cache, level, Scene(new Dictionary<string, TextureImage> { ["/Game/U"] = Bc1(8, Bc1Mips(8)) }, new PreparedMeshAsset("/Game/MU", Quad("MU"), "/Game/U")));

        // The camera turns back: a scene with a new mesh M2 drawing the cached T is staged to the end, then passed over for
        // the next one (the viewport stages only the newest scene), which draws neither. M2 stays cached (staged in this
        // upload), T was last used two uploads ago.
        var passed = Scene(red, new PreparedMeshAsset("/Game/M2", Quad("M2"), "/Game/T"));
        for (var i = 0; !cache.Stage(gl.Renderer, passed, 1000.0); i++)
        {
            Assert.True(i < 100_000, "staging never finished");
            Thread.Yield();
        }

        level = Show(gl, cache, level, Scene(new Dictionary<string, TextureImage> { ["/Game/V"] = Bc1(8, Bc1Mips(8)) }, new PreparedMeshAsset("/Game/MV", Quad("MV"), "/Game/V")));

        // The camera comes back to M2: the cache hands out M2 as it was staged.
        level = Show(gl, cache, level, passed);
        Assert.Equal(1, level.AddClone(CopyId, 0, FTransform.Identity, "M2", placements: [Placement("/Game/M2")]));
        var drawn = Record.Exception(() => gl.Renderer.Render(gl.Target, level.Scene, TopView()));
        _output.WriteLine($"M2 staged for a scene passed over, shown two uploads later: {drawn?.GetType().Name ?? "drawn"} {drawn?.Message}");
        Assert.Null(drawn); // threw "Cannot access a disposed object" (GpuTexture): T was freed while the staged M2 still drew with it
        AssertRed(gl);
        level.Dispose();
    }

    [GlFact]
    public void EveryDrawShowsItsOwnMaterialsTexturesAfterEvictionAndReupload()
    {
        // D06 suspects on the GPU side: a texture unit left bound by the previous draw (a normal map, another mesh's colour),
        // a freed texture name handed to a new texture, a re-upload landing in the wrong object. Three meshes drawn in one
        // frame (mesh id order): a red texture with a purple "normal map", a green texture without one, an untextured blue
        // material; then all of them evicted, the camera elsewhere, and uploaded again with the textures in another order.
        using var gl = GlHarness.Create(64, 64);
        gl.UseUnlit();
        var cache = new GpuMeshCache();
        var textures = new Dictionary<string, TextureImage>
        {
            ["/Game/T_Red"] = Bc1(16, Bc1Mips(16)),
            ["/Game/T_Normal"] = Bc1(16, Bc1Mips(16, PurpleBlock)),
            ["/Game/T_Green"] = Bc1(16, Bc1Mips(16, GreenBlock)),
        };
        var meshes = new[]
        {
            new PreparedMeshAsset("/Game/M_Red", Quad("M_Red", "MI_Red"), null)
            {
                MaterialTextures = new Dictionary<string, string> { ["MI_Red"] = "/Game/T_Red", ["MI_Red" + GpuMesh.NormalMapSuffix] = "/Game/T_Normal" },
            },
            new PreparedMeshAsset("/Game/M_Green", Quad("M_Green", "MI_Green"), null) { MaterialTextures = new Dictionary<string, string> { ["MI_Green"] = "/Game/T_Green" } },
            new PreparedMeshAsset("/Game/M_Blue", Quad("M_Blue", "MI_Blue"), null) { MaterialTints = new Dictionary<string, Vector4> { ["MI_Blue"] = new(0f, 0f, 1f, 1f) } },
        };

        var level = Show(gl, cache, null, Scene(textures, meshes));
        var first = Drawn(level);
        var names = Names();
        for (var i = 0; i < meshes.Length; i++)
        {
            level.RemoveClone(CopyId + 1 + (uint)i);
        }

        level = FlyOn(gl, cache, level, "U"); // every mesh and texture above freed (their GL names free for reuse)
        var freed = !gl.Renderer.Meshes.Values.Any(m => m.Name.StartsWith("M_", StringComparison.Ordinal));
        level = Show(gl, cache, level, Scene(textures.Reverse().ToDictionary(t => t.Key, t => t.Value), meshes.Reverse().ToArray()));
        var again = Drawn(level);
        _output.WriteLine($"first upload: {first} (texture names {names}); all freed {freed}; uploaded again: {again} (texture names {Names()})");

        Assert.True(freed);
        Assert.Equal("red True, green True, blue True, other 0", first);
        Assert.Equal(first, again);
        level.Dispose();

        // The GL names each mesh's colour and normal map have (a name freed by the eviction may come back for another texture).
        string Names() => string.Join(", ", gl.Renderer.Meshes.Values.Where(m => m.Name.StartsWith("M_", StringComparison.Ordinal)).OrderBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => $"{m.Name} {m.Lods[0].Sections[0].Texture?.Handle}/{m.Lods[0].Sections[0].NormalMap?.Handle}"));

        string Drawn(LevelScene scene)
        {
            for (var i = 0; i < meshes.Length; i++)
            {
                Assert.Equal(1, scene.AddClone(CopyId + 1 + (uint)i, 0, new FTransform(new FVector((i - 1) * 2500f, 0f, 0f)), meshes[i].MeshPath, placements: [Placement(meshes[i].MeshPath)]));
            }

            var camera = new FlyCamera();
            camera.SetClipRange(10f, 100_000f);
            camera.Orbit(Vector3.Zero, 0f, -89f, 15000f);
            gl.Renderer.Render(gl.Target, scene.Scene, camera);
            var rgba = gl.Target.ReadColorRgba();
            var (r, g, b, other) = (0, 0, 0, 0);
            for (var p = 0; p < rgba.Length; p += 4)
            {
                var (pr, pg, pb) = (rgba[p], rgba[p + 1], rgba[p + 2]);
                if (pr > 240 && pg < 16 && pb < 16)
                {
                    r++;
                }
                else if (pg > 240 && pr < 16 && pb < 16)
                {
                    g++;
                }
                else if (pb > 240 && pr < 16 && pg < 16)
                {
                    b++;
                }
                else if (pr + pg + pb > 0)
                {
                    other++; // a normal map, a mix of two textures: anything but the three colours and the clear colour
                }
            }

            return $"red {r > 0}, green {g > 0}, blue {b > 0}, other {other}";
        }
    }

    [GlFact]
    public void ClearDeletesCachedTextures()
    {
        using var gl = GlHarness.Create(32, 32);
        var cache = new GpuMeshCache();
        var mips = Bc1Mips(256);
        var textures = Enumerable.Range(0, 8).ToDictionary(i => $"/Game/T_{i}", _ => Bc1(256, mips));
        var level = Show(gl, cache, null, TerrainScene(textures, 64));
        var terrain = gl.Renderer.Meshes[level.TerrainNodes[0].Mesh!.Id];
        var detail = terrain.Detail!;
        var cached = textures.Select(t => cache.Texture(gl.Renderer, t.Key, t.Value))
            .Append(terrain.Texture!).Append(detail.Weights).Concat(detail.Layers.OfType<GpuTexture>())
            .Select(t => (t.Handle, t.EstimatedBytes)).ToList();
        Assert.Equal(8 + 2 + 4, cached.Count);

        // The viewport's OnOpenGlDeinit (pop out, dock back): the scene, the cache, then the renderer, the context still current.
        level.Dispose();
        cache.Clear();
        gl.Renderer.Dispose();
        var leaked = cached.Where(t => gl.Context.Gl.IsTexture(t.Handle)).ToList();
        _output.WriteLine($"{cached.Count} cached textures ({cached.Sum(t => t.EstimatedBytes) / 1024} KB); left in video memory after the GL re-init: {leaked.Count} ({leaked.Sum(t => t.EstimatedBytes) / 1024} KB)");
        Assert.Empty(leaked);
    }

    [GlFact]
    public void TerrainLayerMeanComesFromThePreparedChain()
    {
        using var gl = GlHarness.Create(32, 32);
        var cache = new GpuMeshCache();
        var scene = TerrainScene(new Dictionary<string, TextureImage>(), 512);
        var layers = scene.LayerTextures!;
        var images = layers.Images;

        // One terrain component whose four 512² layers the cache has not seen: what the first streaming step with ground does.
        var clock = Stopwatch.StartNew();
        Assert.True(cache.Stage(gl.Renderer, scene, double.MaxValue));
        var stageMs = clock.Elapsed.TotalMilliseconds;
        using var level = LevelSceneUploader.Upload(gl.Renderer, scene, cache: cache);
        var detail = gl.Renderer.Meshes[level.TerrainNodes[0].Mesh!.Id].Detail!;
        _output.WriteLine($"Stage of one terrain component with four new 512² layers: {stageMs:0.0} ms");

        Assert.Equal(4, detail.Means.Count);
        var paths = images.Keys.ToList(); // the layers by coverage, as the detail takes them
        for (var k = 0; k < 4; k++)
        {
            var expected = LinearMean(images[paths[k]]);
            _output.WriteLine($"layer {k}: mean {detail.Means[k]}, per texel {expected}");
            Assert.True(Vector3.Distance(expected, detail.Means[k]) < 1e-3f, $"layer {k}: {detail.Means[k]} vs {expected}");
            Assert.Equal(layers.Find(paths[k])!.Mean, detail.Means[k]); // the worker's, not worked out again on the render thread
        }
    }

    /// <summary>Stages <paramref name="next"/> until done, then uploads or updates and trims, as the viewport's frame does.</summary>
    private static LevelScene Show(GlHarness gl, GpuMeshCache cache, LevelScene? level, PreparedLevelScene next)
    {
        for (var i = 0; !cache.Stage(gl.Renderer, next, 1000.0); i++)
        {
            Assert.True(i < 100_000, "staging never finished");
            Thread.Yield(); // the worker packs the meshes
        }

        if (level is null)
        {
            level = LevelSceneUploader.Upload(gl.Renderer, next, cache: cache);
        }
        else
        {
            level.Update(next);
        }

        cache.Trim(gl.Renderer);
        return level;
    }

    /// <summary>The camera flies on: one more streaming step than an unused upload survives, none drawing what came before.</summary>
    private static LevelScene FlyOn(GlHarness gl, GpuMeshCache cache, LevelScene level, string tag)
    {
        for (var step = 0; step <= GpuMeshCache.KeepUploads; step++)
        {
            var other = $"/Game/{tag}{step}";
            level = Show(gl, cache, level, Scene(new Dictionary<string, TextureImage> { [other] = Bc1(8, Bc1Mips(8)) }, new PreparedMeshAsset($"/Game/M{tag}{step}", Quad("MU"), other)));
        }

        return level;
    }

    private static bool Disposed(GpuTexture texture)
    {
        try
        {
            texture.Bind();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static PreparedLevelScene Scene(IReadOnlyDictionary<string, TextureImage> textures, params PreparedMeshAsset[] meshes) =>
        new([], [], meshes.ToDictionary(m => m.MeshPath, StringComparer.OrdinalIgnoreCase), textures, [], [], [], TimeSpan.Zero);

    /// <summary>
    /// <paramref name="textures"/> and one terrain component with a ground texture and four paint layers of the default
    /// catalog, each a <paramref name="layerSize"/>² sRGB noise image, the first covering the most.
    /// </summary>
    private static PreparedLevelScene TerrainScene(IReadOnlyDictionary<string, TextureImage> textures, int layerSize)
    {
        var styles = new[] { "Forest_Ground", "Gravel", "Grass_Continental", "Beach_Sand", "White_Gravel", "Seabed", "River_Weeds" }
            .Select(TerrainLayerCatalog.Default.Resolve)
            .Where(s => s.DiffuseTexture is not null && s.TilingCm > 0f)
            .DistinctBy(s => s.DiffuseTexture, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        Assert.Equal(4, styles.Count);
        const int quads = 7;
        var n = quads + 1;
        var weights = styles.Select((s, k) => new LandscapeLayerWeights(s.Name, null, true, Enumerable.Repeat((byte)(200 - (k * 40)), n * n).ToArray())).ToList();
        var images = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        for (var k = 0; k < styles.Count; k++)
        {
            var rgba = new byte[layerSize * layerSize * 4];
            new Random(k).NextBytes(rgba);
            images[styles[k].DiffuseTexture!] = new TextureImage($"T_Layer{k}", layerSize, layerSize, rgba, "PF_B8G8R8A8", 0, layerSize, layerSize, IsSrgb: true, IsNormalMap: false);
        }

        var mesh = MeshData.Create("C_0", [0, 0, 0, 700, 0, 0, 0, 700, 0, 700, 700, 0], [0u, 1u, 2u, 2u, 1u, 3u], [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1]);
        var terrain = new PreparedTerrain("C_0", "Landscape_Test", mesh)
        {
            Albedo = new TerrainAlbedo(4, Enumerable.Repeat((byte)128, 4 * 4 * 4).ToArray()),
            Layers = new LandscapeComponentLayers("C_0", (0, 0), quads, weights, null, [], weights.Count, []),
        };
        return new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), textures, [], [terrain], [], TimeSpan.Zero)
        {
            LayerTextures = new TerrainLayerTextures(images, images.Keys), // the chains: built on the preparer's worker
            LayerCatalog = TerrainLayerCatalog.Default,
        };
    }

    /// <summary>The mean linear colour of <paramref name="image"/>, texel by texel in double precision.</summary>
    private static Vector3 LinearMean(TextureImage image)
    {
        double r = 0, g = 0, b = 0;
        var count = image.Width * image.Height;
        for (var i = 0; i < count; i++)
        {
            r += Linear(image.Rgba[i * 4]);
            g += Linear(image.Rgba[(i * 4) + 1]);
            b += Linear(image.Rgba[(i * 4) + 2]);
        }

        return new Vector3((float)(r / count), (float)(g / count), (float)(b / count));

        static double Linear(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>A 20 m two-sided square at the origin (UE space, Z up).</summary>
    private static MeshData Quad(string name, string? material = null) =>
        MeshData.Create(name, [-1000, -1000, 0, 1000, -1000, 0, -1000, 1000, 0, 1000, 1000, 0], [0u, 1u, 2u, 2u, 1u, 3u, 2u, 1u, 0u, 3u, 1u, 2u],
            [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 0, 1, 0, 0, 1, 1, 1], material is null ? null : [new MeshSection(material, 0, 12)]);

    private static ScenePlacement Placement(string meshPath)
    {
        var component = new ComponentRecord(1, "Mesh", "StaticMeshComponent", true, null, FTransform.Identity, FTransform.Identity, meshPath, []);
        var actor = new ActorRecord(0, "StaticMeshActor_1", "/Script/Engine.StaticMeshActor", 1, [component], FTransform.Identity, ActorKind.StaticMeshActor, meshPath, []);
        return new ScenePlacement(meshPath, UeToGl.ModelMatrix(FTransform.Identity), FTransform.Identity, 1, "StaticMeshActor_1", 0, actor, component, null);
    }

    private static FlyCamera TopView()
    {
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 100_000f);
        camera.Orbit(Vector3.Zero, 0f, -89f, 5000f);
        return camera;
    }

    private static void AssertRed(GlHarness gl)
    {
        var pixel = gl.Pixel(gl.Target.ReadColorRgba(), 16, 16);
        Assert.True(pixel[0] > 240 && pixel[1] < 10 && pixel[2] < 10, $"{pixel[0]},{pixel[1]},{pixel[2]}");
    }

    private static List<byte[]> Bc1Mips(int size, byte[]? block = null)
    {
        var mips = new List<byte[]>();
        for (var s = size; s >= 1; s /= 2)
        {
            var blocks = Math.Max(1, s / 4) * Math.Max(1, s / 4);
            mips.Add(Enumerable.Range(0, blocks).SelectMany(_ => block ?? RedBlock).ToArray());
        }

        return mips;
    }

    private static TextureImage Bc1(int size, List<byte[]> mips) =>
        new("T_Test", size, size, [], "PF_DXT1", 0, size, size, IsSrgb: false, IsNormalMap: false) { CompressedMips = mips };
}
