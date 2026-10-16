using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Economy;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Economy page over the project's store, with small game tables of its own (no game files): a placed trader is listed at
/// once, every edit is one journal entry that Ctrl+Z undoes, the Code view and the numbers follow each other, a file is
/// imported with a summary, and nothing is editable without a project.
/// </summary>
public sealed class EconomyPageViewModelTests
{
    private const string Armory = "A_0_Armory";
    private const string Saloon = "A_0_Saloon";

    private static EconomyDefaults Tables(int akPrice = 11200) => new(
        [
            new TradeableDefault("Weapon_AK47", "AK-47", "RangedWeapon", ["Armorer"], akPrice, 3700, true, 100, false),
            new TradeableDefault("Weapon_M9", "M9", "RangedWeapon", ["Armorer"], 1000, 300, true, 0, false),
            new TradeableDefault("Ammo_9mm", "9mm", "Ammo", ["Armorer"], 50, 10, true, 0, false),
            new TradeableDefault("Knife", "Knife", "MeleeWeapon", ["Armorer"], 200, 50, false, 0, false),
            new TradeableDefault("Grenade", "Grenade", "Explosive", ["Armorer"], 900, 200, true, 50, true),
            new TradeableDefault("Beer_Can", "Beer", "Drinks", ["Bartender"], 30, 10, true, 5, false),
            new TradeableDefault("Gold_Bar", "Gold", "Valuables", ["Armorer", "Bartender"], 5000, 4000, true, 0, false) { ImmuneToOverrides = true },
        ],
        [new TraderInfo(Armory, "Armorer", "/Game/X/Armory"), new TraderInfo(Saloon, "Bartender", "/Game/X/Saloon")]);

    private static async Task<EconomyPageViewModel> OpenAsync(AppTestContext ctx, bool project = true)
    {
        if (project)
        {
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        }

        ctx.Services.Economy.UseDefaults(Tables());
        return new EconomyPageViewModel(ctx.Services);
    }

    private static int Applied(AppTestContext ctx) => ctx.Services.Projects.Current!.Journal.Applied.Count;

    private static EconomyItemViewModel Row(EconomyPageViewModel page, string trader, string code)
    {
        page.SelectedTrader = page.Traders.Single(t => t.Name == trader);
        return page.Items.Single(i => i.Code == code);
    }

    /// <summary>A file in the game's own layout: the game's settings with one changed, and the given entries.</summary>
    private static string GameFile(string? settingKey, string? settingValue, params (string Trader, TradeableOverride Entry)[] entries)
    {
        var file = EconomyOverride.CreateDefault();
        if (settingKey is not null)
        {
            foreach (var op in file.SettingOps(settingKey, "\"" + settingValue + "\""))
            {
                file.Apply(op);
            }
        }

        foreach (var (trader, entry) in entries)
        {
            file.Set(trader, entry, keep: true);
        }

        return file.ToJson();
    }

    [AvaloniaFact]
    public async Task APlacedTraderAppearsAtOnce()
    {
        // #4: a regression guard; the path (journal → store sync → page) already worked at 6967844.
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);

        ctx.Services.Projects.Apply(ProjectEconomyTests.PlaceTrader("A_3_Armory_1"));

