using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Core.Abstractions;

namespace ScumStudio.Pak.Reading;

/// <summary>
/// <see cref="IFileSource"/> over an extracted directory tree, e.g. <c>orig/</c> (which contains <c>SCUM/Content/...</c>)
/// or a mod staging folder.
/// </summary>
/// <remarks>
/// The tree is indexed once at construction (and again on <see cref="Refresh"/>) so lookups are case-insensitive
/// on every OS, matching Unreal semantics. When two files differ only by case (possible on Linux), the first one in
/// ordinal order wins and a warning is logged. Reads are thread-safe.
/// </remarks>
public sealed class LooseFileSource : IFileSource
{
    private readonly ILogger _logger;
    private volatile Index _index;

    /// <summary>Creates a source over <paramref name="rootDirectory"/>.</summary>
    /// <param name="rootDirectory">Directory whose contents map to virtual paths.</param>
    /// <param name="virtualPrefix">
    /// Virtual path the root corresponds to. Empty (default) when the root contains <c>SCUM/</c>;
    /// e.g. <c>SCUM/Content</c> when the root is a Content folder.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public LooseFileSource(string rootDirectory, string virtualPrefix = "", ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"Directory not found: {rootDirectory}");
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        VirtualPrefix = VirtualPath.Normalize(virtualPrefix ?? string.Empty);
        _logger = logger ?? NullLogger.Instance;
        _index = BuildIndex();
    }

    /// <summary>Absolute root directory.</summary>
    public string RootDirectory { get; }

    /// <summary>Virtual path of <see cref="RootDirectory"/> (empty = project root).</summary>
    public string VirtualPrefix { get; }

    /// <inheritdoc />
    public string DisplayName => $"{_index.Files.Count} files in {RootDirectory}";

    /// <summary>Number of indexed files.</summary>
    public int Count => _index.Files.Count;

    /// <summary>Re-scans the directory (after files were added or removed).</summary>
    public void Refresh() => _index = BuildIndex();

    /// <inheritdoc />
    public bool Exists(string virtualPath) => _index.Files.ContainsKey(VirtualPath.Normalize(virtualPath));

    /// <summary>Returns the absolute file behind <paramref name="virtualPath"/>, or null.</summary>
    public string? GetFullPath(string virtualPath) =>
        _index.Files.TryGetValue(VirtualPath.Normalize(virtualPath), out var file) ? file : null;

    /// <inheritdoc />
    public bool TryGetBytes(string virtualPath, [NotNullWhen(true)] out byte[]? data)
    {
        data = null;
        var file = GetFullPath(virtualPath);
        if (file is null)
        {
            return false;
        }

        try
        {
            data = File.ReadAllBytes(file);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadBytesAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        var file = GetFullPath(virtualPath);
        if (file is null)
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string virtualDirectory = "", bool recursive = true) =>
        FileSourceListing.Filter(_index.Sorted, virtualDirectory, recursive);

    private Index BuildIndex()
    {
        var files = new Dictionary<string, string>(VirtualPath.Comparer);
        var all = Directory.EnumerateFiles(RootDirectory, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Virtual: VirtualPath.Combine(VirtualPrefix, Path.GetRelativePath(RootDirectory, f))))
            .OrderBy(f => f.Virtual, StringComparer.Ordinal);
        foreach (var (full, virtualPath) in all)
        {
            if (!files.TryAdd(virtualPath, full))
            {
                _logger.LogWarning("Ignoring {File}: another file differs only by letter case ({Existing}).", full, files[virtualPath]);
            }
        }

        var sorted = files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        return new Index(files, sorted);
    }

    private sealed record Index(Dictionary<string, string> Files, string[] Sorted);
}
