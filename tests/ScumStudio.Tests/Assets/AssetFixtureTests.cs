using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Export;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Meshes;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Pak.Reading;
using ScumStudio.Tests.Fixtures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// ScumStudio.Assets against the user's real cooked files (SCUM_FIXTURES):
/// <list type="bullet">
/// <item><c>orig/</c>: stock WolfsWagen / plane / weapon packages extracted from the game (loose layout).</item>
/// <item><c>build/</c>: the user's rebuilt WolfsWagen packages, made by build_skmesh.py / build_textures.py from the UE Viewer
/// export in <c>Content/ConZ_Files/Models/Vehicles/Cars/WolfsWagen/</c> (SK_WolfsWagen.gltf is the SUV source, node
/// <c>SK_SUV_04</c>; its PNGs are the texture sources).</item>
/// </list>
/// </summary>
public sealed class AssetFixtureTests
{
    private const string WolfsWagenMeshes = "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes";
    private const string WolfsWagenTextures = "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Textures";
    private const string StockSkeletalMesh = WolfsWagenMeshes + "/SK_WolfsWagen.SK_WolfsWagen";
    private const string WheelStaticMesh = WolfsWagenMeshes + "/SM_WolfsWagen_Wheel_FrontLeft";

    private static string UserExportFolder => FixturePaths.Combine("Content", "ConZ_Files", "Models", "Vehicles", "Cars", "WolfsWagen");

    [FixturesFact]
    public void LooseCatalog_ListsWolfsWagenPackages_WithClasses()
    {
        using var catalog = AssetCatalog.Open(FixturePaths.OrigRoot);
        Assert.Equal(AssetSourceKind.Loose, catalog.SourceKind);
        var packages = catalog.PackageFiles;
        Assert.Equal(FixturePaths.OrigFiles(".uasset").Count + FixturePaths.OrigFiles(".umap").Count, packages.Count);
        Assert.All(packages, p => Assert.StartsWith("SCUM/Content/", p, StringComparison.Ordinal));
        Assert.Contains("SCUM/Content/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/SK_WolfsWagen.uasset", packages);

        var index = catalog.BuildIndex();
        Assert.True(index.HasClasses);
        var ww = index.GetFolder("/Game/ConZ_Files/Models/Vehicles2/WolfsWagen");
        Assert.NotNull(ww);
        Assert.True(ww!.TotalPackageCount > 150, $"WolfsWagen packages: {ww.TotalPackageCount}");
        Assert.Equal("SkeletalMesh", index.Find(StockSkeletalMesh)!.ClassName);
        Assert.Equal("StaticMesh", index.Find(WheelStaticMesh)!.ClassName);
        Assert.Equal("Texture2D", index.Find(WolfsWagenTextures + "/T_Interior_D")!.ClassName);
        Assert.Equal("MaterialInstanceConstant", catalog.GetMainClassName("/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Materials/MI_WW_Interior"));
        Assert.Contains(catalog.FindByClass("SkeletalMesh"), e => e.Name == "SK_WolfsWagen");
        Assert.All(index.Entries, e => Assert.False(string.IsNullOrEmpty(e.ClassName), e.PackagePath));

        // Package SK_WolfsWagen_Dashboard holds export SK_WolfsWagen_Dashboard_V1.
        var dashboard = index.Find(WolfsWagenMeshes + "/SK_WolfsWagen_Dashboard")!;
        Assert.Equal("SK_WolfsWagen_Dashboard_V1", dashboard.MainExportName);
        Assert.Equal(WolfsWagenMeshes + "/SK_WolfsWagen_Dashboard.SK_WolfsWagen_Dashboard_V1", dashboard.ObjectPath);
        Assert.Equal("SK_WolfsWagen_Dashboard_V1", catalog.LoadObject(dashboard.ObjectPath).Name);
        Assert.Equal("SK_WolfsWagen_Dashboard_V1", catalog.LoadObject(dashboard.PackagePath).Name);
        Assert.Throws<KeyNotFoundException>(() => catalog.LoadObject(dashboard.PackagePath + ".NoSuchExport"));

        var exports = catalog.GetExports(StockSkeletalMesh);
        var main = Assert.Single(exports);
        Assert.Equal(("SkeletalMesh", "SK_WolfsWagen"), (main.ClassName, main.Name));
    }

