using Microsoft.Extensions.Logging;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Level.World;
using ScumStudio.Mcp.Studio;

namespace ScumStudio.App.Services;

/// <summary>
/// <see cref="IStudioHost"/> of the desktop app: the MCP tools work on the same catalog, project session and journal as the
/// pages, so every change an AI makes shows up immediately (map viewport, entity list, history, Vehicles/Weapons values) and
/// can be undone from the toolbar. Journal changes are marshalled to the UI thread; the AES key stays in the key store.
/// </summary>
public sealed class AppStudioHost : IStudioHost
{
    private readonly AppServices _services;
    private readonly Func<MapPageViewModel?> _map;
    private readonly object _worldGate = new();
    private (AssetCatalog Catalog, WorldIndex? World)? _world;

    /// <summary>Creates the host. <paramref name="map"/> returns the Map page when it exists (its tile-aware world index is reused).</summary>
    public AppStudioHost(AppServices services, Func<MapPageViewModel?>? map = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _map = map ?? (() => null);
    }

    /// <summary>Raised (on the calling thread) after each tool call.</summary>
    public event EventHandler<StudioActivity>? Activity;

    /// <inheritdoc />
    public string Kind => "app";

    /// <inheritdoc />
    public AssetCatalog? Catalog => _services.Workspace.Catalog;

    /// <inheritdoc />
    public WorldIndex? World
    {
        get
        {
            if (Catalog is not { } catalog)
            {
                return null;
            }

            if (_map() is { World: { } shown } && ReferenceEquals(_services.Workspace.Catalog, catalog))
            {
                return shown;
            }

            lock (_worldGate)
            {
                if (_world is not { } cached || !ReferenceEquals(cached.Catalog, catalog))
                {
                    var index = WorldIndex.FromCatalog(catalog);
                    cached = (catalog, index.Packages.Count == 0 ? null : index);
                    _world = cached;
                }

                return cached.World;
            }
        }
    }

    /// <inheritdoc />
    public Project? Project => _services.Projects.Current;

    /// <inheritdoc />
    public IStudioUi? Ui { get; set; }

    /// <inheritdoc />
    public async Task<string> OpenSourceAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (string.IsNullOrWhiteSpace(_services.Settings.Load().GamePaksFolder))
            {
                throw new InvalidOperationException("No game Paks folder is configured in ScumStudio: pass 'path' (the game's SCUM\\Content\\Paks folder or an extracted folder).");
            }

            await _services.Workspace.ConnectAsync(ProgressSink.Null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _services.Workspace.OpenPathAsync(path.Trim().Trim('"'), ProgressSink.Null, cancellationToken).ConfigureAwait(false);
        }

        var ws = _services.Workspace;
        if (ws.Catalog is null)
        {
            throw new InvalidOperationException(ws.StatusText);
        }

        return $"Opened {ws.Catalog.DisplayName}: {ws.StatusText}." + (ws.LockedContainers > 0
            ? $" {ws.LockedContainers} encrypted pak(s) could not be mounted: the user must enter the AES key in ScumStudio's setup (it is never sent to the AI)."
            : string.Empty);
    }

    /// <inheritdoc />
    public async Task<Project> CreateProjectAsync(string parentFolder, string name, CancellationToken cancellationToken)
    {
        var project = await _services.Projects.CreateAsync(parentFolder, name, cancellationToken).ConfigureAwait(false);
        await UiRoundTripAsync().ConfigureAwait(false);
        return project;
    }

    /// <inheritdoc />
    public async Task<Project> OpenProjectAsync(string path, CancellationToken cancellationToken)
    {
        var project = await _services.Projects.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        await UiRoundTripAsync().ConfigureAwait(false);
        if (Catalog is null && project.Manifest.Sources.FirstOrDefault(s => s.Role == ProjectSourceRole.Client) is { } source
            && (File.Exists(source.Path) || Directory.Exists(source.Path)))
        {
            await OpenSourceAsync(source.Path, cancellationToken).ConfigureAwait(false);
        }

        return project;
    }

    /// <inheritdoc />
    public JournalEntry Apply(EditOp op) => OnUiThread(() => _services.Projects.Apply(op));

    /// <inheritdoc />
    public JournalEntry? Undo() => OnUiThread(() => _services.Projects.Undo());

    /// <inheritdoc />
    public JournalEntry? Redo() => OnUiThread(() => _services.Projects.Redo());

    /// <inheritdoc />
    public Task<IReadOnlyList<ExportResult>> ExportAsync(ExportOptions options, bool includeServer, CancellationToken cancellationToken)
    {
        var project = Project ?? throw new InvalidOperationException("No project is open.");
        var request = new ModExportRequest(project, options.OutputDirectory, options.ModName, includeServer, options.PakChunkIndex);
        return ModExportService.ExportAsync(_services, request, ProgressSink.Null, cancellationToken);
    }

    /// <inheritdoc />
    public void ReportActivity(string tool, string summary, bool isError)
    {
        if (isError)
        {
            _services.Logger.LogWarning("AI tool {Tool} failed: {Summary}", tool, summary);
        }
        else
        {
            _services.Logger.LogInformation("AI tool {Tool}: {Summary}", tool, summary);
        }

        Activity?.Invoke(this, new StudioActivity(tool, summary, isError, DateTimeOffset.Now));
    }

    /// <summary>
    /// Completes after the UI thread ran everything posted before it: <see cref="ProjectSession"/> publishes a new project
    /// through a posted update, so the next tool call must not run before <see cref="Project"/> shows it.
    /// </summary>
    private Task UiRoundTripAsync() => _services.Dispatcher.InvokeAsync(() => { });

    private T OnUiThread<T>(Func<T> work) =>
        _services.Dispatcher.CheckAccess() ? work() : _services.Dispatcher.InvokeAsync(work).GetAwaiter().GetResult();
}

/// <summary>One tool call of an AI client.</summary>
/// <param name="Tool">Tool name.</param>
/// <param name="Summary">Short result text.</param>
/// <param name="IsError">True when the call failed.</param>
/// <param name="At">Local time.</param>
public sealed record StudioActivity(string Tool, string Summary, bool IsError, DateTimeOffset At);
