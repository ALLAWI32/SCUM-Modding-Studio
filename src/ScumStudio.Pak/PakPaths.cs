using ScumStudio.Core.Abstractions;

namespace ScumStudio.Pak;

/// <summary>
/// Path conventions shared by the pak reader and writer.
/// </summary>
public static class PakPaths
{
    /// <summary>SCUM's Unreal project name, the first segment of every virtual path.</summary>
    public const string DefaultProjectName = "SCUM";

    /// <summary>Mount point used by SCUM mod paks (entries then read <c>SCUM/Content/...</c>).</summary>
    public const string DefaultMountPoint = "../../../";

    /// <summary>
    /// Converts an entry path as reported by a pak reader (mount point already applied) into a canonical
    /// virtual path (<c>SCUM/Content/...</c>).
    /// </summary>
    /// <remarks>
    /// Handles (1) leading <c>../</c> segments left over from a <c>../../../</c> mount point, (2) a leading slash,
    /// and (3) the stock <c>pakchunk0_s51</c> quirk where the pak mounts at <c>../../../SCUM/</c> but its entries
    /// also start with <c>SCUM/</c>, so readers report <c>SCUM/SCUM/Content/...</c>.
    /// </remarks>
    /// <param name="entryPath">Path as reported by the reader.</param>
    /// <param name="projectName">Project folder name (default <c>SCUM</c>).</param>
    public static string NormalizeEntryPath(string entryPath, string projectName = DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(entryPath);
        var path = VirtualPath.Normalize(entryPath);
        while (path.StartsWith("../", StringComparison.Ordinal))
        {
            path = path[3..];
        }

        if (path == "..")
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(projectName))
        {
            var doubled = projectName + "/" + projectName + "/";
            while (path.StartsWith(doubled, StringComparison.OrdinalIgnoreCase))
            {
                path = path[(projectName.Length + 1)..];
            }
        }

        return path;
    }

    /// <summary>
    /// True when <paramref name="virtualPath"/> is a file a SCUM mod pak may carry:
    /// anything below <c>&lt;Project&gt;/Content/</c>, or <c>&lt;Project&gt;/AssetRegistry.bin</c>.
    /// </summary>
    public static bool IsModPakEntry(string virtualPath, string projectName = DefaultProjectName)
    {
        var path = VirtualPath.Normalize(virtualPath);
        return path.StartsWith(projectName + "/Content/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, projectName + "/AssetRegistry.bin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a virtual path to a file below <paramref name="rootDirectory"/>, refusing anything that would escape it
    /// (for example <c>..</c> segments in a hostile pak).
    /// </summary>
    /// <exception cref="InvalidDataException">The path escapes the root or is empty.</exception>
    public static string ToSafeFilePath(string rootDirectory, string virtualPath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var segments = VirtualPath.Normalize(virtualPath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s == ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.IsPathRooted(s)))
        {
            throw new InvalidDataException($"Refusing unsafe entry path '{virtualPath}'.");
        }

        var full = Path.GetFullPath(Path.Combine([root, .. segments]));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidDataException($"Refusing entry path '{virtualPath}' outside the output directory.");
        }

        return full;
    }
}
