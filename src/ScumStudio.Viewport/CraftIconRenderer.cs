using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Targets;

namespace ScumStudio.Viewport;

/// <summary>
/// Draws a craftable's 3D model as the picture its recipe shows in the crafting menu (Jenni and the owner, 2026-10-16: "a
/// bed must show a bed, a fridge a fridge"), like the game's own icons (<c>ICO_Chair_Improvised_Wood</c>: the object alone
/// on a transparent background, seen from the front left and a little above, filling the square): the mesh with its own
/// textures (<see cref="MeshPreviewLoader"/>), drawn off screen at twice the size with 4x MSAA on a clear background of
/// alpha 0, cropped to what was drawn and fitted into the square with a small margin. One hidden OpenGL context on a thread
/// of its own, made on the first picture; each picture is drawn once per renderer (the server cook reuses the client's).
/// </summary>
public sealed class CraftIconRenderer : IDisposable
{
    /// <summary>Pictures are drawn at this multiple of their size and shrunk (smooth edges).</summary>
    public const int Supersample = 2;

    /// <summary>The space left around the object, as a share of the picture's edge on each side.</summary>
    public const float Margin = 0.04f;

    private readonly AssetCatalog _catalog;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Lazy<TextureImage?>> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Action> _jobs = [];
    private readonly object _lock = new();
    private Thread? _thread;
    private SceneRenderer? _renderer;
    private RenderTarget? _target;
    private volatile string? _failure;
    private volatile bool _disposed;

    /// <summary>Creates a renderer of the meshes of <paramref name="catalog"/> (the client's: a server cook has no render data).</summary>
    public CraftIconRenderer(AssetCatalog catalog, ILogger? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The camera's yaw in degrees (UE convention; the thumbnails' -135 sees an object from its front left).</summary>
    public float Yaw { get; init; } = -135f;

    /// <summary>The camera's pitch in degrees (negative looks down).</summary>
    public float Pitch { get; init; } = -24f;

    /// <summary>Why this PC cannot draw (no OpenGL 4.3 context), or null.</summary>
    public string? Failure => _failure;

    /// <summary>
    /// The picture of <paramref name="request"/>'s mesh, <see cref="CraftPictureRequest.Size"/> pixels square, RGBA8 with a
    /// transparent background; null when it cannot be drawn (no mesh, no geometry, no OpenGL). Thread-safe.
    /// </summary>
    public TextureImage? Render(CraftPictureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = $"{request.ImportedFolder}|{request.Mesh}|{request.Size}";
        return _pictures.GetOrAdd(key, _ => new Lazy<TextureImage?>(() => Draw(request), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// The square picture of <paramref name="size"/> pixels made from a drawn frame (<paramref name="edge"/> pixels square,
    /// colours blended over a clear colour of alpha 0, so premultiplied): the colours taken back from the coverage, the frame
    /// cropped to what was drawn (alpha above 8) and fitted into the square with <see cref="Margin"/> around it. Null when
    /// nothing was drawn.
    /// </summary>
    public static byte[]? Compose(byte[] frame, int edge, int size)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var rgba = (byte[])frame.Clone();
        int minX = edge, minY = edge, maxX = -1, maxY = -1;
        for (var y = 0; y < edge; y++)
        {
            for (var x = 0; x < edge; x++)
            {
                var i = ((y * edge) + x) * 4;
                var a = rgba[i + 3];
                if (a is > 0 and < 255)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        rgba[i + c] = (byte)Math.Min(255, rgba[i + c] * 255 / a);
                    }
                }
                else if (a == 0)
                {
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = 0;
                }

                if (a > 8)
                {
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                }
            }
        }

        if (maxX < 0)
        {
            return null;
        }

        // The crop: a square around what was drawn, its margin included (transparent where it leaves the frame).
        var side = (int)MathF.Ceiling(Math.Max(maxX - minX + 1, maxY - minY + 1) / (1f - (2f * Margin)));
        var cx = (minX + maxX + 1) / 2;
        var cy = (minY + maxY + 1) / 2;
        var square = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        {
            var sy = cy - (side / 2) + y;
            if (sy < 0 || sy >= edge)
            {
                continue;
            }

            for (var x = 0; x < side; x++)
            {
                var sx = cx - (side / 2) + x;
                if (sx >= 0 && sx < edge)
                {
                    rgba.AsSpan(((sy * edge) + sx) * 4, 4).CopyTo(square.AsSpan(((y * side) + x) * 4));
                }
            }
        }

        return ImageFiles.Fit(new TextureImage("icon", side, side, square, "PF_R8G8B8A8", 0, side, side, true, false), size, size);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _jobs.CompleteAdding();
        _thread?.Join(TimeSpan.FromSeconds(10));
    }

