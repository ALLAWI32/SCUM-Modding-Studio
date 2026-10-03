using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>View of the MapPage page: hosts the 3D viewport and forwards Delete / F keys to the view model.</summary>
public partial class MapPageView : UserControl
{
    private MapPageViewModel? _viewModel;
    private bool _maximized;
    private readonly Avalonia.Threading.DispatcherTimer _worldTimer;
    private GridLength[]? _savedSizes;
    private Window? _popout;

    /// <summary>Creates the view.</summary>
    public MapPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MapPageViewModel);
        Viewport3d.PickToggled += (_, pick) => _viewModel?.ToggleGroup(pick.Id, pick.Instance);
        // Shape handles dragged in the view: live preview, journaled when let go.
        Viewport3d.ShapeHandleDragged += (_, drag) =>
        {
            if (_viewModel is { } vm)
            {
                vm.IsShapeDragging = true;
                vm.DragShapeHandle(drag.Index, drag.Sway);
            }
        };
        Viewport3d.ShapeEndDragged += (_, drag) =>
        {
            if (_viewModel is { } vm)
            {
                vm.IsShapeDragging = true;
                vm.DragShapeEnd(drag.AtEnd, drag.Value, drag.Welded);
            }
        };
        Viewport3d.LegDragged += (_, down) =>
        {
            if (_viewModel is { } vm)
            {
                vm.IsShapeDragging = true;
                vm.DragLegs(down);
            }
        };
        Viewport3d.ScaleHandleDragged += (_, world) =>
        {
            if (_viewModel is { } vm)
            {
                vm.IsShapeDragging = true;
                vm.DragScale(world);
            }
        };
        Viewport3d.ShapeHandleReleased += (_, _) =>
        {
            if (_viewModel is { } vm)
            {
                vm.IsShapeDragging = false;
                vm.CommitShape();
            }
        };
        Viewport3d.ActorPicked += (_, id) =>
        {
            _viewBeforeJump = null; // a click of its own: "Last object" starts over
            if (id != 0)
            {
                Viewport3d.Focus();
            }
        };
        Viewport3d.TransformDragged += (_, e) => _viewModel?.ApplyDraggedTransform(e.Id, e.RootWorld);
        // Shape sliders: the edit is journaled when the thumb is let go (one undo step per drag).
        ShapePanel.AddHandler(Avalonia.Controls.Primitives.Thumb.DragStartedEvent, (_, _) =>
        {
            if (_viewModel is { } vm)
            {
                vm.IsShapeDragging = true;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        ShapePanel.AddHandler(Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, (_, _) =>
        {
            if (_viewModel is { } vm)
            {
                vm.IsShapeDragging = false;
                vm.CommitShape();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        // World mode follows the camera: the cell under it loads in detail.
        _worldTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _worldTimer.Tick += (_, _) =>
        {
            if (_viewModel is { IsWorldMode: true } vm)
            {
                vm.UpdateWorldCamera(Viewport3d.CameraUe);
            }
        };
        _worldTimer.Start();
    }

    private void Attach(MapPageViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.FrameSelectionRequested -= OnFrameSelectionRequested;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.FrameSelectionRequested += OnFrameSelectionRequested;
            _viewModel.AimPointProvider = () => Viewport3d.AimPointUe(MapPageViewModel.PlacementDistance);
        }
    }

    private void OnFrameSelectionRequested(object? sender, EventArgs e) => Viewport3d.FrameSelection();

    // Where the camera was before "Last object" jumped; pressing it again goes back there (owner: "as if nothing happened").
    private (System.Numerics.Vector3 Position, float Yaw, float Pitch)? _viewBeforeJump;

    private void OnLastObjectClick(object? sender, RoutedEventArgs e) => JumpToLastObject();

    private void OnExtendClick(object? sender, RoutedEventArgs e) => _viewModel?.Extend();

    /// <summary>First press: to the object picked before, camera on it. Second press: back to the object and view before.</summary>
    private void JumpToLastObject()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewBeforeJump is { } view)
        {
            _viewBeforeJump = null;
            _viewModel.GoToPrevious(frame: false);
            Viewport3d.RestoreView(view);
            return;
        }

        var before = Viewport3d.SaveView();
        if (_viewModel.GoToPrevious(frame: true))
        {
            _viewBeforeJump = before;
        }
    }

    private void OnFrameAllClick(object? sender, RoutedEventArgs e) => Viewport3d.FrameAll();

    private void OnDroneClick(object? sender, RoutedEventArgs e) => Viewport3d.ToggleDrone();

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => SetMaximized(!_maximized);

    /// <summary>Hides the side panels and the entity list so the 3D view fills the page (and back, with the user's sizes).</summary>
    private void SetMaximized(bool on)
    {
        if (on == _maximized)
        {
            return;
        }

        _maximized = on;
        var columns = MapBody.ColumnDefinitions;
        var rows = CenterGrid.RowDefinitions;
        if (on)
        {
            _savedSizes = [columns[0].Width, columns[1].Width, columns[3].Width, columns[4].Width, rows[1].Height, rows[2].Height];
            columns[0].Width = columns[1].Width = columns[3].Width = columns[4].Width = new GridLength(0);
            rows[1].Height = rows[2].Height = new GridLength(0);
        }
        else if (_savedSizes is { } saved)
        {
            (columns[0].Width, columns[1].Width, columns[3].Width, columns[4].Width, rows[1].Height, rows[2].Height) =
                (saved[0], saved[1], saved[2], saved[3], saved[4], saved[5]);
        }

        LeftPanel.IsVisible = RightPanel.IsVisible = EntitiesPanel.IsVisible = !on;
        LeftSplitter.IsVisible = RightSplitter.IsVisible = RowSplitter.IsVisible = !on;
        MaximizeButton.Content = on ? Localization.Loc.T("Map.Restore") : Localization.Loc.T("Map.Maximize");
        Viewport3d.Focus();
    }

    /// <summary>Moves the 3D view (with its overlays) into its own window, e.g. for a second monitor; closing it brings it back.</summary>
    private void OnPopOutClick(object? sender, RoutedEventArgs e)
    {
        if (_popout is not null)
        {
            _popout.Close();
            return;
        }

        var content = ViewportContent;
        Viewport.Child = new TextBlock
        {
            Text = Localization.Loc.T("Map.PoppedOut"),
            Classes = { "muted" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var window = new Window
        {
            Title = Localization.Loc.T("Map.PopOutTitle"),
            Width = 1440,
            Height = 900,
            DataContext = DataContext,
            Content = content,
        };
        window.KeyDown += (_, args) => HandleShortcut(args); // Delete, Ctrl+C/V work in the pop-out too
        window.Closed += (_, _) =>
        {
            window.Content = null;
            Viewport.Child = content;
            _popout = null;
            PopOutButton.Content = Localization.Loc.T("Map.PopOut");
        };
        _popout = window;
        PopOutButton.Content = Localization.Loc.T("Map.DockBack");
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        HandleShortcut(e);
    }

    private void HandleShortcut(KeyEventArgs e)
    {
        if (e.Handled || _viewModel is null || e.Source is TextBox)
        {
            return;
        }

        if (e.Key == Key.F11)
        {
            SetMaximized(!_maximized);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _viewModel.SelectAllOfKind(wholeMap: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.HasKindSelection)
        {
            _viewModel.ClearKindSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _viewModel.HasKindSelection)
        {
            _ = _viewModel.DeleteKindSelectionAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _viewModel.DeleteSelectedCommand.CanExecute(null))
        {
            _viewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _viewModel.CopySelectedCommand.CanExecute(null))
        {
            _viewModel.CopySelectedCommand.Execute(null);
            // The last copy wins: a path copied in Assets earlier must not beat this object on Ctrl+V.
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.ClearAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.E && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _viewModel.Extend(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None)
        {
            JumpToLastObject();
            e.Handled = true;
        }
        else if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = PasteAsync(_viewModel);
        }
    }

    /// <summary>Ctrl+V: an object path copied in Assets is added in front of the camera; otherwise the object copied here.</summary>
    private async Task PasteAsync(MapPageViewModel viewModel)
    {
        var text = TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        if (text?.TrimStart().StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) == true && viewModel.AddObject(text))
        {
            return;
        }

        if (viewModel.PasteCommand.CanExecute(null))
        {
            viewModel.PasteCommand.Execute(null);
        }
    }
}
