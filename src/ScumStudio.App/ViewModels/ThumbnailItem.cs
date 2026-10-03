using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// A list row or tile whose small picture exists only while a <see cref="Controls.ThumbnailImage"/> shows it: the first
/// visible control asks for it, the last one leaving the screen cancels the work and drops the bitmap (the disk cache
/// makes the next visit cheap), so scrolling through thousands of packages decodes only what is on screen.
/// </summary>
public abstract class ThumbnailItem : ObservableObject
{
    private Bitmap? _thumbnail;
    private CancellationTokenSource? _cts;
    private int _shown;

    /// <summary>The picture, or null (the class icon is shown instead).</summary>
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    /// <summary>A control started showing this item (UI thread).</summary>
    public void RequestThumbnail()
    {
        if (++_shown == 1 && _thumbnail is null && _cts is not { IsCancellationRequested: false })
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = LoadAsync(cts);
        }
    }

    /// <summary>A control stopped showing this item (UI thread).</summary>
    public void ReleaseThumbnail()
    {
        if (--_shown > 0)
        {
            return;
        }

        _shown = 0;
        _cts?.Cancel();
        _cts = null;
        Thumbnail = null;
    }

    /// <summary>Makes the picture on a worker; null when the item has none.</summary>
    protected abstract Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken);

    private async Task LoadAsync(CancellationTokenSource cts)
    {
        try
        {
            var bitmap = await LoadThumbnailAsync(cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
            {
                Thumbnail = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No picture: the class icon stays.
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }
        }
    }
}
