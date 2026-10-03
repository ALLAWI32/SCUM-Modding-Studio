using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Catalog;
using ScumStudio.Mcp.Studio;

namespace ScumStudio.App.Services;

/// <summary>
/// <see cref="IStudioUi"/> over the main window: pages, the Map page's 3D viewport (levels, selection, camera,
/// screenshots of the real OpenGL frame) and the Vehicles/Weapons pages. Every call runs on the UI thread.
/// </summary>
public sealed class AppStudioUi : IStudioUi
{
    private static readonly TimeSpan GlGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SceneTimeout = TimeSpan.FromSeconds(30);
    private readonly AppServices _services;
    private readonly MainWindowViewModel _shell;
    private readonly Func<TopLevel?> _window;

    /// <summary>Creates the UI bridge for <paramref name="shell"/> shown in <paramref name="window"/>.</summary>
    public AppStudioUi(AppServices services, MainWindowViewModel shell, Func<TopLevel?> window)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>The Map page if it was created (it is created on first navigation).</summary>
    public MapPageViewModel? MapIfCreated =>
        _shell.NavItems.FirstOrDefault(n => n.Key == "map") is { IsPageCreated: true } item ? item.Page as MapPageViewModel : null;

    /// <inheritdoc />
    public Task<StudioUiState> GetStateAsync(CancellationToken cancellationToken) => OnUi(() =>
    {
        var map = MapIfCreated;
        var levels = map?.PreparedScene?.Documents.Select(d => d.PackagePath).ToList() ?? [];
        FVector? location = null;
        float yaw = 0f, pitch = 0f;
        if (map?.PreparedScene is not null && FindViewport() is { IsGlReady: true } viewport)
        {
            location = UeToGl.ToUePoint(viewport.Camera.Position);
            yaw = viewport.Camera.Yaw;
            pitch = viewport.Camera.Pitch;
        }

        return Task.FromResult(new StudioUiState(_shell.CurrentPage?.Key ?? string.Empty, levels, map?.SelectedActor?.Reference, location, yaw, pitch));
    });

    /// <inheritdoc />
    public Task NavigateAsync(string page, CancellationToken cancellationToken) => OnUi(() =>
    {
        if (_shell.NavigateTo(page) is null)
        {
            throw new InvalidOperationException("Unknown page: " + page);
        }

        return Task.FromResult(true);
    });

