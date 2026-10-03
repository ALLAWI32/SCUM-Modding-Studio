using System.Numerics;
using System.Reflection;
using System.Text;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.World;
using UeMatrix = CUE4Parse.UE4.Objects.Core.Math.FMatrix;
using UeQuat = CUE4Parse.UE4.Objects.Core.Math.FQuat;
using UeRotator = CUE4Parse.UE4.Objects.Core.Math.FRotator;
using UeTransform = CUE4Parse.UE4.Objects.Core.Math.FTransform;
using UeVector = CUE4Parse.UE4.Objects.Core.Math.FVector;

namespace ScumStudio.Level.Reading;

/// <summary>Options for <see cref="Cue4ParseLevelReader"/>.</summary>
public sealed record Cue4ParseLevelReaderOptions
{
    /// <summary>
    /// Follow each export's template (archetype) for properties the level does not store, e.g. the relative transform
    /// and mesh of a Blueprint component that only exist on the Blueprint's SCS template. Loads the Blueprint packages
    /// when they are in the catalog. Default true.
    /// </summary>
    public bool ResolveTemplates { get; init; } = true;

    /// <summary>Maximum template chain length followed per property. Default 8.</summary>
    public int MaxTemplateDepth { get; init; } = 8;

    /// <summary>Read <c>PerInstanceSMData</c> of ISM/HISM/foliage components. Default true.</summary>
    public bool ReadInstances { get; init; } = true;

    /// <summary>
    /// Complete Blueprint actors from their classes: walk the <c>SimpleConstructionScript</c> of the actor's
    /// Blueprint-generated class and of every Blueprint parent class (parent first, honouring
    /// <c>InheritableComponentHandler</c> overrides) and synthesize every component the level does not store (see
    /// <see cref="LevelData.SynthesizedComponents"/>). Needs <see cref="ResolveTemplates"/> and the Blueprint packages in
    /// the catalog. Default true.
    /// </summary>
    /// <remarks>
    /// UE 4.27 cooked levels normally store every SCS-created component of a placed Blueprint instance as an export (with
    /// only its delta properties), so on stock levels this mostly confirms that nothing is missing; it matters for levels
    /// or actors whose components were stripped, and it is what fills in child actors (see <see cref="ExpandChildActors"/>).
    /// </remarks>
    public bool ExpandBlueprintComponents { get; init; } = true;

    /// <summary>
    /// For each <c>ChildActorComponent</c> whose child actor the level does not store (no <c>ChildActor</c> reference to a
    /// level export), spawn its <c>ChildActorClass</c> virtually: the child's default subobjects (from
    /// <c>ChildActorTemplate</c>, the <c>*_CAT</c> export, and the class default object) and its construction-script
    /// components become synthesized components of the owning actor named <c>ChildActorComponent/Component</c>, the
    /// child's root snapped to the child actor component. Nested child actors are expanded up to
    /// <see cref="MaxChildActorDepth"/>. Child actors that the level stores stay separate actors. Needs
    /// <see cref="ResolveTemplates"/>. Default true.
    /// </summary>
    public bool ExpandChildActors { get; init; } = true;

    /// <summary>Maximum nesting of expanded child actors (a child actor spawning child actors, ...). Default 4.</summary>
    public int MaxChildActorDepth { get; init; } = 4;
}

