using System.Numerics;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Meshes;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using UePackage = CUE4Parse.UE4.Assets.Package;

namespace ScumStudio.Viewport;

/// <summary>One drawable piece of a <see cref="PreviewModel"/>: a mesh (or one material section of it) at a UE-space transform.</summary>
/// <param name="Name">Display name (mesh, or mesh/material).</param>
/// <param name="Mesh">Geometry in UE space (centimetres).</param>
/// <param name="TexturePath">Key into <see cref="PreviewModel.Textures"/>, or null when untextured.</param>
/// <param name="Transform">Where the part sits (UE space).</param>
/// <param name="Tint">Linear RGBA multiplied into the surface (null = white).</param>
/// <param name="AlphaCutoff">Opacity clip of a masked material (leaves, grass, fences): texels whose alpha is below it are cut out; 0 = opaque.</param>
public sealed record PreviewPart(string Name, MeshData Mesh, string? TexturePath, FTransform Transform, Vector4? Tint = null, float AlphaCutoff = 0f)
{
    /// <summary>The material object path the part is drawn with (its mesh's material slot), or empty.</summary>
    public string Material { get; init; } = string.Empty;

    /// <summary>Shiny paint: metal (x) and gloss (y), 0..1, where the texture's alpha is set; zero = plain shading.</summary>
    public Vector2 Surface { get; init; }

    /// <summary>Key into <see cref="PreviewModel.Textures"/> of the material's normal map, or null.</summary>
    public string? NormalPath { get; init; }

    /// <summary>Roughness range of an opaque master-shader material (see <c>GpuSection.Roughness</c>); zero = plain diffuse.</summary>
    public Vector2 Roughness { get; init; }

    /// <summary>The vehicle attachment package this part draws (<c>/Game/…/BPC_X_Door_FrontLeft</c>), or empty for the vehicle's own mesh.</summary>
    public string Attachment { get; init; } = string.Empty;

    /// <summary>
    /// The UV channel the material reads its colour mask through (SCUM's <c>M_Car_01</c> 'Color Mask UV Channel': 1 on the
    /// Laika's and the WolfsWagen's bodies, 0 on the Rager's); its colour texture is always read through UV0. The mesh
    /// carries that channel (<see cref="MeshData.Uv(int)"/>).
    /// </summary>
    public int MaskUv { get; init; }
}

/// <summary>A CPU-side model for <c>MeshPreview</c>: parts plus the decoded base-colour textures they share.</summary>
/// <param name="Name">What is shown (asset or Blueprint name).</param>
/// <param name="Parts">Parts to draw.</param>
/// <param name="Textures">Decoded textures by object path.</param>
public sealed record PreviewModel(string Name, IReadOnlyList<PreviewPart> Parts, IReadOnlyDictionary<string, TextureImage> Textures)
{
    /// <summary>Total triangles.</summary>
    public long Triangles => Parts.Sum(p => (long)p.Mesh.TriangleCount);

    /// <summary>Add-on kits the vehicle's empty slots take (<c>ArmorLight</c>, <c>ArmorHeavy</c>), for the armour choice.</summary>
    public IReadOnlyList<string> AddOns { get; init; } = [];
}

/// <summary>
/// Builds <see cref="PreviewModel"/>s on a worker thread (no GL): a single static/skeletal mesh split by material with
/// each section's base-colour texture, or a cooked Blueprint (weapon, vehicle, magazine …) whose mesh is found on its
/// component templates / CDO up the parent-class chain, plus, for vehicles, the default attachments placed on their
/// sockets (SCUM's <c>_chassisSlot</c> / <c>_slots</c> → <c>ParentSocket</c> + <c>MeshSetup.Mesh</c>).
/// </summary>
public sealed class MeshPreviewLoader
{
    private const int MaxClassDepth = 8;

    /// <summary>Child actor components inside child actors followed at most (a cycle stops at its first repeat).</summary>
    private const int MaxChildActorDepth = 4;

    /// <summary>Mesh components one Blueprint places at most, child actors included (instances apart: they never take a later component's place).</summary>
    private const int MaxComponents = MaxAttachments * 8;

    /// <summary>Instance parts one Blueprint draws at most, child actors included (an instance of a two-material mesh is two).</summary>
    private const int MaxInstanceParts = 4096;

    /// <summary>Vertices one Blueprint's instance parts add at most (the Map's stand-in copies every instance into one mesh).</summary>
    private const long MaxInstanceVertices = 1 << 20;

