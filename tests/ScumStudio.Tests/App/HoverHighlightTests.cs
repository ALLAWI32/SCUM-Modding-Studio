using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Controls;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-12): "a clear square behind the icon when I hover". Ghost and icon buttons on the title strip (Bg0)
/// and on a panel header (Surface) draw a raised fill with a hairline frame under the pointer, their glyph brightens,
/// and nothing moves when the pointer comes and goes.
/// </summary>
[Collection(LanguageCollection.Name)] // shows the main window: never next to a test that switches the UI language
public sealed class HoverHighlightTests
{
    [AvaloniaFact]
    public void HoveringAnIconButtonShowsASquareBehindIt()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            vm.NavigateTo("map");
            HeadlessUi.Pump();
            var undo = HeadlessUi.FindNamed<Button>(window, "UndoButton")!;
            undo.Command = new RelayCommand(() => { }); // undo needs history; the style is what is under test
            var setup = HeadlessUi.Find<Button>(window).Single(b => b.Command == vm.OpenSetupCommand);
            HeadlessUi.Pump();

            var stroke = Brush(window, "StrokeBrush");
            var frame = Brush(window, "ButtonBorderBrushPointerOver");
            var text = Brush(window, "TextBrush");
            foreach (var button in new[] { HeadlessUi.FindNamed<Button>(window, "DiscordButton")!, HeadlessUi.FindNamed<Button>(window, "SupportButton")!, setup, undo })
            {
                var name = button.Name ?? "Setup";
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                var glyph = button.GetVisualDescendants().OfType<GlyphIcon>().First();
                var restGlyph = Colour(glyph.Foreground);
                var neutral = restGlyph == Brush(window, "TextSecondaryBrush") || restGlyph == Brush(window, "TextMutedBrush");
                Color? ownColour = neutral ? null : restGlyph; // Discord blue, the blaze heart
                var restBounds = button.Bounds;
                var restBorder = presenter.BorderThickness;
                var surface = SurfaceBehind(button);

                window.MouseMove(Centre(button, window));
                HeadlessUi.Pump();
                Assert.True(button.IsPointerOver, name);
                var (fill, edge) = (Colour(presenter.Background), Colour(presenter.BorderBrush));
                Assert.Equal(stroke, fill);
                Assert.Equal(frame, edge);
                Assert.True(presenter.BorderThickness.Left >= 1 && presenter.BorderThickness.Top >= 1, $"{name}: {presenter.BorderThickness}");
                // The fill stands out from the surface it sits on and the frame from the fill (Stroke on Surface is
                // 1.41:1; the old Surface2 hover was 1.10:1 and a Surface3 fill only 1.26:1).
                Assert.True(Contrast(fill, surface) >= 1.3, $"{name}: fill {Contrast(fill, surface):0.00}:1 on {surface}");
                Assert.True(Contrast(edge, fill) >= 1.3, $"{name}: frame {Contrast(edge, fill):0.00}:1 on its fill");
                Assert.Equal(ownColour ?? text, Colour(glyph.Foreground));
                Assert.Equal(restBounds, button.Bounds);

                window.MouseMove(new Point(window.Bounds.Width / 2, window.Bounds.Height - 60)); // empty viewport area
                HeadlessUi.Pump();
                Assert.False(button.IsPointerOver, name);
                Assert.Equal(Colors.Transparent, Colour(presenter.BorderBrush));
                Assert.NotEqual(stroke, Colour(presenter.Background));
                Assert.Equal(restBorder, presenter.BorderThickness); // the frame is always there, only its colour changes
                Assert.Equal(restBounds, button.Bounds);
                Assert.Equal(restGlyph, Colour(glyph.Foreground));
            }
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>WCAG relative-luminance contrast of two opaque colours.</summary>
    internal static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    /// <summary>The colour of the nearest opaque background behind <paramref name="control"/>.</summary>
    private static Color SurfaceBehind(Control control)
    {
        foreach (var ancestor in control.GetVisualAncestors())
        {
            var background = ancestor switch
            {
                Border b => b.Background,
                Panel p => p.Background,
                Avalonia.Controls.Primitives.TemplatedControl t => t.Background,
                _ => null,
            };
            if (background is ISolidColorBrush { Color.A: 255 } solid)
            {
                return solid.Color;
            }
        }

        throw new InvalidOperationException("No opaque surface behind " + control);
    }

    private static Point Centre(Control control, Visual root) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;

    private static Color Brush(Window window, string key) =>
        Colour(window.FindResource(key) as IBrush);

    private static Color Colour(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Transparent;
}
