using CommunityToolkit.Mvvm.ComponentModel;

namespace ScumStudio.App.ViewModels;

/// <summary>Base class of the pages shown in the main window's content area.</summary>
public abstract partial class PageViewModel : ViewModelBase
{
    /// <summary>Creates a page.</summary>
    /// <param name="key">Stable key used for navigation (e.g. <c>assets</c>).</param>
    /// <param name="title">Page title.</param>
    /// <param name="subtitle">One-line description under the title.</param>
    /// <remarks>
    /// <paramref name="title"/> and <paramref name="subtitle"/> are the English fallbacks of the string keys
    /// <c>Page.&lt;key&gt;.Title</c> and <c>Page.&lt;key&gt;.Subtitle</c> (see <see cref="Localization.Loc"/>).
    /// </remarks>
    protected PageViewModel(string key, string title, string subtitle)
    {
        Key = key;
        _title = title;
        _subtitleFallback = subtitle;
        _subtitle = _shownStaticSubtitle = StaticSubtitle;
    }

    private readonly string _title;
    private readonly string _subtitleFallback;
    private string _shownStaticSubtitle;

    /// <summary>Navigation key.</summary>
    public string Key { get; }

    /// <summary>Title in the current UI language.</summary>
    public string Title => Localization.Loc.Instance.Or($"Page.{Key}.Title", _title);

    private string StaticSubtitle => Localization.Loc.Instance.Or($"Page.{Key}.Subtitle", _subtitleFallback);

    /// <summary>Icon resource key of the page (same glyph as its workspace tab).</summary>
    public virtual string IconKey => Key switch
    {
        "map" => "Icon.Map",
        "vehicles" => "Icon.Vehicle",
        "weapons" => "Icon.Weapon",
        "assets" => "Icon.Assets",
        "projects" => "Icon.Projects",
        "settings" => "Icon.Settings",
        _ => "Icon.Info",
    };

    /// <summary>Subtitle (may change, e.g. with counts).</summary>
    [ObservableProperty]
    private string _subtitle;

    /// <summary>Called each time the page becomes visible.</summary>
    public virtual void OnNavigatedTo()
    {
    }

    /// <summary>
    /// Called after the UI language changed: refreshes <see cref="Title"/> and the static subtitle. Pages that compose
    /// texts in code override this to rebuild them (counts in the subtitle refresh on the page's next update).
    /// </summary>
    public virtual void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        if (Subtitle == _shownStaticSubtitle)
        {
            Subtitle = _shownStaticSubtitle = StaticSubtitle;
        }
    }
}

/// <summary>A page that reacts to the global search box in the top bar.</summary>
public interface ISearchablePage
{
    /// <summary>Filters the page by <paramref name="text"/> (null/empty clears the filter).</summary>
    void ApplySearch(string? text);
}

/// <summary>A key/value row of a details or properties panel.</summary>
/// <param name="Name">Label.</param>
/// <param name="Value">Value text.</param>
public sealed record PropertyRow(string Name, string Value);

/// <summary>A labelled count (chips such as "Landscape 400").</summary>
/// <param name="Label">Label.</param>
/// <param name="Count">Count.</param>
public sealed record CountChip(string Label, int Count)
{
    /// <summary>Count formatted with thousands separators.</summary>
    public string CountText => Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
}
