using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.ViewModels;

/// <summary>A recent project entry.</summary>
public sealed class RecentProjectViewModel
{
    /// <summary>Creates an entry.</summary>
    public RecentProjectViewModel(string path, IAsyncRelayCommand<string?> open)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.TrimEndingDirectorySeparator(path));
        Exists = Project.Exists(path);
        OpenCommand = open;
    }

    /// <summary>Project folder.</summary>
    public string Path { get; }

    /// <summary>Folder name without <c>.ssproj</c>.</summary>
    public string Name { get; }

    /// <summary>False when the folder no longer holds a project.</summary>
    public bool Exists { get; }

    /// <summary>Opens the project (parameter: <see cref="Path"/>).</summary>
    public IAsyncRelayCommand<string?> OpenCommand { get; }
}

/// <summary>Projects page: create/open projects, recent list, the open project's manifest and the mod export.</summary>
public sealed partial class ProjectsPageViewModel : PageViewModel, IDisposable
{
    private readonly AppServices _services;

    /// <summary>A view model for the Dump window.</summary>
    public DumpViewModel CreateDump() => new(_services);

    /// <summary>Creates the page.</summary>
    public ProjectsPageViewModel(AppServices services)
        : base("projects", "Projects", "Every edit lives in a project: a folder with project.json, journal.jsonl and notes.md")
    {
        _services = services;
        _newProjectFolder = DefaultProjectsFolder();
        var settings = _services.Settings.Load();
        _exportFolder = string.IsNullOrWhiteSpace(settings.ClientModsOutputFolder) ? DefaultExportFolder() : settings.ClientModsOutputFolder;
        _exportServer = !string.IsNullOrWhiteSpace(settings.ServerPaksFolder);
        _services.SettingsChanged += OnSettingsChanged;
        _services.Projects.Changed += OnProjectChanged;
        _services.Workspace.CatalogChanged += OnCatalogChanged;
        Refresh();
    }

    /// <summary>The project session.</summary>
    public ProjectSession Session => _services.Projects;

    /// <summary>Recent projects.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent))]
    private IReadOnlyList<RecentProjectViewModel> _recentProjects = [];

    /// <summary>Manifest rows of the open project.</summary>
    [ObservableProperty]
    private IReadOnlyList<PropertyRow> _currentRows = [];

    /// <summary>Name of the project to create.</summary>
    [ObservableProperty]
    private string _newProjectName = "MyMapMod";

    /// <summary>Parent folder of the project to create.</summary>
    [ObservableProperty]
    private string _newProjectFolder;

    /// <summary>Folder that receives <c>Client/</c> and <c>Server/</c> of the export.</summary>
    [ObservableProperty]
    private string _exportFolder;

    /// <summary>Pak name; empty = the project name.</summary>
    [ObservableProperty]
    private string _exportModName = string.Empty;

    /// <summary>Also build the server pak.</summary>
    [ObservableProperty]
    private bool _exportServer;

    /// <summary>Why the export button is disabled, or what it will do.</summary>
    [ObservableProperty]
    private string _exportHint = string.Empty;

    /// <summary>True when the export can run now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _canExport;

    /// <summary>Summary rows of the last export.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportResult))]
    private IReadOnlyList<PropertyRow> _exportRows = [];

    /// <summary>Warnings of the last export.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportWarnings))]
    private IReadOnlyList<string> _exportWarnings = [];

    /// <summary>Output folder of the last export (for "Open folder").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenExportFolderCommand))]
    private string? _lastExportFolder;

    /// <summary>Results of the last export (one per cook).</summary>
    public IReadOnlyList<ExportResult> LastExport { get; private set; } = [];

    /// <summary>True when there are recent projects.</summary>
    public bool HasRecent => RecentProjects.Count > 0;

    /// <summary>True after an export ran.</summary>
    public bool HasExportResult => ExportRows.Count > 0;

    /// <summary>True when the last export produced warnings.</summary>
    public bool HasExportWarnings => ExportWarnings.Count > 0;

