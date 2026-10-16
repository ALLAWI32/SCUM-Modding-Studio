using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Tests.Formats;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// A synthetic loose game with the base element families of SCUM 1.3.3 (layout of the SDK of build 149664 and the pak
/// list): the improvised bed (<c>CR_Bed_Improvised</c> → <c>BP_Bed_Improvised</c>, a respawn point with a weather mask
/// mesh, whose <c>_upgradeRecipe</c> <c>CR_Bed_Improvised_Upgrade</c> makes <c>BP_Bed_Improvised_Upgrade</c>), the
/// improvised wooden chest (a <c>BaseBuildingItemComponent</c> spawning <c>Improvised_Wooden_Chest</c> + <c>_ES</c>, with a
/// mesh variation), the improvised chair
/// (the furniture template), a modular wall, an element without a recipe and a DLC bed.
/// </summary>
internal sealed class ElementFixture : IDisposable
{
    public const string Placeables = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/";
    public const string Elements = "/Game/ConZ_Files/BaseBuilding/BaseElements/";
    public const string Meshes = "/Game/ConZ_Files/Models/BaseBuilding/";
    public const string Containers = "/Game/ConZ_Files/Items/Containers/";
    public const string Bed = Elements + "BP_Bed_Improvised";
    public const string BedRecipe = Placeables + "CR_Bed_Improvised";
    public const string BedUpgrade = Elements + "BP_Bed_Improvised_Upgrade";
    public const string BedUpgradeRecipe = Placeables + "CR_Bed_Improvised_Upgrade"; // the pak list's name (not CR_Upgrade_*)
    public const string BedMesh = Meshes + "SM_Bed_Improvised";
    public const string BedUpgradeMesh = Meshes + "SM_Bed_Improvised_Upgrade";
    public const string OtherMesh = Meshes + "SM_Cot_Field";
    public const string WeatherMesh = Meshes + "SM_Weather_Bed";
    public const string Chest = Elements + "BP_Base_Improvised_Wooden_Chest";
    public const string ChestRecipe = Placeables + "CR_Improvised_Wooden_Chest";
    public const string ChestItem = Containers + "Improvised_Wooden_Chest";
    public const string ChestMesh = Meshes + "SM_Chest_Improvised";
    public const string ChestVariation = Meshes + "SM_Chest_Improvised_Worn";
    public const string Chair = Elements + "BP_Chair_Improvised_Wood";
    public const string ChairRecipe = Placeables + "CR_Chair_Improvised_Wood";
    public const string Wall = Elements + "BPC_Base_Modular_Wall_Wood";
    public const string Lonely = Elements + "BP_Base_Statue";
    public const string Dlc = "/SCUM/Plugins/GameFeatures/AsianDecorPack/Content/BaseBuilding/BP_Bed_Asian";
    public const string ItemRecipe = "/Game/ConZ_Files/Items/Crafting/Recipes/Items/CR_Small_Battery_Charger";
    public const string Plank = "/Game/ConZ_Files/Items/Crafting/Ingredients/CI_Plank";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "scumstudio-elements-" + Guid.NewGuid().ToString("N"));

    public ElementFixture()
    {
        Write(Plank, Asset("CI_Plank", "/Script/SCUM", "CraftingIngredientTag", (_, _) => { }));
        foreach (var mesh in new[] { BedMesh, BedUpgradeMesh, OtherMesh, WeatherMesh, ChestMesh, ChestVariation, Meshes + "SM_Chair_Improvised_Wood", Meshes + "SM_Wall_Wood", Meshes + "SM_Statue" })
        {
            Write(mesh, Asset(Leaf(mesh), "/Script/Engine", "StaticMesh", (_, _) => { }));
        }

        Write(BedRecipe, Recipe(BedRecipe, Bed));
        Write(Bed, Element(Bed, "BaseBuildingComponent", "Improvised bed", BedMesh, (p, w) =>
        {
            w.Float("_healthPoints", 1000).Float("_hoursToDecay", 72).Bool("_isUsedForRespawn", true)
                .Object("_upgradeRecipe", p.Import("/Script/SCUM", "PlaceableCraftingRecipe", p.Package(BedUpgradeRecipe), Leaf(BedUpgradeRecipe)))
                .Object("_weatherMaskMesh", p.Import("/Script/Engine", "StaticMesh", p.Package(WeatherMesh), Leaf(WeatherMesh)));
        }));
        Write(BedUpgradeRecipe, Recipe(BedUpgradeRecipe, BedUpgrade));
        Write(BedUpgrade, Element(BedUpgrade, "BaseBuildingComponent", "Upgraded bed", BedUpgradeMesh, (_, w) => w.Float("_healthPoints", 2000).Bool("_isUsedForRespawn", true)));

        Write(ChestRecipe, Recipe(ChestRecipe, Chest));
        Write(Chest, Element(Chest, "BaseBuildingItemComponent", "Improvised wooden chest", ChestMesh, (p, w) =>
        {
            w.Float("_healthPoints", 500).Object("_itemClass", p.Import("/Script/Engine", "BlueprintGeneratedClass", p.Package(ChestItem), Leaf(ChestItem) + "_C"), "ClassProperty")
                .ObjectArray("_staticMeshVariations", p.Import("/Script/Engine", "StaticMesh", p.Package(ChestVariation), Leaf(ChestVariation)));
        }));
        Write(ChestItem, Blueprint(ChestItem, "/Script/SCUM", "Item", (_, w) => w.Float("Weight", 4)));
        Write(ChestItem + "_ES", Blueprint(ChestItem + "_ES", "/Script/SCUM", "ItemEntitySetup", (_, w) => w.Text("Caption", "Improvised wooden chest")));
        Write(ItemRecipe, Recipe(ItemRecipe, "/Game/ConZ_Files/Items/Equipment/Small_Battery_Charger", "ItemCraftingRecipe"));

        Write(ChairRecipe, Recipe(ChairRecipe, Chair));
        Write(Chair, Element(Chair, "BaseBuildingComponent", "Improvised chair", Meshes + "SM_Chair_Improvised_Wood", (_, w) => w.Float("_healthPoints", 300)));
        Write(Placeables + "CR_Wall_Wood", Recipe(Placeables + "CR_Wall_Wood", Wall));
        Write(Wall, Element(Wall, "ModularBaseBuildingComponent", "Wooden wall", Meshes + "SM_Wall_Wood", (_, w) => w.Float("_healthPoints", 3000)));
        Write(Lonely, Element(Lonely, "BaseBuildingComponent", "Statue", Meshes + "SM_Statue", (_, w) => w.Float("_healthPoints", 100)));
        Write(Dlc, Element(Dlc, "BaseBuildingComponent", "Asian bed", BedMesh, (_, w) => w.Bool("_isUsedForRespawn", true)));
        Catalog = AssetCatalog.OpenLoose(Path.Combine(_root, "game"));
    }

    public AssetCatalog Catalog { get; }

    public static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    public void Dispose()
    {
        Catalog.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a file the test runner still holds; the temp folder is cleaned later
        }
    }

    private void Write(string package, PackageBytes bytes)
    {
        var relative = package.StartsWith("/Game/", StringComparison.Ordinal) ? "SCUM/Content/" + package["/Game/".Length..] : package.TrimStart('/');
        var file = Path.Combine(_root, "game", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file + ".uasset", bytes.UAsset);
        File.WriteAllBytes(file + ".uexp", bytes.UExp);
    }

    /// <summary>A data asset (recipe, tag, mesh): one export of a native class.</summary>
    private static PackageBytes Asset(string name, string scriptPackage, string className, Action<FixturePackage, FixtureWriter> properties)
    {
        var p = new FixturePackage();
        var cls = p.Import("/Script/CoreUObject", "Class", p.Package(scriptPackage), className);
        var template = p.Import(scriptPackage, className, p.Package(scriptPackage), "Default__" + className);
        var w = new FixtureWriter(p);
        properties(p, w);
        p.Export(cls, 0, template, name, 0x3, w.End());
        return p.Build();
    }

    private static PackageBytes Recipe(string recipe, string product, string className = "PlaceableCraftingRecipe") =>
        Asset(Leaf(recipe), "/Script/SCUM", className, (p, w) =>
        {
            var plank = p.Import("/Script/SCUM", "CraftingIngredientTag", p.Package(Plank), "CI_Plank");
            w.Soft("Product", $"{product}.{Leaf(product)}_C").Text("Caption", Leaf(recipe)).Text("Description", Leaf(recipe))
                .StructArray("Ingredients", "CraftingIngredientSlot", s => s
                    .ObjectArray("AllowedTypes", plank)
                    .Struct("Amount", "CraftingIngredientAmount", a => a.Int("NoSkill", 4).Int("Basic", 4).Int("Medium", 3).Int("Advanced", 3).Int("AboveAdvanced", 2))
                    .Enum("Purpose", "ECraftingIngredientPurpose", "ECraftingIngredientPurpose::Material"));
        });

    /// <summary>A base element: a Blueprint class of a native base building component whose class default shows a mesh.</summary>
    private static PackageBytes Element(string element, string native, string name, string mesh, Action<FixturePackage, FixtureWriter> values) =>
        Blueprint(element, "/Script/SCUM", native, (p, w) =>
        {
            w.Text("_name", name);
            values(p, w);
            w.Object("StaticMesh", p.Import("/Script/Engine", "StaticMesh", p.Package(mesh), Leaf(mesh)));
        });

    /// <summary>A Blueprint class (<c>X_C</c>, super a native class) and its class default <c>Default__X_C</c>.</summary>
    private static PackageBytes Blueprint(string package, string scriptPackage, string native, Action<FixturePackage, FixtureWriter> defaults)
    {
        var p = new FixturePackage();
        var leaf = Leaf(package);
        var generated = p.Import("/Script/CoreUObject", "Class", p.Package("/Script/Engine"), "BlueprintGeneratedClass");
        var super = p.Import("/Script/CoreUObject", "Class", p.Package(scriptPackage), native);
        var template = p.Import(scriptPackage, native, p.Package(scriptPackage), "Default__" + native);
        var w = new FixtureWriter(p);
        defaults(p, w);
        var cls = p.Export(generated, super, 0, leaf + "_C", 0x1, new FixtureWriter(p).End());
        p.Export(cls, 0, template, $"Default__{leaf}_C", 0x31, w.End());
        return p.Build();
    }
}

