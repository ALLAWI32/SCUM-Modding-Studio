using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.ViewModels;

/// <summary>One card of "Your projects": a project found in the projects folder or opened before.</summary>
public sealed class ProjectCardViewModel
{
    /// <summary>Creates the card, reading the edit count and last-edit time from the folder.</summary>
    public ProjectCardViewModel(string path, bool isCurrent, IAsyncRelayCommand<string?> open, IRelayCommand<string?> showInFolder)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.TrimEndingDirectorySeparator(path));
        Exists = Project.Exists(path);
        IsCurrent = isCurrent;
        OpenCommand = open;
        ShowInFolderCommand = showInFolder;
        if (Exists)
        {
            var journal = System.IO.Path.Combine(path, Project.JournalFileName);
            Edits = CountEdits(journal);
            LastEdited = new[] { journal, System.IO.Path.Combine(path, Project.ManifestFileName) }.Where(File.Exists).Max(File.GetLastWriteTime);
        }

        Details = Exists ? Loc.F("Projects.Card.Details", Edits, LastEdited.ToString("g", CultureInfo.CurrentCulture)) : path;
    }

    /// <summary>Project folder.</summary>
    public string Path { get; }

    /// <summary>Folder name without <c>.ssproj</c>.</summary>
    public string Name { get; }

    /// <summary>False when the folder no longer holds a project.</summary>
    public bool Exists { get; }

    /// <summary>True for the project open now.</summary>
    public bool IsCurrent { get; }

    /// <summary>True when another card has the same name: the card shows <see cref="Path"/> to tell them apart.</summary>
    public bool ShowFolder { get; set; }

    /// <summary>Number of edits in the journal (undone ones included, as in the open project's "Edits" row).</summary>
    public int Edits { get; }

    /// <summary>Last time the journal or manifest was written (local time).</summary>
    public DateTime LastEdited { get; }

    /// <summary>"Edits: 12 · last edited 06/10/2026 14:02", or the path of a missing project.</summary>
    public string Details { get; }

    /// <summary>Opens the project (parameter: <see cref="Path"/>).</summary>
    public IAsyncRelayCommand<string?> OpenCommand { get; }

    /// <summary>Shows the project folder in the file manager (parameter: <see cref="Path"/>).</summary>
    public IRelayCommand<string?> ShowInFolderCommand { get; }

    // ponytail: counts lines by text instead of parsing JSON; the journal is compact and "type" is a top-level field.
    private static int CountEdits(string journal)
    {
        try
        {
            // The open project keeps its journal open for appending: share write access to read it.
            using var reader = new StreamReader(new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var count = 0;
            while (reader.ReadLine() is { } line)
            {
                count += line.Contains("\"type\":\"edit\"", StringComparison.Ordinal) ? 1 : 0;
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>A project opened before whose folder is gone: "Not found: C:\...\MyMapMod.ssproj".</summary>
public sealed record MissingProjectRow(string Path)
{
    /// <summary>The line shown for it.</summary>
    public string Text => Loc.F("Projects.NotFound", Path);
}

/// <summary>A mod imported into the open project (see <see cref="ProjectMods"/>).</summary>
public sealed partial class ImportedModViewModel : ObservableObject
{
    private readonly Func<ImportedModViewModel, Task> _remove;

    /// <summary>Creates the entry for the unpacked mod in <paramref name="folder"/>.</summary>
    public ImportedModViewModel(string folder, Func<ImportedModViewModel, Task> remove)
    {
        Folder = folder;
        _remove = remove;
        Name = System.IO.Path.GetFileName(folder);
        var files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList() : [];
        Summary = Loc.F("Mods.Summary", files.Count(f => f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)),
            files.Count(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The unpacked mod's folder.</summary>
    public string Folder { get; }

    /// <summary>The mod's name (its pak name).</summary>
    public string Name { get; }

    /// <summary>"3 maps, 120 assets".</summary>
    public string Summary { get; }

    /// <summary>
    /// True when Export mod puts the mod into the project's pak (the default); off, it stays installed as its own pak (a
    /// big map) and the project's pak, mounted after it, still wins where both have a file.
    /// </summary>
    public bool IsCarried
    {
        get => ProjectMods.IsCarried(Folder);
        set
        {
            ProjectMods.SetCarried(Folder, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Takes the mod out of the project.</summary>
    [RelayCommand]
    private Task RemoveAsync() => _remove(this);
}

/// <summary>Projects page: your projects (one click to open), create/open, the open project's manifest and the mod export.</summary>
public sealed partial class ProjectsPageViewModel : PageViewModel, IDisposable
{
    private readonly AppServices _services;
    private string? _shownCurrent;

    /// <summary>A view model for the Dump window.</summary>
    public DumpViewModel CreateDump() => new(_services);

    /// <summary>Creates the page.</summary>
    public ProjectsPageViewModel(AppServices services)
        : base("projects", "Projects", "Every edit lives in a project: a folder with project.json, journal.jsonl and notes.md")
    {
        _services = services;
        _newProjectFolder = services.ProjectsFolder;
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

    /// <summary>Your projects: every project in the projects folder and every one opened before; the open one, then newest edit first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjects), nameof(RecentProject), nameof(HasRecentProject), nameof(OpenRecentText))]
    private IReadOnlyList<ProjectCardViewModel> _projects = [];

    /// <summary>Projects opened before whose folder is gone (listed under the cards with Remove).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingProjects))]
    private IReadOnlyList<MissingProjectRow> _missingProjects = [];

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

    /// <summary>True when <see cref="Projects"/> has a card.</summary>
    public bool HasProjects => Projects.Count > 0;

    /// <summary>True when a project opened before is gone.</summary>
    public bool HasMissingProjects => MissingProjects.Count > 0;

    /// <summary>The project offered by "Open MyMapMod" while none is open: the last one opened, else the last one edited.</summary>
    public ProjectCardViewModel? RecentProject =>
        _services.Settings.Load().RecentProjects.Select(p => Projects.FirstOrDefault(c => PathComparer.Equals(c.Path, Path.TrimEndingDirectorySeparator(p))))
            .FirstOrDefault(c => c is not null) ?? Projects.FirstOrDefault();

    /// <summary>True when there is a project to offer in the empty "Current project" card.</summary>
    public bool HasRecentProject => RecentProject is not null;

    /// <summary>"Open MyMapMod".</summary>
    public string OpenRecentText => RecentProject is { } recent ? Loc.F("Projects.OpenNamed", recent.Name) : string.Empty;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>True after an export ran.</summary>
    public bool HasExportResult => ExportRows.Count > 0;

    /// <summary>True when the last export produced warnings.</summary>
    public bool HasExportWarnings => ExportWarnings.Count > 0;

    /// <summary><c>Documents/ScumStudio Projects</c> (or the user profile when Documents is unavailable).</summary>
    public static string DefaultProjectsFolder() => Path.Combine(DocumentsFolder(), "ScumStudio Projects");

    /// <summary><c>Documents/ScumStudio Exports</c>.</summary>
    public static string DefaultExportFolder() => Path.Combine(DocumentsFolder(), "ScumStudio Exports");

    /// <summary>
    /// The project folders to list: <c>*.ssproj</c> folders with a <c>project.json</c> in <paramref name="folders"/>, then
    /// <paramref name="recent"/> (kept even when gone, so the card can say "missing"); duplicates removed.
    /// </summary>
    public static IReadOnlyList<string> FindProjects(IEnumerable<string> folders, IEnumerable<string> recent)
    {
        var found = new List<string>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                found.AddRange(Directory.EnumerateDirectories(Path.GetFullPath(folder), "*" + Project.FolderExtension).Where(Project.Exists));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder lists nothing; the recent list still shows its projects.
            }
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return found.Concat(recent).Select(Path.TrimEndingDirectorySeparator).Distinct(comparer).ToList();
    }

    /// <summary>
    /// Rebuilds the "Your projects" cards (the projects folder, the new-project folder and the recent list); recent
    /// projects whose folder is gone go to <see cref="MissingProjects"/> instead.
    /// </summary>
    public void RefreshProjects()
    {
        var current = _shownCurrent = _services.Projects.DirectoryPath;
        var cards = FindProjects([_services.ProjectsFolder, NewProjectFolder], _services.Settings.Load().RecentProjects)
            .Select(p => new ProjectCardViewModel(p, current is not null && PathComparer.Equals(p, current), OpenRecentCommand, ShowInFolderCommand))
            .OrderByDescending(c => c.IsCurrent)
            .ThenByDescending(c => c.LastEdited)
            .ToList();
        foreach (var twin in cards.Where(c => c.Exists).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g))
        {
            twin.ShowFolder = true;
        }

        MissingProjects = cards.Where(c => !c.Exists).Select(c => new MissingProjectRow(c.Path)).ToList();
        Projects = cards.Where(c => c.Exists).ToList();
    }

    /// <summary>Takes a project whose folder is gone off the recent list (nothing on disk changes).</summary>
    [RelayCommand]
    private void RemoveMissing(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _services.UpdateSettings(s => s with
        {
            RecentProjects = s.RecentProjects.Where(p => !PathComparer.Equals(Path.TrimEndingDirectorySeparator(p), path)).ToList(),
            LastProjectPath = s.LastProjectPath is { } last && PathComparer.Equals(Path.TrimEndingDirectorySeparator(last), path) ? null : s.LastProjectPath,
        }); // SettingsChanged refreshes the page
    }

    /// <summary>Re-reads your projects and the open project.</summary>
    public void Refresh()
    {
        RefreshProjects();
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        ImportedMods = _services.Projects.Mods.Select(f => new ImportedModViewModel(f, RemoveModAsync)).ToList();
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

        // Every edit raises Changed: re-read the project folders only when another project opened or it closed.
        if (!string.Equals(_services.Projects.DirectoryPath, _shownCurrent, StringComparison.Ordinal))
        {
            RefreshProjects();
        }

        RefreshCurrent();
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
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Projects.PickProject"), _services.ProjectsFolder).ConfigureAwait(true) is { } folder)
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

    /// <summary>Mods made by others imported into the open project (read over the game files, carried by the export).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportedMods))]
    private IReadOnlyList<ImportedModViewModel> _importedMods = [];

    /// <summary>True when the open project has imported mods.</summary>
    public bool HasImportedMods => ImportedMods.Count > 0;

    /// <summary>Imports someone's mod pak into the open project (owner: "add other people's mods, their cars, their map").</summary>
    [RelayCommand]
    private async Task ImportModAsync()
    {
        if (!_services.Projects.HasProject)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Mods.NeedProject"));
            return;
        }

        var exports = Directory.Exists(Path.Combine(ExportFolder, "Client")) ? Path.Combine(ExportFolder, "Client") : ExportFolder;
        if (await _services.Dialogs.OpenFileAsync(Loc.T("Mods.Pick"), "pak", Loc.T("Mods.PakFiles"), exports).ConfigureAwait(true) is not { } pak)
        {
            return;
        }

        var (ok, result) = await _services.Operations.RunAsync(Loc.F("Mods.Importing", System.IO.Path.GetFileName(pak)),
            (_, ct) => _services.Projects.ImportModAsync(pak, ct)).ConfigureAwait(true);
        if (ok)
        {
            _services.Notifications.Success(Loc.T("Mods.Imported"), Loc.F("Mods.ImportedDetail", System.IO.Path.GetFileName(pak), result.Files));
        }

        Refresh();
    }

    private async Task RemoveModAsync(ImportedModViewModel mod)
    {
        await _services.Projects.RemoveModAsync(mod.Folder).ConfigureAwait(true);
        Refresh();
    }

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
    private void OpenExportFolder() => Reveal(LastExportFolder!, select: false, Loc.T("Projects.ExportFolder"));

    /// <summary>Shows a project's folder selected in Explorer (its parent folder elsewhere).</summary>
    [RelayCommand]
    private void ShowInFolder(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Reveal(path, select: true, Loc.T("Projects.ShowInFolder"));
        }
    }

    private void Reveal(string path, bool select, string title)
    {
        try
        {
            Process.Start(select && OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new ProcessStartInfo(select ? Path.GetDirectoryName(path)! : path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or FileNotFoundException)
        {
            _services.Notifications.Info(title, path);
        }
    }
}
