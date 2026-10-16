using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Projects;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Textures;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Formats;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// New textures from pictures (<see cref="TextureWriter"/>): the stock texture's layout, format and properties at the
/// clone's own path, read back by CUE4Parse; and the export pass that ships them with the clone's material.
/// </summary>
public sealed class TextureWriterTests
{
    private const string Template = SyntheticTexture.Path + ".T_Body_D";
    private const string NewTexture = "/Game/Tests/Clone/Textures/T_CarGold_Body_Gold_DM";

    [Theory]
    [InlineData(256)]
    [InlineData(2048)]
    public void APictureIsWrittenInTheTemplatesLayoutAndCue4ParseReadsIt(int size)
    {
        using var temp = new LevelTempDirectory();
        var (bytes, ubulk) = SyntheticTexture.Build(SyntheticTexture.Path);
        var template = CookedPackage.Parse(bytes.UAsset, bytes.UExp, ubulk, SyntheticTexture.Path);
        var picture = Picture(size);

        var written = TextureWriter.Write(template, Template, NewTexture, picture, size, size);
        SyntheticTexture.WriteLoose(temp.Path, NewTexture, written.Bytes, written.UBulk);

        var package = CookedPackage.Parse(written.Bytes.UAsset, written.Bytes.UExp, written.UBulk);
        Assert.True(CookedTexture2DTests.OffsetsAreAbsolute(package));
        var cooked = CookedTexture2D.Read(package, 0);
        Assert.Equal("TEXTUREGROUP_Vehicle", ((EnumValue)cooked.Properties.Find("LODGroup")!.Value).Value.Split("::")[^1]);
        var imported = (IntPointValue)cooked.Properties.Find("ImportedSize")!.Value;
        Assert.Equal((size, size), (imported.X, imported.Y));
        Assert.All(cooked.Platforms[0].Mips, m => Assert.Equal(Math.Max(m.SizeX, m.SizeY) >= 128, !m.IsInline));
        Assert.Contains(NewTexture, package.Names);
        Assert.DoesNotContain(package.Names, n => n.EndsWith("/T_Body_D", StringComparison.Ordinal) || n == "T_Body_D");

        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var texture = catalog.LoadObject<UTexture2D>(NewTexture + ".T_CarGold_Body_Gold_DM");
        Assert.Equal(EPixelFormat.PF_DXT1, texture.Format);
        Assert.False(texture.SRGB);
        Assert.Equal(MipChain.FullCount(size, size), texture.PlatformData.Mips.Length);
        var decoded = TextureDecoder.Decode(texture);
        Assert.Equal((size, size), (decoded.Width, decoded.Height));
        var error = picture.Select((v, i) => i % 4 == 3 ? 0 : Math.Abs(v - decoded.Rgba[i])).Sum() / (size * size * 3.0);
        Assert.True(error <= 6, $"mean abs error {error:F2}");
    }

    [Fact]
    public void RewritingATextureUnderItsOwnPathKeepsEveryHeaderByte()
    {
        var (bytes, ubulk) = SyntheticTexture.Build(SyntheticTexture.Path);
        var template = CookedPackage.Parse(bytes.UAsset, bytes.UExp, ubulk, SyntheticTexture.Path);

        var written = TextureWriter.Write(template, Template, SyntheticTexture.Path, Picture(256), 256, 256);

        Assert.Equal(bytes.UAsset, written.Bytes.UAsset);
        Assert.Equal(bytes.UExp.Length, written.Bytes.UExp.Length);
        Assert.Equal(ubulk!.Length, written.UBulk!.Length);
        var before = CookedTexture2D.Read(template, 0).Platforms[0];
        var after = CookedTexture2D.Read(CookedPackage.Parse(written.Bytes.UAsset, written.Bytes.UExp, written.UBulk), 0).Platforms[0];
        Assert.Equal(before.SkipOffset, after.SkipOffset);
        Assert.Equal(before.Mips.Select(m => (m.Flags, m.OffsetInFile, m.SizeOnDisk, m.SizeX)), after.Mips.Select(m => (m.Flags, m.OffsetInFile, m.SizeOnDisk, m.SizeX)));
        var payloads = before.Mips.Where(m => m.IsInline).Select(m => (Start: (int)m.OffsetInFile - template.Summary.TotalHeaderSize, m.SizeOnDisk)).ToList();
        for (var i = 0; i < bytes.UExp.Length; i++)
        {
            if (!payloads.Any(p => i >= p.Start && i < p.Start + p.SizeOnDisk))
            {
                Assert.True(bytes.UExp[i] == written.Bytes.UExp[i], $"uexp byte {i} changed outside the mip pixels");
            }
        }
    }

