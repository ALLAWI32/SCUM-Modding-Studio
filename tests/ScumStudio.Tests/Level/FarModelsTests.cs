using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Pak;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Tests.Level;

public sealed class FarModelsTests
{
    [Fact]
    public void OnlyTrianglesWhollyInsideAreMadeEmpty()
    {
        uint[] indices = [0, 1, 2, 2, 3, 4, 4, 5, 0];
        bool[] inside = [true, true, true, true, false, true];

        Assert.Equal(1, FarModels.CutTriangles(indices, inside));
        Assert.Equal([0u, 0, 0, 2, 3, 4, 4, 5, 0], indices);
    }

    [Fact]
    public void ACutBoxFollowsItsPlacementAndMargin()
    {
        var box = new CutBox(new FTransform(new FRotator(0, 90, 0), new FVector(1000, 0, 0), FVector.One), new BoundingBox(new(-100, -10, 0), new(100, 10, 50)));

        Assert.True(box.Contains(new FVector(1000, 90, 25), 0f), "rotated: the long side runs along world Y");
        Assert.False(box.Contains(new FVector(1090, 0, 25), 0f));
        Assert.True(box.Contains(new FVector(1015, 0, 25), 10f), "within the margin");
    }

    [MapSliceFact]
    public async Task DeletingTheBankHidesItsOwnFarModelsAndNothingElse()
    {
        const string level = MapSlice.MapsPath + "A_0_Outpost_Ext_Bank";
        using var catalog = MapSlice.Open();
        var bank = LevelDocument.Load(new Cue4ParseLevelReader(catalog), level).Actors.Single(a => a.Kind == ActorKind.Blueprint);
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Far.ssproj"), "Far Test");
        project.Apply(new DeleteActorOp(new ActorRef(level, bank.Name)));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });

        Assert.Contains(result.FarModels, l => l.StartsWith("A_0_Outpost_Ext_Bank: far model hidden", StringComparison.Ordinal));
        using var overlay = MapSlice.Open(result.StagingDirectory);
        Assert.True(overlay.TryGetPackageFile(FarModels.IslandLevel, out var file));
        var island = CookedPackage.Parse(file.Read(), overlay.Provider.Files[file.Path[..file.Path.LastIndexOf('.')] + ".uexp"].Read(), null, FarModels.IslandLevel);
        var scales = FarModels.ReadDescriptions(island, out _).ToDictionary(d => d.Name, d => d.World.Scale3D.X);
        Assert.InRange(scales["A_0_Outpost_Ext_Bank"], 0f, 0.001f);
        Assert.InRange(scales["A_0_Outpost_Bank"], 0f, 0.001f);
        Assert.Equal(1f, scales["A_0_Outpost_Ext_Saloon"]);
        Assert.Equal(1f, scales["A_0_Outpost"]);
    }

    /// <summary>Real game files only (<c>SCUM_PAKS</c>, key from this PC's store): A_0's HLOD proxies are empty, B_4's are not.</summary>
    [Fact]
    public async Task DeletingAPropOfAnHlodClusterCutsItOutOfTheProxy()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        const string level = MapSlice.MapsPath + "B_4_Outpost_Exterior";
        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), level);
        var proxyMesh = document.Actors.Where(a => a.ClassName == "LODActor").SelectMany(a => a.Components).First(c => c.StaticMeshPath is not null);
        var meshes = new BendSupport(catalog).Describe;
        // The static mesh actor nearest a proxy: a member of its cluster, which the proxy's merged mesh shows.
        var member = document.Actors.Where(a => a.Kind == ActorKind.StaticMeshActor && FarModels.BoxesOf(a, meshes).Any())
            .MinBy(a => FVector.Distance(a.WorldTransform.Translation, proxyMesh.WorldTransform.Translation))!;
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Hlod.ssproj"), "Hlod Test");
        project.Apply(new DeleteActorOp(new ActorRef(level, member.Name)));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });

        Assert.Contains(result.FarModels, l => l.Contains("(HLOD)", StringComparison.Ordinal));
        using var overlay = AssetCatalog.OpenLoose(result.StagingDirectory);
        static int Solid(UStaticMesh mesh)
        {
            var lod = mesh.RenderData!.LODs![0];
            var i = lod.IndexBuffer!.Indices32 is { Length: > 0 } wide ? wide : lod.IndexBuffer!.Indices16!.Select(x => (uint)x).ToArray();
            return Enumerable.Range(0, i.Length / 3).Count(t => i[3 * t] != i[(3 * t) + 1] || i[3 * t] != i[(3 * t) + 2]);
        }

        var changed = result.FarModels.Where(l => l.Contains("(HLOD)", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(changed);
        var patched = document.Actors.Where(a => a.ClassName == "LODActor").SelectMany(a => a.Components).Where(c => c.StaticMeshPath is not null)
            .Count(c => Solid(overlay.LoadObject<UStaticMesh>(c.StaticMeshPath!)) < Solid(catalog.LoadObject<UStaticMesh>(c.StaticMeshPath!)));
        Assert.True(patched >= 1, "at least one proxy mesh lost triangles");
    }
}
