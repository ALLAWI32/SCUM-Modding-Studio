using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Registry;
using ScumStudio.Pak;
using ScumStudio.Pak.Reading;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Real game files (r40; Discord igor8802 and redriver6155: "it was working at some point but now only predefined
/// ingredients will work. maybe let us create new groups?"): igor8802's Book90 recipe, whose ingredients include items given a
/// new tag (<c>Scissors_Plastic</c> as a tool, <c>Car_Repair_Kit</c>), and a project group, exported as the Craftables page's
/// pak and as the whole project's Craftables pak, for the client and (with <c>SCUM_SERVER_PAKS</c>) the dedicated server.
/// The game accepts an item as an ingredient when its entity setup's <c>CraftingIngredientInstances</c> names the tag object a
/// recipe slot's <c>AllowedTypes</c> names (SDK: <c>UItemEntitySetup</c>, <c>FCraftingIngredientSlot</c>), and the tag's
/// <c>ConsumptionMethod</c> says how it is used up; so each pak must carry the recipe asking for the tag, the tag itself at
/// the path both import, of a method the item can satisfy, and the item's setup listing it, with the tag's registry record.
/// </summary>
public sealed class CraftIngredientTagsRealTests(ITestOutputHelper output)
{
    private const string Book = "/Game/ConZ_Files/Models/Buildings/Abandoned_School/Meshes/Misc/Books/SM_Book90";
    private const string Items = "/Game/ConZ_Files/Items/Equipment/Active_Items/";
    private const string Scissors = Items + "Scissors_Plastic";
    private const string RepairKit = Items + "Car_Repair_Kit";
    private const string LeadPlate = Items + "Lead_Plate";
    private const string Name = "TagMod";

    /// <summary>The new item tags and a project group, in both roles: the recipe, the tags, the setups and the registry.</summary>
    [PaksFact]
    public async Task NewTagsAndAProjectGroupReachTheGameInBothRoles()
    {
        using (var client = Open(RealPaks.Folder!))
        {
            await CheckAsync(client, ProjectSourceRole.Client);
        }

        if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is { Length: > 0 } server && Directory.Exists(server))
        {
            using var dedicated = Open(server);
            await CheckAsync(dedicated, ProjectSourceRole.Server);
        }
        else
        {
            output.WriteLine("SCUM_SERVER_PAKS not set: the server role was not checked.");
        }
    }

    /// <summary>
    /// An item a game update added after the studio's embedded catalogue (SCUM 1.3.3.4) is the game's own: the picker lets it get
    /// a new tag (r40: the list alone greyed out "Only this item" for the 10 items 1.3.4 added).
    /// </summary>
    [PaksFact]
    public void ItemsALaterGameUpdateAddedCanGetANewTag()
    {
        const string jacket = "/Game/ConZ_Files/Items/Clothes/Jackets_Coats/Mutant_Jacket";
        using var catalog = Open(RealPaks.Folder!);
        var index = IngredientIndex.Of(catalog);
        if (!CraftablesPlanner.IsItem(catalog, jacket))
        {
            output.WriteLine($"{jacket} is not in these game files; nothing to check.");
            return;
        }

        var stock = AssetDumper.Packages.Select(p => p.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var setup = index.SetupOf(jacket)!;
        Assert.DoesNotContain(setup, stock); // newer than the embedded catalogue
        Assert.False(CraftablesPageViewModel.LookupOf(index, jacket, stock).CanAddTag); // the list alone refuses it
        Assert.True(CraftablesPageViewModel.LookupOf(index, jacket, stock, s => CraftablesPlanner.IsGamePackage(catalog, s)).CanAddTag);
        Assert.Equal(NewTagRefusal.None, CraftablesPlanner.NewTagRefusalOf(catalog, index, jacket));
    }

    private async Task CheckAsync(AssetCatalog catalog, ProjectSourceRole role)
    {
        var index = IngredientIndex.Of(catalog);
        var dlc = CraftablesPlanner.Items(catalog).First(i => index.SetupOf(i)?.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(NewTagRefusal.Dlc, CraftablesPlanner.NewTagRefusalOf(catalog, index, dlc));
        foreach (var item in new[] { Scissors, RepairKit, LeadPlate })
        {
            var choice = index.Resolve(item);
            output.WriteLine($"{role} {PackageMap.Leaf(item)}: {choice.Match} tag={choice.Tag} setup={index.SetupOf(item)} uses={index.CountsUses(item)}");
        }

        // igor8802's recipe (screenshot 2026-10-20) plus a group of the project: plastic scissors, a lead plate and a DLC item,
        // which cannot join (its setup is not changed). The scissors are in the group and have a tag of their own.
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine(Name + ".ssproj"), Name);
        var group = new CraftGroup("Cutting Kit", CraftGroups.TagFor("Cutting Kit", index.Tags.Select(t => t.Name)), [Scissors, LeadPlate, dlc]);
        var book = CraftablesPageViewModel.Describe(catalog, Book)!;
        new CraftablesFile
        {
            Items =
            [
                book with
                {
                    Name = "Book90",
                    Token = "SS_Book90",
                    Exported = false,
                    Ingredients =
                    [
                        new CraftIngredient("CI_Paper", 20),
                        new CraftIngredient("CI_Glue", 1),
                        new CraftIngredient("CI_Group_Sewing_Kit", 1),
                        new CraftIngredient(string.Empty, 1, IsTool: true, Item: Scissors, AddTag: true),
                        new CraftIngredient(string.Empty, 1, Item: RepairKit, AddTag: true),
                        new CraftIngredient(group.Tag, 2),
                    ],
                },
            ],
            Groups = [group],
        }.Save(project.DirectoryPath);
        Assert.Equal("CI_Group_Cutting_Kit", group.Tag);

        var options = new ExportOptions { OutputDirectory = temp.Combine("out"), ProjectDirectory = project.DirectoryPath, LeaveOutEconomy = true };
        var page = await CraftablesExporter.ExportAsync(CraftablesFile.Load(project.DirectoryPath), Name, catalog,
            options with { Module = ExportModule.Craftables, Elsewhere = ModuleExport.Elsewhere(project.State, ExportModule.Craftables, Name, options.PakChunkIndex) }, role);
        var whole = await CraftablesExporter.ExportAsync(CraftablesFile.Load(project.DirectoryPath), Name, catalog, options, role, alone: true);
        foreach (var result in new[] { page!, whole! })
        {
            output.WriteLine($"{role} {Path.GetFileName(result.PakPath)}: {result.Validation?.Summary}");
            foreach (var warning in result.Warnings)
            {
                output.WriteLine("  warning: " + warning);
            }

            Assert.True(result.Validation is { Passed: true }, string.Join('\n', result.Validation?.Problems ?? ["not checked"]));
            Assert.Contains(result.Warnings, w => w.Contains($"Group '{group.Name}': {PackageMap.Leaf(dlc)} is left out of the group", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Warnings, w => w.Contains("shows the caption of", StringComparison.Ordinal) || w.Contains("shows the picture of", StringComparison.Ordinal));

            // The install note names the replaced setups and what hides them.
            var report = await File.ReadAllTextAsync(result.ReportPath!);
            Assert.Contains("New ingredient tags:", report, StringComparison.Ordinal);
            Assert.Contains("Scissors_Plastic_ES", report, StringComparison.Ordinal);
            CheckPak(result.PakPath!, catalog, index, group, role);
        }
    }

    private void CheckPak(string pak, AssetCatalog catalog, IngredientIndex index, CraftGroup group, ProjectSourceRole role)
    {
        using var source = PakFileSource.OpenFile(pak, new PakFileSourceOptions { ProjectName = catalog.ProjectName });
        var files = source.EnumerateFiles().ToList();
        var packages = files.Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName)).ToList();
        output.WriteLine($"  {role} {Path.GetFileName(pak)}: {string.Join(", ", packages.Select(PackageMap.Leaf))}");
        CookedPackage Read(string package)
        {
            var header = files.Single(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                && string.Equals(AssetPaths.ToPackagePath(f, catalog.ProjectName), package, StringComparison.OrdinalIgnoreCase));
            if (!source.TryGetBytes(header, out var uasset) || !source.TryGetBytes(header[..^".uasset".Length] + ".uexp", out var uexp))
            {
                throw new InvalidOperationException($"{pak}: {package} cannot be read.");
            }

            return CookedPackage.Parse(uasset, uexp, null, package);
        }

        // (1) The recipe asks for the new tags and the group, in the order of the list, the scissors as a tool.
        var recipePath = Assert.Single(packages, p => PackageMap.Leaf(p) == "CR_SS_Book90");
        var recipe = Read(recipePath);
        var slots = RecipeIngredients.ReadSlots(recipe);
        output.WriteLine($"  {role} recipe: {string.Join(" | ", slots.Select(s => $"{s.TagText} x{s.Amount.NoSkill}{(s.IsTool ? " tool" : string.Empty)}"))}");
        Assert.Equal(["CI_Paper", "CI_Glue", "CI_Group_Sewing_Kit", "CI_SSI_Scissors_Plastic", "CI_SSI_Car_Repair_Kit", group.Tag], slots.Select(s => s.TagText));
        Assert.True(slots[3].IsTool);
        Assert.Equal(2, slots[5].Amount.NoSkill);
        var imported = TagImports(recipe);

        // (2) Each new tag is in the pak at the path the recipe imports, of a method its items satisfy, showing its item under
        // its own caption; (3) every item that gets it lists it in its setup (the scissors both their own and the group's).
        var expected = new (string Tag, string? Method, string Shows, string[] Members)[]
        {
            ("CI_SSI_Scissors_Plastic", null, Scissors, [Scissors]), // no uses: whole items
            ("CI_SSI_Car_Repair_Kit", "CICM_Uses", RepairKit, [RepairKit]), // a repair kit has uses, like the game's own tags of such items
            (group.Tag, null, Scissors, [Scissors, LeadPlate]), // scissors have no uses: the group counts whole items
        };
        foreach (var (tag, method, shows, members) in expected)
        {
            var path = RecipeIngredients.TagPath(tag);
            Assert.Contains(path, imported);
            var tagPackage = Read(PackageMap.Normalize(path));
            var at = Enumerable.Range(0, tagPackage.Exports.Count).Single(i => tagPackage.GetExportClassName(i) == "CraftingIngredientTag");
            Assert.Equal(tag, tagPackage.ResolveName(tagPackage.Exports[at].ObjectName));
            var block = tagPackage.ReadProperties(at);
            var stored = block.Find("ConsumptionMethod")?.Value is ObjectValue { Index: < 0 } o ? tagPackage.ResolveName(tagPackage.Imports[-o.Index - 1].ObjectName) : null;
            Assert.Equal(method, stored);
            Assert.Equal(CraftablesPlanner.GamePath(shows) + "." + PackageMap.Leaf(shows) + "_C", Assert.IsType<SoftObjectValue>(block.Find("UIClassRepresentative")!.Value).AssetPath);
            Assert.True(Assert.IsType<BoolValue>(block.Find("OverrideClassRepresentativeCaption")!.Value).Value);
            output.WriteLine($"  {role} {tag}: method {stored ?? "(default)"}, caption '{Assert.IsType<TextValue>(block.Find("ClassRepresentativeCaption")!.Value).SourceString}'");

            foreach (var member in members)
            {
                var setup = Read(index.SetupOf(member)!);
                var listed = Assert.IsType<ArrayValue>(ItemSetups.ClassDefaults(setup)!.Find(IngredientInstances.Property)!.Value);
                var instances = IngredientIndex.StoredInstances(setup, listed);
                output.WriteLine($"  {role} {PackageMap.Leaf(setup.BasePath)}: {string.Join(", ", instances.Select(i => PackageMap.Leaf(PackageMap.Normalize(i.TagPath))))}");
                Assert.Contains(instances, i => string.Equals(i.TagPath, path, StringComparison.OrdinalIgnoreCase) && i.Amount == 1);
            }
        }

        // The lead plate keeps its own tag next to the group's.
        var lead = Read(index.SetupOf(LeadPlate)!);
        Assert.Contains(IngredientIndex.StoredInstances(lead, Assert.IsType<ArrayValue>(ItemSetups.ClassDefaults(lead)!.Find(IngredientInstances.Property)!.Value)),
            i => i.TagPath.EndsWith(".CI_Lead_Plate", StringComparison.OrdinalIgnoreCase));

        // The pak's registry: a record for each new tag like the game's own tags, the setups' records kept, the recipe's.
        var registry = ModuleExport.ReadPak(pak, catalog.ProjectName).Registry;
        Assert.NotNull(registry);
        var records = registry.Assets.ToDictionary(a => a.ObjectPath, StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, _, _, members) in expected)
        {
            Assert.True(records.TryGetValue(RecipeIngredients.TagPath(tag), out var record), tag + " has no record");
            Assert.Equal("CraftingIngredientTag", record.AssetClass);
            foreach (var setup in members.Select(m => index.SetupOf(m)!))
            {
                Assert.True(records.TryGetValue(setup + "." + PackageMap.Leaf(setup), out var setupRecord), setup + " has no record");
                Assert.Contains(registry.GetTags(setupRecord), t => t.Key == "PrimaryAssetType" && t.Value.Text == "LWObjectSetup");
            }
        }

        Assert.True(records.ContainsKey(recipePath + "." + PackageMap.Leaf(recipePath)), "the recipe has no record");
        var stock = AssetRegistryFile.Parse(RegistryMerge.ReadStock(catalog)!).Assets.Select(a => a.ObjectPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(stock.Where(path => !records.ContainsKey(path)).Take(5)); // no record of the game lost
    }

    private static HashSet<string> TagImports(CookedPackage package) => package.Imports
        .Where(i => package.ResolveName(i.ClassName) == "CraftingIngredientTag" && i.OuterIndex < 0)
        .Select(i => package.ResolveName(package.Imports[-i.OuterIndex - 1].ObjectName) + "." + package.ResolveName(i.ObjectName))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static AssetCatalog Open(string paks) => AssetCatalog.OpenPaks(paks, new AssetCatalogOptions
    {
        AesKey = AesKeyText.FromEnvironmentOrStore(),
        PakFileFilter = OwnPaks.Filter(OwnPaks.ProjectNames(Name)),
    });
}
