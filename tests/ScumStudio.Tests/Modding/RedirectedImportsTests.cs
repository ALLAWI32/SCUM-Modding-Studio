using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The large cabinet's element given a fridge copy to spawn (defolt_0999's large fridge): the cooker lists the cabinet
/// item's default subobjects among the element's imports, and after the redirect the ones the fridge has no object for
/// would name nothing (the export's package check refused the whole mod). <see cref="RedirectedImports"/> rewrites them to
/// their class's default object when nothing in the package refers to them.
/// </summary>
public sealed class RedirectedImportsTests
{
    private const string Fridge = "/Game/ConZ_Files/Items/Equipment/Storage/SS_Fridge";

    // The fridge copy has its class, its class default and an Inventory: not the cabinet's BuriableComponent or paint job.
    private static bool? Exists(string path) => path.StartsWith(Fridge + ".", StringComparison.Ordinal)
        ? path[(Fridge.Length + 1)..] is "SS_Fridge_C" or "Default__SS_Fridge_C" or "Default__SS_Fridge_C.Inventory"
        : null;

    private static List<string> ImportPaths(CookedPackage package) =>
        Enumerable.Range(1, package.Imports.Count).Select(i => package.GetFullPath(-i)).ToList();

    [Fact]
    public void ImportsTheTargetHasNoObjectForNameTheirClassDefault()
    {
        var element = Element(referenceBuriable: false);
        Assert.Contains(Fridge + ".Default__SS_Fridge_C.BuriableComponent", ImportPaths(element));

        var bytes = RedirectedImports.Settle(element, Exists, out var rewritten);

        Assert.NotNull(bytes);
        var settled = CookedPackage.Parse(bytes!.UAsset, bytes.UExp, null, element.BasePath);
        var paths = ImportPaths(settled);
        Assert.Contains("/Script/SCUM.Default__BuriableChestItemComponent", paths);
        Assert.Contains("/Script/SCUM.Default__ChestPaintJobItemComponent", paths);
        Assert.All(paths.Where(p => p.StartsWith(Fridge + ".", StringComparison.Ordinal)), p => Assert.True(Exists(p), p));
        Assert.Contains(Fridge + ".Default__SS_Fridge_C.Inventory", paths); // what the fridge has stays
        Assert.Equal(2, rewritten.Count);
        Assert.Equal(element.Imports.Count, settled.Imports.Count); // in place: /Script/SCUM was imported already
        Assert.All(Enumerable.Range(0, element.Exports.Count), e => Assert.True(settled.GetExportData(e).Span.SequenceEqual(element.GetExportData(e).Span)));
        Assert.Equal(element.Exports.Select(e => (e.ClassIndex, e.SuperIndex, e.TemplateIndex, e.OuterIndex)), settled.Exports.Select(e => (e.ClassIndex, e.SuperIndex, e.TemplateIndex, e.OuterIndex)));
    }

    [Fact]
    public void AnImportSomethingRefersToIsLeftForThePackageCheck()
    {
        var element = Element(referenceBuriable: true);

        Assert.NotNull(RedirectedImports.Settle(element, Exists, out var rewritten));

        Assert.Equal([Fridge + ".SS_Fridge_C.ChestPaintJobItem_GEN_VARIABLE -> /Script/SCUM.Default__ChestPaintJobItemComponent"], rewritten);
    }

    [Fact]
    public void APackageWhoseImportsAllExistIsLeftAsItIs()
    {
        Assert.Null(RedirectedImports.Settle(Element(referenceBuriable: false), _ => true, out var rewritten));
        Assert.Empty(rewritten);
        Assert.Null(RedirectedImports.Settle(Element(referenceBuriable: false), _ => null, out _)); // unknown: never touched
    }

    // BP_SS_Fridge: a base element whose _itemClass is the fridge copy, importing (as the cooker does for the cabinet) the
    // item's class, class default, Inventory, BuriableComponent and paint-job component template.
    private static CookedPackage Element(bool referenceBuriable)
    {
        var p = new FixturePackage();
        var generated = p.Import("/Script/CoreUObject", "Class", p.Package("/Script/Engine"), "BlueprintGeneratedClass");
        var super = p.Import("/Script/CoreUObject", "Class", p.Package("/Script/SCUM"), "BaseBuildingItemComponent");
        var template = p.Import("/Script/SCUM", "BaseBuildingItemComponent", p.Package("/Script/SCUM"), "Default__BaseBuildingItemComponent");
        var item = p.Package(Fridge);
        var cls = p.Import("/Script/Engine", "BlueprintGeneratedClass", item, "SS_Fridge_C");
        var cdo = p.Import(Fridge, "SS_Fridge_C", item, "Default__SS_Fridge_C");
        p.Import("/Script/SCUM", "ChestInventoryComponent", cdo, "Inventory");
        var buriable = p.Import("/Script/SCUM", "BuriableChestItemComponent", cdo, "BuriableComponent");
        p.Import("/Script/SCUM", "ChestPaintJobItemComponent", cls, "ChestPaintJobItem_GEN_VARIABLE");
        var w = new FixtureWriter(p).Object("_itemClass", cls, "ClassProperty");
        if (referenceBuriable)
        {
            w.Object("_buriableChestItemComponent", buriable);
        }

        var bp = p.Export(generated, super, 0, "BP_SS_Fridge_C", 0x1, new FixtureWriter(p).End());
        p.Export(bp, 0, template, "Default__BP_SS_Fridge_C", 0x31, w.End());
        var bytes = p.Build();
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, "/Game/ConZ_Files/BaseBuilding/BaseElements/Storage/BP_SS_Fridge");
    }
}
