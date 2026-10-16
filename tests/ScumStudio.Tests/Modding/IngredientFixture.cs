using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Crafting;
using ScumStudio.Tests.Formats;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// A small loose game for the item-ingredient tests (no paks needed): items with entity setups that list ingredient tags
/// (<c>CraftingIngredientInstances</c>), one setup that inherits its list from a parent setup, one setup not named after
/// its item, the tags themselves and the item recipe template the planner copies.
/// </summary>
internal sealed class IngredientFixture : IDisposable
{
    private const string Items = "/Game/ConZ_Files/Items/";

    /// <summary>An item with a tag of its own: <c>CI_Lead_Plate</c> (setup stores it, the tag shows it).</summary>
    public const string LeadPlate = Items + "Crafting/Resources/Lead_Plate";

    /// <summary>An item whose setup stores no list and inherits <c>[CI_Group_Rifles]</c> from <see cref="RifleSetup"/>.</summary>
    public const string Ak47 = Items + "Weapons/Weapon_AK47";

    /// <summary>An item whose setup stores <c>[CI_Weapon_M16, CI_Group_Rifles]</c>.</summary>
    public const string M16 = Items + "Weapons/Weapon_M16";

    /// <summary>The inventory icon the M16's setup stores (<c>GridInventoryIcon</c>): its recipe's picture.</summary>
    public const string M16Icon = Items + "Weapons/Icons/ICO_M16_Inventory.ICO_M16_Inventory";

    /// <summary>The picture of the item recipe template (<see cref="Recipe"/>).</summary>
    public const string RecipeIcon = "/Game/ConZ_Files/Items/Item_Icons/ICO_Small_Battery_Charger.ICO_Small_Battery_Charger";

    /// <summary>The parent setup of the AK-47's (no item of its own).</summary>
    public const string RifleSetup = Items + "Weapons/Weapon_Rifle_ES";

    /// <summary>An item whose setup is not named after it: <see cref="FeederSetup"/>, whose <c>Actor</c> names it.</summary>
    public const string Feeder = Items + "Traps/AnimalBaitFeederItem_Ridgeline";

    /// <summary>The setup of <see cref="Feeder"/>.</summary>
    public const string FeederSetup = Items + "Traps/AnimalBaitFeeder_Ridgeline_ES";

    /// <summary>The item recipe the planner copies for an item it does not know (<see cref="CraftablesPlanner.PowerRecipe"/>).</summary>
    public const string Recipe = CraftablesPlanner.PowerRecipe;

