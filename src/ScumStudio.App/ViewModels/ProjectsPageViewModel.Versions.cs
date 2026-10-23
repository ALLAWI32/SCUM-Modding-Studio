using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.ViewModels;

/// <summary>One saved snapshot of the open project, with Open as new project and Restore (asks first).</summary>
public sealed partial class SnapshotRowViewModel : ObservableObject
{
    private readonly Func<SnapshotRowViewModel, Task> _openAsNew;
    private readonly Func<SnapshotRowViewModel, Task> _restore;

    /// <summary>Creates the row for <paramref name="snapshot"/>.</summary>
    public SnapshotRowViewModel(ProjectSnapshot snapshot, Func<SnapshotRowViewModel, Task> openAsNew, Func<SnapshotRowViewModel, Task> restore)
    {
        Snapshot = snapshot;
        _openAsNew = openAsNew;
        _restore = restore;
    }

    /// <summary>The snapshot.</summary>
    public ProjectSnapshot Snapshot { get; }

    /// <summary>Its name, or its time when it has none.</summary>
    public string Name => Snapshot.Name.Length > 0 ? Snapshot.Name : Time;

    /// <summary>When it was saved (local time).</summary>
    public string Time => Snapshot.Taken.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>"22/10/2026 14:05 · Edits: 12 · 3.4 MB".</summary>
    public string Details => Loc.F("Projects.Snapshot.Details", Time, Snapshot.Edits, Size(Snapshot.Bytes));

    /// <summary>True while Restore waits for the confirmation.</summary>
    [ObservableProperty]
    private bool _isConfirming;

    /// <summary>"Put the project back as it was at …?".</summary>
    public string ConfirmText => Loc.F("Projects.Snapshot.Confirm", Name);

    /// <summary>A new project from this snapshot (the project stays as it is).</summary>
    [RelayCommand]
    private Task OpenAsNewAsync() => _openAsNew(this);

    /// <summary>First click: asks. The confirmation restores.</summary>
    [RelayCommand]
    private void Restore() => IsConfirming = true;

    /// <summary>Restores (the project as it is now is saved as a snapshot first).</summary>
    [RelayCommand]
    private Task ConfirmRestoreAsync()
    {
        IsConfirming = false;
        return _restore(this);
    }

    /// <summary>Keeps the project as it is.</summary>
    [RelayCommand]
    private void CancelRestore() => IsConfirming = false;

    /// <summary>"3.4 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, bytes / 1024):N0} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024d * 1024):N1} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024d * 1024 * 1024):N2} GB"),
    };
}

/// <summary>One line of "Check my install": a badge (OK, Note, Check, Fix) and what was found.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Badge">The badge's text.</param>
/// <param name="Text">What was found and what to do.</param>
public sealed record InstallRowViewModel(InstallSeverity Severity, string Badge, string Text)
{
    /// <summary>A problem to fix.</summary>
    public bool IsProblem => Severity == InstallSeverity.Problem;

    /// <summary>Something to check.</summary>
    public bool IsWarning => Severity == InstallSeverity.Warning;

    /// <summary>As it should be.</summary>
    public bool IsOk => Severity == InstallSeverity.Ok;
}

/// <summary>
/// Copies of the open project (Discord: salvador.garcia, pheidian): Duplicate project, snapshots with Open as new project and
/// Restore; and Check my install (hexter_98, youngpiegus).
/// </summary>
public sealed partial class ProjectsPageViewModel
{
    private string? _snapshotsOf;

    /// <summary>Name of the copy Duplicate project makes (default "&lt;project&gt; copy").</summary>
    [ObservableProperty]
    private string _duplicateName = string.Empty;

    /// <summary>Name of the next snapshot (may stay empty: the time names it).</summary>
    [ObservableProperty]
    private string _snapshotName = string.Empty;

    /// <summary>The open project's snapshots, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshots))]
    private IReadOnlyList<SnapshotRowViewModel> _snapshots = [];

    /// <summary>True when the open project has snapshots.</summary>
    public bool HasSnapshots => Snapshots.Count > 0;

    /// <summary>What the last Check my install found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallRows))]
    private IReadOnlyList<InstallRowViewModel> _installRows = [];

    /// <summary>True after Check my install ran.</summary>
    public bool HasInstallRows => InstallRows.Count > 0;

