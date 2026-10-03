using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Viewport;

/// <summary>
/// The whole-island terrain backdrop kept on disk: building it reads ~1.8 GB of landscape data from 400 tiles and took
/// 15-17 s at every map start on the owner's PC; read back from the cache it takes well under a second. The file name
/// holds a hash of the tiles' file sizes and the bake options, so a game update or other settings build a new one.
/// </summary>
public static class TerrainCache
{
    // Bump when the cached data or the bake changes meaning.
    private const int Format = 1;

    /// <summary>The cache file for <paramref name="tiles"/> baked with <paramref name="options"/> under <paramref name="folder"/>.</summary>
    public static string PathFor(string folder, AssetCatalog catalog, IReadOnlyList<string> tiles, LevelSceneOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(options);
        var key = new StringBuilder($"v{Format}|{options.LandscapeStep}|{options.TerrainTextureSize}|{options.Ground}|{options.ResolveLayerTextures}");
        foreach (var tile in tiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            key.Append('|').Append(tile);
            if (catalog.TryGetPackageFile(tile, out var file))
            {
                var stem = file.Path[..file.Path.LastIndexOf('.')];
                key.Append(':').Append(file.Size);
                foreach (var ext in new[] { ".uexp", ".ubulk" })
                {
                    key.Append(':').Append(catalog.Provider.Files.TryGetValue(stem + ext, out var part) ? part.Size : 0);
                }
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..16].ToLowerInvariant();
        return Path.Combine(folder, $"island-terrain-{hash}.bin");
    }

    /// <summary>Writes the terrain of <paramref name="scene"/> to <paramref name="path"/> (atomically).</summary>
    public static void Save(string path, PreparedLevelScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var file = File.Create(temp))
        using (var zip = new BrotliStream(file, CompressionLevel.Fastest))
        using (var w = new BinaryWriter(zip))
        {
            w.Write(Format);
            w.Write((int)scene.Ground);
            w.Write(scene.SeaLevelCm.HasValue);
            w.Write(scene.SeaLevelCm ?? 0f);
            w.Write(scene.Terrain.Count);
            foreach (var t in scene.Terrain)
            {
                w.Write(t.Name);
                w.Write(t.LevelName);
                WriteMesh(w, t.Mesh);
                w.Write(t.Albedo is not null);
                if (t.Albedo is { } albedo)
                {
                    w.Write(albedo.Size);
                    w.Write(albedo.Rgba.Length);
                    w.Write(albedo.Rgba);
                }
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The terrain kept at <paramref name="path"/>, or null when there is none or it cannot be read.</summary>
    public static PreparedLevelScene? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var file = File.OpenRead(path);
            using var zip = new BrotliStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(zip, 1 << 20));
            if (r.ReadInt32() != Format)
            {
                return null;
            }

            var ground = (GroundMode)r.ReadInt32();
            var hasSea = r.ReadBoolean();
            var sea = r.ReadSingle();
            var count = r.ReadInt32();
            var terrain = new List<PreparedTerrain>(count);
            for (var i = 0; i < count; i++)
            {
                var name = r.ReadString();
                var level = r.ReadString();
                var mesh = ReadMesh(r);
                TerrainAlbedo? albedo = null;
                if (r.ReadBoolean())
                {
                    var size = r.ReadInt32();
                    albedo = new TerrainAlbedo(size, r.ReadBytes(r.ReadInt32()));
                }

                terrain.Add(new PreparedTerrain(name, level, mesh) { Albedo = albedo });
            }

            return new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, Assets.Textures.TextureImage>(), [], terrain, [], clock.Elapsed)
            {
                Ground = ground,
                SeaLevelCm = hasSea ? sea : null,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteMesh(BinaryWriter w, MeshData m)
    {
        w.Write(m.Name);
        WriteFloats(w, m.Positions);
        WriteFloats(w, m.Normals);
        WriteFloats(w, m.Uv0);
        w.Write(m.Indices.Length);
        foreach (var i in m.Indices)
        {
            w.Write(i);
        }

        w.Write(m.Sections.Length);
        foreach (var s in m.Sections)
        {
            w.Write(s.MaterialName);
            w.Write(s.FirstIndex);
            w.Write(s.IndexCount);
        }

        foreach (var v in new[] { m.Bounds.Min, m.Bounds.Max })
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }
    }

    private static MeshData ReadMesh(BinaryReader r)
    {
        var name = r.ReadString();
        var positions = ReadFloats(r);
        var normals = ReadFloats(r);
        var uv = ReadFloats(r);
        var indices = new uint[r.ReadInt32()];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = r.ReadUInt32();
        }

        var sections = new MeshSection[r.ReadInt32()];
        for (var i = 0; i < sections.Length; i++)
        {
            sections[i] = new MeshSection(r.ReadString(), r.ReadInt32(), r.ReadInt32());
        }

        var min = new System.Numerics.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var max = new System.Numerics.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return new MeshData(name, positions, normals, uv, indices, sections, new BoundingBox(min, max));
    }

    private static void WriteFloats(BinaryWriter w, float[] values)
    {
        w.Write(values.Length);
        w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static float[] ReadFloats(BinaryReader r)
    {
        var values = new float[r.ReadInt32()];
        r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }
}
