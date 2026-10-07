using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;

namespace ScumStudio.Tests.Level;

/// <summary>The Replace tool's family rule and the fit of a replacement to what it replaces, on made-up paths and bounds.</summary>
public sealed class ReplaceFamiliesTests
{
    private const string Models = "/Game/ConZ_Files/Models";

    private static readonly DumpPackage[] Packages =
    [
        new($"{Models}/Road/Asphalt_Road/SM_Road_Asphalt_01", "roads", "StaticMesh"),
        new($"{Models}/Road/Asphalt_Road/SM_Road_Asphalt_02", "roads", "StaticMesh"),
        new($"{Models}/Road/Gravel_road/SM_Road_Gravel_01", "roads", "StaticMesh"),
        new($"{Models}/Road/Distant_Models/SM_Road_Gravel_01_Far", "roads", "StaticMesh"),
        new($"{Models}/Road/KrkBridge/SM_Bridge_01", "roads", "StaticMesh"),
        new($"{Models}/Bridges/SM_Bridge_Wood_01", "bridges", "StaticMesh"),
        new($"{Models}/Buildings/Farm/SM_Barn_01", "buildings", "StaticMesh"),
        new($"{Models}/Buildings/Farm/Parts/SM_Barn_Wall_01", "buildings", "StaticMesh"),
        new($"{Models}/Buildings/Farm/BP_Farmhouse_01", "buildings", "Blueprint"),
        new($"{Models}/Buildings/Farm/BP_Farmhouse_02", "buildings", "BlueprintGeneratedClass"),
        new($"{Models}/Buildings/City/BP_Block_01", "buildings", "Blueprint"),
        new($"{Models}/Objects/Outdoor/Walls/SM_Wall_Stone_01", "walls", "StaticMesh"),
        new($"{Models}/Objects/Outdoor/Walls/Brick/SM_Wall_Brick_01", "walls", "StaticMesh"),
        new($"{Models}/Objects/Outdoor/Fence/Continental_Fences/SM_Fence_01", "fences", "StaticMesh"),
        new($"{Models}/Objects/Outdoor/Fence/CoveredFence/SM_Fence_Covered_01", "fences", "StaticMesh"),
        new("/Game/ConZ_Files/Foliage/Continental/Trees/Oak/SM_Oak_01", "trees", "StaticMesh"),
        new("/Game/ConZ_Files/Foliage/Mediterranean/Trees/SM_Pine_01", "trees", "StaticMesh"),
        new("/Game/ConZ_Files/Foliage/Continental/Bush/SM_Bush_01", "bushes", "StaticMesh"),
        new("/Game/ConZ_Files/Foliage/Farming/AppleTree/SM_AppleTree_01", "farming", "StaticMesh"),
        new("/Game/ConZ_Files/Foliage/Farming/Carrot/SM_Carrot_01", "farming", "StaticMesh"),
        new($"{Models}/Props/Crates/SM_Crate_01", "props", "StaticMesh"),
        new($"{Models}/Props/Crates/SM_Crate_02", "props", "StaticMesh"),
        new($"{Models}/Props/Crates/T_Crate_D", "props", "Texture2D"),
        new($"{Models}/Props/Barrels/SM_Barrel_01", "props", "StaticMesh"),
    ];

