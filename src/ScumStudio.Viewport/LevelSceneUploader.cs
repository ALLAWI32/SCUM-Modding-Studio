using ScumStudio.Assets.Landscape;
using ScumStudio.Assets.Textures;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Viewport;

/// <summary>A level scene living on the GPU: the scene graph, its bounds and the id → placement lookup for picking.</summary>
public sealed class LevelScene : IDisposable
{
    /// <summary>Where the upload (or the last <see cref="Update"/>) of this scene spent its time.</summary>
    public UploadTimings? UploadTimings { get; internal set; }

    /// <summary>The cache that owns this scene's meshes and terrain (null: the scene owns them).</summary>
    internal GpuMeshCache? Cache { get; }

    private readonly SceneRenderer _renderer;
    private readonly LevelUploadOptions _options;
    private readonly List<MeshHandle> _handles = []; // owned by the scene (not the cache's)
    private readonly List<GpuTexture> _textures = []; // owned by the scene (not the cache's)
    private readonly HashSet<string> _ownedMeshPaths = new(StringComparer.OrdinalIgnoreCase);

    // The cache's textures that meshes the scene owns draw with (added meshes, bent and reshaped copies, pins), by mesh id:
    // no cached mesh may use them any more, so each update keeps them (owner: "Cannot access a disposed object" after a paste).
    private readonly Dictionary<int, string[]> _borrowed = [];
    private Dictionary<string, MeshHandle> _meshHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, List<ScenePlacement>> _byId = [];
    private readonly Dictionary<uint, List<(SceneNode Node, ScenePlacement? Source)>> _clones = [];
    private readonly Dictionary<uint, (uint SourceId, string? MeshPath, IReadOnlyList<ScenePlacement>? Placements)> _cloneSources = [];
    private Dictionary<uint, List<SceneNode>>? _nodesById;
    private readonly HashSet<uint> _movedActors = [];
    private Dictionary<string, GpuTexture> _gpuTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PreparedMeshAsset> _extraAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, (MeshHandle Bent, SceneNode Node, MeshHandle Straight, IReadOnlyList<SplineMeshParams> Spline)> _bent = [];
    private readonly HashSet<InstanceKey> _movedInstances = [];
    private readonly HashSet<InstanceKey> _addedInstances = [];
    private Dictionary<InstanceKey, List<SceneNode>>? _instanceNodes;
    private List<TerrainPart> _terrainParts = [];
    private SceneNode? _seaNode;

    // The nodes of each level (by its slot, see PreparedLevelScene.Slots) under a group node of their own: a level that stays
    // loaded keeps its nodes, one that goes takes them along.
    private readonly Dictionary<int, LevelGroup> _groups = [];

    internal LevelScene(SceneRenderer renderer, PreparedLevelScene prepared, LevelUploadOptions options, GpuMeshCache? cache)
    {
        _renderer = renderer;
        _options = options;
        Cache = cache;
        Prepared = prepared;
    }

    /// <summary>One level's nodes: the group under the root, the document and placements it was built from, its terrain.</summary>
    private sealed record LevelGroup(SceneNode Node, LevelDocument Document, List<ScenePlacement> Placements, BoundingBox Bounds, int Placed, List<TerrainPart> Terrain);

    /// <summary>True when the scene holds shimmering stand-ins (<see cref="PreparedMeshAsset.Shimmer"/>): frames must keep coming for the pulse.</summary>
    public bool HasShimmer { get; private set; }

    /// <summary>Selectable ids of actors currently drawn at an overridden transform (see <see cref="SetActorTransform"/>).</summary>
    public IReadOnlyCollection<uint> MovedActors => _movedActors;

    /// <summary>Selectable ids of the clones currently in the scene (see <see cref="AddClone"/>).</summary>
    public IReadOnlyCollection<uint> CloneIds => _clones.Keys;

    /// <summary>True when a mesh with this path is uploaded (prepared with the scene or added with <see cref="AddMesh"/>).</summary>
    public bool HasMesh(string meshPath) => _meshHandles.ContainsKey(meshPath);

    /// <summary>Uploads a mesh loaded after the scene was prepared (render thread), so clones can draw it.</summary>
    public void AddMesh(ExtraMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (_meshHandles.ContainsKey(mesh.Asset.MeshPath))
        {
            return;
        }

        foreach (var (path, image) in mesh.Textures)
        {
            if (!_gpuTextures.ContainsKey(path))
            {
                var texture = TextureUpload.Create(_renderer, image);
                _gpuTextures[path] = texture;
                _textures.Add(texture);
            }
        }

        var handle = AddOwnedMesh(mesh.Asset);
        _handles.Add(handle);
        _meshHandles[mesh.Asset.MeshPath] = handle;
        _extraAssets[mesh.Asset.MeshPath] = mesh.Asset;
        _ownedMeshPaths.Add(mesh.Asset.MeshPath);
        HasShimmer |= mesh.Asset.Shimmer; // a copied spawner's stand-in in a scene that had none keeps the pulse going
    }

    /// <summary>
    /// Draws the single-mesh actor (or clone) <paramref name="selectableId"/> bent along <paramref name="spline"/> in its
    /// component space, the way the game draws a <c>SplineMeshActor</c>; null puts the straight mesh back. The node keeps
    /// its id and transform (the caller gives bent actors their root at scale 1: the scale is in the curve), so picking,
    /// selection and moving work as before. Each call replaces the previous bent copy on the GPU.
    /// </summary>
    public void SetBend(uint selectableId, IReadOnlyList<SplineMeshParams>? spline)
    {
        if (_bent.TryGetValue(selectableId, out var current))
        {
            if (spline is not null && current.Spline.SequenceEqual(spline) && ReferenceEquals(current.Node.Mesh, current.Bent) && current.Node.Parent is not null)
            {
                return; // already drawn like this
            }

            if (ReferenceEquals(current.Node.Mesh, current.Bent))
            {
                current.Node.Mesh = current.Straight;
            }

            RemoveOwnedMesh(current.Bent);
            _bent.Remove(selectableId);
        }

        if (spline is null)
        {
            return;
        }

        var nodes = NodesOf(selectableId).Where(n => n.Mesh is not null).Take(2).ToList();
        if (nodes.Count != 1)
        {
            return; // not drawn, or more than one mesh: only single-mesh actors bend
        }

        var node = nodes[0];
        var path = _meshHandles.FirstOrDefault(m => m.Value.Id == node.Mesh!.Id).Key;
        if (path is null || !(Prepared.Meshes.TryGetValue(path, out var asset) || _extraAssets.TryGetValue(path, out asset)))
        {
            return;
        }

        var lods = asset.Lods.Select(l => SplineMeshDeformer.DeformPieces(l, spline)).ToList();
        var bent = AddOwnedMesh(asset with { MeshPath = path + "#bent", Mesh = lods[0], Lods = lods });
        _bent[selectableId] = (bent, node, node.Mesh!, spline);
        node.Mesh = bent;
    }

    /// <summary>Ids drawn bent (see <see cref="SetBend"/>).</summary>
    public IReadOnlyCollection<uint> BentIds => _bent.Keys;

    private readonly Dictionary<InstanceKey, (MeshHandle Bent, SceneNode Node, MeshHandle Straight, SplineMeshParams Spline, string? Mesh)> _bentSegments = [];

    /// <summary>Road, rail and bridge pieces drawn with a new curve (see <see cref="SetSegmentBend"/>).</summary>
    public IReadOnlyCollection<InstanceKey> BentSegments => _bentSegments.Keys;

