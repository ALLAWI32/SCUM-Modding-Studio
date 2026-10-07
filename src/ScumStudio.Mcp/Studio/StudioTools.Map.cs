using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Mcp.Protocol;

namespace ScumStudio.Mcp.Studio;

/// <content>Map tools: levels, actors and their edits.</content>
public sealed partial class StudioTools
{
    private static readonly string[] ActorKinds = Enum.GetNames<ActorKind>();

    private IEnumerable<McpTool> MapTools()
    {
        yield return new McpTool("list_levels",
            "Lists The_Island's sublevels (streaming levels): name, kind (Poi = places/buildings, Landscape = terrain tiles, TvBase, " +
            "Pripyat, …), map cell (A_0 … D_4, Z_0 … Z_4) and package path. Filter by cell, kind and name text.",
            ToolSchema.Object()
                .String("cell", "Map cell, e.g. A_0.")
                .String("kind", "Level kind.", choices: Enum.GetNames<WorldPackageKind>())
                .String("filter", "Words that must all appear in the level name, e.g. 'outpost saloon'.")
                .Integer("limit", "Most levels to return (default 200).", minimum: 1, maximum: 5000)
                .Build(),
            (call, _) =>
            {
                var world = RequireWorld();
                MapCell? cell = null;
                if (call.GetString("cell") is { } cellText)
                {
                    cell = MapCell.TryParse(cellText, out var parsed) ? parsed : throw new ToolArgumentException($"'{cellText}' is not a map cell (A_0 … D_4, Z_0 … Z_4).");
                }

                WorldPackageKind? kind = call.GetString("kind") is { } kindText
                    ? Enum.TryParse<WorldPackageKind>(kindText, ignoreCase: true, out var k) ? k : throw new ToolArgumentException($"Unknown level kind '{kindText}'.")
                    : null;
                var tokens = Tokens(call.GetString("filter")).ToList();
                var matches = world.Sublevels
                    .Where(p => cell is null || p.Cell == cell)
                    .Where(p => kind is null || p.Kind == kind)
                    .Where(p => MatchesAll(tokens, p.Name))
                    .OrderBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var limit = call.GetInt("limit", 200);
                return Task.FromResult(McpToolResult.Json(new
                {
                    total = matches.Count,
                    shown = Math.Min(limit, matches.Count),
                    levels = matches.Take(limit).Select(p => new { name = p.Name, kind = p.Kind.ToString(), cell = p.Cell?.ToString(), path = p.PackagePath }),
                }));
            })
        { Title = "List levels", ReadOnly = true, Idempotent = true };

        yield return new McpTool("list_actors",
            "Lists the actors of one level with their state in the project: name, class, kind, mesh, world location/rotation, " +
            "instance count (foliage/rocks), and whether the project deleted, moved or added it. Filter by words (name, class or mesh) and kind.",
            ToolSchema.Object()
                .String("level", "Level name or package path (see list_levels).", required: true)
                .String("filter", "Words that must all appear in the name, class or mesh.")
                .String("kind", "Actor kind.", choices: ActorKinds)
                .Boolean("includeDeleted", "Also list actors the project deleted (default true).")
                .Integer("limit", "Most actors to return (default 200).", minimum: 1, maximum: 10000)
                .Build(),
            (call, ct) =>
            {
                var path = ResolveLevel(call.RequireString("level"));
                var document = Level(path, ct);
                var state = _host.Project?.State;
                var tokens = Tokens(call.GetString("filter")).ToList();
                ActorKind? kind = call.GetString("kind") is { } kindText
                    ? Enum.TryParse<ActorKind>(kindText, ignoreCase: true, out var k) ? k : throw new ToolArgumentException($"Unknown actor kind '{kindText}'.")
                    : null;
                var includeDeleted = call.GetBool("includeDeleted", true);
                var rows = new List<object>();
                var total = 0;
                var limit = call.GetInt("limit", 200);
                foreach (var actor in document.Actors)
                {
                    var reference = new ActorRef(path, actor.Name);
                    var deleted = state?.IsDeleted(reference) == true;
                    if ((!includeDeleted && deleted) || (kind is not null && actor.Kind != kind)
                        || !MatchesAll(tokens, actor.Name, actor.ClassName, actor.StaticMeshPath))
                    {
                        continue;
                    }

                    total++;
                    if (rows.Count >= limit)
                    {
                        continue;
                    }

                    var transform = state?.GetTransformOverride(reference) is { } moved ? moved : TransformValue.FromTransform(actor.WorldTransform);
                    rows.Add(new
                    {
                        name = actor.Name,
                        @class = actor.ClassName,
                        kind = actor.Kind.ToString(),
                        mesh = actor.StaticMeshPath is null ? null : ShortName(actor.StaticMeshPath),
                        location = Vec(transform.Location),
                        rotation = Rot(transform.Rotation),
                        instances = actor.InstanceTransforms.Count == 0 ? (int?)null : actor.InstanceTransforms.Count,
                        deleted = deleted ? true : (bool?)null,
                        moved = state?.GetTransformOverride(reference) is not null ? true : (bool?)null,
                    });
                }

                foreach (var (added, op) in state?.AddedActors.Where(a => string.Equals(a.Key.Level, path, StringComparison.OrdinalIgnoreCase)) ?? [])
                {
                    if (!MatchesAll(tokens, added.Actor, AddedClass(op, document), AddedMesh(op)))
                    {
                        continue;
                    }

                    total++;
                    if (rows.Count < limit && state!.GetAddedTransform(added) is { } t)
                    {
                        rows.Add(new
                        {
                            name = added.Actor,
                            @class = AddedClass(op, document),
                            kind = "Added",
                            mesh = AddedMesh(op) is { } m ? ShortName(m) : null,
                            location = Vec(t.Location),
                            rotation = Rot(t.Rotation),
                            instances = (int?)null,
                            deleted = state.IsDeleted(added) ? true : (bool?)null,
                            moved = (bool?)null,
                            added = true,
                        });
                    }
                }

                return Task.FromResult(McpToolResult.Json(new { level = document.Name, path, total, shown = rows.Count, actors = rows }));
            })
        { Title = "List actors", ReadOnly = true, Idempotent = true };

        yield return new McpTool("get_actor",
            "Details of one actor: class path, kind, mesh, world and root-relative transform (with the project's override), " +
            "components (class, mesh, instance count) and its state in the project.",
            ToolSchema.Object()
                .String("level", "Level name or package path.", required: true)
                .String("actor", "Actor name (see list_actors).", required: true)
                .Build(),
            (call, ct) =>
            {
                var path = ResolveLevel(call.RequireString("level"));
                var document = Level(path, ct);
                var name = call.RequireString("actor");
                var reference = new ActorRef(path, name);
                var state = _host.Project?.State;
                if (document.FindActor(name) is not { } actor)
                {
                    if (state?.AddedActors.TryGetValue(reference, out var op) == true && state.GetAddedTransform(reference) is { } added)
                    {
                        return Task.FromResult(McpToolResult.Json(new
                        {
                            name,
                            level = document.Name,
                            added = true,
                            createdBy = op.Describe(),
                            @class = AddedClass(op, document),
                            mesh = AddedMesh(op),
                            location = Vec(added.Location),
                            rotation = Rot(added.Rotation),
                            scale = Vec(added.Scale, 3),
                            deleted = state.IsDeleted(reference),
                        }));
                    }

                    throw new ToolArgumentException($"'{name}' is not an actor of {document.Name}. Use list_actors.");
                }

                var relative = actor.Root?.Relative ?? TransformValue.FromTransform(actor.WorldTransform);
                var current = state?.GetTransformOverride(reference) ?? relative;
                return Task.FromResult(McpToolResult.Json(new
                {
                    name = actor.Name,
                    level = document.Name,
                    classPath = actor.ClassPath,
                    kind = actor.Kind.ToString(),
                    mesh = actor.StaticMeshPath,
                    worldLocation = Vec(actor.WorldTransform.Translation),
                    worldRotation = Rot(actor.WorldTransform.Rotator()),
                    worldScale = Vec(actor.WorldTransform.Scale3D, 3),
                    rootTransform = new { location = Vec(current.Location), rotation = Rot(current.Rotation), scale = Vec(current.Scale, 3) },
                    movedByProject = state?.GetTransformOverride(reference) is not null,
                    deletedByProject = state?.IsDeleted(reference) == true,
                    instances = actor.InstanceTransforms.Count,
                    components = actor.Components.Where(c => c.IsSceneComponent || c.StaticMeshPath is not null).Take(80).Select(c => new
                    {
                        name = c.Name,
                        @class = c.ClassName,
                        mesh = c.StaticMeshPath is null ? null : ShortName(c.StaticMeshPath),
                        instances = c.Instances.Count == 0 ? (int?)null : c.Instances.Count,
                        location = Vec(c.WorldTransform.Translation),
                    }),
                }));
            })
        { Title = "Actor details", ReadOnly = true, Idempotent = true };

        yield return new McpTool("delete_actors",
            "Deletes actors from a level (journaled, undoable). Deleting a building also removes the child actors it spawned when exported.",
            ToolSchema.Object()
                .String("level", "Level name or package path.", required: true)
                .Strings("actors", "Actor names to delete.", required: true)
                .Build(),
            (call, ct) => Task.FromResult(ApplyPerActor(call, ct, (reference, _, _) => new DeleteActorOp(reference))))
        { Title = "Delete actors", Destructive = true, Idempotent = true };

        yield return new McpTool("restore_actors",
            "Restores actors the project deleted (journaled).",
            ToolSchema.Object()
                .String("level", "Level name or package path.", required: true)
                .Strings("actors", "Actor names to restore.", required: true)
                .Build(),
            (call, ct) => Task.FromResult(ApplyPerActor(call, ct, (reference, _, _) => new RestoreActorOp(reference))))
        { Title = "Restore actors", Idempotent = true };

        yield return new McpTool("delete_all_of_kind",
            "Deletes every actor of the same kind in one or more levels or a whole cell, as one undoable edit: by static mesh " +
            "(StaticMeshActors, mesh roots and — unless includeInstances is false — every foliage/rock instance drawing it) or by " +
            "class (e.g. a Blueprint). Give the mesh/class path in 'value' or name an example actor in 'likeActor'.",
            ToolSchema.Object()
                .Strings("levels", "Level names or paths.")
                .String("cell", "Instead of levels: every sublevel of this cell (A_0 …).")
                .String("by", "Match by mesh or by class.", required: true, choices: ["mesh", "class"])
                .String("value", "Mesh object path (/Game/…/SM_X.SM_X) or class path (/Game/…/BP_X.BP_X_C or a short class name).")
                .String("likeActor", "Name of an actor in the first level whose mesh/class to match (instead of value).")
                .Boolean("includeInstances", "With by=mesh, also delete matching ISM/HISM instances (default true).")
                .Build(),
            DeleteAllOfKind)
        { Title = "Delete all of a kind", Destructive = true };

        yield return new McpTool("move_actor",
            "Sets the transform of an actor's root (journaled): absolute location / rotation / scale, and/or an offset added to the " +
            "current location. Units cm, rotation [pitch, yaw, roll] degrees.",
            ToolSchema.Object()
                .String("level", "Level name or package path.", required: true)
                .String("actor", "Actor name.", required: true)
                .Vector("location", "New location [x, y, z] (cm, world).")
                .Vector("offset", "Added to the location [dx, dy, dz] (cm).")
                .Vector("rotation", "New rotation [pitch, yaw, roll] (degrees).")
                .Vector("scale", "New scale [x, y, z].")
                .Build(),
            MoveActor)
        { Title = "Move / rotate / scale actor" };

        yield return new McpTool("duplicate_actor",
            "Copies an actor inside its level (journaled): a new actor with the same class, mesh and stored child actors at 'location' " +
            "or shifted by 'offset' (default 200 cm along +X).",
            ToolSchema.Object()
                .String("level", "Level name or package path.", required: true)
                .String("actor", "Actor to copy.", required: true)
                .Vector("location", "Location of the copy [x, y, z] (cm).")
                .Vector("offset", "Offset from the source [dx, dy, dz] (default [200, 0, 0]).")
                .Vector("rotation", "Rotation of the copy [pitch, yaw, roll].")
                .String("name", "Name of the copy (default <Source>_Copy, made unique).")
                .Build(),
            DuplicateActor)
        { Title = "Duplicate actor" };

        yield return new McpTool("add_static_mesh",
            "Places a new StaticMeshActor with the given mesh in a level (journaled). Find meshes with search_assets (className StaticMesh).",
            ToolSchema.Object()
                .String("level", "Level name or package path.", required: true)
                .String("mesh", "Static mesh path (/Game/…/SM_X or /Game/…/SM_X.SM_X).", required: true)
                .Vector("location", "Location [x, y, z] (cm, world).", required: true)
                .Vector("rotation", "Rotation [pitch, yaw, roll] (default 0).")
                .Vector("scale", "Scale [x, y, z] (default 1).")
                .Build(),
            AddStaticMesh)
        { Title = "Add static mesh actor" };

        yield return new McpTool("copy_actor_to_level",
            "Copies an actor (a building Blueprint with its child actors, or a mesh actor) from one level into another level at a new " +
            "place (journaled). Within the same level this is a duplicate.",
            ToolSchema.Object()
                .String("sourceLevel", "Level of the actor to copy.", required: true)
                .String("actor", "Actor to copy.", required: true)
                .String("targetLevel", "Level that receives the copy.", required: true)
                .Vector("location", "Location [x, y, z] (cm, world). Default: the source location.")
                .Vector("rotation", "Rotation [pitch, yaw, roll]. Default: the source rotation.")
                .Vector("scale", "Scale. Default: the source scale.")
                .Build(),
            CopyActorToLevel)
        { Title = "Copy actor to another level" };

        yield return new McpTool("level_references",
            "Follows every package reference of the given levels or cell (meshes, Blueprints, materials, textures, built data) and " +
            "reports how many are present in the open game files and which folders are missing (useful for extracted folders).",
            ToolSchema.Object()
                .Strings("levels", "Level names or paths.")
                .String("cell", "Every sublevel of this cell.")
                .Integer("top", "How many folders to list (default 15).", minimum: 1, maximum: 200)
                .Build(),
            (call, ct) =>
            {
                var roots = LevelsFrom(call);
                var report = new PackageDependencyWalker(RequireCatalog(), _logger).Walk(roots, cancellationToken: ct);
                var top = call.GetInt("top", 15);
                return Task.FromResult(McpToolResult.Json(new
                {
                    levels = report.Roots.Count,
                    present = report.Present.Count,
                    files = report.Files.Count,
                    missing = report.Missing.Count,
                    unresolvedEngineReferences = report.Unresolved.Count,
                    missingByFolder = report.MissingByFolder().Take(top).Select(f => new { folder = f.Key, count = f.Value }),
                }));
            })
        { Title = "Level references", ReadOnly = true, Idempotent = true };
    }

