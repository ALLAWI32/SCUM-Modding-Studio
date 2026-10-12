using System.Runtime.InteropServices;

namespace ScumStudio.App.Services;

/// <summary>
/// One app window per user session (zarkos6175: "two copies of the app open, project not saved"): the first window owns
/// a named mutex, a second start asks it to come to the front through a named event and quits once it answers. Windows
/// only (named events are not supported elsewhere, where every start owns and the project lock protects).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const int AnyProcess = -1; // ASFW_ANY

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private readonly EventWaitHandle? _activated;
    private RegisteredWaitHandle? _listening;
    private int _disposed;

    private SingleInstance(Mutex? mutex, EventWaitHandle? activate, EventWaitHandle? activated)
    {
        _mutex = mutex;
        _activate = activate;
        _activated = activated;
    }

    /// <summary>
    /// Owns <paramref name="name"/> for this process, waiting up to <paramref name="wait"/> for the owner to let go (after
    /// an update the old version is still closing); null when another process owns it. A mutex left by a process that
    /// died is taken over; one this start may not open (made by a window running as administrator) is left alone.
    /// </summary>
    public static SingleInstance? Acquire(string name, TimeSpan wait)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SingleInstance(null, null, null);
        }

        Mutex mutex;
        bool owned;
        try
        {
            mutex = new Mutex(true, @"Local\" + name, out var createdNew);
            try
            {
                // An existing mutex is ours only once taken: owned elsewhere it stays theirs, released or abandoned it is taken here.
                owned = createdNew || mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return new SingleInstance(null, null, null); // cannot reach that window: this one runs on its own
        }

        if (!owned)
        {
            mutex.Dispose();
            return null;
        }

        // Made now, not at Listen: a start that signals before the window is up is answered once it is.
        var activate = OpenEvent(name, "Activate");
        var activated = OpenEvent(name, "Activated");
        if (activate is null || activated is null)
        {
            activate?.Dispose();
            activated?.Dispose();
            return new SingleInstance(mutex, null, null);
        }

        return new SingleInstance(mutex, activate, activated);
    }

    /// <summary>Asks the owner of <paramref name="name"/> to show its window; true once it answered within <paramref name="ackWait"/>.</summary>
    public static bool SignalOwner(string name, TimeSpan ackWait)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var activate = OpenEvent(name, "Activate");
        using var activated = OpenEvent(name, "Activated");
        if (activate is null || activated is null)
        {
            return false;
        }

        activated.Reset();
        AllowSetForegroundWindow(AnyProcess); // this start has the focus (the user just ran it) and may hand it on
        activate.Set();
        return activated.WaitOne(ackWait);
    }

    /// <summary>
    /// Runs <paramref name="activate"/> (on a pool thread) each time another start signals; it returns whether the window
    /// will come forward, and only then is the other start told to quit (a window that is closing says no).
    /// </summary>
    public void Listen(Func<bool> activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        if (_activate is null || _listening is not null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _listening = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
        {
            try
            {
                if (Volatile.Read(ref _disposed) == 0 && activate())
                {
                    _activated!.Set();
                }
            }
            catch (ObjectDisposedException)
            {
                // closed while answering
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Stops answering and lets go of the mutex (on the thread that took it: the app's main thread).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _listening?.Unregister(null);
        _activate?.Dispose();
        _activated?.Dispose();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // another thread: the mutex is let go (abandoned) when the process ends
            }

            _mutex.Dispose();
        }
    }

    private static EventWaitHandle? OpenEvent(string name, string suffix)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + "." + suffix);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
