using Avalonia.Threading;

namespace ScumStudio.App.Services;

/// <summary>
/// Marshals work to the UI thread. View models use it (not <see cref="Dispatcher"/> directly) so they can be tested
/// without a running Avalonia dispatcher (<see cref="InlineUiDispatcher"/>).
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues <paramref name="action"/> on the UI thread.</summary>
    void Post(Action action);
}

/// <summary>Extensions for <see cref="IUiDispatcher"/>.</summary>
public static class UiDispatcherExtensions
{
    /// <summary>Runs <paramref name="action"/> now when on the UI thread, otherwise posts it.</summary>
    public static void Invoke(this IUiDispatcher dispatcher, Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Post(action);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread and completes when it ran (immediately when already on the UI
    /// thread). Exceptions thrown by the action fault the task.
    /// </summary>
    public static Task InvokeAsync(this IUiDispatcher dispatcher, Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread and completes with its result (immediately when already on the UI
    /// thread). Exceptions thrown by the work fault the task.
    /// </summary>
    public static Task<T> InvokeAsync<T>(this IUiDispatcher dispatcher, Func<T> work)
    {
        if (dispatcher.CheckAccess())
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }
}

/// <summary><see cref="IUiDispatcher"/> over Avalonia's <see cref="Dispatcher.UIThread"/>.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    /// <inheritdoc />
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>
/// Runs everything immediately on the calling thread (unit tests, tools), serialized by a lock so collections owned by
/// view models are never mutated by two threads at once. <see cref="CheckAccess"/> is false so that
/// <see cref="UiDispatcherExtensions.Invoke"/> always goes through the lock.
/// </summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    private readonly object _gate = new();

    /// <inheritdoc />
    public bool CheckAccess() => false;

    /// <inheritdoc />
    public void Post(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }
}