    private McpToolResult ApplyPerActor(ToolCall call, CancellationToken cancellationToken, Func<ActorRef, LevelDocument, ActorRecord?, EditOp> create)
    {
        var project = RequireProject();
        var path = ResolveLevel(call.RequireString("level"));
        var document = Level(path, cancellationToken);
        var names = call.GetStrings("actors");
        if (names.Count == 0)
        {
            throw new ToolArgumentException("Give at least one actor name in 'actors'.");
        }

        var done = new List<string>();
        var failed = new List<string>();
        foreach (var name in names)
        {
            var reference = new ActorRef(path, name);
            var actor = document.FindActor(name);
            if (actor is null && !project.State.IsAdded(reference))
            {
                failed.Add($"{name}: not an actor of {document.Name}");
                continue;
            }

            var op = create(reference, document, actor);
            if (project.State.Validate(op) is { } error)
            {
                failed.Add($"{name}: {error}");
                continue;
            }

            var entry = _host.Apply(op);
            done.Add($"#{entry.Seq} {op.Describe()}");
        }

        var text = (done.Count > 0 ? string.Join('\n', done) : "Nothing changed.") + (failed.Count > 0 ? "\nSkipped:\n" + string.Join('\n', failed) : string.Empty);
        return done.Count == 0 && failed.Count > 0 ? McpToolResult.Error(text) : McpToolResult.Text(text);
    }

