using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;

namespace ScumStudio.Tests.App;

/// <summary>
/// Static checks over every <c>.axaml</c> of the app, for the two Avalonia traps the build does not catch (or reports
/// in a confusing way), plus a runtime pass that applies every style of <c>Styles/Controls.axaml</c> to a control in a
/// headless window:
/// <list type="bullet">
/// <item>two <c>Setter</c>s for the same property in one <c>Style</c> compile, then throw "Duplicate setter
/// encountered" the moment a matching control is styled;</item>
/// <item>a selector list that mixes control families (<c>TextBlock.mono, TextBox.mono</c>) resolves the property on
/// the common base type and fails with AVLN2000 - or, worse, silently styles the wrong thing.</item>
/// </list>
/// </summary>
public sealed partial class StyleLintTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static IEnumerable<object[]> AxamlFiles() =>
        Directory.EnumerateFiles(AppSourceDirectory, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(f => new object[] { Path.GetRelativePath(AppSourceDirectory, f) });

    [Fact]
    public void EveryAppXamlFileIsChecked()
    {
        var files = AxamlFiles().Select(f => (string)f[0]).ToList();
        Assert.Contains(files, f => f.EndsWith("Controls.axaml", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("MainWindow.axaml", StringComparison.Ordinal));
        Assert.True(files.Count >= 10, string.Join(", ", files));
    }

    [Theory]
    [MemberData(nameof(AxamlFiles))]
    public void NoStyleSetsTheSamePropertyTwice(string file)
    {
        var problems = new List<string>();
        foreach (var style in Load(file).Descendants().Where(e => e.Name.LocalName is "Style" or "ControlTheme"))
        {
            var duplicates = style.Elements()
                .Where(e => e.Name.LocalName == "Setter")
                .Select(e => (string?)e.Attribute("Property"))
                .Where(p => p is not null)
                .GroupBy(p => p, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            problems.AddRange(duplicates.Select(p => $"{file}: '{SelectorOf(style)}' sets {p} more than once"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(AxamlFiles))]
    public void NoSelectorMixesControlFamilies(string file)
    {
        var problems = new List<string>();
        foreach (var style in Load(file).Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var selector = SelectorOf(style);
            var families = SplitAlternatives(selector)
                .Select(TargetType)
                .Where(t => t is not null)
                .Select(t => FamilyOf(t!))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (families.Count > 1)
            {
                problems.Add($"{file}: '{selector}' mixes {string.Join(" + ", families)}; split it into one Style per family");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(AxamlFiles))]
    public void ViewsUseTokensInsteadOfLiteralColours(string file)
    {
        if (!file.StartsWith("Views", StringComparison.Ordinal))
        {
            return; // App.axaml defines the tokens; Styles may use a shadow colour.
        }

        var literals = Load(file).Descendants()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName is "Background" or "Foreground" or "BorderBrush" or "Fill" or "Stroke" or "Color")
            .Where(a => a.Value.StartsWith('#') || a.Value is "White" or "Black")
            .Select(a => $"{file}: {a.Parent!.Name.LocalName}.{a.Name.LocalName}=\"{a.Value}\"")
            .ToList();
        Assert.True(literals.Count == 0, "Use a DynamicResource token from App.axaml:" + Environment.NewLine + string.Join(Environment.NewLine, literals));
    }

    [Fact]
    public void ResourcesUsedByTheViewsExist()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "App.axaml", Path.Combine("Styles", "Icons.axaml") })
        {
            foreach (var key in Load(file).Descendants().Select(e => (string?)e.Attribute(Xaml + "Key")).Where(k => k is not null))
            {
                defined.Add(key!);
            }
        }

        var missing = new List<string>();
        foreach (var file in AxamlFiles().Select(f => (string)f[0]))
        {
            var text = File.ReadAllText(Path.Combine(AppSourceDirectory, file));
            foreach (Match match in ResourceReference().Matches(text))
            {
                var key = match.Groups[1].Value;
                if (!defined.Contains(key))
                {
                    missing.Add($"{file}: {key}");
                }
            }

            foreach (Match match in IconReference().Matches(text))
            {
                if (!defined.Contains(match.Groups[1].Value))
                {
                    missing.Add($"{file}: {match.Groups[1].Value}");
                }
            }
        }

        // Icon keys handed out by view models (IconKey properties).
        foreach (var file in Directory.EnumerateFiles(AppSourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in IconString().Matches(File.ReadAllText(file)))
            {
                if (!defined.Contains(match.Groups[1].Value))
                {
                    missing.Add($"{Path.GetRelativePath(AppSourceDirectory, file)}: {match.Groups[1].Value}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Undefined resources:" + Environment.NewLine + string.Join(Environment.NewLine, missing.Distinct()));
    }

    /// <summary>Applies every style of Controls.axaml to a matching control; a duplicate setter would throw here.</summary>
    [AvaloniaFact]
    public void EveryControlsStyleAppliesAtRuntime()
    {
        var root = new StackPanel();
        var built = 0;
        foreach (var style in Load(Path.Combine("Styles", "Controls.axaml")).Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            foreach (var alternative in SplitAlternatives(SelectorOf(style)))
            {
                if (Build(alternative) is { } sample)
                {
                    root.Children.Add(sample);
                    built++;
                }
            }
        }

        Assert.True(built > 60, $"only {built} samples were built");
        var window = new Window { Width = 800, Height = 600, Content = new ScrollViewer { Content = root } };
        try
        {
            window.Show();
            HeadlessUi.Pump();
            Assert.True(root.IsMeasureValid);
        }
        finally
        {
            window.Close();
        }
    }

    private static string AppSourceDirectory { get; } = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "..", "src", "ScumStudio.App"));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private static XDocument Load(string relativePath) => XDocument.Load(Path.Combine(AppSourceDirectory, relativePath));

    private static string SelectorOf(XElement style) => (string?)style.Attribute("Selector") ?? (string?)style.Attribute("TargetType") ?? string.Empty;

    /// <summary>Splits a selector list at top-level commas (not inside <c>:not(...)</c> or <c>:is(...)</c>).</summary>
    private static IEnumerable<string> SplitAlternatives(string selector)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < selector.Length; i++)
        {
            switch (selector[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return selector[start..i].Trim();
                    start = i + 1;
                    break;
            }
        }

        yield return selector[start..].Trim();
    }

    /// <summary>The control type a selector alternative styles (its last segment), or null for <c>^</c>-only selectors.</summary>
    private static string? TargetType(string alternative)
    {
        var last = Segments(alternative).LastOrDefault();
        return last is null ? null : SegmentType().Match(last) is { Success: true } m ? m.Groups[1].Value : null;
    }

    private static IEnumerable<string> Segments(string alternative) =>
        alternative.Replace("/template/", " ", StringComparison.Ordinal).Replace(">", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string FamilyOf(string type) => type switch
    {
        "TextBlock" or "SelectableTextBlock" => "text blocks",
        "Border" => "borders",
        "Ellipse" or "Rectangle" or "Path" or "Line" => "shapes",
        "ContentPresenter" => "presenters",
        "Panel" or "StackPanel" or "Grid" or "DockPanel" or "WrapPanel" => "panels",
        _ when type.Contains('|', StringComparison.Ordinal) => "custom controls (" + type + ")",
        _ => "templated controls",
    };

    /// <summary>Builds the control tree an alternative describes (outermost first), skipping template parts.</summary>
    private static Control? Build(string alternative)
    {
        var templateIndex = alternative.IndexOf("/template/", StringComparison.Ordinal);
        var path = templateIndex >= 0 ? alternative[..templateIndex] : alternative;
        Control? outer = null;
        Control? current = null;
        foreach (var segment in Segments(path))
        {
            var match = SegmentParts().Match(segment);
            if (!match.Success || CreateControl(match.Groups[1].Value) is not { } control)
            {
                return null;
            }

            foreach (Capture cls in match.Groups[2].Captures)
            {
                control.Classes.Add(cls.Value);
            }

            foreach (Capture pseudo in match.Groups[3].Captures)
            {
                ((IPseudoClasses)control.Classes).Set(":" + pseudo.Value, true);
            }

            if (current is null)
            {
                outer = control;
            }
            else if (!Nest(current, control))
            {
                return null;
            }

            current = control;
        }

        return outer;
    }

    private static Control? CreateControl(string typeName)
    {
        var name = typeName.Contains('|', StringComparison.Ordinal) ? typeName[(typeName.IndexOf('|', StringComparison.Ordinal) + 1)..] : typeName;
        var type = new[] { typeof(Button).Assembly, typeof(ScumStudio.App.Controls.GlyphIcon).Assembly, typeof(Avalonia.Controls.Shapes.Ellipse).Assembly }
            .SelectMany(a => a.GetExportedTypes())
            .FirstOrDefault(t => t.Name == name && typeof(Control).IsAssignableFrom(t) && !typeof(TopLevel).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null);
        return type is null ? null : (Control?)Activator.CreateInstance(type);
    }

    private static bool Nest(Control parent, Control child)
    {
        switch (parent)
        {
            case ItemsControl items when child is ContentControl or ListBoxItem or TreeViewItem:
                items.Items.Add(child);
                return true;
            case ItemsControl items:
                items.Items.Add(new ContentControl { Content = child });
                return true;
            case Decorator decorator:
                decorator.Child = child;
                return true;
            case ContentControl content:
                content.Content = child;
                return true;
            case Panel panel:
                panel.Children.Add(child);
                return true;
            case ContentPresenter presenter:
                presenter.Content = child;
                return true;
            default:
                return false;
        }
    }

    [GeneratedRegex(@"\{(?:DynamicResource|StaticResource)\s+([A-Za-z0-9_.]+)\}")]
    private static partial Regex ResourceReference();

    [GeneratedRegex(@"Kind=""(Icon\.[A-Za-z0-9]+)""")]
    private static partial Regex IconReference();

    [GeneratedRegex(@"""(Icon\.[A-Za-z0-9]+)""")]
    private static partial Regex IconString();

    [GeneratedRegex(@"^\^?((?:[A-Za-z_]\w*\|)?[A-Za-z_]\w*)")]
    private static partial Regex SegmentType();

    [GeneratedRegex(@"^((?:[A-Za-z_]\w*\|)?[A-Za-z_]\w*)(?:\.([\w-]+))*(?::([\w-]+))*(?:#\w+)?$")]
    private static partial Regex SegmentParts();
}
