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
/// with a cache do not own these meshes; <see cref="Clear"/> frees everything before the GL context goes.
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

    // The impostor atlas of a cached mesh (only that mesh draws with it): freed with the mesh.
    private readonly Dictionary<string, GpuTexture> _atlases = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _taken;
    private int _generation;
    private Staging? _staging;

    /// <summary>Meshes on the GPU (diagnostics).</summary>
    public int MeshCount => _meshes.Count;

    /// <summary>Meshes the last upload had to send to the GPU (diagnostics; 0 when <see cref="Stage"/> sent them all beforehand).</summary>
    public int LastUploaded { get; private set; }

    /// <summary>The impostor pictures <see cref="Stage"/> gives the trees and bushes of a scene (set its folder to keep them on disk).</summary>
    public ImpostorCache Impostors { get; } = new();

    /// <summary>Cached meshes drawing an impostor past their last LOD (diagnostics).</summary>
    public int ImpostorCount => _atlases.Count;

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
            entry = (TextureUpload.Create(renderer, image), 0);
        }
        else if (image.Width > entry.Texture.Width)
        {
            entry = entry with { Texture = Sharpen(renderer, entry.Texture, image) };
        }

        _textures[path] = entry with { Used = _generation };
        _taken?.Add(path);
        return entry.Texture;
    }

    /// <summary>
    /// The cached texture of <paramref name="path"/>, counted as used by the current upload (a mesh the scene owns draws
    /// with it), or null when the cache does not hold it.
    /// </summary>
    internal GpuTexture? Keep(string path)
    {
        if (!_textures.TryGetValue(path, out var entry))
        {
            return null;
        }

        _textures[path] = entry with { Used = _generation };
        return entry.Texture;
    }

    /// <summary>
    /// A sharper image of a texture already on the GPU (the texture quality went up): every mesh drawing with the old copy
    /// switches to the new one, so a cached mesh never keeps showing the blurrier texture (owner: "sometimes it shows me
    /// the weaker graphics").
    /// </summary>
    private static GpuTexture Sharpen(SceneRenderer renderer, GpuTexture old, TextureImage image)
    {
        old.TakeOver(TextureUpload.Create(renderer, image)); // the same object everywhere, now sharp
        return old;
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
    /// that went, a pin or a replaced mesh keeps what it draws on the GPU while it is drawn. So do <paramref name="putBack"/>,
    /// the meshes a bend or a replaced mesh gives back to its node when undone.
    /// </summary>
    internal void KeepDrawn(IEnumerable<SceneNode> nodes, IEnumerable<MeshHandle> putBack)
    {
        var drawn = putBack.Select(m => m.Id).ToHashSet();
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
    /// vertex data packed on a worker thread), then terrain, then the impostors of its trees (<see cref="Impostors"/>), until
    /// <paramref name="budgetMs"/> is spent. Returns true once nothing is left, so that showing the scene
    /// (<see cref="LevelScene.Update"/>) only builds nodes; call it once a frame until then. A newer scene replaces the one
    /// being staged (what was sent stays cached).
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
            if (!_textures.TryGetValue(path, out var known))
            {
                _textures[path] = (TextureUpload.Create(renderer, image), _generation);
            }
            else if (image.Width > known.Texture.Width)
            {
                _textures[path] = known with { Texture = Sharpen(renderer, known.Texture, image) };
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

                // The mesh lives as long as its textures: a cached texture it takes counts as used now too. A scene staged and
                // then passed over for a newer one left its meshes cached with textures that the next Trim freed, and the
                // scene that came back to them threw "Cannot access a disposed object".
                var uses = textures.Keys.ToArray();
                _meshes[item.Asset.MeshPath] = (LevelSceneUploader.AddMesh(renderer, item.Asset, textures, item.Packed), uses, _generation);
                Touch(uses);
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

        // Trees and bushes get their impostor card: found in memory or on disk, or baked, one bake a call (eight small
        // renders and read-backs, once per mesh and session).
        if (!staging.Candidates.IsCompleted)
        {
            return false;
        }

        var baked = false;
        string[] candidates = staging.Candidates.IsCompletedSuccessfully ? staging.Candidates.Result : [];
        foreach (var path in candidates.AsSpan(Math.Min(staging.NextCandidate, candidates.Length)))
        {
            if (baked || clock.Elapsed.TotalMilliseconds >= budgetMs)
            {
                return false;
            }

            staging.NextCandidate++;
            if (!_atlases.ContainsKey(path) && _meshes.TryGetValue(path, out var entry)
                && Impostors.Give(renderer, path, entry.Handle, out baked) is { } atlas)
            {
                _atlases[path] = atlas;
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
            if (_atlases.Remove(key, out var atlas))
            {
                atlas.Dispose();
            }
        }

        foreach (var (key, entry) in _textures.Where(t => t.Value.Used < oldest).ToList())
        {
            entry.Texture.Dispose();
            _textures.Remove(key);
        }
    }

    /// <summary>
    /// Deletes every cached texture (meshes', terrain ground and layer weights) and forgets every entry, with the GL context
    /// still current and before the renderer, which deletes the meshes, is disposed (the viewport's GL de-init). The context
    /// shares its objects with the window's, so a texture only forgotten stayed in video memory at every pop-out and dock-back.
    /// </summary>
    public void Clear()
    {
        _staging?.Cancel.Cancel();
        _staging = null;
        foreach (var entry in _textures.Values)
        {
            entry.Texture.Dispose();
        }

        foreach (var entry in _terrain.Values)
        {
            entry.Texture?.Dispose();
            entry.Weights?.Dispose();
        }

        foreach (var atlas in _atlases.Values)
        {
            atlas.Dispose();
        }

        _atlases.Clear();
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

            // A texture the cache holds blurrier (the quality went up) is sharpened here too, a few a frame, not all in the
            // frame that shows the scene (owner: "the app freezes as I fly").
            Textures = new Queue<(string, TextureImage)>(scene.Textures
                .Where(t => !cache._textures.TryGetValue(t.Key, out var known) || t.Value.Width > known.Texture.Width)
                .Select(t => (t.Key, t.Value)));
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

            // The meshes instanced placements (foliage, HISM) draw, found on a worker: a level holds 100,000 placements.
            Candidates = Task.Run(() =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var candidates = new List<string>();
                foreach (var placement in scene.Placements)
                {
                    if (placement.Instance is not null && seen.Add(placement.MeshPath)
                        && scene.Meshes.TryGetValue(placement.MeshPath, out var asset) && ImpostorCache.IsCandidate(asset))
                    {
                        candidates.Add(placement.MeshPath);
                    }
                }

                return candidates.ToArray();
            }, token);
        }

        public PreparedLevelScene Scene { get; }

        public CancellationTokenSource Cancel { get; } = new();

        public Queue<(string Path, TextureImage Image)> Textures { get; }

        public Queue<PreparedTerrain> Terrain { get; }

        public ConcurrentQueue<(PreparedMeshAsset Asset, PreparedMesh? Packed)> Packed { get; } = new();

        public Task Packing { get; }

        public Task<string[]> Candidates { get; }

        public int NextCandidate { get; set; }
    }
}
