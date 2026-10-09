using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner: "the textures are very bad, white; give me the real colour". The Military_Hangar_RW roofs at the B_2 airfield
/// drew their near-white aluminium panels as they are; SCUM's master shader draws them at 'Base Material Brightness' 0.2,
/// as dark as the game's own bake of the hangar (the atlas its far LODs wear). Real game files only.
/// </summary>
public sealed class HangarColourRealTests
{
    [Fact]
    public void TheHangarRoofIsAsDarkAsTheGamesOwnBakeOfIt()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = ScumStudio.Pak.AesKeyText.FromEnvironmentOrStore() });
        var airport = LevelDocument.Load(new Cue4ParseLevelReader(catalog), "/Game/ConZ_Files/Maps/The_Island/B_2_Airport_03");
        var scene = new LevelScenePreparer(catalog).Prepare([airport], new LevelSceneOptions { IncludeInstances = false, TextureSize = 64 });

        var roofs = scene.Meshes.Values.SelectMany(m => m.MaterialTextures
            .Where(t => t.Key.Contains("/MI_MH_Metal_Sheets_", StringComparison.OrdinalIgnoreCase) && !t.Key.EndsWith(ScumStudio.Rendering.Resources.GpuMesh.NormalMapSuffix, StringComparison.Ordinal))
            .Select(t => (Material: t.Key, Texture: t.Value, Tint: m.MaterialTints.TryGetValue(t.Key, out var tint) ? tint : Vector4.One))).ToList();
        Assert.NotEmpty(roofs);

        var bake = MeanLuminance(catalog, "/Game/ConZ_Files/Models/Objects/Outdoor/Military/Military_Hangar_RW/Atlas/T_MH_D.T_MH_D"); // ~0.075
        foreach (var roof in roofs)
        {
            var drawn = MeanLuminance(catalog, roof.Texture) * Luminance(new Vector3(roof.Tint.X, roof.Tint.Y, roof.Tint.Z)); // panels ~0.53 untinted
            Assert.True(drawn > bake * 0.5 && drawn < bake * 2, $"{roof.Material}: drawn {drawn:0.###}, the game's bake {bake:0.###}");
        }
    }

    private static float Luminance(Vector3 linear) => Vector3.Dot(linear, new Vector3(0.2126f, 0.7152f, 0.0722f));

    private static float MeanLuminance(AssetCatalog catalog, string texture)
    {
        var image = TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(texture), maxSize: 64);
        var sum = Vector3.Zero;
        for (var i = 0; i < image.Rgba.Length; i += 4)
        {
            sum += new Vector3(ColorSpace.SrgbToLinear(image.Rgba[i] / 255f), ColorSpace.SrgbToLinear(image.Rgba[i + 1] / 255f), ColorSpace.SrgbToLinear(image.Rgba[i + 2] / 255f));
        }

        return Luminance(sum / (image.Rgba.Length / 4));
    }
}
