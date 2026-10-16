using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;

namespace ScumStudio.App.Services;

/// <summary>What <see cref="ProjectEconomy.Notice"/> reports (the Economy page words it).</summary>
public enum EconomyNoticeKind
{
    /// <summary>The file found before the first write (one the app could not read, or writes differently) was kept as a <c>.bak</c>.</summary>
    BackupKept,

    /// <summary>The file was changed outside the app since its last write; that version was kept as a <c>.bak</c> before the next write.</summary>
    ChangedOutside,

    /// <summary>The game's economy tables could not be read: prices, pictures and stock are unknown.</summary>
    TablesFailed,
}

/// <summary>A notice of <see cref="ProjectEconomy"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Path">The backup written, when there is one.</param>
public sealed record EconomyNotice(EconomyNoticeKind Kind, string? Path);

/// <summary>What <see cref="ProjectEconomy.ApplyText"/> did.</summary>
/// <param name="Problems">What the validator found (with an error nothing was applied).</param>
/// <param name="Entry">The journal entry of the change, or null when nothing changed.</param>
/// <param name="Changes">How many single changes the entry holds.</param>
public sealed record EconomyApplyResult(IReadOnlyList<EconomyProblem> Problems, JournalEntry? Entry, int Changes);

/// <summary>What <see cref="ProjectEconomy.Import"/> did.</summary>
/// <param name="Imported">True when the file is the project's economy now.</param>
/// <param name="Problems">What the validator found (with an error nothing was imported).</param>
/// <param name="Diff">What changed, when imported.</param>
/// <param name="BackupPath">The copy of the project's file before the import, or null when it had none.</param>
/// <param name="KeptPlacedSections">Sections of the project's placed traders the imported file lacked, kept.</param>
/// <param name="Entry">The journal entry (one whole-file step; null when the file was the same).</param>
public sealed record EconomyImportResult(
    bool Imported, IReadOnlyList<EconomyProblem> Problems, EconomyDiff? Diff, string? BackupPath, int KeptPlacedSections, JournalEntry? Entry);

