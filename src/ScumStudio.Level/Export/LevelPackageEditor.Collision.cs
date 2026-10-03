using System.Security.Cryptography;
using System.Text;
using ScumStudio.Assets.Meshes;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;

namespace ScumStudio.Level.Export;

/// <summary>
/// Collision for a spline mesh piece the mod adds: a <c>BodySetup</c> subobject of its <c>SplineMeshComponent</c> holding
/// only boxes (see <see cref="Model.PieceCollision"/>). A cooked game never builds collision for a spline mesh itself; it
/// reads the editor's. Boxes are created straight from <c>AggGeom</c> with no cooked data, and the body says so
/// (<c>bHasCookedCollisionData</c> false, <c>bNeverNeedsCookedCollisionData</c> true), so nothing is cooked at load.
/// Laid out like the BodySetup of a road piece in a cooked landscape tile: tagged properties, no object guid, the body
/// guid, <c>bCooked</c>.
/// </summary>
public static partial class LevelPackageEditor
{
    private const string BodySetupClass = "BodySetup";
    private const uint BodySetupFlags = 0x8; // RF_Transactional, as the cooker writes it

    /// <summary>The serialized BodySetup made of <paramref name="boxes"/> (component space).</summary>
    private static byte[] BoxBodySetup(Func<string, FNameRef> name, IReadOnlyList<CollisionBox> boxes, string seed)
    {
        var elements = new ByteWriter(boxes.Count * 160);
        foreach (var box in boxes)
        {
            WriteStructTag(elements, name, "Center", "Vector", box.Center.X, box.Center.Y, box.Center.Z);
            WriteStructTag(elements, name, "Rotation", "Rotator", box.Rotation.Pitch, box.Rotation.Yaw, box.Rotation.Roll);
            WriteFloatTag(elements, name, "X", box.Size.X);
            WriteFloatTag(elements, name, "Y", box.Size.Y);
            WriteFloatTag(elements, name, "Z", box.Size.Z);
            elements.FName(name("None"));
        }

        // TArray<FKBoxElem>: the count, one tag for the element struct, the elements.
        var array = new ByteWriter(elements.Length + 64);
        array.I32(boxes.Count);
        array.FName(name("BoxElems"));
        array.FName(name(StructPropertyType));
        array.I32(elements.Length);
        array.I32(0);
        array.FName(name("KBoxElem"));
        array.Guid(default);
        array.U8(0);
        array.Raw(elements.WrittenSpan);

        var geom = new ByteWriter(array.Length + 64);
        geom.FName(name("BoxElems"));
        geom.FName(name("ArrayProperty"));
        geom.I32(array.Length);
        geom.I32(0);
        geom.FName(name(StructPropertyType));
        geom.U8(0);
        geom.Raw(array.WrittenSpan);
        geom.FName(name("None"));

        var w = new ByteWriter(geom.Length + 200);
        w.FName(name("AggGeom"));
        w.FName(name(StructPropertyType));
        w.I32(geom.Length);
        w.I32(0);
        w.FName(name("KAggregateGeom"));
        w.Guid(default);
        w.U8(0);
        w.Raw(geom.WrittenSpan);

        // Queries against the complex shape use the boxes too; no triangle mesh is ever looked for.
        w.FName(name("CollisionTraceFlag"));
        w.FName(name("ByteProperty"));
        w.I32(8);
        w.I32(0);
        w.FName(name("ECollisionTraceFlag"));
        w.U8(0);
        w.FName(name("CTF_UseSimpleAsComplex"));
        WriteBoolTag(w, name, "bHasCookedCollisionData", false);
        WriteBoolTag(w, name, "bNeverNeedsCookedCollisionData", true);
        w.FName(name("None"));
        w.I32(0); // no object guid
        w.Guid(GuidFor(seed)); // BodySetupGuid: the same piece gets the same guid in every export
        w.I32(0); // bCooked: no cooked data follows
        return w.ToArray();
    }

    private static void WriteBoolTag(ByteWriter w, Func<string, FNameRef> name, string property, bool value)
    {
        w.FName(name(property));
        w.FName(name("BoolProperty"));
        w.I32(0);
        w.I32(0);
        w.U8(value ? (byte)1 : (byte)0);
        w.U8(0);
    }

    /// <summary>A guid derived from <paramref name="seed"/> (stable across exports, never zero).</summary>
    private static FGuid GuidFor(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var guid = new FGuid(BitConverter.ToUInt32(hash, 0), BitConverter.ToUInt32(hash, 4), BitConverter.ToUInt32(hash, 8), BitConverter.ToUInt32(hash, 12));
        return guid.IsZero ? new FGuid(1, 0, 0, 0) : guid;
    }
}