/// <summary>Name, import and export tables of one synthetic package.</summary>
internal sealed class FixturePackage
{
    private readonly List<string> _names = ["None"];
    private readonly List<ImportEntry> _imports = [];
    private readonly List<ExportEntry> _exports = [];
    private readonly List<ReadOnlyMemory<byte>> _data = [];

    public FNameRef Name(string text)
    {
        var index = _names.IndexOf(text);
        if (index < 0)
        {
            _names.Add(text);
            index = _names.Count - 1;
        }

        return new FNameRef(index);
    }

    /// <summary>Adds an import; returns its package index (negative).</summary>
    public int Import(string classPackage, string className, int outer, string objectName)
    {
        _imports.Add(new ImportEntry(Name(classPackage), Name(className), outer, Name(objectName)));
        return -_imports.Count;
    }

    /// <summary>The import of package <paramref name="path"/> (added once).</summary>
    public int Package(string path)
    {
        var index = _imports.FindIndex(i => i.OuterIndex == 0 && _names[i.ObjectName.Index] == path);
        return index >= 0 ? -(index + 1) : Import("/Script/CoreUObject", "Package", 0, path);
    }

    /// <summary>Adds an export; returns its package index (positive).</summary>
    public int Export(int classIndex, int superIndex, int templateIndex, string name, uint flags, byte[] payload)
    {
        _exports.Add(new ExportEntry
        {
            ClassIndex = classIndex,
            SuperIndex = superIndex,
            TemplateIndex = templateIndex,
            ObjectName = Name(name),
            ObjectFlags = flags,
            IsAsset = _exports.Count == 0 ? 1 : 0,
            FirstExportDependency = -1,
        });
        _data.Add(payload);
        return _exports.Count;
    }

