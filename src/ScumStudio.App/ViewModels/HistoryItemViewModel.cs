using System.Globalization;
using ScumStudio.Level.Editing;

namespace ScumStudio.App.ViewModels;

/// <summary>A row of the history panel (one journal edit).</summary>
public sealed class HistoryItemViewModel
{
    /// <summary>Wraps <paramref name="item"/>.</summary>
    public HistoryItemViewModel(HistoryItem item) => Item = item;

    /// <summary>The journal item.</summary>
    public HistoryItem Item { get; }

    /// <summary>Sequence number.</summary>
    public long Seq => Item.Seq;

    /// <summary>"#12".</summary>
    public string SeqText => "#" + Item.Seq.ToString(CultureInfo.InvariantCulture);

    /// <summary>Local time of the edit.</summary>
    public string TimeText => Item.At.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Summary of the edit.</summary>
    public string Summary => Item.Summary;

    /// <summary>Level name (last path segment) or empty.</summary>
    public string LevelName => Item.Level is { } level ? level[(level.LastIndexOf('/') + 1)..] : string.Empty;

    /// <summary>Status label.</summary>
    public string StatusText => Localization.Loc.Instance.Or("History.Status." + Item.Status, Item.Status.ToString());

    /// <summary>True when applied.</summary>
    public bool IsApplied => Item.Status == HistoryStatus.Applied;

    /// <summary>True when undone (redoable).</summary>
    public bool IsUndone => Item.Status == HistoryStatus.Undone;

    /// <summary>True when discarded.</summary>
    public bool IsDiscarded => Item.Status == HistoryStatus.Discarded;

    /// <summary>Row opacity (undone/discarded edits are dimmed).</summary>
    public double Opacity => IsApplied ? 1.0 : 0.55;
}
