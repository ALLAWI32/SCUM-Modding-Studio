using Avalonia.VisualTree;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Prefabs menu and the History right-click as the owner sees them (screenshots with <c>SCUMSTUDIO_SCREENSHOTS</c>):
/// a saved house is listed, placed, and Go to moves the camera to it; Settings shows the library. Real game files only.
/// </summary>
public sealed class PrefabsMenuRealTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ThePrefabsMenuSavesAndPlacesAndGoToMovesTheCamera()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "PrefabMenu");
            await map.LoadLevelsAsync(["/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01"]);
            HeadlessUi.Pump();

            var button = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "PrefabsButton");
            Assert.True(button.IsVisible);
            button.Flyout!.ShowAt(button);
            HeadlessUi.Pump();
            var panel = Assert.IsType<Avalonia.Controls.StackPanel>(((Avalonia.Controls.Flyout)button.Flyout).Content);
            Assert.Equal("PrefabsPanel", panel.Name);
            Assert.Contains(panel.GetVisualDescendants().OfType<Avalonia.Controls.Button>(), b => b.Name == "SavePrefabButton");
            Assert.Contains(panel.GetVisualDescendants().OfType<Avalonia.Controls.Button>(), b => b.Name == "ImportPrefabButton");
            Assert.False(map.HasPrefabs);

            var house = map.AllActors.First(a => a.Actor.Kind == ActorKind.Blueprint && a.Name.Contains("House", StringComparison.OrdinalIgnoreCase));
            map.SelectedActor = house;
            map.PrefabName = "House";
            HeadlessUi.Pump();
            Assert.True(map.SavePrefabCommand.CanExecute(null));
            map.SavePrefabCommand.Execute(null);
            HeadlessUi.Pump();
            var row = Assert.Single(map.Prefabs);
            Assert.Equal("House", row.Name);

            var aim = house.Actor.WorldTransform.Translation + new FVector(8000f, 0f, 0f);
            map.AimPointProvider = () => aim;
            row.Place.Execute(null);
            HeadlessUi.Pump();
            Assert.Single(ctx.Services.Projects.History);
            HeadlessUi.SaveScreenshot(window, "map-prefabs-menu");
            button.Flyout.Hide();
            HeadlessUi.Pump();

            // History › Go to: the camera leaves where it was and the placed house is the selection.
            var viewport = HeadlessUi.FindNamed<LevelViewport>(window, "Viewport3d")!;
            map.SelectedActor = null;
            HeadlessUi.Pump();
            var before = viewport.CameraUe;
            map.SelectedHistoryItem = ctx.Services.Projects.History[0];
            Assert.True(map.GoToEditCommand.CanExecute(null));
            await map.GoToEditCommand.ExecuteAsync(null);
            HeadlessUi.Pump();
            Assert.True(map.SelectedActor?.IsAdded, "the placed house is selected");
            Assert.NotEqual(before, viewport.CameraUe);
            Assert.True((viewport.CameraUe - aim).Size() < 3000f, $"camera {viewport.CameraUe} near {aim}");
            HeadlessUi.SaveScreenshot(window, "map-history-goto");

            // Settings: the Prefabs card with its folder and Import.
            Assert.IsType<SettingsPageViewModel>(vm.NavigateTo("settings"));
            HeadlessUi.Pump();
            var card = HeadlessUi.FindNamed<Avalonia.Controls.Border>(window, "PrefabsCard");
            Assert.True(card is { IsVisible: true });
            Assert.NotNull(HeadlessUi.FindNamed<Avalonia.Controls.Button>(window, "SettingsImportPrefabButton"));
            Assert.Contains(ctx.Services.Prefabs.Folder, HeadlessUi.Find<Avalonia.Controls.SelectableTextBlock>(window).Select(t => t.Text));
            HeadlessUi.SaveScreenshot(window, "settings-prefabs");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
