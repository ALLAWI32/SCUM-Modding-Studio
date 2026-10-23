using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Core.Games;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.App.Services;

/// <summary>
/// "Check my install" for the app (Projects page button, and a line in the toast after every export): runs
/// <see cref="InstallCheck"/> on the folders of the Settings (game folder, server folder, export folder) for the open
/// project and words what it found in the UI language. Pak names and folders are shown, never the key it opens paks with.
/// </summary>
public static class InstallCheckService
{
    /// <summary>
    /// What the check looks at for <paramref name="project"/>: the Settings' folders, the export folder and mod name of the
    /// export, and the client paks an export just wrote (<paramref name="written"/>, after an export; null for the button).
    /// </summary>
    public static InstallCheckInput Input(AppServices services, Project project, string? exportFolder = null, string? modName = null, IEnumerable<string>? written = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(project);
        var settings = services.Settings.Load();
        services.Keys.TryGet(out var key);
        return new InstallCheckInput
        {
            ModNames = OwnPaks.ProjectNames(project.Manifest.Name, modName),
            GamePaks = PaksFolder(settings.GamePaksFolder),
            ServerPaks = ModExportService.ResolveServerPaks(settings.ServerPaksFolder) ?? PaksFolder(settings.ServerPaksFolder),
            ExportFolder = string.IsNullOrWhiteSpace(exportFolder)
                ? string.IsNullOrWhiteSpace(settings.ClientModsOutputFolder) ? ViewModels.ProjectsPageViewModel.DefaultExportFolder() : settings.ClientModsOutputFolder
                : exportFolder,
            HasRegistry = path => InstallCheck.HasRegistry(path, key),
            Written = written?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [],
            HasNewAssets = HasNewAssets(project),
        };
    }

    /// <summary>The client paks among an export's results (what <see cref="ToastLineAsync"/> checks the game's ~mods for).</summary>
    public static IReadOnlyList<string> ClientPaks(IEnumerable<ExportResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.Where(r => r.Role == ProjectSourceRole.Client && r.PakPath is not null).Select(r => r.PakPath!).ToList();
    }

    /// <summary>Runs the check on a worker thread (finding the server's folder can search the disk too).</summary>
    public static Task<IReadOnlyList<InstallFinding>> RunAsync(AppServices services, Project project, string? exportFolder = null, string? modName = null,
        IEnumerable<string>? written = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => InstallCheck.Check(Input(services, project, exportFolder, modName, written)), cancellationToken);

    /// <summary>The finding in the UI language, starting with whose folder it is about.</summary>
    public static string Describe(InstallFinding finding, InstallCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(input);
        var files = string.Join(", ", finding.Files);
        var paks = finding.Side == InstallSide.Server ? input.ServerPaks : input.GamePaks;
        var mods = paks is null ? GameInstall.ModsFolderName : Path.Combine(paks, GameInstall.ModsFolderName);
        var text = finding.Issue switch
        {
            InstallIssue.NoFolder => finding.Folder is null ? (finding.Side == InstallSide.Server ? Loc.T("Install.NoFolder.Server") : Loc.T("Install.NoFolder.Game")) : Loc.F("Install.NoFolder.Missing", finding.Folder),
            InstallIssue.NotInstalled => finding.Folder is null ? Loc.F("Install.NotInstalled.NoExport", mods) : Loc.F("Install.NotInstalled", mods, finding.Folder),
            InstallIssue.Installed => Loc.F("Install.Installed", files),
            InstallIssue.Stale => Loc.F("Install.Stale", files),
            InstallIssue.Missing => Loc.F("Install.Missing", files, finding.Folder ?? string.Empty),
            InstallIssue.NotCopied => Loc.F("Install.NotCopied", files, mods, finding.Folder ?? string.Empty),
            InstallIssue.OtherMode => Loc.F("Install.OtherMode", files),
            InstallIssue.InSubfolder => Loc.F("Install.InSubfolder", files, mods),
            InstallIssue.MissingRegistry => Loc.F("Install.MissingRegistry", files),
            InstallIssue.BothModes => Loc.F("Install.BothModes", files),
            InstallIssue.OldName => Loc.F("Install.OldName", files),
            InstallIssue.RegistryAbove => finding.Severity == InstallSeverity.Info ? Loc.F("Install.RegistryAbove.NoNew", files) : Loc.F("Install.RegistryAbove", files),
            InstallIssue.RegistryBelow => Loc.F("Install.RegistryBelow", files),
            InstallIssue.Unreadable => Loc.F("Install.Unreadable", files),
            InstallIssue.LooseInPaks => Loc.F("Install.LooseInPaks", files, finding.Folder ?? paks ?? string.Empty),
            InstallIssue.NoSig => Loc.F("Install.NoSig", files),
            InstallIssue.WrongCook => (finding.Side == InstallSide.Server ? Loc.F("Install.WrongCook.Server", files) : Loc.F("Install.WrongCook.Game", files)),
            InstallIssue.NoteFileOpenLog => Loc.T("Install.Note.FileOpenLog"),
            InstallIssue.NoteFriends => Loc.T("Install.Note.Friends"),
            _ => files,
        };
        return finding.Issue is InstallIssue.NoteFileOpenLog or InstallIssue.NoteFriends ? text : (finding.Side == InstallSide.Server ? Loc.T("Install.Side.Server") : Loc.T("Install.Side.Game")) + ": " + text;
    }

