using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Viewport;

/// <summary>
/// The impostor pictures of trees and bushes (<see cref="ImpostorBaker"/>), baked once per mesh and kept for the session,
/// and on disk under <see cref="Directory"/> when one is set, so the next session loads them instead of baking. Which
/// meshes get one: those foliage and HISM instances draw (<see cref="IsCandidate"/>), at least <see cref="MinRadius"/>
/// across and with more than one LOD (grass and pebbles stay meshes until the game culls them).
/// </summary>
public sealed class ImpostorCache
{
    /// <summary>Smallest LOD 0 bounding radius (cm) that gets an impostor.</summary>
    public const float MinRadius = 150f;

    private static readonly byte[] Magic = "SSIM"u8.ToArray();
    private readonly Dictionary<string, byte[]> _pictures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder of the cached pictures (raw RGBA behind a short header), or null to keep them in memory only.</summary>
    public string? Directory { get; set; }

    /// <summary>When false, meshes get no impostor (they keep drawing their last LOD up to the game's cull distance).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Atlases baked this session (diagnostics).</summary>
    public int Baked { get; private set; }

    /// <summary>A mesh instanced placements draw, with LODs, that is not a stand-in, a card or water.</summary>
    public static bool IsCandidate(PreparedMeshAsset asset) =>
        asset.Lods.Count > 1 && !asset.Billboard && !asset.Shimmer && !asset.Water;

    /// <summary>The key of a mesh's pictures: its path, vertex and index counts (a game update that changes it bakes again) and the bake version.</summary>
    public static string KeyOf(string meshPath, GpuMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(meshPath);
        ArgumentNullException.ThrowIfNull(mesh);
        return $"{meshPath}|{mesh.VertexCount}|{mesh.IndexCount}|v{ImpostorBaker.Version}|{ImpostorBaker.DefaultViews}x{ImpostorBaker.DefaultCell}";
    }

    /// <summary>
    /// Gives <paramref name="handle"/>'s mesh its impostor card and returns the atlas (the caller owns it), or null when it
    /// takes none. <paramref name="baked"/> says the pictures were rendered now (the costly case) rather than found.
    /// </summary>
    public GpuTexture? Give(SceneRenderer renderer, string meshPath, MeshHandle handle, out bool baked)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(handle);
        baked = false;
        if (!Enabled || !renderer.Meshes.TryGetValue(handle.Id, out var mesh) || mesh.ImpostorLod >= 0 || mesh.Lods.Length < 2
            || mesh.Billboard || mesh.Water || mesh.Bounds.Extent.Length() < MinRadius)
        {
            return null;
        }

        var key = KeyOf(meshPath, mesh);
        if (!_pictures.TryGetValue(key, out var pictures))
        {
            pictures = Load(key);
            if (pictures is null)
            {
                pictures = ImpostorBaker.Bake(renderer, handle);
                baked = true;
                Baked++;
                Save(key, pictures);
            }

            _pictures[key] = pictures;
        }

        var atlas = ImpostorBaker.Upload(renderer, pictures);
        mesh.SetImpostor(atlas, ImpostorBaker.DefaultViews, ImpostorBaker.ScreenSizeFor(mesh.LodScreenSizes));
        return atlas;
    }

    private string? FileOf(string key) =>
        Directory is { Length: > 0 } folder ? Path.Combine(folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))) + ".imp") : null;

    private static int Bytes => ImpostorBaker.DefaultViews * ImpostorBaker.DefaultCell * ImpostorBaker.DefaultCell * 4;

    private byte[]? Load(string key)
    {
        if (FileOf(key) is not { } file || !File.Exists(file))
        {
            return null;
        }

        try
        {
            var data = File.ReadAllBytes(file);
            var header = Magic.Length + 12;
            if (data.Length != header + Bytes || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Magic.Length)) != ImpostorBaker.Version
                || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Magic.Length + 4)) != ImpostorBaker.DefaultViews
                || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Magic.Length + 8)) != ImpostorBaker.DefaultCell)
            {
                return null; // another version or a torn write: baked again
            }

            return data[header..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save(string key, byte[] pictures)
    {
        if (FileOf(key) is not { } file)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            using (var stream = File.Create(temp))
            {
                Span<byte> header = stackalloc byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(header, ImpostorBaker.Version);
                BinaryPrimitives.WriteInt32LittleEndian(header[4..], ImpostorBaker.DefaultViews);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..], ImpostorBaker.DefaultCell);
                stream.Write(Magic);
                stream.Write(header);
                stream.Write(pictures);
            }

            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a read-only or full disk: the pictures stay in memory for this session
        }
    }
}
