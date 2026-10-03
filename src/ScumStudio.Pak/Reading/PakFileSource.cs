using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Core.Abstractions;

namespace ScumStudio.Pak.Reading;

/// <summary>
/// <see cref="IFileSource"/> over one or more <c>.pak</c> files, backed by CUE4Parse's <see cref="DefaultFileProvider"/>
/// (UE 4.27 versions). Virtual paths are normalised to <c>SCUM/Content/...</c> whatever the pak's mount point.
/// </summary>
/// <remarks>
/// <para>Encrypted stock paks need the AES key (entered by the user at runtime, <see cref="PakFileSourceOptions.AesKey"/>);
/// it is submitted under the zero key GUID as SCUM uses a single key. Paks that cannot be mounted are reported in
/// <see cref="Archives"/> with <see cref="PakArchiveInfo.IsMounted"/> = false.</para>
/// <para>When several paks contain the same path, CUE4Parse's rule applies (higher read order, i.e. <c>_P</c> patch paks, wins).</para>
/// <para>The underlying <see cref="Provider"/> is exposed for ScumStudio.Assets (package loading, exports).</para>
/// <para>CUE4Parse 1.2.2 inflates zlib entries through a native zlib-ng library that is not shipped; unless the host has
/// called <c>ZlibHelper.Initialize</c>, zlib-compressed entries are inflated here with System.IO.Compression instead
/// (stock SCUM paks and the user's mod paks are stored uncompressed, so this only matters for <c>pak pack --compression zlib</c>).</para>
/// </remarks>
public sealed class PakFileSource : IFileSource, IDisposable
{
    private readonly ILogger _logger;
    private readonly string _projectName;
    private readonly Dictionary<string, GameFile> _files;
    private readonly string[] _sorted;
    private readonly string _displayName;
    private bool _disposed;

    private PakFileSource(DefaultFileProvider provider, PakFileSourceOptions options, string displayName)
    {
        Provider = provider;
        _logger = options.Logger ?? NullLogger.Instance;
        _projectName = options.ProjectName;
        _displayName = displayName;
        (_files, _sorted) = BuildIndex();
    }

    /// <summary>The CUE4Parse provider (for package loading in ScumStudio.Assets). Owned by this source.</summary>
    public DefaultFileProvider Provider { get; }

    /// <inheritdoc />
    public string DisplayName => _displayName;

    /// <summary>Number of distinct virtual paths available.</summary>
    public int Count => _files.Count;

    /// <summary>Every pak/container found, mounted or not.</summary>
    public IReadOnlyList<PakArchiveInfo> Archives
    {
        get
        {
            var mounted = Provider.MountedVfs.Select(v => Describe(v, true));
            var unmounted = Provider.UnloadedVfs.Select(v => Describe(v, false));
            return mounted.Concat(unmounted).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// Opens every <c>.pak</c> (and IoStore <c>.utoc</c>) directly inside <paramref name="paksDirectory"/>
    /// (e.g. <c>...\SCUM\Content\Paks</c>, or a folder of mod paks).
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    /// <exception cref="ArgumentException">The AES key text is invalid (message never contains the key).</exception>
    public static PakFileSource OpenDirectory(string paksDirectory, PakFileSourceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paksDirectory);
        options ??= new PakFileSourceOptions();
        var directory = Path.GetFullPath(paksDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Paks directory not found: {directory}");
        }

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
            .Where(f => options.PakFileFilter?.Invoke(Path.GetFileName(f)) ?? true)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Open(directory, files, options, $"{files.Count} paks in {directory}");
    }

    /// <summary>Opens the given pak files (they may live in different folders).</summary>
    /// <exception cref="FileNotFoundException">A pak does not exist.</exception>
    /// <exception cref="ArgumentException">No paks given, or the AES key text is invalid.</exception>
    public static PakFileSource OpenFiles(IEnumerable<string> pakFiles, PakFileSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pakFiles);
        options ??= new PakFileSourceOptions();
        var files = pakFiles.Select(Path.GetFullPath).ToList();
        if (files.Count == 0)
        {
            throw new ArgumentException("At least one pak file is required.", nameof(pakFiles));
        }

        foreach (var file in files.Where(f => !File.Exists(f)))
        {
            throw new FileNotFoundException($"Pak file not found: {file}", file);
        }

        var display = files.Count == 1 ? files[0] : $"{files.Count} paks ({string.Join(", ", files.Select(Path.GetFileName))})";
        return Open(Path.GetDirectoryName(files[0])!, files, options, display);
    }

