using System.Numerics;

namespace ScumStudio.Viewport.Decals;

/// <summary>
/// The library's designs (2026-10-17 redraw, owner: "primitive, digits like a clock; make them clean and smooth, and
/// more"): our own art from smooth curves (<see cref="Ink"/>), nothing traced from a brand, a font or an artist. Ids of the
/// first library are kept for the designs that replace them, so decals saved in a project still find their design.
/// </summary>
public static partial class DecalLibrary
{
    private const uint Black = 0x161616;
    private const uint White = 0xF2F2F2;
    private const uint Red = 0xC0221C;
    private const uint Orange = 0xF06A14;
    private const uint Yellow = 0xFFC41F;
    private const uint Olive = 0x4B5A32;
    private const uint Bone = 0xECE4D2;
    private const uint Gold = 0xD8A83A;
    private const uint Blue = 0x1F4C9A;

    internal static IReadOnlyList<DecalDesign> Build()
    {
        var list = new List<DecalDesign>();
        list.AddRange(Enumerable.Range(0, 6).Select(Tribal));
        list.Add(TribalSun());
        list.Add(TribalBand());
        list.AddRange(Enumerable.Range(0, 8).Select(Flames));
        list.AddRange(Stripes());
        list.AddRange(Enumerable.Range(0, 10).Select(RacingNumber));
        list.AddRange(Enumerable.Range(0, 10).Select(StencilNumber));
        list.AddRange(Enumerable.Range(0, 10).Select(BoldNumber));
        list.AddRange(Army());
        list.AddRange(Camo());
        list.AddRange(Anime());
        list.AddRange(Skulls());
        list.AddRange(Shapes());
        list.AddRange(Stars());
        list.AddRange(Logos());
        return list;
    }

    private static DecalDesign D(string id, string category, float w, float h, params (Ink Ink, uint Colour)[] regions) =>
        new(id, category, w, h, [.. regions.Select(r => new DecalRegion(r.Ink.ToString(), r.Colour))]);

    private static Vector2 V(float x, float y) => new(x, y);

    private static Vector2 Dir(float degrees) => new(MathF.Cos(degrees * MathF.PI / 180f), MathF.Sin(degrees * MathF.PI / 180f));

    private static float Rand(Random rnd, float from, float to) => from + ((float)rnd.NextDouble() * (to - from));

    // ---------------------------------------------------------------- Tribal

    /// <summary>A pointed, hooked blade along a Bézier spine: wide near its root, sharp at its tip.</summary>
    private static Vector2[] BladeSpine(Vector2 root, float angleDeg, float length, float bend)
    {
        var d = Dir(angleDeg);
        var n = new Vector2(-d.Y, d.X);
        var tip = root + (d * length) + (n * bend * 0.5f);
        return Curve.Bezier(root, root + (d * length * 0.35f) + (n * bend), tip - (d * length * 0.3f) + (n * bend * 1.5f), tip, 40);
    }

    private static Func<float, float> BladeWidth(float w) => t => w * MathF.Pow(1f - t, 0.85f) * (0.6f + (0.4f * MathF.Sin(MathF.PI * MathF.Min(1f, t * 1.8f))));

    private static DecalDesign Tribal(int variant)
    {
        var rnd = new Random(100 + variant);
        var (main, accent, dots) = (new Ink(), new Ink(), new Ink());
        var spikes = 3 + (variant % 3);
        for (var j = 0; j < spikes; j++)
        {
            var k = j - ((spikes - 1) / 2f);
            var root = V(88, 50 + (k * 9));
            var angle = 180f + (k * (64f / spikes)) + Rand(rnd, -6, 6);
            var length = Rand(rnd, 52, 80) - (MathF.Abs(k) * 6);
            var bend = (j % 2 == 0 ? 1 : -1) * Rand(rnd, 12, 22);
            var w = Rand(rnd, 8, 11);
            var spine = BladeSpine(root, angle, length, bend);
            var inner = spine[4..26];
            foreach (var mirrored in new[] { false, true })
            {
                var s = mirrored ? spine.Select(p => V(200 - p.X, p.Y)).ToArray() : spine;
                var i = mirrored ? inner.Select(p => V(200 - p.X, p.Y)).ToArray() : inner;
                main.Taper(s, BladeWidth(w));
                accent.Taper(i, t => w * 0.32f * MathF.Sin(MathF.PI * t));
                if (variant >= 3)
                {
                    var tip = s[^1];
                    dots.Circle(tip + (Vector2.Normalize(s[^1] - s[^8]) * 7f), 3f);
                }
            }
        }

        main.Fill(Curve.Spline([V(100, 14), V(112, 50), V(100, 86), V(88, 50)], 16));
        accent.Fill(Curve.Spline([V(100, 30), V(105, 50), V(100, 70), V(95, 50)], 12));
        var id = $"tribal-{variant + 1}";
        return variant >= 3
            ? D(id, "Tribal", 200, 100, (main, Black), (accent, Red), (dots, Black))
            : D(id, "Tribal", 200, 100, (main, Black), (accent, Red));
    }

    private static DecalDesign TribalSun()
    {
        var (blades, ring) = (new Ink(), new Ink());
        for (var i = 0; i < 12; i++)
        {
            var a = i * 30f;
            var root = V(50, 50) + (Dir(a) * 22f);
            blades.Taper(BladeSpine(root, a + 12f, 27f, 9f), BladeWidth(6.5f));
        }

        ring.Ring(V(50, 50), 13f, 21f).Circle(V(50, 50), 7f);
        return D("tribal-7", "Tribal", 100, 100, (blades, Black), (ring, Red));
    }

