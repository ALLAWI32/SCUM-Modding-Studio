using System.Numerics;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Model;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Level.Export;

/// <summary>A relative transform to write into one component export.</summary>
/// <param name="Actor">Actor object name (unique in the level).</param>
/// <param name="Component">Component name inside the actor; null = the actor's <c>RootComponent</c>.</param>
/// <param name="Value">The new relative location, rotation and scale.</param>
public sealed record TransformPatch(string Actor, string? Component, TransformValue Value);

/// <summary>New <c>SplineParams</c> for one stored SplineMeshComponent (a bent road, rail or bridge piece).</summary>
/// <param name="Actor">Owning actor name.</param>
/// <param name="Component">Component name.</param>
/// <param name="Spline">The curve to write (component space).</param>
/// <param name="Collision">Boxes following the new curve (component space) to replace the piece's cooked collision; null keeps it.</param>
public sealed record SplinePatch(string Actor, string Component, SplineMeshParams Spline, IReadOnlyList<Assets.Meshes.CollisionBox>? Collision = null);

/// <summary>
/// One ISM/HISM instance to rewrite inside its component's <c>PerInstanceSMData</c>. A null <paramref name="Local"/>
/// "deletes" the instance by collapsing it in place (scale 0.0001), which keeps the array length, the HISM cluster tree
/// and every other instance index valid.
/// </summary>
/// <param name="Actor">Owning actor name.</param>
/// <param name="Component">ISM/HISM component name.</param>
/// <param name="Index">Instance index in the pristine array.</param>
/// <param name="Local">New instance transform in component space, or null to collapse it.</param>
public sealed record InstancePatch(string Actor, string Component, int Index, FTransform? Local);

/// <summary>
/// The pristine instance transforms of one component (as the level reader decoded them). The editor locates the
/// component's <c>PerInstanceSMData</c> in the native data by its element count and verifies the first matrices against
/// these before writing anything.
/// </summary>
/// <param name="Actor">Owning actor name.</param>
/// <param name="Component">Component name.</param>
/// <param name="Instances">Component-space transforms in array order.</param>
public sealed record InstanceArrayHint(string Actor, string Component, IReadOnlyList<FTransform> Instances);

/// <summary>What <see cref="LevelPackageEditor"/> changes in one level package.</summary>
public sealed record LevelEditRequest
{
    /// <summary>Actor object names to remove from the level's actor list (case-insensitive).</summary>
    public IReadOnlyCollection<string> DeleteActors { get; init; } = [];

    /// <summary>Relative transforms to patch into component exports.</summary>
    public IReadOnlyList<TransformPatch> Transforms { get; init; } = [];

    /// <summary>ISM/HISM instances to collapse or move.</summary>
    public IReadOnlyList<InstancePatch> Instances { get; init; } = [];

    /// <summary>Pristine instance arrays of the components named in <see cref="Instances"/> (one per component).</summary>
    public IReadOnlyList<InstanceArrayHint> InstanceHints { get; init; } = [];

    /// <summary>Actors to create by copying existing actors of this level.</summary>
    public IReadOnlyList<ActorCopy> Copies { get; init; } = [];

    /// <summary>New <c>StaticMeshActor</c>s to create from a mesh path.</summary>
    public IReadOnlyList<StaticMeshActorAdd> StaticMeshAdds { get; init; } = [];

    /// <summary>Actors to create by copying actors of other level packages (Blueprint buildings placed from elsewhere).</summary>
    public IReadOnlyList<ForeignActorCopy> ForeignCopies { get; init; } = [];

    /// <summary>Spline mesh pieces to reshape.</summary>
    public IReadOnlyList<SplinePatch> SplinePatches { get; init; } = [];

    /// <summary>True when the request changes nothing.</summary>
    public bool IsEmpty => DeleteActors.Count == 0 && Transforms.Count == 0 && Instances.Count == 0 && Copies.Count == 0 && StaticMeshAdds.Count == 0
        && ForeignCopies.Count == 0 && SplinePatches.Count == 0;
}

