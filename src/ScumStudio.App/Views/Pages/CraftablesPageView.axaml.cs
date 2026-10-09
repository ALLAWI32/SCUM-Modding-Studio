using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>Craftables page (see <see cref="ViewModels.CraftablesPageViewModel"/>).</summary>
public partial class CraftablesPageView : UserControl
{
    /// <summary>Creates the view.</summary>
    public CraftablesPageView()
    {
        InitializeComponent();
        // The gallery cuts its tiles into rows like the Assets page's grid, so it needs the width.
        CraftGalleryScroll.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && DataContext is CraftablesPageViewModel vm)
            {
                vm.Gallery.Columns = AssetsPageViewModel.ColumnsFor(e.NewSize.Width);
            }
        };

        // A click on a row shows its craftable even when it was selected already (the gallery open over it).
        CraftList.AddHandler(TappedEvent, (_, e) =>
        {
            if (DataContext is CraftablesPageViewModel vm && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            {
                vm.ShowSelected();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
