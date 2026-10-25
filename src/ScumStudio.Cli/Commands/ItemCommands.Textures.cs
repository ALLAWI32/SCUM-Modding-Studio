using System.CommandLine;
using System.CommandLine.Invocation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Textures;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Cli.Commands;

/// <content>
/// <c>item textures</c> and <c>item texture</c>: the Textures panel of the Vehicles page for any clone, so a weapon or an
/// item can wear a picture of its own while every stored value stays as the stock item's (a clone copies them all). The
/// recorded edits are the same three the panel records (see <c>ModulePageViewModel.TextureReplaceOp</c>): the new texture's pixels, the
/// clone's material parameter pointing at it, and the material's streaming entry.
/// </content>
internal sealed partial class ItemCommands
{
    /// <summary>One texture slot of a material instance of a clone.</summary>
    private sealed record CloneSlot(string StockMaterial, string Material, string Parameter, string TexturePath, int Width, int Height, string PixelFormat);

    private static Command BuildTextures()
    {
        var project = new Argument<string>("projectDir", "Project folder (e.g. MyMod.ssproj).");
        var source = SourceArgument();
        var clone = new Argument<string>("clone", "The clone's name (e.g. Weapon_RPK-74_Gold) or package path, as recorded by 'item clone'.");
        var aes = AesOption();
        var command = new Command("textures", "List the texture slots of a clone's materials as '<material>\\t<parameter>\\t<stock texture>\\t<WxH>\\t<format>\\t[replaced]'.") { project, source, clone, aes };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ItemCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var proj = Project.Open(parse.GetValueForArgument(project));
                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                if (FindClone(proj, parse.GetValueForArgument(clone)) is not { } found)
                {
                    logger.LogError("{Clone} is not a clone of this project (record one with 'scumstudio item clone').", parse.GetValueForArgument(clone));
                    return 2;
                }

                var slots = CloneSlots(catalog, found);
                foreach (var slot in slots)
                {
                    var export = MaterialExportKey(catalog, proj, slot.Material);
                    var replaced = export is not null
                        && proj.State.GetAssetValue(slot.Material, export, MaterialParameters.PathOf("TextureParameterValues", slot.Parameter)) is not null;
                    Console.Out.WriteLine($"{PackageMap.Leaf(slot.Material)}\t{slot.Parameter}\t{slot.TexturePath}\t{slot.Width}x{slot.Height}\t{slot.PixelFormat}{(replaced ? "\treplaced" : string.Empty)}");
                }

                logger.LogInformation("{Count} texture slot(s).", slots.Count);
                return 0;
            });
        });
        return command;
    }

    private static Command BuildTexture()
    {
        var project = new Argument<string>("projectDir", "Project folder (e.g. MyMod.ssproj).");
        var source = SourceArgument();
        var clone = new Argument<string>("clone", "The clone's name or package path.");
        var parameter = new Argument<string>("parameter", "The texture slot (the second column of 'item textures'), e.g. BaseColor.");
        var picture = new Argument<string>("picture", "A PNG or JPG file. It is scaled to the stock texture's size.");
        var material = new Option<string?>("--material", "Only the material of this name, when several of the clone's materials have the slot.");
        var reset = new Option<bool>("--reset", "Put the slot back to the game's own texture (the picture argument is then ignored).");
        var aes = AesOption();
        var command = new Command("texture", "Record a new picture for one texture slot of a clone in the project journal (exported by 'project export'). The clone keeps every stored value of the stock item.") { project, source, clone, parameter, picture, material, reset, aes };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ItemCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var proj = Project.Open(parse.GetValueForArgument(project));
                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                if (FindClone(proj, parse.GetValueForArgument(clone)) is not { } found)
                {
                    logger.LogError("{Clone} is not a clone of this project (record one with 'scumstudio item clone').", parse.GetValueForArgument(clone));
                    return 2;
                }

                var wanted = parse.GetValueForArgument(parameter);
                var materialFilter = parse.GetValueForOption(material);
                var candidates = CloneSlots(catalog, found)
                    .Where(s => string.Equals(s.Parameter, wanted, StringComparison.OrdinalIgnoreCase)
                        && (materialFilter is null || string.Equals(PackageMap.Leaf(s.Material), materialFilter, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (candidates.Count == 0)
                {
                    logger.LogError("The clone has no texture slot '{Parameter}'{Filter} (see 'scumstudio item textures').", wanted, materialFilter is null ? string.Empty : " in " + materialFilter);
                    return 2;
                }

                if (candidates.Count > 1)
                {
                    logger.LogError("{Count} materials have the slot '{Parameter}' ({Names}); pick one with --material.", candidates.Count, wanted,
                        string.Join(", ", candidates.Select(c => PackageMap.Leaf(c.Material))));
                    return 2;
                }

                var slot = candidates[0];
                var export = MaterialExportKey(catalog, proj, slot.Material);
                if (export is null)
                {
                    logger.LogError("{Material} could not be read.", slot.Material);
                    return 2;
                }

                var texturePackage = TextureWrites.NewPackage(found.NewPrimary, slot.Material, slot.Parameter);
                var textureLeaf = PackageMap.Leaf(texturePackage);
                var templateLeaf = slot.TexturePath[(slot.TexturePath.LastIndexOf('.') + 1)..];

                if (parse.GetValueForOption(reset))
                {
                    var keys = new (string Package, string Export, string Path)[]
                    {
                        (texturePackage, textureLeaf, TextureWrites.PixelsPath),
                        (slot.Material, export, MaterialParameters.PathOf("TextureParameterValues", slot.Parameter)),
                        (slot.Material, export, MaterialTextureStreaming.PathOf(templateLeaf)),
                    };
                    var undo = keys.Select(k => proj.State.GetAssetValue(k.Package, k.Export, k.Path)).OfType<AssetValueOverride>()
                        .Select(v => (EditOp)new SetAssetValueOp(v.Package, v.Export, v.Path, v.ValueKind, v.Current, v.Base)).ToList();
                    if (undo.Count == 0)
                    {
                        Console.Out.WriteLine("The slot is already the game's own.");
                        return 0;
                    }

                    var resetEntry = proj.Apply(new BatchOp("Texture reset", undo));
                    Console.Out.WriteLine($"#{resetEntry.Seq}: {PackageMap.Leaf(slot.Material)} {slot.Parameter} back to the game's texture.");
                    return 0;
                }

                TextureImage image;
                try
                {
                    image = ImageFiles.Load(parse.GetValueForArgument(picture));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    logger.LogError("Cannot read the picture: {Message}", ex.Message);
                    return 2;
                }

                var file = ProjectTextures.Save(proj.DirectoryPath, image.Rgba, image.Width, image.Height);
                string Current(string package, string exportKey, string path, string stock) => proj.State.GetAssetValue(package, exportKey, path)?.Current ?? stock;

                var parameterPath = MaterialParameters.PathOf("TextureParameterValues", slot.Parameter);
                var streamingPath = MaterialTextureStreaming.PathOf(templateLeaf);
                var pixels = TextureWrites.Value(slot.TexturePath, file);
                SetAssetValueOp[] ops =
                [
                    new(texturePackage, textureLeaf, TextureWrites.PixelsPath, "Texture", Current(texturePackage, textureLeaf, TextureWrites.PixelsPath, string.Empty), pixels),
                    new(slot.Material, export, parameterPath, "Text", Current(slot.Material, export, parameterPath, slot.TexturePath), texturePackage + "." + textureLeaf),
                    new(slot.Material, export, streamingPath, "Text", Current(slot.Material, export, streamingPath, templateLeaf), textureLeaf),
                ];
                var changed = ops.Where(o => o.Old != o.New).ToList<EditOp>();
                if (changed.Count == 0)
                {
                    Console.Out.WriteLine("That picture is already recorded for this slot.");
                    return 0;
                }

                // Not validated, as in the Textures panel: the texture package is new, so no clone package of the state holds it yet.
                var entry = proj.Apply(new BatchOp("Texture replace", changed));
                Console.Out.WriteLine($"#{entry.Seq}: {PackageMap.Leaf(slot.Material)} {slot.Parameter} <- {Path.GetFileName(parse.GetValueForArgument(picture))} ({image.Width}x{image.Height} -> {slot.Width}x{slot.Height}, {slot.PixelFormat})");
                Console.Out.WriteLine("Export with 'scumstudio project export'; the client pak carries the texture.");
                return 0;
            });
        });
        return command;
    }

    /// <summary>The clone whose new primary or any new package matches <paramref name="nameOrPath"/> (name or full path).</summary>
    private static CloneAssetOp? FindClone(Project project, string nameOrPath)
    {
        var package = CloneTarget(project, nameOrPath);
        return package is null ? null : project.State.FindCloneOf(package);
    }

    /// <summary>The export key of a clone's material instance (the key the value edits use), or null when it cannot be read.</summary>
    private static string? MaterialExportKey(AssetCatalog catalog, Project project, string materialPackage)
    {
        try
        {
            return TunableReader.ExportKeys(AssetEditing.ReadForEditing(catalog, project.State, materialPackage))[0];
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The 2D texture slots of every material instance in the clone's family (stock material read from the game files).</summary>
    private static List<CloneSlot> CloneSlots(AssetCatalog catalog, CloneAssetOp clone)
    {
        var slots = new List<CloneSlot>();
        foreach (var pair in clone.Packages)
        {
            if (!catalog.GetExports(pair.Old).Any(e => e.ClassName == "MaterialInstanceConstant"))
            {
                continue;
            }

            var objectPath = pair.Old + "." + PackageMap.Leaf(pair.Old);
            if (!catalog.TryLoadObject<UMaterialInstanceConstant>(objectPath, out var instance))
            {
                continue;
            }

            foreach (var texture in new MaterialInspector(catalog).Inspect(instance).Textures.Where(t => t.TexturePath is { Length: > 0 }))
            {
                if (!catalog.TryLoadObject<UTexture2D>(texture.TexturePath!, out var loaded))
                {
                    continue; // a cube map, a texture array, or a texture the game files do not have
                }

                var info = TextureDecoder.Describe(loaded);
                slots.Add(new CloneSlot(pair.Old, pair.New, texture.Name, texture.TexturePath!, info.Width, info.Height, info.PixelFormat));
            }
        }

        return slots;
    }
}