/// <summary>What <see cref="LevelPackageEditor.Apply"/> did to a level package.</summary>
public sealed record LevelEditReport
{
    /// <summary>Object name of the edited <c>ULevel</c> export (normally <c>PersistentLevel</c>).</summary>
    public required string LevelExport { get; init; }

    /// <summary>Entries of the <c>Actors</c> array before the edit (null entries included).</summary>
    public required int ActorsBefore { get; init; }

    /// <summary>Entries of the <c>Actors</c> array after the edit.</summary>
    public required int ActorsAfter { get; init; }

    /// <summary>Actors removed from the list, in list order.</summary>
    public required IReadOnlyList<string> RemovedActors { get; init; }

    /// <summary>Components whose transform was written, as <c>Actor.Component</c>.</summary>
    public required IReadOnlyList<string> PatchedTransforms { get; init; }

    /// <summary>Names appended to the package name table (for inserted property tags).</summary>
    public required IReadOnlyList<string> AddedNames { get; init; }

    /// <summary>Actors created by copying (their new names).</summary>
    public IReadOnlyList<string> AddedActors { get; init; } = [];

    /// <summary>ISM/HISM instances collapsed in place.</summary>
    public int DeletedInstances { get; init; }

    /// <summary>ISM/HISM instances moved.</summary>
    public int MovedInstances { get; init; }

    /// <summary>Requested changes that could not be applied, with the reason.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Rewrites a cooked level package (<c>.umap</c> + <c>.uexp</c>) with actors removed and transforms changed, keeping
/// everything else byte-identical. Deleting an actor removes its entry from the <c>ULevel::Actors</c> array in the
/// level export's native data; the actor's own exports stay in the package (never registered with the world, they are
/// garbage collected after load), so no package index has to be remapped and the header layout is unchanged. A transform
/// is written into the component's <c>RelativeLocation</c>/<c>RelativeRotation</c>/<c>RelativeScale3D</c> tags in place
/// when they exist and inserted before the <c>None</c> terminator otherwise (adding the property and struct names to the
/// name table when missing). The package is then rebuilt with <see cref="PackageWriter"/>, which recomputes export sizes
/// and offsets.
/// </summary>
public static partial class LevelPackageEditor
{
    private const string LevelClassName = "Level";
    private const string PersistentLevelName = "PersistentLevel";
    private const string RootComponentProperty = "RootComponent";
    private const string StructPropertyType = "StructProperty";
    private const int VectorSize = 12;

    /// <summary>Property name and struct of the three relative transform members.</summary>
    private static readonly (string Property, string Struct)[] TransformProperties =
    [
        ("RelativeLocation", "Vector"),
        ("RelativeRotation", "Rotator"),
        ("RelativeScale3D", "Vector"),
    ];

