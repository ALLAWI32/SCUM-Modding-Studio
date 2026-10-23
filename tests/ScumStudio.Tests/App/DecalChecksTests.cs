using ScumStudio.App.Services;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Textures;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner, 2026-10-18: his cloned Rager showed its stickers in the studio and none in the game. The Decals tab had saved them
/// (decals/BPC_Rager1.json), but Apply decals, which bakes them into the clone's textures, was never pressed, and the export
/// said nothing. An export now names such a clone: never applied, or changed after the last apply.
/// </summary>
public sealed class DecalChecksTests : IDisposable
{
    private const string Rager = "/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager";
    private const string MyRager = "/Game/ConZ_Files/Vehicles/Car/Rager/BPC_MyRager";
    private const string Outer = "/Game/ConZ_Files/Models/Vehicles2/Pickup/Materials/MI_Rager_Outer";
    private const string MyOuter = "/Game/ConZ_Files/Models/Vehicles2/Pickup/Materials/MI_MyRager_Outer";
    private const string StockTexture = "/Game/ConZ_Files/Models/Vehicles2/Pickup/Textures/T_Rager_Outer_D.T_Rager_Outer_D";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-decalchecks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task DecalsDrawnButNotAppliedAreNamedUntilTheyAreBakedAndAgainWhenTheyChange()
    {
        using var project = await Project.CreateAsync(Path.Combine(_dir, "project"), "MyMapMod");
        project.Apply(new CloneAssetOp("vehicle", Rager, MyRager, [new PackagePair(Rager, MyRager), new PackagePair(Outer, MyOuter)]));
        Assert.Empty(DecalChecks.Pending(project.DirectoryPath, project.State)); // nothing drawn

        // Decals drawn (the tab saves them as they are placed), none applied: the pak would have none of them.
        var sticker = new DecalLayer { MaterialPath = Outer + ".MI_Rager_Outer", DesignId = "flame-1", WidthCm = 60, HeightCm = 30 };
        var decals = new DecalSet("BPC_MyRager", [sticker]);
        decals.Save(project.DirectoryPath);
        var pending = Assert.Single(DecalChecks.Pending(project.DirectoryPath, project.State));
        Assert.Equal(("BPC_MyRager", true), pending);
        var warning = Assert.Single(DecalChecks.Warnings([pending]));
        Assert.StartsWith("BPC_MyRager: its decals are drawn but not applied", warning, StringComparison.Ordinal);
        Assert.Contains("Apply decals", warning, StringComparison.Ordinal);

        // Hidden layers only: nothing to bake, nothing said.
        (decals with { Layers = [sticker with { Hidden = true }] }).Save(project.DirectoryPath);
        Assert.Empty(DecalChecks.Pending(project.DirectoryPath, project.State));
        decals.Save(project.DirectoryPath);

        // Apply decals: the pixels of the clone's own Diffuse Map texture are recorded (ModulePageViewModel.TextureReplaceOp).
        var picture = Path.Combine(project.DirectoryPath, ProjectTextures.FolderName, "bake.png");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        File.WriteAllBytes(picture, [1, 2, 3]);
        File.SetLastWriteTimeUtc(picture, DateTime.UtcNow.AddSeconds(10)); // after the save above, on any file system
        var texture = TextureWrites.NewPackage(MyRager, MyOuter, "Diffuse Map");
        var bake = new SetAssetValueOp(texture, PackageMap.Leaf(texture), TextureWrites.PixelsPath, "Texture", string.Empty, TextureWrites.Value(StockTexture, ProjectTextures.FolderName + "/bake.png"));
        project.Apply(bake);
        Assert.Empty(DecalChecks.Pending(project.DirectoryPath, project.State));

        // The decals change after the bake: the pak would have the earlier ones.
        File.SetLastWriteTimeUtc(DecalSet.PathOf(project.DirectoryPath, "BPC_MyRager"), DateTime.UtcNow.AddMinutes(1));
        pending = Assert.Single(DecalChecks.Pending(project.DirectoryPath, project.State));
        Assert.Equal(("BPC_MyRager", false), pending);
        Assert.StartsWith("BPC_MyRager: its decals changed after they were last applied", Assert.Single(DecalChecks.Warnings([pending])), StringComparison.Ordinal);

        // Ctrl+Z on the apply: never applied again.
        project.Apply(bake.Inverse());
        Assert.Equal(("BPC_MyRager", true), Assert.Single(DecalChecks.Pending(project.DirectoryPath, project.State)));

        // Another clone without decals, and a stock vehicle, are never named; an unreadable decals file is the tab's to report.
        project.Apply(new CloneAssetOp("vehicle", Rager, Rager + "2", [new PackagePair(Rager, Rager + "2")]));
        File.WriteAllText(DecalSet.PathOf(project.DirectoryPath, "BPC_Rager2"), "{ not json");
        Assert.Single(DecalChecks.Pending(project.DirectoryPath, project.State));
    }

