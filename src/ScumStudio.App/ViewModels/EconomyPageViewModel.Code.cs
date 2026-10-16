using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Economy;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The Code view of the Economy page (owner, 2026-10-12: "the real EconomyOverride.json text … kept in sync both ways in real
/// time; errors never lose the user's text"): the project's file exactly as the export writes it. While nothing is typed it
/// follows the store, so an edit of the numbers shows at once. Typing makes a draft: it is checked on a worker after a short
/// pause and, without errors, applied after a second of quiet (or when the box is left) as one journal entry; with errors
/// nothing is applied and the draft stays until it is fixed or discarded. A text box lays out its whole text on every key
/// (one placed trader's ~700 entries: about 240 ms a key, measured headless), so by default the box holds the settings and
/// the section of one trader, itself a valid file; applying it merges that section back into the whole file.
/// </summary>
public sealed partial class EconomyPageViewModel
{
    private const int CheckDelayMs = 300;
    private const int ApplyDelayMs = 1000;
    private const string TraderScope = "trader";
    private const string WholeScope = "whole";

    private string? _draft;
    private string? _draftScope;
    private bool _draftApplied;
    private bool _applyingCode;
    private int _codeVersion;
    private CancellationTokenSource? _codeWait;
    private IReadOnlyList<EconomyProblem> _codeProblems = [];
    private (string Text, EconomyContext Context, IReadOnlyList<EconomyProblem> Problems)? _checked;
    private string? _scopedText;

    /// <summary>What the code box holds: the settings and one trader's section (fast to type in), or the whole file.</summary>
    public IReadOnlyList<EconomyCategory> CodeScopes { get; } =
        [new EconomyCategory(TraderScope, Loc.T("Economy.Code.Scope.Trader")), new EconomyCategory(WholeScope, Loc.T("Economy.Code.Scope.Whole"))];

    /// <summary>The scope shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTraderScope))]
    private EconomyCategory? _selectedCodeScope;

    /// <summary>The box holds the settings and the selected trader's section.</summary>
    public bool IsTraderScope => SelectedCodeScope?.Key != WholeScope;

    /// <summary>The Code view is shown instead of the Numbers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCodeShown), nameof(IsNumbersShown))]
    private bool _isCodeView;

    /// <summary>The problems of the shown text, errors first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private IReadOnlyList<EconomyProblemRow> _problems = [];

    /// <summary>The Code view is on screen.</summary>
    public bool IsCodeShown => HasCatalog && IsCodeView;

    /// <summary>The Numbers view is on screen.</summary>
    public bool IsNumbersShown => HasCatalog && !IsCodeView;

    /// <summary>The text of the code box: the user's draft, else the project's file (or its scope) as the export writes it.</summary>
    public string CodeText
    {
        get => _draft ?? StoreText;
        set => OnCodeTyped(value ?? string.Empty);
    }

    /// <summary>The scope's text from the store: the whole file, or its settings with the selected trader's section only.</summary>
    private string StoreText
    {
        get
        {
            if (!IsTraderScope)
            {
                return _store.Text;
            }

            if (_scopedText is null)
            {
                var trader = SelectedTrader?.Name;
                var skip = _store.Economy.Traders.Where(n => !string.Equals(n, trader, StringComparison.OrdinalIgnoreCase)).Concat(_store.Hidden);
                _scopedText = _store.Economy.ToJson(skip.ToHashSet(StringComparer.OrdinalIgnoreCase));
            }

            return _scopedText;
        }
    }

    /// <summary>The draft has errors and is not applied.</summary>
    public bool IsCodeDraft => _draft is not null && !_draftApplied && ErrorCount > 0;

    /// <summary>The text shown is the project's economy and reads without errors.</summary>
    public bool IsCodeValid => (_draft is null || _draftApplied) && ErrorCount == 0;

    /// <summary>The text has errors.</summary>
    public bool HasCodeErrors => ErrorCount > 0;

    /// <summary>The text has warnings only (unknown codes, sale above price …).</summary>
    public bool HasCodeWarningsOnly => ErrorCount == 0 && _codeProblems.Count > 0;

    /// <summary>The problems list is not empty.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>"Valid · applied", or "2 errors · 1 warnings".</summary>
    public string CodeStatus => IsCodeValid && _codeProblems.Count == 0
        ? Loc.T("Economy.Code.Valid")
        : Loc.F("Economy.Code.Problems", ErrorCount, _codeProblems.Count - ErrorCount);

    /// <summary>"Not applied: 2 errors. Your text is kept …".</summary>
    public string CodeDraftText => Loc.F("Economy.Code.Draft", ErrorCount);

    private int ErrorCount => _codeProblems.Count(p => p.IsError);

    /// <summary>Checks the shown text now (tests; the view checks after a pause).</summary>
    public void ValidateCodeNow()
    {
        CancelCodeWait();
        var (text, context) = (CodeText, _store.Context);

        // The worker usually checked this very text a moment ago (review, economy: twice 25 ms a MB on the UI thread).
        var problems = _checked is { } known && ReferenceEquals(known.Context, context) && string.Equals(known.Text, text, StringComparison.Ordinal)
            ? known.Problems
            : EconomyValidator.Validate(text, context);
        _checked = (text, context, problems);
        ShowProblems(problems);
    }

    /// <summary>
    /// Applies the draft now if it reads without errors (one journal entry; the numbers follow); with errors it stays a draft.
    /// Returns true when the project's economy is the text shown.
    /// </summary>
    public bool ApplyCodeNow()
    {
        if (_draft is null || _draftApplied)
        {
            return true;
        }

        ValidateCodeNow();
        if (ErrorCount > 0 || !CanEdit)
        {
            return false;
        }

        if ((_draftScope is { } trader ? Merged(_draft, trader) : _draft) is not { } text)
        {
            return false;
        }

        EconomyApplyResult result;
        _applyingCode = true;
        try
        {
            result = _store.ApplyText(text, _draftScope is null ? _codeProblems : null); // the whole file was just checked
        }
        finally
        {
            _applyingCode = false;
        }

        _draftApplied = !EconomyValidator.HasErrors(result.Problems);
        if (_draftScope is null)
        {
            ShowProblems(result.Problems); // a scope's problems are already listed by its own lines
        }
        else
        {
            RaiseCode();
        }

        return _draftApplied;
    }

    /// <summary>The whole file: <paramref name="part"/> (settings and a trader's section) with the store's other sections.</summary>
    private string? Merged(string part, string trader)
    {
        EconomyOverride file;
        try
        {
            file = EconomyOverride.Parse(part);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        file.KeepSections(_store.Economy, _store.Economy.Traders.Where(n => !string.Equals(n, trader, StringComparison.OrdinalIgnoreCase)));
        return file.ToJson();
    }

    partial void OnSelectedCodeScopeChanged(EconomyCategory? value) => OnCodeScopeChanged();

    /// <summary>The scope or its trader changed: the box shows the new one (a valid draft is applied first; one with errors stays).</summary>
    private void OnCodeScopeChanged()
    {
        _scopedText = null;
        if (_draft is not null && !_draftApplied && !ApplyCodeNow())
        {
            RaiseCode();
            return;
        }

        _draft = null;
        _draftApplied = false;
        if (IsCodeView)
        {
            ScheduleCheck(apply: false);
        }

        RaiseCode();
    }

    /// <summary>Applies the text and shows it the way the game writes the file.</summary>
    [RelayCommand]
    private void FormatCode()
    {
        if (ApplyCodeNow())
        {
            _draft = null;
            _draftApplied = false;
            ScheduleCheck(apply: false);
            RaiseCode();
        }
    }

    /// <summary>Throws the draft away: the box shows the project's economy again.</summary>
    [RelayCommand]
    private void DiscardCode()
    {
        _draft = null;
        _draftApplied = false;
        ShowProblems([]);
        ScheduleCheck(apply: false); // the project's text is checked on a worker
        RaiseCode();
    }

    partial void OnIsCodeViewChanged(bool value)
    {
        if (value)
        {
            ScheduleCheck(apply: false);
            RaiseCode();
        }
        else
        {
            ApplyCodeNow();
        }
    }

    /// <summary>The store changed (a numbers edit, an undo, an import): the box follows unless it holds a draft not applied.</summary>
    private void OnStoreTextChanged()
    {
        _scopedText = null;
        if (_applyingCode || (_draft is not null && !_draftApplied))
        {
            return; // the user's text is never overwritten
        }

        _draft = null;
        _draftApplied = false;
        if (IsCodeView)
        {
            ScheduleCheck(apply: false);
        }

        RaiseCode();
    }

    private void OnCodeTyped(string text)
    {
        if (text == CodeText)
        {
            return;
        }

        if (_draft is null || _draftApplied)
        {
            _draftScope = IsTraderScope ? SelectedTrader?.Name ?? string.Empty : null; // the section the draft stands for
        }

        _draft = text == StoreText ? null : text;
        _draftApplied = false;
        RaiseCode();
        ScheduleCheck(apply: true);
    }

    /// <summary>After a pause, checks the text on a worker; without errors, applies it after a second of quiet.</summary>
    private void ScheduleCheck(bool apply)
    {
        CancelCodeWait();
        var wait = _codeWait = new CancellationTokenSource();
        var version = ++_codeVersion;
        var text = CodeText;
        var context = _store.Context;
        _ = Task.Run(async () =>
        {
            await Task.Delay(CheckDelayMs, wait.Token).ConfigureAwait(false);
            var result = EconomyValidator.Validate(text, context);
            _services.Dispatcher.Post(() =>
            {
                if (version == _codeVersion)
                {
                    _checked = (text, context, result);
                    ShowProblems(result);
                }
            });
            if (!apply || result.Any(p => p.IsError))
            {
                return;
            }

            await Task.Delay(ApplyDelayMs, wait.Token).ConfigureAwait(false);
            _services.Dispatcher.Post(() =>
            {
                if (version == _codeVersion)
                {
                    ApplyCodeNow();
                }
            });
        }, wait.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private void CancelCodeWait()
    {
        _codeVersion++;
        _codeWait?.Cancel();
        _codeWait?.Dispose();
        _codeWait = null;
    }

    private void ShowProblems(IReadOnlyList<EconomyProblem> problems)
    {
        _codeProblems = problems;
        Problems = [.. problems.OrderByDescending(p => p.IsError).ThenBy(p => p.Line).ThenBy(p => p.Column).Select(p => new EconomyProblemRow(p))];
        RaiseCode();
    }

    /// <summary>
    /// Import… (Discord: "the economy doesn't have a way to upload its own economy file"): a server's live
    /// <c>EconomyOverride.json</c>, or any older list, becomes the project's economy as one journal entry, with a summary of
    /// what changed. A file with errors changes nothing: its text opens in Code as a draft with its problems listed. The
    /// user's file is only read.
    /// </summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (!CanEdit)
        {
            return;
        }

        var path = await _services.Dialogs.OpenFileAsync(Loc.T("Economy.Import.Title"), ".json", Loc.T("Economy.Import.Filter"), ServerConfigFolder());
        if (path is null)
        {
            return;
        }

        string text;
        try
        {
            text = await Task.Run(() => File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Economy.Import.Refused"), ex.Message);
            return;
        }

        ImportText(text, Path.GetFileName(path));
    }

    /// <summary>The last import's summary card, until dismissed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportSummary))]
    private EconomyImportCard? _importSummary;

    /// <summary>The summary card is shown.</summary>
    public bool HasImportSummary => ImportSummary is not null;

    /// <summary>Hides the summary card.</summary>
    [RelayCommand]
    private void DismissImport() => ImportSummary = null;

    /// <summary>Imports <paramref name="text"/> read from <paramref name="name"/> (the Import button, tests).</summary>
    public void ImportText(string text, string name)
    {
        var result = _store.Import(text, name);
        if (!result.Imported)
        {
            var errors = result.Problems.Count(p => p.IsError);
            if (errors == 0)
            {
                // Refused without an error: the project's file could not be backed up first (review, economy).
                _services.Notifications.Error(Loc.T("Economy.Import.Refused"), Loc.F("Economy.Import.NotBackedUp", name));
                return;
            }

            if (_draft is not null && !_draftApplied && !ApplyCodeNow())
            {
                // The user's own text in Code is not applied yet and has errors: it is never replaced (review, economy).
                _services.Notifications.Error(Loc.T("Economy.Import.Refused"), Loc.F("Economy.Import.DraftKept", name, errors));
                return;
            }

            // The file's text as a draft in Code, its problems listed, so it can be fixed and applied there.
            _services.Notifications.Error(Loc.T("Economy.Import.Refused"), Loc.F("Economy.Import.RefusedDetail", name, errors));
            SelectedCodeScope = CodeScopes[1];
            CancelCodeWait();
            _draft = text;
            _draftScope = null;
            _draftApplied = false;
            ShowProblems(result.Problems);
            IsCodeView = true;
            return;
        }

        var card = EconomyImportCard.From(name, result);
        ImportSummary = card;
        _services.Notifications.Success(Loc.T("Economy.Import.Done"), card.Title);
    }

    /// <summary>The server's <c>SCUM\Saved\Config\WindowsServer</c> when the settings know its paks folder (it is there), else null.</summary>
    private string? ServerConfigFolder()
    {
        if (_services.Settings.Load().ServerPaksFolder is not { Length: > 0 } paks)
        {
            return null;
        }

        try
        {
            var folder = Path.GetFullPath(Path.Combine(paks, "..", "..", "Saved", "Config", "WindowsServer"));
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private void RaiseCode()
    {
        OnPropertyChanged(nameof(CodeText));
        OnPropertyChanged(nameof(IsCodeDraft));
        OnPropertyChanged(nameof(IsCodeValid));
        OnPropertyChanged(nameof(HasCodeErrors));
        OnPropertyChanged(nameof(HasCodeWarningsOnly));
        OnPropertyChanged(nameof(CodeStatus));
        OnPropertyChanged(nameof(CodeDraftText));
    }
}

/// <summary>The card after an import: what changed, the changed traders, what was kept, and that Ctrl+Z undoes it.</summary>
/// <param name="Title">"EconomyOverride.json: 1 settings, 3 traders, 12 items changed".</param>
/// <param name="Traders">The changed traders (the first 8, then "…").</param>
/// <param name="Detail">Placed traders kept, warnings, and the undo hint.</param>
public sealed record EconomyImportCard(string Title, string Traders, string Detail)
{
    private const int ShownTraders = 8;

    /// <summary>Some trader changed.</summary>
    public bool HasTraders => Traders.Length > 0;

    /// <summary>The card of <paramref name="result"/> for the file <paramref name="name"/>.</summary>
    public static EconomyImportCard From(string name, EconomyImportResult result)
    {
        var diff = result.Diff;
        var items = diff is null ? 0 : diff.ItemsAdded + diff.ItemsRemoved + diff.ItemsChanged;
        var title = Loc.F("Economy.Import.Summary", name, diff?.SettingsChanged ?? 0, diff?.ChangedTraders.Count ?? 0, items);
        var names = diff?.ChangedTraders ?? [];
        var traders = string.Join(", ", names.Take(ShownTraders)) + (names.Count > ShownTraders ? ", …" : string.Empty);
        var parts = new List<string>();
        if (result.KeptPlacedSections > 0)
        {
            parts.Add(Loc.F("Economy.Import.Kept", result.KeptPlacedSections));
        }

        if (result.Problems.Count(p => !p.IsError) is > 0 and var warnings)
        {
            parts.Add(Loc.F("Economy.Import.Warnings", warnings));
        }

        if (result.Entry is not null)
        {
            parts.Add(Loc.T("Economy.Import.Undo"));
        }

        return new EconomyImportCard(title, traders, string.Join(" · ", parts));
    }
}

/// <summary>A problem of the Code view's text: where it is and what is wrong, in the user's language.</summary>
public sealed class EconomyProblemRow(EconomyProblem problem)
{
    /// <summary>The validator's finding.</summary>
    public EconomyProblem Problem { get; } = problem;

    /// <summary>An error (the text is not applied), not a warning.</summary>
    public bool IsError => Problem.IsError;

    /// <summary>1-based line.</summary>
    public int Line => Problem.Line;

    /// <summary>1-based column.</summary>
    public int Column => Problem.Column;

    /// <summary>"Line 12, column 7".</summary>
    public string Location => Loc.F("Economy.Code.Line", Line, Column);

    /// <summary>The message of its kind with its trader, code, key and value.</summary>
    public string Message
    {
        get
        {
            var key = "Economy.Problem." + Problem.Kind;
            return Loc.F(key, Problem.Trader ?? string.Empty, Problem.Code ?? string.Empty, Problem.Key ?? string.Empty, Problem.Value ?? string.Empty);
        }
    }

    /// <summary>The character index of 1-based <paramref name="line"/> and <paramref name="column"/> in <paramref name="text"/> (clamped).</summary>
    public static int IndexOf(string text, int line, int column)
    {
        var index = 0;
        for (var l = 1; l < line && index < text.Length; l++)
        {
            var next = text.IndexOf('\n', index);
            if (next < 0)
            {
                return text.Length;
            }

            index = next + 1;
        }

        var end = text.IndexOf('\n', index);
        var lineEnd = end < 0 ? text.Length : end > index && text[end - 1] == '\r' ? end - 1 : end;
        return Math.Clamp(index + Math.Max(0, column - 1), index, lineEnd);
    }
}
