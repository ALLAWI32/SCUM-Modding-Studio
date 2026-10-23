using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Import;
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
    /// <summary>Output folder; the exporter writes into the mod's own folder there, <c>&lt;OutputDirectory&gt;/&lt;Mod&gt;/Client</c> or <c>/Server</c> (<see cref="ExportFolder"/>).</summary>
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

    /// <summary>Imported mods' folders (<see cref="ProjectMods"/>): their files go into the pak, under what the export rewrote.</summary>
    public IReadOnlyList<string> Mods { get; init; } = [];

    /// <summary>
    /// The project folder whose <c>imports</c> hold the 3D models placed on the map (<see cref="Import.ModelLibrary"/>): the
    /// pak carries every one a written level draws. It is also the folder new textures' pictures are read from
    /// (<see cref="ProjectTextures"/>). Null = the exported project's own folder (none for a bare state).
    /// </summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>
    /// The ground height at a world X/Y for the collision check of placed rocks (<see cref="CollisionCheck.Placed"/>);
    /// null = the export catalog's landscape (<see cref="GroundHeights"/>).
    /// </summary>
    public Func<float, float, float?>? Ground { get; init; }

    /// <summary>
    /// The economy to write next to the pak as <c>EconomyOverride.json</c> (placed traders get their sections); null = the
    /// project's own file when exporting a project (none: only when traders are placed).
    /// </summary>
    public EconomyOverride? Economy { get; init; }

    /// <summary>True: no <c>EconomyOverride.json</c> is written (the project's could not be read; see <see cref="Warnings"/>).</summary>
    public bool LeaveOutEconomy { get; init; }

    /// <summary>Warnings found before the export (the project's economy file could not be read); the result lists them first.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// For the server cook: reads the stock server's 12-byte sound-event stub (<c>ServerEventStubs.FromServerPaks</c>), or
    /// throws <see cref="ServerEventStubException"/>; called only when an event has to be replaced. Null = read it from the
    /// export's catalog (<c>ServerEventStubs.FromCatalog</c>).
    /// </summary>
    public Func<ScumStudio.Pak.Server.AkAudioEventStubBuilder>? ServerEventStubs { get; init; }

    /// <summary>
    /// Draws a craftable's 3D model as the picture its recipe shows in the crafting menu (Viewport <c>CraftIconRenderer</c>),
    /// or null when this export cannot draw: such a craftable keeps its template's picture, with a warning.
    /// </summary>
    public Func<Modding.Crafting.CraftPictureRequest, Assets.Textures.TextureImage?>? ModelPictures { get; init; }

    /// <summary>
    /// Read every staged package back before packing (<see cref="ModPackageValidator"/>) and refuse the pak when anything is
    /// wrong. Always on in the app and the command line; off only for tests that stage packages they know are partial.
    /// </summary>
    public bool Validate { get; init; } = true;

    /// <summary>
    /// One page's mod (owner 2026-10-17: each part as its own mod): the pak, its staging folder and its report are named after
    /// it (<see cref="ModuleExport"/>), and the state passed in is that page's part (<see cref="ModuleExport.StateOf"/>). Null:
    /// the whole project's pak.
    /// </summary>
    public ExportModule? Module { get; init; }

    /// <summary>
    /// The pak of the same project that ships a package this pak does not (package path → its file name), or null: the
    /// package check accepts references to it and the result names that pak (<see cref="ValidationResult.Needs"/>).
    /// </summary>
    public Func<string, string?>? Elsewhere { get; init; }

    /// <summary>
    /// Other paks of the same project this page's pak only works with although none of its packages names theirs (sentry
    /// robots replaced with NPCs: <see cref="ModuleExport.GoesWith"/>). Its install note names them as it names
    /// <see cref="ValidationResult.Needs"/>.
    /// </summary>
    public IReadOnlyList<string> GoesWith { get; init; } = [];

    /// <summary>
    /// The server cook: the client's game files, for the package check (<see cref="ValidationOptions.Client"/>: a dedicated
    /// server cook leaves out what only players' games use). Null for the client cook.
    /// </summary>
    public AssetCatalog? ClientCatalog { get; init; }
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

    /// <summary>Far models (seen from far away) that were cut or hidden because objects in them were removed or moved.</summary>
    public IReadOnlyList<string> FarModels { get; init; } = [];

    /// <summary>The placed traders, one line each: who, where, which outpost and how the outpost knows it.</summary>
    public IReadOnlyList<string> Traders { get; init; } = [];

    /// <summary>The <c>EconomyOverride.json</c> written next to the pak, or null.</summary>
    public string? EconomyPath { get; init; }

    /// <summary>The game's traders whose trade posts the project deleted: their sections are left out of <see cref="EconomyPath"/>.</summary>
    public IReadOnlyList<string> RemovedTraders { get; init; } = [];

    /// <summary>Server cook: the sound events (game paths) replaced by the server's 12-byte stubs.</summary>
    public IReadOnlyList<string> StubbedEvents { get; init; } = [];

    /// <summary>
    /// Item spawners this export no longer has, as <c>level: actor (item)</c>: deleted ones of the game and ones an earlier
    /// export into this folder placed. Items they already spawned stay in players' worlds (the server saves them).
    /// </summary>
    public IReadOnlyList<string> RemovedItemSpawners { get; init; } = [];

    /// <summary>What the package check found (null when <see cref="ExportOptions.Validate"/> was off).</summary>
    public ValidationResult? Validation { get; init; }

    /// <summary>
    /// The placed meshes this export wrote into the terrain tile under them instead of the level they were placed into (they
    /// stand out of that level's reach; see <see cref="FarPlacement"/>).
    /// </summary>
    public IReadOnlyList<FarPlacement.Move> FarPlaced { get; init; } = [];

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
public sealed partial class ProjectExporter
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

    /// <summary>The client cook's spawn place lists and edited places, read by its export and checked by the server's that follows (audit F281).</summary>
    internal Spawns.SpawnPlacesReference? ClientSpawnReference { get; set; }

    /// <summary>
    /// True to write every placed mesh into the level it was placed into, as 3.5.1 did (no <see cref="FarPlacement"/>): the
    /// before/after measurement of the real-file tests only.
    /// </summary>
    internal bool KeepPlacedInTheirLevel { get; init; }

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
        ArgumentNullException.ThrowIfNull(options);
        options = options.ProjectDirectory is null ? options with { ProjectDirectory = project.DirectoryPath } : options;
        if (options.Economy is null)
        {
            var (economy, warning) = LoadEconomy(project);
            options = warning is null ? options with { Economy = economy } : options with { LeaveOutEconomy = true, Warnings = [.. options.Warnings, warning] };
        }

        options = options.ProjectDirectory is null ? options with { ProjectDirectory = project.DirectoryPath } : options;
        return ExportAsync(project.State, project.Manifest.Name, catalog, options, role, progress, cancellationToken);
    }

    /// <summary>
    /// The project's <c>EconomyOverride.json</c> for an export, without the sections of placed traders that are off the map
    /// (<see cref="TraderPosts.ParkedTraders"/>); null when it has none. A file that cannot be read gives a warning instead
    /// (audit F287: it stopped the export and the command line), and the export writes no economy file.
    /// </summary>
    public static (EconomyOverride? Economy, string? Warning) LoadEconomy(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            if (EconomyOverride.LoadFrom(project.DirectoryPath) is not { } economy)
            {
                return (null, null);
            }

            var parked = TraderPosts.ParkedTraders(project.Journal, project.State);
            return (parked.Count > 0 ? economy.Without(parked) : economy, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, EconomyUnreadable(ex.Message));
        }
    }

    /// <summary>The export report's line for a project economy file that could not be read (<paramref name="reason"/>): none was written.</summary>
    public static string EconomyUnreadable(string reason) =>
        $"{EconomyOverride.FileName} of the project could not be read ({reason}): no economy file was written. Fix it on the Economy page (Code) or import the server's file, and export again.";

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
        var module = options.Module;
        if (role == ProjectSourceRole.Client)
        {
            ClientSpawnReference = null;
        }

        // The mod's own folder holds what is copied into the game; the staged files sit in its hidden work folder (Layout).
        var roleDirectory = ExportFolder(options.OutputDirectory, modName, module is not null, role);
        var work = WorkFolder(roleDirectory);
        var carriedNotes = CarryOldNotes(options.OutputDirectory, role, work);
        var staging = Path.Combine(work, module is { } part ? ModuleExport.StagingFolder(part) : StagingFolderName);
        ResetDirectory(staging);

        // Instances are read so instance edits can be located and verified in PerInstanceSMData.
        var reader = new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions
        {
            ExpandBlueprintComponents = false,
            ExpandChildActors = false,
            ReadInstances = true,
        }, _logger);

        var levels = new List<ExportedLevel>();
        var warnings = new List<string>(options.Warnings);
        var solidPieces = 0;

        // A placed mesh out of its level's reach goes into the terrain tile under it (FarPlacement); the levels of added
        // instances are read for that now and kept for the rewrite below.
        var farDocuments = new Dictionary<string, LevelDocument?>(StringComparer.OrdinalIgnoreCase);
        var (farState, farMoves) = KeepPlacedInTheirLevel ? (state, Array.Empty<FarPlacement.Move>()) : PlaceFar(state, catalog, options.ClientCatalog, reader, farDocuments, warnings, cancellationToken);
        state = farState;

        // Imported 3D models placed on the map are read from the project's imports folder, not the game's files.
        var models = new ModelCarry(options.ProjectDirectory);
        var modelMeshes = models.Describe(Import.ModelLibrary.UsedOnMap(state));
        var stockMeshes = options.BendMeshes ?? new BendSupport(catalog).Describe;
        Func<string, BendMesh?> gameMeshes = mesh => stockMeshes(MeshLoot.StockPath(mesh)); // a loot-free copy is its stock mesh's shape
        Func<string, BendMesh?> bendMeshes = modelMeshes.Count == 0 ? gameMeshes : mesh => modelMeshes.TryGetValue(mesh, out var model) ? model : gameMeshes(mesh);
        var undrawn = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Undrawn(string mesh) => undrawn.TryGetValue(mesh, out var known) ? known : undrawn[mesh] = IsWorldGridOnly(catalog, mesh);
        var packageCache = new Dictionary<string, CookedPackage?>(StringComparer.OrdinalIgnoreCase);
        var documentCache = new Dictionary<string, LevelDocument?>(StringComparer.OrdinalIgnoreCase);
        var placed = new List<(string Level, string File, HashSet<string> Actors)>();
        var cuts = new List<CutBox>();
        var proxies = new List<FarModels.Candidate>();
        var spawnPlaces = new List<ExportedAsset>();
        var traderLines = new List<string>();

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

        // A pak this project exported, imported back into it: its levels already hold the journal's edits, applied again on top.
        foreach (var mod in options.Mods.Where(m => string.Equals(SanitizeModName(Path.GetFileName(m)), modName, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"The imported mod '{Path.GetFileName(mod)}' is a pak of this project: its levels already hold this project's edits and the export applies them a second time "
                + "(copied rocks and trees twice, \"already exists\" and \"not in the level's actor list\" lines). Remove it from the project's imported mods and export again.");
        }

        // A placed mesh is created only when the package check will find it (ModPackageValidator.CheckImports): the game's files
        // with the imported mods read over them, the project's own models, another pak of the project, or for the server's cook
        // the client's files (a client-only asset). Only paks say what the game has: a loose folder may be a partial slice of
        // it (a test's synthetic level, a map slice), and is not judged.
        Func<string, bool>? meshExists = catalog.Provider is CUE4Parse.FileProvider.Vfs.IVfsFileProvider { MountedVfs.Count: > 0 }
            ? mesh => ModelLibrary.IsModelMesh(mesh) || catalog.PackageExists(mesh) || options.ClientCatalog?.PackageExists(mesh) == true
                      || options.Elsewhere?.Invoke(AssetPaths.SplitObjectPath(mesh).PackagePath) is not null
            : null;
        var traders = PlanTraders(state, catalog, SourcePackage, SourceDocument, warnings);
        var placedMeshes = new List<PlacedMesh>();

        // The loot the meshes of this cook carry themselves: a component whose loot is written draws a loot-free copy of a
        // mesh with loot of its own (built after the levels from the same cook's mesh).
        var meshLoot = MeshLoot.FromCatalog(catalog);

        // The changed levels are read side by side first; the loop takes each one (or the error reading it threw) in order.
        var ahead = reader.ReadAhead(state.ChangedLevels.Where(l => !Spawns.SpawnPlaces.IsStaticData(l) && !farDocuments.ContainsKey(l) && catalog.TryGetPackageFile(l, out _)).ToList(), cancellationToken);
        foreach (var level in state.ChangedLevels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Rewriting {level}");
            if (!catalog.TryGetPackageFile(level, out var file))
            {
                warnings.Add($"{level}: not found in {catalog.DisplayName}; skipped.");
                continue;
            }

            // The island's spawn places are not a level: their edits rewrite the static data asset that holds them, after
            // the levels (the spawn points inside placed objects go too).
            if (Spawns.SpawnPlaces.IsStaticData(level))
            {
                continue;
            }

            LevelDocument? document = null;
            try
            {
                document = farDocuments.TryGetValue(level, out var known) && known is not null ? known
                    : ahead.TryGetValue(level, out var read) ? read.Value : LevelDocument.Load(reader, level, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
            {
                warnings.Add($"{level}: the actor graph could not be read ({ex.Message}); child actors and inferred root components are not resolved.");
            }

            var (uasset, uexp, ubulk) = ReadPackageFiles(catalog, file);
            var package = CookedPackage.Parse(uasset, uexp, ubulk, level);
            packageCache[level] = package;
            documentCache[level] = document;

            var request = models.Keep(level, PlanLevel(state, level, document, warnings, SourcePackage, SourceDocument, bendMeshes, options.StraightPieces, traders, Undrawn, meshExists, meshLoot), warnings);
            if (request.IsEmpty)
            {
                warnings.Add($"{level}: no exportable change; skipped.");
                continue;
            }

            var (bytes, report) = LevelPackageEditor.Apply(package, request with { Packages = SourcePackage, AssetClass = catalog.GetMainClassName });
            if (role == ProjectSourceRole.Client && document is not null)
            {
                CollectFarCuts(document, request, bendMeshes, cuts, proxies);
                placedMeshes.AddRange(PlacedMeshes(level, request, document));
            }

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
                    .Concat(request.Transforms.Select(t => t.Actor)).Concat(request.Instances.Select(i => i.Actor)).Concat(request.InstanceAdds.Select(a => a.Actor))
                    .Concat(request.SplinePatches.Select(s => s.Actor)).Concat(request.SpawnPoints.Select(s => s.Actor)).Concat(request.Meshes.Where(m => !m.IsLootFreeCopy).Select(m => m.Actor)),
                StringComparer.OrdinalIgnoreCase)));

            warnings.AddRange(report.Warnings.Select(w => $"{level}: {w}"));
            traderLines.AddRange(DescribeTraders(state, level, traders, report));
            levels.Add(new ExportedLevel(level, virtualPath, report));
            _logger.LogInformation("{Level}: {Before} -> {After} actors, {Removed} removed, {Transforms} transform(s) written.",
                level, report.ActorsBefore, report.ActorsAfter, report.RemovedActors.Count, report.PatchedTransforms.Count);
        }

        // The loot-free copies of the meshes the written levels now draw instead (loot replaced or taken out).
        var lootFreeMeshes = await WriteLootFreeMeshesAsync(catalog, meshLoot, staging, warnings, cancellationToken).ConfigureAwait(false);
        if (lootFreeMeshes.Count > 0)
        {
            _logger.LogInformation("Loot-free mesh copies: {Count}.", lootFreeMeshes.Count);
        }

        var volumes = new List<GrassClearing.Volume>();
        var footprints = GrassFootprints(state, catalog, bendMeshes);
        GrowStreamingAreas(staging, placed, bendMeshes, warnings, cancellationToken, (level, actor) => footprints(level, actor).Select(v => v with { Solid = GrassClearing.IsSolid(actor) }), volumes);
        if (volumes.Count > 0)
        {
            progress?.Report("Clearing the grass under placed objects");
            ClearGrassUnder(catalog, reader, volumes.Select(v => v.Outline).ToList(), staging, levels, warnings, cancellationToken);
        }

        if (state.TerrainHoles.Count > 0)
        {
            progress?.Report("Cutting the terrain holes");
            CutTerrainHoles(catalog, state.TerrainHoles, staging, levels, warnings, cancellationToken);
        }

        // The Map page's mod writes the island's spawn places only when its placed objects clear some (with the Spawns page's
        // edits too: one file holds both, and the Map pak loads above the Spawns pak); the Spawns page's mod writes its edits.
        if ((volumes.Count > 0 || (module != ExportModule.Map && state.ChangedLevels.Any(Spawns.SpawnPlaces.IsStaticData)))
            && catalog.TryGetPackageFile(Spawns.SpawnPlaces.StaticDataPath, out var placesFile)
            && await WriteSpawnPlacesAsync(state, catalog, placesFile, staging, warnings, volumes, role, cancellationToken).ConfigureAwait(false) is { } placesAsset)
        {
            spawnPlaces.Add(placesAsset);
        }
        if (placedMeshes.Count > 0)
        {
            warnings.AddRange(CollisionCheck.Placed(placedMeshes, bendMeshes, options.Ground ?? new GroundHeights(catalog).At));
        }

        IReadOnlyList<string> farModels = [];
        if (cuts.Count > 0)
        {
            progress?.Report("Cutting removed objects out of the far models");
            farModels = FarModels.Apply(catalog, cuts, proxies, staging, warnings);
            _logger.LogInformation("Far models: {Count} changed.", farModels.Count);
        }

        // Vehicles and items: clones (renamed families) and stored-value edits, plus the merged AssetRegistry.bin.
        var assets = new List<ExportedAsset>(spawnPlaces.Concat(lootFreeMeshes));
        var registered = new List<RegisteredAsset>();
        var assetValues = new List<(string, string, string, string)>();
        var writeRegistry = false;
        var assetRequest = BuildAssetRequest(state) with { DataAssets = traders.DataAssets, FileRoot = options.ProjectDirectory };
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

        // The imported 3D models the written levels draw: their cooked mesh, materials and textures, as the import made them.
        var modelAssets = models.CopyInto(staging, catalog.ProjectName);
        if (modelAssets.Count > 0)
        {
            assets.AddRange(modelAssets);
            _logger.LogInformation("Imported 3D models: {Packages} package(s) carried into the pak.", modelAssets.Count);
        }

        // Imported mods (someone's pak unpacked into the project) go in too, under what this export rewrote from them. Their
        // registries are never shipped as they are: the staged one is stock plus their records of what ships plus ours.
        var carriedPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var carried = ProjectMods.CarryInto(options.Mods, staging, catalog.ProjectName, carriedPackages);
        if (carried > 0)
        {
            _logger.LogInformation("Imported mods: {Files} file(s) carried into the pak.", carried);
            writeRegistry |= ProjectMods.MergeRegistries(options.Mods, staging, catalog, warnings);
        }

        if (levels.Count == 0 && assets.Count == 0 && carried == 0)
        {
            throw new InvalidOperationException("Nothing was exported. " + string.Join(" ", warnings));
        }

        // Every staged package is in place (levels, vehicles, imported mods): the server's sound events become its stubs.
        var stubbed = await StubEventsAsync(staging, catalog, options, role, warnings, cancellationToken).ConfigureAwait(false);

        // The list of placed item spawners (in the hidden work folder) is written once the pak is: a refused export leaves
        // the last good export's list.
        var removedSpawners = ItemSpawnersGone(state, work, documentCache, warnings, out var placedSpawners);
        var pakTarget = Path.Combine(roleDirectory, module is { } own ? ModuleExport.PakFileName(modName, own, options.PakChunkIndex) : PakFileName(modName, options.PakChunkIndex));
        var reportPath = Path.Combine(roleDirectory, module is { } reported ? ModuleExport.ReportFileName(reported) : ReportFileName);

        // Every staged package is read back against the game's own before anything is packed: a pak the game could trip
        // over (loading, playing, leaving a server, closing the game) is never written (owner 2026-10-16). It runs after
        // the server's stubs are written, so it checks the packages that go into the pak. Refused, the report says NOT
        // EXPORTED and names the earlier pak, which stays as it is (Discord, O Piqueno: the old pak and report looked like this export's).
        ValidationResult? validation;
        try
        {
            validation = Validate(staging, catalog, options, progress, cancellationToken, carriedPackages, roleDirectory);
        }
        catch (ModValidationException refused)
        {
            // The earlier pak and .sig are never renamed, moved or deleted (an export into a server's mods folder keeps the mod that works).
            throw WriteRefusal(refused, module is null ? modName : ModuleExport.ModName(modName, module.Value), role, reportPath, options.WritePak ? pakTarget : null, warnings);
        }

        warnings.AddRange(validation?.Notes ?? []);

        string? pakPath = null;
        string? sigPath = null;
        if (options.WritePak)
        {
            pakPath = pakTarget;
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

        WriteItemSpawners(work, placedSpawners, warnings);

        // The economy goes next to the pak: the server reads it from its config folder (see the report). The game's traders
        // whose trade posts are deleted lose their sections (the outpost levels with deletions were read above).
        string? economyPath = null;
        var removedTraders = TraderPosts.RemovedStockTraders(state,
            documentCache.Where(d => d.Value is not null && TraderPosts.IsOutpostLevel(d.Key)).Select(d => d.Value!)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!options.LeaveOutEconomy && module is null && EconomyFor(state, options.Economy, removedTraders) is { } economy) // a page's mod: the Economy page exports it
        {
            economyPath = economy.SaveTo(roleDirectory, withNote: true);
        }

        var result = new ExportResult
        {
            Role = role,
            ModName = module is { } named ? ModuleExport.ModName(modName, named) : modName,
            StagingDirectory = staging,
            PakPath = pakPath,
            SigPath = sigPath,
            Levels = levels,
            Assets = assets,
            Registered = registered,
            AssetValues = assetValues,
            Warnings = warnings,
            SolidPieces = solidPieces,
            FarModels = farModels,
            Traders = traderLines,
            EconomyPath = economyPath,
            RemovedTraders = economyPath is null ? [] : removedTraders,
            StubbedEvents = stubbed,
            RemovedItemSpawners = removedSpawners,
            Validation = validation,
            FarPlaced = farMoves,
        };
        await File.WriteAllTextAsync(reportPath, BuildReport(result, catalog, module, module is null ? null : ModuleExport.RegistryPakFileName(modName, options.PakChunkIndex), goesWith: options.GoesWith), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        // What an export before 3.5.1 left loose beside the mod's folder goes now that this one's pak is written (a staging-only
        // run, --no-pak, keeps the only pak the user has). The old Craftables pak is the Craftables pak's of this export to replace.
        foreach (var old in options.WritePak ? RemoveOldLayout(options.OutputDirectory, modName, options.PakChunkIndex, role, keepCraftables: module is null) : [])
        {
            _logger.LogInformation("Removed {Path} (an export before 3.5.1 left it loose in the output folder).", old);
        }

        if (options.WritePak && carriedNotes)
        {
            DropOldNotes(options.OutputDirectory, role); // this mod's work folder has them now
        }

        return result with { ReportPath = reportPath };
    }

    /// <summary>
    /// Runs <see cref="ModPackageValidator"/> over <paramref name="staging"/> (unless <see cref="ExportOptions.Validate"/> is
    /// off) and throws <see cref="ModValidationException"/> when it found anything; the pak is then not written.
    /// <paramref name="carried"/> names the packages carried from imported mods as their authors made them. The list of problems
    /// is written to <paramref name="listDirectory"/> (the mod's folder, where the report is; default: beside the staging folder).
    /// </summary>
    internal static ValidationResult? Validate(string staging, AssetCatalog catalog, ExportOptions options, IProgress<string>? progress, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? carried = null, string? listDirectory = null)
    {
        if (!options.Validate)
        {
            return null;
        }

        progress?.Report("Checking every package");
        var result = ModPackageValidator.Validate(staging, catalog, new ValidationOptions
        {
            Client = options.ClientCatalog,
            Carried = carried ?? new Dictionary<string, string>(),
        }, cancellationToken, options.Elsewhere);
        var list = Path.Combine(listDirectory ?? Path.GetDirectoryName(Path.GetFullPath(staging))!, ModPackageValidator.ListFileName);
        if (result.Passed)
        {
            File.Delete(list); // an earlier export's list
            return result;
        }

        File.WriteAllLines(list, [result.Summary, .. result.Problems], new UTF8Encoding(false));
        throw new ModValidationException(result, list);
    }

    /// <summary>
    /// The places of what <paramref name="request"/> removes or moves away (deleted and moved actors as they stood, deleted and
    /// moved instances) and the level's HLOD proxies, for <see cref="FarModels"/>.
    /// </summary>
    private static void CollectFarCuts(LevelDocument document, LevelEditRequest request, Func<string, BendMesh?> meshes, List<CutBox> cuts, List<FarModels.Candidate> proxies)
    {
        var gone = new HashSet<string>(request.DeleteActors.Concat(request.Transforms.Select(t => t.Actor)).Concat(request.Meshes.Where(m => !m.IsLootFreeCopy).Select(m => m.Actor)), StringComparer.OrdinalIgnoreCase);
        foreach (var actor in document.Actors)
        {
            if (gone.Contains(actor.Name))
            {
                cuts.AddRange(FarModels.BoxesOf(actor, meshes));
            }
            else if (actor.ClassName == "LODActor")
            {
                foreach (var c in actor.Components.Where(c => c.StaticMeshPath is not null))
                {
                    proxies.Add(new FarModels.Candidate($"{document.Name} {actor.Name} (HLOD)", c.WorldTransform, [c.StaticMeshPath!], null));
                }
            }
        }

        foreach (var patch in request.Instances)
        {
            if (document.Actors.FirstOrDefault(a => a.Name.Equals(patch.Actor, StringComparison.OrdinalIgnoreCase))?.FindComponent(patch.Component) is { } c
                && patch.Index >= 0 && patch.Index < c.Instances.Count
                && FarModels.BoxOf(c, c.Instances[patch.Index], meshes) is { } box)
            {
                cuts.Add(box);
            }
        }
    }

    /// <summary>
    /// Where the request puts static meshes, for the collision check: added mesh actors (not bent pieces, which carry their
    /// own boxes), copies and moved mesh actors of the level, and added or moved instances.
    /// </summary>
    internal static IEnumerable<PlacedMesh> PlacedMeshes(string level, LevelEditRequest request, LevelDocument document)
    {
        foreach (var add in request.StaticMeshAdds.Where(a => a.Spline is null))
        {
            yield return new PlacedMesh($"{level}: {add.NewName}", add.StaticMesh, add.Transform.ToTransform(), add.CollisionProfile is not null);
        }

        foreach (var copy in request.Copies)
        {
            if (document.FindActor(copy.SourceActor) is { Kind: ActorKind.StaticMeshActor, StaticMeshPath: { } mesh, Root.AttachParent: null } source)
            {
                yield return new PlacedMesh($"{level}: {copy.NewName}", mesh, copy.RootTransform?.ToTransform() ?? source.WorldTransform, true);
            }
        }

        foreach (var move in request.Transforms)
        {
            if (document.FindActor(move.Actor) is { Kind: ActorKind.StaticMeshActor, StaticMeshPath: { } mesh, Root: { AttachParent: null } root }
                && string.Equals(move.Component ?? root.Name, root.Name, StringComparison.OrdinalIgnoreCase))
            {
                yield return new PlacedMesh($"{level}: {move.Actor}", mesh, move.Value.ToTransform(), true);
            }
        }

        var instances = request.Instances.Where(i => i.Local is not null).Select(i => (i.Actor, i.Component, Local: i.Local!.Value, Name: $"[{i.Index}]"))
            .Concat(request.InstanceAdds.Select(a => (a.Actor, a.Component, a.Local, Name: "(added)")));
        foreach (var (actor, component, local, name) in instances)
        {
            if (document.FindActor(actor)?.FindComponent(component) is { StaticMeshPath: { } mesh } c)
            {
                yield return new PlacedMesh($"{level}: {actor}.{component}{name} at {(local * c.WorldTransform).Translation}", mesh, local * c.WorldTransform, true);
            }
        }
    }

    /// <summary>
    /// Grows each written level's streaming area (<see cref="StreamingArea"/>) over the actors it added or moved, measured
    /// in the level as written (read back from <paramref name="staging"/>: bent pieces, copies, attached parts in place).
    /// </summary>
    private void GrowStreamingAreas(string staging, List<(string Level, string File, HashSet<string> Actors)> placed,
        Func<string, BendMesh?> meshes, List<string> warnings, CancellationToken cancellationToken,
        Func<string, ActorRecord, IEnumerable<GrassClearing.Volume>>? footprintsOf = null, List<GrassClearing.Volume>? footprints = null)
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
                    if (footprintsOf is not null && footprints is not null)
                    {
                        // "Clear grass and spawns under it": the footprints as written (bent pieces bent, copies in place).
                        footprints.AddRange(document.Actors.Where(a => actors.Contains(a.Name)).SelectMany(a => footprintsOf(level, a)));
                    }

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
    /// spawned and what hangs on them, see <see cref="PlanAttached"/>), deleted parts of kept actors, root/component transform
    /// overrides; unsupported edits (added actors, instance edits) become warnings.
    /// </summary>
    public static LevelEditRequest PlanLevel(EditState state, string level, LevelDocument? document, List<string> warnings) =>
        PlanLevel(state, level, document, warnings, null, null);

    /// <summary>True when every instance the actor draws is deleted in <paramref name="state"/> (and none was added back).</summary>
    private static bool AllInstancesDeleted(EditState state, ActorRecord actor, string level) =>
        actor.InstanceTransforms.All(i => state.IsDeleted(new InstanceRef(level, actor.Name, i.ComponentName, i.InstanceIndex)))
        && !state.AddedInstances.Keys.Any(k => SameLevel(k.Level, level) && string.Equals(k.Actor, actor.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A scale the brush and the part delete write for "nothing left of this part".</summary>
    private static bool IsNothing(FVector scale) => MathF.Abs(scale.X) < 1e-4f && MathF.Abs(scale.Y) < 1e-4f && MathF.Abs(scale.Z) < 1e-4f;

    /// <summary>The profile SCUM's standing trees collide as (its tree foliage uses it; players and cars stop at the trunk).</summary>
    public const string StandingTree = "SCUM_TreeStump";

    /// <summary>
    /// For a mesh copy that does not know what it was copied from (made before copies kept it): a tree whose mesh's own
    /// default collision lets players and vehicles through (<c>SCUM_Foliage</c>) collides as the game's tree foliage does
    /// (<see cref="StandingTree"/>); null otherwise. The owner's copied cypresses, pines and acacias had no collision.
    /// </summary>
    // ponytail: by folder (Foliage/.../Trees/) and default profile; a copy made now carries its source's own profile.
    public static string? StandingTreeProfile(string mesh, Func<string, BendMesh?>? meshes) =>
        mesh.Contains("/Foliage/", StringComparison.OrdinalIgnoreCase) && mesh.Contains("/Trees/", StringComparison.OrdinalIgnoreCase)
        && meshes?.Invoke(mesh)?.DefaultProfile is "SCUM_Foliage"
            ? StandingTree
            : null;

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
    /// <param name="traders">The placed traders' plan (<see cref="PlanTraders"/>): new outposts' managers go into their level.</param>
    /// <param name="undrawnMesh">
    /// True for a mesh the game never draws (every material the editor's WorldGridMaterial: a lake's top, a helper shape); such
    /// a part never keeps an actor whose drawn parts are all deleted. Null: every visible mesh counts.
    /// </param>
    /// <param name="meshExists">
    /// True when the game files (or the project's own models) have a mesh object path. A placed mesh the game does not have
    /// (a <c>/Game/...</c> path pasted into the Map's Add object box, a project made against another build of SCUM) is not
    /// created and a warning names it; the import would otherwise point at nothing and the package check would stop the
    /// whole export (O Piqueno, 3.4.0: <c>LakeWaterSurface_B_4</c>). Null: every mesh is taken as the game's.
    /// </param>
    /// <param name="meshLoot">
    /// The loot the meshes carry themselves (see <see cref="MeshLoot"/>): a component whose loot is written gets the loot-free
    /// copy of its mesh where that mesh carries loot (the copies asked for are built by the export). Null: loot is written
    /// on the components only, as before 3.6.0.
    /// </param>
    public static LevelEditRequest PlanLevel(
        EditState state, string level, LevelDocument? document, List<string> warnings,
        Func<string, CookedPackage?>? sourcePackages, Func<string, LevelDocument?>? sourceDocuments, Func<string, BendMesh?>? bendMeshes = null,
        IReadOnlyCollection<ActorRef>? straightPieces = null, TraderExportPlan? traders = null, Func<string, bool>? undrawnMesh = null,
        Func<string, bool>? meshExists = null, MeshLoot? meshLoot = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentNullException.ThrowIfNull(warnings);

        var deleted = new HashSet<string>(
            state.DeletedActors.Where(a => SameLevel(a.Level, level) && !state.IsAdded(a)).Select(a => a.Actor),
            StringComparer.OrdinalIgnoreCase);

        var deleteComponents = PlanDeletedComponents(state, level, document, warnings, deleted);
        if (document is not null)
        {
            PlanWholeActors(state, level, document, warnings, deleted, deleteComponents, undrawnMesh);
        }

        // Duplicates become copies of the pristine source actor (deleted copies are simply not created); other added
        // actors (new meshes, Blueprints from other levels) are not exported yet.
        var copies = new List<ActorCopy>();
        var meshAdds = new List<StaticMeshActorAdd>();
        var foreignCopies = new List<ForeignActorCopy>();
        var lifts = new List<ForeignActorCopy>(); // after the trade posts they point at
        var tradeJoins = new List<(string Post, string Outpost)>();
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
                    var addedMesh = state.GetMeshOverride(added) ?? meshActor.StaticMesh; // replaced since it was added
                    if (meshExists?.Invoke(addedMesh) == false)
                    {
                        warnings.Add($"{added}: neither the game nor the mod has the mesh {addedMesh}, so the actor was not created (the game would draw nothing there). Delete it, or place the object again from Assets.");
                        break;
                    }

                    if (FarModels.IsFarViewMesh(addedMesh))
                    {
                        warnings.Add($"{added}: {addedMesh} is a far-view model (low detail, blurred, merged with what stood around it, no collision); delete it and copy the real building instead.");
                    }
                    else if (FarModels.IsUndersideMesh(addedMesh))
                    {
                        warnings.Add($"{added}: {addedMesh} is the underside of the water (flipped normals, underwater material): the game never shows it as a placed object; delete it and place the lake's top surface (the same name without _FN) instead.");
                    }

                    meshAdds.Add(new StaticMeshActorAdd(meshActor.NewName, addedMesh, state.GetAddedTransform(added) ?? meshActor.Transform)
                    {
                        CollisionProfile = meshActor.CollisionProfile ?? StandingTreeProfile(addedMesh, bendMeshes),
                        DrawsIntoLandscape = bendMeshes?.Invoke(addedMesh)?.DrawsIntoLandscape == true,
                        OnlyIntoLandscape = bendMeshes?.Invoke(addedMesh)?.OnlyIntoLandscape == true,
                        OverrideMaterials = state.GetMeshOverride(added) is null ? meshActor.OverrideMaterials : null, // a new mesh draws its own
                    });
                    break;
                case AddBlueprintActorOp blueprint when sourcePackages is not null:
                    var sourcePackage = sourcePackages(blueprint.Source.Level);
                    if (sourcePackage is null)
                    {
                        warnings.Add($"{added}: the source level {blueprint.Source.Level} could not be read; the Blueprint actor was not created.");
                        break;
                    }

                    var sourceRootName = sourceDocuments?.Invoke(blueprint.Source.Level)?.FindActor(blueprint.Source.Actor)?.Root is { IsSynthesized: false } sr ? sr.Name : null;
                    (blueprint.Mechanic is null ? foreignCopies : lifts).Add(new ForeignActorCopy(sourcePackage, blueprint.Source.Actor, blueprint.NewName, state.GetAddedTransform(added) ?? blueprint.Transform, sourceRootName)
                    {
                        Item = blueprint.Item,
                        Trade = blueprint.Trader is { } trader ? TradeCopyOf(trader) : null,
                        AssignedTradePost = blueprint.Mechanic,
                    });
                    if (blueprint.Trader is { Name.Length: > 0 } joining)
                    {
                        tradeJoins.Add((blueprint.NewName, joining.Outpost)); // a bank is in no list, like the game's
                    }

                    break;
                default:
                    warnings.Add($"{added}: this kind of added actor cannot be exported without the source level.");
                    break;
            }
        }

        foreignCopies.AddRange(lifts);
        if (traders is not null && sourcePackages is not null)
        {
            AddManagers(traders, level, sourcePackages, document, foreignCopies, warnings);
        }

        PlanBends(state, level, document, warnings, bendMeshes, deleted, copies, meshAdds, straightPieces);

        // What hangs on a deleted object follows it, the straight originals of bent actors included (audit F053).
        var owner = new Dictionary<int, (ActorRecord Actor, ComponentRecord Component)>();
        var detached = new List<ActorRecord>();
        if (document is not null && (deleted.Count > 0 || deleteComponents.Count > 0))
        {
            foreach (var actor in document.Actors)
            {
                foreach (var component in actor.Components.Where(c => !c.IsSynthesized))
                {
                    owner.TryAdd(component.ExportIndex, (actor, component));
                }
            }

            detached = PlanAttached(level, document, warnings, deleted, deleteComponents, undrawnMesh);

            // A deleted child actor takes the ChildActorComponent that spawned it along: left in a kept parent, the component
            // would spawn a fresh child from its class when the level loads, and the deleted door would be back.
            foreach (var actor in document.Actors)
            {
                if (deleted.Contains(actor.Name) && actor.ParentComponent is { } parent && owner.TryGetValue(parent, out var spawner) && !deleted.Contains(spawner.Actor.Name))
                {
                    deleteComponents.Add((spawner.Actor.Name, spawner.Component.Name));
                }
            }

            // A same-level copy keeps its source's parent (Copy.cs): with that parent gone it has no place to stand on.
            foreach (var copy in copies)
            {
                if (document.FindActor(copy.SourceActor)?.Root is { AttachParent: { } parent } && owner.TryGetValue(parent, out var on)
                    && (deleted.Contains(on.Actor.Name) || deleteComponents.Contains((on.Actor.Name, on.Component.Name))))
                {
                    warnings.Add($"{level}: {copy.NewName} copies {copy.SourceActor}, which stood on a deleted object; in game the copy may leave its place (copy an object that stands on its own instead).");
                }
            }
        }

        if (document is not null)
        {
            WarnLeftovers(state, level, document, warnings, deleted, bendMeshes);
        }

        // A part of an actor that went whole goes with it.
        deleteComponents = deleteComponents.Where(c => !deleted.Contains(c.Actor))
            .DistinctBy(c => (c.Actor.ToUpperInvariant(), c.Component.ToUpperInvariant()))
            .OrderBy(c => document?.FindActor(c.Actor)?.FindComponent(c.Component)?.ExportIndex ?? 0)
            .ToList();

        var transforms = new List<TransformPatch>();
        foreach (var (actor, component, value) in state.TransformOverrides)
        {
            if (!SameLevel(actor.Level, level) || state.IsAdded(actor) || deleted.Contains(actor.Actor))
            {
                continue; // an added actor is reported below, a deleted one has no place
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

        // Kept actors that stood on a deleted object: taken off it where they stand in the world, with the project's moves.
        var detach = new List<(string Actor, string Component)>();
        foreach (var actor in detached)
        {
            if (actor.Root is not { IsSynthesized: false } root)
            {
                warnings.Add($"{level}: {actor.Name} stood on a deleted object and its root is not stored in the level; in game it may leave its place.");
                continue;
            }

            transforms.RemoveAll(t => string.Equals(t.Actor, actor.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Component, root.Name, StringComparison.OrdinalIgnoreCase));
            transforms.Add(new TransformPatch(actor.Name, root.Name, TransformValue.FromTransform(Placed(state, level, document!, owner, actor, root).World)));
            detach.Add((actor.Name, root.Name));
        }

        // Replaced meshes of kept stored components and of copies (a bent one was re-created above and left the level; an
        // added mesh actor took its new mesh above).
        var meshPatches = new List<MeshPatch>();
        foreach (var (actor, component, mesh) in state.MeshOverrides.Where(m => SameLevel(m.Actor.Level, level) && !state.IsDeleted(m.Actor) && !deleted.Contains(m.Actor.Actor)))
        {
            if (state.IsAdded(actor) && (state.AddedActors[actor] is AddStaticMeshActorOp || CreatedBent(actor, meshAdds)))
            {
                if (component.Length > 0)
                {
                    warnings.Add($"{actor}.{component}: the actor is exported as one new mesh actor{(CreatedBent(actor, meshAdds) ? " (bent)" : string.Empty)}, which takes only its own mesh; this part's mesh was not written.");
                }

                continue;
            }

            var componentName = component.Length > 0 ? component : null;
            if (componentName is null && StoredTarget(state, actor, document, sourceDocuments)?.Record is { Root: { } root })
            {
                if (root.IsSynthesized)
                {
                    warnings.Add($"{actor}: its root component '{root.Name}' is not stored in the level package (inherited from the Blueprint); its mesh was not replaced.");
                    continue;
                }

                componentName = root.Name;
            }

            meshPatches.Add(new MeshPatch(actor.Actor, componentName, mesh, bendMeshes?.Invoke(mesh)?.BodySetupGuid));
        }

        // Road, rail and bridge pieces pushed sideways or given another mesh: their SplineParams are rewritten in place
        // (a replaced piece keeps its curve: the game fits the new mesh to it) with collision boxes following the curve.
        var splinePatches = new List<SplinePatch>();
        var replacedPieces = state.MeshOverrides.Where(m => m.Component.Length > 0 && SameLevel(m.Actor.Level, level) && !state.IsDeleted(m.Actor) && !deleted.Contains(m.Actor.Actor))
            .Select(m => (m.Actor, m.Component, Shape: state.GetSegmentShape(m.Actor, m.Component)));
        foreach (var (actor, component, shape) in state.SegmentSways.Where(s => SameLevel(s.Actor.Level, level) && !state.IsDeleted(s.Actor)).Concat(replacedPieces).DistinctBy(s => (s.Actor, s.Component)))
        {
            var replaced = state.GetMeshOverride(actor, component);
            if (StoredTarget(state, actor, document, sourceDocuments)?.Record?.FindComponent(component) is { SplineMesh: { } spline, IsSynthesized: false } piece)
            {
                var shaped = shape.IsStraight ? spline : SplineEnds.Shape(spline, shape.Sway1, shape.Sway2, shape.Start, shape.End);
                var collision = (replaced ?? piece.StaticMeshPath) is { } meshPath && bendMeshes?.Invoke(meshPath) is { Boxes.Count: > 0 } info
                    ? PieceCollision.Bend(info.Boxes!, shaped, info.Bounds)
                    : null;
                splinePatches.Add(new SplinePatch(actor.Actor, component, shaped, collision));
            }
            else if (replaced is null || !shape.IsStraight)
            {
                warnings.Add($"{actor}.{component}: not a spline mesh piece stored in the level; its bend was not written.");
            }
        }

        // Spawner point arrays (patrol paths, loot points) of kept actors and of copies (igor8802: a duplicated sentry's route
        // was dropped): rewritten whole from their stored elements, a copy's from the source's it carries.
        var spawnPoints = new List<SpawnPointsPatch>();
        foreach (var (actor, component, array, points) in state.SpawnPointOverrides.Where(s => SameLevel(s.Actor.Level, level) && !state.IsDeleted(s.Actor)))
        {
            spawnPoints.Add(new SpawnPointsPatch(actor.Actor, component, array, points));
        }

        // Instance edits: collapsed (deleted) and moved ISM/HISM instances of actors that are themselves kept. The pristine
        // instance lists come from the level document so the editor can locate and verify PerInstanceSMData.
        var instancePatches = new List<InstancePatch>();
        var hints = new Dictionary<(string Actor, string Component), InstanceArrayHint>();
        foreach (var instance in state.DeletedInstances.Where(i => SameLevel(i.Level, level) && !state.IsDeleted(i.ActorRef) && !deleted.Contains(i.Actor) && !state.IsAdded(i)))
        {
            AddInstancePatch(instance, null);
        }

        foreach (var (instance, value) in state.InstanceOverrides.Where(i => SameLevel(i.Instance.Level, level) && !state.IsDeleted(i.Instance) && !deleted.Contains(i.Instance.Actor) && !state.IsAdded(i.Instance)))
        {
            AddInstancePatch(instance, value.ToTransform());
        }

        // Added instances (copied trees) are appended behind the stored ones, in index order, where they stand now.
        var instanceAdds = new List<InstanceAdd>();
        foreach (var instance in state.AddedInstances.Keys.Where(i => SameLevel(i.Level, level) && !state.IsDeleted(i)).OrderBy(i => i.Index))
        {
            if (EnsureHint(instance))
            {
                instanceAdds.Add(new InstanceAdd(instance.Actor, instance.Component, state.GetAddedInstanceTransform(instance)!.Value.ToTransform()));
            }
        }

        // Loot of mesh components (the Map's loot editor, a copied part's loot); a duplicate carries its source's loot edits.
        var loot = new List<LootPatch>();
        foreach (var (actor, component, setting) in state.LootOverrides.Where(l => SameLevel(l.Actor.Level, level) && !state.IsDeleted(l.Actor) && !deleted.Contains(l.Actor.Actor)))
        {
            if (LootComponent(actor, component) is { } name)
            {
                loot.Add(new LootPatch(actor.Actor, name, setting));
            }
            else
            {
                warnings.Add($"{actor}: no mesh component to carry its loot; it was not written.");
            }
        }

        foreach (var meshActor in state.AddedActors.Where(a => SameLevel(a.Key.Level, level) && !state.IsDeleted(a.Key)).Select(a => a.Value).OfType<AddStaticMeshActorOp>())
        {
            if (meshActor.Loot is { } presets && state.GetLoot(new ActorRef(meshActor.Level, meshActor.NewName)) is null)
            {
                loot.Add(new LootPatch(meshActor.NewName, "StaticMeshComponent0", new LootSetting(presets.Count > 0, presets))); // a copied part's loot (none = decoration)
            }
        }

        foreach (var copy in copies)
        {
            foreach (var (actor, component, setting) in state.LootOverrides.Where(l => SameLevel(l.Actor.Level, level) && string.Equals(l.Actor.Actor, copy.SourceActor, StringComparison.OrdinalIgnoreCase)))
            {
                if (state.GetLoot(actor with { Actor = copy.NewName }, component) is null && LootComponent(actor, component) is { } name)
                {
                    loot.Add(new LootPatch(copy.NewName, name, setting));
                }
            }
        }

        // Placed objects with no loot inside, then every loot write against the loot its mesh carries itself.
        var lootMeshes = PlanNoLoot(state, level, document, sourceDocuments, deleted, meshLoot, loot, spawnPoints, warnings);
        loot = SettleLoot(state, level, document, sourceDocuments, meshLoot, lootMeshes, loot, meshPatches, warnings);

        PlanCopyParts(state, level, warnings, meshAdds, transforms);
        // What a sentry spawner spawns (the Map's spawn editor); a same-level copy carries its source's choice.
        var sentries = state.SpawnChoices.Where(c => c.Property == Spawns.SpawnChoices.SentryClass && SameLevel(c.Actor.Level, level)).ToList();
        var softClasses = sentries
            .Where(c => !state.IsDeleted(c.Actor) && !deleted.Contains(c.Actor.Actor))
            .Select(c => new SoftClassPatch(c.Actor.Actor, c.Property, c.Value))
            .ToList();
        foreach (var copy in copies)
        {
            foreach (var (actor, property, value) in sentries.Where(c => string.Equals(c.Actor.Actor, copy.SourceActor, StringComparison.OrdinalIgnoreCase)))
            {
                if (state.GetSpawnChoice(actor with { Actor = copy.NewName }, property) is null)
                {
                    softClasses.Add(new SoftClassPatch(copy.NewName, property, value));
                }
            }
        }

        return new LevelEditRequest
        {
            Loot = loot,
            SoftClasses = softClasses,
            DeleteActors = deleted.ToList(),
            DeleteComponents = deleteComponents,
            Detach = detach,
            Transforms = transforms,
            Instances = instancePatches,
            InstanceAdds = instanceAdds,
            InstanceHints = hints.Values.ToList(),
            Copies = copies,
            StaticMeshAdds = meshAdds,
            ForeignCopies = foreignCopies,
            SplinePatches = splinePatches,
            SpawnPoints = spawnPoints,
            Meshes = meshPatches,
            TradeJoins = tradeJoins,
        };

        void AddInstancePatch(InstanceRef instance, FTransform? local)
        {
            if (EnsureHint(instance))
            {
                instancePatches.Add(new InstancePatch(instance.Actor, instance.Component, instance.Index, local));
            }
        }

        // The stored mesh component a loot setting is for: named, or the root (an added mesh's StaticMeshComponent0, a copy's source's root).
        string? LootComponent(ActorRef actor, string component) =>
            component.Length > 0 ? component
            : state.IsAdded(actor) ? state.AddedActors[actor] switch
            {
                AddStaticMeshActorOp => "StaticMeshComponent0",
                DuplicateActorOp d => document?.FindActor(d.Source.Actor)?.Root is { IsSynthesized: false } r ? r.Name : null,
                _ => null,
            }
            : document?.FindActor(actor.Actor)?.Root is { IsSynthesized: false } root ? root.Name : null;

        // The component's pristine instance list (read once per component); false, with a warning, when it has none.
        bool EnsureHint(InstanceRef instance)
        {
            var key = (instance.Actor.ToLowerInvariant(), instance.Component.ToLowerInvariant());
            if (hints.ContainsKey(key))
            {
                return true;
            }

            var actor = StoredTarget(state, instance.ActorRef, document, sourceDocuments)?.Record; // a copy's are its source's
            if (actor is null)
            {
                warnings.Add($"{instance}: the actor's instance list could not be read; instance edit skipped.");
                return false;
            }

            var known = actor.InstanceTransforms
                .Where(t => string.Equals(t.ComponentName, instance.Component, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.InstanceIndex)
                .Select(t => t.LocalTransform)
                .ToList();
            if (known.Count == 0)
            {
                warnings.Add($"{instance}: component '{instance.Component}' has no stored instances; instance edit skipped.");
                return false;
            }

            hints[key] = new InstanceArrayHint(instance.Actor, instance.Component, known);
            return true;
        }
    }

    /// <summary>
    /// Components deleted from actors that stay (a fireplace part with its fire, light and sound), by the level's names. The
    /// journal is checked again as the studio checks a delete (<see cref="EditOpFactory.DeleteComponents"/>): a journal edited
    /// by hand, merged, or read with another root inferred never makes an actor's root, a part its C++ class makes or a part
    /// something kept hangs under an inert object; each is reported and stays.
    /// </summary>
    private static List<(string Actor, string Component)> PlanDeletedComponents(EditState state, string level, LevelDocument? document, List<string> warnings, HashSet<string> deleted)
    {
        var picked = new List<(ActorRecord Actor, ComponentRecord Component)>();
        foreach (var (actor, component) in state.DeletedComponents.Where(k => SameLevel(k.Actor.Level, level) && !state.IsAdded(k.Actor) && !deleted.Contains(k.Actor.Actor)))
        {
            if (document is null)
            {
                warnings.Add($"{actor}.{component}: the level could not be read to check this part; it was not deleted.");
            }
            else if (document.FindActor(actor.Actor) is not { } record || record.FindComponent(component) is not { } stored)
            {
                warnings.Add($"{actor}.{component}: not stored in the level; that part was not deleted.");
            }
            else if (PartRules.WhyNotDeletable(record, stored) is { } refusal)
            {
                warnings.Add($"{actor}: component '{stored.Name}' {refusal}; it was not deleted.");
            }
            else
            {
                picked.Add((record, stored));
            }
        }

        // A part with a kept thing under it stays, and so do the deleted parts it hangs under.
        for (var dropped = document is not null; dropped;)
        {
            dropped = false;
            var going = picked.Select(p => p.Component.ExportIndex).ToHashSet();
            foreach (var p in picked.ToList())
            {
                if (PartRules.KeptUnder(document!, [p.Component.ExportIndex], (a, c) => deleted.Contains(a.Name) || going.Contains(c.ExportIndex)) is { } kept)
                {
                    warnings.Add($"{level}: {kept.Actor.Name}.{kept.Component.Name} hangs under {p.Actor.Name}.{p.Component.Name} and stays; that part was not deleted.");
                    picked.Remove(p);
                    dropped = true;
                }
            }
        }

        return picked.Select(p => (p.Actor.Name, p.Component.Name)).ToList();
    }

    // A part the game draws: the map's rule, less a mesh left on the WorldGrid material unless the level paints it over.
    private static bool IsDrawn(ActorRecord actor, ComponentRecord component, Func<string, bool>? undrawnMesh) =>
        PartRules.IsDrawnPart(actor, component)
        && (component.OverrideMaterials?.Any(m => m is not null && !m.StartsWith(WorldGridMaterial, StringComparison.OrdinalIgnoreCase)) == true
            || undrawnMesh?.Invoke(component.StaticMeshPath!) != true);

    /// <summary>
    /// An actor whose every part the map draws is gone (scaled to nothing or deleted), and every instance too, goes whole:
    /// older projects "deleted" a burning barrel part by part and its fire, sound, heat and collision stayed in game. A
    /// hidden, helper or WorldGrid mesh, which the map never draws, does not keep it, nor do instances beside parts (research
    /// H-D': both did).
    /// </summary>
    private static void PlanWholeActors(EditState state, string level, LevelDocument document, List<string> warnings, HashSet<string> deleted,
        IReadOnlyCollection<(string Actor, string Component)> deletedParts, Func<string, bool>? undrawnMesh)
    {
        var collapsed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (actor, component) in state.TransformOverrides.Where(o => SameLevel(o.Actor.Level, level) && !state.IsAdded(o.Actor) && o.Component.Length > 0 && IsNothing(o.Value.Scale))
                     .Select(o => (o.Actor.Actor, o.Component)).Concat(deletedParts))
        {
            (collapsed.TryGetValue(actor, out var parts) ? parts : collapsed[actor] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(component);
        }

        var touched = collapsed.Keys
            .Concat(state.DeletedInstances.Where(i => SameLevel(i.Level, level)).Select(i => i.Actor))
            .Where(a => !deleted.Contains(a) && !state.IsAdded(new ActorRef(level, a)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var wholly = 0;
        foreach (var name in touched)
        {
            if (document.FindActor(name) is not { } actor)
            {
                continue;
            }

            var drawn = actor.Components.Where(c => !c.IsSynthesized && IsDrawn(actor, c, undrawnMesh)).ToList();
            var gone = drawn.All(c => collapsed.TryGetValue(actor.Name, out var parts) && parts.Contains(c.Name));
            if ((drawn.Count > 0 || actor.InstanceTransforms.Count > 0) && gone && AllInstancesDeleted(state, actor, level))
            {
                deleted.Add(actor.Name);
                wholly++;
            }
        }

        if (wholly > 0)
        {
            warnings.Add($"{level}: {wholly} actor(s) whose every drawn part was scaled to nothing or deleted are removed whole (their fire, sound, heat and collision go too).");
        }
    }

    /// <summary>
    /// What hangs on a deleted actor or deleted part follows it: a stored child actor its ChildActorComponent spawned goes, and
    /// so does an attached actor that draws nothing (a fire emitter, its light and sound hung on a fireplace). An attached
    /// actor with a mesh of its own (a lamp on a shelf, a bridge's fence) stays and is returned, to be taken off its parent
    /// where it stands: the parent is an inert object in the written level, the game nulls the link and the relative place
    /// would become a world place round the origin (audit F053). So does a trader, spawner or volume hung on it: the export,
    /// like the companions, never deletes those on its own (<see cref="ActorRoles.IsNeverTakenAlong"/>).
    /// </summary>
    private static List<ActorRecord> PlanAttached(string level, LevelDocument document, List<string> warnings, HashSet<string> deleted,
        IReadOnlyCollection<(string Actor, string Component)> deletedParts, Func<string, bool>? undrawnMesh)
    {
        var goneParts = deletedParts.Select(p => document.FindActor(p.Actor)?.FindComponent(p.Component)?.ExportIndex).OfType<int>().ToHashSet();
        bool Draws(ActorRecord actor) => actor.InstanceTransforms.Count > 0 || actor.Components.Any(c => IsDrawn(actor, c, undrawnMesh));

        // Breadth first: the fate of what an actor hangs from is settled before its own. One that stays (detached) keeps
        // whatever hangs on it.
        var taken = new List<string>();
        var detached = new List<ActorRecord>();
        foreach (var d in Attachments.Dependents(document, new HashSet<string>(deleted, StringComparer.OrdinalIgnoreCase), goneParts))
        {
            if (!deleted.Contains(d.Parent.Name) && !goneParts.Contains(d.ViaComponent))
            {
                continue;
            }

            if (d.Kind == DependentKind.ChildActor)
            {
                deleted.Add(d.Actor.Name); // a stored child actor goes with what spawned it
            }
            else if (ActorRoles.IsNeverTakenAlong(document, d.Actor))
            {
                detached.Add(d.Actor);
                warnings.Add($"{level}: {d.Actor.Name} ({d.Actor.ClassName}) hung on deleted {d.Parent.Name} and stays where it stands; the export never deletes a trader, spawner or volume on its own: delete it in the studio if it should go.");
            }
            else if (Draws(d.Actor))
            {
                detached.Add(d.Actor);
            }
            else
            {
                deleted.Add(d.Actor.Name);
                taken.Add(d.Actor.Name);
            }
        }

        if (taken.Count > 0)
        {
            warnings.Add($"{level}: {taken.Count} actor(s) attached to deleted objects and drawing nothing go with them (their fire, light and sound): {string.Join(", ", taken)}.");
        }

        if (detached.Count > 0)
        {
            warnings.Add($"{level}: kept where they stand in the world, taken off the deleted object they stood on: {string.Join(", ", detached.Select(a => a.Name))}.");
        }

        return detached;
    }

    /// <summary>
    /// Says what still belongs to an object the journal deletes in this level and stays (a journal from an older studio, the
    /// MCP, a delete of the object alone): a fire, light, sound or decal at its spot (<see cref="Companions.Find"/>), and the
    /// items a deleted spawner already gave out. The export never deletes these on its own.
    /// </summary>
    private static void WarnLeftovers(EditState state, string level, LevelDocument document, List<string> warnings, HashSet<string> deleted, Func<string, BendMesh?>? bendMeshes)
    {
        var seeds = state.DeletedActors.Where(a => SameLevel(a.Level, level) && !state.IsAdded(a) && document.FindActor(a.Actor) is not null).Select(a => new DeleteSeed(a)).ToList();
        if (seeds.Count == 0)
        {
            return;
        }

        // The deleted objects' own bounds; the kept ones (the ownership guard) stand in as 2 m boxes, so the check never reads
        // every mesh of the level.
        var seedMeshes = seeds.SelectMany(s => document.FindActor(s.Actor.Actor)!.Components).Select(c => c.StaticMeshPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = Companions.Find([document], seeds, state, mesh => seedMeshes.Contains(mesh) ? bendMeshes?.Invoke(mesh)?.Bounds : null);
        foreach (var c in found.Companions.Where(c => c.DefaultOn && !c.Locked && !c.HasOwnMesh && c.Rule is not (CompanionRule.ChildActor or CompanionRule.Attached)))
        {
            if (!deleted.Contains(c.Actor.Actor) && (c.Component is null ? !state.IsDeleted(c.Actor) : !state.IsDeleted(c.Actor, c.Component)))
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{level}: {c.Actor.Actor}{(c.Component is null ? string.Empty : "." + c.Component)} ({c.ClassName}) stays {c.DistanceCm / 100f:0.#} m from deleted {c.Seed.Actor}; delete it with the object in the studio."));
            }
        }

        foreach (var note in found.Notes.Where(n => n.Kind == CompanionNoteKind.SpawnedItemsStay))
        {
            warnings.Add($"{level}: items {note.Actor.Actor} already spawned stay in worlds that ran before this mod; only the game removes them.");
        }
    }

    /// <summary>
    /// Where a stored component stands in the world with the project's moves, its own and its parents' (as the level has it
    /// when none moved); Moved tells whether one did. A moved one hangs in its parent's space as the level reads it: on the
    /// parent mesh's socket, with its absolute location, rotation or scale (audit F201).
    /// </summary>
    private static (FTransform World, bool Moved) Placed(EditState state, string level, LevelDocument document, IReadOnlyDictionary<int, (ActorRecord Actor, ComponentRecord Component)> owner,
        ActorRecord actor, ComponentRecord component, int depth = 0)
    {
        var actorRef = new ActorRef(level, actor.Name);
        var moved = (component.ExportIndex == actor.RootComponent ? state.GetTransformOverride(actorRef) : null) ?? state.GetTransformOverride(actorRef, component.Name);
        if (component.AttachParent is { } parent && depth < 64 && owner.TryGetValue(parent, out var up))
        {
            var (parentWorld, parentMoved) = Placed(state, level, document, owner, up.Actor, up.Component, depth + 1);
            if (moved is null && !parentMoved)
            {
                return (component.WorldTransform, false);
            }

            return (InParent(document, component, (moved ?? component.Relative).ToTransform(), parentWorld), true);
        }

        return moved is { } own ? (own.ToTransform(), true) : (component.WorldTransform, false);
    }

    /// <summary>
    /// <paramref name="relative"/> placed in its parent's space as the level reads it (<c>LevelDocument</c>): on the parent
    /// mesh's socket, with the component's absolute location, rotation or scale (audit F201).
    /// </summary>
    private static FTransform InParent(LevelDocument document, ComponentRecord component, FTransform relative, FTransform parentWorld)
    {
        var stored = component.ExportIndex >= 0 && component.ExportIndex < document.Data.Exports.Count ? document.Data.Exports[component.ExportIndex] : null;
        var world = relative * (stored?.AttachSocket is { } socket ? socket * parentWorld : parentWorld);
        return stored is { AbsoluteLocation: true } or { AbsoluteRotation: true } or { AbsoluteScale: true }
            ? new FTransform(
                stored.AbsoluteRotation ? relative.Rotation : world.Rotation,
                stored.AbsoluteLocation ? relative.Translation : world.Translation,
                stored.AbsoluteScale ? relative.Scale3D : world.Scale3D)
            : world;
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
            IReadOnlyList<string?>? materials;
            var name = actor.Actor;
            var added = state.AddedActors.GetValueOrDefault(actor);
            switch (added)
            {
                case AddStaticMeshActorOp when !meshAdds.Exists(a => string.Equals(a.NewName, actor.Actor, StringComparison.OrdinalIgnoreCase)):
                    continue; // left out above (a mesh neither the game nor the mod has), and named in the warnings
                case AddStaticMeshActorOp meshActor:
                    mesh = state.GetMeshOverride(actor) ?? meshActor.StaticMesh;
                    transform = state.GetAddedTransform(actor) ?? meshActor.Transform;
                    materials = meshActor.OverrideMaterials;
                    break;
                case DuplicateActorOp duplicate when document?.FindActor(duplicate.Source.Actor) is { Kind: ActorKind.StaticMeshActor } source:
                    mesh = state.GetMeshOverride(actor) ?? source.StaticMeshPath; // a copy given another mesh bends as that one
                    transform = InWorld(document, source, state.GetAddedTransform(actor) ?? duplicate.Transform);
                    materials = source.Root?.OverrideMaterials;
                    break;
                case null when document?.FindActor(actor.Actor) is { Kind: ActorKind.StaticMeshActor, Root: { } root } pristine:
                    mesh = state.GetMeshOverride(actor) ?? pristine.StaticMeshPath; // a replaced piece bends as its new mesh
                    transform = InWorld(document, pristine, state.GetTransformOverride(actor) ?? root.Relative);
                    materials = root.OverrideMaterials;
                    name = UniqueName(document, actor.Actor + "_Bent", meshAdds);
                    break;
                default:
                    warnings.Add($"{actor}: only a single-mesh actor (StaticMeshActor) can be bent; exported straight.");
                    continue;
            }

            if (mesh is not null && BendSupport.IsRock(mesh))
            {
                warnings.Add($"{actor}: exported straight - a rock or cliff keeps its exact collision only straight; bent it would get rough boxes that players sink into and fall under (collision check).");
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
                bent.Add(new StaticMeshActorAdd(pieceName, mesh, transform with { Scale = FVector.One }, pieces[i], collision, info.BodySetupGuid)
                {
                    DrawsIntoLandscape = info.DrawsIntoLandscape,
                    OnlyIntoLandscape = info.OnlyIntoLandscape,
                    OverrideMaterials = state.GetMeshOverride(actor) is null ? materials : null, // a new mesh draws its own (as TryPatchMesh drops them)
                });
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
                    foreach (var (_, part, _) in state.MeshOverrides.Where(m => m.Component.Length > 0 && ActorRef.Comparer.Equals(m.Actor, actor)))
                    {
                        warnings.Add($"{actor}.{part}: the actor is exported as one new mesh actor (bent), which takes only its own mesh; this part's mesh was not written.");
                    }

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
            actor.Root is { AttachParent: { } parentIndex } hung
            && document.Actors.SelectMany(a => a.Components).FirstOrDefault(c => c.ExportIndex == parentIndex) is { } parent
                ? TransformValue.FromTransform(InParent(document, hung, relative.ToTransform(), parent.WorldTransform))
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

    private const string WorldGridMaterial = "/Engine/EngineMaterials/WorldGridMaterial";

    /// <summary>
    /// True for a mesh left on the engine's default grid material only: the game never draws it (a lake's top surface, the
    /// weather shells round the bunkers; the map skips such meshes the same way).
    /// </summary>
    private static bool IsWorldGridOnly(AssetCatalog catalog, string meshPath)
    {
        try
        {
            if (!catalog.TryLoadObject<CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh>(meshPath, out var mesh))
            {
                return false;
            }

            var materials = Assets.Meshes.MeshExtractor.DescribeStaticMesh(mesh).Materials;
            return materials.Count > 0 && materials.All(m => m.MaterialPath.StartsWith(WorldGridMaterial, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // unreadable: counted as drawn, the actor stays
        }
    }

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

    /// <summary>A package's <c>.uasset</c>/<c>.umap</c>, <c>.uexp</c> and (when there is one) <c>.ubulk</c> bytes.</summary>
    internal static (byte[] UAsset, byte[] UExp, byte[]? UBulk) ReadPackageFiles(AssetCatalog catalog, CUE4Parse.FileProvider.Objects.GameFile file)
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

    /// <summary>
    /// Writes the spawn place edits of <paramref name="state"/> into the island's static data, staged next to the levels,
    /// and removes the stock spawn points standing inside <paramref name="volumes"/> (the placed objects that clear what
    /// is under them). The edits name stock places by their number in the client's lists: a server cook gets them only when
    /// its lists match the client's read in the same run, in length and where each edited place stands (audit F281); else
    /// only the new places and the points under its own placed objects. A zone's settings or a biome is imported only when
    /// its package is in the game files or among the project's copies.
    /// </summary>
    private async Task<ExportedAsset?> WriteSpawnPlacesAsync(EditState state, AssetCatalog catalog, CUE4Parse.FileProvider.Objects.GameFile file, string staging, List<string> warnings,
        IReadOnlyList<GrassClearing.Volume> volumes, ProjectSourceRole role, CancellationToken cancellationToken)
    {
        var request = Spawns.SpawnPlacesEditor.Plan(state, warnings);
        if (request.IsEmpty && volumes.Count == 0)
        {
            return null;
        }

        var (uasset, uexp, ubulk) = ReadPackageFiles(catalog, file);
        var package = CookedPackage.Parse(uasset, uexp, ubulk, Spawns.SpawnPlaces.StaticDataPath);
        if (role == ProjectSourceRole.Client)
        {
            ClientSpawnReference = Spawns.SpawnPlacesEditor.Reference(package, request);
        }
        else
        {
            request = Spawns.SpawnPlacesEditor.ForOtherCook(request, ClientSpawnReference, package, warnings);
        }

        var blockedNames = new List<string>();
        request = Spawns.SpawnPlacesEditor.Block(request, Spawns.SpawnPlaces.Read(package), volumes, out var blocked, blockedNames);
        if (request.IsEmpty)
        {
            return null;
        }

        if (blocked > 0)
        {
            warnings.Add($"Spawn places: {blocked} vehicle or zombie/NPC spawn point(s) stood inside placed rocks or cliffs and were removed: {string.Join(", ", blockedNames)}.");
        }

        var (bytes, report) = Spawns.SpawnPlacesEditor.Apply(package, request, path => path.StartsWith('/') && AssetEditing.ResolvePackage(catalog, state, path) is not null);
        var virtualPath = file.Path.Replace('\\', '/');
        var dot = virtualPath.LastIndexOf('.');
        var target = Path.Combine([staging, .. virtualPath[..dot].Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await bytes.WriteAsync(target, virtualPath[dot..], cancellationToken).ConfigureAwait(false);
        warnings.AddRange(report.Warnings.Select(w => $"Spawn places: {w}"));
        _logger.LogInformation("Spawn places: {Deleted} removed, {Moved} moved, {Added} copied, {Created} new, {SubZones} sub-zone(s) added.", report.Deleted, report.Moved, report.Added, report.Created, report.SubZones);
        return new ExportedAsset(Spawns.SpawnPlaces.StaticDataPath, virtualPath, false);
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
        return new AssetModRequest(clones, edits)
        {
            Replacements = new Dictionary<string, string>(state.AssetReplacements, StringComparer.OrdinalIgnoreCase),
            FreeModels = state.AssetClones.Where(c => c.Kind == VehicleAttachmentEdits.PartKind).Select(c => c.NewPrimary).ToList(),
        };
    }

    private static void ResetDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// The export report of <paramref name="result"/>: what to install first, then the details. <paramref name="craftables"/>: the
    /// Craftables pak's report (no levels; its packages are the craftables'). <paramref name="goesWith"/>: the other page paks a
    /// page's pak only works with (<see cref="ExportOptions.GoesWith"/>), named with those its packages need.
    /// </summary>
    internal static string BuildReport(ExportResult result, AssetCatalog catalog, ExportModule? module = null, string? registryPak = null, bool craftables = false, IReadOnlyList<string>? goesWith = null)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(result.ModName).Append(" — ").Append(result.Role).AppendLine(" export");
        sb.AppendLine();

        // What to copy comes first (owner: "just the pak, correct"); the details follow for whoever needs them.
        sb.AppendLine("## Install");
        sb.AppendLine();
        sb.Append("- Files: ").AppendLine(result.PakPath is null ? "(no pak written: staged files only)"
            : Path.GetFileName(result.PakPath) + (result.SigPath is null ? " (copy a stock .sig next to it)" : " + " + Path.GetFileName(result.SigPath)));
        if (result.EconomyPath is not null)
        {
            sb.Append("- Economy: ").Append(Path.GetFileName(result.EconomyPath)).AppendLine(" goes to the server's SCUM\\Saved\\Config\\WindowsServer (see its README).");
        }

        sb.AppendLine();
        sb.AppendLine(module is not null
            ? ModuleExport.InstallNote(result.Role, Path.GetFileName(result.PakPath) ?? result.ModName, registryPak ?? string.Empty,
                [.. (result.Validation?.Needs ?? []).Concat(goesWith ?? []).Distinct(StringComparer.OrdinalIgnoreCase)])
            : InstallNote(result.Role));
        if (craftables && CraftablesExporter.TaggedSetupsNote(result) is { } tagged)
        {
            sb.AppendLine();
            sb.AppendLine(tagged);
        }

        sb.AppendLine();
        sb.AppendLine("## Details");
        sb.AppendLine();
        sb.Append("- Source: ").AppendLine(catalog.DisplayName);
        if (!craftables)
        {
            sb.Append("- Levels rewritten: ").Append(result.Levels.Count.ToString(CultureInfo.InvariantCulture))
                .Append(", actors removed: ").Append(result.RemovedActorCount.ToString(CultureInfo.InvariantCulture))
                .Append(", transforms written: ").AppendLine(result.PatchedTransformCount.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append(craftables ? "- Craftables packages: " : "- Vehicle/item packages: ").Append(result.Assets.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(result.Assets.Count(a => a.IsClone).ToString(CultureInfo.InvariantCulture)).Append(" new), values written: ")
            .Append(result.AssetValues.Count.ToString(CultureInfo.InvariantCulture)).Append(", registry records added: ")
            .AppendLine(result.Registered.Count.ToString(CultureInfo.InvariantCulture));
        sb.Append("- Pak: ").AppendLine(result.PakPath ?? "(not written)");
        sb.Append("- Signature: ").AppendLine(result.SigPath ?? "(none — copy a stock .sig next to the pak)");
        sb.Append("- Work files (hidden): ").AppendLine(result.StagingDirectory);
        if (result.FarModels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Far models");
            sb.AppendLine();
            foreach (var line in result.FarModels)
            {
                sb.Append("- ").AppendLine(line);
            }
        }

        if (result.FarPlaced.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Placed far from their level");
            sb.AppendLine();
            sb.AppendLine("The game loads a level only near its area. These stand out of their level's reach, so each went into the terrain tile under it, which the game loads within its streaming distance (1 km for almost every tile; the project keeps them where they were). The game's LODs still apply; not yet checked in the game: whether it also hides small pieces sooner by their size.");
            sb.AppendLine();
            foreach (var move in result.FarPlaced)
            {
                sb.Append("- ").AppendLine(move.Describe());
            }
        }

        if (!craftables)
        {
            sb.AppendLine();
            sb.AppendLine("## Levels");
        }

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

            if (r.NeutralizedExports > 0)
            {
                sb.Append("- Objects of removed actors made inert: ").AppendLine(r.NeutralizedExports.ToString(CultureInfo.InvariantCulture));
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

        var lootCopies = result.Assets.Where(a => MeshLoot.IsCopy(a.PackagePath)).ToList();
        if (lootCopies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Loot");
            sb.AppendLine();
            sb.AppendLine("The game also reads loot from a searchable object's model, so a change written on the object alone can leave the model's loot spawning. "
                          + "Objects whose loot this project changed or took out draw these copies of their model, which carry no loot: they look the same and give only the loot set in the studio.");
            foreach (var asset in lootCopies)
            {
                sb.Append("- New: ").AppendLine(asset.VirtualPath);
            }
        }

        if (result.Assets.Count > lootCopies.Count)
        {
            sb.AppendLine();
            sb.AppendLine(craftables ? "## Craftables" : "## Vehicles and items");
            sb.AppendLine();
            foreach (var asset in result.Assets.Except(lootCopies))
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

        if (result.StubbedEvents.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Sound events (server)");
            sb.AppendLine();
            sb.Append("- Replaced by the server's 12-byte stubs: ").AppendLine(result.StubbedEvents.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var path in result.StubbedEvents)
            {
                sb.Append("- ").AppendLine(path);
            }
        }

        if (result.RemovedItemSpawners.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Item spawners removed");
            sb.AppendLine();
            sb.AppendLine(ItemSpawnerNote);
            sb.AppendLine();
            foreach (var line in result.RemovedItemSpawners)
            {
                sb.Append("- ").AppendLine(line);
            }
        }

        AppendTraderReport(sb, result);
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