    /// <summary>The badge of a finding: OK, Note, Check or Fix.</summary>
    public static string Badge(InstallSeverity severity) => severity switch
    {
        InstallSeverity.Ok => Loc.T("Install.Badge.Ok"),
        InstallSeverity.Info => Loc.T("Install.Badge.Info"),
        InstallSeverity.Warning => Loc.T("Install.Badge.Warning"),
        _ => Loc.T("Install.Badge.Problem"),
    };

    /// <summary>
    /// One line for the toast after an export (the game's side; the Projects page's button says everything): this export is
    /// not in the game's ~mods yet (and this project's paks of the other install mode are there), things to fix, installed but
    /// older than this export, or all as it should be. Null when the game folder is not set. "As it should be" needs the
    /// export's own paks in the findings (<see cref="InstallCheckInput.Written"/>): without them it says only that the
    /// installed paks match the export folder.
    /// </summary>
    public static string? ToastLine(IReadOnlyList<InstallFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Any(f => f.Side == InstallSide.Game && f.Issue == InstallIssue.NoFolder))
        {
            return null;
        }

        var problems = findings.Count(f => f.Severity is InstallSeverity.Problem or InstallSeverity.Warning
                                           && f.Issue is not (InstallIssue.NotInstalled or InstallIssue.NotCopied or InstallIssue.OtherMode)
                                           && !(f.Side == InstallSide.Game && f.Issue is InstallIssue.Stale or InstallIssue.Missing));

        // The export is not (all) in the game's ~mods: where to copy it from comes first, then what else is there to fix.
        if (findings.FirstOrDefault(f => f.Side == InstallSide.Game && f.Issue is InstallIssue.NotInstalled or InstallIssue.NotCopied) is { } copy)
        {
            var line = copy.Folder is null ? Loc.T("Install.Toast.NotInstalledNoExport") : Loc.F("Install.Toast.NotInstalled", copy.Folder);
            if (findings.FirstOrDefault(f => f.Side == InstallSide.Game && f.Issue == InstallIssue.OtherMode) is { } otherMode)
            {
                line += " " + Loc.F("Install.Toast.OtherMode", string.Join(", ", otherMode.Files));
            }

            return problems > 0 ? line + " " + Loc.F("Install.Toast.Problems", problems) : line;
        }

        if (problems > 0)
        {
            return Loc.F("Install.Toast.Problems", problems);
        }

        return findings.Any(f => f.Side == InstallSide.Game && f.Issue is InstallIssue.Stale or InstallIssue.Missing)
            ? Loc.T("Install.Toast.CopyAgain")
            : Loc.T("Install.Toast.Ok");
    }

    /// <summary>
    /// <see cref="ToastLine"/> of the open project after an export into <paramref name="exportFolder"/> as
    /// <paramref name="modName"/> that wrote the client paks <paramref name="written"/> (<see cref="ClientPaks"/>); null when
    /// it cannot be said (no game folder set, or the check failed: the line is optional, the export's own toast stands).
    /// </summary>
    public static async Task<string?> ToastLineAsync(AppServices services, Project project, string? exportFolder = null, string? modName = null,
        IEnumerable<string>? written = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            return ToastLine(await RunAsync(services, project, exportFolder, modName, written).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            services.Logger.LogWarning(ex, "Check my install after the export failed: {Message}", ex.Message);
            return null; // the export's own toast stands without the line
        }
    }

    // Whether the project has new assets the game's asset list must name (clones, craftables); null when not known.
    private static bool? HasNewAssets(Project project)
    {
        if (project.State.AssetClones.Count > 0)
        {
            return true;
        }

        try
        {
            return CraftablesFile.Load(project.DirectoryPath).Items.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return null;
        }
    }

    // The Paks folder of a setting that points at it, or at its ~mods folder (where the mods are copied); null when not set or gone.
    private static string? PaksFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(Path.GetFileName(full), GameInstall.ModsFolderName, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(full) : full;
    }
}
