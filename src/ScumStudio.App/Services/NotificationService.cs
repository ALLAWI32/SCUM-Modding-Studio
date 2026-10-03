using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Services;

/// <summary>Severity of a toast notification.</summary>
public enum ToastSeverity
{
    /// <summary>Neutral information.</summary>
    Info,

    /// <summary>A finished operation.</summary>
    Success,

    /// <summary>Something the user should look at.</summary>
    Warning,

    /// <summary>An operation failed.</summary>
    Error,
}

/// <summary>
/// Short-lived notifications. The main window shows them as compact chips in the status bar's notification tray, so they
/// never cover a working panel; every notification is also written to the Console (log) so nothing is lost when a chip
/// times out. Thread-safe: may be called from worker threads; changes to <see cref="Toasts"/> happen on the UI thread.
/// </summary>
public sealed class NotificationService
{
    /// <summary>Maximum number of toasts kept on screen.</summary>
    public const int MaxVisible = 3;

    /// <summary>Calls of <see cref="ShowCoalesced"/> with the same group within this window update one toast.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeSpan? _autoDismiss;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, (ToastViewModel Toast, DateTime At, int Count)> _groups = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the service; <paramref name="autoDismiss"/> null keeps toasts until closed. Success toasts last half of
    /// <paramref name="autoDismiss"/>, errors twice as long.
    /// </summary>
    public NotificationService(IUiDispatcher dispatcher, TimeSpan? autoDismiss, ILogger? logger = null)
    {
        _dispatcher = dispatcher;
        _autoDismiss = autoDismiss;
        _logger = logger;
    }

    /// <summary>Visible toasts, newest last.</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>Shows a toast.</summary>
    public ToastViewModel Show(ToastSeverity severity, string title, string? message = null)
    {
        var toast = Create(severity, title, message);
        _dispatcher.Invoke(() =>
        {
            Toasts.Add(toast);
            Trim();
        });

        ScheduleDismiss(toast);
        return toast;
    }

    /// <summary>
    /// Shows a toast that replaces the previous toast of the same <paramref name="group"/> when that one is still on
    /// screen and less than <see cref="CoalesceWindow"/> old, so a burst (e.g. AI tool calls) is one toast, not a stack.
    /// The replacement's message ends with how many more notifications it stands for.
    /// </summary>
    public void ShowCoalesced(string group, ToastSeverity severity, string title, string? message = null) =>
        _dispatcher.Invoke(() =>
        {
            var now = DateTime.UtcNow;
            var count = 1;
            var index = -1;
            if (_groups.TryGetValue(group, out var previous) && now - previous.At < CoalesceWindow)
            {
                index = Toasts.IndexOf(previous.Toast);
                if (index >= 0)
                {
                    count = previous.Count + 1;
                }
            }

            var detail = count > 1 ? $"{message}{(string.IsNullOrEmpty(message) ? string.Empty : " · ")}+{count - 1} more" : message;
            var shown = Create(severity, title, detail, log: false);
            Log(severity, title, message);
            if (index >= 0)
            {
                Toasts[index] = shown;
            }
            else
            {
                Toasts.Add(shown);
                Trim();
            }

            _groups[group] = (shown, now, count);
            ScheduleDismiss(shown);
        });

    /// <summary>Shows an information toast.</summary>
    public ToastViewModel Info(string title, string? message = null) => Show(ToastSeverity.Info, title, message);

    /// <summary>Shows a success toast.</summary>
    public ToastViewModel Success(string title, string? message = null) => Show(ToastSeverity.Success, title, message);

    /// <summary>Shows a warning toast.</summary>
    public ToastViewModel Warning(string title, string? message = null) => Show(ToastSeverity.Warning, title, message);

    /// <summary>Shows an error toast.</summary>
    public ToastViewModel Error(string title, string? message = null) => Show(ToastSeverity.Error, title, message);

    /// <summary>Removes a toast.</summary>
    public void Dismiss(ToastViewModel toast) => _dispatcher.Invoke(() => Toasts.Remove(toast));

    /// <summary>Removes every toast.</summary>
    public void DismissAll() => _dispatcher.Invoke(Toasts.Clear);

    /// <summary>How long a toast of <paramref name="severity"/> stays, or null when toasts stay until closed.</summary>
    public TimeSpan? LifetimeOf(ToastSeverity severity) => _autoDismiss is not { } delay
        ? null
        : severity switch
        {
            ToastSeverity.Success => delay / 2,
            ToastSeverity.Error => delay * 2,
            _ => delay,
        };

    private ToastViewModel Create(ToastSeverity severity, string title, string? message, bool log = true)
    {
        var toast = new ToastViewModel(severity, title, message ?? string.Empty);
        toast.CloseCommand = new RelayCommand(() => Dismiss(toast));
        if (log)
        {
            Log(severity, title, message);
        }

        return toast;
    }

    private void Log(ToastSeverity severity, string title, string? message)
    {
        // Information level on purpose: failures are already logged as errors by whoever raised them, and the
        // Console's warning/error counters must not count a problem twice.
        _logger?.LogInformation("[{Severity}] {Title}{Separator}{Message}", severity.ToString().ToLowerInvariant(), title,
            string.IsNullOrWhiteSpace(message) ? string.Empty : " — ", message ?? string.Empty);
    }

    private void Trim()
    {
        while (Toasts.Count > MaxVisible)
        {
            Toasts.RemoveAt(0);
        }
    }

    private void ScheduleDismiss(ToastViewModel toast)
    {
        if (LifetimeOf(toast.Severity) is { } lifetime)
        {
            _ = Task.Delay(lifetime).ContinueWith(_ => Dismiss(toast), TaskScheduler.Default);
        }
    }
}
