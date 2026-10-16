using System.Diagnostics;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Economy page's edits through <see cref="ScumStudio.App.Services.ProjectEconomy"/>: each one journal entry (Ctrl+Z
/// undoes the economy, not the last map edit), the import of a server's file and the code view's text.
/// </summary>
public sealed class ProjectEconomyEditTests(ITestOutputHelper output)
{
    private static TradeableOverride Price(string code, int price) => new(code) { PurchasePrice = price };

    [Fact]
    public async Task AnEntryEditIsOneJournalEntryThatUndoesAndRedoes()
    {
        // Audit F113/F298: the page wrote the model directly, so Ctrl+Z undid the last map edit instead.
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var path = Path.Combine(project.DirectoryPath, EconomyOverride.FileName);
        var store = ctx.Services.Economy;
        var projects = ctx.Services.Projects;
        Assert.True(store.CanEdit);
        var before = store.Economy.ToJson();
        var history = projects.History.Count;

        Assert.NotNull(store.SetEntry("A_0_Armory", "Weapon_AK47", Price("Weapon_AK47", 15000)));
        Assert.Equal(history + 1, projects.History.Count);
        Assert.Equal(15000, store.Economy.Find("A_0_Armory", "Weapon_AK47")?.PurchasePrice);
        Assert.Equal(store.Economy.ToJson(), File.ReadAllText(path));
        Assert.Contains("\"base-purchase-price\": \"15000\"", store.Text, StringComparison.Ordinal);
        Assert.Null(store.SetEntry("A_0_Armory", "Weapon_AK47", Price("Weapon_AK47", 15000))); // nothing changes: nothing journaled

        projects.Undo();
        Assert.Equal(before, store.Economy.ToJson());
        Assert.Equal(before, File.ReadAllText(path));
        projects.Redo();
        Assert.Equal(15000, store.Economy.Find("A_0_Armory", "Weapon_AK47")?.PurchasePrice);
        Assert.Equal(store.Economy.ToJson(), File.ReadAllText(path));

        // Several rows at once (the page's bulk edit) are one entry too, and a setting is one more.
        var oneEdit = store.Economy.ToJson();
        var entry = store.SetEntries("A_0_Armory", [("Weapon_AK47", Price("Weapon_AK47", 9000)), ("Frag_Grenade", Price("Frag_Grenade", 50))], "Economy: 2 prices");
        Assert.IsType<BatchOp>(entry!.Op);
        Assert.NotNull(store.SetSetting("traders-unlimited-stock", "1"));
        Assert.Equal("1", store.Economy.Setting("traders-unlimited-stock"));
        Assert.Equal(history + 3, projects.History.Count);
        Assert.NotNull(store.SetSetting("traders-unlimited-stock", null)); // back to the game's value
        Assert.Equal("0", store.Economy.Setting("traders-unlimited-stock"));
        projects.Undo();
        projects.Undo();
        projects.Undo();
        Assert.Equal(oneEdit, store.Economy.ToJson());
        Assert.Equal(oneEdit, File.ReadAllText(path));

        // A reset is one entry; the journal holds what the page did after a reopen (nothing replayed, nothing lost).
        Assert.NotNull(store.ResetTrader("A_0_Armory"));
        Assert.Empty(store.Economy.Entries("A_0_Armory"));
        var folder = project.DirectoryPath;
        projects.Close();
        Assert.False(store.CanEdit);
        Assert.Null(store.SetEntry("A_0_Armory", "Weapon_AK47", Price("Weapon_AK47", 1))); // audit F299: no project, no edit
        await projects.OpenAsync(folder);
        Assert.Empty(store.Economy.Entries("A_0_Armory"));
        projects.Undo();
        Assert.Equal(oneEdit, store.Economy.ToJson());
    }

