using Avalonia.Controls;
using ScumStudio.App.Views;

namespace ScumStudio.App.Views.Pages;

/// <summary>View of the ProjectsPage page.</summary>
public partial class ProjectsPageView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ProjectsPageView()
    {
        InitializeComponent();
    }

    private void OnDumpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.ProjectsPageViewModel vm)
        {
            var window = new DumpWindow { DataContext = vm.CreateDump() };
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }
    }
}
