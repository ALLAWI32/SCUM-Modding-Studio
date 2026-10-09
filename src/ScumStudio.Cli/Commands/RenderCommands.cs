using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Meshes;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Pak;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Rendering.Import;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.Snapshots;
using ScumStudio.Viewport;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio render mesh|obj|test-scene</c>: offscreen OpenGL 4.3 renders to PNG (ScumStudio.Rendering).
/// Needs a GL 4.3 capable driver and a display (on Linux servers run under <c>xvfb-run</c>).
/// </summary>
internal sealed partial class RenderCommands : ICommandModule
{
    /// <summary>Exit code when no OpenGL 4.3 context can be created.</summary>
    public const int NoGlExitCode = 3;

    /// <inheritdoc />
    public Command Build()
    {
        var render = new Command("render", "Render meshes, a whole sublevel or the procedural test scene to PNG with the offscreen OpenGL renderer.");
        render.AddCommand(BuildMesh());
        render.AddCommand(BuildLevel());
        render.AddCommand(BuildObj());
        render.AddCommand(BuildTestScene());
        return render;
    }

    private static Option<FileInfo> OutputOption() =>
        new(["--output", "-o"], "Output .png file.") { IsRequired = true };

    private static Option<string> SizeOption() =>
        new("--size", () => "1280x720", "Image size WIDTHxHEIGHT in pixels.");

    private static Option<float> YawOption(float defaultValue) =>
        new("--yaw", () => defaultValue, "Camera heading in degrees (UE yaw of the view direction).");

    private static Option<float> PitchOption(float defaultValue) =>
        new("--pitch", () => defaultValue, "Camera pitch in degrees (negative looks down).");

    private static Option<float?> DistOption() =>
        new("--dist", "Camera distance from the mesh centre in centimetres (default: fit the bounds).");

    private static Option<bool> NoGridOption() => new("--no-grid", "Do not draw the ground grid.");

