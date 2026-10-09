using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Spawns page on the real game (<c>SCUM_PAKS</c>, the key from this PC's store) and, with <c>SCUM_SERVER_PAKS</c>, a
/// copy of the server's settings; pictures go to <c>SCUMSTUDIO_SCREENSHOTS</c>. Skipped without them.
/// </summary>
public sealed class SpawnsRealTests
{
    [AvaloniaFact]
    public async Task VehiclesZonesAndServerSettingsShowAsSliders()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        var serverPaks = CopyServer(ctx);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks, ServerPaksFolder = serverPaks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            HeadlessUi.Pump();
            var spawns = (SpawnsPageViewModel)vm.NavigateTo("spawns")!;
            HeadlessUi.Pump();

            // A world-spawn Rager: fuel 0..10 %, battery, parts, and the server's limits for Ragers (the list loads in the
            // background: under a full test run it is not there right away).
            Assert.True(HeadlessUi.PumpUntil(() => spawns.Entries.Any(e => e.PackagePath.EndsWith("/AutomaticSpawn/RagerSpawnPreset", StringComparison.Ordinal)), TimeSpan.FromSeconds(60)));
            spawns.SelectedEntry = spawns.Entries.First(e => e.PackagePath.EndsWith("/AutomaticSpawn/RagerSpawnPreset", StringComparison.Ordinal));
            Assert.True(HeadlessUi.PumpUntil(() => spawns.DetailCards.Count > 0, TimeSpan.FromSeconds(30)));
            var fuel = spawns.DetailCards.SelectMany(c => c.Rows).OfType<SpawnRangeViewModel>().First();
            Assert.Equal(0, fuel.From.Value);
            Assert.Equal(10, fuel.To.Value);
            if (serverPaks is not null)
            {
                Assert.Contains(spawns.DetailCards.SelectMany(c => c.Rows).OfType<ServerSettingViewModel>(), r => r.Key == "scum.RagerMaxAmount");
            }

            fuel.SetBothCommand.Execute("1"); // always a full tank
            Assert.Equal(2, spawns.PendingCount);
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "spawns-vehicles");
            spawns.DiscardCommand.Execute(null);
            Assert.Equal(0, spawns.PendingCount);

            spawns.IsZombiesTab = true;
            spawns.SelectedEntry = spawns.Entries.First(e => e.PackagePath.EndsWith("/HTZ_Military_TV_Bunker", StringComparison.Ordinal));
            Assert.True(HeadlessUi.PumpUntil(() => spawns.DetailCards.Count >= 2, TimeSpan.FromSeconds(30)));
            HeadlessUi.SaveScreenshot(window, "spawns-zombies");

            spawns.IsServerTab = true;
            HeadlessUi.Pump();
            if (serverPaks is not null)
            {
                Assert.NotEmpty(spawns.ServerCards);
                var drones = spawns.ServerCards.SelectMany(c => c.Rows).OfType<ServerSettingViewModel>().First(r => r.Key == "scum.MaxAllowedDrones");
                drones.Value = 3;
                spawns.ApplyCommand.Execute(null);
                var ini = File.ReadAllText(ServerSettingsFile.PathFor(serverPaks)!);
                Assert.Contains("scum.MaxAllowedDrones=3", ini, StringComparison.Ordinal);
                Assert.True(File.Exists(ServerSettingsFile.PathFor(serverPaks) + ".bak"));
            }

            HeadlessUi.SaveScreenshot(window, "spawns-server");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>
    /// igor8802 (Discord): "It would be possible to assign NPCs instead of zombies." Most threat zones already choose
    /// between a zombie encounter and an armed-NPC encounter by weight (Village 75 / 25): the sliders must say which is
    /// which, so a zombie weight of 0 spawns NPCs instead (they read "Group 1", "Group 2").
    /// </summary>
    [Fact]
    public async Task ZoneGroupsSayWhetherTheySpawnZombiesOrArmedNpcs()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var spawns = new SpawnsPageViewModel(ctx.Services);
        spawns.IsZombiesTab = true;
        spawns.SelectedEntry = spawns.Entries.First(e => e.PackagePath.EndsWith("/MTZ_Settlement_Village", StringComparison.Ordinal));
        for (var i = 0; i < 300 && spawns.DetailCards.Count == 0; i++)
        {
            await Task.Delay(100);
        }

        var groups = spawns.DetailCards.Single(c => c.Title == Loc.T("Spawns.Card.What")).Rows.Cast<SpawnSliderViewModel>().ToList();
        Assert.Equal([Loc.T("Spawns.Encounter.Zombies"), Loc.T("Spawns.Encounter.Npcs")], groups.Select(g => g.Label));
        Assert.Equal([75d, 25d], groups.Select(g => g.Value));
        Assert.EndsWith("MTZ_Settlement_Village_NPC_Encounter_C", groups[1].Tip, StringComparison.Ordinal);
    }

    // The server's settings, copied into the test folder (never the real server's file) next to an empty Paks folder
    // holding a copy of its first stock pak name, so the app takes it for a server cook.
    private static string? CopyServer(AppTestContext ctx)
    {
        if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is not { Length: > 0 } real || ServerSettingsFile.PathFor(real) is not { } ini || !File.Exists(ini))
        {
            return null;
        }

        var paks = Directory.CreateDirectory(ctx.Combine("server", "SCUM", "Content", "Paks")).FullName;
        var stock = Directory.EnumerateFiles(real, "pakchunk0*-WindowsServer.pak").First();
        File.WriteAllBytes(Path.Combine(paks, Path.GetFileName(stock)), []);
        var config = Directory.CreateDirectory(ctx.Combine("server", "SCUM", "Saved", "Config", "WindowsServer")).FullName;
        File.Copy(ini, Path.Combine(config, ServerSettingsFile.FileName));
        return paks;
    }
}
