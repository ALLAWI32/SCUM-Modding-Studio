using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Textures;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Formats;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The pictures craftables get when the game has none of them (Jenni, Discord D07: every craftable showed the improvised
/// chair): where the new texture goes (next to the recipe, never a numbered leaf), how it is written and registered like its
/// template, how a picture that could not be made leaves the recipe on the template's, and how a drawn frame becomes a
/// square icon on a transparent background.
/// </summary>
public sealed class CraftPicturesTests
{
    private const string Recipe = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_SS_Bed";
    private const string ChairIcon = "/Game/ConZ_Files/Items/Item_Icons/Modular_BB_Icons/ICO_Chair_Improvised_Wood";

    [Fact]
    public void ThePictureIsANewPackageNextToItsRecipe()
    {
        Assert.Equal("/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/ICO_SS_Bed", CraftPictures.PackageFor(Recipe, "SS_Bed"));

        // A recipe in another folder (the craftable's own folder) takes its picture along.
        Assert.Equal("/Game/Mods/Jenni/Furniture/ICO_SS_Bed", CraftPictures.PackageFor("/Game/Mods/Jenni/Furniture/CR_SS_Bed", "SS_Bed"));

        // An old token ending in _<digits> would make a numbered FName: the leaf ends in letters.
        var numbered = CraftPictures.PackageFor(Recipe, "SS_Chair_01");
        Assert.EndsWith("/ICO_SS_Chair_01_Icon", numbered, StringComparison.Ordinal);
        Assert.False(TextureWrites.EndsInNumber(numbered));
    }

    [Theory]
    [InlineData("#Model", true, false)]
    [InlineData("#model", true, false)]
    [InlineData("icons/SS_Bed.png", false, true)]
    [InlineData("icons/SS_Bed.JPG", false, true)]
    [InlineData(ChairIcon, false, false)]
    [InlineData("ICO_Chair", false, false)]
    [InlineData(null, false, false)]
    public void TheIconValuesAreTold(string? icon, bool fromModel, bool file)
    {
        Assert.Equal(fromModel, CraftPictures.IsFromModel(icon));
        Assert.Equal(file, CraftPictures.IsProjectFile(icon));
    }

    [Fact]
    public void APictureThatCouldNotBeMadeLeavesTheRecipeOnTheTemplatesPicture()
    {
        var picture = CraftPictures.PackageFor(Recipe, "SS_Bed");
        var clone = new ClonePlan(CraftablesPlanner.FurnitureRecipe, Recipe, [new(CraftablesPlanner.FurnitureRecipe, Recipe)])
        {
            Redirects = [new("/Game/Mesh/SM_Chair", "/Game/Mesh/SM_Bed"), new(ChairIcon, picture)],
        };
        var plan = new CraftablesPlan(new AssetModRequest([clone], new Dictionary<string, IReadOnlyList<TunableEdit>>()), [], [])
        {
            Pictures = [new CraftPicture("Bed", picture, ChairIcon, CraftPictureSource.Model, "/Game/Mesh/SM_Bed") { Recipe = Recipe }],
        };

        var without = CraftPictures.Without(plan, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { picture });

        Assert.Empty(without.Pictures);
        Assert.Equal([new KeyValuePair<string, string>("/Game/Mesh/SM_Chair", "/Game/Mesh/SM_Bed")], without.Request.Clones[0].Redirects);
        Assert.Same(plan, CraftPictures.Without(plan, new HashSet<string>()));
    }

    [Fact]
    public void ThePictureIsWrittenInItsTemplatesFormatAndRegisteredLikeIt()
    {
        using var temp = new LevelTempDirectory();
        SyntheticTexture.WriteLoose(temp.Path, SyntheticTexture.Path, "PF_BC7", 64, srgb: true);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var registry = SyntheticGame.Registry(new SyntheticGame.Record(SyntheticTexture.Path + ".T_Body_D", "Texture2D",
            [("Dimensions", AssetRegistryValue.Ansi("64x64")), ("Format", AssetRegistryValue.Ansi("PF_BC7")), ("LODGroup", AssetRegistryValue.Ansi("TEXTUREGROUP_UI"))]));
        const string picture = "/Game/Mods/Jenni/ICO_SS_Bed";
        var red = Enumerable.Range(0, 64 * 64).SelectMany(i => i % 64 < 32 ? new byte[] { 200, 40, 30, 255 } : new byte[4]).ToArray();
        var request = new AssetModRequest([], new Dictionary<string, IReadOnlyList<TunableEdit>>())
        {
            BaseRegistry = registry.Save(),
            Pictures =
            [
                new PictureWrite(picture, SyntheticTexture.Path, red, 64, 64, "Bed"),
                new PictureWrite("/Game/Mods/Jenni/ICO_SS_Gone", SyntheticTexture.Path, red, 64, 64, "Gone", Recipe: "/Game/Mods/Jenni/CR_SS_Gone"),
            ],
        };

        var result = AssetModBuilder.Build(catalog, request);

        // Only the picture whose recipe is built ships (no recipe named: written), at its own path, as a new package.
        var written = Assert.Single(result.Packages);
        Assert.Equal(picture, written.PackagePath);
        Assert.True(written.IsClone);
        Assert.Empty(result.Warnings);
        SyntheticTexture.WriteLoose(temp.Path, picture, written.Bytes, written.UBulk);
        using var reread = AssetCatalog.OpenLoose(temp.Path);
        var texture = reread.LoadObject<UTexture2D>(picture + ".ICO_SS_Bed");
        Assert.Equal(EPixelFormat.PF_BC7, texture.Format);
        Assert.True(texture.SRGB);
        var decoded = TextureDecoder.Decode(texture);
        Assert.Equal(0, decoded.Rgba[(40 * 4) + 3]); // the right half stays transparent
        Assert.InRange(decoded.Rgba[0], 190, 210);

        // Its record is the template's, tags and all, under its own path.
        Assert.Equal([new RegisteredAsset(picture + ".ICO_SS_Bed", "Texture2D")], result.Registered);
        var back = AssetRegistryFile.Parse(result.AssetRegistry!);
        var record = Assert.Single(back.Assets, a => a.ObjectPath == picture + ".ICO_SS_Bed");
        Assert.Equal("Texture2D", record.AssetClass);
        Assert.Equal("PF_BC7", back.GetTags(record).Single(t => t.Key == "Format").Value.Text);
    }

    [Fact]
    public void ADrawnFrameBecomesASquareIconOnATransparentBackground()
    {
        // A 64 px frame with a 10 x 20 block drawn left of centre and a half-covered edge pixel (premultiplied: 100 of 200).
        const int edge = 64;
        var frame = new byte[edge * edge * 4];
        for (var y = 10; y < 30; y++)
        {
            for (var x = 5; x < 15; x++)
            {
                frame.AsSpan(((y * edge) + x) * 4, 4).Fill(0);
                frame[((y * edge) + x) * 4] = 180;
                frame[(((y * edge) + x) * 4) + 3] = 255;
            }
        }

        frame[((30 * edge) + 5) * 4] = 100;
        frame[(((30 * edge) + 5) * 4) + 3] = 128;

        var icon = CraftIconRenderer.Compose(frame, edge, 32)!;

        Assert.Equal(32 * 32 * 4, icon.Length);
        Assert.Equal(0, icon[3]); // corners: transparent
        Assert.Equal(0, icon[^1]);
        var centre = ((16 * 32) + 16) * 4;
        Assert.Equal(255, icon[centre + 3]);
        Assert.InRange(icon[centre], 170, 190);

        // The object fills the square but for the margin: its rows run from about 4 % to 96 % of the height.
        var rows = Enumerable.Range(0, 32).Where(y => Enumerable.Range(0, 32).Any(x => icon[(((y * 32) + x) * 4) + 3] > 128)).ToList();
        Assert.InRange(rows.First(), 0, 3);
        Assert.InRange(rows.Last(), 28, 31);
        Assert.Null(CraftIconRenderer.Compose(new byte[edge * edge * 4], edge, 32));
    }

    [Fact]
    public void APictureFileThatIsNotSquareKeepsItsShape()
    {
        var wide = new TextureImage("wide", 64, 32, Enumerable.Repeat((byte)255, 64 * 32 * 4).ToArray(), "PF_R8G8B8A8", 0, 64, 32, true, false);

        var square = CraftPictures.Fit(wide, 32, 32);

        Assert.Equal(32 * 32 * 4, square.Length);
        Assert.Equal(0, square[3]); // above the picture: transparent
        Assert.Equal(255, square[(((16 * 32) + 16) * 4) + 3]);
        Assert.Equal(0, square[(((31 * 32) + 16) * 4) + 3]);
    }
}
