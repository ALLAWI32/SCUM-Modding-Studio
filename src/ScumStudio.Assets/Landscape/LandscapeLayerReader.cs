using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Landscape;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using UeVector4 = CUE4Parse.UE4.Objects.Core.Math.FVector4;

namespace ScumStudio.Assets.Landscape;

/// <summary>One paint layer of a landscape component.</summary>
/// <param name="Name">Layer name: the <c>LandscapeLayerInfoObject</c> name without <c>_LayerInfo</c> (e.g. <c>Forest_Ground</c>).</param>
/// <param name="LayerInfoPath">Object path of the layer info (usually not part of the cooked tile itself), or null.</param>
/// <param name="WeightBlended">
/// True for weight-blended layers (their weights sum to 255 per sample); false for layers painted on top
/// (<c>EraseFoliage</c>), from the combination material's <c>TerrainLayerWeightParameters.bWeightBasedBlend</c>.
/// </param>
/// <param name="Weights">Weight per sample (0..255), <c>SampleCount²</c> bytes, row-major <c>y * SampleCount + x</c> in quad coordinates.</param>
public sealed record LandscapeLayerWeights(string Name, string? LayerInfoPath, bool WeightBlended, byte[] Weights)
{
    /// <summary>Mean weight over the component (0..1): the share of the component's area this layer covers.</summary>
    public double Coverage => Weights.Length == 0 ? 0 : Weights.Sum(w => (double)w) / (255.0 * Weights.Length);
}

/// <summary>Density of one <c>LandscapeGrassType</c> over a component (cooked <c>FLandscapeComponentGrassData</c>).</summary>
/// <param name="GrassTypeName">Grass type object name (e.g. <c>Coastal_Forest_LandscapeGrassType</c>).</param>
/// <param name="GrassTypePath">Grass type object path, or null when it cannot be resolved.</param>
/// <param name="Density">Density per sample (0..255), row-major; <c>SampleCount²</c> bytes when it matches the component grid.</param>
public sealed record LandscapeGrassDensity(string GrassTypeName, string? GrassTypePath, byte[] Density)
{
    /// <summary>Mean density (0..1).</summary>
    public double MeanDensity => Density.Length == 0 ? 0 : Density.Sum(d => (double)d) / (255.0 * Density.Length);
}

/// <summary>
/// The decoded paint layers of one <c>ULandscapeComponent</c>: per-layer weights resampled to one value per quad corner
/// (the same grid as <see cref="LandscapeSurface"/>), the visibility/hole layer and the cooked grass densities.
/// </summary>
/// <param name="Name">Component export name.</param>
/// <param name="SectionBase">Section base (landscape quad coordinates).</param>
/// <param name="ComponentSizeQuads">Quads per side.</param>
/// <param name="Layers">Paint layers in allocation order (without the hole layer).</param>
/// <param name="Holes">Weights of the visibility layer (<c>DataLayer</c>; 255 = hole), or null when the component has none.</param>
/// <param name="Grass">Grass densities.</param>
/// <param name="AllocationCount">Number of <c>WeightmapLayerAllocations</c> of the component (all layers including holes).</param>
/// <param name="Warnings">Problems found while decoding (missing weightmaps, odd grass sizes).</param>
public sealed record LandscapeComponentLayers(
    string Name,
    (int X, int Y) SectionBase,
    int ComponentSizeQuads,
    IReadOnlyList<LandscapeLayerWeights> Layers,
    byte[]? Holes,
    IReadOnlyList<LandscapeGrassDensity> Grass,
    int AllocationCount,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Samples per side (<see cref="ComponentSizeQuads"/> + 1).</summary>
    public int SampleCount => ComponentSizeQuads + 1;

    /// <summary>The layer named <paramref name="name"/> (case-insensitive), or null.</summary>
    public LandscapeLayerWeights? Find(string name) =>
        Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Weight (0..1) of every layer with a non-zero weight at sample (<paramref name="x"/>, <paramref name="y"/>), strongest first.</summary>
    public IReadOnlyList<(string Name, float Weight, bool WeightBlended)> WeightsAt(int x, int y)
    {
        var n = SampleCount;
        var i = Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1);
        var result = new List<(string, float, bool)>();
        foreach (var layer in Layers)
        {
            if (i < layer.Weights.Length && layer.Weights[i] > 0)
            {
                result.Add((layer.Name, layer.Weights[i] / 255f, layer.WeightBlended));
            }
        }

        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return result;
    }

    /// <summary>True when sample (<paramref name="x"/>, <paramref name="y"/>) is a hole (visibility weight above half).</summary>
    public bool IsHole(int x, int y)
    {
        if (Holes is null)
        {
            return false;
        }

        var n = SampleCount;
        return Holes[Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1)] > 127;
    }
}

