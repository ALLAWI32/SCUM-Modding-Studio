using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Export;

/// <summary>Why a copy is refused (see <see cref="LevelPackageEditor.CheckCopy(CookedPackage, string, bool)"/>).</summary>
public enum CopyRefusalReason
{
    /// <summary>
    /// A member whose native data (after its tags) the writer cannot carry into another level: a brush's model or polygon
    /// set, a body of an unknown layout, a foliage actor or a landscape component (they hold the source level's object indices
    /// there).
    /// </summary>
    NativeData,

    /// <summary>A stored value the reader could not decode, so the writer cannot re-index it for another level.</summary>
    UnreadableValue,
}

/// <summary>A refused copy: the reason, the member (export) that causes it and its class.</summary>
public sealed record CopyRefusal(CopyRefusalReason Reason, string Member, string ClassName)
{
    /// <summary>One English line for the export report.</summary>
    public string Describe() => Reason switch
    {
        CopyRefusalReason.NativeData => $"{Member} ({ClassName}) holds native data the studio cannot carry into another level",
        _ => $"{Member} ({ClassName}) stores a value the studio cannot read, so it cannot be carried into another level",
    };
}

/// <summary>
/// The copy guard (D15, D04; audit R2 B3): a copy into another level whose members the writer cannot carry is refused
/// instead of written into a level the game cannot load ("corrupted data", seviscache; Samobor, thezombie_97). Refused:
/// brushes' models and polygon sets, bodies of another layout than the cooked one the writer re-indexes, foliage actors and
/// landscape components with native data, and values the reader could not decode. Lights, Cascade and Niagara effects and
/// sounds are copied: every value they store is decoded (a Niagara component's user parameters too, which the reader
/// could not read before and the copier then carried with the source level's names and object indices), each copy gets
/// keys of its own (<c>LevelPackageEditor.CopyKeys.cs</c>) and its level as its outer's dependency. What else is
/// re-indexed: a body's cooked format names, field path owners and string table ids.
/// </summary>
public static partial class LevelPackageEditor
{
    private const uint BulkPayloadAtEndOfFile = 0x1;
    private const uint BulkPayloadInSeparateFile = 0x100;
    private const uint BulkSize64Bit = 0x2000;
    private const uint BulkBadDataVersion = 0x8000;

    /// <summary>Classes whose native data holds names or object indices the copier does not re-index (of a body only its cooked format names are, <see cref="BodySetupFormatNames"/>).</summary>
    private static readonly HashSet<string> NativeDataClasses = new(StringComparer.Ordinal)
    {
        "BrushComponent", "Model", "Polys", BodySetupClass,
        "InstancedFoliageActor", // FoliageInfos, keyed by foliage types
        "LandscapeComponent", "LandscapeHeightfieldCollisionComponent", "LandscapeMeshCollisionComponent", // materials, physical materials
    };

