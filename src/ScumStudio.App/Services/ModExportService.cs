using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Games;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.Services;

/// <summary>What the Export card asks for.</summary>
/// <param name="Project">The open project.</param>
/// <param name="OutputFolder">Folder that receives <c>Client/</c> (and <c>Server/</c>).</param>
/// <param name="ModName">Pak name (default: the project name).</param>
/// <param name="IncludeServer">Also build the server pak from the configured server Paks folder.</param>
/// <param name="PakChunkIndex">The <c>pakchunkNNN</c> number.</param>
public sealed record ModExportRequest(
    Project Project,
    string OutputFolder,
    string? ModName,
    bool IncludeServer,
    int PakChunkIndex = ProjectExporter.DefaultPakChunkIndex);

/// <summary>
/// Runs <see cref="ProjectExporter"/> for the app: the client pak against the connected workspace catalog and, on request,
/// the server pak against the server Paks folder from the settings (opened with the stored key, never shown).
/// </summary>
public static class ModExportService
{
    /// <summary>Why <see cref="ExportAsync"/> cannot run right now, or null when it can.</summary>
    public static string? Blocker(AppServices services, Project? project)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (project is null)
        {
            return Localization.Loc.T("Export.OpenProjectFirst");
        }

        if (project.State.IsEmpty)
        {
            return Localization.Loc.T("Export.NothingYet");
        }

        if (services.Workspace.Catalog is null)
        {
            return Localization.Loc.T("Export.ConnectFirst");
        }

