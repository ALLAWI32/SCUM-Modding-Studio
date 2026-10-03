using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio pkg ...</c>: inspect cooked packages with the in-house reader/writer (ScumStudio.Formats).
/// <list type="bullet">
/// <item><c>pkg info &lt;path&gt;</c> - header dump (like <c>ue4pkg.py</c>).</item>
/// <item><c>pkg props &lt;path&gt; [--export N] [--json]</c> - tagged properties per export (like <c>dumpprops.py</c>).</item>
/// <item><c>pkg verify-roundtrip &lt;path|dir&gt; [--recursive]</c> - byte-exact rebuild check (like <c>ue4write.py</c>).</item>
/// </list>
/// </summary>
internal sealed class PkgCommands : ICommandModule
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <inheritdoc />
    public Command Build()
    {
        var pkg = new Command("pkg", "Inspect cooked UE 4.27 packages (.uasset/.umap + .uexp).");
        pkg.AddCommand(BuildInfo());
        pkg.AddCommand(BuildProps());
        pkg.AddCommand(BuildVerify());
        pkg.AddCommand(BuildTileInfo());
        return pkg;
    }

    private static Command BuildTileInfo()
    {
        var path = new Argument<string>("path", "A level package (with or without extension) or a folder of .umap files.");
        var recursive = new Option<bool>(["--recursive", "-r"], "Recurse into sub-folders.");
        var json = new Option<bool>("--json", "Write JSON instead of text.");
        var verify = new Option<bool>("--verify", "Re-serialise each record and check it matches the stored bytes exactly.");
        var cmd = new Command("tile-info", "Print the World Composition tile record (FWorldTileInfo: bounds, layer, streaming distance, parent) stored in each sublevel's summary.")
        {
            path, recursive, json, verify,
        };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = Run(() =>
            {
                var target = ctx.ParseResult.GetValueForArgument(path);
                var files = Directory.Exists(target)
                    ? Directory.EnumerateFiles(target, "*.umap", ctx.ParseResult.GetValueForOption(recursive) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToList()
                    : [target];
                var asJson = ctx.ParseResult.GetValueForOption(json);
                var doVerify = ctx.ParseResult.GetValueForOption(verify);
                var rows = new JsonArray();
                int tiles = 0, others = 0, ok = 0, diff = 0;
                foreach (var file in files)
                {
                    var p = CookedPackage.Load(file);
                    var info = WorldTileInfo.TryRead(p);
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (info is null)
                    {
                        others++;
                        if (!asJson)
                        {
                            Console.WriteLine($"{name}\t(no tile info)");
                        }

                        continue;
                    }

                    tiles++;
                    string? verdict = null;
                    if (doVerify)
                    {
                        var w = new ByteWriter();
                        info.Write(w);
                        var stored = p.UAsset.AsSpan(p.Summary.WorldTileInfoDataOffset, info.SerializedLength);
                        var same = w.WrittenSpan.SequenceEqual(stored);
                        verdict = same ? "OK" : "DIFF";
                        if (same) ok++; else diff++;
                    }

                    if (asJson)
                    {
                        rows.Add(new JsonObject
                        {
                            ["package"] = name,
                            ["boundsMin"] = new JsonArray(info.BoundsMin.X, info.BoundsMin.Y, info.BoundsMin.Z),
                            ["boundsMax"] = new JsonArray(info.BoundsMax.X, info.BoundsMax.Y, info.BoundsMax.Z),
                            ["layer"] = info.Layer.Name,
                            ["streamingDistance"] = info.Layer.StreamingDistance,
                            ["distanceStreamingEnabled"] = info.Layer.DistanceStreamingEnabled,
                            ["parent"] = info.HasParent ? info.ParentTilePackageName : null,
                            ["zOrder"] = info.ZOrder,
                            ["lodCount"] = info.LodList.Count,
                            ["serializedLength"] = info.SerializedLength,
                            ["verify"] = verdict,
                        });
                    }
                    else
                    {
                        Console.WriteLine($"{name}\t{info}{(verdict is null ? string.Empty : $"\t[{verdict}]")}");
                    }
                }

                if (asJson)
                {
                    Console.WriteLine(rows.ToJsonString(JsonOptions));
                }
                else
                {
                    Console.WriteLine($"{tiles} tile(s), {others} package(s) without tile info{(doVerify ? $", re-serialised {ok} identical, {diff} different" : string.Empty)}");
                }

                return doVerify && diff > 0 ? 1 : 0;
            });
        });
        return cmd;
    }

    private static Command BuildInfo()
    {
        var path = new Argument<string>("path", "Package path, with or without extension.");
        var deps = new Option<bool>("--deps", "Also print the event-driven-loader preload dependencies of every export.");
        var export = new Option<int?>("--export", "With --deps: only this export (1-based, as listed).");
        var cmd = new Command("info", "Print the package summary, names, imports and exports.") { path, deps, export };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = Run(() =>
            {
                var p = CookedPackage.Load(ctx.ParseResult.GetValueForArgument(path));
                Console.WriteLine(p.Describe());
                if (ctx.ParseResult.GetValueForOption(deps))
                {
                    var only = ctx.ParseResult.GetValueForOption(export);
                    Console.WriteLine(p.DescribeDependencies(only is { } e ? e - 1 : -1));
                }

                return 0;
            });
        });
        return cmd;
    }

    private static Command BuildProps()
    {
        var path = new Argument<string>("path", "Package path, with or without extension.");
        var export = new Option<int?>("--export", "Only this export (1-based, as listed by 'pkg info').");
        var json = new Option<bool>("--json", "Write JSON instead of text.");
        var cmd = new Command("props", "Dump the tagged properties of every export.") { path, export, json };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = Run(() =>
            {
                var p = CookedPackage.Load(ctx.ParseResult.GetValueForArgument(path));
                var only = ctx.ParseResult.GetValueForOption(export);
                var asJson = ctx.ParseResult.GetValueForOption(json);
                if (only is { } n && (n < 1 || n > p.Exports.Count))
                {
                    Console.Error.WriteLine($"--export must be between 1 and {p.Exports.Count}.");
                    return 2;
                }

                var indices = only is { } one ? [one - 1] : Enumerable.Range(0, p.Exports.Count).ToArray();
                var array = new JsonArray();
                if (!asJson)
                {
                    Console.WriteLine(p.Describe());
                }

                foreach (var i in indices)
                {
                    var e = p.Exports[i];
                    try
                    {
                        var block = p.ReadProperties(i);
                        if (asJson)
                        {
                            array.Add(block.ToJson());
                            continue;
                        }

                        Console.WriteLine($"\n--- EXPORT {i + 1}: {p.ResolveName(e.ObjectName)} ({p.ResolveIndex(e.ClassIndex)}) size={e.SerialSize}");
                        var text = block.Format();
                        if (text.Length > 0)
                        {
                            Console.WriteLine(text);
                        }

                        var head = p.GetExportData(i).Span[block.EndOffset..];
                        head = head[..Math.Min(48, head.Length)];
                        Console.WriteLine($"  [properties end @ {block.EndOffset}, remaining native bytes: {block.NativeDataLength}] head={Convert.ToHexString(head).ToLowerInvariant()}");
                    }
                    catch (Exception ex) when (ex is FormatException or EndOfStreamException)
                    {
                        if (asJson)
                        {
                            array.Add(new JsonObject { ["export"] = i + 1, ["name"] = p.ResolveName(e.ObjectName), ["error"] = ex.Message });
                        }
                        else
                        {
                            Console.WriteLine($"\n--- EXPORT {i + 1}: {p.ResolveName(e.ObjectName)} ({p.ResolveIndex(e.ClassIndex)}) size={e.SerialSize}");
                            Console.WriteLine("  !! property parse failed: " + ex.Message);
                        }
                    }
                }

                if (asJson)
                {
                    var root = new JsonObject { ["package"] = p.BasePath, ["exports"] = array };
                    Console.WriteLine(root.ToJsonString(JsonOptions));
                }

                return 0;
            });
        });
        return cmd;
    }

    private static Command BuildVerify()
    {
        var path = new Argument<string>("path", "A package path or a directory of packages.");
        var recursive = new Option<bool>(["--recursive", "-r"], "Recurse into sub-directories.");
        var storedHashes = new Option<bool>("--stored-hashes", "Reuse the stored name hashes instead of recomputing them.");
        var quiet = new Option<bool>(["--quiet", "-q"], "Only print packages that differ and the summary.");
        var cmd = new Command("verify-roundtrip", "Read, rebuild and compare packages byte-for-byte.") { path, recursive, storedHashes, quiet };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = Run(() =>
            {
                var target = ctx.ParseResult.GetValueForArgument(path);
                var stored = ctx.ParseResult.GetValueForOption(storedHashes);
                var q = ctx.ParseResult.GetValueForOption(quiet);
                var sw = Stopwatch.StartNew();
                IReadOnlyList<RoundTripResult> results = Directory.Exists(target)
                    ? RoundTripVerifier.VerifyDirectory(target, ctx.ParseResult.GetValueForOption(recursive), stored)
                    : [RoundTripVerifier.Verify(target, stored)];
                foreach (var r in results)
                {
                    if (!q || !r.IsLayoutIdentical)
                    {
                        Console.WriteLine(r.ToString());
                    }
                }

                var ok = results.Count(r => r.IsIdentical);
                var okStored = results.Count(r => !r.IsIdentical && r.IsLayoutIdentical);
                var errors = results.Count(r => r.Error is not null);
                var diff = results.Count - ok - okStored - errors;
                Console.WriteLine($"{results.Count} packages: {ok} identical, {okStored} identical with stored name hashes, {diff} different, {errors} errors ({sw.Elapsed.TotalSeconds:F1} s)");
                return diff == 0 && errors == 0 && results.Count > 0 ? 0 : 1;
            });
        });
        return cmd;
    }

    internal static int Run(Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }
}

