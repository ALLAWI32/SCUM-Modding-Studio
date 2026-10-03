using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;

namespace ScumStudio.Tests.App;

/// <summary>Switches the app-wide <see cref="Loc.Instance"/>, so these tests never run next to tests that read English texts.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection
{
    public const string Name = "UI language";
}

/// <summary>The string tables (en, ar, ru, de) and the live language switch.</summary>
[Collection(LanguageCollection.Name)]
public sealed partial class LocalizationTests
{
    public static IEnumerable<object[]> Translations() => Loc.Languages.Skip(1).Select(l => new object[] { l.Code });

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryEnglishKeyIsTranslatedWithTheSamePlaceholders(string code)
    {
        var english = Loc.Table("en");
        var table = Loc.Table(code);
        Assert.True(english.Count > 300, $"only {english.Count} English keys");
        Assert.Empty(english.Keys.Except(table.Keys)); // a text that stays English is listed with its English wording
        Assert.Empty(table.Keys.Except(english.Keys));
        var problems = english
            .Where(e => !Placeholders(e.Value).SequenceEqual(Placeholders(table[e.Key])))
            .Select(e => $"{e.Key}: '{e.Value}' vs '{table[e.Key]}'")
            .ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryKeyTheAppUsesExistsInEnglish()
    {
        var english = Loc.Table("en");
        var source = Path.GetFullPath(Path.Combine(ThisDirectory(), "..", "..", "..", "src", "ScumStudio.App"));
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            var keys = file.EndsWith(".axaml", StringComparison.Ordinal) ? XamlKey().Matches(text) : CodeKey().Matches(text);
            foreach (Match match in keys)
            {
                if (!english.ContainsKey(match.Groups[1].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Keys missing from en.json:" + Environment.NewLine + string.Join(Environment.NewLine, missing.Distinct()));
    }

    [Fact]
    public void EveryInteractiveControlHasAHoverDescription()
    {
        string[] interactive = ["Button", "ToggleButton", "CheckBox", "ComboBox", "TextBox", "Slider", "MenuItem", "TabItem", "ToggleSwitch", "RadioButton"];
        var views = Path.GetFullPath(Path.Combine(ThisDirectory(), "..", "..", "..", "src", "ScumStudio.App", "Views"));
        var missing = Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => System.Xml.Linq.XDocument.Load(file).Descendants()
                .Where(e => interactive.Contains(e.Name.LocalName))
                .Where(e => e.Attribute("ToolTip.Tip") is null && !e.Elements().Any(c => c.Name.LocalName == "ToolTip.Tip"))
                .Select(e => $"{Path.GetFileName(file)}: <{e.Name.LocalName} {string.Join(' ', e.Attributes().Take(3))}>"))
            .ToList();
        Assert.True(missing.Count == 0, "Controls without ToolTip.Tip:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void UnknownLanguagesAndKeysFallBack()
    {
        var loc = Loc.Instance;
        Assert.Equal("en", loc.Language);
        Assert.Equal("Map", loc["Nav.map"]);
        Assert.Equal("No.Such.Key", loc["No.Such.Key"]);
        Assert.Equal("fallback", loc.Or("No.Such.Key", "fallback"));
    }

    [AvaloniaFact]
    public void PickingALanguageSwitchesTheOpenWindowAndIsSaved()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            // Hover descriptions wait 1.5 s (global style).
            var setupButton = HeadlessUi.Find<Button>(window).First(b => ToolTip.GetTip(b) is not null);
            Assert.Equal(1500, ToolTip.GetShowDelay(setupButton));
            Assert.Contains("Map", Texts(window));
            Assert.Contains("Console", Texts(window));

            var settings = Assert.IsType<SettingsPageViewModel>(vm.NavigateTo("settings"));
            HeadlessUi.Pump();
            settings.SelectedLanguage = Loc.Languages.Single(l => l.Code == "de");
            HeadlessUi.Pump();

            Assert.Equal("de", Loc.Instance.Language);
            Assert.Equal("de", ctx.Services.Settings.Load().Ui.Language);
            var texts = Texts(window);
            Assert.Contains("Karte", texts);          // workspace tab (view model)
            Assert.Contains("Einstellungen", texts);  // page title (view model)
            Assert.Contains("Konsole", texts);        // status bar ({Tr} binding)
            Assert.Contains("Sprache", texts);        // the picker's own label
            Assert.Equal("nicht festgelegt", vm.GamePill.Value);
            HeadlessUi.SaveScreenshot(window, "page-settings-de");

            settings.SelectedLanguage = Loc.Languages.Single(l => l.Code == "ar");
            HeadlessUi.Pump();
            Assert.Contains("الخريطة", Texts(window));
            Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, window.FlowDirection);
            HeadlessUi.SaveScreenshot(window, "page-settings-ar");

            settings.SelectedLanguage = Loc.Languages.Single(l => l.Code == "ru");
            HeadlessUi.Pump();
            Assert.Contains("Карта", Texts(window));

            // Every page opens in every language (with SCUMSTUDIO_SCREENSHOTS set, one PNG per page and language).
            foreach (var language in Loc.Languages.Skip(1))
            {
                Loc.Instance.Language = language.Code;
                foreach (var key in MainWindowViewModel.PageKeys)
                {
                    var page = vm.NavigateTo(key)!;
                    HeadlessUi.Pump();
                    Assert.Contains(HeadlessUi.Find<TextBlock>(window), t => t.Text == page.Title);
                    HeadlessUi.SaveScreenshot(window, $"page-{key}-{language.Code}");
                }
            }

            vm.NavigateTo("settings");
            settings.SelectedLanguage = Loc.Languages[0];
            HeadlessUi.Pump();
            Assert.Contains("Map", Texts(window));
            Assert.Equal("en", ctx.Services.Settings.Load().Ui.Language);
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void ArabicTextReadsRightToLeftInsideTheLeftToRightLayout()
    {
        var arabic = new TextBlock { Text = Loc.Table("ar")["Settings.KeyStored"] }; // "مفتاح AES محفوظ."
        var english = new TextBlock { Text = "AES key" };
        var window = new Window { Content = new StackPanel { Children = { arabic, english } } };
        try
        {
            window.Show();
            HeadlessUi.Pump();
            Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, arabic.FlowDirection);
            Assert.Equal(Avalonia.Media.TextAlignment.Left, arabic.TextAlignment);
            Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, english.FlowDirection);
            Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, window.FlowDirection);

            arabic.Text = "Weapon_AK47";
            Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, arabic.FlowDirection);
        }
        finally
        {
            window.Close();
        }
    }

    private static HashSet<string?> Texts(Window window) => HeadlessUi.Find<TextBlock>(window).Select(t => t.Text).ToHashSet();

    private static IEnumerable<string> Placeholders(string text) => Placeholder().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal);

    private static string ThisDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{Tr ([A-Za-z0-9_.]+)\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"Loc\.[TF]\(""([^""]+)""")]
    private static partial Regex CodeKey();
}