        Assert.True(Assert.Single(page.Traders, t => t.Name == "A_3_Armory_1").IsPlaced);
        Assert.Equal(["Weapon_AK47", "Weapon_M9", "Ammo_9mm", "Knife", "Grenade"], ctx.Services.Economy.Economy.Entries("A_3_Armory_1").Select(e => e.Code));
    }

    [AvaloniaFact]
    public async Task AnEditIsOneJournalEntryAndCtrlZShowsTheOldValue()
    {
        // Audit F113/F298: the page wrote the file without a journal entry, so Ctrl+Z undid the last Map edit instead.
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);
        var store = ctx.Services.Economy;
        var before = Applied(ctx);

        Row(page, Armory, "Weapon_AK47").PurchasePrice = 12000;

        Assert.Equal(before + 1, Applied(ctx));
        Assert.True(ctx.Services.Projects.CanUndo);
        Assert.Equal(12000, store.Economy.Find(Armory, "Weapon_AK47")?.PurchasePrice);

        ctx.Services.Projects.Undo();
        Assert.Equal(11200, page.Items.Single(i => i.Code == "Weapon_AK47").PurchasePrice);
        Assert.Null(store.Economy.Find(Armory, "Weapon_AK47"));
        Assert.Null(EconomyOverride.LoadFrom(ctx.Services.Projects.DirectoryPath!)?.Find(Armory, "Weapon_AK47"));

        ctx.Services.Projects.Redo();
        Assert.Equal(12000, page.Items.Single(i => i.Code == "Weapon_AK47").PurchasePrice);
    }

    [AvaloniaFact]
    public async Task BulkActionsLeaveUnknownAndUntouchedRowsAlone()
    {
        // Audit F104/F110/F301: an imported code the tables do not know was written out as 0/0/0 by the next bulk action.
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);
        var store = ctx.Services.Economy;
        page.ImportText(GameFile(null, null, (Armory, new TradeableOverride("Mod_Item"))), "old.json");
        var unknown = Row(page, Armory, "Mod_Item");
        Assert.True(unknown.IsUnknown);
        var before = Applied(ctx);

        page.ScalePrices("1.1");
        page.SetOnSale("false");

        Assert.Equal(before + 2, Applied(ctx)); // one entry per action
        Assert.True(store.Economy.Find(Armory, "Mod_Item")!.IsDefault);
        Assert.Equal("-1", store.Economy.RawValue(Armory, "Mod_Item", "base-purchase-price"));
        Assert.Equal(12320, store.Economy.Find(Armory, "Weapon_AK47")!.PurchasePrice);
        Assert.False(store.Economy.Find(Armory, "Weapon_AK47")!.CanBePurchased);
        Assert.Null(store.Economy.Find(Armory, "Gold_Bar")); // locked: left alone
    }

    [AvaloniaFact]
    public async Task TheCodeFollowsTheNumbers()
    {
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);

        Row(page, Armory, "Weapon_M9").SellPrice = 456;

        Assert.Contains("\"456\"", page.CodeText, StringComparison.Ordinal);
        Assert.Equal(ctx.Services.Economy.Text, page.CodeText);
        Assert.True(page.IsCodeValid);
    }

    [AvaloniaFact]
    public async Task ValidCodeUpdatesTheNumbersInOneEntry()
    {
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);
        var m9 = Row(page, Armory, "Weapon_M9");
        m9.SellPrice = 456;
        var before = Applied(ctx);

        page.CodeText = page.CodeText.Replace("\"456\"", "\"789\"", StringComparison.Ordinal);
        Assert.True(page.ApplyCodeNow());

        Assert.Equal(before + 1, Applied(ctx));
        Assert.Equal(789, page.Items.Single(i => i.Code == "Weapon_M9").SellPrice);
        Assert.Equal(789, ctx.Services.Economy.Economy.Find(Armory, "Weapon_M9")?.SellPrice);
    }

    [AvaloniaFact]
    public async Task TheCodeShowsOneTraderOrTheWholeFile()
    {
        // A text box lays out its whole text on every key: by default it holds the settings and one trader's section.
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);
        var store = ctx.Services.Economy;
        Row(page, Saloon, "Beer_Can").SellPrice = 12;
        Row(page, Armory, "Weapon_M9").SellPrice = 456;
        Assert.DoesNotContain(Saloon, page.CodeText, StringComparison.Ordinal);

        page.CodeText = page.CodeText.Replace("\"456\"", "\"457\"", StringComparison.Ordinal);
        Assert.True(page.ApplyCodeNow());

        Assert.Equal(457, store.Economy.Find(Armory, "Weapon_M9")?.SellPrice);
        Assert.Equal(12, store.Economy.Find(Saloon, "Beer_Can")?.SellPrice); // merged back, the other section kept
        page.SelectedCodeScope = page.CodeScopes.Single(s => !ReferenceEquals(s, page.CodeScopes[0]));
        Assert.Equal(store.Text, page.CodeText);
        Assert.Contains(Saloon, page.CodeText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CtrlZInTheCodeBoxUndoesTextNotTheJournal()
    {
        // MainWindow's Ctrl+Z undid the project's last step under the text being typed (found headless, 2026-10-12).
        using var ctx = AppTestContext.Create(inline: false, dialogs: new ScriptedDialogService());
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
            HeadlessUi.Pump();
            ctx.Services.Economy.SetSetting("economy-logging", "0");
            var applied = Applied(ctx);
            var page = (EconomyPageViewModel)vm.NavigateTo("economy")!;
            HeadlessUi.Pump();
            page.IsCodeView = true;
            window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CodePanel").IsVisible = true; // it needs game files
            HeadlessUi.Pump();
            var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "CodeBox");
            box.Focus();
            box.CaretIndex = box.Text!.IndexOf("\"0\"", StringComparison.Ordinal) + 1;
            var original = box.Text;
            window.KeyTextInput("7");
            HeadlessUi.Pump();
            Assert.NotEqual(original, box.Text);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            HeadlessUi.Pump();

            Assert.Equal(original, box.Text);
            Assert.Equal(applied, Applied(ctx));
            Assert.Equal("0", ctx.Services.Economy.Economy.Setting("economy-logging"));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ABoxIsReadWhenLeftOrOnEnterNotPerKey()
    {
        // Audit F111/F112: the price box saved on every keystroke, and -1 became 0.
        using var ctx = AppTestContext.Create(inline: false, dialogs: new ScriptedDialogService());
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        HeadlessUi.Pump();
        using var page = await OpenAsync(ctx, project: false);
        var store = ctx.Services.Economy;
        var window = new Window { Width = 1600, Height = 900, Content = new ScumStudio.App.Views.Pages.EconomyPageView { DataContext = page } };
        window.Show();
        try
        {
            window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "EconomyBody").IsVisible = true; // it needs game files
            Row(page, Armory, "Weapon_AK47");
            page.FilterText = "AK-47";
            Assert.True(HeadlessUi.PumpUntil(() => window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "BuyBox")));
            var buy = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "BuyBox");
            var applied = Applied(ctx);
            buy.Focus();

            buy.Text = "12 0";
            HeadlessUi.Pump();
            Assert.Equal(applied, Applied(ctx)); // typing alone writes nothing
            var row = page.Items.Single(i => i.Code == "Weapon_AK47");
            Assert.Same(row, buy.DataContext);
            buy.Text = "12 000";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            HeadlessUi.Pump();
            Assert.Equal(12000, store.Economy.Find(Armory, "Weapon_AK47")?.PurchasePrice);
            Assert.Equal(applied + 1, Applied(ctx));
            Assert.Same(row, page.Items.Single(i => i.Code == "Weapon_AK47")); // the row is kept (its picture too)
            Assert.Same(row, buy.DataContext);
            Assert.Equal("12000", buy.Text);

            buy.Text = "abc";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            HeadlessUi.Pump();
            Assert.True(page.Items.Single(i => i.Code == "Weapon_AK47").IsInvalid);
            Assert.Equal(12000, store.Economy.Find(Armory, "Weapon_AK47")?.PurchasePrice);

            buy.Text = "-1";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            HeadlessUi.Pump();
            Assert.Null(store.Economy.Find(Armory, "Weapon_AK47")); // the game's price again: no entry
            Assert.Equal("11200", buy.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task InvalidCodeKeepsTheTextAndChangesNothing()
    {
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);
        Row(page, Armory, "Weapon_M9").SellPrice = 456;
        var path = Path.Combine(ctx.Services.Projects.DirectoryPath!, EconomyOverride.FileName);
        var file = File.ReadAllText(path);
        var before = Applied(ctx);
        var text = page.CodeText.Replace("\"456\"", "\"4x6\"", StringComparison.Ordinal);
        var broken = text[..text.LastIndexOf('}')];

        page.CodeText = broken;
        page.ValidateCodeNow();

        Assert.Contains(page.Problems, p => p.IsError && p.Line > 1);
        Assert.False(page.ApplyCodeNow());
        Assert.True(page.IsCodeDraft);
        Assert.Equal(file, File.ReadAllText(path));
        Assert.Equal(before, Applied(ctx));

        ctx.Services.Economy.SetSetting("economy-logging", "0"); // the store changes elsewhere: the draft stays
        Assert.Equal(broken, page.CodeText);
        page.DiscardCodeCommand.Execute(null);
        Assert.Equal(ctx.Services.Economy.Text, page.CodeText);
    }

    [AvaloniaFact]
    public async Task ImportShowsTheSummaryAndBothViews()
    {
        var dialogs = new ScriptedDialogService();
        using var ctx = AppTestContext.Create(dialogs: dialogs);
        using var page = await OpenAsync(ctx);
        var source = ctx.Combine("server", "EconomyOverride.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, GameFile("traders-unlimited-stock", "1",
            (Armory, new TradeableOverride("Weapon_AK47") { PurchasePrice = 15000 }),
            (Armory, new TradeableOverride("Weapon_M9") { SellPrice = 350 }),
            (Saloon, new TradeableOverride("Beer_Can") { RequiredFame = 7 })));
        dialogs.OpenFiles.Enqueue(source);
        var original = File.ReadAllText(source);
        var before = Applied(ctx);

        await page.ImportCommand.ExecuteAsync(null);

        Assert.Equal(before + 1, Applied(ctx));
        Assert.Equal(Loc.F("Economy.Import.Summary", "EconomyOverride.json", 1, 2, 3), page.ImportSummary?.Title);
        Assert.Contains(Armory, page.ImportSummary!.Traders, StringComparison.Ordinal);
        Assert.Equal(15000, Row(page, Armory, "Weapon_AK47").PurchasePrice);
        Assert.Contains("\"15000\"", page.CodeText, StringComparison.Ordinal);
        Assert.Equal("1", page.SettingRows.Single(r => r.Key == "traders-unlimited-stock").Value);
        Assert.Equal(original, File.ReadAllText(source)); // only read

        ctx.Services.Projects.Undo();
        Assert.Equal(11200, page.Items.Single(i => i.Code == "Weapon_AK47").PurchasePrice);
        Assert.Equal("0", page.SettingRows.Single(r => r.Key == "traders-unlimited-stock").Value);
        page.DismissImportCommand.Execute(null);
        Assert.False(page.HasImportSummary);
    }

    [AvaloniaFact]
    public async Task ARefusedImportOpensTheFileInCodeAndChangesNothing()
    {
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);
        var before = Applied(ctx);
        const string broken = "{ \"economy-override\": { \"traders\": { \"A_0_Armory\": [ { \"tradeable-code\": \"Weapon_AK47\" ";

        page.ImportText(broken, "old.json");

        Assert.Equal(before, Applied(ctx));
        Assert.True(page.IsCodeView);
        Assert.Equal(broken, page.CodeText);
        Assert.True(page.IsCodeDraft);
        Assert.Contains(page.Problems, p => p.IsError);
    }

    [AvaloniaFact]
    public async Task ARefusedImportNeverReplacesTheUsersOwnDraft()
    {
        // Review (economy): a refused import opened its text in Code over the user's own unapplied text with errors.
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);
        page.IsCodeView = true;
        var mine = page.CodeText.Replace("\"traders\":", "\"traders\": oops", StringComparison.Ordinal);
        page.CodeText = mine;
        page.ValidateCodeNow();
        Assert.True(page.IsCodeDraft);
        var before = Applied(ctx);

        const string Broken = "{ \"economy-override\": { \"traders\": { \"A_0_Armory\": [ { \"tradeable-code\": \"Weapon_AK47\" ";
        var errors = EconomyValidator.Validate(Broken, ctx.Services.Economy.Context).Count(p => p.IsError);
        page.ImportText(Broken, "old.json");

        Assert.Equal(mine, page.CodeText);
        Assert.True(page.IsCodeDraft);
        Assert.Equal(before, Applied(ctx));
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Message == Loc.F("Economy.Import.DraftKept", "old.json", errors));
    }

    [AvaloniaFact]
    public async Task AnImportedUnknownTraderIsListed()
    {
        // Audit F444: sections of traders neither the game nor the map knows were invisible on the page.
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);

        page.ImportText(GameFile(null, null, ("Z_9_Ghost", new TradeableOverride("Mod_Item") { SellPrice = 400 })), "old.json");

        var ghost = Assert.Single(page.Traders, t => t.Name == "Z_9_Ghost");
        Assert.True(ghost.IsUnknown);
        var item = Row(page, "Z_9_Ghost", "Mod_Item");
        Assert.True(item.IsUnknown);
        Assert.Equal(400, item.SellPrice);
        Assert.Equal("-1", item.PurchaseText);
    }

    [AvaloniaFact]
    public async Task NewGameTablesRebuildTheShownRows()
    {
        // Audit F280: rows kept across a store change showed the old tables' prices.
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);
        Assert.Equal(11200, Row(page, Armory, "Weapon_AK47").PurchasePrice);

        ctx.Services.Economy.UseDefaults(Tables(akPrice: 9999));

        Assert.Equal(9999, page.Items.Single(i => i.Code == "Weapon_AK47").PurchasePrice);
    }

    [AvaloniaFact]
    public async Task WithoutAProjectNothingCanBeEdited()
    {
        // Audit F299: without a project the page took edits and kept none of them.
        var dialogs = new ScriptedDialogService();
        using var ctx = AppTestContext.Create(dialogs: dialogs);
        using var page = await OpenAsync(ctx, project: false);
        dialogs.OpenFiles.Enqueue(ctx.Combine("none.json"));

        var ak = Row(page, Armory, "Weapon_AK47");
        Assert.False(page.CanEdit);
        Assert.False(ak.IsEditable);
        Assert.All(page.SettingRows, r => Assert.False(r.IsEditable));
        ak.PurchasePrice = 5;
        await page.ImportCommand.ExecuteAsync(null);

        Assert.Equal(11200, page.Items.Single(i => i.Code == "Weapon_AK47").PurchasePrice);
        Assert.Null(ctx.Services.Economy.Economy.Find(Armory, "Weapon_AK47"));
        Assert.Single(dialogs.OpenFiles); // the file dialog was not even shown
    }

    [AvaloniaFact]
    public async Task ASettingIsEditedAndResetToTheGamesValue()
    {
        using var ctx = AppTestContext.Create(dialogs: new ScriptedDialogService());
        using var page = await OpenAsync(ctx);
        var store = ctx.Services.Economy;
        var before = Applied(ctx);
        EconomySettingRow Setting(string key) => page.SettingRows.Single(r => r.Key == key);
        Assert.Equal(EconomyOverride.SettingKeys, page.SettingRows.Select(r => r.Key));

        Setting("traders-unlimited-stock").Flag = true;
        Setting("tradeable-rotation-time-ingame-hours-min").Text = "50";
        Setting("gold-base-price").Text = "12 000";

        Assert.Equal(before + 3, Applied(ctx));
        Assert.Equal("1", store.Economy.Setting("traders-unlimited-stock"));
        Assert.Equal("50.0", store.Economy.Setting("tradeable-rotation-time-ingame-hours-min"));
        Assert.Equal("12000", store.Economy.Setting("gold-base-price"));
        Assert.True(Setting("traders-unlimited-stock").CanReset);

        Setting("gold-base-price").Text = "abc";
        Assert.True(Setting("gold-base-price").IsInvalid);
        Assert.Equal("12000", store.Economy.Setting("gold-base-price"));

        Setting("traders-unlimited-stock").ResetCommand.Execute(null);
        Assert.Equal("0", store.Economy.Setting("traders-unlimited-stock"));
        Assert.False(Setting("traders-unlimited-stock").CanReset);
        ctx.Services.Projects.Undo();
        Assert.True(Setting("traders-unlimited-stock").Flag);
    }
}