    // CUE4Parse 1.2.2 decomposes each instance's FMatrix approximately; the level reader decomposes the stored matrix
    // exactly (Cue4ParseLevelReader.ConvertInstance), and so does the preview.
    private static readonly FieldInfo? InstanceMatrixField =
        typeof(FInstancedStaticMeshInstanceData).GetField("Transform", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    /// <summary>
    /// Chassis (0), the parts in its slots (1) and theirs: a Rager's doors hang on its body sides (2). Which parts a stock
    /// vehicle has comes from its world spawn preset (see <see cref="StockParts"/>), not from the depth.
    /// </summary>
    private const int MaxAttachmentDepth = 4;

    private const string WorldSpawnPresets = "/Game/ConZ_Files/Vehicles/SpawningPresets/AutomaticSpawn/";
    private const int MaxAttachments = 128;

    private readonly AssetCatalog _catalog;
    private readonly ILogger _logger;
    private readonly Dictionary<string, TextureImage> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialInfo?> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MeshData> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkinWeights> _weights = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a loader over <paramref name="catalog"/>.</summary>
    public MeshPreviewLoader(AssetCatalog catalog, ILogger? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Largest texture edge in pixels (0 = no textures).</summary>
    public int TextureSize { get; init; } = 1024;

    /// <summary>
    /// Keep colour textures and normal maps as their cooked blocks for the GPU (<see cref="TextureDecoder.DecodeForGpu"/>):
    /// full-size textures at a quarter of the memory, but no RGBA pixels (repainting needs those: leave it off there).
    /// </summary>
    public bool ForGpu { get; init; }

    /// <summary>The LOD taken for each part, from the mesh's LOD list (null = the finest with render data).</summary>
    public Func<IReadOnlyList<MeshLodInfo>, int>? LodOf { get; init; }

    /// <summary>True for foliage types (<c>FoliageType_InstancedStaticMesh</c>: most of the island's trees, bushes and grass).</summary>
    public static bool IsFoliageTypeClass(string? className) =>
        className is not null && className.StartsWith("FoliageType", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads what an asset of class <paramref name="className"/> looks like in game: a foliage type's tree, bush or grass,
    /// a Blueprint's model (null when it shows no mesh), else the mesh at <paramref name="objectPath"/>. The one entry the
    /// Assets page's 3D view and its tile pictures share.
    /// </summary>
    /// <exception cref="InvalidDataException">The object is not a mesh, or a foliage type's mesh cannot be loaded.</exception>
    public PreviewModel? Load(string objectPath, string packagePath, string? className) =>
        IsFoliageTypeClass(className) ? LoadFoliageType(objectPath)
        : className is not null && className.Contains("Blueprint", StringComparison.OrdinalIgnoreCase) ? LoadBlueprint(packagePath)
        : LoadMesh(objectPath);

    /// <summary>
    /// Loads the mesh a foliage type paints with the materials it puts on it (at scale 1: the game varies each instance's
    /// scale within the type's range), or an actor foliage type's Blueprint.
    /// </summary>
    /// <exception cref="InvalidDataException">The type names no mesh, or its mesh cannot be loaded.</exception>
    public PreviewModel LoadFoliageType(string objectPath)
    {
        var type = ResolveFoliageType(objectPath);
        var leaf = objectPath[(objectPath.LastIndexOfAny(['/', '.']) + 1)..];
        if (type.Mesh is null && type.ActorClass is { } actor && LoadBlueprint(PackageOf(actor)) is { } model)
        {
            return model with { Name = leaf };
        }

        var meshPath = type.Mesh ?? throw new InvalidDataException(type.ActorClass is { } actorClass ? $"{objectPath}: its actor {actorClass} shows no mesh." : $"{objectPath} names no mesh.");
        var mesh = TryLoadMeshObject(meshPath) ?? throw new InvalidDataException($"{objectPath}: its mesh {meshPath} could not be loaded.");
        var parts = new List<PreviewPart>();
        AddMeshParts(parts, mesh, meshPath, FTransform.Identity, overrides: type.OverrideMaterials);
        return new PreviewModel(leaf, parts, _textures);
    }

    /// <summary>
    /// Loads one static or skeletal mesh (LOD 0) with a part per material; <paramref name="overrideMaterials"/> replace the
    /// materials of their slots (a null or empty entry keeps the mesh's own).
    /// </summary>
    /// <exception cref="InvalidDataException">The object is not a mesh.</exception>
    /// <param name="objectPath">The mesh.</param>
    /// <param name="overrideMaterials">Materials for its slots (null or empty entries keep the mesh's own).</param>
    /// <param name="pose">A skeletal mesh's bones in another pose than its bind pose (<see cref="Skinning"/>; the scale figure's arms down), or null.</param>
    public PreviewModel LoadMesh(string objectPath, IReadOnlyList<string?>? overrideMaterials = null, IReadOnlyDictionary<string, FTransform>? pose = null)
    {
        var obj = _catalog.LoadObject(objectPath);
        if (!MeshExtractor.IsMesh(obj))
        {
            throw new InvalidDataException($"{objectPath} is a {obj.ExportType}, not a mesh.");
        }

        var parts = new List<PreviewPart>();
        AddMeshParts(parts, obj, objectPath, FTransform.Identity, pose, overrideMaterials);
        return new PreviewModel(obj.Name, parts, _textures);
    }

    /// <summary>The mesh a foliage type paints, the materials it puts on the mesh's slots and, for an actor foliage type, its actor class.</summary>
    internal sealed record FoliageTypeMesh(string? Mesh, IReadOnlyList<string?> OverrideMaterials, string? ActorClass);

    /// <summary>
    /// Reads the foliage type at <paramref name="objectPath"/>: its <c>Mesh</c>, <c>OverrideMaterials</c> (by slot) and
    /// <c>ActorClass</c> (<c>FoliageType_Actor</c>), as object paths.
    /// </summary>
    internal FoliageTypeMesh ResolveFoliageType(string objectPath)
    {
        var type = _catalog.LoadObject(objectPath);
        var mesh = type.GetOrDefault<FPackageIndex>("Mesh") is { IsNull: false } m && PathOf(m) is { Length: > 0 } path ? path : null;
        var overrides = (type.GetOrDefault<FPackageIndex[]>("OverrideMaterials") ?? []).Select(o => o is { IsNull: false } ? PathOf(o) : null).ToList();
        var actor = type.GetOrDefault<FPackageIndex>("ActorClass") is { IsNull: false } a && PathOf(a) is { Length: > 0 } cls ? cls : null;
        return new FoliageTypeMesh(mesh, overrides, actor);
    }

    /// <summary>
    /// Loads the mesh a cooked Blueprint shows in game (plus a vehicle's default attachments), or null when no mesh is
    /// referenced by it or its parent classes.
    /// </summary>
    /// <param name="packagePath">Blueprint package.</param>
    /// <param name="addOn">A vehicle's add-on kit to fit in its empty slots (<c>ArmorLight</c>, <c>ArmorHeavy</c>), or null for stock.</param>
    public PreviewModel? LoadBlueprint(string packagePath, string? addOn = null)
    {
        // Buildings, rooms and props are many meshes in the construction script, characters a body with a head, hair and
        // gear on it (posed from their idle); vehicles are one skeletal body plus slots.
        if (TryGetDefaultObject(packagePath)?.GetOrDefault<FStructFallback>("_chassisSlot") is null)
        {
            var assembled = new List<PreviewPart>();
            AddComponents(assembled, packagePath);
            if (assembled.Count > 0)
            {
                return new PreviewModel(PackageLeaf(packagePath), assembled, _textures);
            }
        }

        var meshPath = FindMeshPath(packagePath);
        if (meshPath is null)
        {
            return null;
        }

        var parts = new List<PreviewPart>();
        var root = TryLoadMeshObject(meshPath);
        if (root is null)
        {
            return null;
        }

        AddMeshParts(parts, root, meshPath, FTransform.Identity);
        var count = 0;
        var addOns = new SortedSet<string>(StringComparer.Ordinal);
        if (TryGetDefaultObject(packagePath) is { } cdo && cdo.GetOrDefault<FStructFallback>("_chassisSlot") is { } chassisSlot)
        {
            AddAttachments(parts, [chassisSlot], root, FTransform.Identity, root, 0, ref count, StockParts(packagePath), addOn, addOns);
        }

        return new PreviewModel(PackageLeaf(packagePath), parts, _textures) { AddOns = [.. addOns] };
    }

    /// <summary>
    /// Every visible static and skeletal mesh component of the Blueprint: the native components its class defaults carry
    /// (a character's body <c>CharacterMesh0</c> on its capsule, <c>HeadNative</c> on the body; a cooked child class
    /// stores only the values it changes, so a component's properties are looked up along the parent chain, child first),
    /// the construction-script nodes (parent classes first; a node attached to a socket of its parent sits on it) and the
    /// extra meshes SCUM's NPC classes name (<c>_additionalComplexMeshes</c>: hair, beards, placed with the head). A
    /// character is posed from frame 0 of the idle its body plays (<c>AnimationData.AnimToPlay</c>, else the Idle
    /// sequence its AnimBlueprint names): skeletal parts are skinned into that pose and sockets follow the posed bones.
    /// A child Blueprint's changes to inherited components (<c>InheritableComponentHandler</c>) win over the parent's
    /// templates, a component's <c>OverrideMaterials</c> replace its mesh's slot materials, a child actor component brings
    /// its class's components (<see cref="MaxChildActorDepth"/> levels deep at most) and an instanced component draws each instance
    /// (the Blueprint's instances share one budget, <see cref="MaxInstanceParts"/>, apart from its components').
    /// </summary>
    private void AddComponents(List<PreviewPart> parts, string packagePath, List<ComponentMesh>? resolved = null, int childDepth = 0, Walk? walk = null)
    {
        var chain = ClassChain(packagePath);
        walk ??= new Walk(packagePath);

        // The class defaults' components by name (the child class's template first) and the class defaults themselves.
        var components = new Dictionary<string, List<UObject>>(StringComparer.OrdinalIgnoreCase);
        var defaults = new List<UObject>();
        foreach (var package in chain)
        {
            var map = package.ExportMap;
            var cdoIndex = Array.FindIndex(map, e => OuterOf(e) < 0 && e.ObjectName.Text.StartsWith("Default__", StringComparison.Ordinal));
            if (cdoIndex < 0 || ExportAt(package, cdoIndex) is not { } cdo)
            {
                continue;
            }

            defaults.Add(cdo);
            for (var i = 0; i < map.Length; i++)
            {
                if (OuterOf(map[i]) != cdoIndex || ExportAt(package, i) is not { } export)
                {
                    continue;
                }

                if (!components.TryGetValue(export.Name, out var templates))
                {
                    components[export.Name] = templates = [];
                }

                templates.Add(export);
            }
        }

        // Inherited construction-script components a child class changes (its InheritableComponentHandler records), the
        // child class's template first: they come before the owner's own template, so the child's mesh and materials win.
        var inherited = new Dictionary<string, List<UObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in chain.SelectMany(p => ExportsOfClass(p, "InheritableComponentHandler")))
        {
            foreach (var record in handler.GetOrDefault<FStructFallback[]>("Records") ?? [])
            {
                if (record?.GetOrDefault<FStructFallback>("ComponentKey") is { } key && key.TryGetValue(out FName variable, "SCSVariableName") && !variable.IsNone
                    && TryLoad<UObject>(record.GetOrDefault<FPackageIndex>("ComponentTemplate")) is { } template)
                {
                    if (!inherited.TryGetValue(variable.Text, out var templates))
                    {
                        inherited[variable.Text] = templates = [];
                    }

                    templates.Add(template);
                }
            }
        }

        var meshes = new Dictionary<string, UObject?>(StringComparer.OrdinalIgnoreCase);
        var worlds = new Dictionary<string, FTransform>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, FTransform>? pose = null;
        foreach (var (name, templates) in components)
        {
            if (MeshOf(name) is USkeletalMesh body && FindIdle(templates) is { } idle && TryPose(body, idle) is { } posed)
            {
                pose = posed;
                break;
            }
        }

        foreach (var name in components.Keys.ToList())
        {
            Place(components[name], WorldOf(name, 0));
        }

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            foreach (var scs in ExportsOfClass(chain[i], "SimpleConstructionScript"))
            {
                foreach (var rootNode in scs.GetOrDefault<FPackageIndex[]>("RootNodes") ?? [])
                {
                    AddNode(rootNode, TryLoad<UObject>(rootNode)?.GetOrDefault<FName>("ParentComponentOrVariableName").Text, FTransform.Identity, 0);
                }
            }
        }

        // The hair and beards SCUM's NPCs carry as extra meshes, rigged like the head: placed with the head component.
        var head = NameOf(defaults, "_headMesh") ?? NameOf(defaults, "Mesh");
        var headWorld = head is null ? FTransform.Identity : WorldOf(head, 0);
        var extras = defaults.SelectMany(d => d.GetOrDefault<FSoftObjectPath[]>("_additionalComplexMeshes") ?? []).Select(p => p.AssetPathName.Text)
            .Where(p => p is { Length: > 0 } && p != "None").Distinct(StringComparer.OrdinalIgnoreCase).Take(8);
        foreach (var extra in extras)
        {
            if (TryLoadMeshObject(extra) is { } mesh)
            {
                AddMeshParts(parts, mesh, extra, headWorld, pose);
            }
        }

        UObject? MeshOf(string name) => components.TryGetValue(name, out var templates) && MeshPathOf(templates) is { } path ? MeshAt(path) : null;

        UObject? MeshAt(string path)
        {
            if (!meshes.TryGetValue(path, out var mesh))
            {
                meshes[path] = mesh = TryLoadMeshObject(path);
            }

            return mesh;
        }

        // A visible component's mesh where the component sits (recorded also when the mesh cannot be loaded), with the
        // materials it overrides; an instanced component's mesh once per instance, a child actor's class where it sits.
        void Place(List<UObject> templates, FTransform world)
        {
            if (!Visible(templates))
            {
                return;
            }

            if (TryGet<FPackageIndex>(templates, "ChildActorClass", out var childClass) && PackageOf(PathOf(childClass)) is { Length: > 0 } child
                && !child.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) && childDepth < MaxChildActorDepth && walk.Expanding.Add(child))
            {
                var firstPart = parts.Count;
                var firstResolved = resolved?.Count ?? 0;
                AddComponents(parts, child, resolved, childDepth + 1, walk);
                walk.Expanding.Remove(child);
                for (var i = firstPart; i < parts.Count; i++)
                {
                    parts[i] = parts[i] with { Transform = parts[i].Transform * world };
                }

                for (var i = firstResolved; i < (resolved?.Count ?? 0); i++)
                {
                    resolved![i] = resolved[i] with { Transform = resolved[i].Transform * world };
                }
            }

            if (MeshPathOf(templates) is not { } path)
            {
                return;
            }

            var overrides = TryGet<FPackageIndex[]>(templates, "OverrideMaterials", out var materials) ? materials.Select(m => m is { IsNull: false } ? PathOf(m) : null).ToList() : null;
            if (Instances(templates) is not { Length: > 0 } instances)
            {
                walk.Components++;
                resolved?.Add(new ComponentMesh(path, world, overrides));
                if (MeshAt(path) is { } mesh)
                {
                    AddMeshParts(parts, mesh, path, world, pose, overrides);
                }

                return;
            }

            // The mesh's parts once, then a copy per instance sharing their geometry (uploaded once by PreviewScene), as
            // many as the Blueprint's instance budget leaves (an instance whose mesh cannot be loaded counts as one part).
            var first = parts.Count;
            if (MeshAt(path) is { } instanced)
            {
                AddMeshParts(parts, instanced, path, FTransform.Identity, null, overrides);
            }

            var shared = parts.GetRange(first, parts.Count - first);
            parts.RemoveRange(first, shared.Count);
            var perInstance = Math.Max(1, shared.Count);
            var vertices = shared.Sum(p => (long)p.Mesh.VertexCount);
            var take = Math.Min(instances.Length, (MaxInstanceParts - walk.InstanceParts) / perInstance);
            take = (int)Math.Max(0, vertices > 0 ? Math.Min(take, (MaxInstanceVertices - walk.InstanceVertices) / vertices) : take);
            if (take < instances.Length)
            {
                _logger.LogDebug("{Blueprint}: {Mesh} draws {Taken} of its {Count} instances (instance budget)", packagePath, path, take, instances.Length);
            }

            walk.InstanceParts += take * perInstance;
            walk.InstanceVertices += take * vertices;
            foreach (var instance in instances.Take(take))
            {
                var at = ConvertInstance(instance) * world;
                resolved?.Add(new ComponentMesh(path, at, overrides));
                parts.AddRange(shared.Select(p => p with { Transform = at }));
            }
        }

        // A component's place: its relative transform under its parent (on the parent's socket when attached to one).
        FTransform WorldOf(string name, int depth)
        {
            if (worlds.TryGetValue(name, out var known))
            {
                return known;
            }

            var world = FTransform.Identity;
            if (depth < 16 && components.TryGetValue(name, out var templates))
            {
                var parentName = TryGet<FPackageIndex>(templates, "AttachParent", out var parent) ? parent.ResolvedObject?.Name.Text : null;
                var socket = TryGet<FName>(templates, "AttachSocketName", out var s) ? s.Text : null;
                var parentWorld = parentName is { Length: > 0 } ? WorldOf(parentName, depth + 1) : FTransform.Identity;
                world = Relative(templates) * (OnSocket(parentName, socket) ?? FTransform.Identity) * parentWorld;
            }

            worlds[name] = world;
            return world;
        }

        FTransform? OnSocket(string? parentName, string? socket) =>
            parentName is { Length: > 0 } && socket is { Length: > 0 } && socket != "None" && MeshOf(parentName) is { } parentMesh ? SocketTransform(parentMesh, socket, pose) : null;

        void AddNode(FPackageIndex index, string? parentName, FTransform fallback, int depth)
        {
            if (depth > 32 || walk.Components >= MaxComponents || TryLoad<UObject>(index) is not { } node)
            {
                return;
            }

            var world = parentName is { Length: > 0 } && (worlds.ContainsKey(parentName) || components.ContainsKey(parentName)) ? WorldOf(parentName, 0) : fallback;
            var variable = node.GetOrDefault<FName>("InternalVariableName").Text;
            if (TryLoad<UObject>(node.GetOrDefault<FPackageIndex>("ComponentTemplate")) is { } template)
            {
                List<UObject> templates = variable is { Length: > 0 } && inherited.TryGetValue(variable, out var changed) ? [.. changed, template] : [template];
                var socket = node.GetOrDefault<FName>("AttachToName").Text;
                world = Relative(templates) * (OnSocket(parentName, socket) ?? FTransform.Identity) * world;
                if (variable is { Length: > 0 })
                {
                    worlds[variable] = world;
                    components.TryAdd(variable, templates);
                }

                Place(variable is { Length: > 0 } ? components[variable] : templates, world);
            }

            foreach (var child in node.GetOrDefault<FPackageIndex[]>("ChildNodes") ?? [])
            {
                AddNode(child, variable, world, depth + 1);
            }
        }
    }

