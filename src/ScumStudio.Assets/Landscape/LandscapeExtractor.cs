using System.Numerics;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using UeIntPoint = CUE4Parse.UE4.Objects.Core.Math.FIntPoint;
using UeRotator = CUE4Parse.UE4.Objects.Core.Math.FRotator;
using UeVector = CUE4Parse.UE4.Objects.Core.Math.FVector;
using UeVector4 = CUE4Parse.UE4.Objects.Core.Math.FVector4;

namespace ScumStudio.Assets.Landscape;

/// <summary>Options for <see cref="LandscapeExtractor"/>.</summary>
public sealed record LandscapeExtractOptions
{
    /// <summary>Vertex step in quads (1 = every heightmap sample, 2 = every second, ...). Larger steps give lighter meshes.</summary>
    public int Step { get; init; } = 1;

    /// <summary>Compute smooth per-vertex normals (false leaves <see cref="MeshData.Normals"/> empty).</summary>
    public bool ComputeNormals { get; init; } = true;

    /// <summary>
    /// Take vertex normals from the heightmap's packed B/A normal (seamless across components, as UE computes them over
    /// neighbours) instead of averaging the triangles of the component, which leaves lighting seams at component edges.
    /// </summary>
    public bool PackedNormals { get; init; } = true;

    /// <summary>Keep the full-resolution <see cref="LandscapeSurface"/> of each component (heights + normals, ~390 KB for 255²).</summary>
    public bool KeepSurface { get; init; } = true;

    /// <summary>Also read each component's paint layers and grass density (<see cref="LandscapeLayerReader"/>).</summary>
    public bool ReadLayers { get; init; }

    /// <summary>With <see cref="ReadLayers"/>: include the cooked grass density maps.</summary>
    public bool ReadGrass { get; init; } = true;
}

/// <summary>One <c>ULandscapeComponent</c> turned into world-space geometry.</summary>
/// <param name="Name">Component export name.</param>
/// <param name="SectionBase">Section base (landscape quad coordinates of the component's origin).</param>
/// <param name="ComponentSizeQuads">Quads per side.</param>
/// <param name="Mesh">
/// World-space mesh (UE centimetres, Z up). Its UVs address a per-component texture of
/// <see cref="LandscapeExtractor.AlbedoTextureSize"/> texels whose texel <c>i</c> is the sample at quad <c>i</c>
/// (<see cref="LandscapeExtractor.TextureCoordinate"/>), so baked terrain textures line up with the samples exactly.
/// </param>
/// <param name="MinHeightCm">Lowest vertex Z in world centimetres.</param>
/// <param name="MaxHeightCm">Highest vertex Z in world centimetres.</param>
public sealed record LandscapeComponentMesh(string Name, (int X, int Y) SectionBase, int ComponentSizeQuads, MeshData Mesh, float MinHeightCm, float MaxHeightCm)
{
    /// <summary>Full-resolution heights and normals (null when <see cref="LandscapeExtractOptions.KeepSurface"/> is off).</summary>
    public LandscapeSurface? Surface { get; init; }

    /// <summary>Paint layers and grass (null unless <see cref="LandscapeExtractOptions.ReadLayers"/> is on).</summary>
    public LandscapeComponentLayers? Layers { get; init; }
}

/// <summary>A <c>LandscapeStreamingProxy</c> (or <c>Landscape</c>) actor of a level with its components.</summary>
/// <param name="Name">Actor export name, e.g. <c>LandscapeStreamingProxy_A_0_1a</c>.</param>
/// <param name="Transform">World transform of the actor's root component (landscape origin, quad size = scale).</param>
/// <param name="SectionOffset">Proxy <c>LandscapeSectionOffset</c>.</param>
/// <param name="Components">Extracted components.</param>
/// <param name="Warnings">Components that could not be extracted, with the reason.</param>
public sealed record LandscapeProxyMesh(string Name, FTransform Transform, (int X, int Y) SectionOffset, IReadOnlyList<LandscapeComponentMesh> Components, IReadOnlyList<string> Warnings)
{
    /// <summary>Total triangles of all components.</summary>
    public int TriangleCount => Components.Sum(c => c.Mesh.TriangleCount);
}