    /// <summary><c>Documents/ScumStudio Projects</c> (or the user profile when Documents is unavailable).</summary>
    public static string DefaultProjectsFolder() => Path.Combine(DocumentsFolder(), "ScumStudio Projects");

    /// <summary><c>Documents/ScumStudio Exports</c>.</summary>
    public static string DefaultExportFolder() => Path.Combine(DocumentsFolder(), "ScumStudio Exports");

    /// <summary>Re-reads recent projects and the open project.</summary>
    public void Refresh()
    {
        var open = OpenRecentCommand;
        RecentProjects = _services.Settings.Load().RecentProjects.Select(p => new RecentProjectViewModel(p, open)).ToList();
        var project = _services.Projects.Current;
        CurrentRows = project is null
            ? []
            :
            [
                new PropertyRow(Loc.T("Projects.Row.Name"), project.Manifest.Name),
                new PropertyRow(Loc.T("Projects.Row.Folder"), project.DirectoryPath),
                new PropertyRow(Loc.T("Projects.Row.GameBuild"), project.Manifest.GameBuild),
                new PropertyRow(Loc.T("Projects.Row.Created"), project.Manifest.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                new PropertyRow(Loc.T("Projects.Row.Modified"), project.Manifest.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                new PropertyRow(Loc.T("Projects.Row.Sources"), project.Manifest.Sources.Count == 0 ? Loc.T("Projects.Row.None") : string.Join(", ", project.Manifest.Sources.Select(s => $"{s.Role} {s.Kind}"))),
                new PropertyRow(Loc.T("Projects.Row.Edits"), project.History.Count.ToString("N0", CultureInfo.CurrentCulture)),
                new PropertyRow(Loc.T("Projects.Row.LevelsToExport"), project.PendingExportSet.Count.ToString("N0", CultureInfo.CurrentCulture)),
                new PropertyRow(Loc.T("Projects.Row.Packages"), project.PendingAssetSet.Count.ToString("N0", CultureInfo.CurrentCulture)),
            ];

        if (project is not null && string.IsNullOrWhiteSpace(ExportModName))
        {
            ExportModName = project.Manifest.Name;
        }

        RefreshExportState();
    }

    /// <inheritdoc />
    public override void OnNavigatedTo() => Refresh();

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Refresh();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.SettingsChanged -= OnSettingsChanged;
        _services.Projects.Changed -= OnProjectChanged;
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
    }

    private static string DocumentsFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(documents) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : documents;
    }

    private void RefreshExportState()
    {
        var project = _services.Projects.Current;
        var blocker = ModExportService.Blocker(_services, project);
        CanExport = blocker is null;
        if (blocker is not null)
        {
            ExportHint = blocker;
            return;
        }

        var levels = project!.PendingExportSet.Count;
        var deleted = project.State.DeletedActors.Count;
        var assets = project.PendingAssetSet.Count;
        var clones = project.State.AssetClones.Count;
        var assetText = assets == 0 ? string.Empty : Loc.F("Projects.ExportHint.Assets", assets, clones);
        var pak = string.Create(CultureInfo.InvariantCulture,
            $"pakchunk{ProjectExporter.DefaultPakChunkIndex}-{ProjectExporter.SanitizeModName(string.IsNullOrWhiteSpace(ExportModName) ? project.Manifest.Name : ExportModName)}_P.pak");
        ExportHint = Loc.F("Projects.ExportHint", levels, deleted, assetText, pak);
    }

    partial void OnExportModNameChanged(string value) => RefreshExportState();

    private void OnSettingsChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(Refresh);

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        if (_services.Projects.Current is { } project && !string.Equals(ExportModName, project.Manifest.Name, StringComparison.Ordinal))
        {
            ExportModName = project.Manifest.Name;
        }