    /// <summary>A mesh a Blueprint component draws: its path, its place in the Blueprint and the materials it puts on the mesh's slots.</summary>
    internal sealed record ComponentMesh(string Mesh, FTransform Transform, IReadOnlyList<string?>? OverrideMaterials);

    /// <summary>The meshes <see cref="LoadBlueprint"/> places for the Blueprint's components, with those that cannot be loaded.</summary>
    internal IReadOnlyList<ComponentMesh> ResolveComponents(string packagePath)
    {
        var resolved = new List<ComponentMesh>();
        AddComponents([], packagePath, resolved);
        return resolved;
    }

    /// <summary>The mesh a component's templates name (skeletal first), or null.</summary>
    private string? MeshPathOf(List<UObject> templates)
    {
        foreach (var property in (ReadOnlySpan<string>)["SkeletalMesh", "StaticMesh"])
        {
            if (TryGet<FPackageIndex>(templates, property, out var index) && IsMesh(index) && PathOf(index) is { Length: > 0 } path)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// The Blueprint's package and its Blueprint parents' (child first), from the class export's <c>SuperIndex</c> in each
    /// export map: no class is deserialized, so a class CUE4Parse cannot read does not end the chain.
    /// </summary>
    private List<UePackage> ClassChain(string packagePath)
    {
        var chain = new List<UePackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = packagePath; current is { Length: > 0 } && chain.Count < MaxClassDepth && seen.Add(current);)
        {
            if (!_catalog.TryLoadPackage(current, out var loaded) || loaded is not UePackage package)
            {
                break;
            }

            chain.Add(package);
            var cls = Array.FindIndex(package.ExportMap, e => OuterOf(e) < 0 && e.SuperIndex is { IsNull: false });
            var parent = cls < 0 ? string.Empty : PackageOf(PathOf(package.ExportMap[cls].SuperIndex));
            current = parent.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) ? null : parent;
        }

        return chain;
    }

