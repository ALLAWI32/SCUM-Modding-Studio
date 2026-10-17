using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Assets.Utils;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.VirtualFileSystem;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Modding;

namespace ScumStudio.Level.Export;

/// <summary>What <see cref="ModPackageValidator"/> found in one staging folder.</summary>
public sealed record ValidationResult
{
    /// <summary>Packages read back.</summary>
    public int Packages { get; init; }

    /// <summary>Exports read back.</summary>
    public int Exports { get; init; }

    /// <summary>Exports whose size could be held to the game's own (the same object, or a class the game's packages read exactly).</summary>
    public int SizeCheckedExports { get; init; }

    /// <summary>Everything that would make the game load, play or tear down the mod differently from its own data; empty when it passed.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>How long the check took.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>True when nothing was found.</summary>
    public bool Passed => Problems.Count == 0;

    /// <summary>The export summary's line: <c>Validator: N packages checked, 0 problems</c>.</summary>
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"Validator: {Packages} packages checked, {Problems.Count} problems ({Exports} objects, {Elapsed.TotalSeconds:0.0} s)");
}

/// <summary>The validator found problems: the export stops and writes no pak (owner 2026-10-16: "everything safe, 100%").</summary>
public sealed class ModValidationException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="result"/>; <paramref name="listPath"/> is the file with every problem, when one was written.</summary>
    public ModValidationException(ValidationResult result, string? listPath = null)
        : base(Describe(result, listPath))
    {
        Result = result;
        ListPath = listPath;
    }

    /// <summary>What was found.</summary>
    public ValidationResult Result { get; }

    /// <summary>The file listing every problem (<see cref="ModPackageValidator.ListFileName"/> next to the staging folder), or null.</summary>
    public string? ListPath { get; }

    private static string Describe(ValidationResult result, string? listPath)
    {
        const int Shown = 25;
        var lines = result.Problems.Take(Shown).Select(p => "- " + p);
        var more = result.Problems.Count > Shown ? $"\n... and {result.Problems.Count - Shown} more" + (listPath is null ? "." : $" (all of them: {listPath}).") : string.Empty;
        return $"The mod was not packed: the package check found {result.Problems.Count} problem(s) that could crash the game or the server "
               + $"(loading, playing, leaving or closing). No pak was written. Please send the list to the ScumStudio team.\n"
               + string.Join('\n', lines) + more;
    }
}

/// <summary>
/// Reads every package an export staged back with CUE4Parse, export by export, against the game's own copy of the same
/// package, and lists what the game would trip over: data that loads and plays but is inconsistent enough to fault when the
/// world is torn down (leaving a server, Alt+F4, a server shutting down), and data that would not load at all. Owner
/// 2026-10-16: with a studio mod installed the game crashed on leaving and closing; the export must be "safe, 100%".
/// <list type="bullet">
/// <item>Each export must take exactly its <c>SerialSize</c> (held to the game: the same object in the game's package, or a
/// class the game's own packages read exactly), and the export, import and preload tables must point inside themselves.</item>
/// <item>Every object reference (tagged properties at any depth, the level's actor list, script actor, model, nav list) must
/// point inside the tables, never at an object the export deleted (a plain <c>Object</c> now: the game nulls the reference, a
/// list keeps a null entry), never at another object than the game's own data does, and the level's actor list must hold only
/// actors of that level, once each.</item>
/// <item>No two exports with one outer and one name; no outer or <c>AttachParent</c> loop; no component attached into
/// another actor unless it is that actor's root (or the game does the same).</item>
/// <item>Every new import must exist in the game or the mod; new GUIDs that must be unique in a level (<c>LightGuid</c>,
/// <c>MapBuildDataId</c>) must be; a Wwise event must be the same cook's as the game's copy (a dedicated server holds stubs);
/// every new <c>AssetRegistry.bin</c> record must name a package the game or the mod has.</item>
/// </list>
/// </summary>
public static class ModPackageValidator
{
    /// <summary>The file an export that failed the check writes next to its staging folder: every problem, one per line.</summary>
    public const string ListFileName = "package-check.txt";

    private const string InertClass = "/Script/CoreUObject.Object";
    private const int MaxProblemsPerPackage = 40;