    [FixturesFact]
    public void OpenLoose_AcceptsProjectAndContentFolders()
    {
        using var fromProject = AssetCatalog.OpenLoose(Path.Combine(FixturePaths.OrigRoot, "SCUM"));
        using var fromContent = AssetCatalog.OpenLoose(FixturePaths.OrigContent);
        Assert.Equal(fromProject.PackageFiles, fromContent.PackageFiles);
        Assert.True(fromContent.PackageExists(StockSkeletalMesh));
        Assert.True(fromContent.PackageExists("SCUM/Content/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/SK_WolfsWagen.uasset"));
    }

    [FixturesFact]
    public void StockSkeletalMesh_ExtractsPlausibleGeometry()
    {
        using var catalog = AssetCatalog.Open(FixturePaths.OrigRoot);
        var sk = catalog.LoadObject<USkeletalMesh>(StockSkeletalMesh);
        var info = MeshExtractor.Describe(sk);

        // Facts recorded in FORMAT_NOTES.md ("SK_WolfsWagen facts"): 3 slots, 5 inlined LODs, 5 bones.
        Assert.Equal(MeshAssetKind.Skeletal, info.Kind);
        Assert.Equal(["Chassis", "Car_Body_1", "Interior"], info.Materials.Select(m => m.SlotName));
        Assert.Equal(WolfsWagenMaterial("MI_WW_Chassis"), info.Materials[0].MaterialPath);
        Assert.Equal(5, info.Lods.Count);
        Assert.Equal(5, info.BoneCount);
        Assert.True(info.Lods.Zip(info.Lods.Skip(1)).All(p => p.First.TriangleCount > p.Second.TriangleCount), "LOD triangle counts must decrease.");

        var mesh = MeshExtractor.Extract(sk);
        Assert.Empty(mesh.Validate());
        Assert.Equal(info.Lods[0].VertexCount, mesh.VertexCount);
        Assert.Equal(info.Lods[0].IndexCount, mesh.Indices.Length);
        Assert.InRange(mesh.VertexCount, 10_000, 100_000);
        Assert.InRange(mesh.TriangleCount, 10_000, 100_000);
        Assert.Equal(mesh.Indices.Length, mesh.Sections.Sum(s => s.IndexCount));
        Assert.Equal(WolfsWagenMaterial("MI_WW_Interior"), mesh.Sections[^1].MaterialName);
        Assert.Equal(mesh.VertexCount * 2, mesh.Uv0.Length);

        // A car: about 4.2 m long (X), 1.75 m wide (Y), 1.3 m tall (Z) in UE centimetres, centred on the root bone.
        AssertNear(mesh.Bounds.Size, new Vector3(420.7f, 175.8f, 131.6f), 2f);
        AssertNear(mesh.Bounds.Min, info.Bounds.Min, 0.5f);
        AssertNear(mesh.Bounds.Max, info.Bounds.Max, 0.5f);
        AssertUnitNormals(mesh);
        Assert.True(ClockwiseFraction(mesh) > 0.95, "UE front faces are clockwise against the normal in UE space.");
    }

    [FixturesFact]
    public void UserBuiltSkeletalMesh_MatchesTheUserGltfExport()
    {
        // build/.../SK_WolfsWagen was generated by the user's build_skmesh.py from Content/.../SK_WolfsWagen.gltf (UE Viewer export of
        // the SUV). FORMAT_NOTES.md: UE.X = gx*100, UE.Y = gz*100, UE.Z = gy*100, then all vertices shifted by X -26.3, Z -16.0.
        var gltf = GltfReader.Load(Path.Combine(UserExportFolder, "SK_WolfsWagen.gltf"));
        var gltfVertices = gltf.Primitives.Sum(p => gltf.Count(p.Position));
        var gltfIndices = gltf.Primitives.Sum(p => gltf.Count(p.Indices));
        var gltfBox = BoundingBox.Empty;
        foreach (var p in gltf.Primitives)
        {
            foreach (var v in gltf.ReadVec3(p.Position))
            {
                gltfBox = gltfBox.Include(new Vector3(v.X * 100f - 26.3f, v.Z * 100f, v.Y * 100f - 16.0f));
            }
        }

        using var catalog = AssetCatalog.Open(FixturePaths.Combine("build"));
        var mesh = MeshExtractor.Extract(catalog.LoadObject<USkeletalMesh>(StockSkeletalMesh));
        Assert.Empty(mesh.Validate());

        // The build drops/merges a few parts (separate door meshes, degenerate glass), so counts match within tolerance.
        Assert.InRange(mesh.VertexCount, (int)(gltfVertices * 0.85), gltfVertices);
        Assert.InRange(mesh.Indices.Length, (int)(gltfIndices * 0.80), gltfIndices);

        // Length (X) and height (Z) are preserved exactly; width (Y) loses the widest door parts.
        Assert.InRange(mesh.Bounds.Min.X, gltfBox.Min.X - 1f, gltfBox.Min.X + 1f);
        Assert.InRange(mesh.Bounds.Max.X, gltfBox.Max.X - 1f, gltfBox.Max.X + 1f);
        Assert.InRange(mesh.Bounds.Min.Z, gltfBox.Min.Z - 1f, gltfBox.Min.Z + 1f);
        Assert.InRange(mesh.Bounds.Max.Z, gltfBox.Max.Z - 1f, gltfBox.Max.Z + 1f);
        Assert.InRange(mesh.Bounds.Size.Y, gltfBox.Size.Y * 0.85f, gltfBox.Size.Y * 1.01f);
    }

