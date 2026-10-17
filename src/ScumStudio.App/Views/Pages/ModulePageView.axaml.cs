using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>View of the Vehicles and Weapons pages (<see cref="ModulePageViewModel"/>).</summary>
public partial class ModulePageView : UserControl
{
    private ModulePageViewModel? _viewModel;

    /// <summary>Creates the view.</summary>
    public ModulePageView()
    {
        InitializeComponent();
        // Right-click selects the row under the pointer first, so the context menu acts on it (like a file explorer).
        ItemList.AddHandler(PointerPressedEvent, OnItemPointerPressed, RoutingStrategies.Tunnel);
        // The Vehicles studio: the 3D view's surface clicks (decals, a new part), gizmo drags and part picks go to the page.
        ItemPreview.SurfacePointer += (_, e) => _viewModel?.OnStudioSurface(e.PartIndex, new System.Numerics.Vector3(e.Origin.X, e.Origin.Y, e.Origin.Z),
            new System.Numerics.Vector3(e.Direction.X, e.Direction.Y, e.Direction.Z), e.IsDrag, e.IsRelease, e.IsCancel);
        ItemPreview.PartGizmo += (_, e) => _viewModel?.OnStudioGizmo(e.PartIndex, e.Start, e.Transform, e.Committed);
        // Decals like the game's tattoos: Ctrl/Shift+wheel and the keys size, turn and move the selected decal.
        ItemPreview.SurfaceInput += (_, e) => e.Handled = _viewModel is { } page
            && (e.Key == Key.None ? page.OnStudioWheel(e.Notches, e.Modifiers) : page.OnStudioKey(e.Key, e.Modifiers));
        ItemPreview.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScumStudio.App.Controls.MeshPreview.SelectedPartIndexProperty && e.NewValue is int index)
            {
                _viewModel?.OnStudioPick(index);
            }
        };
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.CloneRequested -= OnCloneRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ModulePageViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloneRequested += OnCloneRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ShowPaintColumn();
    }

    // The paint panel's width (owner: "make it smaller or bigger like the other panels"), kept while the app runs.
    private static double _paintWidth = 320;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModulePageViewModel.ShowStudioPanel))
        {
            ShowPaintColumn();
        }
    }

    /// <summary>The splitter and paint columns take room only while there is paint; their width is remembered.</summary>
    private void ShowPaintColumn()
    {
        var columns = PreviewBody.ColumnDefinitions;
        if (columns[2].ActualWidth > 0)
        {
            _paintWidth = columns[2].ActualWidth;
        }

        var shown = _viewModel?.ShowStudioPanel == true;
        columns[1].Width = new GridLength(shown ? 8 : 0);
        columns[2].MinWidth = shown ? 240 : 0;
        columns[2].MaxWidth = shown ? 760 : 0;
        columns[2].Width = new GridLength(shown ? _paintWidth : 0);
    }

    /// <summary>A click on the studio tab shown keeps it shown (a toggle unchecks itself on a second click).</summary>
    private void OnStudioTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: false } tab)
        {
            tab.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        }
    }

    private void OnCloneRequested(object? sender, EventArgs e)
    {
        CloneNameBox.Focus();
        CloneNameBox.SelectAll();
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ItemList).Properties.IsRightButtonPressed
            && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ModuleItemViewModel item })
        {
            ItemList.SelectedItem = item;
        }
    }
}
