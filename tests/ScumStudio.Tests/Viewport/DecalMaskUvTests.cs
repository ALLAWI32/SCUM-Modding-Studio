using System.Numerics;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// A colour mask read through its own UV channel (the Laika's and the WolfsWagen's bodies: <c>M_Car_01</c>'s 'Color Mask UV
/// Channel' 1): <see cref="DecalBaker"/> takes the paint off where that channel puts the decal, the paint preview reads the
/// mask there (<see cref="DecalBaker.MaskInColourUv"/>), and a decal never spills onto an island whose texels another place uses.
/// </summary>
public sealed class DecalMaskUvTests
{
    private const int Size = 256;

    [Fact]
    public void TheMaskLosesItsPaintWhereItsOwnChannelPutsTheDecal()
    {
        var panel = Panel("Body", FTransform.Identity, (0f, 0f, 0.5f, 1f)) with { MaskUv = 1 };
        var layer = new DecalLayer { Position = new Vector3(0, 20, 10), Normal = Vector3.UnitX, WidthCm = 20, HeightCm = 20 };
        var baked = DecalBaker.Bake(Flat(128, 128, 128), Flat(0, 255, 0), [panel], DecalBaker.Stamps(layer, Solid, panel.Transform));

        // The decal spans y 10..30, z 0..20: UV0 u 0.30..0.40, v 0.30..0.50; UV1 (mirrored, upside down) u 0.60..0.70, v 0.50..0.70.
        var (colour, mask) = (Changed(baked.Diffuse, 128, 128, 128), Changed(baked.Mask, 0, 255, 0));
        Assert.InRange(colour.Count, Size * Size * 0.02f * 0.8f, Size * Size * 0.02f * 1.3f);
        Assert.All(colour, t => Assert.True(Within(t, 0.30f, 0.40f, 0.30f, 0.50f), $"colour texel {t} off the decal"));
        Assert.InRange(mask.Count, Size * Size * 0.02f * 0.8f, Size * Size * 0.02f * 1.3f);
        Assert.All(mask, t => Assert.True(Within(t, 0.60f, 0.70f, 0.50f, 0.70f), $"mask texel {t} off the decal"));

        // What the game shows at the decal's middle: its colour, not the paint (any paint colour).
        var (uv0, uv1) = (new Vector2(0.35f, 0.40f), new Vector2(0.65f, 0.60f));
        var shown = PaintRule.Shown(Linear(baked.Diffuse, uv0), baked.Mask.Rgba, Index(uv1), new Vector3(0, 0, 1), new Vector3(0, 0, 1));
        Assert.True(Vector3.Distance(shown, new Vector3(1, 0, 1)) < 0.02f, $"{shown}");
        Assert.Empty(baked.SharedUv);
    }

    [Fact]
    public void ThePaintPreviewReadsTheMaskThroughItsOwnChannel()
    {
        var panel = Panel("Body", FTransform.Identity, (0f, 0f, 0.5f, 1f)) with { MaskUv = 1 };
        // Paint only on the mask's left quarter: UV1 u 0.5..0.75 is y 0..50 of the panel, UV0 u 0.25..0.5.
        var mask = Flat(0, 0, 0);
        for (var y = 0; y < Size; y++)
        {
            for (var x = Size / 2; x < Size * 3 / 4; x++)
            {
                mask.Rgba[(((y * Size) + x) * 4) + 1] = 255;
            }
        }

        var inUv0 = DecalBaker.MaskInColourUv(mask, [panel], Size, Size);
        Assert.Equal(255, inUv0.Rgba[Index(new Vector2(0.40f, 0.5f)) + 1]); // y = 30: painted
        Assert.Equal(0, inUv0.Rgba[Index(new Vector2(0.10f, 0.5f)) + 1]);   // y = −30: bare
        var painted = PaintBaker.Bake(Flat(128, 128, 128), mask, new Vector4(0, 0, 1, 1), null, [panel]);
        Assert.Equal(255, painted.Rgba[Index(new Vector2(0.40f, 0.5f)) + 3]); // alpha: the paint coverage
        Assert.Equal(0, painted.Rgba[Index(new Vector2(0.10f, 0.5f)) + 3]);
        Assert.Same(mask, DecalBaker.MaskInColourUv(mask, [panel with { MaskUv = 0 }], Size, Size)); // UV0: the mask itself
    }

