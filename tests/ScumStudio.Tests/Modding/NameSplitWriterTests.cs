using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Formats;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The import, material-parameter and DataTable writers add a name ending in <c>_&lt;number&gt;</c> the way the cooker
/// stores it (text, number). Written whole with number 0 it is a different FName: an AK-47 given AK-15 magazines imported
/// the mount twice and the game crashed (checkpoint 46).
/// </summary>
public sealed class NameSplitWriterTests
{
    private const string Mount = "/Game/X/BP_M_AK15_762_39.BP_M_AK15_762_39_C";

    [Fact]
    public void AnImportOfANumberedPackageIsSplit()
    {
        var tables = PackageTables.Of(Synthetic(), 0);
        var index = tables.Import("/Script/Engine", "BlueprintGeneratedClass", Mount);

        var import = tables.Imports[-index - 1];
        var package = tables.Imports[-import.OuterIndex - 1];
        Assert.Equal(("/Game/X/BP_M_AK15_762", 40), (tables.Names[package.ObjectName.Index], package.ObjectName.Number));
        Assert.Equal(("BP_M_AK15_762_39_C", 0), (tables.Names[import.ObjectName.Index], import.ObjectName.Number));
        Assert.DoesNotContain(tables.Names, n => n.EndsWith("_39", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExistingSplitImportIsReused()
    {
        var tables = PackageTables.Of(Synthetic(), 0);
        var first = tables.Import("/Script/Engine", "BlueprintGeneratedClass", Mount);
        var bytes = PackageWriter.Build(PackageWriter.ToBuildInput(Synthetic()) with
        {
            Names = tables.Names, NameIsWide = tables.Wide, Imports = tables.Imports, Exports = tables.Exports, PreloadDependencies = tables.Preload,
        });

        var again = PackageTables.Of(CookedPackage.Parse(bytes.UAsset, bytes.UExp), 0);
        var count = again.Imports.Count;
        Assert.Equal(first, again.Import("/Script/Engine", "BlueprintGeneratedClass", Mount));
        Assert.Equal(count, again.Imports.Count);
    }

    [Fact]
    public void ANumberedMaterialParameterIsSplit()
    {
        var bytes = MaterialParameters.Apply(MaterialInstance(), [new("MI_Test", MaterialParameters.PathOf("ScalarParameterValues", "Tint_2"), "0.5")]);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);

        Assert.DoesNotContain("Tint_2", edited.Names);
        Assert.True(Contains(edited.GetExportData(0).Span, edited.Names.ToList().IndexOf("Tint"), 3), "the payload names ('Tint', 3)");
    }

    [Fact]
    public void ANumberedRowNameIsSplit()
    {
        var bytes = DataTableEdits.AddRowCopies(DataTable(), [("Knife", "Knife_2")]);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var payload = edited.GetExportData(0).ToArray();
        var row = DataTableRows.Read(edited, payload, 0)[^1];

        Assert.Equal("Knife_2", row.Name);
        Assert.Equal(("Knife", 3), (edited.Names[BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(row.Offset))], BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(row.Offset + 4))));
        Assert.DoesNotContain("Knife_2", edited.Names);
    }

    private static CookedPackage Synthetic()
    {
        var bytes = new SyntheticPackage().BuildPackage();
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp);
    }

    private static bool Contains(ReadOnlySpan<byte> data, int index, int number)
    {
        Span<byte> pair = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(pair, index);
        BinaryPrimitives.WriteInt32LittleEndian(pair[4..], number);
        return data.IndexOf(pair) >= 0;
    }

    /// <summary>A material instance with one scalar parameter, "Rough".</summary>
    private static CookedPackage MaterialInstance()
    {
        List<string> names =
        [
            "/Script/CoreUObject", "/Script/Engine", "Package", "Class", "MaterialInstanceConstant", "MI_Test", "None", "ArrayProperty",
            "StructProperty", "ScalarParameterValues", "ScalarParameterValue", "ParameterInfo", "MaterialParameterInfo", "Name",
            "NameProperty", "ParameterValue", "FloatProperty", "Rough",
        ];
        var b = new Tags(names);
        var info = new ByteWriter();
        b.Tag(info, "Name", "NameProperty", 8);
        info.FName(new FNameRef(names.IndexOf("Rough")));
        b.None(info);

        var element = new ByteWriter();
        b.Tag(element, "ParameterInfo", "StructProperty", info.Length, "MaterialParameterInfo");
        element.Raw(info.WrittenSpan);
        b.Tag(element, "ParameterValue", "FloatProperty", 4);
        element.F32(1f);
        b.None(element);

        var array = new ByteWriter();
        array.I32(1);
        b.Tag(array, "ScalarParameterValues", "StructProperty", element.Length, "ScalarParameterValue");
        array.Raw(element.WrittenSpan);

        var payload = new ByteWriter();
        b.Tag(payload, "ScalarParameterValues", "ArrayProperty", array.Length, inner: "StructProperty");
        payload.Raw(array.WrittenSpan);
        b.None(payload);
        payload.I32(0);
        return Package(names, "MaterialInstanceConstant", "MI_Test", payload.ToArray());
    }

    /// <summary>A DataTable with one row, "Knife" (Damage 10).</summary>
    private static CookedPackage DataTable()
    {
        List<string> names = ["/Script/CoreUObject", "/Script/Engine", "Package", "Class", "DataTable", "WeaponDesc_Table", "None", "FloatProperty", "Damage", "Knife"];
        var b = new Tags(names);
        var payload = new ByteWriter();
        b.None(payload);
        payload.I32(0); // no object guid
        payload.I32(1);
        payload.FName(new FNameRef(names.IndexOf("Knife")));
        b.Tag(payload, "Damage", "FloatProperty", 4);
        payload.F32(10f);
        b.None(payload);
        return Package(names, "DataTable", "WeaponDesc_Table", payload.ToArray());
    }

    private static CookedPackage Package(List<string> names, string className, string objectName, byte[] payload)
    {
        List<ImportEntry> imports =
        [
            new(new FNameRef(0), new FNameRef(names.IndexOf("Package")), 0, new FNameRef(1)),
            new(new FNameRef(0), new FNameRef(names.IndexOf("Class")), -1, new FNameRef(names.IndexOf(className))),
        ];
        var bytes = PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = names,
            Imports = imports,
            Exports = [new ExportEntry { ClassIndex = -2, ObjectName = new FNameRef(names.IndexOf(objectName)), ObjectFlags = 0x1, IsAsset = 1, FirstExportDependency = -1 }],
            ExportData = [payload],
        });
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp);
    }

    /// <summary>UE 4.27 property tags over a fixed name table.</summary>
    private sealed class Tags(List<string> names)
    {
        public void Tag(ByteWriter w, string name, string type, int size, string? structName = null, string? inner = null)
        {
            w.FName(new FNameRef(names.IndexOf(name)));
            w.FName(new FNameRef(names.IndexOf(type)));
            w.I32(size);
            w.I32(0);
            if (structName is not null)
            {
                w.FName(new FNameRef(names.IndexOf(structName)));
                w.Guid(default);
            }
            else if (inner is not null)
            {
                w.FName(new FNameRef(names.IndexOf(inner)));
            }

            w.U8(0);
        }

        public void None(ByteWriter w) => w.FName(new FNameRef(names.IndexOf("None")));
    }
}