    private static int OuterOf(FObjectExport export) => export.OuterIndex is { IsExport: true } outer ? outer.Index - 1 : -1;

    /// <summary>The exports of <paramref name="package"/> whose class is the engine class <paramref name="className"/>.</summary>
    private IEnumerable<UObject> ExportsOfClass(UePackage package, string className)
    {
        for (var i = 0; i < package.ExportMap.Length; i++)
        {
            if (package.ExportMap[i].ClassIndex is { IsImport: true } c && package.ImportMap[-c.Index - 1].ObjectName.Text == className
                                                                       && ExportAt(package, i) is { } export)
            {
                yield return export;
            }
        }
    }

    /// <summary>Export <paramref name="index"/> of <paramref name="package"/>, or null when CUE4Parse cannot read it.</summary>
    private UObject? ExportAt(UePackage package, int index)
    {
        try
        {
            return ((CUE4Parse.UE4.Assets.IPackage)package).GetExport(index);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Export {Index} of {Package} could not be read: {Message}", index, package.Name, ex.Message);
            return null;
        }
    }

    /// <summary>The first value of <paramref name="property"/> along a component's templates (the child class's first).</summary>
    private static bool TryGet<T>(List<UObject> templates, string property, out T value)
    {
        foreach (var template in templates)
        {
            if (template.TryGetValue(out value!, property))
            {
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>The instances (component space) of an instanced static mesh component, the child class's template first; empty for others.</summary>
    private static FInstancedStaticMeshInstanceData[] Instances(List<UObject> templates)
    {
        foreach (var template in templates)
        {
            if (template is UInstancedStaticMeshComponent { PerInstanceSMData: { Length: > 0 } data })
            {
                return data;
            }
        }

        return [];
    }

    /// <summary>
    /// What one Blueprint's walk has used, its child actors' included: the mesh components placed, the instance parts and
    /// their vertices (<see cref="MaxComponents"/>, <see cref="MaxInstanceParts"/>), and the child actor classes being expanded.
    /// </summary>
    private sealed class Walk(string root)
    {
        public HashSet<string> Expanding { get; } = new(StringComparer.OrdinalIgnoreCase) { root };

        public int Components { get; set; }

        public int InstanceParts { get; set; }

        public long InstanceVertices { get; set; }
    }

    private static FTransform ConvertInstance(FInstancedStaticMeshInstanceData data)
    {
        if (InstanceMatrixField?.GetValue(data) is CUE4Parse.UE4.Objects.Core.Math.FMatrix m)
        {
            return FTransform.FromMatrix(new Matrix4x4(m.M00, m.M01, m.M02, m.M03, m.M10, m.M11, m.M12, m.M13, m.M20, m.M21, m.M22, m.M23, m.M30, m.M31, m.M32, m.M33));
        }

        var t = data.TransformData;
        return new FTransform(new FQuat(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W).GetNormalized(),
            new FVector(t.Translation.X, t.Translation.Y, t.Translation.Z), new FVector(t.Scale3D.X, t.Scale3D.Y, t.Scale3D.Z));
    }

    private static bool Visible(List<UObject> templates) =>
        !(TryGet<bool>(templates, "bVisible", out var visible) && !visible) && !(TryGet<bool>(templates, "bHiddenInGame", out var hidden) && hidden);

    private static FTransform Relative(List<UObject> templates)
    {
        var l = TryGet<CUE4Parse.UE4.Objects.Core.Math.FVector>(templates, "RelativeLocation", out var location) ? location : new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);
        var r = TryGet<CUE4Parse.UE4.Objects.Core.Math.FRotator>(templates, "RelativeRotation", out var rotation) ? rotation : new CUE4Parse.UE4.Objects.Core.Math.FRotator(0, 0, 0);
        var s = TryGet<CUE4Parse.UE4.Objects.Core.Math.FVector>(templates, "RelativeScale3D", out var scale) ? scale : new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1);
        return new FTransform(new FRotator(r.Pitch, r.Yaw, r.Roll), new FVector(l.X, l.Y, l.Z), new FVector(s.X, s.Y, s.Z));
    }

    /// <summary>The export name an object property of the class defaults points at (<c>_headMesh</c> → <c>HeadNative</c>), or null.</summary>
    private static string? NameOf(List<UObject> defaults, string property) =>
        defaults.Select(d => d.GetOrDefault<FPackageIndex>(property)).FirstOrDefault(i => i is { IsNull: false })?.ResolvedObject?.Name.Text;

    /// <summary>
    /// The idle a mesh component plays: the sequence of a single-node component (<c>AnimationData.AnimToPlay</c>: SCUM's
    /// traders), else the shortest-named <c>*Idle*</c> sequence its AnimBlueprint's class defaults reference (its
    /// sequence players: the sentry's <c>Idle_01</c>). Null for a component without one.
    /// </summary>
    private UAnimSequence? FindIdle(List<UObject> templates)
    {
        foreach (var template in templates)
        {
            if (template.GetOrDefault<FStructFallback>("AnimationData")?.GetOrDefault<FPackageIndex>("AnimToPlay") is { IsNull: false } play && TryLoad<UAnimSequence>(play) is { } single)
            {
                return single;
            }
        }

        if (!TryGet<FPackageIndex>(templates, "AnimClass", out var animClass) || PathOf(animClass) is not { Length: > 0 } abp
            || !abp.StartsWith("/Game/", StringComparison.Ordinal) || TryGetDefaultObject(PackageOf(abp)) is not { } cdo)
        {
            return null;
        }

        UAnimSequence? best = null;
        foreach (var index in ObjectReferences(cdo.Properties, 0))
        {
            if (index.ResolvedObject?.Name.Text is { } name && name.Contains("Idle", StringComparison.OrdinalIgnoreCase)
                && (best is null || name.Length < best.Name.Length) && TryLoad<UAnimSequence>(index) is { } sequence)
            {
                best = sequence;
            }
        }

        return best;
    }

    /// <summary>Every object reference in <paramref name="properties"/>, structs and arrays included.</summary>
    private static IEnumerable<FPackageIndex> ObjectReferences(IEnumerable<FPropertyTag> properties, int depth)
    {
        if (depth > 8)
        {
            yield break;
        }

        foreach (var property in properties)
        {
            foreach (var reference in ObjectReferences(property.Tag, depth))
            {
                yield return reference;
            }
        }
    }

    private static IEnumerable<FPackageIndex> ObjectReferences(FPropertyTagType? tag, int depth)
    {
        switch (tag)
        {
            case ObjectProperty { Value: { IsNull: false } value }:
                yield return value;
                break;
            case StructProperty { Value.StructType: FStructFallback fallback }:
                foreach (var reference in ObjectReferences(fallback.Properties, depth + 1))
                {
                    yield return reference;
                }

                break;
            case ArrayProperty { Value.Properties: { } items }:
                foreach (var item in items)
                {
                    foreach (var reference in ObjectReferences(item, depth + 1))
                    {
                        yield return reference;
                    }
                }

                break;
        }
    }

    /// <summary>Frame 0 of <paramref name="idle"/> on <paramref name="body"/>'s skeleton as a pose (<see cref="Skinning.Pose"/>), or null when it cannot be read.</summary>
    private IReadOnlyDictionary<string, FTransform>? TryPose(USkeletalMesh body, UAnimSequence idle)
    {
        try
        {
            return Skinning.Pose(MeshExtractor.ReferenceBones(body), AnimationPose.Locals(idle));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Idle {Idle} could not pose {Mesh}: {Message}", idle.Name, body.Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The skeletal (preferred) or static mesh referenced by the Blueprint's component templates / CDO, following
    /// the parent-class chain for cooked Blueprints that inherit their mesh.
    /// </summary>
    public string? FindMeshPath(string packagePath)
    {
        var chain = ClassChain(packagePath);
        foreach (var package in chain)
        {
            string? staticMesh = null;
            for (var i = 0; i < package.ExportMap.Length; i++)
            {
                if (package.ExportMap[i].SuperIndex is { IsNull: false } || ExportAt(package, i) is not { } export || export is UStruct)
                {
                    continue;
                }

                // A CDO's "SkeletalMesh" is often its mesh *component*; only an object that is a mesh counts.
                if (export.TryGetValue<FPackageIndex>(out var skeletal, "SkeletalMesh") && IsMesh(skeletal) && PathOf(skeletal) is { Length: > 0 } sk)
                {
                    return sk;
                }

                if (staticMesh is null && export.TryGetValue<FPackageIndex>(out var stat, "StaticMesh") && IsMesh(stat) && PathOf(stat) is { Length: > 0 } sm)
                {
                    staticMesh = sm;
                }
            }

            if (staticMesh is not null)
            {
                return staticMesh;
            }
        }

        // SCUM's attachment parts (BPC_WolfsWagen_Door_FrontLeft) name their mesh only softly, in MeshSetup.Mesh (child
        // class first). Other mesh references on an item's defaults (its magazine, a dropped variant) are not its look:
        // the Map's loot stand-ins come from here and such an item keeps its icon.
        return chain.Select(p => Array.FindIndex(p.ExportMap, e => OuterOf(e) < 0 && e.ObjectName.Text.StartsWith("Default__", StringComparison.Ordinal)) is var cdo and >= 0
                ? ExportAt(p, cdo) : null)
            .Select(d => d?.GetOrDefault<FStructFallback>("MeshSetup")?.GetOrDefault<FSoftObjectPath>("Mesh").AssetPathName.Text)
            .FirstOrDefault(p => p is { Length: > 0 } && p != "None");
    }

    /// <summary>
    /// The parts a stock <paramref name="vehiclePackage"/> spawns with in the world: the attachment classes its world spawn
    /// preset names (a Rager's doors, panels, lights, seats, wheels; no armour, no roof rack). Null when no preset names
    /// the vehicle (every slot then shows its first part).
    /// </summary>
    public HashSet<string>? StockParts(string vehiclePackage)
    {
        var vehicle = vehiclePackage + ".";
        foreach (var preset in _catalog.PackageFiles
                     .Select(f => AssetPaths.ToPackagePath(f, _catalog.ProjectName))
                     .Where(p => p.StartsWith(WorldSpawnPresets, StringComparison.OrdinalIgnoreCase) && !p.Contains("_RadiationZone", StringComparison.OrdinalIgnoreCase)))
        {
            if (!_catalog.TryLoadPackage(preset, out var package) || package is not CUE4Parse.UE4.Assets.AbstractUePackage { NameMap: { } names })
            {
                continue;
            }

            var paths = names.Select(n => n.Name ?? string.Empty).ToList();
            if (paths.Any(n => n.StartsWith(vehicle, StringComparison.OrdinalIgnoreCase)))
            {
                return paths.Where(n => n.StartsWith("/Game/", StringComparison.Ordinal) && !n.Contains('.', StringComparison.Ordinal))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        return null;
    }

    private void AddAttachments(List<PreviewPart> parts, IEnumerable<FStructFallback> slots, UObject parentMesh, FTransform parentWorld, UObject vehicleMesh, int depth, ref int count,
        HashSet<string>? stock = null, string? addOn = null, ISet<string>? addOns = null)
    {
        if (depth >= MaxAttachmentDepth)
        {
            return;
        }

        foreach (var slot in slots)
        {
            var classes = slot.GetOrDefault<FSoftObjectPath[]>("PossibleAttachmentClasses");
            if (classes is not { Length: > 0 } || count >= MaxAttachments)
            {
                continue;
            }

            // The part the stock vehicle has in this slot (none: an add-on slot such as armour or a roof rack); without
            // a preset, the first class (the plain part; armour and other variants follow). An empty slot takes the
            // chosen add-on kit (BPC_Rager_Body_ArmorLight_Left for "ArmorLight").
            var packages = classes.Select(c => PackageOf(c.AssetPathName.Text)).ToList();
            foreach (var kit in packages.Select(KitOf).OfType<string>())
            {
                addOns?.Add(kit);
            }

            var attachmentPackage = stock is null
                ? packages[0]
                : packages.FirstOrDefault(stock.Contains)
                  ?? (addOn is null ? null : packages.FirstOrDefault(p => KitOf(p) == addOn))
                  ?? string.Empty;
            if (attachmentPackage.Length == 0 || TryGetDefaultObject(attachmentPackage) is not { } cdo)
            {
                continue;
            }

            count++;
            var socket = cdo.GetOrDefault<FName>("ParentSocket").Text;
            // A socket of the parent part is in its space; one only the vehicle's own mesh has is in vehicle space already
            // (multiplying that by the parent's place doubled the offset: armour floated off the doors).
            var world = SocketTransform(parentMesh, socket) is { } local ? local * parentWorld
                : SocketTransform(vehicleMesh, socket) ?? parentWorld;
            var meshPath = cdo.GetOrDefault<FStructFallback>("MeshSetup")?.GetOrDefault<FSoftObjectPath>("Mesh").AssetPathName.Text;
            var mesh = string.IsNullOrEmpty(meshPath) || meshPath == "None" ? null : TryLoadMeshObject(meshPath);
            if (mesh is not null)
            {
                var first = parts.Count;
                AddMeshParts(parts, mesh, meshPath!, world);
                for (var i = first; i < parts.Count; i++)
                {
                    parts[i] = parts[i] with { Attachment = attachmentPackage };
                }
            }

            var children = cdo.GetOrDefault<FStructFallback[]>("_slots");
            if (children is { Length: > 0 })
            {
                AddAttachments(parts, children.Where(c => c is not null), mesh ?? parentMesh, world, vehicleMesh, depth + 1, ref count, stock, addOn, addOns);
            }
        }
    }

    /// <summary>The add-on kit an attachment class belongs to (<c>ArmorLight</c>, <c>ArmorHeavy</c>), or null.</summary>
    private static string? KitOf(string package)
    {
        var leaf = package[(package.LastIndexOf('/') + 1)..];
        return leaf.Contains("_ArmorLight", StringComparison.OrdinalIgnoreCase) ? "ArmorLight"
            : leaf.Contains("_ArmorHeavy", StringComparison.OrdinalIgnoreCase) ? "ArmorHeavy"
            : null;
    }

    /// <summary>
    /// Adds one part per material of <paramref name="mesh"/> (sections sharing a material are merged); a mesh without
    /// render data is skipped. A skeletal mesh is skinned into <paramref name="pose"/> when one is given (<see cref="Skinning"/>).
    /// </summary>
    private void AddMeshParts(List<PreviewPart> parts, UObject mesh, string meshPath, FTransform transform, IReadOnlyDictionary<string, FTransform>? pose = null,
        IReadOnlyList<string?>? overrides = null)
    {
        if (!_meshes.TryGetValue(meshPath, out var data))
        {
            try
            {
                // The first LOD that still has render data (cooked meshes may strip LOD 0 for far-only or hidden parts).
                data = MeshExtractor.Extract(mesh, LodOf is null ? 0 : LodOf(MeshExtractor.Describe(mesh).Lods));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug("Mesh {Mesh} could not be extracted: {Message}", meshPath, ex.Message);
                return;
            }

            _meshes[meshPath] = data;
        }

        // A car body whose colour mask lies in another UV channel (the Laika's, the WolfsWagen's): that channel comes along.
        var maskUv = GroupSections(data, overrides).Select(g => MaskUvOf(g.Material)).DefaultIfEmpty(0).Max();
        if (maskUv > 0 && data.Uv(maskUv) is null)
        {
            try
            {
                data = _meshes[meshPath] = MeshExtractor.Extract(mesh, LodOf is null ? 0 : LodOf(MeshExtractor.Describe(mesh).Lods), extraUvs: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug("Mesh {Mesh} keeps only its first UV channel: {Message}", meshPath, ex.Message);
            }
        }

        if (pose is not null && mesh is USkeletalMesh skeletal)
        {
            try
            {
                if (!_weights.TryGetValue(meshPath, out var weights))
                {
                    _weights[meshPath] = weights = MeshExtractor.ExtractSkinWeights(skeletal, LodOf is null ? 0 : LodOf(MeshExtractor.Describe(mesh).Lods));
                }

                data = Skinning.Skin(data, weights, pose);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug("Mesh {Mesh} keeps its bind pose: {Message}", meshPath, ex.Message);
            }
        }

        var groups = GroupSections(data, overrides);
        foreach (var group in groups)
        {
            var (texture, tint, clip, roughness) = ResolveMaterial(group.Material);
            var normal = texture is null ? null : ResolveNormal(group.Material);
            MeshData part;
            if (groups.Count == 1)
            {
                part = data;
            }
            else
            {
                var indices = new List<uint>();
                foreach (var section in group.Sections)
                {
                    indices.AddRange(new ArraySegment<uint>(data.Indices, section.FirstIndex, section.IndexCount));
                }

                part = MeshData.Create(data.Name, data.Positions, indices.ToArray(), data.Normals, data.Uv0) with { ExtraUvs = data.ExtraUvs };
            }

            var name = group.Material.Length == 0 ? data.Name : data.Name + " / " + group.Material[(group.Material.LastIndexOfAny(['/', '.']) + 1)..];
            parts.Add(new PreviewPart(name, part, texture, transform, tint, clip)
            {
                Material = group.Material, NormalPath = normal, Roughness = roughness, MaskUv = MaskUvOf(group.Material),
            });
        }
    }

    /// <summary>
    /// The sections of <paramref name="data"/> by the material they are drawn with: the component's override for the
    /// section's slot when it names one (by slot index: two slots sharing a material can take different overrides), else
    /// the mesh's own slot material. Sections ending up with the same material are merged into one part.
    /// </summary>
    internal static IReadOnlyList<(string Material, IReadOnlyList<MeshSection> Sections)> GroupSections(MeshData data, IReadOnlyList<string?>? overrides) =>
        data.Sections.GroupBy(s => overrides is not null && s.SlotIndex >= 0 && s.SlotIndex < overrides.Count && overrides[s.SlotIndex] is { Length: > 0 } o ? o : s.MaterialName,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, (IReadOnlyList<MeshSection>)g.ToList())).ToList();

    /// <summary>
    /// The material's base-colour texture (decoded once per path) and its look (<see cref="MaterialLooks"/>). A groom
    /// (MetaHuman hair, beard, mustache cards) has no colour texture, only a coverage atlas in its <c>Alpha</c> parameter:
    /// it is drawn as dark strands cut out by that atlas (<see cref="CoverageMask"/>) instead of solid cards wrapping the head.
    /// </summary>
    private (string? TexturePath, Vector4? Tint, float Clip, Vector2 Roughness) ResolveMaterial(string materialPath)
    {
        if (TextureSize <= 0 || string.IsNullOrEmpty(materialPath))
        {
            return (null, MaterialLooks.Untextured, 0f, Vector2.Zero);
        }

        if (!_materials.TryGetValue(materialPath, out var material))
        {
            try
            {
                material = new MaterialInspector(_catalog).Inspect(materialPath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug("Material {Material} could not be inspected: {Message}", materialPath, ex.Message);
                material = null;
            }

            _materials[materialPath] = material;
        }

        if (material?.BaseColorTexture is { } texturePath && Decoded(texturePath, () => DecodeTexture(texturePath)))
        {
            var (tint, clip, roughness) = MaterialLooks.Of(material, materialPath, textured: true);
            return (texturePath, tint, clip, roughness);
        }

        if (material?.Textures.FirstOrDefault(IsCoverage) is { } coverage && Decoded(coverage.TexturePath + MaskSuffix, () => CoverageMask(coverage.TexturePath)))
        {
            return (coverage.TexturePath + MaskSuffix, GroomTint, 0.5f, Vector2.Zero);
        }

        var look = MaterialLooks.Of(material, materialPath, textured: false);
        return (null, look.Tint, look.Clip, look.Roughness);
    }

    /// <summary>The material's scalar naming the UV channel of its colour mask (<c>M_Car_01</c>).</summary>
    public const string MaskUvParameter = "Color Mask UV Channel";

    /// <summary>
    /// The UV channel <paramref name="materialPath"/> reads its colour mask through (<see cref="PreviewPart.MaskUv"/>): its
    /// <see cref="MaskUvParameter"/>, 0 without one (the master's default) or when the material cannot be read.
    /// </summary>
    private int MaskUvOf(string materialPath)
    {
        if (string.IsNullOrEmpty(materialPath))
        {
            return 0;
        }

        if (!_materials.TryGetValue(materialPath, out var material))
        {
            try
            {
                material = new MaterialInspector(_catalog).Inspect(materialPath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug("Material {Material} could not be inspected: {Message}", materialPath, ex.Message);
                material = null;
            }

            _materials[materialPath] = material;
        }

        var channel = material?.Scalars.FirstOrDefault(s => string.Equals(s.Name, MaskUvParameter, StringComparison.OrdinalIgnoreCase))?.Value ?? 0f;
        return Math.Clamp((int)MathF.Round(channel), 0, 7);
    }

    /// <summary>The material's normal map (decoded once per path), or null.</summary>
    private string? ResolveNormal(string materialPath) =>
        _materials.TryGetValue(materialPath, out var material) && material?.NormalTexture is { } path && Decoded(path, () => DecodeTexture(path)) ? path : null;

    private TextureImage DecodeTexture(string path) => ForGpu
        ? TextureDecoder.DecodeForGpu(_catalog.LoadObject<UTexture2D>(path), maxSize: TextureSize)
        : TextureDecoder.Decode(_catalog.LoadObject<UTexture2D>(path), maxSize: TextureSize);

    /// <summary>Dark hair for groom cards drawn through their coverage atlas.</summary>
    private static Vector4 GroomTint => new(0.10f, 0.075f, 0.06f, 1f);

    private const string MaskSuffix = "#mask";

    /// <summary>A groom's coverage atlas (<c>Hair_S_Clean_CardsAtlas_Coverage</c>, <c>Beard_L_Messy_CardsAtlas_Coverage</c> …).</summary>
    private static bool IsCoverage(TextureParameter parameter) =>
        parameter.TexturePath.Contains("Coverage", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes a texture once under <paramref name="key"/> into the model's textures; false when it cannot be decoded.</summary>
    private bool Decoded(string key, Func<TextureImage> decode)
    {
        if (_textures.ContainsKey(key))
        {
            return true;
        }

        try
        {
            _textures[key] = decode();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Texture {Texture} could not be decoded: {Message}", key, ex.Message);
            return false;
        }
    }

    /// <summary>A coverage atlas as a white texture whose alpha is the coverage (its red channel), for the alpha clip.</summary>
    private TextureImage CoverageMask(string texturePath)
    {
        var source = TextureDecoder.Decode(_catalog.LoadObject<UTexture2D>(texturePath), maxSize: TextureSize);
        var rgba = new byte[source.Rgba.Length];
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
            rgba[i + 3] = source.Rgba[i];
        }

        return source with { Name = source.Name + MaskSuffix, Rgba = rgba, IsSrgb = false, IsNormalMap = false };
    }

    /// <summary>
    /// World transform (mesh space) of a socket or bone of <paramref name="mesh"/>, or null when it has none by that name;
    /// in <paramref name="pose"/> (bone name → posed mesh-space transform) when given, else in the reference pose.
    /// </summary>
    private static FTransform? SocketTransform(UObject mesh, string socket, IReadOnlyDictionary<string, FTransform>? pose = null)
    {
        if (string.IsNullOrEmpty(socket) || socket == "None")
        {
            return null;
        }

        switch (mesh)
        {
            case USkeletalMesh skeletal:
                // Sockets live on the mesh or on its skeleton asset (SCUM's vehicles keep them on the skeleton).
                IEnumerable<FPackageIndex> sockets = skeletal.Sockets ?? [];
                if (TryLoad<USkeleton>(skeletal.Skeleton) is { Sockets: { Length: > 0 } shared })
                {
                    sockets = sockets.Concat(shared);
                }

                foreach (var index in sockets)
                {
                    if (TryLoad<USkeletalMeshSocket>(index) is { } s && string.Equals(s.SocketName.Text, socket, StringComparison.OrdinalIgnoreCase))
                    {
                        var local = new FTransform(new FRotator(s.RelativeRotation.Pitch, s.RelativeRotation.Yaw, s.RelativeRotation.Roll),
                            new FVector(s.RelativeLocation.X, s.RelativeLocation.Y, s.RelativeLocation.Z),
                            new FVector(s.RelativeScale.X, s.RelativeScale.Y, s.RelativeScale.Z));
                        return local * (BoneTransform(skeletal, s.BoneName.Text, pose) ?? FTransform.Identity);
                    }
                }

                return BoneTransform(skeletal, socket, pose);
            case UStaticMesh stat:
                foreach (var index in stat.Sockets ?? [])
                {
                    if (TryLoad<UStaticMeshSocket>(index) is { } s && string.Equals(s.SocketName.Text, socket, StringComparison.OrdinalIgnoreCase))
                    {
                        return new FTransform(new FRotator(s.RelativeRotation.Pitch, s.RelativeRotation.Yaw, s.RelativeRotation.Roll),
                            new FVector(s.RelativeLocation.X, s.RelativeLocation.Y, s.RelativeLocation.Z),
                            new FVector(s.RelativeScale.X, s.RelativeScale.Y, s.RelativeScale.Z));
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>Transform of a bone in mesh space: in <paramref name="pose"/> when it names the bone, else the reference pose (bone-relative poses composed up to the root).</summary>
    private static FTransform? BoneTransform(USkeletalMesh mesh, string bone, IReadOnlyDictionary<string, FTransform>? pose = null)
    {
        if (pose is not null && pose.TryGetValue(bone, out var posed))
        {
            return posed;
        }

        var skeleton = mesh.ReferenceSkeleton;
        if (skeleton?.FinalRefBoneInfo is not { } bones || skeleton.FinalRefBonePose is not { } poses)
        {
            return null;
        }

        var index = Array.FindIndex(bones, b => string.Equals(b.Name.Text, bone, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var world = FTransform.Identity;
        for (var guard = 0; index >= 0 && index < poses.Length && guard < bones.Length; guard++)
        {
            var p = poses[index];
            world *= new FTransform(new FQuat(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W),
                new FVector(p.Translation.X, p.Translation.Y, p.Translation.Z),
                new FVector(p.Scale3D.X, p.Scale3D.Y, p.Scale3D.Z));
            index = bones[index].ParentIndex;
        }

        return world;
    }

    /// <summary>
    /// The parts of <paramref name="model"/> as one mesh in the model's space (each part's vertices placed by its transform,
    /// one section per part named by its material) with the parts' textures, colours and clip values, as a prepared asset
    /// under <paramref name="meshPath"/>: a whole vehicle the level scene draws and instances like any mesh.
    /// </summary>
    public static PreparedMeshAsset Merge(PreviewModel model, string meshPath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(meshPath);
        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var indices = new List<uint>();
        var sections = new List<MeshSection>();
        var materialTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tints = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        var cutoffs = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var roughness = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in model.Parts)
        {
            var mesh = part.Mesh;
            var at = part.Transform;
            var baseVertex = (uint)(positions.Count / 3);
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                var p = at.TransformPosition(new FVector(mesh.Positions[i * 3], mesh.Positions[(i * 3) + 1], mesh.Positions[(i * 3) + 2]));
                positions.Add(p.X);
                positions.Add(p.Y);
                positions.Add(p.Z);
                var n = mesh.Normals.Length == 0 ? new FVector(0f, 0f, 1f) : at.TransformVectorNoScale(new FVector(mesh.Normals[i * 3], mesh.Normals[(i * 3) + 1], mesh.Normals[(i * 3) + 2]));
                normals.Add(n.X);
                normals.Add(n.Y);
                normals.Add(n.Z);
                uvs.Add(mesh.Uv0.Length == 0 ? 0f : mesh.Uv0[i * 2]);
                uvs.Add(mesh.Uv0.Length == 0 ? 0f : mesh.Uv0[(i * 2) + 1]);
            }

            var material = part.Material.Length > 0 ? part.Material : part.Name;
            sections.Add(new MeshSection(material, indices.Count, mesh.Indices.Length));
            foreach (var index in mesh.Indices)
            {
                indices.Add(index + baseVertex);
            }

            if (part.TexturePath is { } texture)
            {
                materialTextures[material] = texture;
                if (part.NormalPath is { } normal)
                {
                    materialTextures[material + ScumStudio.Rendering.Resources.GpuMesh.NormalMapSuffix] = normal;
                }

                if (part.Roughness.Y > 0f)
                {
                    roughness[material] = part.Roughness;
                }
            }

            if (part.Tint is { } tint)
            {
                tints[material] = tint;
            }

            if (part.AlphaCutoff > 0f)
            {
                cutoffs[material] = part.AlphaCutoff;
            }
        }

        var merged = MeshData.Create(model.Name, positions.ToArray(), indices.ToArray(), normals.ToArray(), uvs.ToArray(), sections.ToArray());
        return new PreparedMeshAsset(meshPath, merged, materialTextures.Values.FirstOrDefault())
        {
            MaterialSlots = sections.Select(s => s.MaterialName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            MaterialTextures = materialTextures,
            MaterialTints = tints,
            MaterialAlphaCutoffs = cutoffs,
            MaterialRoughness = roughness,
        };
    }

    private UObject? TryGetDefaultObject(string packagePath)
    {
        try
        {
            return _catalog.TryLoadPackage(packagePath, out var package)
                ? package.GetExports().FirstOrDefault(e => e.Name.StartsWith("Default__", StringComparison.Ordinal))
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Package {Package} could not be read: {Message}", packagePath, ex.Message);
            return null;
        }
    }

    private static T? TryLoad<T>(FPackageIndex? index) where T : UObject
    {
        if (index is null || index.IsNull)
        {
            return null;
        }

        try
        {
            return index.Load<T>();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The class name of an import (<c>StaticMesh</c>, <c>BlueprintGeneratedClass</c> …) from the import map; empty for others.</summary>
    private static string ImportClass(FPackageIndex index) =>
        index.IsImport && index.Owner is UePackage package && -index.Index - 1 < package.ImportMap.Length ? package.ImportMap[-index.Index - 1].ClassName.Text : string.Empty;

    /// <summary>True when <paramref name="index"/> is a static or skeletal mesh: by its import's class, else by loading it.</summary>
    private static bool IsMesh(FPackageIndex index)
    {
        if (index.IsNull)
        {
            return false;
        }

        if (ImportClass(index) is "StaticMesh" or "SkeletalMesh")
        {
            return true;
        }

        try
        {
            return MeshExtractor.IsMesh(index.Load());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private UObject? TryLoadMeshObject(string objectPath)
    {
        try
        {
            var obj = _catalog.LoadObject(objectPath);
            return MeshExtractor.IsMesh(obj) ? obj : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Mesh {Mesh} could not be loaded: {Message}", objectPath, ex.Message);
            return null;
        }
    }

    private string PathOf(FPackageIndex index)
    {
        try
        {
            var path = index.ResolvedObject?.GetPathName();
            return path is null ? string.Empty : AssetPaths.NormalizeObjectPath(path, _catalog.ProjectName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    /// <summary><c>/Game/A/B.B_C</c> → <c>/Game/A/B</c>.</summary>
    private static string PackageOf(string objectPath)
    {
        if (string.IsNullOrEmpty(objectPath) || objectPath == "None")
        {
            return string.Empty;
        }

        var dot = objectPath.IndexOf('.', objectPath.LastIndexOf('/') + 1);
        return dot < 0 ? objectPath : objectPath[..dot];
    }

    private static string PackageLeaf(string packagePath) => packagePath[(packagePath.LastIndexOf('/') + 1)..];
}
