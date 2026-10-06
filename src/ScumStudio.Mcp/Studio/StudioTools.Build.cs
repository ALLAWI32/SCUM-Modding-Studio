using System.Globalization;
using System.Text.Json;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Mcp.Protocol;

namespace ScumStudio.Mcp.Studio;

/// <summary>
/// Building tools for an AI: browse the game's objects by category, ask what is at a point, place many objects at once
/// so they rest on the ground or on what stands there (nothing floats, a crate lands on the table, a wall piece on the
/// one below), and plant forests the way the game does (its own sinking into the ground, random turn and size). Every
/// call is one journal step: one undo removes all of it.
/// </summary>
public sealed partial class StudioTools
{
    private const int MaxPerCall = 2000;
    private GroundProbe? _probe;
    private AssetCatalog? _probeCatalog;
    private readonly Dictionary<string, ActorRef?> _placedBlueprints = new(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<McpTool> BuildTools()
    {
        yield return new McpTool("list_object_categories",
            "The game's objects sorted into categories: 'pickup' (weapons, attachments, ammo, explosives, items a player carries) and " +
            "'world' (buildings, furniture, exterior props, base building, nature: trees, bushes, grass, rocks; wrecks, vehicles, " +
            "roads, water, characters), each with sub-categories (e.g. furniture.tables, nature.trees, exterior.fences) and how many " +
            "objects (static meshes and Blueprints) they hold. Use the ids with list_objects.",
            ToolSchema.Object()
                .String("root", "Only this category and its sub-categories (an id such as 'nature' or 'furniture.kitchen').")
                .Integer("depth", "How many levels of sub-categories to list (default 2).", minimum: 1, maximum: 6)
                .Build(), ListObjectCategories)
        { Title = "Object categories", ReadOnly = true, Idempotent = true };

        yield return new McpTool("list_objects",
            "Objects (static meshes and Blueprints) of a category, for building: name, object path to pass to place_objects / " +
            "scatter_objects, class and category; with withSize also the size in cm (x, y, z) so you can plan spacing and stacking.",
            ToolSchema.Object()
                .String("category", "Category id from list_object_categories (e.g. 'nature.trees', 'furniture.tables').", required: true)
                .String("filter", "Words that must all appear in the name or path.")
                .String("type", "Only meshes or only Blueprints (default both).", choices: ["mesh", "blueprint", "any"])
                .Boolean("withSize", "Also read each object's size (slower; at most 60 objects).")
                .Integer("limit", "Most objects to return (default 100).", minimum: 1, maximum: 2000)
                .Build(), ListObjects)
        { Title = "Objects of a category", ReadOnly = true, Idempotent = true };

        yield return new McpTool("ground_height",
            "What is at a map point (UE cm): the terrain height, the top of the highest object standing there, and the sublevels " +
            "it belongs to (where place_objects would add new actors).",
            ToolSchema.Object()
                .Number("x", "X (cm).", required: true)
                .Number("y", "Y (cm).", required: true)
                .Build(), GroundHeight)
        { Title = "Ground at a point", ReadOnly = true, Idempotent = true };

        var item = ToolSchema.Object()
            .String("object", "Static mesh or Blueprint: object path, package path or just its name (from list_objects). A Blueprint " +
                              "(a house, a lamp, a crate with loot) is copied from one instance the game already placed somewhere.")
            .String("copyLevel", "Instead of 'object': copy this level's actor (with copyActor).")
            .String("copyActor", "Actor name in copyLevel to copy (any actor: a whole house, a prop).")
            .Number("x", "X (cm).", required: true)
            .Number("y", "Y (cm).", required: true)
            .Number("z", "Z (cm). With snap 'surface' (default) the object drops from here onto what is below; omit to drop from the sky (lands on top of whatever stands there).")
            .Number("yaw", "Turn around the vertical axis (degrees).")
            .Number("pitch", "Pitch (degrees).")
            .Number("roll", "Roll (degrees).")
            .Number("scale", "Uniform scale (default 1).")
            .String("snap", "surface (default): rest on the highest terrain or object below; ground: on the terrain only; none: exactly at z.",
                choices: ["surface", "ground", "none"])
            .String("level", "Sublevel to add it to (default: the smallest sublevel whose bounds contain the point, else the landscape tile).");
        yield return new McpTool("place_objects",
            "Places many objects at once, each resting on what is under it: the terrain or the top of an object already there " +
            "(placed earlier in the same call too, so you can stack: a floor, then walls on it, then a roof). The game's own " +
            "plants and rocks keep the depth the game plants them at. One journal step: undo removes them all. Units: cm, degrees. " +
            "Inside buildings use snap 'none' with an explicit z (a building counts as one box from outside).",
            ToolSchema.Object()
                .Objects("objects", "What to place (at most 2000).", item, required: true)
                .String("title", "History title of this step (e.g. 'Build a farm house').")
                .Build(), PlaceObjects)
        { Title = "Place objects", Destructive = false };

        yield return new McpTool("scatter_objects",
            "Plants objects naturally over an area: a dense forest, a bush line, grass, rocks. Random positions with a minimum " +
            "spacing, random turn and size; each sits on the terrain the way the game plants that mesh (its own depth, learned " +
            "from the game's instances nearby). Skips water and the footprints of buildings. One journal step.",
            ToolSchema.Object()
                .Strings("objects", "Meshes to mix (object paths or names), e.g. two oak and one pine species.", required: true)
                .Number("x", "Centre X (cm).", required: true)
                .Number("y", "Centre Y (cm).", required: true)
                .Number("radius", "Radius of the area (cm), e.g. 5000 = 50 m.", required: true)
                .Integer("count", "How many to plant (at most 2000).", required: true, minimum: 1, maximum: MaxPerCall)
                .Number("minSpacing", "Least distance between two (cm, default 300).")
                .Number("scaleMin", "Smallest scale (default 0.8).")
                .Number("scaleMax", "Largest scale (default 1.2).")
                .Integer("seed", "Random seed (same seed, same layout).")
                .String("level", "Sublevel to add them to (default: the landscape tile under each point).")
                .String("title", "History title of this step (e.g. 'Plant a pine forest').")
                .Build(), ScatterObjects)
        { Title = "Scatter objects" };

        yield return new McpTool("copy_place",
            "Copies a whole place the game already has - a village, an outpost, a farm, a military camp (a Poi sublevel from " +
            "list_levels) - to another spot: every building, prop and Blueprint actor (doors, lamps, loot spawners) comes along, " +
            "turned by 'yaw' around the place's centre, each keeping its height above the ground so the copy follows the new " +
            "terrain. Foliage instances and terrain are not copied. One journal step.",
            ToolSchema.Object()
                .String("sourceLevel", "The place to copy (sublevel name, e.g. B_2_Farm_01).", required: true)
                .Number("x", "Where its centre goes: X (cm).", required: true)
                .Number("y", "Where its centre goes: Y (cm).", required: true)
                .Number("yaw", "Turn the whole place (degrees, default 0).")
                .String("filter", "Only actors whose name, class or mesh contains these words.")
                .String("level", "Sublevel to add the copies to (default: the one under the new centre).")
                .String("title", "History title of this step.")
                .Build(), CopyPlace)
        { Title = "Copy a whole place" };
    }

    private Task<McpToolResult> CopyPlace(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var probe = Probe();
        var source = Level(ResolveLevel(call.RequireString("sourceLevel")), cancellationToken);
        var tokens = Tokens(call.GetString("filter")).ToList();
        var actors = source.Actors
            .Where(a => a.Kind is ActorKind.StaticMeshActor or ActorKind.Blueprint && a.Root is not null && a.Root.AttachParent is null
                        && MatchesAll(tokens, a.Name, a.ClassPath, a.StaticMeshPath))
            .Take(MaxPerCall)
            .ToList();
        if (actors.Count == 0)
        {
            return Task.FromResult(McpToolResult.Error($"{source.Name} has no buildings or props to copy{(tokens.Count > 0 ? " matching the filter" : string.Empty)}."));
        }

        var centre = actors.Aggregate(FVector.Zero, (s, a) => s + a.WorldTransform.Translation) * (1f / actors.Count);
        var x = (float)Required(call, "x");
        var y = (float)Required(call, "y");
        var yaw = (float)(call.GetNumber("yaw") ?? 0);
        var turn = new FRotator(0f, yaw, 0f).Quaternion();
        var targetPath = call.GetString("level") is { Length: > 0 } l ? ResolveLevel(l) : probe.LevelsAt(x, y).FirstOrDefault()?.PackagePath
                         ?? throw new ToolArgumentException($"({x:0}, {y:0}) is not on the island.");
        var target = Level(targetPath, cancellationToken);
        var ops = new List<EditOp>();
        var taken = new HashSet<ActorRef>(ActorRef.Comparer);
        foreach (var actor in actors)
        {
            var from = actor.WorldTransform;
            var offset = turn.RotateVector(new FVector(from.Translation.X - centre.X, from.Translation.Y - centre.Y, 0f));
            var nx = x + offset.X;
            var ny = y + offset.Y;
            // Same height above the ground as at home, so the copy follows the new terrain.
            var above = from.Translation.Z - (probe.TerrainHeight(from.Translation.X, from.Translation.Y) ?? from.Translation.Z);
            var nz = (probe.TerrainHeight(nx, ny) ?? from.Translation.Z) + above;
            var rotation = from.Rotation.Rotator();
            var transform = new TransformValue(new FVector(nx, ny, nz), rotation with { Yaw = rotation.Yaw + yaw }, from.Scale3D);
            if (actor.Kind == ActorKind.Blueprint)
            {
                var op = EditOpFactory.AddBlueprintActor(target, source, actor, transform, project.State);
                ops.Add(op with { NewName = NewName(target, op.NewName, project.State, taken) });
            }
            else if (actor.StaticMeshPath is { } mesh)
            {
                ops.Add(new AddStaticMeshActorOp(target.PackagePath, NewName(target, ShortName(mesh) + "_Added", project.State, taken), mesh, transform));
            }
        }

        var title = call.GetString("title") is { Length: > 0 } t ? t : $"Copy {source.Name}";
        var entry = _host.Apply(new BatchOp(title, ops));
        return Task.FromResult(McpToolResult.Text(string.Create(CultureInfo.InvariantCulture,
            $"#{entry.Seq} {title}: {ops.Count} actor(s) of {source.Name} copied to ({x:0}, {y:0}) in {target.Name}, turned {yaw:0}°. Undo removes them all.")));
    }

    // ------------------------------------------------------------------ browse

    private Task<McpToolResult> ListObjectCategories(ToolCall call, CancellationToken cancellationToken)
    {
        var counts = ObjectCounts();
        var depth = call.GetInt("depth", 2);
        var roots = AssetDumper.Tree.ToDictionary(n => n.Id, StringComparer.Ordinal);
        object Node(DumpNode n, int level) => new
        {
            id = n.Id,
            title = n.Title,
            objects = counts.GetValueOrDefault(n.Id),
            children = level < depth ? n.Children.Where(c => counts.GetValueOrDefault(c.Id) > 0).Select(c => Node(c, level + 1)).ToList() : null,
        };

        if (call.GetString("root") is { Length: > 0 } root)
        {
            var node = Find(AssetDumper.Tree, root) ?? throw new ToolArgumentException($"No category '{root}'. Call list_object_categories without root.");
            return Task.FromResult(McpToolResult.Json(Node(node, 1)));
        }

        object Group(string id, string title, string[] ids) => new
        {
            id,
            title,
            children = ids.Where(roots.ContainsKey).Select(i => Node(roots[i], 1)).ToList(),
        };

        return Task.FromResult(McpToolResult.Json(new[]
        {
            Group("pickup", "Things a player picks up", ["weapons", "attachments", "ammo", "explosives", "items"]),
            Group("world", "Things that stay in the world", ["buildings", "furniture", "exterior", "basebuilding", "nature", "wrecks", "vehicles", "roads", "water", "characters"]),
        }));

        static DumpNode? Find(IEnumerable<DumpNode> nodes, string id)
        {
            foreach (var n in nodes)
            {
                if (n.Id == id)
                {
                    return n;
                }

                if (id.StartsWith(n.Id + ".", StringComparison.Ordinal) && Find(n.Children, id) is { } child)
                {
                    return child;
                }
            }

            return null;
        }
    }

    private Task<McpToolResult> ListObjects(ToolCall call, CancellationToken cancellationToken)
    {
        var catalog = RequireCatalog();
        var category = call.RequireString("category");
        var type = call.GetString("type") ?? "any";
        var tokens = Tokens(call.GetString("filter")).ToList();
        var withSize = call.GetBool("withSize", false);
        var limit = call.GetInt("limit", 100);
        var probe = Probe();
        var matches = AssetDumper.Packages
            .Where(p => (p.Node == category || p.Node.StartsWith(category + ".", StringComparison.Ordinal)) && IsObjectClass(p.ClassName)
                        && (type == "any" || (type == "blueprint") == (p.ClassName == "Blueprint"))
                        && MatchesAll(tokens, p.PackagePath) && catalog.PackageExists(p.PackagePath))
            .ToList();
        var rows = matches.Take(limit).Select((p, i) =>
        {
            var objectPath = ObjectPathOf(p.PackagePath);
            var size = withSize && i < 60 && p.ClassName != "Blueprint" && probe.MeshBounds(objectPath) is { IsEmpty: false } b ? Vec(new FVector(b.Size.X, b.Size.Y, b.Size.Z), 0) : null;
            return new { name = ShortName(p.PackagePath), path = objectPath, @class = p.ClassName, category = p.Node, sizeCm = size };
        }).ToList();
        return Task.FromResult(McpToolResult.Json(new { category, total = matches.Count, objects = rows }));
    }

    private Task<McpToolResult> GroundHeight(ToolCall call, CancellationToken cancellationToken)
    {
        var probe = Probe();
        var x = (float)Required(call, "x");
        var y = (float)Required(call, "y");
        var top = probe.SurfaceBelow(x, y, float.MaxValue, AddedBoxes(probe));
        return Task.FromResult(McpToolResult.Json(new
        {
            terrainZ = probe.TerrainHeight(x, y) is { } t ? Round(t) : (double?)null,
            surfaceZ = top is { } s ? Round(s.Z) : (double?)null,
            surfaceIs = top?.What,
            levels = probe.LevelsAt(x, y).Select(p => p.Name).ToList(),
        }));
    }

    // ------------------------------------------------------------------ place

    private Task<McpToolResult> PlaceObjects(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var probe = Probe();
        probe.ClearPlaced();
        var items = call.GetElement("objects") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().ToList() : throw new ToolArgumentException("'objects' must be a list.");
        if (items.Count is 0 or > MaxPerCall)
        {
            throw new ToolArgumentException($"Give 1 to {MaxPerCall} objects.");
        }

        var added = AddedBoxes(probe);
        var ops = new List<EditOp>();
        var taken = new HashSet<ActorRef>(ActorRef.Comparer);
        var report = new List<object>();
        foreach (var (element, index) in items.Select((e, i) => (e, i)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = $"objects[{index}]";
            var x = Num(element, "x") ?? throw new ToolArgumentException($"{at}: 'x' is required.");
            var y = Num(element, "y") ?? throw new ToolArgumentException($"{at}: 'y' is required.");
            var scale = Num(element, "scale") ?? 1f;
            var rotation = new FRotator(Num(element, "pitch") ?? 0f, Num(element, "yaw") ?? 0f, Num(element, "roll") ?? 0f);
            var snap = Str(element, "snap") ?? "surface";
            var levelPath = Str(element, "level") is { Length: > 0 } l ? ResolveLevel(l) : probe.LevelsAt(x, y).FirstOrDefault()?.PackagePath
                            ?? throw new ToolArgumentException($"{at}: ({x:0}, {y:0}) is not on the island (no sublevel there). Give 'level'.");
            var document = Level(levelPath, cancellationToken);

            var (op, mesh) = NewActor(element, at, document, project.State, taken, cancellationToken);
            var z = PlaceZ(probe, snap, x, y, Num(element, "z"), mesh, rotation, scale, added, at);
            op = op switch
            {
                AddStaticMeshActorOp a => a with { Transform = new TransformValue(new FVector(x, y, z), rotation, new FVector(scale, scale, scale)) },
                AddBlueprintActorOp b => b with { Transform = new TransformValue(new FVector(x, y, z), rotation, new FVector(scale, scale, scale)) },
                _ => op,
            };
            ops.Add(op);
            if (mesh is not null && probe.WorldBounds(mesh, new FTransform(rotation, new FVector(x, y, z), new FVector(scale, scale, scale))) is { } box)
            {
                probe.AddPlaced(box); // the next ones can stand on it
            }

            report.Add(new { name = op.GetPrimaryTarget()?.Actor, level = document.Name, location = Vec(new FVector(x, y, z)) });
        }

        var title = call.GetString("title") is { Length: > 0 } t ? t : string.Create(CultureInfo.InvariantCulture, $"Place {ops.Count} object(s)");
        var entry = _host.Apply(new BatchOp(title, ops));
        return Task.FromResult(McpToolResult.Json(new { step = entry.Seq, title, placed = report })
            .WithSummary(string.Create(CultureInfo.InvariantCulture, $"#{entry.Seq} {title}: {ops.Count} object(s)")));
    }

    private Task<McpToolResult> ScatterObjects(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var catalog = RequireCatalog();
        var probe = Probe();
        probe.ClearPlaced();
        var meshes = call.GetStrings("objects").Select(o => ResolveObject(catalog, o)).ToList();
        if (meshes.Count == 0 || meshes.Any(m => m.ClassName == "Blueprint"))
        {
            throw new ToolArgumentException("'objects' needs static meshes (trees, bushes, grass, rocks); Blueprints are placed with place_objects.");
        }

        var cx = (float)Required(call, "x");
        var cy = (float)Required(call, "y");
        var radius = (float)Required(call, "radius");
        var count = Math.Clamp(call.GetInt("count", 100), 1, MaxPerCall);
        var spacing = (float)(call.GetNumber("minSpacing") ?? 300);
        var scaleMin = (float)(call.GetNumber("scaleMin") ?? 0.8);
        var scaleMax = Math.Max(scaleMin, (float)(call.GetNumber("scaleMax") ?? 1.2));
        var random = new Random(call.GetInt("seed", 12345));
        var fixedLevel = call.GetString("level") is { Length: > 0 } fl ? ResolveLevel(fl) : null;
        var added = AddedBoxes(probe);

        var points = new List<(float X, float Y)>();
        for (var attempts = count * 30; points.Count < count && attempts > 0; attempts--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var angle = random.NextDouble() * Math.PI * 2;
            var distance = radius * Math.Sqrt(random.NextDouble());
            var p = (X: cx + (float)(Math.Cos(angle) * distance), Y: cy + (float)(Math.Sin(angle) * distance));
            if (points.Any(q => ((q.X - p.X) * (q.X - p.X)) + ((q.Y - p.Y) * (q.Y - p.Y)) < spacing * spacing)
                || probe.TerrainHeight(p.X, p.Y) is not { } ground || ground < 30f // the sea and river beds
                || probe.SurfaceBelow(p.X, p.Y, float.MaxValue, added) is { What: "object" })
            {
                continue;
            }

            points.Add(p);
        }

        var ops = new List<EditOp>();
        var taken = new HashSet<ActorRef>(ActorRef.Comparer);
        foreach (var p in points)
        {
            var (mesh, _) = meshes[random.Next(meshes.Count)];
            var scale = scaleMin + ((float)random.NextDouble() * (scaleMax - scaleMin));
            var rotation = new FRotator(0f, (float)(random.NextDouble() * 360.0), 0f);
            var levelPath = fixedLevel ?? probe.LevelsAt(p.X, p.Y).FirstOrDefault(l => l.Kind == WorldPackageKind.Landscape)?.PackagePath;
            if (levelPath is null)
            {
                continue;
            }

            var document = Level(levelPath, cancellationToken);
            var z = PlaceZ(probe, "ground", p.X, p.Y, null, mesh, rotation, scale, added, "scatter");
            var name = NewName(document, ShortName(mesh) + "_Added", project.State, taken);
            ops.Add(new AddStaticMeshActorOp(document.PackagePath, name, mesh, new TransformValue(new FVector(p.X, p.Y, z), rotation, new FVector(scale, scale, scale))));
        }

        if (ops.Count == 0)
        {
            return Task.FromResult(McpToolResult.Error("Nothing planted: the area is water, buildings or off the island."));
        }

        var title = call.GetString("title") is { Length: > 0 } t ? t : string.Create(CultureInfo.InvariantCulture, $"Plant {ops.Count} object(s)");
        var entry = _host.Apply(new BatchOp(title, ops));
        return Task.FromResult(McpToolResult.Text(string.Create(CultureInfo.InvariantCulture,
            $"#{entry.Seq} {title}: {ops.Count} of {count} planted within {radius / 100f:0} m of ({cx:0}, {cy:0}) (the rest fell on water, buildings or too close to another). Undo removes them all.")));
    }

    /// <summary>
    /// The Z where an object's base rests: on the terrain or the highest object top below (<paramref name="snap"/>), with
    /// the game's own planting depth for meshes it scatters itself, else the mesh's lowest point on the surface.
    /// </summary>
    private static float PlaceZ(GroundProbe probe, string snap, float x, float y, float? z, string? mesh, FRotator rotation, float scale,
        IReadOnlyList<BoundingBox> added, string at)
    {
        if (snap == "none")
        {
            return z ?? throw new ToolArgumentException($"{at}: snap 'none' needs 'z'.");
        }

        (float Z, string What)? surface = snap == "ground"
            ? probe.TerrainHeight(x, y) is { } g ? (g, "terrain") : null
            : probe.SurfaceBelow(x, y, z ?? float.MaxValue, added);
        if (surface is not { } s)
        {
            return z ?? throw new ToolArgumentException($"{at}: nothing to stand on at ({x:0}, {y:0}) (sea or off the island); give 'z' and snap 'none'.");
        }

        if (mesh is null)
        {
            return s.Z; // a copied actor: its pivot is its base
        }

        if (s.What == "terrain" && probe.PlantOffset(mesh, x, y) is { } offset)
        {
            return s.Z + (offset * scale); // as deep as the game plants it (trees and rocks sink a little)
        }

        var bottom = probe.WorldBounds(mesh, new FTransform(rotation, FVector.Zero, new FVector(scale, scale, scale)))?.Min.Z ?? 0f;
        return s.Z - bottom;
    }

    /// <summary>The add operation of one place_objects entry and the mesh that decides its footprint (null for copied actors).</summary>
    private (EditOp Op, string? Mesh) NewActor(JsonElement element, string at, LevelDocument document, EditState state, HashSet<ActorRef> taken, CancellationToken ct)
    {
        var catalog = RequireCatalog();
        if (Str(element, "copyLevel") is { Length: > 0 } copyLevel)
        {
            var sourceDoc = Level(ResolveLevel(copyLevel), ct);
            var actorName = Str(element, "copyActor") ?? throw new ToolArgumentException($"{at}: copyLevel needs copyActor.");
            var source = sourceDoc.FindActor(actorName) ?? throw new ToolArgumentException($"{at}: '{actorName}' is not an actor of {sourceDoc.Name}.");
            return Copy(sourceDoc, source);
        }

        var (path, className) = ResolveObject(catalog, Str(element, "object") ?? throw new ToolArgumentException($"{at}: give 'object' or copyLevel + copyActor."));
        if (className != "Blueprint")
        {
            return (new AddStaticMeshActorOp(document.PackagePath, NewName(document, ShortName(path) + "_Added", state, taken), path, TransformValue.Identity), path);
        }

        // A Blueprint is created by copying one instance the game placed somewhere (its components come along).
        var placed = PlacedInstance(path, ct) ?? throw new ToolArgumentException(
            $"{at}: {ShortName(path)} is a Blueprint that no level of the island places, so there is nothing to copy. Pick a static mesh, or copyLevel/copyActor of a similar actor.");
        var instanceDoc = Level(placed.Level, ct);
        return Copy(instanceDoc, instanceDoc.FindActor(placed.Actor)!);

        (EditOp, string?) Copy(LevelDocument sourceDoc, ActorRecord source)
        {
            if (source.Kind == ActorKind.Blueprint)
            {
                var op = EditOpFactory.AddBlueprintActor(document, sourceDoc, source, TransformValue.Identity, state);
                return (op with { NewName = NewName(document, op.NewName, state, taken) }, null);
            }

            var mesh = source.StaticMeshPath ?? throw new ToolArgumentException($"{at}: '{source.Name}' has no mesh to copy.");
            return (new AddStaticMeshActorOp(document.PackagePath, NewName(document, ShortName(mesh) + "_Added", state, taken), mesh, TransformValue.Identity), mesh);
        }
    }

    /// <summary>An actor of the Blueprint class somewhere on the island (header scan of the sublevels; remembered).</summary>
    private ActorRef? PlacedInstance(string classObjectPath, CancellationToken ct)
    {
        var package = AssetPaths.SplitObjectPath(classObjectPath).PackagePath;
        if (_placedBlueprints.TryGetValue(package, out var known))
        {
            return known;
        }

        var catalog = RequireCatalog();
        var walker = new PackageDependencyWalker(catalog, _logger);
        var levels = RequireWorld().Packages.Where(p => p.IsContentLevel)
            .Select(p => p.PackagePath)
            .AsParallel().WithDegreeOfParallelism(4)
            .Where(p =>
            {
                try
                {
                    return walker.ReadReferencedPackages(p).Contains(package, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    return false;
                }
            })
            .Take(8)
            .ToList();
        ActorRef? found = null;
        foreach (var level in levels.Order(StringComparer.OrdinalIgnoreCase))
        {
            var actor = Level(level, ct).Actors.FirstOrDefault(a => a.Kind == ActorKind.Blueprint && EditOpFactory.SameObject(AssetPaths.SplitObjectPath(a.ClassPath).PackagePath, package));
            if (actor is not null)
            {
                found = new ActorRef(level, actor.Name);
                break;
            }
        }

        _placedBlueprints[package] = found;
        return found;
    }

    /// <summary>Resolves an object path, package path or bare name to (object path, catalogue class).</summary>
    private static (string Path, string ClassName) ResolveObject(AssetCatalog catalog, string text)
    {
        var name = text.Trim().Trim('"', '\'');
        var package = name.Contains('/', StringComparison.Ordinal) ? AssetPaths.SplitObjectPath(name).PackagePath : null;
        var known = AssetDumper.Packages.Where(p => package is not null
                ? string.Equals(p.PackagePath, package, StringComparison.OrdinalIgnoreCase)
                : IsObjectClass(p.ClassName) && p.PackagePath.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.ClassName == "Blueprint")
            .FirstOrDefault();
        package ??= known.PackagePath ?? throw new ToolArgumentException($"No object is called '{name}'. Use list_objects to find one.");
        if (!catalog.PackageExists(package))
        {
            throw new ToolArgumentException($"{package} is not in the open game files.");
        }

        return (ObjectPathOf(package), known.ClassName ?? catalog.GetMainClassName(package) ?? "StaticMesh");
    }

    private static string ObjectPathOf(string packagePath) => packagePath + "." + packagePath[(packagePath.LastIndexOf('/') + 1)..];

    private static bool IsObjectClass(string className) => className is "StaticMesh" or "SkeletalMesh" or "Blueprint";

    /// <summary>Objects (meshes and Blueprints) per category id, sub-categories included.</summary>
    private static Dictionary<string, int> ObjectCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in AssetDumper.Packages.Where(p => IsObjectClass(p.ClassName)))
        {
            for (var id = p.Node; id.Length > 0; id = id.LastIndexOf('.') is var dot and > 0 ? id[..dot] : string.Empty)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }

    /// <summary>A name free in the level, the project and this batch.</summary>
    private static string NewName(LevelDocument document, string baseName, EditState state, HashSet<ActorRef> taken)
    {
        var name = EditOpFactory.UniqueActorName(document, baseName, state);
        for (var i = 2; taken.Contains(new ActorRef(document.PackagePath, name)); i++)
        {
            name = EditOpFactory.UniqueActorName(document, string.Create(CultureInfo.InvariantCulture, $"{baseName}{i}"), state);
        }

        taken.Add(new ActorRef(document.PackagePath, name));
        return name;
    }

    /// <summary>The probe for the open game files (built once: the world index with tile bounds).</summary>
    private GroundProbe Probe()
    {
        var catalog = RequireCatalog();
        if (_probe is null || !ReferenceEquals(_probeCatalog, catalog))
        {
            _probe = new GroundProbe(catalog, RequireWorld().WithTileInfo(catalog, _logger), path => Level(path, CancellationToken.None));
            _probeCatalog = catalog;
            _placedBlueprints.Clear();
        }

        return _probe;
    }

    /// <summary>Boxes of the static meshes the project already added (so new objects stand on them too).</summary>
    private List<BoundingBox> AddedBoxes(GroundProbe probe)
    {
        var state = _host.Project?.State;
        var boxes = new List<BoundingBox>();
        foreach (var (actor, op) in state?.AddedActors ?? new Dictionary<ActorRef, EditOp>())
        {
            if (op is AddStaticMeshActorOp add && !state!.IsDeleted(actor) && state.GetAddedTransform(actor) is { } t
                && probe.WorldBounds(add.StaticMesh, t.ToTransform()) is { } box)
            {
                boxes.Add(box);
            }
        }

        return boxes;
    }

    private static double Required(ToolCall call, string name) => call.GetNumber(name) ?? throw new ToolArgumentException($"'{name}' is required.");

    private static float? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
