using System.Globalization;
using System.Text.RegularExpressions;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// A spot typed as coordinates (F20, defolt_0999: "Is there a way to find it using its coordinates?"): X Y and an optional
/// Z, in metres as the Entities list shows them ("3551.5, -5162.5, 74.1 m") or in the game's centimetres
/// ("X=355150 Y=-516250 Z=7410", as in the game's admin commands). A unit at the end ("m", "cm") decides; without one,
/// a number beyond 20 000 is centimetres (the island reaches about 7 500 m from its centre), anything else metres.
/// </summary>
/// <param name="X">UE X in centimetres.</param>
/// <param name="Y">UE Y in centimetres.</param>
/// <param name="Z">UE Z in centimetres, null when only X and Y were typed.</param>
/// <param name="InCentimetres">True when the numbers were read as centimetres.</param>
/// <param name="UnitGiven">True when the text named its unit.</param>
public readonly partial record struct MapCoordinates(float X, float Y, float? Z, bool InCentimetres, bool UnitGiven)
{
    /// <summary>Largest number read as metres when no unit is given.</summary>
    public const float MetresLimit = 20_000f;

    /// <summary>Reads <paramref name="text"/>; false when it is not two or three coordinates (a name, a path, one number).</summary>
    public static bool TryParse(string? text, out MapCoordinates coordinates)
    {
        coordinates = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var body = text.Trim();
        string? unit = null;
        if (UnitPattern().Match(body) is { Success: true } u)
        {
            unit = u.Groups[1].Value.ToLowerInvariant();
            body = body[..u.Index].TrimEnd();
        }

        float[] numbers;
        var labelled = LabelPattern().Matches(body);
        if (labelled.Count > 0)
        {
            // X=… Y=… (Z=…): each axis once, nothing else but separators.
            var axes = new Dictionary<char, float>();
            foreach (Match m in labelled)
            {
                if (!axes.TryAdd(char.ToUpperInvariant(m.Groups[1].Value[0]), Number(m.Groups[2].Value)))
                {
                    return false;
                }
            }

            if (LabelPattern().Replace(body, string.Empty).Any(c => !char.IsWhiteSpace(c) && c is not (',' or ';'))
                || !axes.TryGetValue('X', out var lx) || !axes.TryGetValue('Y', out var ly))
            {
                return false;
            }

            numbers = axes.TryGetValue('Z', out var lz) ? [lx, ly, lz] : [lx, ly];
        }
        else
        {
            // "3551,5 -5162,5": decimal commas when the numbers are apart by spaces and none has a point.
            if (!body.Contains('.') && body.Contains(' ') && !body.Contains(", ", StringComparison.Ordinal))
            {
                body = DecimalComma().Replace(body, ".");
            }

            if (!ListPattern().IsMatch(body))
            {
                return false;
            }

            numbers = [.. NumberPattern().Matches(body).Select(m => Number(m.Value))];
        }

        if (numbers.Length is < 2 or > 3 || numbers.Any(n => !float.IsFinite(n)))
        {
            return false;
        }

        var centimetres = unit is null ? MathF.Max(MathF.Abs(numbers[0]), MathF.Abs(numbers[1])) > MetresLimit : unit == "cm";
        var scale = centimetres ? 1f : 100f;
        coordinates = new MapCoordinates(numbers[0] * scale, numbers[1] * scale, numbers.Length == 3 ? numbers[2] * scale : null, centimetres, unit is not null);
        return true;

        static float Number(string s) => float.Parse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>"3551.5, -5162.5, 74.1 m" (as the Entities list shows a location; Z left out when not typed).</summary>
    public string MetresText => Z is { } z
        ? string.Create(CultureInfo.InvariantCulture, $"{X / 100f:0.0}, {Y / 100f:0.0}, {z / 100f:0.0} m")
        : string.Create(CultureInfo.InvariantCulture, $"{X / 100f:0.0}, {Y / 100f:0.0} m");

    /// <summary>"X=355150 Y=-516250 Z=7410" (the game's centimetres).</summary>
    public string CentimetresText => Z is { } z
        ? string.Create(CultureInfo.InvariantCulture, $"X={X:0} Y={Y:0} Z={z:0}")
        : string.Create(CultureInfo.InvariantCulture, $"X={X:0} Y={Y:0}");

    [GeneratedRegex(@"(?i)(?<=[\d\s])(cm|m)$")]
    private static partial Regex UnitPattern();

    [GeneratedRegex(@"(?i)\b([xyz])\s*[=:]?\s*([-+]?\d+(?:[.,]\d+)?)")]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"(?<=\d),(?=\d)")]
    private static partial Regex DecimalComma();

    [GeneratedRegex(@"^[-+]?\d+(?:\.\d+)?(?:\s*[,;\s]\s*[-+]?\d+(?:\.\d+)?){1,2}$")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"[-+]?\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
