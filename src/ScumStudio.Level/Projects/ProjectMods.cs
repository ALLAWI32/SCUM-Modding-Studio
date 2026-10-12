using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Modding;
using ScumStudio.Modding.Registry;
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

    /// <summary>Suffix of the folder a mod is unpacked into before it is complete (never read or carried).</summary>
    public const string ImportingSuffix = ".importing";

    /// <summary>The imported mods' folders (each holds <c>SCUM/Content</c>), oldest import first; empty without any.</summary>
    public static IReadOnlyList<string> Folders(string projectDirectory)
    {
        var root = Path.Combine(projectDirectory, FolderName);
        return Directory.Exists(root)
            ? new DirectoryInfo(root).EnumerateDirectories()
                .Where(d => !d.Name.EndsWith(ImportingSuffix, StringComparison.OrdinalIgnoreCase)) // an import that did not finish
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
        var work = target + ImportingSuffix;
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        var files = 0;
        try
        {
            using var source = PakFileSource.OpenFile(pakPath, new PakFileSourceOptions { AesKey = string.IsNullOrWhiteSpace(aesKey) ? null : aesKey });
            if (source.Archives.Count == 0 || !source.Archives[0].IsMounted)
            {
                throw new InvalidDataException($"{Path.GetFileName(pakPath)} could not be opened (an encrypted pak needs its own key).");
            }

            // Only what the game loads: Hektor's map pak also held 210 MB of JSON dumps and pictures from his tools.
            foreach (var path in source.EnumerateArchiveFiles(Path.GetFileName(pakPath)).Where(IsGameFile))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = await source.ReadBytesAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new IOException($"Could not read {path}.");
                var file = PakPaths.ToSafeFilePath(work, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, data, cancellationToken).ConfigureAwait(false);
                files++;
            }
        }
        catch
        {
            // F012: a half-unpacked mod (disk full, a bad entry, cancelled) must not stay behind.
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }

            throw;
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

    private static readonly HashSet<string> GameFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".uasset", ".umap", ".uexp", ".ubulk", ".uptnl", ".bin", ".ushaderbytecode", ".locres", ".locmeta",
    };

    /// <summary>True for a file the game reads from a pak (packages, the asset registry, shaders, translations).</summary>
    public static bool IsGameFile(string path) => GameFileExtensions.Contains(Path.GetExtension(path));

    /// <summary>File that marks a mod the export leaves out (the server and players install it on its own).</summary>
    public const string NotCarriedMarker = ".not-in-export";

    /// <summary>
    /// True when the export puts the mod into the project's pak (the default). A big mod (Hektor's map: 5.8 GB) can stay
    /// installed as its own pak instead: the game mounts our <c>_P</c> pak over it, so the project's edits still win.
    /// </summary>
    public static bool IsCarried(string modFolder) => !File.Exists(Path.Combine(modFolder, NotCarriedMarker));

    /// <summary>Sets whether the export puts the mod into the project's pak.</summary>
    public static void SetCarried(string modFolder, bool carried)
    {
        var marker = Path.Combine(modFolder, NotCarriedMarker);
        if (carried)
        {
            File.Delete(marker);
        }
        else
        {
            File.WriteAllText(marker, "Export mod leaves this imported mod out of the project's pak.\n");
        }
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
    /// of a modded level is the mod's level with the project's edits) and the mods' <c>AssetRegistry.bin</c>, which is never
    /// shipped as it is (<see cref="MergeRegistries"/>). Returns how many files were carried.
    /// </summary>
    public static int CarryInto(IReadOnlyList<string> modFolders, string staging, string projectName = AssetPaths.DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(modFolders);
        var written = Directory.Exists(staging)
            ? Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(staging, f)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var carried = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in modFolders.Where(IsCarried))
        {
            if (AssetCatalog.FindLooseProjectRoot(folder, projectName) is not { } root)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(IsGameFile))
            {
                var relative = Path.Combine(projectName, Path.GetRelativePath(root, file));
                if (string.Equals(Path.GetFileName(file), RegistryFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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

    private const string RegistryFileName = "AssetRegistry.bin";

    /// <summary>The carried mods that have an <c>AssetRegistry.bin</c> of their own (next to their <c>Content</c>) and that file, oldest import first.</summary>
    public static IReadOnlyList<(string Mod, string Registry)> Registries(IReadOnlyList<string> modFolders, string projectName = AssetPaths.DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(modFolders);
        return modFolders.Where(IsCarried)
            .Select(folder => (Mod: Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), Root: AssetCatalog.FindLooseProjectRoot(folder, projectName)))
            .Where(m => m.Root is not null && File.Exists(Path.Combine(m.Root, RegistryFileName)))
            .Select(m => (m.Mod, Path.Combine(m.Root!, RegistryFileName)))
            .ToList();
    }

    /// <summary>
    /// When carried mods have an <c>AssetRegistry.bin</c> (P0 #1, F005/N012): writes the staged registry as the game's stock
    /// one (or the one this export already staged, which is stock plus its own records), plus each mod's records of
    /// packages that ship in <paramref name="staging"/> (<see cref="RegistryMerge.Build"/>). Call it after
    /// <see cref="CarryInto"/>. Returns true when a registry was written.
    /// </summary>
    public static bool MergeRegistries(IReadOnlyList<string> modFolders, string staging, AssetCatalog catalog, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(warnings);
        var registries = Registries(modFolders, catalog.ProjectName);
        if (registries.Count == 0)
        {
            return false;
        }

        var target = Path.Combine([staging, .. AssetModBuilder.AssetRegistryPath.Split('/')]);
        var baseBytes = File.Exists(target) ? File.ReadAllBytes(target) : RegistryMerge.ReadBase(catalog, warnings);
        if (baseBytes is null)
        {
            warnings.Add($"The imported mods' AssetRegistry.bin is not shipped: {catalog.DisplayName} has no registry to add their records to.");
            return false;
        }

        var mods = new List<(string, AssetRegistryFile)>();
        foreach (var (name, registry) in registries)
        {
            try
            {
                mods.Add((name, AssetRegistryFile.Load(registry)));
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
            {
                warnings.Add($"{name}: its AssetRegistry.bin cannot be read ({ex.Message}); its records are not in the shipped registry.");
            }
        }

        var shipped = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(staging, f).Replace('\\', '/'))
            .Where(AssetPaths.IsPackageFile)
            .Select(f => PackageNameOf(f, catalog.ProjectName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = RegistryMerge.Build(AssetRegistryFile.Parse(baseBytes), mods, shipped.Contains, warnings);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, merged.Save());
        return true;
    }

    // The registry's package name of a staged file: /Game/... for the project's content, /<Plugin>/... for a plugin's.
    private static string PackageNameOf(string file, string projectName)
    {
        var plugins = file.IndexOf("/Plugins/", StringComparison.OrdinalIgnoreCase);
        var content = plugins < 0 ? -1 : file.IndexOf("/Content/", plugins, StringComparison.OrdinalIgnoreCase);
        if (content < 0)
        {
            return AssetPaths.ToPackagePath(file, projectName);
        }

        var plugin = file[(file.LastIndexOf('/', content - 1) + 1)..content];
        return "/" + plugin + AssetPaths.ToPackagePath(projectName + file[content..], projectName)["/Game".Length..];
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
