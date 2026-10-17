using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Textures;

namespace ScumStudio.Modding;

public sealed partial record AssetModRequest
{
    /// <summary>
    /// New pictures (a craftable's recipe icon drawn from its model or read from a picture file): each is written as a new
    /// texture in its template's cooked layout and format (<see cref="TextureWriter"/>) and registered like the template.
    /// </summary>
    public IReadOnlyList<PictureWrite> Pictures { get; init; } = [];
}

public static partial class AssetModBuilder
{
    /// <summary>
    /// Writes <see cref="AssetModRequest.Pictures"/> (a picture whose recipe was not built is left out) and returns the
    /// (template, picture) pairs written, for the registry. A template that cannot be read or written is a warning.
    /// </summary>
    private static List<(string Owner, KeyValuePair<string, string> Pair)> WritePictures(AssetCatalog catalog, AssetModRequest request,
        Dictionary<string, (CookedPackage Package, PackageBytes Bytes, bool IsClone)> built, IReadOnlySet<string> dropped, List<string> warnings)
    {
        var written = new List<(string, KeyValuePair<string, string>)>();
        foreach (var picture in request.Pictures)
        {
            var path = PackageMap.Normalize(picture.Package);
            if (picture.Recipe is { } recipe && (dropped.Contains(PackageMap.Normalize(recipe)) || !built.ContainsKey(PackageMap.Normalize(recipe))))
            {
                continue;
            }

            var template = PackageMap.Normalize(picture.Template);
            try
            {
                var result = TextureWriter.Write(ModdableAssets.ReadPackage(catalog, template), template, path, picture.Rgba, picture.Width, picture.Height);
                built[path] = (CookedPackage.Parse(result.Bytes.UAsset, result.Bytes.UExp, result.UBulk, path), result.Bytes, true);
                written.Add((picture.Owner, new KeyValuePair<string, string>(template, path)));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException
                                           or NotSupportedException or ArgumentException)
            {
                warnings.Add($"{picture.Owner}: its picture {path} was not written ({ex.Message}); the crafting menu shows no picture for it.");
            }
        }

        return written;
    }
}
