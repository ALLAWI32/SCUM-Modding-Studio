using Avalonia.Controls;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>The Craftables page's ingredient picker (see <see cref="IngredientPickerViewModel"/>).</summary>
public partial class CraftIngredientPicker : UserControl
{
    /// <summary>Creates the view.</summary>
    public CraftIngredientPicker()
    {
        InitializeComponent();
        // The tiles are cut into rows like the gallery's grid, so the picker needs the width.
        CraftPickerScroll.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && DataContext is IngredientPickerViewModel vm)
            {
                vm.Columns = AssetsPageViewModel.ColumnsFor(e.NewSize.Width);
            }
        };
    }
}
