using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner and Hektor: "import someone's pak mod (a map, cars) and work on it". A mod pak is unpacked into the project, the
/// app reads it over the game files, and the export carries it. Real files only: the game (<c>SCUM_PAKS</c>) and a mod pak
/// (<c>SCUMSTUDIO_TEST_MOD_PAK</c>, default: the owner's BMW car mod in the server's <c>~mods</c>).
/// </summary>
public sealed class ImportModRealTests
{
    private const string DefaultModPak = @"C:\SCUMServer\server\SCUM\Content\Paks\~mods\pakchunk97-BMW_P.pak";

    [Fact]
    public async Task AnImportedModIsReadOverTheGameAndCarriedByTheExport()
    {
        var mod = Environment.GetEnvironmentVariable("SCUMSTUDIO_TEST_MOD_PAK") is { Length: > 0 } m ? m : DefaultModPak;
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks) || !File.Exists(mod))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "With BMW");

        var (folder, files) = await ctx.Services.Projects.ImportModAsync(mod);
        Assert.True(files > 0);
        Assert.Equal([folder], ctx.Services.Projects.Mods);
        Assert.Equal("BMW", Path.GetFileName(folder));

        // The app now reads the mod's packages over the game files.
        var root = AssetCatalog.FindLooseProjectRoot(folder)!;
        var asset = Directory.EnumerateFiles(Path.Combine(root, "Content"), "*.uasset", SearchOption.AllDirectories).First();
        var packagePath = "/Game/" + Path.GetRelativePath(Path.Combine(root, "Content"), asset).Replace('\\', '/')[..^".uasset".Length];
        Assert.Equal([folder], ctx.Services.Workspace.ModFolders);
        Assert.True(ctx.Services.Workspace.Catalog!.PackageExists(packagePath), packagePath);

        // The export carries the mod's files next to the project's own edits.
        var rock = ctx.Services.Workspace.Catalog.PackageFiles.First(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && f.Contains("/Rocks/", StringComparison.OrdinalIgnoreCase));
        project.Apply(new AddStaticMeshActorOp("/Game/ConZ_Files/Maps/The_Island/A_4_Airfield", "Rock_Added",
            AssetPaths.ToPackagePath(rock, "SCUM") + "." + Path.GetFileNameWithoutExtension(rock), ScumStudio.Level.Model.TransformValue.Identity));
        var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false, Mods = ctx.Services.Projects.Mods });
        Assert.True(File.Exists(Path.Combine(result.StagingDirectory, "SCUM", Path.GetRelativePath(root, asset))));
        Assert.True(File.Exists(Path.Combine(result.StagingDirectory, "SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "A_4_Airfield.umap")));

        // Removed again: the game files are read without it.
        await ctx.Services.Projects.RemoveModAsync(folder);
        Assert.Empty(ctx.Services.Projects.Mods);
        Assert.Empty(ctx.Services.Workspace.ModFolders);
    }
}
