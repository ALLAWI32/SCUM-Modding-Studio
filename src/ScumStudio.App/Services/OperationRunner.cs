using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ScumStudio.Core.Abstractions;

namespace ScumStudio.App.Services;

/// <summary>
/// Runs long operations off the UI thread with progress in the status bar. Exceptions never escape: they are logged
/// and shown as an error toast, and the call returns false. Several operations may run at once; the status bar shows
/// the latest one.
/// </summary>
public sealed partial class OperationRunner : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;
    private readonly NotificationService _notifications;
    private readonly ILogger _logger;
    private int _running;
    private int _generation;

    /// <summary>Creates the runner.</summary>
    public OperationRunner(IUiDispatcher dispatcher, NotificationService notifications, ILogger logger)
    {
        _dispatcher = dispatcher;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>True while at least one operation runs.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Status line text ("Ready" when idle).</summary>
    [ObservableProperty]
    private string _statusText = Localization.Loc.T("Status.Ready");

    /// <summary>Progress of the current step in percent (0-100).</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>True when the current step has no known total.</summary>
    [ObservableProperty]
    private bool _isIndeterminate = true;

    /// <summary>
    /// Runs <paramref name="work"/> and returns true when it completed. <paramref name="work"/> receives a progress sink
    /// (safe to call from any thread) and a cancellation token.
    /// </summary>
    public async Task<bool> RunAsync(string title, Func<IProgressSink, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        var (ok, _) = await RunAsync<object?>(title, async (p, ct) =>
        {
            await work(p, ct).ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(true);
        return ok;
    }

    /// <summary>Runs <paramref name="work"/> and returns (true, result) when it completed, (false, default) otherwise.</summary>
    public async Task<(bool Ok, T? Result)> RunAsync<T>(string title, Func<IProgressSink, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var generation = Begin(title);
        var sink = ProgressSink.Create(update => _dispatcher.Invoke(() => OnProgress(generation, update)))
            .Throttle(TimeSpan.FromMilliseconds(100));
        try
        {
            var result = await Task.Run(() => work(sink, cancellationToken), cancellationToken).ConfigureAwait(true);
            End(generation, Localization.Loc.F("Status.Done", title));
            return (true, result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("{Operation} was cancelled.", title);
            End(generation, Localization.Loc.F("Status.Cancelled", title));
            return (false, default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} failed: {Message}", title, ex.Message);
            _notifications.Error(Localization.Loc.F("Status.FailedTitle", title), ex.Message);
            End(generation, Localization.Loc.F("Status.Failed", title));
            return (false, default);
        }
    }

    /// <summary>Shows "Ready" in the new UI language when idle.</summary>
    public void OnLanguageChanged()
    {
        if (!IsBusy)
        {
            StatusText = Localization.Loc.T("Status.Ready");
        }
    }

    private int Begin(string title)
    {
        var generation = Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _running);
        _dispatcher.Invoke(() =>
        {
            IsBusy = true;
            StatusText = title + "...";
            IsIndeterminate = true;
            Progress = 0;
        });
        return generation;
    }

    private void OnProgress(int generation, ProgressUpdate update)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        StatusText = update.IsIndeterminate ? update.Step + "..." : $"{update.Step} ({update.Current:N0} / {update.Total:N0})";
        IsIndeterminate = update.IsIndeterminate;
        Progress = (update.Fraction ?? 0) * 100;
    }

    private void End(int generation, string text)
    {
        var remaining = Interlocked.Decrement(ref _running);
        _dispatcher.Invoke(() =>
        {
            if (remaining <= 0)
            {
                IsBusy = false;
                IsIndeterminate = false;
                Progress = 0;
            }

            if (generation == Volatile.Read(ref _generation) || remaining <= 0)
            {
                StatusText = text;
            }
        });
    }
}
