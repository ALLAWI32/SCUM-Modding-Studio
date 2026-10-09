using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Meshes;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;

namespace ScumStudio.Viewport;

/// <summary>What to include when a level scene is prepared.</summary>
public sealed record LevelSceneOptions
{
    /// <summary>Finest LOD index used for every static mesh (falls back to LOD 0 when a mesh has fewer LODs).</summary>
    public int Lod { get; init; }

    /// <summary>
    /// Number of LODs uploaded per mesh from <see cref="Lod"/> on (the renderer picks one per cluster by projected
    /// size); 1 draws the finest LOD everywhere.
    /// </summary>
    public int MaxLods { get; init; } = 8;

    /// <summary>Largest texture edge in pixels; 0 disables base colour textures.</summary>
    public int TextureSize { get; init; } = 512;

    /// <summary>Include instanced static mesh (ISM/HISM) instances.</summary>
    public bool IncludeInstances { get; init; } = true;

    /// <summary>Build terrain for landscape tiles among the documents.</summary>
    public bool IncludeLandscape { get; init; } = true;

    /// <summary>Terrain vertex step in quads (1 = full detail).</summary>
    public int LandscapeStep { get; init; } = 1;

    /// <summary>Only actors whose name, class or mesh path contains this text (null = all).</summary>
    public string? Filter { get; init; }

    /// <summary>Selectable-id offset per document index (ids are <c>documentIndex &lt;&lt; shift | exportIndex + 1</c>).</summary>
    public int DocumentIdShift { get; init; } = 20;

    /// <summary>
    /// The number each document's ids are made from (its slot; ids are <c>slot &lt;&lt; shift | exportIndex + 1</c>), index-aligned
    /// with the documents; null = the document index. A level that stays loaded from one scene to the next keeps its slot
    /// (see <see cref="LevelScenePreparer.SlotsAfter"/>), so its actors keep their ids and a viewport keeps its nodes.
    /// </summary>
    public IReadOnlyList<int>? DocumentSlots { get; init; }

    /// <summary>How terrain is coloured (default <see cref="GroundMode.Realistic"/>: baked colours from the paint layers).</summary>
    public GroundMode Ground { get; init; } = GroundMode.Realistic;

    /// <summary>
    /// Draw a translucent sea plane at <see cref="SeaLevelCm"/>: null (default) = automatically when the scene has terrain
    /// that goes below sea level, true = whenever there is terrain, false = never.
    /// </summary>
    public bool? SeaPlane { get; init; }

    /// <summary>Sea surface height (UE centimetres; SCUM's sea is at Z ≈ 0).</summary>
    public float SeaLevelCm { get; init; }

    /// <summary>
    /// Layer look table; null = the embedded default with the user's <c>terrain-layers.json</c> override
    /// (<see cref="TerrainLayerCatalog.LoadDefault"/>).
    /// </summary>
    public TerrainLayerCatalog? LayerCatalog { get; init; }

    /// <summary>
    /// Realistic mode: tile the layer textures found in the game files over the ground (and use their mean colour where
    /// a texture is missing); off = the catalog's fallback palette only.
    /// </summary>
    public bool ResolveLayerTextures { get; init; } = true;

    /// <summary>
    /// Baked ground texture size per terrain component in Realistic mode with layer textures; 0 (default) =
    /// <see cref="TerrainBakeSettings.DefaultTexturedSize"/> (1024²). Smaller saves GPU memory, larger shows more detail up close.
    /// </summary>
    public int TerrainTextureSize { get; init; }
}

/// <summary>One mesh placement of a prepared scene, with the level object it came from.</summary>
/// <param name="MeshPath">
/// Key of the mesh in <see cref="PreparedLevelScene.Meshes"/>: the static mesh object path, or for a spline mesh component the
/// key of its bent copy (<c>path#spline&lt;n&gt;</c>, see <see cref="SplineMeshPlacements"/>; the component keeps the real path).
/// </param>
/// <param name="GlModel">World matrix in the renderer's GL space (for meshes uploaded with <c>MeshSpace.Unreal</c>).</param>
/// <param name="World">UE world transform (centimetres).</param>
/// <param name="SelectableId">Id reported by picking (0 = not pickable).</param>
/// <param name="Name">Display name (<c>Actor/Component[instance]</c>).</param>
/// <param name="DocumentIndex">Slot of the owning document in the prepared scene (see <see cref="PreparedLevelScene.Slots"/>; its index unless slots were given).</param>
/// <param name="Actor">Owning actor.</param>
/// <param name="Component">Component, when the placement is a component mesh.</param>
/// <param name="Instance">ISM/HISM instance, when the placement is an instance.</param>
public sealed record ScenePlacement(
    string MeshPath,
    Matrix4x4 GlModel,
    FTransform World,
    uint SelectableId,
    string Name,
    int DocumentIndex,
    ActorRecord Actor,
    ComponentRecord? Component,
    ActorInstance? Instance)
{
    /// <summary>
    /// The instance key of this placement: an ISM/HISM instance, or one spline mesh piece (a road, river bank or bridge
    /// segment, <see cref="InstanceKey.Segment"/>) so a click picks that piece, not the whole road; or one part of a
    /// Blueprint the level stores (<see cref="InstanceKey.Part"/>: a wall, shelf or lamp of a hangar), picked only in part
    /// mode; a spawn part's pin or drawn item (<see cref="Spawner"/>) is that part; null otherwise (the root component: that is
    /// the actor itself).
    /// </summary>
    public InstanceKey? InstanceKey => Instance is { } i ? Viewport.InstanceKey.Of(SelectableId, i.ComponentName, i.InstanceIndex)
        : SpawnPoint is { } sp ? Viewport.InstanceKey.Of(SelectableId, sp.Key, Viewport.InstanceKey.SpawnPointBase - sp.Index)
        : LootMarker is { } m ? Viewport.InstanceKey.Of(SelectableId, m.Component, Viewport.InstanceKey.LootPoint - m.Index)
        : Spawner is { } s ? Viewport.InstanceKey.Of(SelectableId, s, Viewport.InstanceKey.Part)
        : Component is { SplineMesh: not null } c ? Viewport.InstanceKey.Of(SelectableId, c.Name, Viewport.InstanceKey.Segment)
        : Component is { IsSynthesized: false, ExportIndex: >= 0 } part && part.ExportIndex != Actor.RootComponent
            ? Viewport.InstanceKey.Of(SelectableId, part.Name, Viewport.InstanceKey.Part)
            : null;

    /// <summary>
    /// What a click on this placement selects inside its actor: <see cref="InstanceKey"/>, except that a part is taken only
    /// with <paramref name="parts"/> (part mode or Alt) or as a spawn part, and never for a component the actor's C++ class
    /// makes (<see cref="ComponentRecord.IsNativeSubobject"/>, a door's leaf: the door is copied, not its mesh); null = the
    /// whole actor.
    /// </summary>
    public InstanceKey? PickKey(bool parts) =>
        InstanceKey is { } key && (key.InstanceIndex != Viewport.InstanceKey.Part || Spawner is not null || SpawnPoint is not null
                                   || (parts && Component is not { IsNativeSubobject: true } && !Actor.IsItemContainer))
            ? key
            : null;

    /// <summary>Distance (cm) beyond which the game does not draw this placement (HISM/foliage <c>InstanceEndCullDistance</c>); 0 = always drawn.</summary>
    public float CullDistance { get; init; }

    /// <summary>The pin of one loot point of a building (its item spawner component and marker index), or null.</summary>
    public (string Component, int Index)? LootMarker { get; init; }

    /// <summary>
    /// The pin of one point of a spawner's stored point array (a sentry's patrol point, a loot point of an item spawner
    /// group): the array's key (see <see cref="Level.Spawns.SpawnPointArray.Key"/>) and the point's index in the list as
    /// it is now. It picks, moves, copies and deletes on its own (<see cref="Viewport.InstanceKey.IsSpawnPoint"/>); null otherwise.
    /// </summary>
    public (string Key, int Index)? SpawnPoint { get; init; }

    /// <summary>
    /// The pin of a spawn part (<see cref="SpawnMarkers.IsSpawnPart"/>: a building's fixed-item spawner or vehicle box) or a
    /// mesh of the item such a spawner spawns (its drill press, stove or fridge, drawn under <c>Spawner/...</c>): the part's
    /// component name, so a click picks that part and pin and item move together; null otherwise.
    /// </summary>
    public string? Spawner { get; init; }
}