    /// <summary>Applies <paramref name="request"/> to <paramref name="package"/> and returns the rebuilt package bytes.</summary>
    /// <exception cref="InvalidDataException">The package has no <c>Level</c> export or its native layout is not the expected one.</exception>
    public static (PackageBytes Bytes, LevelEditReport Report) Apply(CookedPackage package, LevelEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(request);

        var levelIndex = FindLevelExport(package);
        var names = package.Names.ToList();
        var wide = package.NameEntries.Select(n => n.IsWide).ToList();
        var exports = package.Exports.ToList();
        var data = new List<ReadOnlyMemory<byte>>(package.Exports.Count + 8);
        for (var i = 0; i < package.Exports.Count; i++)
        {
            data.Add(package.GetExportData(i));
        }

        var preload = package.ReadPreloadDependencies().ToList();
        var warnings = new List<string>();
        var addedNames = new List<string>();

        // New actors first (copies, then new static mesh actors): they append exports/imports; the level export's
        // dependency groups are then re-emitted once with every new actor, and the actors are added to ULevel::Actors.
        var imports = package.Imports.ToList();
        var (addedIndices, addedActors) = CopyActors(package, levelIndex, request.Copies, exports, data, imports, names, wide, addedNames, preload, warnings);
        addedIndices.AddRange(AddStaticMeshActors(package, levelIndex, request.StaticMeshAdds, exports, data, imports, names, wide, addedNames, preload, addedActors, warnings));
        addedIndices.AddRange(ImportActors(package, levelIndex, request.ForeignCopies, exports, data, imports, names, wide, addedNames, preload, addedActors, warnings));
        RegisterActorsWithLevel(package, levelIndex, exports, preload, addedIndices);

        var actors = RemoveActors(package, levelIndex, request.DeleteActors, addedIndices, warnings);
        data[levelIndex] = actors.Payload;

        var patched = new List<string>();
        foreach (var patch in request.Transforms)
        {
            if (TryPatchTransform(package, levelIndex, patch, data, names, wide, addedNames, warnings) is { } label)
            {
                patched.Add(label);
            }
        }

        var (deletedInstances, movedInstances) = PatchInstances(package, levelIndex, request, data, warnings);
        foreach (var patch in request.SplinePatches)
        {
            if (TryPatchSpline(package, levelIndex, patch, data, names, wide, addedNames, warnings))
            {
                patched.Add($"{patch.Actor}.{patch.Component} (spline)");
            }
        }

        var bytes = PackageWriter.Build(new PackageBuildInput
        {
            Summary = package.Summary,
            Names = names,
            NameIsWide = wide,
            Imports = imports,
            Exports = exports,
            ExportData = data,
            PreloadDependencies = preload,
            AssetRegistryData = package.ReadAssetRegistryData(),
        });

        var report = new LevelEditReport
        {
            LevelExport = package.ResolveName(package.Exports[levelIndex].ObjectName),
            ActorsBefore = actors.Before,
            ActorsAfter = actors.After,
            RemovedActors = actors.Removed,
            PatchedTransforms = patched,
            AddedNames = addedNames,
            AddedActors = addedActors,
            DeletedInstances = deletedInstances,
            MovedInstances = movedInstances,
            Warnings = warnings,
        };
        return (bytes, report);
    }

    /// <summary>
    /// Reads the <c>Actors</c> array of the level export: (1-based export indices as stored, 0 = null entry) and the
    /// resolved actor names (null for null entries).
    /// </summary>
    /// <exception cref="InvalidDataException">The package has no <c>Level</c> export or its native layout is not the expected one.</exception>
    public static IReadOnlyList<(int PackageIndex, string? Name)> ReadActorList(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var levelIndex = FindLevelExport(package);
        var layout = ReadActorArray(package, levelIndex);
        return layout.Indices
            .Select(i => (i, i > 0 && i <= package.Exports.Count ? package.ResolveName(package.Exports[i - 1].ObjectName) : null))
            .ToList();
    }

