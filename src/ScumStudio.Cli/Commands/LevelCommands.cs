using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Level.Serialization;
using ScumStudio.Level.World;
using ScumStudio.Pak;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio level list|dump</c>: The_Island's sublevels (<see cref="WorldIndex"/>) and one sublevel's actor graph
/// (<see cref="LevelDocument"/>). <c>&lt;source&gt;</c> is a Paks folder, a single <c>.pak</c> or a loose folder
/// (<c>SCUM/Content/...</c>). Encrypted stock paks need <c>--aes</c> or the <c>SCUMSTUDIO_AES_KEY</c> environment variable;
/// the key is never printed.
/// </summary>
internal sealed class LevelCommands : ICommandModule
{
    /// <inheritdoc />
    public Command Build()
    {
        var level = new Command("level", "The_Island sublevels: list them by cell/kind and dump a sublevel's actors.");
        level.AddCommand(BuildList());
        level.AddCommand(BuildDump());
        level.AddCommand(BuildRefs());
        return level;
    }

    private static Argument<string> SourceArgument() =>
        new("source", "Paks folder, single .pak, or loose folder containing SCUM/Content (or Content).");

    private static Option<string?> AesOption() =>
        new(["--aes", "-a"], $"AES-256 key (64 hex chars) for encrypted stock paks. Prefer the {AesKeyText.EnvironmentVariable} environment variable: command lines can end up in shell history.");