    [FixturesFact]
    public void StaticMesh_ExtractsNonEmptyGeometry()
    {
        using var catalog = AssetCatalog.Open(FixturePaths.OrigRoot);
        var sm = catalog.LoadObject<UStaticMesh>(WheelStaticMesh);
        var info = MeshExtractor.Describe(sm);
        Assert.Equal(MeshAssetKind.Static, info.Kind);
        Assert.Equal(["Wheels", "Chassis"], info.Materials.Select(m => m.SlotName));
        Assert.True(info.Lods.Count >= 2);

        var mesh = MeshExtractor.Extract(sm);
        Assert.Empty(mesh.Validate());
        Assert.True(mesh.VertexCount > 100 && mesh.TriangleCount > 100, $"{mesh.VertexCount} vertices, {mesh.TriangleCount} triangles");
        Assert.Equal(info.Lods[0].VertexCount, mesh.VertexCount);
        Assert.Equal(2, mesh.Sections.Length);
        Assert.Equal(WolfsWagenMaterial("MI_WW_Wheels"), mesh.Sections[0].MaterialName);
        // Wheel radius ~33 cm (FORMAT_NOTES: physics radius 31.5 cm).
        Assert.InRange(mesh.Bounds.Extent.X, 30f, 36f);
        Assert.InRange(mesh.Bounds.Extent.Z, 30f, 36f);
        AssertUnitNormals(mesh);

        var lod1 = MeshExtractor.Extract(sm, 1);
        Assert.Equal(info.Lods[1].VertexCount, lod1.VertexCount);
        Assert.True(lod1.TriangleCount < mesh.TriangleCount);
        Assert.Throws<InvalidDataException>(() => MeshExtractor.Extract(sm, info.Lods.Count));

        // The LOD chain: every LOD with render data, finest first, with the cooked screen-size thresholds (decreasing).
        var chain = MeshExtractor.ExtractLods(sm);
        Assert.Equal(info.Lods.Count(l => !l.IsStripped), chain.Lods.Count);
        Assert.Equal(chain.Lods.Count, chain.ScreenSizes.Count);
        Assert.Equal(mesh.VertexCount, chain.Lods[0].VertexCount);
        Assert.Equal(lod1.VertexCount, chain.Lods[1].VertexCount);
        Assert.True(chain.ScreenSizes[1] < chain.ScreenSizes[0] && chain.ScreenSizes[1] > 0f, string.Join(", ", chain.ScreenSizes));
        Assert.Single(MeshExtractor.ExtractLods(sm, 0, 1).Lods);
        Assert.Equal(lod1.VertexCount, MeshExtractor.ExtractLods(sm, 1).Lods[0].VertexCount);
    }

