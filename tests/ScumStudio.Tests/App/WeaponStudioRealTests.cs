using Avalonia.Headless.XUnit;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// The owner's Weapons studio round (r37) on the real game files (<c>SCUM_PAKS</c>, key from this PC's store): the page
/// opens on the AKM, the gun is drawn opaque with its own metal and its magazines laid out beside it, paint blends with
/// its texture at a gentle strength, and a clone shows its own spawn command and in-game name. With <c>WEAPON_SHOTS</c>
/// set each look is also rendered to a PNG.
/// </summary>
public sealed class WeaponStudioRealTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public async Task TheAkmIsDrawnAsTheGameHasIt()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "AKM look");
            var weapons = (WeaponsPageViewModel)vm.NavigateTo("weapons")!;
            await weapons.LoadCompletion;

            // 1. Opens on the AKM (3.4.0: 1H_Beam_Dildo), firearms before every melee weapon.
            Assert.Equal("Weapon_AKM", weapons.SelectedItem?.Name);
            Assert.Equal(["Weapon_AKM", "Weapon_AK47"], weapons.Items.Take(2).Select(i => i.Name));
            var guns = weapons.AllItems.Where(i => i.Asset.Kind == ModdableKind.Weapon).ToList();
            Assert.True(guns.FindLastIndex(i => i.Asset.Category == "Ranged_Weapons") < guns.FindIndex(i => i.Asset.Category == ModdableAssets.MeleeCategory));

            await weapons.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => weapons.HasPaints, TimeSpan.FromSeconds(30)), "no paint");
            var model = weapons.Preview!;
            foreach (var part in model.Parts)
            {
                output.WriteLine($"{part.Name} | tex {part.TexturePath} | tint {part.Tint} | clip {part.AlphaCutoff} | r {part.Roughness} | s {part.Surface}");
            }

            // 3. Opaque (3.4.0 cut the gun out by its roughness: the AK magazine came out ghostly), its own metal and roughness.
            Assert.All(model.Parts, p => Assert.Equal(0f, p.AlphaCutoff));
            var receiver = model.Parts.Single(p => p.Material.EndsWith(".MI_AKM_Receiver", StringComparison.Ordinal));
            Assert.StartsWith("#look/", receiver.TexturePath, StringComparison.Ordinal);
            Assert.Equal(0.75f, receiver.Surface.X, 3);
            Assert.True(receiver.Roughness.Y > 0.15f);
            var alpha = model.Textures[receiver.TexturePath!].Rgba.Where((_, i) => i % 4 == 3).ToList();
            Assert.Contains(alpha, a => a > 200); // metal
            Assert.Contains(alpha, a => a < 50);  // not

            // 6. The magazines the AKM mesh carries (its AK magazine and the RPK drum) lie under it, each its own paint row.
            Assert.True(weapons.HasWeaponMagazine);
            var magazines = model.Parts.Where(p => ModulePageViewModel.IsMagazineMaterial(p.Material)).ToList();
            Assert.Equal(2, magazines.Count);
            var (gunMin, _) = ModulePageViewModel.UeBounds(model.Parts.Except(magazines));
            Assert.All(magazines, m => Assert.True(ModulePageViewModel.UeBounds([m]).Max.Z < gunMin.Z, m.Name + " is not under the gun"));
            var magazineRows = weapons.Paints.Where(p => p.PartLabel == ScumStudio.App.Localization.Loc.T("Weapons.Part.Magazine")).ToList();
            Assert.Equal(2, magazineRows.Count);
            Assert.Equal(magazineRows, weapons.Paints.TakeLast(2)); // the gun first, the magazines last
            output.WriteLine(string.Join(" | ", weapons.Paints.Select(p => p.Title)));
            Assert.Equal(ScumStudio.App.Localization.Loc.T("Weapons.Part.Body"), weapons.Paints.Single(p => p.MaterialPath.EndsWith(".MI_AKM_Receiver", StringComparison.Ordinal)).PartLabel);
            Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, WeaponShots.Folder);
            weapons.ShowPreviewTab = true;
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "page-akm-3d");
            await WeaponShots.SaveAsync(model, "akm-stock");
            await WeaponShots.SaveAsync(model, "akm-stock-side", yaw: -90f, pitch: -5f);
            weapons.ShowWeaponMagazine = false;
            Assert.DoesNotContain(weapons.Preview!.Parts, p => magazines.Any(m => m.Material == p.Material));
            await WeaponShots.SaveAsync(weapons.Preview!, "akm-no-magazines-side", yaw: -90f, pitch: -5f);
            weapons.ShowWeaponMagazine = true;

            // 4. Gold over the gun's own print at the default strength: a multiply the game does (Difuse_Colorization),
            //    the texture, metal mask and roughness kept.
            var body = weapons.Paints.Single(p => p.MaterialPath.EndsWith(".MI_AKM_Receiver", StringComparison.Ordinal));
            Assert.False(body.Plain);
            Assert.Equal(PaintMaterialViewModel.DefaultStrength, body.Strength, 3);
            body.ApplyFinishCommand.Execute(PaintFinish.All.Single(f => f.Key == "Gold"));
            var gold = weapons.Preview!.Parts.Single(p => p.Material == body.MaterialPath);
            Assert.StartsWith("#look/", gold.TexturePath, StringComparison.Ordinal);
            Assert.Equal(body.LinearColour, gold.Tint);
            Assert.Equal(receiver.Roughness, gold.Roughness);
            Assert.InRange(body.LinearColour.X, 1f, 3.01f);
            Assert.True(body.LinearColour.Z < 0.5f, "gold leaves little blue");
            await WeaponShots.SaveAsync(weapons.Preview!, "akm-gold");
            await WeaponShots.SaveAsync(weapons.Preview!, "akm-gold-side", yaw: -90f, pitch: -5f);
            body.Strength = 1;
            await WeaponShots.SaveAsync(weapons.Preview!, "akm-gold-full-side", yaw: -90f, pitch: -5f);
            body.Strength = 0.35;
            await WeaponShots.SaveAsync(weapons.Preview!, "akm-gold-light-side", yaw: -90f, pitch: -5f);
            var stored = body.LinearColour;
            weapons.ApplyPaintCommand.Execute(null);
            Assert.Equal(0.35, body.Strength, 3); // the slider keeps its place after Apply

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var info = new ScumStudio.Assets.Materials.MaterialInspector(written).Inspect(body.MaterialPath);
            var tint = info.Vectors.Single(v => v.Name == "Difuse_Colorization").Value;
            Assert.Equal(stored.X, tint.X, 3);
            Assert.Equal(stored.Z, tint.Z, 3);
            Assert.DoesNotContain("FlatWhite", info.Textures.Single(t => t.Name == "Color").TexturePath, StringComparison.Ordinal); // the print stays

            // 5. A clone: its spawn command before it exists, then its own name in game.
            Assert.True(await weapons.SelectAsync("Weapon_AKM"));
            weapons.CloneName = "AKM_Gold";
            Assert.Equal("#SpawnItem AKM_Gold", weapons.CloneSpawnText);
            await weapons.CreateCloneAsync();
            Assert.Equal("AKM_Gold", weapons.SelectedItem?.Name);
            Assert.Equal("#SpawnItem AKM_Gold", weapons.SpawnCommandText);
            Assert.Contains("Weapon_AKM", weapons.CloneOfText, StringComparison.Ordinal);
            var order = weapons.AllItems.Select(i => i.Name).ToList();
            Assert.Equal(order.IndexOf("Weapon_AKM") + 1, order.IndexOf("AKM_Gold")); // right after its gun
            weapons.CaptionEdit = "Golden AKM";
            await weapons.ApplyCaptionCommand.ExecuteAsync(null);
            Assert.Equal("Golden AKM", weapons.Caption);
            weapons.ShowPreviewTab = false;
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "page-clone-values");
        }
        finally
        {
            Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, null);
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>The same look on other guns: opaque, their own metal, their magazines (when their mesh carries some) under them.</summary>
    [AvaloniaFact]
    public async Task OtherGunsAreDrawnTheSameWay()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var weapons = (WeaponsPageViewModel)vm.NavigateTo("weapons")!;
            await weapons.LoadCompletion;
            var whole = ScumStudio.App.Localization.Loc.T("Weapons.Part.Whole");
            foreach (var name in new[] { "Weapon_M16A4", "Weapon_MP5", "Weapon_RPK-74", "Weapon_M1911", "Weapon_SVD_Dragunov", "Weapon_M1887", "1H_KitchenKnife" })
            {
                Assert.True(await weapons.SelectAsync(name), name);
                await weapons.PreviewCompletion;
                Assert.True(HeadlessUi.PumpUntil(() => weapons.HasPaints, TimeSpan.FromSeconds(30)), name + " has no paint");
                var model = weapons.Preview!;
                output.WriteLine($"{name}: {string.Join(" | ", weapons.Paints.Select(p => p.Title))}; magazines apart: {weapons.HasWeaponMagazine}");
                Assert.All(model.Parts.Where(p => p.TexturePath?.StartsWith("#look/", StringComparison.Ordinal) == true), p => Assert.Equal(0f, p.AlphaCutoff));
                if (name is "Weapon_SVD_Dragunov" or "1H_KitchenKnife")
                {
                    Assert.Equal(whole, weapons.Paints[0].PartLabel); // its only material besides the magazine
                }
                if (name.StartsWith("Weapon_", StringComparison.Ordinal))
                {
                    Assert.Contains(model.Parts, p => p.TexturePath?.StartsWith("#look/", StringComparison.Ordinal) == true);
                }

                await WeaponShots.SaveAsync(model, name + "-side", yaw: -90f, pitch: -5f);
            }
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
