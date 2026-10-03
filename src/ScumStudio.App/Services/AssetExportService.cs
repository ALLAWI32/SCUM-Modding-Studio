using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Export;
using ScumStudio.Assets.Meshes;
using ScumStudio.Assets.Textures;

namespace ScumStudio.App.Services;

/// <summary>Export actions of the Assets page, wired to the ScumStudio.Assets helpers.</summary>
public static class AssetExportService
{
    /// <summary>True for texture classes that <see cref="TextureDecoder"/> handles.</summary>
    public static bool IsTextureClass(string? className) =>
        className is not null && className.EndsWith("Texture2D", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for static and skeletal meshes.</summary>
    public static bool IsMeshClass(string? className) =>
        className is not null &&
        (PackageIndex.ClassNameMatches(className, "StaticMesh") || PackageIndex.ClassNameMatches(className, "SkeletalMesh"));

    /// <summary>True for Blueprints that can show a model (not UI widgets or animation graphs).</summary>
    public static bool IsBlueprintClass(string? className) =>
        className is not null && className.Contains("Blueprint", StringComparison.OrdinalIgnoreCase)
        && !className.Contains("Widget", StringComparison.OrdinalIgnoreCase)
        && !className.Contains("Anim", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for materials and material instances (not physical materials, functions or parameter collections).</summary>
    public static bool IsMaterialClass(string? className) =>
        className is not null && className.Contains("Material", StringComparison.OrdinalIgnoreCase)
        && !className.Contains("Physical", StringComparison.OrdinalIgnoreCase)
        && !className.Contains("Function", StringComparison.OrdinalIgnoreCase)
        && !className.Contains("Collection", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes the package's texture (largest available mip) and writes it as PNG.</summary>
    /// <returns>The decoded image (for its size/format).</returns>
    /// <exception cref="InvalidDataException">The package holds no <c>Texture2D</c>.</exception>
    public static async Task<TextureImage> ExportTexturePngAsync(AssetCatalog catalog, string packagePath, string outputPath, CancellationToken cancellationToken = default)
    {
        var texture = catalog.LoadFirstExport<UTexture2D>(packagePath)
                      ?? throw new InvalidDataException($"{packagePath} does not contain a Texture2D.");
        var image = TextureDecoder.Decode(texture);
        await PngWriter.SaveAsync(image, outputPath, cancellationToken).ConfigureAwait(false);
        return image;
    }

    /// <summary>Extracts LOD 0 of the package's static or skeletal mesh and writes it as glTF (+ .bin).</summary>
    /// <returns>The files written.</returns>
    /// <exception cref="InvalidDataException">The package holds no mesh.</exception>
    public static Task<IReadOnlyList<string>> ExportMeshGltfAsync(AssetCatalog catalog, string packagePath, string outputPath, CancellationToken cancellationToken = default)
    {
        UObject mesh = (UObject?)catalog.LoadFirstExport<UStaticMesh>(packagePath)
                       ?? catalog.LoadFirstExport<USkeletalMesh>(packagePath)
                       ?? throw new InvalidDataException($"{packagePath} does not contain a StaticMesh or SkeletalMesh.");
        var data = MeshExtractor.Extract(mesh);
        return GltfExporter.SaveAsync(data, outputPath, cancellationToken: cancellationToken);
    }
}
