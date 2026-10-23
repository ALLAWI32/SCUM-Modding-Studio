using ScumStudio.Assets.Catalog;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// The sentry robot of today's game files (<c>SCUM_PAKS</c>) still loads whole for the Map's sentry pins: only a mesh that
/// cannot be drawn is left out (<see cref="BrokenMeshes"/>; pheidian's SCUM 0.6 files had one with no vertices).
/// </summary>
public sealed class BrokenMeshesRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [PaksFact]
    public void TodaysSentryRobotLoadsWhole()
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var extra = new LevelScenePreparer(catalog).PrepareMesh(SpawnModels.SentryMesh, new LevelSceneOptions { TextureSize = 0 });

        Assert.NotNull(extra);
        Assert.All(extra.Asset.Lods, lod => Assert.Null(BrokenMeshes.ProblemOf(lod)));
        Assert.False(BrokenMeshes.WasWarned(SpawnModels.SentryMesh));
        output.WriteLine($"{SpawnModels.SentryMesh}: {extra.Asset.Lods.Count} LOD(s), {extra.Asset.Mesh.VertexCount:N0} vertices, {extra.Asset.Mesh.TriangleCount:N0} triangles");
    }
}
