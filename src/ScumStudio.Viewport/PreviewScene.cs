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
    private readonly List<GpuTexture> _textures = [];

    private PreviewScene(SceneRenderer renderer, PreviewModel model)
    {
        _renderer = renderer;
        Model = model;
        var textures = new Dictionary<string, GpuTexture>(StringComparer.OrdinalIgnoreCase);
        var meshes = new Dictionary<MeshData, MeshHandle>(ReferenceEqualityComparer.Instance);
        var id = 0u;
        foreach (var part in model.Parts)
        {
            id++; // each part is pickable on its own (1-based, the order of Model.Parts)
            GpuTexture? texture = null;
            if (part.TexturePath is { } path && model.Textures.TryGetValue(path, out var image) && !textures.TryGetValue(path, out texture))
            {
                texture = renderer.CreateTexture(image.Width, image.Height, image.Rgba, image.IsSrgb);
                textures[path] = texture;
                _textures.Add(texture);
            }

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
            if (part.Tint is { } tint)
            {
                node.Tint = tint;
            }

            Bounds = Bounds.Union(LevelSceneUploader.TransformBounds(handle.Bounds, matrix));
        }
    }

    /// <summary>The model this scene was built from.</summary>
    public PreviewModel Model { get; }

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

    /// <summary>Releases the GPU meshes and textures (GL context current).</summary>
    public void Dispose()
    {
        Scene.Root.Clear();
        foreach (var handle in _handles)
        {
            _renderer.RemoveMesh(handle);
        }

        foreach (var texture in _textures)
        {
            texture.Dispose();
        }

        _handles.Clear();
        _textures.Clear();
    }
}
