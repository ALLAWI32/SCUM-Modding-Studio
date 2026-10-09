using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding;

/// <summary>What to build: clones and stored-value edits (on stock or cloned packages).</summary>
/// <param name="Clones">Clone plans, applied first.</param>
/// <param name="Edits">Package path → edits of that package (a clone's new path, or a stock package to override).</param>
public sealed record AssetModRequest(IReadOnlyList<ClonePlan> Clones, IReadOnlyDictionary<string, IReadOnlyList<TunableEdit>> Edits)
{
    /// <summary>Stock package → the stock package written in its place (ground textures, tree meshes).</summary>
    public IReadOnlyDictionary<string, string> Replacements { get; init; } = new Dictionary<string, string>();

    /// <summary>Data assets copied under a new path with ids of their own (a placed trader's personality, a new outpost's description).</summary>
    public IReadOnlyList<DataAssetCopy> DataAssets { get; init; } = [];

    /// <summary>Recipe package → its new ingredient list (craftables, see <see cref="Crafting.RecipeIngredients"/>), written after the clones.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Crafting.CraftIngredient>> Ingredients { get; init; } = new Dictionary<string, IReadOnlyList<Crafting.CraftIngredient>>();

    /// <summary>The <c>AssetRegistry.bin</c> to register into instead of the source's (the main pak's of the same export), or null.</summary>
    public byte[]? BaseRegistry { get; init; }

    /// <summary>True when nothing is requested.</summary>
    public bool IsEmpty => Clones.Count == 0 && Edits.Count == 0 && Replacements.Count == 0 && DataAssets.Count == 0;
}

/// <summary>
/// A data asset copied under a new package with a persistent id of its own: a placed trader's
/// <c>TraderPersonalityDataAsset</c> (<c>TraderPersistentId</c>, <c>HumanReadableTraderName</c>) or a new outpost's
/// <c>TradingOutpostDescriptionDataAsset</c> (<c>TradeOutpostPersistentId</c>). Primary assets (the personality) are
/// registered in <c>AssetRegistry.bin</c> like the template, so the game's asset scan lists them.
/// </summary>
/// <param name="Template">Object (or package) path of the stock asset.</param>
/// <param name="NewPackage">Package path of the copy.</param>
/// <param name="PersistentId">The 16 bytes written over the asset's <c>Guid</c> property.</param>
public sealed record DataAssetCopy(string Template, string NewPackage, byte[] PersistentId)
{
    /// <summary>Written over the asset's <c>HumanReadableTraderName</c>; null keeps it.</summary>
    public string? TraderName { get; init; }
}

/// <summary>A finished package to stage under <c>SCUM/Content/…</c>.</summary>
/// <param name="PackagePath">Package path.</param>
/// <param name="Bytes">Header + .uexp.</param>
/// <param name="UBulk">.ubulk, if any.</param>
/// <param name="IsClone">True for a new (cloned) package, false for an overridden stock package.</param>
public sealed record BuiltAssetPackage(string PackagePath, PackageBytes Bytes, byte[]? UBulk, bool IsClone)
{
    /// <summary>Pak/staging path of the header (<c>SCUM/Content/…/X.uasset</c>).</summary>
    public string HeaderFilePath(string projectName = AssetPaths.DefaultProjectName) =>
        AssetPaths.ToFilePathWithoutExtension(PackagePath, projectName) + ".uasset";
}

