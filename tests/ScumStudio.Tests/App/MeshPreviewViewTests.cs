using System.Globalization;
using System.Numerics;
using ScumStudio.App.Controls;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering;

namespace ScumStudio.Tests.App;

/// <summary>
/// The 3D preview's scale grid, size readout, supersampling and light modes (owner: "any 3D asset you show me … at the
/// original resolution … the real colours"). Reads English texts, so it shares the UI language collection.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class MeshPreviewViewTests
{
    [Fact]
    public void GridIsInMetresForACar()
    {
        // The grid gave a 4.5 m car 10 cm cells, so nothing told its size: from 50 cm up a cell is a metre.
        Assert.Equal(100f, MeshPreview.GridCellFor(450f));
        Assert.Equal(100f, MeshPreview.GridCellFor(50f));
        Assert.Equal(100f, MeshPreview.GridCellFor(3000f)); // a building: metres too, major lines every 10 m
    }

    [Fact]
    public void GridForSmallItems()
    {
        Assert.Equal(10f, MeshPreview.GridCellFor(30f));
        Assert.Equal(10f, MeshPreview.GridCellFor(5f));
        Assert.Equal(1f, MeshPreview.GridCellFor(3f));
    }

    [Fact]
    public void SizeReadout()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            // GL bounds: X is UE X (length), Y is UE Z (height), Z is UE Y (width).
            var car = new BoundingBox(Vector3.Zero, new Vector3(412f, 152f, 186f));
            Assert.Equal("4.12 × 1.86 × 1.52 m", MeshPreview.PreviewSize(car));
            Assert.Equal("20 × 20 × 20 cm", MeshPreview.PreviewSize(new BoundingBox(new Vector3(-10f), new Vector3(10f))));
            Assert.Equal("2.5 × 0.8 × 0.8 cm", MeshPreview.PreviewSize(new BoundingBox(Vector3.Zero, new Vector3(2.5f, 0.8f, 0.8f))));
            Assert.Equal(string.Empty, MeshPreview.PreviewSize(BoundingBox.Empty));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("4,12 × 1,86 × 1,52 m", MeshPreview.PreviewSize(car));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void RenderScaleIsTwoUntil4K()
    {
        Assert.Equal(2, MeshPreview.RenderScale(1280, 720));
        Assert.Equal(2, MeshPreview.RenderScale(1920, 1080)); // exactly a 4K frame
        Assert.Equal(1, MeshPreview.RenderScale(3840, 2160));
        Assert.Equal(1, MeshPreview.RenderScale(2100, 600)); // an edge past 4096
    }

    [Fact]
    public void SunSettings()
    {
        var key = Vector3.Normalize(new Vector3(-1f, -1f, 0.3f));
        var sun = MeshPreview.SettingsFor(PreviewLight.Sun, new RenderSettings(), 100f, key);
        Assert.True(sun.Sky);
        Assert.True(sun.Shadows);
        Assert.Equal(13.5f, sun.TimeOfDay);
        Assert.True(sun.ShadowDistance >= 3000f);
        Assert.Equal(RenderSettings.IslandSunDirection, sun.LightDirection); // the sun stays where it is as the camera turns
        Assert.Equal(6000f, MeshPreview.SettingsFor(PreviewLight.Sun, new RenderSettings(), 1000f, key).ShadowDistance);

        // Back to the studio from the sun: the studio light as it was before the sun option.
        var studio = MeshPreview.SettingsFor(PreviewLight.Studio, sun, 100f, key);
        Assert.False(studio.Sky);
        Assert.False(studio.Shadows);
        Assert.Null(studio.TimeOfDay);
        Assert.Equal(0f, studio.Exposure);
        Assert.Equal(new Vector3(0.5f, 0.52f, 0.56f), studio.SkyColor);
        Assert.Equal(new Vector3(0.2f, 0.19f, 0.17f), studio.GroundColor);
        Assert.Equal(new Vector3(0.72f, 0.7f, 0.64f), studio.LightColor);
        Assert.Equal(key, studio.LightDirection);
        Assert.Equal(new RenderSettings().ClearColor, studio.ClearColor);
    }

    [Fact]
    public void ExpandedPreviewFillsTheDetailsPanel()
    {
        Assert.Equal(240d, ScumStudio.App.Views.Pages.AssetsPageView.PreviewHeightFor(false, 900d));
        Assert.Equal(816d, ScumStudio.App.Views.Pages.AssetsPageView.PreviewHeightFor(true, 900d)); // below the details' title
        Assert.Equal(240d, ScumStudio.App.Views.Pages.AssetsPageView.PreviewHeightFor(true, 200d)); // never smaller than before
    }

    [Fact]
    public void SunToggleConvertsBothWays()
    {
        Assert.Equal(true, PreviewLightConverter.IsSun.Convert(PreviewLight.Sun, typeof(bool?), null, CultureInfo.InvariantCulture));
        Assert.Equal(false, PreviewLightConverter.IsSun.Convert(PreviewLight.Studio, typeof(bool?), null, CultureInfo.InvariantCulture));
        Assert.Equal(PreviewLight.Sun, PreviewLightConverter.IsSun.ConvertBack(true, typeof(PreviewLight), null, CultureInfo.InvariantCulture));
        Assert.Equal(PreviewLight.Studio, PreviewLightConverter.IsSun.ConvertBack(false, typeof(PreviewLight), null, CultureInfo.InvariantCulture));
    }
}
