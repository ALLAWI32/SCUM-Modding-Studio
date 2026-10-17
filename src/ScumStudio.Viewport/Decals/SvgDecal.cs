using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ScumStudio.Viewport.Decals;

/// <summary>
/// An SVG drawing as a decal design (owner: "import our own pictures"): every filled shape (<c>path</c>, <c>rect</c>,
/// <c>circle</c>, <c>ellipse</c>, <c>polygon</c>, <c>polyline</c>) through its own and its groups' transforms, flattened to
/// outlines; shapes one after another in the same fill make one colour region, so the decal gets a colour picker per
/// colour of the drawing. Strokes, gradients (a grey fill), text, images, clip paths and <c>use</c> are not drawn: a
/// drawing that relies on them should be saved as a PNG instead.
/// </summary>
public static class SvgDecal
{
    /// <summary>More regions than this are merged by colour (one picker per colour).</summary>
    private const int MaxRegions = 12;

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "defs", "clipPath", "mask", "symbol", "title", "desc", "metadata", "style", "pattern", "marker", "linearGradient", "radialGradient", "text", "image", "use", "script" };

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0x000000, ["white"] = 0xFFFFFF, ["red"] = 0xFF0000, ["green"] = 0x008000, ["lime"] = 0x00FF00, ["blue"] = 0x0000FF,
        ["yellow"] = 0xFFFF00, ["orange"] = 0xFFA500, ["purple"] = 0x800080, ["gray"] = 0x808080, ["grey"] = 0x808080, ["silver"] = 0xC0C0C0,
        ["maroon"] = 0x800000, ["navy"] = 0x000080, ["teal"] = 0x008080, ["olive"] = 0x808000, ["aqua"] = 0x00FFFF, ["cyan"] = 0x00FFFF,
        ["fuchsia"] = 0xFF00FF, ["magenta"] = 0xFF00FF, ["pink"] = 0xFFC0CB, ["gold"] = 0xFFD700, ["brown"] = 0xA52A2A, ["darkred"] = 0x8B0000,
    };

    /// <summary>The drawing in <paramref name="file"/> as design <paramref name="id"/>.</summary>
    public static DecalDesign Load(string file, string id) => Parse(File.ReadAllText(file), id);

    /// <summary>The drawing <paramref name="svg"/> as design <paramref name="id"/> (category <c>Stickers</c>).</summary>
    /// <exception cref="FormatException">Not an SVG, or nothing filled in it.</exception>
    public static DecalDesign Parse(string svg, string id)
    {
        ArgumentNullException.ThrowIfNull(svg);
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Not an SVG drawing: " + ex.Message, ex);
        }

        if (doc.Root is not { } root || root.Name.LocalName != "svg")
        {
            throw new FormatException("Not an SVG drawing: no <svg> element.");
        }

        var shapes = new List<(uint Colour, FillRule Rule, List<Vector2[]> Polygons)>();
        Walk(root, Matrix3x2.Identity, new Style(0x000000, FillRule.NonZero, false), shapes, isRoot: true);
        shapes.RemoveAll(s => s.Polygons.Count == 0);
        if (shapes.Count == 0)
        {
            throw new FormatException("Nothing filled in this SVG drawing.");
        }

        var (min, size) = Box(root, shapes.SelectMany(s => s.Polygons));
        var groups = new List<(uint Colour, FillRule Rule, List<Vector2[]> Polygons)>();
        foreach (var shape in shapes)
        {
            if (groups.Count > 0 && groups[^1].Colour == shape.Colour && groups[^1].Rule == shape.Rule)
            {
                groups[^1].Polygons.AddRange(shape.Polygons);
            }
            else
            {
                groups.Add((shape.Colour, shape.Rule, [.. shape.Polygons]));
            }
        }

        if (groups.Count > MaxRegions)
        {
            groups = [.. groups.GroupBy(g => (g.Colour, g.Rule)).Select(g => (g.Key.Colour, g.Key.Rule, g.SelectMany(x => x.Polygons).ToList()))];
        }

        var regions = groups.Select(g => new DecalRegion(PathOf(g.Polygons, min), g.Colour, g.Rule)).ToList();
        return new DecalDesign(id, "Stickers", MathF.Max(size.X, 1e-3f), MathF.Max(size.Y, 1e-3f), regions);
    }

    private sealed record Style(uint Fill, FillRule Rule, bool None);

    private static void Walk(XElement element, Matrix3x2 parent, Style inherited, List<(uint, FillRule, List<Vector2[]>)> shapes, bool isRoot = false)
    {
        var name = element.Name.LocalName;
        if (Skipped.Contains(name) || Attribute(element, "display") == "none" || Attribute(element, "visibility") == "hidden")
        {
            return;
        }

        var transform = (Attribute(element, "transform") is { } t ? Transform(t) : Matrix3x2.Identity) * parent;
        var style = inherited;
        if (Attribute(element, "fill") is { } fill)
        {
            style = fill.Trim() == "none" ? style with { None = true } : Colour(fill) is { } c ? style with { Fill = c, None = false } : style;
        }

        if (Attribute(element, "fill-rule") is { } rule)
        {
            style = style with { Rule = rule.Trim() == "evenodd" ? FillRule.EvenOdd : FillRule.NonZero };
        }

        if (name is "g" or "svg" or "a" || isRoot)
        {
            foreach (var child in element.Elements())
            {
                Walk(child, transform, style, shapes);
            }

            return;
        }

        if (style.None)
        {
            return;
        }

        var data = name switch
        {
            "path" => Attribute(element, "d"),
            "rect" => Rect(element),
            "circle" => Ellipse(Number(element, "cx"), Number(element, "cy"), Number(element, "r"), Number(element, "r")),
            "ellipse" => Ellipse(Number(element, "cx"), Number(element, "cy"), Number(element, "rx"), Number(element, "ry")),
            "polygon" or "polyline" => Attribute(element, "points") is { } points ? "M" + points + "Z" : null,
            _ => null,
        };
        if (data is null)
        {
            return;
        }

        var polygons = Outline(data).Select(p => p.Select(v => Vector2.Transform(v, transform)).ToArray()).Where(p => p.Length >= 3).ToList();
        shapes.Add((style.Fill, style.Rule, polygons));
    }

    /// <summary>An attribute, or the same property in the element's <c>style</c>.</summary>
    private static string? Attribute(XElement element, string name)
    {
        if (element.Attribute("style")?.Value is { } css)
        {
            foreach (var item in css.Split(';'))
            {
                var colon = item.IndexOf(':');
                if (colon > 0 && item[..colon].Trim() == name)
                {
                    return item[(colon + 1)..].Trim();
                }
            }
        }

        return element.Attribute(name)?.Value;
    }

    private static float Number(XElement element, string name) =>
        Attribute(element, name) is { } v && float.TryParse(v.Trim().TrimEnd('x', 'p').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;

    private static string? Rect(XElement e)
    {
        var (x, y, w, h) = (Number(e, "x"), Number(e, "y"), Number(e, "width"), Number(e, "height"));
        return w <= 0 || h <= 0 ? null : FormattableString.Invariant($"M{x},{y}H{x + w}V{y + h}H{x}Z");
    }

    private static string? Ellipse(float cx, float cy, float rx, float ry)
    {
        if (rx <= 0 || ry <= 0)
        {
            return null;
        }

        const float K = 0.5522848f;
        var (kx, ky) = (rx * K, ry * K);
        return FormattableString.Invariant(
            $"M{cx + rx},{cy}C{cx + rx},{cy + ky} {cx + kx},{cy + ry} {cx},{cy + ry}C{cx - kx},{cy + ry} {cx - rx},{cy + ky} {cx - rx},{cy}C{cx - rx},{cy - ky} {cx - kx},{cy - ry} {cx},{cy - ry}C{cx + kx},{cy - ry} {cx + rx},{cy - ky} {cx + rx},{cy}Z");
    }

    /// <summary><c>#rgb</c>, <c>#rrggbb</c>, <c>rgb(r, g, b)</c>, a basic colour name; a gradient is grey.</summary>
    internal static uint? Colour(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) ? rgb : null;
        }

        if (v.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && v.EndsWith(')'))
        {
            var parts = v[4..^1].Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length == 3 && parts.All(p => float.TryParse(p.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                uint Channel(string p) => (uint)Math.Clamp(MathF.Round(float.Parse(p.TrimEnd('%'), CultureInfo.InvariantCulture) * (p.EndsWith('%') ? 2.55f : 1f)), 0, 255);
                return (Channel(parts[0]) << 16) | (Channel(parts[1]) << 8) | Channel(parts[2]);
            }

            return null;
        }

        if (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return 0x808080;
        }

        return v.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ? 0x000000 : Named.TryGetValue(v, out var named) ? named : null;
    }

    /// <summary>An SVG <c>transform</c> list as one matrix (the rightmost applies first).</summary>
    internal static Matrix3x2 Transform(string text)
    {
        var result = Matrix3x2.Identity;
        var at = 0;
        while (at < text.Length)
        {
            var open = text.IndexOf('(', at);
            var close = open < 0 ? -1 : text.IndexOf(')', open);
            if (open < 0 || close < 0)
            {
                break;
            }

            var name = text[at..open].Trim(' ', ',', '\t', '\n', '\r');
            var a = Numbers(text[(open + 1)..close]).ToArray();
            float A(int i, float fallback = 0f) => i < a.Length ? a[i] : fallback;
            static float Rad(float deg) => deg * MathF.PI / 180f;
            var m = name switch
            {
                "matrix" when a.Length >= 6 => new Matrix3x2(a[0], a[1], a[2], a[3], a[4], a[5]),
                "translate" => Matrix3x2.CreateTranslation(A(0), A(1)),
                "scale" => Matrix3x2.CreateScale(A(0, 1f), A(1, A(0, 1f))),
                "rotate" => Matrix3x2.CreateRotation(Rad(A(0)), new Vector2(A(1), A(2))),
                "skewX" => new Matrix3x2(1, 0, MathF.Tan(Rad(A(0))), 1, 0, 0),
                "skewY" => new Matrix3x2(1, MathF.Tan(Rad(A(0))), 0, 1, 0, 0),
                _ => Matrix3x2.Identity,
            };
            result = m * result; // "A B" draws B first, then A
            at = close + 1;
        }

        return result;
    }

    private static IEnumerable<float> Numbers(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == ',')
            {
                i++;
                continue;
            }

            var start = i;
            if (text[i] is '+' or '-')
            {
                i++;
            }

            var dot = false;
            while (i < text.Length && (char.IsDigit(text[i]) || (text[i] == '.' && !dot)))
            {
                dot |= text[i] == '.';
                i++;
            }

            if (i < text.Length && text[i] is 'e' or 'E')
            {
                i++;
                if (i < text.Length && text[i] is '+' or '-')
                {
                    i++;
                }

                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }
            }

            if (i == start)
            {
                i++; // not a number: skip it
                continue;
            }

            if (float.TryParse(text.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                yield return v;
            }
        }
    }

    /// <summary>
    /// The outlines of path data with every SVG command (absolute and relative <c>M L H V C S Q T A Z</c>), curves and arcs
    /// cut into straight pieces.
    /// </summary>
    internal static List<Vector2[]> Outline(string data)
    {
        var polygons = new List<Vector2[]>();
        var current = new List<Vector2>();
        var (p, start, lastCtrl) = (Vector2.Zero, Vector2.Zero, Vector2.Zero);
        var lastCmd = ' ';
        var tokens = Tokens(data);
        var k = 0;
        var cmd = ' ';
        float N()
        {
            if (k < tokens.Count && tokens[k].Number is { } v)
            {
                k++;
                return v;
            }

            throw new FormatException("A number is missing in the path data.");
        }

        bool HasNumber() => k < tokens.Count && tokens[k].Number is not null;
        void Close()
        {
            if (current.Count >= 3)
            {
                polygons.Add([.. current]);
            }

            current = [];
        }

        while (k < tokens.Count)
        {
            if (tokens[k].Command is { } c)
            {
                cmd = c;
                k++;
            }
            else if (cmd == ' ')
            {
                throw new FormatException("Path data does not start with a command.");
            }

            var rel = char.IsLower(cmd);
            Vector2 Pt(float x, float y) => rel ? p + new Vector2(x, y) : new Vector2(x, y);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    Close();
                    p = Pt(N(), N());
                    start = p;
                    current.Add(p);
                    cmd = rel ? 'l' : 'L'; // more pairs after M are lines
                    break;
                case 'L':
                    p = Pt(N(), N());
                    current.Add(p);
                    break;
                case 'H':
                    p = new Vector2(rel ? p.X + N() : N(), p.Y);
                    current.Add(p);
                    break;
                case 'V':
                    p = new Vector2(p.X, rel ? p.Y + N() : N());
                    current.Add(p);
                    break;
                case 'C':
                    {
                        var (c1, c2, e) = (Pt(N(), N()), Pt(N(), N()), Pt(N(), N()));
                        Cubic(current, p, c1, c2, e);
                        (lastCtrl, p) = (c2, e);
                        break;
                    }

                case 'S':
                    {
                        var c1 = lastCmd is 'C' or 'S' ? (2 * p) - lastCtrl : p;
                        var (c2, e) = (Pt(N(), N()), Pt(N(), N()));
                        Cubic(current, p, c1, c2, e);
                        (lastCtrl, p) = (c2, e);
                        break;
                    }

                case 'Q':
                    {
                        var (q, e) = (Pt(N(), N()), Pt(N(), N()));
                        Cubic(current, p, p + (2f / 3f * (q - p)), e + (2f / 3f * (q - e)), e);
                        (lastCtrl, p) = (q, e);
                        break;
                    }

                case 'T':
                    {
                        var q = lastCmd is 'Q' or 'T' ? (2 * p) - lastCtrl : p;
                        var e = Pt(N(), N());
                        Cubic(current, p, p + (2f / 3f * (q - p)), e + (2f / 3f * (q - e)), e);
                        (lastCtrl, p) = (q, e);
                        break;
                    }

                case 'A':
                    {
                        var (rx, ry, angle, large, sweep) = (N(), N(), N(), N(), N());
                        var e = Pt(N(), N());
                        Arc(current, p, rx, ry, angle, large != 0, sweep != 0, e);
                        p = e;
                        break;
                    }

                case 'Z':
                    Close();
                    p = start;
                    break;
                default:
                    throw new FormatException($"Unknown path command '{cmd}'.");
            }

            lastCmd = char.ToUpperInvariant(cmd);
            if (char.ToUpperInvariant(cmd) == 'Z')
            {
                cmd = ' ';
                if (HasNumber())
                {
                    throw new FormatException("A number follows Z in the path data.");
                }
            }
        }

        Close();
        return polygons;
    }

    private static List<(char? Command, float? Number)> Tokens(string data)
    {
        var tokens = new List<(char?, float?)>();
        var i = 0;
        while (i < data.Length)
        {
            var c = data[i];
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                tokens.Add((c, null));
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            var start = i;
            if (data[i] is '+' or '-')
            {
                i++;
            }

            var dot = false;
            while (i < data.Length && (char.IsDigit(data[i]) || (data[i] == '.' && !dot)))
            {
                dot |= data[i] == '.';
                i++;
            }

            if (i < data.Length && data[i] is 'e' or 'E')
            {
                i++;
                if (i < data.Length && data[i] is '+' or '-')
                {
                    i++;
                }

                while (i < data.Length && char.IsDigit(data[i]))
                {
                    i++;
                }
            }

            if (i == start || !float.TryParse(data.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                throw new FormatException($"Unreadable path data at '{data[start..Math.Min(data.Length, start + 12)]}'.");
            }

            tokens.Add((null, v));
        }

        return tokens;
    }

    private static void Cubic(List<Vector2> into, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        var length = Vector2.Distance(p0, p1) + Vector2.Distance(p1, p2) + Vector2.Distance(p2, p3);
        var steps = Math.Clamp((int)MathF.Ceiling(length / 2f), 6, 48);
        for (var s = 1; s <= steps; s++)
        {
            var t = s / (float)steps;
            var u = 1 - t;
            into.Add((u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3));
        }
    }

    /// <summary>An elliptical arc (SVG 1.1 F.6.5, radii scaled up when too small).</summary>
    private static void Arc(List<Vector2> into, Vector2 p0, float rx, float ry, float angleDeg, bool large, bool sweep, Vector2 p1)
    {
        (rx, ry) = (MathF.Abs(rx), MathF.Abs(ry));
        if (rx < 1e-6f || ry < 1e-6f || p0 == p1)
        {
            into.Add(p1);
            return;
        }

        var phi = angleDeg * MathF.PI / 180f;
        var (sin, cos) = MathF.SinCos(phi);
        var d = (p0 - p1) / 2;
        var x1 = (cos * d.X) + (sin * d.Y);
        var y1 = (-sin * d.X) + (cos * d.Y);
        var lambda = (x1 * x1 / (rx * rx)) + (y1 * y1 / (ry * ry));
        if (lambda > 1)
        {
            (rx, ry) = (rx * MathF.Sqrt(lambda), ry * MathF.Sqrt(lambda));
        }

        var num = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
        var den = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
        var coef = MathF.Sqrt(MathF.Max(0, num / den)) * (large == sweep ? -1 : 1);
        var (cx1, cy1) = (coef * rx * y1 / ry, -coef * ry * x1 / rx);
        var centre = new Vector2((cos * cx1) - (sin * cy1), (sin * cx1) + (cos * cy1)) + ((p0 + p1) / 2);
        static float Angle(Vector2 u, Vector2 v) => MathF.Atan2((u.X * v.Y) - (u.Y * v.X), Vector2.Dot(u, v));
        var a = new Vector2((x1 - cx1) / rx, (y1 - cy1) / ry);
        var b = new Vector2((-x1 - cx1) / rx, (-y1 - cy1) / ry);
        var theta = Angle(Vector2.UnitX, a);
        var delta = Angle(a, b);
        if (!sweep && delta > 0)
        {
            delta -= MathF.Tau;
        }
        else if (sweep && delta < 0)
        {
            delta += MathF.Tau;
        }

        var steps = Math.Clamp((int)MathF.Ceiling(MathF.Abs(delta) * MathF.Max(rx, ry) / 2f), 6, 64);
        for (var s = 1; s <= steps; s++)
        {
            var t = theta + (delta * s / steps);
            var (st, ct) = MathF.SinCos(t);
            into.Add(s == steps ? p1 : centre + new Vector2((cos * rx * ct) - (sin * ry * st), (sin * rx * ct) + (cos * ry * st)));
        }
    }

    /// <summary>The drawing's box: its <c>viewBox</c>, its width and height, or what its shapes cover.</summary>
    private static (Vector2 Min, Vector2 Size) Box(XElement root, IEnumerable<Vector2[]> polygons)
    {
        if (root.Attribute("viewBox")?.Value is { } viewBox && Numbers(viewBox).ToArray() is { Length: 4 } v && v[2] > 0 && v[3] > 0)
        {
            return (new Vector2(v[0], v[1]), new Vector2(v[2], v[3]));
        }

        var (w, h) = (Number(root, "width"), Number(root, "height"));
        if (w > 0 && h > 0 && root.Attribute("width")?.Value.Contains('%') != true)
        {
            return (Vector2.Zero, new Vector2(w, h));
        }

        var points = polygons.SelectMany(p => p).ToList();
        var (min, max) = (points.Aggregate(Vector2.Min), points.Aggregate(Vector2.Max));
        return (min, max - min);
    }

    private static string PathOf(IEnumerable<Vector2[]> polygons, Vector2 min)
    {
        var text = new StringBuilder();
        foreach (var polygon in polygons)
        {
            for (var i = 0; i < polygon.Length; i++)
            {
                var q = polygon[i] - min;
                text.Append(i == 0 ? 'M' : 'L').Append(q.X.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(q.Y.ToString("0.###", CultureInfo.InvariantCulture));
            }

            text.Append('Z');
        }

        return text.ToString();
    }
}
