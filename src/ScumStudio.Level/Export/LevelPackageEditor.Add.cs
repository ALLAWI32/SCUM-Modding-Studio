using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Model;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Level.Export;

/// <summary>A new <c>StaticMeshActor</c> to create in the level (the export of <c>AddStaticMeshActorOp</c>).</summary>
/// <param name="NewName">Actor object name (unique in the level).</param>
/// <param name="StaticMesh">Object path of the mesh, e.g. <c>/Game/X/SM_Rock.SM_Rock</c> (a package path is accepted too).</param>
/// <param name="Transform">Relative transform of the root component (world transform for a level actor).</param>
/// <param name="Spline">
/// When set, a <c>SplineMeshActor</c> drawing the mesh bent along this curve (<see cref="BendShape"/>) instead of a
/// <c>StaticMeshActor</c>.
/// </param>
/// <param name="Collision">
/// For a spline piece: its collision as boxes in component space (<see cref="PieceCollision"/>), written as the
/// component's <c>BodySetup</c>. A cooked game builds no collision for a spline mesh by itself: without these the piece is
/// seen but not solid.
/// </param>
/// <param name="MeshBodySetupGuid">
/// The mesh's <c>BodySetupGuid</c>, written as <c>CachedMeshBodySetupGuid</c>: a spline mesh component whose cached guid
/// differs rebuilds its collision from the mesh at load (copying the mesh's body over ours, then failing to cook its
/// triangles in a cooked game), so with the guid the boxes stay.
/// </param>
public sealed record StaticMeshActorAdd(string NewName, string StaticMesh, TransformValue Transform, SplineMeshParams? Spline = null,
    IReadOnlyList<Assets.Meshes.CollisionBox>? Collision = null, FGuid? MeshBodySetupGuid = null)
{
    /// <summary>
    /// A collision profile to collide as (what the copied tree or rock used), written as <c>BodyInstance.CollisionProfileName</c>
    /// with <c>bUseDefaultCollision</c> off; null collides as the mesh does by default.
    /// </summary>
    public string? CollisionProfile { get; init; }

    /// <summary>
    /// Draw into the landscape's virtual texture (<c>RuntimeVirtualTextures</c> = <c>RTV_Landscape</c>), as the game places its
    /// road pieces; the ground then shows the road (<see cref="BendMesh.DrawsIntoLandscape"/>).
    /// </summary>
    public bool DrawsIntoLandscape { get; init; }

    /// <summary>
    /// And never on screen, with no shadow (<c>VirtualTextureRenderPassType</c> Never, <c>CastShadow</c> off), as the game's
    /// gravel road pieces (<see cref="BendMesh.OnlyIntoLandscape"/>): on screen they show bright pink.
    /// </summary>
    public bool OnlyIntoLandscape { get; init; }
}

/// <summary>
/// Creating <c>StaticMeshActor</c>s from scratch in a cooked level: two exports (the actor and its
/// <c>StaticMeshComponent0</c>) shaped exactly like the ones the cooker writes (tagged properties + <c>bHasGuid</c>, the
/// component with an empty <c>LODData</c> array), the engine class / archetype imports and the mesh import added when the
/// package lacks them, event-driven-loader dependency groups mirroring a cooked actor, and the actor appended to
/// <c>ULevel::Actors</c>.
/// </summary>
public static partial class LevelPackageEditor
{
    private const string EnginePackage = "/Script/Engine";
    private const string CoreUObjectPackage = "/Script/CoreUObject";
    private const string StaticMeshActorClass = "StaticMeshActor";
    private const string StaticMeshComponentClass = "StaticMeshComponent";
    private const string StaticMeshClass = "StaticMesh";
    private const string DefaultStaticMeshActor = "Default__StaticMeshActor";
    private const string StaticMeshComponent0 = "StaticMeshComponent0";
    private const uint ActorFlags = 0x8; // RF_Transactional
    private const uint ComponentFlags = 0x40008; // RF_DefaultSubObject | RF_Transactional
    private const string LandscapeTexturePackage = "/Game/ConZ_Files/Landscape/VT_Landscape/RTV_Landscape";