        return null;
    }

    /// <summary>Exports the client pak and optionally the server pak; returns one result per cook.</summary>
    /// <exception cref="InvalidOperationException">No catalog, no edits, or the server folder is missing.</exception>
    public static async Task<IReadOnlyList<ExportResult>> ExportAsync(
        AppServices services, ModExportRequest request, IProgressSink progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        if (Blocker(services, request.Project) is { } blocker)
        {
            throw new InvalidOperationException(blocker);
        }

        var catalog = services.Workspace.Catalog!;

        // Collision check: pieces the game's logs say got no collision since the last export are exported straight.
        var (straight, found) = CollisionDoctor.Check(request.Project, CollisionDoctor.GameLogs(services.Settings.Load().ServerPaksFolder));
        if (found.Count > 0)
        {
            services.Notifications.Warning(Localization.Loc.T("Collision.Title"),
                Localization.Loc.F("Collision.Replaced", found.Count, string.Join(", ", found.Take(6).Select(a => a.Actor))));
        }

        var options = new ExportOptions
        {
            OutputDirectory = request.OutputFolder,
            ModName = request.ModName,
            PakChunkIndex = request.PakChunkIndex,
            StraightPieces = straight,

            // Bent pieces' collision is built from the client's meshes for both cooks (the server cook has no geometry).
            BendMeshes = new BendSupport(catalog).Describe,

            // Imported mods go into both paks (the server needs a modded map's levels too).
            Mods = ProjectMods.Folders(request.Project.DirectoryPath),
        };
        var exporter = new ProjectExporter(services.Logger);
        var results = new List<ExportResult>(2);

        progress.Report(Localization.Loc.T("Export.ClientPak"), 0, 0);
        results.Add(await exporter.ExportAsync(request.Project, catalog, options, ProjectSourceRole.Client,
            new StepProgress(progress), cancellationToken).ConfigureAwait(false));
        CollisionDoctor.Exported(request.Project);
        if (results[0].SolidPieces > 0)
        {
            services.Notifications.Info(Localization.Loc.T("Collision.Title"), Localization.Loc.F("Collision.Solid", results[0].SolidPieces));
        }

        if (request.IncludeServer)
        {
            // The client pak is written by now: a server problem is reported, it never turns the export into a failure.
            var configured = services.Settings.Load().ServerPaksFolder;
            var serverFolder = ResolveServerPaks(configured);
            if (serverFolder is null)
            {
                services.Notifications.Warning(Localization.Loc.T("Export.ServerSkipped"), string.IsNullOrWhiteSpace(configured)
                    ? Localization.Loc.T("Export.NoServerFolder")
                    : Localization.Loc.F("Export.ServerNoCook", configured));
            }
            else
            {
                if (!string.Equals(Path.GetFullPath(serverFolder), Path.GetFullPath(configured!), StringComparison.OrdinalIgnoreCase))
                {
                    // Settings pointed at the server's mods folder: use the game paks next to it from now on, and keep the
                    // mods folder as the place the server pak is copied to.
                    services.UpdateSettings(s => s with
                    {
                        ServerPaksFolder = serverFolder,
                        ServerModsOutputFolder = string.IsNullOrWhiteSpace(s.ServerModsOutputFolder) ? configured : s.ServerModsOutputFolder,
                    });
                    services.Notifications.Info(Localization.Loc.T("Export.ServerFolderFixed"), Localization.Loc.F("Export.ServerFolderFixedDetail", serverFolder, configured));
                }

                try
                {
                    progress.Report(Localization.Loc.T("Export.OpeningServer"), 0, 0);
                    services.Keys.TryGet(out var key);
                    using var server = AssetCatalog.OpenPaks(serverFolder, new AssetCatalogOptions { AesKey = key, Logger = services.Logger, LooseOverlays = options.Mods });
                    progress.Report(Localization.Loc.T("Export.ServerPak"), 0, 0);
                    results.Add(await exporter.ExportAsync(request.Project, server, options, ProjectSourceRole.Server,
                        new StepProgress(progress), cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
                {
                    services.Logger.LogWarning("The server pak was not built: {Message}", ex.Message);
                    services.Notifications.Warning(Localization.Loc.T("Export.ServerSkipped"), ex.Message);
                }
            }
        }

        services.Logger.LogInformation("Exported {Name}: {Count} pak(s) to {Folder}.", results[0].ModName, results.Count, request.OutputFolder);
        return results;
    }

    /// <summary>True when <paramref name="folder"/> holds the stock server cook (<c>pakchunk0*-WindowsServer.pak</c>), not just mod paks.</summary>
    public static bool IsServerPaksFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
        && Directory.EnumerateFiles(folder, "pakchunk0*-WindowsServer.pak").Any();

    /// <summary>
    /// The folder with the server's stock cook for the configured <paramref name="folder"/>: the folder itself, or, when it
    /// is the server's mods folder (owner's setup: <c>C:\SCUMServer\server_mods</c>), the Paks folder of the
    /// <c>SCUMServer.exe</c> installed next to it. Null when there is none.
    /// </summary>
    public static string? ResolveServerPaks(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        if (IsServerPaksFolder(folder))
        {
            return folder;
        }

        var parent = Directory.GetParent(Path.GetFullPath(folder));
        if (parent?.Parent is null)
        {
            return null; // never search a whole drive
        }

        return GameLocator.FindDedicatedServersUnder(parent.FullName, maxDepth: 4, maxDirectories: 2_000)
            .Select(s => s.PaksDirectory)
            .FirstOrDefault(IsServerPaksFolder);
    }

    /// <summary>
    /// One-click export: the client pak into the last export folder (or Documents\ScumStudio Exports), the server pak too when
    /// the server Paks folder holds the stock server cook, and both copied (pak + sig) into the client/server mods folders
    /// set in Settings, so they are ready to load. Returns the results and the files copied into the mods folders.
    /// </summary>
    public static async Task<(IReadOnlyList<ExportResult> Results, IReadOnlyList<string> Installed)> QuickExportAsync(
        AppServices services, Project project, string defaultFolder, IProgressSink progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(project);
        var settings = services.Settings.Load();
        var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.ClientModsOutputFolder) ? defaultFolder : settings.ClientModsOutputFolder);
        var request = new ModExportRequest(project, folder, null, ResolveServerPaks(settings.ServerPaksFolder) is not null);
        var results = await ExportAsync(services, request, progress, cancellationToken).ConfigureAwait(false);
        var installed = new List<string>();
        foreach (var result in results)
        {
            var target = result.Role == ProjectSourceRole.Server ? services.Settings.Load().ServerModsOutputFolder : null;
            if (string.IsNullOrWhiteSpace(target) || result.PakPath is null)
            {
                continue;
            }

            Directory.CreateDirectory(target);
            foreach (var file in new[] { result.PakPath, result.SigPath }.OfType<string>())
            {
                var copy = Path.Combine(target, Path.GetFileName(file));
                if (!string.Equals(Path.GetFullPath(copy), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, copy, overwrite: true);
                }

                installed.Add(copy);
            }
        }

        return (results, installed);
    }

    private sealed class StepProgress(IProgressSink sink) : IProgress<string>
    {
        public void Report(string value) => sink.Report(value, 0, 0);
    }
}