/// <summary>
/// Identifies one ISM/HISM instance placement inside a loaded scene: the owning actor's selectable id, the component
/// (stored lower-case, UE names are case-insensitive) and the instance index. Used to hide deleted instances.
/// </summary>
/// <param name="SelectableId">Selectable id of the owning actor.</param>
/// <param name="Component">Component name, lower-case.</param>
/// <param name="InstanceIndex">Instance index in the pristine <c>PerInstanceSMData</c>.</param>
public readonly record struct InstanceKey(uint SelectableId, string Component, int InstanceIndex)
{
    /// <summary><see cref="InstanceIndex"/> of a whole spline mesh component (one road or bridge piece).</summary>
    public const int Segment = -1;

    /// <summary><see cref="InstanceIndex"/> of one component of an actor (a part of a Blueprint building), picked in part mode.</summary>
    public const int Part = -2;

    /// <summary>
    /// <see cref="InstanceIndex"/> of the first loot point of an item spawner component (a building's shelf): point <c>i</c>
    /// is <c>LootPoint - i</c>. Picked on its own to show what spawns there; it moves with its building.
    /// </summary>
    public const int LootPoint = -1000;

    /// <summary>
    /// <see cref="InstanceIndex"/> of the first point of a spawner's stored point array (a sentry's patrol path, an item
    /// spawner group's loot points): point <c>i</c> is <c>SpawnPointBase - i</c>, <see cref="Component"/> is the array's key.
    /// Picked, moved, copied and deleted on its own; the pins are regenerated from the current list (<c>LevelScene.ReplacePins</c>).
    /// </summary>
    public const int SpawnPointBase = -1_000_000;

    /// <summary>True for a loot point key (see <see cref="LootPoint"/>).</summary>
    public bool IsLootPoint => InstanceIndex <= LootPoint && !IsSpawnPoint;

    /// <summary>The marker index of a loot point key.</summary>
    public int Marker => LootPoint - InstanceIndex;

    /// <summary>True for a spawn point key (see <see cref="SpawnPointBase"/>).</summary>
    public bool IsSpawnPoint => InstanceIndex <= SpawnPointBase;

    /// <summary>The point's index in its array for a spawn point key.</summary>
    public int Point => SpawnPointBase - InstanceIndex;

    /// <summary>Creates a key, normalising the component name.</summary>
    public static InstanceKey Of(uint selectableId, string component, int instanceIndex) =>
        new(selectableId, component.ToLowerInvariant(), instanceIndex);
}

/// <summary>
/// An actor added by the project as a copy of a loaded actor, drawn by cloning the source's placements
/// (see <c>LevelScene.AddClone</c>).
/// </summary>
/// <param name="Id">Selectable id of the clone (outside the ids of the loaded documents).</param>
/// <param name="SourceId">Selectable id of the copied actor.</param>
/// <param name="RootWorld">World transform of the clone's root component (UE space).</param>
/// <param name="Name">Actor name of the clone.</param>
/// <param name="MeshPath">For a new mesh actor (<paramref name="SourceId"/> 0): the static mesh to draw at <paramref name="RootWorld"/>.</param>
/// <param name="Placements">
/// For a copy of an actor whose level is not loaded (<paramref name="SourceId"/> 0): that actor's placements, read from its
/// level, cloned instead of a loaded actor's.
/// </param>
public sealed record ActorClone(uint Id, uint SourceId, FTransform RootWorld, string Name, string? MeshPath = null, IReadOnlyList<ScenePlacement>? Placements = null);

/// <summary>
/// A curve the viewport draws with its draggable handles (Shape menu): two that push it sideways, one on each end that
/// moves the end (longer, shorter, welded to another piece), and one on each corner of each end (wider, narrower).
/// <paramref name="Base"/> is the curve before any of them (component space), <paramref name="ComponentWorld"/> places it.
/// </summary>
/// <param name="ComponentWorld">World transform of the spline mesh component (or of the bent actor's root, scale 1).</param>
/// <param name="Base">The curve without the handles' pushes and end edits.</param>
/// <param name="Sway1">First handle's push, cm (positive right).</param>
/// <param name="Sway2">Second handle's push, cm.</param>
/// <param name="MeshBounds">The straight mesh's bounds (where the corners are).</param>
/// <param name="Start">The start's edit.</param>
/// <param name="End">The end's edit.</param>
public sealed record ShapeHandleInfo(FTransform ComponentWorld, SplineMeshParams Base, float Sway1, float Sway2, BoundingBox MeshBounds, SplineEnd Start = default, SplineEnd End = default)
{
    /// <summary>The curve with its ends edited, before the sideways pushes (where the push handles rest).</summary>
    public SplineMeshParams Unpushed => SplineEnds.Apply(Base, Start, End);

    /// <summary>The curve as drawn.</summary>
    public SplineMeshParams Shaped => SplineEnds.Shape(Base, Sway1, Sway2, Start, End);
}

/// <summary>
/// A single object the viewport draws scale handles on (owner: "click an object and make it bigger or longer"): its ends
/// make it longer or shorter, a top corner bigger or smaller.
/// </summary>
/// <param name="World">Where it stands, with its scale (UE world).</param>
/// <param name="Bounds">Its mesh's bounds in its own space.</param>
public sealed record ScaleHandleInfo(FTransform World, BoundingBox Bounds);

/// <summary>One object of a multi-selection where it stands now: an actor's root (<paramref name="Instance"/> null) or one instance or road piece.</summary>
/// <param name="Id">Selectable id of the actor.</param>
/// <param name="Instance">The instance or segment, or null for the whole actor.</param>
/// <param name="World">Its world transform (UE space).</param>
public sealed record GroupWorld(uint Id, InstanceKey? Instance, FTransform World);

/// <summary>A mesh loaded after the scene was prepared (for actors added by the project), with its decoded textures.</summary>
/// <param name="Asset">The mesh.</param>
/// <param name="Textures">Decoded textures by path (the asset's base colour, when any).</param>
public sealed record ExtraMesh(PreparedMeshAsset Asset, IReadOnlyDictionary<string, TextureImage> Textures);

/// <summary>
/// A mesh ready for upload: its LOD chain plus the decoded base colour textures of its materials (by path, shared
/// between meshes).
/// </summary>
/// <param name="MeshPath">Object path of the mesh.</param>
/// <param name="Mesh">The finest LOD (<c>Lods[0]</c>).</param>
/// <param name="TexturePath">Base colour texture of the first material that has one (the default for sections without their own), or null.</param>
public sealed record PreparedMeshAsset(string MeshPath, MeshData Mesh, string? TexturePath)
{
    /// <summary>The LOD chain, finest first (at least <see cref="Mesh"/>).</summary>
    public IReadOnlyList<MeshData> Lods { get; init; } = [Mesh];

    /// <summary>Per LOD, the screen size below which the next coarser LOD takes over (see <c>MeshLodChain</c>).</summary>
    public IReadOnlyList<float> LodScreenSizes { get; init; } = [1f];

    /// <summary>Base colour texture path per material object path (only materials with a texture); sections look their texture up by <c>MeshSection.MaterialName</c>.</summary>
    public IReadOnlyDictionary<string, string> MaterialTextures { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Masked materials (leaves, grass, chain-link): material path → opacity clip value applied to the texture's alpha.</summary>
    public IReadOnlyDictionary<string, float> MaterialAlphaCutoffs { get; init; } = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Materials without a texture (glass, painted plastic, rubber): material path → their colour (linear RGBA).</summary>
    public IReadOnlyDictionary<string, Vector4> MaterialTints { get; init; } = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opaque materials with a roughness range (SCUM's master shader scales the base colour's alpha into it): material path
    /// → (min, max). Their normal maps are in <see cref="MaterialTextures"/> under the material path plus <c>GpuMesh.NormalMapSuffix</c>.
    /// </summary>
    public IReadOnlyDictionary<string, Vector2> MaterialRoughness { get; init; } = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mesh's material per slot (index = slot, the order <c>OverrideMaterials</c> addresses).</summary>
    public IReadOnlyList<string> MaterialSlots { get; init; } = [];

    /// <summary>
    /// Every slot is on the engine's grid material: a volume the game never draws (weather masks, environment
    /// descriptions) unless a placement gives it real materials through <c>OverrideMaterials</c>.
    /// </summary>
    public bool IsEditorOnly { get; init; }

    /// <summary>A spawn stand-in: its translucent materials pulse in opacity ("keep glinting") with the renderer's time (<c>SceneRenderer.Time</c>).</summary>
    public bool Shimmer { get; init; }

    /// <summary>A camera-facing card (an item's inventory icon at a loot point): drawn spanning the camera's right and up.</summary>
    public bool Billboard { get; init; }

    /// <summary>Every material is water (a lake surface, a river piece): drawn with the sea's water shading (<c>GpuMesh.Water</c>).</summary>
    public bool Water { get; init; }
}

/// <summary>What a material contributes to the viewport: its base-colour texture (or colour without one) and, when masked, the alpha clip value.</summary>
internal readonly record struct MaterialLook(string? Texture, float AlphaCutoff, Vector4 Tint, string? Normal = null, Vector2 Roughness = default);

/// <summary>A terrain component ready for upload (positions already in UE world centimetres).</summary>
public sealed record PreparedTerrain(string Name, string LevelName, MeshData Mesh)
{
    /// <summary>Baked ground texture (null in <see cref="GroundMode.Plain"/> or when no height data was kept).</summary>
    public TerrainAlbedo? Albedo { get; init; }

