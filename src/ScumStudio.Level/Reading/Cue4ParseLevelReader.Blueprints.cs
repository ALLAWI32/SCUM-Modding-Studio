using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Reading;

/// <summary>
/// Blueprint support of <see cref="Cue4ParseLevelReader"/>: class chains of Blueprint-generated classes, their
/// construction scripts (SCS) and inheritable component overrides, and the expansion of Blueprint actors and child actors
/// into synthesized components.
/// </summary>
/// <remarks>
/// <para>Cooked layout (UE 4.27, confirmed on SCUM 1.3.3 packages): a Blueprint package holds the class export
/// (<c>BP_X_C</c>, class <c>BlueprintGeneratedClass</c>, <c>SuperIndex</c> = the parent class), its CDO
/// (<c>Default__BP_X_C</c>, whose native default subobjects such as <c>Root</c> are exports with the CDO as outer),
/// <c>SimpleConstructionScript_0</c> (outer = the class; tagged <c>RootNodes</c>, <c>AllNodes</c>,
/// <c>DefaultSceneRootNode</c>), one <c>SCS_Node_N</c> per node (outer = the SCS; tagged <c>ComponentClass</c>,
/// <c>ComponentTemplate</c>, <c>ChildNodes</c>, <c>InternalVariableName</c>, <c>VariableGuid</c> and, for a node
/// attached to an inherited or native component, <c>ParentComponentOrVariableName</c>,
/// <c>bIsParentComponentNative</c>, <c>ParentComponentOwnerClassName</c>, <c>AttachToName</c>), one component template
/// per node named <c>&lt;Variable&gt;_GEN_VARIABLE</c> (outer = the class), an optional <c>InheritableComponentHandler</c>
/// (outer = the class; tagged <c>Records[]</c> of <c>ComponentTemplate</c> + <c>ComponentKey</c>
/// {<c>OwnerClass</c>, <c>SCSVariableName</c>, <c>AssociatedGuid</c>}) whose override templates are also
/// <c>&lt;Variable&gt;_GEN_VARIABLE</c> exports with <c>RF_InheritableComponentTemplate</c>, and per child actor component
/// a child actor template <c>&lt;Variable&gt;_GEN_VARIABLE_&lt;ChildClass&gt;_CAT</c> (outer = the component template)
/// with its own default subobjects.</para>
/// <para>A level stores every component a placed Blueprint instance owns (<c>CreationMethod = SimpleConstructionScript</c>,
/// only delta properties, template = the <c>_GEN_VARIABLE</c> export) and every child actor a child actor component
/// spawned, as a level actor named <c>&lt;Component&gt;_GEN_VARIABLE_&lt;ChildClass&gt;_CAT_&lt;N&gt;</c> with
/// <c>ParentComponent</c> = the component, the component holding <c>ChildActor</c> = that actor. The SCS's
/// <c>DefaultSceneRootNode</c> is only in <c>RootNodes</c> when it is used, so walking <c>RootNodes</c> does not
/// resurrect an unused default root.</para>
/// <para>The class hierarchy is read from the export/import maps (the class export's <c>SuperIndex</c>) and only the SCS,
/// SCS node, inheritable component handler and template exports are deserialized, so a Blueprint class does not have to
/// be deserializable as a <c>UBlueprintGeneratedClass</c> (CUE4Parse 1.2.2 has no dedicated types for SCS nodes; they are
/// read as tagged properties).</para>
/// </remarks>
public sealed partial class Cue4ParseLevelReader
{
    private const string ChildActorComponentClass = "ChildActorComponent";
    private const int MaxBlueprintParentDepth = 16;
    private const int MaxScsNodeDepth = 64;

