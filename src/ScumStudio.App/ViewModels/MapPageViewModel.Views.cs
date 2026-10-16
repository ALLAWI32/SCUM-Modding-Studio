using ScumStudio.App.Localization;
using ScumStudio.App.Services;

namespace ScumStudio.App.ViewModels;

/// <summary>Esc cancelling a drag in the viewport (seviscache #5): what the page does with it.</summary>
public sealed partial class MapPageViewModel
{
    /// <summary>
    /// Esc during a Shape, scale or legs handle drag: the shape it previewed goes and the journal's comes back (the
    /// transforms, bends and sliders), and the half-second journaling still waiting does nothing. Nothing is journaled.
    /// </summary>
    public void CancelShape()
    {
        _shapePending = false;
        _shapeVersion++;
        _legsDragFrom = null;
        IsShapeDragging = false;
        RefreshEdits();
    }

    /// <summary>Says in a short note that the move was cancelled (the object is back where it was).</summary>
    public void ReportDragCancelled() =>
        _services.Notifications.ShowCoalesced("map-drag-cancelled", ToastSeverity.Info, Loc.T("Map.DragCancelled"));
}
