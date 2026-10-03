using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ScumStudio.Core.Abstractions;
using ScumStudio.Pak;
using ScumStudio.Pak.Inspection;
using ScumStudio.Pak.Reading;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio pak info|list|unpack|pack</c>: inspect, extract and build .pak files.
/// Every subcommand accepts <c>--aes &lt;hex&gt;</c> (or the <c>SCUMSTUDIO_AES_KEY</c> environment variable) for encrypted
/// stock paks; the key is never printed or logged.
/// </summary>
internal sealed class PakCommands : ICommandModule
{
    /// <inheritdoc />
    public Command Build()
    {
        var pak = new Command("pak", "Inspect, list, unpack and build .pak files (UE 4.27, pak v11).");
        pak.AddCommand(BuildInfo());
        pak.AddCommand(BuildList());
        pak.AddCommand(BuildUnpack());
        pak.AddCommand(BuildPack());
        return pak;
    }

    private static Option<string?> AesOption() =>
        new(["--aes", "-a"], $"AES-256 key (64 hex chars) for encrypted stock paks. Prefer the {AesKeyText.EnvironmentVariable} environment variable: command lines can end up in shell history.");

    private static Argument<FileInfo> PakArgument() =>
        new Argument<FileInfo>("pak", "Path of the .pak file.").ExistingOnly();

