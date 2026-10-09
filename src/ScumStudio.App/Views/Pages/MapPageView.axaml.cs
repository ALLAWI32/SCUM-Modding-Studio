using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.App.Views.Pages;

/// <summary>View of the MapPage page: hosts the 3D viewport and forwards Delete / F keys to the view model.</summary>
public partial class MapPageView : UserControl
{
    private MapPageViewModel? _viewModel;
    private DispatcherTimer? _paintTimer; // timer mode: plants one object a tick while the brush button is held
    private FVector _paintAt;
    private bool _maximized;
    private readonly Avalonia.Threading.DispatcherTimer _worldTimer;
    private int _ticks;
    private GridLength[]? _savedSizes;
    private Window? _popout;

    /// <summary>Creates the view.</summary>
    public MapPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MapPageViewModel);
        Viewport3d.PickToggled += (_, pick) => _viewModel?.ToggleGroup(pick.Id, pick.Instance);
        Viewport3d.BrushPainted += (_, stroke) =>
        {
            if (_viewModel is { BrushPaint: true } painting)
            {
                if (painting.PaintTimed)
                {
                    // Timer mode: the brush only says where it is; a tick plants one object there.
                    _paintAt = stroke.To;
                    if (_paintTimer is null)
                    {
                        painting.PaintOneAt(_paintAt);
                        _paintTimer = new DispatcherTimer(TimeSpan.FromSeconds(Math.Max(0.05, painting.PaintEvery)), DispatcherPriority.Input,
                            (_, _) => _viewModel?.PaintOneAt(_paintAt));
                        _paintTimer.Start();
                    }

                    return;
                }

                painting.PaintAt(stroke.To, stroke.From);
            }
            else
            {
                _viewModel?.BrushAt(stroke.To, stroke.From);
            }
        };
        Viewport3d.BrushStrokeEnded += (_, _) =>
        {
            _paintTimer?.Stop();
            _paintTimer = null;
            _viewModel?.EndPaintStroke();
        };
        // The Replacer's cards are filled when the menu opens (the selection changes far more often); while a menu is open
        // the first pictures of its lists are made on workers, closing it lets them go (the disk cache keeps them).
        var replace = (Flyout)ReplaceButton.Flyout!;
        replace.Opening += (_, _) =>
        {
            _viewModel?.RefreshReplaceCandidates();
            _viewModel?.ReplacePicker.Prefetch();
        };
        replace.Closed += (_, _) => _viewModel?.ReplacePicker.Close();
        var paint = (Flyout)PaintPaletteButton.Flyout!;
        paint.Opening += (_, _) =>
        {
            _viewModel?.RefreshPaintCandidates();
            _viewModel?.PaintPicker.Prefetch();
        };
        paint.Closed += (_, _) => _viewModel?.PaintPicker.Close();
        // Plant chosen with nothing to plant yet: the palette opens right away (the owner looked for where to pick trees).
        BrushPaintModeButton.IsCheckedChanged += (_, _) =>
        {
            if (BrushPaintModeButton.IsChecked == true && _viewModel is { HasPaintPalette: false })
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PaintPaletteButton.Flyout?.ShowAt(PaintPaletteButton));
            }
        };
        var landscape = (Flyout)LandscapeButton.Flyout!;
        _landscapeFlyout = landscape;
        landscape.Opening += (_, _) =>
        {
            _viewModel?.TreeToSwap.Prefetch();
            _viewModel?.TreeSwapWith.Prefetch();
        };
        landscape.Closed += (_, _) =>
        {
            _viewModel?.TreeToSwap.Close();
            _viewModel?.TreeSwapWith.Close();
        };
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
        Viewport3d.TransformDragged += (_, e) => _viewModel?.ApplyDraggedTransform(e.Id, e.RootWorld, e.Scaled);
        // Right-click selects the history row under the pointer first, so its menu (Go to, Select) acts on it.
        HistoryList.AddHandler(PointerPressedEvent, OnHistoryPointerPressed, RoutingStrategies.Tunnel);
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

            // The camera is kept every 3 s while something is shown, so the map reopens where it was left.
            if (_viewModel is { HasView: true } shown && ++_ticks % 6 == 0)
            {
                shown.RememberView(Viewport3d.CameraUe, Viewport3d.Camera.Yaw, Viewport3d.Camera.Pitch);
            }
        };
        _worldTimer.Start();
    }

    private void Attach(MapPageViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.FrameSelectionRequested -= OnFrameSelectionRequested;
            _viewModel.CopiedObjects -= OnCopiedObjects;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.CopiedObjects += OnCopiedObjects;
            // First open of the map this run: start where the camera was left, so streaming loads that area right away.
            if (_viewModel.SavedView is { } saved && !_viewModel.HasView)
            {
                var at = new FVector(saved.X, saved.Y, saved.Z);
                Viewport3d.InitialView = (at, saved.Yaw, saved.Pitch);
                Viewport3d.SetView(at, null, saved.Yaw, saved.Pitch);
            }

            _viewModel.FrameSelectionRequested += OnFrameSelectionRequested;
            _viewModel.AimPointProvider = () => Viewport3d.AimPointUe(MapPageViewModel.PlacementDistance);
        }
    }

    private void OnFrameSelectionRequested(object? sender, EventArgs e)
    {
        // Before the GL scene is up the camera still goes there: 15 m away, looking at the object.
        if (!Viewport3d.FrameSelection() && _viewModel?.SelectedRootWorld is { } root)
        {
            Viewport3d.SetView(root.Translation + new FVector(-1200f, -600f, 700f), root.Translation, null, null);
        }
    }

    private void OnHistoryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(HistoryList).Properties.IsRightButtonPressed
            && (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: HistoryItemViewModel row })
        {
            HistoryList.SelectedItem = row;
        }
    }

    // Where the camera was before "Last object" jumped; pressing it again goes back there (owner: "as if nothing happened").
    private (System.Numerics.Vector3 Position, float Yaw, float Pitch)? _viewBeforeJump;

    private void OnLastObjectClick(object? sender, RoutedEventArgs e) => JumpToLastObject();

    private void OnExtendClick(object? sender, RoutedEventArgs e) => _viewModel?.Extend();

    private void OnAddObjectOpened(object? sender, EventArgs e) => _viewModel?.PrepareTraders();

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

    private TopLevel? _keyRoot;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The shortcuts also work while focus is outside the page (a toast, the tab bar, the title bar): the owner found
        // Delete, Ctrl+C and Ctrl+V "sometimes working, sometimes not". The page's own handler runs first and marks the key.
        _keyRoot = TopLevel.GetTopLevel(this);
        _keyRoot?.AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Bubble);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyRoot?.RemoveHandler(KeyDownEvent, OnRootKeyDown);
        _keyRoot = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsEffectivelyVisible)
        {
            HandleShortcut(e);
        }
    }

    /// <summary>Copy stored something in the studio: the system clipboard empties, so Ctrl+V pastes that and not an older Assets path.</summary>
    private void OnCopiedObjects() => _ = TopLevel.GetTopLevel(this)?.Clipboard?.ClearAsync();

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
            _viewModel.CopySelectedCommand.Execute(null); // empties the system clipboard too (CopiedObjects)
            e.Handled = true;
        }
        else if (e.Key == Key.E && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _viewModel.Extend(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.End && e.KeyModifiers == KeyModifiers.None && _viewModel.FitToGroundCommand.CanExecute(null))
        {
            _viewModel.FitToGroundCommand.Execute(null); // Unreal's key for dropping an object to the floor
            e.Handled = true;
        }
        else if (e.Key == Key.OemComma && e.KeyModifiers == KeyModifiers.None)
        {
            _viewModel.CycleOrientation(); // Blender's key for the transform orientation
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

    private Flyout? _landscapeFlyout;

    /// <summary>Trees in the Landscape menu swap a tree type on the whole island; with an object selected the owner wanted the Replacer.</summary>
    private void OnTreesToReplace(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _landscapeFlyout?.Hide();
        if (ReplaceButton.IsEnabled)
        {
            ReplaceButton.Flyout?.ShowAt(ReplaceButton);
        }
    }
}
