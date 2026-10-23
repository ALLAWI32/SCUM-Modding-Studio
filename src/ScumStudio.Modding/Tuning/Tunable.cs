using System.Globalization;

namespace ScumStudio.Modding.Tuning;

/// <summary>Value type of a <see cref="Tunable"/>.</summary>
public enum TunableKind
{
    /// <summary>FloatProperty.</summary>
    Float,

    /// <summary>DoubleProperty.</summary>
    Double,

    /// <summary>IntProperty / Int8 / Int16 / Int64.</summary>
    Int,

    /// <summary>UInt16 / UInt32 / UInt64 / numeric ByteProperty.</summary>
    UInt,

    /// <summary>BoolProperty.</summary>
    Bool,

    /// <summary>EnumProperty or enum ByteProperty (an FName from the package's name table).</summary>
    Enum,

    /// <summary>Top-level TextProperty (written back as a culture-invariant text; the export is resized).</summary>
    Text,

    /// <summary>Native Vector struct: "x, y, z".</summary>
    Vector,

    /// <summary>Native Rotator struct: "pitch, yaw, roll".</summary>
    Rotator,

    /// <summary>Native LinearColor struct: "r, g, b, a".</summary>
    Color,

    /// <summary>
    /// The part a vehicle spawn preset puts in one slot (<see cref="VehicleParts"/>): the attachment class path
    /// (<c>/Game/…/BPC_X.BPC_X_C</c>), or empty for "no part".
    /// </summary>
    Part,

    /// <summary>Whether a weapon socket takes attachments of one mount type (<see cref="WeaponMounts"/>): "true"/"false".</summary>
    Mount,
}

/// <summary>
/// One stored value of a cooked package that can be edited: a numeric/bool/enum/text/vector property inside an export's
/// tagged properties. Cooked Blueprints store only values that differ from their parent class, so these are exactly the
/// values the game designers tuned for this asset (damage, rate of fire, wheel radius, engine RPM, weight, …).
/// </summary>
/// <param name="Export">Export key: the export's object name, with <c>#n</c> appended for the n-th repeat of a name.</param>
/// <param name="Path">Property path inside the export (<c>Name</c>, <c>Struct.Member</c>, <c>Array[i].Member</c>, <c>Name#k</c>).</param>
/// <param name="Kind">Value type.</param>
/// <param name="Value">Current value in invariant text (see <see cref="TunableValue"/>).</param>
public sealed record Tunable(string Export, string Path, TunableKind Kind, string Value)
{
    /// <summary>Property name (last path segment).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Export class name (e.g. <c>Weapon_RPK-74_C</c>, <c>DcxVehicleMovementComponent4W</c>).</summary>
    public string ExportClass { get; init; } = string.Empty;

    /// <summary>Readable group: the export without <c>Default__</c>/<c>_GEN_VARIABLE</c> plus the parent struct path.</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>Enum type for <see cref="TunableKind.Enum"/> (e.g. <c>EWeaponCategory</c>), else null.</summary>
    public string? EnumType { get; init; }

    /// <summary>Allowed values for enums: the enum's values present in the package name table.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>False for values that cannot be written safely (e.g. text nested in a struct).</summary>
    public bool CanEdit { get; init; } = true;

    /// <summary>Why <see cref="CanEdit"/> is false.</summary>
    public string? ReadOnlyReason { get; init; }

    /// <summary>
    /// For a value the package does not store but takes from a parent class (<see cref="InheritedValues"/>): that class
    /// (<c>/Script/SCUM.ItemEntitySetup</c>, <c>/Game/…/2H_Katana.2H_Katana_C</c>), else null. Its <see cref="Value"/> is the
    /// parent's, or empty when the parent is game code (the value is in no file).
    /// </summary>
    public string? InheritedFrom { get; init; }

    /// <summary>
    /// The property's tag type in the game file (<c>ByteProperty</c>, <c>IntProperty</c>, <c>UInt16Property</c> …) when the
    /// reader knows it, else null. A whole number must fit it: a bag's <c>NumRows</c> is a ByteProperty, 0 to 255
    /// (<see cref="TunableValue.CheckRange"/>).
    /// </summary>
    public string? TagType { get; init; }

    /// <summary>Stable key within a package: <c>Export|Path</c>.</summary>
    public string Key => Export + "|" + Path;
}

/// <summary>An edit of one tunable: the new value in invariant text.</summary>
/// <param name="Export">Export key (<see cref="Tunable.Export"/>).</param>
/// <param name="Path">Property path (<see cref="Tunable.Path"/>).</param>
/// <param name="Value">New value text.</param>
public sealed record TunableEdit(string Export, string Path, string Value);