/// <summary>Result of <see cref="AssetModBuilder.Build"/>.</summary>
/// <param name="Packages">Every package to stage.</param>
/// <param name="AssetRegistry">The merged <c>AssetRegistry.bin</c> when clones were registered, else null.</param>
/// <param name="Registered">Registry records added.</param>
/// <param name="Applied">Edits written, as (package, key, old, new).</param>
/// <param name="Warnings">Problems that did not stop the build.</param>
public sealed record AssetModBuildResult(
    IReadOnlyList<BuiltAssetPackage> Packages,
    byte[]? AssetRegistry,
    IReadOnlyList<RegisteredAsset> Registered,
    IReadOnlyList<(string Package, string Key, string Old, string New)> Applied,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds the vehicle/weapon part of a mod from pristine packages: clones every planned family (names remapped,
/// payloads verbatim), writes the stat edits, and registers the clones in the source's effective
/// <c>AssetRegistry.bin</c> (so other installed mods' records are kept). The same request applied to a client and a
/// server catalog gives the two cooks of the mod.
/// </summary>
public static class AssetModBuilder
{
    /// <summary>Provider path of the cooked registry.</summary>
    public const string AssetRegistryPath = "SCUM/AssetRegistry.bin";

    /// <summary>Builds <paramref name="request"/> against <paramref name="catalog"/>.</summary>
    public static AssetModBuildResult Build(AssetCatalog catalog, AssetModRequest request)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string>();
        var built = new Dictionary<string, (CookedPackage Package, PackageBytes Bytes, bool IsClone)>(StringComparer.OrdinalIgnoreCase);
        var maps = new List<PackageMap>();

        foreach (var plan in request.Clones)
        {
            var map = plan.ToMap();
            maps.Add(map);
            foreach (var (oldPath, newPath) in plan.Packages)
            {
                CookedPackage source;
                try
                {
                    source = ModdableAssets.ReadPackage(catalog, oldPath);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException)
                {
                    warnings.Add($"{oldPath}: not cloned ({ex.Message}).");
                    continue;
                }

                var clone = PackageCloner.Clone(source, oldPath, map);
                var parsed = CookedPackage.Parse(clone.Bytes.UAsset, clone.Bytes.UExp, clone.UBulk, newPath);
                built[newPath] = (parsed, clone.Bytes, true);
            }
        }

        // A cloned melee weapon looks its hit damage up in WeaponDesc_Table by its own name: it gets a copy of its template's row.
        foreach (var plan in request.Clones.Where(c => ModdableAssets.IsMelee(c.Template)))
        {
            var table = DataTableEdits.WeaponDescTable;
            try
            {
                var package = built.TryGetValue(table, out var kept) ? kept.Package : ModdableAssets.ReadPackage(catalog, table);
                var bytes = DataTableEdits.AddRowCopies(package, [(PackageMap.Leaf(plan.Template), PackageMap.Leaf(plan.NewPrimary))]);
                built[table] = (CookedPackage.Parse(bytes.UAsset, bytes.UExp, package.UBulk, table), bytes, false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
            {
                warnings.Add($"{plan.NewPrimary}: no damage row of its own in {table} ({ex.Message}).");
            }
        }

        AddTradeRows(catalog, request.Clones, built, warnings);

        // New traders' personalities and new outposts' descriptions: a renamed copy with its own id (and trader name).
        foreach (var copy in request.DataAssets)
        {
            var template = PackageMap.Normalize(copy.Template);
            var path = PackageMap.Normalize(copy.NewPackage);
            if (built.ContainsKey(path))
            {
                continue;
            }

            try
            {
                var map = new PackageMap([new KeyValuePair<string, string>(template, path)]);
                var clone = PackageCloner.Clone(ModdableAssets.ReadPackage(catalog, template), template, map);
                var bytes = PatchDataAsset(CookedPackage.Parse(clone.Bytes.UAsset, clone.Bytes.UExp, clone.UBulk, path), copy);
                built[path] = (CookedPackage.Parse(bytes.UAsset, bytes.UExp, clone.UBulk, path), bytes, true);
                maps.Add(map);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
            {
                warnings.Add($"{path}: not created from {template} ({ex.Message}).");
            }
        }

        // Another stock asset under a stock path (owner: snow on the ground, pines where the oaks stand): a rename-copy.
        foreach (var (target, with) in request.Replacements)
        {
            var path = PackageMap.Normalize(target);
            try
            {
                var source = ModdableAssets.ReadPackage(catalog, with);
                var copy = PackageCloner.Clone(source, with, new PackageMap([new KeyValuePair<string, string>(with, path)]));
                built[path] = (CookedPackage.Parse(copy.Bytes.UAsset, copy.Bytes.UExp, copy.UBulk, path), copy.Bytes, false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException or ArgumentException)
            {
                warnings.Add($"{path}: not replaced with {with} ({ex.Message}).");
            }
        }

        // Craftables: each cloned recipe gets its own ingredient list (tag imports added where needed).
        foreach (var (recipe, ingredients) in request.Ingredients)
        {
            var path = PackageMap.Normalize(recipe);
            try
            {
                var found = built.TryGetValue(path, out var kept);
                var isClone = found && kept.IsClone;
                var package = found ? kept.Package : ModdableAssets.ReadPackage(catalog, path);
                var bytes = Crafting.RecipeIngredients.Rewrite(package, ingredients);
                built[path] = (CookedPackage.Parse(bytes.UAsset, bytes.UExp, package.UBulk, path), bytes, isClone);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
            {
                warnings.Add($"{path}: ingredients not written ({ex.Message}).");
            }
        }

        var applied = new List<(string, string, string, string)>();
        foreach (var (packagePath, edits) in request.Edits)
        {
            if (edits.Count == 0)
            {
                continue;
            }

            var path = PackageMap.Normalize(packagePath);
            CookedPackage package;
            var isClone = false;
            if (built.TryGetValue(path, out var existing))
            {
                package = existing.Package;
                isClone = existing.IsClone;
            }
            else
            {
                try
                {
                    package = ModdableAssets.ReadPackage(catalog, path);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException)
                {
                    warnings.Add($"{path}: edits skipped ({ex.Message}).");
                    continue;
                }
            }

            try
            {
                // Stored values first (same-size patches), then parameters by name (a material instance may gain entries).
                // Spawn preset slots and weapon sockets are rewritten whole (they add exports, imports and array items).
                var parts = edits.Where(e => VehicleParts.IsEdit(e.Path)).ToList();
                var mounts = edits.Where(e => WeaponMounts.IsEdit(e.Path)).ToList();
                var stored = edits.Where(e => !MaterialParameters.IsKeyed(e.Path) && !VehicleParts.IsEdit(e.Path) && !WeaponMounts.IsEdit(e.Path)).ToList();
                var keyed = edits.Where(e => MaterialParameters.IsKeyed(e.Path)).ToList();
                var bytes = PackageWriter.Rebuild(package);
                foreach (var (some, apply) in new (List<TunableEdit>, Func<CookedPackage, IEnumerable<TunableEdit>, PackageBytes>)[] { (parts, VehicleParts.Apply), (mounts, WeaponMounts.Apply) })
                {
                    if (some.Count > 0)
                    {
                        bytes = apply(package, some);
                        package = CookedPackage.Parse(bytes.UAsset, bytes.UExp, package.UBulk, path);
                        applied.AddRange(some.Select(e => (path, e.Export + "|" + e.Path, string.Empty, e.Value)));
                    }
                }

                if (stored.Count > 0)
                {
                    var result = TunablePatcher.Apply(package, stored);
                    bytes = result.Bytes;
                    package = CookedPackage.Parse(bytes.UAsset, bytes.UExp, package.UBulk, path);
                    applied.AddRange(result.Applied.Select(a => (path, a.Key, a.Old, a.New)));
                }

                if (keyed.Count > 0)
                {
                    bytes = MaterialParameters.Apply(package, keyed, ParentGuids(catalog, package, warnings));
                    applied.AddRange(keyed.Select(e => (path, e.Export + "|" + e.Path, string.Empty, e.Value)));
                }

                built[path] = (CookedPackage.Parse(bytes.UAsset, bytes.UExp, package.UBulk, path), bytes, isClone);
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add($"{path}: edits skipped ({ex.Message}).");
            }
        }

        byte[]? registryBytes = null;
        var registered = new List<RegisteredAsset>();
        if (maps.Count > 0)
        {
            var source = request.BaseRegistry ?? (catalog.Provider.Files.TryGetValue(AssetRegistryPath, out var registryFile) ? registryFile.Read() : null);
            if (source is not null)
            {
                var registry = AssetRegistryFile.Parse(source);
                foreach (var map in maps)
                {
                    registered.AddRange(RegistryCloner.Register(registry, map, warnings));
                }

                registryBytes = registry.Save();
            }
            else
            {
                warnings.Add($"{AssetRegistryPath} is not in {catalog.DisplayName}: the clones are built but not registered, so the game's spawn commands will not find them.");
            }
        }

        var packages = built
            .OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase)
            .Select(b => new BuiltAssetPackage(b.Key, b.Value.Bytes, b.Value.Package.UBulk, b.Value.IsClone))
            .ToList();
        return new AssetModBuildResult(packages, registryBytes, registered, applied, warnings);
    }

    /// <summary>
    /// A clone of something the traders sell (a car the mechanic has, a gun the armorer has) gets its template's row in
    /// <c>Table_TradeableDesc</c> under its own class and name, so it is sold too (owner: "sell my modded cars").
    /// </summary>
    private static void AddTradeRows(AssetCatalog catalog, IReadOnlyList<ClonePlan> clones,
        Dictionary<string, (CookedPackage Package, PackageBytes Bytes, bool IsClone)> built, List<string> warnings)
    {
        if (clones.Count == 0)
        {
            return;
        }

        var path = DataTableEdits.TradeableTable;
        try
        {
            var table = built.TryGetValue(path, out var kept) ? kept.Package : ModdableAssets.ReadPackage(catalog, path);
            var index = Enumerable.Range(0, table.Exports.Count).First(i => table.GetExportClassName(i) == "DataTable");
            var rows = DataTableRows.Read(table, index).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var copies = clones
                .Select(c => (Template: PackageMap.Leaf(c.Template) + "_C", Leaf: PackageMap.Leaf(c.NewPrimary), c.NewPrimary))
                .Where(c => rows.Contains(c.Template))
                .Select(c => new RowCopy(c.Template, c.Leaf + "_C")
                {
                    SoftPath = ("TradeableClass", $"{c.NewPrimary}.{c.Leaf}_C"),
                    Caption = ("TradingEntryCaption", TradeCaption(c.Leaf)),
                })
                .ToList();
            if (copies.Count == 0)
            {
                return;
            }

            var bytes = DataTableEdits.AddRowCopies(table, copies);
            built[path] = (CookedPackage.Parse(bytes.UAsset, bytes.UExp, table.UBulk, path), bytes, false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
        {
            warnings.Add($"{path}: the clones are not sold by the traders ({ex.Message}).");
        }
    }

    /// <summary>
    /// A copied data asset's id (its first <c>Guid</c> property, 16 bytes in place) and trader name (an FString: the
    /// property grows or shrinks with it).
    /// </summary>
    /// <exception cref="InvalidOperationException">The asset has no Guid property.</exception>
    private static PackageBytes PatchDataAsset(CookedPackage package, DataAssetCopy copy)
    {
        if (copy.PersistentId.Length != 16)
        {
            throw new InvalidOperationException("A persistent id is 16 bytes.");
        }

        var payload = package.GetExportData(0).ToArray();
        var block = PropertyReader.ReadPayload(package, payload, 0);
        var id = block.Properties.FirstOrDefault(t => t.Value is GuidValue)
            ?? throw new InvalidOperationException($"{package.BasePath} has no persistent id (Guid) property.");
        copy.PersistentId.CopyTo(payload, id.Value.Offset);

        if (copy.TraderName is { } name && block.Find("HumanReadableTraderName") is { Value: StrValue } text)
        {
            var value = new byte[4 + name.Length + 1];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(value, name.Length + 1);
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(value, 4);
            var result = new byte[payload.Length - text.Size + value.Length];
            payload.AsSpan(0, text.ValueOffset).CopyTo(result);
            value.CopyTo(result, text.ValueOffset);
            payload.AsSpan(text.ValueOffset + text.Size).CopyTo(result.AsSpan(text.ValueOffset + value.Length));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(text.SizeFieldOffset), value.Length);
            payload = result;
        }

        var data = Enumerable.Range(0, package.Exports.Count).Select(i => i == 0 ? (ReadOnlyMemory<byte>)payload : package.GetExportData(i)).ToArray();
        return PackageWriter.Build(PackageWriter.ToBuildInput(package) with { ExportData = data });
    }

    /// <summary>The name a clone shows in the trade menu: <c>BPC_RagerGold</c> → <c>RagerGold</c>, underscores as spaces.</summary>
    public static string TradeCaption(string leaf) =>
        (leaf.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) ? leaf[4..] : leaf).Replace('_', ' ');

    /// <summary>The expression guids of a material instance's parent material (empty, with a warning, when unreadable).</summary>
    private static IReadOnlyDictionary<string, byte[]> ParentGuids(AssetCatalog catalog, CookedPackage instance, List<string> warnings)
    {
        try
        {
            if (MaterialParameters.ParentPackage(instance) is { } parent)
            {
                return MaterialParameters.ExpressionGuids(ModdableAssets.ReadPackage(catalog, parent));
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException)
        {
            warnings.Add($"{instance.BasePath}: parent material not read ({ex.Message}); new parameters get no expression guid.");
        }

        return new Dictionary<string, byte[]>();
    }
}
