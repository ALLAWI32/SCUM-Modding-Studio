using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Viewport;

/// <summary>A <see cref="PreviewModel"/> living on the GPU: its scene graph and GL-space bounds (render thread, GL context current).</summary>
public sealed class PreviewScene : IDisposable
{
    private readonly SceneRenderer _renderer;
    private readonly List<MeshHandle> _handles = [];
    private readonly List<(SceneNode Node, MeshHandle Handle)> _parts = [];
    private readonly Dictionary<string, (TextureImage Image, GpuTexture Texture)> _textures = new(StringComparer.OrdinalIgnoreCase);

    private PreviewScene(SceneRenderer renderer, PreviewModel model)
    {
        _renderer = renderer;
        Model = model;
        var meshes = new Dictionary<MeshData, MeshHandle>(ReferenceEqualityComparer.Instance);
        var id = 0u;
        foreach (var part in model.Parts)
        {
            id++; // each part is pickable on its own (1-based, the order of Model.Parts)
            var texture = TextureOf(part, model);
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
                _handles.Add(handle);
            }

            var matrix = UeToGl.ModelMatrix(part.Transform);
            var node = Scene.Add(handle, matrix, id, part.Name);
            Style(node, part);
            _parts.Add((node, handle));
            Bounds = Bounds.Union(LevelSceneUploader.TransformBounds(handle.Bounds, matrix));
        }
    }

    /// <summary>The model this scene was built from (or last restyled to).</summary>
    public PreviewModel Model { get; private set; }

    /// <summary>The renderable scene graph.</summary>
    public Scene Scene { get; } = new();

    /// <summary>GL-space bounds of every part.</summary>
    public BoundingBox Bounds { get; private set; } = BoundingBox.Empty;

    /// <summary>Uploads <paramref name="model"/> (textures once per path, a mesh once per part geometry) into <paramref name="renderer"/>.</summary>
    public static PreviewScene Upload(SceneRenderer renderer, PreviewModel model)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(model);
        return new PreviewScene(renderer, model);
    }

    /// <summary>
    /// Takes the look of <paramref name="model"/> (tints, shine, textures) when it has the same parts as <see cref="Model"/>
    /// (a repaint), without uploading the meshes again or moving the camera. False when the parts differ: upload it instead.
    /// </summary>
    public bool Restyle(PreviewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Parts.Count != _parts.Count
            || model.Parts.Where((p, i) => !ReferenceEquals(p.Mesh, Model.Parts[i].Mesh) || p.Transform != Model.Parts[i].Transform).Any())
        {
            return false;
        }

        var old = _textures.Values.Select(t => t.Texture).ToList();
        for (var i = 0; i < _parts.Count; i++)
        {
            var (node, handle) = _parts[i];
            Style(node, model.Parts[i]);
            _renderer.SetMeshTexture(handle, TextureOf(model.Parts[i], model));
        }

        var used = _textures.Values.Select(t => t.Texture).ToHashSet();
        foreach (var texture in old.Where(t => !used.Contains(t)))
        {
            texture.Dispose();
        }

        Model = model;
        return true;
    }

    /// <summary>Releases the GPU meshes and textures (GL context current).</summary>
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

        _handles.Clear();
        _parts.Clear();
        _textures.Clear();
    }

    private static void Style(SceneNode node, PreviewPart part)
    {
        node.Tint = part.Tint ?? System.Numerics.Vector4.One;
        node.Surface = part.Surface;
    }

    /// <summary>The part's texture, uploaded once per path and again when the model brings a new image for that path.</summary>
    private GpuTexture? TextureOf(PreviewPart part, PreviewModel model)
    {
        if (part.TexturePath is not { } path || !model.Textures.TryGetValue(path, out var image))
        {
            return null;
        }

        if (_textures.TryGetValue(path, out var known) && ReferenceEquals(known.Image, image))
        {
            return known.Texture;
        }

        var texture = _renderer.CreateTexture(image.Width, image.Height, image.Rgba, image.IsSrgb);
        _textures[path] = (image, texture);
        return texture;
    }
}
