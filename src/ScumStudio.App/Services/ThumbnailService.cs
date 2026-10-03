using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Targets;
using ScumStudio.Viewport;

namespace ScumStudio.App.Services;

/// <summary>
/// Small PNG previews for the asset tiles and list rows: a texture's small mip, a material's base-colour texture, or a
/// mesh drawn with its textures through one shared off-screen OpenGL context on a worker thread. Made only when a tile
/// on screen asks, kept in <c>cache/thumbnails/&lt;hash&gt;.png</c> keyed by package path and file size (a game update
/// refreshes them), so a second visit costs one small PNG read.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    /// <summary>Largest edge of a thumbnail in pixels.</summary>
    public const int Size = 192;

    /// <summary>Meshes render at twice the size and shrink (4 samples a pixel): smooth edges, sharper detail.</summary>
    private const int RenderSize = Size * 2;

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _decodeGate = new(2, 2);
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);
    private readonly BlockingCollection<Action> _glJobs = [];
    private readonly object _glLock = new();
    private Thread? _glThread;
    private SceneRenderer? _renderer;
    private RenderTarget? _target;
    private volatile string? _glFailure;
    private volatile bool _disposed;

    /// <summary>Creates the service writing into <paramref name="directory"/>.</summary>
    public ThumbnailService(string directory, ILogger logger)
    {
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Cache folder.</summary>
    public string Directory { get; }

    /// <summary>True when packages of <paramref name="className"/> get a thumbnail (textures, materials, meshes).</summary>
    public static bool Supports(string? className) =>
        AssetExportService.IsTextureClass(className) || AssetExportService.IsMaterialClass(className) || AssetExportService.IsMeshClass(className)
        || AssetExportService.IsBlueprintClass(className);

    /// <summary>The thumbnail PNG of <paramref name="entry"/> (its class must be resolved), or null when it has none.</summary>
    public Task<string?> GetAssetAsync(AssetCatalog catalog, PackageEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(entry);
        var className = entry.ClassName;
        if (!Supports(className) || !catalog.TryGetPackageFile(entry.PackagePath, out var file))
        {
            return Task.FromResult<string?>(null);
        }

        var path = CacheFile(entry.PackagePath, file.Size);
        if (AssetExportService.IsMeshClass(className) || AssetExportService.IsBlueprintClass(className))
        {
            var blueprint = AssetExportService.IsBlueprintClass(className);
            return _glFailure is not null
                ? Task.FromResult<string?>(null)
                : MakeAsync(path, entry.Name, async ct =>
                {
                    var model = await DecodeAsync(() =>
                    {
                        var loader = new MeshPreviewLoader(catalog, _logger) { TextureSize = 512 };
                        return blueprint ? loader.LoadBlueprint(entry.PackagePath) : loader.LoadMesh(entry.ObjectPath);
                    }, ct).ConfigureAwait(false);
                    return model is null || model.Parts.Count == 0 ? null : (await RenderAsync(model, ct).ConfigureAwait(false), RenderSize, RenderSize);
                }, cancellationToken);
        }

        return MakeAsync(path, entry.Name, async ct =>
        {
            var image = await DecodeAsync(() =>
            {
                if (AssetExportService.IsTextureClass(className))
                {
                    return catalog.LoadFirstExport<UTexture2D>(entry.PackagePath) is { } texture ? TextureDecoder.Decode(texture, maxSize: Size) : null;
                }

                var baseColor = new MaterialInspector(catalog).Inspect(entry.ObjectPath).BaseColorTexture;
                return baseColor is null ? null : TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(baseColor), maxSize: Size);
            }, ct).ConfigureAwait(false);
            return image is null ? null : (image.Rgba, image.Width, image.Height);
        }, cancellationToken);
    }

    /// <summary>The thumbnail PNG of the texture at <paramref name="objectPath"/> (inventory icons), or null.</summary>
    public Task<string?> GetTextureAsync(AssetCatalog catalog, string objectPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrEmpty(objectPath);
        var dot = objectPath.IndexOf('.', objectPath.LastIndexOf('/') + 1);
        var package = dot < 0 ? objectPath : objectPath[..dot];
        if (!catalog.TryGetPackageFile(package, out var file))
        {
            return Task.FromResult<string?>(null);
        }

        return MakeAsync(CacheFile(objectPath, file.Size), objectPath, async ct =>
        {
            var image = await DecodeAsync(() => TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(objectPath), maxSize: Size), ct).ConfigureAwait(false);
            return (image.Rgba, image.Width, image.Height);
        }, cancellationToken);
    }

    /// <summary>Halves the image until its largest edge is at most <paramref name="max"/> (2×2 box filter).</summary>
    internal static (byte[] Rgba, int Width, int Height) Shrink(byte[] rgba, int width, int height, int max)
    {
        while (width > max || height > max)
        {
            var w = Math.Max(1, width / 2);
            var h = Math.Max(1, height / 2);
            var next = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                var sy = Math.Min(height - 1, y * 2);
                var sy1 = Math.Min(height - 1, sy + 1);
                for (var x = 0; x < w; x++)
                {
                    var sx = Math.Min(width - 1, x * 2);
                    var sx1 = Math.Min(width - 1, sx + 1);
                    for (var c = 0; c < 4; c++)
                    {
                        next[(((y * w) + x) * 4) + c] = (byte)((rgba[(((sy * width) + sx) * 4) + c] + rgba[(((sy * width) + sx1) * 4) + c]
                            + rgba[(((sy1 * width) + sx) * 4) + c] + rgba[(((sy1 * width) + sx1) * 4) + c]) / 4);
                    }
                }
            }

            (rgba, width, height) = (next, w, h);
        }

        return (rgba, width, height);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _glJobs.CompleteAdding();
        _glThread?.Join(TimeSpan.FromSeconds(5));
    }

    // Bump RenderVersion when thumbnails render differently (v2: masked leaves cut out), so stale ones are made again.
    private const string RenderVersion = "3"; // v3: Blueprints, 2x supersampled

    private string CacheFile(string key, long size) =>
        Path.Combine(Directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + size + "|" + RenderVersion)))[..24] + ".png");

    /// <summary>Returns the cached PNG, or makes it with <paramref name="produce"/> (null pixels = no thumbnail, remembered for the session).</summary>
    private async Task<string?> MakeAsync(string path, string name, Func<CancellationToken, Task<(byte[] Rgba, int Width, int Height)?>> produce, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (_failed.ContainsKey(path) || _disposed)
        {
            return null;
        }

        try
        {
            if (await produce(cancellationToken).ConfigureAwait(false) is not { } pixels)
            {
                _failed[path] = 0;
                return null;
            }

            var (rgba, width, height) = Shrink(pixels.Rgba, pixels.Width, pixels.Height, Size);
            System.IO.Directory.CreateDirectory(Directory);
            var temp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
            await using (var stream = File.Create(temp))
            {
                await PngWriter.WriteAsync(stream, rgba, width, height, cancellationToken).ConfigureAwait(false);
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
            _failed[path] = 0;
            _logger.LogDebug("No thumbnail for {Name}: {Message}", name, ex.Message);
            return null;
        }
    }

    /// <summary>Runs CPU decoding on the pool, at most two at a time (CUE4Parse reads are the bottleneck, not cores).</summary>
    private async Task<T> DecodeAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>Draws <paramref name="model"/> on the GL thread (started on first use) into a <see cref="Size"/>² frame.</summary>
    private Task<byte[]> RenderAsync(PreviewModel model, CancellationToken cancellationToken)
    {
        lock (_glLock)
        {
            if (_disposed)
            {
                return Task.FromCanceled<byte[]>(new CancellationToken(true));
            }

            if (_glThread is null)
            {
                _glThread = new Thread(GlLoop) { IsBackground = true, Name = "ScumStudio thumbnails (GL)" };
                _glThread.Start();
            }
        }

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _glJobs.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            if (_renderer is null || _target is null)
            {
                tcs.TrySetException(new GlContextUnavailableException(_glFailure ?? "The thumbnail renderer is not available."));
                return;
            }

            try
            {
                tcs.TrySetResult(Render(model));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private void GlLoop()
    {
        OffscreenGlContext? context = null;
        try
        {
            if (OffscreenGlContext.TryCreate(Size, Size, out context, out var reason))
            {
                _renderer = new SceneRenderer(context!);
                _renderer.Settings = _renderer.Settings with
                {
                    ShowGrid = false,
                    SkyColor = new Vector3(0.5f, 0.52f, 0.56f),
                    GroundColor = new Vector3(0.2f, 0.19f, 0.17f),
                    LightColor = new Vector3(0.72f, 0.7f, 0.64f),
                };
                _target = _renderer.CreateTarget(RenderSize, RenderSize);
            }
            else
            {
                _glFailure = reason ?? "no OpenGL 4.3 context";
                _logger.LogWarning("Mesh thumbnails are off: {Reason}", _glFailure);
            }

            foreach (var job in _glJobs.GetConsumingEnumerable())
            {
                job();
            }
        }
        finally
        {
            _target?.Dispose();
            _renderer?.Dispose();
            context?.Dispose();
            _target = null;
            _renderer = null;
        }
    }

    private byte[] Render(PreviewModel model)
    {
        var renderer = _renderer!;
        using var scene = PreviewScene.Upload(renderer, model);
        if (scene.Bounds.IsEmpty)
        {
            throw new InvalidDataException("The mesh has no geometry.");
        }

        var camera = new FlyCamera();
        var distance = camera.Frame(scene.Bounds, 1f, -135f, -20f, margin: 1.05f);
        camera.FitClipRange(distance, MathF.Max(scene.Bounds.Extent.Length(), 1f));
        renderer.Settings = renderer.Settings with { LightDirection = Vector3.Normalize(camera.Forward - (camera.Up * 0.8f) + (camera.Right * 0.35f)) };
        renderer.Render(_target!, scene.Scene, camera);
        return _target!.ReadColorRgba();
    }
}
