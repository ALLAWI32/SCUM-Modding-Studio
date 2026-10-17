using System.Numerics;
using Avalonia.Headless.XUnit;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Modding.Textures;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-16): "vehicle decals like the game's tattoos". On the real game files (<c>SCUM_PAKS</c>, key from this
/// PC's store; nothing of the game is written into the repository): the game's tattoo list is found and every tattoo
/// decodes; one placed on a Rager clone and baked changes texels only where it lies; "Other side" lands on the mirrored
/// surface; a preset put on a second Rager clone bakes byte for byte the same; an undo gives byte-identical textures back.
/// What only the game can show (how the baked tattoo looks under SCUM's own lighting and paint) is not checked here.
/// </summary>
public sealed class GameTattooRealTests(ITestOutputHelper output)
{
    [PaksFact]
    public async Task TheGameTattooListIsFoundAndEveryTattooDecodes()
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var entries = catalog.LoadFirstExport<UObject>(GameTattoos.DataPath)!.GetOrDefault<FStructFallback[]>("Tattoos") ?? [];
        var tattoos = GameTattoos.Read(catalog);
        var disabled = entries.Count(e => e.GetOrDefault<bool>("IsDisabled"));
        var dlc = entries.Count(e => !e.GetOrDefault<bool>("IsDisabled") && (e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName[]>("DLCVersions") ?? []).Length > 0);
        output.WriteLine($"{entries.Length} entries in the game's list: {disabled} disabled, {dlc} DLC, {tattoos.Count} offered");
        Assert.Equal(entries.Length - disabled - dlc, tattoos.Count);
        Assert.InRange(entries.Length, 60, GameTattoos.CellsPerChannel * 3);
        Assert.InRange(tattoos.Count, 40, entries.Length);
        Assert.Equal(tattoos.Count, tattoos.Select(t => t.Index).Distinct().Count());
        Assert.All(tattoos, t => Assert.False(entries[t.Index].GetOrDefault<bool>("IsDisabled")));

        var atlas = GameTattoos.LoadAtlas(catalog);
        Assert.Equal((2048, 4096), (atlas.Width, atlas.Height));
        foreach (var tattoo in tattoos)
        {
            var cut = GameTattoos.Cut(atlas, tattoo);
            var ink = cut.Planes[0].Count(v => v > 0.5f);
            Assert.True(cut.Width is > 4 and <= 512 && cut.Height is > 4 and <= 512, $"tattoo {tattoo.Index}: {cut.Width} x {cut.Height}");
            Assert.True(ink > 20, $"tattoo {tattoo.Index} has {ink} inked texels");
            Assert.True(ink < cut.Width * cut.Height, $"tattoo {tattoo.Index} is solid: not a coverage mask");
        }

        var picture = GameTattoos.Picture(GameTattoos.Cut(GameTattoos.LoadAtlas(catalog, 1024), tattoos[0]), (226, 226, 226));
        Assert.Contains(picture.Rgba.Where((_, i) => i % 4 == 3), a => a > 200);

        // The library's picture is made once and kept in the thumbnail cache (outside the repository).
        var cache = Path.Combine(Path.GetTempPath(), "ScumStudioTests", Guid.NewGuid().ToString("N"));
        using var thumbnails = new ScumStudio.App.Services.ThumbnailService(cache, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        try
        {
            var made = 0;
            (byte[] Rgba, int Width, int Height)? Make()
            {
                made++;
                return picture;
            }

            var png = await thumbnails.GetDerivedAsync(catalog, GameTattoos.AtlasPath, "tattoo-test", Make);
            Assert.True(png is not null && File.Exists(png));
            Assert.Equal(png, await thumbnails.GetDerivedAsync(catalog, GameTattoos.AtlasPath, "tattoo-test", Make));
            Assert.Equal(1, made);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [AvaloniaPaksFact]
    public async Task AGameTattooOnARagerCloneBakesWhereItLiesMirrorsCopiesAsAPresetAndUndoes()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder! });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Rager tattoos");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "RagerTat";
            await vehicles.CreateCloneAsync();
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints && vehicles.Preview is not null, TimeSpan.FromSeconds(60)));
            var catalog = ctx.Services.Workspace.Catalog!;
            var model = vehicles.Preview!;
            vehicles.StudioTab = StudioTab.Decals;

            // The game's tattoos in the library, a big one of the green channel armed.
            await vehicles.LoadGameTattoosAsync();
            Assert.NotEmpty(vehicles.GameTattooItems);
            var item = vehicles.GameTattooItems.First(i => i.Tattoo!.Index >= GameTattoos.CellsPerChannel);
            vehicles.ArmDecalCommand.Execute(item);
            await vehicles.EnsureGameTattooAsync(item.Tattoo!.Id);

            // A click from the car's right side (+Y), as the user makes it (the outermost surface), onto the middle of a big
            // side-facing triangle of the body paint that is the first thing the click meets.
            var ray = SideAims(model).Select(a => new SurfaceRay(-1, a + new Vector3(0, 400, 0), -Vector3.UnitY))
                .First(r => MeshRay.Hit(model, r) is { } hit && model.Parts[hit.PartIndex].Material.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));
            var body = MeshRay.Hit(model, ray)!.Value.PartIndex;
            output.WriteLine($"clicking {model.Parts[body].Name} ({model.Parts[body].Attachment}) from {ray.Origin}");
            vehicles.OnStudioSurface(-1, ray.Origin, ray.Direction, false, false, false);
            vehicles.OnStudioSurface(-1, ray.Origin, ray.Direction, false, true, false);
            var layer = Assert.Single(vehicles.Decals.Layers);
            Assert.Equal(item.Tattoo.Id, layer.DesignId);
            output.WriteLine($"tattoo {item.Tattoo.Index} on {layer.PartKey} ({layer.MaterialPath}) at {layer.Position}, {layer.WidthCm:0.#} x {layer.HeightCm:0.#} cm");

            var material = layer.MaterialPath;
            var (diffuse, mask) = Stock(catalog, material);
            var parts = model.Parts.Where(p => p.Material == material).ToList();
            async Task<DecalBakeResult> Bake() => Assert.Single(await vehicles.BakeDecalsAsync(m => m == material ? (diffuse, mask) : null)).Result;

            // Changed texels only where it lies: inside what a solid stamp of the same place and size covers.
            var tattooBake = await Bake();
            var changed = Changed(diffuse, mask, tattooBake);
            var solid = new DecalImage(1, 1, [1f, 0f, 1f, 1f]);
            var footprint = Changed(diffuse, mask, DecalBaker.Bake(diffuse, mask, parts, DecalBaker.Stamps(layer, solid, PartOf(model, layer).Transform)));
            output.WriteLine($"{changed.Count} texels changed by the tattoo, {footprint.Count} under its box");
            Assert.True(changed.Count > 200, "the tattoo shows");
            Assert.Empty(changed.Except(footprint));
            Assert.True(changed.Count < footprint.Count, "a tattoo is ink, not a solid box");

            // Other side: the mirrored copy sits on the mirrored point of the car, facing the mirrored way.
            Assert.True(vehicles.MirrorToOtherSide());
            var copy = vehicles.Decals.Layers.Single(l => l.Id != layer.Id);
            var (at, normal) = World(model, layer);
            var (copyAt, copyNormal) = World(model, copy);
            output.WriteLine($"original {at} n {normal}; other side {copyAt} n {copyNormal} on {copy.PartKey}");
            Assert.True(Vector3.Distance(copyAt, new Vector3(at.X, -at.Y, at.Z)) < 3f, $"{copyAt} is not the mirror of {at}");
            Assert.True(Vector3.Dot(copyNormal, new Vector3(normal.X, -normal.Y, normal.Z)) > 0.9f);
            Assert.Equal(!layer.FlipX, copy.FlipX);
            var bothBake = await Bake();
            Assert.True(Changed(diffuse, mask, bothBake).Count > changed.Count, "the other side's tattoo is baked too");

            // A preset of both, put on a second Rager clone: the same bake, byte for byte.
            Assert.True(vehicles.SaveDecalPresetAs("Both sides"));
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "RagerTat2";
            await vehicles.CreateCloneAsync();
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints && vehicles.Preview is not null && vehicles.Decals.CloneLeaf == "BPC_RagerTat2", TimeSpan.FromSeconds(60)));
            Assert.Empty(vehicles.Decals.Layers);
            Assert.True(vehicles.AddDecalPreset("Both sides"));
            var presetBake = await Bake();
            Assert.Equal(bothBake.Diffuse.Rgba, presetBake.Diffuse.Rgba);
            Assert.Equal(bothBake.Mask.Rgba, presetBake.Mask.Rgba);

            // An action and its undo: byte-identical textures.
            vehicles.SelectedDecal = vehicles.DecalLayers[0];
            Assert.True(vehicles.TurnDecal(35));
            Assert.True(vehicles.ResizeDecal(1.4f));
            var turned = await Bake();
            Assert.NotEqual(presetBake.Diffuse.Rgba, turned.Diffuse.Rgba);
            vehicles.UndoDecalCommand.Execute(null);
            var undone = await Bake();
            Assert.Equal(presetBake.Diffuse.Rgba, undone.Diffuse.Rgba);
            Assert.Equal(presetBake.Mask.Rgba, undone.Mask.Rgba);

            // Applied as one journal step and taken back as one.
            await vehicles.ApplyDecalsCommand.ExecuteAsync(null);
            Assert.Equal(2, ctx.Services.Projects.Current!.State.AssetValueOverrides.Count(o => TextureWrites.IsEdit(o.Path)));
            ctx.Services.Projects.Undo();
            Assert.DoesNotContain(ctx.Services.Projects.Current!.State.AssetValueOverrides, o => TextureWrites.IsEdit(o.Path));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>The middles of the triangles of the body paint facing +Y at mid height (the car's right side), biggest first, vehicle space.</summary>
    private static IEnumerable<Vector3> SideAims(PreviewModel model)
    {
        var tris = new List<(Vector3 Centre, Vector3 Cross)>();
        foreach (var part in model.Parts.Where(p => p.Material.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal)))
        {
            var (p, idx) = (part.Mesh.Positions, part.Mesh.Indices);
            Vector3 V(uint i) => part.Transform.TransformPosition(new FVector(p[i * 3], p[(i * 3) + 1], p[(i * 3) + 2])).ToVector3();
            tris.AddRange(Enumerable.Range(0, idx.Length / 3).Select(t => (A: V(idx[t * 3]), B: V(idx[(t * 3) + 1]), C: V(idx[(t * 3) + 2])))
                .Select(t => (Centre: (t.A + t.B + t.C) / 3f, Cross: Vector3.Cross(t.B - t.A, t.C - t.A)))
                .Where(t => t.Cross.Length() > 1f && Math.Abs(Vector3.Normalize(t.Cross).Y) > 0.9f && t.Centre.Y > 20f));
        }

        var (minZ, maxZ) = (tris.Min(t => t.Centre.Z), tris.Max(t => t.Centre.Z));
        return tris.Where(t => t.Centre.Z > minZ + ((maxZ - minZ) * 0.25f) && t.Centre.Z < maxZ - ((maxZ - minZ) * 0.25f))
            .OrderByDescending(t => t.Cross.Length()).Take(40).Select(t => t.Centre);
    }

    private static PreviewPart PartOf(PreviewModel model, DecalLayer layer) => model.Parts.First(p =>
        p.Material == layer.MaterialPath && ModulePageViewModel.DecalPartKey(p.Attachment) == layer.PartKey);

    private static (Vector3 At, Vector3 Normal) World(PreviewModel model, DecalLayer layer)
    {
        var part = PartOf(model, layer);
        return (part.Transform.TransformPosition(FVector.FromVector3(layer.Position)).ToVector3(),
            Vector3.Normalize(part.Transform.TransformVectorNoScale(FVector.FromVector3(layer.Normal)).ToVector3()));
    }

    /// <summary>The game's own colour texture and colour mask of <paramref name="material"/>.</summary>
    private static (TextureImage Diffuse, TextureImage Mask) Stock(AssetCatalog catalog, string material)
    {
        var slots = ModulePageViewModel.ReadTextureSlots(catalog, material);
        TextureImage Load(string parameter) => TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(slots.Single(s => s.Parameter == parameter).TexturePath));
        return (Load("Diffuse Map"), Load("Color Mask"));
    }

    /// <summary>The texels a bake changed in the colour texture or the colour mask.</summary>
    private static HashSet<int> Changed(TextureImage diffuse, TextureImage mask, DecalBakeResult bake)
    {
        Assert.Equal((diffuse.Width, diffuse.Height), (bake.Diffuse.Width, bake.Diffuse.Height));
        var set = new HashSet<int>();
        for (var i = 0; i < diffuse.Width * diffuse.Height; i++)
        {
            var d = i * 4;
            if (diffuse.Rgba[d] != bake.Diffuse.Rgba[d] || diffuse.Rgba[d + 1] != bake.Diffuse.Rgba[d + 1] || diffuse.Rgba[d + 2] != bake.Diffuse.Rgba[d + 2])
            {
                set.Add(i);
                continue;
            }

            if (mask.Width == diffuse.Width && mask.Height == diffuse.Height
                && (mask.Rgba[d] != bake.Mask.Rgba[d] || mask.Rgba[d + 1] != bake.Mask.Rgba[d + 1] || mask.Rgba[d + 2] != bake.Mask.Rgba[d + 2]))
            {
                set.Add(i);
            }
        }

        return set;
    }
}