    private static Command BuildMesh()
    {
        var source = new Argument<string>("source", "Paks folder, single .pak, or loose folder containing SCUM/Content (or Content).");
        var objectPath = new Argument<string>("objectPath", "StaticMesh/SkeletalMesh object or package path, e.g. /Game/ConZ_Files/.../SM_Y.");
        var aes = new Option<string?>(["--aes", "-a"], $"AES-256 key for encrypted stock paks. Prefer the {AesKeyText.EnvironmentVariable} environment variable.");
        var output = OutputOption();
        var size = SizeOption();
        var yaw = YawOption(-135f);
        var pitch = PitchOption(-20f);
        var dist = DistOption();
        var lod = new Option<int>("--lod", () => 0, "LOD index to render.");
        var noTexture = new Option<bool>("--no-texture", "Flat grey: no textures.");
        var textureSize = new Option<int>("--texture-size", () => 8192, "Largest texture edge in pixels (the default takes the game's full size, as the Assets page does).");
        var noGrid = NoGridOption();
        var command = new Command("mesh", "Render a cooked StaticMesh or SkeletalMesh as the Assets page shows it (every material, normal maps) to PNG.")
        {
            source, objectPath, aes, output, size, yaw, pitch, dist, lod, noTexture, textureSize, noGrid,
        };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<RenderCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                var (width, height) = ParseSize(parse.GetValueForOption(size)!);
                var key = parse.GetValueForOption(aes);
                key = string.IsNullOrWhiteSpace(key) ? AesKeyText.FromEnvironmentOrStore() : key;
                using var catalog = AssetCatalog.Open(parse.GetValueForArgument(source), new AssetCatalogOptions
                {
                    AesKey = string.IsNullOrWhiteSpace(key) ? null : AesKeyText.Normalize(key),
                    Logger = logger,
                });
                var obj = catalog.LoadObject(parse.GetValueForArgument(objectPath));
                if (!MeshExtractor.IsMesh(obj))
                {
                    logger.LogError("{Name} is a {Type}, not a StaticMesh or SkeletalMesh.", obj.Name, obj.ExportType);
                    return 2;
                }

                // The Assets page's 3D preview: a part per material with its colour texture, normal map and roughness, at
                // the game's full texture size (block-compressed on the GPU), under the same studio light.
                var lodIndex = parse.GetValueForOption(lod);
                var loader = new MeshPreviewLoader(catalog, logger)
                {
                    TextureSize = parse.GetValueForOption(noTexture) ? 0 : Math.Max(16, parse.GetValueForOption(textureSize)),
                    ForGpu = true,
                    LodOf = lodIndex > 0 ? lods => Math.Min(lodIndex, lods.Count - 1) : null,
                };
                var model = loader.LoadMesh(parse.GetValueForArgument(objectPath));
                if (!TryCreateContext(width, height, logger, out var context))
                {
                    return NoGlExitCode;
                }

                byte[] rgba;
                float distance;
                using (context)
                {
                    using var renderer = new SceneRenderer(context);
                    using var scene = PreviewScene.Upload(renderer, model);
                    var bounds = scene.Bounds;
                    var radius = MathF.Max(bounds.Extent.Length(), 1f);
                    var camera = new FlyCamera();
                    distance = camera.Frame(bounds, (float)width / height, parse.GetValueForOption(yaw), parse.GetValueForOption(pitch));
                    if (parse.GetValueForOption(dist) is { } d && d > 0f)
                    {
                        distance = d;
                        camera.FitClipRange(d, radius);
                        camera.Orbit(bounds.Center, parse.GetValueForOption(yaw), parse.GetValueForOption(pitch), d);
                    }

                    renderer.Settings = renderer.Settings with
                    {
                        SkyColor = new System.Numerics.Vector3(0.5f, 0.52f, 0.56f),
                        GroundColor = new System.Numerics.Vector3(0.2f, 0.19f, 0.17f),
                        LightColor = new System.Numerics.Vector3(0.72f, 0.7f, 0.64f),
                        LightDirection = System.Numerics.Vector3.Normalize(camera.Forward - (camera.Up * 0.8f) + (camera.Right * 0.35f)),
                        ShowGrid = !parse.GetValueForOption(noGrid),
                        GridHeight = bounds.IsEmpty ? 0f : bounds.Min.Y,
                        GridCellSize = MathF.Pow(10f, MathF.Floor(MathF.Log10(radius)) - 1f),
                        GridFadeDistance = MathF.Max(distance * 3f, radius * 6f),
                    };
                    using var target = renderer.CreateTarget(width, height);
                    renderer.Render(target, scene.Scene, camera);
                    rgba = target.ReadColorRgba();
                }

                var file = parse.GetValueForOption(output)!;
                await ImageExport.SavePngAsync(rgba, width, height, file.FullName, ct).ConfigureAwait(false);
                var normals = model.Parts.Count(p => p.NormalPath is not null);
                Console.Out.WriteLine(Invariant($"{model.Name}: {model.Parts.Count} part(s), {model.Triangles:N0} triangles, {model.Textures.Count} texture(s) ({string.Join(", ", model.Textures.Values.Select(t => $"{t.Width}x{t.Height}").Distinct())}), {normals} part(s) with a normal map, distance {distance:0.#}"));
                Console.Out.WriteLine(file.FullName);
                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildObj()
    {
        var file = new Argument<FileInfo>("file", "Wavefront .obj file (right-handed, Y up; e.g. from 'asset mesh-export').");
        var output = OutputOption();
        var size = SizeOption();
        var yaw = YawOption(-135f);
        var pitch = PitchOption(-20f);
        var dist = DistOption();
        var ueAxes = new Option<bool>("--ue-axes", "The file is in UE axes (Z up, centimetres), e.g. exported with 'asset mesh-export --ue-axes'.");
        var scale = new Option<float>("--scale", () => 1f, "Multiply positions (e.g. 100 for an OBJ in metres, so camera distances are in centimetres).");
        var texture = new Option<FileInfo?>("--texture", "Optional albedo image (PNG/JPEG/...) applied to the whole mesh.");
        var noGrid = NoGridOption();
        var command = new Command("obj", "Render a Wavefront OBJ mesh to PNG (fallback input that needs no game files).")
        {
            file, output, size, yaw, pitch, dist, ueAxes, scale, texture, noGrid,
        };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<RenderCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                var (width, height) = ParseSize(parse.GetValueForOption(size)!);
                var input = parse.GetValueForArgument(file);
                var mesh = await ObjReader.LoadAsync(input.FullName, cancellationToken: ct).ConfigureAwait(false);
                var s = parse.GetValueForOption(scale);
                if (s != 1f)
                {
                    mesh = MeshData.Create(mesh.Name, mesh.Positions.Select(p => p * s).ToArray(), mesh.Indices, mesh.Normals, mesh.Uv0, mesh.Sections);
                }

                (int, int, byte[], bool)? tex = null;
                if (parse.GetValueForOption(texture) is { } texFile)
                {
                    var (rgba, tw, th) = await ImageExport.LoadRgbaAsync(texFile.FullName, ct).ConfigureAwait(false);
                    tex = (tw, th, rgba, true);
                }

                var options = new SnapshotOptions
                {
                    Width = width,
                    Height = height,
                    Yaw = parse.GetValueForOption(yaw),
                    Pitch = parse.GetValueForOption(pitch),
                    Distance = parse.GetValueForOption(dist),
                    ShowGrid = !parse.GetValueForOption(noGrid),
                    Tint = tex is null ? new SnapshotOptions().Tint : System.Numerics.Vector4.One,
                };
                var space = parse.GetValueForOption(ueAxes) ? MeshSpace.Unreal : MeshSpace.Gl;
                return await RenderSnapshotAsync(mesh, space, options, tex, parse.GetValueForOption(output)!, logger, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildTestScene()
    {
        var output = OutputOption();
        var size = SizeOption();
        var columns = new Option<int>("--columns", () => 64, "Cubes along X.");
        var rows = new Option<int>("--rows", () => 64, "Cubes along Z.");
        var yaw = YawOption(45f);
        var pitch = PitchOption(-35f);
        var noGrid = NoGridOption();
        var noSelect = new Option<bool>("--no-select", "Do not highlight the cube picked at the image centre.");
        var command = new Command("test-scene", "Render a procedural grid of cube instances (instancing, culling, picking, selection) and print the id picked at the image centre.")
        {
            output, size, columns, rows, yaw, pitch, noGrid, noSelect,
        };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<RenderCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                var (width, height) = ParseSize(parse.GetValueForOption(size)!);
                if (!TryCreateContext(width, height, logger, out var context))
                {
                    return NoGlExitCode;
                }

                // All GL work happens synchronously on this thread; the PNG is written after the context is released
                // (an await may resume on another thread, where the context is not current).
                var frame = RenderTestScene(
                    context,
                    width,
                    height,
                    parse.GetValueForOption(columns),
                    parse.GetValueForOption(rows),
                    parse.GetValueForOption(yaw),
                    parse.GetValueForOption(pitch),
                    !parse.GetValueForOption(noGrid),
                    !parse.GetValueForOption(noSelect));
                var outFile = parse.GetValueForOption(output)!;
                await ImageExport.SavePngAsync(frame.Rgba, width, height, outFile.FullName, ct).ConfigureAwait(false);
                foreach (var line in frame.Report)
                {
                    Console.Out.WriteLine(line);
                }

                Console.Out.WriteLine(outFile.FullName);
                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    /// <summary>Renders the cube grid (pick at the centre, highlight, render, read back) and disposes the context.</summary>
    private static (byte[] Rgba, List<string> Report) RenderTestScene(
        OffscreenGlContext context, int width, int height, int columns, int rows, float yaw, float pitch, bool showGrid, bool select)
    {
        using (context)
        {
            using var renderer = new SceneRenderer(context);
            renderer.Settings = renderer.Settings with { ShowGrid = showGrid };
            var cube = renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
            var grid = CubeGridScene.Build(cube, columns, rows);
            var camera = new FlyCamera();
            grid.FrameCamera(camera, yaw, pitch);
            using var target = renderer.CreateTarget(width, height);

            var clock = Stopwatch.StartNew();
            var (cx, cy) = (width / 2, height / 2);
            var pick = renderer.Pick(target, grid.Scene, camera, cx, cy);
            var pickMs = clock.Elapsed.TotalMilliseconds;
            if (pick is not null && select)
            {
                pick.Node.Selected = true;
            }

            clock.Restart();
            var stats = renderer.Render(target, grid.Scene, camera);
            var rgba = target.ReadColorRgba();
            var renderMs = clock.Elapsed.TotalMilliseconds;

            var report = new List<string>
            {
                $"renderer: {renderer.Info} (reverse-Z {(renderer.UsesReverseZ ? "on" : "off")})",
                $"scene: {grid.Count} instances of '{cube.Name}' ({grid.Columns}x{grid.Rows})",
                Invariant($"frame: {stats.Batches} instanced draw(s), {stats.Instances} drawn, {stats.Culled} culled, {stats.Triangles} triangles, {renderMs:0.0} ms incl. read-back"),
            };
            if (pick is null)
            {
                report.Add(Invariant($"picked id at centre ({cx}, {cy}): none ({pickMs:0.0} ms)"));
            }
            else
            {
                var id = pick.InstanceId - 1;
                report.Add(Invariant($"picked id at centre ({cx}, {cy}): {pick.InstanceId} (mesh {pick.MeshId}, {pick.Node.Name}, column {id % grid.Columns}, row {id / grid.Columns}), world ({pick.WorldPosition.X:0.#}, {pick.WorldPosition.Y:0.#}, {pick.WorldPosition.Z:0.#}), {pickMs:0.0} ms"));
            }

            return (rgba, report);
        }
    }

    private static async Task<int> RenderSnapshotAsync(
        MeshData mesh,
        MeshSpace space,
        SnapshotOptions options,
        (int Width, int Height, byte[] Rgba, bool Srgb)? texture,
        FileInfo output,
        ILogger logger,
        CancellationToken ct)
    {
        if (!TryCreateContext(options.Width, options.Height, logger, out var context))
        {
            return NoGlExitCode;
        }

        SnapshotResult result;
        using (context)
        {
            result = MeshSnapshot.Render(context, mesh, space, options, texture);
        }

        await ImageExport.SavePngAsync(result.Rgba, result.Width, result.Height, output.FullName, ct).ConfigureAwait(false);
        var b = result.Bounds;
        Console.Out.WriteLine(Invariant($"{mesh.Name}: {mesh.VertexCount} vertices, {mesh.TriangleCount} triangles, texture {(texture is null ? "none" : $"{texture.Value.Width}x{texture.Value.Height}")}, bounds (GL) ({b.Min.X:0.#}, {b.Min.Y:0.#}, {b.Min.Z:0.#}) .. ({b.Max.X:0.#}, {b.Max.Y:0.#}, {b.Max.Z:0.#}), distance {result.Distance:0.#}"));
        Console.Out.WriteLine(output.FullName);
        return 0;
    }

    internal static bool TryCreateContext(int width, int height, ILogger logger, out OffscreenGlContext context)
    {
        if (OffscreenGlContext.TryCreate(width, height, out var created, out var reason))
        {
            context = created!;
            logger.LogDebug("OpenGL: {Info}", created!.Info);
            return true;
        }

        logger.LogError("{Reason} On Linux without a display run the command under xvfb-run.", reason);
        context = null!;
        return false;
    }

    /// <summary>Parses <c>WIDTHxHEIGHT</c> (e.g. <c>1280x720</c>).</summary>
    internal static (int Width, int Height) ParseSize(string text)
    {
        var parts = text.Trim().Split(new[] { 'x', 'X', '*' }, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            && w is > 0 and <= 16384 && h is > 0 and <= 16384)
        {
            return (w, h);
        }

        throw new ArgumentException($"Invalid --size '{text}': expected WIDTHxHEIGHT, e.g. 1280x720.");
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

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
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            logger.LogError("{Type}: {Message}", ex.GetType().Name, ex.Message);
            return 1;
        }
    }
}
