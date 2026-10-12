using System.Security.Cryptography;
using System.Text;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Modding.Crafting;
using ScumStudio.Tests.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// A craftable keeps its asset names for life (audit F037, N046, N030, N034, N039, N070, N099; P0 #1): the token is stored
/// the first time the file is read, a rename keeps it, and a new token never ends in '_' and a number and never collides.
/// </summary>
public sealed class CraftableTokenTests
{
    private const string Json = """{ "Items": [ { "Name": "Oak table #2", "Source": "/Game/A/SM_T", "Mesh": "/Game/A/SM_T" } ] }""";

    [Fact]
    public void LegacyTokenIsStoredOnFirstLoad()
    {
        var file = CraftablesFile.Parse(Json);
        var oak = Assert.Single(file.Items);
        Assert.Equal("SS_Oak_table_2", oak.Token);
        Assert.Equal(CraftablesFile.LegacyTokenOf(oak.Name), oak.Token);
        Assert.True(file.TokensAdded);
        Assert.Contains("\"Token\": \"SS_Oak_table_2\"", file.ToJson(), StringComparison.Ordinal);

        var again = CraftablesFile.Parse(file.ToJson());
        Assert.False(again.TokensAdded);
        Assert.Equal(file.ToJson(), again.ToJson());
    }

    [Fact]
    public void RenameKeepsTheAssetPaths()
    {
        // Objects players placed are saved by class path: 'Chair' renamed 'Wooden Chair' still ships CR_SS_Chair/BP_SS_Chair.
        using var temp = new TempDirectory();
        using var catalog = AssetCatalog.OpenLoose(CraftFixture.WriteLoose(temp.Path, CraftFixture.Stock()));
        var chair = Assert.Single(CraftablesFile.Parse("""{ "Items": [ { "Name": "Chair", "Source": "/Game/ConZ_Files/Models/Table/SM_Table_01", "Mesh": "/Game/ConZ_Files/Models/Table/SM_Table_01", "Ingredients": [ { "Tag": "CI_Plank", "Amount": 3 } ] } ] }""").Items);

        var plan = CraftablesPlanner.Plan(catalog, [chair with { Name = "Wooden Chair" }]);
        Assert.Empty(plan.Warnings);
        var entry = Assert.Single(plan.Entries);
        Assert.Equal(CraftFixture.Placeables + "CR_SS_Chair", entry.Recipe);
        Assert.Equal(CraftFixture.Elements + "BP_SS_Chair", entry.Product);
        Assert.Contains(plan.Request.Edits[entry.Recipe], e => e.Path == "Caption" && e.Value == "Wooden Chair");
    }

