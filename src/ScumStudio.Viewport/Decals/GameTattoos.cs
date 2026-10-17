using System.Globalization;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;

namespace ScumStudio.Viewport.Decals;

/// <summary>
/// One of SCUM's own tattoos (character creation's tattoo picker): entry <paramref name="Index"/> of the game's
/// <c>PrisonerTattooData</c>, drawn in one colour channel of one cell of <c>T_Tattoos_Atlas</c>.
/// </summary>
/// <param name="Index">Its place in the game's list (the game's tattoo id).</param>
/// <param name="PreviewScale">How much the game's picker zooms in on it (small designs are zoomed in more).</param>
/// <param name="Locations">Where the game lets it go on a body (<c>ArmUpperLeft</c>, <c>Chest</c> …).</param>
public sealed record GameTattoo(int Index, float PreviewScale, IReadOnlyList<string> Locations)
{
    /// <summary>The design id saved in <c>decals.json</c> (<c>game-tattoo-12</c>).</summary>
    public string Id => GameTattoos.IdOf(Index);

    /// <summary>The atlas channel holding it: 0 red, 1 green, 2 blue.</summary>
    public int Channel => Index / GameTattoos.CellsPerChannel;

    /// <summary>The atlas column of its cell.</summary>
    public int Column => Index % GameTattoos.Columns;

    /// <summary>The atlas row of its cell.</summary>
    public int Row => Index % GameTattoos.CellsPerChannel / GameTattoos.Columns;
}

/// <summary>
/// SCUM's tattoos as decals, read from the user's own game files at run time (never copied into the repository or the
/// app). The game lists them in <c>PrisonerTattooData</c> (90 entries in 2026: preview zoom, body places, DLC, disabled)
/// and draws them from <c>T_Tattoos_Atlas</c>: 2048 x 4096, BC7, linear, a 4 x 8 grid of 512 px cells where each of the
/// red, green and blue channels holds one tattoo as ink coverage (white = ink), so entry <c>i</c> sits in channel
/// <c>i / 32</c>, cell <c>i % 32</c> row by row. The tattoo material (<c>M_PrisonerTattooPreview</c>) tints that coverage
/// with one colour (<c>TattooColor</c>, black by default): a tattoo has no colour regions of its own.
/// </summary>
public static class GameTattoos
{
    /// <summary>The game's list of tattoos.</summary>
    public const string DataPath = "/Game/ConZ_Files/Characters/Prisoner/Models/Male/Tattoos/PrisonerTattooData";

    /// <summary>The game's tattoo pictures.</summary>
    public const string AtlasPath = "/Game/ConZ_Files/Characters/Prisoner/Models/Male/Tattoos/T_Tattoos_Atlas";

    /// <summary>Columns of the atlas grid.</summary>
    public const int Columns = 4;

    /// <summary>Rows of the atlas grid.</summary>
    public const int Rows = 8;

    /// <summary>Cells in one colour channel.</summary>
    public const int CellsPerChannel = Columns * Rows;

    /// <summary>The library category of the game's tattoos.</summary>
    public const string Category = "GameTattoos";

    /// <summary>The colour a tattoo starts with: the game's ink (<c>TattooColor</c> is black).</summary>
    public const uint Ink = 0x141414;

    private const string Prefix = "game-tattoo-";

    /// <summary>Coverage under this (of 255) is compression noise, not ink.</summary>
    private const int Floor = 6;

