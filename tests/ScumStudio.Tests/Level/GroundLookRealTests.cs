using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "the ground snowy; other trees on the map". The export writes the snow texture under the grass texture's path,
/// the snow grass type under the green one's and a pine under an oak's, and the game reads them as those. Real game files
/// only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class GroundLookRealTests
{
    private const string Grass = "/Game/ConZ_Files/Landscape/LandscapeTextures/T_Grass_Continental_D";
    private const string Snow = "/Game/ConZ_Files/Materials/Snow/T_GroundSnow_03_D";

    private readonly ITestOutputHelper _output;

    public GroundLookRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SnowAndASwappedTreeAreWrittenUnderTheGamesPaths()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var trees = AssetDumper.Packages.Where(p => p.ClassName == "StaticMesh" && p.PackagePath.Contains("/Foliage/", StringComparison.Ordinal) && p.PackagePath.Contains("/Trees/", StringComparison.Ordinal)).Select(p => p.PackagePath).ToList();
        var oak = trees.First(t => t.Contains("Oak", StringComparison.OrdinalIgnoreCase));
        var pine = trees.First(t => t.Contains("Pine", StringComparison.OrdinalIgnoreCase));

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-looks-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var project = await Project.CreateAsync(Path.Combine(dir, "project"), "Winter");
            var snow = GroundLooks.Edits(project.State, GroundLook.Snow, catalog.PackageExists);
            Assert.True(snow.Count > 10, snow.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            project.Apply(new BatchOp("Snow", snow));
            project.Apply(new ReplaceAssetOp(oak, null, pine));
            var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), WritePak = false });
            _output.WriteLine(string.Join(Environment.NewLine, result.Warnings));
            Assert.DoesNotContain(result.Warnings, w => w.Contains("not replaced", StringComparison.Ordinal));

            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var drawn = (UTexture2D)written.LoadObject(Grass + ".T_Grass_Continental_D");
            var snowTexture = (UTexture2D)catalog.LoadObject(Snow + ".T_GroundSnow_03_D");
            var grassTexture = (UTexture2D)catalog.LoadObject(Grass + ".T_Grass_Continental_D");
            _output.WriteLine($"grass {grassTexture.Format} {grassTexture.PlatformData.SizeX}, snow {snowTexture.Format} {snowTexture.PlatformData.SizeX}, written {drawn.Format} {drawn.PlatformData.SizeX}");
            Assert.Equal(snowTexture.Format, drawn.Format);
            Assert.Equal(snowTexture.PlatformData.SizeX, drawn.PlatformData.SizeX);
            Assert.Equal(snowTexture.PlatformData.Mips.Length, drawn.PlatformData.Mips.Length);
            Assert.NotNull(drawn.GetFirstMip()?.BulkData?.Data); // the pixels came along (.ubulk)

            // The pine is read as the oak, with the pine's model.
            var oakLeaf = oak[(oak.LastIndexOf('/') + 1)..];
            var pineLeaf = pine[(pine.LastIndexOf('/') + 1)..];
            var swapped = (UStaticMesh)written.LoadObject(oak + "." + oakLeaf);
            var original = (UStaticMesh)catalog.LoadObject(pine + "." + pineLeaf);
            Assert.Equal(original.RenderData!.LODs![0].VertexBuffer!.NumVertices, swapped.RenderData!.LODs![0].VertexBuffer!.NumVertices);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
