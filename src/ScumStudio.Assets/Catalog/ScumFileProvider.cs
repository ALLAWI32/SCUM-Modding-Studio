using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace ScumStudio.Assets.Catalog;

/// <summary>
/// CUE4Parse file provider that can combine <c>.pak</c>/<c>.utoc</c> containers with loose cooked files
/// (<c>.uasset</c>/<c>.uexp</c>/<c>.ubulk</c>) extracted to a folder.
/// </summary>
/// <remarks>
/// <para>CUE4Parse's own <c>DefaultFileProvider</c> mounts loose files under the <em>name of the folder</em> it was given
/// (<c>orig/SCUM/Content/X.uasset</c> opened at <c>orig</c> becomes <c>orig/SCUM/Content/X.uasset</c>, and the project name, which
/// <c>/Game/...</c> paths resolve against, becomes <c>orig</c>). This provider instead locates the project folder (the one that
/// contains <c>Content/</c>) and mounts it as <c>&lt;ProjectName&gt;/</c>, so loose files get the same
/// <c>SCUM/Content/...</c> paths as pak entries and <c>/Game/...</c> object paths resolve identically for both.</para>
/// <para>Loose directories added with a higher read order than the paks override pak entries with the same path
/// (used for "stock paks + my extracted edits" setups).</para>
/// </remarks>
public sealed class ScumFileProvider : AbstractVfsFileProvider
{
    private static readonly HashSet<string> LooseExtensions = new(GameFile.UeKnownExtensions, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _aliased = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty provider using UE 4.27 versions and case-insensitive paths.</summary>
    public ScumFileProvider(VersionContainer? versions = null)
        : base(versions ?? new VersionContainer(EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase)
    {
    }

    /// <summary>Loose project folders that were added (full paths).</summary>
    public List<string> LooseRoots { get; } = [];

    /// <summary>No-op: containers and loose folders are added explicitly.</summary>
    public override void Initialize()
    {
    }

    /// <summary>
    /// Adds every cooked file below <paramref name="projectDirectory"/> (the folder that contains <c>Content/</c>)
    /// under the virtual root <c><paramref name="projectName"/>/</c>.
    /// </summary>
    /// <returns>Number of files added.</returns>
    public int AddLooseProject(string projectDirectory, string projectName, long readOrder)
    {
        var baseDir = new DirectoryInfo(Path.GetFullPath(projectDirectory));
        if (!baseDir.Exists)
        {
            throw new DirectoryNotFoundException($"Loose asset folder not found: {baseDir.FullName}");
        }

        var files = new Dictionary<string, GameFile>(PathComparer);
        foreach (var file in baseDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var ext = file.Extension.TrimStart('.');
            if (!LooseExtensions.Contains(ext))
            {
                continue;
            }

            var gameFile = new OsGameFile(baseDir, file, projectName + "/", Versions);
            files[gameFile.Path] = gameFile;
        }

        Files.AddFiles(files, readOrder);
        LooseRoots.Add(baseDir.FullName);
        return files.Count;
    }

    /// <summary>
    /// Adds aliases for entries that containers report as <c>&lt;Project&gt;/&lt;Project&gt;/Content/...</c> (stock
    /// <c>pakchunk0_s51</c> mounts at <c>../../../SCUM/</c> while its entries also start with <c>SCUM/</c>), so every package is
    /// reachable as <c>&lt;Project&gt;/Content/...</c>. Aliases keep the container's read order. Returns the number of aliases added.
    /// </summary>
    public int AliasDoubledProjectPaths(string projectName)
    {
        var doubled = projectName + "/" + projectName + "/";
        var added = 0;
        foreach (var vfs in MountedVfs)
        {
            if (!_aliased.Add(vfs.Path))
            {
                continue;
            }

            var aliases = new Dictionary<string, GameFile>(PathComparer);
            foreach (var (path, file) in vfs.Files)
            {
                if (path.StartsWith(doubled, StringComparison.OrdinalIgnoreCase))
                {
                    aliases[path[(projectName.Length + 1)..]] = file;
                }
            }

            if (aliases.Count > 0)
            {
                Files.AddFiles(aliases, vfs.ReadOrder);
                added += aliases.Count;
            }
        }

        return added;
    }

    /// <summary>Submits a 256-bit AES key (raw bytes) under the zero GUID (SCUM uses a single key) and mounts what it unlocks.</summary>
    internal int SubmitAesKey(byte[] key) => SubmitKey(new FGuid(), new FAesKey(key));
}
