using System.Diagnostics.CodeAnalysis;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
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
public sealed class ScumFileProvider : AbstractVfsFileProvider, IFileProvider
{
    private static readonly HashSet<string> LooseExtensions = new(GameFile.UeKnownExtensions, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _aliased = new(StringComparer.OrdinalIgnoreCase);

    // The packages CUE4Parse loaded on this thread to resolve imports while an ImportScope is open, by path.
    [ThreadStatic]
    private static Dictionary<string, IPackage?>? _importScope;

    // Packages a scope keeps at most (they hold their file's bytes): the shared ones (a Blueprint, a parent material) are
    // met again long before this many others.
    private const int ImportScopeCapacity = 256;

    /// <summary>
    /// Until the result is disposed, a package CUE4Parse loads on this thread to resolve an import (a component's template,
    /// a Blueprint class, a material) is read once and then reused. CUE4Parse reads the whole imported package again for
    /// every import it resolves: the 16,677 objects of the TV_Base_A_3_Abandoned level read their Blueprints' packages
    /// thousands of times, 8.3 GB of garbage for one level (owner: "read 9172 ms (6337 MB allocated)", the app stalls while
    /// the collector cleans it up). Per thread and per scope, so nothing is kept after it and no package is shared between
    /// threads. Nested scopes share the outer one.
    /// </summary>
    public static IDisposable ImportScope()
    {
        if (_importScope is not null)
        {
            return NoScope.Instance;
        }

        _importScope = new Dictionary<string, IPackage?>(StringComparer.OrdinalIgnoreCase);
        return new Scope();
    }

    /// <inheritdoc cref="AbstractFileProvider.TryLoadPackage(string, out IPackage)" />
    /// <remarks>Re-implemented for the calls CUE4Parse makes through <see cref="IFileProvider"/> (see <see cref="ImportScope"/>).</remarks>
    bool IFileProvider.TryLoadPackage(string path, [MaybeNullWhen(false)] out IPackage package)
    {
        if (_importScope is not { } scope)
        {
            return TryLoadPackage(path, out package);
        }

        if (!scope.TryGetValue(path, out var kept))
        {
            if (scope.Count >= ImportScopeCapacity)
            {
                scope.Clear(); // a long scope keeps what it reuses most recently, not every package it ever met
            }

            scope[path] = kept = TryLoadPackage(path, out var loaded) ? loaded : null;
        }

        package = kept!;
        return kept is not null;
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _importScope = null;
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }

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
    /// Registers every plugin that has content under its mount name (<c>WoodlandHunterPack</c> ->
    /// <c>SCUM/Plugins/GameFeatures/WoodlandHunterPack</c>): the engine mounts a plugin's <c>Content</c> at <c>/&lt;Plugin&gt;/</c>
    /// and the DLC levels reference their Blueprint classes, templates and meshes that way; with the virtual path known,
    /// <c>FixPath</c> (and so every import CUE4Parse resolves) finds the files. What
    /// CUE4Parse's own <c>.uplugin</c> scan would record, without reading the plugin descriptors.
    /// </summary>
    /// <returns>Number of plugins added.</returns>
    public int RegisterPluginRoots()
    {
        var added = 0;
        foreach (var path in Files.Keys)
        {
            var plugins = path.IndexOf("/Plugins/", StringComparison.OrdinalIgnoreCase);
            var content = plugins < 0 ? -1 : path.IndexOf("/Content/", plugins, StringComparison.OrdinalIgnoreCase);
            if (content < 0)
            {
                continue;
            }

            var folder = path[..content];
            var name = folder[(folder.LastIndexOf('/') + 1)..];
            if (!VirtualPaths.ContainsKey(name))
            {
                VirtualPaths[name] = folder;
                added++;
            }
        }

        return added;
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