    public IngredientFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "scumstudio-ingredients-" + Guid.NewGuid().ToString("N"));
        Write(LeadPlate, Blueprint(LeadPlate, null, "Item", (_, _) => { }));
        Write(LeadPlate + "_ES", Blueprint(LeadPlate + "_ES", null, "ItemEntitySetup", (b, w) =>
        {
            b.Text(w, "Caption", "Lead plate");
            b.StructArray(w, IngredientInstances.Property, "CraftingIngredientInstance", [b.Instance("CI_Lead_Plate")]);
        }));
        Write(RifleSetup, Blueprint(RifleSetup, null, "WeaponEntitySetup", (b, w) =>
            b.StructArray(w, IngredientInstances.Property, "CraftingIngredientInstance", [b.Instance("CI_Group_Rifles")])));
        Write(Ak47, Blueprint(Ak47, null, "Weapon", (_, _) => { }));
        Write(Ak47 + "_ES", Blueprint(Ak47 + "_ES", RifleSetup, null, (b, w) => b.Text(w, "Caption", "AK-47")));
        Write(M16, Blueprint(M16, null, "Weapon", (_, _) => { }));
        Write(M16 + "_ES", Blueprint(M16 + "_ES", null, "WeaponEntitySetup", (b, w) =>
        {
            b.Text(w, "Caption", "M16");
            b.Soft(w, "GridInventoryIcon", M16Icon);
            b.StructArray(w, IngredientInstances.Property, "CraftingIngredientInstance", [b.Instance("CI_Weapon_M16"), b.Instance("CI_Group_Rifles")]);
        }));
        Write(Feeder, Blueprint(Feeder, null, "Item", (_, _) => { }));
        Write(FeederSetup, Blueprint(FeederSetup, null, "ItemEntitySetup", (b, w) => b.Soft(w, "Actor", Feeder + "." + Leaf(Feeder) + "_C")));
        Write(RecipeIngredients.TagFolder + "/CI_Lead_Plate", Tag("CI_Lead_Plate", LeadPlate, "Lead plate"));
        Write(RecipeIngredients.TagFolder + "/CI_Group_Rifles", Tag("CI_Group_Rifles", null, "Rifles"));
        Write(RecipeIngredients.TagFolder + "/CI_Weapon_M16", Tag("CI_Weapon_M16", M16, "M16"));
        Write(Recipe, ItemRecipe());
        Catalog = AssetCatalog.OpenLoose(Root);
    }

    /// <summary>The folder the game is written to (<c>SCUM/Content/…</c>).</summary>
    public string Root { get; }

    /// <summary>The game, opened loose.</summary>
    public AssetCatalog Catalog { get; }

    public void Dispose()
    {
        Catalog.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private void Write(string packagePath, PackageBytes bytes)
    {
        var stem = Path.Combine([Root, "SCUM", "Content", .. packagePath["/Game/".Length..].Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllBytes(stem + ".uasset", bytes.UAsset);
        File.WriteAllBytes(stem + ".uexp", bytes.UExp);
    }

    // A Blueprint class (X_C, deriving from a native class or a parent Blueprint) and its class default object.
    private static PackageBytes Blueprint(string path, string? parent, string? native, Action<Builder, ByteWriter> defaults)
    {
        var b = new Builder();
        var leaf = Leaf(path);
        var generated = b.Import("/Script/CoreUObject", "Class", b.Package("/Script/Engine"), "BlueprintGeneratedClass");
        var super = parent is null
            ? b.Import("/Script/CoreUObject", "Class", b.Package("/Script/SCUM"), native!)
            : b.Import("/Script/Engine", "BlueprintGeneratedClass", b.Package(parent), Leaf(parent) + "_C");
        var none = new ByteWriter();
        none.FName(b.N("None"));
        b.Export(leaf + "_C", generated, super, none.ToArray(), isAsset: false);
        var w = new ByteWriter();
        defaults(b, w);
        b.Export("Default__" + leaf + "_C", 1, 0, b.Close(w), isAsset: false);
        return b.Build();
    }

    // A CraftingIngredientTag data asset: the item it shows and its caption.
    private static PackageBytes Tag(string name, string? shows, string caption)
    {
        var b = new Builder();
        var type = b.Import("/Script/CoreUObject", "Class", b.Package("/Script/SCUM"), "CraftingIngredientTag");
        var w = new ByteWriter();
        if (shows is not null)
        {
            b.Soft(w, "UIClassRepresentative", shows + "." + Leaf(shows) + "_C");
        }

        b.Text(w, "ClassRepresentativeCaption", caption);
        b.Export(name, type, 0, b.Close(w), isAsset: true);
        return b.Build();
    }

    // An ItemCraftingRecipe with a product and one ingredient slot (1 lead plate, a material).
    private static PackageBytes ItemRecipe()
    {
        var b = new Builder();
        var type = b.Import("/Script/CoreUObject", "Class", b.Package("/Script/SCUM"), "ItemCraftingRecipe");
        var lead = b.TagImport("CI_Lead_Plate");
        var w = new ByteWriter();
        b.Soft(w, "Icon", RecipeIcon);
        b.Soft(w, "Product", "/Game/ConZ_Files/Items/Equipment/Small_Battery_Charger.Small_Battery_Charger_C");
        var slot = new ByteWriter();
        var allowed = new ByteWriter();
        allowed.I32(1);
        allowed.I32(lead);
        b.Tag(slot, "AllowedTypes", "ArrayProperty", allowed.Length, x => x.FName(b.N("ObjectProperty")));
        slot.Raw(allowed.WrittenSpan);
        var amounts = new ByteWriter();
        foreach (var level in new[] { "NoSkill", "Basic", "Medium", "Advanced", "AboveAdvanced" })
        {
            b.Tag(amounts, level, "IntProperty", 4);
            amounts.I32(1);
        }

        amounts.FName(b.N("None"));
        b.Tag(slot, "Amount", "StructProperty", amounts.Length, x =>
        {
            x.FName(b.N("CraftingIngredientAmounts"));
            x.Guid(default);
        });
        slot.Raw(amounts.WrittenSpan);
        b.Tag(slot, "Purpose", "EnumProperty", 8, x => x.FName(b.N("ECraftingIngredientPurpose")));
        slot.FName(b.N("ECraftingIngredientPurpose::Material"));
        slot.FName(b.N("None"));
        b.StructArray(w, "Ingredients", "CraftingIngredientSlot", [slot.ToArray()]);
        b.Export(Leaf(Recipe), type, 0, b.Close(w), isAsset: true);
        return b.Build();
    }

    /// <summary>Name, import and export tables of one package, and tagged property writers.</summary>
    private sealed class Builder
    {
        private readonly List<string> _names = ["None"];
        private readonly List<ImportEntry> _imports = [];
        private readonly List<ExportEntry> _exports = [];
        private readonly List<byte[]> _data = [];

        public FNameRef N(string name)
        {
            var index = _names.IndexOf(name);
            if (index < 0)
            {
                _names.Add(name);
                index = _names.Count - 1;
            }

            return new FNameRef(index);
        }

        public int Package(string path) => Import("/Script/CoreUObject", "Package", 0, path);

        public int Import(string classPackage, string className, int outer, string objectName)
        {
            var found = _imports.FindIndex(i => i.OuterIndex == outer && _names[i.ObjectName.Index] == objectName && _names[i.ClassName.Index] == className);
            if (found >= 0)
            {
                return -(found + 1);
            }

            _imports.Add(new ImportEntry(N(classPackage), N(className), outer, N(objectName)));
            return -_imports.Count;
        }

        public int TagImport(string tag) => Import("/Script/SCUM", "CraftingIngredientTag", Package(RecipeIngredients.TagFolder + "/" + tag), tag);

        public void Export(string name, int classIndex, int superIndex, byte[] data, bool isAsset)
        {
            _exports.Add(new ExportEntry
            {
                ClassIndex = classIndex,
                SuperIndex = superIndex,
                ObjectName = N(name),
                ObjectFlags = 0x1,
                IsAsset = isAsset ? 1 : 0,
                FirstExportDependency = -1,
            });
            _data.Add(data);
        }

        public PackageBytes Build() => PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = _names,
            Imports = _imports,
            Exports = _exports,
            ExportData = [.. _data.Select(d => (ReadOnlyMemory<byte>)d)],
        });

        public void Tag(ByteWriter w, string name, string type, int size, Action<ByteWriter>? extra = null)
        {
            w.FName(N(name));
            w.FName(N(type));
            w.I32(size);
            w.I32(0);
            extra?.Invoke(w);
            w.U8(0);
        }

        public void Text(ByteWriter w, string name, string text)
        {
            var value = new ByteWriter();
            value.U32(0); // flags
            value.U8(0); // Base history
            value.FString(string.Empty);
            value.FString(name + "_Key");
            value.FString(text);
            Tag(w, name, "TextProperty", value.Length);
            w.Raw(value.WrittenSpan);
        }

        public void Soft(ByteWriter w, string name, string objectPath)
        {
            var value = new ByteWriter();
            value.FName(N(objectPath));
            value.FString(string.Empty);
            Tag(w, name, "SoftClassProperty", value.Length);
            w.Raw(value.WrittenSpan);
        }

        public byte[] Instance(string tag)
        {
            var e = new ByteWriter();
            Tag(e, "Type", "ObjectProperty", 4);
            e.I32(TagImport(tag));
            Tag(e, "Amount", "FloatProperty", 4);
            e.F32(1);
            e.FName(N("None"));
            return e.ToArray();
        }

        public void StructArray(ByteWriter w, string name, string structName, IReadOnlyList<byte[]> elements)
        {
            var value = new ByteWriter();
            value.I32(elements.Count);
            Tag(value, name, "StructProperty", elements.Sum(e => e.Length), x =>
            {
                x.FName(N(structName));
                x.Guid(default);
            });
            foreach (var element in elements)
            {
                value.Raw(element);
            }

            Tag(w, name, "ArrayProperty", value.Length, x => x.FName(N("StructProperty")));
            w.Raw(value.WrittenSpan);
        }

        public byte[] Close(ByteWriter w)
        {
            w.FName(N("None"));
            w.I32(0); // no object guid
            return w.ToArray();
        }
    }
}
