using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using ScumStudio.App.Controls;

namespace ScumStudio.Tests.App;

/// <summary>
/// The mouse must reach the 3D viewport. OpenGlControlBase draws through a child surface visual that Avalonia's hit
/// test ignores, so without its own hit area every click, drag and wheel went to the panel behind it (owner report:
/// "no control at all" on the Map page).
/// </summary>
public sealed class LevelViewportInputTests
{
    [AvaloniaFact]
    public void PointerHitTestFindsTheViewport()
    {
        var viewport = new LevelViewport();
        var window = new Window { Width = 400, Height = 300, Content = new Border { Background = Brushes.Black, Padding = new Thickness(20), Child = viewport } };
        window.Show();
        HeadlessUi.Pump();

        Assert.Same(viewport, window.InputHitTest(new Point(200, 150)));
        Assert.NotSame(viewport, window.InputHitTest(new Point(5, 5)));
        window.Close();
    }

    [AvaloniaFact]
    public void PointerHitTestFindsTheMeshPreview()
    {
        var preview = new MeshPreview();
        var window = new Window { Width = 400, Height = 300, Content = new Border { Background = Brushes.Black, Padding = new Thickness(20), Child = preview } };
        window.Show();
        HeadlessUi.Pump();

        Assert.Same(preview, window.InputHitTest(new Point(200, 150)));
        Assert.NotSame(preview, window.InputHitTest(new Point(5, 5)));
        window.Close();
    }
}

/// <summary>Drone mode: Tab toggles it, Escape always leaves it, and losing focus leaves it.</summary>
public sealed class LevelViewportDroneTests
{
    [AvaloniaFact]
    public void EscapeAndTabLeaveDroneMode()
    {
        var viewport = new LevelViewport();
        var window = new Window { Width = 400, Height = 300, Content = viewport };
        window.Show();
        HeadlessUi.Pump();
        viewport.Focus();

        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(viewport.IsDroneMode);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(viewport.IsDroneMode);

        viewport.ToggleDrone();
        Assert.True(viewport.IsDroneMode);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.False(viewport.IsDroneMode);
        window.Close();
    }
}
