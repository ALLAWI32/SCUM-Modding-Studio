namespace ScumStudio.App.ViewModels;

/// <summary>A planned feature card on a module page.</summary>
/// <param name="Title">Feature name.</param>
/// <param name="Description">What it does and which proven technique it builds on.</param>
/// <param name="IconKey">Icon resource key.</param>
/// <param name="Status">Short status label ("Planned", "Research", ...).</param>
public sealed record FeatureCard(string Title, string Description, string IconKey, string Status);

/// <summary>A module that is designed but not built yet: an informative overview page.</summary>
public abstract class PlaceholderPageViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    protected PlaceholderPageViewModel(string key, string title, string subtitle, string iconKey, string headline, string body,
        IReadOnlyList<FeatureCard> features, IReadOnlyList<string> foundations)
        : base(key, title, subtitle)
    {
        _iconKey = iconKey;
        Headline = headline;
        Body = body;
        Features = features;
        Foundations = foundations;
    }

    private readonly string _iconKey;

    /// <summary>Hero icon.</summary>
    public override string IconKey => _iconKey;

    /// <summary>Hero headline.</summary>
    public string Headline { get; }

    /// <summary>Hero paragraph.</summary>
    public string Body { get; }

    /// <summary>Planned features.</summary>
    public IReadOnlyList<FeatureCard> Features { get; }

    /// <summary>Shared ScumStudio libraries the module reuses.</summary>
    public IReadOnlyList<string> Foundations { get; }
}
