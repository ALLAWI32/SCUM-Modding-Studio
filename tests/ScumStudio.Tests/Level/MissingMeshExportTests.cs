using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak.Writing;
using ScumStudio.Tests.Modding;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The export's own check of a placed mesh (<see cref="MissingMeshExportRealTests"/> has the user's case on the real game
/// files): what it judges against, and what it must keep. Synthetic game, no SCUM files needed.
/// </summary>
public sealed class MissingMeshExportTests
{
    private const string LevelPath = SyntheticLevels.LevelPath;

    /// <summary>
    /// The game as paks (only paks say what the game has) with an imported mod read over them, as the app opens them: a
    /// placed mesh of the game and one only the mod ships are both created; one neither has is left out and named. O
    /// Piqueno's export checked 216 packages (imported mods): their meshes must stay in.
    /// </summary>
    [Fact]
    public async Task KeepsPlacedMeshesOfTheGameAndOfAnImportedModAndLeavesOutTheRest()
    {
        using var temp = new LevelTempDirectory();
        var game = temp.Combine("game");
        SyntheticGame.WriteGame(game);
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        SyntheticGame.Write(game, SyntheticLevels.RockPackage, SyntheticGame.Package(SyntheticLevels.RockPackage));
        var paks = temp.Combine("Paks");
        Directory.CreateDirectory(paks);
        await new PakWriter().WriteFromDirectoryAsync(game, Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"));

        const string ModRock = "/Game/Mods/SM_ModRock";
        const string Gone = "/Game/Nowhere/SM_Gone.SM_Gone";
        var mod = temp.Combine("project", ProjectMods.FolderName, "ModA");
        SyntheticGame.Write(mod, ModRock, SyntheticGame.Package(ModRock));

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { LooseOverlays = [mod] });
        Assert.True(catalog.PackageExists(SyntheticLevels.RockPackage));
        Assert.True(catalog.PackageExists(ModRock)); // the mod's, read over the game
        Assert.False(catalog.PackageExists(Gone));
        using var project = Project.Create(temp.Combine("project", "Mods.ssproj"), "Mods");
        var at = TransformValue.At(-900, 250, 0);
        project.Apply(new AddStaticMeshActorOp(LevelPath, "SM_Rock_Added", SyntheticLevels.RockPackage + ".SM_Rock", at));
        project.Apply(new AddStaticMeshActorOp(LevelPath, "SM_ModRock_Added", ModRock + ".SM_ModRock", at));
        project.Apply(new AddStaticMeshActorOp(LevelPath, "SM_Gone_Added", Gone, at));

        var result = await new ProjectExporter().ExportAsync(project, catalog,
            new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false, Mods = [mod], Validate = false }); // the synthetic game names packages it does not hold

        Assert.Equal(["SM_Rock_Added", "SM_ModRock_Added"], Assert.Single(result.Levels).Report.AddedActors);
        var warning = Assert.Single(result.Warnings, w => w.Contains("neither the game nor the mod has the mesh", StringComparison.Ordinal));
        Assert.Contains("SM_Gone_Added", warning, StringComparison.Ordinal);
        Assert.Contains(Gone, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("SM_ModRock", StringComparison.Ordinal) || w.Contains("SM_Rock_Added", StringComparison.Ordinal));
    }
}
