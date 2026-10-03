using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
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
public sealed record PreviewPart(string Name, MeshData Mesh, string? TexturePath, FTransform Transform, Vector4? Tint = null, float AlphaCutoff = 0f);

/// <summary>A CPU-side model for <c>MeshPreview</c>: parts plus the decoded base-colour textures they share.</summary>
/// <param name="Name">What is shown (asset or Blueprint name).</param>
/// <param name="Parts">Parts to draw.</param>
/// <param name="Textures">Decoded textures by object path.</param>
public sealed record PreviewModel(string Name, IReadOnlyList<PreviewPart> Parts, IReadOnlyDictionary<string, TextureImage> Textures)
{
    /// <summary>Total triangles.</summary>
    public long Triangles => Parts.Sum(p => (long)p.Mesh.TriangleCount);
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

    /// <summary>Chassis (0) and the parts in its slots (1); deeper slots hold optional add-ons (armour plates, wheel guards) a stock vehicle does not spawn with.</summary>
    private const int MaxAttachmentDepth = 2;
    private const int MaxAttachments = 64;

    private readonly AssetCatalog _catalog;
    private readonly ILogger _logger;
    private readonly Dictionary<string, TextureImage> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialInfo?> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MeshData> _meshes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a loader over <paramref name="catalog"/>.</summary>
    public MeshPreviewLoader(AssetCatalog catalog, ILogger? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Largest texture edge in pixels (0 = no textures).</summary>
    public int TextureSize { get; init; } = 1024;

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
    public PreviewModel? LoadBlueprint(string packagePath)
    {
        // Buildings, rooms and props are many meshes in the construction script; vehicles are one skeletal body plus slots.
        if (TryGetDefaultObject(packagePath)?.GetOrDefault<FStructFallback>("_chassisSlot") is null)
        {
            var assembled = new List<PreviewPart>();
            AddConstructionScripts(assembled, packagePath);
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
        if (TryGetDefaultObject(packagePath) is { } cdo && cdo.GetOrDefault<FStructFallback>("_chassisSlot") is { } chassisSlot)
        {
            AddAttachments(parts, [chassisSlot], root, FTransform.Identity, root, 0, ref count);
        }

        return new PreviewModel(PackageLeaf(packagePath), parts, _textures);
    }

    /// <summary>
    /// Every static and skeletal mesh component of the Blueprint's construction scripts (parent classes first), each at
    /// its template's relative transform along the SCS tree: a house Blueprint shows its walls, roof and furniture.
    /// </summary>
    private void AddConstructionScripts(List<PreviewPart> parts, string packagePath)
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

        var worlds = new Dictionary<string, FTransform>(StringComparer.OrdinalIgnoreCase);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            foreach (var scs in chain[i].GetExports().Where(e => e.ExportType == "SimpleConstructionScript"))
            {
                foreach (var rootNode in scs.GetOrDefault<FPackageIndex[]>("RootNodes") ?? [])
                {
                    var parentName = TryLoad<UObject>(rootNode)?.GetOrDefault<FName>("ParentComponentOrVariableName").Text;
                    AddNode(rootNode, parentName is { Length: > 0 } && worlds.TryGetValue(parentName, out var w) ? w : FTransform.Identity, 0);
                }
            }
        }

        void AddNode(FPackageIndex index, FTransform parentWorld, int depth)
        {
            if (depth > 32 || parts.Count >= MaxAttachments * 8 || TryLoad<UObject>(index) is not { } node)
            {
                return;
            }

            var world = parentWorld;
            if (TryLoad<UObject>(node.GetOrDefault<FPackageIndex>("ComponentTemplate")) is { } template)
            {
                var l = template.GetOrDefault("RelativeLocation", new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0));
                var r = template.GetOrDefault("RelativeRotation", new CUE4Parse.UE4.Objects.Core.Math.FRotator(0, 0, 0));
                var s = template.GetOrDefault("RelativeScale3D", new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1));
                world = new FTransform(new FRotator(r.Pitch, r.Yaw, r.Roll), new FVector(l.X, l.Y, l.Z), new FVector(s.X, s.Y, s.Z)) * parentWorld;
                if (node.GetOrDefault<FName>("InternalVariableName").Text is { Length: > 0 } name)
                {
                    worlds[name] = world;
                }

                var visible = template.GetOrDefault("bVisible", true) && !template.GetOrDefault("bHiddenInGame", false);
                foreach (var property in (ReadOnlySpan<string>)["StaticMesh", "SkeletalMesh"])
                {
                    if (visible && template.TryGetValue<FPackageIndex>(out var mesh, property) && IsMesh(mesh) && PathOf(mesh) is { Length: > 0 } meshPath
                        && TryLoadMeshObject(meshPath) is { } meshObject)
                    {
                        AddMeshParts(parts, meshObject, meshPath, world);
                    }
                }
            }

