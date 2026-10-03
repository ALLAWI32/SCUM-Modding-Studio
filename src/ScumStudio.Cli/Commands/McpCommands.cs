using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using Microsoft.Extensions.Logging;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Studio;
using ScumStudio.Mcp.Transports;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio mcp</c>: Model Context Protocol server on stdio so AI assistants (Claude Desktop, Claude Code, other MCP
/// clients) can drive ScumStudio headless — or, with <c>--connect</c>, a bridge to the running app's HTTP endpoint so the
/// AI controls the live window. Standard output carries only MCP messages; logs go to standard error.
/// </summary>
internal sealed class McpCommands : ICommandModule
{
    /// <summary>Environment variable with the app's MCP bearer token (bridge mode).</summary>
    public const string TokenVariable = "SCUMSTUDIO_MCP_TOKEN";

    /// <inheritdoc />
    public Command Build()
    {
        var source = new Option<string?>("--source", $"Game files to open at start: the game's SCUM\\Content\\Paks folder or an extracted folder (or set {HeadlessStudioHost.SourceVariable}). The AES key is read from {Pak.AesKeyText.EnvironmentVariable}.");
        var serverSource = new Option<string?>("--server-source", "Server Paks folder for server pak exports.");
        var project = new Option<string?>("--project", "Project folder (.ssproj) to open at start.");
        var connect = new Option<string?>("--connect", "Bridge mode: URL of the running ScumStudio app's MCP endpoint, e.g. http://127.0.0.1:47130/mcp (Settings → AI control).");
        var token = new Option<string?>("--token", $"Bearer token for --connect (prefer the {TokenVariable} environment variable).");
        var listTools = new Option<bool>("--list-tools", "Print the tool list as Markdown and exit.");
        var installSkill = new Option<bool>("--install-skill", "Install the ScumStudio skill for Claude Code (~/.claude/skills/scumstudio/SKILL.md) and exit.");
        var command = new Command("mcp", "Run a Model Context Protocol server on stdio for AI assistants (headless, or bridged to the running app with --connect).")
        {
            source, serverSource, project, connect, token, listTools, installSkill,
        };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<McpCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            if (parse.GetValueForOption(installSkill))
            {
                logger.LogInformation("Claude Code skill written to {Path}.", ScumStudio.Mcp.Skill.StudioSkill.Install());
                ctx.ExitCode = 0;
                return;
            }

            if (parse.GetValueForOption(listTools))
            {
                Console.Out.Write(ToolsMarkdown());
                ctx.ExitCode = 0;
                return;
            }

            if (parse.GetValueForOption(connect) is { } url)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
                {
                    logger.LogError("'{Url}' is not a URL.", url);
                    ctx.ExitCode = 2;
                    return;
                }

                using var bridge = new McpHttpBridge(endpoint, parse.GetValueForOption(token) ?? Environment.GetEnvironmentVariable(TokenVariable), logger: logger);
                logger.LogInformation("MCP bridge: stdio ⇄ {Url}.", endpoint);
                await bridge.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), ct).ConfigureAwait(false);
                ctx.ExitCode = 0;
                return;
            }

            using var host = new HeadlessStudioHost(logger) { ServerSource = parse.GetValueForOption(serverSource) };
            try
            {
                var start = parse.GetValueForOption(source) ?? Environment.GetEnvironmentVariable(HeadlessStudioHost.SourceVariable);
                if (!string.IsNullOrWhiteSpace(start))
                {
                    logger.LogInformation("{Message}", await host.OpenSourceAsync(start, ct).ConfigureAwait(false));
                }

                if (parse.GetValueForOption(project) is { Length: > 0 } projectPath)
                {
                    await host.OpenProjectAsync(projectPath, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            {
                // Keep serving: the AI can call open_source / open_project itself and sees the error there.
                logger.LogError("MCP start-up: {Message}", ex.Message);
            }

            var server = StudioTools.CreateServer(host, logger, [RenderLevelsTool(host, logger)]);
            logger.LogInformation("ScumStudio MCP server on stdio: {Count} tools.", server.Tools.Count);
            await McpStdioTransport.RunAsync(server, Console.OpenStandardInput(), Console.OpenStandardOutput(), logger, ct).ConfigureAwait(false);
            ctx.ExitCode = 0;
        });
        return command;
    }

    /// <summary>Markdown table of every tool (headless + app-only), for docs/MCP.md.</summary>
    internal static string ToolsMarkdown()
    {
        using var host = new HeadlessStudioHost();
        var headless = new StudioTools(host).CreateTools([RenderLevelsTool(host, null)]).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var all = new StudioTools(new ListingHost(host)).CreateTools([RenderLevelsTool(host, null)]);
        var sb = new StringBuilder();
        sb.AppendLine("| Tool | Where | Kind | What it does |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var tool in all)
        {
            var where = tool.Name == "render_levels" ? "CLI" : headless.Contains(tool.Name) ? "app + CLI" : "app";
            var kind = tool.ReadOnly ? "read" : where == "app" ? "view (UI only)" : tool.Destructive ? "edit (destructive, undoable)" : tool.OpenWorld ? "files" : "edit (undoable)";
            sb.Append("| `").Append(tool.Name).Append("` | ").Append(where).Append(" | ").Append(kind).Append(" | ")
                .Append(tool.Description.Replace("|", "\\|", StringComparison.Ordinal)).AppendLine(" |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>render_levels</c> (CLI only): renders levels off-screen with the project's deletions and moves applied and returns
    /// the PNG, so a headless AI can look at the map.
    /// </summary>
    internal static McpTool RenderLevelsTool(HeadlessStudioHost host, ILogger? logger) => new(
        "render_levels",
        "Renders levels (or a whole cell) off-screen to a PNG with the project's deletions and moves applied (added actors are not drawn " +
        "here; the desktop app's screenshot shows everything). Camera: yaw/pitch around the scene centre in degrees.",
        ToolSchema.Object()
            .Strings("levels", "Level names or paths.")
            .String("cell", "A whole map cell (A_0 …): its POI sublevels.")
            .Integer("width", "Image width (default 1280).", minimum: 64, maximum: 4096)
            .Integer("height", "Image height (default 720).", minimum: 64, maximum: 4096)
            .Number("yaw", "Camera yaw around the scene in degrees (default -135).")
            .Number("pitch", "Camera pitch in degrees (default -30).")
            .Vector("lookAt", "Point to look at [x, y, z] (UE cm, e.g. an actor location from list_actors). Default: the dense middle of the scene.")
            .Number("distance", "Camera distance from the target in cm (default: fit the scene, or 5000 with lookAt).")
            .Boolean("terrain", "Draw landscape tiles among the levels (default true).")
            .Build(),
        async (call, ct) =>
        {
            var catalog = host.Catalog ?? throw new InvalidOperationException("No game files are open: call open_source first.");
            var world = host.World ?? throw new InvalidOperationException("The open game files contain no The_Island levels.");
            var paths = call.GetStrings("levels").Select(n => world.Find(n)?.PackagePath ?? throw new ToolArgumentException($"Level not found: {n}")).ToList();
            if (call.GetString("cell") is { } cellText)
            {
                if (!Level.World.MapCell.TryParse(cellText, out var cell))
                {
                    throw new ToolArgumentException($"'{cellText}' is not a map cell.");
                }

                paths.AddRange(world.InCell(cell).Where(p => p.Kind == Level.World.WorldPackageKind.Poi).Select(p => p.PackagePath));
            }

            if (paths.Count == 0)
            {
                throw new ToolArgumentException("Give 'levels' or 'cell'.");
            }

            var width = call.GetInt("width", 1280);
            var height = call.GetInt("height", 720);
            var reader = new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions(), logger);
            var documents = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => LevelDocument.Load(reader, p, ct)).ToList();
            var prepared = new LevelScenePreparer(catalog, logger).Prepare(documents, new LevelSceneOptions
            {
                IncludeLandscape = call.GetBool("terrain", true),
                LandscapeStep = documents.Count > 4 ? 4 : 1,
            }, cancellationToken: ct);
            if (prepared.Placements.Count == 0 && prepared.Terrain.Count == 0)
            {
                return McpToolResult.Error("Nothing to draw (no static meshes or terrain found for these levels in the open game files).");
            }

            var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            if (!RenderCommands.TryCreateContext(width, height, log, out var context))
            {
                return McpToolResult.Error("OpenGL is not available for off-screen rendering on this machine (on Linux run under xvfb-run).");
            }

            byte[] rgba;
            int hidden = 0, moved = 0;
            using (context)
            {
                using var renderer = new SceneRenderer(context);
                using var level = LevelSceneUploader.Upload(renderer, prepared);
                var state = host.Project?.State;
                if (state is not null)
                {
                    foreach (var (id, placements) in level.PlacementsById)
                    {
                        var placement = placements[0];
                        var document = prepared.Documents[placement.DocumentIndex];
                        var reference = new ActorRef(document.PackagePath, placement.Actor.Name);
                        if (state.IsDeleted(reference))
                        {
                            level.SetVisible(id, false);
                            hidden++;
                        }
                        else if (state.GetTransformOverride(reference) is { } transform && placement.Actor.Root?.AttachParent is null)
                        {
                            level.SetActorTransform(id, transform.ToTransform());
                            moved++;
                        }
                    }
                }

                var camera = new FlyCamera();
                var yaw = (float)(call.GetNumber("yaw") ?? -135);
                var pitch = (float)(call.GetNumber("pitch") ?? -30);
                var fullRadius = MathF.Max(level.Bounds.Extent.Length(), 1f);
                if (call.GetVector3("lookAt") is { } look)
                {
                    var distance = (float)(call.GetNumber("distance") ?? 5000);
                    camera.FitClipRange(distance, MathF.Max(distance, fullRadius));
                    camera.Orbit(UeToGl.Point(new FVector(look.X, look.Y, look.Z)), yaw, pitch, distance);
                }
                else
                {
                    // Frame the dense middle of the scene (5th..95th percentile of placement positions), so a single far
                    // object does not shrink the area of interest to a few pixels.
                    var focus = TrimmedBounds(level.PlacementsById.Values.SelectMany(p => p).Select(p => UeToGl.Point(p.World.Translation)).ToList()) ?? level.Bounds;
                    var fitted = camera.Frame(focus, (float)width / height, yaw, pitch, 1.1f);
                    var distance = (float?)call.GetNumber("distance") ?? fitted;
                    camera.FitClipRange(distance, fullRadius);
                    camera.Orbit(focus.Center, yaw, pitch, distance);
                }
                renderer.Settings = renderer.Settings with { ShowGrid = false };
                using var target = renderer.CreateTarget(width, height);
                renderer.Render(target, level.Scene, camera);
                rgba = target.ReadColorRgba();
            }

            var file = Path.Combine(Path.GetTempPath(), "scumstudio-render-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                await ImageExport.SavePngAsync(rgba, width, height, file, ct).ConfigureAwait(false);
                var png = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                return new McpToolResult().AddImage(png).AddText(
                    $"{documents.Count} level(s): {prepared.Placements.Count} placements, {prepared.Terrain.Count} terrain components, {prepared.MissingMeshes.Count} meshes missing from the game files; project edits shown: {hidden} hidden, {moved} moved.");
            }
            finally
            {
                File.Delete(file);
            }
        })
    { Title = "Render levels (headless)", ReadOnly = true };

    /// <summary>Bounds of the 5th..95th percentile of <paramref name="points"/> per axis, or null for fewer than 10 points.</summary>
    internal static Core.Geometry.BoundingBox? TrimmedBounds(IReadOnlyList<System.Numerics.Vector3> points)
    {
        if (points.Count < 10)
        {
            return null;
        }

        static float Percentile(IEnumerable<float> values, double q)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted[(int)Math.Clamp(Math.Round(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
        }

        var min = new System.Numerics.Vector3(Percentile(points.Select(p => p.X), 0.05), Percentile(points.Select(p => p.Y), 0.05), Percentile(points.Select(p => p.Z), 0.05));
        var max = new System.Numerics.Vector3(Percentile(points.Select(p => p.X), 0.95), Percentile(points.Select(p => p.Y), 0.95), Percentile(points.Select(p => p.Z), 0.95));
        return new Core.Geometry.BoundingBox(min, System.Numerics.Vector3.Max(max, min + System.Numerics.Vector3.One));
    }

    /// <summary>A host that exposes a UI so <see cref="ToolsMarkdown"/> also lists the app-only tools.</summary>
    private sealed class ListingHost(HeadlessStudioHost inner) : IStudioHost, IStudioUi
    {
        public string Kind => "app";

        public Assets.Catalog.AssetCatalog? Catalog => null;

        public Level.World.WorldIndex? World => null;

        public Level.Projects.Project? Project => null;

        public IStudioUi? Ui => this;

        public Task<string> OpenSourceAsync(string? path, CancellationToken cancellationToken) => inner.OpenSourceAsync(path, cancellationToken);

        public Task<Level.Projects.Project> CreateProjectAsync(string parentFolder, string name, CancellationToken cancellationToken) => inner.CreateProjectAsync(parentFolder, name, cancellationToken);

        public Task<Level.Projects.Project> OpenProjectAsync(string path, CancellationToken cancellationToken) => inner.OpenProjectAsync(path, cancellationToken);

        public JournalEntry Apply(EditOp op) => inner.Apply(op);

        public JournalEntry? Undo() => null;

        public JournalEntry? Redo() => null;

        public Task<IReadOnlyList<Level.Export.ExportResult>> ExportAsync(Level.Export.ExportOptions options, bool includeServer, CancellationToken cancellationToken) => inner.ExportAsync(options, includeServer, cancellationToken);

        public void ReportActivity(string tool, string summary, bool isError)
        {
        }

        public Task<StudioUiState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(new StudioUiState("map", [], null, null, 0, 0));

        public Task NavigateAsync(string page, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ShowLevelsAsync(IReadOnlyList<string> packagePaths, int landscapeStep, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<string> SelectActorAsync(ActorRef actor, bool frame, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<string> SetCameraAsync(FVector? location, FVector? lookAt, float? yaw, float? pitch, bool frameAll, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<byte[]?> CaptureViewportAsync(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public Task<byte[]?> CaptureWindowAsync(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public Task<string> ShowItemAsync(string packagePath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
    }
}
