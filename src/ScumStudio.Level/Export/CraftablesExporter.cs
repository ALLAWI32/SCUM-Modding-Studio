using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
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
    /// registry of the same export (null: the source's own). Returns null when the list is empty.
    /// </summary>
    public static async Task<ExportResult?> ExportAsync(
        CraftablesFile craftables, string modName, AssetCatalog catalog, ExportOptions options, ProjectSourceRole role,
        byte[]? baseRegistry = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(craftables);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        logger ??= NullLogger.Instance;
        if (craftables.Items.Count == 0)
        {
            return null;
        }

        var name = ProjectExporter.SanitizeModName(modName) + Suffix;
        var roleDirectory = Path.Combine(Path.GetFullPath(options.OutputDirectory), ProjectExporter.RoleFolder(role));
        var staging = Path.Combine(roleDirectory, StagingFolderName);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        var plan = CraftablesPlanner.Plan(catalog, craftables.Items);
        var warnings = new List<string>(plan.Warnings);
        var built = AssetModBuilder.Build(catalog, plan.Request with { BaseRegistry = baseRegistry });
        warnings.AddRange(built.Warnings);
        var assets = new List<ExportedAsset>();
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
            pakPath = Path.Combine(roleDirectory, ProjectExporter.PakFileName(name, options.PakChunkIndex + 1));
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

    /// <summary>The registry the project's own pak staged in <paramref name="result"/>, or null.</summary>
    public static byte[]? StagedRegistry(ExportResult? result)
    {
        var path = result is null ? null : Path.Combine([result.StagingDirectory, .. AssetModBuilder.AssetRegistryPath.Split('/')]);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