    /// <summary>
    /// The Map's check before a paste into another level (the level's package is not at hand there): a volume among
    /// <paramref name="actor"/> and, through <paramref name="level"/>, its stored child actors, whose brush shape holds native
    /// data the copier cannot carry into another level (the export refuses it, <see cref="CheckCopy(CookedPackage, string, bool)"/>);
    /// null otherwise and always for a copy inside its own level.
    /// </summary>
    public static CopyRefusal? RefusedCopy(ActorRecord actor, LevelDocument? level, bool otherLevel)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!otherLevel)
        {
            return null;
        }

        var seen = new HashSet<int>();
        var queue = new Queue<ActorRecord>([actor]);
        while (queue.TryDequeue(out var next))
        {
            if (!seen.Add(next.ExportIndex))
            {
                continue;
            }

            if (next.Components.FirstOrDefault(c => !c.IsSynthesized && c.ClassName == "BrushComponent") is { } brush)
            {
                return new CopyRefusal(CopyRefusalReason.NativeData, brush.Name, brush.ClassName);
            }

            foreach (var component in next.Components.Where(c => !c.IsSynthesized))
            {
                if (component.ChildActor is { } child && level?.FindActor(child) is { } childActor)
                {
                    queue.Enqueue(childActor);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Why copying <paramref name="actor"/> of <paramref name="source"/> (with its components and stored child actors) would
    /// write a broken level, or null when it can be copied. <paramref name="otherLevel"/>: the copy goes into another level
    /// (the stricter rules).
    /// </summary>
    public static CopyRefusal? CheckCopy(CookedPackage source, string actor, bool otherLevel)
    {
        ArgumentNullException.ThrowIfNull(source);
        var level = FindLevelExport(source);
        var index = FindExport(source, actor, level + 1);
        if (index < 0)
        {
            return null;
        }

        var members = CollectMembers(source, index);
        var set = new HashSet<int>(members);
        for (var k = 0; k < members.Count; k++)
        {
            foreach (var reference in ObjectReferences(source.ReadProperties(members[k]).Properties))
            {
                if (reference.Index > 0 && !set.Contains(reference.Index - 1) && IsActorOfLevel(source, reference.Index - 1, level + 1))
                {
                    members.AddRange(CollectMembers(source, reference.Index - 1).Where(set.Add));
                }
            }
        }

        return CheckMembers(source, members, otherLevel);
    }

    /// <summary>
    /// Test seam: inside the returned scope (this async flow only) the copier writes what the guard would refuse, so a test
    /// can show what the guard stops. The app never opens it.
    /// </summary>
    internal static IDisposable WithoutCopyGuard()
    {
        GuardOff.Value = true;
        return new GuardScope();
    }

    private static readonly AsyncLocal<bool> GuardOff = new();

    private sealed class GuardScope : IDisposable
    {
        public void Dispose() => GuardOff.Value = false;
    }

    /// <summary>The first member of a copy that makes it refused (see the class remarks), or null.</summary>
    private static CopyRefusal? CheckMembers(CookedPackage source, IReadOnlyList<int> members, bool otherLevel)
    {
        if (GuardOff.Value)
        {
            return null;
        }

        if (!otherLevel)
        {
            return null; // the same package: every name and index of the copied bytes still means the same object
        }

        foreach (var member in members)
        {
            var className = source.GetExportClassName(member);
            var name = source.ResolveName(source.Exports[member].ObjectName);
            var payload = source.GetExportBytes(member);
            var block = PropertyReader.ReadPayload(source, payload, member);
            if (AllValues(block.Properties).Any(v => v is RawValue || v is TextValue { HistoryType: not (-1 or 0 or 11) }))
            {
                return new CopyRefusal(CopyRefusalReason.UnreadableValue, name, className);
            }

            if (NativeDataClasses.Contains(className) && !IsPlainTail(payload, block.EndOffset)
                && !(className == BodySetupClass && BodySetupFormatNames(payload, block.EndOffset) is not null))
            {
                return new CopyRefusal(CopyRefusalReason.NativeData, name, className);
            }
        }

        return null;
    }

    /// <summary>Native data that is only UObject's object guid flag (0, or 1 and the guid), or nothing.</summary>
    private static bool IsPlainTail(byte[] payload, int start)
    {
        var length = payload.Length - start;
        return length == 0
            || (length == 4 && BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(start)) == 0)
            || (length == 20 && BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(start)) == 1);
    }

    /// <summary>
    /// The offsets of the format names (<c>PhysXPC</c> …) in a cooked <c>BodySetup</c>'s native data: the object guid flag,
    /// <c>BodySetupGuid</c>, <c>bCooked</c> and, when cooked, the <c>FFormatContainer</c> (count, then per format its FName
    /// and an inline bulk payload). Null when the data is laid out any other way (a payload elsewhere in the file, an
    /// unknown flag, a length that does not add up).
    /// </summary>
    private static List<int>? BodySetupFormatNames(byte[] payload, int start)
    {
        var r = new BulkCursor(payload, start);
        var hasGuid = r.I32();
        if (hasGuid is not (0 or 1) || (hasGuid == 1 && !r.Skip(16)) || !r.Skip(16))
        {
            return null;
        }

        var names = new List<int>();
        var cooked = r.I32();
        if (cooked == 0)
        {
            return r.AtEnd ? names : null;
        }

        var count = r.I32();
        if (cooked != 1 || count is < 0 or > 16)
        {
            return null;
        }

        for (var i = 0; i < count; i++)
        {
            names.Add(r.Position);
            if (!r.Skip(8))
            {
                return null;
            }

            var flags = (uint)r.I32();
            if ((flags & (BulkPayloadAtEndOfFile | BulkPayloadInSeparateFile | BulkBadDataVersion)) != 0)
            {
                return null; // the payload lies elsewhere in the source file: its offset means nothing in another level
            }

            var wide = (flags & BulkSize64Bit) != 0;
            _ = wide ? r.I64() : r.I32(); // element count
            var size = wide ? r.I64() : r.I32();
            _ = r.I64(); // offset in file (an inline payload is read where it stands)
            if (size < 0 || !r.Skip(size))
            {
                return null;
            }
        }

        return r.AtEnd ? names : null;
    }

    /// <summary>Every value of a tag list, nested ones (struct members, array, set and map items) included.</summary>
    private static IEnumerable<PropertyValue> AllValues(IEnumerable<PropertyTag> tags) => tags.SelectMany(t => AllValues(t.Value));

    private static IEnumerable<PropertyValue> AllValues(PropertyValue value)
    {
        yield return value;
        var inner = value switch
        {
            StructValue s => s.Properties.SelectMany(t => AllValues(t.Value)),
            ArrayValue a => a.Items.SelectMany(AllValues),
            SetValue set => set.Removed.Concat(set.Items).SelectMany(AllValues),
            MapValue map => map.RemovedKeys.SelectMany(AllValues).Concat(map.Entries.SelectMany(e => AllValues(e.Key).Concat(AllValues(e.Value)))),
            PerPlatformValue p => AllValues(p.Default),
            NativeStructValue n => n.Fields.SelectMany(f => AllValues(f.Value)),
            _ => [],
        };
        foreach (var v in inner)
        {
            yield return v;
        }
    }

    /// <summary>The owner (an FPackageIndex after the path names) of each field path value of a tag list, with its offset.</summary>
    private static IEnumerable<(int Offset, int Owner)> FieldPathOwners(IEnumerable<PropertyTag> tags) =>
        AllValues(tags).OfType<FieldPathValue>().Where(f => f.Offset >= 0).Select(f => (f.Offset + 4 + (8 * f.Path.Count), f.Owner));

    /// <summary>Reads the native data of a body without throwing: every read past the end leaves the cursor invalid.</summary>
    private sealed class BulkCursor(byte[] data, int position)
    {
        private bool _valid = position >= 0 && position <= data.Length;

        public int Position { get; private set; } = position;

        public bool AtEnd => _valid && Position == data.Length;

        public bool Skip(long count)
        {
            _valid &= count >= 0 && Position + count <= data.Length;
            if (_valid)
            {
                Position += (int)count;
            }

            return _valid;
        }

        public byte U8() => Skip(1) ? data[Position - 1] : (byte)0;

        public int I32() => Skip(4) ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Position - 4)) : 0;

        public long I64() => Skip(8) ? BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(Position - 8)) : 0;
    }
}
