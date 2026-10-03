using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Mcp.Protocol;

namespace ScumStudio.Mcp.Studio;

/// <summary>
/// The ScumStudio MCP tool set: status, game files, projects (journal, undo/redo, export), map levels and actors
/// (list, inspect, delete, restore, move, duplicate, add meshes, copy between levels, delete-all-of-kind), vehicles and
/// weapons (values, clone, remove clone), asset search and — in the desktop app — UI control (navigate, show levels,
/// select, camera, screenshots). Every change goes through the project journal, so the user can undo anything the AI did.
/// </summary>
public sealed partial class StudioTools
{
    /// <summary>Instructions sent to the AI in the <c>initialize</c> result.</summary>
    public const string Instructions =
        "ScumStudio is a modding studio for SCUM (Unreal Engine 4.27.2, cooked game files). Workflow: " +
        "1) get_status. 2) If no game files are open, open_source (the game's SCUM\\Content\\Paks folder or an extracted folder; " +
        "the AES key is configured by the user and never passes through this server). 3) Edits need a project: create_project or " +
        "open_project. Every edit is written to the project's journal and can be undone (undo/redo, get_history). " +
        "4) Map: list_levels (The_Island has ~1,900 sublevels in cells A_0..D_4 and Z_0..Z_4), list_actors / get_actor, then " +
        "delete_actors, delete_all_of_kind, move_actor, duplicate_actor, add_static_mesh, copy_actor_to_level, restore_actors. " +
        "Building: list_object_categories / list_objects (sizes), ground_height, place_objects (many objects in one undoable step, each " +
        "resting on the terrain or on what stands there, so nothing floats) and scatter_objects (forests, bushes, rocks planted like the game). " +
        "5) Vehicles and weapons: list_items, get_item_values, set_item_values, clone_item (new name; the clone is registered so " +
        "#SpawnItem <Name> / #SpawnVehicle BPC_<Name> work), remove_item_clone. 6) export_mod builds pakchunk900-<Name>_P.pak " +
        "(+ server pak) for the user to copy into SCUM\\Content\\Paks. Units: centimetres; axes X forward, Y right, Z up; " +
        "rotation [pitch, yaw, roll] in degrees. In the desktop app also: navigate, show_levels, select_actor, set_camera, " +
        "screenshot (look at your work), show_item. Cooked Blueprints store only values that differ from their parent class.";

    private readonly IStudioHost _host;
    private readonly ILogger _logger;
    private readonly Dictionary<string, LevelDocument> _levels = new(StringComparer.OrdinalIgnoreCase);
    private AssetCatalog? _levelsCatalog;
    private Cue4ParseLevelReader? _reader;

