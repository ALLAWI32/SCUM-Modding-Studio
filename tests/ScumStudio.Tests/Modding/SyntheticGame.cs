using System.Text;
using ScumStudio.Formats;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Tests.Formats;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Small game files for the export-safety tests that must run without the game: cooked packages that import other
/// packages, a DataTable with named rows, and an <c>AssetRegistry.bin</c> whose records are made with
/// <see cref="AssetRegistryFile.AddAsset"/> from one seed record per class.
/// </summary>
internal static class SyntheticGame
{
    /// <summary>A generator-like Blueprint that imports its entity setup and <see cref="Missing"/>.</summary>
    public const string Gen = "/Game/T/BP_Gen";

    /// <summary>The entity setup of <see cref="Gen"/>.</summary>
    public const string GenEs = Gen + "_ES";

    /// <summary>A package <see cref="Gen"/> imports that the game files do not have.</summary>
    public const string Missing = "/Game/T/BP_Gen_Missing";

    /// <summary>A Blueprint whose name table already holds <c>/Game/T/BP_Taken</c>: cloning it as that name fails.</summary>
    public const string Bad = "/Game/T/BP_Bad";

    /// <summary>A stock item (an item craftable's template product): its record has a tag <see cref="Stock2"/>'s lacks.</summary>
    public const string Stock1 = "/Game/S/BP_Stock1";

    /// <summary>A stock record no mod's registry may drop.</summary>
    public const string Stock2 = "/Game/S/BP_Stock2.BP_Stock2";

    /// <summary>A crafting recipe with one ingredient slot (3 <c>CI_Plank</c>), as the game's recipes have.</summary>
    public const string Recipe = "/Game/T/CR_Recipe";

    /// <summary>The game's registry for <see cref="WriteGame"/>: the Blueprint and class records of <see cref="Gen"/>, its setup, two others.</summary>
    public static AssetRegistryFile StockRegistry() => Registry(
        new Record(Gen + ".BP_Gen", "Blueprint", [.. PrimaryTags("Item", "BP_Gen"), ("GeneratedClass", AssetRegistryValue.NumberlessExport("BlueprintGeneratedClass", "BP_Gen_C", Gen))]),
        new Record(Gen + ".BP_Gen_C", "BlueprintGeneratedClass", PrimaryTags("Item", "BP_Gen")),
        new Record(GenEs + ".BP_Gen_ES_C", "BlueprintGeneratedClass", PrimaryTags("LWObjectSetup", "BP_Gen_ES")),
        new Record(Stock1 + ".BP_Stock1", "Blueprint", [.. PrimaryTags("Item", "BP_Stock1"), ("Weight", AssetRegistryValue.Ansi("2.5"))]),
        new Record(Stock2, "Blueprint", PrimaryTags("Item", "BP_Stock2")));

    /// <summary>
    /// Writes a small game below <paramref name="root"/> (<c>SCUM/Content</c>, <c>SCUM/AssetRegistry.bin</c>): <see cref="Gen"/>,
    /// <see cref="GenEs"/>, <see cref="Bad"/>, <see cref="Recipe"/> and a <c>Table_TradeableDesc</c> that sells <c>BP_Gen_C</c>.
    /// Returns its registry.
    /// </summary>
    public static AssetRegistryFile WriteGame(string root)
    {
        Write(root, Gen, Package(Gen, [GenEs, Missing], ["BP_Gen_C", "Default__BP_Gen_C"]));
        Write(root, GenEs, Package(GenEs, [], ["BP_Gen_ES_C"]));
        Write(root, Bad, Package(Bad, [], ["/Game/T/BP_Taken"]));
        Write(root, Recipe, RecipePackage(Recipe));
        Write(root, ScumStudio.Modding.Tuning.DataTableEdits.TradeableTable,
            DataTable(ScumStudio.Modding.Tuning.DataTableEdits.TradeableTable, ["BP_Gen_C", "BPC_Rager_C"]));
        var registry = StockRegistry();
        Write(root, registry);
        return registry;
    }

    /// <summary>A one-export package named after its leaf; its name table holds <paramref name="imports"/> (imported packages) and <paramref name="names"/>.</summary>
    public static PackageBytes Package(string path, IReadOnlyList<string>? imports = null, IReadOnlyList<string>? names = null)
    {
        imports ??= [];
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        var table = new List<string> { "/Script/CoreUObject", "/Script/Engine", "Package", "Class", "Blueprint", "None", path, leaf };
        table.AddRange(imports.Concat(names ?? []).Where(n => !table.Contains(n)));
        var payload = new ByteWriter();
        payload.FName(new FNameRef(table.IndexOf("None")));
        payload.I32(0);
        return Build(table, imports, leaf, "Blueprint", payload.ToArray());
    }

