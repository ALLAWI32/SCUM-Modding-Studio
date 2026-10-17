using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding;
using ScumStudio.Modding.Crafting;
using static ScumStudio.Tests.Modding.ElementFixture;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// "Place in front" (experimental, owner 2026-10-17: his 15 m cabin appears around him): the craftable's recipe gets a
/// <c>PlacementLocationOffset</c> that grows with its size, so the ghost and the element built from it move together; small
/// things keep the game's placement (synthetic game, <see cref="ElementFixture"/>; the real cabin in
/// <see cref="CraftBuildPlaceRealTests"/>).
/// </summary>
public sealed class CraftPlaceInFrontTests
{
    private static Craftable House(float size, InFrontSettings? inFront) => new()
    {
        Name = "Log House",
        Source = OtherMesh,
        Mesh = OtherMesh,
        Kind = CraftKind.Furniture,
        SizeMeters = size,
        Ingredients = [new("CI_Plank", 50)],
        InFront = inFront,
    };

    [Theory]
    [InlineData(0.8f, 0f)] // a stool
    [InlineData(2.99f, 0f)] // a 2-3 m thing stays where the game puts it
    [InlineData(3f, 2.5f)]
    [InlineData(15f, 8.5f)] // the owner's cabin
    [InlineData(28f, 15f)] // capped
    [InlineData(float.NaN, 0f)]
    public void TheAutomaticDistanceGrowsWithTheSize(float size, float expected) =>
        Assert.Equal(expected, CraftablesPlanner.AutoInFrontMeters(size), 3);

    [Fact]
    public void TheDistanceIsTheAutomaticOneUnlessSetAndOffWithoutTheSwitch()
    {
        Assert.Equal(0f, CraftablesPlanner.InFrontMeters(House(15, null)));
        Assert.Equal(8.5f, CraftablesPlanner.InFrontMeters(House(15, new InFrontSettings())));
        Assert.Equal(12f, CraftablesPlanner.InFrontMeters(House(15, new InFrontSettings(12))));
        Assert.Equal(CraftablesPlanner.InFrontMaxMeters, CraftablesPlanner.InFrontMeters(House(15, new InFrontSettings(99))));
        Assert.Equal(0f, CraftablesPlanner.InFrontMeters(House(15, new InFrontSettings(float.PositiveInfinity))));
        Assert.Equal((850f, 0f), CraftablesPlanner.InFrontOffset(InFrontAxis.PlusX, 8.5f));
        Assert.Equal((-850f, 0f), CraftablesPlanner.InFrontOffset(InFrontAxis.MinusX, 8.5f));
        Assert.Equal((0f, 850f), CraftablesPlanner.InFrontOffset(InFrontAxis.PlusY, 8.5f));
        Assert.Equal((0f, -850f), CraftablesPlanner.InFrontOffset(InFrontAxis.MinusY, 8.5f));
    }

    [Fact]
    public async Task ALargeCraftablesRecipeGetsThePlacementOffsetAndTheElementStaysTheSame()
    {
        using var game = new ElementFixture();
        var off = CraftablesPlanner.Plan(game.Catalog, [House(15, null)]);
        var plan = CraftablesPlanner.Plan(game.Catalog, [House(15, new InFrontSettings())]);
        var entry = Assert.Single(plan.Entries);
        var edit = Assert.Single(plan.Request.Edits[entry.Recipe], e => e.Path == "PlacementLocationOffset");
        Assert.Equal("CR_SS_Log_House", edit.Export);
        Assert.Equal("850, 0, 0", edit.Value);
        Assert.Contains(plan.Warnings, w => w.Contains("8.5 m along +X", StringComparison.Ordinal) && w.Contains("experimental", StringComparison.Ordinal));
        Assert.DoesNotContain(off.Request.Edits.Values.SelectMany(e => e), e => e.Path == "PlacementLocationOffset");

        // Another direction, and a set distance.
        var side = CraftablesPlanner.Plan(game.Catalog, [House(15, new InFrontSettings(10, InFrontAxis.MinusY))]);
        Assert.Equal("0, -1000, 0", Assert.Single(side.Request.Edits[entry.Recipe], e => e.Path == "PlacementLocationOffset").Value);

        var before = AssetModBuilder.Build(game.Catalog, off.Request);
        var result = AssetModBuilder.Build(game.Catalog, plan.Request);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("PlacementLocationOffset", StringComparison.Ordinal));

