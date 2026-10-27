using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using ScumStudio.Mcp.Studio;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio server</c>: Lua mods that run inside the dedicated server (through UE4SS), such as announcements, a join
/// message or an event log. The commands install UE4SS next to <c>SCUMServer.exe</c> and manage the mods as files; they never
/// start or stop the server.
/// </summary>
internal sealed class ServerCommands : ICommandModule
{
    /// <inheritdoc />
    public Command Build()
    {
        var server = new Command("server", "Lua mods for the dedicated server (run inside it through UE4SS).");
        server.AddCommand(BuildStatus());
        server.AddCommand(BuildInstall());
        server.AddCommand(BuildApplySettings());
        server.AddCommand(BuildSwitch());
        server.AddCommand(BuildTemplates());
        server.AddCommand(BuildMods());
        server.AddCommand(BuildAdd());
        server.AddCommand(BuildMod("enable", "Switch a server mod on.", (store, name) => store.SetEnabled(name, true)));
        server.AddCommand(BuildMod("disable", "Switch a server mod off.", (store, name) => store.SetEnabled(name, false)));
        server.AddCommand(BuildMod("remove", "Delete a server mod.", (store, name) => store.Remove(name)));
        server.AddCommand(BuildLog());
        return server;
    }

    private static Option<string?> ServerOption() =>
        new("--server", $"The server: its Paks folder, SCUM\\Binaries\\Win64 or the install folder (or set {HeadlessStudioHost.ServerSourceVariable}).");

    private static ServerInstall? Resolve(string? given, ILogger logger)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var path = given ?? Environment.GetEnvironmentVariable(HeadlessStudioHost.ServerSourceVariable);
        if (ServerInstall.Find(path) is { } found)
        {
            return found;
        }