    private static DecalDesign TribalBand()
    {
        var (hooks, line) = (new Ink(), new Ink());
        for (var i = 0; i < 6; i++)
        {
            var x = 26f + (i * 50f);
            var up = i % 2 == 0;
            hooks.Taper(BladeSpine(V(x, 50), up ? -42f : 42f, 58f, up ? 18f : -18f), BladeWidth(12f));
            hooks.Taper(BladeSpine(V(x + 6, 50), up ? 150f : -150f, 34f, up ? -12f : 12f), BladeWidth(8f));
        }

        line.Stroke(Curve.Spline([V(4, 50), V(80, 46), V(160, 54), V(240, 46), V(296, 50)], 16, closed: false), 9f);
        return D("tribal-8", "Tribal", 300, 100, (hooks, Black), (line, Red));
    }

    // ---------------------------------------------------------------- Flames

    /// <summary>One flame tongue from off the left edge to its tip: an S-shaped spine, tapering to a point.</summary>
    private static void Tongue(Ink ink, float y, float length, float rise, float hw)
    {
        var spine = Curve.Bezier(V(-12, y), V(length * 0.36f, y + (rise * 0.1f) + (hw * 0.5f)), V(length * 0.62f, y + (rise * 0.95f) - (hw * 0.3f)), V(length, y + rise), 48);
        ink.Taper(spine, t => hw * MathF.Pow(1f - t, 0.7f) * (1f + (0.18f * MathF.Sin(t * MathF.PI * 2.4f))));
    }

    private static DecalDesign Flames(int variant)
    {
        var rnd = new Random(200 + variant);
        var count = 4 + (variant % 4);
        var (rim, outer, inner) = (new Ink(), new Ink(), new Ink());
        for (var i = 0; i < count; i++)
        {
            var y = 16f + (68f * (i + 0.5f) / count) + Rand(rnd, -3, 3);
            var length = 132f + (88f * MathF.Sin(MathF.PI * (i + 0.5f) / count)) + Rand(rnd, -16, 16);
            var rise = Rand(rnd, -22, 6) - (variant >= 4 ? 8 : 0);
            var hw = (54f / count) + Rand(rnd, 0, 3);
            Tongue(rim, y, length + 6, rise, hw + 3.2f);
            Tongue(outer, y, length, rise, hw);
            Tongue(inner, y, length * 0.6f, rise * 0.6f, hw * 0.5f);
        }

        var colours = variant switch
        {
            1 => (Black, 0x2A7BE0u, 0x9AD8FFu), // blue flames
            3 => (Black, 0x7A1FC0u, 0xE07AFFu), // purple
            5 => (White, Black, 0x5A5A5Au), // ghost flames
            _ => (Black, Orange, Yellow),
        };
        return D($"flames-{variant + 1}", "Flames", 240, 100, (rim, colours.Item1), (outer, colours.Item2), (inner, colours.Item3));
    }

    // ---------------------------------------------------------------- Stripes

