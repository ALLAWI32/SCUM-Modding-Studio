using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Level.Editing;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The History panel's right-click menu (owner: "so I can see what an edit touched"): Go to flies the camera to the
/// edit's object and selects it, Select only selects it. Whole-island edits (a ground look, a package value) have no place.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>The history row the menu acts on (right-click selects it first).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoToEditCommand), nameof(SelectEditCommand))]
    private HistoryItemViewModel? _selectedHistoryItem;

    private bool CanActOnHistory() => SelectedHistoryItem is not null;

    [RelayCommand(CanExecute = nameof(CanActOnHistory))]
    private Task GoToEditAsync() => ShowEditAsync(SelectedHistoryItem, frame: true);

    [RelayCommand(CanExecute = nameof(CanActOnHistory))]
    private Task SelectEditAsync() => ShowEditAsync(SelectedHistoryItem, frame: false);

    /// <summary>
    /// Selects the object <paramref name="row"/>'s edit touched (loading its level with the shown ones when it is not
    /// loaded) and, with <paramref name="frame"/>, asks the view to fly the camera to it. False when the edit has no
    /// place or its object is not in the map now.
    /// </summary>
    public async Task<bool> ShowEditAsync(HistoryItemViewModel? row, bool frame)
    {
        if (row is null)
        {
            return false;
        }

        if (TargetOf(row.Item.Op) is not { } target)
        {
            _services.Notifications.Info(Loc.T("History.GoTo"), Loc.T("History.NoPlace"));
            return false;
        }

        var (actor, component, index) = target;
        var item = ActorOf(actor);
        if (item is null && World?.Packages.Any(p => p.IsMap && string.Equals(p.PackagePath, actor.Level, StringComparison.OrdinalIgnoreCase)) == true)
        {
            var shown = PreparedScene?.Documents.Select(d => d.PackagePath).Where(p => !string.Equals(p, actor.Level, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
            await LoadLevelsAsync([actor.Level, .. shown]).ConfigureAwait(true);
            item = ActorOf(actor);
        }

        if (item is null)
        {
            _services.Notifications.Info(Loc.T("History.GoTo"), Loc.F("History.NotInMap", actor.Actor));
            return false;
        }

        if (component is not null)
        {
            // A road piece and a part of a building are told apart by what the component is.
            var kind = index == InstanceKey.Part && item.Actor.FindComponent(component) is { SplineMesh: not null } ? InstanceKey.Segment : index;
            SelectedInstanceKey = InstanceKey.Of(item.SelectableId, component, kind);
        }
        else
        {
            SelectedInstanceKey = null;
        }

        SelectedActor = item;
        if (frame)
        {
            FrameSelectionRequested?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>The object an edit touched: the actor, and for an instance or piece its component and index; null for edits without a place.</summary>
    internal static (ActorRef Actor, string? Component, int Index)? TargetOf(EditOp op) => op switch
    {
        BatchOp batch => batch.Ops.Select(TargetOf).FirstOrDefault(t => t is not null),
        SetInstanceTransformOp i => (i.Target.ActorRef, i.Target.Component, i.Target.Index),
        DeleteInstanceOp i => (i.Target.ActorRef, i.Target.Component, i.Target.Index),
        RestoreInstanceOp i => (i.Target.ActorRef, i.Target.Component, i.Target.Index),
        AddInstanceOp i => (i.Target.ActorRef, i.Target.Component, i.Target.Index),
        RemoveAddedInstanceOp i => (i.Target.ActorRef, i.Target.Component, i.Target.Index),
        SetTransformOp { Component: { } piece } t => (t.Target, piece, InstanceKey.Part),
        SwaySegmentOp s => (s.Target, s.Component, InstanceKey.Segment),
        _ => op.GetPrimaryTarget() is { } actor ? (actor, null, 0) : null,
    };
}
