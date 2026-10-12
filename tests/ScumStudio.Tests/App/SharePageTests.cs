using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views.Pages;
using ScumStudio.Core.Settings;
using ScumStudio.Core.Sharing;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.App;

/// <summary>
/// "Share your mod" (owner, 2026-10-12): the Share card over the window, the display name in the settings, an upload of
/// only the chosen cells, Get as one undoable step and Remove, a clash shown with nothing applied, the gallery offline,
/// and moderation only with the key file. The gallery is <see cref="FakeModShareServer"/> in process.
/// </summary>
[Collection(LanguageCollection.Name)] // compares texts with Loc while ShareButtonHeaderTests switches the language
public sealed class SharePageTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/B_1_Farm";

    [AvaloniaFact]
    public async Task TheShareButtonOpensTheCardAndEveryControlHasATooltip()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Card");
        Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.HasProject)); // set through the UI dispatcher
        ctx.Services.Projects.Apply(new SetTransformOp(new ActorRef(Outpost, "House_2"), TransformValue.Identity, Somewhere));
        ctx.Services.Projects.Apply(new SetAssetValueOp("/Game/ConZ_Files/Vehicles/Kinglet/BP_Kinglet", "Default__BP_Kinglet_C", "MaxSpeed", "Float", "100", "120"));
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            Assert.Null(vm.Share);
            var button = HeadlessUi.FindNamed<Button>(window, "ShareButton")!;
            Assert.NotNull(ToolTip.GetTip(button));
            button.Command!.Execute(button.CommandParameter);
            HeadlessUi.Pump();

            var share = Assert.IsType<SharePageViewModel>(vm.Share);
            Assert.True(share.IsOpen);
            var view = HeadlessUi.Find<SharePageView>(window).Single();
            Assert.True(HeadlessUi.FindNamed<Panel>(view, "ShareCard")!.IsEffectivelyVisible);
            Assert.Equal(["A_0"], share.Sectors.Select(s => s.Key));
            Assert.Equal(1, share.AssetEdits);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.GetType() == typeof(TextBlock) && t.Text == share.Sectors[0].Label && t.IsEffectivelyVisible); // "A_0" keeps its underscore (no access key)
            Assert.False(share.IsModerator && !File.Exists(ModeratorKey.DefaultPath)); // Moderate only with the key file

            foreach (var tab in share.Tabs)
            {
                share.SelectedTab = tab;
                HeadlessUi.Pump();
                HeadlessUi.SaveScreenshot(window, $"share-card-{tab.Tab}");
                var silent = view.GetVisualDescendants().OfType<Control>()
                    .Where(c => c is Button or TextBox or CheckBox or ToggleButton or ComboBox && c.IsEffectivelyVisible && c.TemplatedParent is null)
                    .Where(c => ToolTip.GetTip(c) is null or "")
                    .Select(c => $"{tab.Tab}: {c.GetType().Name} {c.Name}")
                    .ToList();
                Assert.True(silent.Count == 0, string.Join(Environment.NewLine, silent));
            }

            share.StartCaptureCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.IsType<MapPageViewModel>(vm.CurrentPage);
            Assert.False(HeadlessUi.FindNamed<Panel>(view, "ShareCard")!.IsEffectivelyVisible);
            Assert.True(HeadlessUi.FindNamed<Border>(view, "CaptureBar")!.IsEffectivelyVisible);
            HeadlessUi.SaveScreenshot(window, "share-capture-bar");
            share.CancelCaptureCommand.Execute(null);
            share.CloseCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.False(HeadlessUi.FindNamed<Panel>(view, "ShareCard")!.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void TheDisplayNameIsKeptInSettingsAndCanBeRemoved()
    {
        using var ctx = AppTestContext.Create();
        using (var share = new SharePageViewModel(ctx.Services, _ => null, Client(new FakeModShareServer())))
        {
            share.DisplayName = "  Allawi\u0007 ";
            share.SaveDisplayName();
            Assert.Equal("Allawi", share.DisplayName);
        }

        // A new start reads it from settings.json in the same data folder.
        Assert.Equal("Allawi", new SettingsStore(ctx.Services.DataDirectory).Load().ModShareName);
        using (var again = new SharePageViewModel(ctx.Services, _ => null, Client(new FakeModShareServer())))
        {
            Assert.Equal("Allawi", again.DisplayName);
            again.ForgetNameCommand.Execute(null);
            Assert.Equal(string.Empty, again.DisplayName);
        }

        Assert.Null(new SettingsStore(ctx.Services.DataDirectory).Load().ModShareName);
    }

    [Fact]
    public void AppSettingsWithADifferentShareNameAreNotEqual()
    {
        var a = new AppSettings { ModShareName = "Allawi", ModShareUrl = "https://share.example" };
        Assert.Equal(a, a with { });
        Assert.Equal(a.GetHashCode(), (a with { }).GetHashCode());
        Assert.NotEqual(a, a with { ModShareName = "Someone" });
        Assert.NotEqual(a, a with { ModShareName = null });
        Assert.NotEqual(a, a with { ModShareUrl = "https://other.example" });
    }

    [Fact]
    public async Task SharingUploadsOnlyTheChosenSectors()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Author");
        ctx.Services.Projects.Apply(new SetTransformOp(new ActorRef(Outpost, "House_2"), TransformValue.Identity, Somewhere));
        ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(Farm, "Barn_2")));
        var fake = new FakeModShareServer();
        using var share = new SharePageViewModel(ctx.Services, _ => null, Client(fake));
        share.Open();
        Assert.Equal(["A_0", "B_1"], share.Sectors.Select(s => s.Key));
        Assert.Empty(fake.Requests); // opening the Share tab asks the gallery nothing

        await share.ShareCommand.ExecuteAsync(null);
        Assert.Empty(fake.Requests); // nothing chosen, no name: nothing is sent
        Assert.False(string.IsNullOrEmpty(share.ShareStatus));

        share.Sectors.Single(s => s.Key == "A_0").IsChecked = true;
        share.DisplayName = "Allawi";
        share.ModName = "Outpost house";
        share.Description = "Moves a house.";
        share.Pictures = Picture();
        await share.ShareCommand.ExecuteAsync(null);

        var upload = Assert.Single(fake.Requests, r => r.Method == "POST");
        Assert.Equal("/upload", upload.Path);
        var content = ShareFile.Read(upload.Body!);
        Assert.Equal(["A_0"], content.Manifest.Sectors);
        Assert.Equal("Allawi", content.Manifest.Author);
        Assert.All(content.Edits.GetTouchedLevels(), level => Assert.Equal(Outpost, level));
        Assert.Single(content.Edits.Ops);
        using var zip = new ZipArchive(new MemoryStream(upload.Body!));
        Assert.All(zip.Entries, e => Assert.True(e.FullName.EndsWith(".json", StringComparison.Ordinal) || e.FullName.EndsWith(".jpg", StringComparison.Ordinal), e.FullName));
        Assert.True(share.IsGalleryTab); // the gallery opens on success
    }

    [Fact]
    public async Task GetAddsTheModAsOneUndoableStepAndRemoveTakesItBack()
    {
        using var ctx = AppTestContext.Create();
        var fake = new FakeModShareServer();
        fake.Seed(await AuthorPackageAsync(ctx));

        var receiver = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Receiver");
        var before = receiver.State.Describe();
        Assert.False(ctx.Services.Economy.Economy.HasSection("A_0_Armory")); // the receiver starts from its own economy
        var history = receiver.History.Count;
        using var share = new SharePageViewModel(ctx.Services, _ => null, Client(fake));
        share.Open();
        share.SelectedTab = share.Tabs.Single(t => t.Tab == ShareTab.Gallery);
        await share.RefreshGalleryCommand.ExecuteAsync(null);
        var card = Assert.Single(share.Gallery);

        await share.GetCommand.ExecuteAsync(card);
        var pending = Assert.IsType<SharePendingViewModel>(share.Pending);
        Assert.False(pending.HasConflict);
        Assert.Contains(pending.Lines, l => l.Contains("A_0", StringComparison.Ordinal));
        Assert.Equal(history, receiver.History.Count); // nothing changes before "Add to my project"

        share.ApplyPendingCommand.Execute(null);
        Assert.Null(share.Pending);
        Assert.Equal(history + 1, receiver.History.Count);
        Assert.NotEqual(before, receiver.State.Describe());
        Assert.Contains(CraftablesFile.Load(receiver.DirectoryPath).Items, c => c.Name == "Shared crate");
        Assert.True(ctx.Services.Economy.Economy.HasSection("A_0_Armory"));
        Assert.True(EconomyOverride.LoadFrom(receiver.DirectoryPath)!.HasSection("A_0_Armory")); // saved, not only in memory
        var installed = Assert.Single(share.Installed);

        // Ctrl+Z takes the edits back as one step.
        ctx.Services.Projects.Undo();
        Assert.Equal(before, receiver.State.Describe());
        ctx.Services.Projects.Redo();

        // Remove takes all of it back: the edits, the craftable and the economy section.
        share.RemoveInstalledCommand.Execute(installed);
        Assert.Equal(before, receiver.State.Describe());
        Assert.Empty(share.Installed);
        Assert.DoesNotContain(CraftablesFile.Load(receiver.DirectoryPath).Items, c => c.Name == "Shared crate");
        Assert.False(ctx.Services.Economy.Economy.HasSection("A_0_Armory"));
    }

    [Fact]
    public async Task AConflictIsShownAndNothingIsApplied()
    {
        using var ctx = AppTestContext.Create();
        var fake = new FakeModShareServer();
        fake.Seed(await AuthorPackageAsync(ctx));

        var receiver = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Receiver");
        ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(Outpost, "House_2"))); // the mod moves this house
        var history = receiver.History.Count;
        using var share = new SharePageViewModel(ctx.Services, _ => null, Client(fake));
        share.Open();
        await share.RefreshGalleryCommand.ExecuteAsync(null);
        await share.GetCommand.ExecuteAsync(share.Gallery.Single());

        var pending = Assert.IsType<SharePendingViewModel>(share.Pending);
        Assert.True(pending.HasConflict);
        Assert.False(string.IsNullOrEmpty(pending.ConflictDetail));
        Assert.False(share.ApplyPendingCommand.CanExecute(null));
        share.ApplyPendingCommand.Execute(null);
        Assert.Equal(history, receiver.History.Count);
        Assert.Empty(share.Installed);
    }

    [Fact]
    public async Task TheGalleryOfflineShowsAMessageAndNoCrash()
    {
        using var ctx = AppTestContext.Create();
        var fake = new FakeModShareServer { Override = _ => throw new HttpRequestException("No route to host.") };
        using var share = new SharePageViewModel(ctx.Services, _ => null, Client(fake));
        share.Open();
        await share.RefreshGalleryCommand.ExecuteAsync(null);
        Assert.Empty(share.Gallery);
        Assert.Equal(Loc.T("Share.Offline"), share.GalleryStatus);

        // No address at all: said as such, nothing sent.
        using var unset = new SharePageViewModel(ctx.Services, _ => null, new ModShareService(NullLogger.Instance, () => null, () => fake));
        await unset.RefreshGalleryCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("Share.NotAvailable"), unset.GalleryStatus);
        Assert.False(unset.IsConfigured);
    }

    [Fact]
    public async Task ModerateAppearsOnlyWithTheKeyFile()
    {
        using var ctx = AppTestContext.Create();
        var keyFile = ctx.Combine("owner", ModeratorKey.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        var publicKey = ModeratorKey.GenerateFile(keyFile);
        var fake = new FakeModShareServer(publicKey) { Now = DateTimeOffset.UtcNow };
        var id = fake.Seed(FakeModShareServer.Package());
        var previous = Environment.GetEnvironmentVariable(ModeratorKey.PathVariable);
        try
        {
            Environment.SetEnvironmentVariable(ModeratorKey.PathVariable, keyFile);
            using var share = new SharePageViewModel(ctx.Services, _ => null, Client(fake));
            share.Open();
            Assert.True(share.IsModerator);
            share.SelectedTab = share.Tabs.Single(t => t.Tab == ShareTab.Moderate);
            await share.RefreshGalleryCommand.ExecuteAsync(null);
            var card = Assert.Single(share.Gallery);
            share.AskRemoveListingCommand.Execute(card);
            Assert.True(card.IsConfirming);
            await share.RemoveListingCommand.ExecuteAsync(card);

            Assert.Contains(fake.Requests, r => r.Method == "DELETE" && r.Path == "/mods/" + id);
            Assert.Empty(fake.Ids);
            Assert.Empty(share.Gallery);

            Environment.SetEnvironmentVariable(ModeratorKey.PathVariable, ctx.Combine("nowhere", ModeratorKey.FileName));
            share.Open();
            Assert.False(share.IsModerator);
            Assert.DoesNotContain(share.Tabs, t => t.Tab == ShareTab.Moderate);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModeratorKey.PathVariable, previous);
        }
    }

    [AvaloniaFact]
    public void NoRequestIsMadeAtStartup()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var fake = new FakeModShareServer();
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            Assert.Null(vm.Share); // the card (and its client) is made on the first click
            var share = new SharePageViewModel(ctx.Services, vm.NavigateTo, Client(fake));
            vm.Share = share;
            vm.OpenShareCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.True(share.IsShareTab);
            Assert.Empty(fake.Requests); // the Share tab asks nothing; the gallery is read when its tab opens
            share.SelectedTab = share.Tabs.Single(t => t.Tab == ShareTab.Gallery);
            Assert.True(HeadlessUi.PumpUntil(() => fake.Requests.Count > 0 && !share.IsGalleryLoading));
            Assert.Equal("/index.json", fake.Requests[0].Path);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheGalleryShowsTheCardsAndWhatGetWouldAdd()
    {
        var fake = new FakeModShareServer();
        using (var author = AppTestContext.Create())
        {
            fake.Seed(await AuthorPackageAsync(author));
        }

        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Receiver");
        Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.HasProject));
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            var share = new SharePageViewModel(ctx.Services, vm.NavigateTo, Client(fake));
            vm.Share = share;
            vm.OpenShareCommand.Execute(null);
            share.SelectedTab = share.Tabs.Single(t => t.Tab == ShareTab.Gallery);
            Assert.True(HeadlessUi.PumpUntil(() => share.Gallery.Count == 1 && !share.IsGalleryLoading));
            var view = HeadlessUi.Find<SharePageView>(window).Single();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Outpost house" && t.IsEffectivelyVisible);
            HeadlessUi.SaveScreenshot(window, "share-gallery");

            var get = share.GetCommand.ExecuteAsync(share.Gallery[0]);
            Assert.True(HeadlessUi.PumpUntil(() => get.IsCompleted));
            Assert.True(HeadlessUi.FindNamed<Border>(view, "PendingPanel")!.IsEffectivelyVisible);
            Assert.True(HeadlessUi.FindNamed<Button>(view, "ApplyPendingButton")!.IsEffectivelyEnabled);
            HeadlessUi.SaveScreenshot(window, "share-preview");
        }
        finally
        {
            window.Close();
        }
    }

    private static ModShareService Client(FakeModShareServer fake) =>
        new(NullLogger.Instance, () => FakeModShareServer.Url, () => fake);

    private static SharePictures Picture()
    {
        var rgba = new byte[64 * 36 * 4];
        new Random(3).NextBytes(rgba);
        return SharePicture.FromRgba(rgba, 64, 36);
    }

    /// <summary>
    /// A package from an author's project: a house moved in A_0, a craftable and the economy of the A_0 armory. The author
    /// project is closed afterwards; the next project created becomes the open one.
    /// </summary>
    private static async Task<byte[]> AuthorPackageAsync(AppTestContext ctx)
    {
        var author = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Author");
        ctx.Services.Projects.Apply(new SetTransformOp(new ActorRef(Outpost, "House_2"), TransformValue.Identity, Somewhere));
        new CraftablesFile { Items = [new Craftable { Name = "Shared crate", Source = "/Game/Props/SM_Crate.SM_Crate", Mesh = "/Game/Props/SM_Crate.SM_Crate" }] }
            .Save(author.DirectoryPath);
        ctx.Services.Economy.Economy.SetSection("A_0_Armory", [new TradeableOverride("Weapon_M1911") { PurchasePrice = 999 }]);
        var pictures = Picture();
        var content = ShareFile.Collect(author, new SectorChoice(new HashSet<string> { "A_0" }, false, false),
            new ShareManifest { Name = "Outpost house", Author = "Allawi", Description = "Moves a house.", AppVersion = "3.1.0", Created = DateTimeOffset.UtcNow },
            pictures.Preview, pictures.Thumb, ctx.Services.Economy.Economy, craftables: true, out _, out _);
        ctx.Services.Projects.Close();
        return ShareFile.Write(content);
    }
}

