using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Export;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Meshes;
using ScumStudio.Assets.Textures;
using ScumStudio.Pak;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio asset ls|info|mesh-export|tex-export</c>: browse and export cooked assets through CUE4Parse
/// (ScumStudio.Assets). <c>&lt;source&gt;</c> is a Paks folder, a single <c>.pak</c>, or a folder of loose extracted files
/// (<c>SCUM/Content/...</c> layout; the folder may be the project root, its parent, or <c>Content</c> itself).
/// Encrypted stock paks need <c>--aes</c> or the <c>SCUMSTUDIO_AES_KEY</c> environment variable; the key is never printed.
/// </summary>
internal sealed partial class AssetCommands : ICommandModule
{
    /// <inheritdoc />
    public Command Build()
    {
        var asset = new Command("asset", "Browse and export cooked assets (meshes, textures, materials) via CUE4Parse.");
        asset.AddCommand(BuildList());
        asset.AddCommand(BuildInfo());
        asset.AddCommand(BuildMeshExport());
        asset.AddCommand(BuildTextureExport());
        asset.AddCommand(BuildLandscapeLayers());
        return asset;
    }

    private static Argument<string> SourceArgument() =>
        new("source", "Paks folder, single .pak, or loose folder containing SCUM/Content (or Content).");

    private static Argument<string> ObjectArgument() =>
        new("objectPath", "Object or package path, e.g. /Game/ConZ_Files/Models/X/SM_Y (.SM_Y optional) or SCUM/Content/.../SM_Y.uasset.");

    private static Option<string?> AesOption() =>
        new(["--aes", "-a"], $"AES-256 key (64 hex chars) for encrypted stock paks. Prefer the {AesKeyText.EnvironmentVariable} environment variable: command lines can end up in shell history.");

    private static Command BuildList()
    {
        var source = SourceArgument();
        var aes = AesOption();
        var filter = new Option<string?>(["--filter", "-f"], "Only packages whose path matches (case-insensitive substring; * and ? are wildcards).");
        var cls = new Option<string?>(["--class", "-c"], "Only packages whose main export has this class (e.g. StaticMesh, SkeletalMesh, Texture2D, World).");
        var noClass = new Option<bool>("--no-class", "Do not read package headers (faster on full game paks); prints paths only. Ignored with --class.");
        var command = new Command("ls", "List packages as '<Class>\\t<ObjectPath>' (sorted by path).") { source, aes, filter, cls, noClass };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<AssetCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                using var catalog = OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var className = parse.GetValueForOption(cls);
                var resolve = !string.IsNullOrWhiteSpace(className) || !parse.GetValueForOption(noClass);
                var index = await catalog.BuildIndexAsync(resolve, null, ct).ConfigureAwait(false);
                var match = BuildMatcher(parse.GetValueForOption(filter));
                var count = 0;
                foreach (var entry in index.Entries)
                {
                    if (!match(entry.PackagePath) && !match(entry.FilePath))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(className) && (entry.ClassName is null || !PackageIndex.ClassNameMatches(entry.ClassName, className)))
                    {
                        continue;
                    }

                    Console.Out.WriteLine(resolve ? $"{entry.ClassName ?? "?"}\t{entry.ObjectPath}" : entry.ObjectPath);
                    count++;
                }