    [Fact]
    public void DuplicateLegacyTokensMigrate()
    {
        // Before 3.2 both became SS_Table_wood and the planner kept the first in its build order: that one keeps its token,
        // the other (never shipped) gets one of its own.
        var file = CraftablesFile.Parse("""
            { "Items": [
              { "Name": "Table (wood)", "Source": "/Game/A/SM_T", "Mesh": "/Game/A/SM_T" },
              { "Name": "Table-wood", "Source": "/Game/A/SM_T", "Mesh": "/Game/A/SM_T" },
              { "Name": "Bench", "Source": "/Game/A/SM_B", "Mesh": "/Game/A/SM_B" },
              { "Name": "bench", "Source": "/Game/A/SM_B", "Mesh": "/Game/A/SM_B", "Kind": "Station" }
            ] }
            """);
        var tokens = file.Items.Select(i => i.Token!).ToList();
        Assert.Equal("SS_Table_wood", tokens[0]);
        Assert.NotEqual("SS_Table_wood", tokens[1], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("SS_bench", tokens[3]); // a station comes first in the build order, so it kept the token
        Assert.NotEqual("SS_Bench", tokens[2], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(tokens, t => Assert.True(CraftablesFile.IsValidToken(t), t));

        // A stored token is claimed before any missing one.
        var stored = CraftablesFile.Parse("""
            { "Items": [
              { "Name": "Table-wood", "Source": "/Game/A/SM_T", "Mesh": "/Game/A/SM_T" },
              { "Name": "Table (wood)", "Token": "SS_Table_wood", "Source": "/Game/A/SM_T", "Mesh": "/Game/A/SM_T" }
            ] }
            """);
        Assert.Equal("SS_Table_wood", stored.Items[1].Token);
        Assert.NotEqual("SS_Table_wood", stored.Items[0].Token, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewTokensNeverSplit()
    {
        // A name with no Latin letter whose SHA-256 starts with 8 digits: the old rule gave SS_<digits>, a numbered FName.
        var digitsOnly = Enumerable.Range(0, 5000).Select(i => "椅" + (char)(0x4E00 + i))
            .First(n => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n)))[..8].All(char.IsAsciiDigit));
        Assert.NotEqual(0, FNameRef.Split(CraftablesFile.LegacyTokenOf(digitsOnly)).Number);
        Assert.NotEqual(0, FNameRef.Split(CraftablesFile.LegacyTokenOf("Shelf 1")).Number);

        foreach (var name in new[] { "Shelf 1", "Chair 01 2", "椅子1", "كرسي 01", "X 0", "Oak table #2", "2", digitsOnly })
        {
            var token = CraftablesFile.NewTokenFor(name, []);
            Assert.True(FNameRef.Split(token).Number == 0, $"{name} -> {token}");
            Assert.True(CraftablesFile.IsValidToken(token), $"{name} -> {token}");
        }

        Assert.Equal("SS_Shelf_v1", CraftablesFile.NewTokenFor("Shelf 1", []));
        Assert.Equal("SS_Chair_01_v2", CraftablesFile.NewTokenFor("Chair 01 2", ["SS_Chair_01"]));
        Assert.Equal("SS_Chair_01", CraftablesFile.NewTokenFor("Chair 01", [])); // '_01' is no number for the engine
        Assert.Equal("SS_Oak_Table", CraftablesFile.NewTokenFor(" Oak  Table ", []));
    }

    [Fact]
    public void DistinctTokensForNonLatinPunctuationAndCase()
    {
        string[][] pairs =
        [
            ["كرسي 01", "طاولة 01"],
            ["椅子1", "桌子1"],
            ["Table (wood)", "Table-wood"],
            ["Disco Light أحمر", "Disco Light أزرق"],
            ["Lamp", "lamp!"],
            ["Table", "table"],
        ];
        foreach (var pair in pairs)
        {
            var first = CraftablesFile.NewTokenFor(pair[0], []);
            var second = CraftablesFile.NewTokenFor(pair[1], [first]);
            Assert.False(string.Equals(first, second, StringComparison.OrdinalIgnoreCase), $"{pair[0]} / {pair[1]}: {first}");
        }

        // The hash follows the name, so the same name gives the same token in every export; the same name twice still differs.
        Assert.Equal(CraftablesFile.NewTokenFor("كرسي 01", []), CraftablesFile.NewTokenFor(" كرسي 01 ", []));
        var chair = CraftablesFile.NewTokenFor("Chair", []);
        var hashed = CraftablesFile.NewTokenFor("Chair", [chair]);
        Assert.NotEqual(hashed, CraftablesFile.NewTokenFor("Chair", [chair, hashed]));
    }

    [Fact]
    public void LegacySplittingTokenStillPlans()
    {
        // A token stored before the FName number rule is never renamed (players have those objects): it is planned.
        using var temp = new TempDirectory();
        using var catalog = AssetCatalog.OpenLoose(CraftFixture.WriteLoose(temp.Path, CraftFixture.Stock()));
        Assert.True(CraftablesFile.IsValidToken("SS_Chair_01_2"));
        var plan = CraftablesPlanner.Plan(catalog, [CraftFixture.Chair("Chair 01 2", "SS_Chair_01_2")]);
        Assert.Empty(plan.Warnings);
        Assert.Equal(CraftFixture.Placeables + "CR_SS_Chair_01_2", Assert.Single(plan.Entries).Recipe);
    }

    [Fact]
    public void ACraftableLeftOutForATakenTokenIsNamed()
    {
        // The page shows on the second row which craftable keeps the token (a hand-edited file); the planner agrees.
        using var temp = new TempDirectory();
        using var catalog = AssetCatalog.OpenLoose(CraftFixture.WriteLoose(temp.Path, CraftFixture.Stock()));
        Craftable[] items = [CraftFixture.Chair("Chair", "SS_Chair"), CraftFixture.Chair("Seat", "ss_chair"), CraftFixture.Chair("Stool", "SS_Stool")];
        Assert.Equal(new string?[] { null, "Chair", null }, CraftablesPlanner.TokenOwners(items));
        var plan = CraftablesPlanner.Plan(catalog, items);
        Assert.Equal(["Chair", "Stool"], plan.Entries.Select(e => e.Name));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Seat: left out", StringComparison.Ordinal));
    }
}
