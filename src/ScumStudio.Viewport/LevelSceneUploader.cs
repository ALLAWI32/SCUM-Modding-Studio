using ScumStudio.Assets.Textures;
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
    private readonly SceneRenderer _renderer;
    private readonly List<MeshHandle> _handles;
    private readonly List<GpuTexture> _textures;
    private readonly Dictionary<string, MeshHandle> _meshHandles;
    private readonly Dictionary<uint, List<ScenePlacement>> _byId;
    private readonly Dictionary<uint, List<(SceneNode Node, ScenePlacement? Source)>> _clones = [];
    private readonly HashSet<uint> _movedActors = [];
    private readonly Dictionary<string, GpuTexture> _gpuTextures;
    private readonly Dictionary<string, PreparedMeshAsset> _extraAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, (MeshHandle Bent, SceneNode Node, MeshHandle Straight, IReadOnlyList<SplineMeshParams> Spline)> _bent = [];
    private readonly HashSet<InstanceKey> _movedInstances = [];
    private Dictionary<InstanceKey, SceneNode>? _instanceNodes;
    private readonly List<TerrainPart> _terrainParts = [];
    private SceneNode? _seaNode;

    internal LevelScene(SceneRenderer renderer, PreparedLevelScene prepared, Scene scene, BoundingBox bounds, BoundingBox terrainBounds,
        Dictionary<uint, List<ScenePlacement>> byId, Dictionary<string, MeshHandle> meshHandles, List<MeshHandle> handles, List<GpuTexture> textures, int placed,
        Dictionary<string, GpuTexture> gpuTextures)
    {
        _gpuTextures = gpuTextures;
        _renderer = renderer;
        Prepared = prepared;
        Scene = scene;
        Bounds = bounds;
        TerrainBounds = terrainBounds;
        _byId = byId;
        _meshHandles = meshHandles;
        _handles = handles;
        _textures = textures;
        PlacedCount = placed;
    }

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
                var texture = _renderer.CreateTexture(image.Width, image.Height, image.Rgba, image.IsSrgb);
                _gpuTextures[path] = texture;
                _textures.Add(texture);
            }
        }

        var handle = LevelSceneUploader.AddMesh(_renderer, mesh.Asset, _gpuTextures);
        _handles.Add(handle);
        _meshHandles[mesh.Asset.MeshPath] = handle;
        _extraAssets[mesh.Asset.MeshPath] = mesh.Asset;
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

            _renderer.RemoveMesh(current.Bent);
            _bent.Remove(selectableId);
        }

        if (spline is null)
        {
            return;
        }

        var nodes = Scene.Nodes.Where(n => n.SelectableId == selectableId && n.Mesh is not null).Take(2).ToList();
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
        var bent = LevelSceneUploader.AddMesh(_renderer, asset with { MeshPath = path + "#bent", Mesh = lods[0], Lods = lods }, _gpuTextures);
        _bent[selectableId] = (bent, node, node.Mesh!, spline);
        node.Mesh = bent;
    }

    /// <summary>Ids drawn bent (see <see cref="SetBend"/>).</summary>
    public IReadOnlyCollection<uint> BentIds => _bent.Keys;

    private readonly Dictionary<InstanceKey, (MeshHandle Bent, SceneNode Node, MeshHandle Straight, SplineMeshParams Spline)> _bentSegments = [];

    /// <summary>Road, rail and bridge pieces drawn with a new curve (see <see cref="SetSegmentBend"/>).</summary>
    public IReadOnlyCollection<InstanceKey> BentSegments => _bentSegments.Keys;

    /// <summary>
    /// Draws the spline piece <paramref name="key"/> along <paramref name="spline"/> instead of the curve the level gives
    /// it (null: as the level has it again). The straight mesh comes from <see cref="PreparedLevelScene.SplineSources"/>.
    /// </summary>
    public void SetSegmentBend(InstanceKey key, SplineMeshParams? spline)
    {
        if (_bentSegments.TryGetValue(key, out var current))
        {
            if (spline is not null && current.Spline == spline && ReferenceEquals(current.Node.Mesh, current.Bent))
            {
                return;
            }

            if (ReferenceEquals(current.Node.Mesh, current.Bent))
            {
                current.Node.Mesh = current.Straight;
            }

            _renderer.RemoveMesh(current.Bent);
            _bentSegments.Remove(key);
        }

        if (spline is null || !InstanceNodes().TryGetValue(key, out var node) || node.Tag is not ScenePlacement placement || node.Mesh is not { } shown)
        {
            return;
        }

        var marker = placement.MeshPath.IndexOf(SplineMeshPlacements.KeyMarker, StringComparison.Ordinal);
        if (marker <= 0 || !Prepared.SplineSources.TryGetValue(placement.MeshPath[..marker], out var straight))
        {
            return;
        }

        var lods = straight.Lods.Select(l => SplineMeshDeformer.Deform(l, spline, straight.Mesh.Bounds)).ToList();
        var bent = LevelSceneUploader.AddMesh(_renderer, straight with { MeshPath = placement.MeshPath + "#reshaped", Mesh = lods[0], Lods = lods }, _gpuTextures);
        _bentSegments[key] = (bent, node, shown, spline);
        node.Mesh = bent;
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
        foreach (var node in Scene.Nodes)
        {
            if (node.SelectableId == selectableId && node.Tag is ScenePlacement placement)
            {
                node.LocalTransform = UeToGl.ModelMatrix(placement.World.GetRelativeTransform(placement.Actor.WorldTransform) * rootWorld);
            }
        }

        _movedActors.Add(selectableId);
    }

    /// <summary>Puts the actor's placements back where the level stores them.</summary>
    public void ResetActorTransform(uint selectableId)
    {
        foreach (var node in Scene.Nodes)
        {
            if (node.SelectableId == selectableId && node.Tag is ScenePlacement placement)
            {
                node.LocalTransform = placement.GlModel;
            }
        }

        _movedActors.Remove(selectableId);
    }

    /// <summary>
    /// Adds (or moves) a clone of the actor <paramref name="sourceId"/> under the id <paramref name="cloneId"/>, with its
    /// root at <paramref name="rootWorld"/>; the clone's nodes carry placements with the new id, so hiding and picking work.
    /// Returns the number of nodes the clone has (0 when the source has no drawable placement).
    /// </summary>
    public int AddClone(uint cloneId, uint sourceId, FTransform rootWorld, string name, string? meshPath = null)
    {
        RemoveClone(cloneId);
        if (sourceId == 0)
        {
            // A new mesh actor: one node drawing the mesh at the root.
            if (meshPath is null || !_meshHandles.TryGetValue(meshPath, out var meshHandle))
            {
                return 0;
            }

            var meshNode = Scene.Add(meshHandle, UeToGl.ModelMatrix(rootWorld), cloneId, name);
            _clones[cloneId] = [(meshNode, null)];
            return 1;
        }

        if (!_byId.TryGetValue(sourceId, out var sources))
        {
            return 0;
        }

        var nodes = new List<(SceneNode Node, ScenePlacement? Source)>();
        var placements = new List<ScenePlacement>();
        foreach (var source in sources)
        {
            if (!_meshHandles.TryGetValue(source.MeshPath, out var handle))
            {
                continue;
            }

            var world = source.World.GetRelativeTransform(source.Actor.WorldTransform) * rootWorld;
            var model = UeToGl.ModelMatrix(world);
            var slash = source.Name.IndexOf('/');
            var placement = source with { GlModel = model, World = world, SelectableId = cloneId, Name = slash < 0 ? name : name + source.Name[slash..] };
            var node = Scene.Add(handle, model, cloneId, placement.Name);
            node.Tag = placement;
            node.MaxDrawDistance = LevelSceneUploader.DrawDistanceOf(placement);
            nodes.Add((node, source));
            placements.Add(placement);
        }

        _clones[cloneId] = nodes;
        _byId[cloneId] = placements;
        return nodes.Count;
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
                Scene.Root.Remove(node);
            }

            _byId.Remove(cloneId);
        }
    }

    /// <summary>The CPU-side data this scene was built from.</summary>
    public PreparedLevelScene Prepared { get; }

    /// <summary>The renderable scene graph.</summary>
    public Scene Scene { get; }

    /// <summary>GL-space bounds of everything drawn (meshes and terrain).</summary>
    public BoundingBox Bounds { get; }

    /// <summary>GL-space bounds of the terrain only (empty without terrain).</summary>
    public BoundingBox TerrainBounds { get; }

    /// <summary>Placements per selectable id (an actor with several components/instances has several placements).</summary>
    public IReadOnlyDictionary<uint, List<ScenePlacement>> PlacementsById => _byId;

    /// <summary>Mesh placements drawn.</summary>
    public int PlacedCount { get; }

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
            var image = i < albedo.Count ? albedo[i] : null;
            var texture = image is null ? null : LevelSceneUploader.CreateTerrainTexture(_renderer, image);
            _renderer.SetMeshTexture(part.Handle, texture);
            part.Node.Tint = texture is null ? LevelSceneUploader.TerrainTint : Vector4.One;
            if (part.Texture is { } old)
            {
                _textures.Remove(old);
                old.Dispose();
            }

            if (texture is not null)
            {
                _textures.Add(texture);
            }

            _terrainParts[i] = part with { Texture = texture };
        }

        Ground = mode;
    }

    internal void AttachTerrain(List<TerrainPart> parts, SceneNode? sea, GroundMode ground)
    {
        _terrainParts.AddRange(parts);
        _seaNode = sea;
        Ground = ground;
    }

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
            if (node.Tag is ScenePlacement placement
                && (placement.InstanceKey is { } key ? keySet.Contains(key) : idSet.Contains(placement.SelectableId)))
            {
                node.Selected = true;
            }
        }
    }

    /// <summary>Draws one instance at <paramref name="world"/> (UE world space), e.g. while it is dragged.</summary>
    public void SetInstanceTransform(InstanceKey key, FTransform world)
    {
        if (InstanceNodes().TryGetValue(key, out var node))
        {
            node.LocalTransform = UeToGl.ModelMatrix(world);
            _movedInstances.Add(key);
        }
    }

    /// <summary>Draws the instances the project moved at their new places and every other moved instance back where the level stores it.</summary>
    public void SetInstanceTransforms(IReadOnlyDictionary<InstanceKey, FTransform>? moved)
    {
        var nodes = InstanceNodes();
        foreach (var key in _movedInstances.Where(k => moved is null || !moved.ContainsKey(k)).ToList())
        {
            if (nodes.TryGetValue(key, out var node) && node.Tag is ScenePlacement placement)
            {
                node.LocalTransform = placement.GlModel;
            }

            _movedInstances.Remove(key);
        }

        foreach (var (key, world) in moved ?? new Dictionary<InstanceKey, FTransform>())
        {
            SetInstanceTransform(key, world);
        }
    }

    private Dictionary<InstanceKey, SceneNode> InstanceNodes()
    {
        if (_instanceNodes is null)
        {
            _instanceNodes = [];
            foreach (var node in Scene.Nodes)
            {
                if (node.Tag is ScenePlacement { InstanceKey: { } key })
                {
                    _instanceNodes.TryAdd(key, node);
                }
            }
        }

        return _instanceNodes;
    }

    /// <summary>Hides or shows every node of the actor with <paramref name="selectableId"/> (used to preview deletions).</summary>
    public void SetVisible(uint selectableId, bool visible)
    {
        foreach (var node in Scene.Nodes)
        {
            if (node.SelectableId == selectableId)
            {
                node.Visible = visible;
            }
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
/// <param name="Texture">Its baked ground texture (owned by the scene), or null.</param>
internal sealed record TerrainPart(SceneNode Node, MeshHandle Handle, GpuTexture? Texture);

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
        options ??= new LevelUploadOptions();
        cache?.Begin();

        var handles = new List<MeshHandle>();
        var textures = new List<GpuTexture>();
        var gpuTextures = new Dictionary<string, GpuTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, image) in prepared.Textures)
        {
            if (cache is not null)
            {
                gpuTextures[path] = cache.Texture(renderer, path, image);
                continue;
            }

            var texture = renderer.CreateTexture(image.Width, image.Height, image.Rgba, image.IsSrgb);
            gpuTextures[path] = texture;
            textures.Add(texture);
        }

        var meshHandles = new Dictionary<string, MeshHandle>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, asset) in prepared.Meshes)
        {
            if (cache is not null)
            {
                meshHandles[path] = cache.Mesh(renderer, asset, gpuTextures);
                continue;
            }

            var handle = AddMesh(renderer, asset, gpuTextures);
            meshHandles[path] = handle;
            handles.Add(handle);
        }

        var scene = new Scene();
        var bounds = BoundingBox.Empty;
        var byId = new Dictionary<uint, List<ScenePlacement>>();
        var placed = 0;
        foreach (var placement in prepared.Placements)
        {
            if (!meshHandles.TryGetValue(placement.MeshPath, out var handle))
            {
                continue;
            }

            var node = scene.Add(handle, placement.GlModel, placement.SelectableId, placement.Name);
            node.Tag = placement;
            node.MaxDrawDistance = DrawDistanceOf(placement);
            bounds = bounds.Union(TransformBounds(handle.Bounds, placement.GlModel));
            if (!byId.TryGetValue(placement.SelectableId, out var list))
            {
                byId[placement.SelectableId] = list = [];
            }

            list.Add(placement);
            placed++;
        }

        var terrainBounds = BoundingBox.Empty;
        var parts = new List<TerrainPart>();
        var layerTextures = new Dictionary<string, GpuTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var terrain in prepared.Terrain)
        {
            var texture = terrain.Albedo is { } albedo ? CreateTerrainTexture(renderer, albedo) : null;
            if (texture is not null)
            {
                textures.Add(texture);
            }

            var handle = renderer.AddMesh(terrain.Mesh, MeshSpace.Unreal, 1f, texture);
            handles.Add(handle);
            if (texture is not null && TerrainDetailFor(renderer, terrain, prepared, cache, layerTextures, textures) is { } detail)
            {
                renderer.SetMeshTerrainDetail(handle, detail);
            }
            var node = scene.Add(handle, Matrix4x4.Identity, 0, $"{terrain.LevelName}/{terrain.Name}");
            node.Tint = texture is null ? TerrainTint : Vector4.One;
            terrainBounds = terrainBounds.Union(handle.Bounds);
            parts.Add(new TerrainPart(node, handle, texture));
        }

        SceneNode? sea = null;
        if (!terrainBounds.IsEmpty && prepared.SeaLevelCm is { } seaLevel)
        {
            var seaMesh = CreateSeaMesh(terrainBounds, seaLevel, options.SeaMarginCm);
            var seaHandle = renderer.AddMesh(seaMesh, MeshSpace.Unreal);
            handles.Add(seaHandle);
            sea = scene.Add(seaHandle, Matrix4x4.Identity, 0, "Sea");
            sea.Tint = options.SeaColor;
        }

        if (!terrainBounds.IsEmpty && options.OutdoorEnvironment)
        {
            scene.Environment = SceneEnvironment.Outdoor(options.TerrainGrid, terrainBounds.Min.Y - 100f);
        }

        bounds = bounds.Union(terrainBounds);
        var level = new LevelScene(renderer, prepared, scene, bounds, terrainBounds, byId, meshHandles, handles, textures, placed, gpuTextures);
        level.AttachTerrain(parts, sea, prepared.Ground);
        return level;
    }

    /// <summary>
    /// Uploads every LOD of <paramref name="asset"/>; each material section draws with its material's texture from
    /// <paramref name="gpuTextures"/> (by the paths in <see cref="PreparedMeshAsset.MaterialTextures"/>), sections
    /// without one with the asset's <see cref="PreparedMeshAsset.TexturePath"/>.
    /// </summary>
    public static MeshHandle AddMesh(SceneRenderer renderer, PreparedMeshAsset asset, IReadOnlyDictionary<string, GpuTexture> gpuTextures)
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

        var prepared = PreparedMesh.FromLods(asset.Lods, asset.LodScreenSizes.ToArray(), MeshSpace.Unreal, 1f);
        return renderer.AddMesh(prepared, texture, materialTextures, asset.MaterialAlphaCutoffs, asset.MaterialTints);
    }

    // Average linear colour of each layer texture (computed once per decoded image).
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
        var top = layers.Layers
            .Where(l => l.Weights.Length == n * n)
            .Select(l => (Layer: l, Style: catalog.Resolve(l.Name)))
            .Where(x => x.Style.DiffuseTexture is { } path && x.Style.TilingCm > 0f && images.Images.ContainsKey(path))
            .OrderByDescending(x => x.Layer.Coverage)
            .Take(4)
            .ToList();
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
        var means = new Vector3[4];
        var tiling = new float[4];
        for (var k = 0; k < top.Count; k++)
        {
            var path = top[k].Style.DiffuseTexture!;
            var image = images.Images[path];
            if (!layerTextures.TryGetValue(path, out var layer))
            {
                layer = cache is not null ? cache.Texture(renderer, path, image) : renderer.CreateTexture(image.Width, image.Height, image.Rgba, image.IsSrgb);
                if (cache is null)
                {
                    owned.Add(layer);
                }

                layerTextures[path] = layer;
            }

            gpu[k] = layer;
            means[k] = LayerMeans.GetValue(image, MeanColour).Value;
            tiling[k] = 1f / top[k].Style.TilingCm;
        }

        return new TerrainDetailTextures(weightTexture, gpu, new Vector4(tiling[0], tiling[1], tiling[2], tiling[3]), means);

        static StrongBox<Vector3> MeanColour(TextureImage image)
        {
            double r = 0, g = 0, b = 0;
            var count = image.Width * image.Height;
            for (var i = 0; i < count; i++)
            {
                r += SrgbToLinear(image.Rgba[i * 4]);
                g += SrgbToLinear(image.Rgba[(i * 4) + 1]);
                b += SrgbToLinear(image.Rgba[(i * 4) + 2]);
            }

            return new StrongBox<Vector3>(count == 0 ? Vector3.One : new Vector3((float)(r / count), (float)(g / count), (float)(b / count)));
        }

        static double SrgbToLinear(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
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
