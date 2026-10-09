using System.Numerics;
using Silk.NET.OpenGL;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Gl;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Rendering.Targets;

namespace ScumStudio.Rendering;

/// <summary>
/// Draws a <see cref="Scene"/> with instancing into a <see cref="RenderTarget"/>, renders the ID buffer for picking and
/// owns the uploaded meshes. Independent of any UI toolkit: hosts pass an <see cref="IGlContext"/>.
/// </summary>
/// <remarks>
/// Every method must be called on the thread where <see cref="Context"/> is current. The renderer restores the GL state
/// it changes that commonly matters to hosts (clip control, blending, scissor, framebuffer binding).
/// </remarks>
public sealed class SceneRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<int, GpuMesh> _meshes = [];
    private readonly ShaderProgram _meshProgram;
    private readonly ShaderProgram _pickProgram;
    private readonly ShaderProgram _gridProgram;
    private readonly ShaderProgram _skyProgram;
    private readonly ShaderProgram _shadowProgram;
    private readonly ShaderProgram _lineProgram;
    private readonly GpuTexture _white;
    private readonly uint _emptyVao;
    private readonly ShaderProgram _strokeProgram;
    private readonly uint _lineVao;
    private readonly uint _lineVbo;
    private readonly uint _indirectBuffer;
    // One batcher per scene: a frame that draws the island backdrop and then the detailed scene must not throw the
    // other scene's batches away (that rebuilt and re-uploaded ~100k instances twice a frame: 84 ms instead of 2).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene, BatchState> _batchers = new();
    private readonly List<DrawRange> _draws = [];
    private readonly List<DrawRange> _shadowDraws = [];
    private DrawElementsIndirectCommand[] _commands = new DrawElementsIndirectCommand[1024];
    private int[] _lodOfCluster = new int[256];
    private int[] _shadowLodOfCluster = new int[256];
    private uint _shadowFbo;
    private uint _shadowMap;
    private uint _copyFbo;
    private uint _copyColor;
    private uint _copyDepth;
    private (int W, int H) _copySize;
    private int _nextMeshId = 1;
    private bool _disposed;

    /// <summary>Compiles the built-in programs on <paramref name="context"/> (made current first).</summary>
    public SceneRenderer(IGlContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        context.MakeCurrent();
        _gl = context.Gl;
        Info = GlInfo.Query(_gl);
        _meshProgram = new ShaderProgram(_gl, "mesh", ShaderSources.MeshVertex, ShaderSources.MeshFragment);
        _pickProgram = new ShaderProgram(_gl, "pick", ShaderSources.MeshVertex, ShaderSources.PickFragment);
        _gridProgram = new ShaderProgram(_gl, "grid", ShaderSources.GridVertex, ShaderSources.GridFragment);
        _skyProgram = new ShaderProgram(_gl, "sky", ShaderSources.GridVertex, ShaderSources.SkyFragment);
        _shadowProgram = new ShaderProgram(_gl, "shadow", ShaderSources.MeshVertex, ShaderSources.ShadowFragment);
        _lineProgram = new ShaderProgram(_gl, "line", ShaderSources.LineVertex, ShaderSources.LineFragment);
        _strokeProgram = new ShaderProgram(_gl, "stroke", ShaderSources.StrokeVertex, ShaderSources.StrokeFragment);
        _white = GpuTexture.Solid(_gl, 255, 255, 255);
        _emptyVao = _gl.GenVertexArray();
        _lineVao = _gl.GenVertexArray();
        _lineVbo = _gl.GenBuffer();
        _indirectBuffer = _gl.GenBuffer();
        GlErrors.Drain(_gl);
    }

    /// <summary>The context the renderer draws with.</summary>
    public IGlContext Context { get; }

    /// <summary>Driver information.</summary>
    public GlInfo Info { get; }

    /// <summary>Appearance settings used by the next frame.</summary>
    public RenderSettings Settings { get; set; } = new();

    /// <summary>
    /// Seconds the next frame is drawn at: drives the opacity pulse of shimmering meshes (<see cref="GpuMesh.Shimmer"/>,
    /// a 1.5 s period). The caller advances it (the viewport from its clock); it stays 0 where frames must not change.
    /// </summary>
    public float Time { get; set; }

    /// <summary>
    /// Line segments drawn on top of the scene by the next <see cref="Render"/> (no depth test): gizmos, helpers. Replace
    /// or clear the list from the render thread.
    /// </summary>
    public List<OverlayLine> Overlay { get; } = [];

    /// <summary>Filled triangles drawn on top of the scene before <see cref="Overlay"/> (no depth test): gizmo heads and cubes.</summary>
    public List<OverlayTriangle> OverlayTriangles { get; } = [];

    /// <summary>Uploaded meshes by id.</summary>
    public IReadOnlyDictionary<int, GpuMesh> Meshes => _meshes;

    /// <summary>Statistics of the last <see cref="Render"/> call.</summary>
    public RenderStats LastStats { get; private set; } = new(0, 0, 0, 0);

    /// <summary>True when the current settings and driver use reverse-Z depth.</summary>
    public bool UsesReverseZ => Settings.ReverseZ && Info.SupportsClipControl;

    /// <summary>
    /// Uploads <paramref name="mesh"/> and returns a handle for scene nodes.
    /// </summary>
    /// <param name="mesh">Renderer-neutral mesh.</param>
    /// <param name="space">Coordinate system of the vertices (<see cref="MeshSpace.Unreal"/> for extracted assets).</param>
    /// <param name="unitScale">Scale applied to positions (1 keeps centimetres).</param>
    /// <param name="texture">Optional albedo texture (not owned by the renderer).</param>
    public MeshHandle AddMesh(MeshData mesh, MeshSpace space = MeshSpace.Unreal, float unitScale = 1f, GpuTexture? texture = null) =>
        AddMesh(PreparedMesh.From(mesh, space, unitScale), texture);

    /// <summary>
    /// Uploads prepared vertex data (every LOD) and returns a handle for scene nodes. Each material section draws with
    /// the texture of its <see cref="PreparedSection.Material"/> in <paramref name="materialTextures"/>, else with
    /// <paramref name="texture"/> (textures are not owned by the renderer). <paramref name="shimmer"/>: a stand-in whose
    /// translucent sections pulse with <see cref="Time"/> (<see cref="GpuMesh.Shimmer"/>); <paramref name="billboard"/>: a
    /// camera-facing card (<see cref="GpuMesh.Billboard"/>).
    /// </summary>
    public MeshHandle AddMesh(PreparedMesh mesh, GpuTexture? texture = null, IReadOnlyDictionary<string, GpuTexture>? materialTextures = null,
        IReadOnlyDictionary<string, float>? materialAlphaCutoffs = null, IReadOnlyDictionary<string, Vector4>? materialTints = null,
        bool shimmer = false, bool billboard = false, IReadOnlyDictionary<string, Vector2>? materialRoughness = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = _nextMeshId++;
        var gpu = new GpuMesh(_gl, id, mesh, texture, materialTextures, materialAlphaCutoffs, materialTints, shimmer, billboard, materialRoughness);
        _meshes[id] = gpu;
        GlErrors.Check(_gl, $"uploading mesh '{mesh.Name}'");
        return new MeshHandle(id, mesh.Name, mesh.Bounds);
    }

    /// <summary>Uploads an RGBA8 texture (see <see cref="GpuTexture.FromRgba8"/>); the caller owns it.</summary>
    public GpuTexture CreateTexture(int width, int height, ReadOnlySpan<byte> rgba, bool srgb = true) =>
        GpuTexture.FromRgba8(_gl, width, height, rgba, srgb);

    /// <summary>
    /// Uploads block-compressed mips as they are (see <see cref="GpuTexture.FromCompressed"/>); null when the driver cannot
    /// take <paramref name="format"/> (decode to RGBA8 and use <see cref="CreateTexture(int, int, ReadOnlySpan{byte}, bool)"/> instead). The caller owns it.
    /// </summary>
    public GpuTexture? CreateCompressedTexture(CompressedFormat format, int width, int height, IReadOnlyList<byte[]> mips, bool srgb) =>
        format is CompressedFormat.Bc1 or CompressedFormat.Bc2 or CompressedFormat.Bc3 && !Info.SupportsS3tc
            ? null
            : GpuTexture.FromCompressed(_gl, format, width, height, mips, srgb);

    /// <summary>Uploads an RGBA8 texture with an explicit wrap mode (e.g. <see cref="TextureWrap.ClampToEdge"/> for terrain tiles); the caller owns it.</summary>
    public GpuTexture CreateTexture(int width, int height, ReadOnlySpan<byte> rgba, bool srgb, TextureWrap wrap) =>
        GpuTexture.FromRgba8(_gl, width, height, rgba, srgb, mipmaps: true, wrap);

    /// <summary>Replaces the albedo texture of an uploaded mesh (the texture stays owned by the caller). Returns false for unknown meshes.</summary>
    public bool SetMeshTexture(MeshHandle handle, GpuTexture? texture)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!_meshes.TryGetValue(handle.Id, out var mesh))
        {
            return false;
        }

        mesh.Texture = texture;
        return true;
    }

    /// <summary>Gives a terrain mesh its close-up layer textures (null removes them); the textures stay owned by the caller.</summary>
    public bool SetMeshTerrainDetail(MeshHandle handle, TerrainDetailTextures? detail)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!_meshes.TryGetValue(handle.Id, out var mesh))
        {
            return false;
        }

        mesh.Detail = detail;
        return true;
    }

    private void BindTerrainDetail(TerrainDetailTextures? detail)
    {
        if (detail is null)
        {
            _meshProgram.Set("uTerrainDetail", 0);
            return;
        }

        detail.Weights.Bind(1);
        for (var i = 0; i < 4; i++)
        {
            (i < detail.Layers.Count && detail.Layers[i] is { } layer ? layer : _white).Bind(2 + i);
            _meshProgram.Set("uLayerMean" + i, i < detail.Means.Count ? detail.Means[i] : Vector3.Zero);
        }

        _meshProgram.Set("uLayerTiling", detail.InvTilingCm);
        _meshProgram.Set("uTerrainDetail", 1);
    }

    /// <summary>Deletes an uploaded mesh. Nodes still referencing it are skipped when drawing.</summary>
    public bool RemoveMesh(MeshHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!_meshes.Remove(handle.Id, out var mesh))
        {
            return false;
        }

        mesh.Dispose();
        return true;
    }

    /// <summary>Creates a render target sized to the context (or to an explicit size).</summary>
    public RenderTarget CreateTarget(int? width = null, int? height = null)
    {
        var (w, h) = Context.Size;
        return new RenderTarget(_gl, width ?? w, height ?? h);
    }

    /// <summary>Renders <paramref name="scene"/> seen by <paramref name="camera"/> into the colour framebuffer of <paramref name="target"/>.</summary>
    /// <param name="target">The render target.</param>
    /// <param name="scene">The scene to draw.</param>
    /// <param name="camera">The camera.</param>
    /// <param name="clear">False draws over what the target already holds (a backdrop scene rendered just before).</param>
    public RenderStats Render(RenderTarget target, Scene scene, FlyCamera camera, bool clear = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);

        var aspect = target.AspectRatio;
        var s = EffectiveSettings(scene);
        ShadowFrame? shadow = s.Shadows ? ShadowView(s, camera) : null;
        PrepareFrame(scene, camera, aspect, target.Height, out var culled, out var instances, shadow?.Bounds);
        var reverseZ = UsesReverseZ;
        var viewProj = camera.ViewMatrix * (reverseZ ? camera.GetReverseZProjection(aspect) : camera.GetProjection(aspect));
        if (shadow is { } sun)
        {
            RenderShadowMap(sun.ViewProj);
        }

        target.BindColor();
        BeginDepthState(reverseZ);
        if (clear)
        {
            _gl.ClearColor(EncodeClear(s.ClearColor.X), EncodeClear(s.ClearColor.Y), EncodeClear(s.ClearColor.Z), s.ClearColor.W);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        }

        _meshProgram.Use();
        _meshProgram.Set("uViewProj", viewProj);
        _meshProgram.Set("uTexture", 0);
        _meshProgram.Set("uSkyColor", s.SkyColor);
        _meshProgram.Set("uGroundColor", s.GroundColor);
        _meshProgram.Set("uHighlight", s.HighlightColor);
        _meshProgram.Set("uCameraPosition", camera.Position);
        _meshProgram.Set("uCameraRight", camera.Right);
        _meshProgram.Set("uCameraUp", camera.Up);
        _meshProgram.Set("uBillboard", 0);
        _meshProgram.Set("uShimmer", 0);
        _meshProgram.Set("uTime", Time);
        _meshProgram.Set("uFogColor", s.FogColor);
        _meshProgram.Set("uFogDensity", MathF.Max(0f, s.FogDensity));
        SetAtmosphere(_meshProgram, s);
        _meshProgram.Set("uWater", 0);
        _meshProgram.Set("uSceneCopy", 0);
        _meshProgram.Set("uSceneColor", CopyColorUnit);
        _meshProgram.Set("uSceneDepth", CopyDepthUnit);
        _meshProgram.Set("uNormalMap", NormalUnit);
        _meshProgram.Set("uHasNormalMap", 0);
        _meshProgram.Set("uRoughness", Vector2.Zero);
        _meshProgram.Set("uShadowMap", ShadowUnit);
        _meshProgram.Set("uShadows", shadow is null ? 0 : 1);
        if (shadow is { } light)
        {
            _meshProgram.Set("uShadowMatrix", light.ViewProj);
            _meshProgram.Set("uShadowTexel", light.Texel);
            _gl.ActiveTexture(TextureUnit.Texture0 + ShadowUnit);
            _gl.BindTexture(TextureTarget.Texture2D, _shadowMap);
            _gl.BindSampler(ShadowUnit, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }
        _meshProgram.Set("uOpaque", 1);
        _meshProgram.Set("uTerrainDetail", 0);
        _meshProgram.Set("uWeights", 1);
        _meshProgram.Set("uLayer0", 2);
        _meshProgram.Set("uLayer1", 3);
        _meshProgram.Set("uLayer2", 4);
        _meshProgram.Set("uLayer3", 5);
        TerrainDetailTextures? boundDetail = null;

        var drawCalls = 0;
        long triangles = 0;
        GpuTexture? bound = null;
        var hasTexture = -1;
        var cutoff = -1f;
        var sectionTint = new Vector4(-1f);
        var shimmer = false;
        var billboard = false;
        var water = false;
        GpuTexture? boundNormal = null;
        var roughness = Vector2.Zero;
        List<DrawRange>? translucent = null;
        foreach (var draw in _draws)
        {
            if (draw.Translucent)
            {
                (translucent ??= []).Add(draw);
                continue;
            }

            DrawRangeNow(draw);
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        if (clear && s.Sky)
        {
            // Only behind the first scene of a frame: a scene drawn over it (clear: false) must not paint sky over the
            // first scene's sea, which writes no depth.
            DrawSky(s, camera, aspect, reverseZ);
        }

        if (s.ShowGrid)
        {
            DrawGrid(s, camera, aspect, viewProj, reverseZ);
        }

        if (translucent is not null)
        {
            // Translucent nodes (tint alpha < 1, e.g. a sea plane) after everything opaque: blended, no depth writes.
            // Water first, over a copy of what is drawn so far (its colour and depth): the water shades the ground it
            // covers by its thickness and covers it, and glass drawn after it still blends over it.
            translucent = [.. translucent.Where(d => d.Mesh.Water), .. translucent.Where(d => !d.Mesh.Water)];
            _meshProgram.Use();
            _meshProgram.Set("uOpaque", 0);
            if (translucent[0].Mesh.Water)
            {
                CopyScene(target);
                Matrix4x4.Invert(reverseZ ? camera.GetReverseZProjection(aspect) : camera.GetProjection(aspect), out var invProj);
                _meshProgram.Set("uInvProj", invProj);
                _meshProgram.Set("uDepthZeroToOne", reverseZ ? 1 : 0);
                _meshProgram.Set("uSceneCopy", 1);
            }

            _gl.Enable(EnableCap.Blend);
            _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
            _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _indirectBuffer);
            foreach (var draw in translucent)
            {
                DrawRangeNow(draw);
            }

            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);

            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        if (Overlay.Count > 0 || OverlayTriangles.Count > 0)
        {
            DrawOverlay(viewProj, target.Width, target.Height);
        }

        GpuTexture.UnbindSampler(_gl);
        GpuTexture.UnbindSampler(_gl, NormalUnit);
        for (var unit = 1; unit <= 5; unit++)
        {
            GpuTexture.UnbindSampler(_gl, unit);
        }

        foreach (var unit in (int[])[ShadowUnit, CopyColorUnit, CopyDepthUnit])
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        _gl.ActiveTexture(TextureUnit.Texture0);
        EndDepthState(reverseZ);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GlErrors.Check(_gl, "rendering the scene");
        LastStats = new RenderStats(drawCalls, instances, culled, triangles);
        return LastStats;

        void DrawRangeNow(DrawRange draw)
        {
            var texture = draw.Texture ?? _white;
            if (!ReferenceEquals(texture, bound))
            {
                texture.Bind(0);
                bound = texture;
            }

            var has = draw.Texture is null ? 0 : 1;
            if (has != hasTexture)
            {
                _meshProgram.Set("uHasTexture", has);
                hasTexture = has;
            }

            var sectionCutoff = draw.Texture is null ? 0f : draw.AlphaCutoff;
            if (sectionCutoff != cutoff)
            {
                // Masked sections (leaves, grass) cut holes by the texture's alpha and are seen from both sides.
                _meshProgram.Set("uAlphaCutoff", sectionCutoff);
                if (sectionCutoff > 0f)
                {
                    _gl.Disable(EnableCap.CullFace);
                }
                else if (s.BackfaceCulling)
                {
                    _gl.Enable(EnableCap.CullFace);
                }

                cutoff = sectionCutoff;
            }

            if (draw.Tint != sectionTint)
            {
                _meshProgram.Set("uSectionTint", draw.Tint);
                sectionTint = draw.Tint;
            }

            if (draw.Mesh.Shimmer != shimmer)
            {
                _meshProgram.Set("uShimmer", draw.Mesh.Shimmer ? 1 : 0);
                shimmer = draw.Mesh.Shimmer;
            }

            if (draw.Mesh.Billboard != billboard)
            {
                _meshProgram.Set("uBillboard", draw.Mesh.Billboard ? 1 : 0);
                billboard = draw.Mesh.Billboard;
            }

            if (draw.Mesh.Water != water)
            {
                _meshProgram.Set("uWater", draw.Mesh.Water ? 1 : 0);
                water = draw.Mesh.Water;
            }

            if (!ReferenceEquals(draw.NormalMap, boundNormal))
            {
                draw.NormalMap?.Bind(NormalUnit);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _meshProgram.Set("uHasNormalMap", draw.NormalMap is null ? 0 : 1);
                boundNormal = draw.NormalMap;
            }

            if (draw.Roughness != roughness)
            {
                _meshProgram.Set("uRoughness", draw.Roughness);
                roughness = draw.Roughness;
            }

            if (!ReferenceEquals(draw.Mesh.Detail, boundDetail))
            {
                BindTerrainDetail(draw.Mesh.Detail);
                boundDetail = draw.Mesh.Detail;
                texture.Bind(0); // the active unit moved: put the albedo back on unit 0
                bound = texture;
            }

            draw.Mesh.DrawIndirect(draw.FirstCommand, draw.CommandCount);
            drawCalls++;
            triangles += (long)draw.Instances * (draw.IndexCount / 3);
        }
    }

    /// <summary>
    /// The settings a frame of <paramref name="scene"/> is drawn with: <see cref="Settings"/> with the scene's
    /// <see cref="Scene.Environment"/> applied (unless <see cref="RenderSettings.UseSceneEnvironment"/> is off).
    /// </summary>
    public RenderSettings EffectiveSettings(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Settings.UseSceneEnvironment && scene.Environment is { } environment ? environment.ApplyTo(Settings) : Settings;
    }

    /// <summary>True when every instance of the batch has a tint alpha below 1 (drawn blended, after opaque geometry).</summary>
    private static bool IsTranslucent(RenderBatch batch)
    {
        foreach (var instance in batch.Instances)
        {
            if (instance.Tint.W >= 0.999f)
            {
                return false;
            }
        }

        return batch.Instances.Length > 0;
    }

    /// <summary>
    /// Renders the ID buffer for the pixel (<paramref name="x"/>, <paramref name="y"/>) (top-left origin, target pixels)
    /// and returns what is visible there, or null for background / non-pickable nodes.
    /// </summary>
    /// <remarks>
    /// Only the requested pixels are rasterised (scissor), so a pick costs one cheap geometry pass. With a
    /// <paramref name="radius"/> the square of pixels around the point counts and the nearest thing in it wins: a click
    /// between a fence's bars takes the fence, not the bridge seen through it.
    /// </remarks>
    public PickResult? Pick(RenderTarget target, Scene scene, FlyCamera camera, int x, int y, int radius = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        if ((uint)x >= (uint)target.Width || (uint)y >= (uint)target.Height)
        {
            return null;
        }

        var aspect = target.AspectRatio;
        var x0 = Math.Max(0, x - radius);
        var y0 = Math.Max(0, y - radius);
        var width = Math.Min(target.Width, x + radius + 1) - x0;
        var height = Math.Min(target.Height, y + radius + 1) - y0;
        var batches = RenderPickPass(target, scene, camera, aspect, x0, y0, width, height);
        var (codes, depths) = target.ReadPickRegion(x0, y0, width, height);
        GlErrors.Check(_gl, "picking");
        PickResult? best = null;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < codes.Length; i++)
        {
            if (codes[i] == 0 || !batches.TryResolvePickCode(codes[i], out var batch, out var node) || node is null || batch is null)
            {
                continue;
            }

            var (px, py) = (x0 + (i % width), y0 + (i / width));
            var world = camera.Unproject(px, py, depths[i], target.Width, target.Height, UsesReverseZ);
            var distance = Vector3.Distance(world, camera.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = new PickResult(batch.Mesh.Id, node.SelectableId, node, depths[i], world);
            }
        }

        return best;
    }

    /// <summary>
    /// Renders the whole ID buffer (for debugging or rectangle selection) and returns the batches whose pick codes
    /// it contains; read pixels with <see cref="RenderTarget.ReadPick"/> and resolve them with
    /// <see cref="BatchResult.TryResolvePickCode"/>.
    /// </summary>
    public BatchResult RenderPickBuffer(RenderTarget target, Scene scene, FlyCamera camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        var result = RenderPickPass(target, scene, camera, target.AspectRatio, null, null, 0, 0);
        GlErrors.Check(_gl, "rendering the ID buffer");
        return result;
    }

    /// <summary>Deletes all meshes and programs (textures passed to <see cref="AddMesh(PreparedMesh, GpuTexture?, IReadOnlyDictionary{string, GpuTexture}?, IReadOnlyDictionary{string, float}?, IReadOnlyDictionary{string, System.Numerics.Vector4}?, bool, bool)"/> are not owned).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var mesh in _meshes.Values)
        {
            mesh.Dispose();
        }

        _meshes.Clear();
        _meshProgram.Dispose();
        _pickProgram.Dispose();
        _gridProgram.Dispose();
        _skyProgram.Dispose();
        _shadowProgram.Dispose();
        if (_shadowFbo != 0)
        {
            _gl.DeleteFramebuffer(_shadowFbo);
            _gl.DeleteTexture(_shadowMap);
        }

        if (_copyFbo != 0)
        {
            _gl.DeleteFramebuffer(_copyFbo);
            _gl.DeleteTexture(_copyColor);
            _gl.DeleteTexture(_copyDepth);
        }
        _lineProgram.Dispose();
        _strokeProgram.Dispose();
        _gl.DeleteVertexArray(_lineVao);
        _gl.DeleteBuffer(_lineVbo);
        _gl.DeleteBuffer(_indirectBuffer);
        _white.Dispose();
        _gl.DeleteVertexArray(_emptyVao);
    }

    private BatchResult RenderPickPass(RenderTarget target, Scene scene, FlyCamera camera, float aspect, int? x, int? y, int width, int height)
    {
        var batches = PrepareFrame(scene, camera, aspect, target.Height, out _, out _);
        var reverseZ = UsesReverseZ;
        var viewProj = camera.ViewMatrix * (reverseZ ? camera.GetReverseZProjection(aspect) : camera.GetProjection(aspect));

        target.BindPick();
        if (x is { } px && y is { } py)
        {
            _gl.Enable(EnableCap.ScissorTest);
            _gl.Scissor(px, target.Height - py - height, (uint)width, (uint)height);
        }

        BeginDepthState(reverseZ);
        _gl.Disable(EnableCap.Blend);
        ReadOnlySpan<uint> zero = [0u, 0u, 0u, 0u];
        _gl.ClearBuffer(BufferKind.Color, 0, zero);
        _gl.Clear(ClearBufferMask.DepthBufferBit);

        _pickProgram.Use();
        _pickProgram.Set("uViewProj", viewProj);
        _pickProgram.Set("uCameraRight", camera.Right);
        _pickProgram.Set("uCameraUp", camera.Up);
        _pickProgram.Set("uBillboard", 0);
        var billboard = false;
        foreach (var draw in _draws)
        {
            // Translucent nodes pick only when they are objects (placed water, glass); the sea lets clicks through.
            if (!draw.Translucent || draw.Pickable)
            {
                if (draw.Mesh.Billboard != billboard)
                {
                    _pickProgram.Set("uBillboard", draw.Mesh.Billboard ? 1 : 0);
                    billboard = draw.Mesh.Billboard;
                }

                draw.Mesh.DrawIndirect(draw.FirstCommand, draw.CommandCount);
            }
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        EndDepthState(reverseZ);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return batches;
    }

    /// <summary>
    /// The per-frame CPU work: fetches the batches (rebuilt by <see cref="SceneBatcher"/> only when the scene changed,
    /// in which case the instance records are uploaded to the meshes once; records patched in place are re-uploaded
    /// one by one), classifies every cluster (<see cref="LodMath.Classify"/>: frustum, cull distance, projected size,
    /// LOD) and writes one indirect draw command per visible cluster and material section of its LOD into
    /// <see cref="_indirectBuffer"/> (left bound), grouped into one <see cref="DrawRange"/> per mesh, LOD and section.
    /// </summary>
    private BatchResult PrepareFrame(Scene scene, FlyCamera camera, float aspect, int viewportHeight, out int culled, out int drawn, BoundingBox? shadowBox = null)
    {
        var state = _batchers.GetValue(scene, _ => new BatchState());
        var batcher = state.Batcher;
        var partials = batcher.PartialBuilds;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var batches = batcher.Get(scene);
        if (state.UploadedBuild != batcher.Builds)
        {
            var built = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var (sent, sentInstances) = (0, 0);

            // A partial rebuild keeps the batches of the meshes it did not touch: their records are on the GPU already.
            var uploaded = new HashSet<RenderBatch>(batches.Batches.Count, ReferenceEqualityComparer.Instance);
            foreach (var batch in batches.Batches)
            {
                uploaded.Add(batch);
                if (!state.Uploaded.Contains(batch) && _meshes.TryGetValue(batch.Mesh.Id, out var mesh))
                {
                    mesh.SetInstances(batch.Instances);
                    sent++;
                    sentInstances += batch.Instances.Length;
                }
            }

            state.Uploaded = uploaded;
            state.UploadedBuild = batcher.Builds;
            batcher.ClearUpdates();
            BatchUploads++;
            LastRebuild = new BatchRebuild(batcher.PartialBuilds != partials, sent, sentInstances, built, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds - built);
        }
        else if (batcher.PendingUpdates.Count > 0)
        {
            foreach (var (b, i) in batcher.PendingUpdates)
            {
                var batch = batches.Batches[b];
                if (_meshes.TryGetValue(batch.Mesh.Id, out var mesh))
                {
                    mesh.UpdateInstance(i, batch.Instances[i]);
                }
            }

            batcher.ClearUpdates();
        }

        var s = Settings;
        Frustum? frustum = s.FrustumCulling ? camera.GetFrustum(aspect) : null;
        var tanHalfFov = MathF.Tan(camera.FieldOfView * 0.5f * UeMath.DegreesToRadians);
        var distanceScale = MathF.Max(s.ViewDistanceScale, 0.01f);
        var minScreenSize = s.CullPixelSize > 0f && viewportHeight > 0 ? s.CullPixelSize / viewportHeight / distanceScale : 0f;
        var eye = camera.Position;
        _draws.Clear();
        _shadowDraws.Clear();
        var count = 0;
        culled = 0;
        drawn = 0;
        foreach (var batch in batches.Batches)
        {
            if (!_meshes.TryGetValue(batch.Mesh.Id, out var mesh) || mesh.IndexCount == 0)
            {
                continue;
            }

            var inView = frustum is not { } f || f.Intersects(batch.Bounds);
            var caster = shadowBox is { } box && !mesh.Billboard && !mesh.Water && Overlaps(box, batch.Bounds);
            if (!inView)
            {
                culled += batch.Instances.Length;
                if (!caster)
                {
                    continue;
                }
            }

            var clusters = batch.Clusters;
            if (_lodOfCluster.Length < clusters.Length)
            {
                Array.Resize(ref _lodOfCluster, Math.Max(clusters.Length, _lodOfCluster.Length * 2));
                Array.Resize(ref _shadowLodOfCluster, _lodOfCluster.Length);
            }

            var usedLods = 0;
            var shadowLods = 0;
            for (var c = 0; c < clusters.Length; c++)
            {
                var lod = -1;
                if (inView)
                {
                    lod = LodMath.Classify(clusters[c], frustum, eye, tanHalfFov, minScreenSize, distanceScale, mesh.LodScreenSizes, s.ObjectDrawDistance, s.LodBias);
                    if (lod < 0)
                    {
                        culled += clusters[c].Count;
                    }
                    else
                    {
                        drawn += clusters[c].Count;
                        usedLods |= 1 << lod;
                    }
                }

                _lodOfCluster[c] = lod;

                // Sun shadows: the clusters in the shadow box, also those just outside the view (a tree behind the camera
                // still shades the ground in front of it), at the LOD and draw distance they would have on screen.
                var shadowLod = -1;
                if (caster && Overlaps(shadowBox!.Value, clusters[c].Bounds))
                {
                    shadowLod = lod >= 0 ? lod : LodMath.Classify(clusters[c], null, eye, tanHalfFov, minScreenSize, distanceScale, mesh.LodScreenSizes, s.ObjectDrawDistance, s.LodBias);
                    shadowLods |= shadowLod >= 0 ? 1 << shadowLod : 0;
                }

                _shadowLodOfCluster[c] = shadowLod;
            }

            if (usedLods == 0 && shadowLods == 0)
            {
                continue;
            }

            var translucent = IsTranslucent(batch);
            var pickable = Array.Exists(batch.Instances, i => i.PickCode != 0);
            Emit(_draws, _lodOfCluster, usedLods, castersOnly: false);
            if (!translucent)
            {
                Emit(_shadowDraws, _shadowLodOfCluster, shadowLods, castersOnly: true);
            }

            void Emit(List<DrawRange> into, int[] lodOfCluster, int lods, bool castersOnly)
            {
                for (var lod = 0; lod < mesh.Lods.Length; lod++)
                {
                    if ((lods & (1 << lod)) == 0)
                    {
                        continue;
                    }

                    foreach (var section in mesh.Lods[lod].Sections)
                    {
                        if (castersOnly && section.Tint.W < 0.999f)
                        {
                            continue; // glass and water do not cast
                        }

                        var first = count;
                        var visible = 0;
                        for (var c = 0; c < clusters.Length; c++)
                        {
                            if (lodOfCluster[c] != lod)
                            {
                                continue;
                            }

                            if (count == _commands.Length)
                            {
                                Array.Resize(ref _commands, count * 2);
                            }

                            var cluster = clusters[c];
                            _commands[count++] = new DrawElementsIndirectCommand(section.IndexCount, (uint)cluster.Count, section.FirstIndex, section.BaseVertex, (uint)cluster.First);
                            visible += cluster.Count;
                        }

                        // A section with its own texture brings its own normal map and roughness; one drawn with the mesh's texture takes the mesh's.
                        var own = section.Texture is not null;
                        var texture = section.HasOwnColour ? null : section.Texture ?? mesh.Texture;
                        var normal = texture is null || mesh.Billboard ? null : own ? section.NormalMap : mesh.NormalMap;
                        into.Add(new DrawRange(mesh, texture, (int)section.IndexCount, first, count - first, visible, translucent || section.Tint.W < 0.999f, section.AlphaCutoff, section.Tint, pickable,
                            normal, texture is null ? Vector2.Zero : own ? section.Roughness : mesh.Roughness));
                    }
                }
            }
        }

        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _indirectBuffer);
        if (count > 0)
        {
            _gl.BufferData<DrawElementsIndirectCommand>(BufferTargetARB.DrawIndirectBuffer, _commands.AsSpan(0, count), BufferUsageARB.StreamDraw);
        }

        return batches;
    }

    private unsafe void DrawOverlay(in Matrix4x4 viewProj, int width, int height)
    {
        var s = Settings;
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.BindVertexArray(_lineVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _lineVbo);
        if (OverlayTriangles.Count > 0)
        {
            // Filled shapes first: position + colour through the plain line program.
            var data = new float[OverlayTriangles.Count * 3 * 7];
            var k = 0;
            foreach (var triangle in OverlayTriangles)
            {
                foreach (var point in new[] { triangle.A, triangle.B, triangle.C })
                {
                    data[k++] = point.X;
                    data[k++] = point.Y;
                    data[k++] = point.Z;
                    data[k++] = triangle.Color.X;
                    data[k++] = triangle.Color.Y;
                    data[k++] = triangle.Color.Z;
                    data[k++] = triangle.Color.W;
                }
            }

            _lineProgram.Use();
            _lineProgram.Set("uViewProj", viewProj);
            _lineProgram.Set("uEncodeSrgb", s.EncodeSrgb ? 1 : 0);
            _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.DynamicDraw);
            const uint stride = 7 * sizeof(float);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            _gl.DisableVertexAttribArray(2);
            _gl.DisableVertexAttribArray(3);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(OverlayTriangles.Count * 3));
        }

        if (Overlay.Count > 0)
        {
            // Lines as screen-space quads: start, end, colour, (end, side, width) and the polyline neighbours per corner,
            // six corners a line. A neighbour equal to the end itself means a square cap there.
            const int floats = 19;
            var data = new float[Overlay.Count * 6 * floats];
            var k = 0;
            ReadOnlySpan<(float End, float Side)> corners = [(0f, -1f), (1f, -1f), (1f, 1f), (0f, -1f), (1f, 1f), (0f, 1f)];
            foreach (var line in Overlay)
            {
                var prev = line.Prev ?? line.Start;
                var next = line.Next ?? line.End;
                foreach (var (end, side) in corners)
                {
                    data[k++] = line.Start.X;
                    data[k++] = line.Start.Y;
                    data[k++] = line.Start.Z;
                    data[k++] = line.End.X;
                    data[k++] = line.End.Y;
                    data[k++] = line.End.Z;
                    data[k++] = line.Color.X;
                    data[k++] = line.Color.Y;
                    data[k++] = line.Color.Z;
                    data[k++] = line.Color.W;
                    data[k++] = end;
                    data[k++] = side;
                    data[k++] = MathF.Max(line.Width, 0.5f);
                    data[k++] = prev.X;
                    data[k++] = prev.Y;
                    data[k++] = prev.Z;
                    data[k++] = next.X;
                    data[k++] = next.Y;
                    data[k++] = next.Z;
                }
            }

            _strokeProgram.Use();
            _strokeProgram.Set("uViewProj", viewProj);
            _strokeProgram.Set("uViewport", new Vector2(Math.Max(1, width), Math.Max(1, height)));
            _strokeProgram.Set("uEncodeSrgb", s.EncodeSrgb ? 1 : 0);
            _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.DynamicDraw);
            const uint stride = floats * sizeof(float);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
            _gl.EnableVertexAttribArray(3);
            _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, (void*)(10 * sizeof(float)));
            _gl.EnableVertexAttribArray(4);
            _gl.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, stride, (void*)(13 * sizeof(float)));
            _gl.EnableVertexAttribArray(5);
            _gl.VertexAttribPointer(5, 3, VertexAttribPointerType.Float, false, stride, (void*)(16 * sizeof(float)));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(Overlay.Count * 6));
            _gl.DisableVertexAttribArray(2);
            _gl.DisableVertexAttribArray(3);
            _gl.DisableVertexAttribArray(4);
            _gl.DisableVertexAttribArray(5);
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
        GlErrors.Drain(_gl);
    }

    private void DrawGrid(RenderSettings s, FlyCamera camera, float aspect, in Matrix4x4 viewProj, bool reverseZ)
    {
        Matrix4x4.Invert(camera.GetViewProjection(aspect), out var invGl);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gridProgram.Use();
        _gridProgram.Set("uInvViewProj", invGl);
        _gridProgram.Set("uViewProj", viewProj);
        _gridProgram.Set("uDepthZeroToOne", reverseZ ? 1 : 0);
        _gridProgram.Set("uHeight", s.GridHeight);
        _gridProgram.Set("uCellSize", MathF.Max(s.GridCellSize, 1e-3f));
        _gridProgram.Set("uMajorEvery", MathF.Max(s.GridMajorEvery, 1f));
        _gridProgram.Set("uFadeDistance", MathF.Max(s.GridFadeDistance, 1f));
        _gridProgram.Set("uCameraPosition", camera.Position);
        _gridProgram.Set("uMinorColor", s.GridMinorColor);
        _gridProgram.Set("uMajorColor", s.GridMajorColor);
        _gridProgram.Set("uAxisXColor", s.GridAxisXColor);
        _gridProgram.Set("uAxisZColor", s.GridAxisZColor);
        _gridProgram.Set("uEncodeSrgb", s.EncodeSrgb ? 1 : 0);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>The sky, haze and tone-mapping uniforms shared by the mesh and sky programs.</summary>
    private static void SetAtmosphere(ShaderProgram program, RenderSettings s)
    {
        program.Set("uLightDirection", SafeNormalize(s.LightDirection));
        program.Set("uLightColor", s.LightColor);
        program.Set("uFogFalloff", MathF.Max(0f, s.FogHeightFalloff));
        program.Set("uSky", s.Sky ? 1 : 0);
        program.Set("uSkyZenith", s.SkyZenithColor);
        program.Set("uSkyHorizon", s.SkyHorizonColor);
        program.Set("uExposure", MathF.Max(0f, s.Exposure));
        program.Set("uEncodeSrgb", s.EncodeSrgb ? 1 : 0);
    }

    private const int ShadowUnit = 6;
    private const int CopyColorUnit = 13;
    private const int CopyDepthUnit = 14;

    /// <summary>
    /// Copies the target's colour and depth (everything opaque drawn so far, both scenes of a frame) into textures the water
    /// reads at its own pixel, and binds them; the target is bound again for drawing.
    /// </summary>
    private void CopyScene(RenderTarget target)
    {
        if (_copySize != (target.Width, target.Height))
        {
            if (_copyFbo != 0)
            {
                _gl.DeleteFramebuffer(_copyFbo);
                _gl.DeleteTexture(_copyColor);
                _gl.DeleteTexture(_copyDepth);
            }

            _copyColor = CopyTexture(SizedInternalFormat.Rgba8);
            _copyDepth = CopyTexture(SizedInternalFormat.DepthComponent32f);
            _copyFbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _copyFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _copyColor, 0);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _copyDepth, 0);
            _copySize = (target.Width, target.Height);
        }

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.ColorFramebuffer);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _copyFbo);
        _gl.BlitFramebuffer(0, 0, target.Width, target.Height, 0, 0, target.Width, target.Height,
            ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        target.BindColor();
        foreach (var (unit, texture) in (ReadOnlySpan<(int, uint)>)[(CopyColorUnit, _copyColor), (CopyDepthUnit, _copyDepth)])
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.BindSampler((uint)unit, 0);
        }

        _gl.ActiveTexture(TextureUnit.Texture0);

        uint CopyTexture(SizedInternalFormat format)
        {
            var texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexStorage2D(TextureTarget.Texture2D, 1, format, (uint)target.Width, (uint)target.Height);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }
    }
    private const int NormalUnit = 7;

    private static bool Overlaps(in BoundingBox a, in BoundingBox b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

    /// <summary>
    /// The sun's orthographic view for this frame: a square of <see cref="RenderSettings.ShadowDistance"/> around a point
    /// half that far in front of the camera, snapped to whole shadow texels so shadow edges do not crawl as the camera
    /// moves, and the world box of everything it can shade.
    /// </summary>
    private static ShadowFrame ShadowView(RenderSettings s, FlyCamera camera)
    {
        var radius = MathF.Max(s.ShadowDistance, 100f);
        var depth = (2f * radius) + 10_000f;
        var dir = SafeNormalize(s.LightDirection);
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, dir, MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY);
        var centre = Vector3.Transform(camera.Position + (camera.Forward * (radius * 0.5f)), view);
        var texel = 2f * radius / ShadowMapSize;
        centre.X = MathF.Round(centre.X / texel) * texel;
        centre.Y = MathF.Round(centre.Y / texel) * texel;
        var proj = Matrix4x4.CreateOrthographicOffCenter(centre.X - radius, centre.X + radius, centre.Y - radius, centre.Y + radius, -centre.Z - depth, -centre.Z + depth);
        Matrix4x4.Invert(view, out var toWorld);
        var bounds = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue));
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(centre.X + ((i & 1) == 0 ? -radius : radius), centre.Y + ((i & 2) == 0 ? -radius : radius), centre.Z + ((i & 4) == 0 ? -depth : depth));
            bounds = bounds.Include(Vector3.Transform(corner, toWorld));
        }

        return new ShadowFrame(view * proj, texel, bounds);
    }

    /// <summary>Depth of the shadow casters picked by <see cref="PrepareFrame"/>, seen from the sun (plain GL depth, not reverse-Z).</summary>
    private void RenderShadowMap(in Matrix4x4 viewProj)
    {
        if (_shadowFbo == 0)
        {
            _shadowMap = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _shadowMap);
            _gl.TexStorage2D(TextureTarget.Texture2D, 1, SizedInternalFormat.DepthComponent32f, ShadowMapSize, ShadowMapSize);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _shadowFbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _shadowMap, 0);
            _gl.DrawBuffer(DrawBufferMode.None);
            _gl.ReadBuffer(ReadBufferMode.None);
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.Viewport(0, 0, ShadowMapSize, ShadowMapSize);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.DepthMask(true);
        _gl.ClearDepth(1.0);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(2f, 4f);
        _shadowProgram.Use();
        _shadowProgram.Set("uViewProj", viewProj);
        _shadowProgram.Set("uTexture", 0);
        _shadowProgram.Set("uBillboard", 0);
        GpuTexture? bound = null;
        var cutoff = -1f;
        foreach (var draw in _shadowDraws)
        {
            var sectionCutoff = draw.Texture is null ? 0f : draw.AlphaCutoff;
            if (sectionCutoff > 0f && !ReferenceEquals(draw.Texture, bound))
            {
                draw.Texture!.Bind(0); // masked leaves cast the shape of their leaves, not of their cards
                bound = draw.Texture;
            }

            if (sectionCutoff != cutoff)
            {
                _shadowProgram.Set("uAlphaCutoff", sectionCutoff);
                cutoff = sectionCutoff;
            }

            draw.Mesh.DrawIndirect(draw.FirstCommand, draw.CommandCount);
        }

        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.PolygonOffsetFill);
    }

    /// <summary>Full-screen sky where the depth buffer is still clear (after the opaque pass, so covered pixels cost nothing).</summary>
    private void DrawSky(RenderSettings s, FlyCamera camera, float aspect, bool reverseZ)
    {
        Matrix4x4.Invert(camera.GetViewProjection(aspect), out var invGl);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthFunc(reverseZ ? DepthFunction.Gequal : DepthFunction.Lequal);
        _skyProgram.Use();
        SetAtmosphere(_skyProgram, s);
        _skyProgram.Set("uInvViewProj", invGl);
        _skyProgram.Set("uFarDepth", reverseZ ? 0f : 1f);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
        _gl.DepthFunc(reverseZ ? DepthFunction.Greater : DepthFunction.Less);
        _gl.DepthMask(true);
    }

    private void BeginDepthState(bool reverseZ)
    {
        _gl.Disable(EnableCap.FramebufferSrgb);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        if (reverseZ)
        {
            _gl.ClipControl(ClipControlOrigin.LowerLeft, ClipControlDepth.ZeroToOne);
            _gl.DepthFunc(DepthFunction.Greater);
            _gl.ClearDepth(0.0);
        }
        else
        {
            _gl.DepthFunc(DepthFunction.Less);
            _gl.ClearDepth(1.0);
        }

        if (Settings.BackfaceCulling)
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Back);
            _gl.FrontFace(FrontFaceDirection.Ccw);
        }
        else
        {
            _gl.Disable(EnableCap.CullFace);
        }
    }

    private void EndDepthState(bool reverseZ)
    {
        if (reverseZ)
        {
            _gl.ClipControl(ClipControlOrigin.LowerLeft, ClipControlDepth.NegativeOneToOne);
        }

        _gl.DepthFunc(DepthFunction.Less);
        _gl.ClearDepth(1.0);
        _gl.Disable(EnableCap.CullFace);
    }

    private float EncodeClear(float linear) => Settings.EncodeSrgb ? ColorSpace.LinearToSrgb(linear) : linear;

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : -Vector3.UnitY;

    /// <summary>
    /// One material section of one LOD of a mesh in the current frame: a run of indirect commands (one per visible
    /// cluster drawn at that LOD), the instances they draw and the texture to bind.
    /// </summary>
    /// <summary>How many times a scene's batches were rebuilt and their instances uploaded (diagnostics, tests).</summary>
    public int BatchUploads { get; private set; }

    /// <summary>The last rebuild of a scene's batches (diagnostics): partial or not, what it sent and where its time went.</summary>
    public BatchRebuild? LastRebuild { get; private set; }

    /// <summary>The batches of one scene and which of its builds the GPU instance buffers hold.</summary>
    private sealed class BatchState
    {
        public SceneBatcher Batcher { get; } = new();

        public int UploadedBuild { get; set; } = -1;

        /// <summary>The batches whose records the meshes' instance buffers hold.</summary>
        public HashSet<RenderBatch> Uploaded { get; set; } = new(ReferenceEqualityComparer.Instance);
    }

    private const uint ShadowMapSize = 2048;

    private readonly record struct ShadowFrame(Matrix4x4 ViewProj, float Texel, BoundingBox Bounds);

    private readonly record struct DrawRange(GpuMesh Mesh, GpuTexture? Texture, int IndexCount, int FirstCommand, int CommandCount, int Instances, bool Translucent, float AlphaCutoff, Vector4 Tint, bool Pickable,
        GpuTexture? NormalMap = null, Vector2 Roughness = default);
}
