using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScumStudio.Viewport.Decals;

/// <summary>How a decal gets its colours.</summary>
public enum DecalColourMode
{
    /// <summary>One colour for the whole design.</summary>
    Single,

    /// <summary>Each region of the design its own colour.</summary>
    PerRegion,

    /// <summary>Each region a colour picked from the palette by the seed: the same seed always gives the same colours.</summary>
    Palette,
}

/// <summary>
/// One decal on a vehicle (saved in the project's <c>decals/&lt;clone&gt;.json</c>): which design or picture, where it sits
/// on which part (part space, so it follows the part), its size, turn, opacity and colours.
/// </summary>
public sealed record DecalLayer
{
    /// <summary>Stable id of the layer.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Library design (<see cref="DecalLibrary"/>), or null for a sticker.</summary>
    public string? DesignId { get; init; }

    /// <summary>The sticker's picture (PNG or JPG, a copy in the project), or null for a library design.</summary>
    public string? StickerFile { get; init; }

    /// <summary>The part it sits on: the attachment leaf without <c>BPC_</c> and the vehicle token (<c>Door_FrontLeft</c>), empty for the body.</summary>
    public string PartKey { get; init; } = string.Empty;

    /// <summary>The material whose textures it is baked into (the stock path).</summary>
    public string MaterialPath { get; init; } = string.Empty;

    /// <summary>Centre of the decal on the part (part space, cm).</summary>
    [JsonConverter(typeof(Vector3Json))]
    public Vector3 Position { get; init; }

    /// <summary>The surface normal there (part space, unit): the decal is projected along it.</summary>
    [JsonConverter(typeof(Vector3Json))]
    public Vector3 Normal { get; init; } = Vector3.UnitX;

    /// <summary>Which way is up on the decal before <see cref="RotationDeg"/> (part space).</summary>
    [JsonConverter(typeof(Vector3Json))]
    public Vector3 Up { get; init; } = Vector3.UnitZ;

    /// <summary>Width on the car, cm.</summary>
    public float WidthCm { get; init; } = 40f;

    /// <summary>Height on the car, cm (the design's aspect unless stretched).</summary>
    public float HeightCm { get; init; } = 20f;

    /// <summary>Turn on the surface, degrees.</summary>
    public float RotationDeg { get; init; }

    /// <summary>Drawn mirrored left to right.</summary>
    public bool FlipX { get; init; }

    /// <summary>A mirrored copy on the other side of the vehicle (across its Y = 0 plane).</summary>
    public bool Mirror { get; init; }

    /// <summary>
    /// Stuck to the surface (owner: "a decal that follows the car and can cover all of it"): projected deep along its
    /// normal onto every part under it, whatever material each is painted with, so one decal crosses doors, fenders and the
    /// hood, each part taking its piece; it may be as large as the whole vehicle.
    /// </summary>
    public bool Wrap { get; init; }

    /// <summary>Not drawn and not baked, but kept in the list (the layer list's eye).</summary>
    public bool Hidden { get; init; }

    /// <summary>Cannot be picked, dragged, nudged or turned in the 3D view (the layer list's lock).</summary>
    public bool Locked { get; init; }

    /// <summary>0 = invisible, 1 = solid.</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>Drawing order: higher is on top.</summary>
    public int Order { get; init; }

    /// <summary>How the colours are chosen.</summary>
    public DecalColourMode ColourMode { get; init; } = DecalColourMode.PerRegion;

    /// <summary>Chosen colours (<c>#RRGGBB</c>): the one colour, or one per region.</summary>
    public IReadOnlyList<string> Colours { get; init; } = [];

    /// <summary>The colours <see cref="DecalColourMode.Palette"/> picks from (<c>#RRGGBB</c>).</summary>
    public IReadOnlyList<string> Palette { get; init; } = [];

    /// <summary>The palette pick's variation.</summary>
    public int Seed { get; init; }

    /// <summary>The colour (sRGB 0..1) of each of <paramref name="regions"/> regions under <see cref="ColourMode"/>; <paramref name="defaults"/> are the design's own.</summary>
    public IReadOnlyList<Vector3> RegionColours(int regions, IReadOnlyList<uint> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var own = defaults.Select(FromRgb).ToList();
        var chosen = Colours.Select(Parse).OfType<Vector3>().ToList();
        var palette = Palette.Select(Parse).OfType<Vector3>().ToList();
        var random = new Random(Seed);
        return Enumerable.Range(0, regions).Select(k => ColourMode switch
        {
            DecalColourMode.Single when chosen.Count > 0 => chosen[0],
            DecalColourMode.PerRegion when k < chosen.Count => chosen[k],
            DecalColourMode.Palette when palette.Count > 0 => palette[random.Next(palette.Count)],
            _ => k < own.Count ? own[k] : Vector3.One,
        }).ToList();
    }

    /// <summary>The design (rasterised as <paramref name="raster"/>) in this layer's colours.</summary>
    public DecalImage Render(DecalRaster raster, DecalDesign design)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(design);
        static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return raster.Colourise([.. RegionColours(design.Regions.Count, [.. design.Regions.Select(r => r.Colour)]).Select(c => new Vector3(Linear(c.X), Linear(c.Y), Linear(c.Z)))]);
    }

    /// <summary><c>#RRGGBB</c> as sRGB 0..1, or null.</summary>
    public static Vector3? Parse(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? FromRgb(v) : null;

    /// <summary>sRGB 0..1 as <c>#RRGGBB</c>.</summary>
    public static string Hex(Vector3 srgb)
    {
        static int B(float c) => (int)MathF.Round(Math.Clamp(c, 0f, 1f) * 255f);
        return $"#{B(srgb.X):X2}{B(srgb.Y):X2}{B(srgb.Z):X2}";
    }

    /// <summary>0xRRGGBB as sRGB 0..1.</summary>
    public static Vector3 FromRgb(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    /// <summary>Writes a vector as <c>[x, y, z]</c>.</summary>
    internal sealed class Vector3Json : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = JsonSerializer.Deserialize<float[]>(ref reader, options) ?? [];
            return v.Length == 3 ? new Vector3(v[0], v[1], v[2]) : throw new JsonException("A vector is [x, y, z].");
        }

        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new[] { value.X, value.Y, value.Z }, options);
    }
}
