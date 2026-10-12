using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views;
using ScumStudio.App.Views.Pages;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-12): every option in its place, nothing covers or pushes another control when the window grows or
/// shrinks. The title strip and the pages outside the Map and Craftables (plus Setup and the dump window) are laid out at
/// the common screen sizes, in English and in Russian (the longest labels): no two controls overlap, none runs past its
/// window or panel, the search field ends before the Discord button. Screenshots with <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ShellLayoutTests
{
    private static readonly (int Width, int Height)[] Sizes = [(1280, 720), (1600, 900), (1920, 1080), (2560, 1440)];

    public static IEnumerable<object[]> PagesAndSizes() =>
        from page in new[] { "projects", "assets", "economy", "spawns", "vehicles", "weapons", "setup", "dump" }
        from size in Sizes
        select new object[] { page, size.Width, size.Height };

    [AvaloniaTheory]
    [MemberData(nameof(PagesAndSizes))]
    public void NothingOverlapsAtCommonScreenSizes(string page, int width, int height)
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, width, height);
        Window? dump = null;
        try
        {
            foreach (var language in new[] { "en", "ru" })
            {
                Loc.Instance.Language = language;
                Visual scope = window;
                Window root = window;
                switch (page)
                {
                    case "setup":
                        vm.OpenSetup();
                        HeadlessUi.Pump();
                        scope = HeadlessUi.FindNamed<Panel>(window, "SetupOverlay")!;
                        break;
                    case "dump":
                        dump ??= new DumpWindow { DataContext = new DumpViewModel(ctx.Services), Width = width, Height = height };
                        dump.Show();
                        HeadlessUi.Pump();
                        scope = root = dump;
                        break;
                    default:
                        var pageVm = vm.NavigateTo(page)!;
                        HeadlessUi.Pump();
                        if (pageVm is ModulePageViewModel module)
                        {
                            ShowValues(module, window);
                        }
                        else
                        {
                            ShowLoadedState(window);
                        }

                        break;
                }

                HeadlessUi.SaveScreenshot(root, $"layout-{page}-{width}x{height}-{language}");
                var problems = Problems(root, scope);
                problems.AddRange(MisalignedToolbars(window));
                var search = HeadlessUi.FindNamed<TextBox>(window, "GlobalSearch")!;
                var discord = HeadlessUi.FindNamed<Button>(window, "DiscordButton")!;
                if (Rect(search, window).Right > Rect(discord, window).Left)
                {
                    problems.Add($"the search ends at {Rect(search, window).Right:0}, after the Discord button starts at {Rect(discord, window).Left:0}");
                }

                Assert.True(problems.Count == 0, $"{page} {width}x{height} {language}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
            }
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
            dump?.Close();
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>F257: at the smallest window (and at 1280) the value names keep room and the item name stays on screen.</summary>
    [AvaloniaFact]
    public void ModuleValuesKeepTheirNamesWhenNarrow()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1280, 720);
        try
        {
            var page = (ModulePageViewModel)vm.NavigateTo("weapons")!;
            HeadlessUi.Pump();
            ShowValues(page, window);
            foreach (var (width, height) in new (double Width, double Height)[] { (1280, 720), (window.MinWidth, window.MinHeight) })
            {
                window.Width = width;
                window.Height = height;
                HeadlessUi.Pump();
                HeadlessUi.SaveScreenshot(window, $"module-values-{width:0}");
                var view = HeadlessUi.Find<ModulePageView>(window).Single(v => v.IsEffectivelyVisible);
                foreach (var row in page.Groups.SelectMany(g => g.Rows))
                {
                    var name = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == row.Label && t.DataContext == row);
                    var stock = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == row.StockDisplay && t.DataContext == row);
                    var shown = Rect(stock, window).Left - Rect(name, window).Left; // room before the stock value begins
                    Assert.True(shown > 40, $"{width}: '{row.Label}' has {shown:0} px before the stock value");
                    Assert.True(Rect(name, window).Right <= Rect(stock, window).Left + 0.5, $"{width}: '{row.Label}' runs under the stock value");
                }

                var title = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == page.SelectedTitle && t.Classes.Contains("h2"));
                Assert.True(title.IsEffectivelyVisible && title.Bounds.Width > 40, $"{width}: the item name is {title.Bounds.Width:0} px wide");
                Assert.True(Problems(window, view).Count == 0, $"{width}: {string.Join(Environment.NewLine, Problems(window, view))}");
            }
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>
    /// The pages as an open catalog shows them, without game files: the toolbars and bodies made visible by hand and the
    /// "connect the game files" cards hidden.
    /// </summary>
    internal static void ShowLoadedState(Window window)
    {
        foreach (var part in window.GetVisualDescendants().OfType<Control>().ToList())
        {
            if (part.Name is "ModuleToolbar" or "ModuleBody" or "AssetsToolbar" or "AssetsBody" or "EconomyBody")
            {
                part.IsVisible = true;
            }
            else if (part.Classes.Contains("emptyState"))
            {
                part.IsVisible = false;
            }
        }

        HeadlessUi.Pump();
    }

    /// <summary>Shows the values editor without game files: a weapon with a few values in the loaded page.</summary>
    internal static void ShowValues(ModulePageViewModel page, Window window)
    {
        const string Package = "/Game/ConZ_Files/Items/Weapons/Weapon_AK47_Tactical_Long_Barrel";
        page.SelectedItem = new ModuleItemViewModel(new ModdableAsset(ModdableKind.Weapon, Package, "Weapons"));
        TunableRowViewModel Row(string name, TunableKind kind, string value) =>
            new(Package, new Tunable("Default__Weapon_AK47_C", name, kind, value) { Name = name }, value);
        page.Groups =
        [
            new TunableGroupViewModel("Damage", [Row("DamagePerShotAtLongRangeMultiplier", TunableKind.Float, "45.5"), Row("MaxRange", TunableKind.Float, "80000")]),
            new TunableGroupViewModel("Handling", [Row("CanBeSuppressedWhileAiming", TunableKind.Bool, "True")]),
        ];
        HeadlessUi.Pump();
        ShowLoadedState(window);
    }

    /// <summary>Overlapping or cut-off interactive controls under <paramref name="scope"/>.</summary>
    internal static List<string> Problems(Window root, Visual scope)
    {
        var controls = scope.GetVisualDescendants().OfType<Control>().Where(IsInteractive)
            .Select(c => (Control: c, Rect: Rect(c, root), Visible: VisibleRect(c, root)))
            .Where(c => c.Visible.Width > 1 && c.Visible.Height > 1)
            .ToList();
        var problems = new List<string>();
        for (var i = 0; i < controls.Count; i++)
        {
            var (control, rect, visible) = controls[i];
            if (rect.Right > root.Bounds.Width + 0.5)
            {
                problems.Add($"{Describe(control)} ends at {rect.Right:0}, past the window ({root.Bounds.Width:0})");
            }
            else if (ClipEdge(control, root) is { } edge && rect.Right > edge + 0.5)
            {
                problems.Add($"{Describe(control)} ends at {rect.Right:0}, past its panel ({edge:0})");
            }

            for (var j = i + 1; j < controls.Count; j++)
            {
                var other = controls[j];
                if (control.IsVisualAncestorOf(other.Control) || other.Control.IsVisualAncestorOf(control))
                {
                    continue;
                }

                var overlap = visible.Intersect(other.Visible);
                if (overlap.Width > 1 && overlap.Height > 1)
                {
                    problems.Add($"{Describe(control)} {visible} overlaps {Describe(other.Control)} {other.Visible}");
                }
            }
        }

        return problems;
    }

    /// <summary>A page's filter box is exactly as wide as the list or tree under it (Assets, Vehicles, Weapons).</summary>
    private static IEnumerable<string> MisalignedToolbars(Window window)
    {
        foreach (var (toolbar, body) in new[] { ("AssetsToolbar", "AssetsBody"), ("ModuleToolbar", "ModuleBody") })
        {
            foreach (var bar in HeadlessUi.Find<Grid>(window).Where(g => g.Name == toolbar && g.IsEffectivelyVisible))
            {
                var under = bar.GetVisualParent()!.GetVisualDescendants().OfType<Grid>().First(g => g.Name == body);
                var (top, below) = (bar.ColumnDefinitions[0].ActualWidth, under.ColumnDefinitions[0].ActualWidth);
                if (Math.Abs(top - below) > 0.5)
                {
                    yield return $"{toolbar}: the filter is {top:0} px wide, the list under it {below:0} px";
                }
            }
        }
    }

    internal static bool IsInteractive(Control c) =>
        c is Button or ComboBox or TextBox or Slider or NumericUpDown or AutoCompleteBox or ListBoxItem
        && c.TemplatedParent is null // template parts (scroll bar arrows, a text box's inner buttons)
        && c.IsEffectivelyVisible;

    /// <summary>Bounds in <paramref name="root"/> coordinates.</summary>
    private static Rect Rect(Control c, Visual root) =>
        c.TranslatePoint(default, root) is { } p ? new Rect(p, c.Bounds.Size) : default;

    /// <summary>The part of the control that is not clipped away by a scroll viewer or a clipping panel.</summary>
    private static Rect VisibleRect(Control c, Visual root)
    {
        var rect = Rect(c, root);
        foreach (var clip in c.GetVisualAncestors().OfType<Control>().Where(a => a.ClipToBounds))
        {
            rect = rect.Intersect(Rect(clip, root));
        }

        return rect;
    }

    /// <summary>The right edge of the nearest clipping ancestor, unless that is a viewer that scrolls sideways.</summary>
    private static double? ClipEdge(Control c, Visual root)
    {
        var clip = c.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.ClipToBounds);
        if (clip is null || (clip is ScrollContentPresenter && clip.TemplatedParent is ScrollViewer { HorizontalScrollBarVisibility: not Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }))
        {
            return null;
        }

        return Rect(clip, root).Right;
    }

    private static string Describe(Control c)
    {
        var text = c switch
        {
            ContentControl { Content: string s } => s,
            TextBox t => t.Watermark ?? t.Name,
            _ => c.Name ?? ToolTip.GetTip(c)?.ToString(),
        };
        return $"{c.GetType().Name}({text})";
    }
}