            foreach (var child in node.GetOrDefault<FPackageIndex[]>("ChildNodes") ?? [])
            {
                AddNode(child, world, depth + 1);
            }
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

    private void AddAttachments(List<PreviewPart> parts, IEnumerable<FStructFallback> slots, UObject parentMesh, FTransform parentWorld, UObject vehicleMesh, int depth, ref int count)
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

            // The first class of a slot is the plain part (armour and other variants follow).
            var attachmentPackage = PackageOf(classes[0].AssetPathName.Text);
            if (attachmentPackage.Length == 0 || TryGetDefaultObject(attachmentPackage) is not { } cdo)
            {
                continue;
            }

            count++;
            var socket = cdo.GetOrDefault<FName>("ParentSocket").Text;
            var world = (SocketTransform(parentMesh, socket) ?? SocketTransform(vehicleMesh, socket) ?? FTransform.Identity) * parentWorld;
            var meshPath = cdo.GetOrDefault<FStructFallback>("MeshSetup")?.GetOrDefault<FSoftObjectPath>("Mesh").AssetPathName.Text;
            var mesh = string.IsNullOrEmpty(meshPath) || meshPath == "None" ? null : TryLoadMeshObject(meshPath);
            if (mesh is not null)
            {
                AddMeshParts(parts, mesh, meshPath!, world);
            }

            var children = cdo.GetOrDefault<FStructFallback[]>("_slots");
            if (children is { Length: > 0 })
            {
                AddAttachments(parts, children.Where(c => c is not null), mesh ?? parentMesh, world, vehicleMesh, depth + 1, ref count);
            }
        }
    }

    /// <summary>Adds one part per material of <paramref name="mesh"/> (sections sharing a material are merged); a mesh without render data is skipped.</summary>
    private void AddMeshParts(List<PreviewPart> parts, UObject mesh, string meshPath, FTransform transform)
    {
        if (!_meshes.TryGetValue(meshPath, out var data))
        {
            try
            {
                // The first LOD that still has render data (cooked meshes may strip LOD 0 for far-only or hidden parts).
                var lod = MeshExtractor.Describe(mesh).Lods.FirstOrDefault(l => !l.IsStripped)?.Index ?? 0;
                data = MeshExtractor.Extract(mesh, lod);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug("Mesh {Mesh} could not be extracted: {Message}", meshPath, ex.Message);
                return;
            }

            _meshes[meshPath] = data;
        }

        var groups = data.Sections.GroupBy(s => s.MaterialName, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in groups)
        {
            var (texture, tint) = ResolveMaterial(group.Key);
            var clip = texture is not null && _materials.TryGetValue(group.Key, out var material) ? material?.OpacityMaskClip ?? 0f : 0f;
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
            parts.Add(new PreviewPart(name, part, texture, transform, tint, clip));
        }
    }

    /// <summary>The material's base-colour texture (decoded once per path) and the tint to use without one.</summary>
    private (string? TexturePath, Vector4? Tint) ResolveMaterial(string materialPath)
    {
        if (TextureSize <= 0 || string.IsNullOrEmpty(materialPath))
        {
            return (null, Untextured);
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

        if (material?.BaseColorTexture is { } texturePath)
        {
            if (!_textures.ContainsKey(texturePath))
            {
                try
                {
                    _textures[texturePath] = TextureDecoder.Decode(_catalog.LoadObject<UTexture2D>(texturePath), maxSize: TextureSize);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogDebug("Texture {Texture} could not be decoded: {Message}", texturePath, ex.Message);
                }
            }

            if (_textures.ContainsKey(texturePath))
            {
                return (texturePath, null);
            }
        }

        return (null, material?.TintColor is { } tint && tint.W > 0f ? tint with { W = 1f } : Untextured);
    }

    private static Vector4 Untextured => new(0.6f, 0.6f, 0.6f, 1f);

    /// <summary>World transform (mesh space) of a socket or bone of <paramref name="mesh"/>, or null when it has none by that name.</summary>
    private static FTransform? SocketTransform(UObject mesh, string socket)
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
                        return local * (BoneTransform(skeletal, s.BoneName.Text) ?? FTransform.Identity);
                    }
                }

                return BoneTransform(skeletal, socket);
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

    /// <summary>Reference-pose transform of a bone in mesh space (bone-relative poses composed up to the root).</summary>
    private static FTransform? BoneTransform(USkeletalMesh mesh, string bone)
    {
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
