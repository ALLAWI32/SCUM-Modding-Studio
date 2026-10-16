using Avalonia.Controls;

namespace ScumStudio.App.Views.Pages;

/// <summary>
/// The Textures panel of the Vehicles 3D tab (the texture slots of the selected clone's paint materials: Replace, Save PNG,
/// Game texture). Hosted by the page; its data context is the page's view model.
/// </summary>
public partial class VehicleStudioTextures : UserControl
{
    /// <summary>Creates the panel.</summary>
    public VehicleStudioTextures()
    {
        InitializeComponent();
    }
}
