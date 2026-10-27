using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using static ScumStudio.Tests.App.SpawnsPageTests;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Spawns page's "Place NPCs" wizard over a made-up island (no paks): who, how many posts, then one click on the map is
/// one journal entry; the user never sees a weight list.
/// </summary>
public sealed class SpawnsNpcWizardTests
{
    private static readonly NpcGuardType Guards = new(
        "/Game/ConZ_Files/Encounters/EncounterClasses/Hight_Threat/HTZ_Test_Zombie_Encounter_Inside.HTZ_Test_Zombie_Encounter_Inside_C",
        ["BP_Guard_Lvl_3", "BP_Guard_Lvl_4"], Bunker);

    [AvaloniaFact]
    public void TheKindsAreListedWithReadableNames()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var page = Open(ctx);

        var options = page.Options([Guards]);

        var kind = Assert.Single(options);
        Assert.Equal("Test Zombie Inside", kind.Title);
        Assert.Equal(Loc.T("Spawns.NpcWizard.Threat.High") + " · Guard Lvl 3, Guard Lvl 4", kind.Detail);
        page.SelectedNpcKind = kind;
        Assert.Equal(Loc.F("Spawns.NpcWizard.Summary", 3, kind.Title), page.NpcSummary);
        page.NpcPosts = 5;
        Assert.Equal(Loc.F("Spawns.NpcWizard.Summary", 5, kind.Title), page.NpcSummary);
    }

    [AvaloniaFact]
    public async Task AClickOnTheMapPlacesTheChosenNpcsInOneUndoStep()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx, ground: 1200f);
        page.IsNpcsTab = true;
        page.SelectedNpcKind = page.Options([Guards])[0];
        page.NpcPosts = 3;
        var history = ctx.Services.Projects.History.Count;
        Assert.True(page.PickNpcPlaceCommand.CanExecute(null));

        page.PickNpcPlaceCommand.Execute(null);

        Assert.True(page.IsNpcPlacing); // the next click places the NPCs, not a plain spawn place
        Assert.Equal(SpawnMapTool.Place, page.MapTool);
        Assert.False(page.PickNpcPlaceCommand.CanExecute(null));

        await page.PlaceAtCommand.ExecuteAsync(new Vector2(-860000f, 500000f));

        Assert.False(page.IsNpcPlacing);
        Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
        var state = ctx.Services.Projects.Current!.State;
        var group = Assert.Single(state.AddedActors.Values.OfType<AddSpawnPlaceOp>(), a => a.Kind == SpawnPlaceKind.CharacterGroup);
        Assert.Equal(3, group.Points!.Count);
        Assert.Equal(1200f + SpawnGroundCheck.CharacterLift, group.Transform.Location.Z);
        Assert.Single(state.AssetClones); // the area's own copy of the settings; the stock ones stay as they are
        Assert.Contains(state.AddedActors.Values.OfType<AddSpawnPlaceOp>(), a => a.Kind == SpawnPlaceKind.Zone); // no stock zone around: a zone of its own

        ctx.Services.Projects.Undo();
        Assert.Empty(ctx.Services.Projects.Current!.State.AddedActors);
        Assert.Empty(ctx.Services.Projects.Current!.State.AssetClones);
    }

    /// <summary>"Own copy of one NPC": the whole chain is a copy in the same step, so the stock NPCs are not what the posts spawn.</summary>
    [AvaloniaFact]
    public async Task AnOwnCopyIsPlacedWithItsChainInOneUndoStep()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx, ground: 500f);
        page.IsNpcsTab = true;
        page.SelectedNpcKind = page.Options([Guards])[0];

        // The characters of the kind are offered, the first is chosen; the summary says the stock NPCs stay.
        Assert.Equal(["Guard Lvl 3", "Guard Lvl 4"], page.NpcCharacters.Select(c => c.Title));
        Assert.Equal("BP_Guard_Lvl_3", page.SelectedNpcCharacter!.Name);
        Assert.Equal(Loc.F("Spawns.NpcWizard.Summary", 3, page.SelectedNpcKind.Title), page.NpcSummary);
        page.NpcOwnCopy = true;
        Assert.Equal(Loc.F("Spawns.NpcWizard.OwnSummary", 3, "Guard Lvl 3"), page.NpcSummary);
        page.SelectedNpcCharacter = page.NpcCharacters[1];
        Assert.Equal(Loc.F("Spawns.NpcWizard.OwnSummary", 3, "Guard Lvl 4"), page.NpcSummary);
        var history = ctx.Services.Projects.History.Count;

        page.PickNpcPlaceCommand.Execute(null);
        await page.PlaceAtCommand.ExecuteAsync(new Vector2(-860000f, 500000f));

        Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
        var state = ctx.Services.Projects.Current!.State;
        var chain = Assert.Single(state.AssetClones);
        Assert.Equal(["HTZ_Test_Bunker_Own", "HTZ_Test_Zombie_Encounter_Inside_Own"], chain.Packages.Select(p => p.New[(p.New.LastIndexOf('/') + 1)..]));
        Assert.Equal(chain.Packages[0].New, chain.NewPrimary); // the zone settings first
        var group = Assert.Single(state.AddedActors.Values.OfType<AddSpawnPlaceOp>(), a => a.Kind == SpawnPlaceKind.CharacterGroup);
        Assert.Equal(3, group.Points!.Count);
        var zone = Assert.Single(state.AddedActors.Values.OfType<AddSpawnPlaceOp>(), a => a.Kind == SpawnPlaceKind.Zone);
        Assert.Equal(chain.NewPrimary, zone.Setting); // the area spawns what the copy's settings say

        ctx.Services.Projects.Undo();
        Assert.Empty(ctx.Services.Projects.Current!.State.AssetClones);
        Assert.Empty(ctx.Services.Projects.Current!.State.AddedActors);
    }

    [AvaloniaFact]
    public async Task ChangingTheToolOrTheCategoryCancelsThePick()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx);
        page.IsNpcsTab = true;
        page.SelectedNpcKind = page.Options([Guards])[0];

        page.PickNpcPlaceCommand.Execute(null);
        Assert.True(page.IsNpcPlacing);
        page.IsPanTool = true;
        Assert.False(page.IsNpcPlacing);

        page.PickNpcPlaceCommand.Execute(null);
        Assert.True(page.IsNpcPlacing);
        page.IsVehiclesTab = true;
        Assert.False(page.IsNpcPlacing);

        page.IsNpcsTab = true;
        page.PickNpcPlaceCommand.Execute(null);
        page.CancelNpcPlaceCommand.Execute(null);
        Assert.False(page.IsNpcPlacing);
        Assert.Empty(ctx.Services.Projects.Current!.State.AddedActors); // nothing was changed
    }

    [AvaloniaFact]
    public void NothingIsPickedWithoutAKindOrAProject()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var page = Open(ctx);
        page.IsNpcsTab = true;

        Assert.False(page.PickNpcPlaceCommand.CanExecute(null)); // no kind yet
        page.SelectedNpcKind = page.Options([Guards])[0];
        page.PickNpcPlaceCommand.Execute(null); // no project: a notice, not a pick
        Assert.False(page.IsNpcPlacing);
    }

    /// <summary>The card shows on the NPC category only, offers the three steps and swaps the pick button for "click the map" while it waits.</summary>
    [AvaloniaFact]
    public async Task TheCardIsOnTheNpcCategoryAndFollowsThePick()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx);
        var window = new Window { Content = new ScumStudio.App.Views.Pages.SpawnsPageView { DataContext = page }, Width = 1500, Height = 900 };
        window.Show();
        try
        {
            HeadlessUi.Pump();
            var card = HeadlessUi.FindNamed<Border>(window, "NpcWizard")!;
            Assert.False(card.IsVisible); // the vehicles category

            page.IsNpcsTab = true;
            page.NpcKinds = page.Options([Guards]);
            page.SelectedNpcKind = page.NpcKinds[0];
            HeadlessUi.Pump();
            var pick = HeadlessUi.FindNamed<Button>(window, "PickNpcPlaceButton")!;
            Assert.True(card.IsVisible);
            Assert.True(pick.IsVisible && pick.IsEffectivelyEnabled);
            Assert.False(HeadlessUi.FindNamed<TextBlock>(window, "NpcWaitingText")!.IsVisible);
            Assert.Equal(page.NpcSummary, HeadlessUi.FindNamed<TextBlock>(window, "NpcSummaryText")!.Text);
            HeadlessUi.SaveScreenshot(window, "page-spawns-npc-wizard");

            // "Own copy" shows which character to copy, and the summary says the stock NPCs stay.
            var characters = HeadlessUi.FindNamed<ComboBox>(window, "NpcCharacterBox")!;
            Assert.False(characters.IsVisible);
            HeadlessUi.FindNamed<CheckBox>(window, "NpcOwnCopyBox")!.IsChecked = true;
            HeadlessUi.Pump();
            Assert.True(page.NpcOwnCopy && characters.IsVisible);
            Assert.Equal(Loc.F("Spawns.NpcWizard.OwnSummary", 3, "Guard Lvl 3"), HeadlessUi.FindNamed<TextBlock>(window, "NpcSummaryText")!.Text);
            HeadlessUi.SaveScreenshot(window, "page-spawns-npc-wizard-own");
            page.NpcOwnCopy = false;
            HeadlessUi.Pump();

            page.PickNpcPlaceCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.False(pick.IsVisible);
            Assert.True(HeadlessUi.FindNamed<TextBlock>(window, "NpcWaitingText")!.IsVisible);
            Assert.True(HeadlessUi.FindNamed<Button>(window, "CancelNpcPlaceButton")!.IsVisible);
            HeadlessUi.SaveScreenshot(window, "page-spawns-npc-wizard-waiting");
        }
        finally
        {
            window.Close();
        }
    }

    private static SpawnsPageViewModel Open(AppTestContext ctx, float? ground = null)
    {
        var source = new FakeSource { Places = [], Ground = ground };
        var page = new SpawnsPageViewModel(ctx.Services, source);
        Assert.True(HeadlessUi.PumpUntil(() => page.Entries.Count > 0));
        return page;
    }
}