    private static IEnumerable<DecalDesign> Stripes()
    {
        var bars = new Ink().RoundRect(0, 16, 300, 26, 0).RoundRect(0, 58, 300, 26, 0);
        var pins = new Ink().RoundRect(0, 8, 300, 4, 0).RoundRect(0, 48, 300, 4, 0).RoundRect(0, 88, 300, 4, 0);
        yield return D("stripes-racing", "Stripes", 300, 100, (bars, White), (pins, Red));

        // A pinstriper's flourish: mirrored sweeps that end in curls.
        var flourish = new Ink();
        foreach (var side in new[] { -1f, 1f })
        {
            Vector2 M(float x, float y) => V(100 + (side * x), y);
            flourish.Stroke(Curve.Join(Curve.Bezier(M(4, 50), M(30, 20), M(60, 80), M(84, 46)), Spiral(M(84, 46), side, 10f)), 2.6f);
            flourish.Stroke(Curve.Bezier(M(10, 50), M(32, 70), M(54, 30), M(70, 58)), 2.2f);
            flourish.Stroke(Curve.Join(Curve.Bezier(M(2, 40), M(14, 22), M(36, 18), M(48, 28)), Spiral(M(48, 28), -side, 6f)), 2f);
        }

        flourish.Ellipse(V(100, 50), 3.5f, 6f);
        yield return D("stripes-pinstripe", "Stripes", 200, 100, (flourish, White));

        var plate = new Ink().RoundRect(0, 0, 240, 60, 8);
        var bands = new Ink();
        for (var x = -60f; x < 260f; x += 28f)
        {
            bands.Fill(Curve.ClipToBox([V(x, 56), V(x + 14, 56), V(x + 48, 4), V(x + 34, 4)], 240, 60));
        }

        yield return D("stripes-hazard", "Stripes", 240, 60, (plate, Yellow), (bands, Black));

        var flag = new Ink();
        var squares = new Ink();
        Vector2 Wave(float x, float y) => V(x, y + (7f * MathF.Sin((x / 300f * MathF.Tau) + (y / 100f))));
        flag.Fill(Curve.Join(
            Enumerable.Range(0, 61).Select(i => Wave(i * 5f, 6)).ToArray(),
            Enumerable.Range(0, 61).Select(i => Wave(300 - (i * 5f), 94)).ToArray()));
        for (var cx = 0; cx < 12; cx++)
        {
            for (var cy = 0; cy < 4; cy++)
            {
                if ((cx + cy) % 2 == 0)
                {
                    var (x0, y0) = (cx * 25f, 6f + (cy * 22f));
                    squares.Fill(Curve.Join(
                        Enumerable.Range(0, 6).Select(i => Wave(x0 + (i * 5f), y0)).ToArray(),
                        Enumerable.Range(0, 6).Select(i => Wave(x0 + 25f - (i * 5f), y0 + 22f)).ToArray()));
                }
            }
        }

        yield return D("stripes-checker", "Stripes", 300, 100, (flag, White), (squares, Black));

        var swoosh = new Ink()
            .Taper(Curve.Bezier(V(6, 80), V(90, 96), V(200, 70), V(296, 12), 60), t => 13f * MathF.Pow(MathF.Sin(MathF.PI * t), 0.7f))
            .Taper(Curve.Bezier(V(40, 92), V(120, 100), V(220, 80), V(290, 40), 60), t => 5f * MathF.Pow(MathF.Sin(MathF.PI * t), 0.7f));
        yield return D("stripes-swoosh", "Stripes", 300, 100, (swoosh, Red));

        var speed = new Ink();
        for (var i = 0; i < 5; i++)
        {
            var y = 14f + (i * 18f);
            var length = 300f - (MathF.Abs(i - 2) * 50f);
            var hw = 6.5f - (MathF.Abs(i - 2) * 1.2f);
            speed.Taper([V(300 - length, y), V(300, y)], t => hw * MathF.Sqrt(1f - t)).Circle(V(300 - length, y), hw);
        }

        yield return D("stripes-speed", "Stripes", 300, 100, (speed, Black));

        var rally = new Ink().RoundRect(0, 32, 300, 36, 0);
        var rallyPins = new Ink().RoundRect(0, 22, 300, 5, 0).RoundRect(0, 73, 300, 5, 0);
        yield return D("stripes-rally", "Stripes", 300, 100, (rally, Blue), (rallyPins, White));

        var spearRim = new Ink().Taper([V(2, 40), V(270, 40)], t => 2.5f + (19f * MathF.Pow(t, 1.6f))).Fill([V(262, 14), V(298, 40), V(262, 66)]);
        var spear = new Ink().Taper([V(10, 40), V(266, 40)], t => 0.5f + (15f * MathF.Pow(t, 1.6f))).Fill([V(266, 22), V(290, 40), V(266, 58)]);
        yield return D("stripes-spear", "Stripes", 300, 80, (spearRim, Black), (spear, Orange));

        var dots = new Ink();
        for (var x = 0; x < 30; x++)
        {
            for (var y = 0; y < 10; y++)
            {
                var r = 4.6f * (1f - (x / 30f));
                if (r > 0.6f)
                {
                    dots.Circle(V(5 + (x * 10f) + (y % 2 * 5f), 5 + (y * 10f)), r);
                }
            }
        }

        yield return D("stripes-fade", "Stripes", 300, 100, (dots, Black));

        var stick = Curve.Join(new[] { V(4, 30), V(200, 30) }, Curve.Bezier(V(200, 30), V(250, 30), V(276, 50), V(286, 92), 30));
        var hockeyRim = new Ink().Stroke(stick, 26f, round: false);
        var hockey = new Ink().Stroke(stick, 17f, round: false);
        yield return D("stripes-hockey", "Stripes", 300, 100, (hockeyRim, Black), (hockey, Yellow));
    }

    /// <summary>A curl that winds in from <paramref name="start"/> (a pinstripe's end).</summary>
    private static Vector2[] Spiral(Vector2 start, float side, float radius)
    {
        var centre = start + V(0, -radius);
        return [.. Enumerable.Range(0, 49).Select(i =>
        {
            var t = i / 48f;
            var a = (90f - (side * t * 400f)) * MathF.PI / 180f;
            var r = radius * (1f - (0.75f * t));
            return centre + V(MathF.Cos(a) * r * side, MathF.Sin(a) * r);
        })];
    }

    // ---------------------------------------------------------------- Numbers

    /// <summary>
    /// The centre lines of a digit drawn with a broad pen in a 60 x 100 box (y down): our own geometric racing face, each
    /// stroke a smooth curve or straight line; closed ones are rings.
    /// </summary>
    internal static (Vector2[] Line, bool Closed)[] DigitStrokes(int digit) => digit switch
    {
        0 => [(Curve.Ellipse(V(30, 50), 21.5f, 41.5f), true)],
        1 => [([V(14, 25), V(33, 8.5f), V(33, 91.5f)], false)],
        2 => [(Curve.Join(Curve.Bezier(V(9, 30), V(9, 3), V(51, 3), V(51, 28)), Curve.Bezier(V(51, 28), V(51, 48), V(13, 63), V(9, 91.5f)), [V(52, 91.5f)]), false)],
        3 => [(Curve.Join(Curve.Bezier(V(8.5f, 24), V(13, 4), V(50, 3), V(50, 26)), Curve.Bezier(V(50, 26), V(50, 44), V(37, 47.5f), V(25, 47.5f))), false),
              (Curve.Join(Curve.Bezier(V(25, 47.5f), V(42, 47.5f), V(52.5f, 56), V(52.5f, 70)), Curve.Bezier(V(52.5f, 70), V(52.5f, 97), V(11, 98), V(8, 77))), false)],
        4 => [([V(41, 91.5f), V(41, 8.5f), V(8, 65), V(53, 65)], false)],
        5 => [(Curve.Join([V(50, 8.5f), V(17, 8.5f), V(14, 45)], Curve.Bezier(V(14, 45), V(24, 38), V(52.5f, 37), V(52.5f, 64)), Curve.Bezier(V(52.5f, 64), V(52.5f, 97), V(13, 98), V(8, 79))), false)],
        6 => [(Curve.Ellipse(V(31, 67), 21.5f, 24.5f), true), (Curve.Bezier(V(49, 13), V(36, 2), V(9.5f, 10), V(9.5f, 64)), false)],
        7 => [([V(8, 8.5f), V(52, 8.5f), V(23, 91.5f)], false)],
        8 => [(Curve.Ellipse(V(30, 28), 18.5f, 19.5f), true), (Curve.Ellipse(V(30, 70), 22f, 21.5f), true)],
        9 => [.. DigitStrokes(6).Select(s => (s.Line.Select(p => V(60 - p.X, 100 - p.Y)).ToArray(), s.Closed))],
        _ => throw new ArgumentOutOfRangeException(nameof(digit)),
    };