    [Fact]
    public void ALeafEndingInANumberIsRefused()
    {
        var (bytes, ubulk) = SyntheticTexture.Build(SyntheticTexture.Path);
        var template = CookedPackage.Parse(bytes.UAsset, bytes.UExp, ubulk, SyntheticTexture.Path);
        Assert.Throws<ArgumentException>(() => TextureWriter.Write(template, Template, "/Game/Tests/Clone/Textures/T_Body_02", Picture(64), 64, 64));
        Assert.Throws<ArgumentException>(() => TextureWriter.Write(template, Template, NewTexture, Picture(64)[..100], 100, 64));
        Assert.False(TextureWrites.EndsInNumber(TextureWrites.NewPackage("/Game/Tests/Clone/BPC_Car_01", "MI_Body_01", "Diffuse Map")));
        Assert.Equal("/Game/Tests/Clone/Textures/T_Car_01_Body_01_RMA", TextureWrites.NewPackage("/Game/Tests/Clone/BPC_Car_01", "/Game/M/MI_Body_01.MI_Body_01", "Rough/Metal/AO"));
    }

    [Fact]
    public void TheExportWritesTheCloneTextureAndTheCloneMaterialUsesIt()
    {
        using var temp = new LevelTempDirectory();
        var game = Path.Combine(temp.Path, "game");
        var project = Path.Combine(temp.Path, "project");
        SyntheticTexture.WriteLoose(game, SyntheticTexture.Path, srgb: true);
        const string stockMaterial = "/Game/Tests/Materials/MI_Body";
        const string cloneMaterial = "/Game/Tests/Materials/MI_Body_Gold";
        var mi = Material(stockMaterial);
        SyntheticTexture.WriteLoose(game, stockMaterial, mi, null);
        var file = ProjectTextures.Save(project, Picture(64), 64, 64);
        using var catalog = AssetCatalog.OpenLoose(game);

        var request = new AssetModRequest(
            [new ClonePlan("/Game/Tests/BPC_Car", "/Game/Tests/BPC_CarGold", [new KeyValuePair<string, string>(stockMaterial, cloneMaterial)])],
            new Dictionary<string, IReadOnlyList<TunableEdit>>
            {
                [NewTexture] = [new TunableEdit("T_CarGold_Body_Gold_DM", TextureWrites.PixelsPath, TextureWrites.Value(Template, file))],
                [cloneMaterial] =
                [
                    new TunableEdit("MI_Body_Gold", MaterialParameters.PathOf("TextureParameterValues", "Diffuse Map"), NewTexture + ".T_CarGold_Body_Gold_DM"),
                    new TunableEdit("MI_Body_Gold", MaterialTextureStreaming.PathOf("T_Body_D"), "T_CarGold_Body_Gold_DM"),
                ],
            })
        { FileRoot = project };
        var result = AssetModBuilder.Build(catalog, request);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("not written", StringComparison.Ordinal) || w.Contains("streaming", StringComparison.Ordinal));
        Assert.All(result.Packages, p => Assert.True(p.IsClone, p.PackagePath));
        Assert.DoesNotContain(result.Packages, p => p.PackagePath is SyntheticTexture.Path or stockMaterial);
        var texture = Assert.Single(result.Packages, p => p.PackagePath == NewTexture);
        Assert.NotNull(texture.UBulk);
        Assert.True(CookedTexture2DTests.OffsetsAreAbsolute(CookedPackage.Parse(texture.Bytes.UAsset, texture.Bytes.UExp, texture.UBulk)));