/// <summary>Formatting and parsing of tunable values (invariant culture, round-trippable).</summary>
public static class TunableValue
{
    /// <summary>Formats a float so it parses back to the same bits.</summary>
    public static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Formats several floats as "a, b, c".</summary>
    public static string Format(params float[] values) => string.Join(", ", values.Select(Format));

    /// <summary>Parses "a, b, c" (also accepts ';' or spaces) into exactly <paramref name="count"/> floats.</summary>
    /// <exception cref="FormatException">Wrong count or not numbers.</exception>
    public static float[] ParseFloats(string text, int count)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Trim().Trim('(', ')').Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != count)
        {
            throw new FormatException($"Expected {count} numbers separated by commas, got '{text}'.");
        }

        return parts.Select(ParseFloat).ToArray();
    }

    /// <summary>Parses one float (invariant; also accepts a decimal comma when there is no dot).</summary>
    public static float ParseFloat(string text)
    {
        var t = text.Trim();
        if (!t.Contains('.') && t.Count(c => c == ',') == 1)
        {
            t = t.Replace(',', '.');
        }

        if (!float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !float.IsFinite(v))
        {
            throw new FormatException($"'{text}' is not a number.");
        }

        return v;
    }

    /// <summary>Parses a bool ("true"/"false"/"1"/"0"/"yes"/"no").</summary>
    public static bool ParseBool(string text) => text.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => throw new FormatException($"'{text}' is not true/false."),
    };

    /// <summary>
    /// The smallest and largest whole number a property of tag type <paramref name="tagType"/> holds (a ByteProperty 0 to 255,
    /// an IntProperty a signed 32-bit number …), or null for a type that is not a whole number or not known.
    /// </summary>
    public static (Int128 Min, Int128 Max)? Range(string? tagType) => tagType switch
    {
        "ByteProperty" => (byte.MinValue, byte.MaxValue),
        "Int8Property" => (sbyte.MinValue, sbyte.MaxValue),
        "Int16Property" => (short.MinValue, short.MaxValue),
        "UInt16Property" => (ushort.MinValue, ushort.MaxValue),
        "IntProperty" => (int.MinValue, int.MaxValue),
        "UInt32Property" => (uint.MinValue, uint.MaxValue),
        "Int64Property" => (long.MinValue, long.MaxValue),
        "UInt64Property" => (ulong.MinValue, ulong.MaxValue),
        _ => null,
    };

    /// <summary>
    /// Why <paramref name="text"/> cannot be written as the whole number <paramref name="tunable"/> is: not a whole number, or
    /// outside what its <see cref="Tunable.TagType"/> holds (a bag's <c>NumRows</c> takes 0 to 255). Null when it fits, or when
    /// the value is not a whole number of a known size. The export writes the value in that size; a value that does not fit
    /// would stop every edit of the package.
    /// </summary>
    public static string? CheckRange(Tunable tunable, string text)
    {
        ArgumentNullException.ThrowIfNull(tunable);
        ArgumentNullException.ThrowIfNull(text);
        if (tunable.Kind is not (TunableKind.Int or TunableKind.UInt) || Range(tunable.TagType) is not { } range)
        {
            return null;
        }

        if (!Int128.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return $"'{text}' is not a whole number.";
        }

        return value < range.Min || value > range.Max
            ? string.Create(CultureInfo.InvariantCulture, $"{tunable.Name} takes a whole number from {range.Min} to {range.Max} (a {tunable.TagType} in the game file); '{text.Trim()}' does not fit.")
            : null;
    }

    /// <summary>True when two value texts mean the same value of <paramref name="kind"/> (numeric compare for numbers).</summary>
    public static bool AreEqual(TunableKind kind, string a, string b)
    {
        try
        {
            return kind switch
            {
                TunableKind.Float => ParseFloat(a).Equals(ParseFloat(b)),
                TunableKind.Double => double.Parse(a, CultureInfo.InvariantCulture).Equals(double.Parse(b, CultureInfo.InvariantCulture)),
                TunableKind.Int => long.Parse(a.Trim(), CultureInfo.InvariantCulture) == long.Parse(b.Trim(), CultureInfo.InvariantCulture),
                TunableKind.UInt => ulong.Parse(a.Trim(), CultureInfo.InvariantCulture) == ulong.Parse(b.Trim(), CultureInfo.InvariantCulture),
                TunableKind.Bool or TunableKind.Mount => ParseBool(a) == ParseBool(b),
                TunableKind.Vector or TunableKind.Rotator => ParseFloats(a, 3).SequenceEqual(ParseFloats(b, 3)),
                TunableKind.Color => ParseFloats(a, 4).SequenceEqual(ParseFloats(b, 4)),
                _ => string.Equals(a, b, StringComparison.Ordinal),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }
}