    /// <summary>The digit as a filled outline <paramref name="width"/> wide, through <paramref name="place"/> (60 x 100 box in).</summary>
    private static Ink Digit(int digit, float width, Matrix3x2 place, Func<Vector2[], bool, IEnumerable<Vector2[]>>? cut = null, bool round = true)
    {
        var ink = new Ink { T = place };
        foreach (var (line, closed) in DigitStrokes(digit))
        {
            foreach (var piece in cut is null ? [line] : cut(line, closed))
            {
                if (closed && cut is null)
                {
                    ink.StrokeClosed(piece, width);
                }
                else
                {
                    ink.Stroke(piece, width, round);
                }
            }
        }

        return ink;
    }

    /// <summary>Slants a 60 x 100 glyph like a racing number and centres it at <paramref name="centre"/>, <paramref name="scale"/> times as big.</summary>
    private static Matrix3x2 Italic(Vector2 centre, float scale) =>
        Matrix3x2.CreateTranslation(-30, -50) * new Matrix3x2(1, 0, -0.17f, 1, 0, 0) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(centre);

    private static DecalDesign RacingNumber(int d)
    {
        var disc = new Ink().Circle(V(50, 50), 47);
        var ring = new Ink().Ring(V(50, 50), 42, 48);
        return D($"number-racing-{d}", "Numbers", 100, 100, (disc, White), (ring, Black), (Digit(d, 14, Italic(V(50, 50), 0.66f)), Black));
    }

    private static DecalDesign BoldNumber(int d)
    {
        var place = Italic(V(45, 58), 1f);
        return D($"number-bold-{d}", "Numbers", 90, 116, (Digit(d, 24, place), Black), (Digit(d, 14, place), Yellow));
    }

    private static DecalDesign StencilNumber(int d)
    {
        // A stencil's bridges: the strokes cut across the middle (a vertical cut, or a level one for 1, 4 and 7).
        var vertical = d is not (1 or 4 or 7);
        IEnumerable<Vector2[]> Cut(Vector2[] line, bool closed)
        {
            bool Out(Vector2 p) => vertical ? MathF.Abs(p.X - 30f) > 3.6f : MathF.Abs(p.Y - (d == 4 ? 80f : 50f)) > 3.6f;
            var pts = line.ToList();
            if (closed)
            {
                var start = pts.FindIndex(Out);
                pts = [.. pts.Skip(start), .. pts.Take(start + 1)];
            }

            var piece = new List<Vector2>();
            foreach (var p in pts)
            {
                if (Out(p))
                {
                    piece.Add(p);
                }
                else if (piece.Count > 0)
                {
                    yield return [.. piece];
                    piece = [];
                }
            }

            if (piece.Count > 1)
            {
                yield return [.. piece];
            }
        }

        var place = Matrix3x2.CreateTranslation(-30, -50) * Matrix3x2.CreateScale(0.7f) * Matrix3x2.CreateTranslation(50, 50);
        var frame = new Ink().RoundRect(3, 3, 94, 94, 10).Hole(Curve.RoundRect(9, 9, 82, 82, 6));
        return D($"number-stencil-{d}", "Numbers", 100, 100, (Digit(d, 14, place, Cut, round: false), White), (frame, White));
    }

    // ---------------------------------------------------------------- Army and camo

    private static IEnumerable<DecalDesign> Army()
    {
        yield return D("army-star", "Army", 100, 100, (new Ink().Circle(V(50, 50), 46), Olive), (new Ink().Star(V(50, 52), 37, 14.5f, 5, -90, 1.5f), White));
        yield return D("army-star-ring", "Army", 100, 100, (new Ink().Ring(V(50, 50), 40, 47), White), (new Ink().Star(V(50, 52), 33, 13, 5, -90, 1.2f), White));
        var chevrons = new Ink();
        for (var i = 0; i < 3; i++)
        {
            var y = 12f + (i * 25f);
            chevrons.Stroke([V(14, y + 26), V(50, y + 4), V(86, y + 26)], 13f);
        }

        yield return D("army-chevrons", "Army", 100, 100, (chevrons, Gold));
        yield return D("army-roundel", "Army", 100, 100, (new Ink().Circle(V(50, 50), 47), Blue), (new Ink().Circle(V(50, 50), 31), White), (new Ink().Circle(V(50, 50), 15), Red));
        yield return BlobCamo("army-camo-woodland", "Army", 300, [Olive, 0x2D2D1E, 0x8C7850, 0x1E1E14], 16);
        yield return BlobCamo("army-camo-desert", "Army", 301, [0xC8AF82, 0x9C8460, 0xE1CDA5, 0x6E5A3C], 12);
        var rnd = new Random(302);
        var (bg, a, b) = (new Ink().RoundRect(0, 0, 200, 100, 0), new Ink(), new Ink());
        for (var i = 0; i < 90; i++)
        {
            var (x, y) = (rnd.Next(0, 40) * 5f, rnd.Next(0, 20) * 5f);
            var (w, h) = (rnd.Next(1, 5) * 5f, rnd.Next(1, 3) * 5f);
            (i % 2 == 0 ? a : b).RoundRect(x, y, MathF.Min(w, 200 - x), MathF.Min(h, 100 - y), 0);
        }

        yield return D("army-camo-digital", "Army", 200, 100, (bg, 0x5A6446), (a, 0x3C4130), (b, 0x8C9173));
    }

