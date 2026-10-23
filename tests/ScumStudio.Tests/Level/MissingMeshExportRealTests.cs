using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// O Piqueno (3.4.0, package-check.txt): "Validator: 216 packages checked, 2 problems": his Map project placed
/// <c>/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_B_4</c> in A_3_TV_Base_Entrance, a lake surface the game does not
/// have (client or dedicated server; the island's own lakes are _B_1, _B_2_4, _B_3_4, _C_1 …), and the export wrote the
/// import, so the package check refused the whole pak. Such an actor is now left out with a warning that names it, and the
/// pak is written; the package check itself stays as strict. Real game files only (<c>SCUM_PAKS</c>, key from this PC's
/// store; <c>SCUM_SERVER_PAKS</c> adds the dedicated server's cook).
/// </summary>
public sealed class MissingMeshExportRealTests
{
    private const string TvBase = MapSlice.MapsPath + "A_3_TV_Base_Entrance";
    private const string MissingPackage = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_B_4";
    private const string Missing = MissingPackage + ".LakeWaterSurface_B_4";
    private const string RealLake = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_B_3_4.LakeWaterSurface_B_3_4";

    /// <summary>The two lines of the user's package-check.txt, word for word.</summary>
    private static readonly string[] UsersLines =
    [
        $"{TvBase}: imports {MissingPackage}, but neither the game nor the mod has the package {MissingPackage} (the game would get null there).",
        $"{TvBase}: imports {Missing}, but neither the game nor the mod has the package {MissingPackage} (the game would get null there).",
    ];

    private static readonly Cue4ParseLevelReaderOptions Plain = new() { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = false };

    private static AssetCatalog Open(string paks) => AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [PaksFact]
    public async Task TheUsersProjectExportsWithTheMissingLakeLeftOutAndNamed()
    {
        using var catalog = Open(RealPaks.Folder!);
        Assert.False(catalog.PackageExists(MissingPackage)); // the game has no such lake
        Assert.True(catalog.PackageExists(RealLake));

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("MyMapMod.ssproj"), "MyMapMod");
        var at = Near(catalog);
        project.Apply(new AddStaticMeshActorOp(TvBase, "LakeWaterSurface_B_3_4_Added", RealLake, at));
        project.Apply(new AddStaticMeshActorOp(TvBase, "LakeWaterSurface_B_4_Added", Missing, at with { Location = at.Location + new FVector(3000f, 0f, 0f) }));

        // 3.4.0 and 3.5.0 threw ModValidationException here with the user's two lines and wrote no pak.
        var client = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("client"), WritePak = false });
        AssertLeftOut(client);

        // The dedicated server's cook, checked against its own files with the client's beside them, leaves it out the same way.
        if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is { Length: > 0 } serverPaks && Directory.Exists(serverPaks))
        {
            using var server = Open(serverPaks);
            Assert.False(server.PackageExists(MissingPackage));
            var cooked = await new ProjectExporter().ExportAsync(project, server,
                new ExportOptions { OutputDirectory = temp.Combine("server"), WritePak = false, ClientCatalog = catalog }, ProjectSourceRole.Server);
            AssertLeftOut(cooked);
        }
    }

    [PaksFact]
    public async Task TheSameImportWrittenAnywayStillStopsThePackageCheck()
    {
        // The level as 3.4.0 wrote it (the plan asks nothing of the game files): the package check still names exactly the
        // user's two lines, so a reference the export itself breaks keeps stopping the pak.
        using var catalog = Open(RealPaks.Folder!);
        using var temp = new LevelTempDirectory();
        var state = new EditState();
        state.Apply(new AddStaticMeshActorOp(TvBase, "LakeWaterSurface_B_4_Added", Missing, Near(catalog)));

        Assert.True(catalog.TryGetPackageFile(TvBase, out var file));
        var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(catalog, file);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog, Plain), TvBase);
        var request = ProjectExporter.PlanLevel(state, TvBase, document, []);
        Assert.Equal(Missing, Assert.Single(request.StaticMeshAdds).StaticMesh);
        var (bytes, _) = LevelPackageEditor.Apply(CookedPackage.Parse(uasset, uexp, ubulk, TvBase), request with { AssetClass = catalog.GetMainClassName });

        var staging = temp.Combine("staging");
        var virtualPath = file.Path.Replace('\\', '/');
        var target = Path.Combine([staging, .. virtualPath[..virtualPath.LastIndexOf('.')].Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await bytes.WriteAsync(target, virtualPath[virtualPath.LastIndexOf('.')..]);
        if (ubulk is not null)
        {
            await File.WriteAllBytesAsync(target + ".ubulk", ubulk);
        }

        var check = ModPackageValidator.Validate(staging, catalog);
        Assert.False(check.Passed);
        Assert.Equal(UsersLines, check.Problems);
    }

    private static void AssertLeftOut(ExportResult result)
    {
        var level = Assert.Single(result.Levels, l => l.PackagePath == TvBase);
        Assert.Equal(["LakeWaterSurface_B_3_4_Added"], level.Report.AddedActors); // the real lake is in, the missing one is not
        Assert.Single(result.Warnings, w => w.Contains("LakeWaterSurface_B_4_Added", StringComparison.Ordinal)
                                            && w.Contains($"neither the game nor the mod has the mesh {Missing}, so the actor was not created", StringComparison.Ordinal));

        // The written level makes no reference to it: nothing the game would get null for.
        var written = CookedPackage.Load(Path.Combine([result.StagingDirectory, .. level.VirtualPath.Split('/')]));
        var imports = Enumerable.Range(0, written.Imports.Count).Select(i => written.GetFullPath(-(i + 1))).ToList();
        Assert.DoesNotContain(imports, i => i.StartsWith(MissingPackage, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(RealLake, imports);
        Assert.DoesNotContain(written.Names, n => n.Contains("LakeWaterSurface_B_4", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A place inside the TV base's own area, ten metres off one of its actors.</summary>
    private static TransformValue Near(AssetCatalog catalog)
    {
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog, Plain), TvBase);
        var anchor = document.Actors.First(a => a.Root is not null).WorldTransform.Translation;
        return TransformValue.Identity with { Location = anchor + new FVector(1000f, 0f, 0f) };
    }
}