        Refresh();
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(RefreshExportState);

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProjectName) || string.IsNullOrWhiteSpace(NewProjectFolder))
        {
            _services.Notifications.Warning(Loc.T("Projects.MissingInfo"), Loc.T("Projects.MissingInfoDetail"));
            return;
        }

        var name = NewProjectName.Trim();
        var folder = NewProjectFolder.Trim();
        var (ok, project) = await _services.Operations.RunAsync(Loc.F("Projects.Creating", name),
            (_, ct) => _services.Projects.CreateAsync(folder, name, ct)).ConfigureAwait(true);
        if (ok && project is not null)
        {
            _services.Notifications.Success(Loc.T("Projects.Created"), project.DirectoryPath);
        }
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Projects.PickParent"), NewProjectFolder).ConfigureAwait(true) is { } folder)
        {
            NewProjectFolder = folder;
        }
    }

    [RelayCommand]
    private async Task BrowseExportFolderAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Projects.PickExport"), ExportFolder).ConfigureAwait(true) is { } folder)
        {
            ExportFolder = folder;
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Projects.PickProject")).ConfigureAwait(true) is { } folder)
        {
            await OpenRecentAsync(folder).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await _services.Operations.RunAsync(Loc.T("Projects.Opening"), (_, ct) => _services.Projects.OpenAsync(path, ct)).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Close() => _services.Projects.Close();

    /// <summary>Builds the mod pak(s) from the open project's edits into <see cref="ExportFolder"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var project = _services.Projects.Current;
        if (project is null || string.IsNullOrWhiteSpace(ExportFolder))
        {
            _services.Notifications.Warning(Loc.T("Projects.CannotExport"), ExportHint);
            return;
        }

        var folder = Path.GetFullPath(ExportFolder.Trim());
        var request = new ModExportRequest(project, folder, string.IsNullOrWhiteSpace(ExportModName) ? null : ExportModName.Trim(), ExportServer);
        var (ok, results) = await _services.Operations.RunAsync(Loc.F("Projects.Exporting", project.Manifest.Name),
            (progress, ct) => ModExportService.ExportAsync(_services, request, progress, ct)).ConfigureAwait(true);
        if (!ok || results is null)
        {
            return;
        }

        LastExport = results;
        LastExportFolder = folder;
        var rows = new List<PropertyRow>();
        var warnings = new List<string>();
        foreach (var result in results)
        {
            rows.Add(new PropertyRow(Loc.F("Projects.Row.Pak", result.Role), result.PakPath ?? Loc.T("Projects.Row.NotWritten")));
            rows.Add(new PropertyRow(Loc.F("Projects.Row.Sig", result.Role), result.SigPath ?? Loc.T("Projects.Row.NoSig")));
            rows.Add(new PropertyRow(Loc.F("Projects.Row.Levels", result.Role),
                Loc.F("Projects.Row.LevelsValue", result.Levels.Count, result.RemovedActorCount, result.PatchedTransformCount)));
            warnings.AddRange(result.Warnings.Select(w => $"{result.Role}: {w}"));
        }

        rows.Add(new PropertyRow(Loc.T("Projects.Row.Report"), results[0].ReportPath ?? string.Empty));
        ExportRows = rows;
        ExportWarnings = warnings;
        _services.UpdateSettings(s => s with { ClientModsOutputFolder = folder });

        if (warnings.Count > 0)
        {
            _services.Notifications.Warning(Loc.T("Projects.ExportWarnings"), Loc.F("Projects.ExportWarningsDetail", warnings.Count, folder));
        }
        else
        {
            _services.Notifications.Success(Loc.T("Projects.Exported"), results[0].PakPath ?? folder);
        }
    }

    private bool CanOpenExportFolder() => !string.IsNullOrEmpty(LastExportFolder) && Directory.Exists(LastExportFolder);

    [RelayCommand(CanExecute = nameof(CanOpenExportFolder))]
    private void OpenExportFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(LastExportFolder!) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or FileNotFoundException)
        {
            _services.Notifications.Info(Loc.T("Projects.ExportFolder"), LastExportFolder);
        }
    }
}
