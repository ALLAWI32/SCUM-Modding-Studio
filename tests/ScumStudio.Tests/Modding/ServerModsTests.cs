using System.IO.Compression;
using MoonSharp.Interpreter;
using ScumStudio.Modding.ServerMods;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Modding;

/// <summary>UE4SS installation and the server's Lua mods, on a fake server folder (no game files needed).</summary>
public sealed class ServerModsTests
{
    private static string FakeServer(LevelTempDirectory temp)
    {
        var win64 = temp.Combine("SCUM Server", "SCUM", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        File.WriteAllText(Path.Combine(win64, "SCUMServer.exe"), "exe");
        Directory.CreateDirectory(temp.Combine("SCUM Server", "SCUM", "Content", "Paks"));
        return win64;
    }

    private static string FakeUe4ssZip(LevelTempDirectory temp, bool evil = false)
    {
        var zipPath = temp.Combine("UE4SS.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }

        Add("dwmapi.dll", "proxy");
        Add("ue4ss/UE4SS.dll", "dll");
        Add("ue4ss/UE4SS-settings.ini", "[General]\r\nbUseUObjectArrayCache = true\r\nbForceGUObjectArrayForIteration = false\r\nEnableAutoReloadingLuaMods = 0\r\n; keep me\r\n[Debug]\r\nGuiConsoleEnabled = 1\r\nConsoleEnabled = 1\r\nGuiConsoleVisible = 1\r\n[Hooks]\r\nHookProcessInternal = 1\r\nHookBeginPlay  = 1\r\nHookEngineTick = 0\r\nHookUObjectProcessEvent = 0\r\n");
        Add("ue4ss/Mods/mods.txt", "ConsoleCommandsMod : 1\r\nKeybinds : 1\r\n");
        Add("ue4ss/Mods/ConsoleCommandsMod/Scripts/main.lua", "-- stock");
        if (evil)
        {
            Add("../escape.txt", "x");
        }

        return zipPath;
    }

    [Fact]
    public void TheServerIsFoundFromAnyFolderInsideIt()
    {
        using var temp = new LevelTempDirectory();
        var win64 = FakeServer(temp);

        foreach (var start in new[] { win64, temp.Combine("SCUM Server"), temp.Combine("SCUM Server", "SCUM"), temp.Combine("SCUM Server", "SCUM", "Content", "Paks") })
        {
            Assert.Equal(win64, ServerInstall.Find(start)?.Win64);
        }

        Assert.Null(ServerInstall.Find(temp.Combine("elsewhere")));
        Assert.Null(ServerInstall.Find(null));
    }

    [Fact]
    public void InstallingUnpacksUe4ssAndSetsItUpForAServer()
    {
        using var temp = new LevelTempDirectory();
        var server = ServerInstall.Find(FakeServer(temp))!;

        Assert.False(server.HasUe4ss);
        Assert.Equal(5, Ue4ssInstaller.InstallFromZip(server, FakeUe4ssZip(temp)));

        Assert.True(server.IsUe4ssActive);
        var ini = File.ReadAllText(server.SettingsFile);
        Assert.Contains("bForceGUObjectArrayForIteration = true", ini, StringComparison.Ordinal);
        Assert.Contains("bUseUObjectArrayCache = false", ini, StringComparison.Ordinal);
        Assert.Contains("EnableAutoReloadingLuaMods = 1", ini, StringComparison.Ordinal);
        Assert.Contains("GuiConsoleEnabled = 0", ini, StringComparison.Ordinal);
        Assert.Contains("ConsoleEnabled = 0", ini, StringComparison.Ordinal);
        Assert.Contains("HookProcessInternal = 0", ini, StringComparison.Ordinal); // the stock hooks crash the server on a player join
        Assert.Contains("HookBeginPlay  = 0", ini, StringComparison.Ordinal);
        Assert.Contains("HookEngineTick = 1", ini, StringComparison.Ordinal);
        Assert.Contains("HookUObjectProcessEvent = 1", ini, StringComparison.Ordinal);
        Assert.Contains("; keep me", ini, StringComparison.Ordinal);
        Assert.Contains("\r\n", ini, StringComparison.Ordinal);

        var store = new ServerModStore(server);
        Assert.Empty(store.List());
        Assert.Contains("ConsoleCommandsMod : 0", File.ReadAllText(Path.Combine(server.ModsFolder, "mods.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void ReinstallingKeepsTheUsersSettingsAndMods()
    {
        using var temp = new LevelTempDirectory();
        var server = ServerInstall.Find(FakeServer(temp))!;
        var zip = FakeUe4ssZip(temp);
        Ue4ssInstaller.InstallFromZip(server, zip);
        var store = new ServerModStore(server);
        store.Save("Mine", "-- my mod");
        File.AppendAllText(server.SettingsFile, "MyOwnKey = 5\r\n");

        Ue4ssInstaller.InstallFromZip(server, zip);

        Assert.Contains("MyOwnKey = 5", File.ReadAllText(server.SettingsFile), StringComparison.Ordinal);
        Assert.Equal("-- my mod", store.Read("Mine"));
        Assert.Equal(["Mine"], store.List().Select(m => m.Name));
    }

    [Fact]
    public void AZipThatLeavesTheFolderIsRefusedAndNotAUe4ssZipToo()
    {
        using var temp = new LevelTempDirectory();
        var server = ServerInstall.Find(FakeServer(temp))!;

        Assert.Throws<InvalidDataException>(() => Ue4ssInstaller.InstallFromZip(server, FakeUe4ssZip(temp, evil: true)));
        Assert.False(File.Exists(temp.Combine("SCUM Server", "SCUM", "Binaries", "escape.txt")));

        var other = temp.Combine("other.zip");
        using (var zip = ZipFile.Open(other, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readme.txt");
        }

        Assert.Throws<InvalidDataException>(() => Ue4ssInstaller.InstallFromZip(server, other));
    }

    [Fact]
    public void UE4SSCanBeSwitchedOffAndOnAgain()
    {
        using var temp = new LevelTempDirectory();
        var server = ServerInstall.Find(FakeServer(temp))!;
        Ue4ssInstaller.InstallFromZip(server, FakeUe4ssZip(temp));

        Ue4ssInstaller.SetActive(server, false);
        Assert.True(server.HasUe4ss);
        Assert.False(server.IsUe4ssActive);
        Assert.True(File.Exists(server.ParkedProxyDll));

        Ue4ssInstaller.SetActive(server, true);
        Assert.True(server.IsUe4ssActive);
        Assert.False(File.Exists(server.ParkedProxyDll));
    }

    [Fact]
    public void ModsAreSavedListedSwitchedAndRemoved()
    {
        using var temp = new LevelTempDirectory();
        var server = ServerInstall.Find(FakeServer(temp))!;
        Ue4ssInstaller.InstallFromZip(server, FakeUe4ssZip(temp));
        var store = new ServerModStore(server);

        store.Save("Announcer", "print('a')");
        store.Save("EventLog", "print('b')");
        Assert.Equal(["Announcer", "EventLog"], store.List().Select(m => m.Name));
        Assert.All(store.List(), m => Assert.True(m.Enabled));

        store.SetEnabled("Announcer", false);
        Assert.False(store.List().Single(m => m.Name == "Announcer").Enabled);
        var text = File.ReadAllLines(Path.Combine(server.ModsFolder, "mods.txt"));
        Assert.Contains("Announcer : 0", text);
        Assert.Equal("Keybinds : 1", text[^1]); // the built-in keybinds stay last

        store.Remove("Announcer");
        Assert.Equal(["EventLog"], store.List().Select(m => m.Name));
        Assert.DoesNotContain("Announcer", File.ReadAllText(Path.Combine(server.ModsFolder, "mods.json")), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => store.Save("../evil", "x"));
        Assert.Throws<ArgumentException>(() => store.Save("with space", "x"));
        Assert.False(ServerModStore.IsValidName("a/b"));
    }

    public static IEnumerable<object[]> Templates() => ServerModTemplates.All.Select(t => new object[] { t.Id });

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryTemplateIsValidLuaWithASettingsBlockAndASafeName(string id)
    {
        var template = ServerModTemplates.All.Single(t => t.Id == id);

        new Script().LoadString(template.Code, null, id);
        Assert.Contains("SETTINGS", template.Code, StringComparison.Ordinal);
        Assert.True(ServerModStore.IsValidName(template.Id));
    }
}
