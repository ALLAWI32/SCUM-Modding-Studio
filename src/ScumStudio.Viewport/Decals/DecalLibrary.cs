using System.Globalization;
using System.Numerics;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ScumStudio.Viewport.Decals;

/// <summary>
/// The decals shipped with the studio (owner: "tattoos and stickers like drift cars"): our own art, drawn here from
/// shapes, curves and seeded noise (nothing traced from a brand or an artist), and stickers from the user's pictures.
/// </summary>
public static class DecalLibrary
{
    /// <summary>The library's categories, in the order the panel shows them.</summary>
    public static IReadOnlyList<string> Categories { get; } = ["Tribal", "Flames", "Stripes", "Numbers", "Army", "Anime", "Skulls", "Shapes", "Logos"];

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

    internal static IReadOnlyList<DecalDesign> Build()
    {
        var list = new List<DecalDesign>();
        for (var i = 0; i < 6; i++)
        {
            list.Add(Tribal(i));
        }

        for (var i = 0; i < 6; i++)
        {
            list.Add(Flames(i));
        }

        list.AddRange(Stripes());
        for (var d = 0; d < 10; d++)
        {
            list.Add(RacingNumber(d));
        }

        for (var d = 0; d < 10; d++)
        {
            list.Add(StencilNumber(d));
        }

        list.AddRange(Army());
        list.AddRange(Anime());
        list.AddRange(Skulls());
        list.AddRange(Shapes());
        list.AddRange(Logos());
        return list;
    }

    private static DecalDesign Design(string id, string category, float w, float h, params DecalRegion[] regions) => new(id, category, w, h, regions);

    private static DecalRegion R(PathBuilder p, uint colour, FillRule rule = FillRule.NonZero) => new(p.ToString(), colour, rule);

    // Tribal: mirrored curved spikes round a centre lens; the accent region is the spikes' inner edge, a third has dots.
    private static DecalDesign Tribal(int variant)
    {
        var rnd = new Random(100 + variant);
        var (main, accent, dots) = (new PathBuilder(), new PathBuilder(), new PathBuilder());
        var spikes = 3 + (variant % 3);
        foreach (var side in new[] { 1f, -1f })
        {
            main.T = accent.T = dots.T = side > 0 ? Matrix3x2.Identity : new Matrix3x2(-1, 0, 0, 1, 200, 0);
            for (var j = 0; j < spikes; j++)
            {
                var b = new Vector2(104 + (j * 12), 50 + ((float)rnd.NextDouble() - 0.5f) * 16);
                var tip = new Vector2(130 + (j * 15) + ((float)rnd.NextDouble() * 12), 50 + (((j % 2 == 0) ? -1 : 1) * (18 + ((float)rnd.NextDouble() * 26))));
                var curl = ((j % 2 == 0) ? 1 : -1) * (0.25f + ((float)rnd.NextDouble() * 0.3f));
                var width = 10 + ((float)rnd.NextDouble() * 6);
                main.Blade(b, tip, width, curl);
                accent.Blade(b + ((tip - b) * 0.15f), b + ((tip - b) * 0.62f), width * 0.32f, curl);
                if (variant >= 3)
                {
                    dots.Circle(tip.X + ((b.X - tip.X) * 0.1f), tip.Y + ((tip.Y > 50 ? -1 : 1) * 9), 2.6f);
                }
            }
        }

        main.T = accent.T = Matrix3x2.Identity;
        main.Lens(100, 50, 9, 34);
        accent.Lens(100, 50, 3, 22);
        return variant >= 3
            ? Design($"tribal-{variant + 1}", "Tribal", 200, 100, R(main, 0x141414), R(accent, 0xB01818), R(dots, 0x141414))
            : Design($"tribal-{variant + 1}", "Tribal", 200, 100, R(main, 0x141414), R(accent, 0xB01818));
    }

