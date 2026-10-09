using Avalonia.Headless.XUnit;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-09): "more control over vehicles: attachments and parts". The Vehicles page's "Default parts" entries
/// (one per spawn preset) and the Weapons page's "Attachments" entry: rows journaled like values (undo/redo), shown on
/// the car in the 3D view, exported where the game reads them. Real game files only (<c>SCUM_PAKS</c>, key from this
/// PC's store); the game's reader (CUE4Parse) reads the export. Screenshots with <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
public sealed class PartsPageRealTests
{
    [AvaloniaFact]
    public async Task ACloneSpawnsWithArmourAndWithoutItsRadio()
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Armoured Wolf");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_WolfsWagen"));
            Assert.Contains(vehicles.Parts, p => p.IsVehicleParts && p.PackagePath.EndsWith("/AutomaticSpawn/WolfsWagenSpawnPreset", StringComparison.Ordinal));
            vehicles.CloneName = "WolfArmour";
            await vehicles.CreateCloneAsync();

            var parts = Assert.Single(vehicles.Parts, p => p.IsVehicleParts && p.PackagePath.EndsWith("/WolfArmourManualSpawnPreset", StringComparison.Ordinal));
            Assert.Equal("Default parts: ManualSpawnPreset", parts.Label);
            vehicles.SelectedPart = parts;
            await vehicles.ValuesCompletion;
            var rows = vehicles.Groups.SelectMany(g => g.Rows).ToList();
            Assert.True(rows.Count > 30, rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)); // every slot, not only "key" values
            var radio = Assert.Single(rows, r => r.Value.EndsWith("/BPC_WolfArmour_Radio.BPC_WolfArmour_Radio_C", StringComparison.Ordinal));
            var armour = Assert.Single(rows, r => r.Tunable.Group == "Door FrontLeft");
            Assert.True(armour.IsEnum && armour.IsPart);
            Assert.Equal("No part", armour.StockDisplay);
            var heavy = Assert.Single(armour.Choices, c => c.EndsWith("BPC_WolfArmour_Door_ArmorHeavy_FrontLeft_C", StringComparison.Ordinal));
            Assert.Equal("Door ArmorHeavy FrontLeft", TunableRowViewModel.ChoiceLabel.Convert(heavy, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));

            radio.Value = string.Empty;
            armour.Value = heavy;
            Assert.Null(armour.Error);
            vehicles.ApplyChangesCommand.Execute(null);
            var state = ctx.Services.Projects.Current!.State;
            Assert.Equal(heavy, state.GetAssetValue(parts.PackagePath, armour.Tunable.Export, armour.Tunable.Path)!.Current);
            Assert.Equal(string.Empty, state.GetAssetValue(parts.PackagePath, radio.Tunable.Export, radio.Tunable.Path)!.Current);
            Assert.True(armour.IsOverridden);
            HeadlessUi.Pump();
            var showButtons = HeadlessUi.Find<Avalonia.Controls.Button>(window).Where(b => Equals(Avalonia.Controls.ToolTip.GetTip(b), "Show this part highlighted on the vehicle in the 3D view")).ToList();
            Assert.True(showButtons.Count(b => b.IsEffectivelyVisible && b.Bounds.Width > 0) > 5, $"{showButtons.Count} buttons, {showButtons.Count(b => b.IsVisible)} visible, widths {string.Join(",", showButtons.Take(3).Select(b => b.Bounds.Width))}");
            HeadlessUi.SaveScreenshot(window, "module-vehicle-parts");

            // Undo takes the armour off again, redo puts it back.
            ctx.Services.Projects.Undo();
            HeadlessUi.Pump();
            Assert.Equal(string.Empty, armour.CommittedValue);
            ctx.Services.Projects.Redo();
            HeadlessUi.Pump();
            Assert.Equal(heavy, armour.CommittedValue);

            // "Show on the car": the 3D tab with the door highlighted.
            vehicles.ShowPartOnCarCommand.Execute(radio);
            Assert.True(vehicles.ShowPreviewTab);
            Assert.EndsWith("/BPC_WolfArmour_Radio", vehicles.HighlightAttachment, StringComparison.Ordinal);
            vehicles.ShowPartOnCarCommand.Execute(armour);
            Assert.EndsWith("/BPC_WolfArmour_Door_ArmorHeavy_FrontLeft", vehicles.HighlightAttachment, StringComparison.Ordinal);
            vehicles.ShowPartOnCarCommand.Execute(rows.Single(r => r.Value.EndsWith("_Door_FrontLeft_C", StringComparison.Ordinal)));
            await vehicles.PreviewCompletion;
            Assert.Contains(vehicles.Preview!.Parts, p => p.Attachment.Length > 0 && VehicleParts.Label(p.Attachment) == VehicleParts.Label(vehicles.HighlightAttachment)); // a clone is drawn from its template's packages
            HeadlessUi.SaveScreenshot(window, "module-vehicle-parts-3d");

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var preset = written.LoadObject<UObject>(parts.PackagePath + ".WolfArmourManualSpawnPreset");
            var spawned = new List<string>();
            void Walk(UObject node)
            {
                spawned.Add(node.GetOrDefault<FSoftObjectPath>("AttachmentClass").AssetPathName.Text);
                foreach (var child in node.GetOrDefault<FPackageIndex[]>("Children") ?? [])
                {
                    if (!child.IsNull && child.Load<UObject>() is { } c)
                    {
                        Walk(c);
                    }
                }
            }

            Walk(preset.GetOrDefault<FPackageIndex>("RootNode").Load<UObject>()!);
            Assert.Contains(heavy, spawned);
            Assert.DoesNotContain(radio.StockValue, spawned);
            Assert.Contains(spawned, s => s.EndsWith("/BPC_WolfArmour_Dashboard.BPC_WolfArmour_Dashboard_C", StringComparison.Ordinal));
            Assert.Equal("/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfArmour.BPC_WolfArmour_C", preset.GetOrDefault<FSoftObjectPath>("VehicleClass").AssetPathName.Text);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AWeaponTakesAnotherMagazineAndLosesItsBayonet()
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "AK mags");
            var weapons = (WeaponsPageViewModel)vm.NavigateTo("weapons")!;
            await weapons.LoadCompletion;
            Assert.True(await weapons.SelectAsync("Weapon_AK47"));
            var part = Assert.Single(weapons.Parts, p => p.IsWeaponMounts);
            Assert.Equal("Attachments", part.Label);
            weapons.SelectedPart = part;
            await weapons.ValuesCompletion;
            var rows = weapons.Groups.SelectMany(g => g.Rows).ToList();
            var ak15 = Assert.Single(rows, r => r.Tunable.Path.EndsWith("_AK15_762_39_C", StringComparison.Ordinal));
            var bayonet = Assert.Single(rows, r => r.Tunable.Path.EndsWith("/BP_MountTypeWeapon_M70_Bayonet.BP_MountTypeWeapon_M70_Bayonet_C", StringComparison.Ordinal));
            Assert.True(ak15.IsBool && !ak15.BoolValue && bayonet.BoolValue);
            Assert.StartsWith("Magazine AK15 762 39: Magazine_AK15", ak15.Label, StringComparison.Ordinal);
            ak15.BoolValue = true;
            bayonet.BoolValue = false;
            weapons.ApplyChangesCommand.Execute(null);
            Assert.Equal(2, ctx.Services.Projects.Current!.State.GetAssetValues(part.PackagePath).Count(v => v.ValueKind == nameof(TunableKind.Mount)));
            HeadlessUi.SaveScreenshot(window, "module-weapon-attachments");

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var cdo = written.LoadPackage(part.PackagePath).GetExports().Single(e => e.Name == "Default__Weapon_AK47_C");
            var mounts = (cdo.GetOrDefault<FStructFallback[]>(WeaponMounts.Property) ?? [])
                .SelectMany(s => s.GetOrDefault<FStructFallback[]>("Items") ?? [])
                .Select(i => i.GetOrDefault<FPackageIndex>("MountType").Name)
                .ToList();
            Assert.Contains("BP_MountTypeWeaponMagazine_AK15_762_39_C", mounts);
            Assert.Contains("BP_MountTypeWeaponMagazine76239_C", mounts);
            Assert.DoesNotContain("BP_MountTypeWeapon_M70_Bayonet_C", mounts);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
