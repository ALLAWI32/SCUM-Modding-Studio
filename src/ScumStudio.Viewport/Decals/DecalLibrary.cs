using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ScumStudio.Viewport.Decals;

/// <summary>
/// The decals shipped with the studio (owner: "tattoos and stickers like drift cars"): our own art, drawn here from
/// shapes, curves and seeded noise (nothing traced from a brand or an artist), and stickers from the user's pictures.
/// </summary>
public static partial class DecalLibrary
{
    /// <summary>The library's categories, in the order the panel shows them.</summary>
    public static IReadOnlyList<string> Categories { get; } = ["Tribal", "Flames", "Stripes", "Numbers", "Army", "Camo", "Anime", "Skulls", "Shapes", "Stars", "Logos"];

    private static readonly Lazy<IReadOnlyList<DecalDesign>> Designs = new(Build);

    /// <summary>Every design of the library.</summary>
    public static IReadOnlyList<DecalDesign> All => Designs.Value;

    /// <summary>The design with <paramref name="id"/>, or null.</summary>
    public static DecalDesign? Find(string? id) => id is null ? null : All.FirstOrDefault(d => d.Id == id);

    /// <summary>
    /// A PNG or JPG picture as a sticker, at most <paramref name="maxSize"/> pixels on its long side (premultiplied linear,
    /// its own transparency kept).
    /// </summary>
    public static DecalImage LoadSticker(string file, int maxSize = 1024)
    {
        using var image = Image.Load<Rgba32>(file);
        var longSide = Math.Max(image.Width, image.Height);
        if (longSide > maxSize)
        {
            image.Mutate(x => x.Resize(Math.Max(1, image.Width * maxSize / longSide), Math.Max(1, image.Height * maxSize / longSide)));
        }

        var rgba = new float[image.Width * image.Height * 4];
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var (p, i) = (row[x], ((y * rows.Width) + x) * 4);
                    var a = p.A / 255f;
                    rgba[i] = ToLinear(p.R) * a;
                    rgba[i + 1] = ToLinear(p.G) * a;
                    rgba[i + 2] = ToLinear(p.B) * a;
                    rgba[i + 3] = a;
                }
            }
        });
        return new DecalImage(image.Width, image.Height, rgba);
    }

    private static float ToLinear(byte v)
    {
        var c = v / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }
}
