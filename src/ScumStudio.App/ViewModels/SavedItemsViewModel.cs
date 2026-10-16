using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Saves;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Clean saved items" (D02, "phantom fridges": defolt_0999, O Piqueno): the items a removed or reverted item spawner of
/// this project already spawned are saved by the game or the server, so the mod alone cannot take them away. Server admins
/// and single players choose the save (SCUM.db), see exactly which saved items belong to those spawners (and, optionally,
/// the same items around the object selected on the Map), and remove only those; a backup is made first and one click puts
/// it back (<see cref="SavedItemCleaner"/>).
/// </summary>
public sealed partial class SavedItemsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly Func<IReadOnlyList<SpawnerTarget>> _targets;
    private readonly Func<SavedItemsCenter?> _center;
    private IReadOnlyList<SpawnerTarget> _foundTargets = [];
    private SaveArea? _foundArea;

    internal SavedItemsViewModel(IDialogService dialogs, Func<IReadOnlyList<SpawnerTarget>> targets, Func<SavedItemsCenter?> center)
    {
        _dialogs = dialogs;
        _targets = targets;
        _center = center;
        _status = Loc.T("Map.SavedItems.Intro");
    }

    /// <summary>The chosen save, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText))]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(RemoveCommand), nameof(RestoreCommand))]
    private string? _savePath;

    /// <summary>Also search the same items around the object selected on the Map.</summary>
    [ObservableProperty]
    private bool _useArea;

    /// <summary>The area's radius in metres.</summary>
    [ObservableProperty]
    private decimal _areaMeters = 30;

    /// <summary>What happened last, or what to do.</summary>
    [ObservableProperty]
    private string _status;

    /// <summary>True while the save is read or changed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(RemoveCommand), nameof(RestoreCommand))]
    private bool _isBusy;

    /// <summary>The newest backup of the chosen save made here, or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private string? _backup;

    /// <summary>The saved items found: ticked rows are removed.</summary>
    public ObservableCollection<SavedItemRowViewModel> Rows { get; } = [];

    /// <summary>The chosen save's path, or "no save chosen".</summary>
    public string SaveText => SavePath ?? Loc.T("Map.SavedItems.NoFile");

    internal Action? OnClose { get; init; }

    /// <summary>Tests: the games running (null = this PC's processes).</summary>
    internal IEnumerable<string>? RunningGames { get; init; }

    internal Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>The single player save folder, where the file picker opens.</summary>
    internal static string SinglePlayerSaves =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCUM", "Saved", "SaveFiles");

    /// <summary>Why the save cannot be used now, in the user's language.</summary>
    internal static string BusyText(SaveCheck check) => check.Busy switch
    {
        SaveBusy.Missing => Loc.T("Map.SavedItems.Busy.Missing"),
        SaveBusy.GameRunning => Loc.F("Map.SavedItems.Busy.Running", check.Detail),
        SaveBusy.NotClosedCleanly => Loc.F("Map.SavedItems.Busy.NotClosed", check.Detail),
        _ => Loc.T("Map.SavedItems.Busy.Open"),
    };

    /// <summary>Lets the user pick SCUM.db (a server's, or single player's).</summary>
    [RelayCommand]
    private async Task ChooseAsync()
    {
        var path = await _dialogs.OpenFileAsync(Loc.T("Map.SavedItems.Choose"), "db", "SCUM.db",
            Directory.Exists(SinglePlayerSaves) ? SinglePlayerSaves : null).ConfigureAwait(true);
        if (path is not null)
        {
            Use(path);
        }
    }

    /// <summary>Takes <paramref name="path"/> as the save (the picker's choice).</summary>
    internal void Use(string path)
    {
        SavePath = path;
        Backup = SavedItemCleaner.LatestBackup(path);
        Rows.Clear();
        RemoveCommand.NotifyCanExecuteChanged();
        var check = SavedItemCleaner.Check(path, RunningGames);
        Status = check.Ok ? Loc.T("Map.SavedItems.Ready") : BusyText(check);
    }

    /// <summary>Lists the saved items of the project's removed spawners.</summary>
    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync()
    {
        var targets = _targets();
        SaveArea? area = null;
        if (UseArea)
        {
            if (_center() is not { } center)
            {
                Status = Loc.T("Map.SavedItems.NoSelection");
                return;
            }

            area = new SaveArea(center.X, center.Y, (double)AreaMeters * 100);
        }

        if (targets.Count == 0 || (area is null && targets.All(t => t.Placed)))
        {
            Rows.Clear();
            Status = Loc.T("Map.SavedItems.NoTargets");
            return;
        }

        var path = SavePath!;
        await RunAsync(() => SavedItemCleaner.Find(path, targets, area, runningGames: RunningGames), found =>
        {
            _foundTargets = targets;
            _foundArea = area;
            Rows.Clear();
            foreach (var item in found)
            {
                var row = new SavedItemRowViewModel(item);
                row.PropertyChanged += (_, _) => RemoveCommand.NotifyCanExecuteChanged();
                Rows.Add(row);
            }

            Status = found.Count == 0 ? Loc.T("Map.SavedItems.None") : Loc.F("Map.SavedItems.Found", found.Count.ToString(CultureInfo.CurrentCulture));
        }).ConfigureAwait(true);
    }

    /// <summary>Removes the ticked items after a backup.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        var path = SavePath!;
        var ids = Rows.Where(r => r.IsChecked).Select(r => r.Item.Id).ToList();
        var (targets, area, now) = (_foundTargets, _foundArea, Now());
        await RunAsync(() => SavedItemCleaner.Remove(path, targets, area, ids, now, runningGames: RunningGames), removal =>
        {
            Backup = removal.BackupPath;
            Rows.Clear();
            Status = Loc.F("Map.SavedItems.Removed", removal.Items.ToString(CultureInfo.CurrentCulture), removal.Contents.ToString(CultureInfo.CurrentCulture),
                Path.GetFileName(removal.BackupPath));
        }).ConfigureAwait(true);
    }

    /// <summary>Puts the newest backup back.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        var (path, backup) = (SavePath!, Backup!);
        await RunAsync(() =>
        {
            SavedItemCleaner.Restore(path, backup, RunningGames);
            return backup;
        }, restored =>
        {
            Rows.Clear();
            Status = Loc.F("Map.SavedItems.Restored", Path.GetFileName(restored));
        }).ConfigureAwait(true);
    }

    /// <summary>Closes the panel.</summary>
    [RelayCommand]
    private void Close() => OnClose?.Invoke();

    private bool CanFind() => SavePath is not null && !IsBusy;

    private bool CanRemove() => SavePath is not null && !IsBusy && Rows.Any(r => r.IsChecked);

    private bool CanRestore() => SavePath is not null && Backup is not null && !IsBusy;

    /// <summary>Runs <paramref name="work"/> off the UI thread; the save's problems become the status line.</summary>
    private async Task RunAsync<T>(Func<T> work, Action<T> done)
    {
        IsBusy = true;
        try
        {
            done(await Task.Run(work).ConfigureAwait(true));
        }
        catch (SaveBusyException ex)
        {
            Status = BusyText(ex.Check);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DbException)
        {
            Status = Loc.F("Map.SavedItems.Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>The area's centre: the object selected on the Map (world cm).</summary>
internal sealed record SavedItemsCenter(double X, double Y, string Name);

/// <summary>One saved item: what it is, why it matches, where it lies.</summary>
public sealed partial class SavedItemRowViewModel : ObservableObject
{
    internal SavedItemRowViewModel(SavedItem item) => Item = item;

    /// <summary>The saved item.</summary>
    public SavedItem Item { get; }

    /// <summary>Ticked rows are removed.</summary>
    [ObservableProperty]
    private bool _isChecked = true;

    /// <summary>Its class without the save's <c>_ES</c>.</summary>
    public string Name => Item.Class.EndsWith("_ES", StringComparison.Ordinal) ? Item.Class[..^3] : Item.Class;

    /// <summary>Why it matches.</summary>
    public string WhyText => Item.Match switch
    {
        SavedItemMatch.Spawned => Loc.F("Map.SavedItems.Why.Spawned", Item.Target),
        SavedItemMatch.Near => Loc.F("Map.SavedItems.Why.Near", Item.Target),
        _ => Loc.T("Map.SavedItems.Why.Area"),
    } + (Item.Contents > 0 ? " · " + Loc.F("Map.SavedItems.Contents", Item.Contents.ToString(CultureInfo.CurrentCulture)) : string.Empty);

    /// <summary>Where it lies (metres, as the Map shows positions).</summary>
    public string PositionText => string.Create(CultureInfo.CurrentCulture, $"{Item.X / 100:0.#}, {Item.Y / 100:0.#}, {Item.Z / 100:0.#} m");
}