    private Task<McpToolResult> DeleteAllOfKind(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var levels = LevelsFrom(call);
        var documents = levels.Select(l => Level(l, cancellationToken)).ToList();
        var by = call.RequireString("by").ToLowerInvariant() switch
        {
            "mesh" => MatchBy.StaticMesh,
            "class" => MatchBy.Class,
            _ => throw new ToolArgumentException("'by' must be 'mesh' or 'class'."),
        };
        string value;
        if (call.GetString("value") is { Length: > 0 } given)
        {
            value = given;
        }
        else if (call.GetString("likeActor") is { Length: > 0 } like)
        {
            var example = documents.Select(d => d.FindActor(like)).FirstOrDefault(a => a is not null)
                          ?? throw new ToolArgumentException($"'{like}' is not an actor of the given levels.");
            value = by == MatchBy.Class ? example.ClassPath : example.StaticMeshPath ?? throw new ToolArgumentException($"{like} has no static mesh; use by=class.");
        }
        else
        {
            throw new ToolArgumentException("Give 'value' (mesh or class path) or 'likeActor'.");
        }

        var scope = call.GetString("cell") is { } cellText && MapCell.TryParse(cellText, out var cell)
            ? EditScope.ForCell(cell)
            : documents.Count == 1 ? EditScope.ForLevel(documents[0].PackagePath) : EditScope.Island;
        var op = EditOpFactory.DeleteAllOfKind(documents, new KindMatch(by, value), scope, project.State, call.GetBool("includeInstances", true));
        if (project.State.Validate(op) is { } error)
        {
            return Task.FromResult(McpToolResult.Error($"Nothing deleted: {error}"));
        }

        var entry = _host.Apply(op);
        return Task.FromResult(McpToolResult.Text($"#{entry.Seq} {op.Describe()} (searched {documents.Count} level(s))."));
    }

