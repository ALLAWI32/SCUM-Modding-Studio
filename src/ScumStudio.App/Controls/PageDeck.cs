using Avalonia;
using Avalonia.Controls;

namespace ScumStudio.App.Controls;

/// <summary>
/// The page area: each page's view is built once (see <see cref="ViewLocator"/>) and stays in the window; switching pages
/// only changes which one is visible. A page keeps everything it had - the Map its camera and uploaded scene, Assets its
/// scroll position - and its OpenGL controls are never detached and attached again: re-attaching an
/// <c>OpenGlControlBase</c> let the compositor copy a texture whose context was already gone
/// (AccessViolationException in Avalonia's <c>GlSkiaImportedImage.SnapshotWithAutomaticSync</c>).
/// </summary>
public sealed class PageDeck : Panel
{
    /// <summary>The page view model to show.</summary>
    public static readonly StyledProperty<object?> CurrentProperty =
        AvaloniaProperty.Register<PageDeck, object?>(nameof(Current));

    private readonly Dictionary<object, Control> _views = new(ReferenceEqualityComparer.Instance);
    private readonly ViewLocator _locator = new();

    static PageDeck() => CurrentProperty.Changed.AddClassHandler<PageDeck>((deck, _) => deck.Show());

    /// <inheritdoc cref="CurrentProperty" />
    public object? Current
    {
        get => GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    /// <summary>The view shown now, or null.</summary>
    public Control? CurrentView { get; private set; }

    private void Show()
    {
        Control? view = null;
        if (Current is { } page && !_views.TryGetValue(page, out view))
        {
            view = _locator.Build(page) ?? new TextBlock();
            view.DataContext = page;
            _views[page] = view;
            Children.Add(view);
        }

        foreach (var child in Children)
        {
            child.IsVisible = ReferenceEquals(child, view);
        }

        CurrentView = view;
    }
}
