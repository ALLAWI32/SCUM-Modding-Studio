using System.Text.Json;
using System.Text.RegularExpressions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;

namespace ScumStudio.Tests.Level;

/// <summary>The server's <c>EconomyOverride.json</c>: the file the game writes, read, edited and written back.</summary>
public sealed class EconomyOverrideTests
{
    // What SCUM wrote into Saved\Config\WindowsNoEditor\EconomyOverride.json on the owner's PC (two sample entries, the
    // traders' sections cut to four).
    private const string GameFile = """
        {
        	"economy-override":
        	{
        		"economy-reset-time-hours": "-1.0",
        		"prices-randomization-time-hours": "-1.0",
        		"tradeable-rotation-time-ingame-hours-min": "48.0",
        		"enable-fame-point-requirement": "1",
        		"traders":
        		{
        			"A_0_Armory": [
        				{
        					"tradeable-code": "Frag_Grenade",
        					"base-purchase-price": "-1",
        					"base-sell-price": "-1",
        					"delta-price": "-1.0",
        					"can-be-purchased": "default",
        					"required-famepoints": "-1",
        					"available-after-sale-only": "default"
        				},
        				{
        					"tradeable-code": "Weapon_AK47",
        					"base-purchase-price": "-1",
        					"base-sell-price": "-1",
        					"delta-price": "-1.0",
        					"can-be-purchased": "default",
        					"required-famepoints": "-1",
        					"available-after-sale-only": "default"
        				}
        			],
        			"A_0_Barber": [],
        			"B_4_Saloon": [],
        			"Z_3_Master_Hunter": []
        		}
        	}
        }
        """;

