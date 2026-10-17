using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>The Decals panel of the Vehicles 3D tab: the library, stickers, the clone's decals and their settings.</summary>
public partial class VehicleStudioDecals : UserControl
{
    /// <summary>How far (DIPs) a press on a design moves before it becomes a drag rather than a click.</summary>
    private const double DragStart = 6;

    private (Point At, DecalDesignItem Design, PointerPressedEventArgs Press)? _press;

    /// <summary>Creates the view.</summary>
    public VehicleStudioDecals()
    {
        InitializeComponent();
        // Owner: "press and hold on a tattoo in the library and drop it on the vehicle where I let go". A click still arms
        // the design for the next click on the car; a press that moves drags it (MeshPreview.DecalDragFormat).
        DecalDesignGrid.AddHandler(PointerPressedEvent, OnDesignPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        DecalDesignGrid.AddHandler(PointerMovedEvent, OnDesignMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        DecalDesignGrid.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnDesignPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = e.GetCurrentPoint(DecalDesignGrid).Properties.IsLeftButtonPressed && (e.Source as StyledElement)?.DataContext is DecalDesignItem design
            ? (e.GetPosition(DecalDesignGrid), design, e)
            : null;
    }

    private async void OnDesignMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || !e.GetCurrentPoint(DecalDesignGrid).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var moved = e.GetPosition(DecalDesignGrid) - press.At;
        if (Math.Abs(moved.X) < DragStart && Math.Abs(moved.Y) < DragStart)
        {
            return;
        }

        _press = null;
        var data = new DataObject();
        data.Set(MeshPreview.DecalDragFormat, press.Design);
        await DragDrop.DoDragDrop(press.Press, data, DragDropEffects.Copy);
    }
}
