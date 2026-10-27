using System.Globalization;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Games;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.App;

/// <summary>The first-run wizard (language, game, server, then everything by itself) and the Home checklist.</summary>
[Collection(LanguageCollection.Name)]
public sealed class WelcomeTests
{
    [Fact]
    public void TheSystemLanguagePicksTheMatchingInterfaceLanguage()
    {
        var codes = Loc.Languages.Select(l => l.Code).ToList();
        Assert.Equal("ru", WelcomeViewModel.SystemLanguage(new CultureInfo("ru-RU"), codes));
        Assert.Equal("de", WelcomeViewModel.SystemLanguage(new CultureInfo("de-AT"), codes));
        Assert.Equal("zh", WelcomeViewModel.SystemLanguage(new CultureInfo("zh-CN"), codes));
        Assert.Equal("sh", WelcomeViewModel.SystemLanguage(new CultureInfo("hr-HR"), codes));
        Assert.Equal("sh", WelcomeViewModel.SystemLanguage(new CultureInfo("sr-Latn-RS"), codes));
        Assert.Equal("en", WelcomeViewModel.SystemLanguage(new CultureInfo("ja-JP"), codes)); // a language the app does not have
        Assert.Equal("en", WelcomeViewModel.SystemLanguage(CultureInfo.InvariantCulture, codes));
    }

    private static string MakeGame(AppTestContext ctx)
    {
        var paks = ctx.Combine("Game", "SCUM", "Content", "Paks");
        Directory.CreateDirectory(paks);
        return paks;
    }

    private static string MakeServer(AppTestContext ctx, bool ue4ss)
    {
        var win64 = ctx.Combine("Srv", "SCUM", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        File.WriteAllText(Path.Combine(win64, "SCUMServer.exe"), "exe");
        var paks = ctx.Combine("Srv", "SCUM", "Content", "Paks");
        Directory.CreateDirectory(paks);
        if (ue4ss)
        {
            Directory.CreateDirectory(Path.Combine(win64, "ue4ss"));
            File.WriteAllText(Path.Combine(win64, "ue4ss", "UE4SS.dll"), "d");
            File.WriteAllText(Path.Combine(win64, "dwmapi.dll"), "p");
        }

        return paks;
    }

    [AvaloniaFact]
    public async Task TheWizardSavesTheLanguageAndTheFoldersAndDerivesTheModFolders()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = MakeGame(ctx);
        var server = MakeServer(ctx, ue4ss: true);
        var closed = new List<bool>();
        var vm = new WelcomeViewModel(ctx.Services, closed.Add);
        try
        {
            Assert.Equal(WelcomeStep.Language, vm.Step);
            Assert.False(vm.CanBack);

            // Picking a language switches the program and is saved at once.
            vm.SelectedLanguage = Loc.Languages.First(l => l.Code == "ru");
            Assert.Equal("ru", Loc.Instance.Language);
            Assert.Equal("ru", ctx.Services.Settings.Load().Ui.Language);
            Assert.Contains("Шаг 1", vm.StepText, StringComparison.Ordinal);

            await vm.NextCommand.ExecuteAsync(null); // the game step: nothing found in the empty Steam roots
            Assert.Equal(WelcomeStep.Game, vm.Step);
            Assert.False(vm.CanNext); // a game folder is needed
            vm.GamePath = game;
            Assert.True(vm.CanNext);

            await vm.NextCommand.ExecuteAsync(null);
            Assert.Equal(WelcomeStep.Server, vm.Step);
            vm.ServerPath = server;

            await vm.NextCommand.ExecuteAsync(null); // everything else by itself
            Assert.Equal(WelcomeStep.Setup, vm.Step);
            Assert.True(vm.IsDone);
            Assert.False(vm.IsBusy);
            Assert.All(vm.Rows, r => Assert.NotEqual(WelcomeCheckState.Fail, r.State));
            Assert.Contains(vm.Rows, r => r.State == WelcomeCheckState.Warn); // the key is not on the (fake) list

            var settings = ctx.Services.Settings.Load();
            Assert.Equal(game, settings.GamePaksFolder);
            Assert.Equal(server, settings.ServerPaksFolder);
            Assert.Equal(Path.Combine(game, "~mods"), settings.ClientModsOutputFolder);
            Assert.Equal(Path.Combine(server, "~mods"), settings.ServerModsOutputFolder);
            Assert.True(ctx.Services.UiState.Current.SetupCompleted);

            vm.FinishCommand.Execute(null);
            Assert.Equal([true], closed);
        }
        finally
        {
            Loc.Instance.Language = "en";
        }
    }

