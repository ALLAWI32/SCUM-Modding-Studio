using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Level.Export;

/// <summary>Settings of one <see cref="ProjectExporter"/> run.</summary>
public sealed record ExportOptions
{
    /// <summary>Output folder; the exporter writes into <c>&lt;OutputDirectory&gt;/Client</c> or <c>/Server</c>.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Mod name used in the pak file name (default: the project name), sanitized by <see cref="ProjectExporter.SanitizeModName"/>.</summary>
    public string? ModName { get; init; }

    /// <summary>The <c>pakchunkNNN</c> number of the mod pak.</summary>
    public int PakChunkIndex { get; init; } = ProjectExporter.DefaultPakChunkIndex;

    /// <summary>A stock <c>.sig</c> to copy next to the pak; default: one found in the source's Paks folder.</summary>
    public string? StockSigPath { get; init; }

    /// <summary>False writes only the staged loose files.</summary>
    public bool WritePak { get; init; } = true;

    /// <summary>Pak entry compression (stock mod paks are stored uncompressed).</summary>
    public PakCompression Compression { get; init; } = PakCompression.None;

    /// <summary>
    /// Shaped actors to export straight: the game's log said they got no collision when bent (see
    /// <see cref="CollisionCheck"/>). Straight, a piece uses the collision the game ships for its mesh.
    /// </summary>
    public IReadOnlyCollection<ActorRef> StraightPieces { get; init; } = [];

    /// <summary>
    /// Where bent pieces' meshes are read (bounds, materials, collision); null = the export's own catalog. The app passes
    /// the client's for the server export: a dedicated server's cook has no render geometry to build collision from.
    /// </summary>
    public Func<string, BendMesh?>? BendMeshes { get; init; }
}

/// <summary>One rewritten level.</summary>
/// <param name="PackagePath">Level package path, e.g. <c>/Game/ConZ_Files/Maps/The_Island/A_0_Outpost</c>.</param>
/// <param name="VirtualPath">Its file inside the pak, e.g. <c>SCUM/Content/ConZ_Files/Maps/The_Island/A_0_Outpost.umap</c>.</param>
/// <param name="Report">What changed.</param>
public sealed record ExportedLevel(string PackagePath, string VirtualPath, LevelEditReport Report);

/// <summary>A vehicle/item package written by an export (a clone, or an overridden stock package).</summary>
/// <param name="PackagePath">Package path.</param>
/// <param name="VirtualPath">Pak entry path of the header.</param>
/// <param name="IsClone">True for a new package created by a clone.</param>
public sealed record ExportedAsset(string PackagePath, string VirtualPath, bool IsClone);

/// <summary>Result of one <see cref="ProjectExporter"/> run (one cook: client or server).</summary>
public sealed record ExportResult
{
    /// <summary>Client or server cook.</summary>
    public required ProjectSourceRole Role { get; init; }

    /// <summary>Sanitized mod name.</summary>
    public required string ModName { get; init; }

    /// <summary>Folder with the rewritten loose files (<c>SCUM/Content/...</c>), also usable as a loose overlay.</summary>
    public required string StagingDirectory { get; init; }

    /// <summary>The written pak, or null with <see cref="ExportOptions.WritePak"/> off.</summary>
    public string? PakPath { get; init; }

    /// <summary>The copied signature, or null when no stock <c>.sig</c> was available.</summary>
    public string? SigPath { get; init; }

    /// <summary>Markdown report written next to the pak.</summary>
    public string? ReportPath { get; init; }

    /// <summary>Levels rewritten, in export-set order.</summary>
    public required IReadOnlyList<ExportedLevel> Levels { get; init; }

    /// <summary>Vehicle/item packages written (clones and overridden stock packages).</summary>
    public IReadOnlyList<ExportedAsset> Assets { get; init; } = [];

    /// <summary>AssetRegistry.bin records added for clones (object path, primary asset type).</summary>
    public IReadOnlyList<RegisteredAsset> Registered { get; init; } = [];

    /// <summary>Vehicle/item values written, as (package, export|path, old, new).</summary>
    public IReadOnlyList<(string Package, string Key, string Old, string New)> AssetValues { get; init; } = [];

    /// <summary>Everything that was skipped or could not be applied.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Bent, longer or repeated pieces written with collision that passed the check.</summary>
    public int SolidPieces { get; init; }

    /// <summary>Total actors removed across levels.</summary>
    public int RemovedActorCount => Levels.Sum(l => l.Report.RemovedActors.Count);

    /// <summary>Total component transforms written across levels.</summary>
    public int PatchedTransformCount => Levels.Sum(l => l.Report.PatchedTransforms.Count);
}