    [FixturesFact]
    public void EveryMeshUnderOrig_Extracts()
    {
        using var catalog = AssetCatalog.Open(FixturePaths.OrigRoot);
        var meshes = catalog.FindByClass("StaticMesh").Concat(catalog.FindByClass("SkeletalMesh")).ToList();
        Assert.True(meshes.Count > 100, $"{meshes.Count} meshes");
        var failures = new List<string>();
        foreach (var entry in meshes)
        {
            try
            {
                var obj = catalog.LoadObject(entry.ObjectPath);
                var info = MeshExtractor.Describe(obj);
                var lod = info.Lods.FirstOrDefault(l => !l.IsStripped);
                if (lod is null)
                {
                    continue;
                }

                var mesh = MeshExtractor.Extract(obj, lod.Index);
                if (mesh.Validate().Count > 0)
                {
                    failures.Add($"{entry.PackagePath}: {string.Join("; ", mesh.Validate())}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.PackagePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [FixturesTheory]
    [InlineData("T_Interior_D", "PF_DXT1")]
    [InlineData("T_Interior_N", "PF_BC5")]
    [InlineData("T_Glass_D", "PF_DXT5")]
    public void StockTexture_SizeAndAspectMatchUserPng(string name, string format)
    {
        using var catalog = AssetCatalog.Open(FixturePaths.OrigRoot);
        var texture = catalog.LoadObject<UTexture2D>($"{WolfsWagenTextures}/{name}");
        var info = TextureDecoder.Describe(texture);
        Assert.Equal(format, info.PixelFormat);

        var full = TextureDecoder.Decode(texture);
        var png = Image.Identify(Path.Combine(UserExportFolder, name + ".png"));
        Assert.Equal((png.Width, png.Height), (full.Width, full.Height));
        Assert.Equal(full.Width * full.Height * 4, full.Rgba.Length);

        var small = TextureDecoder.Decode(texture, 256);
        Assert.Equal((256, 256), (small.Width, small.Height));
        Assert.Equal((double)png.Width / png.Height, (double)small.Width / small.Height, 3);
        Assert.Equal(full.SourceWidth, png.Width);
    }

    [FixturesTheory]
    [InlineData("T_Interior_D")]
    [InlineData("T_Interior_N")]
    [InlineData("T_Interior_RMA")]
    [InlineData("T_Glass_D")]
    public void UserBuiltTexture_DecodesToTheSourcePng(string name)
    {
        // build/.../Textures/<name> was encoded by the user's build_textures.py from Content/.../<name>.png.
        using var catalog = AssetCatalog.Open(FixturePaths.Combine("build"));
        var decoded = TextureDecoder.Decode(catalog.LoadObject<UTexture2D>($"{WolfsWagenTextures}/{name}"), 256);
        using var png = Image.Load<Rgba32>(Path.Combine(UserExportFolder, name + ".png"));
        png.Mutate(x => x.Resize(decoded.Width, decoded.Height, KnownResamplers.Box));

        var mad = new double[4];
        for (var y = 0; y < decoded.Height; y++)
        {
            for (var x = 0; x < decoded.Width; x++)
            {
                var p = png[x, y];
                var o = (y * decoded.Width + x) * 4;
                mad[0] += Math.Abs(p.R - decoded.Rgba[o]);
                mad[1] += Math.Abs(p.G - decoded.Rgba[o + 1]);
                mad[2] += Math.Abs(p.B - decoded.Rgba[o + 2]);
                mad[3] += Math.Abs(p.A - decoded.Rgba[o + 3]);
            }
        }

        var n = decoded.Width * (double)decoded.Height;
        // Block compression + different mip filters: a mean error of a few levels. A channel swap or a wrong decoder is > 20.
        Assert.All(mad, m => Assert.True(m / n < 6.0, $"{name}: mean abs error per channel {string.Join(", ", mad.Select(v => (v / n).ToString("F2")))}"));
    }

    [FixturesFact]
    public void MaterialInstance_ResolvesParametersAndBaseColour()
    {
        using var catalog = AssetCatalog.Open(FixturePaths.OrigRoot);
        var info = new MaterialInspector(catalog).Inspect("/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Materials/MI_WW_Interior");

        Assert.Equal(WolfsWagenMaterial("MI_WW_Interior"), info.ObjectPath);
        // Parent M_Car_01 lives in a stock pak that is not part of the fixture: listed, but the chain stops there.
        Assert.Equal(["/Game/ConZ_Files/Materials/Car/M_Car_01.M_Car_01"], info.ParentChain);
        Assert.Equal($"{WolfsWagenTextures}/T_Interior_D.T_Interior_D", info.BaseColorTexture);
        Assert.Contains(info.Textures, t => t.Name == "Normal Map" && t.TexturePath.EndsWith("T_Interior_N.T_Interior_N", StringComparison.Ordinal));
        Assert.Contains(info.Scalars, s => s.Name == "Headlight Strength");
        Assert.All(info.Textures, t => Assert.StartsWith("/Game/", t.TexturePath, StringComparison.Ordinal));

        // A material instance whose parent is another instance in the fixture: parameters merge along the chain.
        var inside = new MaterialInspector(catalog).Inspect("/Game/ConZ_Files/Models/Vehicles2/Plane_01/Materials/MI_Plane_01_Body_Inside_A");
        Assert.Equal(2, inside.ParentChain.Count);
        Assert.EndsWith("T_Plane_01_Body_D.T_Plane_01_Body_D", inside.BaseColorTexture, StringComparison.Ordinal);
        Assert.NotNull(inside.TintColor);
        Assert.Contains(inside.Textures, t => t.Source == "MI_Plane_01_Body_A");
    }

    [FixturesFact]
    public async Task MeshExport_StockSkeletalMesh_WritesValidGltfAndObj()
    {
        using var catalog = AssetCatalog.Open(FixturePaths.OrigRoot);
        var mesh = MeshExtractor.Extract(catalog.LoadObject(StockSkeletalMesh));
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-ww-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = await GltfExporter.SaveAsync(mesh, Path.Combine(dir, "SK_WolfsWagen.gltf"));
            var gltf = GltfReader.Load(files[0]);
            Assert.Equal(mesh.Sections.Length, gltf.Primitives.Count);
            Assert.Equal(mesh.Indices.Length, gltf.Primitives.Sum(p => gltf.Count(p.Indices)));
            var positions = gltf.ReadVec3(gltf.Primitives[0].Position);
            Assert.Equal(mesh.VertexCount, positions.Length);
            var (min, max) = gltf.AccessorBounds(gltf.Primitives[0].Position);
            AssertNear(max - min, new Vector3(mesh.Bounds.Size.X, mesh.Bounds.Size.Z, mesh.Bounds.Size.Y) * 0.01f, 0.001f);

            // In glTF space (right-handed, Y up) front faces must be counter-clockwise against the normals.
            var normals = gltf.ReadVec3(gltf.Primitives[0].Normal!.Value);
            var ccw = 0;
            var total = 0;
            foreach (var prim in gltf.Primitives)
            {
                var idx = gltf.ReadIndices(prim.Indices);
                for (var t = 0; t < idx.Length; t += 3)
                {
                    var face = Vector3.Cross(positions[idx[t + 1]] - positions[idx[t]], positions[idx[t + 2]] - positions[idx[t]]);
                    var d = Vector3.Dot(face, normals[idx[t]]);
                    if (d != 0)
                    {
                        total++;
                        ccw += d > 0 ? 1 : 0;
                    }
                }
            }

            Assert.True(ccw > total * 0.95, $"{ccw} of {total} triangles counter-clockwise");

            var obj = await ObjExporter.SaveAsync(mesh, Path.Combine(dir, "SK_WolfsWagen.obj"));
            var lines = File.ReadLines(obj[0]).ToList();
            Assert.Equal(mesh.VertexCount, lines.Count(l => l.StartsWith("v ", StringComparison.Ordinal)));
            Assert.Equal(mesh.TriangleCount, lines.Count(l => l.StartsWith("f ", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [FixturesFact]
    public void PakSource_LoadsMeshesFromUserModPak()
    {
        var pak = FixturePaths.Pak("pakchunk99-SUV_01_P.pak");
        using var catalog = AssetCatalog.Open(pak);
        Assert.Equal(AssetSourceKind.Paks, catalog.SourceKind);
        Assert.Equal(0, catalog.UnmountedContainerCount);
        var suv = catalog.FindByClass("SkeletalMesh").First(e => e.Name == "SK_SUV_01");
        var mesh = MeshExtractor.Extract(catalog.LoadObject(suv.ObjectPath));
        Assert.Empty(mesh.Validate());
        Assert.True(mesh.VertexCount > 100_000, $"{mesh.VertexCount} vertices");

        // The same package through the unpacked tree gives identical geometry.
        using var loose = AssetCatalog.Open(FixturePaths.BuildSepTree());
        var fromTree = MeshExtractor.Extract(loose.LoadObject(suv.ObjectPath));
        Assert.Equal(mesh.Positions, fromTree.Positions);
        Assert.Equal(mesh.Indices, fromTree.Indices);

        // And through a PakFileSource's provider (ScumStudio.Pak) wrapped with FromProvider.
        using var source = PakFileSource.OpenFile(pak);
        using var wrapped = AssetCatalog.FromProvider(source.Provider);
        Assert.Equal(mesh.VertexCount, MeshExtractor.Extract(wrapped.LoadObject(suv.ObjectPath)).VertexCount);
    }

    [FixturesFact]
    public async Task PakSource_DoubledProjectMount_IsAliased()
    {
        // Stock pakchunk0_s51 quirk: mounted at ../../../SCUM/ with entries that also start with SCUM/, so readers report
        // SCUM/SCUM/Content/... . Reproduce it with the in-house writer and check the catalog still finds /Game/ paths.
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-doubled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var entries = new List<ScumStudio.Pak.Writing.PakWriterEntry>();
            foreach (var ext in new[] { ".uasset", ".uexp" })
            {
                var virtualPath = "SCUM/Content/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/SM_WolfsWagen_Wheel_FrontLeft" + ext;
                entries.Add(ScumStudio.Pak.Writing.PakWriterEntry.FromFile(virtualPath, FixturePaths.OrigFile(virtualPath)));
            }

            var pak = Path.Combine(dir, "pakchunk0_s51-WindowsNoEditor.pak");
            var writer = new ScumStudio.Pak.Writing.PakWriter(new ScumStudio.Pak.Writing.PakWriterOptions { MountPoint = "../../../SCUM/" });
            await writer.WriteAsync(entries, pak);

            using var catalog = AssetCatalog.Open(dir);
            Assert.Equal(["SCUM/Content/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/SM_WolfsWagen_Wheel_FrontLeft.uasset"], catalog.PackageFiles);
            var mesh = MeshExtractor.Extract(catalog.LoadObject(WheelStaticMesh));
            using var stock = AssetCatalog.Open(FixturePaths.OrigRoot);
            Assert.Equal(MeshExtractor.Extract(stock.LoadObject(WheelStaticMesh)).Positions, mesh.Positions);
            Assert.Equal("StaticMesh", Assert.Single(catalog.BuildIndex().Entries).ClassName);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [FixturesFact]
    public void LooseOverlay_OverridesPakEntries()
    {
        // Stock tree as base, the user's build/ tree layered on top: SK_WolfsWagen comes from build/.
        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot, new AssetCatalogOptions { LooseOverlays = [FixturePaths.Combine("build")] });
        var overlaid = MeshExtractor.Describe(catalog.LoadObject(StockSkeletalMesh));
        using var stock = AssetCatalog.Open(FixturePaths.OrigRoot);
        var original = MeshExtractor.Describe(stock.LoadObject(StockSkeletalMesh));
        Assert.NotEqual(original.Lods[0].VertexCount, overlaid.Lods[0].VertexCount);
        Assert.True(catalog.PackageFiles.Count > stock.PackageFiles.Count);
    }

    private static string WolfsWagenMaterial(string name) => $"/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Materials/{name}.{name}";

    private static void AssertNear(Vector3 actual, Vector3 expected, float tolerance) =>
        Assert.True(Vector3.Distance(actual, expected) <= tolerance, $"{actual} vs {expected} (tolerance {tolerance})");

    private static void AssertUnitNormals(MeshData mesh)
    {
        Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
        for (var i = 0; i < mesh.Normals.Length; i += 3)
        {
            var len = new Vector3(mesh.Normals[i], mesh.Normals[i + 1], mesh.Normals[i + 2]).Length();
            Assert.InRange(len, 0.99f, 1.01f);
        }
    }

    private static double ClockwiseFraction(MeshData mesh)
    {
        int cw = 0, total = 0;
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var a = V(mesh.Positions, mesh.Indices[t]);
            var d = Vector3.Dot(Vector3.Cross(V(mesh.Positions, mesh.Indices[t + 1]) - a, V(mesh.Positions, mesh.Indices[t + 2]) - a), V(mesh.Normals, mesh.Indices[t]));
            if (d != 0)
            {
                total++;
                cw += d < 0 ? 1 : 0;
            }
        }

        return total == 0 ? 0 : (double)cw / total;

        static Vector3 V(float[] a, uint i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);
    }
}