    /// <summary>
    /// A <c>PlaceableCraftingRecipe</c> whose <c>Ingredients</c> hold one slot: <c>AllowedTypes</c> (the imported tag),
    /// <c>Purpose</c> and <c>Amount</c> per skill level, the members <see cref="ScumStudio.Modding.Crafting.RecipeIngredients"/> rewrites.
    /// </summary>
    public static PackageBytes RecipePackage(string path)
    {
        const string plank = ScumStudio.Modding.Crafting.RecipeIngredients.TagFolder + "/CI_Plank";
        string[] levels = ["NoSkill", "Basic", "Medium", "Advanced", "AboveAdvanced"];
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        List<string> table =
        [
            "/Script/CoreUObject", "/Script/Engine", "Package", "Class", "PlaceableCraftingRecipe", "None", path, leaf, plank,
            "Ingredients", "ArrayProperty", "StructProperty", "CraftingIngredient", "AllowedTypes", "ObjectProperty", "Purpose",
            "EnumProperty", "ECraftingIngredientPurpose", "ECraftingIngredientPurpose::Material", "Amount", "CraftingIngredientAmount",
            "IntProperty", .. levels,
        ];
        FNameRef N(string n) => new(table.IndexOf(n));
        void Tag(ByteWriter w, string name, string type, int size, Action<ByteWriter> rest)
        {
            w.FName(N(name));
            w.FName(N(type));
            w.I32(size);
            w.I32(0);
            rest(w);
        }

        var amount = new ByteWriter();
        foreach (var level in levels)
        {
            Tag(amount, level, "IntProperty", 4, x => x.U8(0));
            amount.I32(3);
        }

        amount.FName(N("None"));
        var slot = new ByteWriter();
        Tag(slot, "AllowedTypes", "ArrayProperty", 8, x => { x.FName(N("ObjectProperty")); x.U8(0); });
        slot.I32(1);
        slot.I32(-3); // the package import of CI_Plank
        Tag(slot, "Purpose", "EnumProperty", 8, x => { x.FName(N("ECraftingIngredientPurpose")); x.U8(0); });
        slot.FName(N("ECraftingIngredientPurpose::Material"));
        Tag(slot, "Amount", "StructProperty", amount.Length, x => { x.FName(N("CraftingIngredientAmount")); x.Guid(default); x.U8(0); });
        slot.Raw(amount.WrittenSpan);
        slot.FName(N("None"));

        var list = new ByteWriter();
        list.I32(1);
        Tag(list, "Ingredients", "StructProperty", slot.Length, x => { x.FName(N("CraftingIngredient")); x.Guid(default); x.U8(0); });
        list.Raw(slot.WrittenSpan);
        var payload = new ByteWriter();
        Tag(payload, "Ingredients", "ArrayProperty", list.Length, x => { x.FName(N("StructProperty")); x.U8(0); });
        payload.Raw(list.WrittenSpan);
        payload.FName(N("None"));
        payload.I32(0);
        return Build(table, [plank], leaf, "PlaceableCraftingRecipe", payload.ToArray());
    }

    /// <summary>A DataTable package whose rows (no members) are named <paramref name="rows"/>.</summary>
    public static PackageBytes DataTable(string path, IReadOnlyList<string> rows)
    {
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        var table = new List<string> { "/Script/CoreUObject", "/Script/Engine", "Package", "Class", "DataTable", "None", path, leaf };
        table.AddRange(rows);
        var none = new FNameRef(table.IndexOf("None"));
        var payload = new ByteWriter();
        payload.FName(none); // no properties
        payload.I32(0); // no guid
        payload.I32(rows.Count);
        foreach (var row in rows)
        {
            payload.FName(new FNameRef(table.IndexOf(row)));
            payload.FName(none);
        }

        return Build(table, [], leaf, "DataTable", payload.ToArray());
    }