/// <summary>
/// Turns a project's applied edits into a mod pak: every level in the pending export set is read pristine from the game
/// files, rewritten by <see cref="LevelPackageEditor"/>, staged as loose files under <c>SCUM/Content/...</c> and packed
/// into <c>pakchunkNNN-&lt;Name&gt;_P.pak</c> (V11, mount point <c>../../../</c>, uncompressed) with a copy of a stock
/// <c>.sig</c> next to it. Run once per cook (client paks, then the server's) — the edits are replayed by actor name, so
/// the same journal produces both variants.
/// </summary>
public sealed class ProjectExporter
{
    /// <summary>Default <c>pakchunk</c> number (well above the stock chunks, loads after them).</summary>
    public const int DefaultPakChunkIndex = 900;

    /// <summary>Sub-folder of the output for the client cook.</summary>
    public const string ClientFolderName = "Client";

    /// <summary>Sub-folder of the output for the server cook.</summary>
    public const string ServerFolderName = "Server";

    /// <summary>Sub-folder holding the staged loose files.</summary>
    public const string StagingFolderName = "staging";

    /// <summary>Name of the Markdown report.</summary>
    public const string ReportFileName = "export-report.md";

    private const string FallbackModName = "ScumStudioMod";

    private readonly ILogger _logger;

