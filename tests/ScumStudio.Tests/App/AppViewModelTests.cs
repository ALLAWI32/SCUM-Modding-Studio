using System.Security.Cryptography;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Security;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Level.World;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>View-model tests of the desktop shell (no UI; inline dispatcher).</summary>
public sealed class AppViewModelTests
{
    [Fact]
    public void BuildingSetsGatherWallsRoadsAndBridgesFromTheCatalogue()
    {
        int Count(string key) => AssetDumper.Packages.Count(p => AssetsPageViewModel.IsObjectClass(p.ClassName)
            && AssetsPageViewModel.BuildingSets.Single(s => s.Key == key).Match(p));
        bool In(string key, string name) => AssetsPageViewModel.BuildingSets.Single(s => s.Key == key).Match(new DumpPackage("/Game/X/" + name, "exterior.fences", "StaticMesh"));

        Assert.True(Count("Assets.Set.Walls") > 500);
        Assert.True(Count("Assets.Set.Roads") > 100);
        Assert.True(Count("Assets.Set.Bridges") > 40);
        Assert.True(In("Assets.Set.Walls", "SM_Stone_Wall_02_Long_c"));
        Assert.False(In("Assets.Set.Walls", "SM_WallPaperRoll_01a"));
        Assert.True(In("Assets.Set.Roads", "SM_Asphalt_Road_Curb_End_Left_01"));
        Assert.False(In("Assets.Set.Roads", "SM_Z_1_1b_RailRoadTunnel_ES_01"));
        Assert.True(In("Assets.Set.Bridges", "SM_RiverBridge_02"));
        Assert.False(In("Assets.Set.Bridges", "A_0_Dr_Tudman_Bridge_Distant_LOD00"));
    }