    private static List<int> AddStaticMeshActors(
        CookedPackage package, int levelIndex, IReadOnlyList<StaticMeshActorAdd> adds, List<ExportEntry> exports, List<ReadOnlyMemory<byte>> data,
        List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, List<int> preload, List<string> added, List<string> warnings)
    {
        var addedIndices = new List<int>();
        if (adds.Count == 0)
        {
            return addedIndices;
        }

        var levelPackageIndex = levelIndex + 1;
        var levelEntry = package.Exports[levelIndex];
        var taken = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
        foreach (var add in adds)
        {
            if (string.IsNullOrWhiteSpace(add.NewName) || FindExport(package, add.NewName, levelPackageIndex) >= 0 || !taken.Add(add.NewName))
            {
                warnings.Add($"An actor named '{add.NewName}' already exists in the level; the new {StaticMeshActorClass} was not created.");
                continue;
            }

            var (meshPackage, meshName) = SplitObjectPath(add.StaticMesh);
            if (meshPackage.Length == 0 || meshName.Length == 0)
            {
                warnings.Add($"'{add.NewName}': '{add.StaticMesh}' is not a mesh object path; the actor was not created.");
                continue;
            }

            FNameRef Name(string value) => GetOrAddName(names, wide, addedNames, value);
            int Import(string classPackage, string className, int outer, string objectName) =>
                GetOrAddImport(imports, names, wide, addedNames, classPackage, className, outer, objectName);

            var spline = add.Spline;
            var (actorClassName, componentClassName, componentName) = spline is null
                ? (StaticMeshActorClass, StaticMeshComponentClass, StaticMeshComponent0)
                : ("SplineMeshActor", "SplineMeshComponent", "SplineMeshComponent0");
            var enginePkg = Import(CoreUObjectPackage, "Package", 0, EnginePackage);
            var actorClass = Import(CoreUObjectPackage, "Class", enginePkg, actorClassName);
            var componentClass = Import(CoreUObjectPackage, "Class", enginePkg, componentClassName);
            var actorArchetype = Import(EnginePackage, actorClassName, enginePkg, "Default__" + actorClassName);
            var componentArchetype = Import(EnginePackage, componentClassName, actorArchetype, componentName);
            var meshPkg = Import(CoreUObjectPackage, "Package", 0, meshPackage);
            var mesh = Import(EnginePackage, StaticMeshClass, meshPkg, meshName);
            var landscapeTexture = add.DrawsIntoLandscape
                ? Import(EnginePackage, "RuntimeVirtualTexture", Import(CoreUObjectPackage, "Package", 0, LandscapeTexturePackage), "RTV_Landscape")
                : 0;

            var actorIndex = exports.Count + 1;
            var componentIndex = exports.Count + 2;
            var collision = spline is not null && add.Collision is { Count: > 0 } boxes ? boxes : null;
            var bodySetupIndex = collision is null ? 0 : exports.Count + 3;

            // Actor: StaticMeshComponent (or SplineMeshComponent) + RootComponent -> the component; bHasGuid 0.
            var actor = new ByteWriter(80);
            WriteObjectTag(actor, Name, componentClassName, componentIndex);
            WriteObjectTag(actor, Name, RootComponentProperty, componentIndex);
            actor.FName(Name("None"));
            actor.I32(0);

            // Component: StaticMesh + relative transform; bHasGuid 0; LODData (empty array).
            var component = new ByteWriter(160);
            WriteObjectTag(component, Name, StaticMeshClass, mesh);
            var t = add.Transform;
            WriteStructTag(component, Name, "RelativeLocation", "Vector", t.Location.X, t.Location.Y, t.Location.Z);
            WriteStructTag(component, Name, "RelativeRotation", "Rotator", t.Rotation.Pitch, t.Rotation.Yaw, t.Rotation.Roll);
            WriteStructTag(component, Name, "RelativeScale3D", "Vector", t.Scale.X, t.Scale.Y, t.Scale.Z);
            if (spline is not null)
            {
                WriteSplineParams(component, Name, spline);
            }

            if (landscapeTexture != 0)
            {
                WriteObjectArrayTag(component, Name, "RuntimeVirtualTextures", [landscapeTexture]);
            }

            if (landscapeTexture != 0 && add.OnlyIntoLandscape)
            {
                WriteBoolTag(component, Name, "CastShadow", false);
                component.FName(Name("VirtualTextureRenderPassType"));
                component.FName(Name("EnumProperty"));
                component.I32(8);
                component.I32(0);
                component.FName(Name("ERuntimeVirtualTextureMainPassType"));
                component.U8(0);
                component.FName(Name("ERuntimeVirtualTextureMainPassType::Never"));
            }

            if (bodySetupIndex == 0 && add.CollisionProfile is { Length: > 0 } profile)
            {
                // Collide as the copied object did: SCUM's tree foliage blocks players as SCUM_TreeStump, the tree mesh's own
                // default (SCUM_Foliage) does not, and a StaticMeshActor uses its mesh's default unless told otherwise.
                WriteBoolTag(component, Name, "bUseDefaultCollision", false);
                WriteCollisionProfileTag(component, Name, profile);
            }

            if (bodySetupIndex != 0)
            {
                WriteObjectTag(component, Name, BodySetupClass, bodySetupIndex);

                // Collide like the placed mesh does: its asset's profile and responses (vehicle wheels use their own
                // channel), as a StaticMeshActor's component does by default; the shapes are the boxes.
                WriteBoolTag(component, Name, "bUseDefaultCollision", true);

                // The mesh's body guid: without it the component rebuilds its collision from the mesh at load and ours is lost
                // (the owner fell through; the game log said "Triangle data ... cannot be accessed at runtime").
                if (add.MeshBodySetupGuid is { IsZero: false } meshGuid)
                {
                    component.FName(Name("CachedMeshBodySetupGuid"));
                    component.FName(Name(StructPropertyType));
                    component.I32(16);
                    component.I32(0);
                    component.FName(Name("Guid"));
                    component.Guid(default);
                    component.U8(0);
                    component.Guid(meshGuid);
                }
            }

            component.FName(Name("None"));
            component.I32(0);
            component.I32(0);

            // Dependencies, shaped like a cooked StaticMeshActor (see `pkg info --deps`).
            var actorFirst = preload.Count;
            preload.Add(componentIndex); // create-before-serialize
            preload.AddRange([actorClass, actorArchetype, componentArchetype]); // serialize-before-create
            preload.Add(levelPackageIndex); // create-before-create
            var componentFirst = preload.Count;
            preload.AddRange([mesh, actorArchetype]); // create-before-serialize
            if (landscapeTexture != 0)
            {
                preload.Add(landscapeTexture); // as on the game's road pieces
            }

            if (bodySetupIndex != 0)
            {
                preload.Add(bodySetupIndex); // its collision exists before it is read (as on a cooked road piece)
            }

            preload.AddRange([componentClass, componentArchetype]); // serialize-before-create
            preload.Add(actorIndex); // create-before-create

            exports.Add(new ExportEntry
            {
                ClassIndex = actorClass,
                SuperIndex = 0,
                TemplateIndex = actorArchetype,
                OuterIndex = levelPackageIndex,
                ObjectName = MakeName(add.NewName, names, wide, addedNames),
                ObjectFlags = ActorFlags,
                PackageGuid = levelEntry.PackageGuid,
                PackageFlags = levelEntry.PackageFlags,
                FirstExportDependency = actorFirst,
                SerializationBeforeSerializationDependencies = 0,
                CreateBeforeSerializationDependencies = 1,
                SerializationBeforeCreateDependencies = 3,
                CreateBeforeCreateDependencies = 1,
            });
            data.Add(actor.ToArray());
            exports.Add(new ExportEntry
            {
                ClassIndex = componentClass,
                SuperIndex = 0,
                TemplateIndex = componentArchetype,
                OuterIndex = actorIndex,
                ObjectName = Name(componentName),
                ObjectFlags = ComponentFlags,
                PackageGuid = levelEntry.PackageGuid,
                PackageFlags = levelEntry.PackageFlags,
                FirstExportDependency = componentFirst,
                SerializationBeforeSerializationDependencies = 0,
                CreateBeforeSerializationDependencies = 2 + (bodySetupIndex != 0 ? 1 : 0) + (landscapeTexture != 0 ? 1 : 0),
                SerializationBeforeCreateDependencies = 2,
                CreateBeforeCreateDependencies = 1,
            });
            data.Add(component.ToArray());

            if (collision is not null)
            {
                var bodyClass = Import(CoreUObjectPackage, "Class", enginePkg, BodySetupClass);
                var bodyArchetype = Import(EnginePackage, BodySetupClass, enginePkg, "Default__" + BodySetupClass);
                var bodyFirst = preload.Count;
                preload.AddRange([bodyClass, bodyArchetype]); // serialize-before-create
                preload.Add(componentIndex); // create-before-create
                exports.Add(new ExportEntry
                {
                    ClassIndex = bodyClass,
                    SuperIndex = 0,
                    TemplateIndex = bodyArchetype,
                    OuterIndex = componentIndex,
                    ObjectName = Name("BodySetup_0"),
                    ObjectFlags = BodySetupFlags,
                    PackageGuid = levelEntry.PackageGuid,
                    PackageFlags = levelEntry.PackageFlags,
                    FirstExportDependency = bodyFirst,
                    SerializationBeforeSerializationDependencies = 0,
                    CreateBeforeSerializationDependencies = 0,
                    SerializationBeforeCreateDependencies = 2,
                    CreateBeforeCreateDependencies = 1,
                });
                data.Add(BoxBodySetup(Name, collision, package.ResolveName(package.Exports[levelIndex].ObjectName) + "/" + add.NewName));
            }

            addedIndices.Add(actorIndex);
            added.Add(add.NewName);
        }

        return addedIndices;
    }