    /// <summary>Opens a single pak file.</summary>
    public static PakFileSource OpenFile(string pakFile, PakFileSourceOptions? options = null) =>
        OpenFiles([pakFile], options);

    /// <inheritdoc />
    public bool Exists(string virtualPath) => _files.ContainsKey(VirtualPath.Normalize(virtualPath));

    /// <summary>
    /// Returns the CUE4Parse <see cref="GameFile"/> for a virtual path (its <see cref="GameFile.Path"/> is the
    /// provider-side path to pass to <c>Provider.LoadPackage</c>).
    /// </summary>
    public bool TryGetGameFile(string virtualPath, [NotNullWhen(true)] out GameFile? file) =>
        _files.TryGetValue(VirtualPath.Normalize(virtualPath), out file);

    /// <inheritdoc />
    public bool TryGetBytes(string virtualPath, [NotNullWhen(true)] out byte[]? data)
    {
        data = null;
        if (!TryGetGameFile(virtualPath, out var file))
        {
            return false;
        }

        try
        {
            data = file is FPakEntry { IsCompressed: true, CompressionMethod: CompressionMethod.Zlib } zlibEntry && ZlibHelper.Instance is null
                ? ExtractZlib(zlibEntry)
                : file.Read();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Failed to read {Path} from {Pak}.", virtualPath, (file as VfsEntry)?.Vfs.Name);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<byte[]?> ReadBytesAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        if (!TryGetGameFile(virtualPath, out var file))
        {
            return Task.FromResult<byte[]?>(null);
        }

        return Task.Run<byte[]?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TryGetBytes(virtualPath, out var data) ? data : null;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string virtualDirectory = "", bool recursive = true) =>
        FileSourceListing.Filter(_sorted, virtualDirectory, recursive);

    /// <summary>
    /// Lists the normalised virtual paths stored in one pak, by file name (<c>pakchunk96-F16_P.pak</c>) or full path,
    /// independent of overrides by other paks. Sorted ordinally.
    /// </summary>
    /// <exception cref="ArgumentException">No mounted pak has that name.</exception>
    public IReadOnlyList<string> EnumerateArchiveFiles(string pakNameOrPath)
    {
        var name = Path.GetFileName(pakNameOrPath);
        var vfs = Provider.MountedVfs.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"No mounted pak named '{name}'.", nameof(pakNameOrPath));
        return vfs.Files.Values
            .Select(f => PakPaths.NormalizeEntryPath(f.Path, _projectName))
            .Distinct(VirtualPath.Comparer)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The name of the pak that provides <paramref name="virtualPath"/>, or null.</summary>
    public string? GetArchiveName(string virtualPath) =>
        TryGetGameFile(virtualPath, out var file) ? (file as VfsEntry)?.Vfs.Name : null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Provider.Dispose();
    }