    /// <inheritdoc />
    public Task<string> ShowLevelsAsync(IReadOnlyList<string> packagePaths, int landscapeStep, CancellationToken cancellationToken) => OnUi(async () =>
    {
        if (_services.Workspace.Catalog is null)
        {
            throw new InvalidOperationException("No game files are open: call open_source first.");
        }

        var map = ShowMap();
        await map.LoadCompletion.ConfigureAwait(true);
        await map.LoadLevelsAsync(packagePaths, landscapeStep).ConfigureAwait(true);
        if (map.PreparedScene is not { } scene ||
            !packagePaths.All(p => scene.Documents.Any(d => string.Equals(d.PackagePath, p, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(map.LoadStatus.Length > 0 ? map.LoadStatus : "The levels could not be loaded.");
        }

        var hidden = map.AllActors.Count(a => a.IsDeleted);
        return string.Create(CultureInfo.InvariantCulture, $"Showing {scene.Documents.Count} level(s) in the 3D viewport: {map.LoadStatus}.") +
               (hidden > 0 ? string.Create(CultureInfo.InvariantCulture, $" {hidden} actor(s) deleted by the project are hidden.") : string.Empty) +
               " Use screenshot to look at it, set_camera / select_actor to move the view.";
    });

    /// <inheritdoc />
    public Task<string> SelectActorAsync(ActorRef actor, bool frame, CancellationToken cancellationToken) => OnUi(async () =>
    {
        var map = ShowMap();
        var item = map.AllActors.FirstOrDefault(a => ActorRef.Comparer.Equals(a.Reference, actor));
        if (item is null)
        {
            var shown = map.PreparedScene?.Documents.Any(d => string.Equals(d.PackagePath, actor.Level, StringComparison.OrdinalIgnoreCase)) == true;
            throw new InvalidOperationException(shown
                ? $"Actor '{actor.Actor}' is not in {ShortName(actor.Level)} (use list_actors)."
                : $"Level {ShortName(actor.Level)} is not shown in the viewport: call show_levels first.");
        }

        map.EntityFilter = string.Empty;
        map.SelectedActor = item;
        var framed = false;
        if (frame && await ReadyViewportAsync(map, cancellationToken).ConfigureAwait(true) is { } viewport)
        {
            framed = viewport.FrameSelection();
        }

        return $"Selected {item.Name} ({item.ClassName}) in {item.LevelName} at {item.LocationText}" +
               (item.IsDeleted ? " — deleted by the project (hidden in the viewport)" : string.Empty) +
               (framed ? "; the camera frames it." : ".");
    });

    /// <inheritdoc />
    public Task<string> SetCameraAsync(FVector? location, FVector? lookAt, float? yaw, float? pitch, bool frameAll, CancellationToken cancellationToken) => OnUi(async () =>
    {
        var map = ShowMap();
        if (map.PreparedScene is null)
        {
            throw new InvalidOperationException("No levels are shown: call show_levels first.");
        }

        var viewport = await ReadyViewportAsync(map, cancellationToken).ConfigureAwait(true)
                       ?? throw new InvalidOperationException("The 3D viewport is not available (OpenGL 4.3 is required).");
        if (frameAll)
        {
            viewport.FrameAll();
        }

        if (location is not null || lookAt is not null || yaw is not null || pitch is not null)
        {
            viewport.SetView(location, lookAt, yaw, pitch);
        }

        var at = UeToGl.ToUePoint(viewport.Camera.Position);
        return string.Create(CultureInfo.InvariantCulture,
            $"Camera at ({at.X:0}, {at.Y:0}, {at.Z:0}) yaw {viewport.Camera.Yaw:0.#} pitch {viewport.Camera.Pitch:0.#}.");
    });

    /// <inheritdoc />
    public Task<byte[]?> CaptureViewportAsync(CancellationToken cancellationToken) => OnUi(async () =>
    {
        if (MapIfCreated is not { PreparedScene: not null } map)
        {
            return null;
        }

        if (!ReferenceEquals(_shell.CurrentPage, map))
        {
            _shell.NavigateTo("map");
        }

        return await CaptureViewportPngAsync(map, cancellationToken).ConfigureAwait(true);
    });

    /// <inheritdoc />
    public Task<byte[]?> CaptureWindowAsync(CancellationToken cancellationToken) => OnUi(async () =>
    {
        if (_window() is not { } top || top.ClientSize.Width < 1 || top.ClientSize.Height < 1)
        {
            return null;
        }

        // The OpenGL viewport is not part of a visual-tree render: its frame is captured separately and put where the
        // window render shows the viewport's plain background (so captions, buttons and toasts over it stay visible).
        LevelViewport? glViewport = null;
        byte[]? viewportPng = null;
        var origin = default(Point);
        if (ReferenceEquals(_shell.CurrentPage, MapIfCreated) && MapIfCreated is { PreparedScene: not null } map &&
            !_shell.IsSetupOpen && FindViewport() is { IsGlReady: true } viewport && viewport.TranslatePoint(default, top) is { } at)
        {
            viewportPng = await CaptureViewportPngAsync(map, cancellationToken).ConfigureAwait(true);
            glViewport = viewport;
            origin = at;
        }

        return ComposeWindow(top, glViewport, viewportPng);
    });

    /// <summary>
    /// Renders <paramref name="top"/> as PNG. An OpenGL control (<paramref name="glControl"/>) is not part of a visual-tree
    /// render, so its own frame (<paramref name="glPng"/>) is pasted where the window render shows the control's plain
    /// background, keeping the captions, buttons and toasts drawn over it.
    /// </summary>
    internal static byte[] ComposeWindow(TopLevel top, Control? glControl, byte[]? glPng)
    {
        var scaling = top.RenderScaling;
        var pixels = new PixelSize(Math.Max(1, (int)Math.Round(top.ClientSize.Width * scaling)), Math.Max(1, (int)Math.Round(top.ClientSize.Height * scaling)));
        var dpi = new Vector(96 * scaling, 96 * scaling);
        using var window = new RenderTargetBitmap(pixels, dpi);
        window.Render(top);
        if (glPng is null || glControl is null || glControl.TranslatePoint(default, top) is not { } origin)
        {
            return Encode(window);
        }

        // Second render without the viewport's overlays: pixels that match the first render are background.
        using var background = new RenderTargetBitmap(pixels, dpi);
        var overlays = (glControl.Parent as Panel)?.Children.Where(c => !ReferenceEquals(c, glControl) && c.IsVisible).ToList() ?? [];
        foreach (var overlay in overlays)
        {
            overlay.IsVisible = false;
        }

        try
        {
            background.Render(top);
        }
        finally
        {
            foreach (var overlay in overlays)
            {
                overlay.IsVisible = true;
            }
        }

        using var frame = new Bitmap(new MemoryStream(glPng));
        var shown = ReadPixels(window, out var stride);
        var plain = ReadPixels(background, out _);
        var gl = ReadPixels(frame, out var glStride);
        var x0 = (int)Math.Round(origin.X * scaling);
        var y0 = (int)Math.Round(origin.Y * scaling);
        var width = Math.Min(frame.PixelSize.Width, pixels.Width - x0);
        var height = Math.Min(frame.PixelSize.Height, pixels.Height - y0);
        for (var y = Math.Max(0, -y0); y < height; y++)
        {
            for (var x = Math.Max(0, -x0); x < width; x++)
            {
                var i = ((y0 + y) * stride) + ((x0 + x) * 4);
                if (shown.AsSpan(i, 4).SequenceEqual(plain.AsSpan(i, 4)))
                {
                    gl.AsSpan((y * glStride) + (x * 4), 4).CopyTo(shown.AsSpan(i, 4));
                }
            }
        }

        using var composed = new WriteableBitmap(pixels, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = composed.Lock())
        {
            for (var y = 0; y < pixels.Height; y++)
            {
                Marshal.Copy(shown, y * stride, buffer.Address + (y * buffer.RowBytes), Math.Min(stride, buffer.RowBytes));
            }
        }

        return Encode(composed);
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var output = new MemoryStream();
        bitmap.Save(output);
        return output.ToArray();
    }

    /// <summary>The pixels of <paramref name="bitmap"/> as premultiplied BGRA rows.</summary>
    private static byte[] ReadPixels(Bitmap bitmap, out int stride)
    {
        using var copy = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock();
        bitmap.CopyPixels(buffer, AlphaFormat.Premul);
        stride = buffer.RowBytes;
        var bytes = new byte[stride * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <inheritdoc />
    public Task<string> ShowItemAsync(string packagePath, CancellationToken cancellationToken) => OnUi(async () =>
    {
        var kind = ModdableAssets.Classify(packagePath)?.Kind;
        var key = kind == ModdableKind.Vehicle ? "vehicles" : "weapons";
        if (_shell.NavigateTo(key) is not ModulePageViewModel page)
        {
            throw new InvalidOperationException("The " + key + " page is not available.");
        }

        var name = ShortName(packagePath);
        if (!await page.SelectAsync(name).ConfigureAwait(true))
        {
            throw new InvalidOperationException($"{name} is not listed on the {page.Title} page.");
        }

        return $"Showing {name} on the {page.Title} page ({page.ValuesSummary}).";
    });

    private MapPageViewModel ShowMap() =>
        _shell.NavigateTo("map") as MapPageViewModel ?? throw new InvalidOperationException("The Map page is not available.");

    private LevelViewport? FindViewport() =>
        _window()?.GetVisualDescendants().OfType<LevelViewport>().FirstOrDefault(v => v.IsEffectivelyVisible);

    /// <summary>
    /// The viewport once it shows <paramref name="map"/>'s current scene, or null when OpenGL is unavailable (it never
    /// initialised within a short grace period) or the scene was not drawn in time.
    /// </summary>
    private async Task<LevelViewport?> ReadyViewportAsync(MapPageViewModel map, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var viewport = FindViewport();
            if (viewport is not null && viewport.IsGlReady && viewport.IsShowing(map.PreparedScene))
            {
                return viewport;
            }

            var waited = DateTime.UtcNow - started;
            if (waited > SceneTimeout || (waited > GlGrace && viewport is not { IsGlReady: true }))
            {
                return null;
            }

            viewport?.RequestNextFrameRendering();
            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task<byte[]?> CaptureViewportPngAsync(MapPageViewModel map, CancellationToken cancellationToken)
    {
        if (await ReadyViewportAsync(map, cancellationToken).ConfigureAwait(true) is not { } viewport)
        {
            return null;
        }

        var file = Path.Combine(Path.GetTempPath(), "scumstudio-mcp-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var save = viewport.SaveScreenshotAsync(file);
            if (await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)).ConfigureAwait(true) != save)
            {
                return null;
            }

            await save.ConfigureAwait(true);
            return await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static Task<T> OnUi<T>(Func<Task<T>> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);

    private static string ShortName(string path)
    {
        var p = path.TrimEnd('/');
        var name = p[(p.LastIndexOf('/') + 1)..];
        var dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }
}