    private static Command BuildList()
    {
        var source = SourceArgument();
        var aes = AesOption();
        var cell = new Option<string?>(["--cell", "-c"], "Only packages of this map cell (A_0 .. D_4, Z_0 .. Z_4).");
        var kind = new Option<WorldPackageKind?>(["--kind", "-k"], "Only packages of this kind (Poi, Landscape, TvBase, Pripyat, BuiltData, Hlod, Misc, Persistent).");
        var all = new Option<bool>("--all", "Include non-level packages (BuiltData, HLOD, layer infos); by default only levels are listed.");
        var noCheck = new Option<bool>("--no-crosscheck", "Do not read The_Island.umap's StreamingLevels even when it is available.");
        var json = new Option<bool>("--json", "Print JSON instead of tab-separated lines.");
        var command = new Command("list", "List The_Island's packages as '<Kind>\\t<Cell>\\t<Streamed>\\t<Name>\\t<PackagePath>'.")
        {
            source, aes, cell, kind, all, noCheck, json,
        };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<LevelCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                MapCell? cellFilter = null;
                if (parse.GetValueForOption(cell) is { } cellText)
                {
                    if (!MapCell.TryParse(cellText, out var parsed))
                    {
                        logger.LogError("'{Cell}' is not a map cell (expected A_0 .. D_4 or Z_0 .. Z_4).", cellText);
                        return 2;
                    }

                    cellFilter = parsed;
                }

                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var index = WorldIndex.FromCatalog(catalog);
                if (!parse.GetValueForOption(noCheck))
                {
                    index = index.TryCrossCheck(new Cue4ParseLevelReader(catalog, logger: logger), logger, ct);
                }

                var kindFilter = parse.GetValueForOption(kind);
                var includeAll = parse.GetValueForOption(all) || kindFilter is WorldPackageKind.BuiltData or WorldPackageKind.Hlod;
                var selected = index.Packages
                    .Where(p => includeAll || p.IsMap)
                    .Where(p => cellFilter is null || p.Cell == cellFilter)
                    .Where(p => kindFilter is null || p.Kind == kindFilter)
                    .ToList();

                if (parse.GetValueForOption(json))
                {
                    var dto = selected.Select(p => new
                    {
                        p.Name,
                        Kind = p.Kind.ToString(),
                        Cell = p.Cell?.ToString(),
                        p.Folder,
                        p.PackagePath,
                        p.IsMap,
                        Streamed = p.IsStreamed,
                        BuiltData = p.BuiltDataPackage,
                        p.Owner,
                        Quadrant = p.LandscapeQuadrant,
                        Variant = p.LandscapeVariant?.ToString(),
                    });
                    Console.Out.WriteLine(JsonSerializer.Serialize(dto, LevelJson.Indented));
                }
                else
                {
                    foreach (var p in selected)
                    {
                        var streamed = p.IsStreamed switch { true => "streamed", false => "not-streamed", _ => "-" };
                        Console.Out.WriteLine($"{p.Kind}\t{p.Cell?.ToString() ?? "-"}\t{streamed}\t{p.Name}\t{p.PackagePath}");
                    }
                }

                var counts = string.Join(", ", index.CountByKind().Select(k => $"{k.Key} {k.Value}"));
                logger.LogInformation("{Selected} of {Total} packages listed ({Counts}; {Sublevels} sublevels) from {Source}.",
                    selected.Count, index.Packages.Count, counts.Length == 0 ? "none" : counts, index.Sublevels.Count(), catalog.DisplayName);
                if (index.PersistentLevel is null)
                {
                    logger.LogInformation("The_Island.umap is not in this source; classification is by name only.");
                }
                else if (index.CrossCheck is { } check)
                {
                    logger.LogInformation(
                        "The_Island.umap streams {Count} levels: {Matched} found, {Missing} missing from the source, {NotStreamed} sublevel(s) in the folder not streamed.",
                        check.StreamingLevelCount, check.Matched, check.MissingPackages.Count, check.NotStreamed.Count);
                    foreach (var missing in check.MissingPackages.Take(20))
                    {
                        logger.LogWarning("Streamed but missing: {Package}", missing.PackagePath);
                    }
                }

                return 0;
            });
        });
        return command;
    }

    private static Command BuildDump()
    {
        var source = SourceArgument();
        var aes = AesOption();
        var sublevel = new Argument<string>("sublevel", "Sublevel name (e.g. A_0_Outpost_Exterior) or package path (/Game/ConZ_Files/Maps/The_Island/...).");
        var json = new Option<bool>("--json", "Print the actor graph as JSON.");
        var instances = new Option<bool>("--instances", "Include every ISM/HISM instance transform (JSON can get very large for foliage).");
        var noTemplates = new Option<bool>("--no-templates", "Do not follow Blueprint templates for values the level does not store (faster, less accurate).");
        var command = new Command("dump", "Dump a sublevel's actors: class, kind, mesh, world transform and components.")
        {
            source, sublevel, aes, json, instances, noTemplates,
        };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<LevelCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var name = parse.GetValueForArgument(sublevel);
                var packagePath = WorldIndex.FromCatalog(catalog).Find(name)?.PackagePath
                                  ?? (name.Contains('/') ? name : null);
                if (packagePath is null || !catalog.PackageExists(packagePath))
                {
                    logger.LogError("Level not found: {Name} (use 'scumstudio level list' to see the available sublevels).", name);
                    return 2;
                }

                var reader = new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions { ResolveTemplates = !parse.GetValueForOption(noTemplates) }, logger);
                var document = LevelDocument.Load(reader, packagePath, ct);
                if (parse.GetValueForOption(json))
                {
                    Console.Out.WriteLine(document.ToJson(parse.GetValueForOption(instances)));
                }
                else
                {
                    PrintDocument(document, parse.GetValueForOption(instances));
                }

                foreach (var warning in document.Warnings.Take(50))
                {
                    logger.LogWarning("{Warning}", warning);
                }

                logger.LogInformation("{Level}: {Actors} actors, {Instances} instances, {Warnings} warning(s).",
                    document.Name, document.Actors.Count, document.InstanceCount, document.Warnings.Count);
                return 0;
            });
        });
        return command;
    }

    private static Command BuildRefs()
    {
        var source = SourceArgument();
        var aes = AesOption();
        var levels = new Argument<string[]>("levels", "Sublevel names or package paths (optional when --cell/--all is given).") { Arity = ArgumentArity.ZeroOrMore };
        var cell = new Option<string?>(["--cell", "-c"], "Every sublevel of this map cell (A_0 .. D_4, Z_0 .. Z_4), landscape tiles included.");
        var kind = new Option<WorldPackageKind?>(["--kind", "-k"], "Every sublevel of this kind (Poi, Landscape, TvBase, Pripyat).");
        var all = new Option<bool>("--all", "Every sublevel of The_Island (the whole island: slow on a Paks source, minutes).");
        var depth = new Option<int>("--depth", () => int.MaxValue, "Reference hops to follow: 0 = the levels only, 1 = what they import directly (meshes, Blueprint classes, built data), default = everything (materials, textures, …).");
        var filesOut = new Option<FileInfo?>("--files", "Write the pak entry paths of every present package (header + .uexp/.ubulk) to this file, one per line — the exact list for an extraction script.");
        var missingOut = new Option<FileInfo?>("--missing", "Write the referenced packages that are NOT in the source to this file, one per line (what a loose slice still lacks).");
        var top = new Option<int>("--top", () => 25, "How many folders to print in the present/missing folder summaries.");
        var json = new Option<bool>("--json", "Print the whole report as JSON.");
        var command = new Command("refs", "Follow every package reference of the given sublevels (meshes, Blueprints, materials, textures, built data …) and report which packages/files the source has and which are missing.")
        {
            source, levels, aes, cell, kind, all, depth, filesOut, missingOut, top, json,
        };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<LevelCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                MapCell? cellFilter = null;
                if (parse.GetValueForOption(cell) is { } cellText)
                {
                    if (!MapCell.TryParse(cellText, out var parsed))
                    {
                        logger.LogError("'{Cell}' is not a map cell (expected A_0 .. D_4 or Z_0 .. Z_4).", cellText);
                        return 2;
                    }

                    cellFilter = parsed;
                }

                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var index = WorldIndex.FromCatalog(catalog);
                var kindFilter = parse.GetValueForOption(kind);
                var roots = new List<string>();
                foreach (var name in parse.GetValueForArgument(levels) ?? [])
                {
                    var packagePath = index.Find(name)?.PackagePath ?? (name.Contains('/') ? name : null);
                    if (packagePath is null || !catalog.PackageExists(packagePath))
                    {
                        logger.LogError("Level not found: {Name} (use 'scumstudio level list' to see the available sublevels).", name);
                        return 2;
                    }

                    roots.Add(packagePath);
                }

                if (cellFilter is not null || kindFilter is not null || parse.GetValueForOption(all))
                {
                    roots.AddRange(index.Sublevels
                        .Where(p => cellFilter is null || p.Cell == cellFilter)
                        .Where(p => kindFilter is null || p.Kind == kindFilter)
                        .Select(p => p.PackagePath));
                }

                if (roots.Count == 0)
                {
                    logger.LogError("Nothing selected: give sublevel names, --cell <cell>, --kind <kind> or --all.");
                    return 2;
                }

                var walker = new PackageDependencyWalker(catalog, logger);
                var report = walker.Walk(roots, parse.GetValueForOption(depth), cancellationToken: ct);
                if (parse.GetValueForOption(filesOut) is { } filesFile)
                {
                    File.WriteAllLines(filesFile.FullName, report.Files);
                    logger.LogInformation("{Count} file paths written to {File}.", report.Files.Count, filesFile.FullName);
                }

                if (parse.GetValueForOption(missingOut) is { } missingFile)
                {
                    File.WriteAllLines(missingFile.FullName, report.Missing);
                    logger.LogInformation("{Count} missing packages written to {File}.", report.Missing.Count, missingFile.FullName);
                }

                var topCount = Math.Max(0, parse.GetValueForOption(top));
                if (parse.GetValueForOption(json))
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new
                    {
                        report.Roots,
                        report.Depth,
                        PresentCount = report.Present.Count,
                        FileCount = report.Files.Count,
                        MissingCount = report.Missing.Count,
                        report.Present,
                        report.Missing,
                        report.Unresolved,
                        MissingByFolder = report.MissingByFolder().Select(p => new { Folder = p.Key, Count = p.Value }),
                        report.Warnings,
                    }, LevelJson.Indented));
                }
                else
                {
                    var o = Console.Out;
                    o.WriteLine($"roots: {report.Roots.Count} level package(s), depth followed: {report.Depth}");
                    o.WriteLine($"present: {report.Present.Count} packages = {report.Files.Count} files");
                    o.WriteLine($"missing: {report.Missing.Count} /Game packages; unresolved engine/plugin references: {report.Unresolved.Count}");
                    o.WriteLine("present by folder:");
                    foreach (var (folder, count) in report.PresentByFolder().Take(topCount))
                    {
                        o.WriteLine($"  {count,6}  {folder}");
                    }

                    if (report.Missing.Count > 0)
                    {
                        o.WriteLine("missing by folder (extract these next):");
                        foreach (var (folder, count) in report.MissingByFolder().Take(topCount))
                        {
                            o.WriteLine($"  {count,6}  {folder}");
                        }
                    }
                }

                foreach (var warning in report.Warnings.Take(20))
                {
                    logger.LogWarning("{Warning}", warning);
                }

                return report.Missing.Count == 0 ? 0 : 3;
            });
        });
        return command;
    }

    private static void PrintDocument(LevelDocument document, bool instances)
    {
        var o = Console.Out;
        o.WriteLine($"level: {document.PackagePath}");
        o.WriteLine($"actors: {document.Actors.Count} ({string.Join(", ", document.CountByKind().Select(k => $"{k.Key} {k.Value}"))}), instances: {document.InstanceCount}");
        foreach (var a in document.Actors)
        {
            var mesh = a.StaticMeshPath is null ? string.Empty : "  mesh " + a.StaticMeshPath;
            var extra = a.InstanceTransforms.Count > 0 ? $"  instances {a.InstanceTransforms.Count}" : string.Empty;
            o.WriteLine($"[{a.ExportIndex}] {a.Kind} {a.Name} ({a.ClassName})  {Fmt(a.WorldTransform)}{mesh}{extra}");
            foreach (var c in a.Components.Where(c => c.IsSceneComponent && c.ExportIndex != a.RootComponent))
            {
                var cm = c.StaticMeshPath is null ? string.Empty : "  mesh " + c.StaticMeshPath;
                o.WriteLine($"    {c.Name} ({c.ClassName})  {Fmt(c.WorldTransform)}{cm}{(c.IsInstanced ? $"  instances {c.Instances.Count}" : string.Empty)}");
            }

            if (instances)
            {
                foreach (var i in a.InstanceTransforms)
                {
                    o.WriteLine($"      {i.ComponentName}[{i.InstanceIndex}]  {Fmt(i.WorldTransform)}");
                }
            }
        }
    }

    private static string Fmt(FTransform t)
    {
        var r = t.Rotator();
        return string.Create(CultureInfo.InvariantCulture,
            $"loc ({N(t.Translation.X)}, {N(t.Translation.Y)}, {N(t.Translation.Z)}) rot ({N(r.Pitch)}, {N(r.Yaw)}, {N(r.Roll)}) scale ({N(t.Scale3D.X, 3)}, {N(t.Scale3D.Y, 3)}, {N(t.Scale3D.Z, 3)})");

        // Rounded, and "+ 0f" turns -0 into 0 so tiny negative noise does not print as "-0".
        static float N(float value, int digits = 2) => MathF.Round(value, digits) + 0f;
    }
}