    // Flames: tongues from the left edge; the inner regions are the same tongues, shorter and thinner.
    private static DecalDesign Flames(int variant)
    {
        var rnd = new Random(200 + variant);
        var count = 4 + variant;
        var tongues = Enumerable.Range(0, count).Select(i => (
            Y: 12 + (76f * (i + 0.5f) / count) + (((float)rnd.NextDouble() - 0.5f) * 6),
            Length: 110 + ((float)rnd.NextDouble() * 85),
            Rise: ((float)rnd.NextDouble() - 0.5f) * 30,
            Width: 20 + ((float)rnd.NextDouble() * 10))).ToList();
        PathBuilder Layer(float scale)
        {
            var p = new PathBuilder();
            foreach (var g in tongues)
            {
                p.Tongue(new Vector2(0, g.Y), g.Length * scale, g.Rise * scale, g.Width * (0.35f + (0.65f * scale)));
            }

            return p;
        }

        var regions = variant % 3 == 2
            ? new[] { R(Layer(1f), 0xC81E0A), R(Layer(0.6f), 0xFFC83C) }
            : new[] { R(Layer(1f), 0xC81E0A), R(Layer(0.7f), 0xF07814), R(Layer(0.42f), 0xFFDC50) };
        return Design($"flames-{variant + 1}", "Flames", 200, 100, regions);
    }

