using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Model;

/// <summary>Coarse actor category used for filtering, colouring and "delete all of kind".</summary>
public enum ActorKind
{
    /// <summary>Anything else (WorldSettings, level script, landscape proxies, foliage actors, brushes, notes, ...).</summary>
    Other,

    /// <summary>A native <c>StaticMeshActor</c>.</summary>
    StaticMeshActor,

    /// <summary>An instance of a Blueprint class (<c>BP_..._C</c>) from another package.</summary>
    Blueprint,

    /// <summary>A native light actor (<c>PointLight</c>, <c>SpotLight</c>, <c>RectLight</c>, <c>DirectionalLight</c>, <c>SkyLight</c>).</summary>
    Light,

    /// <summary>A native volume (<c>BlockingVolume</c>, <c>PostProcessVolume</c>, <c>TriggerVolume</c>, ...).</summary>
    Volume,
}

/// <summary>A component of an actor in a <see cref="LevelDocument"/>.</summary>
/// <remarks>
/// Besides the component exports of the level, an actor lists <em>synthesized</em> components
/// (<see cref="IsSynthesized"/>): Blueprint construction-script components the level does not store, and the components of
/// child actors that a <c>ChildActorComponent</c> spawns when the level does not store that child actor. A child actor's
/// components are flattened into the owning actor under <c>ChildActorComponentName/ComponentName</c> (nested child actors
/// add further segments), attached so that every <see cref="WorldTransform"/> is final; a renderer places every
/// component with a <see cref="StaticMeshPath"/> (plus <see cref="ActorRecord.InstanceTransforms"/>) without knowing
/// about Blueprints. Child actors the level does store stay separate actors, linked through <see cref="ChildActor"/> and
/// <see cref="ActorRecord.ParentComponent"/>.
/// </remarks>
/// <param name="ExportIndex">
/// Zero-based export index in the level package; a negative synthetic id (unique in the document, never -1) for a
/// synthesized component.
/// </param>
/// <param name="Name">Component name (unique within its actor), e.g. <c>StaticMeshComponent0</c> or <c>Door/Door Mesh</c>.</param>
/// <param name="ClassName">Short class name, e.g. <c>StaticMeshComponent</c>.</param>
/// <param name="IsSceneComponent">True when the component has a transform.</param>
/// <param name="AttachParent">Export index of the parent scene component, or null for a root/unattached component.</param>
/// <param name="RelativeTransform">Transform relative to the parent (location/rotation/scale as stored, defaults applied).</param>
/// <param name="WorldTransform">Composed world transform (<c>Relative * ParentWorld</c>).</param>
/// <param name="StaticMeshPath">Object path of the static mesh, or null.</param>
/// <param name="Instances">Per-instance transforms in component space (ISM/HISM/foliage), empty otherwise.</param>
public sealed record ComponentRecord(
    int ExportIndex,
    string Name,
    string ClassName,
    bool IsSceneComponent,
    int? AttachParent,
    FTransform RelativeTransform,
    FTransform WorldTransform,
    string? StaticMeshPath,
    IReadOnlyList<FTransform> Instances)
{
    /// <summary>Item spawners: the places it puts loot (<c>SpawnerMarkers</c>, from the component or its Blueprint template).</summary>
    public IReadOnlyList<SpawnMarker> SpawnMarkers { get; init; } = [];

    /// <summary>
    /// The collision profile the component sets (<c>BodyInstance.CollisionProfileName</c>, stored or from a template): a
    /// tree's foliage <c>SCUM_TreeStump</c> while its mesh's own default lets players through. Null when not set.
    /// </summary>
    public string? CollisionProfile { get; init; }

    /// <summary>Full class path (e.g. <c>/Script/Engine.StaticMeshComponent</c>).</summary>
    public string ClassPath { get; init; } = string.Empty;

    /// <summary>
    /// The relative transform exactly as stored (rotator in degrees, not re-derived from a quaternion); edit operations
    /// use it as their "old" value.
    /// </summary>
    public TransformValue Relative { get; init; } = TransformValue.Identity;

    /// <summary>True for an instanced static mesh component (ISM/HISM/foliage), even with zero instances.</summary>
    public bool IsInstanced { get; init; }

    /// <summary>Instanced components: distance (cm) beyond which the game draws none of the instances (<c>InstanceEndCullDistance</c>); 0 = never culled.</summary>
    public int InstanceEndCullDistance { get; init; }

    /// <summary>
    /// Spline mesh components (roads, river banks, fences along landscape splines): how the mesh is bent, in component
    /// space; null for every other component. A renderer draws <see cref="StaticMeshPath"/> deformed along it.
    /// </summary>
    public SplineMeshParams? SplineMesh { get; init; }

    /// <summary>
    /// Material object paths replacing the mesh's material slots, by slot index (null = the slot keeps its own); null when
    /// the component overrides nothing.
    /// </summary>
    public IReadOnlyList<string?>? OverrideMaterials { get; init; }

    /// <summary>Names of the properties stored on the component export itself.</summary>
    public IReadOnlyList<string> PropertyNames { get; init; } = [];

    /// <summary>True when some values came from the component's template (Blueprint SCS / archetype).</summary>
    public bool UsesTemplateValues { get; init; }

    /// <summary>
    /// True when the component is not an export of the level but was created from its Blueprint template (see the remarks);
    /// <see cref="ExportIndex"/> is then negative and edit operations cannot target it.
    /// </summary>
    public bool IsSynthesized { get; init; }

    /// <summary>Object path of the template the component was created from (synthesized components), or null.</summary>
    public string? TemplatePath { get; init; }

    /// <summary>
    /// True for a component the actor's C++ class creates (its template is a subobject of a class default object,
    /// <c>…BP_Door.Default__BP_Door_C:Door Mesh</c>), not one of its Blueprint's parts (<c>…_GEN_VARIABLE</c> templates): a
    /// door's leaf. Salvador (Discord): "When I duplicate an openable door and place it somewhere else, it doesn't open in
    /// the game" - a click on the leaf picked it as a part and its copy was a plain mesh, so a click takes the whole actor.
    /// </summary>
    public bool IsNativeSubobject { get; init; }

    /// <summary>
    /// True for a static mesh component (class chain reaches <c>StaticMeshComponent</c>, e.g. SCUM's
    /// <c>InteriorStaticMeshComponent</c>, or a <c>StaticMesh</c> property is present).
    /// </summary>
    public bool IsStaticMeshComponent { get; init; }

    /// <summary><c>bVisible &amp;&amp; !bHiddenInGame</c> as stored (true when not stored).</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>Child actor components: object path of the child actor class, or null.</summary>
    public string? ChildActorClassPath { get; init; }

    /// <summary>Child actor components: export index of the child actor the level stores for it (a separate actor), or null.</summary>
    public int? ChildActor { get; init; }
}