    /// <summary>Re-emits the level export's dependency groups with <paramref name="newActors"/> created before it is serialized.</summary>
    private static void RegisterActorsWithLevel(CookedPackage package, int levelIndex, List<ExportEntry> exports, List<int> preload, IReadOnlyList<int> newActors)
    {
        if (newActors.Count == 0)
        {
            return;
        }

        var level = exports[levelIndex];
        var first = preload.Count;
        AppendGroups(package, package.Exports[levelIndex], new Dictionary<int, int>(), preload, extraCreateBeforeSerialize: newActors);
        exports[levelIndex] = level with
        {
            FirstExportDependency = first,
            CreateBeforeSerializationDependencies = package.Exports[levelIndex].CreateBeforeSerializationDependencies + newActors.Count,
        };
    }

    /// <summary>(<c>/Game/X/SM_Rock</c>, <c>SM_Rock</c>) from an object path, a package path or <c>Package.Object</c>.</summary>
    internal static (string Package, string Object) SplitObjectPath(string path)
    {
        var p = (path ?? string.Empty).Trim();
        var dot = p.LastIndexOf('.');
        var slash = p.LastIndexOf('/');
        if (dot > slash && dot >= 0)
        {
            return (p[..dot], p[(dot + 1)..]);
        }

        return slash >= 0 ? (p, p[(slash + 1)..]) : (string.Empty, string.Empty);
    }