/// <summary>Share of the terrain area covered by one paint layer (see <see cref="LandscapeLayerReader.Summarize"/>).</summary>
/// <param name="Name">Layer name.</param>
/// <param name="Components">Number of components that allocate the layer.</param>
/// <param name="AreaShare">Weighted area share over all summarised components (0..1).</param>
/// <param name="WeightBlended">False for layers painted on top of the blended ones.</param>
public sealed record LandscapeLayerArea(string Name, int Components, double AreaShare, bool WeightBlended);

/// <summary>
/// Reads the paint layers of cooked landscape components (UE 4.27, verified on SCUM's island tiles).
/// <para>Each component lists <c>WeightmapLayerAllocations</c> {LayerInfo, WeightmapTextureIndex, WeightmapTextureChannel}
/// over its <c>WeightmapTextures</c> (B8G8R8A8, decoded to RGBA so channel 0..3 = R, G, B, A). The component's window in a
/// (possibly shared) weightmap starts at <c>round(WeightmapScaleBias.zw * size - 0.5)</c>, and quad coordinates map to
/// texels with the same subsection rule as the heightmap (<see cref="LandscapeExtractor.TexelIndex"/>). Whether a layer is
/// weight-blended comes from the per-combination <c>LandscapeMaterialInstanceConstant</c>'s static parameters (the
/// component's material instance or one of its parents). Grass densities come from the cooked
/// <c>GrassData</c> (one map per <c>LandscapeGrassType</c>).</para>
/// </summary>
public static class LandscapeLayerReader
{
    /// <summary>Name of UE's visibility (hole) layer.</summary>
    public const string HoleLayerName = "DataLayer";

    /// <summary>Layers painted on top of the weight-blended ones when the material does not say otherwise.</summary>
    private static readonly HashSet<string> NonBlendedByDefault = new(StringComparer.OrdinalIgnoreCase) { "EraseFoliage", HoleLayerName };

    /// <summary>Layer name of a layer info object name (<c>Forest_Ground_LayerInfo</c> → <c>Forest_Ground</c>).</summary>
    public static string LayerNameOf(string layerInfoName)
    {
        ArgumentNullException.ThrowIfNull(layerInfoName);
        const string suffix = "_LayerInfo";
        return layerInfoName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? layerInfoName[..^suffix.Length] : layerInfoName;
    }

    /// <summary>
    /// First texel of a component's window in a weightmap of <paramref name="textureSize"/> texels, from the
    /// <c>WeightmapScaleBias</c> offset (which points at the texel centre: <c>(origin + 0.5) / size</c>).
    /// </summary>
    public static int WeightmapOrigin(float scaleBiasOffset, int textureSize) => (int)MathF.Round(scaleBiasOffset * textureSize - 0.5f);

    /// <summary>
    /// Resamples one channel of an RGBA weightmap window to one byte per quad corner
    /// (<c>(sizeQuads + 1)²</c> values written to <paramref name="destination"/>), honouring the subsection layout.
    /// Texels outside the texture read as 0.
    /// </summary>
    public static void ExtractChannel(ReadOnlySpan<byte> rgba, int textureWidth, int textureHeight, int originX, int originY, int channel,
        int sizeQuads, int subsectionQuads, int numSubsections, Span<byte> destination)
    {
        if ((uint)channel > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "Channel must be 0..3 (R, G, B, A).");
        }