/// <summary>One place an item spawner puts loot (an element of <c>ItemSpawnerComponent.SpawnerMarkers</c>).</summary>
/// <param name="Local">Where, in the spawner component's space.</param>
/// <param name="Preset">The loot preset's name (e.g. <c>World_Environment</c>: what can spawn there).</param>
/// <param name="Probability">Chance that something spawns, in percent.</param>
/// <param name="MinQuantity">Fewest items.</param>
/// <param name="MaxQuantity">Most items.</param>
public sealed record SpawnMarker(FTransform Local, string Preset, float Probability, int MinQuantity, int MaxQuantity)
{
    /// <summary>
    /// Class path of the preset (<c>/Game/.../Airfield_Hangar/World_Shelf.World_Shelf_C</c>; many folders have a
    /// <c>World_Shelf</c>), or null for a world spawner's fixed item.
    /// </summary>
    public string? PresetPath { get; init; }

    /// <summary>
    /// A world spawner's fixed item: the class path of the item it always spawns (<c>_item</c>, e.g.
    /// <c>/Game/.../BP_Work_Drillpress_01.BP_Work_Drillpress_01_C</c>), or null for a loot point.
    /// </summary>
    public string? ItemClassPath { get; init; }
}

/// <summary>
/// One trader of a trade post (an element of its class's <c>_traderMarkers</c>): who it is (its personality: the name the
/// server's EconomyOverride.json uses, e.g. <c>A_0_Armory</c>, and its type, which decides what it sells), the NPC that
/// stands there and where.
/// </summary>
/// <param name="Local">Where the NPC stands, in the trade post's space.</param>
/// <param name="Name">The trader's name (<c>HumanReadableTraderName</c>).</param>
/// <param name="Type">The trader type without its enum prefix (<c>Armorer</c>, <c>Mechanic</c>, <c>GeneralGoods</c> …).</param>
/// <param name="NpcClass">Class path of the NPC (<c>…/BP_ArmsDealer_01.BP_ArmsDealer_01_C</c>), or empty.</param>
/// <param name="PersonalityPath">Object path of the personality data asset, or empty.</param>
public sealed record TraderMarker(FTransform Local, string Name, string Type, string NpcClass, string PersonalityPath);

