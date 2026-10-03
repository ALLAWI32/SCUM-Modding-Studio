namespace ScumStudio.App.ViewModels;

/// <summary>An entry of the navigation rail; creates its page on first use.</summary>
public sealed class NavItemViewModel : ViewModelBase
{
    private readonly Func<PageViewModel> _factory;
    private PageViewModel? _page;

    /// <summary>Creates an entry.</summary>
    public NavItemViewModel(string key, string title, string iconKey, string? badge, Func<PageViewModel> factory)
    {
        Key = key;
        _title = title;
        IconKey = iconKey;
        Badge = badge;
        _factory = factory;
    }

    /// <summary>Navigation key.</summary>
    public string Key { get; }

    private readonly string _title;

    /// <summary>Label in the current UI language (string key <c>Nav.&lt;key&gt;</c>; the constructor's English text is the fallback).</summary>
    public string Title => Localization.Loc.Instance.Or($"Nav.{Key}", _title);

    /// <summary>Hover description of the tab (string key <c>Nav.&lt;key&gt;.Tip</c>).</summary>
    public string Tip => Localization.Loc.Instance.Or($"Nav.{Key}.Tip", string.Empty);

    /// <summary>Icon resource key (see <c>Styles/Icons.axaml</c>).</summary>
    public string IconKey { get; }

    /// <summary>Small badge text such as "Soon", or null.</summary>
    public string? Badge { get; }

    /// <summary>True when <see cref="Badge"/> is set.</summary>
    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    /// <summary>True once the page was created.</summary>
    public bool IsPageCreated => _page is not null;

    /// <summary>The page (created on first access).</summary>
    public PageViewModel Page => _page ??= _factory();

    /// <summary>Re-reads <see cref="Title"/> and lets a created page refresh its texts after the UI language changed.</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tip));
        _page?.OnLanguageChanged();
    }
}
