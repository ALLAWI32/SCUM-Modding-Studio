using Avalonia.Headless.XUnit;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Abstractions;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Textures;
using ScumStudio.Pak;
using ScumStudio.Tests.Formats;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// m7ammed: "could you add tools for editing materials and importing textures?". A picture replaces a texture of a Rager
/// clone's body paint: the export carries a new cooked texture under the clone's folder in the stock texture's format,
/// size and mip chain (read back and decoded by CUE4Parse, the game's reader), the clone's material points at it, and the
/// stock texture and the stock material are not in the mod. Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class TextureReplaceRealTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public async Task ARagerCloneWearsAPictureAsItsOwnTextureAndTheStockTextureStays()
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Rager texture");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));

            // A stock vehicle is refused: its textures are the game's.
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints, TimeSpan.FromSeconds(30)));
            var stockOuter = vehicles.Paints.Single(p => p.MaterialPath.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));
            Assert.False(await vehicles.ReplaceTextureAsync(stockOuter.MaterialPath, "Diffuse Map", Picture(64, 64)));
            Assert.Empty(ctx.Services.Projects.Current!.State.AssetValueOverrides);

            vehicles.CloneName = "RagerTex";
            await vehicles.CreateCloneAsync();
            Assert.Equal("BPC_RagerTex", vehicles.SelectedItem!.Name);
            var clone = ctx.Services.Projects.Current!.State.FindCloneOf(vehicles.SelectedItem.PackagePath)!;
            var outerCopy = clone.Packages.Single(p => p.Old.EndsWith("/MI_Rager_Outer", StringComparison.Ordinal)).New;
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints, TimeSpan.FromSeconds(30)));
            var outer = vehicles.Paints.Single(p => p.MaterialPath.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));

            var catalog = ctx.Services.Workspace.Catalog!;
            var slots = ModulePageViewModel.ReadTextureSlots(catalog, outer.MaterialPath);
            foreach (var s in slots)
            {
                output.WriteLine($"{s.Parameter}: {s.TexturePath} {s.Width}x{s.Height} {s.PixelFormat} sRGB={s.IsSrgb}");
            }

            // The body's colour picture (an sRGB slot; "Diffuse Map" on M_Car_01's children).
            var slot = slots.FirstOrDefault(s => s.Parameter == "Diffuse Map") ?? slots.First(s => s.IsSrgb);
            var stockTexture = slot.TexturePath[..slot.TexturePath.LastIndexOf('.')];
            var stockPackage = ModdableAssetsRead(catalog, stockTexture);
            var stock = CookedTexture2D.Read(stockPackage, CookedTexture2D.FindExport(stockPackage)).Platforms[0];

            // A 300 x 200 picture (not the texture's size): left half red, right half blue.
            Assert.True(await vehicles.ReplaceTextureAsync(outer.MaterialPath, slot.Parameter, Picture(300, 200)));
            var pixels = Assert.Single(ctx.Services.Projects.Current!.State.AssetValueOverrides, o => TextureWrites.IsEdit(o.Path));
            var newTexture = pixels.Package;
            output.WriteLine($"{slot.Parameter} -> {newTexture}");
            Assert.StartsWith(clone.NewPrimary[..clone.NewPrimary.LastIndexOf('/')], newTexture, StringComparison.OrdinalIgnoreCase); // the clone's folder
            Assert.False(TextureWrites.EndsInNumber(newTexture));

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, catalog,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            Assert.DoesNotContain(result.Warnings, w => w.Contains("texture", StringComparison.OrdinalIgnoreCase));
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);

            // The game's own texture and material are not in the mod.
            Assert.False(written.PackageExists(stockTexture));
            Assert.False(written.PackageExists(outer.MaterialPath[..outer.MaterialPath.LastIndexOf('.')]));

            // The new texture: CUE4Parse reads it in the stock texture's format, size and mip count, with valid offsets.
            var leaf = newTexture[(newTexture.LastIndexOf('/') + 1)..];
            var texture = written.LoadObject<UTexture2D>(newTexture + "." + leaf);
            Assert.Equal(stock.PixelFormat, texture.Format.ToString());
            Assert.Equal((stock.SizeX, stock.SizeY, stock.Mips.Count), (texture.PlatformData.SizeX, texture.PlatformData.SizeY, texture.PlatformData.Mips.Length));
            Assert.Equal(slot.IsSrgb, texture.SRGB);
            var cooked = ModdableAssetsRead(written, newTexture);
            Assert.True(CookedTexture2DTests.OffsetsAreAbsolute(cooked));
            var image = TextureDecoder.Decode(texture);
            Assert.Equal((stock.SizeX, stock.SizeY), (image.Width, image.Height));
            var (left, right) = (Mean(image, 0.05, 0.40), Mean(image, 0.60, 0.95));
            output.WriteLine($"decoded {image.Width}x{image.Height}: left {left}, right {right}");
            Assert.True(left.R > 200 && left.B < 50, $"left half {left}");
            Assert.True(right.B > 200 && right.R < 50, $"right half {right}");

            // The clone's material wears it; every other texture slot stays the game's.
            var info = new MaterialInspector(written).Inspect(outerCopy + "." + outerCopy[(outerCopy.LastIndexOf('/') + 1)..]);
            Assert.Equal(newTexture + "." + leaf, info.Textures.Single(t => t.Name == slot.Parameter).TexturePath, ignoreCase: true);
            foreach (var other in slots.Where(s => s.Parameter != slot.Parameter))
            {
                // (inherited slots are not listed: the parent material is not in the mod)
                if (info.Textures.FirstOrDefault(t => t.Name == other.Parameter) is { } kept)
                {
                    Assert.Equal(other.TexturePath, kept.TexturePath, ignoreCase: true);
                }
            }

            // Ctrl+Z takes the whole replace back in one step.
            ctx.Services.Projects.Undo();
            Assert.DoesNotContain(ctx.Services.Projects.Current!.State.AssetValueOverrides, o => o.Package == newTexture || (o.Package == outerCopy && o.Path.Contains(slot.Parameter, StringComparison.Ordinal)));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>
    /// Owner: "tattoos and stickers like drift cars". A decal clicked onto a Rager clone's roof and applied ends up in the
    /// clone's own colour texture and colour mask (one journal step), which the export writes as cooked textures the game's
    /// reader decodes, changed where the decal is; the stock textures stay out of the mod.
    /// </summary>
    [AvaloniaFact]
    public async Task ADecalClickedOnARagerCloneIsBakedIntoItsOwnTextures()
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Rager decals");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "RagerInk";
            await vehicles.CreateCloneAsync();
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints && vehicles.Preview is not null, TimeSpan.FromSeconds(30)));
            var catalog = ctx.Services.Workspace.Catalog!;

            // The biggest part wearing the outer body paint; a ray straight down onto the middle of it (its roof or bonnet).
            var model = vehicles.Preview!;
            var body = Enumerable.Range(0, model.Parts.Count)
                .Where(i => model.Parts[i].Material.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal))
                .MaxBy(i => model.Parts[i].Mesh.Positions.Length);
            var part = model.Parts[body];
            var bounds = part.Mesh.Bounds;
            output.WriteLine($"body part {body}: {part.Name} {part.Attachment} cutoff {part.AlphaCutoff} tint {part.Tint} bounds {bounds.Min}..{bounds.Max} at {part.Transform.Translation}");
            // A ray straight down onto the middle of its highest upward-facing triangle (a part splits per material: these
            // are only the outer-paint triangles of the chassis mesh).
            var (p, idx) = (part.Mesh.Positions, part.Mesh.Indices);
            System.Numerics.Vector3 V(uint i) => new(p[i * 3], p[(i * 3) + 1], p[(i * 3) + 2]);
            var top = Enumerable.Range(0, idx.Length / 3)
                .Select(t => (A: V(idx[t * 3]), B: V(idx[(t * 3) + 1]), C: V(idx[(t * 3) + 2])))
                .Where(t => System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(t.B - t.A, t.C - t.A)) is { } n && Math.Abs(n.Z) > 0.9f
                    && System.Numerics.Vector3.Cross(t.B - t.A, t.C - t.A).Length() > 0.5f)
                .Select(t => (t.A + t.B + t.C) / 3f)
                .MaxBy(c => c.Z);
            var aim = part.Transform.TransformPosition(new ScumStudio.Core.Mathematics.FVector(top.X, top.Y, top.Z));
            output.WriteLine($"{idx.Length / 3} triangles in {part.Mesh.Sections.Count()} sections; aiming at {aim}");
            var ray = new ScumStudio.Viewport.Decals.SurfaceRay(body, new System.Numerics.Vector3(aim.X, aim.Y, aim.Z + 300), -System.Numerics.Vector3.UnitZ);
            Assert.NotNull(ScumStudio.Viewport.Decals.MeshRay.Hit(model, ray));
            vehicles.StudioTab = StudioTab.Decals;
            vehicles.ArmDecalCommand.Execute(vehicles.DecalDesigns.First(d => d.Design.Category == "Flames"));
            vehicles.OnStudioSurface(body, ray.Origin, ray.Direction, false, false, false);
            vehicles.OnStudioSurface(body, ray.Origin, ray.Direction, false, true, false);
            var layer = Assert.Single(vehicles.Decals.Layers);
            output.WriteLine($"decal on {layer.PartKey} ({layer.MaterialPath}) at {layer.Position}");

            await vehicles.ApplyDecalsCommand.ExecuteAsync(null);
            var pixels = ctx.Services.Projects.Current!.State.AssetValueOverrides.Where(o => TextureWrites.IsEdit(o.Path)).Select(o => o.Package).ToList();
            output.WriteLine("written: " + string.Join(", ", pixels));
            Assert.Equal(2, pixels.Count); // the colour texture and the colour mask
            ctx.Services.Projects.Undo(); // one step
            Assert.DoesNotContain(ctx.Services.Projects.Current!.State.AssetValueOverrides, o => TextureWrites.IsEdit(o.Path));
            ctx.Services.Projects.Redo();

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, catalog,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            Assert.DoesNotContain(result.Warnings, w => w.Contains("texture", StringComparison.OrdinalIgnoreCase));
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var slots = ModulePageViewModel.ReadTextureSlots(catalog, layer.MaterialPath);
            foreach (var package in pixels)
            {
                var leaf = package[(package.LastIndexOf('/') + 1)..];
                var texture = written.LoadObject<UTexture2D>(package + "." + leaf);
                Assert.True(CookedTexture2DTests.OffsetsAreAbsolute(ModdableAssetsRead(written, package)));
                var parameter = leaf.EndsWith("_CM", StringComparison.Ordinal) ? "Color Mask" : "Diffuse Map";
                var stockPath = slots.Single(s => s.Parameter == parameter).TexturePath;
                Assert.False(written.PackageExists(stockPath[..stockPath.LastIndexOf('.')]));
                var stock = TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(stockPath));
                var image = TextureDecoder.Decode(texture);
                Assert.Equal((stock.Width, stock.Height), (image.Width, image.Height));
                var changed = Enumerable.Range(0, image.Width * image.Height).Count(i => Math.Abs(image.Rgba[i * 4] - stock.Rgba[i * 4]) + Math.Abs(image.Rgba[(i * 4) + 1] - stock.Rgba[(i * 4) + 1]) + Math.Abs(image.Rgba[(i * 4) + 2] - stock.Rgba[(i * 4) + 2]) > 60);
                output.WriteLine($"{parameter}: {package} {image.Width}x{image.Height} {texture.Format}, {changed} texels changed by the decal");
                Assert.InRange(changed, 50, image.Width * image.Height / 4); // the decal is there, the rest is the game's
            }
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private static CookedPackage ModdableAssetsRead(AssetCatalog catalog, string packagePath) =>
        ScumStudio.Modding.Catalog.ModdableAssets.ReadPackage(catalog, packagePath);

    private static TextureImage Picture(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var red = x < width / 2;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (red ? (byte)230 : (byte)10, (byte)20, red ? (byte)10 : (byte)230, (byte)255);
            }
        }

        return new TextureImage("left red, right blue", width, height, rgba, "PF_R8G8B8A8", 0, width, height, true, false);
    }

    /// <summary>Mean colour of the columns between <paramref name="from"/> and <paramref name="to"/> (fractions of the width).</summary>
    private static (int R, int G, int B) Mean(TextureImage image, double from, double to)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = (int)(image.Width * from); x < (int)(image.Width * to); x++)
            {
                var i = ((y * image.Width) + x) * 4;
                (r, g, b, n) = (r + image.Rgba[i], g + image.Rgba[i + 1], b + image.Rgba[i + 2], n + 1);
            }
        }

        return ((int)(r / n), (int)(g / n), (int)(b / n));
    }
}