        var material = Assert.Single(result.Packages, p => p.PackagePath == cloneMaterial);
        var package = CookedPackage.Parse(material.Bytes.UAsset, material.Bytes.UExp);
        Assert.Contains(package.Imports, i => package.ResolveName(i.ObjectName) == NewTexture);
        var streaming = (ArrayValue)package.ReadProperties(0).Find("TextureStreamingData")!.Value;
        Assert.Equal("T_CarGold_Body_Gold_DM", ((NameValue)((StructValue)streaming.Items[0]).Properties.First(t => t.Name == "TextureName").Value).Value);
    }

    [Fact]
    public void AMissingPictureIsAWarningNotACrash()
    {
        using var temp = new LevelTempDirectory();
        SyntheticTexture.WriteLoose(temp.Path, SyntheticTexture.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var request = new AssetModRequest([], new Dictionary<string, IReadOnlyList<TunableEdit>>
        {
            [NewTexture] = [new TunableEdit("T_CarGold_Body_Gold_DM", TextureWrites.PixelsPath, TextureWrites.Value(Template, "textures/missing.png"))],
            ["/Game/Tests/Clone/Textures/T_Undone_DM"] = [new TunableEdit("T_Undone_DM", TextureWrites.PixelsPath, string.Empty)],
        })
        { FileRoot = temp.Path };

        var result = AssetModBuilder.Build(catalog, request);
        Assert.Empty(result.Packages);
        Assert.Contains(result.Warnings, w => w.Contains(NewTexture, StringComparison.Ordinal) && w.Contains("not written", StringComparison.Ordinal));
    }

    /// <summary>A material instance with a <c>Diffuse Map</c> texture parameter and its streaming entry.</summary>
    internal static PackageBytes Material(string packagePath, string texturePackage = SyntheticTexture.Path)
    {
        var leaf = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        var textureLeaf = texturePackage[(texturePackage.LastIndexOf('/') + 1)..];
        var p = new SyntheticLevelPackage();
        p.Name(packagePath);
        var texture = p.ObjectImport(texturePackage, "/Script/Engine", "Texture2D", textureLeaf);
        var export = p.Export(leaf, p.ScriptClass("/Script/Engine", "MaterialInstanceConstant"), 0);
        p.SetPayload(export, p.Properties(t =>
        {
            t.StructArray("TextureParameterValues", "TextureParameterValue", item =>
            {
                item.TaggedStruct("ParameterInfo", "MaterialParameterInfo", info =>
                {
                    info.NameValue("Name", "Diffuse Map");
                    info.ByteEnum("Association", "EMaterialParameterAssociation", "GlobalParameter");
                });
                item.Object("ParameterValue", texture);
            });
            t.StructArray("TextureStreamingData", "MaterialTextureInfo", item =>
            {
                item.Float("SamplingScale", 1f);
                item.NameValue("TextureName", textureLeaf);
            });
        }));
        return p.Build();
    }

    /// <summary>A smooth colour picture (what a livery mostly is) with a few hard edges.</summary>
    internal static byte[] Picture(int size)
    {
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var o = (y * size + x) * 4;
                rgba[o] = (byte)(x * 255 / (size - 1));
                rgba[o + 1] = (byte)(y * 255 / (size - 1));
                rgba[o + 2] = (byte)(((x / Math.Max(size / 8, 1)) + (y / Math.Max(size / 8, 1))) % 2 == 0 ? 200 : 40);
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }
}