    /// <summary>Creates an exporter.</summary>
    public ProjectExporter(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Letters, digits, <c>_</c> and <c>-</c> only (spaces become <c>_</c>); never empty.</summary>
    public static string SanitizeModName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return FallbackModName;
        }

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' ? ch : '_');
        }

        var result = sb.ToString().Trim('_');
        return result.Length == 0 ? FallbackModName : result;
    }

    /// <summary>The mod pak file name, e.g. <c>pakchunk900-MyMod_P.pak</c>.</summary>
    public static string PakFileName(string modName, int chunkIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"pakchunk{chunkIndex}-{SanitizeModName(modName)}_P.pak");

    /// <summary>Output sub-folder of a cook.</summary>
    public static string RoleFolder(ProjectSourceRole role) => role == ProjectSourceRole.Server ? ServerFolderName : ClientFolderName;

    /// <summary>Exports the applied edits of <paramref name="project"/> against the game files in <paramref name="catalog"/>.</summary>
    /// <exception cref="InvalidOperationException">The project has no edits, or no level could be exported.</exception>
    public Task<ExportResult> ExportAsync(
        Project project, AssetCatalog catalog, ExportOptions options, ProjectSourceRole role = ProjectSourceRole.Client,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ExportAsync(project.State, project.Manifest.Name, catalog, options, role, progress, cancellationToken);
    }

    /// <summary>Exports <paramref name="state"/> (see <see cref="ExportAsync(Project, AssetCatalog, ExportOptions, ProjectSourceRole, IProgress{string}?, CancellationToken)"/>).</summary>
    /// <exception cref="InvalidOperationException">The state has no edits, or no level could be exported.</exception>
    public async Task<ExportResult> ExportAsync(
        EditState state, string projectName, AssetCatalog catalog, ExportOptions options, ProjectSourceRole role,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        if (state.IsEmpty)
        {
            throw new InvalidOperationException("Nothing to export: the project has no applied edits.");
        }

        var modName = SanitizeModName(options.ModName ?? projectName);
        var roleDirectory = Path.Combine(Path.GetFullPath(options.OutputDirectory), RoleFolder(role));
        var staging = Path.Combine(roleDirectory, StagingFolderName);
        ResetDirectory(staging);

        // Instances are read so instance edits can be located and verified in PerInstanceSMData.
        var reader = new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions
        {
            ExpandBlueprintComponents = false,
            ExpandChildActors = false,
            ReadInstances = true,
        }, _logger);

        var levels = new List<ExportedLevel>();
        var warnings = new List<string>();
        var solidPieces = 0;
        var bendMeshes = options.BendMeshes ?? new BendSupport(catalog).Describe;
        var packageCache = new Dictionary<string, CookedPackage?>(StringComparer.OrdinalIgnoreCase);
        var documentCache = new Dictionary<string, LevelDocument?>(StringComparer.OrdinalIgnoreCase);
        var placed = new List<(string Level, string File, HashSet<string> Actors)>();
        foreach (var level in state.ChangedLevels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Rewriting {level}");
            if (!catalog.TryGetPackageFile(level, out var file))
            {
                warnings.Add($"{level}: not found in {catalog.DisplayName}; skipped.");
                continue;
            }

            LevelDocument? document = null;
            try
            {
                document = LevelDocument.Load(reader, level, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
            {
                warnings.Add($"{level}: the actor graph could not be read ({ex.Message}); child actors and inferred root components are not resolved.");
            }

            var (uasset, uexp, ubulk) = ReadPackageFiles(catalog, file);
            var package = CookedPackage.Parse(uasset, uexp, ubulk, level);
            packageCache[level] = package;
            documentCache[level] = document;

            // Other levels are read on demand (Blueprint actors copied from elsewhere) and cached for this export.
            CookedPackage? SourcePackage(string path)
            {
                if (!packageCache.TryGetValue(path, out var cached))
                {
                    cached = TryReadPackage(catalog, path, warnings);
                    packageCache[path] = cached;
                }

                return cached;
            }

            LevelDocument? SourceDocument(string path)
            {
                if (!documentCache.TryGetValue(path, out var cached))
                {
                    try
                    {
                        cached = LevelDocument.Load(reader, path, cancellationToken);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
                    {
                        cached = null;
                    }

                    documentCache[path] = cached;
                }

                return cached;
            }

            var request = PlanLevel(state, level, document, warnings, SourcePackage, SourceDocument, bendMeshes, options.StraightPieces);
            if (request.IsEmpty)
            {
                warnings.Add($"{level}: no exportable change; skipped.");
                continue;
            }

            var (bytes, report) = LevelPackageEditor.Apply(package, request);

            // The collision check: every spline piece written carries its boxes and its mesh's body guid.
            var pieces = request.StaticMeshAdds.Where(a => a.Spline is not null).Select(a => a.NewName).ToList();
            if (pieces.Count > 0)
            {
                var problems = CollisionCheck.Verify(CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, level), pieces);
                warnings.AddRange(problems.Select(p => $"{level}: collision: {p}"));
                solidPieces += pieces.Count - problems.Count;
            }

            var virtualPath = file.Path.Replace('\\', '/');
            var dot = virtualPath.LastIndexOf('.');
            var stem = dot > virtualPath.LastIndexOf('/') ? virtualPath[..dot] : virtualPath;
            var extension = dot > virtualPath.LastIndexOf('/') ? virtualPath[dot..] : ".umap";
            var target = Path.Combine([staging, .. stem.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await bytes.WriteAsync(target, extension, cancellationToken).ConfigureAwait(false);
            if (ubulk is not null)
            {
                await File.WriteAllBytesAsync(target + ".ubulk", ubulk, cancellationToken).ConfigureAwait(false);
            }

            placed.Add((level, target + extension, new HashSet<string>(
                request.StaticMeshAdds.Select(a => a.NewName).Concat(request.Copies.Select(c => c.NewName)).Concat(request.ForeignCopies.Select(c => c.NewName))
                    .Concat(request.Transforms.Select(t => t.Actor)).Concat(request.Instances.Select(i => i.Actor)).Concat(request.SplinePatches.Select(s => s.Actor)),
                StringComparer.OrdinalIgnoreCase)));

            warnings.AddRange(report.Warnings.Select(w => $"{level}: {w}"));
            levels.Add(new ExportedLevel(level, virtualPath, report));
            _logger.LogInformation("{Level}: {Before} -> {After} actors, {Removed} removed, {Transforms} transform(s) written.",
                level, report.ActorsBefore, report.ActorsAfter, report.RemovedActors.Count, report.PatchedTransforms.Count);
        }

        GrowStreamingAreas(staging, placed, bendMeshes, warnings, cancellationToken);

        // Vehicles and items: clones (renamed families) and stored-value edits, plus the merged AssetRegistry.bin.
        var assets = new List<ExportedAsset>();
        var registered = new List<RegisteredAsset>();
        var assetValues = new List<(string, string, string, string)>();
        var writeRegistry = false;
        var assetRequest = BuildAssetRequest(state);
        if (!assetRequest.IsEmpty)
        {
            progress?.Report("Building vehicles and items");
            var built = AssetModBuilder.Build(catalog, assetRequest);
            warnings.AddRange(built.Warnings);
            foreach (var package in built.Packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var virtualPath = catalog.TryGetPackageFile(package.PackagePath, out var existing)
                    ? existing.Path.Replace('\\', '/')
                    : package.HeaderFilePath(catalog.ProjectName);
                var dot = virtualPath.LastIndexOf('.');
                var stem = virtualPath[..dot];
                var target = Path.Combine([staging, .. stem.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await package.Bytes.WriteAsync(target, virtualPath[dot..], cancellationToken).ConfigureAwait(false);
                if (package.UBulk is not null)
                {
                    await File.WriteAllBytesAsync(target + ".ubulk", package.UBulk, cancellationToken).ConfigureAwait(false);
                }

                assets.Add(new ExportedAsset(package.PackagePath, virtualPath, package.IsClone));
            }

            if (built.AssetRegistry is not null)
            {
                var registryTarget = Path.Combine([staging, .. AssetModBuilder.AssetRegistryPath.Split('/')]);
                Directory.CreateDirectory(Path.GetDirectoryName(registryTarget)!);
                await File.WriteAllBytesAsync(registryTarget, built.AssetRegistry, cancellationToken).ConfigureAwait(false);
                writeRegistry = true;
            }

            registered.AddRange(built.Registered);
            assetValues.AddRange(built.Applied);
            _logger.LogInformation("Vehicles/items: {Packages} package(s), {Values} value(s), {Registered} registry record(s).",
                built.Packages.Count, built.Applied.Count, built.Registered.Count);
        }

        if (levels.Count == 0 && assets.Count == 0)
        {
            throw new InvalidOperationException("Nothing was exported. " + string.Join(" ", warnings));
        }

        string? pakPath = null;
        string? sigPath = null;
        if (options.WritePak)
        {
            pakPath = Path.Combine(roleDirectory, PakFileName(modName, options.PakChunkIndex));
            progress?.Report($"Packing {Path.GetFileName(pakPath)}");
            var writer = new PakWriter(new PakWriterOptions { Compression = options.Compression, IncludeAssetRegistry = writeRegistry }, _logger);
            await writer.WriteFromDirectoryAsync(staging, pakPath, null, cancellationToken).ConfigureAwait(false);

            var sigSource = options.StockSigPath ?? FindStockSig(catalog);
            if (sigSource is null)
            {
                warnings.Add($"No stock .sig found: copy {SigCopier.DefaultStockSigName} from the game's Paks folder to {SigCopier.GetSigPath(pakPath)}.");
            }
            else
            {
                sigPath = SigCopier.Copy(sigSource, pakPath, overwrite: true, _logger);
            }
        }

        var result = new ExportResult
        {
            Role = role,
            ModName = modName,
            StagingDirectory = staging,
            PakPath = pakPath,
            SigPath = sigPath,
            Levels = levels,
            Assets = assets,
            Registered = registered,
            AssetValues = assetValues,
            Warnings = warnings,
            SolidPieces = solidPieces,
        };
        var reportPath = Path.Combine(roleDirectory, ReportFileName);
        await File.WriteAllTextAsync(reportPath, BuildReport(result, catalog), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return result with { ReportPath = reportPath };
    }

    /// <summary>
    /// Grows each written level's streaming area (<see cref="StreamingArea"/>) over the actors it added or moved, measured
    /// in the level as written (read back from <paramref name="staging"/>: bent pieces, copies, attached parts in place).
    /// </summary>
    private void GrowStreamingAreas(string staging, List<(string Level, string File, HashSet<string> Actors)> placed,
        Func<string, BendMesh?> meshes, List<string> warnings, CancellationToken cancellationToken)
    {
        if (placed.All(p => p.Actors.Count == 0))
        {
            return;
        }

        var boxes = new List<(string Level, string File, FVector Min, FVector Max)>();
        using (var written = AssetCatalog.OpenLoose(staging))
        {
            var reader = new Cue4ParseLevelReader(written, new Cue4ParseLevelReaderOptions
            {
                ExpandBlueprintComponents = false,
                ExpandChildActors = false,
                ReadInstances = true,
            }, _logger);
            foreach (var (level, file, actors) in placed.Where(p => p.Actors.Count > 0))
            {
                try
                {
                    var document = LevelDocument.Load(reader, level, cancellationToken);
                    if (StreamingArea.Of(document.Actors.Where(a => actors.Contains(a.Name)), meshes) is { } box)
                    {
                        boxes.Add((level, file, box.Min, box.Max));
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
                {
                    warnings.Add($"{level}: its streaming area could not be measured ({ex.Message}); what was placed far from the level's own area may disappear in the game when you get there.");
                }
            }
        }

        foreach (var (level, file, min, max) in boxes)
        {
            if (StreamingArea.Grow(file, min, max))
            {
                _logger.LogInformation("{Level}: streaming area grown to cover ({MinX:0}, {MinY:0})..({MaxX:0}, {MaxY:0}).", level, min.X, min.Y, max.X, max.Y);
            }
        }
    }

    /// <summary>
    /// Translates the edits of one level into a package edit request: deleted actors (plus the stored child actors they
    /// spawned), root/component transform overrides; unsupported edits (added actors, instance edits) become warnings.
    /// </summary>
    public static LevelEditRequest PlanLevel(EditState state, string level, LevelDocument? document, List<string> warnings) =>
        PlanLevel(state, level, document, warnings, null, null);

    /// <summary>
    /// <see cref="PlanLevel(EditState, string, LevelDocument?, List{string})"/> with access to other levels, so Blueprint
    /// actors added from another level (<see cref="AddBlueprintActorOp"/>) can be copied: <paramref name="sourcePackages"/>
    /// returns the pristine package of a level path (null when unavailable), <paramref name="sourceDocuments"/> its document.
    /// </summary>
    /// <param name="state">The edits.</param>
    /// <param name="level">Level package path.</param>
    /// <param name="document">The level as read (null when it could not be read).</param>
    /// <param name="warnings">Receives what could not be exported.</param>
    /// <param name="sourcePackages">Pristine package of another level path (null when unavailable).</param>
    /// <param name="sourceDocuments">Document of another level path.</param>
    /// <param name="bendMeshes">
    /// Bounds and spline-mesh support of a mesh object path, for bent actors (<see cref="BendActorOp"/>); without it bends
    /// are not exported.
    /// </param>
    /// <param name="straightPieces">Shaped actors to export straight (see <see cref="ExportOptions.StraightPieces"/>).</param>
    public static LevelEditRequest PlanLevel(
        EditState state, string level, LevelDocument? document, List<string> warnings,
        Func<string, CookedPackage?>? sourcePackages, Func<string, LevelDocument?>? sourceDocuments, Func<string, BendMesh?>? bendMeshes = null,
        IReadOnlyCollection<ActorRef>? straightPieces = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentNullException.ThrowIfNull(warnings);

        var deleted = new HashSet<string>(
            state.DeletedActors.Where(a => SameLevel(a.Level, level) && !state.IsAdded(a)).Select(a => a.Actor),
            StringComparer.OrdinalIgnoreCase);

        if (document is not null && deleted.Count > 0)
        {
            var owner = new Dictionary<int, ActorRecord>();
            foreach (var actor in document.Actors)
            {
                foreach (var component in actor.Components.Where(c => !c.IsSynthesized))
                {
                    owner.TryAdd(component.ExportIndex, actor);
                }
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var actor in document.Actors)
                {
                    if (!deleted.Contains(actor.Name) && actor.ParentComponent is { } parent
                        && owner.TryGetValue(parent, out var parentActor) && deleted.Contains(parentActor.Name))
                    {
                        deleted.Add(actor.Name);
                        changed = true;
                    }
                }
            }
        }

        var transforms = new List<TransformPatch>();
        foreach (var (actor, component, value) in state.TransformOverrides)
        {
            if (!SameLevel(actor.Level, level))
            {
                continue;
            }

            if (state.IsAdded(actor))
            {
                continue; // reported below with the added actor
            }

            if (deleted.Contains(actor.Actor))
            {
                continue;
            }

            var componentName = component.Length > 0 ? component : null;
            if (componentName is null && document?.FindActor(actor.Actor) is { Root: { } root })
            {
                if (root.IsSynthesized)
                {
                    warnings.Add($"{actor}: its root component '{root.Name}' is not stored in the level package (inherited from the Blueprint); moving it is not supported yet.");
                    continue;
                }

                componentName = root.Name;
            }

            transforms.Add(new TransformPatch(actor.Actor, componentName, value));
        }

        // Duplicates become copies of the pristine source actor (deleted copies are simply not created); other added
        // actors (new meshes, Blueprints from other levels) are not exported yet.
        var copies = new List<ActorCopy>();
        var meshAdds = new List<StaticMeshActorAdd>();
        var foreignCopies = new List<ForeignActorCopy>();
        foreach (var (added, op) in state.AddedActors.Where(a => SameLevel(a.Key.Level, level)))
        {
            if (state.IsDeleted(added))
            {
                continue; // added and deleted again: nothing to create
            }

            switch (op)
            {
                case DuplicateActorOp duplicate:
                    var sourceRoot = document?.FindActor(duplicate.Source.Actor)?.Root is { IsSynthesized: false } root ? root.Name : null;
                    copies.Add(new ActorCopy(duplicate.Source.Actor, duplicate.NewName, state.GetAddedTransform(added), sourceRoot));
                    break;
                case AddStaticMeshActorOp meshActor:
                    meshAdds.Add(new StaticMeshActorAdd(meshActor.NewName, meshActor.StaticMesh, state.GetAddedTransform(added) ?? meshActor.Transform));
                    break;
                case AddBlueprintActorOp blueprint when sourcePackages is not null:
                    var sourcePackage = sourcePackages(blueprint.Source.Level);
                    if (sourcePackage is null)
                    {
                        warnings.Add($"{added}: the source level {blueprint.Source.Level} could not be read; the Blueprint actor was not created.");
                        break;
                    }

                    var sourceRootName = sourceDocuments?.Invoke(blueprint.Source.Level)?.FindActor(blueprint.Source.Actor)?.Root is { IsSynthesized: false } sr ? sr.Name : null;
                    foreignCopies.Add(new ForeignActorCopy(sourcePackage, blueprint.Source.Actor, blueprint.NewName, state.GetAddedTransform(added) ?? blueprint.Transform, sourceRootName));
                    break;
                default:
                    warnings.Add($"{added}: this kind of added actor cannot be exported without the source level.");
                    break;
            }
        }

        PlanBends(state, level, document, warnings, bendMeshes, deleted, copies, meshAdds, straightPieces);

        // Road, rail and bridge pieces pushed sideways: their SplineParams are rewritten in place.
        var splinePatches = new List<SplinePatch>();
        foreach (var (actor, component, shape) in state.SegmentSways.Where(s => SameLevel(s.Actor.Level, level) && !state.IsDeleted(s.Actor)))
        {
            if (document?.FindActor(actor.Actor)?.FindComponent(component) is { SplineMesh: { } spline, IsSynthesized: false } piece)
            {
                var shaped = SplineEnds.Shape(spline, shape.Sway1, shape.Sway2, shape.Start, shape.End);
                var collision = piece.StaticMeshPath is { } meshPath && bendMeshes?.Invoke(meshPath) is { Boxes.Count: > 0 } info
                    ? PieceCollision.Bend(info.Boxes!, shaped, info.Bounds)
                    : null;
                splinePatches.Add(new SplinePatch(actor.Actor, component, shaped, collision));
            }
            else
            {
                warnings.Add($"{actor}.{component}: not a spline mesh piece stored in the level; its bend was not written.");
            }
        }

        // Instance edits: collapsed (deleted) and moved ISM/HISM instances of actors that are themselves kept. The pristine
        // instance lists come from the level document so the editor can locate and verify PerInstanceSMData.
        var instancePatches = new List<InstancePatch>();
        var hints = new Dictionary<(string Actor, string Component), InstanceArrayHint>();
        foreach (var instance in state.DeletedInstances.Where(i => SameLevel(i.Level, level) && !state.IsDeleted(i.ActorRef)))
        {
            AddInstancePatch(instance, null);
        }

        foreach (var (instance, value) in state.InstanceOverrides.Where(i => SameLevel(i.Instance.Level, level) && !state.IsDeleted(i.Instance)))
        {
            AddInstancePatch(instance, value.ToTransform());
        }

        return new LevelEditRequest
        {
            DeleteActors = deleted.ToList(),
            Transforms = transforms,
            Instances = instancePatches,
            InstanceHints = hints.Values.ToList(),
            Copies = copies,
            StaticMeshAdds = meshAdds,
            ForeignCopies = foreignCopies,
            SplinePatches = splinePatches,
        };

        void AddInstancePatch(InstanceRef instance, FTransform? local)
        {
            var key = (instance.Actor.ToLowerInvariant(), instance.Component.ToLowerInvariant());
            if (!hints.ContainsKey(key))
            {
                var actor = document?.FindActor(instance.Actor);
                if (actor is null)
                {
                    warnings.Add($"{instance}: the actor's instance list could not be read; instance edit skipped.");
                    return;
                }

                var known = actor.InstanceTransforms
                    .Where(t => string.Equals(t.ComponentName, instance.Component, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.InstanceIndex)
                    .Select(t => t.LocalTransform)
                    .ToList();
                if (known.Count == 0)
                {
                    warnings.Add($"{instance}: component '{instance.Component}' has no stored instances; instance edit skipped.");
                    return;
                }

                hints[key] = new InstanceArrayHint(instance.Actor, instance.Component, known);
            }

            instancePatches.Add(new InstancePatch(instance.Actor, instance.Component, instance.Index, local));
        }
    }

    /// <summary>
    /// Bent actors become <c>SplineMeshActor</c>s: a bent level actor is removed and re-created bent as
    /// <c>&lt;name&gt;_Bent</c>; a bent added mesh actor or copy is created bent instead of straight. The actor's scale goes
    /// into the curve (<see cref="BendShape"/>), so the new component has scale 1.
    /// </summary>
    private static void PlanBends(EditState state, string level, LevelDocument? document, List<string> warnings, Func<string, BendMesh?>? bendMeshes,
        HashSet<string> deleted, List<ActorCopy> copies, List<StaticMeshActorAdd> meshAdds, IReadOnlyCollection<ActorRef>? straightPieces)
    {
        foreach (var (actor, shape) in state.Bends.Where(b => SameLevel(b.Key.Level, level) && !state.IsDeleted(b.Key)))
        {
            if (straightPieces?.Contains(actor, ActorRef.Comparer) == true)
            {
                warnings.Add($"{actor}: exported straight - in the game it had no collision when bent (collision check).");
                continue;
            }

            string? mesh;
            TransformValue transform;
            var name = actor.Actor;
            var added = state.AddedActors.GetValueOrDefault(actor);
            switch (added)
            {
                case AddStaticMeshActorOp meshActor:
                    mesh = meshActor.StaticMesh;
                    transform = state.GetAddedTransform(actor) ?? meshActor.Transform;
                    break;
                case DuplicateActorOp duplicate when document?.FindActor(duplicate.Source.Actor) is { Kind: ActorKind.StaticMeshActor } source:
                    mesh = source.StaticMeshPath;
                    transform = InWorld(document, source, state.GetAddedTransform(actor) ?? duplicate.Transform);
                    break;
                case null when document?.FindActor(actor.Actor) is { Kind: ActorKind.StaticMeshActor, Root: { } root } pristine:
                    mesh = pristine.StaticMeshPath;
                    transform = InWorld(document, pristine, state.GetTransformOverride(actor) ?? root.Relative);
                    name = UniqueName(document, actor.Actor + "_Bent", meshAdds);
                    break;
                default:
                    warnings.Add($"{actor}: only a single-mesh actor (StaticMeshActor) can be bent; exported straight.");
                    continue;
            }

            if (mesh is null || bendMeshes?.Invoke(mesh) is not { } info)
            {
                warnings.Add($"{actor}: the mesh's bounds could not be read; exported straight.");
                continue;
            }

            if (info.Problem is { } problem)
            {
                warnings.Add($"{actor}: {problem}; exported straight.");
                continue;
            }

            // Collision check: a piece whose collision cannot be built bent (no boxes, or no mesh body guid to keep the game
            // from rebuilding and losing them) is exported straight, with the collision the game ships for its mesh.
            if (info.Boxes is not { Count: > 0 } meshBoxes || info.BodySetupGuid is null)
            {
                warnings.Add($"{actor}: exported straight - its collision could not be built bent ({(info.BodySetupGuid is null ? "its mesh has no collision body" : "no shapes to bend")}) (collision check).");
                continue;
            }

            // A piece made much longer repeats: one SplineMeshActor per piece, the first under the actor's own name.
            var pieces = BendShape.Pieces(info.Bounds, transform.Scale, shape);
            var bent = new List<StaticMeshActorAdd>();
            for (var i = 0; i < pieces.Count; i++)
            {
                var pieceName = i == 0 ? name : UniqueName(document, name + "_" + (i + 1).ToString(CultureInfo.InvariantCulture), meshAdds.Concat(bent));
                var collision = PieceCollision.Bend(meshBoxes, pieces[i], info.Bounds);
                bent.Add(new StaticMeshActorAdd(pieceName, mesh, transform with { Scale = FVector.One }, pieces[i], collision, info.BodySetupGuid));
            }

            switch (added)
            {
                case AddStaticMeshActorOp:
                    meshAdds.RemoveAll(a => string.Equals(a.NewName, actor.Actor, StringComparison.OrdinalIgnoreCase));
                    break;
                case DuplicateActorOp:
                    copies.RemoveAll(c => string.Equals(c.NewName, actor.Actor, StringComparison.OrdinalIgnoreCase));
                    break;
                default:
                    deleted.Add(actor.Actor); // the straight original leaves the level
                    break;
            }

            meshAdds.AddRange(bent);
        }

        static string UniqueName(LevelDocument? document, string wanted, IEnumerable<StaticMeshActorAdd> taken)
        {
            var used = taken.Select(a => a.NewName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = wanted;
            for (var n = 2; document?.FindActor(name) is not null || used.Contains(name); n++)
            {
                name = wanted + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            return name;
        }

        // A bent actor is written as a new actor of its own: one attached to another (a bridge's fence) needs its place in the world.
        static TransformValue InWorld(LevelDocument document, ActorRecord actor, TransformValue relative) =>
            actor.Root?.AttachParent is { } parentIndex
            && document.Actors.SelectMany(a => a.Components).FirstOrDefault(c => c.ExportIndex == parentIndex) is { } parent
                ? TransformValue.FromTransform(relative.ToTransform() * parent.WorldTransform)
                : relative;
    }

    /// <summary>Finds a stock signature next to the catalog's paks: <c>pakchunk44-WindowsNoEditor.sig</c>, else any stock <c>*.sig</c>.</summary>
    public static string? FindStockSig(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SourceKind != AssetSourceKind.Paks || catalog.SourcePath is not { } source)
        {
            return null;
        }

        var directory = Directory.Exists(source) ? source : Path.GetDirectoryName(source);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        var preferred = Path.Combine(directory, SigCopier.DefaultStockSigName);
        if (File.Exists(preferred))
        {
            return preferred;
        }

        return Directory.EnumerateFiles(directory, "*.sig", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_P", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool SameLevel(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static CookedPackage? TryReadPackage(AssetCatalog catalog, string level, List<string> warnings)
    {
        if (!catalog.TryGetPackageFile(level, out var file))
        {
            warnings.Add($"{level}: not found in {catalog.DisplayName}.");
            return null;
        }

        try
        {
            var (uasset, uexp, ubulk) = ReadPackageFiles(catalog, file);
            return CookedPackage.Parse(uasset, uexp, ubulk, level);
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException)
        {
            warnings.Add($"{level}: could not be read ({ex.Message}).");
            return null;
        }
    }

    private static (byte[] UAsset, byte[] UExp, byte[]? UBulk) ReadPackageFiles(AssetCatalog catalog, CUE4Parse.FileProvider.Objects.GameFile file)
    {
        var path = file.Path.Replace('\\', '/');
        var dot = path.LastIndexOf('.');
        var stem = dot > path.LastIndexOf('/') ? path[..dot] : path;
        var uasset = file.Read();
        if (!catalog.Provider.Files.TryGetValue(stem + ".uexp", out var uexpFile))
        {
            throw new FileNotFoundException($"{stem}.uexp is missing next to {path}.", stem + ".uexp");
        }

        var uexp = uexpFile.Read();
        var ubulk = catalog.Provider.Files.TryGetValue(stem + ".ubulk", out var ubulkFile) ? ubulkFile.Read() : null;
        return (uasset, uexp, ubulk);
    }

    /// <summary>The vehicle/item part of <paramref name="state"/>: clone plans and the current value of every override.</summary>
    public static AssetModRequest BuildAssetRequest(EditState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var clones = state.AssetClones
            .OrderBy(c => c.NewPrimary, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ClonePlan(c.Template, c.NewPrimary, c.Packages.Select(p => new KeyValuePair<string, string>(p.Old, p.New)).ToList()))
            .ToList();
        var edits = state.AssetValueOverrides
            .GroupBy(v => v.Package, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TunableEdit>)g.Select(v => new TunableEdit(v.Export, v.Path, v.Current)).ToList(), StringComparer.OrdinalIgnoreCase);
        return new AssetModRequest(clones, edits);
    }

    private static void ResetDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
    }

    private static string BuildReport(ExportResult result, AssetCatalog catalog)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(result.ModName).Append(" — ").Append(result.Role).AppendLine(" export");
        sb.AppendLine();
        sb.Append("- Source: ").AppendLine(catalog.DisplayName);
        sb.Append("- Levels rewritten: ").Append(result.Levels.Count.ToString(CultureInfo.InvariantCulture))
            .Append(", actors removed: ").Append(result.RemovedActorCount.ToString(CultureInfo.InvariantCulture))
            .Append(", transforms written: ").AppendLine(result.PatchedTransformCount.ToString(CultureInfo.InvariantCulture));
        sb.Append("- Vehicle/item packages: ").Append(result.Assets.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(result.Assets.Count(a => a.IsClone).ToString(CultureInfo.InvariantCulture)).Append(" new), values written: ")
            .Append(result.AssetValues.Count.ToString(CultureInfo.InvariantCulture)).Append(", registry records added: ")
            .AppendLine(result.Registered.Count.ToString(CultureInfo.InvariantCulture));
        sb.Append("- Pak: ").AppendLine(result.PakPath ?? "(not written)");
        sb.Append("- Signature: ").AppendLine(result.SigPath ?? "(none — copy a stock .sig next to the pak)");
        sb.Append("- Staging: ").AppendLine(result.StagingDirectory);
        sb.AppendLine();
        sb.AppendLine("## Levels");
        foreach (var level in result.Levels)
        {
            var r = level.Report;
            sb.AppendLine();
            sb.Append("### ").AppendLine(level.PackagePath);
            sb.Append("- File: ").AppendLine(level.VirtualPath);
            sb.Append("- Actor list: ").Append(r.ActorsBefore.ToString(CultureInfo.InvariantCulture)).Append(" -> ")
                .AppendLine(r.ActorsAfter.ToString(CultureInfo.InvariantCulture));
            foreach (var removed in r.RemovedActors)
            {
                sb.Append("- Removed: ").AppendLine(removed);
            }

            foreach (var patched in r.PatchedTransforms)
            {
                sb.Append("- Transform: ").AppendLine(patched);
            }

            foreach (var added in r.AddedNames)
            {
                sb.Append("- Name added: ").AppendLine(added);
            }
        }

        if (result.Assets.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Vehicles and items");
            sb.AppendLine();
            foreach (var asset in result.Assets)
            {
                sb.Append("- ").Append(asset.IsClone ? "New: " : "Overridden: ").AppendLine(asset.VirtualPath);
            }

            foreach (var (package, key, old, value) in result.AssetValues)
            {
                sb.Append("- Value: ").Append(package).Append(' ').Append(key).Append(": ").Append(old).Append(" -> ").AppendLine(value);
            }

            foreach (var record in result.Registered)
            {
                sb.Append("- Registered (").Append(record.PrimaryAssetType).Append("): ").AppendLine(record.ObjectPath);
            }
        }

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            foreach (var warning in result.Warnings)
            {
                sb.Append("- ").AppendLine(warning);
            }
        }

        return sb.ToString();
    }
}
