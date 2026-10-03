using ScumStudio.Core.Games;

namespace ScumStudio.Tests.Core;

public sealed class GameLocatorTests
{
    private static string Vdf(string path) => path.Replace("\\", "\\\\");

    /// <summary>Creates a fake SCUM install below <paramref name="installDir"/> and returns its Paks folder.</summary>
    private static string MakeInstall(TempFolder temp, string exeName, params string[] installDir)
    {
        temp.File("x", [.. installDir, "SCUM", "Content", "Paks", "pakchunk0-WindowsNoEditor.pak"]);
        temp.File("x", [.. installDir, "SCUM", "Binaries", "Win64", exeName]);
        return Path.Combine([temp.Path, .. installDir, "SCUM", "Content", "Paks"]);
    }

    private static string Manifest(string appId, string installDir) => $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"Universe"		"1"
        	"name"		"SCUM"
        	"installdir"		"{{installDir}}"
        	"StateFlags"		"4"
        }
        """;

    private static GameLocator Locator(params string[] steamRoots) =>
        new(new GameLocatorOptions { SteamRoots = steamRoots, ServerSearchRoots = [] });

    [Fact]
    public void FindsClientThroughLibraryFoldersAndManifest()
    {
        using var temp = new TempFolder();
        var steam = temp.Dir("Steam");
        var library = temp.Dir("Games", "SteamLibrary");
        temp.File($$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{Vdf(steam)}}"
            		"label"		""
            		"apps" { "228980" "1" }
            	}
            	"1"
            	{
            		"path"		"{{Vdf(library)}}"
            		"apps"
            		{
            			"513710"		"20000000000"
            		}
            	}
            }
            """, "Steam", "steamapps", "libraryfolders.vdf");
        temp.File(Manifest(GameLocator.ScumAppId, "SCUM"), "Games", "SteamLibrary", "steamapps", "appmanifest_513710.acf");
        var paks = MakeInstall(temp, GameLocator.ClientExecutableName, "Games", "SteamLibrary", "steamapps", "common", "SCUM");

        var locator = Locator(steam);
        Assert.Equal([steam, library], locator.GetSteamLibraries());

        var game = locator.FindGame();
        Assert.NotNull(game);
        Assert.Equal(GameInstallKind.Client, game!.Kind);
        Assert.Equal(paks, game.PaksDirectory);
        Assert.Equal(Path.Combine(library, "steamapps", "common", "SCUM"), game.InstallDirectory);
        Assert.Equal(Path.Combine(game.InstallDirectory, "SCUM", "Binaries", "Win64", "SCUM.exe"), game.ExecutablePath);
        Assert.Equal(Path.Combine(paks, "~mods"), game.ModsDirectory);
        Assert.Contains("appmanifest_513710.acf", game.Source);
        Assert.True(GameLocator.ContainsPaks(game.PaksDirectory));
    }

    [Fact]
    public void ReadsLegacyLibraryFormatAndCustomInstallDir()
    {
        using var temp = new TempFolder();
        var steam = temp.Dir("Steam");
        var library = temp.Dir("D", "SteamLibrary");
        temp.File($$"""
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1600000000"
            	"ContentStatsID"		"-123"
            	"1"		"{{Vdf(library)}}"
            }
            """, "Steam", "steamapps", "libraryfolders.vdf");
        temp.File(Manifest(GameLocator.ScumAppId, "SCUM Game"), "D", "SteamLibrary", "steamapps", "appmanifest_513710.acf");
        var paks = MakeInstall(temp, GameLocator.ClientExecutableName, "D", "SteamLibrary", "steamapps", "common", "SCUM Game");

        var game = Locator(steam).FindGame();
        Assert.NotNull(game);
        Assert.Equal(paks, game!.PaksDirectory);
    }

    [Fact]
    public void FallsBackToCommonFolderWithoutManifest()
    {
        using var temp = new TempFolder();
        var steam = temp.Dir("Steam");
        var paks = MakeInstall(temp, GameLocator.ClientExecutableName, "Steam", "steamapps", "common", "SCUM");

        var game = Locator(steam).FindGame();
        Assert.NotNull(game);
        Assert.Equal(paks, game!.PaksDirectory);
        Assert.Contains("folder", game.Source);
    }

    [Fact]
    public void ManifestPointingToMissingFolderFindsNothing()
    {
        using var temp = new TempFolder();
        var steam = temp.Dir("Steam");
        temp.File(Manifest(GameLocator.ScumAppId, "SCUM"), "Steam", "steamapps", "appmanifest_513710.acf");
        temp.Dir("Steam", "steamapps", "common", "SCUM", "SCUM"); // no Content/Paks

        Assert.Null(Locator(steam).FindGame());
        Assert.Null(Locator(Path.Combine(temp.Path, "no-steam-here")).FindGame());
        Assert.Empty(Locator().GetSteamRoots());
    }

    [Fact]
    public void MalformedLibraryFileIsReportedAndSkipped()
    {
        using var temp = new TempFolder();
        using var log = new CapturedLog();
        var steam = temp.Dir("Steam");
        temp.File("\"libraryfolders\" { \"0\" { \"path\" ", "Steam", "steamapps", "libraryfolders.vdf");
        var paks = MakeInstall(temp, GameLocator.ClientExecutableName, "Steam", "steamapps", "common", "SCUM");

        var locator = new GameLocator(new GameLocatorOptions { SteamRoots = [steam], ServerSearchRoots = [] }, log.Logger);
        Assert.Equal(paks, locator.FindGame()?.PaksDirectory);
        Assert.Contains("libraryfolders.vdf", log.Text);
    }

    [Fact]
    public void FindsDedicatedServerByExecutable()
    {
        using var temp = new TempFolder();
        var root = temp.Dir("SCUMServer");
        var paks = MakeInstall(temp, "scumserver.EXE", "SCUMServer", "server");
        temp.File("log", "SCUMServer", "server", "SCUM", "Saved", "Logs", "SCUMServer.exe"); // inside Saved: ignored

        var servers = GameLocator.FindDedicatedServersUnder(root);
        var server = Assert.Single(servers);
        Assert.Equal(GameInstallKind.DedicatedServer, server.Kind);
        Assert.Equal(paks, server.PaksDirectory);
        Assert.Equal(Path.Combine(root, "server"), server.InstallDirectory);
        Assert.EndsWith("scumserver.EXE", server.ExecutablePath);

        var locator = new GameLocator(new GameLocatorOptions { SteamRoots = [], ServerSearchRoots = [root] });
        Assert.Equal(paks, locator.FindDedicatedServer()?.PaksDirectory);
        Assert.Null(locator.FindGame());

        // Depth limit: server/SCUM/Binaries/Win64 is four levels below the root.
        Assert.Empty(GameLocator.FindDedicatedServersUnder(root, maxDepth: 3));
        Assert.Single(GameLocator.FindDedicatedServersUnder(root, maxDepth: 4));
        Assert.Empty(GameLocator.FindDedicatedServersUnder(Path.Combine(temp.Path, "missing")));
    }

    [Fact]
    public void FindsDedicatedServerThroughSteamManifest()
    {
        using var temp = new TempFolder();
        var steam = temp.Dir("Steam");
        temp.File(Manifest(GameLocator.ScumServerAppId, "SCUM Dedicated Server"), "Steam", "steamapps", "appmanifest_3792580.acf");
        var paks = MakeInstall(temp, GameLocator.ServerExecutableName, "Steam", "steamapps", "common", "SCUM Dedicated Server");

        var locator = Locator(steam);
        var server = Assert.Single(locator.FindDedicatedServers());
        Assert.Equal(paks, server.PaksDirectory);
        Assert.NotNull(server.ExecutablePath);
        Assert.Null(locator.FindGame());
    }

    [Fact]
    public void ResolvesPaksFolderFromAnyPartOfTheInstall()
    {
        using var temp = new TempFolder();
        var paks = MakeInstall(temp, GameLocator.ClientExecutableName, "common", "SCUM");
        temp.Dir("common", "SCUM", "SCUM", "Content", "Paks", "~mods");
        var install = Path.Combine(temp.Path, "common", "SCUM");

        string[] inputs =
        [
            install,
            install + Path.DirectorySeparatorChar,
            Path.Combine(install, "SCUM"),
            Path.Combine(install, "SCUM", "Content"),
            paks,
            Path.Combine(paks, "~mods"),
            Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"),
            Path.Combine(install, "SCUM", "Binaries", "Win64"),
            Path.Combine(install, "SCUM", "Binaries", "Win64", "SCUM.exe"),
            "\"" + install + "\"",
        ];
        foreach (var input in inputs)
        {
            Assert.Equal(paks, GameLocator.ResolvePaksFolder(input));
        }

        Assert.Null(GameLocator.ResolvePaksFolder(temp.Dir("unrelated", "folder")));
        Assert.Null(GameLocator.ResolvePaksFolder(Path.Combine(temp.Path, "missing")));
        Assert.Null(GameLocator.ResolvePaksFolder("   "));
        Assert.False(GameLocator.ContainsPaks(temp.Dir("empty", "Paks")));
    }

    [Fact]
    public void ResolvesLowerCaseFoldersOnCaseSensitiveFileSystems()
    {
        using var temp = new TempFolder();
        var paks = temp.Dir("scum", "content", "paks");

        // Linux resolves the real lower-case names; Windows (case-insensitive) returns the probed casing.
        Assert.Equal(paks, GameLocator.ResolvePaksFolder(temp.Path), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void KeyValuesParserHandlesSteamSyntax()
    {
        var root = KeyValuesNode.Parse("""
            // comment line
            "Root"
            {
                "path"      "C:\\Program Files (x86)\\Steam"   // trailing comment
                "quote"     "say \"hi\"\tnow"
                unquoted    value
                "platform"  "win"   [$WIN32]
                "Nested"    [$WINDOWS]
                {
                    "Deep"  "1"
                }
                "empty" {}
            }
            """);

        var section = Assert.Single(root.Children);
        Assert.True(section.IsSection);
        Assert.Equal(@"C:\Program Files (x86)\Steam", section.GetValue("PATH"));
        Assert.Equal("say \"hi\"\tnow", section.GetValue("quote"));
        Assert.Equal("value", section.GetValue("unquoted"));
        Assert.Equal("win", section.GetValue("platform"));
        Assert.Equal("1", section.Child("nested")?.GetValue("deep"));
        Assert.Empty(section.Child("empty")!.Children);
        Assert.Null(section.Child("missing"));
        Assert.Equal(6, section.Children.Count);

        Assert.Throws<FormatException>(() => KeyValuesNode.Parse("\"a\" { \"b\" \"c\""));
        Assert.Throws<FormatException>(() => KeyValuesNode.Parse("\"a\" \"unterminated"));
        Assert.Throws<FormatException>(() => KeyValuesNode.Parse("}"));
        Assert.Throws<FormatException>(() => KeyValuesNode.Parse("\"key\""));
        Assert.Empty(KeyValuesNode.Parse("  // only a comment\n").Children);
    }
}
