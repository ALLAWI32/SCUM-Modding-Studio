using ScumStudio.Level.Projects;
using ScumStudio.Pak.Reading;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Hektor's map pak (found by another session): zlib-compressed, a GameFeature plugin level next to SCUM/Content, 210 MB of
/// JSON dumps and pictures, 5.8 GB in all. The import keeps what the game loads, the export can leave a big mod out, and
/// plugin content survives the pak writer.
/// </summary>
public sealed class ProjectModsTests : IDisposable
{
    private const string Plugin = "SCUM/Plugins/GameFeatures/WoodlandHunterPack/Content/World/Maps/The_Island/C_2_Outpost_HuntersGrotto.umap";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-mods-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AZlibModWithAPluginLevelIsImportedWithoutJunkAndCanStayOutOfTheExport()
    {
        // The mod as Hektor's tools packed it.
        var staging = Path.Combine(_dir, "hektor");
        var files = new Dictionary<string, byte[]>
        {
            ["SCUM/Content/ConZ_Files/Maps/The_Island/Landscape_A_0_1.umap"] = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 7)).ToArray(),
            ["SCUM/Content/ConZ_Files/Maps/The_Island/Landscape_A_0_1.uexp"] = Enumerable.Range(0, 90_000).Select(i => (byte)(i % 13)).ToArray(),
            [Plugin] = [1, 2, 3, 4, 5],
            ["SCUM/Content/WaterSplines.json"] = "{\"dump\": true}"u8.ToArray(),
            ["SCUM/Content/Preview.png"] = [137, 80, 78, 71],
        };
        foreach (var (path, data) in files)
        {
            var file = Path.Combine(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, data);
        }

        var pak = Path.Combine(_dir, "pakchunk90-DesertMap.pak");
        await new PakWriter(new PakWriterOptions { Compression = PakCompression.Zlib, OnlyGameContent = false }).WriteFromDirectoryAsync(staging, pak, null, CancellationToken.None);

        // Imported: the packages, inflated, the plugin level too; no dumps or pictures.
        var project = Path.Combine(_dir, "project");
        var (folder, count) = await ProjectMods.ImportAsync(project, pak, null);
        Assert.Equal("DesertMap", Path.GetFileName(folder));
        Assert.Equal(3, count);
        foreach (var path in files.Keys.Where(ProjectMods.IsGameFile))
        {
            Assert.Equal(files[path], await File.ReadAllBytesAsync(Path.Combine(folder, path)));
        }

        Assert.False(File.Exists(Path.Combine(folder, "SCUM/Content/WaterSplines.json")));
        Assert.False(File.Exists(Path.Combine(folder, "SCUM/Content/Preview.png")));

        // Carried into the export (plugin level included) and packed with it, unless it is to stay its own pak.
        var export = Path.Combine(_dir, "export");
        Assert.True(ProjectMods.IsCarried(folder));
        Assert.Equal(3, ProjectMods.CarryInto([folder], export));
        var ours = Path.Combine(_dir, "ours.pak");
        await new PakWriter(new PakWriterOptions()).WriteFromDirectoryAsync(export, ours, null, CancellationToken.None);
        using (var written = PakFileSource.OpenFile(ours))
        {
            Assert.True(written.Exists(Plugin));
        }

        ProjectMods.SetCarried(folder, false);
        Assert.False(ProjectMods.IsCarried(folder));
        Assert.Equal(0, ProjectMods.CarryInto([folder], Path.Combine(_dir, "export2")));
        Assert.Equal([folder], ProjectMods.Folders(project)); // still read over the game
        ProjectMods.SetCarried(folder, true);
        Assert.True(ProjectMods.IsCarried(folder));
    }
}