    /// <summary>The design id of the game's tattoo <paramref name="index"/>.</summary>
    public static string IdOf(int index) => Prefix + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The game's tattoo index of a design id, or null when it is not one of the game's tattoos.</summary>
    public static int? IndexOf(string? id) =>
        id is not null && id.StartsWith(Prefix, StringComparison.Ordinal) && int.TryParse(id.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : null;

    /// <summary>
    /// The tattoos a player can pick in the game: the list's entries that are not disabled and need no paid DLC (those
    /// stay out: a mod would hand their pictures to every player), within the atlas.
    /// </summary>
    public static IReadOnlyList<GameTattoo> Read(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var data = catalog.LoadFirstExport<UObject>(DataPath) ?? throw new InvalidDataException($"{DataPath} has no data.");
        var entries = data.GetOrDefault<FStructFallback[]>("Tattoos") ?? [];
        var tattoos = new List<GameTattoo>();
        for (var i = 0; i < entries.Length && i < CellsPerChannel * 3; i++)
        {
            var entry = entries[i];
            if (entry is null || entry.GetOrDefault<bool>("IsDisabled") || (entry.GetOrDefault<FName[]>("DLCVersions") ?? []).Length > 0)
            {
                continue;
            }

            var locations = (entry.GetOrDefault<FName[]>("ValidLocations") ?? []).Select(n => n.Text[(n.Text.LastIndexOf(':') + 1)..]).Where(n => n is not "None").ToList();
            tattoos.Add(new GameTattoo(i, entry.GetOrDefault("TattooPreviewScale", 1f), locations));
        }

        return tattoos;
    }

    /// <summary>The atlas decoded at most <paramref name="maxSize"/> px on its long side (0: the full 2048 x 4096).</summary>
    public static TextureImage LoadAtlas(AssetCatalog catalog, int maxSize = 0)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var texture = catalog.LoadFirstExport<UTexture2D>(AtlasPath) ?? throw new InvalidDataException($"{AtlasPath} is not a texture.");
        return TextureDecoder.Decode(texture, maxSize);
    }

    /// <summary>
    /// <paramref name="tattoo"/> cut out of <paramref name="atlas"/> as one coverage plane, trimmed to its ink (a small
    /// design is not lost in its cell), with its own aspect.
    /// </summary>
    public static DecalRaster Cut(TextureImage atlas, GameTattoo tattoo)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(tattoo);
        var (cw, ch) = (atlas.Width / Columns, atlas.Height / Rows);
        var (x0, y0) = (tattoo.Column * cw, tattoo.Row * ch);
        var channel = tattoo.Channel;
        var (minX, minY, maxX, maxY) = (cw, ch, -1, -1);
        for (var y = 0; y < ch; y++)
        {
            for (var x = 0; x < cw; x++)
            {
                if (atlas.Rgba[((((y0 + y) * atlas.Width) + x0 + x) * 4) + channel] > 24)
                {
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                }
            }
        }

        if (maxX < 0)
        {
            return new DecalRaster(1, 1, [new float[1]]); // an empty cell
        }

        var margin = Math.Max(1, cw / 128);
        (minX, minY, maxX, maxY) = (Math.Max(0, minX - margin), Math.Max(0, minY - margin), Math.Min(cw - 1, maxX + margin), Math.Min(ch - 1, maxY + margin));
        var (w, h) = (maxX - minX + 1, maxY - minY + 1);
        var plane = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = atlas.Rgba[((((y0 + minY + y) * atlas.Width) + x0 + minX + x) * 4) + channel];
                plane[(y * w) + x] = v <= Floor ? 0f : (v - Floor) / (255f - Floor);
            }
        }

        return new DecalRaster(w, h, [plane]);
    }

    /// <summary>The design entry of <paramref name="tattoo"/> (one colour region in the game's ink; its picture is <see cref="Cut"/>).</summary>
    public static DecalDesign Design(GameTattoo tattoo, DecalRaster cut)
    {
        ArgumentNullException.ThrowIfNull(tattoo);
        ArgumentNullException.ThrowIfNull(cut);
        return new DecalDesign(tattoo.Id, Category, cut.Width, cut.Height, [new DecalRegion(string.Empty, Ink)]);
    }

    /// <summary>A small picture of a cut tattoo for the library: <paramref name="ink"/> where the ink is, clear elsewhere (straight RGBA).</summary>
    public static (byte[] Rgba, int Width, int Height) Picture(DecalRaster cut, (byte R, byte G, byte B) ink)
    {
        ArgumentNullException.ThrowIfNull(cut);
        var plane = cut.Planes[0];
        var rgba = new byte[cut.Width * cut.Height * 4];
        for (var i = 0; i < plane.Length; i++)
        {
            (rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2], rgba[(i * 4) + 3]) = (ink.R, ink.G, ink.B, (byte)MathF.Round(Math.Clamp(plane[i], 0f, 1f) * 255f));
        }

        return (rgba, cut.Width, cut.Height);
    }
}