    /// <summary>A random 256-bit test key generated at runtime (never a real game key).</summary>
    private static string RandomKeyHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(AesKeyHex.KeyBytes));

    /// <summary>Malformed key inputs by case name (values are generated at run time so test ids stay stable).</summary>
    private static string MalformedKey(string kind)
    {
        var hex = RandomKeyHex();
        return kind switch
        {
            "text" => "not a key",
            "short" => hex[..63],
            "long" => hex + "0",
            "nonhex" => "0x" + hex[..62] + "ZZ",
            "128bit" => hex[..32],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    [Theory]
    [InlineData("text")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("nonhex")]
    [InlineData("128bit")]
    public void SetupRejectsAMalformedKeyAndNeverStoresIt(string kind)
    {
        var input = MalformedKey(kind);
        using var ctx = AppTestContext.Create();
        var closed = new List<bool>();
        var setup = new SetupViewModel(ctx.Services, closed.Add) { GamePaksFolder = ctx.Combine("Paks") };

        setup.KeyInput = input;
        Assert.False(setup.Save());
        setup.SaveAndCloseCommand.Execute(null);

        Assert.Empty(closed); // dialog stays open
        Assert.Equal(AesKeyHex.InvalidKeyMessage, setup.KeyError);
        Assert.True(setup.HasKeyError);
        Assert.False(ctx.Services.Keys.HasKey);
        Assert.False(ctx.Services.Keys.TryGet(out _));
        Assert.Null(ctx.Services.Settings.Load().GamePaksFolder); // nothing was saved
        AssertTextNowhere(ctx, input);

        // Typing again clears the error.
        setup.KeyInput = "a";
        Assert.Null(setup.KeyError);
    }

    [Fact]
    public void SetupStoresAValidKeyProtectedAndClearsTheInput()
    {
        using var ctx = AppTestContext.Create();
        var key = RandomKeyHex();
        var keyEvents = 0;
        ctx.Services.KeyChanged += (_, _) => keyEvents++;
        var closed = new List<bool>();
        var setup = new SetupViewModel(ctx.Services, closed.Add) { GamePaksFolder = ctx.Combine("Paks"), KeyInput = "0x" + key.ToLowerInvariant() };

        setup.SaveAndCloseCommand.Execute(null);

        Assert.Equal([true], closed);
        Assert.Equal(string.Empty, setup.KeyInput);
        Assert.False(setup.HasKeyInput);
        Assert.Null(setup.KeyError);
        Assert.True(setup.HasStoredKey);
        Assert.Equal(1, keyEvents);
        Assert.True(ctx.Services.Keys.TryGet(out var stored));
        Assert.Equal(AesKeyHex.Normalize(key), stored);
        Assert.Equal(ctx.Combine("Paks"), ctx.Services.Settings.Load().GamePaksFolder);
        Assert.True(ctx.Services.UiState.Current.SetupCompleted);
        AssertTextNowhere(ctx, key);
    }

    [Fact]
    public void SetupFindsTheKeyOnlineAndStoresItWithoutShowingIt()
    {
        // Owner: "the tool goes to the site, searches SCUM and copies the key next to it; if not, it asks the person".
        // The list as the site writes it (made-up keys): the game's name, then its key.
        var key = RandomKeyHex();
        var page = $"<li>597. Scribble It 0x{RandomKeyHex()}</li><li>598. <mark>SCUM</mark> &nbsp; 0x{key}</li><li>599. SD Gundam 0x{RandomKeyHex()}</li>";
        Assert.Equal(AesKeyHex.Normalize(key), OnlineKeyFinder.Parse(page));
        Assert.Null(OnlineKeyFinder.Parse($"<li>SCUMM Engine 0x{RandomKeyHex()}</li><li>SCUM (demo)</li>")); // other names, no key

        // No key yet: the setup looks it up at once and stores it; the status says so, the key itself shows nowhere.
        using (var ctx = AppTestContext.Create(keyFinder: _ => Task.FromResult(OnlineKeyFinder.Parse(page))))
        {
            var setup = new SetupViewModel(ctx.Services, _ => { });
            Assert.False(setup.IsFindingKey);
            Assert.True(setup.HasStoredKey);
            Assert.True(ctx.Services.Keys.TryGet(out var stored));
            Assert.Equal(AesKeyHex.Normalize(key), stored);
            Assert.Equal("Found online, tested on your game files and stored.", setup.KeyOnlineStatus);
            AssertTextNowhere(ctx, key);
        }

        // Not on the list, or no internet: nothing is stored and the person is asked to paste it.
        using (var ctx = AppTestContext.Create())
        {
            var setup = new SetupViewModel(ctx.Services, _ => { });
            Assert.False(setup.HasStoredKey);
            Assert.Contains("Paste it", setup.KeyOnlineStatus, StringComparison.Ordinal);
        }

        using (var ctx = AppTestContext.Create(keyFinder: _ => throw new HttpRequestException("offline")))
        {
            var setup = new SetupViewModel(ctx.Services, _ => { });
            Assert.False(setup.HasStoredKey);
            Assert.Contains("could not be reached", setup.KeyOnlineStatus, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SetupKeepsTheStoredKeyWhenTheBoxIsEmptyAndForgetsItOnRequest()
    {
        using var ctx = AppTestContext.Create();
        Assert.True(ctx.Services.TryStoreKey(RandomKeyHex()));
        var setup = new SetupViewModel(ctx.Services, _ => { });
        Assert.True(setup.HasStoredKey);

        Assert.True(setup.Save());
        Assert.True(ctx.Services.Keys.HasKey);

        setup.ForgetKeyCommand.Execute(null);
        Assert.False(ctx.Services.Keys.HasKey);
        Assert.False(setup.HasStoredKey);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "AES key removed");
    }

    [Fact]
    public async Task SetupConnectionTestValidatesItsInputs()
    {
        using var ctx = AppTestContext.Create();
        var setup = new SetupViewModel(ctx.Services, _ => { });

        await setup.TestConnectionCommand.ExecuteAsync(null);
        Assert.False(setup.TestSucceeded);
        Assert.True(setup.IsTestFailed);
        Assert.Contains("Paks folder", setup.TestResult, StringComparison.Ordinal);

        setup.GamePaksFolder = ctx.Combine("Paks");
        setup.KeyInput = "123";
        await setup.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(AesKeyHex.InvalidKeyMessage, setup.KeyError);
        Assert.Contains("not valid", setup.TestResult, StringComparison.Ordinal);
        Assert.False(ctx.Services.Keys.HasKey);

        // An empty folder mounts nothing: reported, not thrown.
        Directory.CreateDirectory(ctx.Combine("Paks"));
        setup.KeyInput = string.Empty;
        await setup.TestConnectionCommand.ExecuteAsync(null);
        Assert.False(setup.IsWorking);
        Assert.False(setup.TestSucceeded);
        Assert.NotNull(setup.TestResult);
    }

    [Fact]
    public async Task SetupAutoDetectReportsWhenNothingIsInstalled()
    {
        using var ctx = AppTestContext.Create();
        var setup = new SetupViewModel(ctx.Services, _ => { });
        await setup.AutoDetectCommand.ExecuteAsync(null);
        Assert.Contains("No SCUM install found", setup.DetectStatus, StringComparison.Ordinal);
        Assert.False(setup.IsWorking);
    }

    [Fact]
    public void MainWindowOffersEveryModuleAndReflectsConfiguration()
    {
        using var ctx = AppTestContext.Create();
        using var vm = new MainWindowViewModel(ctx.Services);

        Assert.Equal(["Map", "Vehicles", "Weapons", "Assets", "Projects", "Settings"], vm.NavItems.Select(n => n.Title));
        Assert.Equal(MainWindowViewModel.PageKeys, vm.NavItems.Select(n => n.Key));
        Assert.Equal("map", vm.CurrentPage?.Key);
        Assert.Equal(PillState.Off, vm.GamePill.State);
        Assert.Equal("not set", vm.GamePill.Value);
        Assert.Equal(PillState.Off, vm.KeyPill.State);
        Assert.Equal(PillState.Off, vm.ServerPill.State);

        Assert.True(ctx.Services.TryStoreKey(RandomKeyHex()));
        Assert.Equal(PillState.Ok, vm.KeyPill.State);
        Assert.Equal("set", vm.KeyPill.Value);

        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = ctx.Combine("Paks"), ServerPaksFolder = ctx.Combine("ServerPaks") });
        Assert.Equal("not connected", vm.GamePill.Value);
        Assert.Equal(PillState.Warn, vm.ServerPill.State);

        foreach (var key in MainWindowViewModel.PageKeys)
        {
            var page = vm.NavigateTo(key);
            Assert.NotNull(page);
            Assert.Same(page, vm.CurrentPage);
            Assert.Equal(key, page!.Key);
        }

        Assert.Null(vm.NavigateTo("nope"));
        Assert.Equal("settings", ctx.Services.UiState.Current.LastPage);
        var vehicles = Assert.IsType<VehiclesPageViewModel>(vm.NavigateTo("vehicles"));
        Assert.True(vehicles.ShowEmptyState); // no game files connected
        Assert.True(vehicles.IsVehicleModule);
        var weapons = Assert.IsType<WeaponsPageViewModel>(vm.NavigateTo("weapons"));
        Assert.False(weapons.IsVehicleModule);
    }

    [Fact]
    public async Task FirstRunOpensSetupAndWorksWithoutAGameFolder()
    {
        using var ctx = AppTestContext.Create();
        using var vm = new MainWindowViewModel(ctx.Services);

        await vm.InitializeAsync();
        Assert.True(vm.IsSetupOpen);
        Assert.NotNull(vm.Setup);

        vm.Setup!.CancelCommand.Execute(null);
        Assert.False(vm.IsSetupOpen);
        Assert.Null(vm.Setup);
        Assert.True(ctx.Services.UiState.Current.SetupCompleted);

        // Every page has a usable empty state without a game folder.
        var assets = (AssetsPageViewModel)vm.NavigateTo("assets")!;
        Assert.True(assets.ShowEmptyState);
        var map = (MapPageViewModel)vm.NavigateTo("map")!;
        Assert.True(map.ShowEmptyState);
        Assert.True(map.EmptyStateNeedsSetup);
        map.OpenSetupCommand.Execute(null);
        Assert.True(vm.IsSetupOpen);

        // Second start: setup is not forced again.
        using var again = new MainWindowViewModel(ctx.Services);
        await again.InitializeAsync();
        Assert.False(again.IsSetupOpen);
    }

    [Fact]
    public async Task FailingOperationsBecomeToastsInsteadOfCrashes()
    {
        using var ctx = AppTestContext.Create();
        var ops = ctx.Services.Operations;

        var ok = await ops.RunAsync("Exploding", (_, _) => throw new InvalidDataException("boom"));

        Assert.False(ok);
        Assert.False(ops.IsBusy);
        Assert.Equal("Exploding - failed", ops.StatusText);
        var toast = Assert.Single(ctx.Services.Notifications.Toasts);
        Assert.True(toast.IsError);
        Assert.Equal("boom", toast.Message);
        Assert.Contains(ctx.Services.Log.Entries, e => e.IsError && e.Message.Contains("boom", StringComparison.Ordinal));

        toast.CloseCommand!.Execute(null);
        Assert.Empty(ctx.Services.Notifications.Toasts);

        var (done, value) = await ops.RunAsync("Answer", (p, _) =>
        {
            p.Report("Counting", 1, 2);
            return Task.FromResult(42);
        });
        Assert.True(done);
        Assert.Equal(42, value);
        Assert.Equal("Answer - done", ops.StatusText);
    }

    [Fact]
    public async Task ToastsAreCappedAndCanAutoDismiss()
    {
        var notifications = new NotificationService(new InlineUiDispatcher(), TimeSpan.FromMilliseconds(20));
        for (var i = 0; i < NotificationService.MaxVisible + 3; i++)
        {
            notifications.Info("t" + i);
        }

        Assert.Equal(NotificationService.MaxVisible, notifications.Toasts.Count);
        Assert.Equal("t" + (NotificationService.MaxVisible + 2), notifications.Toasts[^1].Title);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (notifications.Toasts.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Empty(notifications.Toasts);
    }

    [Fact]
    public void ToastLifetimesFollowTheSeverityAndBurstsCoalesce()
    {
        var notifications = new NotificationService(new InlineUiDispatcher(), TimeSpan.FromSeconds(6));
        Assert.Equal(TimeSpan.FromSeconds(3), notifications.LifetimeOf(ToastSeverity.Success));
        Assert.Equal(TimeSpan.FromSeconds(6), notifications.LifetimeOf(ToastSeverity.Info));
        Assert.Equal(TimeSpan.FromSeconds(12), notifications.LifetimeOf(ToastSeverity.Error));
        Assert.Null(new NotificationService(new InlineUiDispatcher(), null).LifetimeOf(ToastSeverity.Success));

        // A burst of AI edits is one toast that counts the others, not a stack covering the window.
        notifications.Error("Could not open the paks");
        notifications.ShowCoalesced("mcp", ToastSeverity.Info, "AI: Set item values", "Weapon_RPG7: 1 value");
        notifications.ShowCoalesced("mcp", ToastSeverity.Info, "AI: Move actor", "SM_Fence_26");
        notifications.ShowCoalesced("mcp", ToastSeverity.Info, "AI: Move actor", "SM_Fence_27");
        Assert.Equal(2, notifications.Toasts.Count);
        Assert.Equal("Could not open the paks", notifications.Toasts[0].Title);
        Assert.Equal("AI: Move actor", notifications.Toasts[1].Title);
        Assert.Equal("SM_Fence_27 · +2 more", notifications.Toasts[1].Message);
    }

    [Fact]
    public void TheOldDefaultAccentIsMigratedToBlaze()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scumstudio-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, UiStateStore.FileName), """{ "accent": "#4c8dff", "setupCompleted": true }""");
            var store = new UiStateStore(directory, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Assert.Equal(AccentPalette.DefaultHex, store.Current.Accent);
            Assert.True(store.Current.SetupCompleted);

            File.WriteAllText(Path.Combine(directory, UiStateStore.FileName), """{ "accent": "#4FB3A2" }""");
            Assert.Equal("#4FB3A2", new UiStateStore(directory, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).Current.Accent);
            Assert.Equal("Blaze", AccentPalette.Options[0].Name);
            Assert.Equal(AccentPalette.DefaultHex, AccentPalette.Options[0].Hex);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MapHistoryIsBoundToTheProjectJournal()
    {
        using var ctx = AppTestContext.Create();
        using var map = new MapPageViewModel(ctx.Services);
        Assert.False(map.Session.HasProject);
        Assert.False(map.UndoCommand.CanExecute(null));

        map.NewProjectName = "Outpost cleanup";
        Assert.True(await map.CreateProjectAsync(ctx.Combine("projects")));
        Assert.True(map.Session.HasProject);
        Assert.Equal("Outpost cleanup", map.Session.DisplayName);
        Assert.Equal("No edits yet", map.Session.HistorySummary);
        var projectDir = map.Session.DirectoryPath!;
        Assert.EndsWith(Project.FolderExtension, projectDir, StringComparison.Ordinal);

        var crate = new ActorRef(EditOpTests.Outpost, "Crate_1");
        var rock = new ActorRef(EditOpTests.Port, "Rock_7");
        map.Session.Apply(new DeleteActorOp(crate));
        map.Session.Apply(new DeleteActorOp(rock));

        Assert.Equal(2, map.Session.History.Count);
        Assert.Equal("Delete A_0_Small_Port/Rock_7", map.Session.History[0].Summary); // newest first
        Assert.True(map.UndoCommand.CanExecute(null));
        Assert.False(map.RedoCommand.CanExecute(null));
        Assert.Equal(2, map.Session.PendingLevelCount);

        map.UndoCommand.Execute(null);
        Assert.True(map.Session.History[0].IsUndone);
        Assert.True(map.RedoCommand.CanExecute(null));
        Assert.Equal(1, map.Session.PendingLevelCount);
        Assert.Contains("1 undone", map.Session.HistorySummary, StringComparison.Ordinal);

        map.RedoCommand.Execute(null);
        Assert.True(map.Session.History[0].IsApplied);
        Assert.False(map.RedoCommand.CanExecute(null));

        map.UndoCommand.Execute(null);
        map.CloseProjectCommand.Execute(null);
        Assert.False(map.Session.HasProject);
        Assert.Empty(map.Session.History);
        Assert.False(map.UndoCommand.CanExecute(null));

        // The journal (including the undo) survived: reopen through the page.
        Assert.True(await map.OpenProjectAsync(projectDir));
        Assert.Equal(2, map.Session.History.Count);
        Assert.True(map.Session.History[0].IsUndone);
        Assert.True(map.RedoCommand.CanExecute(null));
        Assert.Contains(projectDir, ctx.Services.Settings.Load().RecentProjects);

        // Undo/redo from the top bar go to the same journal.
        using var shell = new MainWindowViewModel(ctx.Services);
        shell.RedoCommand.Execute(null);
        Assert.True(map.Session.History[0].IsApplied);
    }

    [Fact]
    public void MapWorldTreeGroupsCellsAndCategories()
    {
        using var ctx = AppTestContext.Create();
        using var map = new MapPageViewModel(ctx.Services);
        var index = WorldIndex.Build(WorldIndexTests.SampleFiles);

        map.LoadWorld(index);

        Assert.True(map.HasWorld);
        Assert.False(map.ShowEmptyState);
        Assert.Equal("The_Island", map.Nodes[0].Title);
        var cellA0 = Assert.Single(map.Nodes, n => n.Title == "Cell A_0");
        Assert.Contains(cellA0.Children, c => c.Title == "POI / grid");
        Assert.Contains(cellA0.Children, c => c.Title == "Landscape");
        Assert.Contains(map.KindChips, c => c.Label == "Landscape" && c.Count > 0);
        Assert.Contains("sublevels", map.Subtitle, StringComparison.Ordinal);

        var outpost = cellA0.Children.SelectMany(c => c.Children).First(n => n.Title == "A_0_Outpost");
        map.SelectedNode = outpost;
        Assert.True(map.HasSelectedPackage);
        Assert.Equal("A_0_Outpost", map.ViewportCaption);
        Assert.Contains(map.SelectedProperties, r => r.Name == "Cell" && r.Value == "A_0");

        map.ApplySearch("Castle");
        Assert.All(Leaves(map.Nodes), n => Assert.Contains("Castle", n.Title, StringComparison.OrdinalIgnoreCase));
        Assert.All(map.Nodes.Where(n => n.Children.Count > 0), n => Assert.True(n.IsExpanded));

        map.ApplySearch("no-such-level");
        Assert.True(map.ShowEmptyState);
        Assert.Equal("Nothing matches", map.EmptyTitle);

        static IEnumerable<WorldTreeNode> Leaves(IEnumerable<WorldTreeNode> nodes) =>
            nodes.SelectMany(n => n.Children.Count == 0 ? [n] : Leaves(n.Children));
    }

    [Fact]
    public void AssetFilterMatchesWordsAndClasses()
    {
        PackageEntry[] entries =
        [
            new("SCUM/Content/ConZ_Files/Vehicles/WolfsWagen/T_WW_Body_D.uasset", "/Game/ConZ_Files/Vehicles/WolfsWagen/T_WW_Body_D", "Texture2D"),
            new("SCUM/Content/ConZ_Files/Vehicles/WolfsWagen/SM_WW_Body.uasset", "/Game/ConZ_Files/Vehicles/WolfsWagen/SM_WW_Body", "StaticMesh"),
            new("SCUM/Content/ConZ_Files/Vehicles/WolfsWagen/SK_WW.uasset", "/Game/ConZ_Files/Vehicles/WolfsWagen/SK_WW", "SkeletalMesh"),
            new("SCUM/Content/ConZ_Files/Items/Weapons/T_AK47_D.uasset", "/Game/ConZ_Files/Items/Weapons/T_AK47_D", null),
        ];

        Assert.Equal(3, AssetsPageViewModel.Filter(entries, "wolfswagen", 10).Total);
        Assert.Equal("SM_WW_Body", Assert.Single(AssetsPageViewModel.Filter(entries, "wolfswagen class:StaticMesh", 10).Matches).Name);
        Assert.Equal("SK_WW", Assert.Single(AssetsPageViewModel.Filter(entries, "class:USkeletalMesh", 10).Matches).Name);
        Assert.Equal(1, AssetsPageViewModel.Filter(entries, "texture", 10).Total); // matched by class name
        var capped = AssetsPageViewModel.Filter(entries, "ConZ_Files", 2);
        Assert.Equal(4, capped.Total);
        Assert.Equal(2, capped.Matches.Count);
        Assert.Empty(AssetsPageViewModel.Filter(entries, "wolfswagen weapons", 10).Matches);

        Assert.Equal(("Icon.Texture", "#9DB35E"), AssetIcons.ForClass("Texture2D"));
        Assert.Equal("Icon.World", AssetIcons.ForClass(null, isMap: true).IconKey);
        Assert.Equal("1.5 KB", AssetDetailsViewModel.FormatBytes(1536).Replace(',', '.'));
    }

    [Fact]
    public void AssetFilterRanksNamesStartingWithTheQueryFirstAndCutsTileRows()
    {
        PackageEntry[] entries =
        [
            new("SCUM/Content/Barrels/SM_Crate.uasset", "/Game/Barrels/SM_Crate", "StaticMesh"),
            new("SCUM/Content/Props/SM_Barrel.uasset", "/Game/Props/SM_Barrel", "StaticMesh"),
            new("SCUM/Content/Props/Barrel_Lid.uasset", "/Game/Props/Barrel_Lid", "StaticMesh"),
        ];

        // Starts-with, then contains in the name, then a folder match; one letter is enough and the cap keeps the best.
        Assert.Equal(["Barrel_Lid", "SM_Barrel", "SM_Crate"], AssetsPageViewModel.Filter(entries, "barrel", 10).Matches.Select(e => e.Name));
        var capped = AssetsPageViewModel.Filter(entries, "b", 2);
        Assert.Equal(3, capped.Total);
        Assert.Equal(["Barrel_Lid", "SM_Barrel"], capped.Matches.Select(e => e.Name));

        var rows = AssetsPageViewModel.ChunkRows(entries.Select(e => new AssetItemViewModel(e)).ToList(), 2);
        Assert.Equal([2, 1], rows.Select(r => r.Tiles.Count));
        Assert.Equal(4, AssetsPageViewModel.ColumnsFor(600));
        Assert.Equal(1, AssetsPageViewModel.ColumnsFor(0));
    }

    [Fact]
    public void SettingsPagePicksAccentsAndForgetsTheKey()
    {
        using var ctx = AppTestContext.Create();
        using var settings = new SettingsPageViewModel(ctx.Services);
        Assert.Equal("Blaze", settings.AccentName);
        Assert.Single(settings.Accents, a => a.IsSelected);

        settings.Accents.First(a => a.Name == "Teal").SelectCommand.Execute(null);
        Assert.Equal("Teal", settings.AccentName);
        Assert.Equal("#4FB3A2", ctx.Services.UiState.Current.Accent);
        Assert.Single(settings.Accents, a => a.IsSelected);
        settings.Accents.First(a => a.Name == "Blaze").SelectCommand.Execute(null); // restore the shared app's accent
        Assert.Equal(AccentPalette.DefaultHex, ctx.Services.UiState.Current.Accent);

        Assert.True(ctx.Services.TryStoreKey(RandomKeyHex()));
        Assert.True(settings.HasKey);
        settings.ForgetKeyCommand.Execute(null);
        Assert.False(settings.HasKey);
        Assert.False(ctx.Services.Keys.HasKey);

        settings.GamePaksFolder = "  \"" + ctx.Combine("Paks") + "\"  ";
        settings.SavePathsCommand.Execute(null);
        Assert.Equal(ctx.Combine("Paks"), ctx.Services.Settings.Load().GamePaksFolder);
    }

    /// <summary>Asserts <paramref name="text"/> appears in no log line and no file of the data folder.</summary>
    private static void AssertTextNowhere(AppTestContext ctx, string text)
    {
        var needle = text.Trim();
        if (needle.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            needle = needle[2..];
        }

        Assert.DoesNotContain(ctx.Services.Log.Entries, e => e.Message.Contains(needle, StringComparison.OrdinalIgnoreCase));
        if (!Directory.Exists(ctx.Services.DataDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(ctx.Services.DataDirectory, "*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain(needle, content, StringComparison.OrdinalIgnoreCase);
        }
    }
}
