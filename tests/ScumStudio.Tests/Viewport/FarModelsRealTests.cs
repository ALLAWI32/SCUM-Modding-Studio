using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// The game's far view and foliage distances from the real game (<c>SCUM_PAKS</c>, key from this PC's store; skipped
/// without), each printing what the owner's benchmark will see.
/// </summary>
public sealed class FarModelsRealTests
{
    private readonly ITestOutputHelper _output;

    public FarModelsRealTests(ITestOutputHelper output) => _output = output;

    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;

    [Fact]
    public void TheIslandListsAFarModelForEveryPlace()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var island = ScumStudio.Modding.Catalog.ModdableAssets.ReadPackage(catalog, FarModels.IslandLevel);
        var descriptions = FarModels.ReadDescriptions(island, out _);
        var models = FarModelsScene.Read(catalog, WorldIndex.FromCatalog(catalog));
        _output.WriteLine($"{descriptions.Count} descriptions, {models.Count} far models, {models.Count(m => m.LevelPath is not null)} with their level, {models.Select(m => m.MeshPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} distinct meshes");

        Assert.Equal(1523, descriptions.Count);
        Assert.InRange(models.Count, 1500, 1523);
        Assert.Contains(models, m => m.LevelPath?.EndsWith("/A_0_Outpost_Exterior", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void TheFirstFiftyFarMeshesArePrepared()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var models = FarModelsScene.Read(catalog, WorldIndex.FromCatalog(catalog)).Take(50).ToList();
        var far = FarModelsScene.Prepare(catalog, models);
        _output.WriteLine($"{far.Placements.Count} placements, {far.Meshes.Count} meshes, {far.MeshTriangles:N0} triangles at LOD 0 ({far.LodTriangles:N0} over all LODs), {far.Textures.Count} textures, {far.MissingMeshes.Count} missing, {far.Elapsed.TotalMilliseconds:0} ms");
        foreach (var missing in far.MissingMeshes.Take(5))
        {
            _output.WriteLine("missing: " + missing);
        }

        Assert.Equal(models.Count, far.RequestedPlacements);
        Assert.True(far.Placements.Count >= 45, $"{far.Placements.Count} of {models.Count} drawn");
        Assert.All(far.Placements, p => Assert.Equal(float.PositiveInfinity, LevelSceneUploader.DrawDistanceOf(p)));
    }

    [Fact]
    public void FoliageComponentsCarryTheGamesCullDistances()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var world = WorldIndex.FromCatalog(catalog);
        var reader = new Cue4ParseLevelReader(catalog);
        var tiles = world.Packages.Where(p => p.IsMap && p.Kind == WorldPackageKind.Landscape).Take(4).ToList();
        var components = tiles.SelectMany(t => LevelDocument.Load(reader, t.PackagePath).Actors).SelectMany(a => a.Components).Where(c => c.IsInstanced).ToList();
        _output.WriteLine($"{tiles.Count} landscape tiles: {components.Count} instanced components, {components.Count(c => c.InstanceEndCullDistance > 0)} with an end cull distance, {components.Count(c => c.InstanceStartCullDistance > 0)} with a start, {components.Sum(c => c.Instances.Count):N0} instances");
        foreach (var c in components.Where(c => c.InstanceStartCullDistance > 0).Take(8))
        {
            _output.WriteLine($"  {c.Name} {c.StaticMeshPath}: {c.InstanceStartCullDistance} → {c.InstanceEndCullDistance} cm, {c.Instances.Count} instances");
        }

        Assert.NotEmpty(components);
        Assert.All(components.Where(c => c.InstanceStartCullDistance > 0), c => Assert.True(c.InstanceStartCullDistance <= c.InstanceEndCullDistance || c.InstanceEndCullDistance == 0));
    }
}
