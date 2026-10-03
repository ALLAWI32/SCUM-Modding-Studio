using System.Text.Json;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Mcp.Studio;

/// <content>Vehicles/weapons tools and asset search.</content>
public sealed partial class StudioTools
{
    private IEnumerable<McpTool> ItemTools()
    {
        yield return new McpTool("list_items",
            "Lists vehicles (BPC_*), weapons (Weapon_*), magazines, ammunition (Cal_*) and projectiles (BP_WeaponBullet_*) of the " +
            "game files plus the project's clones, with their entity setup (_ES: in-game name, weight, inventory size).",
            ToolSchema.Object()
                .String("kind", "Only this kind.", choices: Enum.GetNames<ModdableKind>())
                .String("filter", "Words that must all appear in the name or folder.")
                .Integer("limit", "Most items to return (default 300).", minimum: 1, maximum: 5000)
                .Build(),
            (call, _) =>
            {
                var catalog = RequireCatalog();
                ModdableKind? kind = call.GetString("kind") is { } k
                    ? Enum.TryParse<ModdableKind>(k, ignoreCase: true, out var parsed) ? parsed : throw new ToolArgumentException($"Unknown kind '{k}'.")
                    : null;
                var tokens = Tokens(call.GetString("filter")).ToList();
                var state = _host.Project?.State;
                var items = ModdableAssets.Find(catalog)
                    .Where(a => kind is null || a.Kind == kind)
                    .Select(a => new { name = a.Name, kind = a.Kind.ToString(), category = a.Category, path = a.PackagePath, entitySetup = a.EntitySetupPath, cloneOf = (string?)null })
                    .ToList();
                if (state is not null)
                {
                    foreach (var clone in state.AssetClones)
                    {
                        if (ModdableAssets.Classify(clone.Template) is { } template && (kind is null || template.Kind == kind))
                        {
                            var es = clone.Packages.Select(p => p.New).FirstOrDefault(p => p.EndsWith("_ES", StringComparison.OrdinalIgnoreCase));
                            items.Add(new { name = ShortName(clone.NewPrimary), kind = template.Kind.ToString(), category = template.Category, path = clone.NewPrimary, entitySetup = es, cloneOf = (string?)ShortName(clone.Template) });
                        }
                    }
                }

                var matches = items.Where(i => MatchesAll(tokens, i.path)).ToList();
                var limit = call.GetInt("limit", 300);
                return Task.FromResult(McpToolResult.Json(new { total = matches.Count, shown = Math.Min(limit, matches.Count), items = matches.Take(limit) }));
            })
        { Title = "List vehicles and weapons", ReadOnly = true, Idempotent = true };

        yield return new McpTool("get_item_values",
            "The stored values of a vehicle/weapon/magazine/ammo/projectile package or its entity setup (_ES) or a vehicle attachment: " +
            "key (export|path, what set_item_values takes), name, type, stock value, current value in the project, enum choices. " +
            "Cooked Blueprints store only values that differ from their parent class.",
            ToolSchema.Object()
                .String("item", "Asset name (Weapon_RPK-74, BPC_WolfsWagen, Weapon_RPK-74_ES, a clone's name) or package path.", required: true)
                .String("filter", "Words that must all appear in the key (e.g. 'damage', 'mass', 'radius').")
                .Integer("limit", "Most values to return (default 300).", minimum: 1, maximum: 5000)
                .Build(),
            (call, _) =>
            {
                var catalog = RequireCatalog();
                var state = _host.Project?.State;
                var path = AssetEditing.ResolvePackage(catalog, state, call.RequireString("item"))
                           ?? throw new ToolArgumentException($"No item or package '{call.GetString("item")}'. Use list_items.");
                var tokens = Tokens(call.GetString("filter")).ToList();
                var tunables = TunableReader.Read(AssetEditing.ReadForEditing(catalog, state, path))
                    .Where(t => MatchesAll(tokens, t.Key))
                    .ToList();
                var limit = call.GetInt("limit", 300);
                return Task.FromResult(McpToolResult.Json(new
                {
                    package = path,
                    isClone = state?.FindCloneOf(path) is not null,
                    total = tunables.Count,
                    values = tunables.Take(limit).Select(t =>
                    {
                        var current = state?.GetAssetValue(path, t.Export, t.Path)?.Current ?? t.Value;
                        return new
                        {
                            key = t.Key,
                            name = t.Name,
                            type = t.Kind.ToString(),
                            stock = t.Value,
                            current,
                            edited = !TunableValue.AreEqual(t.Kind, current, t.Value) ? true : (bool?)null,
                            choices = t.Kind == TunableKind.Enum ? t.Choices : null,
                            readOnly = t.CanEdit ? (bool?)null : true,
                        };
                    }),
                }));
            })
        { Title = "Vehicle/weapon values", ReadOnly = true, Idempotent = true };

        yield return new McpTool("set_item_values",
            "Changes stored values of a vehicle/weapon package (journaled, undoable): each entry is a key from get_item_values and the " +
            "new value as text (numbers with '.', true/false, an enum choice like EWeaponCategory::Rifles, \"x, y, z\" for vectors, " +
            "plain text for captions).",
            ToolSchema.Object()
                .String("item", "Asset name or package path (the same as for get_item_values).", required: true)
                .Objects("values", "Values to set.", ToolSchema.Object()
                    .String("key", "Value key: <export>|<path>.", required: true)
                    .String("value", "New value.", required: true), required: true)
                .Build(),
            SetItemValues)
        { Title = "Change vehicle/weapon values" };

        yield return new McpTool("clone_item",
            "Clones a stock vehicle or item under a new name (journaled): items copy the item and its _ES; vehicles copy the whole " +
            "family (entity setup, item container, anim/physics assets, curves, mount slots, attachments, manual spawn presets) while " +
            "meshes and textures stay shared. The clone is registered in AssetRegistry.bin at export, so #SpawnItem <newName> or " +
            "#SpawnVehicle BPC_<newName> finds it. 'caption' sets its in-game name.",
            ToolSchema.Object()
                .String("template", "Stock asset name or path (Weapon_RPK-74, BPC_WolfsWagen, …).", required: true)
                .String("newName", "Items: the new asset name (Weapon_RPK-74_Gold). Vehicles: the new token (Hunter → BPC_Hunter).", required: true)
                .String("caption", "In-game name written to the clone's entity setup.")
                .Boolean("includeAttachments", "Vehicles: also clone the attachment classes (default true).")
                .Boolean("includeSpawnPresets", "Vehicles: also clone the manual spawn presets (default true).")
                .Build(),
            CloneItem)
        { Title = "Clone vehicle/item" };

        yield return new McpTool("remove_item_clone",
            "Removes a clone made by clone_item (its edited values are reset first; journaled).",
            ToolSchema.Object().String("item", "The clone's name or package path.", required: true).Build(),
            (call, _) =>
            {
                var project = RequireProject();
                var catalog = RequireCatalog();
                var path = AssetEditing.ResolvePackage(catalog, project.State, call.RequireString("item")) ?? throw new ToolArgumentException("No such clone.");
                var clone = project.State.FindCloneOf(path) ?? throw new ToolArgumentException($"{ShortName(path)} is not a clone of this project.");
                var lines = new List<string>();
                foreach (var value in clone.Packages.SelectMany(p => project.State.GetAssetValues(p.New)).ToList())
                {
                    var reset = _host.Apply(new SetAssetValueOp(value.Package, value.Export, value.Path, value.ValueKind, value.Current, value.Base));
                    lines.Add($"#{reset.Seq} {reset.Op.Describe()}");
                }

                var entry = _host.Apply(clone.Inverse());
                lines.Add($"#{entry.Seq} {entry.Op.Describe()}");
                return Task.FromResult(McpToolResult.Text(string.Join('\n', lines)));
            })
        { Title = "Remove clone", Destructive = true };
    }

