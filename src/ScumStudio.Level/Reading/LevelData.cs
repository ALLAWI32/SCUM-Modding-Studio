using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Reading;

/// <summary>
/// Raw, parser-independent content of one cooked level package, as produced by an <see cref="ILevelReader"/>:
/// the export table (names, classes, outers) plus the properties the actor graph needs. <see cref="Model.LevelDocument"/>
/// turns it into actors, components and world transforms.
/// </summary>
public sealed record LevelData
{
    /// <summary>UE package path, e.g. <c>/Game/ConZ_Files/Maps/The_Island/A_0_Outpost</c>.</summary>
    public required string PackagePath { get; init; }

    /// <summary>
    /// Every export of the package in export-map order; <c>Exports[i].Index == i</c>. Only actor and component exports
    /// carry properties (<see cref="LevelExportData.IsLoaded"/>).
    /// </summary>
    public required IReadOnlyList<LevelExportData> Exports { get; init; }

    /// <summary>
    /// Zero-based export indices of <c>ULevel::Actors</c>, in order (null entries and imports removed).
    /// </summary>
    public required IReadOnlyList<int> ActorIndices { get; init; }

    /// <summary>Export index of the <c>ULevel</c> (<c>PersistentLevel</c>), or -1 when unknown.</summary>
    public int LevelExportIndex { get; init; } = -1;

    /// <summary>Export index of the <c>UWorld</c>, or -1 when unknown.</summary>
    public int WorldExportIndex { get; init; } = -1;

    /// <summary>Export index of the level script actor, or -1.</summary>
    public int LevelScriptActorIndex { get; init; } = -1;

    /// <summary>Non-fatal problems met while reading (unreadable exports, unresolved references).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Components that are not exports of the level package but that the game creates when it spawns the level's actors:
    /// Blueprint construction-script (SCS) components missing from the level, and the components of child actors that a
    /// <c>ChildActorComponent</c> spawns but that the level does not store. Each has
    /// <see cref="LevelExportData.IsSynthesized"/> set, a unique negative <see cref="LevelExportData.Index"/> (a synthetic id,
    /// never -1) and <see cref="LevelExportData.OuterIndex"/> = the owning actor's export index. Their values come from the
    /// Blueprint templates; they cannot be edited in the level package.
    /// </summary>
    public IReadOnlyList<LevelExportData> SynthesizedComponents { get; init; } = [];

    /// <summary>Level (package) name, e.g. <c>A_0_Outpost</c>.</summary>
    public string Name => PackagePath[(PackagePath.LastIndexOf('/') + 1)..];
}

/// <summary>
/// One export of a level package. Header fields come from the export map; the remaining fields are read from tagged
/// properties (falling back along the export's template/archetype chain, because cooked levels only store deltas) and are
/// only filled for actors and their components.
/// </summary>
public sealed record LevelExportData
{
    /// <summary>Zero-based export index.</summary>
    public required int Index { get; init; }

    /// <summary>Object name (unique among the objects sharing its outer).</summary>
    public required string Name { get; init; }

    /// <summary>Short class name, e.g. <c>StaticMeshActor</c>, <c>StaticMeshComponent</c>, <c>BP_Lamp_C</c>.</summary>
    public required string ClassName { get; init; }

    /// <summary>Full class object path, e.g. <c>/Script/Engine.StaticMeshActor</c> or <c>/Game/X/BP_Lamp.BP_Lamp_C</c>.</summary>
    public string ClassPath { get; init; } = string.Empty;

    /// <summary>True when the class is a Blueprint-generated class from another package (not a native <c>/Script</c> class).</summary>
    public bool IsBlueprintClass { get; init; }

    /// <summary>Zero-based export index of the outer object, or -1 when the outer is the package itself.</summary>
    public int OuterIndex { get; init; } = -1;

    /// <summary>Object path of the template/archetype (e.g. a Blueprint's <c>..._GEN_VARIABLE</c> component), or null.</summary>
    public string? TemplatePath { get; init; }

    /// <summary>True when the export's properties were read (actors and components).</summary>
    public bool IsLoaded { get; init; }

    /// <summary>Names of the tagged properties stored on the export itself (its delta), in stored order.</summary>
    public IReadOnlyList<string> PropertyNames { get; init; } = [];

    /// <summary>True for an actor component (outer is an actor and the class is a component class).</summary>
    public bool IsComponent { get; init; }

    /// <summary>True for a scene component (has a transform and can be attached).</summary>
    public bool IsSceneComponent { get; init; }

    /// <summary>Actors: export index of <c>RootComponent</c>, or null when neither the export nor its archetype names one.</summary>
    public int? RootComponent { get; init; }

    /// <summary>Scene components: export index of <c>AttachParent</c>, or null when not attached (or attached outside this package).</summary>
    public int? AttachParent { get; init; }

    /// <summary>
    /// Scene components attached to a socket of the parent's static mesh (<c>AttachSocketName</c>): that socket's transform
    /// in the parent's space, or null when not attached to a socket (or the socket could not be found).
    /// </summary>
    public FTransform? AttachSocket { get; init; }

    /// <summary>Scene components: <c>RelativeLocation</c>; null = not stored anywhere (engine default zero).</summary>
    public FVector? RelativeLocation { get; init; }

