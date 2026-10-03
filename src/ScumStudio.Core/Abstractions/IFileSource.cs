using System.Diagnostics.CodeAnalysis;

namespace ScumStudio.Core.Abstractions;

/// <summary>
/// Read-only access to game files by virtual path, independent of where they live
/// (a loose extracted directory, one or more mounted paks, or a layered combination).
/// </summary>
/// <remarks>
/// Virtual paths use forward slashes, no leading slash, and start at the project root,
/// e.g. <c>SCUM/Content/ConZ_Files/Maps/The_Island/A_0_Outpost.umap</c> or <c>SCUM/AssetRegistry.bin</c>.
/// Lookups are case-insensitive (Unreal paths are). Use <see cref="VirtualPath.Normalize"/> to
/// canonicalise user input. Implementations live in ScumStudio.Pak and must be thread-safe for reads.
/// </remarks>
public interface IFileSource
{
    /// <summary>Human-readable description for logs and UI (e.g. "3 paks in D:\SCUM\Content\Paks").</summary>
    string DisplayName { get; }

    /// <summary>True when a file exists at <paramref name="virtualPath"/>.</summary>
    bool Exists(string virtualPath);

    /// <summary>
    /// Reads a whole file. Returns false (and <paramref name="data"/> = null) when it does not exist.
    /// </summary>
    bool TryGetBytes(string virtualPath, [NotNullWhen(true)] out byte[]? data);

    /// <summary>
    /// Reads a whole file asynchronously; returns null when it does not exist.
    /// </summary>
    Task<byte[]?> ReadBytesAsync(string virtualPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates virtual paths of files under <paramref name="virtualDirectory"/>
    /// (empty string = everything). Returned paths are normalised (see <see cref="VirtualPath"/>).
    /// </summary>
    /// <param name="virtualDirectory">Directory prefix, e.g. <c>SCUM/Content/ConZ_Files/Maps</c>.</param>
    /// <param name="recursive">When false only direct children are returned.</param>
    IEnumerable<string> EnumerateFiles(string virtualDirectory = "", bool recursive = true);
}
