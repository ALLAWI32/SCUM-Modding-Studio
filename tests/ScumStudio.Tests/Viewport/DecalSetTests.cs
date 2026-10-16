using System.Numerics;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.Viewport;

/// <summary>The project's <c>decals/&lt;clone&gt;.json</c> (<see cref="DecalSet"/>).</summary>
public sealed class DecalSetTests
{
    [Fact]
    public void ASetSurvivesTheRoundTrip()
    {
        var layer = new DecalLayer
        {
            DesignId = "tribal-2", PartKey = "Door_FrontLeft", MaterialPath = "/Game/Cars/MI_Body.MI_Body", Position = new Vector3(1.5f, -60, 80),
            Normal = Vector3.UnitY, Up = Vector3.UnitZ, WidthCm = 55, HeightCm = 27.5f, RotationDeg = 12, FlipX = true, Mirror = true, Opacity = 0.75f,
            Order = 3, ColourMode = DecalColourMode.Palette, Colours = ["#112233"], Palette = ["#FF0000", "#00FF00"], Seed = 7,
        };
        var set = new DecalSet("BPC_MyRager", [layer, new DecalLayer { StickerFile = "decals/stickers/cat.png", Order = 1 }]);

        var back = DecalSet.FromJson(set.ToJson());
        Assert.Equal(set.CloneLeaf, back.CloneLeaf);
        Assert.Equal(DecalSet.CurrentVersion, back.Version);
        Assert.Equal(2, back.Layers.Count);
        var l = back.Layers[0];
        Assert.Equal(layer with { Colours = l.Colours, Palette = l.Palette }, l);
        Assert.Equal(layer.Colours, l.Colours);
        Assert.Equal(layer.Palette, l.Palette);
        Assert.Equal(["decals/stickers/cat.png", null], back.Ordered.Select(x => x.StickerFile));
        Assert.Contains("\"colourMode\": \"palette\"", set.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFieldsAreSkippedAndTheVersionIsKept()
    {
        const string Json = """
            { "version": 3, "cloneLeaf": "BPC_X", "futureThing": { "a": 1 },
              "layers": [ { "id": "abc", "designId": "flames-1", "glitter": true, "position": [1, 2, 3], "opacity": 0.5 } ] }
            """;
        var set = DecalSet.FromJson(Json);
        Assert.Equal(3, set.Version);
        Assert.Equal("flames-1", set.Layers.Single().DesignId);
        Assert.Equal(new Vector3(1, 2, 3), set.Layers[0].Position);
        Assert.Equal(0.5f, set.Layers[0].Opacity);
        Assert.Contains("\"version\": 3", set.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASetIsSavedPerClone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "decalset-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(DecalSet.Load(dir, "BPC_A").Layers);
            new DecalSet("BPC_A", [new DecalLayer { DesignId = "skull-plain" }]).Save(dir);
            Assert.True(File.Exists(Path.Combine(dir, "decals", "BPC_A.json")));
            Assert.Equal("skull-plain", DecalSet.Load(dir, "BPC_A").Layers.Single().DesignId);
            Assert.Empty(DecalSet.Load(dir, "BPC_B").Layers);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