    /// <summary>A smooth blob: a closed spline through points round a centre at random radii.</summary>
    private static Vector2[] Blob(Random rnd, Vector2 centre, float radius, float stretch = 1.6f)
    {
        var n = 7 + rnd.Next(4);
        var turn = Rand(rnd, 0, 360);
        return Curve.Spline([.. Enumerable.Range(0, n).Select(i =>
        {
            var a = (turn + (i * 360f / n)) * MathF.PI / 180f;
            var r = radius * Rand(rnd, 0.55f, 1.15f);
            return centre + V(MathF.Cos(a) * r * stretch, MathF.Sin(a) * r);
        })], 10);
    }

    private static DecalDesign BlobCamo(string id, string category, int seed, uint[] colours, int blobs)
    {
        var rnd = new Random(seed);
        var layers = new[] { new Ink(), new Ink(), new Ink() };
        for (var i = 0; i < blobs * 3; i++)
        {
            layers[i % 3].Fill(Blob(rnd, V(Rand(rnd, -10, 210), Rand(rnd, -5, 105)), Rand(rnd, 7, 15)));
        }

        return D(id, category, 200, 100, (new Ink().RoundRect(0, 0, 200, 100, 0), colours[0]), (layers[0], colours[1]), (layers[1], colours[2]), (layers[2], colours[3]));
    }

    private static IEnumerable<DecalDesign> Camo()
    {
        var rnd = new Random(310);
        var tiger = new Ink();
        for (var i = 0; i < 26; i++)
        {
            var x = Rand(rnd, -10, 210);
            var top = Rand(rnd, -10, 30);
            tiger.Taper(Curve.Bezier(V(x, top), V(x + Rand(rnd, -14, 14), top + 25), V(x + Rand(rnd, -18, 18), top + 50), V(x + Rand(rnd, -10, 10), top + Rand(rnd, 50, 90)), 30),
                t => 4.5f * MathF.Sin(MathF.PI * t));
        }

        yield return D("camo-tiger", "Camo", 200, 100, (new Ink().RoundRect(0, 0, 200, 100, 0), 0x7A6A3C), (tiger, Black));
        yield return BlobCamo("camo-urban", "Camo", 311, [0x8A8A86, 0x4A4A48, 0xC4C4BE, 0x1E1E1E], 14);
        yield return BlobCamo("camo-arctic", "Camo", 312, [0xEEF0F2, 0xB8C0C8, 0x8A949E, 0xFFFFFF], 12);
        var splinter = new[] { new Ink(), new Ink(), new Ink() };
        var rs = new Random(313);
        for (var i = 0; i < 30; i++)
        {
            var c = V(Rand(rs, 0, 200), Rand(rs, 0, 100));
            var angle = Rand(rs, -20, 20);
            var (l, w) = (Rand(rs, 20, 46), Rand(rs, 7, 14));
            var d = Dir(angle);
            var n = new Vector2(-d.Y, d.X);
            splinter[i % 3].Fill([c - (d * l), c + (n * w), c + (d * l * 0.8f), c - (n * w * 0.6f)]);
        }

        yield return D("camo-splinter", "Camo", 200, 100, (new Ink().RoundRect(0, 0, 200, 100, 0), 0x9A9468), (splinter[0], 0x5E6440), (splinter[1], 0x7A4E30), (splinter[2], 0x3C3C2A));
    }

    // ---------------------------------------------------------------- Anime