    /// <summary>
    /// Engine scene component classes (by short name) whose instances CUE4Parse may return as plain objects. Only exact
    /// engine names: SCUM's own classes are not guessed from their names (e.g. <c>AbandonedBunkerLightComponent</c> is not
    /// a light), except for the <c>*MeshComponent</c> suffix.
    /// </summary>
    private static readonly HashSet<string> SceneClassNames = new(StringComparer.Ordinal)
    {
        "SceneComponent", "PrimitiveComponent", "MeshComponent", "ChildActorComponent", "ShapeComponent", "BoxComponent",
        "SphereComponent", "CapsuleComponent", "ArrowComponent", "BillboardComponent", "DecalComponent", "TextRenderComponent",
        "AudioComponent", "SplineComponent", "SplineMeshComponent", "ParticleSystemComponent", "SpringArmComponent",
        "CameraComponent", "WidgetComponent", "BrushComponent", "PostProcessComponent", "ExponentialHeightFogComponent",
        "LightComponent", "LocalLightComponent", "PointLightComponent", "SpotLightComponent", "RectLightComponent",
        "DirectionalLightComponent", "SkyLightComponent", "SceneCaptureComponent2D", "SceneCaptureComponentCube",
    };

    private readonly Dictionary<string, BlueprintClass?> _blueprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _blueprintsLock = new();

    private static bool IsInstancedClassName(string className) =>
        className.Contains("InstancedStaticMesh", StringComparison.Ordinal) || className.StartsWith("FoliageInstanced", StringComparison.Ordinal);

    /// <summary>
    /// StaticMeshComponent and its subclasses by name: ISM/HISM/foliage, SCUM's <c>InteriorStaticMeshComponent</c>, and
    /// <c>SplineMeshComponent</c>.
    /// </summary>
    private static bool IsStaticMeshClassName(string className) =>
        className.EndsWith("StaticMeshComponent", StringComparison.Ordinal) || className.Equals("SplineMeshComponent", StringComparison.Ordinal);

    private static bool IsSceneClassName(string className) =>
        SceneClassNames.Contains(className) || className.EndsWith("MeshComponent", StringComparison.Ordinal);

    private static bool IsComponentClassName(string className) =>
        className.EndsWith("Component", StringComparison.Ordinal) || className.EndsWith("Component_C", StringComparison.Ordinal)
                                                                 || IsSceneClassName(className);

    private static string StripGenVariable(string name) =>
        name.EndsWith(GenVariableSuffix, StringComparison.Ordinal) ? name[..^GenVariableSuffix.Length] : name;

    /// <summary>
    /// Short class names from the most derived class up: the class itself for a native class; for a Blueprint-generated
    /// class its Blueprint ancestors and the first native ancestor (read from the class exports' <c>SuperIndex</c>).
    /// </summary>
    private IReadOnlyList<string> ClassChain(string classPath, bool isBlueprint)
    {
        var name = ExportTable.ShortName(classPath);
        if (!isBlueprint || !Options.ResolveTemplates)
        {
            return [name];
        }

        return GetBlueprint(classPath)?.ClassChain ?? [name];
    }

    /// <summary>Class path, short class name and Blueprint flag of an export of <paramref name="package"/> (from the maps).</summary>
    private (string ClassPath, string ClassName, bool IsBlueprint) ClassOfExport(Package package, int export)
    {
        var classIndex = package.ExportMap[export].ClassIndex;
        if (classIndex is not { IsNull: false })
        {
            return (string.Empty, string.Empty, false);
        }

        var classPath = ExportTable.PathOf(package, classIndex.Index, _catalog.ProjectName);
        var isBlueprint = classIndex.IsExport || ExportTable.IsBlueprintClassImport(package, classIndex, classPath);
        return (classPath, ExportTable.ShortName(classPath), isBlueprint);
    }

    private UObject? TryGetExport(Package package, int export)
    {
        try
        {
            return export >= 0 && export < package.ExportMapLength ? ((IPackage)package).GetExport(export) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not read export {Export} of {Package}.", export, package.Name);
            return null;
        }
    }

    private static int OuterOf(FObjectExport export) => export.OuterIndex is { IsExport: true } outer ? outer.Index - 1 : -1;