    private Task<McpToolResult> MoveActor(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var path = ResolveLevel(call.RequireString("level"));
        var document = Level(path, cancellationToken);
        var name = call.RequireString("actor");
        var reference = new ActorRef(path, name);
        var actor = document.FindActor(name);
        var added = project.State.IsAdded(reference);
        if (actor is null && !added)
        {
            throw new ToolArgumentException($"'{name}' is not an actor of {document.Name}. Use list_actors.");
        }

        var current = added
            ? project.State.GetAddedTransform(reference)!.Value
            : project.State.GetTransformOverride(reference) ?? actor!.Root?.Relative ?? TransformValue.FromTransform(actor.WorldTransform);
        var location = call.GetVector3("location") is { } l ? ToVector(l) : current.Location;
        if (call.GetVector3("offset") is { } o)
        {
            location = new FVector(location.X + o.X, location.Y + o.Y, location.Z + o.Z);
        }

        var target = new TransformValue(
            location,
            call.GetVector3("rotation") is { } r ? ToRotator(r) : current.Rotation,
            call.GetVector3("scale") is { } s ? ToVector(s) : current.Scale);
        if (target == current)
        {
            return Task.FromResult(McpToolResult.Text($"{name} is already there; nothing changed."));
        }

        var op = added
            ? EditOpFactory.SetAddedActorTransform(reference, target, project.State)
            : EditOpFactory.SetTransform(document, actor!, target, project.State);
        var entry = _host.Apply(op);
        return Task.FromResult(McpToolResult.Text($"#{entry.Seq} {op.Describe()}"));
    }

