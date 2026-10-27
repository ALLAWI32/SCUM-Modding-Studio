namespace ScumStudio.App.ViewModels;

/// <summary>An entry of the navigation rail; creates its page on first use.</summary>
public sealed class NavItemViewModel : ViewModelBase
{
    private readonly Func<PageViewModel> _factory;
    private PageViewModel? _page;

    /// <summary>Creates an entry.</summary>
    public NavItemViewModel(string key, string title, string iconKey, string? badge, Func<PageViewModel> factory, string? groupKey = null)
    {
        GroupKey = groupKey;
        Key = key;
        _title = title;
        IconKey = iconKey;
        Badge = badge;
        _factory = factory;
    }

    /// <summary>Navigation key.</summary>
    public string Key { get; }

    /// <summary>The group this entry starts in the left rail (world, content, server, tools), or null when it belongs to the group above.</summary>
    public string? GroupKey { get; }

    /// <summary>True when this entry opens a group in the rail.</summary>
    public bool HasGroup => GroupKey is not null;

    /// <summary>The group's name in the current UI language (string key <c>Nav.group.&lt;key&gt;</c>).</summary>
    public string GroupTitle => GroupKey is null ? string.Empty : Localization.Loc.Instance.Or($"Nav.group.{GroupKey}", GroupKey);

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
        OnPropertyChanged(nameof(GroupTitle));
        _page?.OnLanguageChanged();
    }
}