    [Fact]
    public async Task ImportReplacesTheEconomyKeepsPlacedSectionsAndExportsItUnchanged()
    {
        // Owner #25 and Discord ("the economy doesn't have a way to upload its own economy file").
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var path = Path.Combine(project.DirectoryPath, EconomyOverride.FileName);
        var store = ctx.Services.Economy;
        var projects = ctx.Services.Projects;
        store.SetSetting("economy-logging", "0");
        var before = File.ReadAllText(path);
        var history = projects.History.Count;

        var result = store.Import(EconomyOverrideTests.GameLayout, "EconomyOverride.json");
        Assert.True(result.Imported);
        Assert.NotNull(result.Entry);
        Assert.Equal(history + 1, projects.History.Count);
        Assert.Equal(EconomyOverrideTests.GameLayout, store.Text);
        Assert.Equal(EconomyOverrideTests.GameLayout, File.ReadAllText(path));
        Assert.Equal(EconomyOverrideTests.GameLayout, store.ForExport().ToJson());
        Assert.Equal(before, File.ReadAllText(result.BackupPath!));
        Assert.Equal((4, 0, 0, 2, 18), (result.Diff!.TradersAdded, result.Diff.TradersRemoved, result.Diff.TradersChanged, result.Diff.ItemsAdded, result.Diff.SettingsChanged));

        projects.Undo();
        Assert.Equal(before, store.Economy.ToJson());
        Assert.Equal(before, File.ReadAllText(path));
        projects.Redo();
        Assert.Equal(EconomyOverrideTests.GameLayout, store.Text);

        // A placed trader's section survives an import of a file that lacks it; the file's own sections win.
        projects.Apply(ProjectEconomyTests.PlaceTrader("A_3_Armory_1"));
        store.SetEntry("A_3_Armory_1", "Weapon_AK47", Price("Weapon_AK47", 777));
        store.SetEntry("A_0_Armory", "Weapon_AK47", Price("Weapon_AK47", 15000));
        var placed = store.Economy.ToJson();
        result = store.Import(EconomyOverrideTests.GameLayout, "server");
        Assert.True(result.Imported);
        Assert.Equal(1, result.KeptPlacedSections);
        Assert.Equal((0, 0, 1, 1), (result.Diff!.TradersAdded, result.Diff.TradersRemoved, result.Diff.TradersChanged, result.Diff.ItemsChanged));
        Assert.Equal(777, store.Economy.Find("A_3_Armory_1", "Weapon_AK47")?.PurchasePrice);
        Assert.Null(store.Economy.Find("A_0_Armory", "Weapon_AK47")?.PurchasePrice);
        Assert.StartsWith(EconomyOverrideTests.GameLayout[..^12], store.Text, StringComparison.Ordinal);
        projects.Undo();
        Assert.Equal(placed, store.Economy.ToJson());
        Assert.Equal(placed, File.ReadAllText(path));

        // Undoing the first import (made before the trader was placed) keeps the parked trader's section; the redos give
        // the same file back.
        projects.Undo();
        projects.Undo();
        projects.Undo();
        Assert.Contains("A_3_Armory_1", store.Parked);
        projects.Undo();
        Assert.True(store.Economy.HasSection("A_3_Armory_1"));
        Assert.False(store.Economy.HasSection("A_0_Armory"));
        for (var i = 0; i < 4; i++)
        {
            projects.Redo();
        }

        Assert.Equal(placed, store.Economy.ToJson());
    }

    [Fact]
    public async Task ImportWithErrorsChangesNothing()
    {
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var path = Path.Combine(project.DirectoryPath, EconomyOverride.FileName);
        var store = ctx.Services.Economy;
        store.SetSetting("economy-logging", "0");
        var text = File.ReadAllText(path);
        var history = ctx.Services.Projects.History.Count;

        var result = store.Import(EconomyOverrideTests.OddEntry, "old list"); // 12,000 and yes
        Assert.False(result.Imported);
        Assert.Contains(result.Problems, p => p is { Kind: EconomyProblemKind.BadNumber, IsError: true, Line: 11, Key: "base-purchase-price" });
        Assert.Contains(result.Problems, p => p is { Kind: EconomyProblemKind.BadBool, IsError: true, Line: 14 });
        Assert.Contains(result.Problems, p => p is { Kind: EconomyProblemKind.UnknownKey, IsError: false, Key: "max-stock" });
        Assert.Equal(EconomyProblemKind.Syntax, Assert.Single(store.Import("{ \"economy-override\": ", "cut").Problems).Kind);
        Assert.Equal(text, File.ReadAllText(path));
        Assert.Equal(history, ctx.Services.Projects.History.Count);
        Assert.Empty(Directory.GetFiles(project.DirectoryPath, "*.bak"));

        ctx.Services.Projects.Close();
        Assert.False(store.Import(EconomyOverrideTests.GameLayout, "no project").Imported);
    }

    [Fact]
    public async Task ApplyTextWithErrorsChangesNothingAndValidTextIsOneEntry()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var store = ctx.Services.Economy;
        var projects = ctx.Services.Projects;
        var text = store.Text;
        var history = projects.History.Count;

        var bad = store.ApplyText(text.Replace("\"traders-unlimited-stock\": \"0\"", "\"traders-unlimited-stock\": \"zero\"", StringComparison.Ordinal));
        Assert.Null(bad.Entry);
        Assert.Contains(bad.Problems, p => p is { Kind: EconomyProblemKind.BadNumber, IsError: true, Key: "traders-unlimited-stock" });
        Assert.Equal("0", store.Economy.Setting("traders-unlimited-stock"));
        Assert.Equal(history, projects.History.Count);