    private static IEnumerable<DecalDesign> Stripes()
    {
        var racing = new PathBuilder().Rect(0, 8, 200, 16).Rect(0, 36, 200, 16);
        var pins = new PathBuilder().Rect(0, 2, 200, 2.5f).Rect(0, 28, 200, 2.5f).Rect(0, 55.5f, 200, 2.5f);
        yield return Design("stripes-racing", "Stripes", 200, 60, R(racing, 0xF0F0F0), R(pins, 0x1E3C8C));

        var thin = new PathBuilder().Rect(0, 4, 200, 3).Rect(0, 12, 200, 1.5f).Rect(0, 18, 200, 3);
        yield return Design("stripes-pinstripe", "Stripes", 200, 25, R(thin, 0xE6E6E6));

        var hazard = new PathBuilder();
        for (var x = -40f; x < 240; x += 28)
        {
            hazard.Poly(Clip([new(x, 0), new(x + 14, 0), new(x - 26, 40), new(x - 40, 40)], 200, 40));
        }

        yield return Design("stripes-hazard", "Stripes", 200, 40, R(new PathBuilder().Rect(0, 0, 200, 40), 0xF5C814), R(hazard, 0x141414));

        var checks = new PathBuilder();
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                if ((x + y) % 2 == 0)
                {
                    checks.Rect(x * 12.5f, y * 12.5f, 12.5f, 12.5f);
                }
            }
        }

        yield return Design("stripes-checker", "Stripes", 200, 50, R(new PathBuilder().Rect(0, 0, 200, 50), 0xF0F0F0), R(checks, 0x141414));

        var swoosh = new PathBuilder().M(0, 70).C(60, 70, 110, 10, 200, 8).L(200, 26).C(120, 26, 70, 84, 0, 86).Z();
        var swooshLine = new PathBuilder().M(0, 92).C(70, 92, 120, 34, 200, 32).L(200, 37).C(124, 37, 74, 97, 0, 97).Z();
        yield return Design("stripes-swoosh", "Stripes", 200, 100, R(swoosh, 0xD21E1E), R(swooshLine, 0x141414));

        var speed = new PathBuilder().Rect(0, 4, 200, 10).Rect(40, 22, 160, 10).Rect(90, 40, 110, 10);
        var speedEnds = new PathBuilder().Poly([new(0, 4), new(14, 4), new(4, 14), new(0, 14)]).Poly([new(40, 22), new(54, 22), new(44, 32), new(40, 32)]);
        yield return Design("stripes-speed", "Stripes", 200, 54, R(speed, 0x1E8CDC), R(speedEnds, 0xF0F0F0));
    }

    // Seven segments a..g of a digit in a 60 x 100 box: (x0, y0, x1, y1), horizontal when y0 == y1.
    private static readonly (float X0, float Y0, float X1, float Y1)[] Segments =
        [(0, 0, 60, 0), (60, 0, 60, 50), (60, 50, 60, 100), (0, 100, 60, 100), (0, 50, 0, 100), (0, 0, 0, 50), (0, 50, 60, 50)];

    private static readonly string[] DigitSegments = ["abcdef", "bc", "abged", "abgcd", "fgbc", "afgcd", "afgedc", "abc", "abcdefg", "abcdfg"];

    private static PathBuilder Digit(int d, Matrix3x2 place, float thickness, float gap, bool square)
    {
        var p = new PathBuilder { T = place };
        foreach (var s in DigitSegments[d])
        {
            var (x0, y0, x1, y1) = Segments[s - 'a'];
            var dir = Vector2.Normalize(new Vector2(x1 - x0, y1 - y0));
            var n = new Vector2(-dir.Y, dir.X) * (thickness / 2);
            var (a, b) = (new Vector2(x0, y0) + (dir * gap), new Vector2(x1, y1) - (dir * gap));
            var bevel = square ? Vector2.Zero : dir * (thickness / 2);
            p.Poly([a, a + bevel + n, b - bevel + n, b, b - bevel - n, a + bevel - n]);
        }

        return p;
    }

    private static DecalDesign RacingNumber(int d)
    {
        var place = Matrix3x2.CreateScale(0.62f) * Matrix3x2.CreateTranslation(31.4f, 19f);
        return Design($"number-racing-{d}", "Numbers", 100, 100,
            R(new PathBuilder().Circle(50, 50, 47), 0xF0F0F0),
            R(new PathBuilder().Circle(50, 50, 48).Circle(50, 50, 43), 0x141414, FillRule.EvenOdd),
            R(Digit(d, place, 15, 2.5f, false), 0x141414));
    }

    private static DecalDesign StencilNumber(int d)
    {
        var place = Matrix3x2.CreateScale(0.7f) * Matrix3x2.CreateTranslation(29f, 15f);
        return Design($"number-stencil-{d}", "Numbers", 100, 100,
            R(Digit(d, place, 13, 6, true), 0xF0F0F0),
            R(new PathBuilder().Rect(4, 4, 92, 92).Rect(9, 9, 82, 82), 0xF0F0F0, FillRule.EvenOdd));
    }

    private static IEnumerable<DecalDesign> Army()
    {
        yield return Design("army-star", "Army", 100, 100,
            R(new PathBuilder().Circle(50, 50, 46), 0x4B5A32), R(new PathBuilder().Star(50, 52, 38, 15, 5, -90), 0xF0F0F0));
        yield return Design("army-star-ring", "Army", 100, 100,
            R(new PathBuilder().Circle(50, 50, 47).Circle(50, 50, 40), 0xF0F0F0, FillRule.EvenOdd), R(new PathBuilder().Star(50, 52, 34, 13, 5, -90), 0xF0F0F0));
        var chevrons = new PathBuilder();
        for (var i = 0; i < 3; i++)
        {
            var y = 10 + (i * 24);
            chevrons.Poly([new(10, y), new(50, y + 26), new(90, y), new(90, y + 14), new(50, y + 40), new(10, y + 14)]);
        }

        yield return Design("army-chevrons", "Army", 100, 100, R(chevrons, 0xD2B450));
        yield return Design("army-roundel", "Army", 100, 100,
            R(new PathBuilder().Circle(50, 50, 47), 0x1E3C78), R(new PathBuilder().Circle(50, 50, 31), 0xF0F0F0), R(new PathBuilder().Circle(50, 50, 15), 0xB41E1E));
        yield return Camo("army-camo-woodland", 300, [0x4B5A32, 0x2D2D1E, 0x8C7850], 14);
        yield return Camo("army-camo-desert", 301, [0xC8AF82, 0x9C8460, 0xE1CDA5], 10);
        yield return DigitalCamo("army-camo-digital", 302, [0x5A6446, 0x3C4130, 0x8C9173]);
    }

    private static DecalDesign Camo(string id, int seed, uint[] colours, int blobs)
    {
        var rnd = new Random(seed);
        var layers = new[] { new PathBuilder(), new PathBuilder() };
        for (var i = 0; i < blobs * 2; i++)
        {
            layers[i % 2].Blob(rnd, new Vector2((float)rnd.NextDouble() * 200, (float)rnd.NextDouble() * 100), 10 + ((float)rnd.NextDouble() * 16));
        }

        return Design(id, "Army", 200, 100, R(new PathBuilder().Rect(0, 0, 200, 100), colours[0]), R(layers[0], colours[1]), R(layers[1], colours[2]));
    }

    private static DecalDesign DigitalCamo(string id, int seed, uint[] colours)
    {
        var rnd = new Random(seed);
        var layers = new[] { new PathBuilder(), new PathBuilder() };
        for (var i = 0; i < 70; i++)
        {
            var (x, y) = (rnd.Next(0, 40) * 5, rnd.Next(0, 20) * 5);
            var (w, h) = (rnd.Next(1, 5) * 5, rnd.Next(1, 3) * 5);
            layers[i % 2].Rect(x, y, Math.Min(w, 200 - x), Math.Min(h, 100 - y));
        }

        return Design(id, "Army", 200, 100, R(new PathBuilder().Rect(0, 0, 200, 100), colours[0]), R(layers[0], colours[1]), R(layers[1], colours[2]));
    }

    private static IEnumerable<DecalDesign> Anime()
    {
        var lines = new PathBuilder();
        for (var i = 0; i < 28; i++)
        {
            var a = i * MathF.Tau / 28;
            var (d, s) = (new Vector2(MathF.Cos(a), MathF.Sin(a)), 0.035f + (0.02f * (i % 3)));
            var side = new Vector2(-d.Y, d.X);
            var inner = 30 + (8 * (i % 4));
            lines.Poly(Clip([new Vector2(100, 50) + (d * inner), new Vector2(100, 50) + (d * 140) + (side * 140 * s), new Vector2(100, 50) + (d * 140) - (side * 140 * s)], 200, 100));
        }

        yield return Design("anime-speed-lines", "Anime", 200, 100, R(lines, 0x141414));

        var rays = new PathBuilder();
        for (var i = 0; i < 16; i++)
        {
            var (a0, a1) = (i * MathF.Tau / 16, (i + 0.5f) * MathF.Tau / 16);
            rays.Poly(Clip([new(100, 100), new(100 + (220 * MathF.Cos(a0 + MathF.PI)), 100 + (220 * MathF.Sin(a0 + MathF.PI))),
                new(100 + (220 * MathF.Cos(a1 + MathF.PI)), 100 + (220 * MathF.Sin(a1 + MathF.PI)))], 200, 100));
        }

        yield return Design("anime-sun-rays", "Anime", 200, 100, R(rays, 0xD21E1E), R(new PathBuilder().Circle(100, 100, 34), 0xD21E1E));

        Vector2[] bolt = [new(58, 0), new(26, 54), new(48, 54), new(34, 100), new(76, 38), new(53, 38), new(70, 0)];
        var centre = new Vector2(50, 50);
        yield return Design("anime-lightning", "Anime", 100, 100,
            R(new PathBuilder().Poly([.. bolt.Select(p => centre + ((p - centre) * 0.96f) + new Vector2(3, 2))]), 0x141414),
            R(new PathBuilder().Poly([.. bolt.Select(p => centre + ((p - centre) * 0.86f))]), 0xFFDC32));

        var (wave, foam) = (new PathBuilder(), new PathBuilder());
        for (var i = 0; i < 3; i++)
        {
            var y = 20 + (i * 26);
            wave.M(0, y).C(40, y - 22, 60, y + 18, 100, y).C(140, y - 22, 160, y + 18, 200, y).L(200, y + 12).C(160, y + 30, 140, y - 10, 100, y + 12).C(60, y + 30, 40, y - 10, 0, y + 12).Z();
            foam.Circle(100 + ((i - 1) * 60), y - 6, 3.5f);
        }

        yield return Design("anime-waves", "Anime", 200, 100, R(wave, 0x1E64B4), R(foam, 0xF0F0F0));

        var (petals, heart) = (new PathBuilder(), new PathBuilder());
        for (var i = 0; i < 5; i++)
        {
            petals.T = Matrix3x2.CreateRotation(i * MathF.Tau / 5, new Vector2(50, 50));
            petals.M(50, 50).C(30, 38, 34, 10, 46, 6).L(50, 12).L(54, 6).C(66, 10, 70, 38, 50, 50).Z();
        }

        heart.Circle(50, 50, 7);
        yield return Design("anime-sakura", "Anime", 100, 100, R(petals, 0xF5AFC8), R(heart, 0xC83C78));

        yield return Design("anime-burst", "Anime", 100, 100,
            R(new PathBuilder().Star(50, 50, 49, 30, 16, 0), 0x141414), R(new PathBuilder().Star(50, 50, 40, 26, 16, 11.25f), 0xFFE650));
    }

    private static IEnumerable<DecalDesign> Skulls()
    {
        PathBuilder Head() => new PathBuilder().Circle(50, 42, 28).Rect(34, 52, 32, 26);
        PathBuilder Face()
        {
            var p = new PathBuilder().Circle(39, 44, 8).Circle(61, 44, 8).Poly([new(50, 54), new(45, 64), new(55, 64)]);
            for (var i = 0; i < 4; i++)
            {
                p.Rect(36.5f + (i * 7.5f), 70, 4, 8);
            }

            return p;
        }

        yield return Design("skull-plain", "Skulls", 100, 100, R(Head(), 0xEBE6D7), R(Face(), 0x141414));
        var bones = new PathBuilder();
        foreach (var angle in new[] { 40f, -40f })
        {
            bones.T = Matrix3x2.CreateRotation(angle * MathF.PI / 180, new Vector2(50, 55));
            bones.Rect(8, 51, 84, 8).Circle(8, 50, 6).Circle(8, 60, 6).Circle(92, 50, 6).Circle(92, 60, 6);
        }

        yield return Design("skull-crossbones", "Skulls", 100, 100, R(bones, 0xEBE6D7), R(Head(), 0xEBE6D7), R(Face(), 0x141414));
        yield return Design("skull-diamond", "Skulls", 100, 100,
            R(new PathBuilder().Poly([new(50, 0), new(100, 50), new(50, 100), new(0, 50)]), 0xB41E1E), R(Head(), 0xEBE6D7), R(Face(), 0x141414));
        var horns = new PathBuilder();
        horns.Blade(new Vector2(34, 30), new Vector2(6, 2), 12, 0.35f);
        horns.T = new Matrix3x2(-1, 0, 0, 1, 100, 0);
        horns.Blade(new Vector2(34, 30), new Vector2(6, 2), 12, 0.35f);
        yield return Design("skull-horns", "Skulls", 100, 100, R(horns, 0x5A1E14), R(Head(), 0xEBE6D7), R(Face(), 0x141414));
    }

    private static IEnumerable<DecalDesign> Shapes()
    {
        var heart = new PathBuilder().M(50, 90).C(10, 62, 0, 36, 18, 20).C(32, 8, 46, 16, 50, 28).C(54, 16, 68, 8, 82, 20).C(100, 36, 90, 62, 50, 90).Z();
        yield return Design("shape-heart", "Shapes", 100, 100, R(heart, 0xC81E3C));

        var (wings, inner) = (new PathBuilder(), new PathBuilder());
        foreach (var t in new[] { Matrix3x2.Identity, new Matrix3x2(-1, 0, 0, 1, 200, 0) })
        {
            wings.T = inner.T = t;
            for (var i = 0; i < 5; i++)
            {
                wings.Blade(new Vector2(108, 40 + (i * 7)), new Vector2(150 + (i * 9), 8 + (i * 16)), 14, -0.25f);
                inner.Blade(new Vector2(110, 42 + (i * 7)), new Vector2(136 + (i * 7), 22 + (i * 13)), 5, -0.25f);
            }
        }

        yield return Design("shape-wings", "Shapes", 200, 100, R(wings, 0xF0F0F0), R(inner, 0xB4B4B4));
        yield return Design("shape-ring", "Shapes", 100, 100, R(new PathBuilder().Circle(50, 50, 48).Circle(50, 50, 36), 0x141414, FillRule.EvenOdd));
        yield return Design("shape-star", "Shapes", 100, 100, R(new PathBuilder().Star(50, 53, 48, 19, 5, -90), 0xFFC81E));
        yield return Design("shape-diamond", "Shapes", 100, 100,
            R(new PathBuilder().Poly([new(50, 2), new(98, 50), new(50, 98), new(2, 50)]), 0x1EA0DC), R(new PathBuilder().Poly([new(50, 20), new(80, 50), new(50, 80), new(20, 50)]), 0xF0F0F0));
        yield return Design("shape-arrow", "Shapes", 200, 100, R(new PathBuilder().Poly([new(0, 35), new(130, 35), new(130, 8), new(198, 50), new(130, 92), new(130, 65), new(0, 65)]), 0xF0F0F0));
    }

    private static IEnumerable<DecalDesign> Logos()
    {
        PathBuilder Shield(float inset) => new PathBuilder().M(10 + inset, 8 + inset).L(90 - inset, 8 + inset).L(90 - inset, 46)
            .C(90 - inset, 72 - (inset / 2), 70, 88 - inset, 50, 96 - (inset * 1.4f)).C(30, 88 - inset, 10 + inset, 72 - (inset / 2), 10 + inset, 46).Z();
        yield return Design("logo-shield", "Logos", 100, 100, R(Shield(0), 0x283C64), R(Shield(7), 0xD2D2D2));
        yield return Design("logo-badge", "Logos", 100, 100,
            R(new PathBuilder().Poly([new(30, 60), new(20, 98), new(36, 88), new(46, 98), new(48, 64)]).Poly([new(70, 60), new(80, 98), new(64, 88), new(54, 98), new(52, 64)]), 0xB41E1E),
            R(new PathBuilder().Circle(50, 40, 38), 0xC8A032), R(new PathBuilder().Circle(50, 40, 29), 0xF0E6C8));
        yield return Design("logo-banner", "Logos", 200, 60,
            R(new PathBuilder().Poly([new(0, 10), new(200, 10), new(186, 30), new(200, 50), new(0, 50), new(14, 30)]), 0x8C1E1E),
            R(new PathBuilder().Rect(24, 16, 152, 28), 0xF0E6C8));
        var hex = Enumerable.Range(0, 6).Select(i => new Vector2(50 + (47 * MathF.Cos(i * MathF.PI / 3)), 50 + (47 * MathF.Sin(i * MathF.PI / 3)))).ToArray();
        yield return Design("logo-hexagon", "Logos", 100, 100,
            R(new PathBuilder().Poly(hex), 0x323232), R(new PathBuilder().Poly([.. hex.Select(p => new Vector2(50, 50) + ((p - new Vector2(50, 50)) * 0.8f))]), 0x9BA5AF));
    }

    /// <summary>A convex polygon clipped to [0, w] x [0, h] (Sutherland–Hodgman).</summary>
    private static Vector2[] Clip(Vector2[] polygon, float w, float h)
    {
        var points = polygon.ToList();
        foreach (var (axis, limit, keepBelow) in new[] { (0, 0f, false), (0, w, true), (1, 0f, false), (1, h, true) })
        {
            bool Inside(Vector2 p) => keepBelow ? p[axis] <= limit : p[axis] >= limit;
            var output = new List<Vector2>();
            for (var i = 0; i < points.Count; i++)
            {
                var (a, b) = (points[i], points[(i + 1) % points.Count]);
                if (Inside(a))
                {
                    output.Add(a);
                }

                if (Inside(a) != Inside(b))
                {
                    output.Add(Vector2.Lerp(a, b, (limit - a[axis]) / (b[axis] - a[axis])));
                }
            }

            points = output;
            if (points.Count == 0)
            {
                break;
            }
        }

        return [.. points];
    }

    /// <summary>
    /// Writes SVG path data. Every shape is wound the same way round, mirrored ones too (<see cref="T"/> with a negative
    /// determinant reverses them), so overlapping shapes of a region add up under the nonzero rule instead of cutting holes.
    /// </summary>
    private sealed class PathBuilder
    {
        private readonly StringBuilder _s = new();

        public Matrix3x2 T { get; set; } = Matrix3x2.Identity;

        private bool Mirrored => T.GetDeterminant() < 0;

        public PathBuilder M(float x, float y) => Cmd('M', new Vector2(x, y));

        public PathBuilder L(float x, float y) => Cmd('L', new Vector2(x, y));

        public PathBuilder C(float x1, float y1, float x2, float y2, float x, float y) => Cmd('C', new(x1, y1), new(x2, y2), new(x, y));

        public PathBuilder Z()
        {
            _s.Append("Z ");
            return this;
        }

        public PathBuilder Poly(Vector2[] points)
        {
            if (points.Length < 3)
            {
                return this;
            }

            var area = 0f;
            for (var i = 0; i < points.Length; i++)
            {
                var (a, b) = (points[i], points[(i + 1) % points.Length]);
                area += (a.X * b.Y) - (b.X * a.Y);
            }

            var ordered = (area < 0) != Mirrored ? points.Reverse().ToArray() : points;
            Cmd('M', ordered[0]);
            foreach (var p in ordered.Skip(1))
            {
                Cmd('L', p);
            }

            return Z();
        }

        public PathBuilder Rect(float x, float y, float w, float h) => Poly([new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)]);

        public PathBuilder Circle(float cx, float cy, float r)
        {
            const float K = 0.5523f;
            var (c, sign) = (new Vector2(cx, cy), Mirrored ? -1f : 1f);
            Vector2 P(float x, float y) => c + new Vector2(x * r, y * r * sign);
            Cmd('M', P(1, 0));
            Cmd('C', P(1, K), P(K, 1), P(0, 1));
            Cmd('C', P(-K, 1), P(-1, K), P(-1, 0));
            Cmd('C', P(-1, -K), P(-K, -1), P(0, -1));
            Cmd('C', P(K, -1), P(1, -K), P(1, 0));
            return Z();
        }

        public PathBuilder Star(float cx, float cy, float outer, float inner, int points, float rotationDeg)
        {
            var star = new Vector2[points * 2];
            for (var i = 0; i < star.Length; i++)
            {
                var a = (rotationDeg * MathF.PI / 180) + (i * MathF.PI / points);
                var r = i % 2 == 0 ? outer : inner;
                star[i] = new Vector2(cx + (r * MathF.Cos(a)), cy + (r * MathF.Sin(a)));
            }

            return Poly(star);
        }

        /// <summary>A curved tapering spike from a base <paramref name="width"/> wide to a sharp tip, bent by <paramref name="curl"/>.</summary>
        public PathBuilder Blade(Vector2 from, Vector2 tip, float width, float curl)
        {
            var d = tip - from;
            var n = Vector2.Normalize(new Vector2(-d.Y, d.X)) * d.Length();
            var (b1, b2) = (from + (Vector2.Normalize(n) * (width / 2)), from - (Vector2.Normalize(n) * (width / 2)));
            var outer = (b1 + (d * 0.35f) + (n * curl), tip - (d * 0.25f) + (n * curl * 0.6f));
            var inner = (tip - (d * 0.3f) + (n * curl * 0.3f), b2 + (d * 0.4f) + (n * curl * 0.5f));
            return Shape(b1, (outer.Item1, outer.Item2, tip), (inner.Item1, inner.Item2, b2));
        }

        /// <summary>A pointed lens (two arcs) centred on (cx, cy).</summary>
        public PathBuilder Lens(float cx, float cy, float halfWidth, float halfHeight) => Shape(new(cx, cy - halfHeight),
            (new(cx + (halfWidth * 1.3f), cy - (halfHeight * 0.5f)), new(cx + (halfWidth * 1.3f), cy + (halfHeight * 0.5f)), new(cx, cy + halfHeight)),
            (new(cx - (halfWidth * 1.3f), cy + (halfHeight * 0.5f)), new(cx - (halfWidth * 1.3f), cy - (halfHeight * 0.5f)), new(cx, cy - halfHeight)));

        /// <summary>A flame tongue from (0, y) along +x, its tip <paramref name="rise"/> above or below its root.</summary>
        public PathBuilder Tongue(Vector2 root, float length, float rise, float width)
        {
            var tip = root + new Vector2(length, rise);
            var top = root - new Vector2(0, width / 2);
            var bottom = root + new Vector2(0, width / 2);
            return Shape(top,
                (top + new Vector2(length * 0.45f, -width * 0.6f), tip - new Vector2(length * 0.35f, (rise * 0.2f) + (width * 0.7f)), tip),
                (tip - new Vector2(length * 0.4f, (rise * 0.3f) - (width * 0.2f)), bottom + new Vector2(length * 0.4f, width * 0.5f), bottom));
        }

        /// <summary>A smooth random blob (camouflage): eight points round <paramref name="centre"/> joined by curves.</summary>
        public PathBuilder Blob(Random rnd, Vector2 centre, float radius)
        {
            var pts = Enumerable.Range(0, 8).Select(i =>
            {
                var a = i * MathF.Tau / 8;
                var r = radius * (0.55f + (0.75f * (float)rnd.NextDouble()));
                return centre + new Vector2(MathF.Cos(a) * r * 1.6f, MathF.Sin(a) * r);
            }).ToArray();
            var segs = new (Vector2, Vector2, Vector2)[pts.Length];
            for (var i = 0; i < pts.Length; i++)
            {
                var (p0, p1, p2, p3) = (pts[(i + pts.Length - 1) % pts.Length], pts[i], pts[(i + 1) % pts.Length], pts[(i + 2) % pts.Length]);
                segs[i] = (p1 + ((p2 - p0) / 6), p2 - ((p3 - p1) / 6), p2); // Catmull-Rom as Bézier
            }

            return Shape(pts[0], segs);
        }

        /// <summary>A closed outline of cubic curves, written the other way round when it (or its mirror image) winds the wrong way.</summary>
        private PathBuilder Shape(Vector2 start, params (Vector2 C1, Vector2 C2, Vector2 End)[] curves)
        {
            var area = 0f;
            var from = start;
            foreach (var (c1, c2, end) in curves)
            {
                var mid = (from + (3 * c1) + (3 * c2) + end) / 8; // the curve's midpoint
                area += (from.X * mid.Y) - (mid.X * from.Y) + (mid.X * end.Y) - (end.X * mid.Y);
                from = end;
            }

            area += (from.X * start.Y) - (start.X * from.Y);
            if ((area < 0) == Mirrored)
            {
                Cmd('M', start);
                foreach (var (c1, c2, end) in curves)
                {
                    Cmd('C', c1, c2, end);
                }

                return Z();
            }

            Cmd('M', curves[^1].End);
            for (var i = curves.Length - 1; i >= 0; i--)
            {
                Cmd('C', curves[i].C2, curves[i].C1, i == 0 ? start : curves[i - 1].End);
            }

            return Z();
        }

        private PathBuilder Cmd(char command, params Vector2[] points)
        {
            _s.Append(command);
            foreach (var p in points)
            {
                var q = Vector2.Transform(p, T);
                _s.Append(' ').Append(q.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(q.Y.ToString("0.##", CultureInfo.InvariantCulture));
            }

            _s.Append(' ');
            return this;
        }

        public override string ToString() => _s.ToString().TrimEnd();
    }
}
