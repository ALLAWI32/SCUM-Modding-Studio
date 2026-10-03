using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;

namespace ScumStudio.App.ViewModels;

/// <summary>A category row of the Dump tree; ticking it ticks its sub-categories.</summary>
public sealed partial class DumpNodeViewModel : ObservableObject
{
    /// <summary>Wraps <paramref name="node"/>.</summary>
    public DumpNodeViewModel(DumpNode node)
    {
        Node = node;
        Children = node.Children.Select(c => new DumpNodeViewModel(c)).ToList();
    }

    /// <summary>The category.</summary>
    public DumpNode Node { get; }

    /// <summary>Sub-categories.</summary>
    public IReadOnlyList<DumpNodeViewModel> Children { get; }

    /// <summary>The category title.</summary>
    public string Text => Node.Title;

    /// <summary>"6,252 · 2,043 MB".</summary>
    public string Info => string.Create(CultureInfo.CurrentCulture, $"{Node.Count:N0} · {Node.SizeMB:N0} MB");

    /// <summary>Ticked for the dump.</summary>
    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        foreach (var child in Children)
        {
            child.IsChecked = value;
        }
    }

    /// <summary>The ticked categories, top-most only (a ticked category already includes its sub-categories).</summary>
    public IEnumerable<string> CheckedIds() => IsChecked ? [Node.Id] : Children.SelectMany(c => c.CheckedIds());
}

/// <summary>The Dump window: tick categories of the game's content and write their raw files to a folder.</summary>
public sealed partial class DumpViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    /// <summary>Creates the dump view model.</summary>
    public DumpViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Roots = AssetDumper.Tree.Select(n => new DumpNodeViewModel(n)).ToList();
        _outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ScumStudio Dump");
    }

    /// <summary>The 26 top categories.</summary>
    public IReadOnlyList<DumpNodeViewModel> Roots { get; }

    /// <summary>Where the files go.</summary>
    [ObservableProperty]
    private string _outputFolder;

    /// <summary>True while dumping.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DumpCommand), nameof(CancelCommand))]
    private bool _isRunning;

    /// <summary>0..100.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>One-line status.</summary>
    [ObservableProperty]
    private string _status = Loc.T("Dump.Status.Start");

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Dump.Pick"), OutputFolder).ConfigureAwait(true) is { } folder)
        {
            OutputFolder = folder;
        }
    }

    private bool CanDump() => !IsRunning;

    /// <summary>Writes the raw files of every ticked category.</summary>
    [RelayCommand(CanExecute = nameof(CanDump))]
    private async Task DumpAsync()
    {
        if (_services.Workspace.Catalog is not { } catalog)
        {
            Status = Loc.T("Dump.Status.NoPaks");
            return;
        }

        var ids = Roots.SelectMany(r => r.CheckedIds()).ToList();
        if (ids.Count == 0)
        {
            Status = Loc.T("Dump.Status.NothingTicked");
            return;
        }

        var packages = AssetDumper.Select(ids);
        var folder = OutputFolder.Trim();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        try
        {
            var progress = new Progress<(int Done, int Total, string Item)>(p =>
            {
                Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                Status = $"{p.Done:N0} / {p.Total:N0}  {p.Item[(p.Item.LastIndexOf('/') + 1)..]}";
            });
            var result = await Task.Run(() => AssetDumper.Dump(catalog, packages, folder, progress, ct), ct).ConfigureAwait(true);
            Progress = 100;
            Status = Loc.F("Dump.Status.Done", result.Packages, result.Files, result.Bytes / 1048576.0, folder);
            _services.Notifications.Success(Loc.T("Dump.Finished"), result.ManifestPath);
        }
        catch (OperationCanceledException)
        {
            Status = Loc.T("Dump.Status.Stopped");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Loc.F("Dump.Status.Failed", ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();
}