    private static PackageBytes Build(List<string> names, IReadOnlyList<string> imports, string leaf, string className, byte[] payload)
    {
        int N(string n) => names.IndexOf(n);
        var importEntries = new List<ImportEntry>
        {
            new(new FNameRef(N("/Script/CoreUObject")), new FNameRef(N("Package")), 0, new FNameRef(N("/Script/Engine"))),
            new(new FNameRef(N("/Script/CoreUObject")), new FNameRef(N("Class")), -1, new FNameRef(N(className))),
        };
        importEntries.AddRange(imports.Select(i => new ImportEntry(new FNameRef(N("/Script/CoreUObject")), new FNameRef(N("Package")), 0, new FNameRef(N(i)))));
        var exports = new List<ExportEntry>
        {
            new()
            {
                ClassIndex = -2,
                ObjectName = new FNameRef(N(leaf)),
                ObjectFlags = 0x8,
                IsAsset = 1,
                FirstExportDependency = 0,
                SerializationBeforeCreateDependencies = 1,
            },
        };
        return PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = names,
            Imports = importEntries,
            Exports = exports,
            ExportData = [payload],
            PreloadDependencies = [-2],
        });
    }

    /// <summary>Writes <paramref name="bytes"/> as <c>&lt;root&gt;/SCUM/Content/…/X.uasset</c> + <c>.uexp</c>.</summary>
    public static void Write(string root, string packagePath, PackageBytes bytes)
    {
        var stem = Path.Combine([root, "SCUM", "Content", .. packagePath["/Game/".Length..].Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllBytes(stem + ".uasset", bytes.UAsset);
        File.WriteAllBytes(stem + ".uexp", bytes.UExp);
    }

    /// <summary>Writes <paramref name="registry"/> as <c>&lt;root&gt;/SCUM/AssetRegistry.bin</c>.</summary>
    public static void Write(string root, AssetRegistryFile registry)
    {
        Directory.CreateDirectory(Path.Combine(root, "SCUM"));
        File.WriteAllBytes(Path.Combine(root, "SCUM", "AssetRegistry.bin"), registry.Save());
    }

    /// <summary>A record to add: object path, class and tags.</summary>
    public sealed record Record(string ObjectPath, string Class, IReadOnlyList<(string Key, AssetRegistryValue Value)>? Tags = null);

    /// <summary>
    /// A parsed registry holding one seed record per class (<c>/Game/Seed/&lt;Class&gt;.&lt;Class&gt;</c>, with one asset bundle)
    /// and then <paramref name="records"/>, written by <see cref="AssetRegistryFile.AddAsset"/>.
    /// </summary>
    public static AssetRegistryFile Registry(params Record[] records)
    {
        var classes = records.Select(r => r.Class).Append("Blueprint").Distinct(StringComparer.Ordinal).ToArray();
        var registry = AssetRegistryFile.Parse(Seed(classes));
        var seeds = registry.Assets.ToDictionary(a => a.AssetClass, StringComparer.Ordinal);
        foreach (var record in records)
        {
            var package = record.ObjectPath[..record.ObjectPath.LastIndexOf('.')];
            var slash = package.LastIndexOf('/');
            registry.AddAsset(seeds[record.Class], record.ObjectPath, package[..slash], package, record.ObjectPath[(record.ObjectPath.LastIndexOf('.') + 1)..], record.Tags ?? []);
        }

        return AssetRegistryFile.Parse(registry.Save());
    }

    /// <summary>The usual primary asset tags of a Blueprint record.</summary>
    public static (string, AssetRegistryValue)[] PrimaryTags(string type, string name) =>
        [("PrimaryAssetType", AssetRegistryValue.NumberlessName(type)), ("PrimaryAssetName", AssetRegistryValue.NumberlessName(name))];

    // Name batch, an empty tag store, one record per class (bundle "Explicit" -> /Game/Seed/Thing.Thing), a 4-byte tail.
    private static byte[] Seed(IReadOnlyList<string> classes)
    {
        var names = new List<string>();
        uint N(string s)
        {
            var i = names.IndexOf(s);
            if (i < 0)
            {
                names.Add(s);
                i = names.Count - 1;
            }

            return (uint)i;
        }

        var records = classes.Select(c => new[] { N($"/Game/Seed/{c}.{c}"), N("/Game/Seed"), N(c), N($"/Game/Seed/{c}"), N(c) }).ToList();
        var (bundle, bundlePath) = (N("Explicit"), N("/Game/Seed/Thing.Thing"));
        var w = new ByteWriter();
        w.Raw(new byte[16]);
        w.I32(8);
        w.U32((uint)names.Count);
        w.U32((uint)names.Sum(n => n.Length));
        w.U64(AssetRegistryFile.ExpectedHashAlgorithm);
        foreach (var n in names)
        {
            w.U64(AssetRegistryFile.ComputeNameHash(n));
        }

        foreach (var n in names)
        {
            w.U8((byte)(n.Length >> 8));
            w.U8((byte)n.Length);
        }

        foreach (var n in names)
        {
            w.Raw(Encoding.Latin1.GetBytes(n));
        }

        w.U32(AssetRegistryFile.StoreBeginMagic);
        for (var i = 0; i < 11; i++)
        {
            w.I32(0);
        }

        w.U32(0);
        w.U32(AssetRegistryFile.StoreEndMagic);
        w.I32(records.Count);
        foreach (var fields in records)
        {
            foreach (var f in fields)
            {
                w.U32(f);
            }

            w.U64(1UL << 63); // numberless keys, no tags
            w.I32(1);
            w.U32(bundle);
            w.I32(1);
            w.U32(bundlePath);
            w.I32(0); // empty sub-path
            w.I32(0); // chunk ids
            w.U32(0); // package flags
        }

        w.Raw([0xEE, 0xEE, 0xEE, 0xEE]); // stands for the dependency section, kept verbatim
        return w.ToArray();
    }
}
