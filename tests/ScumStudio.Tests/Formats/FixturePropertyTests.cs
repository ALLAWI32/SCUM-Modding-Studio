using System.Buffers.Binary;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.Formats;

/// <summary>Property dumps of real SCUM packages checked against facts recorded in FORMAT_NOTES.md.</summary>
public sealed class FixturePropertyTests
{
    private static CookedPackage Load(string virtualPath) => CookedPackage.Load(FixturePaths.OrigFile(virtualPath));

    [FixturesFact]
    public void SkWolfsWagenMatchesFormatNotes()
    {
        var pkg = Load("SCUM/Content/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/SK_WolfsWagen");

        // FORMAT_NOTES: "file ... SK_WolfsWagen.uasset/.uexp (3789 + 5706597 bytes)"; unversioned 4.27 summary.
        Assert.Equal(3789, pkg.UAsset.Length);
        Assert.Equal(5706597, pkg.UExp.Length);
        Assert.Equal(-7, pkg.Summary.LegacyFileVersion);
        Assert.Equal(522, pkg.Summary.EffectiveVersionUE4);
        Assert.Equal(0x80000000u, pkg.Summary.PackageFlags);
        Assert.Empty(pkg.Summary.CustomVersions);
        Assert.Equal(pkg.Summary.TotalHeaderSize + pkg.UExp.Length - 4, pkg.Summary.BulkDataStartOffset);

        Assert.Single(pkg.Exports);
        Assert.Equal("SkeletalMesh", pkg.GetExportClassName(0));
        foreach (var mi in new[] { "MI_WW_Chassis", "MI_WW_BodyInside", "MI_WW_Interior" })
        {
            Assert.Contains(pkg.Imports, im => pkg.ResolveName(im.ObjectName) == mi && pkg.ResolveName(im.ClassName) == "MaterialInstanceConstant");
        }

        foreach (var bone in new[] { "Root", "b_Wheel_BackLeft", "b_Wheel_BackRight", "b_Wheel_FrontLeft", "b_Wheel_FrontRight" })
        {
            Assert.Contains(bone, pkg.Names);
        }

        // Preload deps (FORMAT_NOTES): SerBeforeSer=[Skeleton]; CreateBeforeSer=[PA + 3 MIs]; SerBeforeCreate=[Class, CDO].
        var e = pkg.Exports[0];
        Assert.Equal((1, 4, 2, 0), (e.SerializationBeforeSerializationDependencies, e.CreateBeforeSerializationDependencies,
            e.SerializationBeforeCreateDependencies, e.CreateBeforeCreateDependencies));
        var preload = pkg.ReadPreloadDependencies();
        Assert.Equal("IMP:SK_WolfsWagen_Skeleton", pkg.ResolveIndex(preload[e.FirstExportDependency]));

        var block = pkg.ReadProperties(0);
        Assert.Equal(0, block.CountRawValues());
        Assert.Equal("IMP:SK_WolfsWagen_Skeleton", Assert.IsType<ObjectValue>(block.Find("Skeleton")!.Value).Reference);
        Assert.Equal("IMP:PA_WolfsWagen", Assert.IsType<ObjectValue>(block.Find("PhysicsAsset")!.Value).Reference);
        var lods = Assert.IsType<ArrayValue>(block.Find("LODInfo")!.Value);
        Assert.Equal("SkeletalMeshLODInfo", lods.StructName);
        Assert.Equal(5, lods.Items.Count); // "5 LODs ... LODInfo has 5 entries"
        var lod0 = Assert.IsType<StructValue>(lods.Items[0]);
        Assert.Equal("PerPlatformFloat", lod0.Find("ScreenSize")!.StructName);
        Assert.NotNull(block.Find("SamplingInfo"));

        // "property block ends before bHasGuid int32 at 8778; native data starts at 8782"
        Assert.Equal(8778, block.EndOffset);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(pkg.GetExportData(0).Span[8778..]));
        Assert.Equal((0x01, 0x00), (pkg.GetExportData(0).Span[8782], pkg.GetExportData(0).Span[8783])); // StripFlags (1,0)
    }

    [FixturesFact]
    public void MaterialInstanceBodyInsideReferencesItsTextures()
    {
        var pkg = Load("SCUM/Content/ConZ_Files/Models/Vehicles2/WolfsWagen/Materials/MI_WW_BodyInside");
        Assert.Equal("MaterialInstanceConstant", pkg.GetExportClassName(0));
        var block = pkg.ReadProperties(0);
        Assert.Equal(0, block.CountRawValues());

        // FORMAT_NOTES: "MI_WW_BodyInside (parent M_Car_01): TextureParameterValues -> T_Car_Body_1_D_v3, T_Car_Body_1_N_v2, T_WWBody_1_RMA, T_WWBody_1_E"
        Assert.Equal("IMP:M_Car_01", ((ObjectValue)block.Find("Parent")!.Value).Reference);
        var textures = Assert.IsType<ArrayValue>(block.Find("TextureParameterValues")!.Value);
        Assert.Equal("TextureParameterValue", textures.StructName);
        var refs = textures.Items.Cast<StructValue>()
            .Select(t => ((ObjectValue)t.Find("ParameterValue")!.Value).Reference)
            .ToList();
        Assert.Equal(new[] { "IMP:T_Car_Body_1_D_v3", "IMP:T_Car_Body_1_N_v2", "IMP:T_WWBody_1_RMA", "IMP:T_WWBody_1_E" }, refs);

        var scalars = Assert.IsType<ArrayValue>(block.Find("ScalarParameterValues")!.Value);
        var first = Assert.IsType<StructValue>(scalars.Items[0]);
        var info = Assert.IsType<StructValue>(first.Find("ParameterInfo")!.Value);
        Assert.Equal("MaterialParameterInfo", info.StructName);
        Assert.IsType<NameValue>(info.Find("Name")!.Value);
        Assert.IsType<FloatValue>(first.Find("ParameterValue")!.Value);
        Assert.IsType<GuidValue>(first.Find("ExpressionGUID")!.Value);

        var vectors = Assert.IsType<ArrayValue>(block.Find("VectorParameterValues")!.Value);
        Assert.All(vectors.Items.Cast<StructValue>(), v => Assert.IsType<LinearColorValue>(v.Find("ParameterValue")!.Value));
        Assert.True(block.NativeDataLength > 100_000, "MI exports carry large inline shader maps");
    }

    [FixturesFact]
    public void BlueprintComponentVectorsParseAsThreeFloats()
    {
        var pkg = Load("SCUM/Content/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen");
        var index = pkg.Exports.Select((e, i) => (e, i)).Single(x => pkg.ResolveName(x.e.ObjectName) == "LBrakeLight_GEN_VARIABLE").i;
        var block = pkg.ReadProperties(index);
        var loc = Assert.IsType<VectorValue>(block.Find("RelativeLocation")!.Value);
        Assert.Equal(12, loc.Size);
        Assert.Equal(-201.38538f, loc.X, 3);
        Assert.Equal(-63.36275f, loc.Y, 3);
        Assert.Equal(68.4207f, loc.Z, 3);
        var rot = Assert.IsType<RotatorValue>(block.Find("RelativeRotation")!.Value);
        Assert.Equal(180f, rot.Yaw, 2);

        var vectors = 0;
        for (var i = 0; i < pkg.Exports.Count; i++)
        {
            foreach (var (_, tag) in pkg.ReadProperties(i).EnumerateAll())
            {
                if (tag.Value is VectorValue v)
                {
                    vectors++;
                    Assert.Equal(12, tag.Size);
                    Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
                }
            }
        }

        Assert.True(vectors >= 3);
    }

    [FixturesFact]
    public void EveryStockExportHasAFullyDecodedPropertyBlock()
    {
        var failures = new List<string>();
        var exports = 0;
        foreach (var path in RoundTripVerifier.FindPackages(FixturePaths.OrigContent))
        {
            var pkg = CookedPackage.Load(path);
            for (var i = 0; i < pkg.Exports.Count; i++)
            {
                exports++;
                try
                {
                    var block = pkg.ReadProperties(i);
                    if (block.CountRawValues() > 0)
                    {
                        failures.Add($"{path} export {i + 1}: {block.CountRawValues()} undecoded values");
                    }

                    _ = block.ToJson().ToJsonString();
                }
                catch (FormatException ex)
                {
                    failures.Add($"{path} export {i + 1}: {ex.Message}");
                }
            }
        }

        Assert.True(exports > 1000, $"only {exports} exports");
        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(20)));
    }
}