    /// <summary>Index (negative) of the import with this class, outer and name, adding it when missing.</summary>
    internal static int GetOrAddImport(
        List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames,
        string classPackage, string className, int outer, string objectName)
    {
        for (var i = 0; i < imports.Count; i++)
        {
            var im = imports[i];
            if (im.OuterIndex == outer
                && NameEquals(names, im.ClassName, className)
                && NameEquals(names, im.ObjectName, objectName)
                && NameEquals(names, im.ClassPackage, classPackage))
            {
                return -(i + 1);
            }
        }

        imports.Add(new ImportEntry(
            GetOrAddName(names, wide, addedNames, classPackage),
            GetOrAddName(names, wide, addedNames, className),
            outer,
            GetOrAddName(names, wide, addedNames, objectName)));
        return -imports.Count;
    }

    private static bool NameEquals(List<string> names, FNameRef name, string text) =>
        name.Index >= 0 && name.Index < names.Count && string.Equals(name.Format(names), text, StringComparison.OrdinalIgnoreCase);

    private static void WriteObjectTag(ByteWriter w, Func<string, FNameRef> name, string property, int packageIndex)
    {
        w.FName(name(property));
        w.FName(name("ObjectProperty"));
        w.I32(4);
        w.I32(0);
        w.U8(0);
        w.I32(packageIndex);
    }

