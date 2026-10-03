using Avalonia;
using Avalonia.Controls;
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
        if (e.PropertyName == nameof(ModulePageViewModel.HasPaints))
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

        var shown = _viewModel?.HasPaints == true;
        columns[1].Width = new GridLength(shown ? 8 : 0);
        columns[2].MinWidth = shown ? 240 : 0;
        columns[2].MaxWidth = shown ? 760 : 0;
        columns[2].Width = new GridLength(shown ? _paintWidth : 0);
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