    public PackageBytes Build() => PackageWriter.Build(new PackageBuildInput
    {
        Summary = SyntheticPackage.DefaultSummary(),
        Names = _names,
        Imports = _imports,
        Exports = _exports,
        ExportData = _data,
    });
}

/// <summary>Tagged properties (UE 4.27 layout) against a <see cref="FixturePackage"/>'s name table.</summary>
internal sealed class FixtureWriter(FixturePackage package)
{
    private readonly ByteWriter _w = new();

    public FixtureWriter Float(string name, float value)
    {
        Tag(name, "FloatProperty", 4, x => x.U8(0));
        _w.F32(value);
        return this;
    }

    public FixtureWriter Int(string name, int value)
    {
        Tag(name, "IntProperty", 4, x => x.U8(0));
        _w.I32(value);
        return this;
    }

    public FixtureWriter Bool(string name, bool value)
    {
        Tag(name, "BoolProperty", 0, x =>
        {
            x.U8(value ? (byte)1 : (byte)0);
            x.U8(0);
        });
        return this;
    }

    public FixtureWriter Object(string name, int index, string type = "ObjectProperty")
    {
        Tag(name, type, 4, x => x.U8(0));
        _w.I32(index);
        return this;
    }

    public FixtureWriter Soft(string name, string path)
    {
        var value = new ByteWriter();
        value.FName(package.Name(path));
        value.FString(string.Empty);
        return Raw(name, "SoftClassProperty", value, x => x.U8(0));
    }

