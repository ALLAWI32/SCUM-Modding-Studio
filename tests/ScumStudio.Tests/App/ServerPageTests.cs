using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views.Pages;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.App;

/// <summary>The Server page on a fake server folder: states, adding a template, editing, switching and removing a mod.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ServerPageTests
{
    internal static ServerInstall FakeServer(AppTestContext ctx, bool withUe4ss)
    {
        var win64 = ctx.Combine("Srv", "SCUM", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        Directory.CreateDirectory(ctx.Combine("Srv", "SCUM", "Content", "Paks"));
        File.WriteAllText(Path.Combine(win64, "SCUMServer.exe"), "exe");
        ctx.Services.UpdateSettings(s => s with { ServerPaksFolder = ctx.Combine("Srv", "SCUM", "Content", "Paks") });
        var server = ServerInstall.Find(win64)!;
        if (withUe4ss)
        {
            var zipPath = ctx.Combine("ue4ss.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var (name, text) in new[] { ("dwmapi.dll", "p"), ("ue4ss/UE4SS.dll", "d"), ("ue4ss/UE4SS-settings.ini", "[General]\r\nbUseUObjectArrayCache = true\r\n") })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(text);
                }
            }

            Ue4ssInstaller.InstallFromZip(server, zipPath);
        }

        return server;
    }

    [AvaloniaFact]
    public void WithoutAServerThePageSaysSoAndOffersNothing()
    {
        using var ctx = AppTestContext.Create(inline: false);

        var page = new ServerPageViewModel(ctx.Services);

        Assert.False(page.HasServer);
        Assert.False(page.CanInstall);
        Assert.False(page.CanEdit);
        Assert.Empty(page.Mods);
    }

    [AvaloniaFact]
    public void AServerWithoutUe4ssOffersTheInstallButOnly()
    {
        using var ctx = AppTestContext.Create(inline: false);
        FakeServer(ctx, withUe4ss: false);

        var page = new ServerPageViewModel(ctx.Services);

        Assert.True(page.HasServer);
        Assert.True(page.CanInstall);
        Assert.False(page.HasUe4ss);
        Assert.False(page.CanEdit);
    }

    [AvaloniaFact]
    public void AModIsAddedEditedSwitchedAndRemoved()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = FakeServer(ctx, withUe4ss: true);
        var page = new ServerPageViewModel(ctx.Services);
        Assert.True(page.CanEdit);

        page.SelectedTemplate = page.Templates.Single(t => t.Id == "Announcer");
        page.AddTemplateCommand.Execute(null);
        page.AddTemplateCommand.Execute(null); // the same template again gets its own name
        Assert.Equal(["Announcer", "Announcer2"], page.Mods.Select(m => m.Name));
        Assert.Equal("Announcer2", page.SelectedMod?.Name);
        Assert.Contains("ANNOUNCER", page.Code, StringComparison.Ordinal);

        page.Code = "-- edited";
        page.SaveCommand.Execute(null);
        Assert.Equal("-- edited", File.ReadAllText(Path.Combine(server.ModsFolder, "Announcer2", "Scripts", "main.lua")));

        page.SelectedMod = page.Mods.First();
        page.ToggleModCommand.Execute(null);
        Assert.False(page.Mods.First().Enabled);
        Assert.Contains("(", page.Mods.First().Display, StringComparison.Ordinal);

        page.SelectedMod = page.Mods.Last();
        page.RemoveCommand.Execute(null);
        Assert.Equal(["Announcer"], page.Mods.Select(m => m.Name));

        page.SwitchUe4ssCommand.Execute(null);
        Assert.False(page.IsActive);
        page.SwitchUe4ssCommand.Execute(null);
        Assert.True(page.IsActive);
    }

    [AvaloniaFact]
    public void ThePageHasItsControls()
    {
        using var ctx = AppTestContext.Create(inline: false);
        FakeServer(ctx, withUe4ss: true);
        var view = new ServerPageView { DataContext = new ServerPageViewModel(ctx.Services) };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        try
        {
            HeadlessUi.Pump();
            foreach (var name in new[] { "InstallButton", "SwitchButton", "ModList", "TemplatePicker", "AddButton", "CodeBox", "SaveButton" })
            {
                Assert.NotNull(HeadlessUi.FindNamed<Control>(view, name));
            }

            // The log is on its own tab, built when the tab is shown.
            ((ServerPageViewModel)view.DataContext!).SelectedTab = 3;
            HeadlessUi.Pump();
            Assert.NotNull(HeadlessUi.FindNamed<Control>(view, "LogBox"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheCloudGuideOpensInItsOwnWindowAndListsTheSettingsTheInstallerWrites()
    {
        var window = new ScumStudio.App.Views.Ue4ssGuideWindow();
        window.Show();
        try
        {
            HeadlessUi.Pump();
            var settings = HeadlessUi.FindNamed<TextBlock>(window, "SettingsBlock")!.Text!;
            Assert.Contains("bForceGUObjectArrayForIteration = true", settings, StringComparison.Ordinal);
            Assert.Contains("EnableAutoReloadingLuaMods = 1", settings, StringComparison.Ordinal);
            Assert.Equal(Ue4ssInstaller.ServerSettings.Count, settings.Split(Environment.NewLine).Length);
            Assert.NotNull(HeadlessUi.FindNamed<Button>(window, "CopyButton"));
            Assert.NotNull(HeadlessUi.FindNamed<Button>(window, "CloseButton"));
            HeadlessUi.SaveScreenshot(window, "ue4ss-guide");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ChangingTheLanguageRefreshesTheTemplateNamesAndTheSwitchLabel()
    {
        using var ctx = AppTestContext.Create(inline: false);
        FakeServer(ctx, withUe4ss: true);
        var page = new ServerPageViewModel(ctx.Services);
        var before = page.Templates[0].Title;
        page.SelectedTemplate = page.Templates.Single(t => t.Id == "EventLog");
        try
        {
            Loc.Instance.Language = "ru";
            page.OnLanguageChanged();

            Assert.NotEqual(before, page.Templates[0].Title);
            Assert.Equal("EventLog", page.SelectedTemplate?.Id);
            Assert.Equal("Выключить UE4SS", page.SwitchLabel);
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
        }
    }
}
