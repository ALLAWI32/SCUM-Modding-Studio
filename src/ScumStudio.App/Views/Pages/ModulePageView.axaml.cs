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
        }

        _viewModel = DataContext as ModulePageViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloneRequested += OnCloneRequested;
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