    private static IEnumerable<DecalDesign> Anime()
    {
        var lines = new Ink();
        for (var i = 0; i < 32; i++)
        {
            var a = (i * 360f / 32) + (i % 2 * 4f);
            var inner = 34f + (10f * (i % 3));
            var c = V(100, 50);
            var spine = new[] { c + (Dir(a) * inner), c + (Dir(a) * 160f) };
            var tapered = new Ink().Taper(spine, t => 0.4f + (6f * t));
            foreach (var p in tapered.Polygons)
            {
                lines.Fill(Curve.ClipToBox(p, 200, 100));
            }
        }

        yield return D("anime-speed-lines", "Anime", 200, 100, (lines, Black));

        var (sun, rays) = (new Ink().Circle(V(100, 100), 34), new Ink());
        for (var i = 0; i < 9; i++)
        {
            var a = 180f + 10f + (i * 20f);
            rays.Taper([V(100, 100) + (Dir(a) * 40f), V(100, 100) + (Dir(a) * 96f)], t => 2f + (10f * t));
        }

        yield return D("anime-sun-rays", "Anime", 200, 100, (rays, Red), (sun, Red));

        var bolt = new Ink().Fill(Curve.Rounded([V(62, 2), V(26, 54), V(48, 54), V(34, 98), V(76, 38), V(54, 38), V(70, 2)], 2.5f));
        var boltShine = new Ink().Fill(Curve.Rounded([V(60, 8), V(34, 50), V(42, 50)], 1.5f));
        yield return D("anime-lightning", "Anime", 100, 100, (bolt, Yellow), (boltShine, White));

        // Seigaiha: rows of overlapping wave rings.
        var (waves, crests) = (new Ink(), new Ink());
        for (var row = 0; row < 4; row++)
        {
            for (var col = -1; col < 9; col++)
            {
                var c = V((col * 28f) + (row % 2 * 14f), 34f + (row * 16f));
                foreach (var (r, ink) in new[] { (17f, waves), (12f, crests), (7f, waves) })
                {
                    var arc = Curve.Arc(c, r, r, 180f, 360f, 32);
                    foreach (var p in new Ink().Stroke(arc, 2.6f).Polygons)
                    {
                        ink.Fill(Curve.ClipToBox(p, 200, 100));
                    }
                }
            }
        }

        yield return D("anime-waves", "Anime", 200, 100, (waves, Blue), (crests, 0x6FA8DC));

        var (petals, centre) = (new Ink(), new Ink());
        for (var i = 0; i < 5; i++)
        {
            var turn = Matrix3x2.CreateRotation(i * MathF.Tau / 5f) * Matrix3x2.CreateTranslation(50, 50);
            petals.T = turn;
            petals.Fill(Curve.Join(Curve.Bezier(V(0, 0), V(-20, -10), V(-16, -38), V(-5, -44)), [V(0, -38)], Curve.Bezier(V(5, -44), V(16, -38), V(20, -10), V(0, 0))));
        }

        centre.Circle(V(50, 50), 6);
        for (var i = 0; i < 5; i++)
        {
            centre.Circle(V(50, 50) + (Dir(-90 + (i * 72) + 36) * 11f), 2.2f);
        }

        yield return D("anime-sakura", "Anime", 100, 100, (petals, 0xF6A6C1), (centre, 0xC2185B));
        var burst = new Ink().Star(V(50, 50), 48, 30, 14, -90, 2f);
        var core = new Ink().Star(V(50, 50), 32, 20, 14, -77, 1.5f);
        yield return D("anime-burst", "Anime", 100, 100, (burst, Red), (core, Yellow));
    }

    // ---------------------------------------------------------------- Skulls

    /// <summary>A skull in a 100 x 100 box: (bone, its eyes, nose and teeth).</summary>
    private static (Ink Bone, Ink Features) Skull(Matrix3x2 place)
    {
        var bone = new Ink { T = place };
        bone.Fill(Curve.Spline([V(50, 10), V(70, 13), V(82, 26), V(85, 44), V(80, 58), V(71, 64), V(70, 76), V(63, 86), V(50, 88), V(37, 86), V(30, 76), V(29, 64), V(20, 58), V(15, 44), V(18, 26), V(30, 13)], 10));
        var features = new Ink { T = place };
        var eye = Curve.Spline([V(26, 47), V(32, 40), V(42, 41), V(45.5f, 48), V(41, 56), V(32, 57)], 10);
        features.Fill(eye).Fill(Curve.MirrorX(eye, 50));
        features.Fill(Curve.Spline([V(50, 58), V(54.5f, 66), V(52, 70.5f), V(48, 70.5f), V(45.5f, 66)], 8));
        for (var x = 38f; x <= 62f; x += 6f)
        {
            features.RoundRect(x - 1.2f, 74, 2.4f, 10.5f, 1.2f);
        }

        return (bone, features);
    }

    private static IEnumerable<DecalDesign> Skulls()
    {
        var (bone, features) = Skull(Matrix3x2.Identity);
        yield return D("skull-plain", "Skulls", 100, 100, (bone, Bone), (features, Black));

        var crossed = new Ink();
        foreach (var (a, b) in new[] { (V(12, 90), V(88, 18)), (V(88, 90), V(12, 18)) })
        {
            crossed.Stroke([a, b], 9f);
            var n = Vector2.Normalize(new Vector2(b.Y - a.Y, a.X - b.X)) * 5f;
            crossed.Circle(a + n, 6.5f).Circle(a - n, 6.5f).Circle(b + n, 6.5f).Circle(b - n, 6.5f);
        }

        var small = Matrix3x2.CreateTranslation(-50, -50) * Matrix3x2.CreateScale(0.74f) * Matrix3x2.CreateTranslation(50, 46);
        var (b2, f2) = Skull(small);
        yield return D("skull-crossbones", "Skulls", 100, 100, (crossed, Bone), (b2, Bone), (f2, Black));

        var diamond = new Ink().Fill(Curve.Rounded([V(50, 2), V(98, 50), V(50, 98), V(2, 50)], 6f));
        var (b3, f3) = Skull(Matrix3x2.CreateTranslation(-50, -50) * Matrix3x2.CreateScale(0.6f) * Matrix3x2.CreateTranslation(50, 50));
        yield return D("skull-diamond", "Skulls", 100, 100, (diamond, Black), (b3, Bone), (f3, Black));

        var horns = new Ink();
        foreach (var side in new[] { 1f, -1f })
        {
            Vector2 M(float x, float y) => V(50 + (side * (x - 50)), y);
            horns.Taper(Curve.Bezier(M(30, 34), M(10, 36), M(4, 18), M(18, 3), 40), t => 7.5f * MathF.Pow(1f - t, 0.9f));
        }

        var (b4, f4) = Skull(Matrix3x2.CreateTranslation(-50, -50) * Matrix3x2.CreateScale(0.8f) * Matrix3x2.CreateTranslation(50, 58));
        yield return D("skull-horns", "Skulls", 100, 100, (horns, Red), (b4, Bone), (f4, Black));

        var wings = new Ink();
        foreach (var side in new[] { 1f, -1f })
        {
            for (var i = 0; i < 6; i++)
            {
                var root = V(100 - (side * 30f), 40f + (i * 5f));
                var angle = side > 0 ? 180f + 20f - (i * 13f) : -20f + (i * 13f);
                wings.Taper(BladeSpine(root, angle, 66f - (i * 7f), side * -6f), t => 7f * MathF.Pow(1f - t, 0.6f) * MathF.Min(1f, 0.4f + (t * 4f)));
            }
        }

        var (b5, f5) = Skull(Matrix3x2.CreateTranslation(-50, -50) * Matrix3x2.CreateScale(0.8f) * Matrix3x2.CreateTranslation(100, 50));
        yield return D("skull-wings", "Skulls", 200, 100, (wings, White), (b5, Bone), (f5, Black));

        var (fire, hot) = (new Ink(), new Ink());
        var rnd = new Random(400);
        for (var i = 0; i < 5; i++)
        {
            var y = 18f + (i * 16f);
            var length = 200f - (MathF.Abs(i - 2) * 26f) + Rand(rnd, -8, 8);
            Tongue(fire, y, length, Rand(rnd, -18, 0), 10f);
            Tongue(hot, y, length * 0.62f, -6f, 5f);
        }

        var (b6, f6) = Skull(Matrix3x2.CreateTranslation(-50, -50) * Matrix3x2.CreateScale(0.92f) * Matrix3x2.CreateTranslation(52, 50));
        yield return D("skull-flames", "Skulls", 220, 100, (fire, Orange), (hot, Yellow), (b6, Bone), (f6, Black));
    }

