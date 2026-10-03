using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Serialization;

/// <summary>
/// Shared System.Text.Json settings for ScumStudio.Level files (journal lines, <c>project.json</c>, level dumps):
/// camelCase names, enums as strings, UE math types as compact arrays
/// (<c>FVector</c> = <c>[x, y, z]</c>, <c>FRotator</c> = <c>[pitch, yaw, roll]</c>, <c>FQuat</c> = <c>[x, y, z, w]</c>).
/// </summary>
public static class LevelJson
{
    /// <summary>Single-line options (JSONL journal records).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    /// <summary>Indented options (project.json, dumps).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    /// <summary>Creates a fresh options instance with the Level converters.</summary>
    public static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new FVectorJsonConverter());
        options.Converters.Add(new FRotatorJsonConverter());
        options.Converters.Add(new FQuatJsonConverter());
        options.Converters.Add(new FTransformJsonConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    internal static float[] ReadFloats(ref Utf8JsonReader reader, int count, string typeName)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected a JSON array of {count} numbers for {typeName}.");
        }

        var values = new float[count];
        var i = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (i >= count)
            {
                throw new JsonException($"Too many numbers for {typeName} (expected {count}).");
            }

            values[i++] = reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetSingle(),
                JsonTokenType.String when float.TryParse(reader.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f) => f,
                _ => throw new JsonException($"Expected a number in {typeName}."),
            };
        }

        if (i != count)
        {
            throw new JsonException($"Expected {count} numbers for {typeName}, got {i}.");
        }

        return values;
    }

    /// <summary>
    /// Writes <c>[a, b, c]</c> on one line even in indented output (vectors stay readable). Numbers use the shortest
    /// round-trip form; -0 is written as 0; NaN/infinities as strings.
    /// </summary>
    internal static void WriteFloats(Utf8JsonWriter writer, ReadOnlySpan<float> values)
    {
        var sb = new System.Text.StringBuilder(values.Length * 8);
        sb.Append('[');
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var v = values[i];
            if (float.IsFinite(v))
            {
                // Adding +0 turns -0 into 0 (IEEE), so rotators derived from quaternions do not print "-0".
                sb.Append((v + 0f).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(float.IsNaN(v) ? "\"NaN\"" : v > 0 ? "\"Infinity\"" : "\"-Infinity\"");
            }
        }

        sb.Append(']');
        writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
    }
}

/// <summary>Writes an <see cref="FVector"/> as <c>[x, y, z]</c>.</summary>
public sealed class FVectorJsonConverter : JsonConverter<FVector>
{
    /// <inheritdoc />
    public override FVector Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var v = LevelJson.ReadFloats(ref reader, 3, nameof(FVector));
        return new FVector(v[0], v[1], v[2]);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FVector value, JsonSerializerOptions options) =>
        LevelJson.WriteFloats(writer, [value.X, value.Y, value.Z]);
}

/// <summary>Writes an <see cref="FRotator"/> as <c>[pitch, yaw, roll]</c> (degrees).</summary>
public sealed class FRotatorJsonConverter : JsonConverter<FRotator>
{
    /// <inheritdoc />
    public override FRotator Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var v = LevelJson.ReadFloats(ref reader, 3, nameof(FRotator));
        return new FRotator(v[0], v[1], v[2]);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FRotator value, JsonSerializerOptions options) =>
        LevelJson.WriteFloats(writer, [value.Pitch, value.Yaw, value.Roll]);
}

/// <summary>Writes an <see cref="FQuat"/> as <c>[x, y, z, w]</c>.</summary>
public sealed class FQuatJsonConverter : JsonConverter<FQuat>
{
    /// <inheritdoc />
    public override FQuat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var v = LevelJson.ReadFloats(ref reader, 4, nameof(FQuat));
        return new FQuat(v[0], v[1], v[2], v[3]);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FQuat value, JsonSerializerOptions options) =>
        LevelJson.WriteFloats(writer, [value.X, value.Y, value.Z, value.W]);
}

/// <summary>
/// Writes an <see cref="FTransform"/> as <c>{"rotation":[x,y,z,w],"translation":[x,y,z],"scale3D":[x,y,z]}</c>
/// (exact round trip; missing members default to identity).
/// </summary>
public sealed class FTransformJsonConverter : JsonConverter<FTransform>
{
    /// <inheritdoc />
    public override FTransform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a JSON object for FTransform.");
        }

        var result = FTransform.Identity;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name?.ToLowerInvariant())
            {
                case "rotation":
                    var q = LevelJson.ReadFloats(ref reader, 4, "FTransform.Rotation");
                    result = result with { Rotation = new FQuat(q[0], q[1], q[2], q[3]) };
                    break;
                case "translation":
                    var t = LevelJson.ReadFloats(ref reader, 3, "FTransform.Translation");
                    result = result with { Translation = new FVector(t[0], t[1], t[2]) };
                    break;
                case "scale3d":
                    var s = LevelJson.ReadFloats(ref reader, 3, "FTransform.Scale3D");
                    result = result with { Scale3D = new FVector(s[0], s[1], s[2]) };
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FTransform value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("rotation");
        LevelJson.WriteFloats(writer, [value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W]);
        writer.WritePropertyName("translation");
        LevelJson.WriteFloats(writer, [value.Translation.X, value.Translation.Y, value.Translation.Z]);
        writer.WritePropertyName("scale3D");
        LevelJson.WriteFloats(writer, [value.Scale3D.X, value.Scale3D.Y, value.Scale3D.Z]);
        writer.WriteEndObject();
    }
}