        // The recipe stores the new Vector struct; the rest of it reads as before.
        var recipe = Built(result, entry.Recipe);
        var block = recipe.ReadProperties(RecipeIngredients.RecipeExport(recipe));
        var tag = block.Find("PlacementLocationOffset")!;
        Assert.Equal("StructProperty", tag.Type);
        Assert.Equal("(850, 0, 0)", tag.Value!.ToString());
        Assert.Equal(RecipeIngredients.Read(Built(before, entry.Recipe)), RecipeIngredients.Read(recipe));
        Assert.Equal(Elements + "BP_SS_Log_House.BP_SS_Log_House_C", Assert.IsType<SoftObjectValue>(block.Find("Product")!.Value).AssetPath);

        // The element (mesh, collision, snap markers, ladder: all its own) is byte for byte the one without the switch.
        Assert.Equal(Bytes(before, entry.Product), Bytes(result, entry.Product));

        // The game's reader (CUE4Parse) reads the offset.
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-infront-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var package in result.Packages)
            {
                await package.Bytes.WriteAsync(Path.Combine(dir, Path.ChangeExtension(package.HeaderFilePath(), null)));
            }

            using var loose = AssetCatalog.OpenLoose(dir);
            var read = loose.LoadObject<UObject>($"{entry.Recipe}.CR_SS_Log_House");
            var offset = read.GetOrDefault<FVector>("PlacementLocationOffset");
            Assert.Equal((850f, 0f, 0f), (offset.X, offset.Y, offset.Z));
            Assert.Equal(Elements + "BP_SS_Log_House.BP_SS_Log_House_C", read.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FSoftObjectPath>("Product").AssetPathName.Text);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void ASmallCraftableOrNoSwitchChangesNothing()
    {
        using var game = new ElementFixture();
        var plain = AssetModBuilder.Build(game.Catalog, CraftablesPlanner.Plan(game.Catalog, [House(1.2f, null)]).Request);
        var small = CraftablesPlanner.Plan(game.Catalog, [House(1.2f, new InFrontSettings())]);
        Assert.DoesNotContain(small.Request.Edits.Values.SelectMany(e => e), e => e.Path == "PlacementLocationOffset");
        Assert.DoesNotContain(small.Warnings, w => w.Contains("experimental", StringComparison.Ordinal));
        var built = AssetModBuilder.Build(game.Catalog, small.Request);
        Assert.Equal(plain.Packages.Select(p => p.PackagePath), built.Packages.Select(p => p.PackagePath));
        foreach (var package in plain.Packages)
        {
            Assert.Equal(Bytes(plain, package.PackagePath), Bytes(built, package.PackagePath));
        }

        // An item is never placed: no recipe offset.
        var item = CraftablesPlanner.Plan(game.Catalog, [House(15, new InFrontSettings()) with { Kind = CraftKind.Item, Source = ChestItem, Mesh = ChestItem }]);
        Assert.DoesNotContain(item.Request.Edits.Values.SelectMany(e => e), e => e.Path == "PlacementLocationOffset");
    }

    [Fact]
    public void AnElementCraftablesOwnRecipeGetsIt()
    {
        using var game = new ElementFixture();
        var bed = new Craftable
        {
            Name = "Camp Bed",
            Source = ElementFixture.Bed,
            Mesh = BedMesh,
            Kind = CraftKind.Element,
            SizeMeters = 4,
            Element = new ElementSettings { Template = ElementFixture.Bed },
            Ingredients = [new("CI_Plank", 6)],
            InFront = new InFrontSettings(),
        };
        var plan = CraftablesPlanner.Plan(game.Catalog, [bed]);
        var entry = Assert.Single(plan.Entries);
        Assert.Equal("300, 0, 0", Assert.Single(plan.Request.Edits[entry.Recipe], e => e.Path == "PlacementLocationOffset").Value);
        Assert.DoesNotContain(plan.Request.Edits.Where(e => e.Key != entry.Recipe).SelectMany(e => e.Value), e => e.Path == "PlacementLocationOffset"); // not the upgrade's
        var recipe = Built(AssetModBuilder.Build(game.Catalog, plan.Request), entry.Recipe);
        Assert.Equal("(300, 0, 0)", recipe.ReadProperties(RecipeIngredients.RecipeExport(recipe)).Find("PlacementLocationOffset")!.Value!.ToString());
    }

    private static CookedPackage Built(AssetModBuildResult result, string path)
    {
        var built = Assert.Single(result.Packages, p => string.Equals(p.PackagePath, path, StringComparison.OrdinalIgnoreCase));
        return CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, built.UBulk, path);
    }

    private static byte[] Bytes(AssetModBuildResult result, string path)
    {
        var built = Assert.Single(result.Packages, p => string.Equals(p.PackagePath, path, StringComparison.OrdinalIgnoreCase));
        return [.. built.Bytes.UAsset, .. built.Bytes.UExp];
    }
}
