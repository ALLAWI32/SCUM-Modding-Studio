namespace ScumStudio.Level.Projects;

/// <summary>The project is already open for editing in another ScumStudio window or process.</summary>
/// <remarks><see cref="Exception.HResult"/> is 0x80070020 (ERROR_SHARING_VIOLATION) on every OS, so callers can tell it apart.</remarks>
public sealed class ProjectLockedException : IOException
{
    /// <summary>HResult of every <see cref="ProjectLockedException"/>.</summary>
    public const int LockedHResult = unchecked((int)0x80070020);

    /// <summary>Creates the exception for the project in <paramref name="directory"/>.</summary>
    public ProjectLockedException(string directory, Exception? innerException = null)
        : base($"The project in {directory} is open in another ScumStudio window or process.", innerException)
    {
        Directory = directory;
        HResult = LockedHResult;
    }

    /// <summary>Full path of the project folder.</summary>
    public string Directory { get; }
}

/// <summary>
/// One writer per project folder: an open exclusive handle on <c>&lt;project&gt;/.lock</c> (share-none on Windows,
/// flock(LOCK_EX) on Unix, where the journal's own share mode locks nothing). The file stays after exit; only the open
/// handle locks, so a crash leaves no stale lock.
/// </summary>
internal sealed class ProjectLock : IDisposable
{
    /// <summary>Name of the lock file in the project folder.</summary>
    public const string FileName = ".lock";

    private readonly FileStream _handle;

    private ProjectLock(FileStream handle) => _handle = handle;

    /// <summary>Locks the existing project folder <paramref name="directory"/>.</summary>
    /// <exception cref="ProjectLockedException">Another window or process holds the lock.</exception>
    public static ProjectLock Acquire(string directory)
    {
        var path = Path.Combine(directory, FileName);
        FileStream handle;
        try
        {
            handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        }
        catch (IOException ex) when (IsHeld(ex))
        {
            throw new ProjectLockedException(directory, ex);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cosmetic: the handle locks either way.
            }
        }

        return new ProjectLock(handle);
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();

    // Windows: ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION. Unix: .NET reports a refused flock with its errno as the
    // HResult, EWOULDBLOCK: 11 on Linux and 35 on macOS (each is EDEADLK on the other). Disk and path errors pass through.
    private static bool IsHeld(IOException ex) => OperatingSystem.IsWindows()
        ? ex.HResult is ProjectLockedException.LockedHResult or unchecked((int)0x80070021)
        : ex.HResult is 11 or 35;
}