/// <summary>
/// <see cref="ILevelReader"/> over CUE4Parse 1.2.2 through an <see cref="AssetCatalog"/> (paks or loose files).
/// </summary>
/// <remarks>
/// <para>Structure (export names, class and template paths, outers) comes from the legacy package's export and import
/// maps without deserializing exports. Only the <c>UWorld</c>, the <c>ULevel</c> (<c>ULevel.Actors</c>,
/// <c>LevelScriptActor</c>), the actors and their components are deserialized; CUE4Parse resolves the class and template
/// of each of those, which reads the Blueprint packages involved when they are in the catalog.</para>
/// <para>Per component: <c>RelativeLocation</c>/<c>RelativeRotation</c>/<c>RelativeScale3D</c>, <c>bAbsolute*</c>,
/// <c>AttachParent</c>, <c>StaticMesh</c>, <c>bVisible</c>/<c>bHiddenInGame</c>, <c>ChildActorClass</c>/<c>ChildActor</c>
/// (tagged properties via <c>UObject.TryGetValue</c>) and <c>UInstancedStaticMeshComponent.PerInstanceSMData</c>. Cooked
/// levels store only deltas against the archetype, so missing values are looked up along <c>UObject.Template</c> (the
/// Blueprint SCS template), and object references found on a template are mapped back to the actor's own components by
/// name (<c>X_GEN_VARIABLE</c> maps to <c>X</c>).</para>
/// <para>Component classes are recognised by their class chain: native classes by name (CUE4Parse has no class layouts
/// for SCUM's <c>/Script</c> classes and returns them as plain <c>UObject</c>s, so e.g. <c>InteriorStaticMeshComponent</c>
/// is a static mesh component because its name ends in <c>StaticMeshComponent</c> and it carries <c>StaticMesh</c>),
/// Blueprint component classes through the <c>SuperStruct</c> chain of their Blueprint-generated classes (the
/// <c>SuperIndex</c> of the class export).</para>
/// <para>Blueprint expansion (construction scripts, child actors): see
/// <see cref="Cue4ParseLevelReaderOptions.ExpandBlueprintComponents"/> and
/// <see cref="Cue4ParseLevelReaderOptions.ExpandChildActors"/>.</para>
/// </remarks>
public sealed partial class Cue4ParseLevelReader : ILevelReader
{
    private const string GenVariableSuffix = "_GEN_VARIABLE";

