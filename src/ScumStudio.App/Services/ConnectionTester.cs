using Microsoft.Extensions.Logging;
using ScumStudio.Core.Games;
using ScumStudio.Pak.Reading;

namespace ScumStudio.App.Services;

/// <summary>Result of <see cref="ConnectionTester.Test"/>.</summary>
/// <param name="Folder">The Paks folder that was opened.</param>
/// <param name="PakCount">Containers found.</param>
/// <param name="MountedCount">Containers mounted.</param>
/// <param name="LockedCount">Encrypted containers that could not be mounted (no or wrong key).</param>
/// <param name="EntryCount">Distinct files readable through the mounted containers.</param>
public sealed record ConnectionTestResult(string Folder, int PakCount, int MountedCount, int LockedCount, int EntryCount)
{
    /// <summary>True when at least one pak mounted and none is locked.</summary>
    public bool IsHealthy => MountedCount > 0 && LockedCount == 0;

    /// <summary>One-line summary for the UI.</summary>
    public string Summary =>
        PakCount == 0
            ? Localization.Loc.T("Setup.NoPaksHere")
            : Localization.Loc.F("Setup.TestSummary", MountedCount, PakCount, EntryCount) +
              (LockedCount > 0 ? Localization.Loc.F("Setup.TestLocked", LockedCount) : ".");
}

/// <summary>Opens a Paks folder with ScumStudio.Pak to verify the folder and key ("Test connection").</summary>
public static class ConnectionTester
{
    /// <summary>
    /// Resolves <paramref name="folder"/> to a Paks folder (install root, SCUM.exe, Content, ... accepted), mounts its paks
    /// with <paramref name="aesKey"/> and counts them. Runs synchronously; call it from a worker thread.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public static ConnectionTestResult Test(string folder, string? aesKey, ILogger? logger, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var resolved = GameLocator.ResolvePaksFolder(folder) ?? Path.GetFullPath(folder);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Folder not found: {resolved}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var source = PakFileSource.OpenDirectory(resolved, new PakFileSourceOptions { AesKey = aesKey, Logger = logger });
        var archives = source.Archives;
        return new ConnectionTestResult(
            resolved,
            archives.Count,
            archives.Count(a => a.IsMounted),
            archives.Count(a => !a.IsMounted && a.IsEncrypted),
            source.Count);
    }
}