    /// <summary>
    /// <c>SplineParams</c> (a tagged <c>SplineMeshParams</c> struct, defaults left out as the cooker does) plus the
    /// component's <c>ForwardAxis</c> and the mesh range <c>SplineBoundaryMin/Max</c>.
    /// </summary>
    private static void WriteSplineParams(ByteWriter w, Func<string, FNameRef> name, SplineMeshParams s)
    {
        WriteSplineParamsTag(w, name, s);

        if (s.ForwardAxis != SplineMeshAxis.X)
        {
            // TEnumAsByte<ESplineMeshAxis::Type>: a ByteProperty tag naming its enum, the value as an FName.
            w.FName(name("ForwardAxis"));
            w.FName(name("ByteProperty"));
            w.I32(8);
            w.I32(0);
            w.FName(name("ESplineMeshAxis"));
            w.U8(0);
            w.FName(name("ESplineMeshAxis::" + s.ForwardAxis));
        }

        if (s.SplineUpDir != Core.Mathematics.FVector.Up)
        {
            // A piece standing upright (longer legs) needs an up direction across it.
            WriteStructTag(w, name, "SplineUpDir", "Vector", s.SplineUpDir.X, s.SplineUpDir.Y, s.SplineUpDir.Z);
        }

        WriteFloatTag(w, name, "SplineBoundaryMin", s.SplineBoundaryMin);
        WriteFloatTag(w, name, "SplineBoundaryMax", s.SplineBoundaryMax);
    }

    /// <summary>The <c>SplineParams</c> tag alone: every member of the struct (offsets only when set), then <c>None</c>.</summary>
    private static void WriteSplineParamsTag(ByteWriter w, Func<string, FNameRef> name, SplineMeshParams s)
    {
        var inner = new ByteWriter(400);
        WriteStructTag(inner, name, "StartPos", "Vector", s.StartPos.X, s.StartPos.Y, s.StartPos.Z);
        WriteStructTag(inner, name, "StartTangent", "Vector", s.StartTangent.X, s.StartTangent.Y, s.StartTangent.Z);
        WriteVector2DTag(inner, name, "StartScale", s.StartScale.X, s.StartScale.Y);
        WriteFloatTag(inner, name, "StartRoll", s.StartRoll);
        if (s.StartOffset != System.Numerics.Vector2.Zero)
        {
            WriteVector2DTag(inner, name, "StartOffset", s.StartOffset.X, s.StartOffset.Y);
        }

        WriteStructTag(inner, name, "EndPos", "Vector", s.EndPos.X, s.EndPos.Y, s.EndPos.Z);
        WriteStructTag(inner, name, "EndTangent", "Vector", s.EndTangent.X, s.EndTangent.Y, s.EndTangent.Z);
        WriteVector2DTag(inner, name, "EndScale", s.EndScale.X, s.EndScale.Y);
        WriteFloatTag(inner, name, "EndRoll", s.EndRoll);
        if (s.EndOffset != System.Numerics.Vector2.Zero)
        {
            WriteVector2DTag(inner, name, "EndOffset", s.EndOffset.X, s.EndOffset.Y);
        }

        inner.FName(name("None"));

        w.FName(name("SplineParams"));
        w.FName(name(StructPropertyType));
        w.I32(inner.Length);
        w.I32(0);
        w.FName(name("SplineMeshParams"));
        w.Guid(default);
        w.U8(0);
        w.Raw(inner.WrittenSpan);
    }

