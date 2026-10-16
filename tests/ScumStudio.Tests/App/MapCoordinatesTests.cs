using ScumStudio.App.ViewModels;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Tests.App;

/// <summary>
/// F20 (defolt_0999: "Is there a way to find it using its coordinates?"): Go to coordinates on the Map reads metres as the
/// Entities list shows them and the game's centimetres, says which, and the Entities list finds what stands nearest.
/// </summary>
public sealed class MapCoordinatesTests
{
    [Theory]
    [InlineData("3551.5, -5162.5, 74.1 m", 355150f, -516250f, 7410f, false)] // copied from the Entities list
    [InlineData("3551.5 -5162.5 74.1", 355150f, -516250f, 7410f, false)]
    [InlineData("3551.5;-5162.5", 355150f, -516250f, null, false)]
    [InlineData("3551,5 -5162,5", 355150f, -516250f, null, false)] // decimal commas
    [InlineData("X=355150 Y=-516250 Z=7410", 355150f, -516250f, 7410f, true)] // the game's admin format
    [InlineData("x: 355150, y: -516250", 355150f, -516250f, null, true)]
    [InlineData("355150 -516250 7410", 355150f, -516250f, 7410f, true)] // beyond 20 000: centimetres
    [InlineData("120 -80 cm", 120f, -80f, null, true)] // a unit decides
    [InlineData("120 -80", 12000f, -8000f, null, false)] // small: metres
    [InlineData("Z=7410 Y=-516250 X=355150", 355150f, -516250f, 7410f, true)]
    public void CoordinatesAreReadInMetresOrTheGamesCentimetres(string text, float x, float y, float? z, bool centimetres)
    {
        Assert.True(MapCoordinates.TryParse(text, out var at), text);
        Assert.Equal(x, at.X, 0.5f);
        Assert.Equal(y, at.Y, 0.5f);
        Assert.Equal(z.HasValue, at.Z.HasValue);
        if (z is { } zz)
        {
            Assert.Equal(zz, at.Z!.Value, 0.5f);
        }

        Assert.Equal(centimetres, at.InCentimetres);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SM_Wall_01")]
    [InlineData("12")]
    [InlineData("1 2 3 4")]
    [InlineData("Wall 12 30")]
    [InlineData("X=1 X=2")]
    [InlineData("Y=3 Z=4")]
    [InlineData("/Game/ConZ_Files/Box_2")]
    public void NamesAndSingleNumbersAreNoCoordinates(string text) => Assert.False(MapCoordinates.TryParse(text, out _), text);

    [Fact]
    public void TheResultSaysBothUnits()
    {
        Assert.True(MapCoordinates.TryParse("X=355150 Y=-516250 Z=7410", out var at));
        Assert.Equal("3551.5, -5162.5, 74.1 m", at.MetresText);
        Assert.Equal("X=355150 Y=-516250 Z=7410", at.CentimetresText);
    }

    /// <summary>The Entities filter: coordinates list the actors within 50 m (root or any instance), nearest first.</summary>
    [Fact]
    public void TheEntitiesListFindsWhatStandsNearCoordinates()
    {
        var level = LevelDocument.FromData(new LevelData { PackagePath = "/Game/Maps/Z_Test", Exports = [], ActorIndices = [] });
        ActorItemViewModel Actor(string name, FVector at, params FVector[] instances)
        {
            var root = new FTransform(FRotator.Zero, at, FVector.One);
            var placed = instances.Select((p, i) => new ActorInstance(1, "HISM", i, "/Game/Rock", FTransform.Identity, new FTransform(FRotator.Zero, p, FVector.One))).ToList();
            return new ActorItemViewModel(level, new ActorRecord(0, name, "/Script/Engine.StaticMeshActor", 1, [], root, ActorKind.StaticMeshActor, "/Game/Box", placed), 1);
        }

        var spot = new FVector(355150f, -516250f, 7410f);
        var near = Actor("Fridge_Added", spot + new FVector(300f, 0f, 0f));
        var nearer = Actor("Invisible_Blocker", spot + new FVector(0f, 120f, 0f));
        var foliage = Actor("Foliage", new FVector(0f, 0f, 0f), spot + new FVector(0f, 0f, 40f)); // root far away, one rock right there
        var far = Actor("Far_House", spot + new FVector(8000f, 0f, 0f));
        var actors = new[] { far, near, foliage, nearer };

        var found = MapPageViewModel.NearCoordinates(actors, "3551.5, -5162.5, 74.1 m");
        Assert.Equal(["Foliage", "Invisible_Blocker", "Fridge_Added"], found!.Select(a => a.Name));
        Assert.Equal(3, MapPageViewModel.NearCoordinates(actors, "X=355150 Y=-516250")!.Count); // flat distance without Z
        Assert.Null(MapPageViewModel.NearCoordinates(actors, "Fridge")); // a name stays a name filter
    }
}