    [Theory]
    [InlineData($"{Models}/Road/Asphalt_Road/SM_Road_Asphalt_01.SM_Road_Asphalt_01", $"{Models}/Road")]
    [InlineData($"{Models}/Bridges/SM_Bridge_Wood_01", $"{Models}/Bridges")]
    [InlineData($"{Models}/Buildings/Farm/Parts/SM_Barn_Wall_01", $"{Models}/Buildings/Farm")]
    [InlineData($"{Models}/Buildings/Farm/BP_Farmhouse_01.BP_Farmhouse_01_C", $"{Models}/Buildings/Farm")]
    [InlineData($"{Models}/Objects/Outdoor/Walls/Brick/SM_Wall_Brick_01", $"{Models}/Objects/Outdoor/Walls")]
    [InlineData($"{Models}/Objects/Outdoor/Fence/Continental_Fences/SM_Fence_01", $"{Models}/Objects/Outdoor/Fence")]
    [InlineData("/Game/ConZ_Files/Foliage/Continental/Trees/Oak/SM_Oak_01.SM_Oak_01", "/Game/ConZ_Files/Foliage/*/Trees")]
    [InlineData("/Game/ConZ_Files/Foliage/Continental/Bush/SM_Bush_01", "/Game/ConZ_Files/Foliage/*/Bush")]
    [InlineData("/Game/ConZ_Files/Foliage/Farming/Carrot/SM_Carrot_01", "/Game/ConZ_Files/Foliage/Farming")]
    [InlineData($"{Models}/Props/Crates/SM_Crate_01", $"{Models}/Props/Crates")]
    public void TheFamilyFollowsTheGamesFolders(string path, string family) =>
        Assert.Equal(family, ReplaceFamilies.Family(path).ToString());

    [Fact]
    public void ARoadPieceGetsEveryRoadWithItselfFirst()
    {
        var choices = ReplaceFamilies.Candidates($"{Models}/Road/Gravel_road/SM_Road_Gravel_01.SM_Road_Gravel_01", Packages);
        Assert.Equal(["SM_Road_Gravel_01", "SM_Bridge_01", "SM_Road_Asphalt_01", "SM_Road_Asphalt_02"], choices.Select(c => c.Name)); // the game keeps its bridges in the road folder
        Assert.True(choices[0].IsCurrent);
        Assert.All(choices.Skip(1), c => Assert.False(c.IsCurrent));
        Assert.All(choices, c => Assert.False(c.IsBlueprint));
        Assert.Equal($"{Models}/Road/Asphalt_Road/SM_Road_Asphalt_01.SM_Road_Asphalt_01", choices[2].ObjectPath);
        Assert.DoesNotContain(choices, c => c.Name.EndsWith("_Far", StringComparison.Ordinal)); // far-view models are never placed
    }

    [Fact]
    public void ABuildingGetsTheBlueprintsOfItsOwnFolderOnly()
    {
        var choices = ReplaceFamilies.Candidates($"{Models}/Buildings/Farm/BP_Farmhouse_01.BP_Farmhouse_01_C", Packages);
        Assert.Equal(["BP_Farmhouse_01", "BP_Farmhouse_02"], choices.Select(c => c.Name));
        Assert.All(choices, c => Assert.True(c.IsBlueprint));
        Assert.Equal($"{Models}/Buildings/Farm/BP_Farmhouse_02.BP_Farmhouse_02_C", choices[1].ObjectPath);

        // A wall of the farm's buildings: the farm's meshes, not its Blueprints.
        var walls = ReplaceFamilies.Candidates($"{Models}/Buildings/Farm/Parts/SM_Barn_Wall_01", Packages);
        Assert.Equal(["SM_Barn_Wall_01", "SM_Barn_01"], walls.Select(c => c.Name));
    }

