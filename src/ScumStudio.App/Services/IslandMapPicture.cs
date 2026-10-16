using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.World;
using ScumStudio.Viewport;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ScumStudio.App.Services;

/// <summary>Where the island picture of the Spawns map comes from.</summary>
public enum IslandMapSource
{
    /// <summary>A picture the user chose (<see cref="IslandMapPicture.ImportAsync"/>).</summary>
    Imported,

    /// <summary>The game's own map texture, decoded from the user's paks.</summary>
    Game,

    /// <summary>The terrain seen from above, drawn from the map page's terrain cache.</summary>
    Terrain,

    /// <summary>No picture: the map shows its grid only.</summary>
    None,
}

/// <summary>The island picture and where it came from.</summary>
/// <param name="PngPath">The PNG on disk (covers <see cref="IslandMap"/>'s square), or null for <see cref="IslandMapSource.None"/>.</param>
/// <param name="Source">Where it came from.</param>
public sealed record IslandMapPictureResult(string? PngPath, IslandMapSource Source);

/// <summary>
/// The picture under the Spawns map, as a PNG in the app's cache: the user's own picture when one was imported, else the
/// game's map texture (<see cref="IslandMap.TexturePath"/>, decoded once from the user's paks and kept; the picture is the
/// game's artwork, so it never ships with the app), else the terrain seen from above, drawn from the newest terrain cache
/// of the Map page. Nothing is downloaded.
/// </summary>
public sealed class IslandMapPicture
{
    /// <summary>Largest edge of the decoded game texture.</summary>
    public const int GameMaxSize = 4096;

    /// <summary>Edge of the terrain picture.</summary>
    public const int TerrainSize = 2048;

    // Bump when the terrain picture is drawn differently.
    private const string TerrainVersion = "1";

