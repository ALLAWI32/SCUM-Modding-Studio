using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;

namespace ScumStudio.App.Services;

/// <summary>
/// The open project's economy (<c>&lt;project&gt;/EconomyOverride.json</c>), kept in step with the map (owner, 2026-10-07):
/// a trader placed on the map gets its section at once, listing its type's stock with the game's values ("ready"); a placed
/// trader that is deleted or undone loses its section again (kept for this session, so undoing the delete brings its edits
/// back); the game's traders whose trade posts the project deleted are <see cref="RemovedStock"/>: hidden on the Economy page
/// and left out of the export, while their sections stay in the project file for when the delete is undone. Every change is
/// written at once and atomically; the file as it was before the first write of the session is kept as
/// <c>EconomyOverride.json.bak</c>.
/// </summary>
public sealed class ProjectEconomy : IDisposable
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private readonly AppServices _services;
    private readonly Dictionary<string, IReadOnlyList<TradeableOverride>> _stash = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<LevelDocument?>> _outposts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _placed = new(StringComparer.OrdinalIgnoreCase);
    private Project? _project;
    private AssetCatalog? _catalog;
    private Task<EconomyDefaults?>? _defaults;
    private Task<IReadOnlyList<TraderInfo>>? _gameTraders;
    private readonly object _gate = new();
    private bool _backedUp;
    private bool _waitingForOutposts;
    private bool _disposed;

    /// <summary>Creates the store; it follows the project session and the game files from now on.</summary>
    public ProjectEconomy(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _services.Projects.Changed += OnProjectChanged;
        _services.Workspace.CatalogChanged += OnCatalogChanged;
    }

    /// <summary>Raised on the UI thread when sections were added or removed, the game's tables arrived or <see cref="RemovedStock"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The economy being edited (the open project's, or the game's defaults without a project).</summary>
    public EconomyOverride Economy { get; private set; } = EconomyOverride.CreateDefault();

    /// <summary>The game's tradeables and traders once read (<see cref="LoadDefaultsAsync"/>), else null.</summary>
    public EconomyDefaults? Defaults => _defaults is { IsCompletedSuccessfully: true } t ? t.Result : null;

    /// <summary>The game's traders, when read (by <see cref="GameTradersAsync"/> or with the tables), else null.</summary>
    public IReadOnlyList<TraderInfo>? GameTraders =>
        Defaults?.Traders ?? (_gameTraders is { IsCompletedSuccessfully: true } t ? t.Result : null);

    /// <summary>The game's traders whose every trade post the project deleted (see <see cref="TraderPosts.RemovedStockTraders"/>).</summary>
    public IReadOnlySet<string> RemovedStock { get; private set; } = None;

    /// <summary>The live placed traders (name → type) as of the last sync.</summary>
    public IReadOnlyDictionary<string, string> Placed => _placed;

    /// <summary>Reads the game's economy tables once per game-files connection (on a worker); null without game files.</summary>
    public Task<EconomyDefaults?> LoadDefaultsAsync()
    {
        if (CurrentCatalog() is not { } catalog)
        {
            return Task.FromResult<EconomyDefaults?>(null);
        }

        if (_defaults is null || _defaults.IsFaulted)
        {
            var task = Task.Run<EconomyDefaults?>(() => EconomyDefaults.Read(catalog));
            _defaults = task;
            _ = task.ContinueWith(t => _services.Dispatcher.Post(() =>
            {
                if (!ReferenceEquals(_defaults, task))
                {
                    return;
                }

                if (t.IsFaulted)
                {
                    _services.Logger.LogWarning("The game's economy could not be read: {Message}", t.Exception?.GetBaseException().Message);
                    return;
                }

                Sync(force: true);
            }), TaskScheduler.Default);
        }

        return _defaults;
    }

    /// <summary>The game's traders (their personalities only: cheaper than the tables), read once per connection.</summary>
    public Task<IReadOnlyList<TraderInfo>> GameTradersAsync()
    {
        if (CurrentCatalog() is not { } catalog)
        {
            return Task.FromResult<IReadOnlyList<TraderInfo>>([]);
        }

        if (GameTraders is { } known)
        {
            return Task.FromResult(known);
        }

        if (_gameTraders is null || _gameTraders.IsFaulted)
        {
            _gameTraders = Task.Run(() => EconomyDefaults.ReadTraders(catalog));
        }

        return _gameTraders;
    }

    /// <summary>
    /// Writes <see cref="Economy"/> into the project (atomically; the file found before the session's first write is kept as
    /// <c>.bak</c>, so a file the app could not read is never lost). Without a project nothing is written.
    /// </summary>
    public void Save()
    {
        if (_project is not { } project || _disposed)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                SaveTo(project);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Economy.NotSaved"), ex.Message);
        }
    }

    private void SaveTo(Project project)
    {
        var path = Path.Combine(project.DirectoryPath, EconomyOverride.FileName);
        if (!_backedUp && File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        _backedUp = true;
        Economy.SaveTo(project.DirectoryPath);
    }

    /// <summary>The file to give the server: <see cref="Economy"/> with every placed trader's section, without <see cref="RemovedStock"/>.</summary>
    public EconomyOverride ForExport() =>
        _project is { } project ? ProjectExporter.EconomyFor(project.State, Economy, RemovedStock) ?? Economy : Economy;

    /// <summary>Brings <see cref="Economy"/> in step with the project now (the page calls it when it is shown).</summary>
    public void Refresh() => Sync(force: false);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _services.Projects.Changed -= OnProjectChanged;
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
    }

    private void OnProjectChanged(object? sender, EventArgs e) => Sync(force: false);

    private void OnCatalogChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        CurrentCatalog();
        Sync(force: true);
    });

    /// <summary>The connected game files; the caches made from other ones are dropped.</summary>
    private AssetCatalog? CurrentCatalog()
    {
        var catalog = _services.Workspace.Catalog;
        if (!ReferenceEquals(catalog, _catalog))
        {
            _catalog = catalog;
            _defaults = null;
            _gameTraders = null;
            _outposts.Clear();
        }

        return catalog;
    }

    /// <summary>
    /// Adds the sections of newly placed traders (a removed one's from the stash), lists a placed trader's stock in an empty
    /// section once the tables are read, removes the sections of placed traders that are gone and updates
    /// <see cref="RemovedStock"/>. Raises <see cref="Changed"/> when anything changed (always with <paramref name="force"/>).
    /// </summary>
    private void Sync(bool force)
    {
        if (_disposed)
        {
            return;
        }

        // Tests run the app's dispatcher inline (a worker's post runs on the worker): one sync at a time.
        lock (_gate)
        {
            SyncCore(force);
        }
    }

    private void SyncCore(bool force)
    {
        var project = _services.Projects.Current;
        if (!ReferenceEquals(project, _project))
        {
            _project = project;
            Load();
            force = true;
        }

        if (project is null)
        {
            if (force)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, op) in TraderPosts.Placed(project.State))
        {
            if (op.Trader is { Name.Length: > 0 } trader)
            {
                live.TryAdd(trader.Name, trader.Type);
            }
        }

        var changed = false;
        foreach (var gone in _placed.Keys.Where(n => !live.ContainsKey(n)).ToList())
        {
            if (Economy.RemoveSection(gone) is { } entries)
            {
                _stash[gone] = entries;
                changed = true;
                _services.Logger.LogInformation("Economy: {Trader} is no longer on the map; its section was removed.", gone);
            }
        }

        var defaults = Defaults;
        var needTables = false;
        foreach (var (name, type) in live)
        {
            if (!Economy.HasSection(name))
            {
                if (_stash.Remove(name, out var entries))
                {
                    Economy.SetSection(name, entries);
                }
                else
                {
                    Economy.EnsureSection(name);
                }

                changed = true;
            }

            if (Economy.Entries(name).Count == 0)
            {
                if (defaults is null)
                {
                    needTables = true;
                }
                else if (Economy.List(name, defaults.StockCodes(type)) is > 0 and var count)
                {
                    changed = true;
                    _services.Notifications.Success(Loc.T("Economy.Ready"), Loc.F("Economy.ReadyDetail", name, count));
                }
            }
        }

        _placed = live;
        if (changed)
        {
            Save();
        }

        if (needTables)
        {
            _ = LoadDefaultsAsync();
        }

        if (UpdateRemovedStock(project) || changed || force)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Load()
    {
        _stash.Clear();
        _placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _backedUp = false;
        RemovedStock = None;
        Economy = EconomyOverride.CreateDefault();
        if (_project is not { } project)
        {
            return;
        }

        try
        {
            Economy = EconomyOverride.LoadFrom(project.DirectoryPath) ?? Economy;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // The next write keeps the unreadable file as EconomyOverride.json.bak.
            _services.Notifications.Warning(Loc.T("Economy.BadFile"), ex.Message);
        }
    }

    /// <summary>
    /// The game's traders whose trade posts are deleted: the outpost levels with deletions are read once (on a worker) for
    /// their trade posts; returns true when <see cref="RemovedStock"/> changed.
    /// </summary>
    private bool UpdateRemovedStock(Project project)
    {
        var levels = project.State.DeletedActors.Select(a => a.Level).Distinct(StringComparer.OrdinalIgnoreCase).Where(TraderPosts.IsOutpostLevel).ToList();
        IReadOnlySet<string> removed = None;
        if (levels.Count > 0 && CurrentCatalog() is { } catalog)
        {
            var reads = levels.Select(level => Outpost(catalog, level)).ToList();
            if (!reads.All(r => r.IsCompleted))
            {
                if (!_waitingForOutposts)
                {
                    _waitingForOutposts = true;
                    _ = Task.WhenAll(reads).ContinueWith(_ => _services.Dispatcher.Post(() =>
                    {
                        _waitingForOutposts = false;
                        Sync(force: false);
                    }), TaskScheduler.Default);
                }

                return false;
            }

            removed = TraderPosts.RemovedStockTraders(project.State, reads.Where(r => r.IsCompletedSuccessfully && r.Result is not null).Select(r => r.Result!));
        }

        if (removed.SetEquals(RemovedStock))
        {
            return false;
        }

        RemovedStock = removed;
        return true;
    }

    /// <summary>An outpost level (its trade posts and their traders), read once per connection.</summary>
    private Task<LevelDocument?> Outpost(AssetCatalog catalog, string level)
    {
        if (!_outposts.TryGetValue(level, out var read))
        {
            var logger = _services.Logger;
            _outposts[level] = read = Task.Run<LevelDocument?>(() =>
            {
                try
                {
                    var reader = new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions { ReadInstances = false, ExpandBlueprintComponents = false, ExpandChildActors = false });
                    return LevelDocument.Load(reader, level, CancellationToken.None);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
                {
                    logger.LogWarning("{Level} could not be read for its traders: {Message}", level, ex.Message);
                    return null;
                }
            });
        }

        return read;
    }
}