        logger.LogError("No SCUMServer.exe found from '{Path}': pass --server <the server's Paks or install folder>.", path ?? "(nothing)");
        return null;
    }

    private static Command BuildStatus()
    {
        var serverOption = ServerOption();
        var command = new Command("status", "Where the server is, whether UE4SS is installed and on, and which mods it has.") { serverOption };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ServerCommands>();
            if (Resolve(ctx.ParseResult.GetValueForOption(serverOption), logger) is not { } server)
            {
                ctx.ExitCode = 2;
                return;
            }

            Console.Out.WriteLine($"server:  {server.Win64}");
            Console.Out.WriteLine($"UE4SS:   {(server.IsUe4ssActive ? "installed, on" : server.HasUe4ss ? "installed, switched off" : "not installed")}");
            foreach (var mod in new ServerModStore(server).List())
            {
                Console.Out.WriteLine($"mod:     {mod.Name} ({(mod.Enabled ? "on" : "off")})");
            }

            ctx.ExitCode = 0;
        });
        return command;
    }

    private static Command BuildInstall()
    {
        var serverOption = ServerOption();
        var zip = new Option<string?>("--zip", "A UE4SS zip you already have. Without it the latest official release is downloaded from github.com/UE4SS-RE/RE-UE4SS and checked against the SHA-256 GitHub published.");
        var command = new Command("install-ue4ss", "Install UE4SS next to SCUMServer.exe and set it up for a server (existing settings and mods are kept).") { serverOption, zip };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ServerCommands>();
            var ct = ctx.GetCancellationToken();
            if (Resolve(ctx.ParseResult.GetValueForOption(serverOption), logger) is not { } server)
            {
                ctx.ExitCode = 2;
                return;
            }

            try
            {
                var zipPath = ctx.ParseResult.GetValueForOption(zip);
                string? temp = null;
                if (zipPath is null)
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    var release = await Ue4ssInstaller.FindLatestAsync(http, ct).ConfigureAwait(false);
                    logger.LogInformation("Downloading {Name} ({Size:N1} MB) from github.com/UE4SS-RE/RE-UE4SS.", release.Name, release.Size / 1_000_000d);
                    temp = Path.Combine(Path.GetTempPath(), "scumstudio-ue4ss-" + Guid.NewGuid().ToString("N"));
                    zipPath = await Ue4ssInstaller.DownloadAsync(http, release, temp, ct).ConfigureAwait(false);
                    logger.LogInformation("SHA-256 checked: {Hash}", release.Sha256 ?? "(GitHub published none)");
                }

                var written = Ue4ssInstaller.InstallFromZip(server, zipPath);
                if (temp is not null)
                {
                    Directory.Delete(temp, recursive: true);
                }

                logger.LogInformation("UE4SS installed: {Count} file(s) in {Folder}. Start the server to load it.", written, server.Win64);
                ctx.ExitCode = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
            {
                logger.LogError("Install failed: {Message}", ex.Message);
                ctx.ExitCode = 1;
            }
        });
        return command;
    }

    private static Command BuildApplySettings()
    {
        var serverOption = ServerOption();
        var command = new Command("apply-settings", "Write the settings a server needs into UE4SS-settings.ini (hooks that crash the server on a player join off, forced object iteration on). The server must be stopped.") { serverOption };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ServerCommands>();
            if (Resolve(ctx.ParseResult.GetValueForOption(serverOption), logger) is not { } server || !server.HasUe4ss)
            {
                ctx.ExitCode = 2;
                return;
            }

            try
            {
                Ue4ssInstaller.ApplyServerSettings(server);
                logger.LogInformation("{Count} setting(s) written to {File}.", Ue4ssInstaller.ServerSettings.Count, server.SettingsFile);
                ctx.ExitCode = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("{Message}", ex.Message);
                ctx.ExitCode = 1;
            }
        });
        return command;
    }

    private static Command BuildSwitch()
    {
        var serverOption = ServerOption();
        var state = new Argument<string>("state", "on or off.");
        var command = new Command("ue4ss", "Switch UE4SS on or off (the server must be stopped).") { state, serverOption };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ServerCommands>();
            var parse = ctx.ParseResult;
            var wanted = parse.GetValueForArgument(state);
            if (Resolve(parse.GetValueForOption(serverOption), logger) is not { } server || wanted is not ("on" or "off"))
            {
                ctx.ExitCode = 2;
                return;
            }

            try
            {
                Ue4ssInstaller.SetActive(server, wanted == "on");
                logger.LogInformation("UE4SS is {State}.", wanted);
                ctx.ExitCode = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("{Message} (is the server still running?)", ex.Message);
                ctx.ExitCode = 1;
            }
        });
        return command;
    }

    private static Command BuildTemplates()
    {
        var command = new Command("templates", "List the ready mods (add one with 'server add <template>').");
        command.SetHandler((InvocationContext ctx) =>
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            foreach (var template in ServerModTemplates.All)
            {
                Console.Out.WriteLine($"{template.Id}\t{template.Code.Split('\n')[0].Trim().TrimStart('-', ' ')}");
            }

            ctx.ExitCode = 0;
        });
        return command;
    }

    private static Command BuildMods()
    {
        var serverOption = ServerOption();
        var command = new Command("mods", "List the server's Lua mods.") { serverOption };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ServerCommands>();
            if (Resolve(ctx.ParseResult.GetValueForOption(serverOption), logger) is not { } server)
            {
                ctx.ExitCode = 2;
                return;
            }

            foreach (var mod in new ServerModStore(server).List())
            {
                Console.Out.WriteLine($"{mod.Name}\t{(mod.Enabled ? "on" : "off")}\t{mod.ScriptPath}");
            }

            ctx.ExitCode = 0;
        });
        return command;
    }

    private static Command BuildAdd()
    {
        var serverOption = ServerOption();
        var template = new Argument<string>("template", "A template from 'server templates'.");
        var name = new Option<string?>("--name", "The mod's name (default: the template's).");
        var file = new Option<string?>("--file", "Use this Lua file instead of the template's text.");
        var command = new Command("add", "Add a mod from a template (or --file) and switch it on. A running server loads it by itself.") { template, serverOption, name, file };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ServerCommands>();
            var parse = ctx.ParseResult;
            if (Resolve(parse.GetValueForOption(serverOption), logger) is not { } server)
            {
                ctx.ExitCode = 2;
                return;
            }

            var id = parse.GetValueForArgument(template);
            var source = ServerModTemplates.All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            var code = parse.GetValueForOption(file) is { } path ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : source?.Code;
            if (code is null)
            {
                logger.LogError("Unknown template '{Template}': see 'scumstudio server templates'.", id);
                ctx.ExitCode = 2;
                return;
            }

            try
            {
                var mod = new ServerModStore(server).Save(parse.GetValueForOption(name) ?? source?.Id ?? id, code);
                logger.LogInformation("Mod {Name} saved and on: {Path}", mod.Name, mod.ScriptPath);
                ctx.ExitCode = 0;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                logger.LogError("{Message}", ex.Message);
                ctx.ExitCode = 1;
            }
        });
        return command;
    }

    private static Command BuildMod(string verb, string description, Action<ServerModStore, string> act)
    {
        var serverOption = ServerOption();
        var name = new Argument<string>("name", "The mod's name (see 'server mods').");
        var command = new Command(verb, description) { name, serverOption };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ServerCommands>();
            if (Resolve(ctx.ParseResult.GetValueForOption(serverOption), logger) is not { } server)
            {
                ctx.ExitCode = 2;
                return;
            }

            try
            {
                act(new ServerModStore(server), ctx.ParseResult.GetValueForArgument(name));
                logger.LogInformation("Done.");
                ctx.ExitCode = 0;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                logger.LogError("{Message}", ex.Message);
                ctx.ExitCode = 1;
            }
        });
        return command;
    }

    private static Command BuildLog()
    {
        var serverOption = ServerOption();
        var lines = new Option<int>("--lines", () => 30, "How many lines from the end.");
        var command = new Command("log", "The end of UE4SS's log: what the mods print and why one did not load.") { serverOption, lines };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ServerCommands>();
            if (Resolve(ctx.ParseResult.GetValueForOption(serverOption), logger) is not { } server || !File.Exists(server.LogFile))
            {
                ctx.ExitCode = 2;
                return;
            }

            using var stream = new FileStream(server.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            foreach (var line in reader.ReadToEnd().Split('\n').TakeLast(ctx.ParseResult.GetValueForOption(lines)))
            {
                Console.Out.WriteLine(line.TrimEnd('\r'));
            }

            ctx.ExitCode = 0;
        });
        return command;
    }
}