    private static int FindExport(Package package, string name, int outer)
    {
        for (var i = 0; i < package.ExportMap.Length; i++)
        {
            var e = package.ExportMap[i];
            if (OuterOf(e) == outer && e.ObjectName.Text.Equals(name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindExportOfClass(Package package, string className, int outer)
    {
        for (var i = 0; i < package.ExportMap.Length; i++)
        {
            var e = package.ExportMap[i];
            if (OuterOf(e) == outer && e.ClassIndex is { IsImport: true } ci
                                    && package.ImportMap[-ci.Index - 1].ObjectName.Text.Equals(className, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Direct subobjects (exports whose outer is <paramref name="outer"/>) of a package, in export order.</summary>
    private static IEnumerable<(Package Package, int Export, string Name)> Subobjects(Package package, int outer)
    {
        for (var i = 0; i < package.ExportMap.Length; i++)
        {
            if (OuterOf(package.ExportMap[i]) == outer)
            {
                yield return (package, i, package.ExportMap[i].ObjectName.Text);
            }
        }
    }

    /// <summary>Object name of a reference (export or import of its package), <c>_GEN_VARIABLE</c> stripped; null for null.</summary>
    private static string? LocalName(FPackageIndex index)
    {
        if (index is not { IsNull: false } || index.Owner is not Package package)
        {
            return null;
        }

        var name = index.IsExport ? package.ExportMap[index.Index - 1].ObjectName.Text : package.ImportMap[-index.Index - 1].ObjectName.Text;
        return StripGenVariable(name);
    }

    private static bool IsBlueprintClassReference(FPackageIndex classIndex, string classPath) =>
        classIndex.Owner is Package package && classIndex.IsImport
            ? ExportTable.IsBlueprintClassImport(package, classIndex, classPath)
            : classIndex.IsExport
              || (!classPath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) && classPath.EndsWith("_C", StringComparison.Ordinal));

    /// <summary>The Blueprint-generated class at <paramref name="classPath"/> (cached), or null when it is not in the catalog.</summary>
    private BlueprintClass? GetBlueprint(string classPath, int depth = 0)
    {
        if (string.IsNullOrEmpty(classPath))
        {
            return null;
        }

        lock (_blueprintsLock)
        {
            if (_blueprints.TryGetValue(classPath, out var cached))
            {
                return cached;
            }
        }

        BlueprintClass? loaded = null;
        try
        {
            loaded = depth <= MaxBlueprintParentDepth ? LoadBlueprint(classPath, depth) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not read Blueprint class {Class}.", classPath);
        }

        lock (_blueprintsLock)
        {
            if (_blueprints.TryGetValue(classPath, out var existing))
            {
                return existing;
            }

            _blueprints[classPath] = loaded;
            return loaded;
        }
    }

    private BlueprintClass? LoadBlueprint(string classPath, int depth)
    {
        var (packagePath, objectName) = AssetPaths.SplitObjectPath(classPath);
        if (string.IsNullOrEmpty(objectName) || packagePath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)
                                             || !_catalog.TryLoadPackage(packagePath, out var loaded) || loaded is not Package package)
        {
            return null;
        }

        var classExport = FindExport(package, objectName, -1);
        if (classExport < 0)
        {
            return null;
        }

        BlueprintClass? parent = null;
        string? superName = null;
        string? missingParent = null;
        if (package.ExportMap[classExport].SuperIndex is { IsNull: false } super)
        {
            var superPath = ExportTable.PathOf(package, super.Index, _catalog.ProjectName);
            superName = ExportTable.ShortName(superPath);
            if (super.IsExport || ExportTable.IsBlueprintClassImport(package, super, superPath))
            {
                parent = GetBlueprint(superPath, depth + 1);
                missingParent = parent is null ? superPath : null;
            }
        }

        var blueprint = new BlueprintClass
        {
            ClassPath = classPath,
            Package = package,
            DefaultObjectExport = FindExport(package, "Default__" + objectName, -1),
            Parent = parent,
            MissingParentPath = missingParent ?? parent?.MissingParentPath,
            ClassChain = [objectName, .. parent?.ClassChain ?? (superName is null ? [] : [superName])],
        };

        var scs = FindExportOfClass(package, "SimpleConstructionScript", classExport);
        if (scs >= 0 && TryGetExport(package, scs) is { } script && TryGetValue(script, "RootNodes", out FPackageIndex[] roots))
        {
            var visited = new HashSet<int>();
            foreach (var root in roots)
            {
                ReadScsNode(blueprint, root, null, visited, 0);
            }
        }

        var handler = FindExportOfClass(package, "InheritableComponentHandler", classExport);
        if (handler >= 0 && TryGetExport(package, handler) is { } ich && TryGetValue(ich, "Records", out FStructFallback[] records))
        {
            foreach (var record in records)
            {
                if (record.TryGetValue(out FPackageIndex template, "ComponentTemplate") && template is { IsExport: true }
                    && record.TryGetValue(out FStructFallback key, "ComponentKey")
                    && key.TryGetValue(out FName variable, "SCSVariableName") && !variable.IsNone)
                {
                    blueprint.Overrides[variable.Text] = template.Index - 1;
                }
            }
        }

        _logger.LogDebug("Blueprint {Class}: {Nodes} SCS nodes, {Overrides} inherited overrides, parent {Parent}.",
            classPath, blueprint.Nodes.Count, blueprint.Overrides.Count, parent?.ClassPath ?? superName ?? "(none)");
        return blueprint;
    }

    private void ReadScsNode(BlueprintClass blueprint, FPackageIndex index, string? treeParent, HashSet<int> visited, int depth)
    {
        if (index is not { IsExport: true } || depth > MaxScsNodeDepth || !visited.Add(index.Index)
            || TryGetExport(blueprint.Package, index.Index - 1) is not { } node)
        {
            return;
        }

        var template = TryGetValue(node, "ComponentTemplate", out FPackageIndex t) && t is { IsExport: true } ? t.Index - 1 : -1;
        var name = TryGetValue(node, "InternalVariableName", out FName variable) && !variable.IsNone
            ? variable.Text
            : TryGetValue(node, "VariableName", out FName legacy) && !legacy.IsNone
                ? legacy.Text
                : template >= 0 ? StripGenVariable(blueprint.Package.ExportMap[template].ObjectName.Text) : node.Name;
        var parentName = treeParent is null && TryGetValue(node, "ParentComponentOrVariableName", out FName p) && !p.IsNone ? p.Text : null;
        var children = TryGetValue(node, "ChildNodes", out FPackageIndex[] childNodes) ? childNodes : [];
        blueprint.Nodes.Add(new ScsNode(blueprint, name, template, treeParent, parentName, children.Length > 0));
        foreach (var child in children)
        {
            ReadScsNode(blueprint, child, name, visited, depth + 1);
        }
    }

    private bool TryGetValue<T>(UObject obj, string name, out T value)
    {
        try
        {
            if (obj.TryGetValue(out value, name))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not read {Property} of {Object}.", name, obj.Name);
        }

        value = default!;
        return false;
    }

    /// <summary>A component created from a template export (a Blueprint SCS template or a default subobject).</summary>
    private LevelExportData SynthesizeComponent(int id, string name, int actorIndex, Package package, int export, UObject template, List<string> warnings)
    {
        var (classPath, className, isBlueprint) = ClassOfExport(package, export);
        var values = ReadValues(new TemplateChain(this, template), name, ClassChain(classPath, isBlueprint), warnings);
        var header = new LevelExportData
        {
            Index = id,
            Name = name,
            ClassName = className,
            ClassPath = classPath,
            IsBlueprintClass = isBlueprint,
            OuterIndex = actorIndex,
            TemplatePath = ExportTable.PathOf(package, export + 1, _catalog.ProjectName),
        };
        return values.ApplyTo(header) with { IsComponent = true, IsSynthesized = true, UsesTemplateValues = true };
    }

    /// <summary>Values read from a component or template (see <see cref="ReadValues"/>).</summary>
    private readonly record struct ComponentValues(
        FVector? Location,
        FRotator? Rotation,
        FVector? Scale,
        bool AbsoluteLocation,
        bool AbsoluteRotation,
        bool AbsoluteScale,
        string? StaticMesh,
        IReadOnlyList<FTransform>? Instances,
        bool IsInstancedClass,
        bool IsStaticMesh,
        bool IsScene,
        bool IsComponent,
        bool IsVisible,
        string? ChildActorClassPath,
        SplineMeshParams? SplineMesh,
        bool FromTemplate,
        int? InstanceEndCullDistance,
        IReadOnlyList<string?>? OverrideMaterials = null,
        IReadOnlyList<SpawnMarker>? SpawnMarkers = null,
        string? CollisionProfile = null)
    {
        public LevelExportData ApplyTo(LevelExportData header) => header with
        {
            InstanceEndCullDistance = InstanceEndCullDistance,
            IsLoaded = true,
            IsComponent = IsComponent,
            IsSceneComponent = IsScene,
            RelativeLocation = Location,
            RelativeRotation = Rotation,
            RelativeScale3D = Scale,
            AbsoluteLocation = AbsoluteLocation,
            AbsoluteRotation = AbsoluteRotation,
            AbsoluteScale = AbsoluteScale,
            StaticMesh = StaticMesh,
            Instances = Instances,
            UsesTemplateValues = FromTemplate,
            IsStaticMeshComponent = IsStaticMesh,
            IsVisible = IsVisible,
            ChildActorClassPath = ChildActorClassPath,
            SplineMesh = SplineMesh,
            OverrideMaterials = OverrideMaterials,
            SpawnMarkers = SpawnMarkers,
            CollisionProfile = CollisionProfile,
        };
    }

    /// <summary>An object followed by its templates (archetypes), loaded lazily once and bounded by <c>MaxTemplateDepth</c>.</summary>
    private sealed class TemplateChain(Cue4ParseLevelReader reader, UObject obj)
    {
        private readonly List<UObject> _items = [obj];
        private bool _complete = !reader.Options.ResolveTemplates;

        public UObject Object => _items[0];

        /// <summary>The object (depth 0) or its template <paramref name="depth"/> levels up, or null past the end.</summary>
        public UObject? this[int depth]
        {
            get
            {
                while (!_complete && _items.Count <= depth)
                {
                    var next = _items.Count > reader.Options.MaxTemplateDepth ? null : reader.LoadTemplate(_items[^1]);
                    if (next is null || _items.Contains(next))
                    {
                        _complete = true;
                    }
                    else
                    {
                        _items.Add(next);
                    }
                }

                return depth < _items.Count ? _items[depth] : null;
            }
        }
    }

    /// <summary>A Blueprint-generated class: package, class chain, own SCS nodes (pre-order) and inherited overrides.</summary>
    private sealed class BlueprintClass
    {
        public required string ClassPath { get; init; }

        public required Package Package { get; init; }

        public required int DefaultObjectExport { get; init; }

        public BlueprintClass? Parent { get; init; }

        /// <summary>A Blueprint ancestor that is not in the catalog (its SCS nodes are unknown), or null.</summary>
        public string? MissingParentPath { get; init; }

        public required IReadOnlyList<string> ClassChain { get; init; }

        public List<ScsNode> Nodes { get; } = [];

        /// <summary>InheritableComponentHandler records: SCS variable name → template export of <see cref="Package"/>.</summary>
        public Dictionary<string, int> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every SCS node of the class chain in construction order: least derived class first, pre-order.</summary>
        public IEnumerable<ScsNode> AllNodes() => Parent is null ? Nodes : Parent.AllNodes().Concat(Nodes);

        /// <summary>
        /// The template this class uses for <paramref name="node"/> (UE <c>USCS_Node::GetActualComponentTemplate</c>): the
        /// most derived inheritable component override between this class and the node's owner, else the node's own.
        /// </summary>
        public (Package Package, int Export) TemplateFor(ScsNode node)
        {
            for (var c = this; c is not null && !ReferenceEquals(c, node.Owner); c = c.Parent)
            {
                if (c.Overrides.TryGetValue(node.Name, out var export))
                {
                    return (c.Package, export);
                }
            }

            return (node.Owner.Package, node.Template);
        }
    }

    /// <summary>One SCS node: variable name, template export in its owner's package, parent (tree or named) and whether it has children.</summary>
    private sealed record ScsNode(BlueprintClass Owner, string Name, int Template, string? TreeParent, string? ParentComponentOrVariableName, bool HasChildren)
    {
        // Keeps the compiler-generated members from walking into BlueprintClass (and back).
        public bool Equals(ScsNode? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    /// <summary>A child actor component to expand; <paramref name="ItemClassPath"/> instead names a world item spawner's fixed item.</summary>
    private readonly record struct PendingChildActor(int Id, string Name, UObject Object, string? ItemClassPath = null);

    /// <summary>Expands the Blueprint actors of one level into synthesized components (see <see cref="LevelData.SynthesizedComponents"/>).</summary>
    private sealed class BlueprintExpansion(Cue4ParseLevelReader reader, IPackage level, LevelExportData[] exports, List<string> warnings)
    {
        private readonly List<LevelExportData> _synthesized = [];
        private readonly Dictionary<int, int> _positions = new();
        private readonly SortedSet<string> _missingClasses = new(StringComparer.OrdinalIgnoreCase);
        private int _nextId = -2;

        public IReadOnlyList<LevelExportData> Synthesized => _synthesized;

        private Cue4ParseLevelReaderOptions Options => reader.Options;

        /// <summary>
        /// Adds the construction-script components of <paramref name="actorIndex"/> that the level does not store
        /// (<paramref name="own"/> = the actor's subobject exports by name), then expands its child actor components whose
        /// child actor the level does not store.
        /// </summary>
        public void ExpandActor(int actorIndex, Dictionary<string, int>? own)
        {
            var actor = exports[actorIndex];
            var names = own is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(own, StringComparer.OrdinalIgnoreCase);
            var childActors = new List<PendingChildActor>();
            if (Options.ExpandChildActors && own is not null)
            {
                foreach (var index in own.Values.Order())
                {
                    if (exports[index] is { IsComponent: true, ChildActor: null, ChildActorClassPath: not null } cac
                        && reader.TryLoad(level, index, warnings) is { } obj)
                    {
                        childActors.Add(new PendingChildActor(index, cac.Name, obj));
                    }
                    else if (exports[index] is { IsComponent: true, SpawnMarkers: [{ ItemClassPath: { } item }] } spawner
                             && reader.TryLoad(level, index, warnings) is { } spawnerObj)
                    {
                        // A world item spawner's fixed item (a house's drill press, stove, fridge) is drawn where it spawns
                        // (Discord igor: "there is no interaction with certain spawn objects", only an empty pin showed).
                        childActors.Add(new PendingChildActor(index, spawner.Name, spawnerObj, item));
                    }
                }
            }

            if (Options.ExpandBlueprintComponents && actor.IsBlueprintClass)
            {
                if (reader.GetBlueprint(actor.ClassPath) is { } blueprint)
                {
                    NoteMissingParent(blueprint);
                    var root = actor.RootComponent;
                    RunConstructionScript(blueprint, actorIndex, string.Empty, names, ref root, childActors);
                    if (root != actor.RootComponent)
                    {
                        exports[actorIndex] = actor with { RootComponent = root };
                    }
                }
                else
                {
                    _missingClasses.Add(actor.ClassPath);
                }
            }

            if (Options.ExpandChildActors)
            {
                foreach (var child in childActors)
                {
                    ExpandChildActor(actorIndex, child, 1);
                }
            }
        }

        private void NoteMissingParent(BlueprintClass blueprint)
        {
            if (blueprint.MissingParentPath is { } missing)
            {
                _missingClasses.Add(missing);
            }
        }

        /// <summary>
        /// Adds one warning listing the Blueprint classes (or Blueprint ancestors) that are not in the source, whose
        /// construction scripts and child actors therefore could not be checked.
        /// </summary>
        public void ReportMissingClasses()
        {
            if (_missingClasses.Count == 0)
            {
                return;
            }

            warnings.Add($"{_missingClasses.Count} Blueprint class(es) are not in the source, so the construction scripts and child actors that depend on them were not expanded: "
                         + string.Join(", ", _missingClasses.Select(p => ExportTable.ShortName(p))) + ".");
        }

        /// <summary>
        /// UE <c>USimpleConstructionScript::ExecuteScriptOnActor</c> for the whole class chain (parent first): each node
        /// not in <paramref name="names"/> becomes a synthesized component attached to its tree parent, to the component
        /// its <c>ParentComponentOrVariableName</c> names, or to <paramref name="root"/>; the first scene node becomes the
        /// root when there is none.
        /// </summary>
        private void RunConstructionScript(
            BlueprintClass blueprint, int actorIndex, string prefix, Dictionary<string, int> names, ref int? root, List<PendingChildActor> childActors)
        {
            foreach (var node in blueprint.AllNodes())
            {
                if (names.ContainsKey(node.Name))
                {
                    continue;
                }

                var (package, templateExport) = blueprint.TemplateFor(node);
                if (templateExport < 0 || reader.TryGetExport(package, templateExport) is not { } template)
                {
                    warnings.Add($"{exports[actorIndex].Name}: construction script node '{prefix}{node.Name}' of {ExportTable.ShortName(node.Owner.ClassPath)} has no readable component template.");
                    continue;
                }

                int? parent = root;
                if ((node.TreeParent ?? node.ParentComponentOrVariableName) is { } parentName)
                {
                    if (names.TryGetValue(parentName, out var p))
                    {
                        parent = p;
                    }
                    else
                    {
                        warnings.Add($"{exports[actorIndex].Name}: parent '{parentName}' of construction script node '{prefix}{node.Name}' not found; attached to the root.");
                    }
                }

                var id = _nextId--;
                var data = reader.SynthesizeComponent(id, prefix + node.Name, actorIndex, package, templateExport, template, warnings);
                var isScene = data.IsSceneComponent || node.HasChildren || node.TreeParent is not null || node.ParentComponentOrVariableName is not null;
                if (!isScene)
                {
                    parent = null;
                }
                else if (parent is null && root is null)
                {
                    root = id;
                }

                Add(data with { IsSceneComponent = isScene, AttachParent = parent });
                names[node.Name] = id;
                if (data.ChildActorClassPath is not null)
                {
                    childActors.Add(new PendingChildActor(id, prefix + node.Name, template));
                }
            }
        }

        /// <summary>
        /// UE <c>UChildActorComponent::CreateChildActor</c> without a stored child actor: the child's default subobjects
        /// (child actor template first, then the class default object) and its construction script become synthesized
        /// components named <c>&lt;component&gt;/&lt;child component&gt;</c>; the child's root is snapped to the child actor
        /// component (location and rotation zero, its own scale), other unattached scene subobjects hang off the root.
        /// </summary>
        private void ExpandChildActor(int actorIndex, PendingChildActor cac, int depth)
        {
            var owner = exports[actorIndex].Name;
            if (depth > Options.MaxChildActorDepth)
            {
                warnings.Add($"{owner}: child actor '{cac.Name}' is nested deeper than {Options.MaxChildActorDepth}; not expanded.");
                return;
            }

            var templates = new TemplateChain(reader, cac.Object);
            var ignored = false;
            string classPath;
            bool isBlueprintClass;
            if (cac.ItemClassPath is { } itemClass)
            {
                // A spawned item: its class only (no child actor template).
                classPath = itemClass;
                isBlueprintClass = !itemClass.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase);
            }
            else if (reader.TryGetProperty(templates, "ChildActorClass", out FPackageIndex classIndex, ref ignored) && !classIndex.IsNull)
            {
                classPath = reader.ObjectPathOf(classIndex);
                isBlueprintClass = IsBlueprintClassReference(classIndex, classPath);
            }
            else
            {
                return;
            }

            var blueprint = isBlueprintClass ? reader.GetBlueprint(classPath) : null;
            if (isBlueprintClass && blueprint is null)
            {
                _missingClasses.Add(classPath);
            }

            Package? templatePackage = null;
            var templateExport = -1;
            if (cac.ItemClassPath is null && reader.TryGetProperty(templates, "ChildActorTemplate", out FPackageIndex templateIndex, ref ignored)
                && templateIndex is { IsExport: true, Owner: Package tp })
            {
                templatePackage = tp;
                templateExport = templateIndex.Index - 1;
            }

            if (blueprint is null && templatePackage is null)
            {
                return;
            }

            if (blueprint is not null)
            {
                NoteMissingParent(blueprint);
            }

            var prefix = cac.Name + "/";
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var nested = new List<PendingChildActor>();

            // Default subobjects: those of the child actor template (*_CAT) win over the class default object's.
            var subobjects = new List<(Package Package, int Export, string Name)>();
            if (templatePackage is not null)
            {
                subobjects.AddRange(Subobjects(templatePackage, templateExport));
            }

            if (blueprint is { DefaultObjectExport: >= 0 })
            {
                subobjects.AddRange(Subobjects(blueprint.Package, blueprint.DefaultObjectExport)
                    .Where(s => !subobjects.Exists(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase))));
            }

            var defaults = new List<(int Id, string Name, Package Package, int Export, UObject Object)>();
            foreach (var (package, export, name) in subobjects)
            {
                if (reader.TryGetExport(package, export) is not { } obj)
                {
                    continue;
                }

                var (subClassPath, _, isBlueprint) = reader.ClassOfExport(package, export);
                if (obj is UActorComponent || reader.ClassChain(subClassPath, isBlueprint).Any(IsComponentClassName))
                {
                    var id = _nextId--;
                    names[name] = id;
                    defaults.Add((id, name, package, export, obj));
                }
            }

            // Root: RootComponent of the child actor template (falling back to the class default object).
            string? rootName = null;
            var archetype = templatePackage is not null
                ? reader.TryGetExport(templatePackage, templateExport)
                : reader.TryGetExport(blueprint!.Package, blueprint.DefaultObjectExport);
            if (archetype is not null && reader.TryGetProperty(new TemplateChain(reader, archetype), "RootComponent", out FPackageIndex rootIndex, ref ignored))
            {
                rootName = LocalName(rootIndex);
            }

            int? childRoot = rootName is not null && names.TryGetValue(rootName, out var namedRoot) ? namedRoot : null;
            var unattached = new List<int>();
            foreach (var d in defaults)
            {
                var data = reader.SynthesizeComponent(d.Id, prefix + d.Name, actorIndex, d.Package, d.Export, d.Object, warnings);
                int? parent = null;
                if (data.IsSceneComponent
                    && reader.TryGetProperty(new TemplateChain(reader, d.Object), "AttachParent", out FPackageIndex attach, ref ignored)
                    && LocalName(attach) is { } attachName && names.TryGetValue(attachName, out var p) && p != d.Id)
                {
                    parent = p;
                }

                if (data.IsSceneComponent && parent is null && d.Id != childRoot)
                {
                    unattached.Add(d.Id);
                }

                Add(data with { AttachParent = parent });
                if (data.ChildActorClassPath is not null)
                {
                    nested.Add(new PendingChildActor(d.Id, prefix + d.Name, d.Object));
                }
            }

            // No RootComponent stored anywhere: the first unattached native scene component is the root.
            if (childRoot is null && rootName is null && unattached.Count > 0)
            {
                childRoot = unattached[0];
                unattached.RemoveAt(0);
            }

            if (blueprint is not null)
            {
                RunConstructionScript(blueprint, actorIndex, prefix, names, ref childRoot, nested);
            }

            // Native components are attached to the root by their constructors (SetupAttachment), which a cooked
            // subobject does not always store.
            foreach (var id in unattached.Where(id => id != childRoot))
            {
                Replace(id, c => c with { AttachParent = childRoot });
            }

            if (childRoot is { } r)
            {
                // SpawnActor at the component transform, then AttachToComponent(SnapToTargetNotIncludingScale).
                Replace(r, c => c with
                {
                    RelativeLocation = null,
                    RelativeRotation = null,
                    AbsoluteLocation = false,
                    AbsoluteRotation = false,
                    IsSceneComponent = true,
                    AttachParent = cac.Id,
                });
            }
            else if (blueprint is not null || defaults.Count > 0)
            {
                // (Without the class and without template subobjects nothing is known; the missing class is reported.)
                warnings.Add($"{owner}: child actor '{cac.Name}' ({ExportTable.ShortName(classPath)}) has no root scene component; its components are not placed.");
            }

            foreach (var child in nested)
            {
                ExpandChildActor(actorIndex, child, depth + 1);
            }
        }

        private void Add(LevelExportData data)
        {
            _positions[data.Index] = _synthesized.Count;
            _synthesized.Add(data);
        }

        private void Replace(int id, Func<LevelExportData, LevelExportData> change)
        {
            if (_positions.TryGetValue(id, out var position))
            {
                _synthesized[position] = change(_synthesized[position]);
            }
        }
    }
}
