using System.Buffers.Binary;
using System.Numerics;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Tests.Formats;

public sealed class PropertyReaderTests
{
    private static (CookedPackage Package, PropertyBlock Block) Load()
    {
        var bytes = new SyntheticPackage().BuildPackage();
        var pkg = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        return (pkg, pkg.ReadProperties(0));
    }

    [Fact]
    public void DecodesAllTagKinds()
    {
        var (_, block) = Load();
        Assert.Equal(0, block.CountRawValues());
        Assert.Equal(12, block.NativeDataLength); // bHasGuid + 8 native bytes

        Assert.Equal(75.5f, Assert.IsType<FloatValue>(block.Find("Health")!.Value).Value);
        Assert.Equal(-3, Assert.IsType<IntValue>(block.Find("Count")!.Value).Value);
        var hidden = block.Find("bHidden")!;
        Assert.True(hidden.BoolValue);
        Assert.Equal(0, hidden.Size);

        var loc = Assert.IsType<VectorValue>(block.Find("RelativeLocation")!.Value);
        Assert.Equal(new Vector3(100f, -200f, 300.25f), loc.ToVector3());
        var rot = Assert.IsType<RotatorValue>(block.Find("RelativeRotation")!.Value);
        Assert.Equal((10f, 90f, 0f), (rot.Pitch, rot.Yaw, rot.Roll));

        // Transform is a TAGGED struct in 4.27 (FORMAT_NOTES), not 10 raw floats.
        var xf = Assert.IsType<StructValue>(block.Find("Offset")!.Value);
        Assert.Equal("Transform", xf.StructName);
        Assert.Equal(new Vector3(1.25f, 1.4f, 1.15f), Assert.IsType<VectorValue>(xf.Find("Scale3D")!.Value).ToVector3());

        var points = Assert.IsType<ArrayValue>(block.Find("Points")!.Value);
        Assert.Equal("Vector", points.StructName);
        Assert.Equal(2, points.Items.Count);
        Assert.Equal(new Vector3(4, 5, 6), Assert.IsType<VectorValue>(points.Items[1]).ToVector3());

        Assert.Equal("EMyEnum::Two", Assert.IsType<EnumValue>(block.Find("Mode")!.Value).Value);
        Assert.Equal("Health_2", Assert.IsType<NameValue>(block.Find("Tag")!.Value).Value);
        var map = Assert.IsType<MapValue>(block.Find("Lookup")!.Value);
        Assert.Equal(2, map.Entries.Count);
        Assert.Equal(0.25f, Assert.IsType<FloatValue>(map.Entries[1].Value).Value);
        Assert.Equal("hello", Assert.IsType<StrValue>(block.Find("Label")!.Value).Value);
        Assert.Equal("IMP:Actor", Assert.IsType<ObjectValue>(block.Find("Owner")!.Value).Reference);
    }

    [Fact]
    public void OffsetsPointAtTheSerializedValues()
    {
        var (pkg, block) = Load();
        var payload = pkg.GetExportBytes(0);
        foreach (var (_, tag) in block.EnumerateAll())
        {
            if (tag.Value is FloatValue f)
            {
                Assert.Equal(f.Value, BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(tag.ValueOffset)));
            }

            if (tag.Value is VectorValue v)
            {
                Assert.Equal(v.X, BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(tag.ValueOffset)));
                Assert.Equal(12, v.Size);
                Assert.Equal(tag.ValueOffset, v.Offset);
            }

