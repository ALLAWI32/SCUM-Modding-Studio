using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.Services;

/// <summary>
/// The open ScumStudio project (<c>.ssproj</c> folder) and its journal-backed history. Shared by the Map and
/// Projects pages and the top bar. Undo/redo go through <see cref="Project.Undo"/>/<see cref="Project.Redo"/>, so they
/// are persisted in <c>journal.jsonl</c> immediately.
/// </summary>
public sealed partial class ProjectSession : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly ILogger _logger;

    /// <summary>Creates the session (no project open).</summary>
    public ProjectSession(AppServices services, ILogger logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>Raised after a project was opened/closed or its journal changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The open project, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(DisplayName), nameof(DirectoryPath))]
    private Project? _current;

    /// <summary>True when a project is open.</summary>
    public bool HasProject => Current is not null;

    /// <summary>Project name, or "No project".</summary>
    public string DisplayName => Current?.Manifest.Name ?? Localization.Loc.T("Project.None");

    /// <summary>Project folder, or null.</summary>
    public string? DirectoryPath => Current?.DirectoryPath;

    /// <summary>History, newest first.</summary>
    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    /// <summary>True when there is an edit to undo.</summary>
    [ObservableProperty]
    private bool _canUndo;

    /// <summary>True when there is an edit to redo.</summary>
    [ObservableProperty]
    private bool _canRedo;

    /// <summary>Number of levels the applied edits touch (the pending export set).</summary>
    [ObservableProperty]
    private int _pendingLevelCount;

    /// <summary>Summary line such as "3 edits, 1 undone".</summary>
    [ObservableProperty]
    private string _historySummary = Localization.Loc.T("History.NoEdits");

    /// <summary>
    /// Creates <c>&lt;parent&gt;/&lt;name&gt;.ssproj</c> and opens it. Sources are taken from the configured game and
    /// server paks folders.
    /// </summary>
    /// <exception cref="IOException">The folder already holds a project.</exception>
    public async Task<Project> CreateAsync(string parentFolder, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var safeName = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var directory = Path.Combine(Path.GetFullPath(parentFolder), safeName + Project.FolderExtension);
        var settings = _services.Settings.Load();
        var sources = new List<ProjectSource>();
        if (!string.IsNullOrWhiteSpace(settings.GamePaksFolder) && Directory.Exists(settings.GamePaksFolder))
        {
            sources.Add(ProjectSource.Detect(settings.GamePaksFolder, ProjectSourceRole.Client));
        }

        if (!string.IsNullOrWhiteSpace(settings.ServerPaksFolder) && Directory.Exists(settings.ServerPaksFolder))
        {
            sources.Add(ProjectSource.Detect(settings.ServerPaksFolder, ProjectSourceRole.Server));
        }

        var project = await Project.CreateAsync(directory, name.Trim(), sources: sources, cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created project {Name} in {Folder}.", project.Manifest.Name, project.DirectoryPath);
        SetCurrent(project);
        return project;
    }

    /// <summary>Opens the project in <paramref name="path"/> (the folder or its <c>project.json</c>).</summary>
    public async Task<Project> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var project = await Project.OpenAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var warning in project.Journal.Warnings)
        {
            _logger.LogWarning("Journal: {Warning}", warning);
        }

        _logger.LogInformation("Opened project {Name} ({Count} journal entries).", project.Manifest.Name, project.History.Count);
        SetCurrent(project);
        return project;
    }

    /// <summary>Closes the open project.</summary>
    public void Close() => SetCurrent(null);

    /// <summary>Applies an edit to the open project.</summary>
    /// <exception cref="InvalidOperationException">No project is open, or the edit is invalid.</exception>
    public JournalEntry Apply(EditOp op)
    {
        var project = Current ?? throw new InvalidOperationException("No project is open.");
        var entry = project.Apply(op);
        Refresh();
        return entry;
    }

    /// <summary>Undoes the last edit; returns it or null.</summary>
    public JournalEntry? Undo()
    {
        var entry = Current?.Undo();
        Refresh();
        return entry;
    }

    /// <summary>Redoes the last undone edit; returns it or null.</summary>
    public JournalEntry? Redo()
    {
        var entry = Current?.Redo();
        Refresh();
        return entry;
    }

    /// <summary>Re-reads history and undo/redo state from the project.</summary>
    public void Refresh()
    {
        _services.Dispatcher.Invoke(() =>
        {
            History.Clear();
            var project = Current;
            if (project is null)
            {
                CanUndo = CanRedo = false;
                PendingLevelCount = 0;
                HistorySummary = Localization.Loc.T("History.NoProject");
            }
            else
            {
                var items = project.History;
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    History.Add(new HistoryItemViewModel(items[i]));
                }

                CanUndo = project.CanUndo;
                CanRedo = project.CanRedo;
                PendingLevelCount = project.PendingExportSet.Count;
                HistorySummary = Summarize(project);
            }

            OnPropertyChanged(nameof(DisplayName));
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Rebuilds the texts of the history panel and the project name after the UI language changed.</summary>
    public void OnLanguageChanged()
    {
        _services.Dispatcher.Invoke(() =>
        {
            var project = Current;
            HistorySummary = project is null ? Localization.Loc.T("History.NoProject") : Summarize(project);
            var rows = History.Select(h => new HistoryItemViewModel(h.Item)).ToList();
            History.Clear();
            rows.ForEach(History.Add);
            OnPropertyChanged(nameof(DisplayName));
        });
    }

    private static string Summarize(Project project)
    {
        var items = project.History;
        var applied = items.Count(i => i.Status == HistoryStatus.Applied);
        var undone = items.Count(i => i.Status == HistoryStatus.Undone);
        return items.Count == 0
            ? Localization.Loc.T("History.NoEdits")
            : Localization.Loc.F("History.Applied", applied) + (undone > 0 ? Localization.Loc.F("History.Undone", undone) : string.Empty)
              + Localization.Loc.F("History.Levels", project.PendingExportSet.Count)
              + (project.PendingAssetSet.Count > 0 ? Localization.Loc.F("History.Packages", project.PendingAssetSet.Count) : string.Empty)
              + Localization.Loc.T("History.ToExport");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Current?.Dispose();
        Current = null;
    }

    private void SetCurrent(Project? project)
    {
        _services.Dispatcher.Invoke(() =>
        {
            var old = Current;
            Current = project;
            if (!ReferenceEquals(old, project))
            {
                old?.Dispose();
            }
        });

        if (project is not null)
        {
            _services.UpdateSettings(s => s.WithRecentProject(project.DirectoryPath));
        }

        Refresh();
        _ = UseModsAsync();
    }

    /// <summary>The open project's imported mods (folders, oldest first); empty without a project.</summary>
    public IReadOnlyList<string> Mods => Current is { } project ? ProjectMods.Folders(project.DirectoryPath) : [];

    /// <summary>
    /// Imports someone's mod pak into the open project (unpacked into its <c>mods</c> folder) and reads it over the game
    /// files from now on; the export carries it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No project is open.</exception>
    /// <exception cref="InvalidDataException">The pak could not be opened.</exception>
    public async Task<(string Folder, int Files)> ImportModAsync(string pakPath, CancellationToken cancellationToken = default)
    {
        var project = Current ?? throw new InvalidOperationException(Localization.Loc.T("History.NoProject"));
        _services.Keys.TryGet(out var key);
        var result = await Task.Run(() => ProjectMods.ImportAsync(project.DirectoryPath, pakPath, key, cancellationToken), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Imported mod {Pak}: {Files} file(s).", Path.GetFileName(pakPath), result.Files);
        await UseModsAsync().ConfigureAwait(false);
        return result;
    }

    /// <summary>Removes an imported mod from the open project.</summary>
    public async Task RemoveModAsync(string folder)
    {
        ProjectMods.Remove(folder);
        await UseModsAsync().ConfigureAwait(false);
    }

    private async Task UseModsAsync()
    {
        try
        {
            await _services.Workspace.UseModsAsync(Mods, ProgressSink.Null).ConfigureAwait(false);
            _services.Dispatcher.Invoke(() => Changed?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException)
        {
            _logger.LogWarning("Imported mods not read: {Message}", ex.Message);
            _services.Notifications.Warning(Localization.Loc.T("Mods.NotRead"), ex.Message);
        }
    }
}
