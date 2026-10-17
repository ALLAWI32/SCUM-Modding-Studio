using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Registry;
using ScumStudio.Pak;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Craftables made from meshes the game has no picture of (Jenni, Discord D07: a sofa, a bed, a fridge and a table all
/// showed the improvised chair in the base building menu) on the real game files (<c>SCUM_PAKS</c> and the key; skipped
/// otherwise) and OpenGL 4.3: each recipe's <c>Icon</c> names a new texture of the mod drawn from its own model, in the stock
/// icon's format, with a transparent background, unlike the chair's and unlike each other's, registered like the stock
/// icon; and the name typed is the recipe's caption and description and the element's name.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class CraftPicturesRealTests
{
    internal static readonly (string Name, string Mesh)[] Furniture =
    [
        ("Old Sofa", "/Game/ConZ_Files/Models/Objects/Indoor/70s_Furniture/SM_Couch_01"),
        ("سرير المستشفى", "/Game/ConZ_Files/Models/Buildings/Hospital/Props/SM_Hospital_Bed_01_Dirty"),
        ("Kitchen Fridge", "/Game/ConZ_Files/Models/Objects/Indoor/70s_Furniture/SM_Fridge_01_Merged_Closed"),
        ("Armory Table", "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01"),
    ];

    /// <summary>Where the contact sheet of the pictures is saved (outside the repository: rendered from the game's meshes).</summary>
    private const string ArtFolder = @"C:\Users\ALLAWI\sswt\art\r35-icons";

    private static AssetCatalog Open() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    internal static CraftablesFile File() => new()
    {
        Items = [.. Furniture.Select(f => new Craftable { Name = f.Name, Source = f.Mesh, Mesh = f.Mesh, Ingredients = [new("CI_Plank", 2)] })],
    };

    [PaksFact]
    public async Task EachFurnitureShowsAPictureOfItsOwnModel()
    {
        Assert.True(GlTestEnvironment.SkipReason is null, "OpenGL 4.3 is needed to draw the pictures: " + GlTestEnvironment.SkipReason);
        using var catalog = Open();
        var chairIcon = CraftablesPlanner.RecipeIcon(catalog, CraftablesPlanner.FurnitureRecipe)!;
        var chair = TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(chairIcon));
        var file = File();
        var plan = CraftablesPlanner.Plan(catalog, file.Items);
        Assert.Equal(4, plan.Pictures.Count);
        Assert.All(plan.Pictures, p => Assert.Equal(CraftPictureSource.Model, p.Source));

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftpictures-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var renderer = new CraftIconRenderer(catalog);
            var result = await CraftablesExporter.ExportAsync(file, "MyMod", catalog,
                new ExportOptions { OutputDirectory = dir, WritePak = false, ModelPictures = renderer.Render }, ProjectSourceRole.Client);
            Assert.DoesNotContain(result!.Warnings, w => w.Contains("picture", StringComparison.OrdinalIgnoreCase));
            using var staged = AssetCatalog.OpenLoose(result.StagingDirectory);
            var registry = AssetRegistryFile.Load(Path.Combine([result.StagingDirectory, .. AssetModBuilder.AssetRegistryPath.Split('/')]));
            var stockRecord = AssetRegistryFile.Parse(RegistryMerge.ReadStock(catalog)!).Assets.Single(a => a.ObjectPath == chairIcon);
            var icons = new List<TextureImage>();
            foreach (var (entry, craftable) in plan.Entries.Zip(CraftablesFile.WithTokens(file.Items)))
            {
                // The recipe names the new picture, a package of the mod next to it, never a stock path.
                var picture = CraftPictures.PackageFor(entry.Recipe, craftable.Token!);
                Assert.Equal(PackageMap.Folder(entry.Recipe), PackageMap.Folder(picture));
                Assert.False(catalog.PackageExists(picture));
                var objectPath = picture + "." + PackageMap.Leaf(picture);
                var recipe = Staged(result, entry.Recipe);
                var props = recipe.ReadProperties(RecipeIngredients.RecipeExport(recipe));
                Assert.Equal(objectPath, ((SoftObjectValue)props.Find(CraftablesPlanner.IconProperty)!.Value).AssetPath);
                Assert.DoesNotContain(recipe.Names, n => n.Contains("ICO_Chair_Improvised_Wood", StringComparison.Ordinal));

                // The name typed is what the menu shows: the recipe's caption and description, and the placed element's name.
                Assert.Equal(craftable.Name, Text(props, "Caption"));
                Assert.Equal(craftable.Name, Text(props, "Description"));
                var element = Staged(result, entry.Product);
                Assert.Equal(craftable.Name, Text(element.ReadProperties(Enumerable.Range(0, element.Exports.Count).First(i => element.ResolveName(element.Exports[i].ObjectName).StartsWith("Default__", StringComparison.Ordinal))), "_name"));

                // The stock icon's format: BC7, one mip in the package, the UI group, never streamed.
                var cooked = Staged(result, picture);
                var texture = CookedTexture2D.Read(cooked, CookedTexture2D.FindExport(cooked));
                Assert.Equal("PF_BC7", texture.Platforms[0].PixelFormat);
                Assert.Single(texture.Platforms[0].Mips);
                Assert.True(texture.Platforms[0].Mips[0].IsInline);
                Assert.EndsWith("TEXTUREGROUP_UI", ((EnumValue)texture.Properties.Find("LODGroup")!.Value).Value, StringComparison.Ordinal);

                // CUE4Parse reads it: the object alone on a transparent background, not the chair.
                var icon = TextureDecoder.Decode(staged.LoadObject<UTexture2D>(objectPath));
                Assert.Equal((chair.Width, chair.Height), (icon.Width, icon.Height));
                Assert.Equal(0, icon.Rgba[3]);
                Assert.Equal(0, icon.Rgba[^1]);
                var clear = Share(icon, a => a == 0);
                var solid = Share(icon, a => a >= 200);
                Assert.True(clear >= 0.2, $"{craftable.Name}: only {clear:P0} of the picture is transparent");
                Assert.True(solid >= 0.1, $"{craftable.Name}: only {solid:P0} of the picture is drawn");
                var fromChair = Difference(icon, chair);
                Assert.True(fromChair >= 20, $"{craftable.Name}: its picture differs from the chair's by only {fromChair:F1}");

                // Registered like the stock icon (a Texture2D record with the same tags).
                var record = Assert.Single(registry.Assets, a => a.ObjectPath == objectPath);
                Assert.Equal("Texture2D", record.AssetClass);
                Assert.Equal(stockRecord.TagCount, record.TagCount);
                Assert.Contains(result.Registered, r => r.ObjectPath == objectPath);
                icons.Add(icon);
            }

            for (var i = 0; i < icons.Count; i++)
            {
                for (var j = i + 1; j < icons.Count; j++)
                {
                    var d = Difference(icons[i], icons[j]);
                    Assert.True(d >= 15, $"{Furniture[i].Name} and {Furniture[j].Name} look alike ({d:F1})");
                }
            }

            // The sheet ends with the chair drawn the same way beside the game's own chair icon (the view and light compared).
            var chairMesh = CraftablesPlanner.StaticMeshImports(catalog, "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Chair_Improvised_Wood").First();
            var drawnChair = renderer.Render(new CraftPictureRequest(chairMesh, chair.Width))!;
            ContactSheet([.. icons, drawnChair, chair], Path.Combine(ArtFolder, "contact-sheet.png"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [PaksFact]
    public void ARenameChangesTheNameShownButNoAssetName()
    {
        using var catalog = Open();
        var items = CraftablesFile.WithTokens(File().Items);
        var before = CraftablesPlanner.Plan(catalog, items);
        var renamed = items.Select(c => c with { Name = "Renamed " + c.Name }).ToList();
        var after = CraftablesPlanner.Plan(catalog, renamed);

        Assert.Equal(before.Entries.Select(e => (e.Recipe, e.Product)), after.Entries.Select(e => (e.Recipe, e.Product)));
        Assert.Equal(before.Pictures.Select(p => p.Package), after.Pictures.Select(p => p.Package));
        foreach (var (entry, craftable) in after.Entries.Zip(renamed))
        {
            Assert.Contains(after.Request.Edits[entry.Recipe], e => e.Path == "Caption" && e.Value == craftable.Name);
            Assert.Contains(after.Request.Edits[entry.Product], e => e.Path == "_name" && e.Value == craftable.Name);
        }
    }

    [PaksFact]
    public void AGamePictureOrAPictureFileCanStillBeChosen()
    {
        using var catalog = Open();
        const string bedIcon = "/Game/ConZ_Files/Items/Item_Icons/AsianFurnitureDLC/ICO_Bed_Asian";
        var items = CraftablesFile.WithTokens(File().Items);
        var plan = CraftablesPlanner.Plan(catalog, [items[0] with { Icon = bedIcon }, items[1] with { Icon = "icons/" + items[1].Token + ".png" }, items[2] with { Icon = CraftPictures.FromModel }]);

        Assert.Contains(plan.Request.Clones[0].Redirects, r => r.Value == bedIcon);
        Assert.Equal([CraftPictureSource.File, CraftPictureSource.Model], plan.Pictures.Select(p => p.Source));
        Assert.Equal("icons/" + items[1].Token + ".png", plan.Pictures[0].File);

        // An item recipe stores no picture (the menu shows the item's): a power craftable's copied generator gets it.
        var power = CraftablesPlanner.Plan(catalog, [items[3] with { Kind = CraftKind.Power }]);
        var picture = Assert.Single(power.Pictures);
        Assert.Equal(PackageMap.Normalize(InventoryIconsOf(catalog)), picture.Template);
        Assert.Contains(power.Request.Clones[0].Redirects, r => r.Key == picture.Template && r.Value == picture.Package);
    }

    private static string InventoryIconsOf(AssetCatalog catalog) =>
        ScumStudio.Modding.Catalog.InventoryIcons.FindIconPath(catalog, CraftablesPlanner.PowerItem + "_ES")!;

    private static CookedPackage Staged(ExportResult result, string packagePath)
    {
        var asset = Assert.Single(result.Assets, a => string.Equals(a.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase));
        var header = Path.Combine([result.StagingDirectory, .. asset.VirtualPath.Split('/')]);
        var ubulk = Path.ChangeExtension(header, ".ubulk");
        return CookedPackage.Parse(System.IO.File.ReadAllBytes(header), System.IO.File.ReadAllBytes(Path.ChangeExtension(header, ".uexp")),
            System.IO.File.Exists(ubulk) ? System.IO.File.ReadAllBytes(ubulk) : null, packagePath);
    }

    private static string? Text(PropertyBlock block, string name) => block.Find(name)?.Value is TextValue text ? text.SourceString : null;

    private static double Share(TextureImage image, Func<byte, bool> alpha)
    {
        var count = 0;
        for (var i = 3; i < image.Rgba.Length; i += 4)
        {
            count += alpha(image.Rgba[i]) ? 1 : 0;
        }

        return count / (image.Rgba.Length / 4.0);
    }

    /// <summary>Mean difference (0-255) of two pictures' colours over black (premultiplied) and coverage.</summary>
    internal static double Difference(TextureImage a, TextureImage b)
    {
        var bRgba = b.Width == a.Width && b.Height == a.Height ? b.Rgba : ImageFiles.Fit(b, a.Width, a.Height);
        double sum = 0;
        for (var i = 0; i < a.Rgba.Length; i += 4)
        {
            var aa = a.Rgba[i + 3] / 255.0;
            var ba = bRgba[i + 3] / 255.0;
            for (var c = 0; c < 3; c++)
            {
                sum += Math.Abs((a.Rgba[i + c] * aa) - (bRgba[i + c] * ba));
            }

            sum += Math.Abs(a.Rgba[i + 3] - bRgba[i + 3]);
        }

        return sum / (a.Rgba.Length);
    }

    /// <summary>The pictures side by side (256 px each) on a grey checkerboard, so their transparency shows.</summary>
    internal static void ContactSheet(IReadOnlyList<TextureImage> pictures, string path)
    {
        const int tile = 256;
        var width = tile * pictures.Count;
        var rgba = new byte[width * tile * 4];
        for (var n = 0; n < pictures.Count; n++)
        {
            var small = ImageFiles.Fit(pictures[n], tile, tile);
            for (var y = 0; y < tile; y++)
            {
                for (var x = 0; x < tile; x++)
                {
                    var s = ((y * tile) + x) * 4;
                    var d = ((y * width) + (n * tile) + x) * 4;
                    var check = ((x / 16) + (y / 16)) % 2 == 0 ? 200 : 160;
                    var a = small[s + 3] / 255.0;
                    for (var c = 0; c < 3; c++)
                    {
                        rgba[d + c] = (byte)Math.Round((small[s + c] * a) + (check * (1 - a)));
                    }

                    rgba[d + 3] = 255;
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PngWriter.Save(new TextureImage("sheet", width, tile, rgba, "PF_R8G8B8A8", 0, width, tile, true, false), path);
        for (var n = 0; n < pictures.Count; n++)
        {
            PngWriter.Save(pictures[n], Path.Combine(Path.GetDirectoryName(path)!, $"icon-{n}.png"));
        }
    }
}
