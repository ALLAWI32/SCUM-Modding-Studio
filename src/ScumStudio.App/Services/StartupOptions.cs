using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.World;

namespace ScumStudio.App.Services;

/// <summary>
/// Developer / automation command line: <c>ScumStudio.App --source &lt;folder-or-paks&gt; --load &lt;sublevel|cell&gt; [--select &lt;actor&gt;]
/// [--page assets|weapons|vehicles --select &lt;name-or-path&gt;] [--screenshot out.png] [--exit-after seconds] [--mcp-port 47130]
/// [--mcp-token text]</c>. Opens a source without the setup dialog, loads a level or a whole cell into the map viewport
/// (or shows a page with an asset selected), optionally saves the viewport's rendered frame (map) or the window with the
/// 3D preview pasted in (other pages) and quits. Used to verify the real OpenGL path on machines without a desktop
/// session (e.g. under <c>xvfb-run</c>) and for owner screenshots.
/// </summary>
public sealed record StartupOptions(string? Source, string? Load, string? Screenshot, double? ExitAfterSeconds, string? Select = null)
{
    /// <summary><c>--mcp-port &lt;port&gt;</c>: runs the MCP server (AI control) on this port for this session.</summary>
    public int? McpPort { get; init; }

    /// <summary><c>--mcp-token &lt;token&gt;</c>: bearer token for this session instead of the stored one.</summary>
    public string? McpToken { get; init; }

    /// <summary><c>--page &lt;key&gt;</c>: shows this page (assets, weapons, vehicles …); <c>--select</c> then names the asset to show.</summary>
    public string? Page { get; init; }

    /// <summary><c>--search &lt;text&gt;</c>: filter run on the Assets page (<c>--select</c> then names the package to select among the results).</summary>
    public string? Search { get; init; }

    /// <summary><c>--view 3d</c>: the Vehicles/Weapons page opens on its 3D tab.</summary>
    public string? View { get; init; }

    /// <summary>Whether any option was given.</summary>
    public bool HasWork => Source is not null || Load is not null || Screenshot is not null || ExitAfterSeconds is not null || Page is not null;

