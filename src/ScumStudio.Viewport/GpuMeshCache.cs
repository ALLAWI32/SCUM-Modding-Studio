using System.Collections.Concurrent;
using System.Diagnostics;
using ScumStudio.Assets.Textures;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Viewport;

/// <summary>
/// Meshes and mesh textures on the GPU kept from one <see cref="LevelSceneUploader.Upload"/> (or <see cref="LevelScene.Update"/>)
/// to the next (render thread, GL context current): when the levels around a moving camera change, only meshes the new set
/// adds are uploaded, and <see cref="Stage"/> sends those a few milliseconds a frame before the scene switches. What the
/// last <see cref="KeepUploads"/> uploads did not use and no node draws is freed by <see cref="Trim"/>. Scenes uploaded
/// with a cache do not own these meshes; <see cref="Clear"/> forgets everything once the GL context is gone.
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
    private Staging? _staging;

    /// <summary>Meshes on the GPU (diagnostics).</summary>
    public int MeshCount => _meshes.Count;

    /// <summary>Meshes the last upload had to send to the GPU (diagnostics; 0 when <see cref="Stage"/> sent them all beforehand).</summary>
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

    /// <summary>The mesh uploaded for <paramref name="meshPath"/>, if any (nothing is uploaded or counted as used).</summary>
    internal bool TryGetMesh(string meshPath, out MeshHandle handle)
    {
        var found = _meshes.TryGetValue(meshPath, out var entry);
        handle = entry.Handle;
        return found;
    }

    /// <summary>The mesh of <paramref name="asset"/> (keyed by its mesh path), uploaded the first time (from <paramref name="packed"/> vertex data when given).</summary>
    internal MeshHandle Mesh(SceneRenderer renderer, PreparedMeshAsset asset, IReadOnlyDictionary<string, GpuTexture> textures, PreparedMesh? packed = null)
    {
        if (!_meshes.TryGetValue(asset.MeshPath, out var entry))
        {
            var uses = asset.MaterialTextures.Values.Append(asset.TexturePath).OfType<string>().Where(textures.ContainsKey).ToArray();
            entry = (LevelSceneUploader.AddMesh(renderer, asset, textures, packed), uses, 0);
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

    /// <summary>
    /// Counts the meshes <paramref name="nodes"/> draw (and their textures) as used by the current upload: a clone of a level
    /// that went, a pin or a replaced mesh keeps what it draws on the GPU while it is drawn.
    /// </summary>
    internal void KeepDrawn(IEnumerable<SceneNode> nodes)
    {
        var drawn = new HashSet<int>();
        foreach (var node in nodes)
        {
            if (node.Mesh is { } mesh)
            {
                drawn.Add(mesh.Id);
            }
        }

        foreach (var (key, entry) in _meshes.Where(m => m.Value.Used != _generation && drawn.Contains(m.Value.Handle.Id)).ToList())
        {
            _meshes[key] = entry with { Used = _generation };
            Touch(entry.Textures);
        }

        foreach (var (key, entry) in _terrain.Where(t => t.Value.Used != _generation && drawn.Contains(t.Value.Handle.Id)).ToList())
        {
            _terrain[key] = entry with { Used = _generation };
            Touch(entry.Textures);
        }
    }

    /// <summary>
    /// Sends to the GPU what <paramref name="prepared"/> draws and the cache does not hold yet: textures, then meshes (their
    /// vertex data packed on a worker thread), then terrain, until <paramref name="budgetMs"/> is spent. Returns true once
    /// nothing is left, so that showing the scene (<see cref="LevelScene.Update"/>) only builds nodes; call it once a frame
    /// until then. A newer scene replaces the one being staged (what was sent stays cached).
    /// </summary>
    public bool Stage(SceneRenderer renderer, PreparedLevelScene prepared, double budgetMs)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(_staging?.Scene, prepared))
        {
            _staging?.Cancel.Cancel();
            _staging = new Staging(prepared, this);
        }

        var staging = _staging;
        var clock = Stopwatch.StartNew();
        while (staging.Textures.Count > 0)
        {
            if (clock.Elapsed.TotalMilliseconds >= budgetMs)
            {
                return false;
            }

            var (path, image) = staging.Textures.Dequeue();
            if (!_textures.ContainsKey(path))
            {
                _textures[path] = (renderer.CreateTexture(image.Width, image.Height, image.Rgba, image.IsSrgb), _generation);
            }
        }

        while (!staging.Packing.IsCompleted || !staging.Packed.IsEmpty)
        {
            if (clock.Elapsed.TotalMilliseconds >= budgetMs || !staging.Packed.TryDequeue(out var item))
            {
                return false; // out of time, or the worker has not packed the next one yet
            }

            if (!_meshes.ContainsKey(item.Asset.MeshPath))
            {
                var textures = new Dictionary<string, GpuTexture>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in LevelPrepareCache.TexturePathsOf(item.Asset))
                {
                    if (_textures.TryGetValue(path, out var texture))
                    {
                        textures[path] = texture.Texture;
                    }
                }

                var uses = textures.Keys.ToArray();
                _meshes[item.Asset.MeshPath] = (LevelSceneUploader.AddMesh(renderer, item.Asset, textures, item.Packed), uses, _generation);
            }
        }

        while (staging.Terrain.Count > 0)
        {
            if (clock.Elapsed.TotalMilliseconds >= budgetMs)
            {
                return false;
            }

            var component = staging.Terrain.Dequeue();
            if (!_terrain.ContainsKey(component))
            {
                Terrain(component, () => LevelSceneUploader.CreateTerrain(renderer, component, prepared, this));
            }
        }

        return true;
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
        _staging?.Cancel.Cancel();
        _staging = null;
        _meshes.Clear();
        _textures.Clear();
        _terrain.Clear();
    }

    /// <summary>What <see cref="Stage"/> still has to send for one prepared scene.</summary>
    private sealed class Staging
    {
        public Staging(PreparedLevelScene scene, GpuMeshCache cache)
        {
            Scene = scene;
            Textures = new Queue<(string, TextureImage)>(scene.Textures.Where(t => !cache._textures.ContainsKey(t.Key)).Select(t => (t.Key, t.Value)));
            Terrain = new Queue<PreparedTerrain>(scene.Terrain.Where(t => !cache._terrain.ContainsKey(t)));
            var meshes = scene.Meshes.Values.Where(m => !cache._meshes.ContainsKey(m.MeshPath)).ToList();
            var token = Cancel.Token;

            // Packing the vertex data (every LOD interleaved) is the CPU half of a mesh upload: a worker does it while frames go on.
            Packing = meshes.Count == 0 ? Task.CompletedTask : Task.Run(() =>
            {
                foreach (var asset in meshes)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    PreparedMesh? packed = null;
                    try
                    {
                        packed = LevelSceneUploader.Pack(asset);
                    }
                    catch (ArgumentException)
                    {
                        // an invalid mesh: the upload packs it again and reports it as before
                    }

                    Packed.Enqueue((asset, packed));
                }
            }, token);
        }

        public PreparedLevelScene Scene { get; }

        public CancellationTokenSource Cancel { get; } = new();

        public Queue<(string Path, TextureImage Image)> Textures { get; }

        public Queue<PreparedTerrain> Terrain { get; }

        public ConcurrentQueue<(PreparedMeshAsset Asset, PreparedMesh? Packed)> Packed { get; } = new();

        public Task Packing { get; }
    }
}