/// <summary>One instance of an instanced static mesh component, in world space.</summary>
/// <param name="ComponentExportIndex">Export index of the owning ISM/HISM component.</param>
/// <param name="ComponentName">Name of that component.</param>
/// <param name="InstanceIndex">Index in the component's <c>PerInstanceSMData</c> (as stored in the pristine package).</param>
/// <param name="StaticMeshPath">Mesh drawn by the component, or null.</param>
/// <param name="LocalTransform">Transform in component space, as stored.</param>
/// <param name="WorldTransform">World transform (<c>Local * ComponentWorld</c>).</param>
public sealed record ActorInstance(
    int ComponentExportIndex,
    string ComponentName,
    int InstanceIndex,
    string? StaticMeshPath,
    FTransform LocalTransform,
    FTransform WorldTransform)
{
    /// <summary>The owning component's <see cref="ComponentRecord.InstanceEndCullDistance"/> (cm; 0 = never culled).</summary>
    public int EndCullDistance { get; init; }
}

/// <summary>An actor of a <see cref="LevelDocument"/> with its components and composed transforms.</summary>
/// <param name="ExportIndex">Zero-based export index in the level package.</param>
/// <param name="Name">Actor object name (unique in the level), e.g. <c>StaticMeshActor_12</c>.</param>
/// <param name="ClassPath">Full class path, e.g. <c>/Script/Engine.StaticMeshActor</c> or <c>/Game/.../BP_Lamp.BP_Lamp_C</c>.</param>
/// <param name="RootComponent">Export index of the root scene component, or null when the actor has none.</param>
/// <param name="Components">Components owned by the actor (export order).</param>
/// <param name="WorldTransform">World transform of the root component (identity without one).</param>
/// <param name="Kind">Category.</param>
/// <param name="StaticMeshPath">
/// Mesh of the root component; for a StaticMeshActor without a mesh root, its first mesh component's; for a Blueprint
/// without a mesh root, a representative mesh (the first visible, non-instanced mesh from the Blueprint's own folder,
/// preferring components attached to the root, else any visible mesh); or null. "Delete all of kind" by mesh keeps matching
/// Blueprints by their root mesh only.
/// </param>
/// <param name="InstanceTransforms">All ISM/HISM instances of the actor in world space.</param>
public sealed record ActorRecord(
    int ExportIndex,
    string Name,
    string ClassPath,
    int? RootComponent,
    IReadOnlyList<ComponentRecord> Components,
    FTransform WorldTransform,
    ActorKind Kind,
    string? StaticMeshPath,
    IReadOnlyList<ActorInstance> InstanceTransforms)
{
    /// <summary>Short class name, e.g. <c>StaticMeshActor</c> or <c>BP_Lamp_C</c>.</summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>True when the root component was inferred (no <c>RootComponent</c> property on the actor or its archetype).</summary>
    public bool RootInferred { get; init; }

    /// <summary>Names of the properties stored on the actor export itself.</summary>
    public IReadOnlyList<string> PropertyNames { get; init; } = [];

    /// <summary>
    /// For a child actor stored in the level (spawned by another actor's <c>ChildActorComponent</c>): export index of that
    /// component; null otherwise.
    /// </summary>
    public int? ParentComponent { get; init; }

    /// <summary>Sentry spawners: the patrol path, as points relative to the spawner (<c>PatrolPoints</c>).</summary>
    public IReadOnlyList<FVector> PatrolPoints { get; init; } = [];

    /// <summary>Trade posts: the traders they place (<c>_traderMarkers</c> of the class); empty for other actors.</summary>
    public IReadOnlyList<TraderMarker> TraderMarkers { get; init; } = [];

    /// <summary>Number of synthesized components (see <see cref="ComponentRecord.IsSynthesized"/>).</summary>
    public int SynthesizedComponentCount => Components.Count(c => c.IsSynthesized);

    /// <summary>The root component record, or null.</summary>
    public ComponentRecord? Root => RootComponent is { } r ? Components.FirstOrDefault(c => c.ExportIndex == r) : null;

    /// <summary>Finds a component by name (case-insensitive).</summary>
    public ComponentRecord? FindComponent(string name) =>
        Components.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
