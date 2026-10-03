using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Core.Abstractions;

namespace ScumStudio.Pak.Server;

/// <summary>
/// Server variant rule for Wwise events (port of tools/server_variant.py): the SCUM dedicated server (WindowsServer cook)
/// serialises AkAudioEvent assets as 12-byte stubs (None terminator + int32 0) and crashes with
/// "Serial size mismatch: Got 12, Expected N" on client-cooked events. Every AkAudioEvent package below
/// <c>SCUM/Content/WwiseAudio/Event</c> is therefore replaced by a clone of a stock server stub, renamed through its
/// name table and export record.
/// </summary>
public sealed class AkAudioEventStubBuilder : IServerVariantBuilder
{
    /// <summary>The stub the user's toolchain uses (from pakchunk0_s3-WindowsServer.pak).</summary>
    public const string DefaultStubGamePath = "/Game/WwiseAudio/Event/DefaultWorkUnit/Vehicles/Vehicle_Ignition_Start";

    /// <summary>Size of a server-cooked AkAudioEvent export.</summary>
    public const int StubExportSize = 12;

    private readonly byte[] _stubUasset;
    private readonly byte[] _stubUexp;
    private readonly string _stubGamePath;
    private readonly ILogger _logger;

    /// <summary>Creates the builder from a server-cooked stub package's bytes.</summary>
    /// <param name="stubUasset">The stub's .uasset.</param>
    /// <param name="stubUexp">The stub's .uexp.</param>
    /// <param name="stubGamePath">The stub's own game path (e.g. <see cref="DefaultStubGamePath"/>).</param>
    /// <param name="logger">Optional logger.</param>
    public AkAudioEventStubBuilder(byte[] stubUasset, byte[] stubUexp, string stubGamePath = DefaultStubGamePath, ILogger? logger = null)
    {
        _stubUasset = stubUasset ?? throw new ArgumentNullException(nameof(stubUasset));
        _stubUexp = stubUexp ?? throw new ArgumentNullException(nameof(stubUexp));
        _stubGamePath = stubGamePath ?? throw new ArgumentNullException(nameof(stubGamePath));
        _logger = logger ?? NullLogger.Instance;
        var stub = CookedPackageLite.Parse(_stubUasset, _stubUexp);
        if (!stub.Names.Contains(_stubGamePath) || !stub.Names.Contains(ShortName(_stubGamePath)))
        {
            throw new ArgumentException($"The stub's name table does not contain {_stubGamePath}.", nameof(stubGamePath));
        }
    }

    /// <summary>
    /// Loads the stub from a server-cooked tree (<c>orig_server/</c>), e.g. with <see cref="DefaultStubGamePath"/>.
    /// </summary>
    public static AkAudioEventStubBuilder FromServerTree(IFileSource serverFiles, string stubGamePath = DefaultStubGamePath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(serverFiles);
        var basePath = GameToVirtual(stubGamePath);
        if (!serverFiles.TryGetBytes(basePath + ".uasset", out var ua) || !serverFiles.TryGetBytes(basePath + ".uexp", out var ux))
        {
            throw new FileNotFoundException($"Server stub package not found: {basePath}.uasset/.uexp");
        }

        return new AkAudioEventStubBuilder(ua, ux, stubGamePath, logger);
    }

    /// <summary>
    /// Builds the stub renamed to <paramref name="gamePath"/> (port of server_variant.py <c>write_stub</c>).
    /// </summary>
    /// <returns>The new .uasset and .uexp bytes.</returns>
    public (byte[] Uasset, byte[] Uexp) CreateStub(string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        var stub = CookedPackageLite.Parse(_stubUasset, _stubUexp);
        var stubName = ShortName(_stubGamePath);
        var name = ShortName(gamePath);
        var names = stub.Names.Select(n => n == _stubGamePath ? gamePath : n == stubName ? name : n).ToList();
        if (names.Count(n => n == gamePath) != 1 || names.Count(n => n == name) != 1)
        {
            throw new InvalidOperationException($"Renaming the stub to {gamePath} makes its name table ambiguous.");
        }

        var (uasset, uexp) = stub.Rebuild(names);

        // Same post-condition as write_stub: first export renamed, 12 bytes, payload unchanged.
        var check = CookedPackageLite.Parse(uasset, uexp);
        var first = check.Exports[0];
        if (check.NameOf(first.ObjectNameIndex, first.ObjectNameNumber) != name
            || first.SerialSize != StubExportSize
            || !check.ExportBytes(0).AsSpan().SequenceEqual(stub.ExportBytes(0)))
        {
            throw new InvalidOperationException($"Stub rebuild check failed for {gamePath}.");
        }

        return (uasset, uexp);
    }

    /// <summary>True when the package's exports include one of class AkAudioEvent (server_variant.py test).</summary>
    public static bool IsAkAudioEvent(byte[] uasset, byte[] uexp)
    {
        var package = CookedPackageLite.Parse(uasset, uexp);
        return package.Exports.Any(e => package.ClassOf(e).EndsWith(":AkAudioEvent", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ApplyInPlaceAsync(string stagingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var content = Path.Combine(Path.GetFullPath(stagingDirectory), PakPaths.DefaultProjectName, "Content");
        var events = Path.Combine(content, "WwiseAudio", "Event");
        var done = new List<string>();
        if (!Directory.Exists(events))
        {
            return done;
        }

        foreach (var uassetPath in Directory.EnumerateFiles(events, "*.uasset", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var basePath = uassetPath[..^".uasset".Length];
            var uexpPath = basePath + ".uexp";
            var uasset = await File.ReadAllBytesAsync(uassetPath, cancellationToken).ConfigureAwait(false);
            var uexp = File.Exists(uexpPath) ? await File.ReadAllBytesAsync(uexpPath, cancellationToken).ConfigureAwait(false) : [];
            if (!IsAkAudioEvent(uasset, uexp))
            {
                continue;
            }

            var relative = VirtualPath.Normalize(Path.GetRelativePath(content, basePath));
            var gamePath = "/Game/" + relative;
            var (stubUasset, stubUexp) = CreateStub(gamePath);
            await File.WriteAllBytesAsync(uassetPath, stubUasset, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(uexpPath, stubUexp, cancellationToken).ConfigureAwait(false);
            done.Add(gamePath);
            _logger.LogDebug("Server stub written for {GamePath}.", gamePath);
        }

        _logger.LogInformation("Server variant: {Count} AkAudioEvent package(s) replaced by 12-byte stubs.", done.Count);
        done.Sort(StringComparer.Ordinal);
        return done;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> BuildAsync(string buildDirectory, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var source = Path.Combine(Path.GetFullPath(buildDirectory), PakPaths.DefaultProjectName);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Build tree has no {PakPaths.DefaultProjectName}/ folder: {buildDirectory}");
        }

        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        var target = Path.Combine(output, PakPaths.DefaultProjectName);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return await ApplyInPlaceAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static string ShortName(string gamePath) => gamePath[(gamePath.LastIndexOf('/') + 1)..];

    private static string GameToVirtual(string gamePath)
    {
        const string prefix = "/Game/";
        if (!gamePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Expected a /Game/ path, got '{gamePath}'.", nameof(gamePath));
        }

        return PakPaths.DefaultProjectName + "/Content/" + gamePath[prefix.Length..];
    }
}