/// <summary>
/// The open project's economy (<c>&lt;project&gt;/EconomyOverride.json</c>), kept in step with the map and the journal.
/// Every edit is an <see cref="EconomyOp"/> in the project's journal (owner, 2026-10-12: "one journal op per change so
/// Ctrl+Z works"): the edit methods only journal it, and this store follows <see cref="Journal.Applied"/> on every change
/// of the project, applying what was done or redone and the inverse of what was undone, so the page, Ctrl+Z and MCP edits
/// take one path; the file holds the result, so nothing is replayed when a project opens. A trader placed on the map gets
/// its section at once, listing its type's stock with the game's values ("ready"); a placed trader that leaves the map
/// keeps its section, <see cref="Parked"/> (audit F100), and the game's traders whose trade posts the project deleted are
/// <see cref="RemovedStock"/>: both are hidden on the page and left out of the export. Every change is written at once and
/// atomically; a file the app did not write as it is (unreadable, written otherwise, or changed outside) is kept as a
/// timestamped <c>.bak</c> first.
/// </summary>
public sealed class ProjectEconomy : IDisposable
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private readonly AppServices _services;
    private readonly Dictionary<string, Task<LevelDocument?>> _outposts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _scanned = [];
    private readonly Dictionary<long, string[]> _placing = [];
    private readonly object _gate = new();
    private Dictionary<string, string> _placed = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<JournalEntry> _seen = [];
    private Project? _project;
    private AssetCatalog? _catalog;
    private Task<EconomyDefaults?>? _defaults;
    private Task<IReadOnlyList<TraderInfo>>? _gameTraders;
    private string? _text;
    private IReadOnlySet<string>? _hidden;
    private EconomyContext? _context;
    private string? _known;
    private (long Length, DateTime At)? _stamp;
    private bool _notSaved;
    private bool _waitingForOutposts;
    private bool _disposed;

    /// <summary>Creates the store; it follows the project session and the game files from now on.</summary>
    public ProjectEconomy(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _services.Projects.Changed += OnProjectChanged;
        _services.Workspace.CatalogChanged += OnCatalogChanged;
    }

    /// <summary>Raised on the UI thread when the economy changed, the game's tables arrived (or failed) or the hidden traders changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a backup was kept or the game's tables could not be read (see <see cref="EconomyNoticeKind"/>).</summary>
    public event EventHandler<EconomyNotice>? Notice;

    /// <summary>The economy being edited (the open project's, or the game's defaults without a project).</summary>
    public EconomyOverride Economy { get; private set; } = EconomyOverride.CreateDefault();

    /// <summary>The game's tradeables and traders once read (<see cref="LoadDefaultsAsync"/>), else null.</summary>
    public EconomyDefaults? Defaults => _defaults is { IsCompletedSuccessfully: true } t ? t.Result : null;

    /// <summary>The game's traders, when read (by <see cref="GameTradersAsync"/> or with the tables), else null.</summary>
    public IReadOnlyList<TraderInfo>? GameTraders =>
        Defaults?.Traders ?? (_gameTraders is { IsCompletedSuccessfully: true } t ? t.Result : null);

    /// <summary>The game's traders whose every trade post the project deleted (see <see cref="TraderPosts.RemovedStockTraders"/>).</summary>
    public IReadOnlySet<string> RemovedStock { get; private set; } = None;

    /// <summary>The placed traders off the map (<see cref="TraderPosts.ParkedTraders"/>: undone, deleted, or placed by an edit a later one discarded): their sections stay in the file.</summary>
    public IReadOnlySet<string> Parked { get; private set; } = None;

    /// <summary>The sections the page does not show and the export leaves out: <see cref="RemovedStock"/> (unless placed again) and <see cref="Parked"/>.</summary>
    public IReadOnlySet<string> Hidden => _hidden ??= RemovedStock.Where(n => !_placed.ContainsKey(n)).Concat(Parked).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The live placed traders (name → type) as of the last sync.</summary>
    public IReadOnlyDictionary<string, string> Placed => _placed;

    /// <summary>
    /// Why the project's file could not be read when it was opened (null when it was read). While it is set the store holds the
    /// game's defaults, so nothing is written over the file but a deliberate edit (after a backup), and every export leaves
    /// the economy out with a warning instead of writing the defaults as the server's economy. A successful edit, code apply
    /// or import clears it.
    /// </summary>
    public string? LoadError { get; private set; }

    /// <summary>True while a project is open: the edit methods journal their changes.</summary>
    public bool CanEdit => _project is not null && !_disposed;

    /// <summary>The file as the export writes it (<see cref="Hidden"/> sections left out), in the game's layout.</summary>
    public string Text => _text ??= Economy.ToJson(Hidden);

    /// <summary>The names <see cref="EconomyValidator"/> checks the code view against (made again after every change).</summary>
    public EconomyContext Context => _context ??= new EconomyContext(
        (GameTraders ?? []).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
        _placed.Keys.Concat(Parked).ToHashSet(StringComparer.OrdinalIgnoreCase),
        Hidden,
        Defaults is { } defaults ? new Func<string, TradeableDefault?>(defaults.Find) : null);

    /// <summary>Reads the game's economy tables once per game-files connection (on a worker); null without game files.</summary>
    public Task<EconomyDefaults?> LoadDefaultsAsync()
    {
        if (CurrentCatalog() is not { } catalog)
        {
            return _defaults is { IsCompletedSuccessfully: true } known ? known : Task.FromResult<EconomyDefaults?>(null);
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
                    // Audit F302: the page waited for tables that never came; it says so now.
                    _services.Logger.LogWarning("The game's economy could not be read: {Message}", t.Exception?.GetBaseException().Message);
                    Notice?.Invoke(this, new EconomyNotice(EconomyNoticeKind.TablesFailed, null));
                    Changed?.Invoke(this, EventArgs.Empty);
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
            return Task.FromResult<IReadOnlyList<TraderInfo>>(GameTraders ?? []);
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
    /// True when <paramref name="name"/> cannot be a new trader's name (audit F277): a game trader, a placed trader (on the
    /// map or parked) or any section of the file.
    /// </summary>
    public bool IsNameReserved(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && (_placed.ContainsKey(name) || Parked.Contains(name) || Economy.HasSection(name)
            || (GameTraders?.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? false));

    /// <summary>Sets one entry (null or the game's values: removed, except at a placed trader); returns the journal entry, or null when nothing changed.</summary>
    public JournalEntry? SetEntry(string trader, string code, TradeableOverride? value) =>
        SetEntries(trader, [(code, value)], $"Economy: {trader} {code}");

    /// <summary>
    /// Sets several entries of <paramref name="trader"/> as one journal entry (a <see cref="BatchOp"/> titled
    /// <paramref name="describe"/> when there is more than one change); only the entries that change are journaled. A
    /// placed trader keeps an entry at the game's values listed (its section lists its whole stock).
    /// </summary>
    public JournalEntry? SetEntries(string trader, IReadOnlyList<(string Code, TradeableOverride? Value)> changes, string describe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trader);
        ArgumentNullException.ThrowIfNull(changes);
        if (!CanEdit)
        {
            return null;
        }

        var keep = _placed.ContainsKey(trader) || Parked.Contains(trader);
        var probe = Economy.Clone();
        var steps = new List<EditOp>();
        foreach (var (code, value) in changes)
        {
            foreach (var step in probe.SetOps(trader, code, value is null ? null : value with { Code = code }, keep))
            {
                probe.Apply(step);
                steps.Add(step);
            }
        }

        return Record(steps, describe);
    }

    /// <summary>Sets a setting (null: the game's value; a key the game does not write is removed); null when nothing changed.</summary>
    public JournalEntry? SetSetting(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!CanEdit)
        {
            return null;
        }

        var text = value ?? EconomyOverride.GameDefault(key);
        return Record([.. Economy.SettingOps(key, text is null ? null : EconomyOverride.Quote(text))], $"Economy: {key}");
    }

    /// <summary>Gives <paramref name="trader"/> its default section back (a placed trader: its type's stock at the game's values; any other: empty).</summary>
    public JournalEntry? ResetTrader(string trader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trader);
        if (!CanEdit)
        {
            return null;
        }

        var old = Economy.SectionText(trader);
        var next = _placed.TryGetValue(trader, out var type) && Defaults is { } defaults
            ? EconomyOverride.StockText(defaults.StockCodes(type))
            : old is null && !Parked.Contains(trader) ? null : "[]";
        var index = Economy.IndexOf(trader);
        return old == next ? null : Record([new SetEconomySectionOp(trader, index >= 0 ? index : Economy.Traders.Count, old, next)], $"Economy: reset {trader}");
    }

    /// <summary>
    /// Applies the code view's text (<see cref="Text"/> edited): checked with <see cref="Context"/>, and with an error nothing
    /// changes. Otherwise the fewest single changes (<see cref="EconomyDiff.Steps"/>) become one journal entry; the hidden
    /// sections the text does not show stay as they are. <paramref name="checkedProblems"/>: what <see cref="EconomyValidator"/> found
    /// in this very text against <see cref="Context"/> (the Code view's worker), so it is not checked again on the UI thread.
    /// </summary>
    public EconomyApplyResult ApplyText(string json, IReadOnlyList<EconomyProblem>? checkedProblems = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!CanEdit)
        {
            return new EconomyApplyResult([], null, 0);
        }

        var problems = checkedProblems ?? EconomyValidator.Validate(json, Context);
        if (!TryRead(json, problems, out var next, out problems))
        {
            return new EconomyApplyResult(problems, null, 0);
        }

        next.KeepSections(Economy, Hidden);
        var steps = EconomyDiff.Steps(Economy, next);
        return new EconomyApplyResult(problems, Record([.. steps], "Economy: code edit"), steps.Count);
    }

    /// <summary>
    /// Makes <paramref name="json"/> (a server's live <c>EconomyOverride.json</c>, or an older list) the project's economy
    /// (owner #25, Discord: "the economy doesn't have a way to upload its own economy file"). With an error nothing changes.
    /// The sections of the project's placed traders that the file lacks are kept (the file's own wins on a name clash), the
    /// project's file is copied to a timestamped <c>.bak</c> first, and the change is one journal entry (Ctrl+Z undoes it).
    /// </summary>
    public EconomyImportResult Import(string json, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (_project is not { } project || _disposed)
        {
            return new EconomyImportResult(false, [], null, null, 0, null);
        }

        var problems = EconomyValidator.Validate(json, Context);
        if (!TryRead(json, problems, out var next, out problems))
        {
            return new EconomyImportResult(false, problems, null, null, 0, null);
        }

        var kept = next.KeepSections(Economy, _placed.Keys.Concat(Parked));
        var diff = EconomyDiff.Compare(Economy, next);
        var (before, after) = (Economy.ToJson(), next.ToJson());
        if (before == after)
        {
            return new EconomyImportResult(true, problems, diff, null, kept, null);
        }

        string? backup = null;
        var path = FilePath(project);
        try
        {
            backup = File.Exists(path) ? Backup(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("Economy: the project's file was not backed up before the import: {Message}", ex.Message);
            return new EconomyImportResult(false, problems, diff, null, kept, null);
        }

        var entry = _services.Projects.Apply(new ReplaceEconomyOp(before, after));
        _services.Logger.LogInformation("Economy: imported {Source} ({Traders} traders changed, {Items} items changed; backup {Backup}).",
            sourceName, diff.ChangedTraders.Count, diff.ItemsAdded + diff.ItemsRemoved + diff.ItemsChanged, backup ?? "none");
        return new EconomyImportResult(true, problems, diff, backup, kept, entry);
    }

    /// <summary>
    /// Writes <see cref="Economy"/> into the project (atomically). A file on disk that is not what the app last wrote (it
    /// could not be read, the app writes it otherwise, or it was changed outside) is kept as a timestamped <c>.bak</c> first
    /// (audit F102/F300). Without a project nothing is written.
    /// </summary>
    public void Save()
    {
        Invalidate();
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

            _notSaved = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("Economy not saved: {Message}", ex.Message);
            if (!_notSaved)
            {
                // Audit F449: one message until a save works again, not one a keystroke.
                _notSaved = true;
                _services.Notifications.Error(Loc.T("Economy.NotSaved"), ex.Message);
            }
        }
    }

    private void SaveTo(Project project)
    {
        var path = FilePath(project);
        var text = Economy.ToJson();
        var stamp = Stamp(path);
        if (stamp is not null && stamp != _stamp)
        {
            if (File.ReadAllText(path) is var found && found != _known)
            {
                var backup = Backup(path);
                _services.Logger.LogInformation("Economy: {Path} was not as the app last wrote it; kept as {Backup}.", path, backup);
                var kind = _known is null ? EconomyNoticeKind.BackupKept : EconomyNoticeKind.ChangedOutside;
                Notice?.Invoke(this, new EconomyNotice(kind, backup));

                // Said here, not by the Economy page: a map edit saves the economy before the page was ever opened.
                if (kind == EconomyNoticeKind.BackupKept)
                {
                    _services.Notifications.Info(Loc.T("Economy.BackupKept"), Loc.F("Economy.BackupKeptDetail", backup));
                }
                else
                {
                    _services.Notifications.Warning(Loc.T("Economy.ChangedOutside"), Loc.F("Economy.ChangedOutsideDetail", backup));
                }
            }
        }
        else if (stamp is not null && text == _known)
        {
            return;
        }

        EconomyOverride.Write(project.DirectoryPath, text);
        _known = text;
        _stamp = Stamp(path);
    }

    /// <summary>The file to give the server: <see cref="Economy"/> with every placed trader's section, without <see cref="Hidden"/>.</summary>
    public EconomyOverride ForExport() =>
        _project is { } project ? ProjectExporter.EconomyFor(project.State, Economy, RemovedStock.Concat(Parked)) ?? Economy : Economy;

    /// <summary>Brings <see cref="Economy"/> in step with the project now (the page calls it when it is shown).</summary>
    public void Refresh() => Sync(force: false);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _services.Projects.Changed -= OnProjectChanged;
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
    }

    /// <summary>Tests: the game's tables without game files.</summary>
    internal void UseDefaults(EconomyDefaults defaults)
    {
        _defaults = Task.FromResult<EconomyDefaults?>(defaults);
        Sync(force: true);
    }

    private static string FilePath(Project project) => Path.Combine(project.DirectoryPath, EconomyOverride.FileName);

    private static (long Length, DateTime At)? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
    }

    /// <summary>Copies the file to <c>EconomyOverride.json.&lt;yyyyMMdd-HHmmss&gt;.bak</c> (never over an older backup); returns the copy.</summary>
    private static string Backup(string path)
    {
        var at = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{path}.{at}.bak";
        for (var n = 2; File.Exists(target); n++)
        {
            target = $"{path}.{at}-{n}.bak";
        }

        File.Copy(path, target, overwrite: false);
        return target;
    }

    /// <summary>Reads checked text; false (with the problems) when it has an error or cannot be read.</summary>
    private static bool TryRead(string json, IReadOnlyList<EconomyProblem> found, out EconomyOverride economy, out IReadOnlyList<EconomyProblem> problems)
    {
        problems = found;
        economy = EconomyOverride.CreateDefault();
        if (EconomyValidator.HasErrors(found))
        {
            return false;
        }

        try
        {
            economy = EconomyOverride.Parse(json);
            return true;
        }
        catch (JsonException ex)
        {
            problems = [.. found, new EconomyProblem(EconomyProblemKind.Syntax, true, 1, 1, null, null, null, ex.Message)];
            return false;
        }
    }

    private JournalEntry? Record(IReadOnlyList<EditOp> steps, string describe) =>
        steps.Count == 0 || !CanEdit ? null : _services.Projects.Apply(steps.Count == 1 ? steps[0] : new BatchOp(describe, steps));

    private void Invalidate() => (_text, _hidden, _context) = (null, null, null);

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
    /// Follows the journal (<see cref="Follow"/>), adds the sections of newly placed traders, lists a placed trader's stock
    /// in an empty section once the tables are read, and updates <see cref="Parked"/> and <see cref="RemovedStock"/>.
    /// Raises <see cref="Changed"/> when anything changed (always with <paramref name="force"/>).
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

        Invalidate();
        if (project is null)
        {
            if (force)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        var followed = Follow(project);
        var changed = followed;
        var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, op) in TraderPosts.Placed(project.State))
        {
            if (op.Trader is { Name.Length: > 0 } trader)
            {
                live.TryAdd(trader.Name, trader.Type);
            }
        }

        var defaults = Defaults;
        var needTables = false;
        foreach (var (name, type) in live)
        {
            if (!Economy.HasSection(name))
            {
                Economy.EnsureSection(name);
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
        var parked = _placing.Values.SelectMany(n => n).Where(n => !live.ContainsKey(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parkedChanged = !parked.SetEquals(Parked);
        if (parkedChanged)
        {
            foreach (var name in parked.Where(n => !Parked.Contains(n)))
            {
                _services.Logger.LogInformation("Economy: {Trader} is no longer on the map; its section is kept for an undo or a redo.", name);
            }

            Parked = parked.Count == 0 ? None : parked;
        }

        // A file that could not be read is written over only by a deliberate (journaled) edit, after it is kept as a .bak: a
        // placed trader's new section alone does not replace it with the game's defaults.
        if (changed && (LoadError is null || followed))
        {
            Save();
            if (followed && !_notSaved)
            {
                LoadError = null;
            }
        }

        if (needTables)
        {
            _ = LoadDefaultsAsync();
        }

        if (UpdateRemovedStock(project) || changed || parkedChanged || force)
        {
            Invalidate();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Applies the economy edits the journal did, undid or redid since the last call: the inverses of the entries no longer
    /// applied (newest first), then the newly applied ones; true when any was an economy edit. A step whose old text is not
    /// what the file holds (the file was edited another way) is applied anyway and logged.
    /// </summary>
    private bool Follow(Project project)
    {
        var applied = project.Journal.Applied;
        var common = 0;
        var max = Math.Min(_seen.Count, applied.Count);
        while (common < max && _seen[common].Seq == applied[common].Seq)
        {
            common++;
        }

        var changed = false;
        for (var i = _seen.Count - 1; i >= common; i--)
        {
            changed |= Play(_seen[i].Op, undo: true);
        }

        for (var i = common; i < applied.Count; i++)
        {
            Scan(applied[i]);
            changed |= Play(applied[i].Op, undo: false);
        }

        var redo = project.Journal.RedoStack;
        foreach (var entry in redo)
        {
            Scan(entry);
        }

        _seen = applied;
        return changed;
    }

    private bool Play(EditOp op, bool undo)
    {
        switch (op)
        {
            case EconomyOp economy:
                var step = undo ? (EconomyOp)economy.Inverse() : economy;
                var before = step is ReplaceEconomyOp ? Economy.Clone() : null;
                try
                {
                    if (!Economy.Apply(step))
                    {
                        _services.Logger.LogWarning("Economy: '{Edit}' did not find the text it changes; applied anyway.", step.Describe());
                    }

                    // A whole file put back (an import undone, or redone) never drops a placed trader's section: it goes last.
                    foreach (var name in before is null ? [] : _placing.Values.SelectMany(n => n).Where(n => !Economy.HasSection(n)))
                    {
                        if (before!.SectionText(name) is { } section)
                        {
                            Economy.Apply(new SetEconomySectionOp(name, Economy.Traders.Count, null, section));
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _services.Logger.LogWarning("Economy: '{Edit}' could not be applied: {Message}", step.Describe(), ex.Message);
                    return false;
                }

                return true;
            case BatchOp batch when batch.Ops.Any(o => o is EconomyOp or BatchOp):
                var changed = false;
                for (var i = 0; i < batch.Ops.Count; i++)
                {
                    changed |= Play(batch.Ops[undo ? batch.Ops.Count - 1 - i : i], undo);
                }

                return changed;
            default:
                return false;
        }
    }

    /// <summary>Notes the traders an entry places, once per entry (a brush batch holds thousands of edits).</summary>
    private void Scan(JournalEntry entry) => Scan(entry.Seq, entry.Op);

    private void Scan(long seq, EditOp op)
    {
        if (_scanned.Add(seq) && TraderPosts.PlacedNames(op).ToArray() is { Length: > 0 } names)
        {
            _placing[seq] = names;
        }
    }

    private void Load()
    {
        _placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _seen = [];
        _scanned.Clear();
        _placing.Clear();
        (_known, _stamp, _notSaved) = (null, null, false);
        LoadError = null;
        RemovedStock = None;
        Parked = None;
        Economy = EconomyOverride.CreateDefault();
        if (_project is not { } project)
        {
            return;
        }

        // The file holds the result of every applied edit: nothing is replayed, the journal is only followed from here.
        _seen = project.Journal.Applied;
        // Every placement ever recorded, the discarded ones too (review, economy): a placed trader whose placement a new edit
        // dropped from the redo stack stays parked, so its section is neither shown as an unknown trader nor exported.
        foreach (var item in project.Journal.History)
        {
            Scan(item.Seq, item.Op);
        }

        var path = FilePath(project);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(path);
            Economy = EconomyOverride.Parse(text);
            if (Economy.ToJson() == text)
            {
                // Written back as it is: no backup needed before the next write.
                (_known, _stamp) = (text, Stamp(path));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Audit F103/F286: never out of the project's Changed event; the next write keeps the file as a .bak.
            Economy = EconomyOverride.CreateDefault();
            LoadError = ex.Message;
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
