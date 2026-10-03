using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.FileProvider.Vfs;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Security;
using ScumStudio.Core.Settings;

namespace ScumStudio.App.Services;

/// <summary>Connection state of the <see cref="GameWorkspace"/>.</summary>
public enum WorkspaceState
{
    /// <summary>No game paks folder configured and no loose folder open.</summary>
    NotConfigured,

    /// <summary>Opening the source.</summary>
    Connecting,

    /// <summary>A catalog is open.</summary>
    Connected,

    /// <summary>The last attempt failed (see <see cref="GameWorkspace.StatusText"/>).</summary>
    Error,
}

/// <summary>
/// Owns the <see cref="AssetCatalog"/> every page reads from: the game's paks (with the stored AES key) or a loose
/// extracted folder. Raises <see cref="CatalogChanged"/> on the UI thread whenever the catalog is replaced.
/// </summary>
public sealed partial class GameWorkspace : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ProtectedKeyStore _keys;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the workspace (nothing is opened until <see cref="ConnectAsync"/>).</summary>
    public GameWorkspace(SettingsStore settings, ProtectedKeyStore keys, IUiDispatcher dispatcher, ILogger logger)
    {
        _settings = settings;
        _keys = keys;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Raised on the UI thread after <see cref="Catalog"/> changed (also when it became null).</summary>
    public event EventHandler? CatalogChanged;

    /// <summary>Connection state.</summary>
    [ObservableProperty]
    private WorkspaceState _state = WorkspaceState.NotConfigured;

    /// <summary>Human-readable status ("12 paks, 245,113 packages").</summary>
    [ObservableProperty]
    private string _statusText = Localization.Loc.T("Workspace.NotConfigured");

    /// <summary>The open catalog, or null.</summary>
    [ObservableProperty]
    private AssetCatalog? _catalog;

    /// <summary>Number of mounted containers (paks) or 0 for loose folders.</summary>
    [ObservableProperty]
    private int _mountedContainers;

    /// <summary>Encrypted containers that could not be mounted (missing or wrong key).</summary>
    [ObservableProperty]
    private int _lockedContainers;

    /// <summary>Number of packages in the catalog.</summary>
    [ObservableProperty]
    private int _packageCount;

    /// <summary>Folder the catalog was opened from.</summary>
    [ObservableProperty]
    private string? _sourcePath;

    /// <summary>True when the catalog is a loose folder rather than the game paks.</summary>
    [ObservableProperty]
    private bool _isLoose;

    /// <summary>Imported mods (the open project's, see <see cref="Level.Projects.ProjectMods"/>) read over the game files.</summary>
    public IReadOnlyList<string> ModFolders { get; private set; } = [];

    /// <summary>
    /// Reads <paramref name="folders"/> over the game files from now on: re-opens the game paks when they are open and the
    /// list changed.
    /// </summary>
    public async Task UseModsAsync(IReadOnlyList<string> folders, IProgressSink progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (folders.SequenceEqual(ModFolders, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        ModFolders = folders.ToList();
        if (Catalog is not null && !IsLoose)
        {
            await ConnectAsync(progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens the configured game paks folder with the stored key. Leaves the workspace <see cref="WorkspaceState.NotConfigured"/>
    /// when no folder is set.
    /// </summary>
    public async Task ConnectAsync(IProgressSink progress, CancellationToken cancellationToken = default)
    {
        var folder = _settings.Load().GamePaksFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            await ReplaceAsync(null, WorkspaceState.NotConfigured, Localization.Loc.T("Workspace.NotConfigured"), null, loose: false).ConfigureAwait(false);
            return;
        }

        await OpenAsync(folder, loose: false, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a loose extracted folder (<c>SCUM/Content/...</c> layout) instead of the game paks.</summary>
    public Task OpenLooseAsync(string folder, IProgressSink progress, CancellationToken cancellationToken = default) =>
        OpenAsync(folder, loose: true, progress, cancellationToken);

    /// <summary>
    /// Opens <paramref name="path"/> for this session without changing the settings: a Paks folder or single <c>.pak</c>
    /// (mounted with the stored key) or an extracted folder (<c>SCUM/Content/...</c>).
    /// </summary>
    public Task OpenPathAsync(string path, IProgressSink progress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new DirectoryNotFoundException("Not found: " + full);
        }

        var loose = Directory.Exists(full) && !Directory.EnumerateFiles(full, "*.*", SearchOption.TopDirectoryOnly)
            .Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
        return OpenAsync(full, loose, progress, cancellationToken);
    }

    /// <summary>Closes the catalog.</summary>
    public void Disconnect() => _ = ReplaceAsync(null, WorkspaceState.NotConfigured, Localization.Loc.T("Workspace.Disconnected"), null, loose: false);

    /// <inheritdoc />
    public void Dispose()
    {
        var old = Catalog;
        Catalog = null;
        old?.Dispose();
        _gate.Dispose();
    }

    private async Task OpenAsync(string folder, bool loose, IProgressSink progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _dispatcher.Invoke(() =>
            {
                State = WorkspaceState.Connecting;
                StatusText = loose ? Localization.Loc.T("Workspace.OpeningFolder") : Localization.Loc.T("Workspace.Mounting");
            });
            progress.ReportIndeterminate(loose ? Localization.Loc.T("Workspace.OpeningLooseFolder") : Localization.Loc.T("Workspace.MountingStep"));

            AssetCatalog catalog;
            try
            {
                catalog = await Task.Run(() => Open(folder, loose), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = ex is DirectoryNotFoundException or FileNotFoundException ? ex.Message : Localization.Loc.F("Workspace.CouldNotOpen", folder, ex.Message);
                _dispatcher.Invoke(() =>
                {
                    State = WorkspaceState.Error;
                    StatusText = message;
                });
                throw;
            }

            progress.ReportIndeterminate(Localization.Loc.T("Workspace.Listing"));
            var packages = await Task.Run(() => catalog.PackageFiles.Count, cancellationToken).ConfigureAwait(false);
            var mounted = catalog.Provider is IVfsFileProvider vfs ? vfs.MountedVfs.Count : 0;
            var locked = catalog.UnmountedContainerCount;
            var status = loose
                ? $"{packages:N0} packages (loose folder)"
                : $"{mounted:N0} paks mounted, {packages:N0} packages" + (locked > 0 ? $", {locked} locked" : string.Empty);
            _logger.LogInformation("Opened {Source}: {Status}.", catalog.DisplayName, status); // the log stays English
            var shown = loose
                ? Localization.Loc.F("Workspace.LooseStatus", packages)
                : Localization.Loc.F("Workspace.Status", mounted, packages) + (locked > 0 ? Localization.Loc.F("Workspace.Locked", locked) : string.Empty);
            _dispatcher.Invoke(() =>
            {
                MountedContainers = mounted;
                LockedContainers = locked;
                PackageCount = packages;
            });
            // Completes only after CatalogChanged ran on the UI thread, so callers can rely on the pages having seen it.
            await ReplaceAsync(catalog, locked > 0 && mounted == 0 ? WorkspaceState.Error : WorkspaceState.Connected, shown, folder, loose).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AssetCatalog Open(string folder, bool loose)
    {
        if (loose)
        {
            return AssetCatalog.OpenLoose(folder, new AssetCatalogOptions { Logger = _logger });
        }

        // The key is only held for the duration of the mount and is never logged. Imported mods are read over the game.
        _keys.TryGet(out var key);
        var mods = ModFolders.Where(Directory.Exists).ToList();
        if (mods.Count > 0)
        {
            _logger.LogInformation("Reading {Count} imported mod(s) over the game files.", mods.Count);
        }

        return AssetCatalog.OpenPaks(folder, new AssetCatalogOptions { AesKey = key, Logger = _logger, LooseOverlays = mods });
    }

    private Task ReplaceAsync(AssetCatalog? catalog, WorkspaceState state, string status, string? source, bool loose)
    {
        return _dispatcher.InvokeAsync(() =>
        {
            var old = Catalog;
            Catalog = catalog;
            SourcePath = source;
            IsLoose = loose;
            if (catalog is null)
            {
                MountedContainers = 0;
                LockedContainers = 0;
                PackageCount = 0;
            }

            // State last: listeners (status pills) read the other properties when it changes.
            StatusText = status;
            State = state;

            if (!ReferenceEquals(old, catalog))
            {
                CatalogChanged?.Invoke(this, EventArgs.Empty);
                old?.Dispose();
            }
        });
    }
}
