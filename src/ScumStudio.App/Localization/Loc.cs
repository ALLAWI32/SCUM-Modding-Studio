using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

// {Tr Key} works in every view without an xmlns line.
[assembly: Avalonia.Metadata.XmlnsDefinition("https://github.com/avaloniaui", "ScumStudio.App.Localization")]

namespace ScumStudio.App.Localization;

/// <summary>A user-interface language: its code and its name written in that language (for the picker).</summary>
/// <param name="Code">Code used in settings and in the table name (<c>en</c>, <c>ar</c>, <c>ru</c>, <c>de</c>, <c>es</c>, <c>tr</c>, <c>sh</c> = Bosnian/Croatian/Serbian in Latin script, <c>zh</c> = Simplified Chinese).</param>
/// <param name="NativeName">Name of the language in the language itself.</param>
public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// The user-interface strings. Tables are the embedded <c>Localization/Strings/&lt;code&gt;.json</c> files, flat
/// <c>"Dotted.Key": "text"</c> maps; English is the default and the fallback for any key a language lacks. Setting
/// <see cref="Language"/> raises <c>Item[]</c>, so every XAML binding to the indexer (<c>{Tr Key}</c>) switches at once,
/// and <see cref="LanguageChanged"/>, on which view models rebuild the texts they compose in code.
/// </summary>
/// <remarks>Only texts change: the layout stays left-to-right and number formatting stays on the OS culture.</remarks>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Default and fallback language.</summary>
    public const string DefaultLanguage = "en";

    /// <summary>The languages of the picker, English first.</summary>
    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("en", "English"),
        new("ar", "العربية"),
        new("ru", "Русский"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("tr", "Türkçe"),
        new("sh", "Bosanski / Hrvatski / Srpski"),
        new("zh", "简体中文"),
    ];

    // Static initialisers run in text order: the tables need Languages, the instance needs the tables.
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables =
        Languages.ToDictionary(l => l.Code, l => LoadTable(l.Code), StringComparer.Ordinal);

    private IReadOnlyDictionary<string, string> _current = Tables[DefaultLanguage];
    private string _language = DefaultLanguage;

    /// <summary>The app-wide instance the views bind to.</summary>
    public static Loc Instance { get; } = new();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after <see cref="Language"/> changed.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>Current language code; unknown or empty codes select English.</summary>
    public string Language
    {
        get => _language;
        set
        {
            var code = value is not null && Tables.ContainsKey(value) ? value : DefaultLanguage;
            if (code == _language)
            {
                return;
            }

            _language = code;
            _current = Tables[code];
            // "Item[]" is the WPF convention for "every indexer value changed"; Avalonia's binding nodes listen for "Item".
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The text of <paramref name="key"/> in the current language, else in English, else the key itself.</summary>
    public string this[string key] => Or(key, key);

    /// <summary>The text of <paramref name="key"/> in the current language, else in English, else <paramref name="fallback"/>.</summary>
    public string Or(string key, string fallback) =>
        _current.TryGetValue(key, out var text) || Tables[DefaultLanguage].TryGetValue(key, out text) ? text : fallback;

    /// <summary>Shorthand for <c>Loc.Instance[key]</c>.</summary>
    public static string T(string key) => Instance[key];

    /// <summary><see cref="T"/> used as a composite format (<c>{0}</c>, <c>{1:N0}</c> …) with the OS culture.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Instance[key], args);

    /// <summary>The raw table of a language (tests compare the tables).</summary>
    public static IReadOnlyDictionary<string, string> Table(string code) => Tables[code];

    private static IReadOnlyDictionary<string, string> LoadTable(string code)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Strings.{code}.json")
                           ?? throw new InvalidOperationException($"The string table Strings.{code}.json is not embedded.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}

/// <summary>
/// <c>{Tr Some.Key}</c>: a one-way binding to <see cref="Loc.Instance"/>'s indexer, so the text follows the language
/// picker without a restart. Usable on any property that takes text (Text, Content, Header, Watermark, ToolTip.Tip …).
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    /// <summary>Creates the extension for <paramref name="key"/>.</summary>
    public TrExtension(string key) => Key = key;

    /// <summary>String key, e.g. <c>Common.Browse</c>.</summary>
    public string Key { get; set; }

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