    /// <summary>A text with a localization key (history Base), as the cooked game stores captions.</summary>
    public FixtureWriter Text(string name, string text)
    {
        var value = new ByteWriter();
        value.U32(0);
        value.U8(0);
        value.FString("SCUM");
        value.FString(Guid.NewGuid().ToString("N"));
        value.FString(text);
        return Raw(name, "TextProperty", value, x => x.U8(0));
    }

    public FixtureWriter Enum(string name, string enumType, string value)
    {
        Tag(name, "EnumProperty", 8, x =>
        {
            x.FName(package.Name(enumType));
            x.U8(0);
        });
        _w.FName(package.Name(value));
        return this;
    }

    public FixtureWriter Struct(string name, string structName, Action<FixtureWriter> members)
    {
        var inner = new FixtureWriter(package);
        members(inner);
        inner.None();
        return Raw(name, "StructProperty", inner._w, x => StructHeader(x, structName));
    }

    public FixtureWriter ObjectArray(string name, params int[] items)
    {
        var value = new ByteWriter();
        value.I32(items.Length);
        foreach (var item in items)
        {
            value.I32(item);
        }

        return Raw(name, "ArrayProperty", value, x =>
        {
            x.FName(package.Name("ObjectProperty"));
            x.U8(0);
        });
    }

    public FixtureWriter StructArray(string name, string structName, params Action<FixtureWriter>[] items)
    {
        var elements = new ByteWriter();
        foreach (var item in items)
        {
            var element = new FixtureWriter(package);
            item(element);
            element.None();
            elements.Raw(element._w.WrittenSpan);
        }

        var value = new ByteWriter();
        value.I32(items.Length);
        value.FName(package.Name(name));
        value.FName(package.Name("StructProperty"));
        value.I32(elements.Length);
        value.I32(0);
        StructHeader(value, structName);
        value.Raw(elements.WrittenSpan);
        return Raw(name, "ArrayProperty", value, x =>
        {
            x.FName(package.Name("StructProperty"));
            x.U8(0);
        });
    }

    /// <summary>The payload: the properties, <c>None</c> and an empty object guid flag.</summary>
    public byte[] End()
    {
        None();
        _w.I32(0);
        return _w.ToArray();
    }

    private void None() => _w.FName(package.Name("None"));

    private void StructHeader(ByteWriter x, string structName)
    {
        x.FName(package.Name(structName));
        x.Guid(default);
        x.U8(0);
    }

    private FixtureWriter Raw(string name, string type, ByteWriter value, Action<ByteWriter> extra)
    {
        Tag(name, type, value.Length, extra);
        _w.Raw(value.WrittenSpan);
        return this;
    }

    private void Tag(string name, string type, int size, Action<ByteWriter> extra)
    {
        _w.FName(package.Name(name));
        _w.FName(package.Name(type));
        _w.I32(size);
        _w.I32(0);
        extra(_w);
    }
}
