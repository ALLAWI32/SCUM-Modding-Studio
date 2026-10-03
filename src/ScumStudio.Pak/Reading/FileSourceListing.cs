using ScumStudio.Core.Abstractions;

namespace ScumStudio.Pak.Reading;

/// <summary>Shared <see cref="IFileSource.EnumerateFiles"/> filtering over a sorted list of virtual paths.</summary>
internal static class FileSourceListing
{
    /// <summary>
    /// Yields the paths below <paramref name="virtualDirectory"/> (all descendants, or direct children only).
    /// </summary>
    public static IEnumerable<string> Filter(IReadOnlyList<string> sortedPaths, string virtualDirectory, bool recursive)
    {
        var directory = VirtualPath.Normalize(virtualDirectory ?? string.Empty);
        foreach (var path in sortedPaths)
        {
            if (directory.Length > 0)
            {
                if (path.Length <= directory.Length
                    || path[directory.Length] != '/'
                    || !path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!recursive && path.IndexOf('/', directory.Length == 0 ? 0 : directory.Length + 1) >= 0)
            {
                continue;
            }

            yield return path;
        }
    }
}