    private Task<McpToolResult> SetItemValues(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var catalog = RequireCatalog();
        var path = AssetEditing.ResolvePackage(catalog, project.State, call.RequireString("item"))
                   ?? throw new ToolArgumentException($"No item or package '{call.GetString("item")}'. Use list_items.");
        if (call.GetElement("values") is not { ValueKind: JsonValueKind.Array } values)
        {
            throw new ToolArgumentException("'values' must be a list of {key, value}.");
        }

        var tunables = TunableReader.Read(AssetEditing.ReadForEditing(catalog, project.State, path)).ToDictionary(t => t.Key, StringComparer.Ordinal);
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var item in values.EnumerateArray())
        {
            var key = item.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : null;
            var value = item.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
            if (key is null || value is null)
            {
                failed.Add("An entry needs 'key' and 'value'.");
                continue;
            }

            if (!tunables.TryGetValue(key, out var tunable))
            {
                failed.Add($"{key}: not a value of {ShortName(path)} (see get_item_values).");
                continue;
            }

            if (!tunable.CanEdit)
            {
                failed.Add($"{key}: read-only ({tunable.ReadOnlyReason}).");
                continue;
            }

            string normalized;
            try
            {
                normalized = Normalize(tunable, value);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                failed.Add($"{key}: {ex.Message}");
                continue;
            }

            var current = project.State.GetAssetValue(path, tunable.Export, tunable.Path)?.Current ?? tunable.Value;
            var op = new SetAssetValueOp(path, tunable.Export, tunable.Path, tunable.Kind.ToString(), current, normalized);
            if (project.State.Validate(op) is { } error)
            {
                failed.Add($"{key}: {error}");
                continue;
            }

            var entry = _host.Apply(op);
            done.Add($"#{entry.Seq} {op.Describe()}");
        }

