using Avalonia;
using Avalonia.Controls;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Controls;

/// <summary>
/// An <see cref="Image"/> for a <see cref="ThumbnailItem"/> row or tile: asks the item for its picture while the control
/// is in the visual tree (a virtualized list realizes only the rows on screen) and releases it when the container is
/// recycled for another item or scrolled away. Bind <see cref="Image.Source"/> to <see cref="ThumbnailItem.Thumbnail"/>.
/// </summary>
public sealed class ThumbnailImage : Image
{
    private ThumbnailItem? _item;
    private bool _attached;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Track(DataContext as ThumbnailItem);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Track(null);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached)
        {
            Track(DataContext as ThumbnailItem);
        }
    }

    private void Track(ThumbnailItem? item)
    {
        if (ReferenceEquals(_item, item))
        {
            return;
        }

        _item?.ReleaseThumbnail();
        _item = item;
        _item?.RequestThumbnail();
    }
}