        var n = sizeQuads + 1;
        if (destination.Length < n * n)
        {
            throw new ArgumentException($"Destination needs {n * n} bytes.", nameof(destination));
        }

        for (var y = 0; y < n; y++)
        {
            var ty = originY + LandscapeExtractor.TexelIndex(y, subsectionQuads, numSubsections);
            for (var x = 0; x < n; x++)
            {
                var tx = originX + LandscapeExtractor.TexelIndex(x, subsectionQuads, numSubsections);
                destination[y * n + x] = (uint)tx < (uint)textureWidth && (uint)ty < (uint)textureHeight
                    ? rgba[(ty * textureWidth + tx) * 4 + channel]
                    : (byte)0;
            }
        }
    }

    /// <summary>
    /// Area share of every layer over <paramref name="components"/> (each component weighted by its sample count), largest
    /// first. Blended shares sum to about 1 when every sample is painted.
    /// </summary>
    public static IReadOnlyList<LandscapeLayerArea> Summarize(IEnumerable<LandscapeComponentLayers> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var sums = new Dictionary<string, (int Components, double Weight, bool Blended)>(StringComparer.OrdinalIgnoreCase);
        double totalSamples = 0;
        foreach (var component in components)
        {
            var samples = (double)component.SampleCount * component.SampleCount;
            totalSamples += samples;
            foreach (var layer in component.Layers)
            {
                var e = sums.TryGetValue(layer.Name, out var existing) ? existing : (Components: 0, Weight: 0d, Blended: layer.WeightBlended);
                sums[layer.Name] = (e.Components + 1, e.Weight + layer.Coverage * samples, e.Blended && layer.WeightBlended);
            }
        }

        return sums.Select(kv => new LandscapeLayerArea(kv.Key, kv.Value.Components, totalSamples > 0 ? kv.Value.Weight / totalSamples : 0, kv.Value.Blended))
            .OrderByDescending(a => a.AreaShare).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads the layers of every landscape component of the level package at <paramref name="packagePath"/>.</summary>
    public static IReadOnlyList<LandscapeComponentLayers> ReadPackage(AssetCatalog catalog, string packagePath, bool includeGrass = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var package = catalog.LoadPackage(packagePath);
        var result = new List<LandscapeComponentLayers>();
        foreach (var export in package.GetExports())
        {
            if (!LandscapeExtractor.IsLandscapeProxyClass(export.ExportType))
            {
                continue;
            }

            foreach (var index in export.GetOrDefault<FPackageIndex[]>("LandscapeComponents") ?? [])
            {
                if (index.Load() is { } component)
                {
                    result.Add(Read(component, includeGrass));
                }
            }
        }

        return result;
    }

    /// <summary>Reads the paint layers (and optionally the grass densities) of one <c>ULandscapeComponent</c>.</summary>
    public static LandscapeComponentLayers Read(UObject component, bool includeGrass = true)
    {
        ArgumentNullException.ThrowIfNull(component);
        var warnings = new List<string>();
        var sizeQuads = component.GetOrDefault("ComponentSizeQuads", 63);
        var subsectionQuads = component.GetOrDefault("SubsectionSizeQuads", sizeQuads);
        var numSubsections = component.GetOrDefault("NumSubsections", 1);
        var sectionBase = (component.GetOrDefault("SectionBaseX", 0), component.GetOrDefault("SectionBaseY", 0));
        var scaleBias = component.GetOrDefault("WeightmapScaleBias", new UeVector4(0f, 0f, 0f, 0f));
        var n = sizeQuads + 1;

        var textures = component.GetOrDefault<FPackageIndex[]>("WeightmapTextures") ?? [];
        var decoded = new TextureImage?[textures.Length];
        var allocations = component.GetOrDefault<FStructFallback[]>("WeightmapLayerAllocations") ?? [];
        var blendFlags = ReadBlendFlags(component);
        var layers = new List<LandscapeLayerWeights>();
        byte[]? holes = null;
        foreach (var allocation in allocations)
        {
            var info = allocation.GetOrDefault<FPackageIndex?>("LayerInfo");
            var infoName = info is { IsNull: false } ? info.Name : string.Empty;
            var name = infoName.Length == 0 ? "(unnamed)" : LayerNameOf(infoName);
            string? infoPath = null;
            try
            {
                infoPath = info?.ResolvedObject?.GetPathName();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The path is informational only.
            }

            var textureIndex = allocation.GetOrDefault<byte>("WeightmapTextureIndex");
            var channel = allocation.GetOrDefault<byte>("WeightmapTextureChannel");
            var weights = new byte[n * n];
            if (textureIndex < textures.Length && channel <= 3)
            {
                try
                {
                    var image = decoded[textureIndex] ??= textures[textureIndex].Load<UTexture2D>() is { } texture ? TextureDecoder.Decode(texture) : null;
                    if (image is null)
                    {
                        warnings.Add($"{component.Name}: weightmap {textureIndex} of {name} could not be loaded.");
                    }
                    else
                    {
                        ExtractChannel(image.Rgba, image.Width, image.Height, WeightmapOrigin(scaleBias.Z, image.Width), WeightmapOrigin(scaleBias.W, image.Height),
                            channel, sizeQuads, subsectionQuads, numSubsections, weights);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException)
                {
                    warnings.Add($"{component.Name}: weightmap {textureIndex} of {name}: {ex.Message}");
                }
            }
            else
            {
                warnings.Add($"{component.Name}: {name} points at weightmap {textureIndex} channel {channel}, the component has {textures.Length} weightmap(s).");
            }

            if (string.Equals(name, HoleLayerName, StringComparison.OrdinalIgnoreCase))
            {
                holes = weights;
                continue;
            }

            var blended = blendFlags.TryGetValue(name, out var flag) ? flag : !NonBlendedByDefault.Contains(name);
            layers.Add(new LandscapeLayerWeights(name, infoPath, blended, weights));
        }

        var grass = includeGrass ? ReadGrass(component, n, warnings) : [];
        return new LandscapeComponentLayers(component.Name, sectionBase, sizeQuads, layers, holes, grass, allocations.Length, warnings);
    }

    /// <summary>
    /// <c>bWeightBasedBlend</c> per layer name from the static parameters of the component's material instance or the
    /// first parent that has them (the per-layer-combination <c>LandscapeMaterialInstanceConstant</c>).
    /// </summary>
    private static Dictionary<string, bool> ReadBlendFlags(UObject component)
    {
        var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var index in component.GetOrDefault<FPackageIndex[]>("MaterialInstances") ?? [])
            {
                UUnrealMaterial? material = index.Load<UMaterialInstance>();
                for (var depth = 0; depth < 8 && material is UMaterialInstance instance; depth++)
                {
                    if (instance.StaticParameters?.TerrainLayerWeightParameters is { Length: > 0 } parameters)
                    {
                        foreach (var p in parameters)
                        {
                            var name = p.ParameterInfo?.Name.Text ?? p.Name;
                            if (!string.IsNullOrEmpty(name))
                            {
                                flags.TryAdd(name, p.bWeightBasedBlend);
                            }
                        }

                        break;
                    }

                    material = instance.Parent;
                }

                if (flags.Count > 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Material chains that cannot be loaded (parents outside the tile) fall back to the defaults.
        }

        return flags;
    }

    private static List<LandscapeGrassDensity> ReadGrass(UObject component, int n, List<string> warnings)
    {
        var result = new List<LandscapeGrassDensity>();
        if (component is not ULandscapeComponent { GrassData.WeightData: { } weightData })
        {
            return result;
        }

        foreach (var (key, data) in weightData)
        {
            if (data is null)
            {
                continue;
            }

            string? path = null;
            try
            {
                path = key.ResolvedObject?.GetPathName();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The path is informational only.
            }

            if (data.Length != n * n)
            {
                warnings.Add($"{component.Name}: grass {key.Name} has {data.Length} samples, expected {n * n}.");
            }

            result.Add(new LandscapeGrassDensity(key.Name, path, data));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.GrassTypeName, b.GrassTypeName));
        return result;
    }
}