    [AvaloniaFact]
    public async Task AFoundGameAndAFoundKeyNeedNothingElseAndAServerIsOptional()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = MakeGame(ctx);
        var vm = new WelcomeViewModel(ctx.Services, _ => { });
        vm.GamePath = game;
        vm.NoServer = true;
        await vm.NextCommand.ExecuteAsync(null); // language to game
        await vm.NextCommand.ExecuteAsync(null); // game to server
        await vm.NextCommand.ExecuteAsync(null); // server to setup
        Assert.True(vm.IsDone);
        Assert.Null(ctx.Services.Settings.Load().ServerPaksFolder);
        Assert.Contains(vm.Rows, r => r.Text == Loc.T("Welcome.Row.NoServer"));
    }

    [AvaloniaFact]
    public void TheWizardRefusesTheServersFolderAsTheGameAndSkippingKeepsNothing()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var closed = new List<bool>();
        var vm = new WelcomeViewModel(ctx.Services, closed.Add);
        vm.Step = WelcomeStep.Game;
        var serverPaks = MakeServer(ctx, ue4ss: false);
        File.WriteAllText(Path.Combine(serverPaks, "pakchunk0-WindowsServer.pak"), "x");
        vm.GamePath = serverPaks; // a dedicated server's Paks folder is not the game's
        Assert.NotNull(vm.GameProblem);
        Assert.False(vm.CanNext);

        vm.SkipCommand.Execute(null);
        Assert.Equal([false], closed);
        Assert.Null(ctx.Services.Settings.Load().GamePaksFolder);
        Assert.True(ctx.Services.UiState.Current.SetupCompleted);
    }

    [AvaloniaFact]
    public void HomeListsWhatIsLeftAndFixesItFromTheServerPage()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var opened = new List<string>();
        var setups = 0;
        var home = new HomePageViewModel(ctx.Services, opened.Add, () => setups++);
        Assert.Contains(home.Rows, r => r.Title == Loc.T("Home.Game") && r.State == WelcomeCheckState.Warn && r.HasAction);
        Assert.Contains("item(s) left", home.Summary, StringComparison.Ordinal);
        home.Rows.First(r => r.Title == Loc.T("Home.Game")).RunCommand.Execute(null);
        Assert.Equal(1, setups);

        var server = MakeServer(ctx, ue4ss: false);
        ctx.Services.UpdateSettings(s => s with { ServerPaksFolder = server });
        home.RefreshCommand.Execute(null);
        var ue4ss = home.Rows.First(r => r.Title == Loc.T("Home.Ue4ss"));
        Assert.Equal(WelcomeCheckState.Warn, ue4ss.State);
        ue4ss.RunCommand.Execute(null);
        Assert.Equal(["server"], opened);

        // With a dump and the exported loot presets those rows turn to done.
        var install = ServerInstall.Find(server)!;
        Directory.CreateDirectory(install.Ue4ssFolder);
        File.WriteAllText(Path.Combine(install.Ue4ssFolder, "scum_dump.txt"), "Class /Script/SCUM.Item");
        var store = new LootOverrideStore(install);
        Directory.CreateDirectory(store.DefaultFolder);
        File.WriteAllText(Path.Combine(store.DefaultFolder, "A-B.json"), "{}");
        home.RefreshCommand.Execute(null);
        Assert.Equal(WelcomeCheckState.Ok, home.Rows.First(r => r.Title == Loc.T("Home.Dump")).State);
        Assert.Equal(WelcomeCheckState.Ok, home.Rows.First(r => r.Title == Loc.T("Home.Loot")).State);
    }

    [AvaloniaFact]
    public void TheWizardCanBeStartedAgainFromHomeAndKeepsTheLanguageInUse()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.UiState.Update(u => u with { SetupCompleted = true });
        ctx.Services.UpdateSettings(s => s with { Ui = s.Ui with { Language = "de" } });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1500, 900);
        try
        {
            vm.NavigateTo("home");
            HeadlessUi.Pump();
            var button = HeadlessUi.FindNamed<Avalonia.Controls.Button>(window, "WizardButton")!;
            Assert.True(button.IsEffectivelyVisible);
            button.Command!.Execute(null);
            HeadlessUi.Pump();

            Assert.True(vm.IsWelcomeOpen);
            Assert.Equal("de", vm.Welcome!.SelectedLanguage.Code); // run again: the language already in use, not the system's
            Assert.Equal(WelcomeStep.Language, vm.Welcome.Step);
            vm.Welcome.SkipCommand.Execute(null);
            Assert.False(vm.IsWelcomeOpen);
        }
        finally
        {
            Loc.Instance.Language = "en";
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheWizardAndHomeShowInTheWindow()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1500, 900);
        try
        {
            vm.OpenWelcome();
            HeadlessUi.Pump();
            Assert.True(HeadlessUi.FindNamed<Avalonia.Controls.Panel>(window, "WelcomeOverlay")!.IsVisible);
            HeadlessUi.SaveScreenshot(window, "welcome-language");
            vm.Welcome!.Step = WelcomeStep.Game;
            vm.Welcome.GamePath = ctx.Combine("Game", "SCUM", "Content", "Paks");
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "welcome-game");
            vm.Welcome.SkipCommand.Execute(null);

            vm.NavigateTo("home");
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "page-home");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