    /// <summary>Creates the tool set over <paramref name="host"/>.</summary>
    public StudioTools(IStudioHost host, ILogger? logger = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>A ready MCP server with every tool of <paramref name="host"/> (plus <paramref name="extraTools"/>).</summary>
    public static McpServer CreateServer(IStudioHost host, ILogger? logger = null, IEnumerable<McpTool>? extraTools = null)
    {
        var tools = new StudioTools(host, logger).CreateTools(extraTools);
        var server = new McpServer(new McpServerInfo
        {
            Name = "scumstudio",
            Title = "ScumStudio (" + host.Kind + ")",
            Version = CoreInfo.Version.Split('+')[0],
            Instructions = Instructions,
        }, tools, logger);
        server.ToolCalled += (_, call) => host.ReportActivity(call.Tool, call.Summary, call.IsError);
        return server;
    }

    /// <summary>Every tool, in a stable order.</summary>
    public IReadOnlyList<McpTool> CreateTools(IEnumerable<McpTool>? extraTools = null)
    {
        var tools = new List<McpTool>();
        tools.AddRange(CoreTools());
        tools.AddRange(MapTools());
        tools.AddRange(BuildTools());
        tools.AddRange(ItemTools());
        tools.AddRange(AssetTools());
        if (_host.Ui is not null)
        {
            tools.AddRange(UiTools(_host.Ui));
        }

        if (extraTools is not null)
        {
            tools.AddRange(extraTools);
        }

        return tools;
    }

    // ------------------------------------------------------------------ status, source, project

    private IEnumerable<McpTool> CoreTools()
    {
        yield return new McpTool("get_status",
            "What is open right now: game files (source, package count, The_Island sublevels per kind), the project (name, folder, " +
            "applied/undoable edits, levels and vehicle/item packages waiting for export) and, in the desktop app, the page, the " +
            "levels shown in the 3D viewport, the selected actor and the camera. Call this first.",
            ToolSchema.Object().Build(), Status)
        { Title = "Studio status", ReadOnly = true, Idempotent = true };

        yield return new McpTool("open_source",
            "Opens the game files: the game's SCUM\\Content\\Paks folder (uses the AES key the user configured; the key is never " +
            "exchanged here), a single .pak, or an extracted folder containing SCUM/Content. Without 'path' the configured game " +
            "Paks folder is used.",
            ToolSchema.Object().String("path", "Paks folder, .pak file or extracted folder. Omit for the configured game folder.").Build(),
            async (call, ct) => McpToolResult.Text(await _host.OpenSourceAsync(call.GetString("path"), ct).ConfigureAwait(false) + Environment.NewLine + StatusLine()))
        { Title = "Open game files", Idempotent = true, OpenWorld = true };

        yield return new McpTool("create_project",
            "Creates a project folder '<folder>/<name>.ssproj' (project.json + journal.jsonl) and opens it. Every edit is recorded " +
            "in its journal; the project is what export_mod builds the mod from.",
            ToolSchema.Object()
                .String("folder", "Parent folder for the project (created when missing).", required: true)
                .String("name", "Project / mod name, e.g. 'Outpost cleanup'.", required: true)
                .Build(),
            async (call, ct) =>
            {
                var project = await _host.CreateProjectAsync(call.RequireString("folder"), call.RequireString("name"), ct).ConfigureAwait(false);
                return McpToolResult.Json(ProjectInfo(project)).WithSummary($"Created project {project.Manifest.Name} in {project.DirectoryPath}");
            })
        { Title = "Create project", OpenWorld = true };

        yield return new McpTool("open_project",
            "Opens an existing project (.ssproj folder or its project.json). Its journal is replayed: undo/redo continue where the user left off.",
            ToolSchema.Object().String("path", "Project folder (…/Name.ssproj) or its project.json.", required: true).Build(),
            async (call, ct) =>
            {
                var project = await _host.OpenProjectAsync(call.RequireString("path"), ct).ConfigureAwait(false);
                return McpToolResult.Json(ProjectInfo(project)).WithSummary($"Opened project {project.Manifest.Name} ({project.History.Count} journal entries)");
            })
        { Title = "Open project", Idempotent = true, OpenWorld = true };

        yield return new McpTool("get_history",
            "The project's edit history, newest first: sequence number, time, applied/undone, summary and target.",
            ToolSchema.Object().Integer("limit", "Most entries to return (default 30).", minimum: 1, maximum: 500).Build(),
            (call, _) =>
            {
                var project = RequireProject();
                var limit = call.GetInt("limit", 30);
                var items = project.History.Reverse().Take(limit).Select(h => new
                {
                    seq = h.Seq,
                    at = h.At.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
                    status = h.Status.ToString().ToLowerInvariant(),
                    summary = h.Summary,
                    target = h.Level is null ? null : ShortName(h.Level) + (h.Actor is null ? string.Empty : "/" + h.Actor),
                });
                return Task.FromResult(McpToolResult.Json(new { total = project.History.Count, canUndo = project.CanUndo, canRedo = project.CanRedo, entries = items }));
            })
        { Title = "Edit history", ReadOnly = true, Idempotent = true };

        yield return new McpTool("undo",
            "Undoes the last edit(s) of the project (persisted in the journal; redo brings them back).",
            ToolSchema.Object().Integer("steps", "How many edits to undo (default 1).", minimum: 1, maximum: 100).Build(),
            (call, _) => Task.FromResult(UndoRedo(call.GetInt("steps", 1), undo: true)))
        { Title = "Undo", Destructive = false };

        yield return new McpTool("redo",
            "Redoes the last undone edit(s).",
            ToolSchema.Object().Integer("steps", "How many edits to redo (default 1).", minimum: 1, maximum: 100).Build(),
            (call, _) => Task.FromResult(UndoRedo(call.GetInt("steps", 1), undo: false)))
        { Title = "Redo" };

        yield return new McpTool("export_mod",
            "Builds the mod from the project's applied edits: '<outputFolder>/Client/pakchunk<N>-<Name>_P.pak' (+ .sig copied from " +
            "a stock pak when available, + export-report.md) with the rewritten levels, changed/cloned vehicle and item packages and " +
            "the merged AssetRegistry.bin; with includeServer also the server pak from the server's Paks folder. The user copies " +
            "the pak and .sig into SCUM\\Content\\Paks.",
            ToolSchema.Object()
                .String("outputFolder", "Folder that receives Client/ (and Server/).", required: true)
                .String("modName", "Pak name (default: the project name).")
                .Boolean("includeServer", "Also build the server pak (needs the server Paks folder configured / given as a project source).")
                .Integer("pakChunkIndex", "The pakchunk number (default 900; higher loads later).", minimum: 1, maximum: 9999)
                .Build(),
            async (call, ct) =>
            {
                RequireProject();
                var options = new ExportOptions
                {
                    OutputDirectory = call.RequireString("outputFolder"),
                    ModName = call.GetString("modName"),
                    PakChunkIndex = call.GetInt("pakChunkIndex", ProjectExporter.DefaultPakChunkIndex),
                };
                var results = await _host.ExportAsync(options, call.GetBool("includeServer", false), ct).ConfigureAwait(false);
                return McpToolResult.Json(new
                {
                    exports = results.Select(r => new
                    {
                        role = r.Role.ToString(),
                        pak = r.PakPath,
                        sig = r.SigPath,
                        report = r.ReportPath,
                        levels = r.Levels.Count,
                        actorsRemoved = r.RemovedActorCount,
                        transformsWritten = r.PatchedTransformCount,
                        vehicleItemPackages = r.Assets.Count,
                        newPackages = r.Assets.Count(a => a.IsClone),
                        valuesWritten = r.AssetValues.Count,
                        registryRecordsAdded = r.Registered.Count,
                        warnings = r.Warnings,
                    }),
                }).WithSummary("Exported " + string.Join(", ", results.Select(r => r.PakPath is null ? "(staged files only)" : Path.GetFileName(r.PakPath))) + " to " + options.OutputDirectory);
            })
        { Title = "Export mod pak", OpenWorld = true };
    }

    private Task<McpToolResult> Status(ToolCall call, CancellationToken cancellationToken) => StatusAsync(cancellationToken);

    private async Task<McpToolResult> StatusAsync(CancellationToken cancellationToken)
    {
        var catalog = _host.Catalog;
        var world = _host.World;
        var project = _host.Project;
        object? ui = null;
        if (_host.Ui is { } u)
        {
            var state = await u.GetStateAsync(cancellationToken).ConfigureAwait(false);
            ui = new
            {
                page = state.Page,
                loadedLevels = state.LoadedLevels.Select(ShortName).ToList(),
                selectedActor = state.SelectedActor is { } a ? new { level = ShortName(a.Level), actor = a.Actor } : null,
                camera = state.CameraLocation is { } c ? new { location = Vec(c), yaw = Round(state.CameraYaw), pitch = Round(state.CameraPitch) } : null,
            };
        }

        return McpToolResult.Json(new
        {
            host = _host.Kind,
            version = CoreInfo.Version.Split('+')[0],
            source = catalog is null ? null : new
            {
                name = catalog.DisplayName,
                path = catalog.SourcePath,
                packages = catalog.PackageFiles.Count,
            },
            world = world is null ? null : new
            {
                sublevels = world.Sublevels.Count(),
                byKind = world.CountByKind().ToDictionary(k => k.Key.ToString(), k => k.Value),
            },
            project = project is null ? null : ProjectInfo(project),
            ui,
            hint = catalog is null ? "No game files open: call open_source." : project is null ? "No project open: call create_project or open_project before editing." : null,
        });
    }

    private string StatusLine()
    {
        var catalog = _host.Catalog;
        return catalog is null
            ? "No game files are open."
            : string.Create(CultureInfo.InvariantCulture, $"Open: {catalog.DisplayName} ({catalog.PackageFiles.Count:N0} packages, {_host.World?.Sublevels.Count() ?? 0:N0} The_Island sublevels).");
    }

    private static object ProjectInfo(Project project) => new
    {
        name = project.Manifest.Name,
        folder = project.DirectoryPath,
        edits = project.History.Count,
        applied = project.Journal.UndoPointer,
        canUndo = project.CanUndo,
        canRedo = project.CanRedo,
        levelsToExport = project.PendingExportSet.Select(ShortName).ToList(),
        vehicleItemPackagesToExport = project.PendingAssetSet.Count,
        clones = project.State.AssetClones.Select(c => ShortName(c.NewPrimary)).ToList(),
    };

    private McpToolResult UndoRedo(int steps, bool undo)
    {
        RequireProject();
        var done = new List<string>();
        for (var i = 0; i < steps; i++)
        {
            var entry = undo ? _host.Undo() : _host.Redo();
            if (entry is null)
            {
                break;
            }

            done.Add($"#{entry.Seq} {entry.Op.Describe()}");
        }

        var verb = undo ? "Undone" : "Redone";
        return done.Count switch
        {
            0 => McpToolResult.Text(undo ? "Nothing to undo." : "Nothing to redo."),
            1 => McpToolResult.Text($"{verb}: {done[0]}"),
            _ => McpToolResult.Text($"{verb} {done.Count} edits:\n" + string.Join('\n', done)),
        };
    }

    // ------------------------------------------------------------------ shared helpers

    private AssetCatalog RequireCatalog() =>
        _host.Catalog ?? throw new InvalidOperationException("No game files are open: call open_source first (the game's SCUM\\Content\\Paks folder or an extracted folder).");

    private Project RequireProject() =>
        _host.Project ?? throw new InvalidOperationException("No project is open: call create_project or open_project first (every edit is recorded in the project's journal).");

    private WorldIndex RequireWorld()
    {
        RequireCatalog();
        return _host.World ?? throw new InvalidOperationException("The open game files contain no The_Island levels (open the game's Paks folder or an extracted map folder).");
    }

    private string ResolveLevel(string nameOrPath)
    {
        var world = RequireWorld();
        if (world.Find(nameOrPath) is { IsMap: true } package)
        {
            return package.PackagePath;
        }

        if (nameOrPath.StartsWith('/') && RequireCatalog().PackageExists(nameOrPath))
        {
            return nameOrPath;
        }

        throw new ToolArgumentException($"Level not found: '{nameOrPath}'. Use list_levels (names like A_0_Outpost_Exterior).");
    }

    private LevelDocument Level(string packagePath, CancellationToken cancellationToken)
    {
        var catalog = RequireCatalog();
        if (!ReferenceEquals(catalog, _levelsCatalog))
        {
            _levels.Clear();
            _levelsCatalog = catalog;
            _reader = new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions(), _logger);
        }

        if (!_levels.TryGetValue(packagePath, out var document))
        {
            document = LevelDocument.Load(_reader!, packagePath, cancellationToken);
            _levels[packagePath] = document;
        }

        return document;
    }

    private static string ShortName(string path)
    {
        var p = path.TrimEnd('/');
        var slash = p.LastIndexOf('/');
        var name = slash < 0 ? p : p[(slash + 1)..];
        var dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    private static double Round(float value, int digits = 2) => Math.Round(value, digits) + 0.0;

    private static double[] Vec(FVector v, int digits = 2) => [Round(v.X, digits), Round(v.Y, digits), Round(v.Z, digits)];

    private static double[] Rot(FRotator r) => [Round(r.Pitch), Round(r.Yaw), Round(r.Roll)];

    private static FVector ToVector((float X, float Y, float Z) v) => new(v.X, v.Y, v.Z);

    private static FRotator ToRotator((float X, float Y, float Z) v) => new(v.X, v.Y, v.Z);

    private static IEnumerable<string> Tokens(string? filter) =>
        (filter ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MatchesAll(IEnumerable<string> tokens, params string?[] fields) =>
        tokens.All(t => fields.Any(f => f is not null && f.Contains(t, StringComparison.OrdinalIgnoreCase)));
}
