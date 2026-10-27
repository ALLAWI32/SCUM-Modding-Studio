using Avalonia.Controls;
using ScumStudio.App.Views;

namespace ScumStudio.App.Views.Pages;

/// <summary>The Server page.</summary>
public partial class ServerPageView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ServerPageView()
    {
        InitializeComponent();
    }

    private void OnGuideClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new Ue4ssGuideWindow();
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