    [Fact]
    public void ADecalSkipsAnIslandWhoseTexelsAnotherPlaceUsesAndRunsOnOneOfItsOwn()
    {
        // A panel; in front of it two small strips the decal reaches: one shares its texels with a part far away (stacked
        // UVs), one has texels of its own.
        var panel = Panel("BPC_Car_Door", FTransform.Identity, (0f, 0f, 0.5f, 1f));
        var stacked = Panel("BPC_Car_Trim", new FTransform(new FVector(1, 0, 0)), (0.6f, 0f, 0.7f, 0.1f), 10f);
        var far = Panel("BPC_Car_Far", new FTransform(new FVector(0, 400, 0)), (0.6f, 0f, 0.7f, 0.1f), 10f);
        var own = Panel("BPC_Car_Strip", new FTransform(new FVector(1, 8, 0)), (0.8f, 0f, 0.9f, 0.1f), 10f);
        var layer = new DecalLayer { Position = new Vector3(0, 0, 0), Normal = Vector3.UnitX, WidthCm = 30, HeightCm = 30 };
        var baked = DecalBaker.Bake(Flat(128, 128, 128), Flat(0, 0, 0), [panel, stacked, far, own], DecalBaker.Stamps(layer, Solid, panel.Transform));

        var changed = Changed(baked.Diffuse, 128, 128, 128);
        Assert.Contains(changed, t => Within(t, 0f, 0.5f, 0f, 1f));             // the panel
        Assert.DoesNotContain(changed, t => Within(t, 0.55f, 0.75f, 0f, 0.15f)); // not the stacked strip: it would show far away
        Assert.Contains(changed, t => Within(t, 0.8f, 0.9f, 0f, 0.1f));         // the strip with texels of its own
        Assert.Empty(baked.SharedUv);

        // Put on the stacked strip itself, it is baked there and the far place that shows it too is named.
        var onTrim = layer with { Position = new Vector3(0, 0, 0), WidthCm = 8, HeightCm = 8 };
        var trimBake = DecalBaker.Bake(Flat(128, 128, 128), Flat(0, 0, 0), [stacked, far], DecalBaker.Stamps(onTrim, Solid, stacked.Transform));
        Assert.Contains(Changed(trimBake.Diffuse, 128, 128, 128), t => Within(t, 0.6f, 0.7f, 0f, 0.1f));
        Assert.Equal(["BPC_Car_Far"], trimBake.SharedUv);
    }

    private static readonly DecalImage Solid = new(1, 1, [1f, 0f, 1f, 1f]);

    /// <summary>
    /// A square panel (side <paramref name="side"/> cm, 16 x 16 quads) in the YZ plane facing +X, at <paramref name="at"/>:
    /// UV0 over <paramref name="uv0"/> (u along +Y, v down), and a UV1 over the right half of the texture mirrored left to
    /// right and upside down (u 1 − y-share / 2, v up).
    /// </summary>
    private static PreviewPart Panel(string name, FTransform at, (float U0, float V0, float U1, float V1) uv0, float side = 100f)
    {
        const int N = 16;
        var (positions, normals, uvs, uv1, indices) = (new List<float>(), new List<float>(), new List<float>(), new List<float>(), new List<uint>());
        for (var j = 0; j <= N; j++)
        {
            for (var i = 0; i <= N; i++)
            {
                var (a, b) = (i / (float)N, j / (float)N); // a along +Y, b down from the top
                positions.AddRange([0f, (a - 0.5f) * side, (0.5f - b) * side]);
                normals.AddRange([1f, 0f, 0f]);
                uvs.AddRange([uv0.U0 + (a * (uv0.U1 - uv0.U0)), uv0.V0 + (b * (uv0.V1 - uv0.V0))]);
                uv1.AddRange([1f - (a * 0.5f), 1f - b]);
            }
        }

        for (var j = 0u; j < N; j++)
        {
            for (var i = 0u; i < N; i++)
            {
                var (p, q, r, s) = ((j * (N + 1)) + i, (j * (N + 1)) + i + 1, ((j + 1) * (N + 1)) + i + 1, ((j + 1) * (N + 1)) + i);
                indices.AddRange([p, q, r, p, r, s]);
            }
        }

        var mesh = MeshData.Create(name, [.. positions], [.. indices], [.. normals], [.. uvs]) with { ExtraUvs = [[.. uv1]] };
        return new PreviewPart(name, mesh, null, at) { Material = "/Game/Cars/MI_Body.MI_Body", Attachment = "/Game/Cars/" + name };
    }

    private static TextureImage Flat(byte r, byte g, byte b)
    {
        var rgba = new byte[Size * Size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
        }

        return new TextureImage("T", Size, Size, rgba, "PF_B8G8R8A8", 0, Size, Size, true, false);
    }

    /// <summary>The texels (index) whose RGB is no longer (r, g, b).</summary>
    private static List<int> Changed(TextureImage image, byte r, byte g, byte b) =>
        [.. Enumerable.Range(0, Size * Size).Where(i => image.Rgba[i * 4] != r || image.Rgba[(i * 4) + 1] != g || image.Rgba[(i * 4) + 2] != b)];

    /// <summary>Whether texel <paramref name="t"/>'s centre lies in u <paramref name="u0"/>..<paramref name="u1"/>, v <paramref name="v0"/>..<paramref name="v1"/> (a texel and the bake's few gutter texels of slack).</summary>
    private static bool Within(int t, float u0, float u1, float v0, float v1)
    {
        const float Slack = 6f / Size;
        var (u, v) = (((t % Size) + 0.5f) / Size, ((t / Size) + 0.5f) / Size);
        return u >= u0 - Slack && u <= u1 + Slack && v >= v0 - Slack && v <= v1 + Slack;
    }

    private static int Index(Vector2 uv) => ((((int)(uv.Y * Size)) * Size) + (int)(uv.X * Size)) * 4;

    private static Vector3 Linear(TextureImage image, Vector2 uv)
    {
        var i = Index(uv);
        static float L(byte v) => v / 255f <= 0.04045f ? v / 255f / 12.92f : MathF.Pow(((v / 255f) + 0.055f) / 1.055f, 2.4f);
        return new Vector3(L(image.Rgba[i]), L(image.Rgba[i + 1]), L(image.Rgba[i + 2]));
    }
}
