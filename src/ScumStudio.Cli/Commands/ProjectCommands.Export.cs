using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak;

namespace ScumStudio.Cli.Commands;

/// <summary><c>scumstudio project export</c>: rewrite the edited levels and pack the mod paks.</summary>
internal sealed partial class ProjectCommands
{
    private static Command BuildExport()
    {
        var dir = ProjectArgument();
        var output = new Option<string>(["--output", "-o"],
            $"Output folder: <out>/{ProjectExporter.ClientFolderName}/pakchunkNNN-<Name>_P.pak (+ .sig, export-report.md, staging/) and <out>/{ProjectExporter.ServerFolderName}/... when a server source is known.")
        {
            IsRequired = true,
        };
        var source = new Option<string?>(["--source", "-s"], "Client source: Paks folder, single .pak or loose folder (default: the project's client source).");
        var serverSource = new Option<string?>("--server-source", "Server source: SCUMServer Paks folder, .pak or loose folder (default: the project's server source, if any).");
        var clientOnly = new Option<bool>("--client-only", "Skip the server export even when a server source is known.");
        var name = new Option<string?>(["--name", "-n"], "Mod name for the pak file name (default: the project name; letters, digits, _ and - are kept).");
        var pakIndex = new Option<int>("--pak-index", () => ProjectExporter.DefaultPakChunkIndex, "pakchunk number of the mod pak.");
        var sig = new Option<string?>("--sig", $"Stock .sig to copy next to the pak (default: {Pak.Writing.SigCopier.DefaultStockSigName} or another stock .sig found in the source Paks folder).");
        var noPak = new Option<bool>("--no-pak", "Only write the staged loose files (staging/SCUM/Content/...), do not pack.");
        var aes = new Option<string?>(["--aes", "-a"], $"AES-256 key (64 hex chars) for encrypted stock paks. Prefer the {AesKeyText.EnvironmentVariable} environment variable.");
        var command = new Command("export", "Rewrite every edited level (deleted actors, moved actors) and pack pakchunkNNN-<Name>_P.pak for the client and, with a server source, for the server.")
        {
            dir, output, source, serverSource, clientOnly, name, pakIndex, sig, noPak, aes,
        };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ProjectCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = await LevelCliSupport.GuardedAsync(logger, async () =>
            {
                using var project = Project.Open(parse.GetValueForArgument(dir));
                if (project.State.IsEmpty)
                {
                    logger.LogError("Nothing to export: the project has no applied edits.");
                    return 2;
                }

                var clientSource = parse.GetValueForOption(source)
                                   ?? project.Manifest.Sources.FirstOrDefault(s => s.Role == ProjectSourceRole.Client)?.Path;
                var server = parse.GetValueForOption(clientOnly)
                    ? null
                    : parse.GetValueForOption(serverSource) ?? project.Manifest.Sources.FirstOrDefault(s => s.Role == ProjectSourceRole.Server)?.Path;
                if (clientSource is null && server is null)
                {
                    logger.LogError("No game files: pass --source <Paks folder | .pak | loose folder> (or create the project with --source).");
                    return 2;
                }

                var options = new ExportOptions
                {
                    OutputDirectory = parse.GetValueForOption(output)!,
                    ModName = parse.GetValueForOption(name),
                    PakChunkIndex = parse.GetValueForOption(pakIndex),
                    StockSigPath = parse.GetValueForOption(sig),
                    WritePak = !parse.GetValueForOption(noPak),
                };
                var aesText = parse.GetValueForOption(aes);
                var exporter = new ProjectExporter(logger);
                BendSupport? meshes = null; // the client's: its answers serve the server export (no render geometry there)
                foreach (var (path, role) in new[] { (clientSource, ProjectSourceRole.Client), (server, ProjectSourceRole.Server) })
                {
                    if (path is null)
                    {
                        continue;
                    }

                    using var catalog = LevelCliSupport.OpenCatalog(path, aesText, logger);
                    meshes ??= new BendSupport(catalog);
                    var result = await exporter.ExportAsync(project, catalog, options with { BendMeshes = meshes.Describe }, role, null, ctx.GetCancellationToken()).ConfigureAwait(false);
                    PrintExport(result);
                    foreach (var warning in result.Warnings)
                    {
                        logger.LogWarning("{Warning}", warning);
                    }
                }

                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static void PrintExport(ExportResult result)
    {
        var o = Console.Out;
        o.WriteLine($"{result.Role}: {result.Levels.Count} level(s) rewritten, {result.RemovedActorCount} actor(s) removed, {result.PatchedTransformCount} transform(s) written");
        if (result.Assets.Count > 0)
        {
            o.WriteLine($"  vehicles/items: {result.Assets.Count} package(s) ({result.Assets.Count(a => a.IsClone)} new), {result.AssetValues.Count} value(s) written, {result.Registered.Count} AssetRegistry record(s) added");
        }
        foreach (var level in result.Levels)
        {
            var r = level.Report;
            o.WriteLine($"  {level.PackagePath}: actors {r.ActorsBefore} -> {r.ActorsAfter}"
                        + (r.RemovedActors.Count > 0 ? $"; removed {string.Join(", ", r.RemovedActors)}" : string.Empty)
                        + (r.PatchedTransforms.Count > 0 ? $"; moved {string.Join(", ", r.PatchedTransforms)}" : string.Empty));
        }

        o.WriteLine($"  pak: {result.PakPath ?? "(not written)"}");
        o.WriteLine($"  sig: {result.SigPath ?? "(none — copy a stock .sig next to the pak)"}");
        o.WriteLine($"  staging: {result.StagingDirectory}");
        o.WriteLine($"  report: {result.ReportPath}");
    }
}