    private readonly ILogger _logger;
    private readonly string? _terrainDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the service caching into <paramref name="cacheDirectory"/>; terrain caches are looked for in its parent folder.</summary>
    public IslandMapPicture(string cacheDirectory, ILogger logger)
        : this(cacheDirectory, logger, Path.GetDirectoryName(Path.GetFullPath(cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory)))))
    {
    }

    internal IslandMapPicture(string cacheDirectory, ILogger logger, string? terrainDirectory)
    {
        CacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _terrainDirectory = terrainDirectory;
    }

    /// <summary>Cache folder.</summary>
    public string CacheDirectory { get; }

    /// <summary>Where the imported picture is kept.</summary>
    public string ImportedPath => Path.Combine(CacheDirectory, "imported.png");

    /// <summary>The best picture available: imported, then the game's (when <paramref name="catalog"/> has it), then the terrain.</summary>
    public Task<IslandMapPictureResult> GetAsync(AssetCatalog? catalog, CancellationToken cancellationToken = default) =>
        GetAsync(catalog is null ? null : FromCatalog(catalog), cancellationToken);

    /// <summary>Copies <paramref name="file"/> (a PNG or JPEG picture) as the imported picture; false when it is not one.</summary>
    public async Task<bool> ImportAsync(string file, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        try
        {
            using var image = await Image.LoadAsync<Rgba32>(file, cancellationToken).ConfigureAwait(false);
            if (image.Metadata.DecodedImageFormat is not (PngFormat or JpegFormat) || image.Width < 2 || image.Height < 2)
            {
                return false;
            }

            Directory.CreateDirectory(CacheDirectory);
            var temp = $"{ImportedPath}.{Environment.CurrentManagedThreadId}.tmp";
            await image.SaveAsPngAsync(temp, cancellationToken).ConfigureAwait(false);
            File.Move(temp, ImportedPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is ImageFormatException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogInformation("Not a map picture: {File}: {Message}", file, ex.Message);
            return false;
        }
    }

    /// <summary>Forgets the imported picture.</summary>
    public void ClearImported()
    {
        try
        {
            File.Delete(ImportedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove {File}: {Message}", ImportedPath, ex.Message);
        }
    }

    /// <summary>The game texture of <paramref name="catalog"/> as a cache key and a decoder, or null when the paks have none.</summary>
    internal static GamePicture? FromCatalog(AssetCatalog catalog)
    {
        var package = IslandMap.TexturePath[..IslandMap.TexturePath.LastIndexOf('.')];
        return catalog.TryGetPackageFile(package, out var file)
            ? new GamePicture($"{file.Path}|{file.Size}", () => TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(IslandMap.TexturePath), GameMaxSize))
            : null;
    }

    internal async Task<IslandMapPictureResult> GetAsync(GamePicture? game, CancellationToken cancellationToken)
    {
        if (IsCompletePng(ImportedPath))
        {
            return new IslandMapPictureResult(ImportedPath, IslandMapSource.Imported);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (game is not null
                && await MakeAsync($"islandmap-{Hash(game.Key)}.png", () => game.Decode() is { } image ? (image.Rgba, image.Width, image.Height) : null, cancellationToken)
                    .ConfigureAwait(false) is { } decoded)
            {
                return new IslandMapPictureResult(decoded, IslandMapSource.Game);
            }

            if (NewestTerrainCache() is { } terrain
                && await MakeAsync($"terrain-{Hash($"{terrain.Name}|{terrain.Length}|{terrain.LastWriteTimeUtc.Ticks}|{TerrainVersion}")}.png", () => DrawTerrain(terrain.FullName), cancellationToken)
                    .ConfigureAwait(false) is { } drawn)
            {
                return new IslandMapPictureResult(drawn, IslandMapSource.Terrain);
            }
        }
        finally
        {
            _gate.Release();
        }

        return new IslandMapPictureResult(null, IslandMapSource.None);
    }

    /// <summary>
    /// The terrain seen from above: the triangles of <paramref name="meshes"/> (world cm, x/y/z triples and their
    /// indices) filled into a <paramref name="size"/>² height grid over the map square (the highest where parts overlap),
    /// lit from the top left; sea below <paramref name="seaLevelCm"/> and outside the land. RGBA8, top row first.
    /// </summary>
    internal static byte[] DrawTerrain(IEnumerable<(float[] Positions, uint[] Indices)> meshes, float seaLevelCm, int size)
    {
        var heights = new float[size * size];
        Array.Fill(heights, float.NaN);
        foreach (var (positions, indices) in meshes)
        {
            var pixels = new double[positions.Length / 3 * 2];
            for (var i = 0; i < pixels.Length / 2; i++)
            {
                var (u, v) = IslandMap.ToMap(positions[i * 3], positions[(i * 3) + 1]);
                (pixels[i * 2], pixels[(i * 2) + 1]) = (u * size, v * size);
            }

            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                FillTriangle(heights, size, pixels, positions, indices[t], indices[t + 1], indices[t + 2]);
            }
        }

        FillGaps(heights, size, passes: 2);
        var pixelCm = IslandMap.SizeCm / size;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var h = heights[(y * size) + x];
                var o = ((y * size) + x) * 4;
                rgba[o + 3] = 255;
                if (float.IsNaN(h) || h < seaLevelCm)
                {
                    var depth = float.IsNaN(h) ? 1f : Math.Clamp((seaLevelCm - h) / 3000f, 0f, 1f);
                    (rgba[o], rgba[o + 1], rgba[o + 2]) = ((byte)(38 - (14 * depth)), (byte)(62 - (22 * depth)), (byte)(78 - (24 * depth)));
                    continue;
                }

                // Slope towards the light (top left of the picture); flat ground is mid grey-green, high ground lighter.
                var left = Sample(heights, size, x - 1, y, h);
                var right = Sample(heights, size, x + 1, y, h);
                var up = Sample(heights, size, x, y - 1, h);
                var down = Sample(heights, size, x, y + 1, h);
                var light = Math.Clamp(0.72f + (((left - right) + (up - down)) / (2f * pixelCm) * 1.8f), 0.25f, 1.25f);
                var lift = Math.Clamp((h - seaLevelCm) / 40000f, 0f, 1f);
                (rgba[o], rgba[o + 1], rgba[o + 2]) = (Shade(92 + (70 * lift), light), Shade(104 + (58 * lift), light), Shade(78 + (72 * lift), light));
            }
        }

        return rgba;

        static byte Shade(float value, float light) => (byte)Math.Clamp(value * light, 0f, 255f);
    }

    /// <summary>True when <paramref name="path"/> is a whole PNG file (signature first, IEND chunk last).</summary>
    internal static bool IsCompletePng(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length < 45)
            {
                return false;
            }

            Span<byte> head = stackalloc byte[8];
            Span<byte> tail = stackalloc byte[12];
            file.ReadExactly(head);
            file.Seek(-12, SeekOrigin.End);
            file.ReadExactly(tail);
            return head.SequenceEqual(PngSignature) && tail.SequenceEqual(PngEnd);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> PngEnd => [0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();

    private static float Sample(float[] heights, int size, int x, int y, float fallback) =>
        x < 0 || y < 0 || x >= size || y >= size || float.IsNaN(heights[(y * size) + x]) ? fallback : heights[(y * size) + x];

    // Every pixel centre inside the triangle gets the height of its plane (barycentric weights from edge functions).
    private static void FillTriangle(float[] heights, int size, double[] pixels, float[] positions, uint a, uint b, uint c)
    {
        var (ax, ay, bx, by, cx, cy) = (pixels[a * 2], pixels[(a * 2) + 1], pixels[b * 2], pixels[(b * 2) + 1], pixels[c * 2], pixels[(c * 2) + 1]);
        var area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        if (Math.Abs(area) < 1e-12)
        {
            return;
        }

        var x0 = Math.Max(0, (int)Math.Ceiling(Math.Min(ax, Math.Min(bx, cx)) - 0.5));
        var x1 = Math.Min(size - 1, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx)) - 0.5));
        var y0 = Math.Max(0, (int)Math.Ceiling(Math.Min(ay, Math.Min(by, cy)) - 0.5));
        var y1 = Math.Min(size - 1, (int)Math.Floor(Math.Max(ay, Math.Max(by, cy)) - 0.5));
        var (az, bz, cz) = (positions[(a * 3) + 2], positions[(b * 3) + 2], positions[(c * 3) + 2]);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var (px, py) = (x + 0.5, y + 0.5);
                var wa = (((bx - px) * (cy - py)) - ((by - py) * (cx - px))) / area;
                var wb = (((cx - px) * (ay - py)) - ((cy - py) * (ax - px))) / area;
                var wc = 1 - wa - wb;
                if (wa < -1e-9 || wb < -1e-9 || wc < -1e-9)
                {
                    continue;
                }

                var z = (float)((wa * az) + (wb * bz) + (wc * cz));
                ref var h = ref heights[(y * size) + x];
                h = float.IsNaN(h) ? z : MathF.Max(h, z);
            }
        }
    }

    private static void FillGaps(float[] heights, int size, int passes)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            var next = (float[])heights.Clone();
            var filled = 0;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (!float.IsNaN(heights[(y * size) + x]))
                    {
                        continue;
                    }

                    var (sum, count) = (0f, 0);
                    foreach (var (dx, dy) in Neighbours)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (nx >= 0 && ny >= 0 && nx < size && ny < size && heights[(ny * size) + nx] is var n && !float.IsNaN(n))
                        {
                            (sum, count) = (sum + n, count + 1);
                        }
                    }

                    if (count >= 2)
                    {
                        next[(y * size) + x] = sum / count;
                        filled++;
                    }
                }
            }

            Array.Copy(next, heights, heights.Length);
            if (filled == 0)
            {
                return;
            }
        }
    }

    private static readonly (int, int)[] Neighbours = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    private (byte[] Rgba, int Width, int Height)? DrawTerrain(string cacheFile)
    {
        if (TerrainCache.TryLoad(cacheFile) is not { Terrain.Count: > 0 } scene)
        {
            return null;
        }

        return (DrawTerrain(scene.Terrain.Select(t => (t.Mesh.Positions, t.Mesh.Indices)), scene.SeaLevelCm ?? 0f, TerrainSize), TerrainSize, TerrainSize);
    }

    private FileInfo? NewestTerrainCache()
    {
        if (_terrainDirectory is null || !Directory.Exists(_terrainDirectory))
        {
            return null;
        }

        return new DirectoryInfo(_terrainDirectory).EnumerateFiles("island-terrain-*.bin").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>Returns the cached PNG <paramref name="name"/>, or makes it on a worker with <paramref name="produce"/> (null = none, remembered for the session).</summary>
    private async Task<string?> MakeAsync(string name, Func<(byte[] Rgba, int Width, int Height)?> produce, CancellationToken cancellationToken)
    {
        var path = Path.Combine(CacheDirectory, name);
        if (IsCompletePng(path))
        {
            return path;
        }

        if (_failed.Contains(path))
        {
            return null;
        }

        try
        {
            if (await Task.Run(produce, cancellationToken).ConfigureAwait(false) is not { } pixels)
            {
                _failed.Add(path);
                return null;
            }

            Directory.CreateDirectory(CacheDirectory);
            var temp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
            await using (var stream = File.Create(temp))
            {
                await PngWriter.WriteAsync(stream, pixels.Rgba, pixels.Width, pixels.Height, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _failed.Add(path);
            _logger.LogWarning("No island picture from {Name}: {Message}", name, ex.Message);
            return null;
        }
    }

    /// <summary>The game's map texture: a cache key (package file and size) and its decoder.</summary>
    internal sealed record GamePicture(string Key, Func<TextureImage?> Decode);
}
