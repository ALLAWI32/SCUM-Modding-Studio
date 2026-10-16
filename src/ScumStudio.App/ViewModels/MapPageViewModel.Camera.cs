using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;
using ScumStudio.Rendering.Cameras;

namespace ScumStudio.App.ViewModels;

/// <summary>The camera's near clip (F15, seviscache: "the camera clips the mesh" in tight rooms).</summary>
public sealed partial class MapPageViewModel
{
    /// <summary>How close to the camera the 3D view still draws, in centimetres (Settings › Near clip, Ctrl+Shift+N).</summary>
    [ObservableProperty]
    private float _nearClip = FlyCamera.DefaultMapNear;

    /// <summary>Ctrl+Shift+N: the next near clip distance (1, 2, 5, 10, 20, 50 cm), kept in the settings.</summary>
    public void CycleNearClip()
    {
        var next = NearClipOption.Next(NearClip);
        NearClip = next.Centimetres;
        _services.UpdateSettings(s => s with { Ui = s.Ui with { NearClipCentimetres = next.Centimetres } });
        _services.Notifications.Info(Loc.F("Map.NearClip.Now", next), Loc.T("Settings.NearClip.Tip"));
    }
}