    // ---------------------------------------------------------------- Shapes and stars

    private static Vector2[] Heart(Vector2 centre, float size)
    {
        var half = Curve.Join(Curve.Bezier(V(0, -0.22f), V(-0.12f, -0.48f), V(-0.48f, -0.42f), V(-0.46f, -0.12f), 24), Curve.Bezier(V(-0.46f, -0.12f), V(-0.44f, 0.12f), V(-0.12f, 0.3f), V(0, 0.46f), 24));
        return [.. half.Concat(Curve.MirrorX(half, 0).Skip(1)).Select(p => centre + (p * size))];
    }

    private static Ink Wing(Ink ink, Vector2 root, float side, float scale)
    {
        for (var i = 0; i < 6; i++)
        {
            var angle = side > 0 ? -18f + (i * 14f) : 198f - (i * 14f);
            ink.Taper(BladeSpine(root + V(0, i * 3f * scale), angle, (78f - (i * 9f)) * scale, side * 5f * scale), t => 7.5f * scale * MathF.Pow(1f - t, 0.6f) * MathF.Min(1f, 0.45f + (t * 4f)));
        }

        return ink;
    }

    private static IEnumerable<DecalDesign> Shapes()
    {
        yield return D("shape-heart", "Shapes", 100, 100, (new Ink().Fill(Heart(V(50, 52), 100)), Red), (new Ink().Fill(Curve.Spline([V(30, 26), V(38, 22), V(36, 32), V(28, 38)], 8)), 0xFF8A8A));
        yield return D("shape-wings", "Shapes", 200, 80, (Wing(Wing(new Ink(), V(108, 28), 1f, 1f), V(92, 28), -1f, 1f), White));
        yield return D("shape-ring", "Shapes", 100, 100, (new Ink().Ring(V(50, 50), 34, 46), Black));
        yield return D("shape-star", "Shapes", 100, 100, (new Ink().Star(V(50, 53), 47, 19, 5, -90, 2.5f), Yellow));
        var gem = new Ink().Fill(Curve.Rounded([V(26, 14), V(74, 14), V(96, 38), V(50, 94), V(4, 38)], 3f));
        var facets = new Ink().Fill([V(30, 18), V(50, 18), V(40, 36)]).Fill([V(9, 38), V(36, 38), V(48, 82)]);
        yield return D("shape-diamond", "Shapes", 100, 100, (gem, 0x2BA3D8), (facets, 0xBFEFFF));
        yield return D("shape-arrow", "Shapes", 200, 80, (new Ink().Fill(Curve.Rounded([V(4, 26), V(130, 26), V(130, 6), V(196, 40), V(130, 74), V(130, 54), V(4, 54)], 4f)), Red));
    }

    private static IEnumerable<DecalDesign> Stars()
    {
        var (light, dark) = (new Ink(), new Ink());
        var c = V(50, 53);
        for (var i = 0; i < 5; i++)
        {
            var a = -90f + (i * 72f);
            var tip = c + (Dir(a) * 47f);
            light.Fill([c, tip, c + (Dir(a + 36f) * 19f)]);
            dark.Fill([c, c + (Dir(a - 36f) * 19f), tip]);
        }

        yield return D("star-nautical", "Stars", 100, 100, (light, White), (dark, Red));

        var tails = new Ink();
        for (var i = 0; i < 3; i++)
        {
            tails.Taper(Curve.Bezier(V(140, 40 + (i * 8f)), V(100, 46 + (i * 12f)), V(50, 70 + (i * 6f)), V(6, 64 + (i * 12f)), 40), t => (6f - (i * 1.5f)) * (1f - t));
        }

        yield return D("star-shooting", "Stars", 200, 100, (tails, Orange), (new Ink().Star(V(150, 38), 32, 13, 5, -80, 2f), Yellow));
        yield return D("star-burst", "Stars", 100, 100, (new Ink().Star(V(50, 50), 48, 26, 12, -90, 2.5f), Gold), (new Ink().Circle(V(50, 50), 15), White));
        var cluster = new Ink().Star(V(60, 52), 40, 16, 5, -90, 2f).Star(V(140, 34), 24, 9.5f, 5, -80, 1.2f).Star(V(166, 74), 16, 6.5f, 5, -100, 1f).Star(V(118, 82), 11, 4.5f, 5, -90, 0.8f);
        yield return D("star-cluster", "Stars", 200, 100, (cluster, White));
        var sheriff = new Ink().Star(V(50, 50), 40, 22, 6, -90, 1f);
        for (var i = 0; i < 6; i++)
        {
            sheriff.Circle(V(50, 50) + (Dir(-90 + (i * 60)) * 42f), 6f);
        }

        yield return D("star-sheriff", "Stars", 100, 100, (sheriff, Gold), (new Ink().Ring(V(50, 50), 12, 16), 0x7A5A12));
    }

