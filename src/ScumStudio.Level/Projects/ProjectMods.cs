using ScumStudio.Assets.Catalog;
using ScumStudio.Pak;
using ScumStudio.Pak.Reading;

namespace ScumStudio.Level.Projects;

/// <summary>
/// Mods made by others, imported into a project (owner and Hektor: "load someone's pak mod, its umap and landscape, and
/// work on it"): each pak is unpacked to <c>&lt;project&gt;/mods/&lt;name&gt;/SCUM/Content/…</c>. The app reads them over
/// the game files (a later import wins where two overlap) and the export carries them in the project's pak, under what
/// the project changed.
/// </summary>
public static class ProjectMods
{
    /// <summary>Folder of the imported mods inside a project.</summary>
    public const string FolderName = "mods";

    /// <summary>The imported mods' folders (each holds <c>SCUM/Content</c>), oldest import first; empty without any.</summary>
    public static IReadOnlyList<string> Folders(string projectDirectory)
    {
        var root = Path.Combine(projectDirectory, FolderName);
        return Directory.Exists(root)
            ? new DirectoryInfo(root).EnumerateDirectories()
                .Where(d => AssetCatalog.FindLooseProjectRoot(d.FullName) is not null)
                .OrderBy(d => d.CreationTimeUtc)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.FullName)
                .ToList()
            : [];
    }

    /// <summary>
    /// Unpacks <paramref name="pakPath"/> into the project's mods folder (replacing an earlier import of the same pak) and
    /// returns the folder and the number of files.
    /// </summary>
    /// <exception cref="InvalidDataException">The pak could not be opened (encrypted with another key, or not a pak).</exception>
    public static async Task<(string Folder, int Files)> ImportAsync(string projectDirectory, string pakPath, string? aesKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        var name = NameOf(pakPath);
        var target = Path.Combine(projectDirectory, FolderName, name);
        var work = target + ".importing";
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        var files = 0;
        using (var source = PakFileSource.OpenFile(pakPath, new PakFileSourceOptions { AesKey = string.IsNullOrWhiteSpace(aesKey) ? null : aesKey }))
        {
            if (source.Archives.Count == 0 || !source.Archives[0].IsMounted)
            {
                throw new InvalidDataException($"{Path.GetFileName(pakPath)} could not be opened (an encrypted pak needs its own key).");
            }

            foreach (var path in source.EnumerateArchiveFiles(Path.GetFileName(pakPath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = await source.ReadBytesAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new IOException($"Could not read {path}.");
                var file = PakPaths.ToSafeFilePath(work, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, data, cancellationToken).ConfigureAwait(false);
                files++;
            }
        }

        if (AssetCatalog.FindLooseProjectRoot(work) is null)
        {
            Directory.Delete(work, recursive: true);
            throw new InvalidDataException($"{Path.GetFileName(pakPath)} holds no game content (no SCUM/Content folder).");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.Move(work, target);
        return (target, files);
    }

    /// <summary>Removes an imported mod.</summary>
    public static void Remove(string modFolder)
    {
        if (Directory.Exists(modFolder))
        {
            Directory.Delete(modFolder, recursive: true);
        }
    }

    /// <summary>
    /// Copies the imported mods' files into an export's staging folder, except files the export wrote itself (its version
    /// of a modded level is the mod's level with the project's edits). Returns how many files were carried.
    /// </summary>
    public static int CarryInto(IReadOnlyList<string> modFolders, string staging, string projectName = AssetPaths.DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(modFolders);
        var written = Directory.Exists(staging)
            ? Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(staging, f)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var carried = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in modFolders)
        {
            if (AssetCatalog.FindLooseProjectRoot(folder, projectName) is not { } root)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.Combine(projectName, Path.GetRelativePath(root, file));
                if (!written.Contains(relative))
                {
                    carried[relative] = file; // a later import wins
                }
            }
        }

        foreach (var (relative, file) in carried)
        {
            var target = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        return carried.Count;
    }

    /// <summary>The folder name of an imported pak: its file name without <c>pakchunkNNN-</c>, <c>_P</c> and the extension.</summary>
    public static string NameOf(string pakPath)
    {
        var name = Path.GetFileNameWithoutExtension(pakPath);
        var dash = name.IndexOf('-');
        if (name.StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase) && dash > 0)
        {
            name = name[(dash + 1)..];
        }

        if (name.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^2];
        }

        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return safe.Length == 0 ? "mod" : safe;
    }
}
