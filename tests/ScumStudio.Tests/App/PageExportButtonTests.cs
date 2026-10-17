using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner 2026-10-17: an Export button on each page that builds only that page's changes. The Vehicles, Weapons, Items and
/// Spawns pages show one in their header, in full and beside the other buttons at every common window size; each one is
/// wired to its own page's mod and explains itself.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class PageExportButtonTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task EveryPageHasItsOwnExportButtonInItsHeader()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Pages");
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1280, 720);
        try
        {
            foreach (var (key, module) in new[] { ("vehicles", ExportModule.Vehicles), ("weapons", ExportModule.Weapons), ("items", ExportModule.Items), ("spawns", ExportModule.Spawns) })
            {
                var page = vm.NavigateTo(key)!;
                var export = page switch
                {
                    ModulePageViewModel m => m.PageExport,
                    SpawnsPageViewModel s => s.PageExport,
                    _ => null,
                };
                Assert.Equal(module, export?.Module);
                foreach (var (width, height) in new[] { (1280, 720), (1920, 1080) })
                {
                    window.Width = width;
                    window.Height = height;
                    HeadlessUi.Pump();
                    var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PageExportButton" && b.IsEffectivelyVisible);
                    Assert.Equal(Loc.T("Export.Module.Button.Tip"), ToolTip.GetTip(button));
                    Assert.Same(export!.ExportCommand, button.Command);
                    var header = button.FindAncestorOfType<Border>(includeSelf: false)!;
                    while (!header.Classes.Contains("pageHeader"))
                    {
                        header = header.FindAncestorOfType<Border>()!;
                    }

                    var box = At(window, header);
                    var rect = At(window, button);
                    Assert.True(box.Inflate(0.5).Contains(rect), $"{key} at {width}: the button {rect} leaves the header {box}");
                    foreach (var other in ((Panel)button.Parent!).Children.Where(c => c != button && c.IsEffectivelyVisible))
                    {
                        var overlap = rect.Intersect(At(window, other));
                        Assert.True(overlap.Width < 0.5 || overlap.Height < 0.5, $"{key} at {width}: the button {rect} overlaps {other.GetType().Name} {At(window, other)}");
                    }

                    HeadlessUi.SaveScreenshot(window, $"page-export-{key}-{width}");
                }
            }

            // The Map: Export mod in the header builds the whole project; "Export map only" under its History builds the Map's.
            var map = Assert.IsType<MapPageViewModel>(vm.NavigateTo("map"));
            await map.LoadCompletion;
            window.Width = 1280;
            window.Height = 720;
            HeadlessUi.Pump();
            var mapOnly = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PageExportButton" && b.IsEffectivelyVisible);
            Assert.Same(map.MapExport.ExportCommand, mapOnly.Command);
            Assert.Equal(ExportModule.Map, map.MapExport.Module);
            Assert.Equal(Loc.T("Export.Module.MapOnly.Tip"), ToolTip.GetTip(mapOnly));
            var history = At(window, window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HistoryPanel"));
            Assert.True(history.Inflate(0.5).Contains(At(window, mapOnly)), $"the button {At(window, mapOnly)} leaves the History panel {history}");
            HeadlessUi.SaveScreenshot(window, "page-export-map-1280");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private static Rect At(Visual window, Visual control) => new(control.TranslatePoint(new Point(0, 0), window)!.Value, control.Bounds.Size);
}