        var text = (done.Count > 0 ? string.Join('\n', done) : "Nothing changed.") + (failed.Count > 0 ? "\nSkipped:\n" + string.Join('\n', failed) : string.Empty);
        return Task.FromResult(done.Count == 0 && failed.Count > 0 ? McpToolResult.Error(text) : McpToolResult.Text(text));
    }

    private Task<McpToolResult> CloneItem(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var catalog = RequireCatalog();
        var template = AssetEditing.ResolvePackage(catalog, null, call.RequireString("template"))
                       ?? throw new ToolArgumentException($"No stock asset '{call.GetString("template")}'. Use list_items.");
        var asset = ModdableAssets.Classify(template) ?? throw new ToolArgumentException($"{ShortName(template)} is not a vehicle or item.");
        var newName = call.RequireString("newName");
        ClonePlan plan;
        try
        {
            plan = asset.Kind == ModdableKind.Vehicle
                ? CloneFamilyPlanner.PlanVehicle(catalog, template, newName, new VehicleCloneOptions
                {
                    IncludeAttachments = call.GetBool("includeAttachments", true),
                    IncludeSpawnPresets = call.GetBool("includeSpawnPresets", true),
                })
                : CloneFamilyPlanner.PlanItem(catalog, template, newName);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(McpToolResult.Error(ex.Message));
        }

        var op = new CloneAssetOp(asset.Kind.ToString().ToLowerInvariant(), template, plan.NewPrimary, plan.Packages.Select(p => new PackagePair(p.Key, p.Value)).ToList());
        var entry = _host.Apply(op);
        var lines = new List<string> { $"#{entry.Seq} {op.Describe()}" };
        if (call.GetString("caption") is { Length: > 0 } caption)
        {
            var es = plan.Packages.Select(p => p.Value).FirstOrDefault(p => p.EndsWith("_ES", StringComparison.OrdinalIgnoreCase));
            var captionValue = es is null ? null : TunableReader.Read(AssetEditing.ReadForEditing(catalog, project.State, es))
                .FirstOrDefault(t => t.Name == "Caption" && t.Kind == TunableKind.Text && t.CanEdit);
            if (captionValue is null)
            {
                lines.Add("No entity setup Caption to set; the clone keeps the stock name.");
            }
            else
            {
                var set = _host.Apply(new SetAssetValueOp(es!, captionValue.Export, captionValue.Path, nameof(TunableKind.Text), captionValue.Value, caption));
                lines.Add($"#{set.Seq} {set.Op.Describe()}");
            }
        }

        var leaf = ShortName(plan.NewPrimary);
        lines.Add(asset.Kind == ModdableKind.Vehicle ? $"In game after export: #SpawnVehicle {leaf}" : $"In game after export: #SpawnItem {leaf}");
        lines.Add("Packages: " + string.Join(", ", plan.Packages.Take(12).Select(p => ShortName(p.Value))) + (plan.Packages.Count > 12 ? $" … ({plan.Packages.Count} total)" : string.Empty));
        return Task.FromResult(McpToolResult.Text(string.Join('\n', lines)));
    }

    private static string Normalize(Tunable tunable, string value) => tunable.Kind switch
    {
        TunableKind.Float => TunableValue.Format(TunableValue.ParseFloat(value)),
        TunableKind.Bool => TunableValue.ParseBool(value) ? "true" : "false",
        TunableKind.Int => long.Parse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture),
        TunableKind.UInt => ulong.Parse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture),
        TunableKind.Vector or TunableKind.Rotator => TunableValue.Format(TunableValue.ParseFloats(value, 3)),
        TunableKind.Color => TunableValue.Format(TunableValue.ParseFloats(value, 4)),
        TunableKind.Enum => tunable.Choices.Contains(value.Trim(), StringComparer.Ordinal)
            ? value.Trim()
            : throw new FormatException($"'{value}' is not one of: {string.Join(", ", tunable.Choices)}."),
        _ => value,
    };

    private IEnumerable<McpTool> AssetTools()
    {
        yield return new McpTool("search_assets",
            "Searches the game files by package path words (e.g. 'barrel', 'SM_Rock', 'Outpost wall'); optionally only one class " +
            "(StaticMesh, SkeletalMesh, Texture2D, Material, MaterialInstanceConstant, BlueprintGeneratedClass, World …).",
            ToolSchema.Object()
                .String("query", "Words that must all appear in the package path.", required: true)
                .String("className", "Only packages whose main export has this class.")
                .Integer("limit", "Most results (default 50).", minimum: 1, maximum: 1000)
                .Build(),
            (call, ct) =>
            {
                var catalog = RequireCatalog();
                var tokens = Tokens(call.RequireString("query")).ToList();
                var className = call.GetString("className");
                var limit = call.GetInt("limit", 50);
                var results = new List<object>();
                var scanned = 0;
                foreach (var file in catalog.PackageFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = AssetPaths.ToPackagePath(file, catalog.ProjectName);
                    if (!MatchesAll(tokens, path))
                    {
                        continue;
                    }

                    if (++scanned > 5000 || results.Count >= limit)
                    {
                        break;
                    }

                    string? cls;
                    try
                    {
                        cls = catalog.GetMainClassName(path);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or InvalidOperationException)
                    {
                        cls = null;
                    }

                    if (className is not null && !string.Equals(cls, className, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    results.Add(new { path, objectPath = AssetPaths.ToObjectPath(path, null, catalog.ProjectName), @class = cls });
                }

                return Task.FromResult(McpToolResult.Json(new { count = results.Count, results }));
            })
        { Title = "Search assets", ReadOnly = true, Idempotent = true };

        yield return new McpTool("get_asset",
            "Exports (objects) of one package with their classes.",
            ToolSchema.Object().String("path", "Package or object path.", required: true).Build(),
            (call, _) =>
            {
                var catalog = RequireCatalog();
                var path = call.RequireString("path");
                if (!catalog.PackageExists(path))
                {
                    throw new ToolArgumentException($"Package not found: {path}");
                }

                var exports = catalog.GetExports(path);
                return Task.FromResult(McpToolResult.Json(new
                {
                    path = AssetPaths.SplitObjectPath(path).PackagePath,
                    mainClass = catalog.GetMainClassName(path),
                    exports = exports.Take(200).Select(e => new { name = e.Name, @class = e.ClassName }),
                    exportCount = exports.Count,
                }));
            })
        { Title = "Asset details", ReadOnly = true, Idempotent = true };
    }
}
