using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Import;
using ScumStudio.Level.Projects;
using ScumStudio.Modding;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Level.Export;

/// <summary>
/// The Craftables mod: the project's craftables (<see cref="CraftablesFile"/>) built by <see cref="CraftablesPlanner"/> and
/// <see cref="AssetModBuilder"/> into a pak of their own, <c>pakchunk&lt;N+1&gt;-&lt;Name&gt;Craftables_P.pak</c> next to the
/// project's pak, with a copied stock <c>.sig</c>. Its <c>AssetRegistry.bin</c> is the project pak's (when that one
/// wrote one) plus the craftables' records: the highest pak's registry is the one the game reads, so it must keep the
/// project's vehicles and items too.
/// </summary>
public static class CraftablesExporter
{
    /// <summary>Appended to the mod name for the pak's name.</summary>
    public const string Suffix = "Craftables";

    /// <summary>Staging folder of the craftables pak inside the role folder.</summary>
    public const string StagingFolderName = "staging-craftables";

    /// <summary>
    /// Builds and packs <paramref name="craftables"/> for one cook. <paramref name="baseRegistry"/> is the project pak's
    /// registry of the same export (null: the source's own). Returns null when the list is empty, after deleting the
    /// Craftables pak an earlier export left in the role folder (<see cref="RemoveStale"/>).
    /// </summary>
    public static async Task<ExportResult?> ExportAsync(
        CraftablesFile craftables, string modName, AssetCatalog catalog, ExportOptions options, ProjectSourceRole role,
        byte[]? baseRegistry = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(craftables);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        logger ??= NullLogger.Instance;
        var name = ProjectExporter.SanitizeModName(modName) + Suffix;
        var roleDirectory = Path.Combine(Path.GetFullPath(options.OutputDirectory), ProjectExporter.RoleFolder(role));
        if (craftables.Items.Count == 0)
        {
            foreach (var removed in RemoveStale(roleDirectory, modName, options.PakChunkIndex))
            {
                logger.LogInformation("Craftables: removed {File} (the project has no craftables now).", removed);
            }

            return null;
        }

        var staging = Path.Combine(roleDirectory, StagingFolderName);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);

        var warnings = new List<string>();
        var items = new List<Craftable>();
        string? ImportedFolder(Craftable c) => c.Imported is null || craftables.ProjectDirectory is null ? null : Path.Combine(craftables.ProjectDirectory, c.Imported);
        foreach (var craftable in craftables.Items)
        {
            if (craftable.Imported is not null && !Directory.Exists(ImportedFolder(craftable)))
            {
                warnings.Add($"{craftable.Name}: left out (its imported model {craftable.Imported} is not in the project folder).");
                continue;
            }

            items.Add(craftable);
        }

        var plan = CraftablesPlanner.Plan(catalog, items);
        warnings.AddRange(plan.Warnings);
        var built = AssetModBuilder.Build(catalog, plan.Request with { BaseRegistry = baseRegistry });
        warnings.AddRange(built.Warnings);
        var assets = new List<ExportedAsset>();

        // Models imported from 3D files (UeCook) of the planned craftables: their cooked mesh, materials and textures ship
        // at their own paths (a craftable left out ships nothing).
        var planned = plan.Entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var folder in items.Where(c => planned.Contains(c.Name)).Select(ImportedFolder).OfType<string>())
        {
            UeCook.CopyTree(folder, staging);
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(AssetPaths.IsPackageFile))
            {
                var virtualPath = Path.GetRelativePath(folder, file).Replace('\\', '/');
                assets.Add(new ExportedAsset(AssetPaths.ToPackagePath(virtualPath, catalog.ProjectName), virtualPath, IsClone: false));
            }
        }
        foreach (var package in built.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = catalog.TryGetPackageFile(package.PackagePath, out var existing)
                ? existing.Path.Replace('\\', '/')
                : package.HeaderFilePath(catalog.ProjectName);
            var dot = virtualPath.LastIndexOf('.');
            var target = Path.Combine([staging, .. virtualPath[..dot].Split('/', StringSplitOptions.RemoveEmptyEntries)]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await package.Bytes.WriteAsync(target, virtualPath[dot..], cancellationToken).ConfigureAwait(false);
            if (package.UBulk is not null)
            {
                await File.WriteAllBytesAsync(target + ".ubulk", package.UBulk, cancellationToken).ConfigureAwait(false);
            }

            assets.Add(new ExportedAsset(package.PackagePath, virtualPath, package.IsClone));
        }

        if (assets.Count == 0)
        {
            throw new InvalidOperationException("No craftable was built. " + string.Join(" ", warnings));
        }

        if (built.AssetRegistry is not null)
        {
            var registry = Path.Combine([staging, .. AssetModBuilder.AssetRegistryPath.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
            await File.WriteAllBytesAsync(registry, built.AssetRegistry, cancellationToken).ConfigureAwait(false);
        }

        string? pakPath = null;
        string? sigPath = null;
        if (options.WritePak)
        {
            pakPath = Path.Combine(roleDirectory, PakFileName(modName, options.PakChunkIndex));
            var writer = new PakWriter(new PakWriterOptions { Compression = options.Compression, IncludeAssetRegistry = built.AssetRegistry is not null }, logger);
            await writer.WriteFromDirectoryAsync(staging, pakPath, null, cancellationToken).ConfigureAwait(false);
            var sigSource = options.StockSigPath ?? ProjectExporter.FindStockSig(catalog);
            if (sigSource is null)
            {
                warnings.Add($"No stock .sig found: copy {SigCopier.DefaultStockSigName} from the game's Paks folder to {SigCopier.GetSigPath(pakPath)}.");
            }
            else
            {
                sigPath = SigCopier.Copy(sigSource, pakPath, overwrite: true, logger);
            }
        }

        logger.LogInformation("Craftables: {Count} craftable(s), {Packages} package(s), {Registered} registry record(s).",
            plan.Entries.Count, assets.Count, built.Registered.Count);
        return new ExportResult
        {
            Role = role,
            ModName = name,
            StagingDirectory = staging,
            PakPath = pakPath,
            SigPath = sigPath,
            Levels = [],
            Assets = assets,
            Registered = built.Registered,
            AssetValues = built.Applied,
            Warnings = warnings,
        };
    }

    /// <summary>File name of the Craftables pak of <paramref name="modName"/> next to the project's pak of chunk <paramref name="pakChunkIndex"/>.</summary>
    public static string PakFileName(string modName, int pakChunkIndex) =>
        ProjectExporter.PakFileName(ProjectExporter.SanitizeModName(modName) + Suffix, pakChunkIndex + 1);

    /// <summary>
    /// Deletes the Craftables pak (and its .sig) of <paramref name="modName"/> from <paramref name="directory"/>: left from an
    /// export with craftables, the game would still load its recipes, and as the highest pak its old registry would hide the
    /// project pak's new records. Returns the deleted files.
    /// </summary>
    public static IReadOnlyList<string> RemoveStale(string directory, string modName, int pakChunkIndex)
    {
        var pak = Path.Combine(directory, PakFileName(modName, pakChunkIndex));
        var removed = new[] { pak, SigCopier.GetSigPath(pak) }.Where(File.Exists).ToList();
        removed.ForEach(File.Delete);
        return removed;
    }

    /// <summary>The registry the project's own pak staged in <paramref name="result"/>, or null.</summary>
    public static byte[]? StagedRegistry(ExportResult? result)
    {
        var path = result is null ? null : Path.Combine([result.StagingDirectory, .. AssetModBuilder.AssetRegistryPath.Split('/')]);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