/// <summary>
/// Turns the landscape of a cooked level package (a World Composition landscape tile such as
/// <c>/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1</c>) into renderable world-space meshes.
/// <para>Layout (UE 4.27, verified on SCUM): the tile's <c>LandscapeStreamingProxy</c> actor lists its
/// <c>LandscapeComponents</c>; each <c>ULandscapeComponent</c> covers <c>ComponentSizeQuads</c> quads split into
/// <c>NumSubsections</c> subsections of <c>SubsectionSizeQuads</c>, and samples its heights from
/// <c>HeightmapTexture</c> (B8G8R8A8: R = height high byte, G = low byte, B/A = normal) in the texel window given by
/// <c>HeightmapScaleBias</c> (x,y = 1/size, z,w = UV offset). Local height = (h - 32768) / 128 landscape units; the
/// component's <c>RelativeLocation</c> is in landscape quads relative to the proxy root, and the root's scale (typically
/// 100 cm per quad) turns everything into centimetres.</para>
/// </summary>
public static class LandscapeExtractor
{
    /// <summary>UE <c>LANDSCAPE_ZSCALE</c>: landscape units per 16-bit height step.</summary>
    public const float ZScale = 1f / 128f;

    /// <summary>True when <paramref name="className"/> is a landscape proxy actor class.</summary>
    public static bool IsLandscapeProxyClass(string? className) =>
        className is "LandscapeStreamingProxy" or "Landscape" or "LandscapeProxy";

    /// <summary>Extracts every landscape proxy of the level package at <paramref name="packagePath"/>.</summary>
    public static IReadOnlyList<LandscapeProxyMesh> Extract(AssetCatalog catalog, string packagePath, LandscapeExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        options ??= new LandscapeExtractOptions();
        var package = catalog.LoadPackage(packagePath);
        var result = new List<LandscapeProxyMesh>();
        foreach (var export in package.GetExports())
        {
            if (!IsLandscapeProxyClass(export.ExportType))
            {
                continue;
            }

            result.Add(ExtractProxy(export, options));
        }

        return result;
    }

    /// <summary>Extracts one proxy actor.</summary>
    public static LandscapeProxyMesh ExtractProxy(UObject proxy, LandscapeExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        options ??= new LandscapeExtractOptions();
        var warnings = new List<string>();
        var root = proxy.GetOrDefault<FPackageIndex?>("RootComponent")?.Load();
        var rootTransform = root is null ? FTransform.Identity : ReadRelativeTransform(root);
        var sectionOffset = proxy.GetOrDefault<UeIntPoint>("LandscapeSectionOffset");
        var components = new List<LandscapeComponentMesh>();
        foreach (var index in proxy.GetOrDefault<FPackageIndex[]>("LandscapeComponents") ?? [])
        {
            var component = index.Load();
            if (component is null)
            {
                warnings.Add($"{proxy.Name}: LandscapeComponents entry could not be loaded.");
                continue;
            }

            try
            {
                components.Add(ExtractComponent(component, rootTransform, options));
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                warnings.Add($"{component.Name}: {ex.Message}");
            }
        }

        return new LandscapeProxyMesh(proxy.Name, rootTransform, ((int)sectionOffset.X, (int)sectionOffset.Y), components, warnings);
    }

