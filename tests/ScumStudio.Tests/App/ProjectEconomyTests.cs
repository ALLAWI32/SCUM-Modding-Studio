using ScumStudio.App.Localization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;

namespace ScumStudio.Tests.App;

/// <summary>
/// The project's <c>EconomyOverride.json</c> as <see cref="ScumStudio.App.Services.ProjectEconomy"/> keeps it: a placed
/// trader's edits through undo, reopen and redo, and a file the app could not read, or one changed outside, never lost.
/// </summary>
public sealed class ProjectEconomyTests
{
    internal const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    internal const string Outpost = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost";

    /// <summary>An armory placed on a farm as a trader of its own (what the Map's Add object box journals).</summary>
    internal static AddBlueprintActorOp PlaceTrader(string name, string actor = "BP_Armory_Placed") =>
        new(Farm, actor, "/Game/X/BP_Outpost_Armory.BP_Outpost_Armory_C", new ActorRef(Outpost, "BP_Armory_1"), TransformValue.FromTransform(FTransform.Identity))
        {
            Trader = new TraderPost(name, "Armorer", "Outpost_A_3"),
        };

    [Fact]
    public async Task APlacedTradersEditsSurviveUndoReopenAndRedo()
    {
        // Audit F100: undo moved the section into a stash that the next open forgot; the redo brought the trader back bare.
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var folder = project.DirectoryPath;
        var store = ctx.Services.Economy;
        ctx.Services.Projects.Apply(PlaceTrader("A_3_Armory_1"));
        Assert.True(store.Economy.HasSection("A_3_Armory_1"));
        store.Economy.Set("A_3_Armory_1", new TradeableOverride("Weapon_AK47") { PurchasePrice = 12345 }, keep: true);
        store.Save();

        ctx.Services.Projects.Undo();
        ctx.Services.Projects.Close();
        await ctx.Services.Projects.OpenAsync(folder);
        ctx.Services.Projects.Redo();

        Assert.Equal(12345, store.Economy.Find("A_3_Armory_1", "Weapon_AK47")?.PurchasePrice);
        Assert.Equal(12345, EconomyOverride.LoadFrom(folder)!.Find("A_3_Armory_1", "Weapon_AK47")?.PurchasePrice);
    }

    [Fact]
    public async Task AnUnreadableFileIsStillKeptAfterTwoSessions()
    {
        // Audit F102: every session's first save copied the file over the one .bak, so the second session lost the original.
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var folder = project.DirectoryPath;
        var path = Path.Combine(folder, EconomyOverride.FileName);
        ctx.Services.Projects.Close();
        const string original = "{ \"economy-override\": { \"traders\": { \"A_0_Armory\": [ { \"tradeable-code\": \"Weapon_AK47\", } ] } ";
        File.WriteAllText(path, original);

        await ctx.Services.Projects.OpenAsync(folder);
        ctx.Services.Projects.Apply(PlaceTrader("A_3_Armory_1")); // its section is written
        ctx.Services.Projects.Close();

        await ctx.Services.Projects.OpenAsync(folder);
        var store = ctx.Services.Economy;
        store.Economy.Set("A_3_Armory_1", new TradeableOverride("Weapon_AK47") { PurchasePrice = 5 }, keep: true);
        store.Save();

        Assert.Contains(Directory.GetFiles(folder, EconomyOverride.FileName + "*.bak"), f => File.ReadAllText(f) == original);
    }

    [Fact]
    public async Task AFileWithDuplicateKeysOpensWithAWarning()
    {
        // Audit F103/F108/F286: the ArgumentException of a duplicate key escaped the project's Changed event.
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var folder = project.DirectoryPath;
        var path = Path.Combine(folder, EconomyOverride.FileName);
        ctx.Services.Projects.Close();
        const string file = "{\"economy-override\":{\"economy-logging\":\"1\",\"economy-logging\":\"0\",\"traders\":{\"A_0_Armory\":[]}}}";
        File.WriteAllText(path, file);
        var store = ctx.Services.Economy;
        var later = 0;
        ctx.Services.Projects.Changed += (_, _) => later++;

        await ctx.Services.Projects.OpenAsync(folder);

        Assert.True(later > 0); // the subscribers after the economy still ran
        Assert.True(store.Economy.HasSection("A_0_Armory")
            || ctx.Services.Notifications.Toasts.Any(t => t.Title.Contains("EconomyOverride.json", StringComparison.Ordinal)));
        Assert.Equal(file, File.ReadAllText(path)); // not overwritten with the game's defaults
    }

    [Fact]
    public async Task AFileChangedOutsideIsKeptBeforeTheNextSave()
    {
        // Audit F300: a file an admin changed by hand while the project was open was overwritten by the next edit.
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var folder = project.DirectoryPath;
        var path = Path.Combine(folder, EconomyOverride.FileName);
        var store = ctx.Services.Economy;
        store.Economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 5 });
        store.Save();

        const string outside = "{\"economy-override\":{\"traders\":{\"B_4_Armory\":[{\"tradeable-code\":\"Weapon_M1911\",\"base-purchase-price\":\"77\"}]}}}";
        File.WriteAllText(path, outside);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        store.Economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 6 });
        store.Save();

        var backup = Assert.Single(Directory.GetFiles(folder, EconomyOverride.FileName + "*.bak"), f => File.ReadAllText(f) == outside);

        // Review (economy): the user is told where that version went, also when the Economy page was never opened (only the
        // page turned the notice into a message, and its text said the page showed the outside version).
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == Loc.T("Economy.ChangedOutside") && (t.Message ?? string.Empty).Contains(backup, StringComparison.Ordinal));
    }
}
