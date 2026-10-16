using Avalonia.Controls;
using Avalonia.Interactivity;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Spawns;

namespace ScumStudio.App.Views.Pages;

/// <summary>The Spawns page (see <see cref="ViewModels.SpawnsPageViewModel"/>).</summary>
public partial class SpawnsPageView : UserControl
{
    private SpawnsPageViewModel? _page;

    /// <summary>Creates the view.</summary>
    public SpawnsPageView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null)
        {
            _page.FocusRequested -= OnFocusRequested;
        }

        _page = DataContext as SpawnsPageViewModel;
        if (_page is not null)
        {
            _page.FocusRequested += OnFocusRequested;
        }
    }

    private void OnFitClick(object? sender, RoutedEventArgs e) => this.FindControl<SpawnMap>("SpawnMap")?.ZoomToFit();

    private void OnFocusRequested(object? sender, SpawnArea area) => this.FindControl<SpawnMap>("SpawnMap")?.ZoomTo(area);
}