    private static Command BuildInfo()
    {
        var pakArg = PakArgument();
        var aes = AesOption();
        var verify = new Option<bool>("--verify", "Also check every entry (SHA-1, sizes, zlib blocks); unencrypted v10/v11 paks only.");
        var command = new Command("info", "Print a pak's version, mount point, encryption, compression and entry count.") { pakArg, aes, verify };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<PakCommands>();
            var file = ctx.ParseResult.GetValueForArgument(pakArg);
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                var header = PakInspector.ReadHeader(file.FullName);
                var o = Console.Out;
                o.WriteLine($"file: {header.FilePath}");
                o.WriteLine($"size: {header.FileLength.ToString("N0", CultureInfo.InvariantCulture)} bytes");
                o.WriteLine($"version: {header.Version}{(header.Version == PakFormat.VersionFnv64BugFix ? " (Fnv64BugFix, UE 4.26/4.27)" : string.Empty)}");
                o.WriteLine($"encrypted index: {(header.EncryptedIndex ? "yes" : "no")}");
                o.WriteLine($"encryption key guid: {header.EncryptionKeyGuid:N}");
                o.WriteLine($"compression: {(header.CompressionMethods.Count == 0 ? "None" : string.Join(", ", header.CompressionMethods))}");
                o.WriteLine($"index: offset {header.IndexOffset}, size {header.IndexSize}, sha1 {header.IndexSha1.ToLowerInvariant()}");
                if (!header.EncryptedIndex)
                {
                    o.WriteLine($"mount point: {header.MountPoint}");
                    o.WriteLine($"entries: {header.EntryCount}");
                    if (header.PathHashSeed is { } seed)
                    {
                        o.WriteLine($"path hash seed: {seed:X8}");
                        o.WriteLine($"path hash index: {(header.HasPathHashIndex == true ? "yes" : "no")}, full directory index: {(header.HasFullDirectoryIndex == true ? "yes" : "no")}");
                    }
                }
                else
                {
                    using var source = OpenPak(file, ctx, aes, logger);
                    var archive = source.Archives.Single();
                    o.WriteLine(archive.IsMounted
                        ? $"entries: {archive.FileCount} (read with the AES key)"
                        : "entries: unknown (index is encrypted; pass --aes or set " + AesKeyText.EnvironmentVariable + ")");
                }

                if (ctx.ParseResult.GetValueForOption(verify))
                {
                    var problems = await PakInspector.VerifyAsync(file.FullName, ctx.GetCancellationToken()).ConfigureAwait(false);
                    foreach (var problem in problems)
                    {
                        o.WriteLine($"PROBLEM: {problem}");
                    }

                    o.WriteLine(problems.Count == 0 ? "verify: OK" : $"verify: {problems.Count} problem(s)");
                    return problems.Count == 0 ? 0 : 1;
                }

                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildList()
    {
        var pakArg = PakArgument();
        var aes = AesOption();
        var filter = new Option<string?>(["--filter", "-f"], "Only entries matching this text (case-insensitive substring; * and ? are wildcards).");
        var command = new Command("list", "List the entries of a pak as virtual paths (SCUM/Content/...).") { pakArg, aes, filter };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<PakCommands>();
            var file = ctx.ParseResult.GetValueForArgument(pakArg);
            var match = BuildMatcher(ctx.ParseResult.GetValueForOption(filter));
            ctx.ExitCode = RunGuarded(logger, () =>
            {
                using var source = OpenPak(file, ctx, aes, logger);
                if (!source.Archives.Single().IsMounted)
                {
                    logger.LogError("{Pak} could not be mounted (encrypted: pass --aes or set {Variable}).", file.Name, AesKeyText.EnvironmentVariable);
                    return 2;
                }

                var count = 0;
                foreach (var path in source.EnumerateArchiveFiles(file.Name).Where(match))
                {
                    Console.Out.WriteLine(path);
                    count++;
                }

                logger.LogInformation("{Count} entries.", count);
                return 0;
            });
        });
        return command;
    }

    private static Command BuildUnpack()
    {
        var pakArg = PakArgument();
        var aes = AesOption();
        var output = new Option<DirectoryInfo>(["--output", "-o"], "Output directory (receives SCUM/Content/...).") { IsRequired = true };
        var include = new Option<string[]>(["--include", "-i"], "Only entries below this virtual path prefix (repeatable), e.g. SCUM/Content/ConZ_Files/Maps.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("unpack", "Extract a pak's entries to a directory.") { pakArg, aes, output, include };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<PakCommands>();
            var file = ctx.ParseResult.GetValueForArgument(pakArg);
            var outDir = ctx.ParseResult.GetValueForOption(output)!;
            var prefixes = (ctx.ParseResult.GetValueForOption(include) ?? []).Select(VirtualPath.Normalize).Where(p => p.Length > 0).ToArray();
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                using var source = OpenPak(file, ctx, aes, logger);
                if (!source.Archives.Single().IsMounted)
                {
                    logger.LogError("{Pak} could not be mounted (encrypted: pass --aes or set {Variable}).", file.Name, AesKeyText.EnvironmentVariable);
                    return 2;
                }

                var count = 0;
                long bytes = 0;
                foreach (var path in source.EnumerateArchiveFiles(file.Name))
                {
                    ct.ThrowIfCancellationRequested();
                    if (prefixes.Length > 0 && !prefixes.Any(p => VirtualPath.IsUnder(path, p)))
                    {
                        continue;
                    }

                    var data = await source.ReadBytesAsync(path, ct).ConfigureAwait(false)
                               ?? throw new IOException($"Could not read {path}.");
                    var target = PakPaths.ToSafeFilePath(outDir.FullName, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target, data, ct).ConfigureAwait(false);
                    count++;
                    bytes += data.Length;
                }

                logger.LogInformation("Unpacked {Count} files ({Bytes:N0} bytes) to {Dir}.", count, bytes, outDir.FullName);
                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildPack()
    {
        var staging = new Argument<DirectoryInfo>("stagingDir", "Staging folder laid out as a project root (contains SCUM/Content/..., optionally SCUM/AssetRegistry.bin).").ExistingOnly();
        var aes = AesOption();
        var output = new Option<FileInfo>(["--output", "-o"], "Output pak, e.g. pakchunk96-F16_P.pak.") { IsRequired = true };
        var compression = new Option<string>("--compression", () => "none", "Entry compression: none or zlib.").FromAmong("none", "zlib", "None", "Zlib");
        var sig = new Option<FileInfo?>("--sig", "Stock .sig to copy next to the pak (e.g. pakchunk44-WindowsNoEditor.sig).");
        var mount = new Option<string>(["--mount-point", "-m"], () => PakPaths.DefaultMountPoint, "Mount point.");
        var noRegistry = new Option<bool>("--no-asset-registry", "Leave SCUM/AssetRegistry.bin out even if it is staged.");
        var command = new Command("pack", "Build an unencrypted pak v11 from a staging folder.") { staging, aes, output, compression, sig, mount, noRegistry };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<PakCommands>();
            var parse = ctx.ParseResult;
            if (!string.IsNullOrEmpty(parse.GetValueForOption(aes)))
            {
                logger.LogWarning("--aes is ignored by 'pak pack': mod paks are written unencrypted.");
            }

            var options = new PakWriterOptions
            {
                Compression = string.Equals(parse.GetValueForOption(compression), "zlib", StringComparison.OrdinalIgnoreCase) ? PakCompression.Zlib : PakCompression.None,
                MountPoint = parse.GetValueForOption(mount) ?? PakPaths.DefaultMountPoint,
                IncludeAssetRegistry = !parse.GetValueForOption(noRegistry),
            };
            var outFile = parse.GetValueForOption(output)!;
            var sigFile = parse.GetValueForOption(sig);
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                if (sigFile is not null && !sigFile.Exists)
                {
                    logger.LogError("Signature file not found: {Sig}", sigFile.FullName);
                    return 2;
                }

                var writer = new PakWriter(options, CliHost.LoggerFactory.CreateLogger<PakWriter>());
                var result = await writer.WriteFromDirectoryAsync(parse.GetValueForArgument(staging).FullName, outFile.FullName, null, ctx.GetCancellationToken()).ConfigureAwait(false);
                Console.Out.WriteLine($"{result.OutputPath}: {result.EntryCount} entries, {result.PakSize.ToString("N0", CultureInfo.InvariantCulture)} bytes"
                                      + $" ({result.CompressedEntryCount} compressed), {result.SkippedFiles.Count} staged file(s) skipped");
                if (sigFile is not null)
                {
                    Console.Out.WriteLine(SigCopier.Copy(sigFile.FullName, outFile.FullName, logger: logger));
                }

                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    /// <summary>Opens a single pak with the key from --aes or the environment (never echoed).</summary>
    private static PakFileSource OpenPak(FileInfo file, InvocationContext ctx, Option<string?> aes, ILogger logger)
    {
        var key = ctx.ParseResult.GetValueForOption(aes);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = AesKeyText.FromEnvironmentOrStore();
        }

        return PakFileSource.OpenFile(file.FullName, new PakFileSourceOptions
        {
            AesKey = string.IsNullOrWhiteSpace(key) ? null : key,
            Logger = logger,
        });
    }

    private static Func<string, bool> BuildMatcher(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return _ => true;
        }

        if (filter.IndexOfAny(['*', '?']) < 0)
        {
            return path => path.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        var pattern = "^" + Regex.Escape(filter).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.IsMatch;
    }

    private static int RunGuarded(ILogger logger, Func<int> action) =>
        RunGuardedAsync(logger, () => Task.FromResult(action())).GetAwaiter().GetResult();

    private static async Task<int> RunGuardedAsync(ILogger logger, Func<Task<int>> action)
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException
                                       or InvalidOperationException or NotSupportedException)
        {
            // Messages from our code never contain key material (AesKeyText validates before CUE4Parse sees the key).
            logger.LogError("{Message}", ex.Message);
            return 1;
        }
    }
}