    /// <summary>Full-resolution heights and normals of the component.</summary>
    public LandscapeSurface? Surface { get; init; }

    /// <summary>Decoded paint layers of the component.</summary>
    public LandscapeComponentLayers? Layers { get; init; }
}

/// <summary>
/// Everything a level scene needs, computed on any thread (no GL): placements, distinct meshes, decoded textures and
/// terrain. <see cref="LevelSceneUploader"/> turns it into a <see cref="LevelScene"/> on the render thread.
/// </summary>
public sealed class PreparedLevelScene
{
    internal PreparedLevelScene(
        IReadOnlyList<LevelDocument> documents,
        IReadOnlyList<ScenePlacement> placements,
        IReadOnlyDictionary<string, PreparedMeshAsset> meshes,
        IReadOnlyDictionary<string, TextureImage> textures,
        IReadOnlyList<string> missingMeshes,
        IReadOnlyList<PreparedTerrain> terrain,
        IReadOnlyList<string> warnings,
        TimeSpan elapsed)
    {
        Documents = documents;
        Placements = placements;
        Meshes = meshes;
        Textures = textures;
        MissingMeshes = missingMeshes;
        Terrain = terrain;
        Warnings = warnings;
        Elapsed = elapsed;
    }

    /// <summary>The level documents (a placement's <see cref="ScenePlacement.DocumentIndex"/> is the slot of its document, see <see cref="Slots"/>).</summary>
    public IReadOnlyList<LevelDocument> Documents { get; }

    /// <summary>The slot of each document (index-aligned with <see cref="Documents"/>; see <see cref="LevelSceneOptions.DocumentSlots"/>), or null when every slot is the index.</summary>
    public IReadOnlyList<int>? Slots { get; init; }

    /// <summary>The slot of the document at <paramref name="documentIndex"/>: the number its actors' ids are made from.</summary>
    public int SlotOf(int documentIndex) => Slots is { } slots ? slots[documentIndex] : documentIndex;

    /// <summary>The selectable id of <paramref name="actor"/> of the document at <paramref name="documentIndex"/>.</summary>
    public uint IdOf(int documentIndex, ActorRecord actor) => LevelScenePreparer.SelectableIdOf(SlotOf(documentIndex), actor, IdShift);

    /// <summary>The id shift the scene was prepared with (<see cref="LevelSceneOptions.DocumentIdShift"/>).</summary>
    public int IdShift { get; init; } = 20;

    /// <summary>The straight meshes of the spline pieces (roads, rails, bridges), by path: CPU only, for bending a piece anew.</summary>
    public IReadOnlyDictionary<string, PreparedMeshAsset> SplineSources { get; init; } = new Dictionary<string, PreparedMeshAsset>();

    /// <summary>The ground layers' diffuse textures (Realistic ground), for the sharp close-up ground; null without them.</summary>
    public TerrainLayerTextures? LayerTextures { get; init; }

    /// <summary>Placements whose mesh was loaded.</summary>
    public IReadOnlyList<ScenePlacement> Placements { get; }

    /// <summary>Distinct meshes by object path.</summary>
    public IReadOnlyDictionary<string, PreparedMeshAsset> Meshes { get; }

    /// <summary>Decoded textures by object path.</summary>
    public IReadOnlyDictionary<string, TextureImage> Textures { get; }

    /// <summary>Meshes that could not be loaded (object path plus reason).</summary>
    public IReadOnlyList<string> MissingMeshes { get; }

    /// <summary>Terrain components.</summary>
    public IReadOnlyList<PreparedTerrain> Terrain { get; }

    /// <summary>Non-fatal problems.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Preparation time.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Total placements requested, including those whose mesh is missing.</summary>
    public int RequestedPlacements { get; init; }

    /// <summary>Total triangles of the distinct meshes (finest LOD).</summary>
    public long MeshTriangles => Meshes.Values.Sum(m => (long)m.Mesh.TriangleCount);

    /// <summary>Total triangles of every uploaded LOD of the distinct meshes.</summary>
    public long LodTriangles => Meshes.Values.Sum(m => m.Lods.Sum(l => (long)l.TriangleCount));

    /// <summary>Number of distinct meshes with more than one LOD.</summary>
    public int MeshesWithLods => Meshes.Values.Count(m => m.Lods.Count > 1);

    /// <summary>Total terrain triangles.</summary>
    public long TerrainTriangles => Terrain.Sum(t => (long)t.Mesh.TriangleCount);

    /// <summary>Ground mode the terrain textures were baked for.</summary>
    public GroundMode Ground { get; init; } = GroundMode.Realistic;

    /// <summary>Height of the sea plane to draw (UE centimetres), or null for none.</summary>
    public float? SeaLevelCm { get; init; }

    /// <summary>Ground queries over the terrain (height, normal, layers, raycast); null without terrain.</summary>
    public TerrainHeightField? HeightField { get; init; }

    /// <summary>The layer look table the terrain was baked with (null without terrain).</summary>
    public TerrainLayerCatalog? LayerCatalog { get; init; }

    /// <summary>Layer textures named by the catalog that are not in the game files (fallback colours were used).</summary>
    public IReadOnlyList<string> MissingLayerTextures { get; init; } = [];

    /// <summary>Wall time of the terrain colour bake.</summary>
    public TimeSpan TerrainBakeTime { get; init; }
}

/// <summary>Builds <see cref="PreparedLevelScene"/>s from level documents (CPU only, safe on a worker thread).</summary>
public sealed class LevelScenePreparer
{
    private readonly AssetCatalog _catalog;
    private readonly ILogger _logger;
    private SpawnModels? _models;
    private MeshPreviewLoader? _previews;

    /// <summary>Creates a preparer over <paramref name="catalog"/>.</summary>
    public LevelScenePreparer(AssetCatalog catalog, ILogger? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Slots for <paramref name="documents"/> (see <see cref="LevelSceneOptions.DocumentSlots"/>) when they replace
    /// <paramref name="shown"/>: a level shown there keeps its slot, a new one takes the lowest slot no level of the new
    /// set holds (so a first load numbers its levels 0, 1, 2, … like their indices).
    /// </summary>
    public static int[] SlotsAfter(IReadOnlyList<LevelDocument> documents, PreparedLevelScene? shown, int shift = 20)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var previous = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; shown is not null && i < shown.Documents.Count; i++)
        {
            previous.TryAdd(shown.Documents[i].PackagePath, shown.SlotOf(i));
        }

        var slots = new int[documents.Count];
        var used = new HashSet<int>();
        for (var i = 0; i < documents.Count; i++)
        {
            slots[i] = previous.TryGetValue(documents[i].PackagePath, out var kept) && used.Add(kept) ? kept : -1;
        }

        var free = 0;
        for (var i = 0; i < documents.Count; i++)
        {
            if (slots[i] < 0)
            {
                while (used.Contains(free))
                {
                    free++;
                }

                used.Add(free);
                slots[i] = free;
            }
        }

        // Ids must stay below 2^31 (added actors and previews number from there): numbered anew in the unlikely case they would not.
        return slots.Any(s => s >= 1 << (31 - shift)) ? Enumerable.Range(0, documents.Count).ToArray() : slots;
    }

    /// <summary>Selectable id of an actor: <c>documentIndex &lt;&lt; shift | exportIndex + 1</c> (the document's slot, see <see cref="LevelSceneOptions.DocumentSlots"/>).</summary>
    public static uint SelectableIdOf(int documentIndex, ActorRecord actor, int shift = 20) =>
        ((uint)documentIndex << shift) | (uint)(actor.ExportIndex + 1);