    /// <summary>Index of the <c>ULevel</c> export (the one named <c>PersistentLevel</c> when there are several).</summary>
    /// <exception cref="InvalidDataException">No export of class <c>Level</c>.</exception>
    public static int FindLevelExport(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var first = -1;
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (!string.Equals(package.GetExportClassName(i), LevelClassName, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(package.ResolveName(package.Exports[i].ObjectName), PersistentLevelName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            if (first < 0)
            {
                first = i;
            }
        }

        return first >= 0 ? first : throw new InvalidDataException($"{package.BasePath ?? "The package"} has no Level export (not a level package).");
    }

    private static (ReadOnlyMemory<byte> Payload, int Before, int After, IReadOnlyList<string> Removed) RemoveActors(
        CookedPackage package, int levelIndex, IReadOnlyCollection<string> deleteActors, IReadOnlyList<int> appendPackageIndices, List<string> warnings)
    {
        var layout = ReadActorArray(package, levelIndex);
        if (deleteActors.Count == 0 && appendPackageIndices.Count == 0)
        {
            return (package.GetExportData(levelIndex), layout.Indices.Length, layout.Indices.Length, []);
        }

        var delete = new HashSet<string>(deleteActors, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        var kept = new List<int>(layout.Indices.Length);
        foreach (var index in layout.Indices)
        {
            if (index > 0 && index <= package.Exports.Count)
            {
                var name = package.ResolveName(package.Exports[index - 1].ObjectName);
                if (delete.Contains(name))
                {
                    removed.Add(name);
                    found.Add(name);
                    continue;
                }
            }

            kept.Add(index);
        }

        foreach (var missing in deleteActors.Where(a => !found.Contains(a)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add(FindExport(package, missing, levelIndex + 1) >= 0
                ? $"Actor '{missing}' exists in the package but is not in the level's actor list; nothing to remove."
                : $"Actor '{missing}' was not found in the level; skipped.");
        }

        kept.AddRange(appendPackageIndices);
        var payload = layout.Payload;
        var w = new ByteWriter(payload.Length + 4 * appendPackageIndices.Count);
        w.Raw(payload.AsSpan(0, layout.CountOffset));
        w.I32(kept.Count);
        foreach (var index in kept)
        {
            w.I32(index);
        }

        w.Raw(payload.AsSpan(layout.TailOffset));
        return (w.ToArray(), layout.Indices.Length, kept.Count, removed);
    }

    /// <summary>
    /// Locates the <c>Actors</c> array in the level export: tagged properties, <c>bHasGuid</c> (+ guid), then the array.
    /// The <c>FURL</c> that follows must start with the protocol <c>unreal</c>, which guards against a layout mismatch.
    /// </summary>
    private static ActorArrayLayout ReadActorArray(CookedPackage package, int levelIndex)
    {
        var block = package.ReadProperties(levelIndex);
        var payload = package.GetExportBytes(levelIndex);
        var r = new ByteReader(payload) { Position = block.EndOffset };
        var hasGuid = r.I32();
        switch (hasGuid)
        {
            case 0:
                break;
            case 1:
                r.Skip(16);
                break;
            default:
                throw new InvalidDataException($"Unexpected UObject guid flag {hasGuid} after the Level export's properties.");
        }

        var countOffset = r.Position;
        var count = r.I32();
        if (count < 0 || count > package.Exports.Count || r.Remaining < 4L * count)
        {
            throw new InvalidDataException($"Implausible Actors count {count} in the Level export.");
        }

        var indices = new int[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = r.I32();
            if (indices[i] > package.Exports.Count || indices[i] < 0)
            {
                throw new InvalidDataException($"Actors[{i}] = {indices[i]} is not an export index.");
            }
        }

        var tailOffset = r.Position;
        string protocol;
        try
        {
            protocol = r.FString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            protocol = string.Empty;
        }

        if (!string.Equals(protocol, "unreal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Level export's native data does not match ULevel::Serialize (no FURL after the Actors array).");
        }

        return new ActorArrayLayout(payload, countOffset, indices, tailOffset);
    }

    private static string? TryPatchTransform(
        CookedPackage package, int levelIndex, TransformPatch patch, IList<ReadOnlyMemory<byte>> data,
        List<string> names, List<bool> wide, List<string> addedNames, List<string> warnings)
    {
        var actorIndex = FindExport(package, patch.Actor, levelIndex + 1);
        if (actorIndex < 0)
        {
            warnings.Add($"Actor '{patch.Actor}' was not found in the level; its transform was not written.");
            return null;
        }

        int componentIndex;
        if (patch.Component is null)
        {
            var actorProps = package.ReadProperties(actorIndex);
            if (actorProps.Find(RootComponentProperty)?.Value is ObjectValue { Index: > 0 } root && root.Index <= package.Exports.Count)
            {
                componentIndex = root.Index - 1;
            }
            else
            {
                warnings.Add($"Actor '{patch.Actor}' stores no RootComponent; give the component name explicitly.");
                return null;
            }
        }
        else
        {
            componentIndex = FindExport(package, patch.Component, actorIndex + 1);
            if (componentIndex < 0)
            {
                warnings.Add($"Component '{patch.Actor}.{patch.Component}' is not stored in the level package; its transform was not written.");
                return null;
            }
        }

        var componentName = package.ResolveName(package.Exports[componentIndex].ObjectName);
        var label = $"{patch.Actor}.{componentName}";
        var patched = PatchTransformPayload(data[componentIndex].ToArray(), package.ReadProperties(componentIndex), patch.Value, label, names, wide, addedNames, warnings);
        if (patched is null)
        {
            return null;
        }

        data[componentIndex] = patched;
        return label;
    }

    /// <summary>
    /// Writes a relative transform into a component payload: existing 12-byte struct tags are overwritten in place, missing
    /// ones are inserted before the <c>None</c> terminator. Returns the new payload, or null (with a warning) when the
    /// payload cannot take it.
    /// </summary>
    private static byte[]? PatchTransformPayload(
        byte[] payload, PropertyBlock block, TransformValue t, string label, List<string> names, List<bool> wide, List<string> addedNames, List<string> warnings)
    {
        (float X, float Y, float Z)[] values =
        [
            (t.Location.X, t.Location.Y, t.Location.Z),
            (t.Rotation.Pitch, t.Rotation.Yaw, t.Rotation.Roll),
            (t.Scale.X, t.Scale.Y, t.Scale.Z),
        ];

        var inserts = new List<int>();
        for (var i = 0; i < TransformProperties.Length; i++)
        {
            var tag = block.Find(TransformProperties[i].Property);
            if (tag is null)
            {
                inserts.Add(i);
                continue;
            }

            if (tag.Type != StructPropertyType || tag.Size != VectorSize)
            {
                warnings.Add($"'{label}.{tag.Name}' is {tag.TypeLabel} (size {tag.Size}); expected a 12-byte struct. Transform not written.");
                return null;
            }

            WriteFloats(payload.AsSpan(tag.ValueOffset, VectorSize), values[i]);
        }

        if (inserts.Count == 0)
        {
            return payload;
        }

        // Insert the missing tags before the terminating "None" (8 bytes: FName index + number) that ends the tagged block.
        var terminator = block.EndOffset - 8;
        if (terminator < 0 || terminator > payload.Length)
        {
            warnings.Add($"'{label}' has no tagged property block to extend; transform not written.");
            return null;
        }

        var w = new ByteWriter(payload.Length + inserts.Count * 61);
        w.Raw(payload.AsSpan(0, terminator));
        foreach (var i in inserts)
        {
            var (property, structName) = TransformProperties[i];
            w.FName(GetOrAddName(names, wide, addedNames, property));
            w.FName(GetOrAddName(names, wide, addedNames, StructPropertyType));
            w.I32(VectorSize);
            w.I32(0); // ArrayIndex
            w.FName(GetOrAddName(names, wide, addedNames, structName));
            w.Guid(default); // StructGuid
            w.U8(0); // HasPropertyGuid
            w.F32(values[i].X);
            w.F32(values[i].Y);
            w.F32(values[i].Z);
        }

        w.Raw(payload.AsSpan(terminator));
        return w.ToArray();
    }

    /// <summary>Scale written into a collapsed ("deleted") instance: invisible, no usable collision, never zero (no NaN normals).</summary>
    public const float CollapsedInstanceScale = 0.0001f;

    private const int MatrixSize = 64;

    private static (int Deleted, int Moved) PatchInstances(
        CookedPackage package, int levelIndex, LevelEditRequest request, IList<ReadOnlyMemory<byte>> data, List<string> warnings)
    {
        if (request.Instances.Count == 0)
        {
            return (0, 0);
        }

        var deleted = 0;
        var moved = 0;
        var hints = request.InstanceHints.ToDictionary(h => (h.Actor, h.Component), h => h, ActorComponentComparer.Instance);
        foreach (var group in request.Instances.GroupBy(i => (i.Actor, i.Component), ActorComponentComparer.Instance))
        {
            var (actorName, componentName) = group.Key;
            var label = $"{actorName}.{componentName}";
            if (!hints.TryGetValue(group.Key, out var hint))
            {
                warnings.Add($"'{label}': no pristine instance list was given; its instance edits were not written.");
                continue;
            }

            var actorIndex = FindExport(package, actorName, levelIndex + 1);
            var componentIndex = actorIndex < 0 ? -1 : FindExport(package, componentName, actorIndex + 1);
            if (componentIndex < 0)
            {
                warnings.Add($"Component '{label}' is not stored in the level package; its instance edits were not written.");
                continue;
            }

            var payload = data[componentIndex].ToArray();
            var block = package.ReadProperties(componentIndex);
            var location = FindInstanceArray(payload, block.EndOffset, hint.Instances);
            if (location is null)
            {
                warnings.Add($"'{label}': PerInstanceSMData with {hint.Instances.Count} instance(s) was not found where expected; its instance edits were not written.");
                continue;
            }

            var (dataOffset, elementSize) = location.Value;
            foreach (var patch in group)
            {
                if (patch.Index < 0 || patch.Index >= hint.Instances.Count)
                {
                    warnings.Add($"'{label}[{patch.Index}]': index outside the {hint.Instances.Count} stored instance(s); skipped.");
                    continue;
                }

                var pristine = hint.Instances[patch.Index];
                var transform = patch.Local ?? new FTransform(pristine.Rotation, pristine.Translation, new FVector(CollapsedInstanceScale));
                WriteMatrix(payload.AsSpan(dataOffset + patch.Index * elementSize, MatrixSize), transform.ToMatrixWithScale());
                if (patch.Local is null)
                {
                    deleted++;
                }
                else
                {
                    moved++;
                }
            }

            data[componentIndex] = payload;
        }

        return (deleted, moved);
    }

    /// <summary>
    /// Locates the bulk-serialized <c>PerInstanceSMData</c> (element size, count, then <c>count × FMatrix</c>) in a
    /// component payload after its tagged properties: the candidate must announce <paramref name="known"/>.Count elements
    /// of 64 (UE 4.22+) or 80 bytes and its first matrices must decompose to the known transforms.
    /// </summary>
    internal static (int DataOffset, int ElementSize)? FindInstanceArray(byte[] payload, int propertiesEnd, IReadOnlyList<FTransform> known)
    {
        if (known.Count == 0)
        {
            return null;
        }

        var span = payload.AsSpan();
        for (var pos = Math.Max(0, propertiesEnd); pos + 8 <= payload.Length; pos++)
        {
            var elementSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span[pos..]);
            if (elementSize is not (MatrixSize or 80))
            {
                continue;
            }

            var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span[(pos + 4)..]);
            if (count != known.Count || pos + 8 + (long)elementSize * count > payload.Length)
            {
                continue;
            }

            var dataOffset = pos + 8;
            var verified = true;
            for (var i = 0; i < Math.Min(count, 4) && verified; i++)
            {
                var decoded = FTransform.FromMatrix(ReadMatrix(span.Slice(dataOffset + i * elementSize, MatrixSize)));
                verified = NearlyEqual(decoded, known[i]);
            }

            if (verified)
            {
                return (dataOffset, elementSize);
            }
        }

        return null;
    }

    private static bool NearlyEqual(FTransform a, FTransform b)
    {
        static bool Close(FVector x, FVector y, float tolerance) =>
            MathF.Abs(x.X - y.X) <= tolerance && MathF.Abs(x.Y - y.Y) <= tolerance && MathF.Abs(x.Z - y.Z) <= tolerance;

        var dot = a.Rotation.X * b.Rotation.X + a.Rotation.Y * b.Rotation.Y + a.Rotation.Z * b.Rotation.Z + a.Rotation.W * b.Rotation.W;
        return Close(a.Translation, b.Translation, 0.5f) && Close(a.Scale3D, b.Scale3D, 0.01f) && MathF.Abs(dot) > 0.999f;
    }

    private static Matrix4x4 ReadMatrix(ReadOnlySpan<byte> bytes)
    {
        Span<float> f = stackalloc float[16];
        for (var i = 0; i < 16; i++)
        {
            f[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
        }

        return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
    }

    private static void WriteMatrix(Span<byte> target, Matrix4x4 m)
    {
        ReadOnlySpan<float> f = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
        for (var i = 0; i < 16; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(target[(i * 4)..], f[i]);
        }
    }

    private sealed class ActorComponentComparer : IEqualityComparer<(string Actor, string Component)>
    {
        public static readonly ActorComponentComparer Instance = new();

        public bool Equals((string Actor, string Component) x, (string Actor, string Component) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Actor, y.Actor) && StringComparer.OrdinalIgnoreCase.Equals(x.Component, y.Component);

        public int GetHashCode((string Actor, string Component) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Actor), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Component));
    }

    private static void WriteFloats(Span<byte> target, (float X, float Y, float Z) value)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(target, value.X);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(target[4..], value.Y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(target[8..], value.Z);
    }

    /// <summary>
    /// The name <paramref name="value"/> as the engine stores it: a trailing <c>_N</c> is the FName number, not part of the
    /// string (<c>ConcreteBlock002_1</c> is "ConcreteBlock002" number 2, as the cooker wrote it). Written as plain text it
    /// is a different FName: a mesh import then did not match the level's own import of the same package, a second one was
    /// added, and the game loaded the package twice and crashed ("was reloaded before it even closed the linker").
    /// </summary>
    private static FNameRef GetOrAddName(List<string> names, List<bool> wide, List<string> added, string value)
    {
        var (text, number) = SplitNumber(value);
        var index = names.FindIndex(n => string.Equals(n, text, StringComparison.Ordinal));
        if (index < 0)
        {
            index = names.Count;
            names.Add(text);
            wide.Add(!ByteWriter.IsAscii(text));
            added.Add(text);
        }

        return new FNameRef(index, number);
    }

    /// <summary>
    /// <c>FName</c>'s split of a trailing number (UE 4.27 <c>ParseNumber</c>): "_" then up to 10 digits, not starting with 0
    /// unless it is the only digit, below int.MaxValue; the stored number is that value + 1 (0 = no number).
    /// </summary>
    internal static (string Text, int Number) SplitNumber(string value)
    {
        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[value.Length - 1 - digits]))
        {
            digits++;
        }

        var first = value.Length - digits;
        if (digits == 0 || digits >= value.Length || digits > 10 || value[first - 1] != '_' || (digits > 1 && value[first] == '0')
            || !long.TryParse(value.AsSpan(first), out var n) || n >= int.MaxValue)
        {
            return (value, 0);
        }

        return (value[..(first - 1)], (int)n + 1);
    }

    /// <summary>Export index of the export named <paramref name="name"/> whose outer is <paramref name="outerPackageIndex"/>, or -1.</summary>
    private static int FindExport(CookedPackage package, string name, int outerPackageIndex)
    {
        for (var i = 0; i < package.Exports.Count; i++)
        {
            var e = package.Exports[i];
            if (e.OuterIndex == outerPackageIndex && string.Equals(package.ResolveName(e.ObjectName), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record ActorArrayLayout(byte[] Payload, int CountOffset, int[] Indices, int TailOffset);
}
