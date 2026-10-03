using System.Net;
using System.Text.RegularExpressions;
using ScumStudio.Core.Security;

namespace ScumStudio.App.Services;

/// <summary>
/// Finds SCUM's pak key on a public list of Unreal Engine game keys: the page is read, its text searched for the game's
/// name and the 256-bit hex key written next to it (owner: "the tool goes to the site, searches SCUM and copies the key
/// beside it; when it cannot find it, it asks the person"). The key goes straight to the caller, which tests it on the
/// game's paks before storing it; it is never logged or shown.
/// </summary>
public static partial class OnlineKeyFinder
{
    /// <summary>The list read: a forum post on gamestranslator.it that collects the AES keys of UE4/UE5 games.</summary>
    public const string ListUrl = "https://www.gamestranslator.it/index.php?/forums/topic/1485-raccolta-di-chiavi-di-crittografia-aes-per-giochi-ue45/";

    /// <summary>Reads <see cref="ListUrl"/> and returns SCUM's key (normalised, <c>0x</c> + 64 hex), or null when the page has none.</summary>
    /// <exception cref="HttpRequestException">The page could not be read.</exception>
    public static async Task<string?> FindAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SCUM-Modding-Studio");
        var html = await http.GetStringAsync(new Uri(ListUrl), cancellationToken).ConfigureAwait(false);
        return Parse(html);
    }

    /// <summary>
    /// The key written right after <paramref name="game"/> (whole word, case as given) in <paramref name="html"/>, tags
    /// and entities removed first: "SCUM 0x0123…" or "SCUM: 0123…". Null when there is none.
    /// </summary>
    public static string? Parse(string html, string game = "SCUM")
    {
        ArgumentNullException.ThrowIfNull(html);
        var text = WebUtility.HtmlDecode(Tags().Replace(html, " "));
        var match = Regex.Match(text, @"(?<![\w-])" + Regex.Escape(game) + @"(?![\w-])[\s:=\-–—]*(?:0x)?([0-9A-Fa-f]{64})(?![0-9A-Fa-f])",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        return match.Success && AesKeyHex.TryNormalize(match.Groups[1].Value, out var key) ? key : null;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
