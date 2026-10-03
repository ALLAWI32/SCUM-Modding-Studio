using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;

namespace ScumStudio.App.ViewModels;

/// <summary>One ground look in the Landscape menu.</summary>
public sealed partial class GroundLookOption : ObservableObject
{
    /// <summary>Creates the option; <paramref name="choose"/> applies it.</summary>
    public GroundLookOption(GroundLook look, Action<GroundLook> choose)
    {
        Look = look;
        Choose = new RelayCommand(() => choose(look));
    }

    /// <summary>The look.</summary>
    public GroundLook Look { get; }

    /// <summary>Its name.</summary>
    public string Label => Loc.T(KeyOf(Look));

    /// <summary>The text key of a look' name.</summary>
    internal static string KeyOf(GroundLook look) => look switch
    {
        GroundLook.Snow => "Map.Looks.Snow",
        GroundLook.Desert => "Map.Looks.Desert",
        GroundLook.Autumn => "Map.Looks.Autumn",
        GroundLook.Grass => "Map.Looks.Grass",
        _ => "Map.Looks.Game",
    };

    /// <summary>True when the project has this look.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Applies the look.</summary>
    public ICommand Choose { get; }
}

/// <summary>A tree of the game, for the Landscape menu's tree swap.</summary>
/// <param name="Name">Its mesh name.</param>
/// <param name="PackagePath">Its mesh package.</param>
public sealed record TreeChoice(string Name, string PackagePath)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A tree the project draws as another one.</summary>
public sealed class TreeSwapRow
{
    /// <summary>Creates the row; <paramref name="putBack"/> removes the swap.</summary>
    public TreeSwapRow(string package, string with, Action<string> putBack)
    {
        Package = package;
        Text = $"{Leaf(package)}  →  {Leaf(with)}";
        PutBack = new RelayCommand(() => putBack(package));
    }

    /// <summary>The replaced tree.</summary>
    public string Package { get; }

    /// <summary>"Oak → Pine".</summary>
    public string Text { get; }

    /// <summary>Draws the game's own tree again.</summary>
    public ICommand PutBack { get; }

    internal static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// The Landscape menu (owner: "the ground snowy, a desert, autumn or all grass; change the map's trees; more menus like
/// company apps"): looks of the whole island's ground and tree swaps, written into the mod as <see cref="ReplaceAssetOp"/>s.
/// </summary>
public sealed partial class MapPageViewModel
{
    private IReadOnlyList<TreeChoice>? _trees;

    /// <summary>The ground looks, the project's one marked.</summary>
    public IReadOnlyList<GroundLookOption> GroundLookOptions { get; private set; } = [];

    /// <summary>Every tree of the game (for the swap pickers).</summary>
    public IReadOnlyList<TreeChoice> Trees => _trees ??= AssetDumper.Packages
        .Where(p => p.ClassName == "StaticMesh" && p.PackagePath.Contains("/Foliage/", StringComparison.OrdinalIgnoreCase)
                    && p.PackagePath.Contains("/Trees/", StringComparison.OrdinalIgnoreCase)
                    && !p.PackagePath.Contains("Debris", StringComparison.OrdinalIgnoreCase) && !p.PackagePath.Contains("Chunk", StringComparison.OrdinalIgnoreCase)
                    && !p.PackagePath.Contains("Branch", StringComparison.OrdinalIgnoreCase) && !p.PackagePath.EndsWith("_Menu", StringComparison.OrdinalIgnoreCase))
        .Select(p => new TreeChoice(TreeSwapRow.Leaf(p.PackagePath), p.PackagePath))
        .DistinctBy(t => t.PackagePath, StringComparer.OrdinalIgnoreCase)
        .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The tree to replace.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwapTreeCommand))]
    private TreeChoice? _treeToSwap;

    /// <summary>The tree drawn in its place.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwapTreeCommand))]
    private TreeChoice? _treeSwapWith;

    /// <summary>The project's tree swaps.</summary>
    [ObservableProperty]
    private IReadOnlyList<TreeSwapRow> _treeSwaps = [];

    /// <summary>True when the project swaps a tree.</summary>
    public bool HasTreeSwaps => TreeSwaps.Count > 0;

    partial void OnTreeSwapsChanged(IReadOnlyList<TreeSwapRow> value) => OnPropertyChanged(nameof(HasTreeSwaps));

    /// <summary>Marks the project's look and lists its tree swaps (with every refresh of the edits).</summary>
    private void RefreshLooks()
    {
        var state = _services.Projects.Current?.State;
        if (GroundLookOptions.Count == 0)
        {
            GroundLookOptions = Enum.GetValues<GroundLook>().Select(l => new GroundLookOption(l, SetGroundLook)).ToList();
            OnPropertyChanged(nameof(GroundLookOptions));
        }

        var current = state is null ? GroundLook.Game : GroundLooks.Current(state);
        foreach (var option in GroundLookOptions)
        {
            option.IsCurrent = option.Look == current;
        }

        var trees = Trees.Select(t => t.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TreeSwaps = state?.AssetReplacements.Where(r => trees.Contains(r.Key))
            .OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(r => new TreeSwapRow(r.Key, r.Value, PutTreeBack))
            .ToList() ?? [];
    }

    /// <summary>Gives the whole island's ground <paramref name="look"/> (one undo step).</summary>
    public void SetGroundLook(GroundLook look)
    {
        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.Looks.NeedProject"));
            RefreshLooks();
            return;
        }

        var catalog = _services.Workspace.Catalog;
        var ops = GroundLooks.Edits(project.State, look, path => catalog?.PackageExists(path) ?? false);
        if (ops.Count > 0)
        {
            ApplyLookOps(ops, Loc.F("Map.Looks.Applied", Loc.T(GroundLookOption.KeyOf(look))));
        }

        RefreshLooks();
    }

    private bool CanSwapTree() => TreeToSwap is { } a && TreeSwapWith is { } b && !string.Equals(a.PackagePath, b.PackagePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Draws <see cref="TreeSwapWith"/> wherever the game has <see cref="TreeToSwap"/> (the whole island).</summary>
    [RelayCommand(CanExecute = nameof(CanSwapTree))]
    private void SwapTree()
    {
        if (_services.Projects.Current is not { } project || TreeToSwap is not { } tree || TreeSwapWith is not { } with)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.Looks.NeedProject"));
            return;
        }

        ApplyLookOps([new ReplaceAssetOp(tree.PackagePath, project.State.GetReplacement(tree.PackagePath), with.PackagePath)],
            Loc.F("Map.Trees.Swapped", tree.Name, with.Name));
        RefreshLooks();
    }

    private void PutTreeBack(string package)
    {
        if (_services.Projects.Current is { } project && project.State.GetReplacement(package) is { } now)
        {
            ApplyLookOps([new ReplaceAssetOp(package, now, null)], Loc.F("Map.Trees.PutBack.Done", TreeSwapRow.Leaf(package)));
        }

        RefreshLooks();
    }

    private void ApplyLookOps(IReadOnlyList<EditOp> ops, string title)
    {
        try
        {
            _services.Projects.Apply(ops.Count == 1 ? ops[0] : new BatchOp(title, ops));
            _services.Notifications.Info(title, Loc.T("Map.Looks.InGame") + Loc.T("Map.CtrlZUndoes"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(title, ex.Message);
        }
    }
}