    /// <summary>Checks every package under <paramref name="stagingDirectory"/> against <paramref name="game"/> (the cook's game files).</summary>
    public static ValidationResult Validate(string stagingDirectory, AssetCatalog game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var clock = Stopwatch.StartNew();
        var root = Path.GetFullPath(stagingDirectory);
        var files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(AssetPaths.IsPackageFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            staged[AssetPaths.ToPackagePath(Path.GetRelativePath(root, file).Replace('\\', '/'), game.ProjectName)] = file;
        }

        var context = new Context(game, staged);
        var problems = new ConcurrentBag<(string Package, string Problem)>();
        var exports = 0;
        var sized = 0;

        // Packages the game has first (they teach which classes read exactly), then the new ones (clones).
        var ordered = staged.OrderBy(s => game.PackageExists(s.Key) ? 0 : 1).ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var phase in ordered.GroupBy(s => game.PackageExists(s.Key)))
        {
            Parallel.ForEach(phase, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6), CancellationToken = cancellationToken }, entry =>
            {
                var found = new List<string>();
                var (count, checkedSize) = CheckPackage(context, entry.Key, entry.Value, found);
                Interlocked.Add(ref exports, count);
                Interlocked.Add(ref sized, checkedSize);
                foreach (var problem in found.Take(MaxProblemsPerPackage))
                {
                    problems.Add((entry.Key, problem));
                }

                if (found.Count > MaxProblemsPerPackage)
                {
                    problems.Add((entry.Key, $"{entry.Key}: {found.Count - MaxProblemsPerPackage} more problem(s) of the kinds above."));
                }
            });
        }

        foreach (var problem in CheckRegistry(root, game, staged))
        {
            problems.Add((AssetModBuilder.AssetRegistryPath, problem));
        }

        return new ValidationResult
        {
            Packages = staged.Count,
            Exports = exports,
            SizeCheckedExports = sized,
            Problems = problems.OrderBy(p => p.Package, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Problem, StringComparer.Ordinal).Select(p => p.Problem).ToList(),
            Elapsed = clock.Elapsed,
        };
    }

    /// <summary>
    /// What one cook of a project changes that the other does not (the server's pak against the client's): a level
    /// rewritten on one side only, actors removed or added on one side only, a vehicle or item clone in one pak only. A
    /// player's game and the server then disagree about what stands where or what can exist (the server keeps blocking with
    /// what the players no longer see, or replicates a class their pak lacks). Empty when both match.
    /// </summary>
    public static IReadOnlyList<string> CompareCooks(ExportResult client, ExportResult server)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(server);
        var lines = new List<string>();
        var c = client.Levels.GroupBy(l => l.PackagePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Report, StringComparer.OrdinalIgnoreCase);
        var s = server.Levels.GroupBy(l => l.PackagePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Report, StringComparer.OrdinalIgnoreCase);
        foreach (var level in c.Keys.Except(s.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"{level} is changed in the client pak only: the server keeps the game's level, so what players deleted or moved there still stands, blocks and spawns on the server.");
        }

        foreach (var level in s.Keys.Except(c.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"{level} is changed in the server pak only: players keep the game's level while the server has the edits.");
        }

        foreach (var (level, report) in c.Where(l => s.ContainsKey(l.Key)).OrderBy(l => l.Key, StringComparer.OrdinalIgnoreCase))
        {
            var other = s[level];
            Differ(level, "removed", report.RemovedActors, other.RemovedActors, lines);
            Differ(level, "added", report.AddedActors, other.AddedActors, lines);
        }

        var clientClones = client.Assets.Where(a => a.IsClone).Select(a => a.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var serverClones = server.Assets.Where(a => a.IsClone).Select(a => a.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var only in clientClones.Except(serverClones, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"{only} (a clone) is in the client pak only: the server cannot spawn or load it.");
        }

        foreach (var only in serverClones.Except(clientClones, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"{only} (a clone) is in the server pak only: players' games cannot show what the server spawns of it.");
        }

        return lines;

        static void Differ(string level, string what, IReadOnlyList<string> clientSide, IReadOnlyList<string> serverSide, List<string> lines)
        {
            var clientOnly = clientSide.Except(serverSide, StringComparer.OrdinalIgnoreCase).ToList();
            var serverOnly = serverSide.Except(clientSide, StringComparer.OrdinalIgnoreCase).ToList();
            if (clientOnly.Count > 0)
            {
                lines.Add($"{level}: {clientOnly.Count} actor(s) {what} in the client pak only ({string.Join(", ", clientOnly.Take(6))}).");
            }

            if (serverOnly.Count > 0)
            {
                lines.Add($"{level}: {serverOnly.Count} actor(s) {what} in the server pak only ({string.Join(", ", serverOnly.Take(6))}).");
            }
        }
    }

    /// <summary>The game's files and what this run learnt from them (shared by the packages checked in parallel).</summary>
    private sealed class Context(AssetCatalog game, IReadOnlyDictionary<string, string> staged)
    {
        public AssetCatalog Game { get; } = game;

        public IReadOnlyDictionary<string, string> Staged { get; } = staged;

        /// <summary>Class path → (exports of the game's packages read exactly, read with bytes left or failing).</summary>
        public ConcurrentDictionary<string, (int Exact, int Inexact)> ClassReads { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Class import path → the class object CUE4Parse builds the export from (null: none could be loaded).</summary>
        public ConcurrentDictionary<string, UStruct?> Classes { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Package path → its exports' paths (outer chain inside the package), for import checks.</summary>
        public ConcurrentDictionary<string, HashSet<string>?> ExportPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One package read back: tables, objects and the references they hold.</summary>
    private sealed class View
    {
        public required string Path { get; init; }

        public required Package Package { get; init; }

        public required byte[] UExp { get; init; }

        public byte[]? UBulk { get; init; }

        public required string[] ExportPaths { get; init; }

        public required string[] ClassPaths { get; init; }

        public required string[] ImportPaths { get; init; }

        public UObject?[] Objects { get; set; } = [];

        public long?[] Left { get; set; } = [];

        public string?[] Errors { get; set; } = [];

        public int Level { get; set; } = -1;

        public ULevel? LevelObject { get; set; }

        public int Count => ExportPaths.Length;

        public bool IsInert(int export) => string.Equals(ClassPaths[export], InertClass, StringComparison.OrdinalIgnoreCase)
                                           && !ExportPaths[export].StartsWith("Default__", StringComparison.Ordinal);

        /// <summary>Outer export of <paramref name="export"/> (0-based), or -1 at the top.</summary>
        public int OuterOf(int export) => Package.ExportMap[export].OuterIndex is { IsExport: true } o ? o.Index - 1 : -1;

        /// <summary>What a package index names, comparable between the game's package and the mod's.</summary>
        public string PathOf(int index) => index switch
        {
            0 => "null",
            > 0 when index <= ExportPaths.Length => ExportPaths[index - 1],
            < 0 when -index <= ImportPaths.Length => ImportPaths[-index - 1],
            _ => $"#{index}",
        };

        public bool InRange(int index) => index >= -ImportPaths.Length && index <= ExportPaths.Length;

        /// <summary>The actor an export belongs to (its ancestor directly under the level), or -1.</summary>
        public int ActorOf(int export)
        {
            for (int e = export, guard = 0; e >= 0 && guard < 256; guard++)
            {
                var outer = OuterOf(e);
                if (outer == Level)
                {
                    return e;
                }

                e = outer;
            }

            return -1;
        }
    }

    /// <summary>One object reference an export holds.</summary>
    private readonly record struct Reference(int Export, string Property, int Index);

    private static (int Exports, int Sized) CheckPackage(Context context, string path, string file, List<string> problems)
    {
        View mod;
        try
        {
            mod = Read(context, path, file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            problems.Add($"{path}: the package cannot be read back ({ex.GetType().Name}: {ex.Message}).");
            return (0, 0);
        }

        View? stock = null;
        var gameOwn = false;
        if (context.Game.TryGetPackageFile(path, out var gameFile))
        {
            gameOwn = IsGameOwn(context.Game, gameFile);
            try
            {
                var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(context.Game, gameFile);
                stock = Read(context, path, uasset, uexp, ubulk);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                stock = null; // the game's own package cannot be read: the mod's is checked on its own
            }
        }

        CheckTables(mod, problems);
        Deserialize(context, mod);
        if (stock is not null)
        {
            Deserialize(context, stock);
            for (var i = 0; i < stock.Count; i++)
            {
                if (!stock.IsInert(i))
                {
                    context.ClassReads.AddOrUpdate(stock.ClassPaths[i], _ => stock.Left[i] == 0 ? (1, 0) : (0, 1),
                        (_, v) => stock.Left[i] == 0 ? (v.Exact + 1, v.Inexact) : (v.Exact, v.Inexact + 1));
                }
            }
        }

        var stockIndex = stock is null ? null : Enumerable.Range(0, stock.Count).GroupBy(i => stock.ExportPaths[i], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sized = CheckSizes(context, mod, stock, stockIndex, problems);
        var modRefs = References(mod);
        var stockRefs = stock is null ? null : References(stock);
        CheckReferences(mod, modRefs, stock, stockRefs, stockIndex, problems);
        CheckLevel(mod, stock, stockIndex, problems);
        CheckAttachment(mod, modRefs, stock, stockRefs, stockIndex, problems);
        CheckImports(context, mod, stock, problems);
        CheckGuids(mod, stock, stockIndex, problems);

        // The cook check holds an event to the game's own copy only: an imported mod's file (a loose overlay or a mod pak) is
        // a client cook that the server export turns into the server's stubs on purpose (D12, ServerEventStubs).
        CheckSoundEvents(mod, gameOwn ? stock : null, stockIndex, problems);
        return (mod.Count, sized);
    }

    /// <summary>
    /// True when <paramref name="file"/> is the game's own: in a game pak (not a <c>_P</c> mod or patch pak), or any file of a
    /// catalog without paks (a loose extract). An imported mod's loose overlay in a pak catalog is not.
    /// </summary>
    private static bool IsGameOwn(AssetCatalog game, GameFile file) => file is VfsEntry entry
        ? !PackageOrigins.IsModContainer(entry.Vfs.Name)
        : game.Provider is not IVfsFileProvider { MountedVfs.Count: > 0 };

    private static View Read(Context context, string path, string file)
    {
        var stem = file[..^System.IO.Path.GetExtension(file).Length];
        var uasset = File.ReadAllBytes(file);
        var uexp = File.Exists(stem + ".uexp") ? File.ReadAllBytes(stem + ".uexp") : throw new FileNotFoundException("The .uexp next to the header is missing.", stem + ".uexp");
        var ubulk = File.Exists(stem + ".ubulk") ? File.ReadAllBytes(stem + ".ubulk") : null;
        return Read(context, path, uasset, uexp, ubulk);
    }

    private static View Read(Context context, string path, byte[] uasset, byte[] uexp, byte[]? ubulk)
    {
        var package = new Package(path, uasset, uexp, ubulk, null, context.Game.Provider, true);
        var imports = new string[package.ImportMap.Length];
        for (var i = 0; i < imports.Length; i++)
        {
            imports[i] = ImportPath(package, i);
        }

        var exports = new string[package.ExportMap.Length];
        var classes = new string[exports.Length];
        for (var i = 0; i < exports.Length; i++)
        {
            exports[i] = ExportPath(package, i, path[(path.LastIndexOf('/') + 1)..]);
            var cls = package.ExportMap[i].ClassIndex?.Index ?? 0;
            classes[i] = cls < 0 && -cls <= imports.Length ? imports[-cls - 1] : cls > 0 && cls <= exports.Length ? path + "." + package.ExportMap[cls - 1].ObjectName.Text : string.Empty;
        }

        return new View { Path = path, Package = package, UExp = uexp, UBulk = ubulk, ExportPaths = exports, ClassPaths = classes, ImportPaths = imports };
    }

    private static string ImportPath(Package package, int import)
    {
        var parts = new List<string>();
        for (int i = import, guard = 0; i >= 0 && i < package.ImportMap.Length && guard < 64; guard++)
        {
            var entry = package.ImportMap[i];
            parts.Add(entry.ObjectName.Text);
            var outer = entry.OuterIndex?.Index ?? 0;
            i = outer < 0 ? -outer - 1 : -1;
        }

        parts.Reverse();
        return parts.Count == 0 ? string.Empty : parts.Count == 1 ? parts[0] : parts[0] + "." + string.Join(':', parts.Skip(1));
    }

    /// <summary>The export's outer chain inside the package; with <paramref name="hideTop"/>, without the top object of that name (the level's world).</summary>
    private static string ExportPath(Package package, int export, string? hideTop = null)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        for (var i = export; i >= 0 && i < package.ExportMap.Length && seen.Add(i);)
        {
            var entry = package.ExportMap[i];
            parts.Add(entry.ObjectName.Text);
            i = entry.OuterIndex is { IsExport: true } o ? o.Index - 1 : -1;
        }

        parts.Reverse();
        if (hideTop is not null && parts.Count > 1 && string.Equals(parts[0], hideTop, StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveAt(0);
        }

        return string.Join('.', parts);
    }

    /// <summary>Tables: indices inside the tables, payloads inside the file and back to back, no duplicate or looping outer chain.</summary>
    private static void CheckTables(View view, List<string> problems)
    {
        var p = view.Package;
        var header = p.Summary.TotalHeaderSize;
        var end = header + view.UExp.Length - 4; // the .uexp ends with the package tag
        if (view.UExp.Length < 4 || BitConverter.ToUInt32(view.UExp, view.UExp.Length - 4) != 0x9E2A83C1)
        {
            problems.Add($"{view.Path}: the .uexp does not end with the package tag.");
        }

        var ranges = new List<(long Offset, long Size, int Export)>();
        for (var i = 0; i < view.Count; i++)
        {
            var e = p.ExportMap[i];
            foreach (var (label, index) in new[] { ("class", e.ClassIndex?.Index ?? 0), ("super", e.SuperIndex?.Index ?? 0), ("template", e.TemplateIndex?.Index ?? 0), ("outer", e.OuterIndex?.Index ?? 0) })
            {
                if (!view.InRange(index))
                {
                    problems.Add($"{view.Path}: {view.ExportPaths[i]}: its {label} index {index} is outside the import/export tables.");
                }
            }

            if (e.SerialOffset < header || e.SerialSize < 0 || e.SerialOffset + e.SerialSize > end)
            {
                problems.Add($"{view.Path}: {view.ExportPaths[i]}: its data ({e.SerialOffset}+{e.SerialSize}) is outside the file ({header}..{end}).");
            }

            ranges.Add((e.SerialOffset, e.SerialSize, i));
        }

        var expected = (long)header;
        foreach (var (offset, size, export) in ranges.OrderBy(r => r.Offset))
        {
            if (offset != expected)
            {
                problems.Add($"{view.Path}: {view.ExportPaths[export]}: its data starts at {offset}, the previous export's ends at {expected} (overlap or gap).");
            }

            expected = offset + size;
        }

        if (ranges.Count > 0 && expected != end)
        {
            problems.Add($"{view.Path}: the exports' data ends at {expected}, the file at {end}.");
        }

        for (var i = 0; i < p.ImportMap.Length; i++)
        {
            if (p.ImportMap[i].OuterIndex?.Index is { } outer && (outer > 0 || !view.InRange(outer) || outer == -(i + 1)))
            {
                problems.Add($"{view.Path}: import {view.ImportPaths[i]} has the outer index {outer} (an import's outer must be another import).");
            }
        }

        if (p.PreloadDependencies is { } preload)
        {
            foreach (var dependency in preload)
            {
                if (dependency is not null && !view.InRange(dependency.Index))
                {
                    problems.Add($"{view.Path}: a preload dependency ({dependency.Index}) is outside the import/export tables.");
                    break;
                }
            }
        }

        foreach (var group in Enumerable.Range(0, view.Count).GroupBy(i => (Outer: p.ExportMap[i].OuterIndex?.Index ?? 0, Name: p.ExportMap[i].ObjectName.Text.ToUpperInvariant())).Where(g => g.Count() > 1))
        {
            problems.Add($"{view.Path}: {group.Count()} objects named {view.ExportPaths[group.First()]} share one outer; the game creates one and the others overwrite it.");
        }

        for (var i = 0; i < view.Count; i++)
        {
            var seen = new HashSet<int> { i };
            for (var o = view.OuterOf(i); o >= 0; o = view.OuterOf(o))
            {
                if (!seen.Add(o))
                {
                    problems.Add($"{view.Path}: {view.ExportPaths[i]}: its outer chain loops.");
                    break;
                }
            }
        }
    }

    /// <summary>Builds and reads every export the way the game's loader does (class, flags, data at its offset), recording the bytes left.</summary>
    private static void Deserialize(Context context, View view)
    {
        var p = view.Package;
        view.Objects = new UObject?[view.Count];
        view.Left = new long?[view.Count];
        view.Errors = new string?[view.Count];
        var versions = context.Game.Provider.Versions;
        for (var i = 0; i < view.Count; i++)
        {
            var e = p.ExportMap[i];
            UObject? obj = null;
            try
            {
                var cls = ClassOf(context, view, e.ClassIndex);
                obj = p.ConstructObject(cls, p, (EObjectFlags)e.ObjectFlags);
                obj.Name = e.ObjectName.Text;
                var outer = view.OuterOf(i);
                obj.Outer = outer >= 0 && outer < i && view.Objects[outer] is { } o ? o : p;
                obj.Flags |= (EObjectFlags)e.ObjectFlags;
                var ar = new FAssetArchive(new FByteArchive(view.Path, view.UExp, versions), p, p.Summary.TotalHeaderSize, null);
                if (view.UBulk is { } bulk)
                {
                    ar.AddPayload(PayloadType.UBULK, p.Summary.BulkDataStartOffset, new Lazy<FArchive?>(() => new FByteArchive(view.Path + ".ubulk", bulk, versions)));
                }

                ar.SeekAbsolute(e.SerialOffset, SeekOrigin.Begin);
                var valid = ar.Position + e.SerialSize;
                obj.Deserialize(ar, valid);
                view.Left[i] = valid - ar.Position;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                view.Errors[i] = $"{ex.GetType().Name}: {ex.Message}";
            }

            view.Objects[i] = obj;
            if (obj is ULevel level && view.Level < 0)
            {
                view.Level = i;
                view.LevelObject = level;
            }
        }
    }

    private static UStruct? ClassOf(Context context, View view, FPackageIndex? index)
    {
        if (index is null || index.IsNull)
        {
            return null;
        }

        if (index.IsExport)
        {
            return view.Package.ResolvePackageIndex(index)?.Object?.Value as UStruct;
        }

        var key = view.ImportPaths[-index.Index - 1];
        return context.Classes.GetOrAdd(key, _ =>
        {
            try
            {
                return view.Package.ResolvePackageIndex(index)?.Object?.Value as UStruct;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                return null;
            }
        });
    }

    /// <summary>Every export must be read to its last byte, as the game's own data of that object or class is.</summary>
    private static int CheckSizes(Context context, View mod, View? stock, Dictionary<string, int>? stockIndex, List<string> problems)
    {
        var sized = 0;
        for (var i = 0; i < mod.Count; i++)
        {
            if (mod.IsInert(i))
            {
                if (mod.Left[i] != 0)
                {
                    problems.Add($"{mod.Path}: {mod.ExportPaths[i]} (a deleted object) is not read exactly ({Describe(mod, i)}).");
                }

                sized++;
                continue;
            }

            var same = stockIndex is not null && stockIndex.TryGetValue(mod.ExportPaths[i], out var s) && string.Equals(stock!.ClassPaths[s], mod.ClassPaths[i], StringComparison.OrdinalIgnoreCase) ? s : -1;
            bool exactInGame;
            if (same >= 0)
            {
                exactInGame = stock!.Left[same] == 0;
                if (!exactInGame && mod.Left[i] == stock.Left[same] && mod.Errors[i] == stock.Errors[same])
                {
                    continue; // read as far as the game's own copy: what CUE4Parse cannot follow is the same in both
                }
            }
            else
            {
                exactInGame = context.ClassReads.TryGetValue(mod.ClassPaths[i], out var reads) && reads.Exact >= 3 && reads.Inexact == 0;
            }

            if (!exactInGame)
            {
                continue;
            }

            sized++;
            if (mod.Left[i] != 0)
            {
                problems.Add($"{mod.Path}: {mod.ExportPaths[i]} ({ShortClass(mod.ClassPaths[i])}) does not take exactly its SerialSize ({Describe(mod, i)}); "
                             + (same >= 0 ? "the game's own copy of it does." : "the game's objects of that class do."));
            }
        }

        return sized;
    }

    private static string Describe(View view, int export) =>
        view.Errors[export] is { } error ? "reading it failed: " + error : $"{view.Left[export]} byte(s) left of {view.Package.ExportMap[export].SerialSize}";

    /// <summary>Every object reference of every export: tagged properties at any depth, plus the level's native ones.</summary>
    private static List<Reference> References(View view)
    {
        var list = new List<Reference>();
        for (var i = 0; i < view.Count; i++)
        {
            if (view.Objects[i] is not { } obj)
            {
                continue;
            }

            foreach (var tag in obj.Properties)
            {
                Walk(tag.Tag, Key(tag), i, list, 0);
            }
        }

        if (view.LevelObject is { } level)
        {
            var actors = level.Actors ?? [];
            for (var k = 0; k < actors.Length; k++)
            {
                list.Add(new Reference(view.Level, $"Actors[{k}]", actors[k]?.Index ?? 0));
            }

            list.Add(new Reference(view.Level, "Model", level.Model?.Index ?? 0));
            var models = level.ModelComponents ?? [];
            for (var k = 0; k < models.Length; k++)
            {
                list.Add(new Reference(view.Level, $"ModelComponents[{k}]", models[k]?.Index ?? 0));
            }

            list.Add(new Reference(view.Level, "LevelScriptActor", level.LevelScriptActor?.Index ?? 0));
            list.Add(new Reference(view.Level, "NavListStart", level.NavListStart?.Index ?? 0));
            list.Add(new Reference(view.Level, "NavListEnd", level.NavListEnd?.Index ?? 0));
        }

        return list;
    }

    private static string Key(FPropertyTag tag) => tag.ArrayIndex == 0 ? tag.Name.Text : $"{tag.Name.Text}({tag.ArrayIndex})";

    private static void Walk(FPropertyTagType? value, string path, int export, List<Reference> list, int depth)
    {
        if (value is null || depth > 32)
        {
            return;
        }

        switch (value.GenericValue)
        {
            case FPackageIndex index:
                list.Add(new Reference(export, path, index.Index));
                break;
            case FScriptInterface iface:
                list.Add(new Reference(export, path, iface.Object?.Index ?? 0));
                break;
            case FScriptDelegate del:
                list.Add(new Reference(export, path, del.Object?.Index ?? 0));
                break;
            case FMulticastScriptDelegate multi:
                for (var k = 0; k < (multi.InvocationList?.Length ?? 0); k++)
                {
                    list.Add(new Reference(export, $"{path}[{k}]", multi.InvocationList![k].Object?.Index ?? 0));
                }

                break;
            case UScriptArray array:
                for (var k = 0; k < array.Properties.Count; k++)
                {
                    Walk(array.Properties[k], $"{path}[{k}]", export, list, depth + 1);
                }

                break;
            case UScriptSet set:
                for (var k = 0; k < set.Properties.Count; k++)
                {
                    Walk(set.Properties[k], $"{path}{{{k}}}", export, list, depth + 1);
                }

                break;
            case UScriptMap map:
                var n = 0;
                foreach (var (key, item) in map.Properties)
                {
                    Walk(key, $"{path}{{{n}}}.Key", export, list, depth + 1);
                    Walk(item, $"{path}{{{n}}}.Value", export, list, depth + 1);
                    n++;
                }

                break;
            case FScriptStruct { StructType: FStructFallback fallback }:
                foreach (var tag in fallback.Properties)
                {
                    Walk(tag.Tag, path + "." + Key(tag), export, list, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// References inside the tables, never to a deleted (inert) object from a kept one, and never to another object than
    /// the game's own data points at while that one is still there.
    /// </summary>
    private static void CheckReferences(View mod, List<Reference> refs, View? stock, List<Reference>? stockRefs, Dictionary<string, int>? stockIndex, List<string> problems)
    {
        Dictionary<(string, string), int>? before = null;
        if (stock is not null && stockRefs is not null)
        {
            before = new Dictionary<(string, string), int>();
            foreach (var r in stockRefs)
            {
                before.TryAdd((stock.ExportPaths[r.Export], r.Property), r.Index);
            }
        }

        var arrays = before is null ? null : ArrayLengths(stock!, stockRefs!);
        var modArrays = before is null ? null : ArrayLengths(mod, refs);
        foreach (var r in refs)
        {
            if (r.Index == 0)
            {
                continue;
            }

            var from = mod.ExportPaths[r.Export];
            if (!mod.InRange(r.Index))
            {
                problems.Add($"{mod.Path}: {from}.{r.Property} points outside the import/export tables ({r.Index}).");
                continue;
            }

            if (mod.IsInert(r.Export))
            {
                continue;
            }

            if (r.Index > 0 && mod.IsInert(r.Index - 1))
            {
                problems.Add($"{mod.Path}: {from}.{r.Property} still points at {mod.ExportPaths[r.Index - 1]}, which the export deleted (the game reads it as null"
                             + (r.Property.Contains('[', StringComparison.Ordinal) ? ", a null entry in that list)." : ")."));
                continue;
            }

            if (before is null || !before.TryGetValue((from, r.Property), out var was) || was == 0 || !SameArrayShape(from, r.Property, arrays!, modArrays!))
            {
                continue;
            }

            var now = mod.PathOf(r.Index);
            var then = stock!.PathOf(was);
            if (string.Equals(now, then, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (r.Index > 0 && was > 0)
            {
                // The game's target still exists in the mod and is not this one: the reference moved to another object.
                if (stockIndex!.ContainsKey(now) && mod.ExportPaths.Contains(then, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{mod.Path}: {from}.{r.Property} points at {now}; the game's points at {then}, which is still there.");
                }
            }
            else if (r.Index < 0 && was < 0)
            {
                var a = mod.Package.ImportMap[-r.Index - 1].ClassName.Text;
                var b = stock.Package.ImportMap[-was - 1].ClassName.Text;
                if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{mod.Path}: {from}.{r.Property} points at a {a} ({now}); the game's points at a {b} ({then}).");
                }
            }
        }
    }

    private static Dictionary<(string, string), int> ArrayLengths(View view, List<Reference> refs)
    {
        var lengths = new Dictionary<(string, string), int>();
        foreach (var r in refs)
        {
            var bracket = r.Property.LastIndexOf('[');
            if (bracket > 0)
            {
                var key = (view.ExportPaths[r.Export], r.Property[..bracket]);
                lengths[key] = lengths.GetValueOrDefault(key) + 1;
            }
        }

        return lengths;
    }

    private static bool SameArrayShape(string export, string property, Dictionary<(string, string), int> stock, Dictionary<(string, string), int> mod)
    {
        var bracket = property.LastIndexOf('[');
        return bracket <= 0 || stock.GetValueOrDefault((export, property[..bracket])) == mod.GetValueOrDefault((export, property[..bracket]));
    }

    /// <summary>The level's actor list: actors of this level only (no component, no deleted or foreign object), each once.</summary>
    private static void CheckLevel(View mod, View? stock, Dictionary<string, int>? stockIndex, List<string> problems)
    {
        if (mod.LevelObject is not { } level)
        {
            if (mod.Package.ExportMap.Any(e => string.Equals(e.ClassName, "Level", StringComparison.Ordinal)))
            {
                problems.Add($"{mod.Path}: the level object cannot be read back ({(mod.Level >= 0 ? Describe(mod, mod.Level) : "no ULevel")}).");
            }

            return;
        }

        var listed = new HashSet<int>();
        foreach (var entry in level.Actors ?? [])
        {
            var index = entry?.Index ?? 0;
            if (index == 0)
            {
                continue;
            }

            if (index < 0 || index > mod.Count)
            {
                problems.Add($"{mod.Path}: the level's actor list holds {mod.PathOf(index)}, which is not an object of this level.");
                continue;
            }

            var actor = index - 1;
            if (!listed.Add(actor))
            {
                problems.Add($"{mod.Path}: the level's actor list holds {mod.ExportPaths[actor]} twice.");
            }
            else if (mod.IsInert(actor))
            {
                problems.Add($"{mod.Path}: the level's actor list still holds {mod.ExportPaths[actor]}, which the export deleted.");
            }
            else if (IsComponentClass(mod.ClassPaths[actor]) || mod.Objects[actor] is CUE4Parse.UE4.Assets.Exports.Component.UActorComponent)
            {
                problems.Add($"{mod.Path}: the level's actor list holds {mod.ExportPaths[actor]}, a {ShortClass(mod.ClassPaths[actor])} (a component, not an actor).");
            }
            else if (mod.OuterOf(actor) != mod.Level)
            {
                problems.Add($"{mod.Path}: the level's actor list holds {mod.ExportPaths[actor]}, which is not an actor of this level (its outer is {mod.PathOf(mod.Package.ExportMap[actor].OuterIndex?.Index ?? 0)}).");
            }
        }

        // An actor the game lists that the mod keeps (not deleted) but no longer lists loads without ever being registered.
        if (stock?.LevelObject is { } stockLevel && stockIndex is not null)
        {
            foreach (var entry in stockLevel.Actors ?? [])
            {
                if (entry is { Index: > 0 } e && e.Index <= stock.Count)
                {
                    var name = stock.ExportPaths[e.Index - 1];
                    var kept = Array.FindIndex(mod.ExportPaths, p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
                    if (kept >= 0 && !mod.IsInert(kept) && !listed.Contains(kept))
                    {
                        problems.Add($"{mod.Path}: {name} is kept but is no longer in the level's actor list (it would load without being part of the level).");
                    }
                }
            }
        }
    }

    /// <summary>AttachParent: no loop, and no component of one actor hung on another actor unless it is that actor's root (or the game does it too).</summary>
    private static void CheckAttachment(View mod, List<Reference> refs, View? stock, List<Reference>? stockRefs, Dictionary<string, int>? stockIndex, List<string> problems)
    {
        var parent = refs.Where(r => r.Property == "AttachParent" && r.Index > 0 && r.Index <= mod.Count && !mod.IsInert(r.Export))
            .GroupBy(r => r.Export).ToDictionary(g => g.Key, g => g.First().Index - 1);
        var roots = refs.Where(r => r.Property == "RootComponent" && r.Index > 0 && r.Index <= mod.Count).Select(r => r.Index - 1).ToHashSet();
        var stockCross = new HashSet<(string, string)>();
        if (stock is not null && stockRefs is not null)
        {
            foreach (var r in stockRefs.Where(r => r.Property == "AttachParent" && r.Index > 0 && r.Index <= stock.Count))
            {
                stockCross.Add((stock.ExportPaths[r.Export].ToUpperInvariant(), stock.ExportPaths[r.Index - 1].ToUpperInvariant()));
            }
        }

        foreach (var (child, attachedTo) in parent)
        {
            var seen = new HashSet<int> { child };
            for (var at = attachedTo; ; at = parent[at])
            {
                if (!seen.Add(at))
                {
                    problems.Add($"{mod.Path}: {mod.ExportPaths[child]}: its AttachParent chain loops.");
                    break;
                }

                if (!parent.ContainsKey(at))
                {
                    break;
                }
            }

            if (mod.Level < 0)
            {
                continue;
            }

            if (mod.OuterOf(attachedTo) == mod.Level)
            {
                problems.Add($"{mod.Path}: {mod.ExportPaths[child]} is attached to {mod.ExportPaths[attachedTo]}, an actor, not a component.");
                continue;
            }

            var owner = mod.ActorOf(child);
            var other = mod.ActorOf(attachedTo);
            if (owner >= 0 && other >= 0 && owner != other && !roots.Contains(child)
                && !stockCross.Contains((mod.ExportPaths[child].ToUpperInvariant(), mod.ExportPaths[attachedTo].ToUpperInvariant())))
            {
                problems.Add($"{mod.Path}: {mod.ExportPaths[child]} (not its actor's root) is attached into another actor, {mod.ExportPaths[attachedTo]}.");
            }
        }
    }

    /// <summary>Every import the game does not already make in its own copy of the package must exist in the game or the mod.</summary>
    private static void CheckImports(Context context, View mod, View? stock, List<string> problems)
    {
        var known = stock is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(stock.ImportPaths, StringComparer.OrdinalIgnoreCase);
        var p = mod.Package;
        for (var i = 0; i < p.ImportMap.Length; i++)
        {
            var path = mod.ImportPaths[i];
            if (known.Contains(path) || path.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dot = path.IndexOf('.');
            var package = dot < 0 ? path : path[..dot];
            if (!context.Staged.ContainsKey(package) && !context.Game.PackageExists(package))
            {
                problems.Add($"{mod.Path}: imports {path}, but neither the game nor the mod has the package {package} (the game would get null there).");
                continue;
            }

            if (dot < 0)
            {
                continue;
            }

            var objects = context.ExportPaths.GetOrAdd(package, key => ExportsOf(context, key));
            var inner = path[(dot + 1)..].Replace(':', '.');
            if (objects is not null && !objects.Contains(inner))
            {
                problems.Add($"{mod.Path}: imports {path}, but {package} has no object {inner} (the game would get null there).");
            }
        }
    }

    private static HashSet<string>? ExportsOf(Context context, string package)
    {
        try
        {
            Package header;
            if (context.Staged.TryGetValue(package, out var file))
            {
                var stem = file[..^Path.GetExtension(file).Length];
                header = new Package(package, File.ReadAllBytes(file), File.Exists(stem + ".uexp") ? File.ReadAllBytes(stem + ".uexp") : null, null, null, context.Game.Provider, true);
            }
            else if (context.Game.TryGetPackageFile(package, out var gameFile))
            {
                header = new Package(gameFile.CreateReader(), null, (Lazy<FArchive?>?)null, null, context.Game.Provider, true);
            }
            else
            {
                return null;
            }

            return Enumerable.Range(0, header.ExportMap.Length).Select(i => ExportPath(header, i)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>GUIDs a level keys its built data and lights by must stay unique in it (unless the game's own copy repeats them too).</summary>
    private static void CheckGuids(View mod, View? stock, Dictionary<string, int>? stockIndex, List<string> problems)
    {
        if (mod.Level < 0)
        {
            return;
        }

        var stockSeen = stock is null ? null : Guids(stock).GroupBy(g => (g.Property, g.Guid)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var group in Guids(mod).GroupBy(g => (g.Property, g.Guid)).Where(g => g.Count() > 1))
        {
            if (stockSeen?.Contains(group.Key) == true)
            {
                continue;
            }

            var holders = group.Select(g => mod.ExportPaths[g.Export]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (holders.Count > 1)
            {
                problems.Add($"{mod.Path}: {group.Key.Property} {group.Key.Guid} is held by {holders.Count} objects ({string.Join(", ", holders.Take(4))}); the game keys one object by it.");
            }
        }
    }

    /// <summary>
    /// A dedicated server's cook holds every Wwise event as a stub of a few bytes (no sound data), a game's cook the whole
    /// event: an event of the other kind than the game's own copy is the wrong cook's (a client event the server would try to
    /// load sound for, or a stub the client would play nothing from).
    /// </summary>
    private static void CheckSoundEvents(View mod, View? stock, Dictionary<string, int>? stockIndex, List<string> problems)
    {
        const long StubSize = 16;
        if (stock is null || stockIndex is null)
        {
            return;
        }

        for (var i = 0; i < mod.Count; i++)
        {
            if (!string.Equals(ShortClass(mod.ClassPaths[i]), "AkAudioEvent", StringComparison.Ordinal) || !stockIndex.TryGetValue(mod.ExportPaths[i], out var s))
            {
                continue;
            }

            var modStub = mod.Package.ExportMap[i].SerialSize <= StubSize;
            var gameStub = stock.Package.ExportMap[s].SerialSize <= StubSize;
            if (modStub != gameStub)
            {
                problems.Add(gameStub
                    ? $"{mod.Path}: {mod.ExportPaths[i]} is a game's sound event, but these game files (a dedicated server's) hold a stub there: the server would load sound data it has no banks for."
                    : $"{mod.Path}: {mod.ExportPaths[i]} is a dedicated server's sound stub, but these game files hold the whole event: the game would play nothing there.");
            }
        }
    }

    private static IEnumerable<(int Export, string Property, FGuid Guid)> Guids(View view)
    {
        for (var i = 0; i < view.Count; i++)
        {
            if (view.IsInert(i) || view.Objects[i] is not { } obj)
            {
                continue;
            }

            foreach (var tag in obj.Properties)
            {
                if (tag.Tag?.GenericValue is FScriptStruct { StructType: FGuid guid } && IsUniqueGuidName(tag.Name.Text) && !IsZero(guid))
                {
                    yield return (i, tag.Name.Text, guid);
                }
            }

            if (obj is UStaticMeshComponent { LODData: { } lods })
            {
                foreach (var lod in lods)
                {
                    if (lod is not null && !IsZero(lod.MapBuildDataId))
                    {
                        yield return (i, "MapBuildDataId", lod.MapBuildDataId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// GUIDs the game keys one object of a level by: a light's built shadow data, a component's built lighting. Not every
    /// <c>*Guid</c> is one: <c>CachedMeshBodySetupGuid</c> names the mesh's shared collision and repeats on purpose.
    /// </summary>
    private static bool IsUniqueGuidName(string name) =>
        string.Equals(name, "LightGuid", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "MapBuildDataId", StringComparison.OrdinalIgnoreCase);

    private static bool IsZero(FGuid guid) => guid.A == 0 && guid.B == 0 && guid.C == 0 && guid.D == 0;

    /// <summary>New registry records (the game's registry has every other) must name a package the game or the mod has.</summary>
    private static IEnumerable<string> CheckRegistry(string root, AssetCatalog game, IReadOnlyDictionary<string, string> staged)
    {
        var path = Path.Combine([root, .. AssetModBuilder.AssetRegistryPath.Split('/')]);
        if (!File.Exists(path))
        {
            yield break;
        }

        AssetRegistryFile registry;
        string? error = null;
        try
        {
            registry = AssetRegistryFile.Load(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            registry = null!;
            error = ex.Message;
        }

        if (error is not null)
        {
            yield return $"{AssetModBuilder.AssetRegistryPath}: cannot be read back ({error}).";
            yield break;
        }

        var stock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (game.TryGetPackageFile(AssetModBuilder.AssetRegistryPath, out _) || game.Provider.Files.ContainsKey(AssetModBuilder.AssetRegistryPath))
        {
            try
            {
                var bytes = game.Provider.Files[AssetModBuilder.AssetRegistryPath].Read();
                stock.UnionWith(AssetRegistryFile.Parse(bytes).Assets.Select(a => a.ObjectPath));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or KeyNotFoundException or ArgumentOutOfRangeException)
            {
                // Without the game's registry every record is checked.
            }
        }

        foreach (var asset in registry.Assets)
        {
            if (!stock.Contains(asset.ObjectPath) && !staged.ContainsKey(asset.PackageName) && !game.PackageExists(asset.PackageName))
            {
                yield return $"{AssetModBuilder.AssetRegistryPath}: the record {asset.ObjectPath} ({asset.AssetClass}) names {asset.PackageName}, which neither the game nor the mod has.";
            }
        }
    }

    private static bool IsComponentClass(string classPath) =>
        ShortClass(classPath).EndsWith("Component", StringComparison.Ordinal) || ShortClass(classPath).EndsWith("Component_C", StringComparison.Ordinal);

    private static string ShortClass(string classPath)
    {
        var cut = classPath.LastIndexOfAny(['.', ':']);
        return cut < 0 ? classPath : classPath[(cut + 1)..];
    }
}
