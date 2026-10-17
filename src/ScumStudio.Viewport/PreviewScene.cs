using System.Diagnostics;
using System.Numerics;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Viewport;

/// <summary>A <see cref="PreviewModel"/> living on the GPU: its scene graph and GL-space bounds (render thread, GL context current).</summary>
public sealed class PreviewScene : IDisposable
{
    /// <summary>The floor under a sunlit preview: a neutral grey.</summary>
    private static readonly Vector4 GroundTint = new(0.3f, 0.3f, 0.28f, 1f);

    private readonly SceneRenderer _renderer;
    private readonly List<MeshHandle> _handles = [];
    private readonly List<(SceneNode Node, MeshHandle Handle)> _parts = [];
    private readonly Dictionary<string, (TextureImage Image, GpuTexture Texture)> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Path, TextureImage Image)> _staged = new();
    private readonly List<SceneNode> _figure = [];
    private readonly List<GpuTexture> _figureTextures = [];
    private PreviewModel? _figureModel;
    private MeshHandle? _groundMesh;
    private SceneNode? _ground;
    private bool _showScale;

    private PreviewScene(SceneRenderer renderer, PreviewModel model, bool staged)
    {
        _renderer = renderer;
        Model = model;
        var meshes = new Dictionary<MeshData, MeshHandle>(ReferenceEqualityComparer.Instance);
        var id = 0u;
        foreach (var part in model.Parts)
        {
            id++; // each part is pickable on its own (1-based, the order of Model.Parts)
            var texture = TextureOf(part, model, staged);
            if (!meshes.TryGetValue(part.Mesh, out var handle))
            {
                var prepared = PreparedMesh.From(part.Mesh, MeshSpace.Unreal, 1f);
                Dictionary<string, float>? cutoffs = null;
                if (part.AlphaCutoff > 0f)
                {
                    cutoffs = [];
                    foreach (var section in prepared.Lods.SelectMany(l => l.Sections))
                    {
                        cutoffs[section.Material] = part.AlphaCutoff; // masked: the texture's alpha cuts the leaves out
                    }
                }

                handle = renderer.AddMesh(prepared, texture, null, cutoffs);
                meshes[part.Mesh] = handle;
                var gpu = renderer.Meshes[handle.Id];
                gpu.NormalMap = texture is null ? null : NormalOf(part, model, staged);
                gpu.Roughness = part.Roughness;
                _handles.Add(handle);
            }

            var matrix = UeToGl.ModelMatrix(part.Transform);
            var node = Scene.Add(handle, matrix, id, part.Name);
            Style(node, part);
            _parts.Add((node, handle));
            ModelBounds = ModelBounds.Union(LevelSceneUploader.TransformBounds(handle.Bounds, matrix));
        }
    }

    /// <summary>The camera yaw the previews open with (<c>MeshPreview</c>, thumbnails): the scale figure faces it.</summary>
    public const float DefaultViewYaw = -135f;

    /// <summary>The model this scene was built from (or last restyled to).</summary>
    public PreviewModel Model { get; private set; }

    /// <summary>The renderable scene graph.</summary>
    public Scene Scene { get; } = new();

    /// <summary>GL-space bounds of the model's parts.</summary>
    public BoundingBox ModelBounds { get; private set; } = BoundingBox.Empty;

    /// <summary>GL-space bounds of every part, and of the scale figure while it stands there (what the camera frames).</summary>
    public BoundingBox Bounds => _figure.Aggregate(ModelBounds, (b, n) => b.Union(LevelSceneUploader.TransformBounds(n.Mesh!.Bounds, n.LocalTransform)));

    /// <summary>The scale figure stands beside the model now.</summary>
    public bool FigureShown => _figure.Count > 0;

    /// <summary>
    /// <see cref="ShowScale"/> is on but the figure drawn is not <see cref="ScaleFigure.Character"/> (it arrived, or went,
    /// after the scene was built): set <see cref="ShowScale"/> again to bring it up to date.
    /// </summary>
    public bool FigureStale => _showScale && !ReferenceEquals(_figureModel, Wanted(true) ? FigureOf() : null);

    /// <summary>Where the figure comes from: <see cref="ScaleFigure.Character"/> (tests stand their own beside a model).</summary>
    internal Func<PreviewModel?> FigureOf { get; set; } = () => ScaleFigure.Character;

    /// <summary>
    /// Stands <see cref="ScaleFigure"/> (the game's prisoner, 1.8 m) beside the model on its ground, facing
    /// <see cref="DefaultViewYaw"/>: nodes that are not parts (not pickable, not in <see cref="Model"/>) but count in
    /// <see cref="Bounds"/>. A model under <see cref="ScaleFigure.MinModelSize"/> gets none, and so does every model while
    /// the figure is not loaded (<see cref="ScaleFigure.Character"/> null): never a stand-in.
    /// </summary>
    public bool ShowScale
    {
        get => _showScale;
        set
        {
            _showScale = value;
            var figure = Wanted(value) ? FigureOf() : null;
            if (ReferenceEquals(figure, _figureModel))
            {
                return;
            }

            RemoveFigure();
            if (figure is null)
            {
                return;
            }

            _figureModel = figure;
            var at = ScaleFigure.Placement(ModelBounds, DefaultViewYaw);
            var uploaded = new Dictionary<string, GpuTexture>(StringComparer.OrdinalIgnoreCase);
            GpuTexture? Texture(string? path)
            {
                if (path is null || !figure.Textures.TryGetValue(path, out var image))
                {
                    return null;
                }

                if (!uploaded.TryGetValue(path, out var texture))
                {
                    uploaded[path] = texture = TextureUpload.Create(_renderer, image);
                    _figureTextures.Add(texture);
                }

                return texture;
            }

            foreach (var part in figure.Parts)
            {
                var texture = Texture(part.TexturePath);
                var handle = _renderer.AddMesh(PreparedMesh.From(part.Mesh, MeshSpace.Unreal, 1f), texture);
                var gpu = _renderer.Meshes[handle.Id];
                gpu.NormalMap = texture is null ? null : Texture(part.NormalPath);
                gpu.Roughness = part.Roughness;
                _handles.Add(handle);
                var node = Scene.Add(handle, UeToGl.ModelMatrix(part.Transform) * at, 0, nameof(ScaleFigure));
                node.Tint = part.Tint ?? Vector4.One;
                _figure.Add(node);
            }
        }
    }

    /// <summary>A figure belongs beside the model: asked for, and the model is large enough to need one.</summary>
    private bool Wanted(bool asked)
    {
        var size = ModelBounds.Size;
        return asked && !ModelBounds.IsEmpty && MathF.Max(size.X, MathF.Max(size.Y, size.Z)) >= ScaleFigure.MinModelSize;
    }

    /// <summary>Takes the figure's nodes, meshes and textures off the scene.</summary>
    private void RemoveFigure()
    {
        foreach (var node in _figure)
        {
            Scene.Root.Remove(node);
            if (node.Mesh is { } mesh && _handles.Remove(mesh))
            {
                _renderer.RemoveMesh(mesh);
            }
        }

        foreach (var texture in _figureTextures)
        {
            texture.Dispose();
        }

        _figure.Clear();
        _figureTextures.Clear();
        _figureModel = null;
    }

    /// <summary>
    /// A wide neutral floor just under the model (the sun's shadows need ground to fall on; beyond the camera's far plane it
    /// meets the sky like a horizon). Not pickable and not in <see cref="Bounds"/>.
    /// </summary>
    public bool Ground
    {
        get => _ground is not null;
        set
        {
            if (value == _ground is not null || ModelBounds.IsEmpty)
            {
                return;
            }

            if (_ground is { } old)
            {
                Scene.Root.Remove(old);
                _ground = null;
                return;
            }

            // A hair below the model's base so the preview's grid, drawn at the base, lies on the floor instead of in it.
            var reach = MathF.Max(ModelBounds.Extent.Length(), 100f);
            _groundMesh ??= Extra(PreparedMesh.From(PrimitiveMeshes.Plane(reach * 100f), MeshSpace.Gl, 1f));
            var centre = ModelBounds.Center;
            _ground = Scene.Add(_groundMesh, Matrix4x4.CreateTranslation(centre.X, ModelBounds.Min.Y - (reach * 0.002f), centre.Z), 0, nameof(Ground));
            _ground.Tint = GroundTint;
        }
    }

    /// <summary>Largest edge of the stand-in a staged texture draws with until <see cref="Stage"/> sends it whole.</summary>
    public const int StandInSize = 256;

    /// <summary>Textures still drawn with their stand-in (<see cref="UploadStaged"/>).</summary>
    public int PendingTextures => _staged.Count;

    /// <summary>Uploads <paramref name="model"/> (textures once per path, a mesh once per part geometry) into <paramref name="renderer"/>.</summary>
    public static PreviewScene Upload(SceneRenderer renderer, PreviewModel model)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(model);
        return new PreviewScene(renderer, model, staged: false);
    }

    /// <summary>
    /// Like <see cref="Upload(SceneRenderer, PreviewModel)"/>, but a texture larger than <see cref="StandInSize"/> goes up as
    /// a stand-in (its own lower mips) and whole later, through <see cref="Stage"/> a few milliseconds a frame: a vehicle's
    /// full-size textures are hundreds of MB (ten materials at 4096 px: 427 MB, 120 ms in one frame).
    /// </summary>
    public static PreviewScene UploadStaged(SceneRenderer renderer, PreviewModel model)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(model);
        return new PreviewScene(renderer, model, staged: true);
    }

    /// <summary>
    /// Sends staged textures whole until <paramref name="budgetMs"/> is spent (at least one a call), each one replacing its
    /// stand-in wherever that is drawn. True once none is left; call it once a frame until then.
    /// </summary>
    public bool Stage(double budgetMs)
    {
        var clock = Stopwatch.StartNew();
        while (_staged.Count > 0)
        {
            if (clock.Elapsed.TotalMilliseconds >= budgetMs)
            {
                return false;
            }

            var (path, image) = _staged.Dequeue();
            if (!_textures.TryGetValue(path, out var shown) || !ReferenceEquals(shown.Image, image))
            {
                continue; // restyled to another image meanwhile
            }

            var whole = TextureUpload.Create(_renderer, image);
            _textures[path] = (image, whole);
            foreach (var (_, handle) in _parts)
            {
                var gpu = _renderer.Meshes[handle.Id];
                if (ReferenceEquals(gpu.Texture, shown.Texture))
                {
                    _renderer.SetMeshTexture(handle, whole);
                }

                if (ReferenceEquals(gpu.NormalMap, shown.Texture))
                {
                    gpu.NormalMap = whole;
                }
            }

            shown.Texture.Dispose();
        }

        return true;
    }

    /// <summary>
    /// <paramref name="image"/> at most <see cref="StandInSize"/> on its longest edge: its own lower mips when it is kept
    /// compressed (null when the chain stops above that size), every n-th pixel when it is RGBA.
    /// </summary>
    internal static TextureImage? StandIn(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var skip = 0;
        while (Math.Max(image.Width, image.Height) >> skip > StandInSize)
        {
            skip++;
        }

        var (w, h) = (Math.Max(1, image.Width >> skip), Math.Max(1, image.Height >> skip));
        if (image.CompressedMips is { } mips)
        {
            return mips.Count > skip ? image with { Width = w, Height = h, MipIndex = image.MipIndex + skip, CompressedMips = mips.Skip(skip).ToArray() } : null;
        }

        if (image.Rgba.Length < image.Width * image.Height * 4)
        {
            return null;
        }

        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                Buffer.BlockCopy(image.Rgba, (((y << skip) * image.Width) + (x << skip)) * 4, rgba, ((y * w) + x) * 4, 4);
            }
        }

        return image with { Width = w, Height = h, Rgba = rgba };
    }

    /// <summary>
    /// Takes the look of <paramref name="model"/> (tints, shine, textures) and the places of its parts when it has the same
    /// parts as <see cref="Model"/> (a repaint, a part moved in the Parts tab), without uploading the meshes again or moving
    /// the camera. False when the parts differ: upload it instead.
    /// </summary>
    public bool Restyle(PreviewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Parts.Count != _parts.Count || model.Parts.Where((p, i) => !ReferenceEquals(p.Mesh, Model.Parts[i].Mesh)).Any())
        {
            return false;
        }

        var old = _textures.Values.Select(t => t.Texture).ToList();
        var moved = false;
        for (var i = 0; i < _parts.Count; i++)
        {
            var (node, handle) = _parts[i];
            Style(node, model.Parts[i]);
            var texture = TextureOf(model.Parts[i], model);
            _renderer.SetMeshTexture(handle, texture);
            var gpu = _renderer.Meshes[handle.Id];
            gpu.NormalMap = texture is null ? null : NormalOf(model.Parts[i], model);
            gpu.Roughness = model.Parts[i].Roughness; // a paint's own roughness on the painted texels
            var matrix = UeToGl.ModelMatrix(model.Parts[i].Transform);
            if (node.LocalTransform != matrix)
            {
                node.LocalTransform = matrix;
                moved = true;
            }
        }

        var used = _textures.Values.Select(t => t.Texture).ToHashSet();
        foreach (var texture in old.Where(t => !used.Contains(t)))
        {
            texture.Dispose();
        }

        Model = model;
        if (moved)
        {
            UpdateBounds();
        }

        return true;
    }

    /// <summary>Index of <paramref name="node"/> in <see cref="Model"/>'s parts, or -1 (the scale figure, the floor, another scene's node).</summary>
    public int PartIndexOf(SceneNode? node) => node is null ? -1 : _parts.FindIndex(p => ReferenceEquals(p.Node, node));

    /// <summary>The node drawing part <paramref name="index"/>, or null.</summary>
    public SceneNode? PartNode(int index) => index >= 0 && index < _parts.Count ? _parts[index].Node : null;

    /// <summary>Where part <paramref name="index"/> is drawn now (UE space), or null for no such part.</summary>
    public FTransform? PartTransform(int index) => index >= 0 && index < _parts.Count ? GlToUe(_parts[index].Node.LocalTransform) : null;

    /// <summary>
    /// Moves part <paramref name="index"/> to <paramref name="transform"/> (UE space; a gizmo drag, a part's offset) with its
    /// mesh and textures kept and the camera untouched; <see cref="ModelBounds"/> follows. False for no such part.
    /// </summary>
    public bool SetPartTransform(int index, FTransform transform)
    {
        if (index < 0 || index >= _parts.Count)
        {
            return false;
        }

        _parts[index].Node.LocalTransform = UeToGl.ModelMatrix(transform);
        UpdateBounds();
        return true;
    }

    /// <summary>Hides part <paramref name="index"/> (a removed door) or shows it again; a hidden part leaves <see cref="ModelBounds"/>. False for no such part.</summary>
    public bool SetPartHidden(int index, bool hidden)
    {
        if (index < 0 || index >= _parts.Count)
        {
            return false;
        }

        _parts[index].Node.Visible = !hidden;
        UpdateBounds();
        return true;
    }

    /// <summary>The bounds of the parts shown, where they stand now.</summary>
    private void UpdateBounds()
    {
        var bounds = BoundingBox.Empty;
        foreach (var (node, handle) in _parts.Where(p => p.Node.Visible))
        {
            bounds = bounds.Union(LevelSceneUploader.TransformBounds(handle.Bounds, node.LocalTransform));
        }

        ModelBounds = bounds;
    }

    /// <summary>The UE transform a part's GL model matrix was made from (<see cref="UeToGl.ModelMatrix"/> undone).</summary>
    private static FTransform GlToUe(Matrix4x4 gl) => FTransform.FromMatrix(UeToGl.AxisSwap * gl * UeToGl.AxisSwap); // the swap is its own inverse

    /// <summary>Releases the GPU meshes and textures (GL context current).</summary>
    /// <summary>
    /// Draws the parts of vehicle attachment <paramref name="attachment"/> (a package path) selected, every other part plain;
    /// empty clears. Parts match by name without the vehicle (<c>BPC_WolfArmour_Door_FrontLeft</c> = <c>BPC_WolfsWagen_Door_FrontLeft</c>):
    /// a clone is drawn from its template's packages.
    /// </summary>
    /// <returns>How many parts are highlighted.</returns>
    public int Highlight(string attachment)
    {
        var count = 0;
        for (var i = 0; i < _parts.Count; i++)
        {
            // A material object path (a gun part's paint row) selects the parts drawn with it.
            var on = attachment.Length > 0 && ((Model.Parts[i].Attachment.Length > 0 && string.Equals(PartKey(Model.Parts[i].Attachment), PartKey(attachment), StringComparison.OrdinalIgnoreCase))
                                               || string.Equals(Model.Parts[i].Material, attachment, StringComparison.OrdinalIgnoreCase));
            _parts[i].Node.Selected = on;
            count += on ? 1 : 0;
        }

        return count;
    }

    /// <summary><c>/Game/…/BPC_WolfsWagen_Door_FrontLeft</c> → <c>Door_FrontLeft</c> (the leaf without <c>BPC_</c> and the vehicle token).</summary>
    private static string PartKey(string package)
    {
        var leaf = package[(package.LastIndexOf('/') + 1)..];
        leaf = leaf.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) ? leaf[4..] : leaf;
        var token = leaf.IndexOf('_');
        return token > 0 ? leaf[(token + 1)..] : leaf;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Scene.Root.Clear();
        foreach (var handle in _handles)
        {
            _renderer.RemoveMesh(handle);
        }

        foreach (var (_, texture) in _textures.Values)
        {
            texture.Dispose();
        }

        foreach (var texture in _figureTextures)
        {
            texture.Dispose();
        }

        _handles.Clear();
        _parts.Clear();
        _textures.Clear();
        _staged.Clear();
        _figure.Clear();
        _figureTextures.Clear();
        _figureModel = null;
        _ground = null;
        _groundMesh = null;
    }

    /// <summary>Uploads a mesh of the scene's own (the floor): untextured, released with the scene.</summary>
    private MeshHandle Extra(PreparedMesh mesh)
    {
        var handle = _renderer.AddMesh(mesh);
        _handles.Add(handle);
        return handle;
    }

    private static void Style(SceneNode node, PreviewPart part)
    {
        node.Tint = part.Tint ?? Vector4.One;
        node.Surface = part.Surface;
    }

    /// <summary>The part's texture, uploaded once per path and again when the model brings a new image for that path.</summary>
    private GpuTexture? TextureOf(PreviewPart part, PreviewModel model, bool staged = false) => Upload(part.TexturePath, model, staged);

    /// <summary>The part's normal map (linear), uploaded like <see cref="TextureOf"/>.</summary>
    private GpuTexture? NormalOf(PreviewPart part, PreviewModel model, bool staged = false) => Upload(part.NormalPath, model, staged);

    private GpuTexture? Upload(string? path, PreviewModel model, bool staged)
    {
        if (path is null || !model.Textures.TryGetValue(path, out var image))
        {
            return null;
        }

        if (_textures.TryGetValue(path, out var known) && ReferenceEquals(known.Image, image))
        {
            return known.Texture;
        }

        GpuTexture texture;
        if (staged && Math.Max(image.Width, image.Height) > StandInSize && StandIn(image) is { } standIn)
        {
            texture = TextureUpload.Create(_renderer, standIn);
            _staged.Enqueue((path, image));
        }
        else
        {
            texture = TextureUpload.Create(_renderer, image);
        }

        _textures[path] = (image, texture);
        return texture;
    }
}