            Assert.Equal(tag.Size, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(tag.SizeFieldOffset)));
        }

        var points = (ArrayValue)block.Find("Points")!.Value;
        Assert.Equal(6f, BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(points.Items[1].Offset + 8)));
        Assert.Equal(1, payload[block.Find("bHidden")!.BoolValueOffset]);
    }

    [Fact]
    public void PathsCoverNestedMembers()
    {
        var (_, block) = Load();
        var paths = block.EnumerateAll().Select(x => x.Path).ToList();
        Assert.Contains("Offset.Translation", paths);
        Assert.Contains("Offset.Scale3D", paths);
        Assert.Same(block.GetByPath("Offset.Scale3D"), block.FindAll("Scale3D", "StructProperty").Single());
    }

    [Fact]
    public void SameSizePatchesAreVisibleAfterReRead()
    {
        var (pkg, block) = Load();
        var uexp = (byte[])pkg.UExp.Clone();
        PackagePatcher.SetFloat(uexp, block, block.Find("Health")!, 12.5f);
        PackagePatcher.SetVector(uexp, block, block.Find("RelativeLocation")!, new Vector3(7, 8, 9));
        PackagePatcher.SetBool(uexp, block, block.Find("bHidden")!, false);
        PackagePatcher.SetVector(uexp, block, (VectorValue)((ArrayValue)block.Find("Points")!.Value).Items[0], new Vector3(-1, -2, -3));
        PackagePatcher.SetVector(uexp, block, block.GetByPath("Offset.Translation")!, new Vector3(0, 0, 50));
        Assert.Throws<InvalidOperationException>(() => PackagePatcher.SetFloat(uexp, block, block.Find("Count")!, 1f));

        var patched = CookedPackage.Parse(pkg.UAsset, uexp).ReadProperties(0);
        Assert.Equal(12.5f, ((FloatValue)patched.Find("Health")!.Value).Value);
        Assert.Equal(new Vector3(7, 8, 9), ((VectorValue)patched.Find("RelativeLocation")!.Value).ToVector3());
        Assert.False(patched.Find("bHidden")!.BoolValue);
        Assert.Equal(new Vector3(-1, -2, -3), ((VectorValue)((ArrayValue)patched.Find("Points")!.Value).Items[0]).ToVector3());
        Assert.Equal(new Vector3(0, 0, 50), ((VectorValue)patched.GetByPath("Offset.Translation")!.Value).ToVector3());
        Assert.Equal(pkg.UExp.Length, uexp.Length);
    }

    [Fact]
    public void TagOffsetsPortMatchesModelOffsets()
    {
        var (pkg, block) = Load();
        var found = PackagePatcher.FindTagValueOffsets(pkg, 0, "Health", "FloatProperty");
        Assert.Equal([(block.ToUExpOffset(block.Find("Health")!.ValueOffset), 4)], found);
        var vec = PackagePatcher.FindTagValueOffsets(pkg, 0, "RelativeLocation", "StructProperty");
        Assert.Equal(block.ToUExpOffset(block.Find("RelativeLocation")!.ValueOffset), vec.Single().Offset);
        Assert.Empty(PackagePatcher.FindTagValueOffsets(pkg, 0, "NotAName", "FloatProperty"));
    }

    [Fact]
    public void UndecodableValuesBecomeRawWithoutBreakingTheBlock()
    {
        var syn = new SyntheticPackage();
        var w = new ScumStudio.Formats.IO.ByteWriter();
        // struct claims to be a Vector but has 5 bytes: must fall back to raw, the next tag still reads.
        syn.Tag(w, "RelativeLocation", "StructProperty", 5, x => { x.FName(new FNameRef(syn.N("Vector"))); x.Guid(default); x.U8(0); });
        w.Raw(new byte[] { 1, 2, 3, 4, 5 });
        syn.Tag(w, "Health", "FloatProperty", 4, x => x.U8(0));
        w.F32(1f);
        syn.None(w);
        var bytes = syn.BuildPackage(w.ToArray());
        var block = CookedPackage.Parse(bytes.UAsset, bytes.UExp).ReadProperties(0);
        Assert.IsType<RawValue>(block.Find("RelativeLocation")!.Value);
        Assert.Equal(1, block.CountRawValues());
        Assert.Equal(1f, ((FloatValue)block.Find("Health")!.Value).Value);
    }

    [Fact]
    public void JsonAndTextDumpsContainValues()
    {
        var (_, block) = Load();
        var json = block.ToJson().ToJsonString();
        Assert.Contains("\"RelativeLocation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"x\":100", json, StringComparison.Ordinal);
        Assert.Contains("\"EMyEnum::Two\"", json, StringComparison.Ordinal);
        var text = block.Format();
        Assert.Contains("  Health (FloatProperty) = 75.5", text, StringComparison.Ordinal);
        Assert.Contains("  Points (ArrayProperty) Array<Vector> x2:", text, StringComparison.Ordinal);
    }
}
