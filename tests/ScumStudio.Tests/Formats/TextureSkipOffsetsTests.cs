using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Tests.Formats;

/// <summary>
/// defolt_0999's real Z_4 project (2026-10-16): clearing grass under placed objects grows a landscape tile's components,
/// which moves the tile's heightmap and weightmap textures; their cooked <c>SkipOffset</c>s are absolute file offsets and
/// kept naming the old place (the package check read 31 of them into other data). The writer now moves them along.
/// </summary>
public sealed class TextureSkipOffsetsTests
{
    private static readonly string[] Names = ["None", "PF_B8G8R8A8", "Texture2D", "Object", "/Script/Engine", "/Script/CoreUObject", "Class", "Package", "Tex", "Before"];

    [Fact]
    public void AMovedTextureTakesItsSkipOffsetAlong()
    {
        var payload = Texture(start: 1000);

        var moved = TextureSkipOffsets.Relocate(payload, Names, previousStart: 1000, newStart: 1300).ToArray();

        Assert.Equal(payload.Length, moved.Length);
        Assert.Equal(1300 + payload.Length - 8, Skip(moved));
        Assert.Equal(payload.AsSpan(0, 24).ToArray(), moved.AsSpan(0, 24).ToArray()); // nothing else changes
    }

    [Fact]
    public void ACopiedTextureIsFoundFromItsClosingName()
    {
        var payload = Texture(start: 5000);

        // The bytes came from another package (the previous start given is not theirs): the closing None still places them.
        var moved = TextureSkipOffsets.Relocate(payload, Names, previousStart: 0, newStart: 200).ToArray();

        Assert.Equal(200 + payload.Length - 8, Skip(moved));
    }

    [Fact]
    public void ATextureInPlaceAndAnythingElseAreLeftAlone()
    {
        var payload = Texture(start: 1000);
        Assert.True(TextureSkipOffsets.Relocate(payload, Names, 1000, 1000).Span.SequenceEqual(payload));

        var garbage = new byte[64];
        Assert.True(TextureSkipOffsets.Relocate(garbage, Names, 0, 100).Span.SequenceEqual(garbage));
    }

    [Fact]
    public void TheWriterMovesATextureBehindAnExportThatGrew()
    {
        var input = Package(beforeLength: 16);
        var first = PackageWriter.Build(input);
        var original = CookedPackage.Parse(first.UAsset, first.UExp);
        Assert.Equal(original.Exports[1].SerialOffset + original.Exports[1].SerialSize - 8, Skip(original.GetExportBytes(1)));

        // The export ahead of the texture grows by 40 bytes (as a landscape component whose grass was cleared).
        var grown = PackageWriter.ToBuildInput(original) with
        {
            ExportData = [new byte[56], original.GetExportData(1)],
        };
        var rewritten = CookedPackage.Parse(PackageWriter.Build(grown).UAsset, PackageWriter.Build(grown).UExp);

        Assert.Equal(original.Exports[1].SerialOffset + 40, rewritten.Exports[1].SerialOffset);
        Assert.Equal(rewritten.Exports[1].SerialOffset + rewritten.Exports[1].SerialSize - 8, Skip(rewritten.GetExportBytes(1)));
    }

    /// <summary>A cooked Texture2D payload as if it started at <paramref name="start"/>: tags, guid flag, strip flags, bCooked, one format, None.</summary>
    private static byte[] Texture(long start)
    {
        var w = new ByteWriter();
        w.FName(new FNameRef(0)); // no tagged properties
        w.I32(0); // bHasGuid
        w.U16(0); // FStripDataFlags (UTexture)
        w.U16(0); // FStripDataFlags (UTexture2D)
        w.I32(1); // bCooked
        w.FName(new FNameRef(1)); // PF_B8G8R8A8
        var skipAt = w.Length;
        w.I64(0);
        for (var k = 0; k < 12; k++)
        {
            w.I32(k + 1); // platform data stand-in
        }

        var end = w.Length;
        w.FName(new FNameRef(0)); // None
        var bytes = w.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(skipAt), start + end);
        return bytes;
    }

    /// <summary>The stored offset: after the None tag (8), bHasGuid (4), two strip flags (2 + 2), bCooked (4) and the format name (8).</summary>
    private static long Skip(byte[] payload) => BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(28));

    private static PackageBuildInput Package(int beforeLength)
    {
        var imports = new List<ImportEntry>
        {
            new(new FNameRef(5), new FNameRef(7), 0, new FNameRef(4)), // /Script/Engine
            new(new FNameRef(5), new FNameRef(6), -1, new FNameRef(2)), // Texture2D
            new(new FNameRef(5), new FNameRef(6), -1, new FNameRef(3)), // Object
        };
        var exports = new List<ExportEntry>
        {
            new() { ClassIndex = -3, ObjectName = new FNameRef(9), FirstExportDependency = -1 },
            new() { ClassIndex = -2, ObjectName = new FNameRef(8), FirstExportDependency = -1 },
        };
        var texture = Texture(start: 0);
        return new PackageBuildInput
        {
            Summary = new PackageSummary
            {
                LegacyFileVersion = -7,
                LegacyUE3Version = 864,
                FolderName = "None",
                PackageFlags = PackageSummary.PkgFilterEditorOnly,
                Guid = new FGuid(1, 2, 3, 4),
                SavedByEngineVersion = new EngineVersionInfo(0, 0, 0, 0, string.Empty),
                CompatibleWithEngineVersion = new EngineVersionInfo(0, 0, 0, 0, string.Empty),
                ChunkIds = [],
            },
            Names = Names,
            Imports = imports,
            Exports = exports,
            ExportData = [new byte[beforeLength], texture],
        };
    }
}
