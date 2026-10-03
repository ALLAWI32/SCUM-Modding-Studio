using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Serialization;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio item list|stats|set|clone</c>: the Vehicles/Weapons modules from the command line. <c>list</c> and
/// <c>stats</c> read the game files; <c>set</c> and <c>clone</c> record journal operations in a project (exported by
/// <c>project export</c> together with the map edits).
/// </summary>
internal sealed class ItemCommands : ICommandModule
{
    /// <inheritdoc />
    public Command Build()
    {
        var item = new Command("item", "Vehicles and weapons: list them, show and change their stored values, clone them under a new name.");
        item.AddCommand(BuildList());
        item.AddCommand(BuildStats());
        item.AddCommand(BuildSet());
        item.AddCommand(BuildClone());
        return item;
    }

    private static Argument<string> SourceArgument() =>
        new("source", "Paks folder, single .pak, or loose folder containing SCUM/Content (or Content).");

    private static Option<string?> AesOption() =>
        new(["--aes", "-a"], $"AES-256 key for encrypted stock paks (prefer the {Pak.AesKeyText.EnvironmentVariable} environment variable).");

    private static Command BuildList()
    {
        var source = SourceArgument();
        var aes = AesOption();
        var kind = new Option<ModdableKind?>(["--kind", "-k"], "Only this kind (Vehicle, Weapon, Magazine, Ammo, Projectile).");
        var command = new Command("list", "List vehicles, weapons, magazines, ammunition and projectiles as '<Kind>\\t<Category>\\t<Name>\\t<PackagePath>'.") { source, aes, kind };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ItemCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var filter = parse.GetValueForOption(kind);
                var assets = ModdableAssets.Find(catalog).Where(a => filter is null || a.Kind == filter).ToList();
                foreach (var a in assets)
                {
                    Console.Out.WriteLine($"{a.Kind}\t{a.Category}\t{a.Name}\t{a.PackagePath}");
                }

                logger.LogInformation("{Count} asset(s) in {Source}.", assets.Count, catalog.DisplayName);
                return 0;
            });
        });
        return command;
    }

    private static Command BuildStats()
    {
        var source = SourceArgument();
        var package = new Argument<string>("package", "Package path or asset name (e.g. Weapon_RPK-74, BPC_WolfsWagen, /Game/ConZ_Files/.../Weapon_RPK-74_ES).");
        var aes = AesOption();
        var filter = new Option<string?>(["--filter", "-f"], "Only values whose name or path contains this text.");
        var json = new Option<bool>("--json", "Print JSON.");
        var command = new Command("stats", "Show every stored value of a package: '<export>|<path>\\t<kind>\\t<value>' (the keys 'item set' takes).") { source, package, aes, filter, json };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ItemCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var path = Resolve(catalog, parse.GetValueForArgument(package));
                if (path is null)
                {
                    logger.LogError("Package not found: {Package} (use 'scumstudio item list').", parse.GetValueForArgument(package));
                    return 2;
                }

                var text = parse.GetValueForOption(filter);
                var tunables = TunableReader.Read(ModdableAssets.ReadPackage(catalog, path))
                    .Where(t => text is null || t.Path.Contains(text, StringComparison.OrdinalIgnoreCase) || t.Export.Contains(text, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (parse.GetValueForOption(json))
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new { Package = path, Values = tunables }, LevelJson.Indented));
                }
                else
                {
                    Console.Out.WriteLine($"package: {path}");
                    foreach (var t in tunables)
                    {
                        Console.Out.WriteLine($"{t.Key}\t{t.Kind}{(t.CanEdit ? string.Empty : " (read-only)")}\t{t.Value}");
                    }
                }

                logger.LogInformation("{Count} value(s).", tunables.Count);
                return 0;
            });
        });
        return command;
    }

    private static Command BuildSet()
    {
        var project = new Argument<string>("projectDir", "Project folder (e.g. MyMod.ssproj).");
        var source = SourceArgument();
        var package = new Argument<string>("package", "Package path or asset name.");
        var key = new Argument<string>("key", "Value key '<export>|<path>' as printed by 'item stats'.");
        var value = new Argument<string>("value", "New value (invariant: 2.5, 100, true, EWeaponCategory::Rifles, \"1, 2, 3\", or text).");
        var aes = AesOption();
        var command = new Command("set", "Record a value change in the project journal (exported by 'project export').") { project, source, package, key, value, aes };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ItemCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var proj = Project.Open(parse.GetValueForArgument(project));
                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var path = Resolve(catalog, parse.GetValueForArgument(package)) ?? CloneTarget(proj, parse.GetValueForArgument(package));
                if (path is null)
                {
                    logger.LogError("Package not found: {Package}.", parse.GetValueForArgument(package));
                    return 2;
                }

                var tunables = TunableReader.Read(ReadForProject(catalog, proj, path));
                var tunable = tunables.FirstOrDefault(t => t.Key == parse.GetValueForArgument(key));
                if (tunable is null)
                {
                    logger.LogError("{Package} has no value '{Key}' (see 'scumstudio item stats').", path, parse.GetValueForArgument(key));
                    return 2;
                }

                var current = proj.State.GetAssetValue(path, tunable.Export, tunable.Path)?.Current ?? tunable.Value;
                var op = new SetAssetValueOp(path, tunable.Export, tunable.Path, tunable.Kind.ToString(), current, parse.GetValueForArgument(value));
                if (proj.State.Validate(op) is { } error)
                {
                    logger.LogError("{Error}", error);
                    return 2;
                }

                var entry = proj.Apply(op);
                Console.Out.WriteLine($"#{entry.Seq}: {op.Describe()}");
                return 0;
            });
        });
        return command;
    }

    private static Command BuildClone()
    {
        var project = new Argument<string>("projectDir", "Project folder (e.g. MyMod.ssproj).");
        var source = SourceArgument();
        var template = new Argument<string>("template", "Stock asset (name or package path), e.g. Weapon_RPK-74 or BPC_WolfsWagen.");
        var name = new Argument<string>("newName", "New name: the item name (Weapon_RPK-74_Gold) or the vehicle token (Hunter → BPC_Hunter).");
        var aes = AesOption();
        var noAttachments = new Option<bool>("--no-attachments", "Vehicles: keep the stock attachment classes.");
        var noPresets = new Option<bool>("--no-presets", "Vehicles: do not clone the manual spawn presets.");
        var command = new Command("clone", "Record a clone of a stock vehicle/item family under a new name in the project journal.") { project, source, template, name, aes, noAttachments, noPresets };
        command.SetHandler((InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<ItemCommands>();
            var parse = ctx.ParseResult;
            ctx.ExitCode = LevelCliSupport.Guarded(logger, () =>
            {
                using var proj = Project.Open(parse.GetValueForArgument(project));
                using var catalog = LevelCliSupport.OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var path = Resolve(catalog, parse.GetValueForArgument(template));
                if (path is null || ModdableAssets.Classify(path) is not { } asset)
                {
                    logger.LogError("Not a vehicle or item: {Template} (use 'scumstudio item list').", parse.GetValueForArgument(template));
                    return 2;
                }

                ClonePlan plan;
                try
                {
                    plan = asset.Kind == ModdableKind.Vehicle
                        ? CloneFamilyPlanner.PlanVehicle(catalog, path, parse.GetValueForArgument(name), new VehicleCloneOptions
                        {
                            IncludeAttachments = !parse.GetValueForOption(noAttachments),
                            IncludeSpawnPresets = !parse.GetValueForOption(noPresets),
                        })
                        : CloneFamilyPlanner.PlanItem(catalog, path, parse.GetValueForArgument(name));
                }
                catch (ArgumentException ex)
                {
                    logger.LogError("{Message}", ex.Message);
                    return 2;
                }

                var op = new CloneAssetOp(asset.Kind.ToString().ToLowerInvariant(), path, plan.NewPrimary, plan.Packages.Select(p => new PackagePair(p.Key, p.Value)).ToList());
                if (proj.State.Validate(op) is { } error)
                {
                    logger.LogError("{Error}", error);
                    return 2;
                }

                var entry = proj.Apply(op);
                Console.Out.WriteLine($"#{entry.Seq}: {op.Describe()}");
                foreach (var p in plan.Packages)
                {
                    Console.Out.WriteLine($"  {p.Key} -> {p.Value}");
                }

                Console.Out.WriteLine(asset.Kind == ModdableKind.Vehicle ? $"In game: #SpawnVehicle {PackageMap.Leaf(plan.NewPrimary)}" : $"In game: #SpawnItem {PackageMap.Leaf(plan.NewPrimary)}");
                return 0;
            });
        });
        return command;
    }

    /// <summary>A package path from a path or an asset name (searched among the moddable assets and their entity setups).</summary>
    private static string? Resolve(Assets.Catalog.AssetCatalog catalog, string nameOrPath)
    {
        if (nameOrPath.StartsWith('/'))
        {
            var p = PackageMap.Normalize(nameOrPath);
            return catalog.PackageExists(p) ? p : null;
        }

        foreach (var a in ModdableAssets.Find(catalog))
        {
            if (string.Equals(a.Name, nameOrPath, StringComparison.OrdinalIgnoreCase))
            {
                return a.PackagePath;
            }

            if (a.EntitySetupPath is { } es && string.Equals(PackageMap.Leaf(es), nameOrPath, StringComparison.OrdinalIgnoreCase))
            {
                return es;
            }
        }

        return null;
    }

    private static string? CloneTarget(Project project, string nameOrPath) =>
        project.State.AssetClones.SelectMany(c => c.Packages)
            .Select(p => p.New)
            .FirstOrDefault(p => string.Equals(p, nameOrPath, StringComparison.OrdinalIgnoreCase) || string.Equals(PackageMap.Leaf(p), nameOrPath, StringComparison.OrdinalIgnoreCase));

    private static Formats.Packages.CookedPackage ReadForProject(Assets.Catalog.AssetCatalog catalog, Project project, string path)
    {
        if (project.State.FindCloneOf(path) is not { } clone)
        {
            return ModdableAssets.ReadPackage(catalog, path);
        }

        var pair = clone.Packages.First(p => string.Equals(p.New, path, StringComparison.OrdinalIgnoreCase));
        var map = new PackageMap(clone.Packages.Select(p => new KeyValuePair<string, string>(p.Old, p.New)));
        var cloned = PackageCloner.Clone(ModdableAssets.ReadPackage(catalog, pair.Old), pair.Old, map);
        return Formats.Packages.CookedPackage.Parse(cloned.Bytes.UAsset, cloned.Bytes.UExp, cloned.UBulk, path);
    }
}