    [Fact]
    public void TheGamesFileRoundTripsThroughAnEdit()
    {
        var economy = EconomyOverride.Parse(GameFile);
        Assert.Equal(["A_0_Armory", "A_0_Barber", "B_4_Saloon", "Z_3_Master_Hunter"], economy.Traders);
        Assert.Equal(2, economy.Entries("A_0_Armory").Count);
        Assert.True(economy.Find("A_0_Armory", "Weapon_AK47")!.IsDefault); // "-1" and "default" are the game's values
        Assert.Contains(("tradeable-rotation-time-ingame-hours-min", "48.0"), economy.Settings);

        // Edit: the AK costs more and needs less fame, grenades are off the shelf, a placed trader gets a section.
        economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 15000, RequiredFame = 50 });
        economy.Set("A_0_Armory", new TradeableOverride("Frag_Grenade") { CanBePurchased = false, AfterSaleOnly = true });
        economy.Set("B_4_Saloon", new TradeableOverride("Beer") { SellPrice = 3, DeltaPrice = 0.5f });
        economy.EnsureSection("A_3_Armory");
        var text = economy.ToJson();
        Assert.Contains("\t\t\t\"A_3_Armory\": []", text, StringComparison.Ordinal); // tab-indented, as the game writes it
        Assert.Contains("\"can-be-purchased\": \"false\"", text, StringComparison.Ordinal);
        Assert.Contains("\"base-sell-price\": \"-1\"", text, StringComparison.Ordinal);

        var back = EconomyOverride.Parse(text);
        Assert.Equal(["A_0_Armory", "A_0_Barber", "B_4_Saloon", "Z_3_Master_Hunter", "A_3_Armory"], back.Traders);
        Assert.Equal(new TradeableOverride("Weapon_AK47") { PurchasePrice = 15000, RequiredFame = 50 }, back.Find("A_0_Armory", "Weapon_AK47"));
        Assert.Equal(new TradeableOverride("Frag_Grenade") { CanBePurchased = false, AfterSaleOnly = true }, back.Find("A_0_Armory", "Frag_Grenade"));
        Assert.Equal(new TradeableOverride("Beer") { SellPrice = 3, DeltaPrice = 0.5f }, back.Find("B_4_Saloon", "Beer"));
        Assert.Equal(economy.Settings, back.Settings);
        Assert.Equal(text, back.ToJson());

        // Back to the game's values: the entry leaves the section.
        back.Set("A_0_Armory", new TradeableOverride("Weapon_AK47"));
        Assert.Null(back.Find("A_0_Armory", "Weapon_AK47"));
    }

    [Fact]
    public void TheFileIsWrittenWithANoteSayingWhereItGoes()
    {
        // Owner: "with the economy file a txt telling where to put it, single player and server".
        var folder = Path.Combine(Path.GetTempPath(), "ss-economy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = EconomyOverride.CreateDefault().SaveTo(folder, withNote: true);
            Assert.True(File.Exists(path));
            var note = File.ReadAllText(Path.Combine(folder, EconomyOverride.ReadmeName));
            Assert.Contains(@"SCUM\Saved\Config\WindowsServer\EconomyOverride.json", note, StringComparison.Ordinal);
            Assert.Contains(@"%LOCALAPPDATA%\SCUM\Saved\Config\WindowsNoEditor\EconomyOverride.json", note, StringComparison.Ordinal);
            Assert.Contains("السيرفر", note, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheGamesTraderCharactersAreKnownAndOthersAreNot()
    {
        Assert.True(TraderPosts.IsTraderNpcPackage("/Game/ConZ_Files/Characters/NPCs/Vendors/Arms_Dealer/Arms_Dealer_01/BP_ArmsDealer_01"));
        Assert.True(TraderPosts.IsTraderNpcPackage("/Game/ConZ_Files/Characters/NPCs/Vendors/Banker/Banker_01/BP_Banker01"));
        Assert.False(TraderPosts.IsTraderNpcPackage("/Game/ConZ_Files/Characters/NPCs/Vendors/BP_Master_Trader"));
        Assert.False(TraderPosts.IsTraderNpcPackage("/Game/ConZ_Files/Characters/NPCs/SedentaryNPCs/BackgroundInteractions/BP_BackgroundInteraction_Angry"));
        Assert.False(TraderPosts.IsTraderNpcPackage("/Game/ConZ_Files/Characters/NPCs/Armed_NPCs/Blueprint/Guard/BP_Guard_Lvl_1"));
        var traders = ScumStudio.Assets.Catalog.AssetDumper.Packages.Where(ScumStudio.App.ViewModels.AssetsPageViewModel.IsTraderNpc).ToList();
        Assert.True(traders.Count >= 8, string.Join(", ", traders.Select(t => t.PackagePath)));
        Assert.True(ScumStudio.App.ViewModels.AssetsPageViewModel.AsksForTraders(" Trader "));
        Assert.True(ScumStudio.App.ViewModels.AssetsPageViewModel.AsksForTraders("تاجر"));
        Assert.False(ScumStudio.App.ViewModels.AssetsPageViewModel.AsksForTraders("table"));
    }

    [Fact]
    public void ANewFileHasTheGamesDefaultSettings()
    {
        var economy = EconomyOverride.CreateDefault();
        Assert.Empty(economy.Traders);
        Assert.Equal(21, economy.Settings.Count);
        Assert.Contains(("traders-unlimited-stock", "0"), economy.Settings);
        Assert.Equal(economy.Settings, EconomyOverride.Parse(economy.ToJson()).Settings);
    }

    [Fact]
    public void APlacedTradersSectionListsItsStockAndKeepsItemsBackAtTheGamesValues()
    {
        var economy = EconomyOverride.CreateDefault();
        Assert.Equal(3, economy.List("Z_3_Armory_1", ["Weapon_AK47", "Frag_Grenade", "Weapon_AK47", "Cal_7_62x39mm_Ammobox"]));
        Assert.Equal(0, economy.List("Z_3_Armory_1", ["Frag_Grenade"])); // listed once
        Assert.All(economy.Entries("Z_3_Armory_1"), e => Assert.True(e.IsDefault)); // "-1" / "default": the game's values
        Assert.Contains("\"tradeable-code\": \"Cal_7_62x39mm_Ammobox\"", economy.ToJson(), StringComparison.Ordinal);

        // An edit, then back to the game's: a placed trader keeps the item listed, a game trader's section drops it.
        economy.Set("Z_3_Armory_1", new TradeableOverride("Weapon_AK47") { PurchasePrice = 9000 });
        economy.Set("Z_3_Armory_1", new TradeableOverride("Weapon_AK47"), keep: true);
        Assert.Equal(3, economy.Entries("Z_3_Armory_1").Count);
        Assert.Equal("Weapon_AK47", economy.Entries("Z_3_Armory_1")[0].Code); // in place
        economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 9000 });
        economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47"));
        Assert.Empty(economy.Entries("A_0_Armory"));

        // Removing an item, a section, and putting the section back.
        Assert.True(economy.Remove("Z_3_Armory_1", "frag_grenade"));
        Assert.False(economy.Remove("Z_3_Armory_1", "Frag_Grenade"));
        var removed = economy.RemoveSection("Z_3_Armory_1")!;
        Assert.False(economy.HasSection("Z_3_Armory_1"));
        Assert.DoesNotContain("Z_3_Armory_1", economy.Traders);
        Assert.Null(economy.RemoveSection("Z_3_Armory_1"));
        economy.SetSection("Z_3_Armory_1", removed);
        Assert.Equal(["Weapon_AK47", "Cal_7_62x39mm_Ammobox"], economy.Entries("Z_3_Armory_1").Select(e => e.Code));
        Assert.False(economy.Without(["Z_3_Armory_1"]).HasSection("Z_3_Armory_1"));
        Assert.True(economy.HasSection("Z_3_Armory_1")); // a copy was changed
    }

    [Fact]
    public void AnAddedItemIsWrittenInFullAndOnSale()
    {
        var beer = new TradeableDefault("Beer_Can", "Beer", "Drinks", ["Bartender"], 30, 10, CanBePurchased: false, RequiredFame: 5, AfterSaleOnly: false);
        var entry = beer.AddedEntry();
        Assert.Equal(new TradeableOverride("Beer_Can") { PurchasePrice = 30, SellPrice = 10, CanBePurchased = true, RequiredFame = 5, AfterSaleOnly = false }, entry);
        Assert.False(entry.IsDefault);
        Assert.True(beer.IsSoldBy("bartender"));
        Assert.False(beer.IsSoldBy("Armorer"));

        var economy = EconomyOverride.CreateDefault();
        economy.Set("A_0_Armory", entry);
        var text = economy.ToJson();
        Assert.Contains("\"base-purchase-price\": \"30\"", text, StringComparison.Ordinal);
        Assert.Contains("\"can-be-purchased\": \"true\"", text, StringComparison.Ordinal);
        Assert.Contains("\"available-after-sale-only\": \"false\"", text, StringComparison.Ordinal);
        Assert.Equal(entry, EconomyOverride.Parse(text).Find("A_0_Armory", "Beer_Can"));

        var defaults = new EconomyDefaults([beer, new TradeableDefault("Gold_Bar", "Gold", "Valuables", ["Bartender"], 1, 1, true, 0, false) { ImmuneToOverrides = true }], []);
        Assert.Same(beer, defaults.Find("beer_can"));
        Assert.Equal(["Beer_Can"], defaults.StockCodes("Bartender")); // the locked row is not listed
    }

    [Fact]
    public void ANewTradersNameIsNumberedPerCellAndType()
    {
        (string, string)[] game = [("B_4_Armory", "Armorer"), ("B_4_Hospital", "Doctor"), ("A_0_Armory", "Armorer"), ("Z_3_Master_Hunter", "MasterHunter")];
        Assert.Equal("B_4_Hospital_2", TraderPosts.DefaultName("B_4", "Doctor", game)); // B_4 has the game's hospital
        Assert.Equal("Z_4_Hospital_1", TraderPosts.DefaultName("Z_4", "Doctor", game));
        Assert.Equal("A_3_Armory_2", TraderPosts.DefaultName("A_3", "Armorer", [.. game, ("A_3_Armory_1", "Armorer")]));
        Assert.Equal("Z_3_Master_Hunter_2", TraderPosts.DefaultName("Z_3", "MasterHunter", game));
        // Always free: a hand-named trader that took the next number is skipped.
        Assert.Equal("B_4_Armory_3", TraderPosts.DefaultName("B_4", "Armorer", [.. game, ("B_4_Armory_2", "GeneralGoods")]));
        Assert.True(TraderPosts.IsValidName(TraderPosts.DefaultName("C_2", "GeneralGoods", game)));

        Assert.Equal("B_4", TraderPosts.CellOf("B_4_Armory"));
        Assert.Equal("A_3", TraderPosts.CellOf("/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01"));
        Assert.Null(TraderPosts.CellOf("Outpost_A_3"));
        Assert.True(TraderPosts.IsOutpostLevel("/Game/ConZ_Files/Maps/The_Island/B_4_Outpost"));
        Assert.True(TraderPosts.IsOutpostLevel("/Game/X/A_0_Outpost_HuntersGrotto"));
        Assert.False(TraderPosts.IsOutpostLevel("/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01"));
    }

    [Fact]
    public void ADeletedOutpostsTradersLeaveTheExportedEconomyAndComeBackOnUndo()
    {
        const string outpost = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost";
        var b = new FakeLevelBuilder(outpost);
        var armory = b.Actor("BP_Armory_1", "/Game/X/BP_Outpost_Armory.BP_Outpost_Armory_C", blueprint: true);
        var doctor = b.Actor("BP_Doctor_1", "/Game/X/BP_Outpost_Doctor.BP_Outpost_Doctor_C", blueprint: true);
        var bank = b.Actor("BP_Bank_1", "/Game/X/BP_Outpost_Bank.BP_Outpost_Bank_C", blueprint: true);
        b.Traders(armory, new TraderMarker(FTransform.Identity, "B_4_Armory", "Armorer", "/Game/X/BP_ArmsDealer.BP_ArmsDealer_C", "/Game/E/B4_Armory.B4_Armory"))
            .Traders(doctor, new TraderMarker(FTransform.Identity, "B_4_Hospital", "Doctor", "/Game/X/BP_Doc.BP_Doc_C", "/Game/E/B4_Doctor.B4_Doctor"))
            .Traders(bank, new TraderMarker(FTransform.Identity, "Banker01", string.Empty, "/Game/X/BP_Banker01.BP_Banker01_C", string.Empty));
        var level = LevelDocument.FromData(b.Build());

        var state = new EditState();
        Assert.Empty(TraderPosts.RemovedStockTraders(state, [level]));
        var delete = new BatchOp("Delete outpost", [new DeleteActorOp(new ActorRef(outpost, "BP_Armory_1")), new DeleteActorOp(new ActorRef(outpost, "BP_Doctor_1")), new DeleteActorOp(new ActorRef(outpost, "BP_Bank_1"))]);
        state.Apply(delete);
        Assert.Equal(["B_4_Armory", "B_4_Hospital"], TraderPosts.RemovedStockTraders(state, [level]).Order(StringComparer.Ordinal)); // the banker has no section

        // A copy of the armory's post that stays keeps the armory on the island.
        var copied = new EditState();
        copied.Apply(new DuplicateActorOp(new ActorRef(outpost, "BP_Armory_1"), "BP_Armory_1_Copy", TransformValue.FromTransform(FTransform.Identity)));
        copied.Apply(delete);
        Assert.Equal(["B_4_Hospital"], TraderPosts.RemovedStockTraders(copied, [level]));

        var economy = EconomyOverride.CreateDefault();
        economy.Set("B_4_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 1 });
        economy.EnsureSection("A_0_Armory");
        var removed = TraderPosts.RemovedStockTraders(state, [level]);
        var exported = ProjectExporter.EconomyFor(state, economy, removed)!;
        Assert.Equal(["A_0_Armory"], exported.Traders);
        Assert.True(economy.HasSection("B_4_Armory")); // the project keeps it for the undo

        state.Apply(delete.Inverse());
        Assert.Empty(TraderPosts.RemovedStockTraders(state, [level]));
        Assert.Equal(["B_4_Armory", "A_0_Armory"], ProjectExporter.EconomyFor(state, economy, [])!.Traders);
    }

    [Fact]
    public void SavingReplacesTheFileInOneStep()
    {
        var folder = Path.Combine(Path.GetTempPath(), "scumstudio-economy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var economy = EconomyOverride.CreateDefault();
            economy.EnsureSection("A_0_Armory");
            var path = economy.SaveTo(folder);
            economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 5 });
            Assert.Equal(path, economy.SaveTo(folder));
            Assert.Equal(5, EconomyOverride.LoadFrom(folder)!.Find("A_0_Armory", "Weapon_AK47")!.PurchasePrice);
            Assert.Equal([EconomyOverride.FileName], Directory.GetFiles(folder).Select(f => Path.GetFileName(f))); // no temporary file left
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // The game's own file, byte for byte as SCUM writes it (read with od on the owner's PC, 2026-10-12): the brace of an
    // object on a line of its own, arrays opened on the key's line, CRLF, tabs and no newline after the last brace.
    internal const string GameLayout =
        "{\r\n" +
        "\t\"economy-override\":\r\n" +
        "\t{\r\n" +
        "\t\t\"economy-reset-time-hours\": \"-1.0\",\r\n" +
        "\t\t\"prices-randomization-time-hours\": \"-1.0\",\r\n" +
        "\t\t\"enable-fame-point-requirement\": \"1\",\r\n" +
        "\t\t\"traders\":\r\n" +
        "\t\t{\r\n" +
        "\t\t\t\"A_0_Armory\": [\r\n" +
        "\t\t\t\t{\r\n" +
        "\t\t\t\t\t\"tradeable-code\": \"Frag_Grenade\",\r\n" +
        "\t\t\t\t\t\"base-purchase-price\": \"-1\",\r\n" +
        "\t\t\t\t\t\"base-sell-price\": \"-1\",\r\n" +
        "\t\t\t\t\t\"delta-price\": \"-1.0\",\r\n" +
        "\t\t\t\t\t\"can-be-purchased\": \"default\",\r\n" +
        "\t\t\t\t\t\"required-famepoints\": \"-1\",\r\n" +
        "\t\t\t\t\t\"available-after-sale-only\": \"default\"\r\n" +
        "\t\t\t\t},\r\n" +
        "\t\t\t\t{\r\n" +
        "\t\t\t\t\t\"tradeable-code\": \"Weapon_AK47\",\r\n" +
        "\t\t\t\t\t\"base-purchase-price\": \"-1\",\r\n" +
        "\t\t\t\t\t\"base-sell-price\": \"-1\",\r\n" +
        "\t\t\t\t\t\"delta-price\": \"-1.0\",\r\n" +
        "\t\t\t\t\t\"can-be-purchased\": \"default\",\r\n" +
        "\t\t\t\t\t\"required-famepoints\": \"-1\",\r\n" +
        "\t\t\t\t\t\"available-after-sale-only\": \"default\"\r\n" +
        "\t\t\t\t}\r\n" +
        "\t\t\t],\r\n" +
        "\t\t\t\"A_0_Barber\": [],\r\n" +
        "\t\t\t\"B_4_Saloon\": [],\r\n" +
        "\t\t\t\"Z_3_Master_Hunter\": []\r\n" +
        "\t\t}\r\n" +
        "\t}\r\n" +
        "}";

    // An older list a server admin kept (Discord: "the economy doesn't have a way to upload its own economy file"): a key
    // the app does not know, a price with a thousands separator, a delta with six decimals, a yes and -1.0 for a whole number.
    internal const string OddEntry =
        "{\r\n" +
        "\t\"economy-override\":\r\n" +
        "\t{\r\n" +
        "\t\t\"economy-logging\": \"1\",\r\n" +
        "\t\t\"traders\":\r\n" +
        "\t\t{\r\n" +
        "\t\t\t\"A_0_Armory\": [\r\n" +
        "\t\t\t\t{\r\n" +
        "\t\t\t\t\t\"tradeable-code\": \"Weapon_AK47\",\r\n" +
        "\t\t\t\t\t\"max-stock\": \"5\",\r\n" +
        "\t\t\t\t\t\"base-purchase-price\": \"12,000\",\r\n" +
        "\t\t\t\t\t\"base-sell-price\": \"-1\",\r\n" +
        "\t\t\t\t\t\"delta-price\": \"0.123456\",\r\n" +
        "\t\t\t\t\t\"can-be-purchased\": \"yes\",\r\n" +
        "\t\t\t\t\t\"required-famepoints\": \"-1.0\",\r\n" +
        "\t\t\t\t\t\"available-after-sale-only\": \"default\"\r\n" +
        "\t\t\t\t}\r\n" +
        "\t\t\t]\r\n" +
        "\t\t}\r\n" +
        "\t}\r\n" +
        "}";

    [Fact]
    public void TheGamesOwnFileIsWrittenBackByteForByte()
    {
        // Owner (#25): "export writes it back unchanged where not edited".
        Assert.Equal(GameLayout, EconomyOverride.Parse(GameLayout).ToJson());
    }

    [Fact]
    public void UnknownKeysAndOddValuesSurviveAnEditOfAnotherValue()
    {
        var economy = EconomyOverride.Parse(OddEntry);
        economy.Set("A_0_Armory", economy.Find("A_0_Armory", "Weapon_AK47")! with { SellPrice = 400 });
        var text = economy.ToJson();
        Assert.Contains("\"max-stock\": \"5\"", text, StringComparison.Ordinal);
        Assert.Contains("\"base-purchase-price\": \"12,000\"", text, StringComparison.Ordinal);
        Assert.Contains("\"delta-price\": \"0.123456\"", text, StringComparison.Ordinal);
        Assert.Contains("\"can-be-purchased\": \"yes\"", text, StringComparison.Ordinal);
        Assert.Equal(OddEntry.Replace("\"base-sell-price\": \"-1\"", "\"base-sell-price\": \"400\"", StringComparison.Ordinal), text); // that line only
    }

    [Fact]
    public void ResettingTheLastValueKeepsWhatTheAppCannotRead()
    {
        // Review (economy): a reset that left the typed values at the game's removed the whole entry, "12,000" and the
        // unknown key with it, although the game reads "12,000" (as 12).
        var economy = EconomyOverride.Parse(
            "{\"economy-override\":{\"traders\":{\"A_0_Armory\":[{\"tradeable-code\":\"Weapon_AK47\",\"max-stock\":\"5\",\"base-purchase-price\":\"12,000\",\"base-sell-price\":\"500\"}]}}}");
        economy.Set("A_0_Armory", economy.Find("A_0_Armory", "Weapon_AK47")! with { SellPrice = null });
        var text = economy.ToJson();
        Assert.Contains("\"max-stock\": \"5\"", text, StringComparison.Ordinal);
        Assert.Contains("\"base-purchase-price\": \"12,000\"", text, StringComparison.Ordinal);
        Assert.Contains("\"base-sell-price\": \"-1\"", text, StringComparison.Ordinal);

        // An entry left with nothing but the game's values still goes.
        var plain = EconomyOverride.CreateDefault();
        plain.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { SellPrice = 500 });
        plain.Set("A_0_Armory", new TradeableOverride("Weapon_AK47"));
        Assert.Empty(plain.Entries("A_0_Armory"));
    }

    [Theory]
    [InlineData("{\"economy-override\":{\"economy-logging\":\"1\",\"economy-logging\":\"0\",\"traders\":{}}}")] // a setting twice
    [InlineData("{\"economy-override\":{\"traders\":{\"A_0_Armory\":[],\"A_0_Armory\":[{\"tradeable-code\":\"Weapon_AK47\"}]}}}")] // a section twice
    [InlineData("{\"economy-override\":{\"traders\":{\"\":[]}}}")] // a section without a name
    [InlineData("{\"economy-override\":{\"economy-logging\":\"1\",\"traders\":[]}}")] // traders as an array
    [InlineData("[]")]
    [InlineData("{}")]
    public void UnusualFilesReadOrFailAsJsonException(string json)
    {
        // Audit F103/F108/F286: a duplicate key threw ArgumentException out of the project's Changed event.
        EconomyOverride economy;
        try
        {
            economy = EconomyOverride.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        var text = economy.ToJson();
        Assert.Single(Regex.Matches(text, "\"traders\""));
        Assert.Equal(text, EconomyOverride.Parse(text).ToJson());
    }

    [Fact]
    public void DuplicateEntriesChangeTogether()
    {
        // Audit F285/F441: Set changed the first of two entries for one code, Remove removed both.
        var economy = EconomyOverride.Parse("{\"economy-override\":{\"traders\":{\"A_0_Armory\":[" +
            "{\"tradeable-code\":\"Weapon_AK47\",\"base-purchase-price\":\"100\"},{\"tradeable-code\":\"Weapon_AK47\",\"base-purchase-price\":\"200\"}]}}}");
        economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 9000 });
        var back = EconomyOverride.Parse(economy.ToJson());
        Assert.Equal([9000, 9000], back.Entries("A_0_Armory").Select(e => e.PurchasePrice));

        economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47"));
        Assert.Null(economy.Find("A_0_Armory", "Weapon_AK47"));
        Assert.Empty(EconomyOverride.Parse(economy.ToJson()).Entries("A_0_Armory"));
    }

    [Fact]
    public void TheNoteSaysTheFileReplacesTheServersOwn()
    {
        // Audit F105/F109: the note told admins to copy the file over the server's own without a word about what is lost.
        var folder = Path.Combine(Path.GetTempPath(), "ss-economy-" + Guid.NewGuid().ToString("N"));
        try
        {
            EconomyOverride.CreateDefault().SaveTo(folder, withNote: true);
            var note = File.ReadAllText(Path.Combine(folder, EconomyOverride.ReadmeName));
            Assert.Contains("REPLACES", note, StringComparison.Ordinal);
            Assert.Contains("back up", note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Import", note, StringComparison.Ordinal);
            Assert.Contains("يستبدل", note, StringComparison.Ordinal);
            Assert.Contains("نسخة احتياطية", note, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void APastedStockPostKeepsItsTrader()
    {
        // Audit F278: only a Duplicate kept a deleted post's trader; a copy pasted into another level is an added Blueprint.
        const string outpost = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost";
        var b = new FakeLevelBuilder(outpost);
        var armory = b.Actor("BP_Armory_1", "/Game/X/BP_Outpost_Armory.BP_Outpost_Armory_C", blueprint: true);
        b.Traders(armory, new TraderMarker(FTransform.Identity, "B_4_Armory", "Armorer", "/Game/X/BP_ArmsDealer.BP_ArmsDealer_C", "/Game/E/B4_Armory.B4_Armory"));
        var level = LevelDocument.FromData(b.Build());

        var state = new EditState();
        var paste = new AddBlueprintActorOp("/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01", "BP_Armory_1_Pasted", "/Game/X/BP_Outpost_Armory.BP_Outpost_Armory_C",
            new ActorRef(outpost, "BP_Armory_1"), TransformValue.FromTransform(FTransform.Identity));
        state.Apply(paste);
        state.Apply(new DeleteActorOp(new ActorRef(outpost, "BP_Armory_1")));
        Assert.Empty(TraderPosts.RemovedStockTraders(state, [level]));

        state.Apply(paste.Inverse()); // the pasted copy deleted too: the trader is gone
        Assert.Equal(["B_4_Armory"], TraderPosts.RemovedStockTraders(state, [level]));
    }
}
