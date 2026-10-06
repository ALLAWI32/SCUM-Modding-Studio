using System.Text.Json;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Reading;
using ScumStudio.Level.Serialization;

namespace ScumStudio.Level.Model;

/// <summary>
/// Read-side model of one sublevel: its actors, their components and world transforms (composed along
/// <c>AttachParent</c> chains), static meshes and ISM/HISM instances. Built from <see cref="LevelData"/> produced by an
/// <see cref="ILevelReader"/>; it never modifies packages (edits are journaled separately and applied at export).
/// </summary>
public sealed class LevelDocument
{
    private static readonly string[] PreferredRootNames = ["DefaultSceneRoot", "RootComponent", "Root", "SceneRoot", "Scene"];

    private readonly Dictionary<string, ActorRecord> _byName;
    private readonly Dictionary<int, ActorRecord> _byIndex;

    private LevelDocument(LevelData data, IReadOnlyList<ActorRecord> actors, IReadOnlyList<string> warnings)
    {
        Data = data;
        Actors = actors;
        Warnings = warnings;
        _byName = new Dictionary<string, ActorRecord>(StringComparer.OrdinalIgnoreCase);
        _byIndex = new Dictionary<int, ActorRecord>();
        foreach (var a in actors)
        {
            _byName.TryAdd(a.Name, a);
            _byIndex.TryAdd(a.ExportIndex, a);
        }
    }

    /// <summary>The raw data the document was built from.</summary>
    public LevelData Data { get; }

    /// <summary>UE package path of the level, e.g. <c>/Game/ConZ_Files/Maps/The_Island/A_0_Outpost</c>.</summary>
    public string PackagePath => Data.PackagePath;

    /// <summary>Level name, e.g. <c>A_0_Outpost</c>.</summary>
    public string Name => Data.Name;

    /// <summary>Actors in <c>ULevel::Actors</c> order.</summary>
    public IReadOnlyList<ActorRecord> Actors { get; }

    /// <summary>Reader warnings plus graph problems (dangling references, attachment cycles, inferred roots).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Loads and builds a level through <paramref name="reader"/>.</summary>
    public static LevelDocument Load(ILevelReader reader, string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return FromData(reader.ReadLevel(packagePath, cancellationToken));
    }

    /// <summary>Loads and builds a level on the thread pool (CUE4Parse reading is synchronous CPU/IO work).</summary>
    public static Task<LevelDocument> LoadAsync(ILevelReader reader, string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Task.Run(() => Load(reader, packagePath, cancellationToken), cancellationToken);
    }

    /// <summary>Builds the actor graph from raw level data.</summary>
    public static LevelDocument FromData(LevelData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new Builder(data).Build();
    }

    /// <summary>Finds an actor by name (case-insensitive), or null.</summary>
    public ActorRecord? FindActor(string name) => _byName.TryGetValue(name, out var a) ? a : null;

    /// <summary>Finds an actor by export index, or null.</summary>
    public ActorRecord? FindActor(int exportIndex) => _byIndex.TryGetValue(exportIndex, out var a) ? a : null;

