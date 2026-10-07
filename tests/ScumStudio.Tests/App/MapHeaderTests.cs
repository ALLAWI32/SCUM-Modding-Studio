using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner and Discord (Russian): the Map header's loading text ran into other text. In every language and at 1280 px, the
/// loading line has room for the whole text and the frame stats step aside while a level loads (screenshots with
/// <c>SCUMSTUDIO_SCREENSHOTS</c>).
/// </summary>
[Collection(LanguageCollection.Name)] // switches or reads the UI language: never in parallel with another language test
public sealed class MapHeaderTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheLoadingTextFitsInEveryLanguage()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ScumStudio.Core.Abstractions.ProgressSink.Null);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1280, 800);
        try
        {
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "UpdateButton").IsVisible = true; // as in a release build: "Up to date · x.y.z"
            foreach (var language in Loc.Languages)
            {
                Loc.Instance.Language = language.Code;
                map.IsWorldMode = true;
                map.WorldStatus = Loc.F("Map.World.Terrain", 170, 400);
                map.IsLoadingLevel = true;
                map.LoadStatus = Loc.F("Map.Preparing", 126, 1837, "Landscape_C_0_1b");
                HeadlessUi.Pump();
                HeadlessUi.SaveScreenshot(window, $"map-header-{language.Code}");

                var loading = window.GetVisualDescendants().OfType<DockPanel>().Single(d => d.Name == "LoadingStatus");
                var text = loading.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == map.LoadStatus);
                Assert.True(text.IsEffectivelyVisible, language.Code);
                var natural = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily, FontWeight = text.FontWeight };
                natural.Measure(Avalonia.Size.Infinity);
                Assert.True(text.Bounds.Width + 1 >= natural.DesiredSize.Width, $"{language.Code}: {text.Bounds.Width} < {natural.DesiredSize.Width}");
                Assert.False(window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "FrameStats").IsVisible, language.Code);

                // The top bar: the search field ends before the Discord/Support buttons begin (it squeezed under them once).
                // Narrow (1280): the tabs are icons only; wide (1600): their names are back. Neither overlaps.
                foreach (var width in new[] { 1280, 1600 })
                {
                    window.Width = width;
                    HeadlessUi.Pump();
                    Assert.Equal(width < MainWindowViewModel.CompactHeaderWidth, vm.IsCompactHeader);
                    var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "GlobalSearch");
                    var discord = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiscordButton");
                    var searchRight = search.TranslatePoint(new Avalonia.Point(search.Bounds.Width, 0), window)!.Value.X;
                    var discordLeft = discord.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.X;
                    Assert.True(searchRight <= discordLeft, $"{language.Code} at {width}: search ends at {searchRight:0}, Discord starts at {discordLeft:0}");
                    Assert.True(search.Bounds.Width >= 90, $"{language.Code} at {width}: search is {search.Bounds.Width:0} wide");
                    var label = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "NavList").GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == vm.SelectedNavItem!.Title);
                    Assert.Equal(!vm.IsCompactHeader, label.IsVisible);
                }

                window.Width = 1280;
                HeadlessUi.Pump();
            }
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
            window.Close();
            vm.Dispose();
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheViewKeepsItsPlaceWhateverTheStatusLineSays()
    {
        // Owner: the header's status text changes width while the island streams, and the 3D view jumps with it.
        using var ctx = AppTestContext.Create(inline: false);
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ScumStudio.Core.Abstractions.ProgressSink.Null);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1280, 800);
        try
        {
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
            map.IsWorldMode = true;
            var viewport = window.GetVisualDescendants().OfType<ScumStudio.App.Controls.LevelViewport>().Single(v => v.Name == "Viewport3d");
            var longName = "Landscape_C_0_1b_With_A_Very_Long_Level_Name_That_Goes_On";
            foreach (var language in Loc.Languages)
            {
                Loc.Instance.Language = language.Code;
                (string World, bool Loading, string Load, string Caption, string Frame)[] lines =
                [
                    (string.Empty, false, string.Empty, "A_0", string.Empty),
                    (Loc.F("Map.World.Streaming", 3), true, Loc.F("Map.Preparing", 126, 1837, longName), Loc.F("Map.MoreLevels", longName, 41), string.Empty),
                    (Loc.F("Map.World.Around", 41), false, string.Empty, Loc.F("Map.MoreLevels", "B_4_Outpost", 7), "39,719 placements · 5,431 drawn · 34,288 culled · 12,345,678 tris · 144 fps"),
                    (Loc.T("Map.World.Ready"), true, "…", "x", "1 fps"),
                ];
                foreach (var width in new[] { 1280, 1600 })
                {
                    window.Width = width;
                    Rect? first = null;
                    foreach (var line in lines)
                    {
                        map.WorldStatus = line.World;
                        map.IsLoadingLevel = line.Loading;
                        map.LoadStatus = line.Load;
                        map.LoadedLevelsCaption = line.Caption;
                        viewport.SetValue(ScumStudio.App.Controls.LevelViewport.FrameInfoProperty, line.Frame);
                        HeadlessUi.Pump();
                        var at = new Rect(viewport.TranslatePoint(new Point(0, 0), window)!.Value, viewport.Bounds.Size);
                        Assert.True(viewport.IsEffectivelyVisible && at.Width > 300 && at.Height > 200, $"the view is shown ({at})");
                        first ??= at;
                        Assert.True(at == first, $"{language.Code} at {width}: the view moved from {first} to {at} for \"{line.World}\" / \"{line.Load}\" / \"{line.Caption}\"");
                    }
                }
            }
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
            window.Close();
            vm.Dispose();
        }
    }
}