    /// <summary>Builds the world-space grid of one component.</summary>
    public static LandscapeComponentMesh ExtractComponent(UObject component, FTransform proxyTransform, LandscapeExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        options ??= new LandscapeExtractOptions();
        var step = Math.Max(1, options.Step);

        var sizeQuads = component.GetOrDefault("ComponentSizeQuads", 63);
        var subsectionQuads = component.GetOrDefault("SubsectionSizeQuads", sizeQuads);
        var numSubsections = component.GetOrDefault("NumSubsections", 1);
        var sectionBaseX = component.GetOrDefault("SectionBaseX", 0);
        var sectionBaseY = component.GetOrDefault("SectionBaseY", 0);
        var scaleBias = component.GetOrDefault("HeightmapScaleBias", new UeVector4(1f, 1f, 0f, 0f));
        var heightmap = component.GetOrDefault<FPackageIndex?>("HeightmapTexture")?.Load<UTexture2D>()
                        ?? throw new InvalidDataException("no HeightmapTexture");

        var image = TextureDecoder.Decode(heightmap);
        var texW = image.Width;
        var texH = image.Height;
        var u0 = (int)MathF.Round(scaleBias.Z * texW);
        var v0 = (int)MathF.Round(scaleBias.W * texH);
        var samplesPerSide = numSubsections * (subsectionQuads + 1);
        if (u0 + samplesPerSide > texW || v0 + samplesPerSide > texH)
        {
            throw new InvalidDataException($"heightmap window {u0},{v0}+{samplesPerSide} exceeds the {texW}x{texH} texture");
        }

        // Component-relative transform (quads → landscape units), then the proxy root (units → centimetres).
        var local = ReadRelativeTransform(component);
        var toWorld = local.ToMatrixWithScale() * proxyTransform.ToMatrixWithScale();

        // Full-resolution samples (one per quad corner): local height, world height and the packed B/A normal.
        var n = sizeQuads + 1;
        var localZ = new float[n * n];
        var worldZ = new float[n * n];
        var packed = new byte[n * n * 2];
        for (var y = 0; y < n; y++)
        {
            var ty = TexelIndex(y, subsectionQuads, numSubsections) + v0;
            for (var x = 0; x < n; x++)
            {
                var tx = TexelIndex(x, subsectionQuads, numSubsections) + u0;
                var p = (ty * texW + tx) * 4;
                var i = y * n + x;
                var h16 = (image.Rgba[p] << 8) | image.Rgba[p + 1];
                localZ[i] = (h16 - 32768) * ZScale;
                worldZ[i] = Vector3.Transform(new Vector3(x, y, localZ[i]), toWorld).Z;
                packed[i * 2] = image.Rgba[p + 2];
                packed[i * 2 + 1] = image.Rgba[p + 3];
            }
        }

        var surface = new LandscapeSurface(component.Name, (sectionBaseX, sectionBaseY), sizeQuads, toWorld, worldZ, packed);

        var vertsPerSide = VertsPerSide(sizeQuads, step);
        var textureSize = AlbedoTextureSize(sizeQuads);
        var positions = new float[vertsPerSide * vertsPerSide * 3];
        var uvs = new float[vertsPerSide * vertsPerSide * 2];
        var minZ = float.MaxValue;
        var maxZ = float.MinValue;
        for (var vy = 0; vy < vertsPerSide; vy++)
        {
            var y = VertexQuad(vy, step, sizeQuads);
            for (var vx = 0; vx < vertsPerSide; vx++)
            {
                var x = VertexQuad(vx, step, sizeQuads);
                var world = Vector3.Transform(new Vector3(x, y, localZ[y * n + x]), toWorld);
                var i = vy * vertsPerSide + vx;
                positions[i * 3] = world.X;
                positions[i * 3 + 1] = world.Y;
                positions[i * 3 + 2] = world.Z;
                uvs[i * 2] = TextureCoordinate(x, textureSize);
                uvs[i * 2 + 1] = TextureCoordinate(y, textureSize);
                minZ = MathF.Min(minZ, world.Z);
                maxZ = MathF.Max(maxZ, world.Z);
            }
        }

        var quads = vertsPerSide - 1;
        var indices = new uint[quads * quads * 6];
        var k = 0;
        for (var y = 0; y < quads; y++)
        {
            for (var x = 0; x < quads; x++)
            {
                var a = (uint)(y * vertsPerSide + x);
                var b = a + 1;
                var c = a + (uint)vertsPerSide;
                var d = c + 1;
                // UE landscape winding (front face up in the left-handed UE world): a-c-b, b-c-d.
                indices[k++] = a; indices[k++] = c; indices[k++] = b;
                indices[k++] = b; indices[k++] = c; indices[k++] = d;
            }
        }

        float[]? normals = null;
        if (options.ComputeNormals)
        {
            if (options.PackedNormals)
            {
                normals = new float[positions.Length];
                for (var vy = 0; vy < vertsPerSide; vy++)
                {
                    var y = VertexQuad(vy, step, sizeQuads);
                    for (var vx = 0; vx < vertsPerSide; vx++)
                    {
                        var normal = surface.NormalAt(VertexQuad(vx, step, sizeQuads), y);
                        var i = (vy * vertsPerSide + vx) * 3;
                        normals[i] = normal.X;
                        normals[i + 1] = normal.Y;
                        normals[i + 2] = normal.Z;
                    }
                }
            }
            else
            {
                normals = ComputeNormals(positions, indices);
            }
        }

        var name = component.Name;
        var mesh = MeshData.Create(name, positions, indices, normals, uvs, [new MeshSection("Landscape", 0, indices.Length)]);
        return new LandscapeComponentMesh(name, (sectionBaseX, sectionBaseY), sizeQuads, mesh, minZ, maxZ)
        {
            Surface = options.KeepSurface ? surface : null,
            Layers = options.ReadLayers ? LandscapeLayerReader.Read(component, options.ReadGrass) : null,
        };
    }

    /// <summary>
    /// Vertices per side of a component mesh of <paramref name="sizeQuads"/> quads at vertex step <paramref name="step"/>:
    /// <c>ceil(sizeQuads / step) + 1</c>. The last vertex is clamped to the component edge (see <see cref="VertexQuad"/>),
    /// so every step reaches quad <paramref name="sizeQuads"/> and neighbouring components share their edge vertices.
    /// </summary>
    public static int VertsPerSide(int sizeQuads, int step)
    {
        step = Math.Max(1, step);
        return (sizeQuads + step - 1) / step + 1;
    }

