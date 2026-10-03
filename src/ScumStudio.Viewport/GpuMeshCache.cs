using ScumStudio.Assets.Textures;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Viewport;

/// <summary>
/// Meshes and mesh textures on the GPU kept from one <see cref="LevelSceneUploader.Upload"/> to the next (render thread,
/// GL context current): when the levels around a moving camera change, only meshes the new set adds are uploaded.
/// What the last <see cref="KeepUploads"/> uploads did not use is freed by <see cref="Trim"/>. Scenes uploaded with a
/// cache do not own these meshes; <see cref="Clear"/> forgets everything once the GL context is gone.
/// </summary>
public sealed class GpuMeshCache
{
    /// <summary>Uploads an unused mesh survives.</summary>
    public const int KeepUploads = 2;

    private readonly Dictionary<string, (MeshHandle Handle, int Used)> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (GpuTexture Texture, int Used)> _textures = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;

    /// <summary>Meshes on the GPU (diagnostics).</summary>
    public int MeshCount => _meshes.Count;

    /// <summary>Meshes the last upload had to send to the GPU (diagnostics).</summary>
    public int LastUploaded { get; private set; }

    /// <summary>Starts an upload.</summary>
    internal void Begin()
    {
        _generation++;
        LastUploaded = 0;
    }

    /// <summary>The texture of <paramref name="path"/>, uploaded the first time.</summary>
    internal GpuTexture Texture(SceneRenderer renderer, string path, TextureImage image)
    {
        if (!_textures.TryGetValue(path, out var entry))
        {
            entry = (renderer.CreateTexture(image.Width, image.Height, image.Rgba, image.IsSrgb), 0);
        }

        _textures[path] = entry with { Used = _generation };
        return entry.Texture;
    }

    /// <summary>The mesh of <paramref name="asset"/> (keyed by its mesh path), uploaded the first time.</summary>
    internal MeshHandle Mesh(SceneRenderer renderer, PreparedMeshAsset asset, IReadOnlyDictionary<string, GpuTexture> textures)
    {
        if (!_meshes.TryGetValue(asset.MeshPath, out var entry))
        {
            entry = (LevelSceneUploader.AddMesh(renderer, asset, textures), 0);
            LastUploaded++;
        }

        _meshes[asset.MeshPath] = entry with { Used = _generation };
        return entry.Handle;
    }

    /// <summary>Frees the meshes and textures the last <see cref="KeepUploads"/> uploads did not use.</summary>
    public void Trim(SceneRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var oldest = _generation - KeepUploads + 1;
        foreach (var (key, entry) in _meshes.Where(m => m.Value.Used < oldest).ToList())
        {
            renderer.RemoveMesh(entry.Handle);
            _meshes.Remove(key);
        }

        foreach (var (key, entry) in _textures.Where(t => t.Value.Used < oldest).ToList())
        {
            entry.Texture.Dispose();
            _textures.Remove(key);
        }
    }

    /// <summary>Forgets every entry without touching GL (the context and its objects are gone).</summary>
    public void Clear()
    {
        _meshes.Clear();
        _textures.Clear();
    }
}