    private TextureImage? Draw(CraftPictureRequest request)
    {
        if (_disposed || _failure is not null)
        {
            return null;
        }

        PreviewModel? model;
        try
        {
            using var own = request.ImportedFolder is { } folder && Directory.Exists(folder) ? AssetCatalog.OpenLoose(folder) : null;
            var mesh = PackageMap.Normalize(request.Mesh);
            model = new MeshPreviewLoader(own ?? _catalog, _logger) { TextureSize = 1024 }.LoadMesh(mesh + "." + PackageMap.Leaf(mesh));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Craftable picture: {Mesh} could not be loaded: {Message}", request.Mesh, ex.Message);
            return null;
        }

        if (model.Parts.Count == 0)
        {
            return null;
        }

        var edge = request.Size * Supersample;
        byte[]? frame;
        try
        {
            frame = OnGlThread(() => DrawFrame(model, edge));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Craftable picture: {Mesh} could not be drawn: {Message}", request.Mesh, ex.Message);
            return null;
        }

        return frame is not null && Compose(frame, edge, request.Size) is { } rgba
            ? new TextureImage(PackageMap.Leaf(request.Mesh), request.Size, request.Size, rgba, "PF_R8G8B8A8", 0, request.Size, request.Size, true, false)
            : null;
    }

    private byte[] DrawFrame(PreviewModel model, int edge)
    {
        var renderer = _renderer ?? throw new GlContextUnavailableException(_failure ?? "The picture renderer is not available.");
        if (_target is null || _target.Width != edge)
        {
            _target?.Dispose();
            _target = renderer.CreateTarget(edge, edge, samples: 4);
        }

        using var scene = PreviewScene.Upload(renderer, model);
        var bounds = scene.ModelBounds;
        if (bounds.IsEmpty)
        {
            throw new InvalidDataException("The mesh has no geometry.");
        }

        var camera = new FlyCamera();
        var distance = camera.Frame(bounds, 1f, Yaw, Pitch, margin: 1.02f);
        camera.FitClipRange(distance, MathF.Max(bounds.Extent.Length(), 1f));
        renderer.Settings = renderer.Settings with { LightDirection = Vector3.Normalize(camera.Forward - (camera.Up * 0.9f) + (camera.Right * 0.45f)) };
        renderer.Render(_target, scene.Scene, camera);
        return _target.ReadColorRgba();
    }

    /// <summary>Runs <paramref name="work"/> on the GL thread (started on first use) and waits for it.</summary>
    private T OnGlThread<T>(Func<T> work)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is null)
            {
                _thread = new Thread(GlLoop) { IsBackground = true, Name = "ScumStudio craftable pictures (GL)" };
                _thread.Start();
            }
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(() =>
        {
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    private void GlLoop()
    {
        OffscreenGlContext? context = null;
        try
        {
            if (OffscreenGlContext.TryCreate(64, 64, out context, out var reason))
            {
                _renderer = new SceneRenderer(context!);
                _renderer.Settings = _renderer.Settings with
                {
                    ShowGrid = false,
                    ClearColor = Vector4.Zero,
                    SkyColor = new Vector3(0.82f, 0.82f, 0.84f),
                    GroundColor = new Vector3(0.38f, 0.37f, 0.35f),
                    LightColor = new Vector3(1.2f, 1.17f, 1.1f),
                };
            }
            else
            {
                _failure = reason ?? "no OpenGL 4.3 context";
                _logger.LogWarning("Craftable pictures cannot be drawn: {Reason}", _failure);
            }

            foreach (var job in _jobs.GetConsumingEnumerable())
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
}
