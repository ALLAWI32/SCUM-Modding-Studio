using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views.Pages;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Vehicles studio beside the 3D view: Materials | Textures | Decals | Parts for a vehicle with a hover text each, in
/// every language; a weapon keeps its materials; each tab gives the 3D view its own mouse.
/// </summary>
[Collection(LanguageCollection.Name)] // switches the UI language: never in parallel with another test that reads localized texts
public sealed partial class VehicleStudioLayoutTests
{
    private static readonly string[] Tabs = ["StudioMaterialsTab", "StudioTexturesTab", "StudioDecalsTab", "StudioPartsTab"];

    [AvaloniaFixturesFact]
    public async Task AVehicleShowsTheFourStudioTabsAndAWeaponItsMaterialsOnly()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            Assert.True(await vehicles.OpenLooseFolderAsync(FixturePaths.OrigRoot));
            Assert.True(await vehicles.SelectAsync("BPC_WolfsWagen"));
            vehicles.ShowPreviewTab = true;
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.ShowStudioPanel && HeadlessUi.FindNamed<ToggleButton>(window, Tabs[0])?.IsEffectivelyVisible == true,
                TimeSpan.FromSeconds(30)));
            var preview = HeadlessUi.FindNamed<MeshPreview>(window, "ItemPreview")!;

            foreach (var language in Loc.Languages)
            {
                Loc.Instance.Language = language.Code;
                HeadlessUi.Pump();
                foreach (var name in Tabs)
                {
                    var tab = HeadlessUi.FindNamed<ToggleButton>(window, name)!;
                    Assert.True(tab.IsEffectivelyVisible, name);
                    var text = Assert.IsType<string>(tab.Content);
                    var tip = Assert.IsType<string>(ToolTip.GetTip(tab));
                    Assert.False(string.IsNullOrWhiteSpace(text) || text.StartsWith("Studio.", StringComparison.Ordinal), $"{language.Code} {name}: '{text}'");
                    Assert.False(string.IsNullOrWhiteSpace(tip) || tip.StartsWith("Studio.", StringComparison.Ordinal), $"{language.Code} {name} tip: '{tip}'");
                }

                Assert.DoesNotContain("Preview.Tool", vehicles.StudioToolHint, StringComparison.Ordinal);
            }

            Loc.Instance.Language = Loc.DefaultLanguage;
            HeadlessUi.Pump();

            // Each tab gives the 3D view its mouse: decals point at the body, parts get the gizmo.
            Assert.Equal(PreviewTool.Orbit, preview.Tool);
            Click(window, "StudioDecalsTab");
            HeadlessUi.Pump();
            Assert.Equal((StudioTab.Decals, PreviewTool.Surface), (vehicles.StudioTab, preview.Tool));
            Assert.False(HeadlessUi.FindNamed<ItemsControl>(window, "PaintList")!.IsEffectivelyVisible);
            Assert.True(HeadlessUi.FindNamed<ScumStudio.App.Views.Pages.VehicleStudioDecals>(window, "StudioDecalsPanel")!.IsEffectivelyVisible);
            Assert.NotNull(HeadlessUi.FindNamed<Button>(window, "ApplyDecalsButton"));
            Click(window, "StudioPartsTab");
            HeadlessUi.Pump();
            Assert.Equal(PreviewTool.Gizmo, preview.Tool);
            Assert.True(HeadlessUi.FindNamed<ScrollViewer>(window, "StudioPartsPanel")!.IsEffectivelyVisible);
            Assert.False(HeadlessUi.FindNamed<ScumStudio.App.Views.Pages.VehicleStudioDecals>(window, "StudioDecalsPanel")!.IsEffectivelyVisible);
            Click(window, "StudioTexturesTab");
            Assert.Equal(PreviewTool.Orbit, preview.Tool);
            Assert.True(HeadlessUi.FindNamed<ScrollViewer>(window, "StudioTexturesPanel")!.IsEffectivelyVisible);
            Assert.False(HeadlessUi.FindNamed<ScrollViewer>(window, "StudioPartsPanel")!.IsEffectivelyVisible);
            Click(window, "StudioPartsTab");
            Click(window, "StudioPartsTab"); // a click on the tab shown keeps it
            HeadlessUi.Pump();
            Assert.True(HeadlessUi.FindNamed<ToggleButton>(window, "StudioPartsTab")!.IsChecked);
            Click(window, "StudioMaterialsTab");
            HeadlessUi.Pump();
            Assert.Equal(PreviewTool.Orbit, preview.Tool);
            HeadlessUi.SaveScreenshot(window, "vehicles-studio-tabs");

            var weapons = (WeaponsPageViewModel)vm.NavigateTo("weapons")!;
            await weapons.LoadCompletion;
            Assert.True(await weapons.SelectAsync("Weapon_RPG7"));
            weapons.ShowPreviewTab = true;
            await weapons.PreviewCompletion;
            HeadlessUi.Pump();
            // A weapon gets Materials | Textures (its clone can wear a picture of its own); decals and parts stay vehicle-only.
            Assert.True(weapons.HasStudioTabs);
            Assert.Equal(PreviewTool.Orbit, weapons.StudioTool);
            Assert.True(weapons.ShowMaterialsPanel);
            var weaponView = HeadlessUi.Find<ModulePageView>(window).Single(v => ReferenceEquals(v.DataContext, weapons));
            Assert.True(HeadlessUi.FindNamed<Border>(weaponView, "StudioTabs")!.IsVisible);
            Assert.True(HeadlessUi.FindNamed<ToggleButton>(weaponView, "StudioTexturesTab")!.IsVisible);
            Assert.False(HeadlessUi.FindNamed<ToggleButton>(weaponView, "StudioDecalsTab")!.IsVisible);
            Assert.False(HeadlessUi.FindNamed<ToggleButton>(weaponView, "StudioPartsTab")!.IsVisible);
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
            window.Close();
            vm.Dispose();
        }
    }

    [Fact]
    public void TheStudioViewsUseTheThemesColoursOnly()
    {
        // FIELD MANUAL: colours come from the theme's tokens, never an inline hex value.
        var pages = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "..", "src", "ScumStudio.App", "Views", "Pages");
        foreach (var file in new[] { "VehicleStudioMaterials.axaml", "VehicleStudioTextures.axaml", "VehicleStudioDecals.axaml", "VehicleStudioParts.axaml", "ModulePageView.axaml" })
        {
            var text = File.ReadAllText(Path.Combine(pages, file));
            Assert.DoesNotMatch(InlineHex(), text);
        }
    }

    /// <summary>Clicks the middle of tab <paramref name="name"/> with the (headless) mouse.</summary>
    private static void Click(Window window, string name)
    {
        var tab = HeadlessUi.FindNamed<ToggleButton>(window, name)!;
        var at = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        HeadlessUi.Pump();
    }

    private static string Here([CallerFilePath] string path = "") => path;

    [GeneratedRegex("=\"#[0-9A-Fa-f]{3,8}\"")]
    private static partial Regex InlineHex();
}