    // ---------------------------------------------------------------- Logos (our own emblems)

    private static Vector2[] ShieldOutline(float x, float y, float w, float h)
    {
        var half = Curve.Join([V(x + (w / 2), y), V(x, y)], Curve.Bezier(V(x, y), V(x, y + (h * 0.55f)), V(x + (w * 0.18f), y + (h * 0.82f)), V(x + (w / 2), y + h), 30));
        return [.. half.Concat(Curve.MirrorX(half, x + (w / 2)).Skip(1).SkipLast(1))];
    }

    private static IEnumerable<DecalDesign> Logos()
    {
        var shield = new Ink().Fill(Curve.Rounded(ShieldOutline(6, 4, 88, 100), 4f));
        var inner = new Ink().Fill(Curve.Rounded(ShieldOutline(14, 11, 72, 84), 3f));
        var chevron = new Ink().Stroke([V(24, 60), V(50, 40), V(76, 60)], 11f);
        yield return D("logo-shield", "Logos", 100, 110, (shield, Black), (inner, Red), (chevron, White));

        var badge = new Ink().Star(V(50, 50), 48, 43, 24, -90, 1.6f);
        yield return D("logo-badge", "Logos", 100, 100, (badge, Gold), (new Ink().Circle(V(50, 50), 36), Black), (new Ink().Star(V(50, 52), 26, 10.5f, 5, -90, 1.2f), Gold));

        var ends = new Ink().Fill([V(2, 26), V(40, 26), V(40, 66), V(2, 66), V(14, 46)]).Fill([V(238, 26), V(200, 26), V(200, 66), V(238, 66), V(226, 46)]);
        var band = new Ink().Fill(Curve.Join(Curve.Bezier(V(26, 14), V(90, 4), V(150, 4), V(214, 14), 30), Curve.Bezier(V(214, 56), V(150, 46), V(90, 46), V(26, 56), 30)));
        yield return D("logo-banner", "Logos", 240, 80, (ends, 0x8E1A16), (band, Red));

        var hex = new Ink().Fill(Curve.Star(V(50, 50), 48, 48, 3, -90, 6f));
        var hexRing = new Ink().Fill(Curve.Star(V(50, 50), 40, 40, 3, -90, 5f)).Hole(Curve.Star(V(50, 50), 34, 34, 3, -90, 4f));
        var bolt = new Ink().Fill(Curve.Rounded([V(56, 20), V(36, 54), V(48, 54), V(42, 80), V(64, 44), V(52, 44)], 1.5f));
        yield return D("logo-hexagon", "Logos", 100, 100, (hex, Black), (hexRing, Gold), (bolt, Gold));

        var wings = Wing(Wing(new Ink(), V(150, 40), 1f, 1.1f), V(90, 40), -1f, 1.1f);
        yield return D("logo-wings", "Logos", 240, 100, (wings, White), (new Ink().Circle(V(120, 50), 30), Black), (new Ink().Star(V(120, 52), 21, 8.5f, 5, -90, 1f), White));
        yield return D("logo-bolt", "Logos", 100, 100, (new Ink().Circle(V(50, 50), 47), Blue), (new Ink().Fill(Curve.Rounded([V(60, 10), V(28, 56), V(46, 56), V(38, 90), V(72, 42), V(54, 42), V(66, 10)], 2f)), Yellow));
        var crest = new Ink().Fill(Curve.Rounded(ShieldOutline(6, 4, 88, 100), 4f));
        var stars = new Ink().Star(V(30, 34), 11, 4.5f, 5).Star(V(50, 30), 13, 5.2f, 5).Star(V(70, 34), 11, 4.5f, 5);
        var stripe = new Ink().Stroke([V(14, 66), V(86, 54)], 8f, round: false);
        yield return D("logo-crest", "Logos", 100, 110, (crest, Blue), (stars, White), (stripe, Red));
        var (leaves, centre) = (new Ink(), new Ink());
        foreach (var side in new[] { -1f, 1f })
        {
            leaves.Stroke(Curve.Arc(V(50, 46), 38, 40, side < 0 ? 100 : 80, side < 0 ? 200 : -20, 40), 3f);
            for (var i = 0; i < 7; i++)
            {
                var a = (side < 0 ? 110f + (i * 13f) : 70f - (i * 13f)) * MathF.PI / 180f;
                var at = V(50, 46) + V(MathF.Cos(a) * 38f, MathF.Sin(a) * 40f);
                leaves.Ellipse(at + (Dir((a * 180f / MathF.PI) + (side * 20f)) * 6f), 8f, 3.4f, (a * 180f / MathF.PI) + 90f + (side * 40f));
            }
        }

        centre.Star(V(50, 48), 22, 9, 5, -90, 1.2f);
        yield return D("logo-laurel", "Logos", 100, 100, (leaves, Gold), (centre, Gold));
    }
}