    private static PakFileSource Open(string directory, IReadOnlyList<string> files, PakFileSourceOptions options, string displayName)
    {
        var logger = options.Logger ?? NullLogger.Instance;
        FAesKey? key = null;
        if (options.AesKey is not null)
        {
            // Validate first so a malformed key never reaches CUE4Parse (whose exceptions could echo it).
            key = new FAesKey(AesKeyText.Normalize(options.AesKey));
        }

        var provider = new DefaultFileProvider(directory, SearchOption.TopDirectoryOnly,
            new VersionContainer(EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in files)
            {
                provider.RegisterVfs(file);
            }

            var mounted = provider.Mount();
            if (key is not null)
            {
                mounted += provider.SubmitKey(new FGuid(), key);
            }

            foreach (var vfs in provider.MountedVfs)
            {
                vfs.IsConcurrent = true;
            }

            var locked = provider.UnloadedVfs.Where(v => v.IsEncrypted).Select(v => v.Name).ToList();
            if (locked.Count > 0)
            {
                logger.LogWarning(
                    "{Count} encrypted pak(s) were not mounted ({Names}); {Reason}.",
                    locked.Count,
                    string.Join(", ", locked.Take(5)) + (locked.Count > 5 ? ", ..." : string.Empty),
                    key is null ? "no AES key was provided" : "the AES key does not match");
            }

            logger.LogDebug("Mounted {Mounted} of {Total} paks from {Source}.", mounted, files.Count, displayName);
            return new PakFileSource(provider, options, displayName);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    private (Dictionary<string, GameFile> Files, string[] Sorted) BuildIndex()
    {
        var files = new Dictionary<string, GameFile>(VirtualPath.Comparer);
        foreach (var file in Provider.Files.Values)
        {
            var path = PakPaths.NormalizeEntryPath(file.Path, _projectName);
            if (path.Length == 0)
            {
                continue;
            }

            if (files.TryGetValue(path, out var existing) && ReadOrder(existing) >= ReadOrder(file))
            {
                continue;
            }

            files[path] = file;
        }

        // Keep the spelling stored in the pak (case-preserving) for listings.
        var sorted = files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        return (files, sorted);
    }

    /// <summary>
    /// Managed replacement for CUE4Parse's native zlib path: reads each compression block (absolute offsets as decoded
    /// by CUE4Parse), decrypts it with the pak's AES key when needed (AES-256-ECB, blocks padded to 16 bytes), and inflates it.
    /// </summary>
    private static byte[] ExtractZlib(FPakEntry entry)
    {
        var output = new byte[entry.UncompressedSize];
        var blockSize = entry.CompressionBlockSize > 0 ? (int)entry.CompressionBlockSize : output.Length;
        byte[]? key = null;
        if (entry.IsEncrypted)
        {
            key = (entry.Vfs as IAesVfsReader)?.AesKey?.Key ?? throw new InvalidDataException("Encrypted entry without an AES key.");
        }

        using var stream = new FileStream(entry.Vfs.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        using var aes = key is null ? null : System.Security.Cryptography.Aes.Create();
        if (aes is not null)
        {
            aes.Key = key!;
        }

        var written = 0;
        foreach (var block in entry.CompressionBlocks)
        {
            var size = checked((int)block.Size);
            var readSize = aes is null ? size : (size + 15) & ~15;
            var raw = new byte[readSize];
            stream.Position = block.CompressedStart;
            stream.ReadExactly(raw);
            if (aes is not null)
            {
                raw = aes.DecryptEcb(raw, System.Security.Cryptography.PaddingMode.None);
            }

            var expected = Math.Min(blockSize, output.Length - written);
            using var zlib = new ZLibStream(new MemoryStream(raw, 0, size), CompressionMode.Decompress);
            zlib.ReadExactly(output, written, expected);
            written += expected;
        }

        if (written != output.Length)
        {
            throw new InvalidDataException($"Zlib entry {entry.Path} inflated to {written} of {output.Length} bytes.");
        }

        return output;
    }

    private static long ReadOrder(GameFile file) => (file as VfsEntry)?.Vfs.ReadOrder ?? long.MinValue;

    private static PakArchiveInfo Describe(IAesVfsReader vfs, bool mounted) =>
        new(
            vfs.Name,
            vfs.Path,
            vfs.MountPoint ?? string.Empty,
            mounted ? vfs.FileCount : 0,
            vfs.IsEncrypted,
            mounted,
            vfs is PakFileReader pak ? (int)pak.Info.Version : null,
            vfs.ReadOrder);
}
