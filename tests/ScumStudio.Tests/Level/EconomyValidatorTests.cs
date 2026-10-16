using System.Diagnostics;
using System.Text;
using ScumStudio.Level.Economy;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The code view's check of an <c>EconomyOverride.json</c> (owner, 2026-10-12: "syntax check and validation messages:
/// unknown trader, unknown tradeable code, bad number"): every problem on its line and column.
/// </summary>
public sealed class EconomyValidatorTests(ITestOutputHelper output)
{
    private static readonly TradeableDefault Ak = new("Weapon_AK47", "AK-47", "RangedWeapon", ["Armorer"], 15000, 3000, true, 0, false);
    private static readonly TradeableDefault Gold = new("Gold_Bar", "Gold", "Valuables", ["Bartender"], 1, 1, true, 0, false) { ImmuneToOverrides = true };

    private static EconomyContext Context => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A_0_Armory" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A_3_Armory_1" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B_4_Armory" },
        code => code switch { "Weapon_AK47" => Ak, "Gold_Bar" => Gold, _ => null });

    [Fact]
    public void AValidGameFileHasNoProblems()
    {
        Assert.Empty(EconomyValidator.Validate(EconomyOverrideTests.GameLayout, null));
        Assert.Empty(EconomyValidator.Validate(EconomyOverride.CreateDefault().ToJson(), Context));
    }

    [Fact]
    public void ASyntaxErrorHasItsLineAndColumn()
    {
        const string text = "{\r\n\t\"economy-override\":\r\n\t{\r\n\t\t\"economy-logging\" \"1\",\r\n\t\t\"traders\": {}\r\n\t}\r\n}";
        var problem = Assert.Single(EconomyValidator.Validate(text, null));
        Assert.Equal(EconomyProblemKind.Syntax, problem.Kind);
        Assert.True(problem.IsError);
        Assert.Equal(4, problem.Line);
        Assert.InRange(problem.Column, 20, 21); // after "economy-logging", where the colon is missing
        Assert.False(string.IsNullOrEmpty(problem.Value));

        var cut = EconomyValidator.Validate(EconomyOverrideTests.GameLayout[..200], null);
        Assert.Equal(EconomyProblemKind.Syntax, Assert.Single(cut).Kind);
    }

    [Fact]
    public void UnknownTraderUnknownCodeImmuneDuplicateAndBadNumberAreFoundOnTheirLines()
    {
        string[] lines =
        [
            "{",
            "\t\"economy-override\":",
            "\t{",
            "\t\t\"traders-unlimited-stock\": \"x\",",
            "\t\t\"traders\":",
            "\t\t{",
            "\t\t\t\"A_0_Armory\": [",
            "\t\t\t\t{\"tradeable-code\": \"Weapon_AK47\", \"base-purchase-price\": \"12,000\"},",
            "\t\t\t\t{\"tradeable-code\": \"Weapon_AK47\", \"can-be-purchased\": \"yes\"},",
            "\t\t\t\t{\"tradeable-code\": \"Weapon_Nope\", \"max-stock\": \"5\"},",
            "\t\t\t\t{\"tradeable-code\": \"Gold_Bar\", \"base-sell-price\": \"-5\"},",
            "\t\t\t\t{\"base-purchase-price\": \"1\"},",
            "\t\t\t\t{\"tradeable-code\": \"Weapon_AK47\", \"base-purchase-price\": \"100\", \"base-sell-price\": \"200\"}",
            "\t\t\t],",
            "\t\t\t\"Z_9_Nobody\": [],",
            "\t\t\t\"A_3_Armory_1\": [],",
            "\t\t\t\"B_4_Armory\": []",
            "\t\t}",
            "\t}",
            "}",
        ];

        var problems = EconomyValidator.Validate(string.Join("\r\n", lines), Context);

        (EconomyProblemKind, int)[] expected =
        [
            (EconomyProblemKind.BadNumber, 4),
            (EconomyProblemKind.BadNumber, 8),
            (EconomyProblemKind.BadBool, 9),
            (EconomyProblemKind.DuplicateEntry, 9),
            (EconomyProblemKind.UnknownKey, 10),
            (EconomyProblemKind.UnknownCode, 10),
            (EconomyProblemKind.NegativeValue, 11),
            (EconomyProblemKind.ImmuneCode, 11),
            (EconomyProblemKind.MissingCode, 12),
            (EconomyProblemKind.DuplicateEntry, 13),
            (EconomyProblemKind.SellAboveBuy, 13),
            (EconomyProblemKind.UnknownTrader, 15),
            (EconomyProblemKind.HiddenTrader, 17),
        ];
        Assert.Equal(expected.Order(), problems.Select(p => (p.Kind, p.Line)).Order());
        var price = problems.Single(p => p is { Kind: EconomyProblemKind.BadNumber, Line: 8 });
        Assert.Equal((62, "A_0_Armory", "Weapon_AK47", "base-purchase-price", "12,000", true), (price.Column, price.Trader, price.Code, price.Key, price.Value, price.IsError));
        Assert.All(problems.Where(p => p.Kind is not (EconomyProblemKind.BadNumber or EconomyProblemKind.BadBool or EconomyProblemKind.MissingCode)), p => Assert.False(p.IsError));
    }

    [Fact]
    public void AFileWithoutTheWrapperIsAccepted()
    {
        // An older list pasted on its own: the settings and traders without "economy-override".
        const string bare = "{\"economy-logging\":\"1\",\"traders\":{\"A_0_Armory\":[{\"tradeable-code\":\"Weapon_AK47\",\"base-purchase-price\":\"500\"}]}}";
        var problem = Assert.Single(EconomyValidator.Validate(bare, null));
        Assert.Equal((EconomyProblemKind.NoRoot, false), (problem.Kind, problem.IsError));

        var economy = EconomyOverride.Parse(bare);
        Assert.Equal(500, economy.Find("A_0_Armory", "Weapon_AK47")?.PurchasePrice);
        Assert.StartsWith("{\r\n\t\"economy-override\":\r\n\t{\r\n\t\t\"economy-logging\": \"1\",", economy.ToJson(), StringComparison.Ordinal);
        Assert.Empty(EconomyValidator.Validate(economy.ToJson(), null));

        // Neither the wrapper nor its members: not an economy file.
        Assert.Contains(EconomyValidator.Validate("{\"name\":\"x\"}", null), p => p is { Kind: EconomyProblemKind.NoRoot, IsError: true });
        Assert.Contains(EconomyValidator.Validate("[]", null), p => p is { Kind: EconomyProblemKind.NotAnObject, IsError: true });
    }

    [Fact]
    public void AMegabyteIsCheckedInAFewMilliseconds()
    {
        // The page checks on a worker as the user types; five placed traders' whole stock is about a megabyte.
        var sb = new StringBuilder("{\"economy-override\":{\"traders\":{");
        for (var t = 0; t < 5; t++)
        {
            sb.Append(t == 0 ? string.Empty : ",").Append("\"A_3_Armory_").Append(t).Append("\":[");
            for (var i = 0; i < 700; i++)
            {
                sb.Append(i == 0 ? string.Empty : ",").Append("{\"tradeable-code\":\"Item_").Append(i)
                    .Append("\",\"base-purchase-price\":\"-1\",\"base-sell-price\":\"-1\",\"delta-price\":\"-1.0\",\"can-be-purchased\":\"default\",\"required-famepoints\":\"-1\",\"available-after-sale-only\":\"default\"}");
            }

            sb.Append(']');
        }

        var text = EconomyOverride.Parse(sb.Append("}}}").ToString()).ToJson();
        EconomyValidator.Validate(text, Context);
        var watch = Stopwatch.StartNew();
        var problems = EconomyValidator.Validate(text, Context);
        watch.Stop();
        output.WriteLine($"{text.Length:N0} characters checked in {watch.Elapsed.TotalMilliseconds:F1} ms ({problems.Count} problems).");
        Assert.Equal(3500 + 4, problems.Count); // unknown codes, and the traders that are neither the game's nor placed
        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
    }
}