    /// <summary>
    /// Rewrites a stored SplineMeshComponent's <c>SplineParams</c>; with <see cref="SplinePatch.Collision"/>, its
    /// <c>BodySetup</c> (the cooked collision of the old curve) becomes boxes that follow the new one. Its
    /// <c>CachedMeshBodySetupGuid</c> stays: dropping it makes the game rebuild the collision from the mesh at load, which
    /// fails in a cooked game (no triangle data at runtime) and leaves the piece with none.
    /// </summary>
    private static bool TryPatchSpline(CookedPackage package, int levelIndex, SplinePatch patch, IList<ReadOnlyMemory<byte>> data,
        List<string> names, List<bool> wide, List<string> addedNames, List<string> warnings)
    {
        var actorIndex = FindExport(package, patch.Actor, levelIndex + 1);
        var componentIndex = actorIndex < 0 ? -1 : FindExport(package, patch.Component, actorIndex + 1);
        if (componentIndex < 0)
        {
            warnings.Add($"'{patch.Actor}.{patch.Component}' is not stored in the level package; its bend was not written.");
            return false;
        }

        var payload = data[componentIndex].ToArray();
        PropertyBlock block;
        try
        {
            block = Formats.Properties.PropertyReader.ReadPayload(package, payload, componentIndex);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            warnings.Add($"'{patch.Actor}.{patch.Component}': its properties could not be read ({ex.Message}); its bend was not written.");
            return false;
        }

        if (block.Find("SplineParams") is not { Type: StructPropertyType } spline)
        {
            warnings.Add($"'{patch.Actor}.{patch.Component}' has no SplineParams; its bend was not written.");
            return false;
        }

        FNameRef Name(string value) => GetOrAddName(names, wide, addedNames, value);
        if (patch.Collision is { Count: > 0 } boxes && block.Find("BodySetup") is { Type: "ObjectProperty" } body
            && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(body.EndOffset - 4)) is var bodyIndex
            && bodyIndex > 0 && bodyIndex <= data.Count)
        {
            data[bodyIndex - 1] = BoxBodySetup(Name, boxes, patch.Actor + "/" + patch.Component);
        }

        var tag = new ByteWriter(500);
        WriteSplineParamsTag(tag, Name, patch.Spline);

        payload = [.. payload.AsSpan(0, spline.Offset), .. tag.ToArray(), .. payload.AsSpan(spline.EndOffset)];

        data[componentIndex] = payload;
        return true;
    }

    /// <summary>
    /// A <c>BodyInstance</c> holding only its <c>CollisionProfileName</c>: loading it, the game takes the profile's object
    /// type, collision and responses (<c>FBodyInstance::LoadProfileData</c>).
    /// </summary>
    private static void WriteCollisionProfileTag(ByteWriter w, Func<string, FNameRef> name, string profile)
    {
        var inner = new ByteWriter(48);
        inner.FName(name("CollisionProfileName"));
        inner.FName(name("NameProperty"));
        inner.I32(8);
        inner.I32(0);
        inner.U8(0);
        inner.FName(name(profile));
        inner.FName(name("None"));
        var data = inner.ToArray();
        w.FName(name("BodyInstance"));
        w.FName(name(StructPropertyType));
        w.I32(data.Length);
        w.I32(0);
        w.FName(name("BodyInstance"));
        w.Guid(default);
        w.U8(0);
        w.Raw(data);
    }

    private static void WriteFloatTag(ByteWriter w, Func<string, FNameRef> name, string property, float value)
    {
        w.FName(name(property));
        w.FName(name("FloatProperty"));
        w.I32(4);
        w.I32(0);
        w.U8(0);
        w.F32(value);
    }

    private static void WriteVector2DTag(ByteWriter w, Func<string, FNameRef> name, string property, float x, float y)
    {
        w.FName(name(property));
        w.FName(name(StructPropertyType));
        w.I32(8);
        w.I32(0);
        w.FName(name("Vector2D"));
        w.Guid(default);
        w.U8(0);
        w.F32(x);
        w.F32(y);
    }

    private static void WriteStructTag(ByteWriter w, Func<string, FNameRef> name, string property, string structName, float x, float y, float z)
    {
        w.FName(name(property));
        w.FName(name(StructPropertyType));
        w.I32(VectorSize);
        w.I32(0);
        w.FName(name(structName));
        w.Guid(default);
        w.U8(0);
        w.F32(x);
        w.F32(y);
        w.F32(z);
    }
}
