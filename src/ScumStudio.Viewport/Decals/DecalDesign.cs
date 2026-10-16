using System.Numerics;

namespace ScumStudio.Viewport.Decals;

/// <summary>One colour region of a <see cref="DecalDesign"/>: an outline in the design's view box and the colour it has until the user picks one.</summary>
/// <param name="Path">SVG path data in view-box units.</param>
/// <param name="Colour">Default colour, 0xRRGGBB (sRGB).</param>
/// <param name="Rule">Fill rule of <paramref name="Path"/>.</param>
public sealed record DecalRegion(string Path, uint Colour, FillRule Rule = FillRule.NonZero);

/// <summary>
/// A decal of the library: vector regions in a <paramref name="Width"/> x <paramref name="Height"/> view box, drawn in
/// order (a later region covers an earlier one), each with its own colour.
/// </summary>
/// <param name="Id">Stable id (saved in <c>decals.json</c>).</param>
/// <param name="Category">Library category (<c>Tribal</c>, <c>Flames</c> ...).</param>
/// <param name="Width">View box width.</param>
/// <param name="Height">View box height.</param>
/// <param name="Regions">Colour regions, bottom first.</param>
public sealed record DecalDesign(string Id, string Category, float Width, float Height, IReadOnlyList<DecalRegion> Regions)
{
    /// <summary>Width over height.</summary>
    public float Aspect => Width / Height;

    /// <summary>
    /// The regions' coverage at <paramref name="pixelsWide"/> pixels across, each plane with what later regions cover
    /// taken out, so the planes never add up to more than 1.
    /// </summary>
    public DecalRaster Rasterize(int pixelsWide)
    {
        var w = Math.Max(1, pixelsWide);
        var h = Math.Max(1, (int)MathF.Round(w / Aspect));
        var scale = Matrix3x2.CreateScale(w / Width, h / Height);
        var planes = Regions.Select(r => PathRaster.Fill(r.Path, w, h, scale, r.Rule)).ToArray();
        var above = new float[w * h]; // what the regions over this one cover
        for (var k = planes.Length - 1; k >= 0; k--)
        {
            for (var i = 0; i < above.Length; i++)
            {
                var own = planes[k][i];
                planes[k][i] = own * (1f - above[i]);
                above[i] += own - (above[i] * own);
            }
        }

        return new DecalRaster(w, h, planes);
    }
}

/// <summary>A design's regions as coverage planes (row-major, top row first).</summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Planes">One coverage plane (0..1) per region.</param>
public sealed record DecalRaster(int Width, int Height, IReadOnlyList<float[]> Planes)
{
    /// <summary>The decal in colour: each region's plane times its colour (linear RGB), the summed coverage as alpha.</summary>
    public DecalImage Colourise(IReadOnlyList<Vector3> colours)
    {
        ArgumentNullException.ThrowIfNull(colours);
        var rgba = new float[Width * Height * 4];
        for (var k = 0; k < Planes.Count; k++)
        {
            var c = colours.Count == 0 ? Vector3.One : colours[Math.Min(k, colours.Count - 1)];
            var plane = Planes[k];
            for (var i = 0; i < plane.Length; i++)
            {
                var a = plane[i];
                if (a > 0f)
                {
                    rgba[i * 4] += c.X * a;
                    rgba[(i * 4) + 1] += c.Y * a;
                    rgba[(i * 4) + 2] += c.Z * a;
                    rgba[(i * 4) + 3] += a;
                }
            }
        }

        return new DecalImage(Width, Height, rgba);
    }
}

/// <summary>A decal ready to bake: premultiplied linear RGBA floats, row-major, top row first.</summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Rgba">Premultiplied linear colour and coverage, <c>Width * Height * 4</c> floats.</param>
public sealed record DecalImage(int Width, int Height, float[] Rgba)
{
    /// <summary>The decal at (<paramref name="s"/>, <paramref name="t"/>) in 0..1 (s across, t down), bilinear; clear outside.</summary>
    public Vector4 Sample(float s, float t)
    {
        if (s is < 0f or > 1f || t is < 0f or > 1f)
        {
            return Vector4.Zero;
        }

        var (x, y) = ((s * Width) - 0.5f, (t * Height) - 0.5f);
        var (x0, y0) = ((int)MathF.Floor(x), (int)MathF.Floor(y));
        var (fx, fy) = (x - x0, y - y0);
        Vector4 At(int px, int py)
        {
            px = Math.Clamp(px, 0, Width - 1);
            py = Math.Clamp(py, 0, Height - 1);
            var i = ((py * Width) + px) * 4;
            return new Vector4(Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
        }

        return Vector4.Lerp(Vector4.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Vector4.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
    }
}
