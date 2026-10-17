using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Textures;

namespace ScumStudio.Level.Export;

public static partial class CraftablesExporter
{
    /// <summary>The drawing of an export that cannot draw (<see cref="ExportOptions.ModelPictures"/> null).</summary>
    private static readonly Func<CraftPictureRequest, TextureImage?> NoDrawing = _ => null;

    /// <summary>
    /// The pictures of <paramref name="plan"/> (<see cref="CraftablesPlan.Pictures"/>) drawn from each craftable's model or
    /// read from its picture file, fitted to the template icon's size, as <see cref="AssetModRequest.Pictures"/>. One that
    /// cannot be made is a warning and is dropped (<see cref="CraftPictures.Without"/>): its recipe keeps the template's.
    /// </summary>
    private static CraftablesPlan MakePictures(CraftablesPlan plan, AssetCatalog catalog, string? projectDirectory, Func<CraftPictureRequest, TextureImage?> draw, List<string> warnings)
    {
        var writes = new List<PictureWrite>();
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var picture in plan.Pictures)
        {
            try
            {
                var (width, height) = CraftablesPlanner.PictureSize(catalog, picture.Template);
                TextureImage? image;
                if (picture.Source == CraftPictureSource.File)
                {
                    image = TextureWrites.ResolveFile(projectDirectory, picture.File ?? string.Empty) is { } full && File.Exists(full)
                        ? ImageFiles.Load(full)
                        : throw new FileNotFoundException($"the picture file {picture.File} is not in the project folder");
                }
                else
                {
                    var folder = picture.Imported is { } imported ? ImportedFolder(projectDirectory, imported, out _) : null;
                    image = draw(new CraftPictureRequest(picture.Mesh, Math.Max(width, height), folder));
                }

                if (image is null)
                {
                    warnings.Add(ReferenceEquals(draw, NoDrawing)
                        ? $"{picture.Craftable}: this export cannot draw 3D models (no OpenGL 4.3), so it shows its template's picture: pick a picture of the game or a picture file for it."
                        : $"{picture.Craftable}: its 3D model {PackageMap.Leaf(picture.Mesh)} could not be drawn, so it shows its template's picture: pick a picture of the game or a picture file for it.");
                    failed.Add(PackageMap.Normalize(picture.Package));
                    continue;
                }

                writes.Add(new PictureWrite(picture.Package, picture.Template, CraftPictures.Fit(image, width, height), width, height, picture.Craftable, picture.Recipe));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                warnings.Add($"{picture.Craftable}: no picture of its own ({ex.Message}); it shows its template's.");
                failed.Add(PackageMap.Normalize(picture.Package));
            }
        }

        plan = CraftPictures.Without(plan, failed);
        return plan with { Request = plan.Request with { Pictures = writes } };
    }
}
