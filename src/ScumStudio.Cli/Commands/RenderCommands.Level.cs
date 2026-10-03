using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Landscape;
using ScumStudio.Core.Mathematics;
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
        var sublevel = new Argument<string[]>("sublevel", "One or more sublevel names (e.g. A_0_Outpost_Exterior A_0_Outpost_Ext_Saloon) or package paths; a cell name such as A_0 renders every sublevel of that cell (POI, TV base, misc and landscape tiles, like the app's Map page).")
        {
            Arity = ArgumentArity.OneOrMore,
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
        var command = new Command("level", "Render whole cooked sublevels (all placed static meshes, plus terrain for landscape tiles) to PNG.")
        {
            source, sublevel, aes, output, size, yaw, pitch, dist, lod, lods, viewDistance, cullPixels, noTexture, textureSize, noInstances, noLandscape, landscapeStep, filter, noGrid, frames,
            ground, sea, noSea, groundTexture, target, island,
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
                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var index = WorldIndex.FromCatalog(catalog);
                var packagePaths = new List<string>();
                foreach (var name in parse.GetValueForArgument(sublevel))
                {
                    if (MapCell.TryParse(name, out var cell))
                    {
                        // Same set the app's Map page loads for a cell (MapPageViewModel.CellPackages): POI, TV base and misc sublevels, then the landscape tiles.
                        var cellLevels = index.InCell(cell)
                            .Where(p => p.IsMap && p.Kind is WorldPackageKind.Poi or WorldPackageKind.Landscape or WorldPackageKind.TvBase or WorldPackageKind.Misc)
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

                if (!TryCreateContext(width, height, logger, out var context))
                {
                    return NoGlExitCode;
                }

                // All GL work happens synchronously on this thread; the PNG is written after the context is released.
                var culling = new RenderSettings { ViewDistanceScale = parse.GetValueForOption(viewDistance), CullPixelSize = parse.GetValueForOption(cullPixels) };
                PreparedLevelScene? backdrop = null;
                if (parse.GetValueForOption(island))
                {
                    // The app's whole-island backdrop (MapPageViewModel.OpenWholeIslandAsync): every landscape tile, step 16, 128² ground.
                    var tiles = index.Packages.Where(p => p.IsMap && p.Kind == WorldPackageKind.Landscape).Select(p => p.PackagePath).ToList();
                    backdrop = new LevelScenePreparer(catalog, logger).PrepareTerrain(tiles, new LevelSceneOptions { LandscapeStep = 16, TerrainTextureSize = 128 }, null, ct);
                }

                var frame = RenderPreparedScene(context, prepared, width, height, parse.GetValueForOption(yaw), parse.GetValueForOption(pitch),
                    parse.GetValueForOption(dist), !parse.GetValueForOption(noGrid), orbitTarget, Math.Max(0, parse.GetValueForOption(frames)), culling, backdrop);

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
                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    /// <summary>
    /// Uploads the prepared scene, frames the camera on it and renders one frame (plus <paramref name="extraFrames"/> timed
    /// frames with the camera orbiting 0.5° per frame); disposes the context.
    /// </summary>
    internal static (byte[] Rgba, List<string> Report) RenderPreparedScene(
        OffscreenGlContext context, PreparedLevelScene prepared, int width, int height, float yaw, float pitch, float? distance, bool showGrid,
        Vector3? orbitTargetUe = null, int extraFrames = 0, RenderSettings? culling = null, PreparedLevelScene? backdrop = null)
    {
        using (context)
        {
            using var renderer = new SceneRenderer(context);
            if (culling is not null)
            {
                renderer.Settings = renderer.Settings with { ViewDistanceScale = culling.ViewDistanceScale, CullPixelSize = culling.CullPixelSize };
            }

            var clock = Stopwatch.StartNew();
            using var level = LevelSceneUploader.Upload(renderer, prepared);
            using var island = backdrop is null ? null : LevelSceneUploader.Upload(renderer, backdrop);
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

            using var target = renderer.CreateTarget(width, height);
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
            if (extraFrames > 0)
            {
                var cpu = new double[extraFrames];
                var total = new double[extraFrames];
                for (var i = 0; i < extraFrames; i++)
                {
                    camera.Orbit(centre, yaw + ((i + 1) * 0.5f), pitch, dist);
                    clock.Restart();
                    stats = Draw();
                    cpu[i] = clock.Elapsed.TotalMilliseconds;
                    context.Gl.Finish();
                    total[i] = clock.Elapsed.TotalMilliseconds;
                }

                report.Add(Invariant($"frames: {extraFrames} extra (orbiting): CPU avg {cpu.Average():0.00} ms, p95 {Percentile95(cpu):0.00} ms; with glFinish avg {total.Average():0.00} ms, p95 {Percentile95(total):0.00} ms; last frame {stats.Batches} draws, {stats.Instances} drawn, {stats.Culled} culled, {stats.Triangles:N0} triangles"));
            }

            return (rgba, report);

            RenderStats Draw()
            {
                if (island is null)
                {
                    return renderer.Render(target, level.Scene, camera);
                }

                var back = renderer.Render(target, island.Scene, camera);
                var front = renderer.Render(target, level.Scene, camera, clear: false);
                return new RenderStats(back.Batches + front.Batches, back.Instances + front.Instances, back.Culled + front.Culled, back.Triangles + front.Triangles);
            }
        }
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