    private Task<McpToolResult> DuplicateActor(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var path = ResolveLevel(call.RequireString("level"));
        var document = Level(path, cancellationToken);
        var name = call.RequireString("actor");
        var actor = document.FindActor(name) ?? throw new ToolArgumentException($"'{name}' is not a stored actor of {document.Name} (added actors cannot be duplicated).");
        var reference = new ActorRef(path, name);
        var current = project.State.GetTransformOverride(reference) ?? actor.Root?.Relative ?? TransformValue.FromTransform(actor.WorldTransform);
        var location = call.GetVector3("location") is { } l ? ToVector(l) : current.Location;
        var offset = call.GetVector3("offset") ?? (call.Has("location") ? (0f, 0f, 0f) : (200f, 0f, 0f));
        location = new FVector(location.X + offset.X, location.Y + offset.Y, location.Z + offset.Z);
        var transform = current with { Location = location, Rotation = call.GetVector3("rotation") is { } r ? ToRotator(r) : current.Rotation };
        var op = EditOpFactory.Duplicate(document, actor, transform, project.State);
        if (call.GetString("name") is { Length: > 0 } newName)
        {
            op = op with { NewName = EditOpFactory.UniqueActorName(document, newName, project.State) };
        }

        var entry = _host.Apply(op);
        return Task.FromResult(McpToolResult.Text($"#{entry.Seq} {op.Describe()} → new actor '{op.NewName}'"));
    }

    private Task<McpToolResult> AddStaticMesh(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var catalog = RequireCatalog();
        var path = ResolveLevel(call.RequireString("level"));
        var document = Level(path, cancellationToken);
        var mesh = AssetPaths.NormalizeObjectPath(call.RequireString("mesh"), catalog.ProjectName);
        if (!catalog.PackageExists(mesh))
        {
            throw new ToolArgumentException($"Static mesh not found: {mesh}. Use search_assets with className StaticMesh.");
        }

        if (FarModels.IsFarViewMesh(mesh))
        {
            throw new ToolArgumentException($"{mesh} is a far-view model (blurred, no collision, merged with its surroundings): copy the real building instead.");
        }

        if (FarModels.IsUndersideMesh(mesh, catalog))
        {
            throw new ToolArgumentException($"{mesh} is the underside of the water (flipped normals, underwater material): the game never shows it as a placed object."
                + (FarModels.TopSideOf(mesh, catalog) is { } top ? $" Place {top} instead." : string.Empty));
        }

        var transform = new TransformValue(
            ToVector(call.GetVector3("location") ?? throw new ToolArgumentException("'location' is required.")),
            call.GetVector3("rotation") is { } r ? ToRotator(r) : new FRotator(0, 0, 0),
            call.GetVector3("scale") is { } s ? ToVector(s) : new FVector(1, 1, 1));
        var op = EditOpFactory.AddStaticMeshActor(document, mesh, transform, project.State);
        var entry = _host.Apply(op);
        return Task.FromResult(McpToolResult.Text($"#{entry.Seq} {op.Describe()} → new actor '{op.NewName}'"));
    }