    /// <summary>
    /// Draws the spline piece <paramref name="key"/> along <paramref name="spline"/> instead of the curve the level gives
    /// it (null: as the level has it again). The straight mesh comes from <see cref="PreparedLevelScene.SplineSources"/>,
    /// or is <paramref name="meshPath"/> (a replaced piece: a mesh added with <see cref="AddMesh"/> or prepared with the scene).
    /// </summary>
    public void SetSegmentBend(InstanceKey key, SplineMeshParams? spline, string? meshPath = null)
    {
        if (_bentSegments.TryGetValue(key, out var current))
        {
            if (spline is not null && current.Spline == spline && string.Equals(current.Mesh, meshPath, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(current.Node.Mesh, current.Bent))
            {
                return;
            }

            if (ReferenceEquals(current.Node.Mesh, current.Bent))
            {
                current.Node.Mesh = current.Straight;
            }

            RemoveOwnedMesh(current.Bent);
            _bentSegments.Remove(key);
        }

        var node = InstanceNodes().GetValueOrDefault(key)?[0];
        if (spline is null || node?.Tag is not ScenePlacement placement || node.Mesh is not { } shown)
        {
            return;
        }

        PreparedMeshAsset? straight;
        if (meshPath is not null)
        {
            if (!(_extraAssets.TryGetValue(meshPath, out straight) || Prepared.Meshes.TryGetValue(meshPath, out straight) || Prepared.SplineSources.TryGetValue(meshPath, out straight)))
            {
                return; // not loaded yet: again when it arrives
            }
        }
        else
        {
            var marker = placement.MeshPath.IndexOf(SplineMeshPlacements.KeyMarker, StringComparison.Ordinal);
            if (marker <= 0 || !Prepared.SplineSources.TryGetValue(placement.MeshPath[..marker], out straight))
            {
                return;
            }
        }

        var lods = straight.Lods.Select(l => SplineMeshDeformer.Deform(l, spline, straight.Mesh.Bounds)).ToList();
        var bent = AddOwnedMesh(straight with { MeshPath = placement.MeshPath + "#reshaped", Mesh = lods[0], Lods = lods });
        _bentSegments[key] = (bent, node, shown, spline, meshPath);
        node.Mesh = bent;
    }

    private readonly Dictionary<uint, (List<(SceneNode Node, MeshHandle Own)> Nodes, string Mesh)> _swappedActors = [];
    private readonly Dictionary<InstanceKey, (List<(SceneNode Node, MeshHandle Own)> Nodes, string Mesh)> _swappedParts = [];

    /// <summary>
    /// Draws the root mesh of the actor <paramref name="selectableId"/> as <paramref name="meshPath"/> (the Replace tool;
    /// a mesh added with <see cref="AddMesh"/> or prepared with the scene), null as its own again. The node keeps its id
    /// and transform; a bend over it is dropped (the caller sets it again over the new mesh).
    /// </summary>
    public void SetActorMesh(uint selectableId, string? meshPath) =>
        Swap(_swappedActors, selectableId, meshPath, () => NodesOf(selectableId).Where(n => n.Mesh is not null && n.Tag is ScenePlacement { InstanceKey: null }).ToList(), () => SetBend(selectableId, null));

    /// <summary>Draws the part (a stored component of a Blueprint building) <paramref name="key"/> as <paramref name="meshPath"/>, null as its own again.</summary>
    public void SetPartMesh(InstanceKey key, string? meshPath) =>
        Swap(_swappedParts, key, meshPath, () => InstanceNodes().GetValueOrDefault(key)?.Where(n => n.Mesh is not null).ToList() ?? [], null);

    /// <summary>
    /// Draws the actors of <paramref name="actors"/> and the parts of <paramref name="parts"/> with the mesh each is
    /// replaced with (the Replace tool; a road piece's is drawn by <see cref="SetSegmentBend"/>), and every other swapped
    /// one as its own again. A mesh not uploaded yet is swapped in by the next call once it is.
    /// </summary>
    public void SetMeshes(IReadOnlyDictionary<uint, string>? actors, IReadOnlyDictionary<InstanceKey, string>? parts)
    {
        foreach (var id in _swappedActors.Keys.Where(id => actors is null || !actors.ContainsKey(id)).ToList())
        {
            SetActorMesh(id, null);
        }

        foreach (var (id, mesh) in actors ?? new Dictionary<uint, string>())
        {
            SetActorMesh(id, mesh);
        }

        foreach (var key in _swappedParts.Keys.Where(k => parts is null || !parts.ContainsKey(k)).ToList())
        {
            SetPartMesh(key, null);
        }

        foreach (var (key, mesh) in parts ?? new Dictionary<InstanceKey, string>())
        {
            if (key.InstanceIndex == InstanceKey.Part)
            {
                SetPartMesh(key, mesh);
            }
        }
    }

    private void Swap<TKey>(Dictionary<TKey, (List<(SceneNode Node, MeshHandle Own)> Nodes, string Mesh)> swaps, TKey key, string? meshPath, Func<List<SceneNode>> nodesOf, Action? unbend)
        where TKey : notnull
    {
        if (swaps.TryGetValue(key, out var current))
        {
            if (string.Equals(current.Mesh, meshPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            unbend?.Invoke();
            foreach (var (node, own) in current.Nodes)
            {
                node.Mesh = own;
            }

            swaps.Remove(key);
        }

        if (meshPath is null || !_meshHandles.TryGetValue(meshPath, out var handle))
        {
            return; // its own mesh, or not loaded yet: again when it arrives
        }

        var nodes = nodesOf();
        if (nodes.Count == 0)
        {
            return;
        }

        unbend?.Invoke();
        swaps[key] = (nodes.Select(n => (n, n.Mesh!)).ToList(), meshPath);
        foreach (var node in nodes)
        {
            node.Mesh = handle;
        }
    }

    /// <summary>Approximate GPU memory of the scene's textures (meshes and terrain) in bytes.</summary>
    public long TextureBytes => _textures.Sum(t => t.EstimatedBytes);

    /// <summary>Number of textures uploaded for the scene (meshes and terrain).</summary>
    public int TextureCount => _textures.Count;

    /// <summary>
    /// Draws every placement of the actor <paramref name="selectableId"/> as if its root component were at
    /// <paramref name="rootWorld"/> (UE world space): each component keeps its offset from the pristine root.
    /// </summary>
    public void SetActorTransform(uint selectableId, FTransform rootWorld)
    {
        foreach (var node in NodesOf(selectableId))
        {
            if (node.Tag is ScenePlacement placement)
            {
                node.LocalTransform = UeToGl.ModelMatrix(placement.World.GetRelativeTransform(placement.Actor.WorldTransform) * rootWorld);
            }
        }

        _movedActors.Add(selectableId);
    }

    /// <summary>Puts the actor's placements back where the level stores them.</summary>
    public void ResetActorTransform(uint selectableId)
    {
        foreach (var node in NodesOf(selectableId))
        {
            if (node.Tag is ScenePlacement placement)
            {
                node.LocalTransform = placement.GlModel;
            }
        }

        _movedActors.Remove(selectableId);
    }

    /// <summary>
    /// The nodes drawing <paramref name="selectableId"/> (owner: a thousand moved objects froze the app for seconds, each
    /// looked up by walking every node of the scene).
    /// </summary>
    private List<SceneNode> NodesOf(uint selectableId)
    {
        if (_nodesById is null)
        {
            _nodesById = [];
            foreach (var node in Scene.Nodes)
            {
                if (node.SelectableId != 0)
                {
                    Index(node);
                }
            }
        }

        return _nodesById.TryGetValue(selectableId, out var nodes) ? nodes : [];
    }

    private void Index(SceneNode node)
    {
        NodesBuilt++;
        if (_nodesById is null)
        {
            return; // built on first use
        }

        if (!_nodesById.TryGetValue(node.SelectableId, out var list))
        {
            _nodesById[node.SelectableId] = list = [];
        }

        list.Add(node);
    }

    /// <summary>
    /// Adds (or moves) a clone of the actor <paramref name="sourceId"/> under the id <paramref name="cloneId"/>, with its
    /// root at <paramref name="rootWorld"/>; the clone's nodes carry placements with the new id, so hiding and picking work.
    /// Returns the number of nodes the clone has (0 when the source has no drawable placement). With
    /// <paramref name="placements"/> (an actor of a level that is not loaded) those are cloned instead of the source's.
    /// </summary>
    public int AddClone(uint cloneId, uint sourceId, FTransform rootWorld, string name, string? meshPath = null, IReadOnlyList<ScenePlacement>? placements = null)
    {
        if (_clones.TryGetValue(cloneId, out var drawn) && _cloneSources.GetValueOrDefault(cloneId) == (sourceId, meshPath, placements))
        {
            // The same copy somewhere else (a move, or every copy again after a drag): its nodes move, nothing is rebuilt.
            var moved = new List<ScenePlacement>(drawn.Count);
            foreach (var (node, source) in drawn)
            {
                if (source is null)
                {
                    node.LocalTransform = UeToGl.ModelMatrix(rootWorld);
                    node.Name = name;
                    continue;
                }

                var placement = CopyOf(source, cloneId, rootWorld, name);
                node.LocalTransform = placement.GlModel;
                node.Name = placement.Name;
                node.Tag = placement;
                moved.Add(placement);
            }

            if (sourceId != 0 || placements is not null)
            {
                _byId[cloneId] = moved;
            }

            return drawn.Count;
        }

        RemoveClone(cloneId);
        if (sourceId == 0 && placements is null)
        {
            // A new mesh actor: one node drawing the mesh at the root.
            if (meshPath is null || !_meshHandles.TryGetValue(meshPath, out var meshHandle))
            {
                return 0;
            }

            var meshNode = Scene.Add(meshHandle, UeToGl.ModelMatrix(rootWorld), cloneId, name);
            _clones[cloneId] = [(meshNode, null)];
            _cloneSources[cloneId] = (sourceId, meshPath, null);
            Index(meshNode);
            return 1;
        }

        if ((placements ?? _byId.GetValueOrDefault(sourceId)) is not { } sources)
        {
            return 0;
        }

        var nodes = new List<(SceneNode Node, ScenePlacement? Source)>();
        var copies = new List<ScenePlacement>();
        foreach (var source in sources)
        {
            if (!_meshHandles.TryGetValue(source.MeshPath, out var handle))
            {
                continue;
            }

            var placement = CopyOf(source, cloneId, rootWorld, name);
            var node = Scene.Add(handle, placement.GlModel, cloneId, placement.Name);
            node.Tag = placement;
            node.MaxDrawDistance = LevelSceneUploader.DrawDistanceOf(placement);
            nodes.Add((node, source));
            copies.Add(placement);
            Index(node);
        }

        _clones[cloneId] = nodes;
        _cloneSources[cloneId] = (sourceId, meshPath, placements);
        _byId[cloneId] = copies;
        return nodes.Count;
    }

    /// <summary>The placement of a copy of <paramref name="source"/> whose actor root is at <paramref name="rootWorld"/>.</summary>
    private static ScenePlacement CopyOf(ScenePlacement source, uint cloneId, FTransform rootWorld, string name)
    {
        var world = source.World.GetRelativeTransform(source.Actor.WorldTransform) * rootWorld;
        var slash = source.Name.IndexOf('/');
        return source with { GlModel = UeToGl.ModelMatrix(world), World = world, SelectableId = cloneId, Name = slash < 0 ? name : name + source.Name[slash..] };
    }

    /// <summary>Moves a clone's root to <paramref name="rootWorld"/> without rebuilding its nodes (drag previews).</summary>
    public void SetCloneTransform(uint cloneId, FTransform rootWorld)
    {
        if (!_clones.TryGetValue(cloneId, out var nodes))
        {
            return;
        }

        foreach (var (node, source) in nodes)
        {
            node.LocalTransform = UeToGl.ModelMatrix(source is null ? rootWorld : source.World.GetRelativeTransform(source.Actor.WorldTransform) * rootWorld);
        }
    }

    /// <summary>Removes a clone added with <see cref="AddClone"/> (no-op for unknown ids).</summary>
    public void RemoveClone(uint cloneId)
    {
        if (_clones.Remove(cloneId, out var nodes))
        {
            foreach (var (node, _) in nodes)
            {
                node.Parent?.Remove(node);
            }

            _byId.Remove(cloneId);
            _cloneSources.Remove(cloneId);
            _nodesById?.Remove(cloneId);
        }
    }

    /// <summary>The CPU-side data this scene was built from.</summary>
    public PreparedLevelScene Prepared { get; private set; }

    /// <summary>The renderable scene graph.</summary>
    public Scene Scene { get; } = new();

    /// <summary>GL-space bounds of everything drawn (meshes and terrain).</summary>
    public BoundingBox Bounds { get; private set; }

    /// <summary>GL-space bounds of the terrain only (empty without terrain).</summary>
    public BoundingBox TerrainBounds { get; private set; }

    /// <summary>Placements per selectable id (an actor with several components/instances has several placements).</summary>
    public IReadOnlyDictionary<uint, List<ScenePlacement>> PlacementsById => _byId;

    /// <summary>Mesh placements drawn.</summary>
    public int PlacedCount { get; private set; }

    /// <summary>Ground queries over the scene's terrain (null without terrain): height, normal, layers, raycast.</summary>
    public TerrainHeightField? HeightField => Prepared.HeightField;

    /// <summary>The ground mode the terrain is currently drawn with.</summary>
    public GroundMode Ground { get; private set; }

    /// <summary>True when the scene has a sea plane (see <see cref="SeaVisible"/>).</summary>
    public bool HasSea => _seaNode is not null;

    /// <summary>Shows or hides the sea plane (no-op without one).</summary>
    public bool SeaVisible
    {
        get => _seaNode?.Visible ?? false;
        set
        {
            if (_seaNode is { } sea)
            {
                sea.Visible = value;
            }
        }
    }

    /// <summary>The scene nodes drawing terrain components, in <see cref="PreparedLevelScene.Terrain"/> order.</summary>
    public IReadOnlyList<SceneNode> TerrainNodes => _terrainParts.Select(p => p.Node).ToList();

    /// <summary>The terrain components drawn: node, GPU mesh and the prepared component (render thread).</summary>
    internal IReadOnlyList<TerrainPart> TerrainParts => _terrainParts;

    /// <summary>
    /// For a coarse whole-island backdrop: hides the landscape tiles <paramref name="detailed"/> draws at full resolution,
    /// so each tile is drawn once, as in the game (both drawn, the coarse one pokes through the detailed one in patches).
    /// When <paramref name="detailed"/> has a sea, the backdrop's is hidden too: the detailed scene is drawn second, so its
    /// sea (wide enough for the whole island) comes after all the ground of both and is the one sea of the view. The
    /// backdrop's, drawn first, was painted over by the detailed seabed ("the water turns into empty blue ground").
    /// The game's far model of a level (<see cref="FarModelsScene"/>) gives way to the level itself in the same frame.
    /// </summary>
    public void HideTerrainOf(PreparedLevelScene? detailed)
    {
        var names = detailed?.Terrain.Select(t => t.LevelName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in TerrainNodes)
        {
            node.Visible = names is null || !names.Contains(node.Name[..Math.Max(0, node.Name.IndexOf('/'))]);
        }

        var levels = detailed?.Documents.Select(d => d.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _groups.Values)
        {
            foreach (var node in group.Node.Children)
            {
                if (node.Tag is ScenePlacement placement && FarModelsScene.LevelOf(placement.Actor) is { } level)
                {
                    node.Visible = levels is null || !levels.Contains(level);
                }
            }
        }

        SeaVisible = detailed?.SeaLevelCm is null;
    }

    /// <summary>
    /// Replaces the terrain textures (render thread), e.g. with <see cref="LevelScenePreparer.BakeTerrain"/> output for
    /// another <see cref="GroundMode"/>; <paramref name="albedo"/> is index-aligned with <see cref="PreparedLevelScene.Terrain"/>
    /// and null entries draw the plain tint.
    /// </summary>
    public void SetTerrainAlbedo(IReadOnlyList<TerrainAlbedo?> albedo, GroundMode mode)
    {
        ArgumentNullException.ThrowIfNull(albedo);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        for (var i = 0; i < _terrainParts.Count; i++)
        {
            var part = _terrainParts[i];

            // A cached component is about to change: this scene takes it over (the cache must not hand out a mesh that now
            // draws another scene's texture).
            if (Cache?.ReleaseTerrain(Prepared.Terrain[i]) is { } owned)
            {
                _handles.Add(owned.Handle);
                _textures.AddRange(new[] { owned.Texture, owned.Weights }.OfType<GpuTexture>());
            }

            var image = i < albedo.Count ? albedo[i] : null;
            var texture = image is null ? null : LevelSceneUploader.CreateTerrainTexture(_renderer, image);
            _renderer.SetMeshTexture(part.Handle, texture);
            part.Node.Tint = texture is null ? LevelSceneUploader.TerrainTint : Vector4.One;
            if (part.Texture is { } old && _textures.Remove(old))
            {
                old.Dispose(); // only what this scene owns
            }

            if (texture is not null)
            {
                _textures.Add(texture);
            }

            _terrainParts[i] = part with { Texture = texture };
        }

        Ground = mode;
    }


    /// <summary>
    /// Shows <paramref name="next"/> instead of <see cref="Prepared"/> without starting over (render thread, GL context
    /// current; only for a scene uploaded with a <see cref="GpuMeshCache"/>): a level both scenes have (the same document in
    /// the same slot, see <see cref="PreparedLevelScene.Slots"/>, drawn the same) keeps its nodes and whatever was done to
    /// them (moves, selection, visibility, bends, pins); a level that is gone takes its nodes along; only a new level gets
    /// nodes. Meshes and textures come from the cache (<see cref="GpuMeshCache.Stage"/> sends new ones over several frames
    /// beforehand) and the new levels' nodes from <see cref="Prebuild"/> when it ran. Clones stay; the caller applies its
    /// edit state again as after an upload.
    /// </summary>
    public void Update(PreparedLevelScene next)
    {
        ArgumentNullException.ThrowIfNull(next);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Cache is null)
        {
            throw new InvalidOperationException("Only a scene uploaded with a GpuMeshCache can be updated.");
        }

        Apply(next);
    }

    /// <summary>
    /// Builds, on a worker thread, the nodes of the levels <paramref name="next"/> brings that this scene does not show yet;
    /// true once they are ready, so that <see cref="Update"/> only hangs them in. Call it once a frame after
    /// <see cref="GpuMeshCache.Stage"/> returned true for <paramref name="next"/> (its meshes must be on the GPU).
    /// </summary>
    public bool Prebuild(PreparedLevelScene next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (Cache is not { } cache)
        {
            return true;
        }

        if (!ReferenceEquals(_plan?.Next, next))
        {
            var handles = new Dictionary<string, MeshHandle>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in next.Meshes.Keys)
            {
                if (cache.TryGetMesh(path, out var handle))
                {
                    handles[path] = handle;
                }
            }

            var groups = new Dictionary<int, LevelGroup>(_groups);
            _plan = (next, Task.Run(() => Plan(next, groups, handles)));
        }

        return _plan.Value.Task.IsCompleted;
    }

    private (PreparedLevelScene Next, Task<UpdatePlan> Task)? _plan;

    /// <summary>Builds (first call) or updates the scene to draw <paramref name="next"/>; see <see cref="Update"/>.</summary>
    internal void Apply(PreparedLevelScene next)
    {
        var clock = Stopwatch.StartNew();
        Cache?.Begin();

        // The new scene's textures and meshes (from the cache: a hit sends nothing), and those the scene owns (added meshes, pins).
        var gpuTextures = new Dictionary<string, GpuTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, image) in next.Textures)
        {
            gpuTextures[path] = Cache?.Texture(_renderer, path, image) ?? Own(TextureUpload.Create(_renderer, image));
        }

