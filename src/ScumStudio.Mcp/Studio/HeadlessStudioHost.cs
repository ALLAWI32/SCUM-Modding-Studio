using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Level.World;
using ScumStudio.Pak;

namespace ScumStudio.Mcp.Studio;

/// <summary>
/// <see cref="IStudioHost"/> without a UI, for <c>scumstudio mcp</c>: opens game files (the AES key comes only from the
/// <c>SCUMSTUDIO_AES_KEY</c> environment variable of the MCP client's configuration), a project, applies journal edits and
/// exports. Not thread-safe; the MCP server runs one tool at a time.
/// </summary>
public sealed class HeadlessStudioHost : IStudioHost, IDisposable
{
    /// <summary>Environment variable with the default game source (Paks folder or extracted folder).</summary>
    public const string SourceVariable = "SCUMSTUDIO_SOURCE";

    /// <summary>Environment variable with the server Paks folder (server pak export).</summary>
    public const string ServerSourceVariable = "SCUMSTUDIO_SERVER_SOURCE";

    private readonly ILogger _logger;
    private AssetCatalog? _catalog;
    private Project? _project;

    /// <summary>Creates the host (nothing open yet).</summary>
    public HeadlessStudioHost(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public string Kind => "cli";

    /// <inheritdoc />
    public AssetCatalog? Catalog => _catalog;

    /// <inheritdoc />
    public WorldIndex? World { get; private set; }

    /// <inheritdoc />
    public Project? Project => _project;

    /// <summary>Server source for exports (Paks folder or extracted server cook), or null.</summary>
    public string? ServerSource { get; set; }

    /// <inheritdoc />
    public IStudioUi? Ui => null;

    /// <inheritdoc />
    public Task<string> OpenSourceAsync(string? path, CancellationToken cancellationToken)
    {
        var source = string.IsNullOrWhiteSpace(path) ? Environment.GetEnvironmentVariable(SourceVariable) : path;
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException($"No source given: pass 'path' (the game's SCUM\\Content\\Paks folder or an extracted folder) or set {SourceVariable} in the MCP server configuration.");
        }

        source = Path.GetFullPath(source.Trim().Trim('"'));
        var key = AesKeyText.FromEnvironmentOrStore();
        var catalog = AssetCatalog.Open(source, new AssetCatalogOptions
        {
            AesKey = string.IsNullOrWhiteSpace(key) ? null : AesKeyText.Normalize(key),
            Logger = _logger,
        });
        _catalog?.Dispose();
        _catalog = catalog;
        var world = WorldIndex.FromCatalog(catalog);
        World = world.Packages.Count == 0 ? null : world;
        var locked = catalog.UnmountedContainerCount;
        _logger.LogInformation("Opened {Source}: {Packages} packages.", catalog.DisplayName, catalog.PackageFiles.Count);
        return Task.FromResult($"Opened {catalog.DisplayName}." + (locked > 0
            ? $" {locked} encrypted pak(s) could not be mounted: set {AesKeyText.EnvironmentVariable} in the MCP server's environment."
            : string.Empty));
    }

    /// <inheritdoc />
    public async Task<Project> CreateProjectAsync(string parentFolder, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var directory = Path.Combine(Path.GetFullPath(parentFolder), safe + Project.FolderExtension);
        var sources = new List<ProjectSource>();
        if (_catalog?.SourcePath is { } client)
        {
            sources.Add(ProjectSource.Detect(client, ProjectSourceRole.Client));
        }

        if (ServerSource is { } server)
        {
            sources.Add(ProjectSource.Detect(server, ProjectSourceRole.Server));
        }

        var project = await Project.CreateAsync(directory, name.Trim(), sources: sources, cancellationToken: cancellationToken).ConfigureAwait(false);
        _project?.Dispose();
        _project = project;
        return project;
    }

    /// <inheritdoc />
    public async Task<Project> OpenProjectAsync(string path, CancellationToken cancellationToken)
    {
        var project = await Project.OpenAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        _project?.Dispose();
        _project = project;
        if (_catalog is null && project.Manifest.Sources.FirstOrDefault(s => s.Role == ProjectSourceRole.Client) is { } source
            && (File.Exists(source.Path) || Directory.Exists(source.Path)))
        {
            await OpenSourceAsync(source.Path, cancellationToken).ConfigureAwait(false);
        }

        ServerSource ??= project.Manifest.Sources.FirstOrDefault(s => s.Role == ProjectSourceRole.Server)?.Path;
        return project;
    }

    /// <inheritdoc />
    public JournalEntry Apply(EditOp op)
    {
        var project = _project ?? throw new InvalidOperationException("No project is open.");
        return project.Apply(op);
    }

    /// <inheritdoc />
    public JournalEntry? Undo() => _project?.Undo();

    /// <inheritdoc />
    public JournalEntry? Redo() => _project?.Redo();

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExportResult>> ExportAsync(ExportOptions options, bool includeServer, CancellationToken cancellationToken)
    {
        var project = _project ?? throw new InvalidOperationException("No project is open.");
        var catalog = _catalog ?? throw new InvalidOperationException("No game files are open.");
        var exporter = new ProjectExporter(_logger);
        var results = new List<ExportResult>
        {
            await exporter.ExportAsync(project, catalog, options, ProjectSourceRole.Client, null, cancellationToken).ConfigureAwait(false),
        };
        if (includeServer)
        {
            var server = ServerSource ?? Environment.GetEnvironmentVariable(ServerSourceVariable)
                         ?? throw new InvalidOperationException($"No server source: add one to the project or set {ServerSourceVariable}.");
            var key = AesKeyText.FromEnvironmentOrStore();
            using var serverCatalog = AssetCatalog.Open(server, new AssetCatalogOptions
            {
                AesKey = string.IsNullOrWhiteSpace(key) ? null : AesKeyText.Normalize(key),
                Logger = _logger,
            });
            results.Add(await exporter.ExportAsync(project, serverCatalog, options, ProjectSourceRole.Server, null, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <inheritdoc />
    public void ReportActivity(string tool, string summary, bool isError)
    {
        if (isError)
        {
            _logger.LogWarning("AI tool {Tool}: {Summary}", tool, summary);
        }
        else
        {
            _logger.LogInformation("AI tool {Tool}: {Summary}", tool, summary);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _project?.Dispose();
        _catalog?.Dispose();
    }
}