    /// <summary>Scene components: <c>RelativeRotation</c>; null = default zero rotator.</summary>
    public FRotator? RelativeRotation { get; init; }

    /// <summary>Scene components: <c>RelativeScale3D</c>; null = default (1, 1, 1).</summary>
    public FVector? RelativeScale3D { get; init; }

    /// <summary><c>bAbsoluteLocation</c>: the relative location is in world space.</summary>
    public bool AbsoluteLocation { get; init; }

    /// <summary><c>bAbsoluteRotation</c>: the relative rotation is in world space.</summary>
    public bool AbsoluteRotation { get; init; }

    /// <summary><c>bAbsoluteScale</c>: the relative scale is in world space.</summary>
    public bool AbsoluteScale { get; init; }

    /// <summary>Static mesh components: object path of <c>StaticMesh</c>, e.g. <c>/Game/X/SM_Rock.SM_Rock</c>.</summary>
    public string? StaticMesh { get; init; }

    /// <summary>
    /// Instanced static mesh components (ISM/HISM/foliage): <c>PerInstanceSMData</c> transforms in component space, in
    /// stored order; null for other components.
    /// </summary>
    public IReadOnlyList<FTransform>? Instances { get; init; }

    /// <summary>
    /// Instanced components: <c>InstanceEndCullDistance</c> (UE centimetres) beyond which the game draws none of the
    /// instances; null when not stored or 0 (never culled by distance).
    /// </summary>
    public int? InstanceEndCullDistance { get; init; }

    /// <summary>Spline mesh components: the bend parameters (see <see cref="SplineMeshParams"/>); null for other components.</summary>
    public SplineMeshParams? SplineMesh { get; init; }

    /// <summary>
    /// Mesh components: <c>OverrideMaterials</c>, the material object path replacing each material slot of the mesh (null
    /// entries keep the slot's own); null when nothing is overridden.
    /// </summary>
    public IReadOnlyList<string?>? OverrideMaterials { get; init; }

    /// <summary>Item spawner components: <c>SpawnerMarkers</c> (where loot appears); null for other components.</summary>
    public IReadOnlyList<SpawnMarker>? SpawnMarkers { get; init; }

    /// <summary>Loot presets of the component's <c>ExamineAssetData</c> (what a search of it gives); null when it has none.</summary>
    public IReadOnlyList<string>? LootPresets { get; init; }

    /// <summary>Primitive components: <c>BodyInstance.CollisionProfileName</c> as stored (or in a template), or null.</summary>
    public string? CollisionProfile { get; init; }

    /// <summary>Box components: <c>BoxExtent</c>, the half size in component space (the engine's 32 cm when not stored); null for other components.</summary>
    public FVector? BoxExtent { get; init; }

    /// <summary>Sentry spawner actors: <c>PatrolPoints</c> relative to the spawner; null for other actors.</summary>
    public IReadOnlyList<FVector>? PatrolPoints { get; init; }

    /// <summary>Trade post actors: their traders (<c>_traderMarkers</c>, usually from the class); null for other actors.</summary>
    public IReadOnlyList<TraderMarker>? TraderMarkers { get; init; }

    /// <summary>Actors whose class chain reaches SCUM's native <c>ItemContainer</c> (lockable, lootable containers).</summary>
    public bool IsItemContainer { get; init; }

    /// <summary>True when the relative transform or instances were taken from the template instead of the export itself.</summary>
    public bool UsesTemplateValues { get; init; }

    /// <summary>
    /// True for a component that is not an export of the level (see <see cref="LevelData.SynthesizedComponents"/>): its
    /// <see cref="Index"/> is a negative synthetic id and every value comes from <see cref="TemplatePath"/>.
    /// </summary>
    public bool IsSynthesized { get; init; }

    /// <summary>
    /// True for a static mesh component: the class chain reaches <c>StaticMeshComponent</c> (including ISM/HISM/foliage and
    /// SCUM subclasses such as <c>InteriorStaticMeshComponent</c>), or the component carries a <c>StaticMesh</c> property.
    /// </summary>
    public bool IsStaticMeshComponent { get; init; }

    /// <summary>
    /// <c>bVisible &amp;&amp; !bHiddenInGame</c> as stored on the component or its templates (true when neither is stored;
    /// native class defaults are not known).
    /// </summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>Child actor components: object path of <c>ChildActorClass</c>, or null.</summary>
    public string? ChildActorClassPath { get; init; }

    /// <summary>Child actor components: export index of the child actor the level stores for it (<c>ChildActor</c>), or null.</summary>
    public int? ChildActor { get; init; }

    /// <summary>Actors spawned by a child actor component: export index of that component (<c>ParentComponent</c>), or null.</summary>
    public int? ParentComponent { get; init; }

    /// <summary>The relative transform as stored (location, rotator, scale; defaults applied for missing parts).</summary>
    public TransformValue RelativeValue =>
        new(RelativeLocation ?? FVector.Zero, RelativeRotation ?? FRotator.Zero, RelativeScale3D ?? FVector.One);

    /// <summary>The relative transform (defaults applied for missing parts).</summary>
    public FTransform RelativeTransform => RelativeValue.ToTransform();
}
