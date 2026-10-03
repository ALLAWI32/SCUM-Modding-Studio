using System.Diagnostics.CodeAnalysis;
using ScumStudio.Core.Abstractions;

namespace ScumStudio.Pak.Reading;

/// <summary>
/// Layers several <see cref="IFileSource"/>s: for every path the <b>first</b> source that has it wins,
/// so a mod (staging folder or mod pak) placed before the stock game files overrides them.
/// </summary>
/// <remarks>Thread-safe for reads when the layers are.</remarks>
public sealed class CompositeFileSource : IFileSource, IDisposable
{
    private readonly IFileSource[] _sources;
    private readonly bool _ownsSources;

    /// <summary>Creates a composite; <paramref name="sources"/> are in priority order (highest first).</summary>
    /// <param name="sources">Layers, highest priority first.</param>
    /// <param name="ownsSources">When true, <see cref="Dispose"/> disposes layers that are <see cref="IDisposable"/>.</param>
    public CompositeFileSource(IEnumerable<IFileSource> sources, bool ownsSources = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToArray();
        if (_sources.Any(s => s is null))
        {
            throw new ArgumentException("Sources must not contain null.", nameof(sources));
        }

        _ownsSources = ownsSources;
    }

    /// <summary>Creates a composite from layers in priority order (highest first).</summary>
    public CompositeFileSource(params IFileSource[] sources)
        : this((IEnumerable<IFileSource>)sources)
    {
    }

    /// <summary>The layers, highest priority first.</summary>
    public IReadOnlyList<IFileSource> Sources => _sources;

    /// <inheritdoc />
    public string DisplayName => string.Join(" > ", _sources.Select(s => s.DisplayName));

    /// <summary>Returns the layer that provides <paramref name="virtualPath"/> (the first that has it), or null.</summary>
    public IFileSource? FindSource(string virtualPath) => _sources.FirstOrDefault(s => s.Exists(virtualPath));

    /// <inheritdoc />
    public bool Exists(string virtualPath) => _sources.Any(s => s.Exists(virtualPath));

    /// <inheritdoc />
    public bool TryGetBytes(string virtualPath, [NotNullWhen(true)] out byte[]? data)
    {
        foreach (var source in _sources)
        {
            if (source.TryGetBytes(virtualPath, out data))
            {
                return true;
            }
        }

        data = null;
        return false;
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadBytesAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        foreach (var source in _sources)
        {
            var data = await source.ReadBytesAsync(virtualPath, cancellationToken).ConfigureAwait(false);
            if (data is not null)
            {
                return data;
            }
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>Union of all layers, each path once (spelled as the highest-priority layer spells it), sorted ordinally.</remarks>
    public IEnumerable<string> EnumerateFiles(string virtualDirectory = "", bool recursive = true)
    {
        var seen = new HashSet<string>(VirtualPath.Comparer);
        var result = new List<string>();
        foreach (var source in _sources)
        {
            foreach (var path in source.EnumerateFiles(virtualDirectory, recursive))
            {
                if (seen.Add(path))
                {
                    result.Add(path);
                }
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_ownsSources)
        {
            return;
        }

        foreach (var source in _sources.OfType<IDisposable>())
        {
            source.Dispose();
        }
    }
}
