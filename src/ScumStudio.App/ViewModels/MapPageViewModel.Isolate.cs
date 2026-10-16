using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Level.World;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Several levels at once and Isolate (seviscache #4: "load only the levels I choose, e.g. all of Blato, its interiors and
/// its landscape tiles"). Ctrl+click and Shift+click select several levels or cells in the World tree; after a short pause
/// (or with Show selected) exactly those load, with nothing streaming in around them. Isolate hides the island's terrain
/// behind them and stops the streaming of the whole island; Whole island turns it off. A plain click loads one level as before.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>The most levels one selection loads (memory, and the viewport's id slots): a whole sector has about 40.</summary>
    public const int MaxSelectedLevels = 64;

    /// <summary>The pause after the last Ctrl or Shift click before the selection loads: a few clicks in a row make one load.</summary>
    public static readonly TimeSpan SelectionPause = TimeSpan.FromMilliseconds(600);

    private int _selectionVersion;

    /// <summary>The World tree's selected nodes (the view sets them as the tree's selection changes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLevelCount), nameof(HasMultiSelection), nameof(ShowSelectedText))]
    [NotifyCanExecuteChangedFor(nameof(ShowSelectedCommand))]
    private IReadOnlyList<WorldTreeNode> _selectedNodes = [];

    /// <summary>Only the loaded levels show: the island's terrain is hidden (it stays in memory) and the island does not stream.</summary>
    [ObservableProperty]
    private bool _isolate;

    /// <summary>
    /// True while the tree's selection is being extended with Ctrl or Shift (the view sets it from each click and key): a
    /// node that becomes the current one then shows its properties but does not load on its own.
    /// </summary>
    public bool ExtendingSelection { get; set; }

    /// <summary>The levels the selection loads (see <see cref="LevelsOf"/>).</summary>
    public int SelectedLevelCount => World is { } world ? LevelsOf(world, SelectedNodes).Count : 0;

    /// <summary>More than one node is selected in the World tree.</summary>
    public bool HasMultiSelection => SelectedNodes.Count > 1;

    /// <summary>"Show selected (5)".</summary>
    public string ShowSelectedText => Loc.F("Map.Levels.ShowSelected", SelectedLevelCount);

    /// <summary>
    /// The content levels of <paramref name="nodes"/>: a level node's own level, a cell's POI, base, city and landscape
    /// levels; never the persistent level, BuiltData, HLOD or a category (a category of 400 tiles is not a choice). Once
    /// each, the landscape tiles last (as a cell loads).
    /// </summary>
    public static IReadOnlyList<string> LevelsOf(WorldIndex world, IEnumerable<WorldTreeNode> nodes) =>
        PackagesOf(world, nodes).Select(p => p.PackagePath).ToList();

    private static List<WorldPackage> PackagesOf(WorldIndex world, IEnumerable<WorldTreeNode> nodes)
    {
        var packages = new List<WorldPackage>();
        foreach (var node in nodes)
        {
            if (node.Package is { } package)
            {
                if (package.IsContentLevel)
                {
                    packages.Add(package);
                }
            }
            else if (node.Cell is { } cell)
            {
                packages.AddRange(world.InCell(cell).Where(p => p.IsContentLevel));
            }
        }

        return packages
            .DistinctBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Kind == WorldPackageKind.Landscape)
            .ToList();
    }

    /// <summary>
    /// The tree's selection changed (the view passes all of it). Extended with Ctrl or Shift, the chosen levels load together
    /// after <see cref="SelectionPause"/>; a plain click has already loaded its level (<see cref="SelectedNode"/>).
    /// </summary>
    public void SelectNodes(IReadOnlyList<WorldTreeNode> nodes)
    {
        SelectedNodes = nodes;
        var version = ++_selectionVersion;
        if (!ExtendingSelection || nodes.Count == 0)
        {
            return;
        }

        _ = Task.Delay(SelectionPause).ContinueWith(_ => _services.Dispatcher.Post(() =>
        {
            if (version == _selectionVersion)
            {
                LevelLoadCompletion = LoadSelectedLevelsAsync();
            }
        }), TaskScheduler.Default);
    }

    /// <summary>Loads the selected levels now (the button under the tree; Ctrl or Shift clicks do it after a pause).</summary>
    [RelayCommand(CanExecute = nameof(CanShowSelected))]
    private Task ShowSelected()
    {
        _selectionVersion++; // a load still waiting for the pause is this one
        return LevelLoadCompletion = LoadSelectedLevelsAsync();
    }

    private bool CanShowSelected() => SelectedLevelCount > 0;

    /// <summary>
    /// Loads exactly the selected levels: world mode ends (nothing streams in around them, as for one level picked in the
    /// tree), at most <see cref="MaxSelectedLevels"/>; landscape tiles in full detail when there are at most four.
    /// </summary>
    public async Task LoadSelectedLevelsAsync()
    {
        if (World is not { } world || PackagesOf(world, SelectedNodes) is not { Count: > 0 } packages)
        {
            return;
        }

        if (packages.Count > MaxSelectedLevels)
        {
            _services.Notifications.Warning(Loc.T("Map.Levels.TooMany"), Loc.F("Map.Levels.TooMany.Message", packages.Count, MaxSelectedLevels));
            packages = packages.Take(MaxSelectedLevels).ToList();
        }

        IsWorldMode = false;
        _loadedCell = null;
        var tiles = packages.Count(p => p.Kind == WorldPackageKind.Landscape);
        _services.Logger.LogInformation("Map: {Count} selected level(s) ({Tiles} landscape tile(s)).", packages.Count, tiles);
        await LoadLevelsAsync(packages.Select(p => p.PackagePath).ToList(), landscapeStep: tiles <= 4 ? 1 : 4).ConfigureAwait(true);
    }

    partial void OnIsolateChanged(bool value)
    {
        if (value)
        {
            WorldStatus = Loc.T("Map.World.Isolated");
        }
        else if (IsWorldMode && LoadMode == MapLoadMode.Island)
        {
            WorldStatus = Loc.T("Map.World.Ready"); // the next camera tick streams again
        }
    }
}
