using System.Text;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Pak.Reading;
using ScumStudio.Pak.Server;

namespace ScumStudio.Level.Export;

/// <summary>
/// The server pak's sound events (D12; audit C2, R2 B2): the dedicated server's cook stores every Wwise event
/// (<c>AkAudioEvent</c>) as a 12-byte stub and dies with "Serial size mismatch: Got 12, Expected N" on a client-cooked
/// one. Imported mods (a car mod's cloned events) and any clone carry client-cooked events, so for the server pak every
/// staged event is replaced by the stock server stub (<see cref="AkAudioEventStubBuilder"/>), read from the server's own
/// game paks (never the mods over them). Without a stub the server pak is refused: an event left as it is crashes the
/// server on every start.
/// </summary>
public static class ServerEventStubs
{
    private static readonly byte[] EventClassName = Encoding.ASCII.GetBytes("AkAudioEvent");

    /// <summary>
    /// Reads the stub from the stock server cook in <paramref name="serverPaks"/>: only the <c>*-WindowsServer</c> paks
    /// directly in that folder, so no mod (a <c>~mods</c> folder, an own pak) can stand in for it.
    /// </summary>
    /// <exception cref="ServerEventStubException">The folder holds no server cook, or its stub cannot be read or is not 12 bytes.</exception>
    public static AkAudioEventStubBuilder FromServerPaks(string serverPaks, string? aesKey, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(serverPaks) || !Directory.Exists(serverPaks))
        {
            throw new ServerEventStubException($"the server's game paks folder '{serverPaks}' does not exist");
        }

        static bool IsServerCook(string name) => name.Contains("-WindowsServer", StringComparison.OrdinalIgnoreCase)
            && name.StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("pakchunk9", StringComparison.OrdinalIgnoreCase);
        if (!Directory.EnumerateFiles(serverPaks, "*.pak").Any(f => IsServerCook(Path.GetFileName(f))))
        {
            throw new ServerEventStubException($"{serverPaks} holds no server game paks (pakchunk0*-WindowsServer.pak)");
        }

        try
        {
            using var paks = PakFileSource.OpenDirectory(serverPaks, new PakFileSourceOptions { AesKey = aesKey, Logger = logger, PakFileFilter = IsServerCook });
            return AkAudioEventStubBuilder.FromServerTree(paks, logger: logger);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            throw new ServerEventStubException($"the server stub {AkAudioEventStubBuilder.DefaultStubGamePath} could not be read from {serverPaks} ({ex.Message})");
        }
    }

    /// <summary>Reads the stub from the export's own catalog (the server cook it was opened on); a client cook's event is refused.</summary>
    /// <exception cref="ServerEventStubException">The catalog has no such package or it is no 12-byte server stub.</exception>
    public static AkAudioEventStubBuilder FromCatalog(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var path = AkAudioEventStubBuilder.DefaultStubGamePath;
        if (!catalog.TryGetPackageFile(path, out var file))
        {
            throw new ServerEventStubException($"{catalog.DisplayName} has no {path}");
        }

        try
        {
            var (uasset, uexp, _) = ProjectExporter.ReadPackageFiles(catalog, file);
            var builder = new AkAudioEventStubBuilder(uasset, uexp);
            _ = builder.CreateStub(path); // proves it is a 12-byte server stub, not the client's event
            return builder;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or (InvalidOperationException and not ServerEventStubException))
        {
            throw new ServerEventStubException($"{path} in {catalog.DisplayName} is not the server's 12-byte stub ({ex.Message})");
        }
    }

    /// <summary>
    /// The <c>AkAudioEvent</c> packages staged under <paramref name="staging"/> (the whole tree, plugins included), as
    /// (header file, game path, export size). The game path is the package's own (from its name table) when it has one.
    /// </summary>
    public static IReadOnlyList<(string File, string GamePath, long Size)> FindEvents(string staging, string projectName = AssetPaths.DefaultProjectName)
    {
        var found = new List<(string, string, long)>();
        if (!Directory.Exists(staging))
        {
            return found;
        }

        foreach (var uasset in Directory.EnumerateFiles(staging, "*.uasset", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var header = File.ReadAllBytes(uasset);
            if (header.AsSpan().IndexOf(EventClassName) < 0)
            {
                continue; // no event names this class: most staged files are skipped without parsing
            }

            var uexpPath = Path.ChangeExtension(uasset, ".uexp");
            var uexp = File.Exists(uexpPath) ? File.ReadAllBytes(uexpPath) : [];
            CookedPackage package;
            try
            {
                package = CookedPackage.Parse(header, uexp, null, uasset);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
            {
                continue;
            }

            var index = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.GetExportClassName(i) == "AkAudioEvent", -1);
            if (index < 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(staging, uasset).Replace('\\', '/');
            var byPath = AssetPaths.ToPackagePath(relative, projectName);
            var shortName = byPath[(byPath.LastIndexOf('/') + 1)..];
            var gamePath = package.Names.FirstOrDefault(n => n.StartsWith('/') && n.EndsWith("/" + shortName, StringComparison.Ordinal)) ?? byPath;
            found.Add((uasset, gamePath, package.Exports[index].SerialSize));
        }

        return found;
    }

    /// <summary>
    /// Replaces every staged event of <paramref name="staging"/> that is not already a server stub by the stub from
    /// <paramref name="stubs"/> (called only when there is one to replace). Returns the replaced game paths.
    /// </summary>
    /// <exception cref="ServerEventStubException">There are events to replace and no stub could be read.</exception>
    public static async Task<IReadOnlyList<string>> ApplyAsync(string staging, Func<AkAudioEventStubBuilder> stubs, string projectName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stubs);
        var events = FindEvents(staging, projectName).Where(e => e.Size != AkAudioEventStubBuilder.StubExportSize).ToList();
        if (events.Count == 0)
        {
            return [];
        }

        AkAudioEventStubBuilder builder;
        try
        {
            builder = stubs();
        }
        catch (ServerEventStubException ex)
        {
            throw new ServerEventStubException(ex.Reason, events.Count, events[0].GamePath);
        }

        var done = new List<string>(events.Count);
        foreach (var (file, gamePath, _) in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (uasset, uexp) = builder.CreateStub(gamePath);
            await File.WriteAllBytesAsync(file, uasset, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.ChangeExtension(file, ".uexp"), uexp, cancellationToken).ConfigureAwait(false);
            done.Add(gamePath);
        }

        return done;
    }
}

/// <summary>The server pak was refused: it would carry client-cooked sound events and no server stub could be read.</summary>
public sealed class ServerEventStubException : InvalidOperationException
{
    /// <summary>Creates the exception for a stub that could not be read.</summary>
    public ServerEventStubException(string reason)
        : base($"No server sound-event stub: {reason}.")
    {
        Reason = reason;
    }

    /// <summary>Creates the exception for a refused server pak.</summary>
    public ServerEventStubException(string reason, int events, string firstEvent)
        : base($"The server pak was not built: it would carry {events} client-cooked sound event(s) (e.g. {firstEvent}) and the dedicated server crashes on those "
            + $"(\"Serial size mismatch: Got 12\"). They are replaced by the server's own 12-byte stubs, but {reason}. Set Settings > Server folder to the dedicated server's SCUM\\Content\\Paks.")
    {
        Reason = reason;
        Events = events;
        FirstEvent = firstEvent;
    }

    /// <summary>Why no stub could be read.</summary>
    public string Reason { get; }

    /// <summary>How many events would have needed a stub (0 when only the stub was read).</summary>
    public int Events { get; }

    /// <summary>The first such event, or null.</summary>
    public string? FirstEvent { get; }
}