    /// <summary>Re-reads the snapshots (and the default copy name) when another project opened, or with <paramref name="force"/>.</summary>
    private void RefreshVersions(bool force = false)
    {
        var project = _services.Projects.Current;
        if (project is null)
        {
            _snapshotsOf = null;
            Snapshots = [];
            InstallRows = [];
            return;
        }

        if (!force && string.Equals(_snapshotsOf, project.DirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(_snapshotsOf, project.DirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            InstallRows = []; // another project: the last check was about the one before
            DuplicateName = ProjectCopies.FreeName(_services.ProjectsFolder, Loc.F("Projects.Copy.DefaultName", project.Manifest.Name));
        }

        _snapshotsOf = project.DirectoryPath;
        Snapshots = ProjectCopies.Snapshots(project.DirectoryPath).Select(s => new SnapshotRowViewModel(s, OpenSnapshotAsNewAsync, RestoreSnapshotAsync)).ToList();
    }

    /// <summary>Copies the open project to a new project (<see cref="DuplicateName"/>) in the projects folder and opens the copy.</summary>
    [RelayCommand]
    private async Task DuplicateAsync()
    {
        var project = _services.Projects.Current;
        if (project is null)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Projects.Copy.NeedProject"));
            return;
        }

        var name = string.IsNullOrWhiteSpace(DuplicateName) ? ProjectCopies.FreeName(_services.ProjectsFolder, Loc.F("Projects.Copy.DefaultName", project.Manifest.Name)) : DuplicateName.Trim();
        var source = project.DirectoryPath;
        var (ok, copy) = await _services.Operations.RunAsync(Loc.F("Projects.Copy.Running", project.Manifest.Name),
            (progress, ct) => _services.Projects.DuplicateAsync(source, name, progress, ct)).ConfigureAwait(true);
        if (ok && copy is not null)
        {
            _services.Notifications.Success(Loc.T("Projects.Copy.Done"), Loc.F("Projects.Copy.DoneDetail", copy.Manifest.Name, copy.DirectoryPath));
        }

        Refresh();
    }

    /// <summary>Saves a snapshot of the open project (<see cref="SnapshotName"/>).</summary>
    [RelayCommand]
    private async Task SaveSnapshotAsync()
    {
        if (!_services.Projects.HasProject)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Projects.Copy.NeedProject"));
            return;
        }

        var name = SnapshotName;
        var (ok, snapshot) = await _services.Operations.RunAsync(Loc.T("Projects.Snapshot.Running"),
            (progress, ct) => _services.Projects.SaveSnapshotAsync(name, progress, ct)).ConfigureAwait(true);
        if (ok && snapshot is not null)
        {
            SnapshotName = string.Empty;
            _services.Notifications.Success(Loc.T("Projects.Snapshot.Saved"), Loc.F("Projects.Snapshot.SavedDetail", snapshot.Path, snapshot.Edits));
        }

        RefreshVersions(force: true);
    }

    private async Task OpenSnapshotAsNewAsync(SnapshotRowViewModel row)
    {
        var project = _services.Projects.Current;
        var baseName = Loc.F("Projects.Snapshot.CopyName", project?.Manifest.Name ?? string.Empty, row.Name).Trim();
        var name = ProjectCopies.FreeName(_services.ProjectsFolder, baseName);
        var (ok, copy) = await _services.Operations.RunAsync(Loc.F("Projects.Copy.Running", row.Name),
            (progress, ct) => _services.Projects.DuplicateAsync(row.Snapshot.Path, name, progress, ct)).ConfigureAwait(true);
        if (ok && copy is not null)
        {
            _services.Notifications.Success(Loc.T("Projects.Copy.Done"), Loc.F("Projects.Copy.DoneDetail", copy.Manifest.Name, copy.DirectoryPath));
        }

        Refresh();
    }

    private async Task RestoreSnapshotAsync(SnapshotRowViewModel row)
    {
        var (ok, before) = await _services.Operations.RunAsync(Loc.F("Projects.Snapshot.Restoring", row.Name),
            (progress, ct) => _services.Projects.RestoreSnapshotAsync(row.Snapshot.Path, Loc.F("Projects.Snapshot.BeforeName", row.Name), progress, ct)).ConfigureAwait(true);
        if (ok && before is not null)
        {
            _services.Notifications.Success(Loc.T("Projects.Snapshot.Restored"), Loc.F("Projects.Snapshot.RestoredDetail", row.Name, before.Name));
        }

        RefreshVersions(force: true);
    }

    /// <summary>Looks into the game's and the server's mods folders for this project's paks and what stands in their way.</summary>
    [RelayCommand]
    private async Task CheckInstallAsync()
    {
        var project = _services.Projects.Current;
        if (project is null)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Install.NeedProject"));
            return;
        }

        var folder = string.IsNullOrWhiteSpace(ExportFolder) ? null : ExportFolder.Trim();
        var modName = string.IsNullOrWhiteSpace(ExportModName) ? null : ExportModName.Trim();
        InstallCheckInput? input = null;
        var (ok, findings) = await _services.Operations.RunAsync(Loc.T("Install.Running"), (_, ct) => Task.Run(() =>
        {
            input = InstallCheckService.Input(_services, project, folder, modName); // finding the server's folder can search the disk
            return InstallCheck.Check(input);
        }, ct)).ConfigureAwait(true);
        if (!ok || findings is null || input is null)
        {
            return;
        }

        InstallRows = findings.Select(f => new InstallRowViewModel(f.Severity, InstallCheckService.Badge(f.Severity), InstallCheckService.Describe(f, input))).ToList();
        var problems = findings.Count(f => f.Severity is InstallSeverity.Problem or InstallSeverity.Warning);
        if (problems > 0)
        {
            _services.Notifications.Warning(Loc.T("Install.Title"), Loc.F("Install.FoundProblems", problems));
        }
        else
        {
            _services.Notifications.Success(Loc.T("Install.Title"), Loc.T("Install.AllGood"));
        }
    }
}
