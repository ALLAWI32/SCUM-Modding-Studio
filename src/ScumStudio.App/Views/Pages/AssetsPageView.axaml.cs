using Avalonia.Controls;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>View of the Assets page.</summary>
public partial class AssetsPageView : UserControl
{
    /// <summary>Creates the view.</summary>
    public AssetsPageView()
    {
        InitializeComponent();
        // The tile grid cuts the list into rows itself (Avalonia has no virtualizing wrap panel), so it needs the width.
        PackageGridScroll.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && DataContext is AssetsPageViewModel vm)
            {
                vm.TileColumns = AssetsPageViewModel.ColumnsFor(e.NewSize.Width);
            }
        };
    }
}