    /// <summary>Parses the process arguments (unknown arguments are ignored).</summary>
    public static StartupOptions Parse(string[] args)
    {
        string? source = null, load = null, shot = null, select = null, mcpToken = null, page = null, search = null, view = null;
        int? mcpPort = null;
        double? exit = null;
        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--source" when next is not null: source = next; i++; break;
                case "--load" when next is not null: load = next; i++; break;
                case "--screenshot" when next is not null: shot = next; i++; break;
                case "--select" when next is not null: select = next; i++; break;
                case "--page" when next is not null: page = next; i++; break;
                case "--search" when next is not null: search = next; i++; break;
                case "--view" when next is not null: view = next; i++; break;
                case "--mcp-port" when next is not null && int.TryParse(next, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536: mcpPort = port; i++; break;
                case "--mcp-token" when next is not null: mcpToken = next; i++; break;
                case "--exit-after" when next is not null && double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s): exit = s; i++; break;
            }
        }

        return new StartupOptions(source, load, shot, exit, select) { McpPort = mcpPort, McpToken = mcpToken, Page = page, Search = search, View = view };
    }

    private bool ShowsPage => Page is { } key && !string.Equals(key, "map", StringComparison.OrdinalIgnoreCase);

    /// <summary>Executes the options on the UI thread after the main window opened.</summary>
    public async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, Window window, MainWindowViewModel viewModel, AppServices services)
    {
        var logger = services.Logger;
        try
        {
            if (Source is { } source)
            {
                logger.LogInformation("Startup: opening source {Source}.", source);
                if (Directory.Exists(source) && !Directory.EnumerateFiles(source, "*.pak").Any())
                {
                    await services.Workspace.OpenLooseAsync(source, ProgressSink.Null).ConfigureAwait(true);
                }
                else
                {
                    services.UpdateSettings(s => s with { GamePaksFolder = source });
                    await services.Workspace.ConnectAsync(ProgressSink.Null).ConfigureAwait(true);
                }

                // A source on the command line replaces the first-run setup.
                viewModel.IsSetupOpen = false;
                viewModel.Setup = null;
            }

            if (ShowsPage)
            {
                switch (viewModel.NavigateTo(Page!))
                {
                    case AssetsPageViewModel assets:
                        await assets.LoadCompletion.ConfigureAwait(true);
                        if ((Search ?? Select) is { } text)
                        {
                            await assets.SearchAsync(text).ConfigureAwait(true);
                        }

                        if (Select is { } wanted)
                        {
                            assets.SelectedItem = assets.Items.FirstOrDefault(i => string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase)) ?? assets.Items.FirstOrDefault();
                            if (assets.SelectedItem is null)
                            {
                                logger.LogWarning("Startup: no package matches {Text}.", wanted);
                            }

                            await assets.DetailsCompletion.ConfigureAwait(true);
                        }

                        break;
                    case ModulePageViewModel module:
                        await module.LoadCompletion.ConfigureAwait(true);
                        module.ShowPreviewTab = string.Equals(View, "3d", StringComparison.OrdinalIgnoreCase);
                        if (Select is { } name)
                        {
                            if (await module.SelectAsync(name).ConfigureAwait(true))
                            {
                                await module.PreviewCompletion.ConfigureAwait(true);
                            }
                            else
                            {
                                logger.LogWarning("Startup: {Name} is not listed on the {Page} page.", name, Page);
                            }
                        }

                        break;
                    case null:
                        logger.LogWarning("Startup: page {Key} not found.", Page);
                        break;
                }
            }

            if (Load is { } load)
            {
                var map = (MapPageViewModel)viewModel.NavigateTo("map")!;
                await map.LoadCompletion.ConfigureAwait(true);
                if (map.World is not { } world)
                {
                    logger.LogWarning("Startup: no world index; cannot load {Name}.", load);
                }
                else if (string.Equals(load, "island", StringComparison.OrdinalIgnoreCase))
                {
                    await map.OpenWholeIslandAsync().ConfigureAwait(true);
                }
                else if (MapCell.TryParse(load, out var cell) && world.InCell(cell).Any(p => p.IsMap))
                {
                    await map.LoadLevelsAsync(MapPageViewModel.CellPackages(world, cell), landscapeStep: 4).ConfigureAwait(true);
                }
                else if (world.Find(load) is { IsMap: true } package)
                {
                    await map.LoadLevelsAsync([package.PackagePath], landscapeStep: 1).ConfigureAwait(true);
                }
                else
                {
                    logger.LogWarning("Startup: level or cell {Name} not found.", load);
                }

                if (Select is { } select)
                {
                    map.SelectedActor = map.AllActors.FirstOrDefault(a => string.Equals(a.Name, select, StringComparison.OrdinalIgnoreCase));
                    if (map.SelectedActor is null)
                    {
                        logger.LogWarning("Startup: actor {Name} not found in the loaded levels.", select);
                    }
                }
            }

            if (Screenshot is { } pageShot && ShowsPage)
            {
                await CapturePageAsync(window, pageShot, logger).ConfigureAwait(true);
            }
            else if (Screenshot is { } shot)
            {
                var viewport = window.GetVisualDescendants().OfType<LevelViewport>().FirstOrDefault();
                if (viewport is null)
                {
                    logger.LogWarning("Startup: no viewport control found for the screenshot.");
                }
                else
                {
                    // Give the GL control a few frames to initialise and upload the scene.
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (!viewport.HasRenderedScene && DateTime.UtcNow < deadline)
                    {
                        viewport.RequestNextFrameRendering();
                        await Task.Delay(100).ConfigureAwait(true);
                    }

                    if (!viewport.IsGlReady)
                    {
                        logger.LogWarning("Startup: the OpenGL viewport never initialised (Avalonia is not rendering with OpenGL on this display; frame info: '{Info}'). No screenshot taken.", viewport.FrameInfo);
                    }
                    else if (!viewport.HasRenderedScene)
                    {
                        logger.LogWarning("Startup: the viewport initialised but drew no scene within 20 s (frame info: '{Info}'). No screenshot taken.", viewport.FrameInfo);
                    }
                    else
                    {
                        if (Select is not null && viewport.SelectedId != 0)
                        {
                            // Frame the selected actor so the gizmo is in view, then let a few frames render.
                            viewport.FrameSelection();
                            for (var i = 0; i < 5; i++)
                            {
                                viewport.RequestNextFrameRendering();
                                await Task.Delay(100).ConfigureAwait(true);
                            }
                        }

                        var saveTask = viewport.SaveScreenshotAsync(shot);
                        var finished = await Task.WhenAny(saveTask, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(true);
                        if (finished == saveTask)
                        {
                            var saved = await saveTask.ConfigureAwait(true);
                            logger.LogInformation("Startup: screenshot saved to {Path} (frame: {Info}).", saved, viewport.FrameInfo);
                            Console.Out.WriteLine(saved);
                        }
                        else
                        {
                            logger.LogWarning("Startup: screenshot timed out (no frame was rendered after the request).");
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Startup options failed: {Message}", ex.Message);
        }

        if (ExitAfterSeconds is { } seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, seconds))).ConfigureAwait(true);
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        }
    }

    /// <summary>Saves the window as PNG with the visible 3D preview's OpenGL frame pasted in (it is not part of a visual-tree render).</summary>
    private static async Task CapturePageAsync(Window window, string path, ILogger logger)
    {
        var preview = window.GetVisualDescendants().OfType<MeshPreview>().FirstOrDefault(p => p.IsEffectivelyVisible);
        byte[]? glPng = null;
        if (preview is { Model: not null })
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!preview.IsShowing(preview.Model) && DateTime.UtcNow < deadline)
            {
                preview.RequestNextFrameRendering();
                await Task.Delay(100).ConfigureAwait(true);
            }

            if (preview.IsShowing(preview.Model))
            {
                var temp = Path.Combine(Path.GetTempPath(), $"scumstudio-preview-{Guid.NewGuid():N}.png");
                var save = preview.SaveScreenshotAsync(temp);
                if (await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(true) == save)
                {
                    glPng = await File.ReadAllBytesAsync(await save.ConfigureAwait(true)).ConfigureAwait(true);
                    File.Delete(temp);
                }
            }
            else
            {
                logger.LogWarning("Startup: the preview did not draw its model within 20 s (GL ready: {Ready}, info: '{Info}').", preview.IsGlReady, preview.Info);
            }
        }

        // Tiles and rows fetch their pictures after they appear: wait until the number shown stops changing (2 s stable once
        // the first picture arrived or 4 s passed without one; 30 s at most).
        var shown = -1;
        for (int i = 0, stable = 0; i < 60 && stable < 4; i++)
        {
            await Task.Delay(500).ConfigureAwait(true);
            var now = window.GetVisualDescendants().OfType<ThumbnailImage>().Count(t => t.Source is not null);
            stable = now == shown && (now > 0 || i >= 8) ? stable + 1 : 0;
            shown = now;
        }

        // Controls that just became visible (image, captions) get their bounds in the next layout pass.
        await Task.Delay(250).ConfigureAwait(true);
        window.UpdateLayout();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, AppStudioUi.ComposeWindow(window, glPng is null ? null : preview, glPng)).ConfigureAwait(true);
        logger.LogInformation("Startup: screenshot saved to {Path}.", path);
        Console.Out.WriteLine(path);
    }
}
