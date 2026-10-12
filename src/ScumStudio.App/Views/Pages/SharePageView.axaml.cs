using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>View of the Share card ("Share your mod") and of its capture bar.</summary>
public partial class SharePageView : UserControl
{
    /// <summary>Creates the view.</summary>
    public SharePageView()
    {
        InitializeComponent();
        // The name is kept when the box is left or Enter is pressed, not at every key (each save reloads the settings' readers).
        DisplayNameBox.LostFocus += (_, _) => (DataContext as SharePageViewModel)?.SaveDisplayName();
        DisplayNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                (DataContext as SharePageViewModel)?.SaveDisplayName();
            }
        };
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachCapture();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachCapture();
    }

    /// <summary>Gives the view model the Map capture of this window (it finds the viewport when asked).</summary>
    private void AttachCapture()
    {
        if (DataContext is SharePageViewModel { Capture: null } vm && TopLevel.GetTopLevel(this) is not null)
        {
            vm.Capture = new ViewportCapture(() => TopLevel.GetTopLevel(this));
        }
    }
}