    // CUE4Parse 1.2.2 turns each instance's FMatrix into FInstancedStaticMeshInstanceData.TransformData with approximate
    // inverse square roots (about 0.2 degrees of rotation error, unnormalised quaternions). The raw matrix is kept in a
    // private field; decomposing it with Core's exact port of FTransform::SetFromMatrix preserves stored transforms.
    private static readonly FieldInfo? InstanceMatrixField =
        typeof(FInstancedStaticMeshInstanceData).GetField("Transform", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    // SCUM paints foliage with its own native component classes (FoliageInstancedTree/Bush/Grass, /Script/ConZ). CUE4Parse
    // does not know them, reads them as plain objects and skips their per-instance data: about 75% of the island's trees,
    // bushes and grass went missing. They derive from the engine's foliage component, so they are read as one.
    private static readonly HashSet<string> FoliageClasses = new(StringComparer.Ordinal);

    private readonly AssetCatalog _catalog;
    private readonly ILogger _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, FTransform>> _meshSockets =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a reader over <paramref name="catalog"/> (not owned).</summary>
    public Cue4ParseLevelReader(AssetCatalog catalog, Cue4ParseLevelReaderOptions? options = null, ILogger? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Options = options ?? new Cue4ParseLevelReaderOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Reader options.</summary>
    public Cue4ParseLevelReaderOptions Options { get; }

    /// <inheritdoc />
    public string DisplayName => _catalog.DisplayName;

    /// <inheritdoc />
    public bool LevelExists(string packagePath) =>
        _catalog.TryGetPackageFile(packagePath, out var file) && file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public LevelData ReadLevel(string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var package = _catalog.LoadPackage(packagePath);
        RegisterFoliageClasses(package);
        var table = ExportTable.Create(package, _catalog.ProjectName);
        var warnings = new List<string>();

        var worldIndex = table.FindFirst("World");
        var levelIndex = -1;
        if (worldIndex >= 0 && TryLoad(package, worldIndex, warnings) is UWorld world && world.PersistentLevel is { IsExport: true } pl)
        {
            levelIndex = pl.Index - 1;
        }

        if (levelIndex < 0)
        {
            levelIndex = table.FindFirst("Level");
        }

        if (levelIndex < 0 || TryLoad(package, levelIndex, warnings) is not ULevel level)
        {
            throw new InvalidDataException($"{table.PackagePath} is not a level package (no World/Level export).");
        }

        var actorIndices = new List<int>();
        var actorSet = new HashSet<int>();
        foreach (var a in level.Actors ?? [])
        {
            if (a is { IsExport: true } && a.Index - 1 < table.Count && actorSet.Add(a.Index - 1))
            {
                actorIndices.Add(a.Index - 1);
            }
        }

        var componentsByActor = new Dictionary<int, Dictionary<string, int>>();
        for (var i = 0; i < table.Count; i++)
        {
            if (actorSet.Contains(table.Outer[i]))
            {
                if (!componentsByActor.TryGetValue(table.Outer[i], out var byName))
                {
                    componentsByActor[table.Outer[i]] = byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                byName.TryAdd(table.Names[i], i);
            }
        }

        var exports = new LevelExportData[table.Count];
        var rootTargets = new HashSet<int>();
        var sockets = new List<(int Index, string Socket)>();
        for (var i = 0; i < table.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = table.Header(i);
            if (actorSet.Contains(i))
            {
                componentsByActor.TryGetValue(i, out var own);
                exports[i] = ReadActor(package, header, own, warnings);
                if (exports[i].RootComponent is { } root && root >= 0 && root < table.Count)
                {
                    rootTargets.Add(root);
                }
            }
            else if (actorSet.Contains(table.Outer[i]))
            {
                exports[i] = ReadComponent(package, header, componentsByActor[table.Outer[i]], warnings, out var socketName);
                if (socketName is not null)
                {
                    sockets.Add((i, socketName));
                }
            }
            else
            {
                exports[i] = header;
            }
        }

        // A component on a mesh socket sits at Relative * Socket * Parent; the socket often carries a scale (0.01 on the
        // ApexHunt display joints), so ignoring it made every link of such a chain 100x too big.
        foreach (var (i, socketName) in sockets)
        {
            if (exports[i].AttachParent is { } p && exports[p].StaticMesh is { } mesh && MeshSockets(mesh).TryGetValue(socketName, out var socket))
            {
                exports[i] = exports[i] with { AttachSocket = socket };
            }
        }

        foreach (var root in rootTargets)
        {
            if (!exports[root].IsSceneComponent)
            {
                exports[root] = exports[root] with { IsSceneComponent = true, IsComponent = true };
            }
        }

        IReadOnlyList<LevelExportData> synthesized = [];
        if (Options.ResolveTemplates && (Options.ExpandBlueprintComponents || Options.ExpandChildActors))
        {
            var expansion = new BlueprintExpansion(this, package, exports, warnings);
            foreach (var actorIndex in actorIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                componentsByActor.TryGetValue(actorIndex, out var own);
                expansion.ExpandActor(actorIndex, own);
            }

            expansion.ReportMissingClasses();
            synthesized = expansion.Synthesized;
        }

        _logger.LogDebug("Read {Package}: {Exports} exports, {Actors} actors, {Synthesized} synthesized components, {Warnings} warnings.",
            table.PackagePath, table.Count, actorIndices.Count, synthesized.Count, warnings.Count);
        return new LevelData
        {
            PackagePath = table.PackagePath,
            Exports = exports,
            ActorIndices = actorIndices,
            LevelExportIndex = levelIndex,
            WorldExportIndex = worldIndex,
            LevelScriptActorIndex = level.LevelScriptActor is { IsExport: true } ls ? ls.Index - 1 : -1,
            Warnings = warnings,
            SynthesizedComponents = synthesized,
        };
    }

    /// <summary>The sockets of a static mesh by name (relative location, rotation and scale), loaded once per mesh.</summary>
    private IReadOnlyDictionary<string, FTransform> MeshSockets(string meshPath) => _meshSockets.GetOrAdd(meshPath, path =>
    {
        var result = new Dictionary<string, FTransform>(StringComparer.OrdinalIgnoreCase);
        var (packagePath, objectName) = AssetPaths.SplitObjectPath(path);
        if (string.IsNullOrEmpty(objectName) || !_catalog.TryLoadPackage(packagePath, out var loaded) || loaded is not Package package)
        {
            return result;
        }

        var index = FindExport(package, objectName, -1);
        if (index < 0 || TryLoad(package, index, []) is not CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh mesh)
        {
            return result;
        }

        foreach (var socketIndex in mesh.Sockets ?? [])
        {
            if (socketIndex.Load() is not { } socket || !socket.TryGetValue(out FName name, "SocketName") || name.IsNone)
            {
                continue;
            }

            var l = socket.GetOrDefault("RelativeLocation", new UeVector(0, 0, 0));
            var r = socket.GetOrDefault("RelativeRotation", new UeRotator(0, 0, 0));
            var s = socket.GetOrDefault("RelativeScale", new UeVector(1, 1, 1));
            result.TryAdd(name.Text, new Model.TransformValue(new FVector(l.X, l.Y, l.Z), new FRotator(r.Pitch, r.Yaw, r.Roll), new FVector(s.X, s.Y, s.Z)).ToTransform());
        }

        return result;
    });

    /// <summary>Maps every <c>FoliageInstanced*</c> class the package imports onto CUE4Parse's foliage component (see <see cref="FoliageClasses"/>).</summary>
    private static void RegisterFoliageClasses(IPackage loaded)
    {
        if (loaded is not Package package)
        {
            return;
        }

        foreach (var import in package.ImportMap)
        {
            var name = import.ObjectName.Text;
            if (import.ClassName.Text == "Class" && name.StartsWith("FoliageInstanced", StringComparison.Ordinal))
            {
                lock (FoliageClasses)
                {
                    if (FoliageClasses.Add(name))
                    {
                        ObjectTypeRegistry.RegisterClass(name, typeof(UFoliageInstancedStaticMeshComponent));
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StreamingLevelInfo> ReadStreamingLevels(string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var package = _catalog.LoadPackage(packagePath);
        var table = ExportTable.Create(package, _catalog.ProjectName);
        var warnings = new List<string>();
        var worldIndex = table.FindFirst("World");
        if (worldIndex < 0 || TryLoad(package, worldIndex, warnings) is not UWorld world)
        {
            throw new InvalidDataException($"{table.PackagePath} has no World export.");
        }

        var entries = world.StreamingLevels is { Length: > 0 } native
            ? native
            : world.TryGetValue(out FPackageIndex[] tagged, "StreamingLevels") ? tagged : [];
        var result = new List<StreamingLevelInfo>(entries.Length);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is not { IsExport: true } || entry.Index - 1 >= table.Count)
            {
                continue;
            }

            var index = entry.Index - 1;
            if (TryLoad(package, index, warnings) is not { } streaming)
            {
                continue;
            }

            var worldAsset = streaming.TryGetValue(out FSoftObjectPath soft, "WorldAsset") ? SoftPathText(soft) : string.Empty;
            var packageToLoad = streaming.TryGetValue(out FName toLoad, "PackageNameToLoad") && !toLoad.IsNone ? toLoad.Text : null;
            var target = !string.IsNullOrEmpty(packageToLoad)
                ? AssetPaths.SplitObjectPath(packageToLoad).PackagePath
                : worldAsset.Length > 0 ? AssetPaths.SplitObjectPath(worldAsset).PackagePath : string.Empty;
            var transform = streaming.TryGetValue(out FStructFallback lt, "LevelTransform") ? ReadTransformStruct(lt) : FTransform.Identity;
            result.Add(new StreamingLevelInfo(
                table.Names[index],
                table.ClassNames[index],
                worldAsset,
                AssetPaths.NormalizeObjectPath(target, _catalog.ProjectName),
                transform,
                streaming.TryGetValue(out bool loaded, "bInitiallyLoaded") && loaded,
                streaming.TryGetValue(out bool visible, "bInitiallyVisible") && visible));
        }

        foreach (var w in warnings)
        {
            _logger.LogDebug("{Package}: {Warning}", table.PackagePath, w);
        }

        return result;
    }

    private LevelExportData ReadActor(IPackage package, LevelExportData header, Dictionary<string, int>? own, List<string> warnings)
    {
        if (TryLoad(package, header.Index, warnings) is not { } actor)
        {
            return header;
        }

        var root = ResolveComponentReference(package, actor, "RootComponent", own, out _);
        return header with
        {
            IsLoaded = true,
            PropertyNames = PropertyNames(actor),
            RootComponent = root,
            ParentComponent = OwnExportReference(package, actor, "ParentComponent"),
        };
    }

    private LevelExportData ReadComponent(IPackage package, LevelExportData header, Dictionary<string, int> siblings, List<string> warnings,
        out string? socketName)
    {
        socketName = null;
        if (TryLoad(package, header.Index, warnings) is not { } obj)
        {
            return header;
        }

        var templates = new TemplateChain(this, obj);
        var classChain = ClassChain(header.ClassPath, header.IsBlueprintClass);
        var values = ReadValues(templates, header.Name, classChain, warnings);
        var parent = ResolveComponentReference(package, templates, "AttachParent", siblings, out var hasAttachProperty);
        if (hasAttachProperty && parent is null)
        {
            warnings.Add($"{header.Name}: AttachParent could not be resolved inside the level.");
        }

        var ignored = false;
        if (parent is not null && TryGetProperty(templates, "AttachSocketName", out FName socket, ref ignored) && !socket.IsNone)
        {
            socketName = socket.Text;
        }

        return values.ApplyTo(header) with
        {
            PropertyNames = PropertyNames(obj),
            IsSceneComponent = values.IsScene || hasAttachProperty,
            AttachParent = parent,
            ChildActor = classChain.Contains(ChildActorComponentClass, StringComparer.Ordinal) ? OwnExportReference(package, obj, "ChildActor") : null,
        };
    }

    /// <summary>
    /// Reads the transform, flags, mesh, instances, visibility and child actor class of a component (or component
    /// template) along its template chain and classifies it from <paramref name="classChain"/> (most derived first).
    /// </summary>
    private ComponentValues ReadValues(TemplateChain templates, string name, IReadOnlyList<string> classChain, List<string> warnings)
    {
        var obj = templates.Object;
        var fromTemplate = false;
        FVector? location = TryGetProperty(templates, "RelativeLocation", out UeVector l, ref fromTemplate) ? new FVector(l.X, l.Y, l.Z) : null;
        FRotator? rotation = TryGetProperty(templates, "RelativeRotation", out UeRotator r, ref fromTemplate) ? new FRotator(r.Pitch, r.Yaw, r.Roll) : null;
        FVector? scale = TryGetProperty(templates, "RelativeScale3D", out UeVector s, ref fromTemplate) ? new FVector(s.X, s.Y, s.Z) : null;
        var ignored = false;
        var absLocation = TryGetProperty(templates, "bAbsoluteLocation", out bool al, ref ignored) && al;
        var absRotation = TryGetProperty(templates, "bAbsoluteRotation", out bool ar, ref ignored) && ar;
        var absScale = TryGetProperty(templates, "bAbsoluteScale", out bool asc, ref ignored) && asc;
        var visible = !(TryGetProperty(templates, "bVisible", out bool v, ref ignored) && !v)
                      && !(TryGetProperty(templates, "bHiddenInGame", out bool hidden, ref ignored) && hidden);

        string? mesh = null;
        if (TryGetProperty(templates, "StaticMesh", out FPackageIndex meshIndex, ref ignored) && !meshIndex.IsNull)
        {
            mesh = ObjectPathOf(meshIndex);
        }

        // Per-slot material replacements (a house in another colour, a painted car); empty slots keep the mesh's own.
        IReadOnlyList<string?>? overrides = null;
        if (mesh is not null && TryGetProperty(templates, "OverrideMaterials", out FPackageIndex[] overrideIndices, ref ignored)
            && overrideIndices.Any(i => i is { IsNull: false }))
        {
            overrides = overrideIndices.Select(i => i is { IsNull: false } ? ObjectPathOf(i) : null).ToList();
        }

        IReadOnlyList<FTransform>? instances = null;
        int? endCullDistance = null;
        var isInstancedClass = obj is UInstancedStaticMeshComponent || classChain.Any(IsInstancedClassName);
        if (isInstancedClass && Options.ReadInstances)
        {
            instances = ReadInstances(templates, name, classChain.Count > 0 ? classChain[0] : string.Empty, warnings, ref fromTemplate);
            // Foliage painting copies the foliage type's CullDistance.Max here; the game draws no instance beyond it.
            if (TryGetProperty(templates, "InstanceEndCullDistance", out int endCull, ref ignored) && endCull > 0)
            {
                endCullDistance = endCull;
            }
        }

        string? childActorClass = null;
        if (classChain.Contains(ChildActorComponentClass, StringComparer.Ordinal)
            && TryGetProperty(templates, "ChildActorClass", out FPackageIndex childClass, ref ignored) && !childClass.IsNull)
        {
            childActorClass = ObjectPathOf(childClass);
        }

        var spline = obj is USplineMeshComponent || classChain.Contains(SplineMeshComponentClass, StringComparer.Ordinal)
            ? ReadSplineMesh(templates)
            : null;

        var isMesh = obj is UStaticMeshComponent || isInstancedClass || mesh is not null || classChain.Any(IsStaticMeshClassName);
        var isScene = obj is USceneComponent || isMesh || location is not null || rotation is not null || scale is not null
                      || instances is not null || childActorClass is not null || classChain.Any(IsSceneClassName);
        var isComponent = obj is UActorComponent || isScene
                          || classChain.Any(c => c.EndsWith("Component", StringComparison.Ordinal) || c.EndsWith("Component_C", StringComparison.Ordinal));
        return new ComponentValues(location, rotation, scale, absLocation, absRotation, absScale, mesh, instances, isInstancedClass,
            isMesh, isScene, isComponent, visible, childActorClass, spline, fromTemplate, endCullDistance, overrides);
    }

    private IReadOnlyList<FTransform> ReadInstances(TemplateChain templates, string name, string className, List<string> warnings, ref bool fromTemplate)
    {
        if (templates.Object is not UInstancedStaticMeshComponent)
        {
            warnings.Add($"{name}: class {className} is not known to CUE4Parse; its instances cannot be read.");
            return [];
        }

        for (var depth = 0; templates[depth] is { } current; depth++)
        {
            if (current is UInstancedStaticMeshComponent { PerInstanceSMData: { Length: > 0 } data })
            {
                fromTemplate |= depth > 0;
                return data.Select(ConvertInstance).ToArray();
            }
        }

        return [];
    }

    /// <summary>
    /// Looks a property up on <paramref name="obj"/>, then along its template chain; sets <paramref name="fromTemplate"/>
    /// when the value came from a template.
    /// </summary>
    private bool TryGetProperty<T>(UObject obj, string name, out T value, ref bool fromTemplate) =>
        TryGetProperty(new TemplateChain(this, obj), name, out value, ref fromTemplate);

    /// <summary>Looks a property up along a (memoized) template chain.</summary>
    private bool TryGetProperty<T>(TemplateChain templates, string name, out T value, ref bool fromTemplate)
    {
        for (var depth = 0; templates[depth] is { } current; depth++)
        {
            try
            {
                if (current.TryGetValue(out value, name))
                {
                    fromTemplate |= depth > 0;
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Could not read {Property} of {Object}.", name, current.Name);
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// An object property stored on <paramref name="obj"/> itself (no template lookup) that points at an export of
    /// <paramref name="package"/>; returns its zero-based export index or null.
    /// </summary>
    private int? OwnExportReference(IPackage package, UObject obj, string property)
    {
        try
        {
            return obj.TryGetValue(out FPackageIndex index, property) && index is { IsExport: true } && ReferenceEquals(index.Owner, package)
                ? index.Index - 1
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not read {Property} of {Object}.", property, obj.Name);
            return null;
        }
    }

    /// <summary>
    /// Resolves an object property that points at a component (RootComponent, AttachParent) to an export index of this
    /// level. A value found on a template lives in the template's package, so it is mapped by name onto
    /// <paramref name="components"/> (the owning actor's components).
    /// </summary>
    private int? ResolveComponentReference(IPackage level, UObject obj, string property, Dictionary<string, int>? components, out bool found) =>
        ResolveComponentReference(level, new TemplateChain(this, obj), property, components, out found);

    private int? ResolveComponentReference(IPackage level, TemplateChain templates, string property, Dictionary<string, int>? components, out bool found)
    {
        var fromTemplate = false;
        found = TryGetProperty(templates, property, out FPackageIndex index, ref fromTemplate) && index is { IsNull: false };
        if (!found)
        {
            return null;
        }

        if (index.IsExport && ReferenceEquals(index.Owner, level))
        {
            return index.Index - 1;
        }

        if (components is null || !index.IsExport || index.Owner?.ResolvePackageIndex(index)?.Name.Text is not { } name)
        {
            return null;
        }

        if (components.TryGetValue(name, out var exact))
        {
            return exact;
        }

        return name.EndsWith(GenVariableSuffix, StringComparison.Ordinal)
               && components.TryGetValue(name[..^GenVariableSuffix.Length], out var stripped)
            ? stripped
            : null;
    }

    private UObject? LoadTemplate(UObject obj)
    {
        try
        {
            return obj.Template?.Load();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not load the template of {Object}.", obj.Name);
            return null;
        }
    }

    private UObject? TryLoad(IPackage package, int index, List<string> warnings)
    {
        try
        {
            return package.GetExport(index);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warnings.Add($"Export {index} could not be read: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private string ObjectPathOf(FPackageIndex index)
    {
        if (index.Owner is Package legacy)
        {
            return ExportTable.PathOf(legacy, index.Index, _catalog.ProjectName);
        }

        try
        {
            return AssetPaths.NormalizeObjectPath(index.ResolvedObject?.GetPathName() ?? index.Name, _catalog.ProjectName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not resolve object reference {Index}.", index.Index);
            return string.Empty;
        }
    }

    private static IReadOnlyList<string> PropertyNames(UObject obj) => obj.Properties.Select(p => p.Name.Text).ToArray();

    private static string SoftPathText(FSoftObjectPath path)
    {
        var asset = path.AssetPathName.IsNone ? string.Empty : path.AssetPathName.Text;
        return string.IsNullOrEmpty(path.SubPathString) ? asset : asset + ":" + path.SubPathString;
    }

    private static FTransform ReadTransformStruct(FStructFallback s)
    {
        var q = s.TryGetValue(out UeQuat rq, "Rotation") ? new FQuat(rq.X, rq.Y, rq.Z, rq.W) : FQuat.Identity;
        var t = s.TryGetValue(out UeVector tv, "Translation") ? new FVector(tv.X, tv.Y, tv.Z) : FVector.Zero;
        var sc = s.TryGetValue(out UeVector sv, "Scale3D") ? new FVector(sv.X, sv.Y, sv.Z) : FVector.One;
        return new FTransform(q, t, sc);
    }

    /// <summary>Instance transform from the stored matrix (exact), falling back to CUE4Parse's decomposition.</summary>
    private static FTransform ConvertInstance(FInstancedStaticMeshInstanceData data)
    {
        if (InstanceMatrixField?.GetValue(data) is UeMatrix m)
        {
            return FTransform.FromMatrix(new Matrix4x4(
                m.M00, m.M01, m.M02, m.M03,
                m.M10, m.M11, m.M12, m.M13,
                m.M20, m.M21, m.M22, m.M23,
                m.M30, m.M31, m.M32, m.M33));
        }

        return Convert(data.TransformData);
    }

    private static FTransform Convert(UeTransform t) =>
        new(new FQuat(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W).GetNormalized(),
            new FVector(t.Translation.X, t.Translation.Y, t.Translation.Z),
            new FVector(t.Scale3D.X, t.Scale3D.Y, t.Scale3D.Z));

    /// <summary>
    /// Names, classes, outers and templates of every export, from the export/import maps of a legacy package (no
    /// deserialization, no other package loaded), or from resolved objects for other package kinds.
    /// </summary>
    private sealed class ExportTable
    {
        private readonly LevelExportData[] _headers;

        private ExportTable(string packagePath, LevelExportData[] headers)
        {
            PackagePath = packagePath;
            _headers = headers;
            Names = headers.Select(h => h.Name).ToArray();
            ClassNames = headers.Select(h => h.ClassName).ToArray();
            Outer = headers.Select(h => h.OuterIndex).ToArray();
        }

        public string PackagePath { get; }

        public int Count => _headers.Length;

        public string[] Names { get; }

        public string[] ClassNames { get; }

        public int[] Outer { get; }

        public LevelExportData Header(int index) => _headers[index];

        public int FindFirst(string className) => Array.FindIndex(_headers, h => h.ClassName.Equals(className, StringComparison.Ordinal));

        public static ExportTable Create(IPackage package, string projectName)
        {
            var packagePath = AssetPaths.NormalizeObjectPath(package.Name, projectName);
            var headers = new LevelExportData[package.ExportMapLength];
            if (package is Package legacy)
            {
                for (var i = 0; i < legacy.ExportMap.Length; i++)
                {
                    var e = legacy.ExportMap[i];
                    var classPath = e.ClassIndex is { IsNull: false } ci ? PathOf(legacy, ci.Index, projectName) : string.Empty;
                    headers[i] = new LevelExportData
                    {
                        Index = i,
                        Name = e.ObjectName.Text,
                        ClassPath = classPath,
                        ClassName = ShortName(classPath),
                        IsBlueprintClass = IsBlueprintClassImport(legacy, e.ClassIndex, classPath),
                        OuterIndex = e.OuterIndex is { IsExport: true } oi ? oi.Index - 1 : -1,
                        TemplatePath = e.TemplateIndex is { IsNull: false } ti ? PathOf(legacy, ti.Index, projectName) : null,
                    };
                }

                return new ExportTable(packagePath, headers);
            }

            for (var i = 0; i < headers.Length; i++)
            {
                var resolved = package.ResolvePackageIndex(new FPackageIndex(package, i + 1));
                var classPath = resolved?.Class?.GetPathName() ?? string.Empty;
                headers[i] = new LevelExportData
                {
                    Index = i,
                    Name = resolved?.Name.Text ?? $"Export_{i}",
                    ClassPath = AssetPaths.NormalizeObjectPath(classPath, projectName),
                    ClassName = ShortName(classPath),
                    IsBlueprintClass = !classPath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) && classPath.EndsWith("_C", StringComparison.Ordinal),
                    OuterIndex = resolved?.Outer is { ExportIndex: >= 0 } outer ? outer.ExportIndex : -1,
                };
            }

            return new ExportTable(packagePath, headers);
        }

        /// <summary>
        /// Object path of a package index (import or export) of <paramref name="package"/>, built from the maps:
        /// <c>/Package.Object:Sub.Sub</c> (UE path syntax).
        /// </summary>
        public static string PathOf(Package package, int packageIndex, string projectName)
        {
            var chain = new List<string>();
            var index = packageIndex;
            for (var guard = 0; index != 0 && guard < 64; guard++)
            {
                if (index < 0)
                {
                    var import = package.ImportMap[-index - 1];
                    chain.Add(import.ObjectName.Text);
                    index = import.OuterIndex?.Index ?? 0;
                }
                else
                {
                    var export = package.ExportMap[index - 1];
                    chain.Add(export.ObjectName.Text);
                    index = export.OuterIndex?.Index ?? 0;
                    if (index == 0)
                    {
                        // Top-level export: its outer is this package.
                        chain.Add(AssetPaths.NormalizeObjectPath(package.Name, projectName));
                    }
                }
            }

            chain.Reverse();
            var sb = new StringBuilder(chain.Count > 0 ? chain[0] : string.Empty);
            for (var i = 1; i < chain.Count; i++)
            {
                sb.Append(i == 1 ? '.' : i == 2 ? ':' : '.').Append(chain[i]);
            }

            return sb.ToString();
        }

        public static bool IsBlueprintClassImport(Package package, FPackageIndex? classIndex, string classPath)
        {
            if (classIndex is not { IsImport: true } ci)
            {
                return false;
            }

            var import = package.ImportMap[-ci.Index - 1];
            return import.ClassName.Text.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal)
                   || (!classPath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) && classPath.EndsWith("_C", StringComparison.Ordinal));
        }

        public static string ShortName(string path)
        {
            var cut = path.LastIndexOfAny(['.', ':', '/']);
            return cut < 0 ? path : path[(cut + 1)..];
        }
    }
}
