namespace ScumStudio.Core.IO;

/// <summary>
/// Crash-safe file replacement: data is written to a temporary file in the same folder, flushed to disk and then
/// renamed over the target, so readers see either the old or the new content, never a torn file.
/// </summary>
public static class AtomicFile
{
    private const int ReplaceAttempts = 5;

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="data"/>.</summary>
    /// <param name="path">Target file; its folder is created when missing.</param>
    /// <param name="data">New content.</param>
    /// <param name="ownerOnly">On Unix, create the file with mode 0600 (owner read/write only). Ignored on Windows,
    /// where files under the user profile already inherit a per-user ACL.</param>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data, bool ownerOnly = false)
    {
        var (target, temp) = Prepare(path);
        try
        {
            using (var stream = new FileStream(temp, CreateOptions(ownerOnly)))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            Commit(temp, target, ownerOnly);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Asynchronously and atomically replaces <paramref name="path"/> with <paramref name="data"/>.</summary>
    /// <param name="path">Target file; its folder is created when missing.</param>
    /// <param name="data">New content.</param>
    /// <param name="ownerOnly">On Unix, create the file with mode 0600; ignored on Windows.</param>
    /// <param name="cancellationToken">Cancels before the rename; the target is then unchanged.</param>
    public static async Task WriteAllBytesAsync(
        string path, ReadOnlyMemory<byte> data, bool ownerOnly = false, CancellationToken cancellationToken = default)
    {
        var (target, temp) = Prepare(path);
        try
        {
            var options = CreateOptions(ownerOnly);
            options.Options = FileOptions.Asynchronous;
            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Commit(temp, target, ownerOnly);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Restricts an existing file to owner read/write (0600) on Unix; no-op on Windows.</summary>
    public static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static (string Target, string Temp) Prepare(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("The path has no parent folder.", nameof(path));
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        return (target, temp);
    }

    private static FileStreamOptions CreateOptions(bool ownerOnly)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    private static void Commit(string temp, string target, bool ownerOnly)
    {
        if (ownerOnly)
        {
            RestrictToOwner(temp);
        }

        // File.Move(overwrite) is rename(2) on Unix and MoveFileEx(REPLACE_EXISTING) on Windows. On Windows a virus
        // scanner or indexer may hold the target open for a moment, so retry briefly.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < ReplaceAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