    /// <summary>Number of actors per kind (kinds without actors are omitted).</summary>
    public IReadOnlyDictionary<ActorKind, int> CountByKind() =>
        Actors.GroupBy(a => a.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Total number of ISM/HISM instances in the level.</summary>
    public int InstanceCount => Actors.Sum(a => a.InstanceTransforms.Count);

    /// <summary>
    /// Serialises the document as JSON: level info, per-kind counts, warnings and every actor with class, kind, mesh,
    /// world transform (location / rotator / scale) and components; instance transforms only with
    /// <paramref name="includeInstances"/> (foliage actors can hold tens of thousands).
    /// </summary>
    /// <remarks>
    /// Components created from Blueprint templates rather than stored in the level carry <c>"synthesized": true</c> and
    /// <c>"template"</c> (their actor <c>"synthesizedCount"</c>); <c>"fromTemplate": true</c> marks values looked up on a
    /// template; <c>"visible": false</c> marks hidden components; child actor components show <c>"childActorClass"</c> and,
    /// when the level stores the child actor, <c>"childActor"</c> (its name), which in turn shows
    /// <c>"parentComponent"</c> (<c>Actor.Component</c>).
    /// </remarks>
    public string ToJson(bool includeInstances = false, bool indented = true)
    {
        var componentNames = new Dictionary<int, string>();
        foreach (var a in Actors)
        {
            foreach (var c in a.Components)
            {
                componentNames.TryAdd(c.ExportIndex, a.Name + "." + c.Name);
            }
        }

        var actorNames = Actors.GroupBy(a => a.ExportIndex).ToDictionary(g => g.Key, g => g.First().Name);

        var dto = new LevelDumpDto(
            PackagePath,
            Name,
            Actors.Count,
            InstanceCount,
            CountByKind().ToDictionary(k => k.Key.ToString(), k => k.Value),
            Warnings,
            Actors.Select(a => ToDto(a, includeInstances, componentNames, actorNames)).ToList());
        return JsonSerializer.Serialize(dto, indented ? LevelJson.Indented : LevelJson.Compact);
    }

    /// <summary>Classifies an actor class (see <see cref="ActorKind"/>).</summary>
    public static ActorKind ClassifyActor(string className, bool isBlueprintClass)
    {
        ArgumentNullException.ThrowIfNull(className);
        if (isBlueprintClass)
        {
            return ActorKind.Blueprint;
        }

        if (className.Equals("StaticMeshActor", StringComparison.Ordinal))
        {
            return ActorKind.StaticMeshActor;
        }

        if (className.EndsWith("Light", StringComparison.Ordinal))
        {
            return ActorKind.Light;
        }

        return className.EndsWith("Volume", StringComparison.Ordinal) ? ActorKind.Volume : ActorKind.Other;
    }

    private static ActorDto ToDto(
        ActorRecord a, bool includeInstances, IReadOnlyDictionary<int, string> componentNames, IReadOnlyDictionary<int, string> actorNames)
    {
        // Parent: the component name inside the same actor, "Actor.Component" for an attachment to another actor.
        string? ParentName(int? parent) => parent is not { } p
            ? null
            : a.Components.FirstOrDefault(x => x.ExportIndex == p)?.Name
              ?? (componentNames.TryGetValue(p, out var foreign) ? foreign : "#" + p.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var components = a.Components.Select(c => new ComponentDto(
            c.ExportIndex,
            c.Name,
            c.ClassName,
            c.IsSceneComponent,
            ParentName(c.AttachParent),
            c.IsSceneComponent ? c.Relative : null,
            c.IsSceneComponent ? WorldValue(c) : null,
            c.StaticMeshPath,
            c.IsInstanced ? c.Instances.Count : null,
            c.UsesTemplateValues ? true : null,
            c.IsSynthesized ? true : null,
            c.IsSynthesized ? c.TemplatePath : null,
            c.IsVisible ? null : false,
            c.ChildActorClassPath,
            c.ChildActor is { } child && actorNames.TryGetValue(child, out var childName) ? childName : null)).ToList();
        var instances = includeInstances && a.InstanceTransforms.Count > 0
            ? a.InstanceTransforms.Select(i => new InstanceDto(i.ComponentName, i.InstanceIndex, TransformValue.FromTransform(i.WorldTransform))).ToList()
            : null;
        return new ActorDto(
            a.ExportIndex,
            a.Name,
            a.ClassPath,
            a.Kind,
            a.StaticMeshPath,
            a.Root is { } root ? WorldValue(root) : TransformValue.FromTransform(a.WorldTransform),
            a.Root?.Name,
            a.RootInferred ? true : null,
            a.InstanceTransforms.Count,
            a.SynthesizedComponentCount > 0 ? a.SynthesizedComponentCount : null,
            a.ParentComponent is { } pc && componentNames.TryGetValue(pc, out var parentComponent) ? parentComponent : null,
            components,
            instances);
    }

    /// <summary>World transform for display: the stored value for unattached components (exact rotator), else derived.</summary>
    private static TransformValue WorldValue(ComponentRecord c) =>
        c.AttachParent is null ? c.Relative : TransformValue.FromTransform(c.WorldTransform);

    private sealed record LevelDumpDto(
        string Package,
        string Name,
        int ActorCount,
        int InstanceCount,
        IReadOnlyDictionary<string, int> Kinds,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<ActorDto> Actors);

    private sealed record ActorDto(
        int Index,
        string Name,
        string Class,
        ActorKind Kind,
        string? Mesh,
        TransformValue Transform,
        string? Root,
        bool? RootInferred,
        int InstanceCount,
        int? SynthesizedCount,
        string? ParentComponent,
        IReadOnlyList<ComponentDto> Components,
        IReadOnlyList<InstanceDto>? Instances);

    private sealed record ComponentDto(
        int Index,
        string Name,
        string Class,
        bool Scene,
        string? Parent,
        TransformValue? Relative,
        TransformValue? World,
        string? Mesh,
        int? InstanceCount,
        bool? FromTemplate,
        bool? Synthesized,
        string? Template,
        bool? Visible,
        string? ChildActorClass,
        string? ChildActor);

    private sealed record InstanceDto(string Component, int Index, TransformValue World);

    /// <summary>Builds actors, components and world transforms from <see cref="LevelData"/>.</summary>
    private sealed class Builder
    {
        private readonly LevelData _data;
        private readonly Dictionary<int, LevelExportData> _exports = new();
        private readonly Dictionary<int, FTransform> _world = new();
        private readonly HashSet<int> _visiting = new();
        private readonly List<string> _warnings;

        public Builder(LevelData data)
        {
            _data = data;
            _warnings = [.. data.Warnings];
            foreach (var e in data.Exports.Concat(data.SynthesizedComponents))
            {
                if (!_exports.TryAdd(e.Index, e))
                {
                    _warnings.Add($"Duplicate export index {e.Index} ({e.Name}); the first one is used.");
                }
            }
        }

        public LevelDocument Build()
        {
            var actorIndices = new List<int>();
            var actorSet = new HashSet<int>();
            foreach (var index in _data.ActorIndices)
            {
                if (!_exports.ContainsKey(index))
                {
                    _warnings.Add($"ULevel.Actors references missing export {index}.");
                }
                else if (actorSet.Add(index))
                {
                    actorIndices.Add(index);
                }
            }

            var componentsByActor = actorIndices.ToDictionary(i => i, _ => new List<LevelExportData>());
            foreach (var e in _data.Exports.Concat(_data.SynthesizedComponents))
            {
                if ((e.IsComponent || e.IsSceneComponent) && componentsByActor.TryGetValue(e.OuterIndex, out var list))
                {
                    list.Add(e);
                }
            }

            var actors = new List<ActorRecord>(actorIndices.Count);
            foreach (var index in actorIndices)
            {
                actors.Add(BuildActor(_exports[index], componentsByActor[index]));
            }

            return new LevelDocument(_data, actors, _warnings);
        }

        private ActorRecord BuildActor(LevelExportData actor, List<LevelExportData> componentExports)
        {
            var components = componentExports.Select(BuildComponent).ToList();
            var (root, inferred) = FindRoot(actor, componentExports);
            if (inferred && root is not null)
            {
                _warnings.Add($"{actor.Name}: RootComponent not stored; using '{_exports[root.Value].Name}'.");
            }

            var world = root is { } r && _exports.TryGetValue(r, out var rootExport) && rootExport.IsSceneComponent
                ? WorldOf(r)
                : FTransform.Identity;
            var kind = ClassifyActor(actor.ClassName, actor.IsBlueprintClass);
            var rootRecord = root is { } rr ? components.FirstOrDefault(c => c.ExportIndex == rr) : null;
            var mesh = rootRecord?.StaticMeshPath;
            if (mesh is null && kind == ActorKind.StaticMeshActor)
            {
                mesh = components.FirstOrDefault(c => c.StaticMeshPath is not null && !c.IsInstanced)?.StaticMeshPath;
            }
            else if (mesh is null && kind == ActorKind.Blueprint)
            {
                // A representative mesh for display: prefer the Blueprint's own meshes (its folder; props are usually
                // borrowed from elsewhere), then what hangs directly off the root.
                var visible = components.Where(c => c.StaticMeshPath is not null && c.IsVisible).ToList();
                var slash = actor.ClassPath.LastIndexOf('/');
                var folder = slash > 0 ? actor.ClassPath[..(slash + 1)] : "\0";
                bool Own(ComponentRecord c) => !c.IsInstanced && c.StaticMeshPath!.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
                bool OnRoot(ComponentRecord c) => root is not null && c.AttachParent == root;
                mesh = (visible.FirstOrDefault(c => Own(c) && OnRoot(c))
                        ?? visible.FirstOrDefault(Own)
                        ?? visible.FirstOrDefault(c => !c.IsInstanced && OnRoot(c))
                        ?? visible.FirstOrDefault(c => !c.IsInstanced)
                        ?? visible.FirstOrDefault())?.StaticMeshPath;
            }

            var instances = new List<ActorInstance>();
            foreach (var c in components.Where(c => c.IsInstanced))
            {
                for (var i = 0; i < c.Instances.Count; i++)
                {
                    instances.Add(new ActorInstance(c.ExportIndex, c.Name, i, c.StaticMeshPath, c.Instances[i], c.Instances[i] * c.WorldTransform)
                    {
                        EndCullDistance = c.InstanceEndCullDistance,
                    });
                }
            }

            return new ActorRecord(actor.Index, actor.Name, actor.ClassPath, root, components, world, kind, mesh, instances)
            {
                ClassName = actor.ClassName,
                RootInferred = inferred && root is not null,
                PropertyNames = actor.PropertyNames,
                ParentComponent = actor.ParentComponent is { } pc && _exports.ContainsKey(pc) ? pc : null,
                PatrolPoints = actor.PatrolPoints ?? [],
                TraderMarkers = actor.TraderMarkers ?? [],
            };
        }

        private ComponentRecord BuildComponent(LevelExportData c)
        {
            var world = c.IsSceneComponent ? WorldOf(c.Index) : FTransform.Identity;
            int? parent = c.AttachParent is { } p && _exports.ContainsKey(p) ? p : null;
            if (c.AttachParent is { } missing && parent is null)
            {
                _warnings.Add($"{c.Name}: AttachParent references missing export {missing}.");
            }

            return new ComponentRecord(
                c.Index,
                c.Name,
                c.ClassName,
                c.IsSceneComponent,
                parent,
                c.RelativeTransform,
                world,
                c.StaticMesh,
                c.Instances ?? [])
            {
                ClassPath = c.ClassPath,
                Relative = c.RelativeValue,
                IsInstanced = c.Instances is not null,
                InstanceEndCullDistance = c.InstanceEndCullDistance ?? 0,
                SplineMesh = c.SplineMesh,
                OverrideMaterials = c.OverrideMaterials,
                SpawnMarkers = c.SpawnMarkers ?? [],
                CollisionProfile = c.CollisionProfile,
                PropertyNames = c.PropertyNames,
                UsesTemplateValues = c.UsesTemplateValues,
                IsSynthesized = c.IsSynthesized,
                TemplatePath = c.IsSynthesized ? c.TemplatePath : null,
                IsNativeSubobject = !c.IsSynthesized && IsDefaultSubobjectPath(c.TemplatePath),
                IsStaticMeshComponent = c.IsStaticMeshComponent || c.StaticMesh is not null,
                IsVisible = c.IsVisible,
                ChildActorClassPath = c.ChildActorClassPath,
                ChildActor = c.ChildActor is { } child && _exports.ContainsKey(child) ? child : null,
            };
        }

        /// <summary>True when <paramref name="template"/> is a subobject of a class default object (<c>….Default__X_C:Name</c>).</summary>
        private static bool IsDefaultSubobjectPath(string? template)
        {
            var cut = template?.LastIndexOfAny([':', '.']) ?? -1;
            if (template is null || cut <= 0)
            {
                return false;
            }

            var outer = template[(template.LastIndexOfAny([':', '.', '/'], cut - 1) + 1)..cut];
            return outer.StartsWith("Default__", StringComparison.Ordinal);
        }

        private (int? Root, bool Inferred) FindRoot(LevelExportData actor, List<LevelExportData> components)
        {
            if (actor.RootComponent is { } stored)
            {
                if (_exports.ContainsKey(stored))
                {
                    return (stored, false);
                }

                _warnings.Add($"{actor.Name}: RootComponent references missing export {stored}.");
            }

            var own = components.Select(c => c.Index).ToHashSet();
            var candidates = components
                .Where(c => c.IsSceneComponent && (c.AttachParent is not { } p || !own.Contains(p)))
                .ToList();
            if (candidates.Count == 0)
            {
                return (null, false);
            }

            foreach (var preferred in PreferredRootNames)
            {
                var match = candidates.FirstOrDefault(c => c.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return (match.Index, true);
                }
            }

            return (candidates[0].Index, true);
        }

        /// <summary>
        /// World transform of a scene component: <c>Relative * ParentWorld</c>, honouring <c>bAbsoluteLocation</c>,
        /// <c>bAbsoluteRotation</c> and <c>bAbsoluteScale</c> (UE <c>USceneComponent::CalcNewComponentToWorld</c>).
        /// </summary>
        private FTransform WorldOf(int index)
        {
            if (_world.TryGetValue(index, out var cached))
            {
                return cached;
            }

            var e = _exports[index];
            var relative = e.RelativeTransform;
            FTransform world;
            if (!_visiting.Add(index))
            {
                _warnings.Add($"{e.Name}: attachment cycle detected; treating it as unattached.");
                return relative;
            }

            try
            {
                if (e.AttachParent is { } p && p != index && _exports.TryGetValue(p, out var parentExport) && parentExport.IsSceneComponent)
                {
                    var parentWorld = e.AttachSocket is { } socket ? socket * WorldOf(p) : WorldOf(p);
                    world = relative * parentWorld;
                    if (e.AbsoluteLocation || e.AbsoluteRotation || e.AbsoluteScale)
                    {
                        world = new FTransform(
                            e.AbsoluteRotation ? relative.Rotation : world.Rotation,
                            e.AbsoluteLocation ? relative.Translation : world.Translation,
                            e.AbsoluteScale ? relative.Scale3D : world.Scale3D);
                    }
                }
                else
                {
                    world = relative;
                }
            }
            finally
            {
                _visiting.Remove(index);
            }

            _world[index] = world;
            return world;
        }
    }
}
