using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ScumStudio.Core;

namespace ScumStudio.App.Services;

/// <summary>What a report is about.</summary>
public enum ReportCategory
{
    /// <summary>Something is broken.</summary>
    Bug,

    /// <summary>A wish.</summary>
    Idea,
}

/// <summary>The outcome of a report: sent, or why not.</summary>
/// <param name="Sent">True when the owner's Discord got it.</param>
/// <param name="Translated">True when the text went in English (translated), false when it went as written.</param>
/// <param name="Error">What went wrong, or null.</param>
public sealed record ReportResult(bool Sent, bool Translated, string? Error = null);

/// <summary>
/// "Report a problem" (owner: people send problems by Discord DM in any language; a form in the app that reaches the
/// owner's Discord in English, with the app's name and picture). The text is machine-translated to English, then posted
/// to the owner's Discord webhook as an embed with the original text and the app's version, language and system. The
/// webhook address is not in the source: it is embedded at build time (<c>report-webhook.txt</c>, written by CI from a
/// secret) or taken from <c>SCUMSTUDIO_REPORT_WEBHOOK</c>; without it, reporting is off and the card points to GitHub.
/// </summary>
public sealed class ReportService
{
    /// <summary>Environment variable that overrides the embedded webhook address (tests, the owner's own builds).</summary>
    public const string WebhookVariable = "SCUMSTUDIO_REPORT_WEBHOOK";

    /// <summary>Longest report text (Discord shows up to 4,096 characters in an embed; the original goes in a field).</summary>
    public const int MaxLength = 1_500;

    /// <summary>Where to send problems when the app cannot (no webhook in this build).</summary>
    public const string IssuesUrl = "https://github.com/ALLAWI32/SCUM-Modding-Studio/issues";

    /// <summary>The picture Discord shows next to a report: the app's icon in the public repository.</summary>
    public const string AvatarUrl = "https://raw.githubusercontent.com/ALLAWI32/SCUM-Modding-Studio/main/docs/images/app-icon.png";

    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger;
    private readonly Func<HttpMessageHandler> _handler;
    private readonly TimeProvider _clock;
    private DateTimeOffset? _lastSent;

    /// <summary>Creates the service over the embedded (or environment) webhook address.</summary>
    public ReportService(ILogger logger, Func<HttpMessageHandler>? handler = null, TimeProvider? clock = null, string? webhookUrl = null)
    {
        _logger = logger;
        _handler = handler ?? (() => new HttpClientHandler());
        _clock = clock ?? TimeProvider.System;
        WebhookUrl = webhookUrl is not null ? (webhookUrl.Length > 0 ? webhookUrl : null) // "" = none (tests)
            : Environment.GetEnvironmentVariable(WebhookVariable)?.Trim() is { Length: > 0 } fromEnvironment ? fromEnvironment : EmbeddedWebhook();
    }

    /// <summary>The webhook address, or null when this build has none.</summary>
    public string? WebhookUrl { get; }

    /// <summary>True when reports can be sent from this build.</summary>
    public bool IsAvailable => WebhookUrl is { Length: > 0 };

    /// <summary>
    /// Translates <paramref name="text"/> to English and posts it with <paramref name="details"/> (version, language,
    /// system, the last log lines) as a Discord embed. One report a minute: the address is in every copy of the app.
    /// </summary>
    public async Task<ReportResult> SendAsync(ReportCategory category, string text, IReadOnlyList<(string Name, string Value)> details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (WebhookUrl is not { } url)
        {
            return new ReportResult(false, false, "This build cannot send reports.");
        }

        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length < 10)
        {
            return new ReportResult(false, false, "The report is too short.");
        }

        if (trimmed.Length > MaxLength)
        {
            trimmed = trimmed[..MaxLength];
        }

        var now = _clock.GetUtcNow();
        if (_lastSent is { } last && now - last < MinimumGap)
        {
            return new ReportResult(false, false, $"Please wait {(int)(MinimumGap - (now - last)).TotalSeconds + 1} s before the next report.");
        }

        using var http = new HttpClient(_handler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SCUM-Modding-Studio/{Version}");
        var translation = await TranslateToEnglishAsync(http, trimmed, cancellationToken).ConfigureAwait(false);
        var payload = BuildPayload(category, trimmed, translation, details, now);
        try
        {
            using var response = await http.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Report not sent: Discord answered {Status}.", (int)response.StatusCode);
                return new ReportResult(false, translation is not null, $"Discord answered {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Report not sent: {Message}", ex.Message);
            return new ReportResult(false, translation is not null, ex.Message);
        }

        _lastSent = now;
        return new ReportResult(true, translation is not null);
    }

    /// <summary>
    /// The Discord webhook message: the app as the sender (name and picture), one embed with the English text, the
    /// original when it was translated, and the details as fields.
    /// </summary>
    public static JsonObject BuildPayload(ReportCategory category, string text, (string English, string Language)? translation, IReadOnlyList<(string Name, string Value)> details, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(details);
        var english = translation?.English ?? text;
        var firstLine = english.Split('\n', 2)[0].Trim();
        var title = $"{category}: {(firstLine.Length > 80 ? firstLine[..77] + "…" : firstLine)}";
        var fields = new JsonArray();
        if (translation is { } t && !string.Equals(t.English, text, StringComparison.Ordinal))
        {
            fields.Add(Field($"Original ({t.Language})", Clip(text, 1_000)));
        }

        foreach (var (name, value) in details.Where(d => d.Value.Length > 0))
        {
            fields.Add(Field(name, Clip(value, 1_000), inline: !value.Contains('\n')));
        }

        return new JsonObject
        {
            ["username"] = CoreInfo.ProductName,
            ["avatar_url"] = AvatarUrl,
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = title,
                ["description"] = Clip(english, 4_000),
                ["color"] = category switch { ReportCategory.Bug => 0xE5533D, ReportCategory.Idea => 0x3DA5E5, _ => 0x9A9A9A },
                ["fields"] = fields,
                ["footer"] = new JsonObject { ["text"] = $"{CoreInfo.ProductName} {Version}" },
                ["timestamp"] = at.ToString("o", CultureInfo.InvariantCulture),
            }),
        };

        static JsonObject Field(string name, string value, bool inline = false) =>
            new() { ["name"] = name, ["value"] = value, ["inline"] = inline };
    }

    /// <summary>
    /// The text in English with the language it was written in, or null when the translation service could not be reached
    /// (the report then goes as written). Google's public translate endpoint, no key, language detected.
    /// </summary>
    private async Task<(string English, string Language)?> TranslateToEnglishAsync(HttpClient http, string text, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q=" + Uri.EscapeDataString(text);
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return ParseTranslation(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation("Report goes untranslated: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>The English text and source language of a <c>translate_a/single</c> answer (<c>[[["Hello","Bonjour",…],…],null,"fr",…]</c>).</summary>
    public static (string English, string Language)? ParseTranslation(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1 || root[0].ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var english = string.Concat(root[0].EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0 && part[0].ValueKind == JsonValueKind.String)
            .Select(part => part[0].GetString()));
        var language = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString() ?? "?" : "?";
        return english.Length == 0 ? null : (english, language);
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string Version => CoreInfo.Version.Split('+')[0];

    private static string? EmbeddedWebhook()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("report-webhook.txt");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        var url = reader.ReadToEnd().Trim();
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
    }
}