        foreach (var (path, texture) in _gpuTextures)
        {
            if (_textures.Contains(texture))
            {
                gpuTextures.TryAdd(path, texture);
            }
        }

        foreach (var path in _borrowed.Values.SelectMany(p => p))
        {
            if (Cache?.Keep(path) is { } kept)
            {
                gpuTextures.TryAdd(path, kept);
            }
        }

        _gpuTextures = gpuTextures;
        var texturesMs = clock.Elapsed.TotalMilliseconds;
        var meshHandles = new Dictionary<string, MeshHandle>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, asset) in next.Meshes)
        {
            meshHandles[path] = Cache?.Mesh(_renderer, asset, gpuTextures) ?? Own(LevelSceneUploader.AddMesh(_renderer, asset, gpuTextures));
        }

        var meshesMs = clock.Elapsed.TotalMilliseconds - texturesMs;

        // Which levels stay and the nodes of the new ones: worked out by Prebuild on a worker when it ran for this scene.
        var plan = _plan is { } prebuilt && ReferenceEquals(prebuilt.Next, next) && prebuilt.Task.IsCompletedSuccessfully
            ? prebuilt.Task.Result
            : Plan(next, _groups, meshHandles);
        _plan = null;
        foreach (var path in _ownedMeshPaths)
        {
            if (_meshHandles.TryGetValue(path, out var owned))
            {
                meshHandles.TryAdd(path, owned);
            }
        }

        _meshHandles = meshHandles;
        foreach (var slot in _groups.Keys.Where(s => !plan.Keep.Contains(s)).ToList())
        {
            RemoveGroup(slot);
        }

        var terrainMs = 0.0;
        var layerTextures = new Dictionary<string, GpuTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, document) in plan.Wanted)
        {
            if (!_groups.ContainsKey(slot))
            {
                var placements = plan.Placements.GetValueOrDefault(slot) ?? [];
                var built = plan.Built.GetValueOrDefault(slot) is { } ready && ready.Matches(meshHandles) ? ready : Build(document, placements, meshHandles);
                terrainMs += AddGroup(slot, document, placements, built, plan.Terrain.GetValueOrDefault(slot) ?? [], next, layerTextures);
            }
        }

        // Terrain parts in the new scene's order (a part SetTerrainAlbedo changed is the current one).
        var parts = new Dictionary<PreparedTerrain, TerrainPart>(ReferenceEqualityComparer.Instance);
        foreach (var part in _groups.Values.SelectMany(g => g.Terrain))
        {
            parts[part.Source] = part;
        }

        foreach (var part in _terrainParts.Where(p => parts.ContainsKey(p.Source)))
        {
            parts[part.Source] = part;
        }

        _terrainParts = next.Terrain.Where(parts.ContainsKey).Select(t => parts[t]).ToList();
        var terrainBounds = BoundingBox.Empty;
        foreach (var part in _terrainParts)
        {
            terrainBounds = terrainBounds.Union(part.Handle.Bounds);
        }

        UpdateSea(terrainBounds, next.SeaLevelCm);
        TerrainBounds = terrainBounds;
        Scene.Environment = !terrainBounds.IsEmpty && _options.OutdoorEnvironment ? SceneEnvironment.Outdoor(_options.TerrainGrid, terrainBounds.Min.Y - 100f) : null;
        var bounds = terrainBounds;
        foreach (var group in _groups.Values)
        {
            bounds = bounds.Union(group.Bounds);
        }

        Bounds = bounds;
        PlacedCount = _groups.Values.Sum(g => g.Placed);
        HasShimmer = next.Meshes.Values.Any(m => m.Shimmer) || _extraAssets.Values.Any(m => m.Shimmer);
        Ground = next.Ground;
        Prepared = next;
        Cache?.KeepDrawn(Scene.Nodes, PutBack()); // what clones, pins and swapped meshes draw stays on the GPU too
        var nodesMs = clock.Elapsed.TotalMilliseconds - texturesMs - meshesMs - terrainMs;
        UploadTimings = new UploadTimings(texturesMs, meshesMs, nodesMs, terrainMs, Cache?.LastUploaded ?? meshHandles.Count);
    }

    // The group of terrain components whose level is not among the documents.
    private const int OtherTerrain = int.MinValue;

    /// <summary>What an update to a prepared scene changes: each level's placements and terrain by slot, the slots whose groups stay, the new groups' nodes.</summary>
    private sealed record UpdatePlan(PreparedLevelScene Next, Dictionary<int, List<ScenePlacement>> Placements, Dictionary<int, List<PreparedTerrain>> Terrain,
        Dictionary<int, LevelDocument?> Wanted, HashSet<int> Keep, Dictionary<int, BuiltGroup> Built);

    /// <summary>A new level's group with its placement nodes (not yet in the scene; terrain comes when it is hung in).</summary>
    private sealed record BuiltGroup(SceneNode Node, BoundingBox Bounds, int Placed, bool Complete)
    {
        /// <summary>True when every placement got a node and each draws the mesh <paramref name="handles"/> has for it.</summary>
        public bool Matches(IReadOnlyDictionary<string, MeshHandle> handles) =>
            Complete && Node.Children.All(n => n.Tag is ScenePlacement p && handles.TryGetValue(p.MeshPath, out var h) && Equals(h, n.Mesh));
    }

    /// <summary>
    /// The plan to go from <paramref name="groups"/> to <paramref name="next"/>: no GL, no scene state but what is passed in,
    /// so it runs on a worker (<see cref="Prebuild"/>) as well as on the render thread.
    /// </summary>
    private static UpdatePlan Plan(PreparedLevelScene next, IReadOnlyDictionary<int, LevelGroup> groups, IReadOnlyDictionary<string, MeshHandle> handles)
    {
        // Each level's placements and terrain components, by slot (a component's level is the document of that name).
        var placements = new Dictionary<int, List<ScenePlacement>>();
        foreach (var placement in next.Placements)
        {
            if (!placements.TryGetValue(placement.DocumentIndex, out var list))
            {
                placements[placement.DocumentIndex] = list = [];
            }

            list.Add(placement);
        }

        var slotOfLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < next.Documents.Count; i++)
        {
            slotOfLevel.TryAdd(next.Documents[i].Name, next.SlotOf(i));
        }

        var terrain = new Dictionary<int, List<PreparedTerrain>>();
        foreach (var component in next.Terrain)
        {
            var slot = slotOfLevel.GetValueOrDefault(component.LevelName, OtherTerrain);
            if (!terrain.TryGetValue(slot, out var list))
            {
                terrain[slot] = list = [];
            }

            list.Add(component);
        }

        var wanted = new Dictionary<int, LevelDocument?>();
        for (var i = 0; i < next.Documents.Count; i++)
        {
            wanted.TryAdd(next.SlotOf(i), next.Documents[i]);
        }

        if (terrain.ContainsKey(OtherTerrain))
        {
            wanted[OtherTerrain] = null;
        }

        // A level stays when its slot holds the same document drawn the same; one that went or changed loses its nodes.
        var keep = new HashSet<int>();
        foreach (var (slot, group) in groups)
        {
            if (wanted.TryGetValue(slot, out var document) && ReferenceEquals(document, group.Document)
                && SamePlacements(group.Placements, placements.GetValueOrDefault(slot)) && SameTerrain(group.Terrain, terrain.GetValueOrDefault(slot)))
            {
                keep.Add(slot);
            }
        }

        var built = new Dictionary<int, BuiltGroup>();
        foreach (var (slot, document) in wanted)
        {
            if (!keep.Contains(slot))
            {
                built[slot] = Build(document, placements.GetValueOrDefault(slot) ?? [], handles);
            }
        }

        return new UpdatePlan(next, placements, terrain, wanted, keep, built);
    }

    /// <summary>A group with a node per placement (those whose mesh <paramref name="handles"/> has), not in any scene yet.</summary>
    private static BuiltGroup Build(LevelDocument? document, List<ScenePlacement> placements, IReadOnlyDictionary<string, MeshHandle> handles)
    {
        // Filled before it hangs in the scene: the scene then logs one change (the group), not one per node.
        var group = new SceneNode(document?.Name ?? "terrain");
        var bounds = BoundingBox.Empty;
        var placed = 0;
        foreach (var placement in placements)
        {
            if (!handles.TryGetValue(placement.MeshPath, out var handle))
            {
                continue;
            }

            group.Add(new SceneNode(placement.Name, handle, placement.GlModel, placement.SelectableId) { Tag = placement, MaxDrawDistance = LevelSceneUploader.DrawDistanceOf(placement) });
            bounds = bounds.Union(LevelSceneUploader.TransformBounds(handle.Bounds, placement.GlModel));
            placed++;
        }

        return new BuiltGroup(group, bounds, placed, placed == placements.Count);
    }

    private GpuTexture Own(GpuTexture texture)
    {
        _textures.Add(texture);
        return texture;
    }

    private MeshHandle Own(MeshHandle mesh)
    {
        _handles.Add(mesh);
        return mesh;
    }

    /// <summary>Uploads a mesh the scene owns, drawing with the scene's textures; those it takes from the cache are noted as borrowed.</summary>
    private MeshHandle AddOwnedMesh(PreparedMeshAsset asset)
    {
        var handle = LevelSceneUploader.AddMesh(_renderer, asset, _gpuTextures);
        var borrowed = LevelPrepareCache.TexturePathsOf(asset).Where(p => _gpuTextures.TryGetValue(p, out var t) && !_textures.Contains(t)).ToArray();
        if (Cache is not null && borrowed.Length > 0)
        {
            _borrowed[handle.Id] = borrowed;
        }

        return handle;
    }

    private void RemoveOwnedMesh(MeshHandle mesh)
    {
        _renderer.RemoveMesh(mesh);
        _borrowed.Remove(mesh.Id);
    }

    /// <summary>The meshes a bend or a replaced mesh gives back to its node when undone (they may be the cache's).</summary>
    private IEnumerable<MeshHandle> PutBack() =>
        _bent.Values.Select(b => b.Straight)
            .Concat(_bentSegments.Values.Select(b => b.Straight))
            .Concat(_swappedActors.Values.Concat(_swappedParts.Values).SelectMany(s => s.Nodes.Select(n => n.Own)));

    private static bool SamePlacements(List<ScenePlacement> drawn, List<ScenePlacement>? next)
    {
        if (next is null || drawn.Count != next.Count)
        {
            return drawn.Count == (next?.Count ?? 0);
        }

        for (var i = 0; i < drawn.Count; i++)
        {
            if (drawn[i].SelectableId != next[i].SelectableId || !string.Equals(drawn[i].MeshPath, next[i].MeshPath, StringComparison.Ordinal) || drawn[i].GlModel != next[i].GlModel)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameTerrain(List<TerrainPart> drawn, List<PreparedTerrain>? next) =>
        drawn.Count == (next?.Count ?? 0) && (next is null || drawn.Select(p => p.Source).SequenceEqual(next, ReferenceEqualityComparer.Instance));

    /// <summary>Hangs a built level group in the scene with its terrain and indexes its nodes; returns the milliseconds spent on its terrain.</summary>
    private double AddGroup(int slot, LevelDocument? document, List<ScenePlacement> placements, BuiltGroup built, List<PreparedTerrain> components, PreparedLevelScene next,
        Dictionary<string, GpuTexture> layerTextures)
    {
        var group = built.Node;
        foreach (var node in group.Children)
        {
            var placement = (ScenePlacement)node.Tag!;
            if (!_byId.TryGetValue(placement.SelectableId, out var list))
            {
                _byId[placement.SelectableId] = list = [];
            }

            list.Add(placement);
            Indexed(node);
        }

        var clock = Stopwatch.StartNew();
        var parts = new List<TerrainPart>(components.Count);
        foreach (var component in components)
        {
            MeshHandle handle;
            GpuTexture? texture;
            if (Cache is { } cache)
            {
                // Kept on the GPU while the tile stays around: only components the camera brings in are sent.
                (handle, texture) = cache.Terrain(component, () => LevelSceneUploader.CreateTerrain(_renderer, component, next, cache));
            }
            else
            {
                (handle, texture) = LevelSceneUploader.CreateTerrain(_renderer, component, next, null, layerTextures, _textures);
                Own(handle);
                if (texture is not null)
                {
                    Own(texture);
                }
            }

            var node = group.Add(new SceneNode($"{component.LevelName}/{component.Name}", handle, Matrix4x4.Identity) { Tint = texture is null ? LevelSceneUploader.TerrainTint : Vector4.One });
            parts.Add(new TerrainPart(node, handle, texture, component));
        }

        Scene.Root.Add(group);
        _groups[slot] = new LevelGroup(group, document, placements, built.Bounds, built.Placed, parts);
        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Takes a level's nodes out, with everything kept per actor of it (moves, bends, swaps, added instances, owned terrain).</summary>
    private void RemoveGroup(int slot)
    {
        var group = _groups[slot];
        _groups.Remove(slot);
        Scene.Root.Remove(group.Node);
        var ids = new HashSet<uint>();
        foreach (var node in group.Node.DescendantsAndSelf())
        {
            if (node.SelectableId != 0)
            {
                ids.Add(node.SelectableId);
            }
        }

        foreach (var id in ids)
        {
            _byId.Remove(id);
            _nodesById?.Remove(id);
            _movedActors.Remove(id);
            _swappedActors.Remove(id);
            if (_bent.Remove(id, out var bent))
            {
                RemoveOwnedMesh(bent.Bent);
            }
        }

        _movedInstances.RemoveWhere(k => ids.Contains(k.SelectableId));
        _addedInstances.RemoveWhere(k => ids.Contains(k.SelectableId));
        foreach (var key in _bentSegments.Keys.Where(k => ids.Contains(k.SelectableId)).ToList())
        {
            RemoveOwnedMesh(_bentSegments[key].Bent);
            _bentSegments.Remove(key);
        }

        foreach (var key in _swappedParts.Keys.Where(k => ids.Contains(k.SelectableId)).ToList())
        {
            _swappedParts.Remove(key);
        }

        if (_instanceNodes is not null)
        {
            foreach (var key in _instanceNodes.Keys.Where(k => ids.Contains(k.SelectableId)).ToList())
            {
                _instanceNodes.Remove(key);
            }
        }

        foreach (var part in group.Terrain)
        {
            // A component SetTerrainAlbedo took over from the cache belongs to the scene now.
            var current = _terrainParts.FirstOrDefault(p => ReferenceEquals(p.Node, part.Node)) ?? part;
            if (_handles.Remove(current.Handle))
            {
                _renderer.RemoveMesh(current.Handle);
            }

            if (current.Texture is { } texture && _textures.Remove(texture))
            {
                texture.Dispose();
            }
        }
    }

    /// <summary>A sea plane over <paramref name="terrainBounds"/> at <paramref name="seaLevelCm"/> (none without either); kept while both stay.</summary>
    private void UpdateSea(BoundingBox terrainBounds, float? seaLevelCm)
    {
        if (_seaNode is { } sea && (terrainBounds.IsEmpty || seaLevelCm is null || terrainBounds != TerrainBounds || seaLevelCm != Prepared.SeaLevelCm || sea.Parent is null))
        {
            sea.Parent?.Remove(sea);
            if (sea.Mesh is { } mesh && _handles.Remove(mesh))
            {
                _renderer.RemoveMesh(mesh);
            }

            _seaNode = null;
        }

        if (_seaNode is null && !terrainBounds.IsEmpty && seaLevelCm is { } seaLevel)
        {
            var handle = Own(_renderer.AddMesh(LevelSceneUploader.CreateSeaMesh(terrainBounds, seaLevel, _options.SeaMarginCm), MeshSpace.Unreal));
            _renderer.Meshes[handle.Id].Water = true;
            _seaNode = Scene.Add(handle, Matrix4x4.Identity, 0, "Sea");
            _seaNode.Tint = _options.SeaColor;
        }
    }

    /// <summary>Adds a new node to the id and instance lookups when they are built.</summary>
    private void Indexed(SceneNode node)
    {
        if (node.SelectableId != 0)
        {
            Index(node);
        }

        if (_instanceNodes is not null && node.Tag is ScenePlacement { InstanceKey: { } key })
        {
            if (!_instanceNodes.TryGetValue(key, out var list))
            {
                _instanceNodes[key] = list = [];
            }

            list.Add(node);
        }
    }

    /// <summary>The group a node of <paramref name="placement"/>'s level hangs in (the root when the level has none).</summary>
    private SceneNode ParentOf(ScenePlacement placement) => _groups.TryGetValue(placement.DocumentIndex, out var group) ? group.Node : Scene.Root;

    /// <summary>
    /// Counts the nodes added after the scene was built (copies, pins, bent pieces, instances given their own node): a
    /// viewport lights its selection again when it changes, since new nodes start unlit.
    /// </summary>
    public int NodesBuilt { get; private set; }

    /// <summary>Distinct mesh handles created (meshes + terrain).</summary>
    public int MeshCount => _handles.Count;

    /// <summary>Whether this scene has been disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Placements of the actor with <paramref name="selectableId"/>, or empty.</summary>
    public IReadOnlyList<ScenePlacement> Find(uint selectableId) =>
        PlacementsById.TryGetValue(selectableId, out var list) ? list : [];

    /// <summary>Marks the nodes of <paramref name="selectableIds"/> as selected and every other node as not selected.</summary>
    public void SetSelection(IEnumerable<uint> selectableIds) => Scene.SetSelection(selectableIds);

    /// <summary>
    /// Highlights only the placement of one ISM/HISM/foliage instance (one tree, rock or plank) instead of every instance
    /// of its actor (a foliage actor holds thousands).
    /// </summary>
    public void SelectInstance(InstanceKey key)
    {
        foreach (var node in Scene.Nodes)
        {
            node.Selected = node.Tag is ScenePlacement { InstanceKey: { } k } && k == key;
        }
    }

    /// <summary>
    /// Also highlights the component meshes of the actors <paramref name="ids"/> and the placements of
    /// <paramref name="instances"/> ("select all of this kind"); the rest keeps its selection state.
    /// </summary>
    public void HighlightAlso(IReadOnlyCollection<uint> ids, IReadOnlyCollection<InstanceKey> instances)
    {
        if (ids.Count == 0 && instances.Count == 0)
        {
            return;
        }

        var idSet = ids.ToHashSet();
        var keySet = instances.ToHashSet();
        foreach (var node in Scene.Nodes)
        {
            // A part of an actor is lit with its actor; an instance or road piece only by its own key.
            if (node.Tag is ScenePlacement placement
                && (placement.InstanceKey is { InstanceIndex: not InstanceKey.Part } key ? keySet.Contains(key)
                    : idSet.Contains(placement.SelectableId) || (placement.InstanceKey is { } part && keySet.Contains(part))))
            {
                node.Selected = true;
            }
        }
    }

    /// <summary>
    /// Draws one instance (or part) at <paramref name="world"/> (UE world space), e.g. while it is dragged. A spawn part
    /// moves its pin and the item it spawns along: the item, and a model standing in for the pin (the vehicle on the floor
    /// of a car shop's box), keep their offset from the spawner; a plain pin stays upright at its own size (a car shop's box
    /// is scaled 11 x 5 x 3.5: a pin scaled with it would stand 4 m tall).
    /// </summary>
    public void SetInstanceTransform(InstanceKey key, FTransform world)
    {
        if (!InstanceNodes().TryGetValue(key, out var nodes) && (nodes = AddInstanceNodes(key)) is null)
        {
            return;
        }

        foreach (var node in nodes)
        {
            var placement = node.Tag as ScenePlacement;
            var moved = placement is { Spawner: { } spawner } && (placement.Component is not null || SpawnMarkers.ModelOf(placement.MeshPath) is not null)
                && placement.Actor.FindComponent(spawner) is { } part
                ? placement.World.GetRelativeTransform(part.WorldTransform) * world
                : world;
            // A marker keeps its own size (a vehicle box its real one, a capsule a person's) and follows the move and turn.
            node.LocalTransform = UeToGl.ModelMatrix(placement is not null && SpawnMarkers.IsMarker(placement.MeshPath)
                ? moved with { Scale3D = placement.World.Scale3D }
                : moved);
        }

        _movedInstances.Add(key);
    }

    /// <summary>
    /// Replaces the spawn point pins of the actor <paramref name="selectableId"/> (the placements with a
    /// <see cref="ScenePlacement.SpawnPoint"/>) with <paramref name="pins"/>: the project added, moved or removed points of
    /// its stored point array, and the pins mirror the list as it is now (their keys are the current indices). The pins of a
    /// copy (<see cref="AddClone"/>) are placed for the copy's root as it is now and move with the copy from then on.
    /// </summary>
    public void ReplacePins(uint selectableId, IReadOnlyList<ScenePlacement> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        var nodes = NodesOf(selectableId);
        var cloned = _clones.GetValueOrDefault(selectableId);
        foreach (var node in nodes.Where(n => n.Tag is ScenePlacement { SpawnPoint: not null }).ToList())
        {
            node.Parent?.Remove(node);
            nodes.Remove(node);
            cloned?.RemoveAll(c => ReferenceEquals(c.Node, node));
        }

        if (!_byId.TryGetValue(selectableId, out var placements))
        {
            _byId[selectableId] = placements = [];
        }

        placements.RemoveAll(p => p.SpawnPoint is not null);
        foreach (var pin in pins.Where(p => p.SpawnPoint is not null))
        {
            if (!_meshHandles.TryGetValue(pin.MeshPath, out var handle))
            {
                if (SpawnMarkers.AssetFor(pin.MeshPath) is not { } asset)
                {
                    continue;
                }

                handle = AddOwnedMesh(asset); // a sentry without a point before: no capsule uploaded yet
                _handles.Add(handle);
                _meshHandles[pin.MeshPath] = handle;
                _ownedMeshPaths.Add(pin.MeshPath);
            }

            var node = ParentOf(pin).Add(new SceneNode(pin.Name, handle, pin.GlModel, pin.SelectableId));
            node.Tag = pin;
            node.MaxDrawDistance = LevelSceneUploader.DrawDistanceOf(pin);
            placements.Add(pin);
            cloned?.Add((node, pin));
            Index(node);
        }

        _instanceNodes = null;
    }

    /// <summary>Draws the instances the project moved at their new places and every other moved instance back where the level stores it.</summary>
    public void SetInstanceTransforms(IReadOnlyDictionary<InstanceKey, FTransform>? moved)
    {
        var nodes = InstanceNodes();
        foreach (var key in _movedInstances.Where(k => moved is null || !moved.ContainsKey(k)).ToList())
        {
            foreach (var node in nodes.GetValueOrDefault(key) ?? [])
            {
                if (_addedInstances.Contains(key))
                {
                    node.Parent?.Remove(node); // an added instance the project no longer has (deleted, undone)
                    _nodesById?.GetValueOrDefault(key.SelectableId)?.Remove(node);
                    _byId.GetValueOrDefault(key.SelectableId)?.RemoveAll(p => ReferenceEquals(p, node.Tag));
                }
                else if (node.Tag is ScenePlacement placement)
                {
                    node.LocalTransform = placement.GlModel;
                }
            }

            if (_addedInstances.Remove(key))
            {
                nodes.Remove(key);
            }

            _movedInstances.Remove(key);
        }

        foreach (var (key, world) in moved ?? new Dictionary<InstanceKey, FTransform>())
        {
            SetInstanceTransform(key, world);
        }
    }

    /// <summary>
    /// Nodes for an instance the project added to a component (a copied tree, a key past the stored instances): a copy of a
    /// sibling placement of the same component under the new key, so it is picked, selected, moved and hidden like the
    /// stored ones; null when the component draws nothing here.
    /// </summary>
    private List<SceneNode>? AddInstanceNodes(InstanceKey key)
    {
        var sibling = _byId.GetValueOrDefault(key.SelectableId)?.FirstOrDefault(p =>
            p.Instance is { } i && string.Equals(i.ComponentName, key.Component, StringComparison.OrdinalIgnoreCase));
        if (sibling?.Instance is not { } source || !_meshHandles.TryGetValue(sibling.MeshPath, out var handle))
        {
            return null;
        }

        var placement = sibling with { Instance = source with { InstanceIndex = key.InstanceIndex }, Name = $"{sibling.Actor.Name}/{source.ComponentName}[{key.InstanceIndex}]" };
        var node = ParentOf(sibling).Add(new SceneNode(placement.Name, handle, placement.GlModel, key.SelectableId));
        node.Tag = placement;
        node.MaxDrawDistance = LevelSceneUploader.DrawDistanceOf(placement);
        Index(node);
        _byId[key.SelectableId].Add(placement);
        _addedInstances.Add(key);
        var nodes = new List<SceneNode> { node };
        InstanceNodes()[key] = nodes;
        return nodes;
    }

    /// <summary>The nodes drawing each instance key (a spawn part has its pin and the meshes of its item).</summary>
    private Dictionary<InstanceKey, List<SceneNode>> InstanceNodes()
    {
        if (_instanceNodes is null)
        {
            _instanceNodes = [];
            foreach (var node in Scene.Nodes)
            {
                if (node.Tag is ScenePlacement { InstanceKey: { } key })
                {
                    if (!_instanceNodes.TryGetValue(key, out var list))
                    {
                        _instanceNodes[key] = list = [];
                    }

                    list.Add(node);
                }
            }
        }

        return _instanceNodes;
    }

    /// <summary>Hides or shows every node of the actor with <paramref name="selectableId"/> (used to preview deletions).</summary>
    public void SetVisible(uint selectableId, bool visible)
    {
        foreach (var node in NodesOf(selectableId))
        {
            node.Visible = visible;
        }
    }

    /// <summary>Releases the GPU meshes and textures of this scene (must run with the GL context current).</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Scene.Root.Clear();
        foreach (var handle in _handles.Concat(_bent.Values.Select(b => b.Bent)).Concat(_bentSegments.Values.Select(b => b.Bent)))
        {
            _renderer.RemoveMesh(handle);
        }

        _bent.Clear();
        _bentSegments.Clear();
        _borrowed.Clear();

        foreach (var texture in _textures)
        {
            texture.Dispose();
        }

        _handles.Clear();
        _textures.Clear();
    }
}

/// <summary>One uploaded terrain component.</summary>
/// <param name="Node">Its scene node.</param>
/// <param name="Handle">Its mesh.</param>
/// <param name="Texture">Its baked ground texture (owned by the scene, or by the cache), or null.</param>
/// <param name="Source">The prepared component it draws.</param>
internal sealed record TerrainPart(SceneNode Node, MeshHandle Handle, GpuTexture? Texture, PreparedTerrain Source);

/// <summary>Options of <see cref="LevelSceneUploader.Upload"/>.</summary>
public sealed record LevelUploadOptions
{
    /// <summary>Give scenes with terrain the outdoor look (sky, island sun, fog) through <see cref="Scene.Environment"/>.</summary>
    public bool OutdoorEnvironment { get; init; } = true;

    /// <summary>
    /// Grid handling in scenes with terrain: <see cref="GridPlacement.Hidden"/> (default; the grid would cut through the
    /// ground below Z = 0), <see cref="GridPlacement.AtHeight"/> (just below the lowest terrain point) or
    /// <see cref="GridPlacement.Settings"/> (the renderer's own grid settings).
    /// </summary>
    public GridPlacement TerrainGrid { get; init; } = GridPlacement.Hidden;

    /// <summary>Sea plane colour (linear RGB) and opacity (alpha &lt; 1 draws it translucent).</summary>
    public Vector4 SeaColor { get; init; } = new(0.035f, 0.12f, 0.17f, 0.75f);

    /// <summary>How far the sea plane extends past the terrain (cm).</summary>
    public float SeaMarginCm { get; init; } = 2_000_000f;
}

/// <summary>Where an upload's time went (milliseconds on the render thread), and how many meshes it sent to the GPU.</summary>
public sealed record UploadTimings(double TexturesMs, double MeshesMs, double NodesMs, double TerrainMs, int MeshesUploaded)
{
    /// <summary>The whole upload.</summary>
    public double TotalMs => TexturesMs + MeshesMs + NodesMs + TerrainMs;
}

/// <summary>Uploads a <see cref="PreparedLevelScene"/> into a <see cref="SceneRenderer"/> (render thread, GL context current).</summary>
public static class LevelSceneUploader
{
    /// <summary>
    /// A placement's draw distance: its cull distance, or unlimited (+∞) for objects without one, so that
    /// <c>RenderSettings.ObjectDrawDistance</c> caps every object while terrain and sea (0) stay drawn.
    /// </summary>
    internal static float DrawDistanceOf(ScenePlacement placement) => placement.CullDistance > 0f ? placement.CullDistance : float.PositiveInfinity;
    /// <summary>Tint applied to terrain (linear RGBA).</summary>
    public static Vector4 TerrainTint { get; } = new(0.42f, 0.47f, 0.34f, 1f);

    /// <summary>
    /// Creates GPU meshes and textures for <paramref name="prepared"/> and builds the scene graph. Terrain is drawn with its
    /// baked ground texture (or <see cref="TerrainTint"/> in <see cref="GroundMode.Plain"/>), plus a translucent sea plane
    /// when the prepared scene asks for one; a scene with terrain gets the outdoor look (<see cref="SceneEnvironment.Outdoor"/>,
    /// grid per <see cref="LevelUploadOptions.TerrainGrid"/>). With a <paramref name="cache"/>, meshes and textures an
    /// earlier upload sent stay where they are (the scene does not own them; see <see cref="GpuMeshCache"/>).
    /// </summary>
    public static LevelScene Upload(SceneRenderer renderer, PreparedLevelScene prepared, LevelUploadOptions? options = null, GpuMeshCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(prepared);
        var level = new LevelScene(renderer, prepared, options ?? new LevelUploadOptions(), cache);
        level.Apply(prepared);
        return level;
    }

    /// <summary>
    /// The GPU side of one terrain component for a <paramref name="cache"/>: its mesh, baked ground texture and layer
    /// weights (the layer textures come from the cache).
    /// </summary>
    internal static (MeshHandle Handle, GpuTexture? Texture, GpuTexture? Weights) CreateTerrain(SceneRenderer renderer, PreparedTerrain terrain, PreparedLevelScene prepared, GpuMeshCache cache)
    {
        var weights = new List<GpuTexture>(1);
        var (handle, texture) = CreateTerrain(renderer, terrain, prepared, cache, new Dictionary<string, GpuTexture>(StringComparer.OrdinalIgnoreCase), weights);
        return (handle, texture, weights.FirstOrDefault());
    }

    /// <summary>
    /// The mesh and baked ground texture of one terrain component; its layer weights (and, without a <paramref name="cache"/>,
    /// the layer textures it is the first to use, shared through <paramref name="layerTextures"/>) go into <paramref name="owned"/>.
    /// </summary>
    internal static (MeshHandle Handle, GpuTexture? Texture) CreateTerrain(SceneRenderer renderer, PreparedTerrain terrain, PreparedLevelScene prepared, GpuMeshCache? cache,
        Dictionary<string, GpuTexture> layerTextures, List<GpuTexture> owned)
    {
        var texture = terrain.Albedo is { } albedo ? CreateTerrainTexture(renderer, albedo) : null;
        var mesh = renderer.AddMesh(PackTerrain(terrain), texture);
        if (texture is not null && TerrainDetailFor(renderer, terrain, prepared, cache, layerTextures, owned) is { } detail)
        {
            renderer.SetMeshTerrainDetail(mesh, detail);
        }

        return (mesh, texture);
    }

    /// <summary>
    /// The vertex data of a terrain component as uploaded: its mesh, with bounds that also hold every full-resolution sample
    /// of its <see cref="PreparedTerrain.Surface"/>, so the same GPU mesh can draw the component finer near the camera
    /// (<see cref="TerrainDetailRings"/>) and still be culled right.
    /// </summary>
    internal static PreparedMesh PackTerrain(PreparedTerrain terrain)
    {
        var packed = PreparedMesh.From(terrain.Mesh, MeshSpace.Unreal, 1f);
        if (terrain.Surface is not { } surface || packed.Bounds.IsEmpty)
        {
            return packed;
        }

        var b = packed.Bounds; // GL space: Y is up (UE Z)
        var samples = new BoundingBox(new Vector3(b.Min.X, surface.MinHeightCm, b.Min.Z), new Vector3(b.Max.X, surface.MaxHeightCm, b.Max.Z));
        return packed with { Bounds = b.Union(samples) };
    }

    /// <summary>
    /// Uploads every LOD of <paramref name="asset"/>; each material section draws with its material's texture from
    /// <paramref name="gpuTextures"/> (by the paths in <see cref="PreparedMeshAsset.MaterialTextures"/>), sections
    /// without one with the asset's <see cref="PreparedMeshAsset.TexturePath"/>.
    /// </summary>
    public static MeshHandle AddMesh(SceneRenderer renderer, PreparedMeshAsset asset, IReadOnlyDictionary<string, GpuTexture> gpuTextures, PreparedMesh? packed = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(gpuTextures);
        GpuTexture? texture = asset.TexturePath is { } tp && gpuTextures.TryGetValue(tp, out var t) ? t : null;
        var materialTextures = new Dictionary<string, GpuTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var (material, path) in asset.MaterialTextures)
        {
            if (gpuTextures.TryGetValue(path, out var gpu))
            {
                materialTextures[material] = gpu;
            }
        }

        var prepared = packed ?? Pack(asset);
        var handle = renderer.AddMesh(prepared, texture, materialTextures, asset.MaterialAlphaCutoffs, asset.MaterialTints, asset.Shimmer, asset.Billboard, asset.MaterialRoughness);
        renderer.Meshes[handle.Id].Water = asset.Water;
        return handle;
    }

    /// <summary>The vertex and index data of every LOD of <paramref name="asset"/> as <see cref="AddMesh"/> sends it (CPU only: safe on a worker thread).</summary>
    public static PreparedMesh Pack(PreparedMeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return PreparedMesh.FromLods(asset.Lods, asset.LodScreenSizes.ToArray(), MeshSpace.Unreal, 1f);
    }

    /// <summary>
    /// The paint layers of a component in the order its four close-up slots take them: by the weight they have on the ground
    /// above <paramref name="seaLevelCm"/> (all of it when none is above), largest first. By coverage of the whole component,
    /// a coast tile's seabed (85% of it, under water) took a slot and the dry grass and sand painted in the outpost's gravel
    /// got none: those patches were drawn flat, without the ground's grain, cut out of it with a dark ragged edge (owner,
    /// 3.4.0: "brown wrinkled puddles").
    /// With more than four such layers (<paramref name="drawable"/>: the ones a slot can draw) the slots go to the four that
    /// leave the least dry ground without grain, the shader's fade (<see cref="GrainShare"/> of the share the slots cover)
    /// summed over the ground: a layer mixed into one that holds a slot adds little, a patch where nothing else is painted
    /// adds all of it. By weight alone the B_4 airfield's dry grass (93% where it is painted, 0.8% of its component) came
    /// fifth behind beach sand mixed into the beach gravel, and stayed a flat brown smudge with no grain (r37 verifier).
    /// The fifth layer is picked the same way: the rest of the ground takes its grain (<c>TerrainDetailTextures.Rest</c>).
    /// </summary>
    internal static List<LandscapeLayerWeights> DetailLayers(LandscapeComponentLayers layers, LandscapeSurface? surface, float seaLevelCm,
        Func<LandscapeLayerWeights, bool>? drawable = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var n = layers.SampleCount;
        var usable = layers.Layers.Where(l => l.Weights.Length == n * n).ToList();
        var dry = new bool[n * n];
        var anyDry = false;
        if (surface is not null && surface.SampleCount == n)
        {
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    anyDry |= dry[(y * n) + x] = surface.SampleHeight(x, y) >= seaLevelCm;
                }
            }
        }

        var byWeight = usable.Select(l =>
        {
            long sum = 0;
            for (var i = 0; i < l.Weights.Length; i++)
            {
                sum += !anyDry || dry[i] ? l.Weights[i] : 0;
            }

            return (Layer: l, Sum: sum);
        }).OrderByDescending(x => x.Sum).ToList();
        var candidates = byWeight.Where(x => x.Sum > 0 && (drawable?.Invoke(x.Layer) ?? true)).Select(x => x.Layer).ToList();
        if (candidates.Count <= DetailSlots)
        {
            return [.. byWeight.Select(x => x.Layer)];
        }

        // Greedy, slot by slot: the layer that adds the most grain to the ground the slots taken so far leave. Every third
        // sample of a full component (3 m apart) is plenty to compare patches (and a ninth of the work on the render thread).
        var step = n > 64 ? 3 : 1;
        var samples = new List<int>();
        for (var y = 0; y < n; y += step)
        {
            for (var x = 0; x < n; x += step)
            {
                if (!anyDry || dry[(y * n) + x])
                {
                    samples.Add((y * n) + x);
                }
            }
        }

        // One pick more than the slots: the fifth is the layer whose grain the rest of the ground takes (the largest patch left).
        // Ground already covered to full grain (GrainShare 1 from 0.6 on) gains nothing more and leaves the count.
        var covered = new int[samples.Count];
        var open = Enumerable.Range(0, samples.Count).ToList();
        var chosen = new List<LandscapeLayerWeights>(DetailSlots + 1);
        for (var slot = 0; slot < Math.Min(candidates.Count, DetailSlots + 1); slot++)
        {
            LandscapeLayerWeights? best = null;
            var bestGain = -1.0;
            foreach (var layer in candidates.Where(c => !chosen.Contains(c)))
            {
                var gain = 0.0;
                foreach (var s in open)
                {
                    var w = layer.Weights[samples[s]];
                    if (w > 0)
                    {
                        gain += ShareOf(covered[s] + w) - ShareOf(covered[s]);
                    }
                }

                if (gain > bestGain + 1e-6)
                {
                    (best, bestGain) = (layer, gain);
                }
            }

            chosen.Add(best!);
            foreach (var s in open)
            {
                covered[s] += best!.Weights[samples[s]];
            }

            open.RemoveAll(s => covered[s] >= FullGrainBytes);
        }

        return [.. chosen, .. byWeight.Select(x => x.Layer).Where(l => !chosen.Contains(l))];
    }

    /// <summary>Number of paint layers a terrain component draws with their own texture up close (the shader's four slots).</summary>
    internal const int DetailSlots = 4;

    /// <summary>
    /// How much of a layer's grain the terrain shader keeps where its slots cover <paramref name="covered"/> (0..1) of the
    /// ground: <c>smoothstep(0.1, 0.6, covered)</c>, as <c>ShaderSources</c> fades it.
    /// </summary>
    internal static float GrainShare(float covered)
    {
        var t = Math.Clamp((covered - 0.1f) / 0.5f, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    // GrainShare of a sum of byte weights (0..4 x 255), looked up.
    private static readonly float[] Shares = [.. Enumerable.Range(0, (DetailSlots * 255) + 1).Select(i => GrainShare(i / 255f))];

    private static float ShareOf(int coveredBytes) => Shares[Math.Min(coveredBytes, Shares.Length - 1)];

    // The byte weight from which GrainShare is 1 (0.6 of 255, rounded up).
    private const int FullGrainBytes = 154;

    // Average linear colour of a layer texture the preparer built no mip chain for (none today; computed once per decoded image).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextureImage, StrongBox<Vector3>> LayerMeans = new();

    /// <summary>
    /// The close-up ground of one terrain component: its four main paint layers (by coverage) with a known diffuse texture,
    /// their weights as an RGBA texture over the component, tiling and average colour. Null without layer data or textures.
    /// </summary>
    private static TerrainDetailTextures? TerrainDetailFor(SceneRenderer renderer, PreparedTerrain terrain, PreparedLevelScene prepared, GpuMeshCache? cache,
        Dictionary<string, GpuTexture> layerTextures, List<GpuTexture> owned)
    {
        if (terrain.Layers is not { } layers || prepared.LayerTextures is not { } images || prepared.LayerCatalog is not { } catalog)
        {
            return null;
        }

        var n = layers.SampleCount;
        bool Drawable(LandscapeLayerWeights l) => catalog.Resolve(l.Name) is { DiffuseTexture: { } path, TilingCm: > 0f } && images.Images.ContainsKey(path);
        var ordered = DetailLayers(layers, terrain.Surface, prepared.SeaLevelCm ?? 0f, Drawable)
            .Where(Drawable)
            .Select(l => (Layer: l, Style: catalog.Resolve(l.Name)))
            .Take(DetailSlots + 1)
            .ToList();
        var top = ordered.Take(DetailSlots).ToList();
        if (top.Count == 0)
        {
            return null;
        }

        var weights = new byte[n * n * 4];
        for (var i = 0; i < n * n; i++)
        {
            for (var k = 0; k < top.Count; k++)
            {
                weights[(i * 4) + k] = top[k].Layer.Weights[i];
            }
        }

        var weightTexture = renderer.CreateTexture(n, n, weights, srgb: false, TextureWrap.ClampToEdge);
        owned.Add(weightTexture);
        var gpu = new GpuTexture?[4];
        var normals = new GpuTexture?[4];
        var means = new Vector3[4];
        var tiling = new float[4];
        GpuTexture Diffuse(string path)
        {
            var image = images.Images[path];
            GpuTexture? layer;
            if (cache is not null)
            {
                layer = cache.Texture(renderer, path, image); // through the cache every time: it notes what this terrain draws with
            }
            else if (!layerTextures.TryGetValue(path, out layer))
            {
                layer = TextureUpload.Create(renderer, image);
                owned.Add(layer);
                layerTextures[path] = layer;
            }

            return layer;
        }

        // The 1×1 mip the preparer's worker built for the bake: worked out here texel by texel, it held the render thread
        // some 20 ms per new layer as the ground streamed in.
        Vector3 Mean(string path) => images.Find(path)?.Mean ?? LayerMeans.GetValue(images.Images[path], i => new StrongBox<Vector3>(new LinearMipChain(i).Mean)).Value;

        for (var k = 0; k < top.Count; k++)
        {
            var path = top[k].Style.DiffuseTexture!;
            gpu[k] = Diffuse(path);
            if (top[k].Style.NhrTexture is { } nhrPath && images.Images.TryGetValue(nhrPath, out var nhrImage))
            {
                if (cache is not null)
                {
                    normals[k] = cache.Texture(renderer, nhrPath, nhrImage);
                }
                else if (!layerTextures.TryGetValue(nhrPath, out normals[k]))
                {
                    normals[k] = TextureUpload.Create(renderer, nhrImage);
                    owned.Add(normals[k]!);
                    layerTextures[nhrPath] = normals[k]!;
                }
            }

            means[k] = Mean(path);
            tiling[k] = 1f / top[k].Style.TilingCm;
        }

        // A fifth layer: the rest of the ground takes its grain (the largest patch the four leave, see DetailLayers).
        var rest = ordered.Count > DetailSlots ? ordered[DetailSlots].Style : null;
        return new TerrainDetailTextures(weightTexture, gpu, new Vector4(tiling[0], tiling[1], tiling[2], tiling[3]), means, normals,
            rest is null ? null : Diffuse(rest.DiffuseTexture!), rest is null ? 0f : 1f / rest.TilingCm, rest is null ? default : Mean(rest.DiffuseTexture!));
    }

    /// <summary>Uploads a baked terrain texture (sRGB, mipmapped, clamp-to-edge so components meet without seams).</summary>
    public static GpuTexture CreateTerrainTexture(SceneRenderer renderer, TerrainAlbedo albedo)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(albedo);
        return renderer.CreateTexture(albedo.Size, albedo.Size, albedo.Rgba, srgb: true, TextureWrap.ClampToEdge);
    }

    /// <summary>
    /// A flat quad at UE height <paramref name="seaLevelCm"/> covering <paramref name="terrainBoundsGl"/> (GL space) plus
    /// <paramref name="marginCm"/> on every side, normal up (UE vertices).
    /// </summary>
    public static MeshData CreateSeaMesh(BoundingBox terrainBoundsGl, float seaLevelCm, float marginCm = 50_000f)
    {
        var min = UeToGl.ToUePoint(terrainBoundsGl.Min);
        var max = UeToGl.ToUePoint(terrainBoundsGl.Max);
        var x0 = MathF.Min(min.X, max.X) - marginCm;
        var x1 = MathF.Max(min.X, max.X) + marginCm;
        var y0 = MathF.Min(min.Y, max.Y) - marginCm;
        var y1 = MathF.Max(min.Y, max.Y) + marginCm;
        float[] positions = [x0, y0, seaLevelCm, x1, y0, seaLevelCm, x0, y1, seaLevelCm, x1, y1, seaLevelCm];
        float[] normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f];
        uint[] indices = [0u, 2u, 1u, 1u, 2u, 3u];
        return MeshData.Create("Sea", positions, indices, normals, null, [new MeshSection("Sea", 0, indices.Length)]);
    }

    /// <summary>Axis-aligned bounds of <paramref name="bounds"/> after <paramref name="model"/> (all eight corners).</summary>
    public static BoundingBox TransformBounds(BoundingBox bounds, in Matrix4x4 model)
    {
        if (bounds.IsEmpty)
        {
            return bounds;
        }

        var result = BoundingBox.Empty;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            result = result.Include(Vector3.Transform(corner, model));
        }

        return result;
    }
}
