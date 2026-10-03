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

    // Meshes and terrain keep the paths of the cached textures they draw with: using one keeps those alive too (a hit sends
    // nothing, so a texture only they used was trimmed while still drawn; owner: "the app freezes as I fly").
    private readonly Dictionary<string, (MeshHandle Handle, string[] Textures, int Used)> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (GpuTexture Texture, int Used)> _textures = new(StringComparer.OrdinalIgnoreCase);

    // Terrain components by the prepared object (the prepare cache hands the same one back while the tile stays loaded):
    // their mesh, baked ground texture and layer weights were re-sent every streaming step (owner: "it stutters as it loads").
    private readonly Dictionary<PreparedTerrain, (MeshHandle Handle, GpuTexture? Texture, GpuTexture? Weights, string[] Textures, int Used)> _terrain = new(ReferenceEqualityComparer.Instance);
    private List<string>? _taken;
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
        _taken?.Add(path);
        return entry.Texture;
    }

    /// <summary>The mesh of <paramref name="asset"/> (keyed by its mesh path), uploaded the first time.</summary>
    internal MeshHandle Mesh(SceneRenderer renderer, PreparedMeshAsset asset, IReadOnlyDictionary<string, GpuTexture> textures)
    {
        if (!_meshes.TryGetValue(asset.MeshPath, out var entry))
        {
            var uses = asset.MaterialTextures.Values.Append(asset.TexturePath).OfType<string>().Where(textures.ContainsKey).ToArray();
            entry = (LevelSceneUploader.AddMesh(renderer, asset, textures), uses, 0);
            LastUploaded++;
        }

        Touch(entry.Textures);
        _meshes[asset.MeshPath] = entry with { Used = _generation };
        return entry.Handle;
    }

    private void Touch(string[] paths)
    {
        foreach (var path in paths)
        {
            if (_textures.TryGetValue(path, out var texture))
            {
                _textures[path] = texture with { Used = _generation };
            }
        }
    }

    /// <summary>
    /// The GPU side of a terrain component (mesh, ground texture, layer weights), made by <paramref name="create"/> the first
    /// time this prepared component is uploaded.
    /// </summary>
    internal (MeshHandle Handle, GpuTexture? Texture) Terrain(PreparedTerrain terrain, Func<(MeshHandle Handle, GpuTexture? Texture, GpuTexture? Weights)> create)
    {
        if (!_terrain.TryGetValue(terrain, out var entry))
        {
            _taken = []; // the ground layer textures it takes from the cache
            var made = create();
            entry = (made.Handle, made.Texture, made.Weights, _taken.ToArray(), 0);
            _taken = null;
        }

        Touch(entry.Textures);
        _terrain[terrain] = entry with { Used = _generation };
        return (entry.Handle, entry.Texture);
    }

    /// <summary>Hands a terrain component's GPU objects to the caller (it now owns them; the cache forgets them).</summary>
    internal (MeshHandle Handle, GpuTexture? Texture, GpuTexture? Weights)? ReleaseTerrain(PreparedTerrain terrain)
    {
        if (!_terrain.Remove(terrain, out var entry))
        {
            return null;
        }

        return (entry.Handle, entry.Texture, entry.Weights);
    }

    /// <summary>Frees the meshes and textures the last <see cref="KeepUploads"/> uploads did not use.</summary>
    public void Trim(SceneRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var oldest = _generation - KeepUploads + 1;
        foreach (var (key, entry) in _terrain.Where(t => t.Value.Used < oldest).ToList())
        {
            renderer.RemoveMesh(entry.Handle);
            entry.Texture?.Dispose();
            entry.Weights?.Dispose();
            _terrain.Remove(key);
        }

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
        _terrain.Clear();
    }
}