    private Task<McpToolResult> CopyActorToLevel(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var sourcePath = ResolveLevel(call.RequireString("sourceLevel"));
        var targetPath = ResolveLevel(call.RequireString("targetLevel"));
        var source = Level(sourcePath, cancellationToken);
        var target = Level(targetPath, cancellationToken);
        var name = call.RequireString("actor");
        var actor = source.FindActor(name) ?? throw new ToolArgumentException($"'{name}' is not a stored actor of {source.Name}.");
        var reference = new ActorRef(sourcePath, name);
        var sameLevel = string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);
        var moved = project.State.GetTransformOverride(reference);
        var current = moved ?? actor.Root?.Relative ?? TransformValue.FromTransform(actor.WorldTransform);
        if (!sameLevel && actor.Root?.AttachParent is { } attach
            && source.Actors.SelectMany(a => a.Components).FirstOrDefault(c => c.ExportIndex == attach) is { } parent)
        {
            // A copy in another level stands in the world: a building's door is relative to its component (all zero there).
            current = TransformValue.FromTransform(moved is { } m ? m.ToTransform() * parent.WorldTransform : actor.WorldTransform);
        }

        var transform = new TransformValue(
            call.GetVector3("location") is { } l ? ToVector(l) : current.Location,
            call.GetVector3("rotation") is { } r ? ToRotator(r) : current.Rotation,
            call.GetVector3("scale") is { } s ? ToVector(s) : current.Scale);
        EditOp op;
        if (sameLevel)
        {
            op = new DuplicateActorOp(reference, EditOpFactory.UniqueActorName(target, name + "_Copy", project.State), transform);
        }
        else if (actor.Kind == ActorKind.StaticMeshActor && actor.StaticMeshPath is { } mesh)
        {
            op = EditOpFactory.AddStaticMeshActor(target, mesh, transform, project.State);
        }
        else
        {
            var baseName = actor.ClassName.EndsWith("_C", StringComparison.Ordinal) ? actor.ClassName[..^2] : actor.ClassName;
            op = new AddBlueprintActorOp(targetPath, EditOpFactory.UniqueActorName(target, baseName + "_Added", project.State), actor.ClassPath, reference, transform);
        }

        var entry = _host.Apply(op);
        return Task.FromResult(McpToolResult.Text($"#{entry.Seq} {op.Describe()}"));
    }

    private List<string> LevelsFrom(ToolCall call)
    {
        var levels = call.GetStrings("levels").Select(ResolveLevel).ToList();
        if (call.GetString("cell") is { } cellText)
        {
            if (!MapCell.TryParse(cellText, out var cell))
            {
                throw new ToolArgumentException($"'{cellText}' is not a map cell (A_0 … D_4, Z_0 … Z_4).");
            }

            levels.AddRange(RequireWorld().Sublevels.Where(p => p.Cell == cell).Select(p => p.PackagePath));
        }

        if (call.GetString("level") is { } single)
        {
            levels.Add(ResolveLevel(single));
        }

        levels = levels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return levels.Count > 0 ? levels : throw new ToolArgumentException("Give 'levels' (names) or 'cell'.");
    }

    private static string AddedClass(EditOp op, LevelDocument document) => op switch
    {
        DuplicateActorOp d => document.FindActor(d.Source.Actor)?.ClassName ?? "Copy",
        AddStaticMeshActorOp => "StaticMeshActor",
        AddBlueprintActorOp b => ShortName(b.ClassPath),
        _ => op.GetType().Name,
    };

    private static string? AddedMesh(EditOp op) => op switch
    {
        AddStaticMeshActorOp s => s.StaticMesh,
        _ => null,
    };
}