        var one = store.ApplyText(text.Replace("\"traders-unlimited-stock\": \"0\"", "\"traders-unlimited-stock\": \"1\"", StringComparison.Ordinal));
        Assert.Equal(1, one.Changes);
        Assert.IsType<SetEconomySettingOp>(one.Entry!.Op);
        Assert.Equal("1", store.Economy.Setting("traders-unlimited-stock"));
        Assert.Null(store.ApplyText(store.Text).Entry); // the same text: no entry

        // A bigger edit (two settings and a trader's prices) is still one entry, and one Ctrl+Z.
        var edited = store.Text
            .Replace("\"economy-logging\": \"1\"", "\"economy-logging\": \"0\"", StringComparison.Ordinal)
            .Replace("\"traders-unlimited-funds\": \"0\"", "\"traders-unlimited-funds\": \"1\"", StringComparison.Ordinal)
            .Replace("\"traders\":\r\n\t\t{\r\n\t\t}", "\"traders\":\r\n\t\t{\r\n\t\t\t\"A_0_Armory\": [{\"tradeable-code\": \"Weapon_AK47\", \"base-purchase-price\": \"15000\"}]\r\n\t\t}", StringComparison.Ordinal);
        var many = store.ApplyText(edited);
        Assert.Equal(3, many.Changes);
        Assert.IsType<BatchOp>(many.Entry!.Op);
        Assert.Equal(15000, store.Economy.Find("A_0_Armory", "Weapon_AK47")?.PurchasePrice);
        Assert.Equal(history + 2, projects.History.Count);
        projects.Undo();
        Assert.Equal("1", store.Economy.Setting("economy-logging"));
        Assert.False(store.Economy.HasSection("A_0_Armory"));
    }

    [Fact]
    public async Task ATradersNameIsReservedWhileItIsParked()
    {
        // Audit F100/F277: an undone trader's section stays in the file, hidden, and its name stays taken.
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var store = ctx.Services.Economy;
        var projects = ctx.Services.Projects;
        projects.Apply(ProjectEconomyTests.PlaceTrader("A_3_Armory_1"));
        Assert.True(store.IsNameReserved("A_3_Armory_1"));
        Assert.Contains("A_3_Armory_1", store.Text, StringComparison.Ordinal);

        projects.Undo();
        Assert.Contains("A_3_Armory_1", store.Parked);
        Assert.Contains("A_3_Armory_1", store.Hidden);
        Assert.True(store.IsNameReserved("a_3_armory_1"));
        Assert.False(store.IsNameReserved("A_3_Armory_2"));
        Assert.DoesNotContain("A_3_Armory_1", store.Text, StringComparison.Ordinal);
        Assert.False(store.ForExport().HasSection("A_3_Armory_1"));
        Assert.True(EconomyOverride.LoadFrom(project.DirectoryPath)!.HasSection("A_3_Armory_1"));
        Assert.True(TraderPosts.ParkedTraders(project.Journal, project.State).SetEquals(["A_3_Armory_1"]));
        Assert.True(ScumStudio.Level.Export.ProjectExporter.LoadEconomy(project).Economy is { } exported && !exported.HasSection("A_3_Armory_1"));

        // A code edit of the page's text (which does not show it) keeps the parked section.
        Assert.NotNull(store.ApplyText(store.Text.Replace("\"economy-logging\": \"1\"", "\"economy-logging\": \"0\"", StringComparison.Ordinal)).Entry);
        Assert.True(store.Economy.HasSection("A_3_Armory_1"));

        // That edit dropped the redo stack: the trader can never come back. Its section stays parked (review, economy: it
        // became a visible unknown trader that every export wrote), also after the project is opened again.
        Assert.Contains("A_3_Armory_1", store.Parked);
        Assert.True(store.IsNameReserved("A_3_Armory_1"));
        Assert.DoesNotContain("A_3_Armory_1", store.Text, StringComparison.Ordinal);
        Assert.False(store.ForExport().HasSection("A_3_Armory_1"));

        var folder = project.DirectoryPath;
        projects.Close();
        await projects.OpenAsync(folder);
        Assert.Contains("A_3_Armory_1", store.Parked);
        Assert.False(store.ForExport().HasSection("A_3_Armory_1"));
        Assert.True(ScumStudio.Level.Export.ProjectExporter.LoadEconomy(projects.Current!).Economy is { } reopened && !reopened.HasSection("A_3_Armory_1"));
        Assert.True(EconomyOverride.LoadFrom(folder)!.HasSection("A_3_Armory_1"));
    }

    [Fact]
    public async Task SaveAndTextStayUnder16msOnFivePlacedTraders()
    {
        // A measurement for the report (the page saves on every edit, on the UI thread): five placed armories, 700 items each.
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
        var store = ctx.Services.Economy;
        store.UseDefaults(new EconomyDefaults(
            Enumerable.Range(0, 700).Select(i => new TradeableDefault($"Item_{i}", $"Item {i}", "Misc", ["Armorer"], 100 + i, 10, true, 0, false)).ToList(), []));
        for (var n = 1; n <= 5; n++)
        {
            ctx.Services.Projects.Apply(ProjectEconomyTests.PlaceTrader($"A_3_Armory_{n}", $"BP_Armory_Placed_{n}"));
        }

        Assert.Equal(3500, Enumerable.Range(1, 5).Sum(n => store.Economy.Entries($"A_3_Armory_{n}").Count));
        var (text, save, edit) = (new List<double>(), new List<double>(), new List<double>());
        for (var i = 0; i < 12; i++)
        {
            var watch = Stopwatch.StartNew();
            _ = store.Economy.ToJson(store.Hidden);
            text.Add(watch.Elapsed.TotalMilliseconds);

            store.Economy.Set("A_3_Armory_1", Price("Item_1", 1000 + i), keep: true);
            watch.Restart();
            store.Save();
            save.Add(watch.Elapsed.TotalMilliseconds);

            watch.Restart();
            store.SetEntry("A_3_Armory_2", "Item_2", Price("Item_2", 2000 + i));
            edit.Add(watch.Elapsed.TotalMilliseconds);
        }

        static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
        output.WriteLine($"{store.Text.Length:N0} characters: Text {Median(text):F1} ms, Save {Median(save):F1} ms, a journaled edit {Median(edit):F1} ms (medians of 12).");
        Assert.True(Median(text) < 200 && Median(save) < 200 && Median(edit) < 200);

        // A code apply (checked again, or with the problems the page's worker found) and an import of the whole file (review,
        // economy: these run on the UI thread and were not measured).
        var (apply, applyChecked, import) = (new List<double>(), new List<double>(), new List<double>());
        for (var i = 0; i < 5; i++)
        {
            var model = EconomyOverride.Parse(store.Text);
            model.Set("A_3_Armory_3", Price("Item_3", 3000 + i), keep: true);
            var edited = model.ToJson();
            var watch = Stopwatch.StartNew();
            Assert.NotNull(store.ApplyText(edited).Entry);
            apply.Add(watch.Elapsed.TotalMilliseconds);

            model.Set("A_3_Armory_3", Price("Item_3", 4000 + i), keep: true);
            edited = model.ToJson();
            var problems = EconomyValidator.Validate(edited, store.Context); // on the page's worker
            watch.Restart();
            Assert.NotNull(store.ApplyText(edited, problems).Entry);
            applyChecked.Add(watch.Elapsed.TotalMilliseconds);

            model.Set("A_3_Armory_4", Price("Item_4", 5000 + i), keep: true);
            edited = model.ToJson();
            watch.Restart();
            Assert.True(store.Import(edited, "server.json").Imported);
            import.Add(watch.Elapsed.TotalMilliseconds);
        }

        output.WriteLine($"Code apply {Median(apply):F1} ms ({Median(applyChecked):F1} ms with the worker's problems), import {Median(import):F1} ms (medians of 5).");
        Assert.True(Median(apply) < 5000 && Median(applyChecked) < 5000 && Median(import) < 5000);

        // The journal list the store compares on every change, on a 10,000-entry journal.
        var big = ctx.Combine("big.jsonl");
        var step = new SetEconomySettingOp("economy-logging", 0, "\"1\"", "\"0\"");
        File.WriteAllLines(big, Enumerable.Range(0, 10_001).Select(i => Journal.SerializeRecord(i == 0
            ? new JournalRecord { Type = JournalRecordType.Header, At = DateTimeOffset.UnixEpoch, Format = Journal.FormatId }
            : new JournalRecord { Seq = i, Type = JournalRecordType.Edit, At = DateTimeOffset.UnixEpoch, Edit = step })));
        using var journal = Journal.Open(big);
        Assert.Equal(10_000, journal.Applied.Count);

        var copy = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            _ = journal.Applied;
        }

        output.WriteLine($"Journal.Applied of 10,000 entries: {copy.Elapsed.TotalMilliseconds / 100:F3} ms.");
        Assert.True(copy.Elapsed.TotalMilliseconds / 100 < 50);
    }
}
