using Avalonia;
using Avalonia.Controls;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>View of the Assets page.</summary>
public partial class AssetsPageView : UserControl
{
    /// <summary>The 3D preview fills the details panel's height and the panel takes more of the page (its Expand button).</summary>
    public static readonly StyledProperty<bool> IsPreviewExpandedProperty =
        AvaloniaProperty.Register<AssetsPageView, bool>(nameof(IsPreviewExpanded));

    /// <summary>Height of the preview region (read-only): <see cref="CompactPreviewHeight"/>, or the panel's height when expanded.</summary>
    public static readonly StyledProperty<double> PreviewHeightProperty =
        AvaloniaProperty.Register<AssetsPageView, double>(nameof(PreviewHeight), CompactPreviewHeight);

    /// <summary>The preview's height in the details panel.</summary>
    internal const double CompactPreviewHeight = 240;

    /// <summary>The details panel's width (the browser grid's third column).</summary>
    private const double CompactDetailsWidth = 360;

    /// <summary>What the details show above the preview: their margin (16 top and bottom), the title row (40) and its spacing (12).</summary>
    private const double DetailsChrome = 84;

    /// <summary>Creates the view.</summary>
    public AssetsPageView()
    {
        InitializeComponent();
        // The tile grid cuts the list into rows itself (Avalonia has no virtualizing wrap panel), so it needs the width.
        PackageGridScroll.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && DataContext is AssetsPageViewModel vm)
            {
                vm.TileColumns = AssetsPageViewModel.ColumnsFor(e.NewSize.Width);
            }
        };
        DetailsScroll.SizeChanged += (_, _) => FitPreview();
    }

    /// <inheritdoc cref="IsPreviewExpandedProperty" />
    public bool IsPreviewExpanded
    {
        get => GetValue(IsPreviewExpandedProperty);
        set => SetValue(IsPreviewExpandedProperty, value);
    }

    /// <inheritdoc cref="PreviewHeightProperty" />
    public double PreviewHeight
    {
        get => GetValue(PreviewHeightProperty);
        private set => SetValue(PreviewHeightProperty, value);
    }

    /// <summary>The preview's height in a details panel <paramref name="detailsHeight"/> tall.</summary>
    internal static double PreviewHeightFor(bool expanded, double detailsHeight) =>
        expanded ? Math.Max(CompactPreviewHeight, Math.Floor(detailsHeight - DetailsChrome)) : CompactPreviewHeight;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPreviewExpandedProperty)
        {
            // Wider too: the tiles keep a third of the space (two columns of tiles at 1280 px), the details never get narrower.
            var details = AssetsBody.ColumnDefinitions[2];
            details.Width = IsPreviewExpanded ? new GridLength(2, GridUnitType.Star) : new GridLength(CompactDetailsWidth);
            details.MinWidth = IsPreviewExpanded ? CompactDetailsWidth : 0;
            FitPreview();
        }
    }

    private void FitPreview() => PreviewHeight = PreviewHeightFor(IsPreviewExpanded, DetailsScroll.Bounds.Height);
}
