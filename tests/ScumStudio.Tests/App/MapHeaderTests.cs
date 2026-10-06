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
}