/// <summary>The title strip keeps its search field usable with the Share button in every language, also when the tab names show.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ShareButtonHeaderTests
{
    [AvaloniaFact]
    public void TheSearchFieldStaysUsableNextToTheShareButton()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1280, 800);
        try
        {
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "UpdateButton").IsVisible = true; // as in a release build
            foreach (var language in Loc.Languages)
            {
                Loc.Instance.Language = language.Code;
                // Compact below CompactHeaderWidth (icons only); the Share button names itself from ShareLabelWidth, where every language has room.
                foreach (var width in new[] { 1280, 1600, (int)MainWindowViewModel.ShareLabelWidth, 1920 })
                {
                    window.Width = width;
                    HeadlessUi.Pump();
                    var search = HeadlessUi.FindNamed<TextBox>(window, "GlobalSearch")!;
                    var discord = HeadlessUi.FindNamed<Button>(window, "DiscordButton")!;
                    var share = HeadlessUi.FindNamed<Button>(window, "ShareButton")!;
                    var searchRight = search.TranslatePoint(new Avalonia.Point(search.Bounds.Width, 0), window)!.Value.X;
                    var discordLeft = discord.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.X;
                    Assert.True(searchRight <= discordLeft, $"{language.Code} at {width}: search ends at {searchRight:0}, Discord starts at {discordLeft:0}");
                    Assert.True(search.Bounds.Width >= 90, $"{language.Code} at {width}: search is {search.Bounds.Width:0} wide");
                    Assert.True(share.IsEffectivelyVisible, $"{language.Code} at {width}");
                    Assert.Equal(width >= MainWindowViewModel.ShareLabelWidth, share.GetVisualDescendants().OfType<TextBlock>().Single().IsEffectivelyVisible);
                }
            }
        }
        finally
        {
            Loc.Instance.Language = "en";
            window.Close();
        }
    }
}
