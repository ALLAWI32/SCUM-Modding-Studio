using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Textures;
using ScumStudio.Pak;
using ScumStudio.Viewport.Decals;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-19): "the external stickers, the PNG files, sometimes do not work when we put them". On the real game
/// files (<c>SCUM_PAKS</c>): a Rager clone gets one sticker of every kind of picture (<see cref="StickerPicturesTests.Kinds"/>)
/// on its body, Apply decals and the export write its colour texture in the game's own format, size and mips
/// (<c>T_Rager_Outer_D</c>: 2048 x 2048, DXT1, 12 mips), read back by the game's reader (CUE4Parse) and decoded, with the
/// stickers in it. A decal on a part whose material the clone does not copy (the glass) says at once and on Apply decals
/// that it shows in the studio only. What only the game shows: the stickers on the car.
/// </summary>
public sealed class StickerPicturesRealTests(ITestOutputHelper output)
{
    [AvaloniaPaksFact]
    public async Task ARagerCloneWithAStickerOfEveryKindExportsAValidColourTexture()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder! });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Stickers");
            var game = ctx.Services.Workspace.Catalog!;
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "RagerStickers";
            await vehicles.CreateCloneAsync();
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints && vehicles.Preview is not null && vehicles.Decals.CloneLeaf == "BPC_RagerStickers", TimeSpan.FromSeconds(90)));
            vehicles.StudioTab = StudioTab.Decals;
            var model = vehicles.Preview!;
            var clone = project.State.FindCloneOf(vehicles.SelectedItem!.PackagePath)!;
            var copied = clone.Packages.Select(p => p.Old).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Where the rays from the side meet the body (MI_Rager_Outer), and a part the clone does not copy.
            var side = Enumerable.Range(0, 9).SelectMany(zi => Enumerable.Range(0, 49).Select(xi => new SurfaceRay(-1, new Vector3(-300 + (xi * 12.5f), 800, 40 + (zi * 20)), -Vector3.UnitY)))
                .Concat(Enumerable.Range(0, 25).SelectMany(yi => Enumerable.Range(0, 25).Select(xi => new SurfaceRay(-1, new Vector3(-300 + (xi * 25), -300 + (yi * 25), 800), -Vector3.UnitZ))))
                .Select(r => (Ray: r, Hit: MeshRay.Hit(model, r)))
                .Where(h => h.Hit is not null)
                .Select(h => (h.Ray, Material: model.Parts[h.Hit!.Value.PartIndex].Material))
                .ToList();
            var body = side.Where(h => h.Material.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal)).Select(h => h.Ray).ToList();
            var kinds = StickerPicturesTests.Kinds().Select(k => (string)k[0]).ToList();
            Assert.True(body.Count >= kinds.Count, $"{body.Count} rays meet the body");

            var folder = ctx.Combine("pictures");
            for (var i = 0; i < kinds.Count; i++)
            {
                var picture = StickerPicturesTests.Make(folder, kinds[i]);
                Assert.True(vehicles.ImportSticker(picture), kinds[i] + ": " + string.Join(" | ", ctx.Services.Notifications.Toasts.Select(t => t.Message)));
                Assert.True(vehicles.PlaceDecal(body[i * body.Count / kinds.Count]), kinds[i]);
            }

            Assert.Equal(kinds.Count, vehicles.Decals.Layers.Count);
            Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.IsError || t.Message.Contains("Apply decals cannot write", StringComparison.Ordinal));

            // A decal on a solid part whose material the clone does not copy (glass takes no decal at all): said when it is put there.
            var foreign = model.Parts.Select((p, i) => (Part: p, Index: i))
                .Where(x => x.Part.AlphaCutoff <= 0f && x.Part.Tint is not { W: < 1f } && x.Part.Material.Contains('.')
                            && !copied.Contains(x.Part.Material[..x.Part.Material.LastIndexOf('.')]))
                .ToList();
            output.WriteLine("parts whose material is not copied: " + string.Join(", ", foreign.Select(f => f.Part.Material).Distinct()));
            var onForeign = foreign.SelectMany(f =>
                {
                    var centre = f.Part.Transform.TransformPosition(ScumStudio.Core.Mathematics.FVector.FromVector3((f.Part.Mesh.Bounds.Min + f.Part.Mesh.Bounds.Max) / 2)).ToVector3();
                    return new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX }
                        .Select(d => new SurfaceRay(f.Index, centre + (d * 1000), -d));
                })
                .FirstOrDefault(r => MeshRay.Hit(model, r) is not null);
            if (onForeign is not null)
            {
                vehicles.ArmDecalCommand.Execute(vehicles.DecalDesigns[0]);
                Assert.True(vehicles.PlaceDecal(onForeign));
                Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Notifications.Toasts.Any(t => t.IsWarning && t.Message.Contains("cannot write this part's material", StringComparison.Ordinal)), TimeSpan.FromSeconds(10)));
            }

            await vehicles.ApplyDecalsCommand.ExecuteAsync(null);
            var pixels = project.State.AssetValueOverrides.Where(o => TextureWrites.IsEdit(o.Path)).ToList();
            var diffuse = Assert.Single(pixels, o => TextureWrites.Parse(o.Current)?.Template.EndsWith(".T_Rager_Outer_D", StringComparison.Ordinal) == true);
            output.WriteLine("written: " + string.Join(", ", pixels.Select(p => p.Package)));

            var result = await new ProjectExporter().ExportAsync(project, game, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            Assert.DoesNotContain(result.Warnings, w => w.Contains("texture not written", StringComparison.Ordinal));
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var leaf = diffuse.Package[(diffuse.Package.LastIndexOf('/') + 1)..];
            var texture = written.LoadObject<UTexture2D>(diffuse.Package + "." + leaf);
            Assert.Equal(("PF_DXT1", 2048, 2048, 12), (texture.Format.ToString(), texture.PlatformData.SizeX, texture.PlatformData.SizeY, texture.PlatformData.Mips.Length));
            var image = TextureDecoder.Decode(texture);
            Assert.Equal((2048, 2048), (image.Width, image.Height));

            // The stickers are in it: the clone's colour texture differs from the game's where they sit.
            var stock = TextureDecoder.Decode(game.LoadObject<UTexture2D>("/Game/ConZ_Files/Models/Vehicles2/Pickup/Textures/T_Rager_Outer_D.T_Rager_Outer_D"));
            var changed = 0;
            for (var i = 0; i < image.Rgba.Length; i += 4)
            {
                if (Math.Abs(image.Rgba[i] - stock.Rgba[i]) + Math.Abs(image.Rgba[i + 1] - stock.Rgba[i + 1]) + Math.Abs(image.Rgba[i + 2] - stock.Rgba[i + 2]) > 60)
                {
                    changed++;
                }
            }

            output.WriteLine($"{changed} of {image.Width * image.Height} texels changed");
            Assert.True(changed > 2000, $"{changed} texels changed");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
