using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner (2026-10-09, screenshots at B_4): "the road I copied is purple in game". The game places its gravel road pieces
/// (Landscape_B_4_2b's spline meshes) drawing only into the landscape's virtual texture: <c>RuntimeVirtualTextures</c> =
/// <c>RTV_Landscape</c>, <c>VirtualTextureRenderPassType</c> Never, no shadow; the ground shows the road. Their material
/// (<c>M_DirtRoad_Master</c>, <c>bHasRuntimeVirtualTextureOutput</c>) draws a placeholder pink on screen. Added or bent copies
/// must be written the same way; other meshes keep drawing on screen. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class LandscapeRoadRealTests
{
    private const string Tile = MapSlice.MapsPath + "Landscape_B_4_2b";
    private const string Road = "/Game/ConZ_Files/Models/Road/Gravel_road/Gravel_Road_01.Gravel_Road_01";
    private const string Crossroads = "/Game/ConZ_Files/Models/Road/Gravel_road/Gravel_Road_Crossroads_01_Flipped.Gravel_Road_Crossroads_01_Flipped";
    private const string BigRock = "/Game/ConZ_Files/Landscape/Rocks/Big_Rock_01_Paint/Big_Rock_01_01.Big_Rock_01_01";
    private const string Asphalt = "/Game/ConZ_Files/Models/Road/Asphalt_Road/SM_Asphalt_Road_Gravel_Border_01.SM_Asphalt_Road_Gravel_Border_01";
    private const string BridgeFill = "/Game/ConZ_Files/Models/Road/KrkBridge/KB_Meshes/SM_KrkBridge_Fill.SM_KrkBridge_Fill";

    private static string? Paks => Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks) ? paks : null;

    [Fact]
    public async Task AddedAndBentRoadPiecesDrawOnlyIntoTheLandscapeAsTheGamesOwn()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        Assert.True(new MaterialInspector(catalog).Inspect("/Game/ConZ_Files/Materials/Road/Gravel_Road/MI_Gravel_Road_01_New.MI_Gravel_Road_01_New").WritesLandscapeTexture);
        // Almost every master can write the landscape texture (rocks and bridges too): the road masters decide, as the game places them
        // (B_4_2b's gravel pieces: Never, no shadow; B_3_2b's asphalt pieces: the default pass, drawn on screen only without the texture).
        var meshes = new BendSupport(catalog);
        Assert.True(meshes.Describe(Crossroads) is { DrawsIntoLandscape: true, OnlyIntoLandscape: true });
        Assert.True(meshes.Describe(Asphalt) is { DrawsIntoLandscape: true, OnlyIntoLandscape: false });
        Assert.True(meshes.Describe(BigRock) is { DrawsIntoLandscape: false, OnlyIntoLandscape: false });
        Assert.True(meshes.Describe(BridgeFill) is { DrawsIntoLandscape: false, OnlyIntoLandscape: false });

        // The owner's two pieces (journal of MyMapMod), a third road bent, and a rock that must still draw on screen.
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Roads.ssproj"), "Roads");
        var road = new AddStaticMeshActorOp(Tile, "Gravel_Road_01_Added", Road,
            new TransformValue(new FVector(575513.2f, -230641.6f, 280.9f), new FRotator(0f, -90.69f, 0f), new FVector(1f, 1.347f, 1f)));
        var crossroads = new AddStaticMeshActorOp(Tile, "Gravel_Road_Crossroads_01_Flipped_Added", Crossroads,
            new TransformValue(new FVector(575527.8f, -229475.6f, 270.3f), new FRotator(0f, 90.04f, 0f), new FVector(1f, 1.33f, 1f)));
        var bent = new AddStaticMeshActorOp(Tile, "Gravel_Road_01_Bent", Road, TransformValue.At(575600f, -231500f, 280f));
        var rock = new AddStaticMeshActorOp(Tile, "Big_Rock_01_01_Added", BigRock, TransformValue.At(575700f, -231000f, 300f));
        var asphalt = new AddStaticMeshActorOp(Tile, "Asphalt_Added", Asphalt, TransformValue.At(575800f, -231000f, 280f));
        foreach (var op in new EditOp[] { road, crossroads, bent, rock, asphalt, new BendActorOp(bent.Created, 0f, 20f) })
        {
            project.Apply(op);
        }

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var package = ModdableAssets.ReadPackage(written, Tile);

        foreach (var (name, actorClass) in new[] { (road.NewName, "StaticMeshActor"), (crossroads.NewName, "StaticMeshActor"), (bent.NewName, "SplineMeshActor") })
        {
            var props = Component(package, name, actorClass);
            var textures = Assert.IsType<ArrayValue>(props.Find("RuntimeVirtualTextures")!.Value);
            var texture = Assert.IsType<ObjectValue>(Assert.Single(textures.Items));
            Assert.Contains("RTV_Landscape", texture.Reference, StringComparison.Ordinal);
            Assert.Equal("ERuntimeVirtualTextureMainPassType::Never", Assert.IsType<EnumValue>(props.Find("VirtualTextureRenderPassType")!.Value).Value);
            Assert.False(Assert.IsType<BoolValue>(props.Find("CastShadow")!.Value).Value);
        }

        var asphaltProps = Component(package, asphalt.NewName, "StaticMeshActor");
        Assert.Contains("RTV_Landscape", Assert.IsType<ObjectValue>(Assert.Single(Assert.IsType<ArrayValue>(asphaltProps.Find("RuntimeVirtualTextures")!.Value).Items)).Reference, StringComparison.Ordinal);
        Assert.Null(asphaltProps.Find("VirtualTextureRenderPassType"));
        Assert.Null(asphaltProps.Find("CastShadow"));

        var rockProps = Component(package, rock.NewName, "StaticMeshActor");
        Assert.Null(rockProps.Find("RuntimeVirtualTextures"));
        Assert.Null(rockProps.Find("VirtualTextureRenderPassType"));

        // The game's loader reads the tile with the new references (CUE4Parse resolves every export).
        Assert.Contains(written.LoadPackage(Tile).GetExports(), e => e.Name == road.NewName);
    }

    private static PropertyBlock Component(CookedPackage package, string actorName, string actorClass)
    {
        var actor = Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName) == actorName);
        Assert.Equal(actorClass, package.GetExportClassName(actor));
        var component = Enumerable.Range(0, package.Exports.Count).First(i => package.Exports[i].OuterIndex == actor + 1 && package.GetExportClassName(i).EndsWith("MeshComponent", StringComparison.Ordinal));
        return package.ReadProperties(component);
    }
}
