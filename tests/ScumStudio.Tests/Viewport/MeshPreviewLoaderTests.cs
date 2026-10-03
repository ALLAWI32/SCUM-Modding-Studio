using ScumStudio.Assets.Catalog;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// <see cref="MeshPreviewLoader"/> over the stock packages of the fixture archive: a weapon Blueprint resolves its
/// skeletal mesh through the CDO component, a vehicle gets its chassis plus the attachments on their sockets, and a
/// mesh is split by material with base-colour textures.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class MeshPreviewLoaderTests
{
    private const string Rpk = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_RPK-74";
    private const string WolfsWagen = "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen";
    private const string WolfsWagenMesh = "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/SK_WolfsWagen";

    [FixturesFact]
    public void WeaponBlueprintShowsItsSkeletalMesh()
    {
        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot);
        var loader = new MeshPreviewLoader(catalog) { TextureSize = 64 };
        Assert.EndsWith("/RPK/SK_RPK-74.SK_RPK-74", loader.FindMeshPath(Rpk), StringComparison.Ordinal);

        var model = loader.LoadBlueprint(Rpk);
        Assert.NotNull(model);
        Assert.Equal("Weapon_RPK-74", model!.Name);
        Assert.NotEmpty(model.Parts);
        Assert.True(model.Triangles > 1000, $"only {model.Triangles} triangles");
        Assert.All(model.Parts, p => Assert.Equal("SK_RPK-74", p.Mesh.Name));
    }

    [FixturesFact]
    public void VehicleBlueprintPlacesAttachmentsOnTheirSockets()
    {
        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot);
        var model = new MeshPreviewLoader(catalog) { TextureSize = 64 }.LoadBlueprint(WolfsWagen);
        Assert.NotNull(model);
        Assert.Contains(model!.Parts, p => p.Mesh.Name == "SK_WolfsWagen");
        Assert.Contains(model.Parts, p => p.Mesh.Name == "SK_WolfsWagen_Door_FrontLeft");
        // The door hangs on the s_Door_FrontLeft socket, not at the chassis origin.
        var door = model.Parts.First(p => p.Mesh.Name == "SK_WolfsWagen_Door_FrontLeft");
        Assert.True(door.Transform.Translation.Size() > 50f, door.Transform.ToString());
        Assert.True(model.Parts.Count(p => p.Mesh.Name.StartsWith("SK_WolfsWagen_", StringComparison.Ordinal)) >= 4, string.Join(", ", model.Parts.Select(p => p.Name).Distinct()));
    }

    /// <summary>Uploads the vehicle preview offscreen and draws it; with <c>SCUMSTUDIO_SCREENSHOTS</c> the frame is saved to look at.</summary>
    [GlFact]
    public async Task VehiclePreviewRendersOffscreen()
    {
        if (FixturePaths.SkipReason is not null)
        {
            return;
        }

        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot);
        var model = new MeshPreviewLoader(catalog) { TextureSize = 512 }.LoadBlueprint(WolfsWagen)!;
        Assert.True(OffscreenGlContext.TryCreate(1280, 720, out var context, out var reason), reason);
        using (context)
        {
            context!.MakeCurrent();
            using var renderer = new SceneRenderer(context);
            using var scene = PreviewScene.Upload(renderer, model);
            Assert.False(scene.Bounds.IsEmpty);
            var camera = new FlyCamera();
            camera.Frame(scene.Bounds, 1280f / 720f, -135f, -20f);
            renderer.Settings = renderer.Settings with { GridHeight = scene.Bounds.Min.Y, GridCellSize = 50f };
            using var target = renderer.CreateTarget(1280, 720);
            var stats = renderer.Render(target, scene.Scene, camera);
            Assert.True(stats.Instances > 1, $"{stats.Instances} instances drawn");
            if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is { Length: > 0 } folder && folder is not "0")
            {
                Directory.CreateDirectory(folder);
                await ImageExport.SavePngAsync(target.ReadColorRgba(), target.Width, target.Height, Path.Combine(folder, "preview-wolfswagen-offscreen.png"));
            }
        }
    }

    [FixturesFact]
    public void MeshIsSplitByMaterialWithBaseColourTextures()
    {
        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot);
        var model = new MeshPreviewLoader(catalog) { TextureSize = 64 }.LoadMesh(WolfsWagenMesh);
        Assert.Equal("SK_WolfsWagen", model.Name);
        Assert.True(model.Parts.Count >= 2, $"{model.Parts.Count} parts");
        Assert.Contains(model.Parts, p => p.TexturePath is { } path && model.Textures[path].Width <= 64);
        Assert.Equal(model.Parts.Sum(p => p.Mesh.TriangleCount), new MeshPreviewLoader(catalog) { TextureSize = 0 }.LoadMesh(WolfsWagenMesh).Parts.Sum(p => p.Mesh.TriangleCount));
    }
}
