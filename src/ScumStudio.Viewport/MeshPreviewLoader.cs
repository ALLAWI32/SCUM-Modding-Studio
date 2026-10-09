using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
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

    /// <summary>Loads one static or skeletal mesh (LOD 0) with a part per material.</summary>
    /// <exception cref="InvalidDataException">The object is not a mesh.</exception>
    public PreviewModel LoadMesh(string objectPath)
    {
        var obj = _catalog.LoadObject(objectPath);
        if (!MeshExtractor.IsMesh(obj))
        {
            throw new InvalidDataException($"{objectPath} is a {obj.ExportType}, not a mesh.");
        }

        var parts = new List<PreviewPart>();
        AddMeshParts(parts, obj, objectPath, FTransform.Identity);
        return new PreviewModel(obj.Name, parts, _textures);
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
    /// </summary>
    private void AddComponents(List<PreviewPart> parts, string packagePath)
    {
        // ponytail: InheritableComponentHandler overrides of parent components are not applied (rarely move a mesh).
        var chain = new List<CUE4Parse.UE4.Assets.IPackage>();
        for (var current = packagePath; current is not null && chain.Count < MaxClassDepth;)
        {
            if (!_catalog.TryLoadPackage(current, out var package))
            {
                break;
            }

            chain.Add(package);
            current = package.GetExports().OfType<UStruct>().FirstOrDefault(s => s.SuperStruct is { IsNull: false }) is { } cls
                ? PackageOf(PathOf(cls.SuperStruct!))
                : null;
        }

        // The class defaults' components by name (the child class's template first) and the class defaults themselves.
        var components = new Dictionary<string, List<UObject>>(StringComparer.OrdinalIgnoreCase);
        var defaults = new List<UObject>();
        foreach (var package in chain)
        {
            var exports = package.GetExports().ToList();
            if (exports.FirstOrDefault(e => e.Name.StartsWith("Default__", StringComparison.Ordinal)) is not { } cdo)
            {
                continue;
            }

            defaults.Add(cdo);
            foreach (var export in exports.Where(e => e.Outer is { } outer && outer.Name == cdo.Name))
            {
                if (!components.TryGetValue(export.Name, out var templates))
                {
                    components[export.Name] = templates = [];
                }

                templates.Add(export);
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
            if (Visible(components[name]) && MeshOf(name) is { } mesh)
            {
                AddMeshParts(parts, mesh, PathOfObject(mesh), WorldOf(name, 0), pose);
            }
        }

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            foreach (var scs in chain[i].GetExports().Where(e => e.ExportType == "SimpleConstructionScript"))
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

        UObject? MeshOf(string name)
        {
            if (meshes.TryGetValue(name, out var known))
            {
                return known;
            }

            var mesh = components.TryGetValue(name, out var templates) ? LoadMeshOf(templates) : null;
            meshes[name] = mesh;
            return mesh;
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
            if (depth > 32 || parts.Count >= MaxAttachments * 8 || TryLoad<UObject>(index) is not { } node)
            {
                return;
            }

            var world = parentName is { Length: > 0 } && (worlds.ContainsKey(parentName) || components.ContainsKey(parentName)) ? WorldOf(parentName, 0) : fallback;
            var variable = node.GetOrDefault<FName>("InternalVariableName").Text;
            if (TryLoad<UObject>(node.GetOrDefault<FPackageIndex>("ComponentTemplate")) is { } template)
            {
                var socket = node.GetOrDefault<FName>("AttachToName").Text;
                world = Relative([template]) * (OnSocket(parentName, socket) ?? FTransform.Identity) * world;
                if (variable is { Length: > 0 })
                {
                    worlds[variable] = world;
                    components.TryAdd(variable, [template]);
                }

                if (Visible([template]) && (variable is { Length: > 0 } ? MeshOf(variable) : LoadMeshOf([template])) is { } mesh)
                {
                    AddMeshParts(parts, mesh, PathOfObject(mesh), world, pose);
                }
            }

            foreach (var child in node.GetOrDefault<FPackageIndex[]>("ChildNodes") ?? [])
            {
                AddNode(child, variable, world, depth + 1);
            }
        }

        UObject? LoadMeshOf(List<UObject> templates)
        {
            foreach (var property in (ReadOnlySpan<string>)["SkeletalMesh", "StaticMesh"])
            {
                if (TryGet<FPackageIndex>(templates, property, out var index) && IsMesh(index) && PathOf(index) is { Length: > 0 } path)
                {
                    return TryLoadMeshObject(path);
                }
            }

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
        var current = packagePath;
        for (var depth = 0; current is not null && depth < MaxClassDepth; depth++)
        {
            if (!_catalog.TryLoadPackage(current, out var package))
            {
                return null;
            }

            string? staticMesh = null;
            string? parent = null;
            foreach (var export in package.GetExports())
            {
                if (export is UStruct { SuperStruct: { IsNull: false } super })
                {
                    parent ??= PackageOf(PathOf(super));
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

            current = parent;
        }

        return null;
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
    private void AddMeshParts(List<PreviewPart> parts, UObject mesh, string meshPath, FTransform transform, IReadOnlyDictionary<string, FTransform>? pose = null)
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

        var groups = data.Sections.GroupBy(s => s.MaterialName, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in groups)
        {
            var (texture, tint, clip) = ResolveMaterial(group.Key);
            var (normal, roughness) = texture is null ? (null, Vector2.Zero) : ResolveSurface(group.Key, clip);
            MeshData part;
            if (groups.Count == 1)
            {
                part = data;
            }
            else
            {
                var indices = new List<uint>();
                foreach (var section in group)
                {
                    indices.AddRange(new ArraySegment<uint>(data.Indices, section.FirstIndex, section.IndexCount));
                }

                part = MeshData.Create(data.Name, data.Positions, indices.ToArray(), data.Normals, data.Uv0);
            }

            var name = group.Key.Length == 0 ? data.Name : data.Name + " / " + group.Key[(group.Key.LastIndexOfAny(['/', '.']) + 1)..];
            parts.Add(new PreviewPart(name, part, texture, transform, tint, clip) { Material = group.Key, NormalPath = normal, Roughness = roughness });
        }
    }

    /// <summary>
    /// The material's base-colour texture (decoded once per path), the tint to draw with (null = the texture as is) and
    /// the alpha clip. A groom (MetaHuman hair, beard, mustache cards) has no colour texture, only a coverage atlas in its
    /// <c>Alpha</c> parameter: it is drawn as dark strands cut out by that atlas (<see cref="CoverageMask"/>) instead of
    /// solid cards wrapping the head.
    /// </summary>
    private (string? TexturePath, Vector4? Tint, float Clip) ResolveMaterial(string materialPath)
    {
        if (TextureSize <= 0 || string.IsNullOrEmpty(materialPath))
        {
            return (null, Untextured, 0f);
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
            return (texturePath, null, material.OpacityMaskClip ?? 0f);
        }

        if (material?.Textures.FirstOrDefault(IsCoverage) is { } coverage && Decoded(coverage.TexturePath + MaskSuffix, () => CoverageMask(coverage.TexturePath)))
        {
            return (coverage.TexturePath + MaskSuffix, GroomTint, 0.5f);
        }

        return (null, material?.TintColor is { } tint && tint.W > 0f ? tint with { W = 1f } : Untextured, 0f);
    }

    /// <summary>The material's normal map (decoded once per path) and, for an opaque one, its roughness range.</summary>
    private (string? NormalPath, Vector2 Roughness) ResolveSurface(string materialPath, float clip)
    {
        if (!_materials.TryGetValue(materialPath, out var material) || material is null)
        {
            return (null, Vector2.Zero);
        }

        var roughness = clip == 0f && !material.IsTranslucent && material.RoughnessRange is { } r ? r : Vector2.Zero;
        return material.NormalTexture is { } path && Decoded(path, () => DecodeTexture(path))
            ? (path, roughness)
            : (null, roughness);
    }

    private TextureImage DecodeTexture(string path) => ForGpu
        ? TextureDecoder.DecodeForGpu(_catalog.LoadObject<UTexture2D>(path), maxSize: TextureSize)
        : TextureDecoder.Decode(_catalog.LoadObject<UTexture2D>(path), maxSize: TextureSize);

    private static Vector4 Untextured => new(0.6f, 0.6f, 0.6f, 1f);

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

    private static bool IsMesh(FPackageIndex index)
    {
        if (index.IsNull)
        {
            return false;
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

    private string PathOfObject(UObject obj)
    {
        try
        {
            return AssetPaths.NormalizeObjectPath(obj.GetPathName(), _catalog.ProjectName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return obj.Name;
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