/// <summary>
/// <c>scumstudio project new|status|history|undo|redo|apply</c>: create a project folder and inspect or move its edit
/// journal from the command line.
/// </summary>
internal sealed partial class ProjectCommands : ICommandModule
{
    /// <inheritdoc />
    public Command Build()
    {
        var project = new Command("project", "ScumStudio projects (.ssproj folders): create, inspect history, undo/redo edits.");
        project.AddCommand(BuildNew());
        project.AddCommand(BuildStatus());
        project.AddCommand(BuildHistory());
        project.AddCommand(BuildUndoRedo(undo: true));
        project.AddCommand(BuildUndoRedo(undo: false));
        project.AddCommand(BuildApply());
        project.AddCommand(BuildExport());
        return project;
    }

    private static Argument<string> ProjectArgument() => new("projectDir", "Project folder (e.g. MyMod.ssproj).");

    private static Command BuildNew()
    {
        var dir = ProjectArgument();
        var name = new Option<string?>(["--name", "-n"], "Project name (default: the folder name without .ssproj).");
        var build = new Option<string?>("--game-build", "Game build the edits are made against (e.g. 1.3.3.4.149664).");
        var sources = new Option<string[]>("--source", "Client source: Paks folder, .pak or loose folder (repeatable).") { AllowMultipleArgumentsPerToken = false };
        var serverSources = new Option<string[]>("--server-source", "Server source: SCUMServer Paks folder, .pak or loose folder (repeatable).") { AllowMultipleArgumentsPerToken = false };
        var command = new Command("new", "Create a project folder with project.json, journal.jsonl and notes.md.") { dir, name, build, sources, serverSources };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ProjectCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                var path = parse.GetValueForArgument(dir);
                if (Project.Exists(path))
                {
                    logger.LogError("{Path} already contains a project.", Path.GetFullPath(path));
                    return 2;
                }

                var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
                var projectName = parse.GetValueForOption(name)
                                  ?? (folderName.EndsWith(Project.FolderExtension, StringComparison.OrdinalIgnoreCase) ? folderName[..^Project.FolderExtension.Length] : folderName);
                var all = (parse.GetValueForOption(sources) ?? []).Select(s => ProjectSource.Detect(s, ProjectSourceRole.Client))
                    .Concat((parse.GetValueForOption(serverSources) ?? []).Select(s => ProjectSource.Detect(s, ProjectSourceRole.Server)))
                    .ToList();
                foreach (var s in all.Where(s => !File.Exists(s.Path) && !Directory.Exists(s.Path)))
                {
                    logger.LogWarning("Source does not exist (yet): {Path}", s.Path);
                }

                using var project = Project.Create(path, projectName, parse.GetValueForOption(build), all);
                Console.Out.WriteLine($"Created project '{project.Manifest.Name}' in {project.DirectoryPath}");
                return 0;
            });
        });
        return command;
    }

    private static Command BuildStatus()
    {
        var dir = ProjectArgument();
        var command = new Command("status", "Show the project manifest, undo/redo position and the pending export set.") { dir };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ProjectCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var project = Project.Open(parse.GetValueForArgument(dir));
                var o = Console.Out;
                var m = project.Manifest;
                o.WriteLine($"project: {m.Name}");
                o.WriteLine($"folder: {project.DirectoryPath}");
                o.WriteLine($"game build: {m.GameBuild}");
                o.WriteLine($"created: {m.Created.ToString("u", CultureInfo.InvariantCulture)}, modified: {m.Modified.ToString("u", CultureInfo.InvariantCulture)}");
                foreach (var s in m.Sources)
                {
                    o.WriteLine($"source: {s.Role} {s.Kind} {s.Path}");
                }

                o.WriteLine($"edits: {project.Journal.UndoPointer} applied, {project.Journal.RedoStack.Count} redoable, {project.History.Count} recorded");
                PrintExportSet(project);
                WriteWarnings(project, logger);
                return 0;
            });
        });
        return command;
    }

    private static Command BuildHistory()
    {
        var dir = ProjectArgument();
        var command = new Command("history", "List every recorded edit: '#seq  time  status  summary  [level/actor]'.") { dir };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ProjectCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var project = Project.Open(parse.GetValueForArgument(dir));
                var history = project.History;
                if (history.Count == 0)
                {
                    Console.Out.WriteLine("(no edits)");
                }

                foreach (var h in history)
                {
                    var target = h.Level is null ? string.Empty : $"  [{h.Level}{(h.Actor is null ? string.Empty : "/" + h.Actor)}]";
                    Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"#{h.Seq}\t{h.At.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z\t{h.Status.ToString().ToLowerInvariant()}\t{h.Summary}{target}"));
                }

                PrintExportSet(project);
                WriteWarnings(project, logger);
                return 0;
            });
        });
        return command;
    }

    private static Command BuildUndoRedo(bool undo)
    {
        var dir = ProjectArgument();
        var command = new Command(undo ? "undo" : "redo", undo ? "Undo the last applied edit." : "Redo the last undone edit.") { dir };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ProjectCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var project = Project.Open(parse.GetValueForArgument(dir));
                var entry = undo ? project.Undo() : project.Redo();
                if (entry is null)
                {
                    Console.Out.WriteLine(undo ? "Nothing to undo." : "Nothing to redo.");
                    return 0;
                }

                Console.Out.WriteLine($"{(undo ? "Undid" : "Redid")} #{entry.Seq}: {entry.Op.Describe()}");
                Console.Out.WriteLine($"edits: {project.Journal.UndoPointer} applied, {project.Journal.RedoStack.Count} redoable");
                return 0;
            });
        });
        return command;
    }

    private static Command BuildApply()
    {
        var dir = ProjectArgument();
        var file = new Argument<FileInfo>("opsFile", "JSONL file with one edit operation per line, e.g. {\"op\":\"deleteActor\",\"target\":{\"level\":\"/Game/...\",\"actor\":\"...\"}}.");
        var command = new Command("apply", "Validate and append edit operations from a JSONL file to the journal (stops at the first invalid one).") { dir, file };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ProjectCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await LevelCliSupport.GuardedAsync(logger, async () =>
            {
                var lines = await File.ReadAllLinesAsync(parse.GetValueForArgument(file).FullName, ct).ConfigureAwait(false);
                using var project = Project.Open(parse.GetValueForArgument(dir));
                var applied = 0;
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    EditOp op;
                    try
                    {
                        op = EditOp.FromJson(line);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogError("Line {Line}: not an edit operation: {Message}", i + 1, ex.Message);
                        return 2;
                    }

                    if (project.State.Validate(op) is { } error)
                    {
                        logger.LogError("Line {Line}: {Error}", i + 1, error);
                        return 2;
                    }

                    var entry = project.Apply(op);
                    Console.Out.WriteLine($"#{entry.Seq}: {op.Describe()}");
                    applied++;
                }

                logger.LogInformation("Applied {Count} edit(s).", applied);
                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static void PrintExportSet(Project project)
    {
        var set = project.PendingExportSet;
        Console.Out.WriteLine($"pending export set: {set.Count} level(s)");
        foreach (var level in set)
        {
            Console.Out.WriteLine($"  {level}");
        }

        var assets = project.PendingAssetSet;
        if (assets.Count > 0)
        {
            Console.Out.WriteLine($"vehicle/item packages: {assets.Count} ({project.State.AssetClones.Count} clone(s), {project.State.AssetValueOverrides.Count} value(s))");
            foreach (var asset in assets)
            {
                Console.Out.WriteLine($"  {asset}");
            }
        }
    }

    private static void WriteWarnings(Project project, ILogger logger)
    {
        foreach (var w in project.Journal.Warnings)
        {
            logger.LogWarning("{Warning}", w);
        }
    }
}

/// <summary>Shared plumbing for the level and project commands.</summary>
internal static class LevelCliSupport
{
    /// <summary>Opens the catalog with the key from --aes or the environment (validated first, never echoed).</summary>
    public static AssetCatalog OpenCatalog(string source, string? aes, ILogger logger)
    {
        var key = string.IsNullOrWhiteSpace(aes) ? AesKeyText.FromEnvironmentOrStore() : aes;
        return AssetCatalog.Open(source, new AssetCatalogOptions
        {
            AesKey = string.IsNullOrWhiteSpace(key) ? null : AesKeyText.Normalize(key),
            Logger = logger,
        });
    }

    public static int Guarded(ILogger logger, Func<int> action) =>
        GuardedAsync(logger, () => Task.FromResult(action())).GetAwaiter().GetResult();

    public static async Task<int> GuardedAsync(ILogger logger, Func<Task<int>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or InvalidOperationException or IOException)
        {
            logger.LogError("{Message}", ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Key text is validated by AesKeyText before it reaches CUE4Parse and is passed as bytes, so no message can contain it.
            logger.LogError("{Type}: {Message}", ex.GetType().Name, ex.Message);
            return 1;
        }
    }
}
