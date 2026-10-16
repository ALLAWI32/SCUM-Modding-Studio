using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;

namespace ScumStudio.App.Controls;

/// <summary>
/// Esc cancels a move, turn or scale (seviscache: "ESC to cancel a move like Blender"): the gizmo, a free drag and the
/// Shape, scale and legs handles. The object goes back where it was and nothing reaches the journal: a gizmo or free drag
/// only previews (<see cref="TransformDragged"/> comes at the release, and is not raised), a handle drag's live preview
/// is dropped by the page (<see cref="ShapeDragCancelled"/>, never <see cref="ShapeHandleReleased"/>). The mouse stays
/// captured until its button comes up, so that release does nothing here and nothing elsewhere.
/// </summary>
public sealed partial class LevelViewport
{
    /// <summary>Raised when Esc cancelled a drag (the page says so in the status line).</summary>
    public event EventHandler? DragCancelled;

    /// <summary>Raised when Esc cancelled a Shape, scale or legs handle drag: the page drops the shape it previewed.</summary>
    public event EventHandler? ShapeDragCancelled;

    /// <summary>True while a gizmo, free or handle drag is under way.</summary>
    public bool IsDragging => _dragging || _freeDragging || IsHandleDragging;

    /// <summary>
    /// Cancels the drag under way, if any: the selection goes back to where the drag began and nothing is journaled. A press
    /// on an object that has not moved yet is let go too (moving on then drags nothing). True when a drag was cancelled.
    /// </summary>
    public bool CancelDrag()
    {
        var handle = IsHandleDragging;
        var cancelled = handle || _dragging || _freeDragging;
        _leftDown = false; // a held press must not start a free drag after this
        if (!cancelled)
        {
            return false;
        }

        if (handle)
        {
            _shapeDragIndex = _scaleDragIndex = -1;
            _legDragging = false;
            _weldedTo = null;
            _weldTargets = [];
        }

        _dragging = _freeDragging = false;
        _dragAxis = GizmoAxis.None;
        _dragPreview = null; // the next frame draws the view model's transforms again
        _snappedTo = null;
        IsDragLabelVisible = false;
        DragLabel = string.Empty;
        UpdateCursor();
        RequestNextFrameRendering();
        if (handle)
        {
            ShapeDragCancelled?.Invoke(this, EventArgs.Empty);
        }

        DragCancelled?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>For tests without OpenGL (no pick pass): the left press just made grabbed the selection at its root, as the pick would have.</summary>
    internal void GrabSelectionForTests()
    {
        _grabId = SelectedId;
        _grabInstance = SelectedInstance;
        _grabScene = Scene;
        _grabPointGl = SelectedRootWorld is { } root ? UeToGl.Point(root.Translation) : default;
    }
}
