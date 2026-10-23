using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Landscape;
using ScumStudio.Core.Mathematics;
using ScumStudio.Core.Settings;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Rendering.Resources;
using ScumStudio.Viewport;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio render level</c>: prepares whole cooked sublevels with <see cref="LevelScenePreparer"/> (static meshes,
/// Blueprint component meshes, ISM/HISM instances, terrain of landscape tiles), uploads them offscreen and renders one
/// frame to PNG. This is the command-line twin of the desktop viewport.
/// </summary>
internal sealed partial class RenderCommands
{
    private static Command BuildLevel()
    {
        var source = new Argument<string>("source", "Paks folder, single .pak, or loose folder containing SCUM/Content (or Content).");
        var sublevel = new Argument<string[]>("sublevel", "One or more sublevel names (e.g. A_0_Outpost_Exterior A_0_Outpost_Ext_Saloon) or package paths; a cell name such as A_0 renders every sublevel of that cell (POI, TV base, misc and landscape tiles, like the app's Map page). May be left out with --around.")
        {
            Arity = ArgumentArity.ZeroOrMore,
        };
        var aes = new Option<string?>(["--aes", "-a"], $"AES-256 key for encrypted stock paks. Prefer the {AesKeyText.EnvironmentVariable} environment variable.");
        var output = OutputOption();
        var size = SizeOption();
        var yaw = YawOption(-135f);
        var pitch = PitchOption(-35f);
        var dist = DistOption();
        var lod = new Option<int>("--lod", () => 0, "Finest LOD index used for every mesh (falls back to LOD 0 when a mesh has fewer LODs).");
        var lods = new Option<int>("--lods", () => 8, "Number of LODs uploaded per mesh from --lod on; the renderer picks one per cluster by projected size (1 = finest LOD everywhere).");
        var viewDistance = new Option<float>("--view-distance", () => 1f, "Multiplier on the cull distances stored on foliage/HISM components and on --cull-pixels (1 = as the game draws).");
        var cullPixels = new Option<float>("--cull-pixels", () => 4f, "Do not draw instance clusters whose largest instance projects smaller than this many pixels (0 = draw everything in view).");
        var noTexture = new Option<bool>("--no-texture", "Flat shading: do not look up base colour textures.");
        var textureSize = new Option<int>("--texture-size", () => 512, "Largest texture edge in pixels (a smaller mip is picked above this).");
        var noInstances = new Option<bool>("--no-instances", "Skip instanced static mesh (ISM/HISM) instances.");
        var noLandscape = new Option<bool>("--no-landscape", "Do not build terrain from landscape tiles among the given levels.");
        var landscapeStep = new Option<int>("--landscape-step", () => 1, "Terrain vertex step in quads (1 = full detail, 2 = quarter of the triangles, ...).");
        var filter = new Option<string?>("--filter", "Only actors whose name, class or mesh path contains this text.");
        var noGrid = NoGridOption();
        var frames = new Option<int>("--frames", () => 0, "Benchmark: render this many extra frames with a slowly orbiting camera and print the average and 95th-percentile frame time (CPU, and CPU + glFinish).");
        var ground = new Option<GroundMode>("--ground", () => GroundMode.Realistic,
            "Terrain colouring: realistic (baked layer colours, rock slopes, shore), layers (debug colour per paint layer), height (colour ramp) or plain (flat tint).");
        var sea = new Option<bool>("--sea", "Always draw the sea plane at Z = 0 when there is terrain (default: only when terrain goes below sea level).");
        var noSea = new Option<bool>("--no-sea", "Never draw the sea plane.");
        var groundTexture = new Option<int>("--ground-texture", () => 0,
            "Baked ground texture size per terrain component in realistic mode (0 = 1024 when the layer textures are in the source, else one texel per height sample).");
        var target = new Option<string?>("--target", "Orbit this UE point 'X,Y,Z' (cm) instead of the scene centre (use with --dist for close-ups).");
        var island = new Option<bool>("--island", "Also draw the whole island's terrain behind the levels, as the app's whole-island mode does (all landscape tiles, coarse).");
        var hour = new Option<float?>("--hour", "Time of day 0-24 for outdoor scenes (SCUM's clock: sunrise 06:00, sunset 21:00; 13.5 = the island's own sun, the default look; the night is the game's moonlit night).");
        var noShadows = new Option<bool>("--no-shadows", "Do not draw sun shadows (the Map view draws them except on the Performance quality).");
        var spawnPlaces = new Option<bool>("--spawn-places", "Also draw the island's spawn places around the levels (vehicle and zombie spawn points, threat zones, hunting areas), as the app's Spawns switch does.");
        var around = new Option<string?>("--around", "Load the levels whole-island mode loads for a camera at this UE point 'X,Y,Z' (cm): every content sublevel whose tile bounds come within --radius; the camera orbits it (unless --target).");
        var radius = new Option<float>("--radius", () => 30_000f, "Detail radius for --around in cm (the Map's stream radius: Performance 20000, Balanced 30000, High 60000, Ultra 100000).");
        var farModels = new Option<bool>("--far-models", "Also draw the game's own far view (the island's DistantLevelManager models) with the island backdrop, hiding those of the levels drawn in detail, as the app's whole-island mode does (implies --island).");
        var renderDistance = new Option<int>("--render-distance", () => 0, "Draw the island backdrop's far models only this many sectors (map cells) from the camera (0 = no limit).");
        var sectorSize = new Option<float>("--sector-size", () => 0f, "Sector size in cm for --render-distance (0 = measured from the cells' landscape tiles).");
        var lodBias = new Option<float>("--lod-bias", () => 1f, "Multiplier on the projected size used to pick a mesh LOD (the Map's qualities: Performance 0.6, Balanced 0.8, High 1, Ultra 1.25).");
        var objectDistance = new Option<float>("--object-distance", () => 0f, "Do not draw the levels' objects farther than this many cm (0 = as far as the game draws them; Performance 30000, Balanced 60000, High 150000).");
        var samples = new Option<int>("--samples", () => 1, "Samples per pixel (the Map view draws with 4: anti-aliased edges and foliage).");
        var temporal = new Option<bool>("--temporal", "Steady the --frames as the Map's Steady foliage switch does (each frame blended with the last one where the camera saw it).");
        var framesOut = new Option<string?>("--frames-out", "Save every --frames frame as a PNG into this folder and print how much the frames flicker (FrameFlicker).");
        var orbitStep = new Option<float>("--orbit-step", () => 0.5f, "Degrees the camera orbits per --frames frame (0.02 is a slow pan).");
        var terrainDetail = new Option<RenderQuality?>("--terrain-detail",
            "Draw the ground near the camera from every landscape sample as the Map view does at this quality (performance, balanced, high, ultra, gamelike); use with --landscape-step 4, the step whole-island and cell views load.");
        var glDebug = new Option<bool>("--gl-debug",
            $"Draw on a debug context and print every message the graphics driver sends (errors and undefined behaviour first); exit code {DriverProblemsExitCode} when it reports a problem. For graphics-card crash reports.");
        var command = new Command("level", "Render whole cooked sublevels (all placed static meshes, plus terrain for landscape tiles) to PNG.")
        {
            source, sublevel, aes, output, size, yaw, pitch, dist, lod, lods, viewDistance, cullPixels, noTexture, textureSize, noInstances, noLandscape, landscapeStep, filter, noGrid, frames,
            ground, sea, noSea, groundTexture, target, island, spawnPlaces, noShadows, hour, around, radius, farModels, renderDistance, sectorSize, lodBias, objectDistance, samples, glDebug, temporal, framesOut, orbitStep,
            terrainDetail,
        };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<RenderCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                var (width, height) = ParseSize(parse.GetValueForOption(size)!);
                Vector3? orbitTarget = null;
                if (parse.GetValueForOption(target) is { Length: > 0 } targetText)
                {
                    if (!TryParseUePoint(targetText, out var point))
                    {
                        logger.LogError("--target must be 'X,Y,Z' in UE centimetres (got '{Target}').", targetText);
                        return 2;
                    }

                    orbitTarget = point;
                }
                Vector3? aroundPoint = null;
                if (parse.GetValueForOption(around) is { Length: > 0 } aroundText)
                {
                    if (!TryParseUePoint(aroundText, out var point))
                    {
                        logger.LogError("--around must be 'X,Y,Z' in UE centimetres (got '{Around}').", aroundText);
                        return 2;
                    }

                    aroundPoint = point;
                    orbitTarget ??= point;
                }

                if (aroundPoint is null && parse.GetValueForArgument(sublevel).Length == 0)
                {
                    logger.LogError("Name at least one sublevel or cell, or give --around X,Y,Z.");
                    return 2;
                }

                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var index = WorldIndex.FromCatalog(catalog);
                var sectors = parse.GetValueForOption(renderDistance);
                if (aroundPoint is not null || (sectors > 0 && parse.GetValueForOption(sectorSize) <= 0f))
                {
                    index = index.WithTileInfo(catalog, logger, cancellationToken: ct); // level and cell bounds
                }

                var packagePaths = new List<string>();
                if (aroundPoint is { } camera)
                {
                    // The set the Map's whole-island mode loads for a camera there (MapPageViewModel.LevelsAround).
                    var near = FarModelsScene.LevelsAround(index, new FVector(camera.X, camera.Y, camera.Z), parse.GetValueForOption(radius));
                    Console.Out.WriteLine(Invariant($"around ({camera.X:0}, {camera.Y:0}, {camera.Z:0}): {near.Count} level(s) within {parse.GetValueForOption(radius) / 100f:0} m"));
                    packagePaths.AddRange(near);
                }

                foreach (var name in parse.GetValueForArgument(sublevel))
                {
                    if (MapCell.TryParse(name, out var cell))
                    {
                        // Same set the app's Map page loads for a cell (MapPageViewModel.CellPackages): POI, TV base, abandoned-city and misc sublevels, then the landscape tiles.
                        var cellLevels = index.InCell(cell)
                            .Where(p => p.IsContentLevel)
                            .OrderBy(p => p.Kind == WorldPackageKind.Landscape)
                            .Select(p => p.PackagePath)
                            .ToList();
                        if (cellLevels.Count > 0)
                        {
                            packagePaths.AddRange(cellLevels);
                            continue;
                        }
                    }

                    var packagePath = index.Find(name)?.PackagePath ?? (name.Contains('/') ? name : null);
                    if (packagePath is null || !catalog.PackageExists(packagePath))
                    {
                        logger.LogError("Level not found: {Name} (use 'scumstudio level list' to see the available sublevels).", name);
                        return 2;
                    }

                    packagePaths.Add(packagePath);
                }

                var clock = Stopwatch.StartNew();
                var reader = new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions(), logger);
                var documents = packagePaths.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => LevelDocument.Load(reader, p, ct)).ToList();
                if (parse.GetValueForOption(spawnPlaces) && Level.Spawns.SpawnPlaces.AreaAround(documents, 5_000f) is { } area
                    && Level.Spawns.SpawnPlaces.Over(Level.Spawns.SpawnPlaces.ReadFrom(catalog), [area]) is { } places)
                {
                    documents.Add(places);
                    logger.LogInformation("Spawn places: {Count} around the levels.", places.Actors.Count);
                }
                var readMs = clock.Elapsed.TotalMilliseconds;

                var options = new LevelSceneOptions
                {
                    Lod = parse.GetValueForOption(lod),
                    MaxLods = Math.Max(1, parse.GetValueForOption(lods)),
                    TextureSize = parse.GetValueForOption(noTexture) ? 0 : Math.Max(16, parse.GetValueForOption(textureSize)),
                    IncludeInstances = !parse.GetValueForOption(noInstances),
                    IncludeLandscape = !parse.GetValueForOption(noLandscape),
                    LandscapeStep = Math.Max(1, parse.GetValueForOption(landscapeStep)),
                    Filter = parse.GetValueForOption(filter),
                    Ground = parse.GetValueForOption(ground),
                    SeaPlane = parse.GetValueForOption(noSea) ? false : parse.GetValueForOption(sea) ? true : null,
                    TerrainTextureSize = Math.Max(0, parse.GetValueForOption(groundTexture)),
                };
                var prepared = new LevelScenePreparer(catalog, logger).Prepare(documents, options, cancellationToken: ct);
                foreach (var warning in prepared.Warnings)
                {
                    logger.LogWarning("{Warning}", warning);
                }

                if (prepared.Placements.Count == 0 && prepared.Terrain.Count == 0)
                {
                    logger.LogError("No placed static meshes or landscape to render{Filter}.", options.Filter is { Length: > 0 } f ? $" matching '{f}'" : string.Empty);
                    return 2;
                }

                if (!TryCreateContext(width, height, logger, out var context, parse.GetValueForOption(glDebug)))
                {
                    return NoGlExitCode;
                }

                // All GL work happens synchronously on this thread; the PNG is written after the context is released.
                var culling = new RenderSettings
                {
                    ViewDistanceScale = parse.GetValueForOption(viewDistance), CullPixelSize = parse.GetValueForOption(cullPixels), Shadows = !parse.GetValueForOption(noShadows), TimeOfDay = parse.GetValueForOption(hour),
                    LodBias = parse.GetValueForOption(lodBias), ObjectDrawDistance = Math.Max(0f, parse.GetValueForOption(objectDistance)),
                };
                PreparedLevelScene? backdrop = null;
                var backdropLines = new List<string>();
                if (parse.GetValueForOption(island) || parse.GetValueForOption(farModels))
                {
                    // The app's whole-island backdrop (MapPageViewModel.OpenWholeIslandAsync): every landscape tile, step 16, 128² ground.
                    var tiles = index.Packages.Where(p => p.IsMap && p.Kind == WorldPackageKind.Landscape).Select(p => p.PackagePath).ToList();
                    backdrop = new LevelScenePreparer(catalog, logger).PrepareTerrain(tiles, new LevelSceneOptions { LandscapeStep = 16, TerrainTextureSize = 128 }, null, ct);
                    if (parse.GetValueForOption(farModels))
                    {
                        // ... and the game's far view of every place, as MapPageViewModel.ShowFarModels adds it.
                        var models = FarModelsScene.Read(catalog, index);
                        var far = FarModelsScene.Prepare(catalog, models, logger, null, ct);
                        backdrop = FarModelsScene.Combine(backdrop, far);
                        var detailed = documents.Select(d => d.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        backdropLines.Add(Invariant($"far models: {models.Count} ({models.Count(m => m.LevelPath is not null)} of a level, {models.Count(m => m.LevelPath is { } l && detailed.Contains(l))} hidden by the levels drawn in detail), {far.Meshes.Count} meshes, {far.MeshTriangles:N0} triangles at LOD 0, {far.LodTriangles:N0} over all LODs, {far.Textures.Count} textures, {far.MissingMeshes.Count} missing; prepared in {far.Elapsed.TotalMilliseconds:0} ms"));
                    }
                }

                var backdropDistance = 0f;
                if (sectors > 0 && backdrop is not null)
                {
                    var sector = parse.GetValueForOption(sectorSize) is > 0f and var given ? given : SectorSize(index);
                    if (sector <= 0f)
                    {
                        logger.LogError("The sector size could not be measured from the landscape tiles; give --sector-size.");
                        return 2;
                    }

                    backdropDistance = sectors * sector;
                    backdropLines.Add(Invariant($"render distance: {sectors} sector(s) x {sector / 100f:N0} m = {backdropDistance / 100f:N0} m for the backdrop's far models"));
                }

                var frame = RenderPreparedScene(context, prepared, width, height, parse.GetValueForOption(yaw), parse.GetValueForOption(pitch),
                    parse.GetValueForOption(dist), !parse.GetValueForOption(noGrid), orbitTarget, Math.Max(0, parse.GetValueForOption(frames)), culling, backdrop, backdropDistance,
                    Math.Max(1, parse.GetValueForOption(samples)), parse.GetValueForOption(glDebug),
                    parse.GetValueForOption(temporal), parse.GetValueForOption(framesOut), parse.GetValueForOption(orbitStep),
                    parse.GetValueForOption(terrainDetail) is { } detailQuality ? TerrainDetailSettings.For(detailQuality) : null);
                frame.Report.InsertRange(Math.Min(2, frame.Report.Count), backdropLines);

                var outFile = parse.GetValueForOption(output)!;
                await ImageExport.SavePngAsync(frame.Rgba, width, height, outFile.FullName, ct).ConfigureAwait(false);
                foreach (var document in documents)
                {
                    Console.Out.WriteLine(Invariant($"level: {document.PackagePath} — {document.Actors.Count} actors, {document.InstanceCount} instances"));
                }

                Console.Out.WriteLine(Invariant($"read {documents.Count} level(s) in {readMs:0} ms; prepared in {prepared.Elapsed.TotalMilliseconds:0} ms"));
                foreach (var line in frame.Report)
                {
                    Console.Out.WriteLine(line);
                }

                foreach (var m in prepared.MissingMeshes.Take(10))
                {
                    Console.Out.WriteLine($"missing mesh: {m}");
                }

                if (prepared.MissingMeshes.Count > 10)
                {
                    Console.Out.WriteLine($"... and {prepared.MissingMeshes.Count - 10} more missing meshes");
                }

                Console.Out.WriteLine(outFile.FullName);
                return frame.DriverProblems > 0 ? DriverProblemsExitCode : 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    /// <summary>Exit code of <c>render level --gl-debug</c> when the driver reported an error, undefined behaviour or a portability problem.</summary>
    public const int DriverProblemsExitCode = 4;

    /// <summary>
    /// Uploads the prepared scene, frames the camera on it and renders one frame (plus <paramref name="extraFrames"/> timed
    /// frames with the camera orbiting 0.5° per frame) into a target of <paramref name="samples"/> samples a pixel; disposes
    /// the context. <paramref name="backdropDistance"/> (cm, 0 = none) caps how far the backdrop's objects (the far models)
    /// are drawn. With <paramref name="driverMessages"/> every message of the driver's debug output goes into the report
    /// and <c>DriverProblems</c> counts the errors, undefined behaviour and portability problems among them.
    /// </summary>
    internal static (byte[] Rgba, List<string> Report, int DriverProblems) RenderPreparedScene(
        OffscreenGlContext context, PreparedLevelScene prepared, int width, int height, float yaw, float pitch, float? distance, bool showGrid,
        Vector3? orbitTargetUe = null, int extraFrames = 0, RenderSettings? culling = null, PreparedLevelScene? backdrop = null, float backdropDistance = 0f,
        int samples = 1, bool driverMessages = false, bool temporal = false, string? framesOut = null, float orbitStep = 0.5f,
        TerrainDetailSettings? terrainDetail = null)
    {
        using (context)
        {
            using var capture = driverMessages ? GlDebugOutput.Capture(context.Gl) : null;
            using var renderer = new SceneRenderer(context);
            if (culling is not null)
            {
                renderer.Settings = renderer.Settings with
                {
                    ViewDistanceScale = culling.ViewDistanceScale, CullPixelSize = culling.CullPixelSize, Shadows = culling.Shadows, TimeOfDay = culling.TimeOfDay,
                    LodBias = culling.LodBias, ObjectDrawDistance = culling.ObjectDrawDistance,
                };
            }

            var clock = Stopwatch.StartNew();
            using var level = LevelSceneUploader.Upload(renderer, prepared);
            var levelMs = clock.Elapsed.TotalMilliseconds;
            using var island = backdrop is null ? null : LevelSceneUploader.Upload(renderer, backdrop);
            island?.HideTerrainOf(prepared); // as the Map view: each tile (and each far model) drawn once
            var uploadMs = clock.Elapsed.TotalMilliseconds;

            var bounds = level.Bounds;
            var camera = new FlyCamera();
            var aspect = (float)width / height;
            var radius = MathF.Max(bounds.Extent.Length(), 1f);
            var dist = camera.Frame(bounds, aspect, yaw, pitch);
            var centre = orbitTargetUe is { } t ? UeToGl.Point(new FVector(t.X, t.Y, t.Z)) : bounds.Center;
            if (distance is { } d && d > 0f)
            {
                dist = d;
                camera.FitClipRange(d, radius + Vector3.Distance(centre, bounds.Center));
                camera.Orbit(centre, yaw, pitch, d);
            }
            else if (orbitTargetUe is not null)
            {
                camera.FitClipRange(dist, radius + Vector3.Distance(centre, bounds.Center));
                camera.Orbit(centre, yaw, pitch, dist);
            }

            var cell = MathF.Pow(10f, MathF.Floor(MathF.Log10(radius)) - 1f);
            renderer.Settings = renderer.Settings with
            {
                ShowGrid = showGrid,
                GridHeight = bounds.IsEmpty ? 0f : bounds.Min.Y,
                GridCellSize = cell,
                GridFadeDistance = MathF.Max(dist * 3f, radius * 6f),
            };

            if (island is not null)
            {
                camera.SetClipRange(10f, 3_000_000f); // as the viewport does with the island behind
            }

            // The Map view's finer ground near the camera, all of it sent before the first frame (the app sends it over frames).
            var rings = terrainDetail is null ? null : new TerrainDetailRings { Settings = terrainDetail };
            var detailLine = string.Empty;
            if (rings is not null)
            {
                var detailClock = Stopwatch.StartNew();
                while (rings.Update(renderer, level, camera.Position, double.PositiveInfinity))
                {
                    Thread.Sleep(2);
                }

                detailLine = Invariant($"terrain detail: {rings.FinerCount} component(s) drawn finer than loaded, {rings.FinerVertices:N0} vertices (budget {terrainDetail!.VertexBudget:N0}; full detail within {terrainDetail.FullRadiusCm / 100f:0} m, every second sample within {terrainDetail.HalfRadiusCm / 100f:0} m), built and sent in {detailClock.Elapsed.TotalMilliseconds:0} ms");
            }

            using var target = renderer.CreateTarget(width, height, samples);
            clock.Restart();
            var stats = Draw();
            var rgba = target.ReadColorRgba();
            var renderMs = clock.Elapsed.TotalMilliseconds;

            var ueMin = UeToGl.ToUePoint(bounds.Min);
            var ueMax = UeToGl.ToUePoint(bounds.Max);
            var report = new List<string>
            {
                $"renderer: {renderer.Info} (reverse-Z {(renderer.UsesReverseZ ? "on" : "off")})",
                Invariant($"meshes: {prepared.Meshes.Count} loaded ({prepared.MeshTriangles:N0} triangles at the finest LOD), {prepared.MissingMeshes.Count} missing, uploaded in {uploadMs:0} ms"),
                Invariant($"lods: {prepared.MeshesWithLods} meshes with more than one LOD, {prepared.LodTriangles:N0} triangles over all LODs; cull distance on {prepared.Placements.Count(p => p.CullDistance > 0f):N0} placements; view distance x{renderer.Settings.ViewDistanceScale:0.##}, cull below {renderer.Settings.CullPixelSize:0.#} px"),
                Invariant($"textures: {level.TextureCount} on the GPU ({prepared.Textures.Count} mesh textures for {prepared.Meshes.Values.Sum(m => m.MaterialTextures.Count)} material slots), about {level.TextureBytes / (1024.0 * 1024.0):N0} MB with mipmaps, anisotropic x{GpuTexture.MaxAnisotropy:0}"),
            };
            if (prepared.Terrain.Count > 0)
            {
                var albedoSize = prepared.Terrain.FirstOrDefault(t => t.Albedo is not null)?.Albedo?.Size;
                report.Add(Invariant($"terrain: {prepared.Terrain.Count} landscape component(s), {prepared.TerrainTriangles:N0} triangles, ground {prepared.Ground.ToString().ToLowerInvariant()}{(albedoSize is { } a ? Invariant($" ({a}² per component)") : string.Empty)} baked in {prepared.TerrainBakeTime.TotalMilliseconds:0} ms, sea {(prepared.SeaLevelCm is { } seaZ ? Invariant($"at Z {seaZ:0} cm") : "off")}"));
                var areas = LandscapeLayerReader.Summarize(prepared.Terrain.Where(t => t.Layers is not null).Select(t => t.Layers!));
                if (areas.Count > 0)
                {
                    report.Add("layers: " + string.Join(", ", areas.Where(a => a.WeightBlended).Take(8).Select(a => Invariant($"{a.Name} {a.AreaShare * 100:0.0}%"))));
                }

                if (prepared.LayerCatalog is { } layerCatalog)
                {
                    var fromTextures = layerCatalog.Layers.Values.Count(l => l.ColorSource == TerrainColorSource.Texture);
                    report.Add(Invariant($"layer colours: {fromTextures} from game textures, {prepared.MissingLayerTextures.Count} layer texture(s) not in the game files (fallback palette)"));
                }
            }

            report.Add(Invariant($"scene: {level.PlacedCount} of {prepared.RequestedPlacements} placements from {prepared.Documents.Sum(d => d.Actors.Count)} actors in {prepared.Documents.Count} level(s); bounds (UE cm) ({ueMin.X:0.#}, {ueMin.Y:0.#}, {ueMin.Z:0.#}) .. ({ueMax.X:0.#}, {ueMax.Y:0.#}, {ueMax.Z:0.#})"));
            report.Add(Invariant($"frame: {stats.Batches} draw(s), {stats.Instances} drawn, {stats.Culled} culled, {stats.Triangles:N0} triangles, {renderMs:0.0} ms incl. read-back, camera distance {dist:0}"));
            if (detailLine.Length > 0)
            {
                report.Add(detailLine);
            }

            if (island is not null && backdrop is not null)
            {
                // The app uploads the backdrop in one frame: this is the hitch when the island (or its far models) appears.
                var farShown = island.Scene.Nodes.Count(n => n.Visible && n.Tag is ScenePlacement p && p.Actor.ClassName == FarModelsScene.ActorClass);
                report.Add(Invariant($"backdrop: {backdrop.Terrain.Count} terrain components, {farShown} of {backdrop.Placements.Count} far models shown, {island.TextureCount} textures; uploaded in {uploadMs - levelMs:0} ms"));
            }

            report.AddRange(Census(prepared));
            if (extraFrames > 0)
            {
                var cpu = new double[extraFrames];
                var total = new double[extraFrames];
                using var stabilizer = temporal ? new TemporalStabilizer(renderer) : null;
                var shots = new List<byte[]>();
                var gen = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                var paused = GC.GetTotalPauseDuration();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < extraFrames; i++)
                {
                    camera.Orbit(centre, yaw + ((i + 1) * orbitStep), pitch, dist);
                    clock.Restart();
                    rings?.Update(renderer, level, camera.Position, 3.0); // as the Map view: part of the frame
                    stats = Draw();
                    stabilizer?.Apply(target, camera);
                    cpu[i] = clock.Elapsed.TotalMilliseconds;
                    context.Gl.Finish();
                    total[i] = clock.Elapsed.TotalMilliseconds;
                    if (framesOut is not null)
                    {
                        shots.Add(target.ReadColorRgba());
                    }
                }

                var perFrame = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)extraFrames;
                report.Add(Invariant($"frames: {extraFrames} extra (orbiting): CPU avg {cpu.Average():0.00} ms, p95 {Percentile95(cpu):0.00} ms; with glFinish avg {total.Average():0.00} ms, p95 {Percentile95(total):0.00} ms; last frame {stats.Batches} draws, {stats.Instances} drawn, {stats.Culled} culled, {stats.Triangles:N0} triangles"));
                report.Add(Invariant($"smoothness: {total.Count(t => t > 33.3)} frame(s) over 33 ms, {total.Count(t => t > 50.0)} over 50 ms, worst {total.Max():0.0} ms; {perFrame / (1024.0 * 1024.0):0.000} MB allocated per frame; GC gen0 {GC.CollectionCount(0) - gen[0]}, gen1 {GC.CollectionCount(1) - gen[1]}, gen2 {GC.CollectionCount(2) - gen[2]}, paused {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:0.0} ms"));
                if (framesOut is not null)
                {
                    Directory.CreateDirectory(framesOut);
                    for (var i = 0; i < shots.Count; i++)
                    {
                        ImageExport.SavePngAsync(shots[i], width, height, Path.Combine(framesOut, Invariant($"frame-{i:000}.png"))).GetAwaiter().GetResult();
                    }

                    report.Add(Invariant($"flicker: {FrameFlicker.Measure(shots, width, height):0.00000} over {shots.Count} frames ({(temporal ? "steadied" : "plain")}, {target.Samples} sample(s), {orbitStep:0.###} degrees a frame); frames in {framesOut}"));
                }
            }

            report.Add(Invariant($"target: {target.Samples} sample(s) a pixel"));
            if (capture is null)
            {
                return (rgba, report, 0);
            }

            // Errors and undefined behaviour first, each kind once with how often it came.
            var messages = capture.Messages;
            report.Add(Invariant($"driver: {messages.Count} debug message(s), {capture.Problems.Count} problem(s) (errors, undefined behaviour, portability)"));
            report.AddRange(messages.GroupBy(m => (m.Type, m.Severity, m.Id)).OrderByDescending(g => g.First().IsProblem).ThenByDescending(g => g.Count())
                .Select(g => Invariant($"driver: {g.Count(),6}x {g.First()}")));
            return (rgba, report, capture.Problems.Count);

            RenderStats Draw()
            {
                if (island is null)
                {
                    return renderer.Render(target, level.Scene, camera);
                }

                // The backdrop's far models stop at the render distance; the detailed levels keep the quality's object distance.
                var settings = renderer.Settings;
                renderer.Settings = backdropDistance > 0f ? settings with { ObjectDrawDistance = backdropDistance } : settings;
                var back = renderer.Render(target, island.Scene, camera);
                renderer.Settings = settings;
                var front = renderer.Render(target, level.Scene, camera, clear: false);
                return new RenderStats(back.Batches + front.Batches, back.Instances + front.Instances, back.Culled + front.Culled, back.Triangles + front.Triangles);
            }
        }
    }

    /// <summary>
    /// Where the triangles are: placements with a game cull distance (foliage, HISM) and without (drawn as far as the levels
    /// reach), their finest and coarsest LOD triangle sums, and the ten meshes that weigh most (LOD 0 triangles x placements).
    /// </summary>
    private static IEnumerable<string> Census(PreparedLevelScene prepared)
    {
        long Finest(string mesh) => prepared.Meshes.TryGetValue(mesh, out var m) ? m.Mesh.TriangleCount : 0;
        long Coarsest(string mesh) => prepared.Meshes.TryGetValue(mesh, out var m) ? m.Lods[^1].TriangleCount : 0;
        var culled = prepared.Placements.Where(p => p.CullDistance > 0f).ToList();
        var open = prepared.Placements.Where(p => p.CullDistance <= 0f).ToList();
        yield return Invariant($"census: {culled.Count:N0} placements with a game cull distance ({culled.Count(p => p.Instance is { StartCullDistance: > 0 }):N0} thinned from a start distance): {culled.Sum(p => Finest(p.MeshPath)):N0} triangles at LOD 0, {culled.Sum(p => Coarsest(p.MeshPath)):N0} at the last LOD; {open.Count:N0} without: {open.Sum(p => Finest(p.MeshPath)):N0} at LOD 0, {open.Sum(p => Coarsest(p.MeshPath)):N0} at the last LOD");
        var heaviest = prepared.Placements
            .GroupBy(p => p.MeshPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Mesh: g.Key, Count: g.Count(), Triangles: g.Count() * Finest(g.Key), Lods: prepared.Meshes.TryGetValue(g.Key, out var m) ? m.Lods.Count : 0,
                Near: g.Min(p => p.CullDistance), Far: g.Max(p => p.CullDistance)))
            .OrderByDescending(x => x.Triangles)
            .Take(10);
        foreach (var (mesh, count, triangles, lods, near, far) in heaviest)
        {
            var reach = far <= 0f ? "no cull distance" : near == far ? Invariant($"culled at {far / 100f:0} m") : Invariant($"culled at {near / 100f:0}-{far / 100f:0} m");
            yield return Invariant($"  heavy: {triangles:N0} triangles = {count:N0} x {triangles / Math.Max(1, count):N0}, {lods} LOD(s), {reach}: {mesh}");
        }
    }

    /// <summary>The size of a map cell (cm): the median extent of the cells' landscape tiles (needs <see cref="WorldIndex.WithTileInfo"/>); 0 when unknown.</summary>
    private static float SectorSize(WorldIndex world)
    {
        var sizes = world.Packages
            .Where(p => p.Kind == WorldPackageKind.Landscape && p.Cell is not null && p.Tile is { BoundsValid: true })
            .GroupBy(p => p.Cell!.Value)
            .Select(g => MathF.Max(g.Max(p => p.Tile!.BoundsMax.X) - g.Min(p => p.Tile!.BoundsMin.X), g.Max(p => p.Tile!.BoundsMax.Y) - g.Min(p => p.Tile!.BoundsMin.Y)))
            .Order()
            .ToList();
        return sizes.Count == 0 ? 0f : sizes[sizes.Count / 2];
    }

    private static double Percentile95(double[] samples)
    {
        var sorted = samples.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(0.95 * sorted.Length) - 1)];
    }

    /// <summary>Parses <c>X,Y,Z</c> (invariant culture, centimetres).</summary>
    internal static bool TryParseUePoint(string text, out Vector3 point)
    {
        point = default;
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        var values = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        point = new Vector3(values[0], values[1], values[2]);
        return true;
    }
}
