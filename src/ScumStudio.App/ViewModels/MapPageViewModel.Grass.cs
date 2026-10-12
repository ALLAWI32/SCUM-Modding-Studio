using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Clear grass under it" (owner and a Discord user, 2026-10-09: "I placed a bridge and the meadow grass grows through
/// it"): a checkbox for the placed or moved objects of the selection (one actor, or the whole actors of a multi-selection).
/// Every change is a <see cref="SetClearGrassOp"/>; the export does the clearing (see <see cref="GrassClearing"/>). The
/// view draws no landscape grass, so there is none to hide; bushes stay (the export leaves the game's foliage as cooked).
/// </summary>
public sealed partial class MapPageViewModel
{
    private bool _syncingGrass;

    /// <summary>True when the selection has a placed or moved object.</summary>
    [ObservableProperty]
    private bool _hasGrassOption;

    /// <summary>On: the export clears the grass and bushes under the selected objects.</summary>
    [ObservableProperty]
    private bool _clearsGrass;

    /// <summary>The selected actors the setting applies to: added, moved or bent ones.</summary>
    private List<ActorItemViewModel> GrassTargets()
    {
        if (_services.Projects.Current?.State is not { } state)
        {
            return [];
        }

        IEnumerable<ActorItemViewModel> items = HasGroup
            ? GroupItems().Where(g => g.Instance is null).Select(g => g.Item)
            : SelectedActor is { } selected ? [selected] : [];
        return items.Where(i => !i.IsDeleted && (i.IsAdded || state.GetTransformOverride(i.Reference) is not null || state.Bends.ContainsKey(i.Reference)))
            .Distinct().ToList();
    }

    /// <summary>Shows the selection's setting (called with the selection's other panels and after every edit).</summary>
    private void RefreshGrassOption()
    {
        _syncingGrass = true;
        try
        {
            var targets = GrassTargets();
            var state = _services.Projects.Current?.State;
            HasGrassOption = targets.Count > 0;
            ClearsGrass = state is not null && targets.Count > 0 && targets.All(t => state.ClearsGrass(t.Reference));
        }
        finally
        {
            _syncingGrass = false;
        }
    }

    partial void OnClearsGrassChanged(bool value)
    {
        if (_syncingGrass || _services.Projects.Current?.State is not { } state)
        {
            return;
        }

        // Back to the default leaves no setting behind (on for added objects, off for moved ones).
        var ops = GrassTargets().Where(t => state.ClearsGrass(t.Reference) != value)
            .Select(t => (EditOp)new SetClearGrassOp(t.Reference, state.GetClearGrass(t.Reference), value == state.IsAdded(t.Reference) ? null : value))
            .ToList();
        if (ops.Count > 0)
        {
            try
            {
                var entry = _services.Projects.Apply(ops.Count == 1 ? ops[0] : new BatchOp(Loc.T("Map.Grass.Clear"), ops));
                _services.Notifications.Info(Loc.T("Map.Grass.Title"), entry.Op.Describe());
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                _services.Notifications.Error(Loc.T("Map.Grass.Title"), ex.Message);
            }
        }

        RefreshHiddenIds();
    }
}