                logger.LogInformation("{Count} of {Total} packages ({Source}).", count, index.Count, catalog.DisplayName);
                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildInfo()
    {
        var source = SourceArgument();
        var objectPath = ObjectArgument();
        var aes = AesOption();
        var json = new Option<bool>("--json", "Dump the object as JSON (CUE4Parse serialisation) instead of the summary.");
        var command = new Command("info", "Print a package's exports and a summary of the object (mesh LODs/bounds, texture mips, material parameters).")
        {
            source, objectPath, aes, json,
        };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<AssetCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = RunGuarded(logger, () =>
            {
                using var catalog = OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var path = parse.GetValueForArgument(objectPath);
                if (!catalog.PackageExists(path))
                {
                    logger.LogError("Package not found: {Path}", path);
                    return 2;
                }

                var obj = catalog.LoadObject(path);
                if (parse.GetValueForOption(json))
                {
                    Console.Out.WriteLine(AssetJson.Serialize(obj));
                    return 0;
                }

                var o = Console.Out;
                o.WriteLine($"package: {AssetPaths.SplitObjectPath(path).PackagePath}");
                o.WriteLine("exports:");
                foreach (var e in catalog.GetExports(path))
                {
                    o.WriteLine($"  [{e.Index}] {e.ClassName} {e.Name}{(e.OuterName is null ? string.Empty : " (outer " + e.OuterName + ")")} {e.SerialSize.ToString("N0", CultureInfo.InvariantCulture)} bytes");
                }

                o.WriteLine($"object: {obj.Name} ({obj.ExportType})");
                switch (obj)
                {
                    case var mesh when MeshExtractor.IsMesh(mesh):
                        PrintMesh(MeshExtractor.Describe(mesh));
                        if (mesh is CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh staticMesh)
                        {
                            var simple = MeshCollision.Read(staticMesh);
                            PrintCollision(simple);
                            if (staticMesh.RenderData?.LODs?.FirstOrDefault(l => !l.SkipLod)?.PositionVertexBuffer?.Verts.Length > 0)
                            {
                                var boxes = Level.Model.PieceCollision.MeshBoxes(MeshExtractor.ExtractStaticMesh(staticMesh, 0), simple);
                                Console.Out.WriteLine($"collision when bent: {boxes.Count} box(es), the largest:");
                                foreach (var b in boxes.OrderByDescending(b => b.Size.X * b.Size.Y).Take(6))
                                {
                                    Console.Out.WriteLine($"  at {Fmt(b.Center)} size {Fmt(b.Size)}");
                                }
                            }
                            else
                            {
                                Console.Out.WriteLine("collision when bent: from the client's mesh (this cook has no render geometry)");
                            }
                        }
                        break;
                    case UTexture2D texture:
                        PrintTexture(TextureDecoder.Describe(texture));
                        break;
                    case UMaterialInterface material:
                        PrintMaterial(new MaterialInspector(catalog).Inspect(material));
                        break;
                    default:
                        o.WriteLine("properties:");
                        foreach (var p in obj.Properties)
                        {
                            o.WriteLine($"  {p.Name.Text} ({p.Tag?.GetType().Name ?? "?"}): {Truncate(p.Tag?.ToString() ?? string.Empty, 160)}");
                        }

                        break;
                }

                return 0;
            });
        });
        return command;
    }

    private static Command BuildMeshExport()
    {
        var source = SourceArgument();
        var objectPath = ObjectArgument();
        var aes = AesOption();
        var output = new Option<FileInfo>(["--output", "-o"], "Output file: .gltf (writes .gltf + .bin) or .obj (writes .obj + .mtl).") { IsRequired = true };
        var lod = new Option<int>("--lod", () => 0, "LOD index to export.");
        var scale = new Option<float>("--scale", () => 0.01f, "Position scale (0.01 = centimetres to metres).");
        var ueAxes = new Option<bool>("--ue-axes", "Keep UE axes (Z up, left-handed) instead of converting to Y up; triangle order is then reversed.");
        var command = new Command("mesh-export", "Export a StaticMesh or SkeletalMesh LOD to glTF 2.0 or OBJ.") { source, objectPath, aes, output, lod, scale, ueAxes };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<AssetCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                var outFile = parse.GetValueForOption(output)!;
                var ext = outFile.Extension.ToLowerInvariant();
                if (ext is not ".gltf" and not ".obj")
                {
                    logger.LogError("Output must end in .gltf or .obj (got '{Ext}').", outFile.Extension);
                    return 2;
                }

                using var catalog = OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var obj = catalog.LoadObject(parse.GetValueForArgument(objectPath));
                if (!MeshExtractor.IsMesh(obj))
                {
                    logger.LogError("{Name} is a {Type}, not a StaticMesh or SkeletalMesh.", obj.Name, obj.ExportType);
                    return 2;
                }

                var mesh = MeshExtractor.Extract(obj, parse.GetValueForOption(lod));
                var options = new MeshExportOptions { Scale = parse.GetValueForOption(scale), ConvertToYUp = !parse.GetValueForOption(ueAxes) };
                var files = ext == ".gltf"
                    ? await GltfExporter.SaveAsync(mesh, outFile.FullName, options, ct).ConfigureAwait(false)
                    : await ObjExporter.SaveAsync(mesh, outFile.FullName, options, ct).ConfigureAwait(false);
                Console.Out.WriteLine($"{mesh.Name}: {mesh.VertexCount} vertices, {mesh.TriangleCount} triangles, {mesh.Sections.Length} sections, "
                                      + $"bounds (cm) {Fmt(mesh.Bounds.Min)} .. {Fmt(mesh.Bounds.Max)}");
                foreach (var f in files)
                {
                    Console.Out.WriteLine(f);
                }

                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildTextureExport()
    {
        var source = SourceArgument();
        var objectPath = ObjectArgument();
        var aes = AesOption();
        var output = new Option<FileInfo>(["--output", "-o"], "Output .png file.") { IsRequired = true };
        var maxSize = new Option<int>("--max-size", () => 0, "Largest mip with width and height <= this (0 = full size).");
        var command = new Command("tex-export", "Decode a Texture2D (BC1/3/4/5/7, BGRA8, G8, ...) and save it as PNG.") { source, objectPath, aes, output, maxSize };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<AssetCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                using var catalog = OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var obj = catalog.LoadObject(parse.GetValueForArgument(objectPath));
                if (obj is not UTexture2D texture)
                {
                    logger.LogError("{Name} is a {Type}, not a Texture2D.", obj.Name, obj.ExportType);
                    return 2;
                }

                var image = TextureDecoder.Decode(texture, parse.GetValueForOption(maxSize));
                var outFile = parse.GetValueForOption(output)!;
                await PngWriter.SaveAsync(image, outFile.FullName, ct).ConfigureAwait(false);
                Console.Out.WriteLine($"{image.Name}: {image.PixelFormat} {image.SourceWidth}x{image.SourceHeight}, mip {image.MipIndex} = {image.Width}x{image.Height} -> {outFile.FullName}");
                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static void PrintMesh(MeshAssetInfo info)
    {
        var o = Console.Out;
        o.WriteLine($"mesh: {info.Kind}, bounds (cm) {Fmt(info.Bounds.Min)} .. {Fmt(info.Bounds.Max)}, size {Fmt(info.Bounds.Size)}"
                    + (info.Kind == MeshAssetKind.Skeletal ? $", {info.BoneCount} bones" : string.Empty));
        foreach (var m in info.Materials)
        {
            o.WriteLine($"  material[{m.Index}] {(m.SlotName.Length > 0 ? m.SlotName : "-")}: {(m.MaterialPath.Length > 0 ? m.MaterialPath : "(none)")}");
        }

        foreach (var l in info.Lods)
        {
            o.WriteLine($"  LOD{l.Index}: {l.VertexCount} vertices, {l.TriangleCount} triangles, {l.SectionCount} sections, "
                        + $"{(l.Uses32BitIndices ? 32 : 16)}-bit indices, {l.TexCoordCount} UV set(s){(l.IsStripped ? ", stripped" : string.Empty)}");
        }
    }

    private static void PrintCollision(MeshCollisionInfo? info)
    {
        var o = Console.Out;
        if (info is null)
        {
            o.WriteLine("collision: none (no body setup)");
            return;
        }

        o.WriteLine($"collision: {info.BoxElements} box(es), {info.Convex} convex, {info.Spheres} sphere(s), {info.Capsules} capsule(s)"
                    + (info.TraceFlag.Length > 0 ? $", trace {info.TraceFlag}" : string.Empty)
                    + FormattableString.Invariant($", body guid {info.BodySetupGuid.A:x8}{info.BodySetupGuid.B:x8}{info.BodySetupGuid.C:x8}{info.BodySetupGuid.D:x8}"));
        foreach (var b in info.Boxes.Take(40))
        {
            o.WriteLine($"  box at {Fmt(b.Center)} size {Fmt(b.Size)} rot ({b.Rotation.Pitch:0.#}, {b.Rotation.Yaw:0.#}, {b.Rotation.Roll:0.#})");
        }
    }

    private static string Fmt(Core.Mathematics.FVector v) => FormattableString.Invariant($"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})");

    private static void PrintTexture(TextureInfo info)
    {
        var o = Console.Out;
        o.WriteLine($"texture: {info.Width}x{info.Height} {info.PixelFormat}, {info.CompressionSettings}, sRGB {(info.IsSrgb ? "yes" : "no")}, {info.Mips.Count} mips");
        foreach (var m in info.Mips)
        {
            o.WriteLine($"  mip{m.Index}: {m.Width}x{m.Height}{(m.HasData ? string.Empty : " (no data)")}");
        }
    }

    private static void PrintMaterial(MaterialInfo info)
    {
        var o = Console.Out;
        o.WriteLine($"material: {info.ObjectPath}");
        foreach (var p in info.ParentChain)
        {
            o.WriteLine($"  parent: {p}");
        }

        o.WriteLine($"  base colour texture: {info.BaseColorTexture ?? "(none)"}");
        o.WriteLine($"  used with spline meshes: {(info.UsedWithSplineMeshes ? "yes" : "no")}");
        o.WriteLine($"  tint: {(info.TintColor is { } tint ? $"{tint.X:0.###} {tint.Y:0.###} {tint.Z:0.###} {tint.W:0.###}" : "(none)")}");
        foreach (var t in info.Textures)
        {
            o.WriteLine($"  texture '{t.Name}' = {t.TexturePath} ({t.Source})");
        }

        foreach (var v in info.Vectors)
        {
            o.WriteLine($"  vector '{v.Name}' = {v.Value.X:0.###} {v.Value.Y:0.###} {v.Value.Z:0.###} {v.Value.W:0.###} ({v.Source})");
        }

        foreach (var s in info.Scalars)
        {
            o.WriteLine($"  scalar '{s.Name}' = {s.Value.ToString("0.####", CultureInfo.InvariantCulture)} ({s.Source})");
        }

        foreach (var r in info.ReferencedTextures)
        {
            o.WriteLine($"  referenced: {r}");
        }
    }

    private static string Fmt(System.Numerics.Vector3 v) =>
        string.Create(CultureInfo.InvariantCulture, $"({v.X:0.##}, {v.Y:0.##}, {v.Z:0.##})");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    /// <summary>Opens the catalog with the key from --aes or the environment (validated first, never echoed).</summary>
    private static AssetCatalog OpenCatalog(string source, string? aes, ILogger logger)
    {
        var key = string.IsNullOrWhiteSpace(aes) ? AesKeyText.FromEnvironmentOrStore() : aes;
        return AssetCatalog.Open(source, new AssetCatalogOptions
        {
            AesKey = string.IsNullOrWhiteSpace(key) ? null : AesKeyText.Normalize(key),
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
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Key text is validated by AesKeyText before it reaches CUE4Parse and is passed as bytes, so no message can contain it.
            logger.LogError("{Type}: {Message}", ex.GetType().Name, ex.Message);
            return 1;
        }
    }
}