    /// <summary>
    /// Every mesh placement of <paramref name="document"/> (component meshes, ISM/HISM instances and spawn pins; with
    /// <paramref name="models"/> the pins of known objects are those objects, see <see cref="SpawnMarkers.PinsOf"/>).
    /// </summary>
    public static List<ScenePlacement> CollectPlacements(LevelDocument document, int documentIndex, LevelSceneOptions? options = null, SpawnModels? models = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new LevelSceneOptions();
        var placements = new List<ScenePlacement>();
        foreach (var actor in document.Actors)
        {
            if (options.Filter is { Length: > 0 } text && !Matches(actor, text))
            {
                continue;
            }

            var id = SelectableIdOf(documentIndex, actor, options.DocumentIdShift);
            foreach (var component in actor.Components)
            {
                // Hidden-in-game components (e.g. EnvironmentDescription helper meshes) are never drawn.
                if (component.StaticMeshPath is null || component.IsInstanced || !component.IsVisible || IsHelperMesh(actor, component))
                {
                    continue;
                }

                // A spawner's drawn item (Spawner/ItemMesh0) picks and moves as that spawner part.
                var slash = component.IsSynthesized ? component.Name.IndexOf('/', StringComparison.Ordinal) : -1;
                var spawner = slash > 0 && actor.FindComponent(component.Name[..slash]) is { } owner && SpawnMarkers.IsSpawnPart(actor, owner) ? owner.Name : null;
                placements.Add(new ScenePlacement(component.StaticMeshPath, UeToGl.ModelMatrix(component.WorldTransform), component.WorldTransform, id,
                    $"{actor.Name}/{component.Name}", documentIndex, actor, component, null) { Spawner = spawner });
            }

            placements.AddRange(PinPlacements(actor, id, documentIndex, options.DocumentIdShift, models));
            if (!options.IncludeInstances)
            {
                continue;
            }

            foreach (var instance in actor.InstanceTransforms)
            {
                if (instance.StaticMeshPath is null)
                {
                    continue;
                }

                placements.Add(new ScenePlacement(instance.StaticMeshPath, UeToGl.ModelMatrix(instance.WorldTransform), instance.WorldTransform, id,
                    $"{actor.Name}/{instance.ComponentName}[{instance.InstanceIndex}]", documentIndex, actor, null, instance)
                {
                    CullDistance = instance.EndCullDistance,
                });
            }
        }

        return placements;

        static bool IsHelperMesh(ActorRecord actor, ComponentRecord component) => HelperMeshes.IsHelper(actor, component);

        static bool Matches(ActorRecord actor, string text) =>
            actor.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || actor.ClassName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (actor.StaticMeshPath?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
            || actor.Components.Any(c => c.StaticMeshPath?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// The spawn pins of <paramref name="actor"/> (see <see cref="SpawnMarkers.PinsOf"/>): pins that pick as the actor use its
    /// id; a building's loot point picks as itself (the building's id with its own key: what spawns there, owner: "the pin
    /// tells me nothing"); a spawn part (a fixed-item spawner, a car shop's vehicle box) picks as that part of the building; a
    /// point of a stored point array picks as that point; a vehicle box the level does not store gets an id of its own (bit
    /// 19), which picks nothing. A viewport calls this again with the actor's points as the project has them (with the
    /// same <paramref name="models"/>, so the pins keep the meshes the scene has).
    /// </summary>
    public static IEnumerable<ScenePlacement> PinPlacements(ActorRecord actor, uint id, int documentIndex, int idShift = 20, SpawnModels? models = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        foreach (var (kind, pin, picksActor, label, marker, part, point, model) in SpawnMarkers.PinsOf(actor, models))
        {
            var pinId = picksActor || marker is not null || part is not null || point is not null ? id : id | (1u << (idShift - 1));
            yield return new ScenePlacement(SpawnMarkers.MeshKey(kind, model), UeToGl.ModelMatrix(pin), pin, pinId, label, documentIndex, actor, null, null)
            {
                LootMarker = marker,
                Spawner = part?.Name,
                SpawnPoint = point,
            };
        }
    }

    /// <summary>
    /// Terrain only (no actors) of many landscape tiles — the whole island as a backdrop. Tiles are read in parallel and
    /// baked one by one, and their height/weight data is dropped after baking so 400 tiles fit in memory.
    /// </summary>
    public PreparedLevelScene PrepareTerrain(
        IReadOnlyList<string> tilePackages,
        LevelSceneOptions? options = null,
        IProgress<(int Done, int Total, string Item)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tilePackages);
        options ??= new LevelSceneOptions();
        var clock = Stopwatch.StartNew();
        var settings = CreateBakeSettings(options, [], out _);
        var parts = new System.Collections.Concurrent.ConcurrentBag<(int Order, PreparedTerrain Terrain)>();
        var warnings = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = 0;
        // ponytail: 4 readers in parallel; raise only if CUE4Parse proves thread-safe beyond that
        Parallel.For(0, tilePackages.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, i =>
        {
            var name = tilePackages[i][(tilePackages[i].LastIndexOf('/') + 1)..];
            try
            {
                var extract = new LandscapeExtractOptions { Step = Math.Max(1, options.LandscapeStep), ReadLayers = true, ReadGrass = false };
                var tile = LandscapeExtractor.Extract(_catalog, tilePackages[i], extract)
                    .SelectMany(p => p.Components)
                    .Select(c => new PreparedTerrain(c.Name, name, c.Mesh) { Surface = c.Surface, Layers = c.Layers })
                    .ToList();
                var albedo = BakeTerrain(tile, settings, out _, cancellationToken);
                for (var k = 0; k < tile.Count; k++)
                {
                    parts.Add((i, tile[k] with { Albedo = albedo[k], Surface = null, Layers = null }));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                warnings.Add($"{name}: landscape could not be built ({ex.Message}).");
            }

            progress?.Report((Interlocked.Increment(ref done), tilePackages.Count, name));
        });

        var terrain = parts.OrderBy(p => p.Order).Select(p => p.Terrain).ToList();
        _logger.LogInformation("Prepared the terrain of {Tiles} tiles ({Components} components) in {Ms:0} ms.", tilePackages.Count, terrain.Count, clock.Elapsed.TotalMilliseconds);
        return new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, TextureImage>(), [], terrain, warnings.ToList(), clock.Elapsed)
        {
            Ground = options.Ground,
            SeaLevelCm = options.SeaPlane == false ? null : options.SeaLevelCm,
        };
    }

    /// <summary>
    /// Prepares the scene of <paramref name="documents"/>: loads meshes, decodes textures, builds terrain. With a
    /// <paramref name="cache"/>, meshes, textures, bent spline pieces and baked terrain that an earlier preparation made
    /// are reused (the levels around a moving camera share most of them).
    /// </summary>
    public PreparedLevelScene Prepare(
        IReadOnlyList<LevelDocument> documents,
        LevelSceneOptions? options = null,
        IProgress<(int Done, int Total, string Item)>? progress = null,
        CancellationToken cancellationToken = default,
        LevelPrepareCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        options ??= new LevelSceneOptions();
        if (cache is null)
        {
            return PrepareCore(documents, options, progress, null, cancellationToken);
        }

        lock (cache.Gate)
        {
            cache.Begin(options);
            try
            {
                return PrepareCore(documents, options, progress, cache, cancellationToken);
            }
            finally
            {
                cache.Trim();
            }
        }
    }

    private PreparedLevelScene PrepareCore(
        IReadOnlyList<LevelDocument> documents,
        LevelSceneOptions options,
        IProgress<(int Done, int Total, string Item)>? progress,
        LevelPrepareCache? cache,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var warnings = new List<string>();

        if (options.DocumentSlots is { } slots && slots.Count != documents.Count)
        {
            throw new ArgumentException($"{slots.Count} slots for {documents.Count} documents.", nameof(options));
        }

        var models = cache is null ? _models ??= new SpawnModels(_catalog, _logger) : cache.ModelsFor(_catalog, _logger);
        var requested = new List<ScenePlacement>();
        for (var i = 0; i < documents.Count; i++)
        {
            requested.AddRange(CollectPlacements(documents[i], options.DocumentSlots?[i] ?? i, options, models));
        }

        var meshPaths = requested.Select(p => p.MeshPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var meshes = new Dictionary<string, PreparedMeshAsset>(StringComparer.OrdinalIgnoreCase);
        var textures = cache?.Textures ?? new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        var materials = cache?.Materials ?? new Dictionary<string, MaterialLook>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var total = meshPaths.Count + (options.IncludeLandscape ? documents.Count : 0);
        var done = 0;
        var prefetch = PrefetchMeshes(meshPaths.Where(p => !(cache?.Meshes.ContainsKey(p) ?? false) && SpawnMarkers.KindOfMesh(p) is null).ToList(),
            options, textures, materials, progress, total, cancellationToken);
        foreach (var meshPath in meshPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prefetch.Meshes.ContainsKey(meshPath))
            {
                done++; // reported while it was read
            }
            else
            {
                progress?.Report((done++, total, meshPath));
            }

            PreparedMeshAsset? asset;
            string reason;
            if (cache is not null && cache.Meshes.TryGetValue(meshPath, out var hit))
            {
                (asset, reason) = (hit.Asset, hit.Reason);
                cache.Meshes[meshPath] = hit with { Used = cache.Generation };
            }
            else if (SpawnMarkers.ModelOf(meshPath) is { } model && SpawnMarkers.KindOfMesh(meshPath) is { } kind)
            {
                // The object that spawns there, half transparent; its plain shape when the model cannot be loaded.
                asset = TryLoadModel(model, options, textures, materials, out var standIn, out reason)
                    ? SpawnMarkers.StandIn(standIn, kind, meshPath)
                    : SpawnMarkers.AssetFor(meshPath);
                reason = string.Empty;
                if (cache is not null)
                {
                    cache.Meshes[meshPath] = (asset, reason, cache.Generation);
                }
            }
            else if (SpawnMarkers.AssetFor(meshPath) is { } marker)
            {
                (asset, reason) = (marker, string.Empty);
            }
            else
            {
                asset = TryLoadMesh(meshPath, options, textures, materials, out var loaded, out reason, prefetch: prefetch) ? loaded : null;
                if (cache is not null)
                {
                    cache.Meshes[meshPath] = (asset, reason, cache.Generation);
                }
            }

            if (asset is not null)
            {
                meshes[meshPath] = asset;
            }
            else if (reason != EditorOnlyReason)
            {
                missing.Add($"{meshPath} ({reason})");
            }
        }

        // Placements that repaint their mesh (OverrideMaterials: a house in another colour, a car without snow) draw a
        // variant of it whose sections use the replacement materials.
        for (var i = 0; i < requested.Count; i++)
        {
            if (requested[i].Component?.OverrideMaterials is not { } overrides || !meshes.TryGetValue(requested[i].MeshPath, out var baseAsset))
            {
                continue;
            }

            var key = $"{requested[i].MeshPath}#mat:{string.Join(",", overrides.Select(o => o ?? string.Empty))}";
            if (!meshes.ContainsKey(key))
            {
                PreparedMeshAsset? variant;
                if (cache is not null && cache.Variants.TryGetValue(key, out var kept))
                {
                    variant = kept.Asset;
                    cache.Variants[key] = kept with { Used = cache.Generation };
                }
                else
                {
                    variant = MakeVariant(baseAsset, overrides, key, options, textures, materials);
                    if (cache is not null)
                    {
                        cache.Variants[key] = (variant, cache.Generation);
                    }
                }

                if (variant is null)
                {
                    continue; // nothing usable to repaint with: the mesh's own materials
                }

                meshes[key] = variant;
            }

            requested[i] = requested[i] with { MeshPath = key };
        }

        var splineCopies = SplineMeshPlacements.Apply(requested, meshes, cache);

        var terrain = new List<PreparedTerrain>();
        var bakedFromCache = cache is not null;
        var tiles = new List<string>(); // landscape documents in order (cache path)
        var unbaked = new List<(string Path, List<PreparedTerrain> Terrain)>();
        if (options.IncludeLandscape)
        {
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((done++, total, document.Name));
                if (!document.Actors.Any(a => LandscapeExtractor.IsLandscapeProxyClass(a.ClassName)))
                {
                    continue;
                }

                if (cache is not null)
                {
                    // Baked once per tile (below, all new tiles in one go); every tile shares one set of bake settings
                    // (island-wide height range).
                    tiles.Add(document.PackagePath);
                    if (!cache.Terrain.ContainsKey(document.PackagePath) && !unbaked.Any(u => string.Equals(u.Path, document.PackagePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        unbaked.Add((document.PackagePath, ExtractTerrain(document, options, warnings)));
                    }

                    continue;
                }

                try
                {
                    var extractOptions = new LandscapeExtractOptions { Step = Math.Max(1, options.LandscapeStep), ReadLayers = true, ReadGrass = false };
                    foreach (var proxy in LandscapeExtractor.Extract(_catalog, document.PackagePath, extractOptions))
                    {
                        warnings.AddRange(proxy.Warnings.Select(w => $"{document.Name}: {w}"));
                        foreach (var c in proxy.Components)
                        {
                            warnings.AddRange((c.Layers?.Warnings ?? []).Select(w => $"{document.Name}: {w}"));
                            terrain.Add(new PreparedTerrain(c.Name, document.Name, c.Mesh) { Surface = c.Surface, Layers = c.Layers });
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    warnings.Add($"{document.Name}: landscape could not be built ({ex.Message}).");
                }
            }
        }

        if (cache is not null)
        {
            // One bake over the components of every new tile keeps all cores busy (a tile has only four); each component
            // bakes on its own, so the colours are the same as tile by tile.
            if (unbaked.Count > 0)
            {
                cache.BakeSettings ??= CreateBakeSettings(options, [], out cache.MissingLayerTextures);
                var all = unbaked.SelectMany(u => u.Terrain).ToList();
                var albedo = all.Count == 0 ? [] : BakeTerrain(all, cache.BakeSettings, out _, cancellationToken);
                var first = 0;
                foreach (var (path, extracted) in unbaked)
                {
                    cache.Terrain[path] = (extracted.Select((t, i) => t with { Albedo = albedo[first + i] }).ToList(), 0);
                    first += extracted.Count;
                }
            }

            foreach (var path in tiles)
            {
                var tile = cache.Terrain[path];
                cache.Terrain[path] = tile with { Used = cache.Generation };
                terrain.AddRange(tile.Terrain);
            }
        }

        var ground = bakedFromCache ? FinishGround(terrain, options, cache!.BakeSettings, cache.MissingLayerTextures) : PrepareGround(terrain, options, warnings, cancellationToken);
        var placements = requested.Where(p => meshes.TryGetValue(p.MeshPath, out var m) && !m.IsEditorOnly).ToList();
        // The straight meshes of the road, rail and bridge pieces stay on the CPU side, so a piece can be bent anew.
        var splineSources = new Dictionary<string, PreparedMeshAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var placement in placements)
        {
            var marker = placement.MeshPath.IndexOf(SplineMeshPlacements.KeyMarker, StringComparison.Ordinal);
            if (marker > 0 && meshes.TryGetValue(placement.MeshPath[..marker], out var straight))
            {
                splineSources.TryAdd(straight.MeshPath, straight);
            }
        }

        // Only what is drawn goes to the GPU (bases of repainted variants, hidden volumes stay behind).
        meshes = placements.Select(p => p.MeshPath).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(k => k, k => meshes[k], StringComparer.OrdinalIgnoreCase);
        if (cache is not null)
        {
            // The shared texture pool holds every cached mesh's textures; the scene carries the ones it draws.
            textures = meshes.Values.SelectMany(LevelPrepareCache.TexturePathsOf).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(textures.ContainsKey).ToDictionary(p => p, p => textures[p], StringComparer.OrdinalIgnoreCase);
        }

        _logger.LogInformation("Prepared {Levels} level(s): {Placements}/{Requested} placements ({WithCull} with a cull distance), {Meshes} meshes ({WithLods} with LODs, {Missing} missing, {Splines} bent along splines), {Textures} textures for {Materials} materials, {Terrain} terrain components ({Ground}, baked in {BakeMs:0} ms) in {Ms:0} ms.",
            documents.Count, placements.Count, requested.Count, placements.Count(p => p.CullDistance > 0f), meshes.Count, meshes.Values.Count(m => m.Lods.Count > 1), missing.Count, splineCopies,
            textures.Count, materials.Count, ground.Terrain.Count, options.Ground, ground.BakeTime.TotalMilliseconds, clock.Elapsed.TotalMilliseconds);
        return new PreparedLevelScene(documents, placements, meshes, textures, missing, ground.Terrain, warnings, clock.Elapsed)
        {
            RequestedPlacements = requested.Count,
            Slots = options.DocumentSlots,
            IdShift = options.DocumentIdShift,
            Ground = options.Ground,
            SeaLevelCm = ground.SeaLevelCm,
            HeightField = ground.HeightField,
            LayerCatalog = ground.Catalog,
            LayerTextures = ground.Textures,
            MissingLayerTextures = ground.MissingTextures,
            TerrainBakeTime = ground.BakeTime,
            SplineSources = splineSources,
        };
    }

    /// <summary>
    /// Bakes the ground textures of <paramref name="terrain"/> for <paramref name="settings"/> (CPU, parallel; safe on a
    /// worker thread). The result is index-aligned with the terrain list; pass it to <see cref="LevelScene.SetTerrainAlbedo"/>
    /// on the render thread to switch ground modes without reloading the levels.
    /// </summary>
    public static IReadOnlyList<TerrainAlbedo?> BakeTerrain(IReadOnlyList<PreparedTerrain> terrain, TerrainBakeSettings settings, out TimeSpan elapsed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(settings);
        var bakeable = new List<(LandscapeSurface, LandscapeComponentLayers?)>();
        var map = new int[terrain.Count];
        for (var i = 0; i < terrain.Count; i++)
        {
            map[i] = terrain[i].Surface is { } surface ? bakeable.Count : -1;
            if (terrain[i].Surface is { } s)
            {
                bakeable.Add((s, terrain[i].Layers));
            }
        }

        var baked = TerrainAlbedoBaker.BakeAll(bakeable, settings, out elapsed, cancellationToken: cancellationToken);
        return map.Select(m => m < 0 ? null : baked[m]).ToList();
    }

    /// <summary>
    /// Bake settings for <paramref name="options"/>: in Realistic mode with <see cref="LevelSceneOptions.ResolveLayerTextures"/>
    /// the layer textures are decoded once from the game files (tiled by the baker; their mean colours go into the catalog).
    /// </summary>
    public TerrainBakeSettings CreateBakeSettings(LevelSceneOptions options, IReadOnlyList<PreparedTerrain> terrain, out IReadOnlyList<string> missingTextures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(terrain);
        var catalog = options.LayerCatalog ?? TerrainLayerCatalog.LoadDefault();
        missingTextures = [];
        TerrainLayerTextures? textures = null;
        if (options.ResolveLayerTextures && options.Ground == GroundMode.Realistic)
        {
            var set = catalog.LoadTextures(_catalog);
            textures = set.Images.Count > 0 ? new TerrainLayerTextures(set.Images) : null;
            catalog = catalog.WithTextureColors(set);
            missingTextures = set.Missing;
        }

        catalog = catalog.WithRules(catalog.Rules with { SeaLevelCm = options.SeaLevelCm });
        var min = terrain.Where(t => t.Surface is not null).Select(t => t.Surface!.MinHeightCm).DefaultIfEmpty(-8000f).Min();
        var max = terrain.Where(t => t.Surface is not null).Select(t => t.Surface!.MaxHeightCm).DefaultIfEmpty(30000f).Max();
        return new TerrainBakeSettings
        {
            Mode = options.Ground,
            Catalog = catalog,
            Textures = textures,
            TextureSize = options.TerrainTextureSize,
            HeightMinCm = MathF.Min(min, options.SeaLevelCm - 100f),
            HeightMaxCm = MathF.Max(max, options.SeaLevelCm + 1000f),
        };
    }

    /// <summary>The terrain components of one landscape document (not baked).</summary>
    private List<PreparedTerrain> ExtractTerrain(LevelDocument document, LevelSceneOptions options, List<string> warnings)
    {
        var terrain = new List<PreparedTerrain>();
        try
        {
            var extractOptions = new LandscapeExtractOptions { Step = Math.Max(1, options.LandscapeStep), ReadLayers = true, ReadGrass = false };
            foreach (var proxy in LandscapeExtractor.Extract(_catalog, document.PackagePath, extractOptions))
            {
                warnings.AddRange(proxy.Warnings.Select(w => $"{document.Name}: {w}"));
                foreach (var c in proxy.Components)
                {
                    warnings.AddRange((c.Layers?.Warnings ?? []).Select(w => $"{document.Name}: {w}"));
                    terrain.Add(new PreparedTerrain(c.Name, document.Name, c.Mesh) { Surface = c.Surface, Layers = c.Layers });
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warnings.Add($"{document.Name}: landscape could not be built ({ex.Message}).");
        }

        return terrain;
    }

    /// <summary>Sea plane and height field over terrain that is already baked (cached tiles).</summary>
    private static (List<PreparedTerrain> Terrain, float? SeaLevelCm, TerrainHeightField? HeightField, TerrainLayerCatalog? Catalog, IReadOnlyList<string> MissingTextures, TimeSpan BakeTime, TerrainLayerTextures? Textures)
        FinishGround(List<PreparedTerrain> baked, LevelSceneOptions options, TerrainBakeSettings? settings, IReadOnlyList<string> missingTextures)
    {
        if (baked.Count == 0)
        {
            return (baked, null, null, null, [], TimeSpan.Zero, null);
        }

        var minZ = baked.Min(t => t.Surface?.MinHeightCm ?? t.Mesh.Bounds.Min.Z);
        float? sea = options.SeaPlane switch
        {
            true => options.SeaLevelCm,
            false => null,
            null => minZ < options.SeaLevelCm ? options.SeaLevelCm : null,
        };
        var surfaces = baked.Where(t => t.Surface is not null).Select(t => (t.Surface!, t.Layers)).ToList();
        return (baked, sea, surfaces.Count > 0 ? new TerrainHeightField(surfaces) : null, settings?.Catalog, missingTextures, TimeSpan.Zero, settings?.Textures);
    }

    private (List<PreparedTerrain> Terrain, float? SeaLevelCm, TerrainHeightField? HeightField, TerrainLayerCatalog? Catalog, IReadOnlyList<string> MissingTextures, TimeSpan BakeTime, TerrainLayerTextures? Textures)
        PrepareGround(List<PreparedTerrain> terrain, LevelSceneOptions options, List<string> warnings, CancellationToken cancellationToken)
    {
        if (terrain.Count == 0)
        {
            return (terrain, null, null, null, [], TimeSpan.Zero, null);
        }

        var settings = CreateBakeSettings(options, terrain, out var missingTextures);
        var albedo = BakeTerrain(terrain, settings, out var bakeTime, cancellationToken);
        var baked = terrain.Select((t, i) => t with { Albedo = albedo[i] }).ToList();
        foreach (var unknown in settings.Catalog.UnknownLayers)
        {
            warnings.Add($"Terrain layer '{unknown}' is not in the layer table (terrain-layers.json); it is drawn with a generated colour.");
        }

        var minZ = baked.Min(t => t.Surface?.MinHeightCm ?? t.Mesh.Bounds.Min.Z);
        float? sea = options.SeaPlane switch
        {
            true => options.SeaLevelCm,
            false => null,
            null => minZ < options.SeaLevelCm ? options.SeaLevelCm : null,
        };
        var surfaces = baked.Where(t => t.Surface is not null).Select(t => (t.Surface!, t.Layers)).ToList();
        var field = surfaces.Count > 0 ? new TerrainHeightField(surfaces) : null;
        return (baked, sea, field, settings.Catalog, missingTextures, bakeTime, settings.Textures);
    }

    /// <summary>
    /// Loads one mesh (and its base colour texture) the way <see cref="Prepare"/> does, for meshes added to a scene after
    /// it was prepared (new actors). Returns null when the mesh cannot be loaded.
    /// </summary>
    public ExtraMesh? PrepareMesh(string meshPath, LevelSceneOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meshPath);
        var textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<string, MaterialLook>(StringComparer.OrdinalIgnoreCase);
        if (SpawnMarkers.ModelOf(meshPath) is { } model && SpawnMarkers.KindOfMesh(meshPath) is { } kind
            && TryLoadModel(model, options ?? new LevelSceneOptions(), textures, materials, out var standIn, out _))
        {
            return new ExtraMesh(SpawnMarkers.StandIn(standIn, kind, meshPath), textures); // the object of a copied spawner's pin
        }

        if (SpawnMarkers.AssetFor(meshPath) is { } marker)
        {
            return new ExtraMesh(marker, new Dictionary<string, TextureImage>()); // a spawn pin of a copied spawner
        }

        return TryLoadMesh(meshPath, options ?? new LevelSceneOptions(), textures, materials, out var asset, out _) ? new ExtraMesh(asset, textures) : null;
    }

    private const string EditorOnlyReason = "editor-only grid material";

    /// <summary>
    /// Loads the model of a spawn stand-in (<see cref="SpawnModels"/>): a mesh by its object path, or a vehicle Blueprint by
    /// its package path (no object name): the body with the stock parts on it, as the 3D preview shows it, merged into one
    /// mesh (<see cref="MeshPreviewLoader.Merge"/>); every part at its coarsest LOD that still has a thousand triangles.
    /// </summary>
    private bool TryLoadModel(string model, LevelSceneOptions options, Dictionary<string, TextureImage> textures, Dictionary<string, MaterialLook> materials,
        out PreparedMeshAsset asset, out string reason)
    {
        asset = null!;
        if (model.StartsWith(SpawnMarkers.IconPrefix, StringComparison.Ordinal))
        {
            // An item without a mesh: its inventory icon on a small camera-facing card.
            var path = model[SpawnMarkers.IconPrefix.Length..];
            try
            {
                if (!textures.TryGetValue(path, out var image))
                {
                    image = TextureDecoder.Decode(_catalog.LoadObject<UTexture2D>(path), maxSize: 128);
                    textures[path] = image;
                }

                asset = SpawnMarkers.IconAsset(model, path, image.Width, image.Height);
                reason = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug("Icon {Icon} could not be decoded: {Message}", path, ex.Message);
                reason = ex.GetType().Name;
                return false;
            }
        }

        if (model.IndexOf('.', model.LastIndexOf('/') + 1) >= 0)
        {
            return TryLoadMesh(model, options, textures, materials, out asset, out reason, standIn: true);
        }

        try
        {
            if (_previews is null || _previews.TextureSize != options.TextureSize)
            {
                _previews = new MeshPreviewLoader(_catalog, _logger) { TextureSize = options.TextureSize, LodOf = StandInLod };
            }

            if (_previews.LoadBlueprint(model) is not { Parts.Count: > 0 } blueprint)
            {
                reason = "no mesh";
                return false;
            }

            foreach (var (path, image) in blueprint.Textures)
            {
                textures.TryAdd(path, image);
            }

            asset = MeshPreviewLoader.Merge(blueprint, model);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Blueprint {Blueprint} could not be loaded as a stand-in: {Message}", model, ex.Message);
            reason = ex.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// Loads a mesh with its LOD chain and material looks. A <paramref name="standIn"/> (the model of a spawn pin, drawn in
    /// many copies over the map) takes a skeletal mesh at its coarsest LOD that still has a thousand triangles.
    /// </summary>
    private bool TryLoadMesh(string meshPath, LevelSceneOptions options, Dictionary<string, TextureImage> textures, Dictionary<string, MaterialLook> materials,
        out PreparedMeshAsset asset, out string reason, bool standIn = false, Prefetch? prefetch = null)
    {
        asset = null!;
        try
        {
            var loaded = !standIn && prefetch is not null && prefetch.Meshes.TryGetValue(meshPath, out var early) ? early.Value : LoadMesh(meshPath, options, standIn);
            if (loaded.NotMeshType is { } type)
            {
                reason = type;
                return false;
            }

            var (info, chain, editorOnly) = (loaded.Info!, loaded.Chain!, loaded.EditorOnly);
            var materialTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var alphaCutoffs = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var tints = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
            var roughness = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
            if (options.TextureSize > 0)
            {
                FindMaterialTextures(info.Materials.Select(m => m.MaterialPath), options.TextureSize, textures, materials, materialTextures, alphaCutoffs, tints, prefetch, roughness);
            }

            asset = new PreparedMeshAsset(meshPath, chain.Lods[0], materialTextures.Values.FirstOrDefault())
            {
                MaterialSlots = info.Materials.Select(m => m.MaterialPath).ToList(),
                IsEditorOnly = editorOnly,
                Lods = chain.Lods,
                LodScreenSizes = chain.ScreenSizes,
                MaterialTextures = materialTextures,
                MaterialAlphaCutoffs = alphaCutoffs,
                MaterialTints = tints,
                MaterialRoughness = roughness,
                Water = info.Materials.Count > 0 && info.Materials.All(m => tints.TryGetValue(m.MaterialPath, out var t) && t == WaterColor),
            };
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Mesh {Mesh} could not be loaded: {Message}", meshPath, ex.Message);
            reason = ex.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// The reading part of <see cref="TryLoadMesh"/>: the mesh object, its description and LOD chain (throws what they throw).
    /// Touches no shared state, so <see cref="PrefetchMeshes"/> runs it on many threads.
    /// </summary>
    private LoadedMesh LoadMesh(string meshPath, LevelSceneOptions options, bool standIn)
    {
        var obj = _catalog.LoadObject(meshPath);
        if (!MeshExtractor.IsMesh(obj))
        {
            return new LoadedMesh(obj.ExportType, null, null, false);
        }

        // Meshes left on the engine's default grid material are volumes the game never draws (weather masks,
        // environment descriptions round the bunkers): drawn here they wrapped whole bases in grey shells.
        var info = MeshExtractor.Describe(obj);
        var editorOnly = info.Materials.Count > 0
                         && info.Materials.All(m => m.MaterialPath.StartsWith("/Engine/EngineMaterials/WorldGridMaterial", StringComparison.OrdinalIgnoreCase));

        var lod = standIn && info.Kind == MeshAssetKind.Skeletal ? StandInLod(info.Lods) : options.Lod;
        MeshLodChain chain;
        try
        {
            chain = MeshExtractor.ExtractLods(obj, lod, options.MaxLods);
        }
        catch (Exception) when (lod > 0)
        {
            chain = MeshExtractor.ExtractLods(obj, 0, options.MaxLods);
        }

        return new LoadedMesh(null, info, chain, editorOnly);
    }

    /// <summary>A mesh as <see cref="LoadMesh"/> read it: <see cref="NotMeshType"/> when the object is no mesh, else its parts.</summary>
    private sealed record LoadedMesh(string? NotMeshType, MeshAssetInfo? Info, MeshLodChain? Chain, bool EditorOnly);

    /// <summary>
    /// What <see cref="PrefetchMeshes"/> read ahead: meshes, material inspections and texture decodes (at <see cref="TextureSize"/>),
    /// each with its result or the exception it threw (a <see cref="Lazy{T}"/> keeps either).
    /// </summary>
    private sealed class Prefetch(int textureSize)
    {
        public int TextureSize { get; } = textureSize;

        public ConcurrentDictionary<string, Lazy<LoadedMesh>> Meshes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<string, Lazy<MaterialInfo>> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<string, Lazy<TextureImage>> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads <paramref name="meshPaths"/> on all cores but one: each mesh with its LOD chain, the materials it uses that
    /// <paramref name="materials"/> does not know yet and their base colour textures that <paramref name="textures"/> lacks.
    /// Those are pure reads of the game files: the loop of <see cref="PrepareCore"/> then runs as it always did, in the same
    /// order, and takes each answer (or exception) from here instead of reading it itself, so the scene is the same.
    /// The two dictionaries are only read here (nothing writes them until this returns).
    /// </summary>
    private Prefetch PrefetchMeshes(IReadOnlyList<string> meshPaths, LevelSceneOptions options, Dictionary<string, TextureImage> textures,
        Dictionary<string, MaterialLook> materials, IProgress<(int Done, int Total, string Item)>? progress, int total, CancellationToken cancellationToken)
    {
        var prefetch = new Prefetch(options.TextureSize);
        foreach (var path in meshPaths)
        {
            prefetch.Meshes.TryAdd(path, new Lazy<LoadedMesh>(() => LoadMesh(path, options, standIn: false)));
        }

        var done = 0;
        var parallel = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.ForEach(prefetch.Meshes, parallel, entry =>
        {
            progress?.Report((Interlocked.Increment(ref done) - 1, total, entry.Key));
            if (!Warm(entry.Value) || options.TextureSize <= 0 || entry.Value.Value.Info is not { } info)
            {
                return;
            }

            foreach (var material in info.Materials.Select(m => m.MaterialPath).Where(p => !string.IsNullOrEmpty(p)))
            {
                if (materials.TryGetValue(material, out var look))
                {
                    if (look.Texture is { } kept && !textures.ContainsKey(kept))
                    {
                        Warm(TextureAhead(kept));
                    }

                    continue;
                }

                var inspected = prefetch.Materials.GetOrAdd(material, m => new Lazy<MaterialInfo>(() => new MaterialInspector(_catalog).Inspect(m)));
                if (Warm(inspected) && inspected.Value.BaseColorTexture is { } texture && !textures.ContainsKey(texture))
                {
                    Warm(TextureAhead(texture));
                    if (inspected.Value.NormalTexture is { } normal && !textures.ContainsKey(normal))
                    {
                        Warm(TextureAhead(normal));
                    }
                }
            }
        });
        return prefetch;

        Lazy<TextureImage> TextureAhead(string path) => prefetch.Textures.GetOrAdd(path, p =>
            new Lazy<TextureImage>(() => TextureDecoder.DecodeForGpu(_catalog.LoadObject<UTexture2D>(p), maxSize: options.TextureSize)));

        // The exception stays in the Lazy and is thrown again where the sequential loop asks for the value.
        static bool Warm<T>(Lazy<T> lazy)
        {
            try
            {
                _ = lazy.Value;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>A material's inspection, read ahead by <paramref name="prefetch"/> or now.</summary>
    private MaterialInfo Inspect(string materialPath, Prefetch? prefetch) =>
        prefetch is not null && prefetch.Materials.TryGetValue(materialPath, out var early) ? early.Value : new MaterialInspector(_catalog).Inspect(materialPath);

    /// <summary>A texture decoded at <paramref name="maxSize"/>, read ahead by <paramref name="prefetch"/> or now.</summary>
    private TextureImage Decode(string texturePath, int maxSize, Prefetch? prefetch) =>
        prefetch is not null && prefetch.TextureSize == maxSize && prefetch.Textures.TryGetValue(texturePath, out var early)
            ? early.Value
            : TextureDecoder.DecodeForGpu(_catalog.LoadObject<UTexture2D>(texturePath), maxSize: maxSize); // cooked blocks: no decode, a quarter of the memory

    /// <summary>The coarsest LOD that still has a thousand triangles (a stand-in's skeletal mesh or vehicle part), else the finest.</summary>
    private static int StandInLod(IReadOnlyList<MeshLodInfo> lods)
    {
        for (var i = lods.Count - 1; i > 0; i--)
        {
            if (!lods[i].IsStripped && lods[i].TriangleCount >= 1000)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Colour of a material that has neither a texture nor a colour parameter (mid grey, as the mesh preview).</summary>
    private static readonly Vector4 Untextured = new(0.6f, 0.6f, 0.6f, 1f);

    /// <summary>River and lake water (linear RGBA, translucent): the sea plane's colour.</summary>
    private static readonly Vector4 WaterColor = new(0.035f, 0.12f, 0.17f, 0.75f);

    /// <summary>
    /// <paramref name="asset"/> repainted by <paramref name="overrides"/> (material per slot, null = keep): sections of
    /// the replaced slots use the new materials, whose textures/colours are resolved like any other. Null when no slot
    /// gets a material that can be loaded (level-local dynamic instances cannot).
    /// </summary>
    private PreparedMeshAsset? MakeVariant(PreparedMeshAsset asset, IReadOnlyList<string?> overrides, string key, LevelSceneOptions options,
        Dictionary<string, TextureImage> textures, Dictionary<string, MaterialLook> materials)
    {
        var bySlot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var s = 0; s < Math.Min(overrides.Count, asset.MaterialSlots.Count); s++)
        {
            if (overrides[s] is { Length: > 0 } material && !material.Contains(":PersistentLevel", StringComparison.Ordinal) && asset.MaterialSlots[s].Length > 0)
            {
                bySlot.TryAdd(asset.MaterialSlots[s], material);
            }
        }

        if (bySlot.Count == 0)
        {
            return null;
        }

        var materialTextures = new Dictionary<string, string>(asset.MaterialTextures, StringComparer.OrdinalIgnoreCase);
        var alphaCutoffs = new Dictionary<string, float>(asset.MaterialAlphaCutoffs, StringComparer.OrdinalIgnoreCase);
        var tints = new Dictionary<string, Vector4>(asset.MaterialTints, StringComparer.OrdinalIgnoreCase);
        var roughness = new Dictionary<string, Vector2>(asset.MaterialRoughness, StringComparer.OrdinalIgnoreCase);
        if (options.TextureSize > 0)
        {
            FindMaterialTextures(bySlot.Values.Distinct(StringComparer.OrdinalIgnoreCase), options.TextureSize, textures, materials, materialTextures, alphaCutoffs, tints, roughness: roughness);
        }

        var lods = asset.Lods.Select(lod => lod with
        {
            Sections = lod.Sections.Select(s => bySlot.TryGetValue(s.MaterialName, out var o) ? s with { MaterialName = o } : s).ToArray(),
        }).ToList();
        return asset with
        {
            MeshPath = key,
            Mesh = lods[0],
            Lods = lods,
            MaterialTextures = materialTextures,
            MaterialAlphaCutoffs = alphaCutoffs,
            MaterialTints = tints,
            MaterialRoughness = roughness,
            IsEditorOnly = false,
        };
    }

    /// <summary>The material's normal map decoded into <paramref name="textures"/> (once per path), or null when it has none or it cannot be read.</summary>
    private string? DecodeNormal(MaterialInfo material, int maxSize, Dictionary<string, TextureImage> textures, Prefetch? prefetch)
    {
        if (material.NormalTexture is not { } path)
        {
            return null;
        }

        try
        {
            if (!textures.ContainsKey(path))
            {
                textures[path] = Decode(path, maxSize, prefetch);
            }

            return path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Material {Material}: normal map {Texture} could not be decoded ({Message}).", material.ObjectPath, path, ex.Message);
            return null;
        }
    }

    private static bool IsWater(string materialPath)
    {
        var name = materialPath[(materialPath.LastIndexOf('/') + 1)..];
        return name.Contains("Water", StringComparison.OrdinalIgnoreCase) || name.Contains("River", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the base colour texture of every material slot into <paramref name="result"/> (material path → texture
    /// path); each material is inspected once per <paramref name="materials"/> cache and each texture decoded once per path.
    /// Masked materials add their clip value to <paramref name="alphaCutoffs"/>; untextured or translucent ones their colour to
    /// <paramref name="tints"/>.
    /// </summary>
    private void FindMaterialTextures(IEnumerable<string> materialPaths, int maxSize, Dictionary<string, TextureImage> textures, Dictionary<string, MaterialLook> materials,
        Dictionary<string, string> result, Dictionary<string, float> alphaCutoffs, Dictionary<string, Vector4> tints, Prefetch? prefetch = null,
        Dictionary<string, Vector2>? roughness = null)
    {
        foreach (var slot in materialPaths.Select(p => new { MaterialPath = p }))
        {
            if (string.IsNullOrEmpty(slot.MaterialPath) || result.ContainsKey(slot.MaterialPath) || tints.ContainsKey(slot.MaterialPath))
            {
                continue;
            }

            if (!materials.TryGetValue(slot.MaterialPath, out var look))
            {
                string? texturePath = null;
                string? normalPath = null;
                var range = Vector2.Zero;
                float clip = 0f;
                var tint = Untextured;
                try
                {
                    var material = Inspect(slot.MaterialPath, prefetch);
                    clip = material.OpacityMaskClip ?? 0f;
                    if (material.TintColor is { W: > 0f } colour)
                    {
                        tint = colour with { W = 1f };
                    }

                    if (material.IsTranslucent)
                    {
                        // Rivers and lakes as see-through water (their shaders have no colour texture); glass and other
                        // translucent surfaces keep their colour but let the room behind show through.
                        tint = IsWater(slot.MaterialPath) ? WaterColor : tint with { W = 0.4f };
                    }

                    if (material.BaseColorTexture is { } path)
                    {
                        if (!textures.ContainsKey(path))
                        {
                            textures[path] = Decode(path, maxSize, prefetch);
                        }

                        texturePath = path;
                        normalPath = DecodeNormal(material, maxSize, textures, prefetch);

                        // Opaque only: a masked or translucent material's alpha is its coverage, not its roughness.
                        range = clip == 0f && !material.IsTranslucent && material.RoughnessRange is { } r ? r : Vector2.Zero;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogDebug("Material {Material}: no usable base colour texture ({Message}).", slot.MaterialPath, ex.Message);
                }

                look = new MaterialLook(texturePath, clip, tint, normalPath, range);
                materials[slot.MaterialPath] = look;
            }
            else if (look.Texture is { } kept && !textures.ContainsKey(kept))
            {
                // The prepare cache dropped the image (no kept mesh drew with it) but kept the material's look: a church
                // streamed in again drew white (owner: "no textures, like San Andreas"). Decode it again.
                try
                {
                    textures[kept] = Decode(kept, maxSize, prefetch);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogDebug("Material {Material}: texture {Texture} could not be decoded again ({Message}).", slot.MaterialPath, kept, ex.Message);
                    look = look with { Texture = null };
                }
            }

            if (look is { Texture: not null, Normal: { } dropped } && !textures.ContainsKey(dropped))
            {
                // The same for the normal map, also when another material brought the colour texture back already.
                try
                {
                    textures[dropped] = Decode(dropped, maxSize, prefetch);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogDebug("Material {Material}: normal map {Texture} could not be decoded again ({Message}).", slot.MaterialPath, dropped, ex.Message);
                    look = look with { Normal = null };
                }
            }

            if (look.Texture is not null)
            {
                result[slot.MaterialPath] = look.Texture;
                if (look.Normal is { } normalMap && textures.ContainsKey(normalMap))
                {
                    result[slot.MaterialPath + ScumStudio.Rendering.Resources.GpuMesh.NormalMapSuffix] = normalMap;
                }

                if (look.Roughness.Y > 0f && roughness is not null)
                {
                    roughness[slot.MaterialPath] = look.Roughness;
                }

                if (look.AlphaCutoff > 0f)
                {
                    alphaCutoffs[slot.MaterialPath] = look.AlphaCutoff; // masked: the texture's alpha cuts the leaves out
                }
            }
            if (look.Texture is null || look.Tint.W < 1f)
            {
                tints[slot.MaterialPath] = look.Tint; // no texture: draw the material's own colour, not white
            }
        }
    }
}
