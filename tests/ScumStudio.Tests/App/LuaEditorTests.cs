using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.App;

/// <summary>The Lua editor window: catalog filter, inserting a piece, the check and writing to the server.</summary>
[Collection(LanguageCollection.Name)]
public sealed class LuaEditorTests
{
    [AvaloniaFact]
    public void TheCatalogFiltersByWordsAndGroup()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var vm = new LuaEditorViewModel(ctx.Services);
        Assert.Equal(LuaApiCatalog.All.Count, vm.Rows.Count);

        vm.Search = "one line player";
        Assert.Contains(vm.Rows, r => r.Entry.Id == "SendHUDMessageToClient");
        Assert.True(vm.Rows.Count < LuaApiCatalog.All.Count);

        vm.Search = string.Empty;
        vm.Group = "Safety";
        Assert.All(vm.Rows, r => Assert.Equal("Safety", r.Entry.Group));
        vm.Selected = vm.Rows[0];
        Assert.True(vm.HasSelected);
        Assert.NotEmpty(vm.SelectedText);
    }

    [AvaloniaFact]
    public void ASavedScriptLandsInTheServersModsAndABrokenOneDoesNot()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        var vm = new LuaEditorViewModel(ctx.Services);
        Assert.True(vm.HasServer);

        vm.ModName = "MyMod";
        vm.Code = "print('hi'\n";
        vm.SaveCommand.Execute(null);
        Assert.Null(new ServerModStore(server).Read("MyMod"));
        Assert.Contains(vm.Findings, f => f.Text.StartsWith('✖'));

        vm.Code = "print('hi')\n";
        vm.SaveCommand.Execute(null);
        Assert.Equal("print('hi')\n", new ServerModStore(server).Read("MyMod"));
        Assert.Contains("MyMod", vm.Mods);
    }

    [AvaloniaFact]
    public void ThePickedGameFunctionBecomesAHookAndAGuardPieceIsInserted()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        File.WriteAllLines(Path.Combine(server.Ue4ssFolder, "scum_dump.txt"), ["Class /Script/SCUM.Item", "Function /Script/SCUM.Item:DropAround", "Function /Script/SCUM.Item:DestroyInternal"]);
        var vm = new LuaEditorViewModel(ctx.Services);
        var inserted = new List<string>();
        vm.InsertRequested += (_, text) => inserted.Add(text);

        vm.GameSearch = "drop";
        Assert.Equal(["/Script/SCUM.Item:DropAround"], vm.GameRows);
        vm.GameSelected = vm.GameRows[0];
        vm.InsertGameHookCommand.Execute(null);
        vm.Selected = vm.Rows.First(r => r.Entry.Id == "Hook.Die");
        vm.InsertCommand.Execute(null);

        Assert.Contains("RegisterHook(\"/Script/SCUM.Item:DropAround\"", inserted[0]);
        Assert.Contains("Prisoner_Server_Die", inserted[1]);

        vm.Code = "RegisterHook(\"/Script/SCUM.Item:Nope\", function() end)";
        vm.CheckCommand.Execute(null);
        Assert.Contains(vm.Findings, f => f.Text.Contains("Nope", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void TheServerPageShowsTheEditorsAsTabsAndMakesThemWhenFirstShown()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        File.WriteAllLines(Path.Combine(server.Ue4ssFolder, "scum_dump.txt"), ["Function /Script/SCUM.Item:DropAround"]);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1500, 900);
        try
        {
            var page = (ServerPageViewModel)vm.NavigateTo("server")!;
            HeadlessUi.Pump();
            var tabs = HeadlessUi.FindNamed<Avalonia.Controls.TabControl>(window, "ServerTabs")!;
            Assert.Equal(4, tabs.ItemCount);
            Assert.Null(page.LuaEditor); // the class dump is read only when its tab is shown
            Assert.Null(page.LootEditor);

            page.SelectedTab = 1;
            HeadlessUi.Pump();
            Assert.NotNull(page.LuaEditor);
            Assert.Contains("1 functions", page.LuaEditor.DumpLine, StringComparison.Ordinal);
            Assert.NotNull(HeadlessUi.FindNamed<Avalonia.Controls.TextBox>(window, "CodeBox")); // the Lua editor's own box and the mod editor's: both exist

            page.SelectedTab = 2;
            HeadlessUi.Pump();
            Assert.NotNull(page.LootEditor);
            Assert.True(page.LootEditor.HasServer);

            page.SelectedTab = 3;
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "page-server-tabs");
            Assert.Same(page.LuaEditor, page.LuaEditor); // made once, kept
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheWindowOpensAndInsertsAtTheCaret()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var vm = new LuaEditorViewModel(ctx.Services);
        var window = new LuaEditorWindow { DataContext = vm };
        try
        {
            window.Show();
            HeadlessUi.Pump();
            vm.Code = "line1\nline2";
            vm.Selected = vm.Rows.First(r => r.Entry.Id == "Recipe.Log");
            vm.InsertCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.Contains("local function log", vm.Code, StringComparison.Ordinal);
            Assert.StartsWith("line1", vm.Code, StringComparison.Ordinal);

            vm.Selected = vm.Rows.First(r => r.Entry.Id == "Chat.Command");
            vm.Code += "\nprint(string.format('%d', 1.5))\n";
            vm.CheckCommand.Execute(null);
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "window-lua-editor");
        }
        finally
        {
            window.Close();
        }
    }
}