    /// <summary>
    /// Review 3.5.1: a sticker nudged and nudged back (or Ctrl+Z in the tab) saved the decals again; Apply decals then baked the
    /// same pixels, which the project's pictures already had (named by their content: nothing rewritten, nothing recorded), so
    /// every later export kept saying the decals changed after the last apply. Pressing Apply decals now clears it.
    /// </summary>
    [Fact]
    public async Task ApplyingTheSameStickersAgainClearsTheWarning()
    {
        using var project = await Project.CreateAsync(Path.Combine(_dir, "project"), "MyMapMod");
        project.Apply(new CloneAssetOp("vehicle", Rager, MyRager, [new PackagePair(Rager, MyRager), new PackagePair(Outer, MyOuter)]));
        var sticker = new DecalLayer { MaterialPath = Outer + ".MI_Rager_Outer", DesignId = "flame-1", WidthCm = 60, HeightCm = 30 };
        new DecalSet("BPC_MyRager", [sticker]).Save(project.DirectoryPath);

        // Apply decals: the picture is saved (named by its pixels) and recorded on the clone's Diffuse Map.
        var rgba = new byte[] { 10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255, 1, 2, 3, 255 };
        var file = await ProjectTextures.SaveAsync(project.DirectoryPath, rgba, 2, 2);
        var picture = TextureWrites.ResolveFile(project.DirectoryPath, file)!;
        File.SetLastWriteTimeUtc(picture, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(DecalSet.PathOf(project.DirectoryPath, "BPC_MyRager"), DateTime.UtcNow.AddMinutes(-11));
        var texture = TextureWrites.NewPackage(MyRager, MyOuter, "Diffuse Map");
        project.Apply(new SetAssetValueOp(texture, PackageMap.Leaf(texture), TextureWrites.PixelsPath, "Texture", string.Empty, TextureWrites.Value(StockTexture, file)));
        Assert.Empty(DecalChecks.Pending(project.DirectoryPath, project.State));

        // Nudged and nudged back: the decals file is newer than the bake, its stickers the same.
        File.SetLastWriteTimeUtc(DecalSet.PathOf(project.DirectoryPath, "BPC_MyRager"), DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(("BPC_MyRager", false), Assert.Single(DecalChecks.Pending(project.DirectoryPath, project.State)));

        // Apply decals again bakes the same pixels: the same picture, no new value; the warning goes.
        Assert.Equal(file, await ProjectTextures.SaveAsync(project.DirectoryPath, rgba, 2, 2));
        Assert.Empty(DecalChecks.Pending(project.DirectoryPath, project.State));
    }

    /// <summary>
    /// Review 3.5.1: stickers applied and then all hidden (or deleted down to hidden ones) showed a clean car in the studio while
    /// the pak, and the game, still had the baked stickers; the export said nothing.
    /// </summary>
    [Fact]
    public async Task StickersAppliedAndThenHiddenAreNamed()
    {
        using var project = await Project.CreateAsync(Path.Combine(_dir, "project"), "MyMapMod");
        project.Apply(new CloneAssetOp("vehicle", Rager, MyRager, [new PackagePair(Rager, MyRager), new PackagePair(Outer, MyOuter)]));
        var sticker = new DecalLayer { MaterialPath = Outer + ".MI_Rager_Outer", DesignId = "flame-1", WidthCm = 60, HeightCm = 30 };
        new DecalSet("BPC_MyRager", [sticker]).Save(project.DirectoryPath);
        var picture = Path.Combine(project.DirectoryPath, ProjectTextures.FolderName, "bake.png");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        File.WriteAllBytes(picture, [1, 2, 3]);
        File.SetLastWriteTimeUtc(picture, DateTime.UtcNow.AddMinutes(-10));
        var texture = TextureWrites.NewPackage(MyRager, MyOuter, "Diffuse Map");
        project.Apply(new SetAssetValueOp(texture, PackageMap.Leaf(texture), TextureWrites.PixelsPath, "Texture", string.Empty, TextureWrites.Value(StockTexture, ProjectTextures.FolderName + "/bake.png")));

        // Every sticker hidden after the bake: the pak still has them.
        new DecalSet("BPC_MyRager", [sticker with { Hidden = true }]).Save(project.DirectoryPath);
        Assert.Equal(("BPC_MyRager", false), Assert.Single(DecalChecks.Pending(project.DirectoryPath, project.State)));
    }
}
