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
    private readonly ShaderProgram _lineProgram;
    private readonly GpuTexture _white;
    private readonly uint _emptyVao;
    private readonly uint _lineVao;
    private readonly uint _lineVbo;
    private readonly uint _indirectBuffer;
    // One batcher per scene: a frame that draws the island backdrop and then the detailed scene must not throw the
    // other scene's batches away (that rebuilt and re-uploaded ~100k instances twice a frame: 84 ms instead of 2).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene, BatchState> _batchers = new();
    private readonly List<DrawRange> _draws = [];
    private DrawElementsIndirectCommand[] _commands = new DrawElementsIndirectCommand[1024];
    private int[] _lodOfCluster = new int[256];
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
        _lineProgram = new ShaderProgram(_gl, "line", ShaderSources.LineVertex, ShaderSources.LineFragment);
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
    /// Line segments drawn on top of the scene by the next <see cref="Render"/> (no depth test): gizmos, helpers. Replace
    /// or clear the list from the render thread.
    /// </summary>
    public List<OverlayLine> Overlay { get; } = [];

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
    /// <paramref name="texture"/> (textures are not owned by the renderer).
    /// </summary>
    public MeshHandle AddMesh(PreparedMesh mesh, GpuTexture? texture = null, IReadOnlyDictionary<string, GpuTexture>? materialTextures = null,
        IReadOnlyDictionary<string, float>? materialAlphaCutoffs = null, IReadOnlyDictionary<string, Vector4>? materialTints = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = _nextMeshId++;
        var gpu = new GpuMesh(_gl, id, mesh, texture, materialTextures, materialAlphaCutoffs, materialTints);
        _meshes[id] = gpu;
        GlErrors.Check(_gl, $"uploading mesh '{mesh.Name}'");
        return new MeshHandle(id, mesh.Name, mesh.Bounds);
    }

    /// <summary>Uploads an RGBA8 texture (see <see cref="GpuTexture.FromRgba8"/>); the caller owns it.</summary>
    public GpuTexture CreateTexture(int width, int height, ReadOnlySpan<byte> rgba, bool srgb = true) =>
        GpuTexture.FromRgba8(_gl, width, height, rgba, srgb);

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
        PrepareFrame(scene, camera, aspect, target.Height, out var culled, out var instances);
        var reverseZ = UsesReverseZ;
        var viewProj = camera.ViewMatrix * (reverseZ ? camera.GetReverseZProjection(aspect) : camera.GetProjection(aspect));
        var s = EffectiveSettings(scene);

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
        _meshProgram.Set("uLightDirection", SafeNormalize(s.LightDirection));
        _meshProgram.Set("uLightColor", s.LightColor);
        _meshProgram.Set("uHighlight", s.HighlightColor);
        _meshProgram.Set("uCameraPosition", camera.Position);
        _meshProgram.Set("uEncodeSrgb", s.EncodeSrgb ? 1 : 0);
        _meshProgram.Set("uFogColor", s.FogColor);
        _meshProgram.Set("uFogDensity", MathF.Max(0f, s.FogDensity));
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
        if (s.ShowGrid)
        {
            DrawGrid(s, camera, aspect, viewProj, reverseZ);
        }

        if (translucent is not null)
        {
            // Translucent nodes (tint alpha < 1, e.g. a sea plane) after everything opaque: blended, no depth writes.
            _meshProgram.Use();
            _meshProgram.Set("uOpaque", 0);
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

        if (Overlay.Count > 0)
        {
            DrawOverlay(viewProj);
        }

        GpuTexture.UnbindSampler(_gl);
        for (var unit = 1; unit <= 5; unit++)
        {
            GpuTexture.UnbindSampler(_gl, unit);
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

    /// <summary>Deletes all meshes and programs (textures passed to <see cref="AddMesh(PreparedMesh, GpuTexture?, IReadOnlyDictionary{string, GpuTexture}?, IReadOnlyDictionary{string, float}?, IReadOnlyDictionary{string, System.Numerics.Vector4}?)"/> are not owned).</summary>
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
        _lineProgram.Dispose();
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
        foreach (var draw in _draws)
        {
            // Translucent nodes pick only when they are objects (placed water, glass); the sea lets clicks through.
            if (!draw.Translucent || draw.Pickable)
            {
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
    private BatchResult PrepareFrame(Scene scene, FlyCamera camera, float aspect, int viewportHeight, out int culled, out int drawn)
    {
        var state = _batchers.GetValue(scene, _ => new BatchState());
        var batcher = state.Batcher;
        var batches = batcher.Get(scene);
        if (state.UploadedBuild != batcher.Builds)
        {
            foreach (var batch in batches.Batches)
            {
                if (_meshes.TryGetValue(batch.Mesh.Id, out var mesh))
                {
                    mesh.SetInstances(batch.Instances);
                }
            }

            state.UploadedBuild = batcher.Builds;
            batcher.ClearUpdates();
            BatchUploads++;
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
        var count = 0;
        culled = 0;
        drawn = 0;
        foreach (var batch in batches.Batches)
        {
            if (!_meshes.TryGetValue(batch.Mesh.Id, out var mesh) || mesh.IndexCount == 0)
            {
                continue;
            }

            if (frustum is { } f && !f.Intersects(batch.Bounds))
            {
                culled += batch.Instances.Length;
                continue;
            }

            var clusters = batch.Clusters;
            if (_lodOfCluster.Length < clusters.Length)
            {
                Array.Resize(ref _lodOfCluster, Math.Max(clusters.Length, _lodOfCluster.Length * 2));
            }

            var usedLods = 0;
            for (var c = 0; c < clusters.Length; c++)
            {
                var lod = LodMath.Classify(clusters[c], frustum, eye, tanHalfFov, minScreenSize, distanceScale, mesh.LodScreenSizes, s.ObjectDrawDistance, s.LodBias);
                _lodOfCluster[c] = lod;
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

            if (usedLods == 0)
            {
                continue;
            }

            var translucent = IsTranslucent(batch);
            var pickable = Array.Exists(batch.Instances, i => i.PickCode != 0);
            for (var lod = 0; lod < mesh.Lods.Length; lod++)
            {
                if ((usedLods & (1 << lod)) == 0)
                {
                    continue;
                }

                foreach (var section in mesh.Lods[lod].Sections)
                {
                    var first = count;
                    var visible = 0;
                    for (var c = 0; c < clusters.Length; c++)
                    {
                        if (_lodOfCluster[c] != lod)
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

                    _draws.Add(new DrawRange(mesh, section.HasOwnColour ? null : section.Texture ?? mesh.Texture, (int)section.IndexCount, first, count - first, visible, translucent || section.Tint.W < 0.999f, section.AlphaCutoff, section.Tint, pickable));
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

    private unsafe void DrawOverlay(in Matrix4x4 viewProj)
    {
        var s = Settings;
        var data = new float[Overlay.Count * 2 * 7];
        var k = 0;
        foreach (var line in Overlay)
        {
            foreach (var (point, color) in new[] { (line.Start, line.Color), (line.End, line.Color) })
            {
                data[k++] = point.X;
                data[k++] = point.Y;
                data[k++] = point.Z;
                data[k++] = color.X;
                data[k++] = color.Y;
                data[k++] = color.Z;
                data[k++] = color.W;
            }
        }

        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _lineProgram.Use();
        _lineProgram.Set("uViewProj", viewProj);
        _lineProgram.Set("uEncodeSrgb", s.EncodeSrgb ? 1 : 0);
        _gl.BindVertexArray(_lineVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _lineVbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.DynamicDraw);
        const uint stride = 7 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.LineWidth(2f);
        _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(Overlay.Count * 2));
        _gl.LineWidth(1f);
        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        GlErrors.Drain(_gl); // a driver may refuse LineWidth > 1 in core profiles; the lines still draw
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

    /// <summary>The batches of one scene and which of its builds the GPU instance buffers hold.</summary>
    private sealed class BatchState
    {
        public SceneBatcher Batcher { get; } = new();

        public int UploadedBuild { get; set; } = -1;
    }

    private readonly record struct DrawRange(GpuMesh Mesh, GpuTexture? Texture, int IndexCount, int FirstCommand, int CommandCount, int Instances, bool Translucent, float AlphaCutoff, Vector4 Tint, bool Pickable);
}
