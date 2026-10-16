using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The journal steps of the economy (owner, 2026-10-12: "one journal op per change so Ctrl+Z works"): each carries the
/// exact text before and after, so it inverts exactly and survives the journal file.
/// </summary>
public sealed class EconomyOpsTests
{
    private const string Ak = "{\"tradeable-code\":\"Weapon_AK47\",\"base-purchase-price\":\"-1\"}";
    private const string Ak15000 = "{\"tradeable-code\":\"Weapon_AK47\",\"base-purchase-price\":\"15000\"}";

    public static IEnumerable<object[]> Ops()
    {
        EditOp[] ops =
        [
            new SetEconomyEntryOp("A_0_Armory", "Weapon_AK47", 1, Ak, Ak15000),
            new SetEconomyEntryOp("A_0_Armory", "Weapon_AK47", 2, null, Ak15000),
            new SetEconomySettingOp("traders-unlimited-stock", 17, "\"0\"", "\"1\""),
            new SetEconomySettingOp("my-own-setting", 21, null, "\"yes\""),
            new SetEconomySectionOp("A_3_Armory_1", 4, null, "[" + Ak + "]"),
            new ReplaceEconomyOp(EconomyOverrideTests.GameLayout, EconomyOverrideTests.OddEntry),
        ];
        return ops.Select(o => new object[] { o });
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public void EveryEconomyOpRoundTripsThroughTheJournalAndInvertsExactly(EditOp op)
    {
        Assert.Equal(op, EditOp.FromJson(op.ToJson()));
        Assert.Equal(op, op.Inverse().Inverse());
        Assert.NotEqual(op, op.Inverse());
        Assert.Empty(op.GetTouchedLevels());
        Assert.Null(op.GetPrimaryTarget());
        Assert.StartsWith("Economy:", op.Describe(), StringComparison.Ordinal);

        var folder = Path.Combine(Path.GetTempPath(), "ss-economy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "journal.jsonl");
            using (var journal = Journal.Open(path))
            {
                journal.Append(op);
                journal.Append(op.Inverse());
                journal.Undo();
            }

            using var back = Journal.Open(path);
            Assert.Equal(op, Assert.Single(back.Applied).Op);
            Assert.Equal(op.Inverse(), Assert.Single(back.RedoStack).Op);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AnOpThenItsInverseGivesTheSameTextBack()
    {
        var economy = EconomyOverride.Parse(EconomyOverrideTests.GameLayout);
        var steps = new List<EconomyOp>();
        void Do(IEnumerable<EconomyOp> ops)
        {
            foreach (var op in ops)
            {
                Assert.True(economy.Apply(op));
                steps.Add(op);
            }
        }

        Do(economy.SetOps("A_0_Armory", "Weapon_AK47", new TradeableOverride("Weapon_AK47") { PurchasePrice = 15000 }, keep: false));
        Assert.Contains("\t\t\t\t\t\"base-purchase-price\": \"15000\",\r\n", economy.ToJson(), StringComparison.Ordinal);
        Do(economy.SetOps("A_0_Armory", "Frag_Grenade", null, keep: false));
        Do(economy.SetOps("B_4_Saloon", "Beer", new TradeableOverride("Beer") { CanBePurchased = false }, keep: false));
        Do(economy.SettingOps("enable-fame-point-requirement", "\"0\""));
        Do(economy.SettingOps("traders-unlimited-stock", "\"1\""));
        Do([new SetEconomySectionOp("A_3_Armory_1", economy.Traders.Count, null, EconomyOverride.StockText(["Weapon_AK47", "Frag_Grenade"]))]);
        Assert.Equal(2, economy.Entries("A_3_Armory_1").Count);
        Assert.Null(economy.Find("A_0_Armory", "Frag_Grenade"));
        Assert.Equal("1", economy.Setting("traders-unlimited-stock"));

        foreach (var op in steps.AsEnumerable().Reverse())
        {
            Assert.True(economy.Apply((EconomyOp)op.Inverse()));
        }

        Assert.Equal(EconomyOverrideTests.GameLayout, economy.ToJson());

        var replace = new ReplaceEconomyOp(economy.ToJson(), EconomyOverrideTests.OddEntry);
        economy.Apply(replace);
        Assert.Equal(EconomyOverrideTests.OddEntry, economy.ToJson());
        economy.Apply((EconomyOp)replace.Inverse());
        Assert.Equal(EconomyOverrideTests.GameLayout, economy.ToJson());
    }

    [Fact]
    public void AStepWhoseEntryMovedStillFindsIt()
    {
        // The index is a hint: an entry removed above it (an older edit undone out of order) does not lose the step.
        var economy = EconomyOverride.Parse(EconomyOverrideTests.GameLayout);
        var change = Assert.Single(economy.SetOps("A_0_Armory", "Weapon_AK47", new TradeableOverride("Weapon_AK47") { PurchasePrice = 15000 }, keep: false));
        economy.Remove("A_0_Armory", "Frag_Grenade");
        Assert.True(economy.Apply(change));
        Assert.Equal(15000, Assert.Single(economy.Entries("A_0_Armory")).PurchasePrice);
    }

    [Fact]
    public void EditStateAcceptsEconomyOpsAloneAndInABatch()
    {
        // Before: EditState.Validate answered "Unsupported operation" and nothing could journal an economy edit.
        var state = new EditState();
        var setting = new SetEconomySettingOp("traders-unlimited-stock", 17, "\"0\"", "\"1\"");
        Assert.Null(state.Validate(setting));
        state.Apply(setting);
        var batch = new BatchOp("Economy: 2 prices", [new SetEconomyEntryOp("A_0_Armory", "Weapon_AK47", 0, Ak, Ak15000), setting.Inverse()]);
        Assert.Null(state.Validate(batch));
        state.Apply(batch);
        Assert.True(state.IsEmpty); // no map or asset edit
    }

    [Fact]
    public void CompareCountsTradersAndItems()
    {
        var before = EconomyOverride.Parse(EconomyOverrideTests.GameLayout);
        var after = before.Clone();
        after.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 15000 });
        after.Set("A_0_Armory", new TradeableOverride("Weapon_M1911") { PurchasePrice = 900 });
        after.Remove("A_0_Armory", "Frag_Grenade");
        after.EnsureSection("A_3_Armory_1");
        after.Apply(new SetEconomySectionOp("B_4_Saloon", after.IndexOf("B_4_Saloon"), "[]", null));
        foreach (var op in after.SettingOps("enable-fame-point-requirement", "\"0\""))
        {
            after.Apply(op);
        }

        var diff = EconomyDiff.Compare(before, after);
        Assert.Equal(new EconomyDiff(1, 1, 1, 1, 1, 1, 1, ["A_0_Armory", "A_3_Armory_1", "B_4_Saloon"]) with { ChangedTraders = diff.ChangedTraders }, diff);
        Assert.Equal(["A_0_Armory", "A_3_Armory_1", "B_4_Saloon"], diff.ChangedTraders);
        Assert.Equal(EconomyOverrideTests.GameLayout, before.ToJson()); // the copy was edited, not the original

        // The fewest single steps, and they give the other file's text exactly.
        var steps = EconomyDiff.Steps(before, after);
        Assert.DoesNotContain(steps, s => s is ReplaceEconomyOp);
        Assert.Equal(6, steps.Count);
        var probe = before.Clone();
        foreach (var step in steps)
        {
            probe.Apply(step);
        }

        Assert.Equal(after.ToJson(), probe.ToJson());
        Assert.Empty(EconomyDiff.Steps(after, probe));
        Assert.True(EconomyDiff.Compare(after, probe).IsEmpty);

        // Settings in another order: one whole-file step.
        var reordered = EconomyOverride.Parse(EconomyOverrideTests.GameLayout.Replace(
            "\t\t\"economy-reset-time-hours\": \"-1.0\",\r\n\t\t\"prices-randomization-time-hours\": \"-1.0\",",
            "\t\t\"prices-randomization-time-hours\": \"-1.0\",\r\n\t\t\"economy-reset-time-hours\": \"-1.0\",", StringComparison.Ordinal));
        Assert.IsType<ReplaceEconomyOp>(Assert.Single(EconomyDiff.Steps(before, reordered)));
    }
}
