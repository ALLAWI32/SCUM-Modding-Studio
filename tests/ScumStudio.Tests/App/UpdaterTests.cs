using System.IO.Compression;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;

namespace ScumStudio.Tests.App;

public sealed class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-updater-tests", Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.1.0+44dcaa5", "0.1.0")]
    [InlineData("1.3-rc1", "1.3.0")]
    [InlineData("v2", "2.0.0")]
    public void VersionsAreReadFromTagsAndBuilds(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), Updater.ParseVersion(text));

    [Fact]
    public void TheLatestReleaseIsReadWithItsWindowsZip()
    {
        const string json = """
            { "tag_name": "v0.2.0", "draft": false, "prerelease": false, "html_url": "https://github.com/x/y/releases/tag/v0.2.0",
              "body": "## New\n- **Spawns** page\n- See [the guide](https://example.com)",
              "assets": [ { "name": "notes.txt", "browser_download_url": "https://a/notes.txt", "size": 5 },
                          { "name": "SCUM-Modding-Studio-0.2.0-win-x64.zip", "browser_download_url": "https://a/b.zip", "size": 123 } ] }
            """;
        var release = Updater.Parse(json)!;

        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("https://a/b.zip", release.ZipUrl);
        Assert.Equal(123, release.ZipSize);
        Assert.Null(Updater.Parse(json.Replace("\"draft\": false", "\"draft\": true", StringComparison.Ordinal)));
        Assert.Equal(
            [new NoteLine("New", true, false), new NoteLine("Spawns page", false, true), new NoteLine("See the guide", false, true)],
            MainWindowViewModel.ReleaseNotes(release.Notes));
    }

    [Fact]
    public void InstallingSwapsTheFilesAndKeepsTheOldOnesUntilTheNextStart()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "ScumStudio.App.exe"), "old app");
        File.WriteAllText(Path.Combine(app, "keep.txt"), "mine");
        var zip = Zip(("ScumStudio.App.exe", "new app"), ("scumstudio.exe", "new cli"));

        // The running exe is held open (as Windows holds a running program); it can still be renamed.
        using (new FileStream(Path.Combine(app, "ScumStudio.App.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Updater.Install(zip, app);
        }

        Assert.Equal("new app", File.ReadAllText(Path.Combine(app, "ScumStudio.App.exe")));
        Assert.Equal("new cli", File.ReadAllText(Path.Combine(app, "scumstudio.exe")));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(app, "ScumStudio.App.exe.old")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "keep.txt")));
        Assert.Equal(1, Updater.CleanUp(app));
        Assert.False(File.Exists(Path.Combine(app, "ScumStudio.App.exe.old")));
    }

    [Fact]
    public void ADownloadThatIsNotTheAppChangesNothing()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "ScumStudio.App.exe"), "old app");

        Assert.Throws<InvalidDataException>(() => Updater.Install(Zip(("readme.txt", "hi")), app));
        Assert.Throws<InvalidDataException>(() => Updater.Install(Zip(("ScumStudio.App.exe", "x"), ("../evil.txt", "x")), app));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(app, "ScumStudio.App.exe")));
        Assert.False(File.Exists(Path.Combine(app, "ScumStudio.App.exe.old")));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact]
    public void WhatsNewShowsOnceForTheVersionItWasKeptFor()
    {
        Updater.SaveWhatsNew(_dir, new ReleaseInfo(new Version(0, 2, 0), "v0.2.0", "- Spawns", null, 0, string.Empty));

        Assert.Null(Updater.TakeWhatsNew(_dir, new Version(0, 1, 0)));
        Updater.SaveWhatsNew(_dir, new ReleaseInfo(new Version(0, 2, 0), "v0.2.0", "- Spawns", null, 0, string.Empty));
        Assert.Equal("- Spawns", Updater.TakeWhatsNew(_dir, new Version(0, 2, 0)));
        Assert.Null(Updater.TakeWhatsNew(_dir, new Version(0, 2, 0)));
    }

    private string Zip(params (string Name, string Text)[] files)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }

        return path;
    }
}

public sealed class UpdateCardTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TheUpdateCardShowsTheNotesInsideTheCard()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            var notes = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"- **Feature {i}.** A long line of release notes that has to wrap inside the card instead of running past its right edge."));
            vm.Update = new ReleaseInfo(new Version(9, 9, 0), "v9.9.0", "## What's new\n" + notes, "https://example.com/a.zip", 1, "https://example.com");
            vm.OpenUpdateCommand.Execute(null);
            HeadlessUi.Pump();

            Assert.True(vm.IsUpdateOpen);
            Assert.True(HeadlessUi.FindNamed<Avalonia.Controls.Panel>(window, "UpdateOverlay")!.IsVisible);
            Assert.Equal(7, vm.UpdateNotes.Count);
            HeadlessUi.SaveScreenshot(window, "update-card");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
