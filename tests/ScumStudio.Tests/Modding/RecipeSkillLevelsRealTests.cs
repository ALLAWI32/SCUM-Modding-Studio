using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Cloning;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// What the game's own recipes store per skill level (CwHedgehog: "adjust the materials required for each level of
/// Engineering/Survival"), read from the real paks (<c>SCUM_PAKS</c> and the key; skipped otherwise). Set
/// <c>SCUM_RESEARCH_OUT</c> to a file path to get the whole table written there.
/// </summary>
public sealed class RecipeSkillLevelsRealTests
{
    private static readonly string[] Levels = ["NoSkill", "Basic", "Medium", "Advanced", "AboveAdvanced"];

    private static AssetCatalog Open() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [PaksFact]
    public void TheGamesRecipesStoreAnAmountPerSkillLevel()
    {
        using var catalog = Open();
        var index = catalog.Index ?? catalog.BuildIndex(resolveClasses: false);
        var recipes = index.Entries.Select(e => e.PackagePath).Where(CraftCategories.IsStockRecipe).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var dump = new StringBuilder();
        var skills = new Dictionary<string, int>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        int read = 0, varying = 0, slotsMissingALevel = 0, slots = 0;
        foreach (var path in recipes)
        {
            CookedPackage package;
            int export;
            try
            {
                package = ModdableAssets.ReadPackage(catalog, path);
                export = RecipeIngredients.RecipeExport(package);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                continue;
            }

            read++;
            var block = package.ReadProperties(export);
            var skill = block.Find("RelevantSkill")?.Value is ObjectValue { Index: < 0 } s ? package.ResolveName(package.Imports[-s.Index - 1].ObjectName) : "(none)";
            skills[skill] = skills.GetValueOrDefault(skill) + 1;
            dump.Append(path).Append(" [").Append(package.GetExportClassName(export)).Append("] skill=").AppendLine(skill);
            var differs = false;
            if (block.Find("Ingredients")?.Value is ArrayValue list)
            {
                foreach (var slot in list.Items.OfType<StructValue>())
                {
                    slots++;
                    var tags = slot.Properties.FirstOrDefault(p => p.Name == "AllowedTypes")?.Value is ArrayValue a
                        ? string.Join("|", a.Items.OfType<ObjectValue>().Where(o => o.Index < 0).Select(o => package.ResolveName(package.Imports[-o.Index - 1].ObjectName)))
                        : "?";
                    var purpose = slot.Properties.FirstOrDefault(p => p.Name == "Purpose")?.Value is EnumValue e ? e.Value : "(default)";
                    var amount = Values(slot, "Amount");
                    var extra = Values(slot, "AdditionalAmount");
                    members.UnionWith(amount.Keys);
                    if (amount.Count < Levels.Length)
                    {
                        slotsMissingALevel++;
                    }

                    var row = Levels.Select(l => amount.GetValueOrDefault(l, 0)).ToArray();
                    differs |= row.Distinct().Count() > 1;
                    dump.Append("    ").Append(tags).Append(' ').Append(purpose)
                        .Append(" Amount=").Append(string.Join('/', Levels.Select(l => amount.TryGetValue(l, out var v) ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")))
                        .Append(" Additional=").AppendLine(string.Join('/', Levels.Select(l => extra.TryGetValue(l, out var v) ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")));
                }
            }

            varying += differs ? 1 : 0;
        }

        dump.Insert(0, $"recipes={recipes.Count} read={read} varying={varying} slots={slots} slotsMissingALevel={slotsMissingALevel} members={string.Join(',', members)}\nskills: {string.Join(", ", skills.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}"))}\n");
        if (Environment.GetEnvironmentVariable("SCUM_RESEARCH_OUT") is { Length: > 0 } output)
        {
            File.WriteAllText(output, dump.ToString());
        }

        Assert.True(read > 100, $"only {read} recipes read");
        Assert.Subset(Levels.ToHashSet(StringComparer.Ordinal), members);
        Assert.True(varying > 0, "no recipe asks a skilled player for another amount");
    }

    /// <summary>
    /// Three of the game's recipes, one per skill (the evidence quoted in the change): the workbench is Engineering, the
    /// improvised bow Survival, C4 Demolition, and each asks a skilled player for less of something.
    /// </summary>
    [PaksFact]
    public void ThreeRecipesOfTheGameAskLessAtHigherLevels()
    {
        using var catalog = Open();
        foreach (var (recipe, skill) in new[] { (CraftablesPlanner.StationRecipe, "EngineeringSkill"), (Items + "CR_Improvised_Bow_20", "SurvivalSkill"), (Items + "CR_C4", "DemolitionSkill") })
        {
            var stock = CraftablesPlanner.StockRecipe(catalog, recipe, out var reason);
            Assert.True(stock is not null, $"{recipe}: {reason}");
            Assert.Equal(skill, stock!.Value.Skill);
            Assert.Contains(stock.Value.Slots, s => s.Amount.AboveAdvanced < s.Amount.NoSkill);
            Assert.All(stock.Value.Slots, s => Assert.True(s.Amount.ToArray().All(a => a >= 1), $"{recipe}: {s.TagText} {s.Amount}"));
        }
    }

    /// <summary>
    /// The export of a game recipe's override (the workbench, every slot new amounts) and of a craftable with its own amounts
    /// per level, for client and server: CUE4Parse reads the written amounts back, the workbench is the only game path in the
    /// pak and keeps its size, and a stale or wrong override is left out with a warning.
    /// </summary>
    [PaksFact]
    public async Task AGameRecipeOverrideShipsOnlyItsAmountsForClientAndServer()
    {
        using var catalog = Open();
        const string workbench = CraftablesPlanner.StationRecipe;
        const string table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
        var stock = CraftablesPlanner.StockRecipe(catalog, workbench, out _)!.Value;
        var wanted = stock.Slots.Select((_, i) => new LevelAmounts(50 + i, 40 + i, 30 + i, 20 + i, 1 + i)).ToList();
        var file = new CraftablesFile
        {
            Items = [new Craftable { Name = "Level Table", Source = table, Mesh = table, Ingredients = [new("CI_Plank", 20, Levels: new LevelAmounts(20, 15, 10, 5, 1))] }],
            RecipeOverrides =
            [
                new StockRecipeOverride { Recipe = workbench, Slots = stock.Slots.Select((s, i) => new StockSlotAmounts(s.TagText, wanted[i])).ToList() },
                new StockRecipeOverride { Recipe = Items + "CR_Improvised_Bow_20", Slots = [new("CI_Not_The_Bows_Slot", new LevelAmounts(1, 1, 1, 1, 1))] },
                new StockRecipeOverride { Recipe = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47", Slots = [new("CI_Plank", new LevelAmounts(1, 1, 1, 1, 1))] },
            ],
        };

        var stockPackage = ModdableAssets.ReadPackage(catalog, workbench);
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-skillmats-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var role in new[] { ProjectSourceRole.Client, ProjectSourceRole.Server })
            {
                // The server's is packed too: the pak itself holds the workbench at its game path with the new amounts.
                var result = await CraftablesExporter.ExportAsync(file, "MyMod", catalog, new ExportOptions { OutputDirectory = dir, WritePak = role == ProjectSourceRole.Server }, role);
                Assert.NotNull(result);
                Assert.Contains(result!.Warnings, w => w.Contains($"replaces the game's recipe {workbench}", StringComparison.Ordinal) && w.Contains("another mod that changes the same recipe conflicts", StringComparison.Ordinal));
                Assert.Contains(result.Warnings, w => w.StartsWith("CR_Improvised_Bow_20: the amounts per skill level are not exported (the game's recipe now asks for", StringComparison.Ordinal));
                Assert.Contains(result.Warnings, w => w.StartsWith("Weapon_AK47: the amounts per skill level are not exported (it is not a crafting recipe of the game)", StringComparison.Ordinal));

                // Stock recipes untouched unless overridden: the workbench is the pak's only package at a game path.
                Assert.Equal([workbench], result.Assets.Where(a => catalog.PackageExists(a.PackagePath)).Select(a => a.PackagePath));

                CookedPackage Staged(string path)
                {
                    var asset = Assert.Single(result.Assets, a => a.PackagePath == path);
                    var staged = Path.Combine([result.StagingDirectory, .. asset.VirtualPath.Split('/')]);
                    return CookedPackage.Parse(File.ReadAllBytes(staged), File.ReadAllBytes(Path.ChangeExtension(staged, ".uexp")), null, path);
                }

                var written = Staged(workbench);
                Assert.Equal(wanted, RecipeIngredients.ReadSlots(written).Select(s => s.Amount));
                Assert.Equal(stock.Slots.Select(s => s.TagText), RecipeIngredients.ReadSlots(written).Select(s => s.TagText));
                Assert.Equal(Enumerable.Range(0, stockPackage.Exports.Count).Select(i => stockPackage.GetExportData(i).Length), Enumerable.Range(0, written.Exports.Count).Select(i => written.GetExportData(i).Length));
                var tableRecipe = result.Assets.Single(a => a.PackagePath.EndsWith("/CR_SS_Level_Table", StringComparison.Ordinal)).PackagePath;
                Assert.Equal(new LevelAmounts(20, 15, 10, 5, 1), RecipeIngredients.ReadSlots(Staged(tableRecipe))[0].Amount);

                // What the game's own reader sees.
                using var loose = AssetCatalog.OpenLoose(result.StagingDirectory);
                int[] Levels(string path, int slot)
                {
                    var amount = loose.LoadObject<UObject>($"{path}.{PackageMap.Leaf(path)}").GetOrDefault<FStructFallback[]>("Ingredients")![slot].GetOrDefault<FStructFallback>("Amount")!;
                    return [.. LevelAmounts.Names.Select(n => amount.GetOrDefault<int>(n))];
                }

                for (var i = 0; i < wanted.Count; i++)
                {
                    Assert.Equal(wanted[i].ToArray(), Levels(workbench, i));
                }

                Assert.Equal([20, 15, 10, 5, 1], Levels(tableRecipe, 0));

                if (role == ProjectSourceRole.Server)
                {
                    Assert.True(File.Exists(result.PakPath), "no server pak written");
                    using var pak = AssetCatalog.OpenPaks(result.PakPath!);
                    var packed = pak.LoadObject<UObject>($"{workbench}.{PackageMap.Leaf(workbench)}").GetOrDefault<FStructFallback[]>("Ingredients")!;
                    Assert.Equal(wanted.Count, packed.Length);
                    Assert.Equal(wanted[0].ToArray(), LevelAmounts.Names.Select(n => packed[0].GetOrDefault<FStructFallback>("Amount")!.GetOrDefault<int>(n)));
                }
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private const string Items = "/Game/ConZ_Files/Items/Crafting/Recipes/Items/";

    private static Dictionary<string, int> Values(StructValue slot, string member) =>
        slot.Properties.FirstOrDefault(p => p.Name == member)?.Value is StructValue levels
            ? levels.Properties.Where(p => p.Value is IntValue).ToDictionary(p => p.Name, p => ((IntValue)p.Value).Value, StringComparer.Ordinal)
            : [];
}
