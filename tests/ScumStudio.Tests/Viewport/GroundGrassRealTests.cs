using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Pak;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// thezombie_97: "is it normal that the grass dissapears?" The game's ground grass is scattered at run time from each
/// landscape component's cooked grass densities and its grass types (<c>LandscapeGrassType</c>: meshes and instances per
/// 10 m²). Measured on a real meadow (<c>SCUM_PAKS</c>): how many grass instances a preview within 100 m of the camera
/// would have to draw, the number behind the choice to say so in the Map rather than draw it.
/// </summary>
public sealed class GroundGrassRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Tile = MapSlice.MapsPath + "Landscape_B_4_2b";
    private const float RadiusCm = 10_000f;

    [PaksFact]
    public void AMeadowHoldsFarMoreGrassThanAPreviewCouldDraw()
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var layers = LandscapeLayerReader.ReadPackage(catalog, Tile).ToDictionary(c => c.Name);
        var surfaces = LandscapeExtractor.Extract(catalog, Tile, new LandscapeExtractOptions { Step = 16, ComputeNormals = false })
            .SelectMany(p => p.Components).ToDictionary(c => c.Name, c => c.Surface!);

        // Instances per 10 m² of each grass type (all its varieties) and its meshes.
        var perTenSquareMetres = new Dictionary<string, (float Density, int Meshes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in layers.Values.SelectMany(c => c.Grass).Select(g => g.GrassTypePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var varieties = catalog.LoadObject(path).GetOrDefault<FStructFallback[]>("GrassVarieties") ?? [];
            perTenSquareMetres[path] = (varieties.Sum(v => Number(v, "GrassDensity")), varieties.Count(v => v.GetOrDefault<FPackageIndex>("GrassMesh") is { IsNull: false }));
        }

        // The meadow: the component with the most land grass; the camera over its middle.
        static bool Land(LandscapeGrassDensity g) => !g.GrassTypeName.Contains("Underwater", StringComparison.OrdinalIgnoreCase);
        var meadow = layers.Values.MaxBy(c => c.Grass.Where(Land).Sum(g => g.MeanDensity))!;
        var centre = surfaces[meadow.Name].WorldPosition(meadow.ComponentSizeQuads / 2, meadow.ComponentSizeQuads / 2);

        double instances = 0;
        var samples = 0;
        foreach (var component in layers.Values)
        {
            var surface = surfaces[component.Name];
            var area = surface.QuadSizeCm * surface.QuadSizeCm / 1e5f; // one sample's ground in units of 10 m²
            for (var y = 0; y < component.SampleCount; y++)
            {
                for (var x = 0; x < component.SampleCount; x++)
                {
                    var p = surface.WorldPosition(x, y);
                    if (((p.X - centre.X) * (p.X - centre.X)) + ((p.Y - centre.Y) * (p.Y - centre.Y)) > RadiusCm * RadiusCm)
                    {
                        continue;
                    }

                    samples++;
                    var i = (y * component.SampleCount) + x;
                    foreach (var grass in component.Grass.Where(g => Land(g) && g.Density.Length > i && g.GrassTypePath is not null))
                    {
                        instances += grass.Density[i] / 255.0 * perTenSquareMetres[grass.GrassTypePath!].Density * area;
                    }
                }
            }
        }

        output.WriteLine($"grass types in {Tile}: {string.Join(", ", perTenSquareMetres.Select(t => $"{t.Key[(t.Key.LastIndexOf('.') + 1)..]} {t.Value.Density:0.#}/10 m² ({t.Value.Meshes} meshes)"))}");
        output.WriteLine($"meadow {meadow.Name} around ({centre.X:0}, {centre.Y:0}): {samples:N0} samples within {RadiusCm / 100f:0} m of the camera (this tile only), about {instances:N0} grass instances the game scatters there");
        Assert.True(perTenSquareMetres.Values.Any(t => t.Density > 0f && t.Meshes > 0), "no grass type with a density and a mesh");
        Assert.True(instances > 10_000, $"{instances:N0} instances");
    }

    /// <summary>A number property of a struct: a plain float or int, or the <c>Default</c> of a per-platform value.</summary>
    private static float Number(FStructFallback s, string name)
    {
        object? value = s.Properties.FirstOrDefault(p => p.Name.Text == name)?.Tag?.GenericValue;
        if (value is FScriptStruct script)
        {
            value = script.StructType;
        }

        if (value is FStructFallback fallback)
        {
            return Number(fallback, "Default");
        }

        var type = value?.GetType();
        var inner = type?.GetField("Default")?.GetValue(value) ?? type?.GetProperty("Default")?.GetValue(value);
        return (inner ?? value) switch
        {
            float f => f,
            int n => n,
            double d => (float)d,
            _ => 0f,
        };
    }
}