/// <summary>
/// <c>scumstudio assetreg ...</c>: read the cooked <c>AssetRegistry.bin</c> (port of <c>assetreg.py</c>).
/// <list type="bullet">
/// <item><c>assetreg list &lt;AssetRegistry.bin&gt; [--filter text] [--tags]</c></item>
/// <item><c>assetreg verify &lt;AssetRegistry.bin&gt;</c> - byte-identical round trip and name hash check.</item>
/// </list>
/// </summary>
internal sealed class AssetRegCommands : ICommandModule
{
    /// <inheritdoc />
    public Command Build()
    {
        var root = new Command("assetreg", "Inspect a cooked AssetRegistry.bin.");
        root.AddCommand(BuildList());
        root.AddCommand(BuildVerify());
        return root;
    }

    private static Command BuildList()
    {
        var file = new Argument<string>("file", "Path of AssetRegistry.bin.");
        var filter = new Option<string?>("--filter", "Case-insensitive substring of ObjectPath or AssetClass.");
        var tags = new Option<bool>("--tags", "Also print the tags of each asset.");
        var limit = new Option<int?>("--limit", "Stop after this many matches.");
        var cmd = new Command("list", "List asset records (ObjectPath, AssetClass, PackageName).") { file, filter, tags, limit };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = PkgCommands.Run(() =>
            {
                var reg = AssetRegistryFile.Load(ctx.ParseResult.GetValueForArgument(file));
                var f = ctx.ParseResult.GetValueForOption(filter);
                var withTags = ctx.ParseResult.GetValueForOption(tags);
                var max = ctx.ParseResult.GetValueForOption(limit) ?? int.MaxValue;
                var shown = 0;
                foreach (var a in reg.Assets)
                {
                    if (f is not null
                        && !a.ObjectPath.Contains(f, StringComparison.OrdinalIgnoreCase)
                        && !a.AssetClass.Contains(f, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (shown++ >= max)
                    {
                        break;
                    }

                    Console.WriteLine($"{a.ObjectPath}\t{a.AssetClass}\t{a.PackageName}");
                    if (withTags)
                    {
                        foreach (var (key, value) in reg.GetTags(a))
                        {
                            Console.WriteLine($"    {key} = {value.Text}");
                        }
                    }
                }

                Console.WriteLine($"{Math.Min(shown, max)} of {reg.AssetCount} assets (version {reg.Version}, {reg.Names.Count} names)");
                return 0;
            });
        });
        return cmd;
    }

    private static Command BuildVerify()
    {
        var file = new Argument<string>("file", "Path of AssetRegistry.bin.");
        var cmd = new Command("verify", "Parse and re-save the registry and compare byte-for-byte.") { file };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = PkgCommands.Run(() =>
            {
                var path = ctx.ParseResult.GetValueForArgument(file);
                var original = File.ReadAllBytes(path);
                var reg = AssetRegistryFile.Parse(original);
                var saved = reg.Save();
                var diff = RoundTripVerifier.FirstDifference(saved, original);
                var badHashes = reg.FindNameHashMismatches().Count;
                Console.WriteLine(diff is null
                    ? $"round trip OK: {reg.Names.Count} names, {reg.AssetCount} assets, store nums [{string.Join(", ", reg.StoreCounts)}], hash mismatches {badHashes}"
                    : $"round trip MISMATCH at byte {diff} (saved {saved.Length}, original {original.Length})");
                return diff is null && badHashes == 0 ? 0 : 1;
            });
        });
        return cmd;
    }
}