    [Fact]
    public void WallsTreesAndOtherObjectsStayInTheirFamily()
    {
        Assert.Equal(["SM_Wall_Brick_01", "SM_Wall_Stone_01"], ReplaceFamilies.Candidates($"{Models}/Objects/Outdoor/Walls/Brick/SM_Wall_Brick_01", Packages).Select(c => c.Name));
        Assert.Equal(["SM_Fence_01", "SM_Fence_Covered_01"], ReplaceFamilies.Candidates($"{Models}/Objects/Outdoor/Fence/Continental_Fences/SM_Fence_01", Packages).Select(c => c.Name));
        Assert.Equal(["SM_Oak_01", "SM_Pine_01"], ReplaceFamilies.Candidates("/Game/ConZ_Files/Foliage/Continental/Trees/Oak/SM_Oak_01.SM_Oak_01", Packages).Select(c => c.Name)); // every biome's trees
        Assert.Equal(["SM_Bush_01"], ReplaceFamilies.Candidates("/Game/ConZ_Files/Foliage/Continental/Bush/SM_Bush_01", Packages).Select(c => c.Name));
        Assert.Equal(["SM_Carrot_01", "SM_AppleTree_01"], ReplaceFamilies.Candidates("/Game/ConZ_Files/Foliage/Farming/Carrot/SM_Carrot_01", Packages).Select(c => c.Name));

        // Anything else: the parent folder, meshes only.
        Assert.Equal(["SM_Crate_02", "SM_Crate_01"], ReplaceFamilies.Candidates($"{Models}/Props/Crates/SM_Crate_02.SM_Crate_02", Packages).Select(c => c.Name));

        // Something the catalogue does not list is still "current".
        var unknown = ReplaceFamilies.Candidates($"{Models}/Props/Crates/SM_Crate_09.SM_Crate_09", Packages);
        Assert.Equal(["SM_Crate_09", "SM_Crate_01", "SM_Crate_02"], unknown.Select(c => c.Name));
        Assert.True(unknown[0].IsCurrent);
    }

    [Fact]
    public void ALongPieceKeepsItsLengthAndHeight()
    {
        var road = new BoundingBox(new Vector3(-500, -200, 0), new Vector3(500, 200, 20)); // 10 m long, 4 m wide
        var longer = new BoundingBox(new Vector3(-1000, -200, 0), new Vector3(1000, 200, 40)); // 20 m long, same width, twice as high
        var fitted = ReplaceFamilies.FitScale(road, new FVector(1.5f, 1f, 1f), longer, isLong: true);
        Assert.Equal(0.75f, fitted.X, 3); // 15 m / 20 m
        Assert.Equal(1f, fitted.Y, 3); // same width: left alone
        Assert.Equal(0.5f, fitted.Z, 3);

        // A much wider piece is narrowed to the old width; a piece long along Y scales along Y.
        var wide = new BoundingBox(new Vector3(-500, -400, 0), new Vector3(500, 400, 20));
        Assert.Equal(0.5f, ReplaceFamilies.FitScale(road, FVector.One, wide, isLong: true).Y, 3);
        var alongY = new BoundingBox(new Vector3(-200, -500, 0), new Vector3(200, 500, 20));
        var fittedY = ReplaceFamilies.FitScale(alongY, FVector.One, new BoundingBox(new Vector3(-200, -250, 0), new Vector3(200, 250, 20)), isLong: true);
        Assert.Equal(new FVector(1f, 2f, 1f), fittedY);

        // A mirrored piece stays mirrored.
        Assert.Equal(new FVector(-0.75f, 1f, 0.5f), ReplaceFamilies.FitScale(road, new FVector(-1.5f, 1f, 1f), longer, isLong: true));
    }

    [Fact]
    public void ABuildingKeepsItsScaleUnlessTheOtherIsOfAVeryDifferentSize()
    {
        var house = new BoundingBox(new Vector3(-600, -400, 0), new Vector3(600, 400, 500));
        var similar = new BoundingBox(new Vector3(-800, -500, 0), new Vector3(800, 500, 600));
        Assert.Equal(FVector.One, ReplaceFamilies.FitScale(house, new FVector(1.2f), similar, isLong: false));

        var shed = new BoundingBox(new Vector3(-150, -100, 0), new Vector3(150, 100, 250)); // a quarter of the footprint
        Assert.Equal(new FVector(4f), ReplaceFamilies.FitScale(house, FVector.One, shed, isLong: false));

        // Unknown bounds: the scale stays.
        Assert.Equal(new FVector(2f), ReplaceFamilies.FitScale(BoundingBox.Empty, new FVector(2f), shed, isLong: true));
    }
}
