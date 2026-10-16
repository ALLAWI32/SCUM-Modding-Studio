using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Level.Export;

/// <summary>
/// A copy's own keys. UE gives a moved or duplicated light a new <c>LightGuid</c> and a moved or duplicated mesh new
/// <c>LODData</c> <c>MapBuildDataId</c>s (<c>ULightComponent</c> / <c>UStaticMeshComponent::InvalidateLightingCacheDetailed</c>):
/// both name the component's baked data in its level's <c>MapBuildData</c>. With its source's key a copy in the same level
/// reads the source's shadow map or lightmap, baked for the source's place (a static light's copy then gives no light at
/// all: the renderer only draws unbuilt static lights itself, <c>FLightSceneInfo::ShouldRenderLightViewIndependent</c>), and
/// a copy in another level shares its key with its source in one world. With a key of its own the copy is unbuilt, as in
/// the editor after a move. An object's lazy pointer guid (<c>FLazyObjectPtr</c>, after the tags) is a key too. Every key
/// is derived from the level, the copy's name and the member, so an export is repeatable.
/// </summary>
public static partial class LevelPackageEditor
{
    /// <summary>Classes whose native data is <c>UStaticMeshComponent</c>'s <c>LODData</c> and nothing after it.</summary>
    private static readonly HashSet<string> MeshLodClasses = new(StringComparer.Ordinal) { "StaticMeshComponent", "SplineMeshComponent", "InteriorStaticMeshComponent" };

    private const byte StripEditor = 1;
    private const byte StripServer = 2;
    private const byte StripOverrideColors = 1; // FStaticMeshComponentLODInfo's class strip flag

    /// <summary>Writes new keys (see the class remarks) into a copied member's <paramref name="payload"/>, in place.</summary>
    /// <param name="payload">The member's payload (tag offsets as in <paramref name="block"/>).</param>
    /// <param name="block">The member's tags.</param>
    /// <param name="className">The member's class (short name).</param>
    /// <param name="level">The edited level (its path), part of every derived key.</param>
    /// <param name="copyName">The copy's actor name.</param>
    /// <param name="member">The member's position in the copy.</param>
    internal static void FreshKeys(byte[] payload, PropertyBlock block, string className, string? level, string copyName, int member)
    {
        if (block.Find("LightGuid")?.Value is GuidValue { Offset: >= 0 } light && light.Offset + 16 <= payload.Length)
        {
            Renew(payload, light.Offset, level, copyName, member, "LightGuid");
        }

        // UObject::Serialize after the tags: the lazy pointer guid flag (UBOOL), and the guid when it is set.
        var tail = block.EndOffset;
        if (tail < 0 || tail + 4 > payload.Length)
        {
            return;
        }

        var hasGuid = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(tail));
        if (hasGuid == 1 && tail + 20 <= payload.Length)
        {
            Renew(payload, tail + 4, level, copyName, member, "ObjectGuid");
        }
        else if (hasGuid != 0)
        {
            return;
        }

        if (MeshLodClasses.Contains(className) && MapBuildDataIds(payload, tail + (hasGuid == 1 ? 20 : 4)) is { } ids)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                Renew(payload, ids[i], level, copyName, member, $"MapBuildDataId{i}");
            }
        }
    }

    /// <summary>
    /// The offsets of the <c>MapBuildDataId</c>s in a cooked <c>LODData</c> starting at <paramref name="start"/> (count, then per
    /// LOD: <c>FStripDataFlags</c>, the guid unless stripped for the server, the override colours unless stripped), or null
    /// when the data does not end exactly with the payload (another layout: nothing is written then).
    /// </summary>
    internal static List<int>? MapBuildDataIds(byte[] payload, int start)
    {
        var r = new BulkCursor(payload, start);
        var count = r.I32();
        if (count is < 0 or > 16)
        {
            return null;
        }

        var ids = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var global = r.U8();
            var classFlags = r.U8();
            if ((global & StripEditor) == 0)
            {
                return null; // editor data (painted vertices) is never in a cooked level
            }

            if ((global & StripServer) == 0)
            {
                ids.Add(r.Position);
                r.Skip(16);
            }

            if ((classFlags & StripOverrideColors) == 0 && r.U8() != 0 && !SkipColorBuffer(r))
            {
                return null;
            }
        }

        return r.AtEnd ? ids : null;
    }

    /// <summary>FColorVertexBuffer::Serialize: strip flags, stride, vertex count, then the colours (element size, count, data).</summary>
    private static bool SkipColorBuffer(BulkCursor r)
    {
        var global = r.U8();
        r.U8();
        var stride = r.I32();
        var vertices = r.I32();
        if (stride != 4 || vertices < 0)
        {
            return false;
        }

        if (vertices == 0 || (global & StripServer) != 0)
        {
            return r.Skip(0);
        }

        var elementSize = r.I32();
        var elements = r.I32();
        return elementSize == 4 && elements == vertices && r.Skip(4L * elements);
    }

    /// <summary>
    /// Writes a key derived from the level, the copy, the member and what it keys (MD5, as <see cref="FreshGameUniqueId"/>) over
    /// the 16 bytes at <paramref name="offset"/>; a zero guid (no key: no build data) stays zero.
    /// </summary>
    private static void Renew(byte[] payload, int offset, string? level, string copyName, int member, string what)
    {
        if (payload.AsSpan(offset, 16).IndexOfAnyExcept((byte)0) >= 0)
        {
            DerivedGuid(level, copyName, member, what).CopyTo(payload.AsSpan(offset));
        }
    }

    /// <summary>A guid derived from the level, the copy, the member and what it keys.</summary>
    private static byte[] DerivedGuid(string? level, string copyName, int member, string what) =>
        MD5.HashData(Encoding.UTF8.GetBytes($"{level}|{copyName}|{member}|{what}"));
}