    /// <summary>Quad coordinate of vertex <paramref name="vertex"/> at step <paramref name="step"/> (clamped to the edge).</summary>
    public static int VertexQuad(int vertex, int step, int sizeQuads) => Math.Min(vertex * Math.Max(1, step), sizeQuads);

    /// <summary>
    /// Edge length of the per-component terrain texture: the samples per side (<c>sizeQuads + 1</c>) rounded up to a power
    /// of two (256 for SCUM's 254-quad components); texels past the last sample repeat it.
    /// </summary>
    public static int AlbedoTextureSize(int sizeQuads)
    {
        var n = sizeQuads + 1;
        var size = 1;
        while (size < n)
        {
            size <<= 1;
        }

        return size;
    }

    /// <summary>UV of quad coordinate <paramref name="q"/> in a texture of <paramref name="textureSize"/> texels: the centre of texel <paramref name="q"/>.</summary>
    public static float TextureCoordinate(int q, int textureSize) => (q + 0.5f) / textureSize;

    /// <summary>
    /// Heightmap/weightmap texel column (or row) of quad coordinate <paramref name="q"/>: subsections store their border
    /// samples twice, so subsection <c>s</c> starts at texel <c>s * (subsectionQuads + 1)</c>. A quad on a subsection
    /// border maps to the earlier subsection (with 2 × 127: q 127 → 127, q 128 → 129, q 254 → 255).
    /// </summary>
    public static int TexelIndex(int q, int subsectionQuads, int numSubsections)
    {
        if (q <= 0 || subsectionQuads <= 0)
        {
            return Math.Max(0, q);
        }

        var subsection = Math.Min((q - 1) / subsectionQuads, Math.Max(1, numSubsections) - 1);
        return subsection * (subsectionQuads + 1) + (q - subsection * subsectionQuads);
    }

    /// <summary>
    /// Decodes a heightmap's packed normal (B = x, A = y, each <c>byte / 127.5 - 1</c>; z = <c>sqrt(1 - x² - y²)</c>),
    /// in the landscape's unrotated, already scaled space. Returns false for bytes that are not a unit-length encoding.
    /// </summary>
    public static bool TryDecodePackedNormal(byte b, byte a, out Vector3 normal)
    {
        var nx = b / 127.5f - 1f;
        var ny = a / 127.5f - 1f;
        var xy = nx * nx + ny * ny;
        if (xy > 1.02f)
        {
            normal = Vector3.UnitZ;
            return false;
        }

        normal = Vector3.Normalize(new Vector3(nx, ny, MathF.Sqrt(MathF.Max(0f, 1f - xy))));
        return true;
    }

    private static FTransform ReadRelativeTransform(UObject sceneComponent)
    {
        var l = sceneComponent.GetOrDefault("RelativeLocation", UeVector.ZeroVector);
        var r = sceneComponent.GetOrDefault("RelativeRotation", UeRotator.ZeroRotator);
        var s = sceneComponent.GetOrDefault("RelativeScale3D", UeVector.OneVector);
        return new FTransform(new FRotator(r.Pitch, r.Yaw, r.Roll), new FVector(l.X, l.Y, l.Z), new FVector(s.X, s.Y, s.Z));
    }

    /// <summary>Area-weighted per-vertex normals from the triangle list.</summary>
    private static float[] ComputeNormals(float[] positions, uint[] indices)
    {
        var n = positions.Length / 3;
        var acc = new Vector3[n];
        for (var t = 0; t < indices.Length; t += 3)
        {
            var i0 = (int)indices[t];
            var i1 = (int)indices[t + 1];
            var i2 = (int)indices[t + 2];
            var p0 = new Vector3(positions[i0 * 3], positions[i0 * 3 + 1], positions[i0 * 3 + 2]);
            var p1 = new Vector3(positions[i1 * 3], positions[i1 * 3 + 1], positions[i1 * 3 + 2]);
            var p2 = new Vector3(positions[i2 * 3], positions[i2 * 3 + 1], positions[i2 * 3 + 2]);
            var normal = Vector3.Cross(p1 - p0, p2 - p0);
            acc[i0] += normal;
            acc[i1] += normal;
            acc[i2] += normal;
        }

        var normals = new float[positions.Length];
        for (var i = 0; i < n; i++)
        {
            var v = acc[i];
            if (v.LengthSquared() > 0f)
            {
                v = Vector3.Normalize(v);
                // Landscape normals point up (+Z in UE); flip if the winding produced downward normals.
                if (v.Z < 0f)
                {
                    v = -v;
                }
            }
            else
            {
                v = Vector3.UnitZ;
            }

            normals[i * 3] = v.X;
            normals[i * 3 + 1] = v.Y;
            normals[i * 3 + 2] = v.Z;
        }

        return normals;
    }
}
