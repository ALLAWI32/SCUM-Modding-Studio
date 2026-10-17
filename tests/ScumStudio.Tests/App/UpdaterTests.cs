using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Projects;

namespace ScumStudio.Tests.App;

public sealed class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-updater-tests", Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string List => Path.Combine(_dir, Updater.CleanupListFile);

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
            Updater.Install(zip, app, List);
        }

        Assert.Equal("new app", File.ReadAllText(Path.Combine(app, "ScumStudio.App.exe")));
        Assert.Equal("new cli", File.ReadAllText(Path.Combine(app, "scumstudio.exe")));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(app, "ScumStudio.App.exe.old")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "keep.txt")));
        Assert.Equal(1, Updater.CleanUp(app, List));
        Assert.False(File.Exists(Path.Combine(app, "ScumStudio.App.exe.old")));
    }

    /// <summary>
    /// seviscache (#6): "Access to the path ...ScumStudio.App.exe.old is denied, also as admin". An older version still runs
    /// from the .old (a second window, Claude Desktop's bridge on scumstudio.exe.old): Windows refuses to delete it.
    /// </summary>
    [Fact]
    public void AHeldOldFileDoesNotStopTheUpdate()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        var exe = Path.Combine(app, "ScumStudio.App.exe");
        File.WriteAllText(exe, "old app");
        File.WriteAllText(exe + ".old", "older app");

        using (new FileStream(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Updater.Install(Zip(("ScumStudio.App.exe", "new app")), app, List);

            Assert.Equal("new app", File.ReadAllText(exe));
            var backup = Assert.Single(Directory.GetFiles(app, "ScumStudio.App.exe.old*"), f => File.ReadAllText(f) == "old app");
            if (OperatingSystem.IsWindows()) // elsewhere an open file can be deleted, and the backup takes the plain name
            {
                Assert.Matches(@"ScumStudio\.App\.exe\.old-\d+$", backup);
            }

            Assert.Contains(backup, Listed());
        }

        Assert.True(Updater.CleanUp(app, List) >= 1);
        Assert.Equal(["ScumStudio.App.exe"], Directory.GetFileSystemEntries(app).Select(Path.GetFileName));
    }

    /// <summary>
    /// #6 as reported: the .old is a program still running (mapped as an image), which Windows renames but never deletes,
    /// not even for an admin.
    /// </summary>
    [Fact]
    public void AnOldFileThatStillRunsDoesNotStopTheUpdate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // elsewhere a running program's file can be deleted
        }

        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        var exe = Path.Combine(app, "ScumStudio.App.exe");
        File.WriteAllText(exe, "old app");
        // A loaded native library is mapped like a running exe; glfw3 ships with the tests for every Windows architecture.
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-" + arch, "native", "glfw3.dll"), exe + ".old");
        var image = NativeLibrary.Load(exe + ".old");
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(exe + ".old")); // "Access to the path ... is denied"

            Updater.Install(Zip(("ScumStudio.App.exe", "new app")), app, List);

            Assert.Equal("new app", File.ReadAllText(exe));
            Assert.Equal("old app", File.ReadAllText(Assert.Single(Directory.GetFiles(app, "ScumStudio.App.exe.old-*"))));
        }
        finally
        {
            NativeLibrary.Free(image);
        }

        Assert.Equal(2, Updater.CleanUp(app, List));
        Assert.Equal(["ScumStudio.App.exe"], Directory.GetFileSystemEntries(app).Select(Path.GetFileName));
    }

    /// <summary>The app unzipped into a shared folder: the next start deletes the backups the update made, nothing else.</summary>
    [Fact]
    public void CleanUpRemovesOnlyWhatTheUpdateLeft()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        Directory.CreateDirectory(Path.Combine(app, "sub"));
        File.WriteAllText(Path.Combine(app, "ScumStudio.App.exe"), "old app");
        string[] mine = [Path.Combine(app, "sub", "keep.ini.old"), Path.Combine(app, "user.txt.old"), Path.Combine(app, "sub", "ScumStudio.App.exe.old")];
        foreach (var file in mine)
        {
            File.WriteAllText(file, "mine");
        }

        Updater.Install(Zip(("ScumStudio.App.exe", "new app")), app, List);

        Assert.Equal(1, Updater.CleanUp(app, List));
        Assert.False(File.Exists(Path.Combine(app, "ScumStudio.App.exe.old")));
        Assert.All(mine, file => Assert.Equal("mine", File.ReadAllText(file)));
        Assert.Equal("new app", File.ReadAllText(Path.Combine(app, "ScumStudio.App.exe")));
    }

    /// <summary>Closing the app (or a crash) during "Installing…" must never leave a half-written exe in the app folder.</summary>
    [Fact]
    public void TheSwapOnlyRenamesFilesThatAreFullyStaged()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "ScumStudio.App.exe"), "old app");
        File.WriteAllText(Path.Combine(app, "scumstudio.exe"), "old cli");
        var big = new string('x', 300_000);
        var zip = Zip(("ScumStudio.App.exe", big), ("scumstudio.exe", "new cli"), ("README.md", "read me"));
        var lengths = new Dictionary<string, long> { ["ScumStudio.App.exe"] = big.Length, ["scumstudio.exe"] = 7, ["README.md"] = 7 };
        var staging = Path.Combine(app, ".update-");
        var ops = new List<string[]>();
        Updater.Trace = op =>
        {
            var parts = op.Split('|');
            ops.Add(parts);
            if (parts[0] == "move" && parts[1].StartsWith(staging, StringComparison.Ordinal))
            {
                Assert.Equal(lengths[Path.GetFileName(parts[2])], new FileInfo(parts[1]).Length); // complete before it goes in
            }
        };
        try
        {
            Updater.Install(zip, app, List);
        }
        finally
        {
            Updater.Trace = null;
        }

        Assert.Equal(3, ops.Count(o => o[0] == "extract"));
        Assert.All(ops, o => Assert.Contains(o[0], new[] { "extract", "move" }));
        Assert.All(ops.Where(o => o[0] == "extract"), o => Assert.StartsWith(staging, o[1], StringComparison.Ordinal));
        Assert.Equal(3, ops.Count(o => o[0] == "move" && o[1].StartsWith(staging, StringComparison.Ordinal)));
        Assert.Equal(big, File.ReadAllText(Path.Combine(app, "ScumStudio.App.exe")));
        Assert.Equal("new cli", File.ReadAllText(Path.Combine(app, "scumstudio.exe")));
        Assert.Empty(Directory.GetDirectories(app, ".update-*"));
    }

    [Fact]
    public void ACorruptDownloadLeavesTheAppFolderUntouched()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "ScumStudio.App.exe"), "old app");
        File.WriteAllText(Path.Combine(app, "scumstudio.exe"), "old cli");
        var zip = Zip(("ScumStudio.App.exe", new string('a', 5000)), ("scumstudio.exe", new string('b', 5000)), ("README.md", "read me"));
        CorruptEntry(zip, 1);
        var before = Snapshot(app);

        Assert.ThrowsAny<Exception>(() => Updater.Install(zip, app, List));

        Assert.Equal(before, Snapshot(app));
    }

    /// <summary>A file that cannot be renamed (opened without delete sharing): the error names it and every file goes back.</summary>
    [Fact]
    public void AFileInUseIsNamedAndEverythingIsPutBack()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // elsewhere an open file can be renamed
        }

        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "ScumStudio.App.exe"), "old app");
        File.WriteAllText(Path.Combine(app, "scumstudio.exe"), "old cli");
        var zip = Zip(("ScumStudio.App.exe", "new app"), ("scumstudio.exe", "new cli"), ("README.md", "read me"));
        var before = Snapshot(app);

        using (new FileStream(Path.Combine(app, "ScumStudio.App.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (new FileStream(Path.Combine(app, "scumstudio.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<UpdateFileInUseException>(() => Updater.Install(zip, app, List));
            Assert.Equal(Path.Combine(app, "scumstudio.exe"), ex.FilePath);
        }

        Assert.Equal(before, Snapshot(app));
    }

    [Fact]
    public void AReleaseOfAnotherVersionIsRefused()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "ScumStudio.App.exe"), "old app");
        var zip = Path.Combine(_dir, "release.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(typeof(Updater).Assembly.Location, "ScumStudio.App.exe"); // carries this build's version
        }

        Assert.Throws<InvalidDataException>(() => Updater.Install(zip, app, List, new Version(99, 0, 0)));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(app, "ScumStudio.App.exe")));
        Assert.Equal(["ScumStudio.App.exe"], Directory.GetFileSystemEntries(app).Select(Path.GetFileName));

        Updater.Install(zip, app, List, Updater.Current);
        Assert.Equal(new FileInfo(typeof(Updater).Assembly.Location).Length, new FileInfo(Path.Combine(app, "ScumStudio.App.exe")).Length);
    }

    [Fact]
    public void ADownloadThatIsNotTheAppChangesNothing()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "ScumStudio.App.exe"), "old app");

        Assert.Throws<InvalidDataException>(() => Updater.Install(Zip(("readme.txt", "hi")), app, List));
        Assert.Throws<InvalidDataException>(() => Updater.Install(Zip(("ScumStudio.App.exe", "x"), ("../evil.txt", "x")), app, List));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(app, "ScumStudio.App.exe")));
        Assert.False(File.Exists(Path.Combine(app, "ScumStudio.App.exe.old")));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    /// <summary>A connection that stops sending after the headers (VPN, captive Wi-Fi) used to freeze the card for good.</summary>
    [Fact]
    public async Task AStalledDownloadTimesOut()
    {
        var release = new ReleaseInfo(new Version(9, 9, 0), "v9.9.0", string.Empty, "https://example.invalid/a-win-x64.zip", 1000, string.Empty);
        var folder = Path.Combine(_dir, "updates");
        using var handler = new StallingHandler();
        var watch = Stopwatch.StartNew();
        var download = Updater.DownloadAsync(release, folder, null, CancellationToken.None, handler, TimeSpan.FromMilliseconds(200));

        Assert.Same(download, await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10))));
        await Assert.ThrowsAsync<TimeoutException>(() => download);
        if (!Ci.On) Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.Empty(Directory.GetFiles(folder));

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Updater.DownloadAsync(release, folder, null, cancel.Token, handler, TimeSpan.FromSeconds(30)).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void StartPassesThePidOfTheOldVersion()
    {
        var info = Updater.StartInfo(Path.Combine(_dir, "ScumStudio.App.exe"), 4242);

        Assert.Equal("--after-update 4242", info.Arguments);
        Assert.Equal(_dir, info.WorkingDirectory);
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

    private string[] Listed() => File.Exists(List) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(List)) ?? [] : [];

    private static string[] Snapshot(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Order(StringComparer.Ordinal)
            .Select(p => Path.GetFileName(p) + "=" + (File.Exists(p) ? Convert.ToHexString(File.ReadAllBytes(p)) : "dir"))];

    /// <summary>Breaks the deflate data of entry <paramref name="index"/> (an invalid block type) so reading it throws.</summary>
    private static void CorruptEntry(string zip, int index)
    {
        var bytes = File.ReadAllBytes(zip);
        var at = -1;
        for (var found = -1; found < index;)
        {
            at = bytes.AsSpan(at + 1).IndexOf("PK\u0003\u0004"u8) + at + 1;
            found++;
        }

        var data = at + 30 + BitConverter.ToUInt16(bytes, at + 26) + BitConverter.ToUInt16(bytes, at + 28);
        bytes.AsSpan(data, 4).Fill(0xFF);
        File.WriteAllBytes(zip, bytes);
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

    /// <summary>Sends the headers and a few bytes, then nothing (until cancelled).</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new StallingStream());
            content.Headers.ContentLength = 1000;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StallingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[..10].Fill(1);
                return 10;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

[Collection(LanguageCollection.Name)] // compares localized texts
public sealed class UpdateCardTests
{
    private static readonly ReleaseInfo Release = new(new Version(9, 9, 0), "v9.9.0", "- New", "https://example.invalid/a-win-x64.zip", 1, "https://example.invalid");

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

    /// <summary>The antivirus blocks starting the new exe: the update is in place, so say restart, and never install again.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task AFailedStartSaysRestartAndNeverInstallsTwice()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var path = ctx.Combine("projects", "Work.ssproj");
        Project.Create(path, "Work").Dispose();
        await ctx.Services.Projects.OpenAsync(path);
        using var vm = Shell(ctx, out var installs);
        vm.StartNewVersion = (_, _) => throw new Win32Exception(5, "Access is denied.");

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(Loc.F("Update.InstalledRestart", "9.9.0"), vm.UpdateStatus);
        Assert.True(vm.IsRestartNeeded);
        Assert.False(vm.InstallUpdateCommand.CanExecute(null));
        Assert.Equal(Path.GetFullPath(path), ctx.Services.Projects.DirectoryPath); // open again: work goes on until the restart
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Single(installs);
    }

    /// <summary>The new version must find the project free (it reopens it at once), and MCP's port closed.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheProjectIsClosedBeforeTheNewVersionStarts()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var path = ctx.Combine("projects", "Restart.ssproj");
        Project.Create(path, "Restart").Dispose();
        await ctx.Services.Projects.OpenAsync(path);
        using var vm = Shell(ctx, out _);
        var shutdown = false;
        vm.RequestShutdown = () => shutdown = true;
        var started = false;
        vm.StartNewVersion = (_, pid) =>
        {
            Assert.Equal(Environment.ProcessId, pid);
            Assert.Null(ctx.Services.Projects.Current);
            Project.Open(path).Dispose(); // what the new version does first
            started = true;
        };

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.True(started, vm.UpdateStatus);
        Assert.True(shutdown);
    }

    /// <summary>An AI tool (open_project) must not reopen the project in the gap: MCP stops before the project closes.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheMcpServerStopsBeforeTheProjectCloses()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var path = ctx.Combine("projects", "Order.ssproj");
        Project.Create(path, "Order").Dispose();
        await ctx.Services.Projects.OpenAsync(path);
        var mcp = ctx.Services.Mcp;
        mcp.PortOverride = 0;
        mcp.TokenOverride = "test-token";
        await mcp.ApplySettingsAsync();
        var port = new Uri(mcp.Url).Port;
        Assert.True(Accepts(port));
        bool? servingAtClose = null;
        ctx.Services.Projects.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectSession.Current) && ctx.Services.Projects.Current is null)
            {
                servingAtClose ??= Accepts(port);
            }
        };
        using var vm = Shell(ctx, out _);
        vm.RequestShutdown = () => { };

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.False(servingAtClose); // IsRunning is published later on the UI thread: ask the port itself

        static bool Accepts(int port)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                tcp.Connect(IPAddress.Loopback, port);
                return true;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task CancelStopsTheDownload()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var vm = Shell(ctx, out var installs);
        var downloading = new TaskCompletionSource();
        vm.DownloadUpdate = async (_, _, _, ct) =>
        {
            downloading.SetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return string.Empty;
        };
        vm.IsUpdateOpen = true;

        var run = vm.InstallUpdateCommand.ExecuteAsync(null);
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.CancelUpdateCommand.CanExecute(null));
        vm.CancelUpdateCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(vm.IsUpdating);
        Assert.Equal(Loc.T("Update.Cancelled"), vm.UpdateStatus);
        Assert.Empty(installs);
        vm.CloseUpdateCommand.Execute(null);
        Assert.False(vm.IsUpdateOpen);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task AStalledDownloadOrAFileInUseSaysWhatToDo()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var vm = Shell(ctx, out var installs);
        vm.DownloadUpdate = (_, _, _, _) => Task.FromException<string>(new TimeoutException("no data"));

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(Loc.F("Update.Stalled", 60), vm.UpdateStatus);
        Assert.False(vm.IsUpdating);
        Assert.Empty(installs);

        vm.DownloadUpdate = (_, folder, _, _) => Task.FromResult(Path.Combine(folder, "fake.zip"));
        vm.InstallUpdateFiles = (_, folder, _, _) => throw new UpdateFileInUseException(Path.Combine(folder, "scumstudio.exe"), new IOException("denied"));

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(Loc.F("Update.FileInUse", "scumstudio.exe"), vm.UpdateStatus);
        Assert.True(vm.InstallUpdateCommand.CanExecute(null)); // nothing was swapped: trying again is fine
    }

    /// <summary>A shell with an update out and fake steps (a fake download, an install that only counts).</summary>
    private static MainWindowViewModel Shell(AppTestContext ctx, out List<string> installs)
    {
        var calls = new List<string>();
        installs = calls;
        return new MainWindowViewModel(ctx.Services)
        {
            Update = Release,
            CanInstallHere = () => true,
            DownloadUpdate = (_, folder, _, _) => Task.FromResult(Path.Combine(folder, "fake.zip")),
            InstallUpdateFiles = (zip, _, _, _) => calls.Add(zip),
            StartNewVersion = (_, _) => { },
        };
    }
}
