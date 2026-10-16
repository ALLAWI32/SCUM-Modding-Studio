using System.Numerics;
using Avalonia.Media;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Core.Mathematics;
using ScumStudio.Modding.Tuning;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Materials per part on the Vehicles studio (owner: "a gold that is bright or not, chrome, diamond-like … every part"):
/// brightness and saturation make the stored colour, roughness is <c>M_Car_01</c>'s <c>Bottom Roughness_01/_02</c> by name,
/// the new finishes, all slots at once, and Game colours.
/// </summary>
public sealed class VehicleStudioMaterialsTests
{
    private const string Body = "/Game/Vehicles/Test/Materials/MI_Test_Body";
    private const string Doors = "/Game/Vehicles/Test/Materials/MI_Test_Doors";

    [Fact]
    public void BrightnessTwoHundredPercentDoublesTheLinearColourWritten()
    {
        var paint = Paint(Body);
        paint.Colour = Color.FromRgb(200, 120, 40);
        paint.Brightness = 2;
        var written = Written(paint, paint.ColourValue);
        var picked = PaintMaterialViewModel.ToLinear(Color.FromRgb(200, 120, 40));
        Assert.Equal(picked.X * 2, written[0], 3);
        Assert.Equal(picked.Y * 2, written[1], 3);
        Assert.Equal(picked.Z * 2, written[2], 3);
        Assert.True(written[0] > 1f); // over 1: a bright gold the game draws brighter than white paint
        Assert.Equal(picked.Y * 2, paint.LinearColour.Y, 3); // the 3D view bakes the same colour

        // Read back (Reset after an apply), the stored colour splits into the picked colour and its brightness again.
        var applied = paint.ColourValue with { Committed = TunableValue.Format(written[0], written[1], written[2], 1f) };
        var again = new PaintMaterialViewModel(paint.MaterialPath, "Body", applied, paint.SecondValue, paint.MetalValue, paint.ClearCoatValue);
        Assert.Equal(written[0], again.Brightness, 2);
        Assert.False(again.IsPending);
    }

    [Fact]
    public void SaturationZeroIsGreyAndTwoIsStronger()
    {
        var paint = Paint(Body);
        paint.Colour = Color.FromRgb(200, 60, 30);
        paint.Saturation = 0;
        var grey = Written(paint, paint.ColourValue);
        Assert.Equal(grey[0], grey[1], 4);
        Assert.Equal(grey[1], grey[2], 4);
        Assert.Equal(PaintMaterialViewModel.ToLinear(Color.FromRgb(200, 60, 30)).X, grey[0], 3); // HSV: the brightest channel stays

        paint.Saturation = 2;
        var strong = Written(paint, paint.ColourValue);
        var picked = PaintMaterialViewModel.ToLinear(Color.FromRgb(200, 60, 30));
        Assert.True(strong[1] < picked.Y && strong[2] <= picked.Z);
        Assert.Equal(picked.X, strong[0], 3);

        var second = Written(paint, paint.SecondValue!); // both colours of a two-tone body take the tone
        Assert.NotNull(second);
    }

    [Fact]
    public void DiamondIsMetalClearCoatAndAMirrorRoughnessByName()
    {
        var paint = Paint(Body);
        paint.ApplyFinishCommand.Execute(PaintFinish.All.Single(f => f.Key == "Diamond"));
        Assert.Equal((1d, 1d, 0.02), (paint.Metal, paint.ClearCoat, paint.Roughness));
        var changes = paint.Changes().ToDictionary(c => c.Value.Tunable.Path, c => c.Text);
        Assert.Equal("0.02", changes[MaterialParameters.PathOf("ScalarParameterValues", "Bottom Roughness_01")]);
        Assert.Equal("0.02", changes[MaterialParameters.PathOf("ScalarParameterValues", "Bottom Roughness_02")]);
        Assert.Equal("1", changes["ScalarParameterValues[0].ParameterValue"]);
        Assert.Equal("1", changes["ScalarParameterValues[1].ParameterValue"]);
        var colour = TunableValue.ParseFloats(changes["VectorParameterValues[0].ParameterValue"], 4);
        Assert.True(colour[2] > colour[0] && colour[0] > 0.8f, "near white with a cool tint");

        // Plain keeps the colour picked and makes it matte.
        paint.Colour = Color.FromRgb(30, 140, 60);
        paint.ApplyFinishCommand.Execute(PaintFinish.All.Single(f => f.Key == "Plain"));
        Assert.Equal(Color.FromRgb(30, 140, 60), paint.Colour);
        Assert.Equal((0d, 0d, 0.6), (paint.Metal, paint.ClearCoat, paint.Roughness));
        Assert.All(PaintFinish.All, f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));
    }

    [Fact]
    public void AFinishGoesToEverySlotWhenPaintedTogetherAndToOneOtherwise()
    {
        using var ctx = AppTestContext.Create();
        using var page = new VehiclesPageViewModel(ctx.Services);
        var (body, doors) = (Paint(Body), Paint(Doors));
        page.ShowPaints([body, doors]);
        var gold = PaintFinish.All.Single(f => f.Key == "GoldBright");

        page.PaintTogether = true;
        body.ApplyFinishCommand.Execute(gold);
        Assert.Equal((gold.Colour!.Value, 1.6, 0.12), (doors.Colour, doors.Brightness, doors.Roughness));
        Assert.Equal(2, page.PaintPendingCount);

        page.DiscardPaintCommand.Execute(null);
        page.PaintTogether = false;
        body.ApplyFinishCommand.Execute(PaintFinish.All.Single(f => f.Key == "GoldDull"));
        Assert.True(body.IsPending);
        Assert.False(doors.IsPending);
    }

    [Fact]
    public void GameColoursPutsBackEveryNewValue()
    {
        using var ctx = AppTestContext.Create();
        using var page = new VehiclesPageViewModel(ctx.Services);
        var paints = new[] { Paint(Body), Paint(Doors) };
        page.ShowPaints(paints);
        page.PaintTogether = false;
        foreach (var paint in paints)
        {
            paint.Colour = Colors.HotPink;
            paint.Brightness = 3;
            paint.Saturation = 0.2;
            paint.Roughness = 0.9;
        }

        page.AllOriginalPaintCommand.Execute(null);
        Assert.All(paints, p =>
        {
            Assert.True(p.IsOriginal);
            Assert.Equal((1d, 1d, 0.35), (p.Brightness, p.Saturation, p.Roughness)); // 0.35: the stock roughness the material stores
            Assert.Empty(p.Changes());
        });
    }

    [Fact]
    public void UsedByNamesThePartsOfAMaterialSlot()
    {
        var mesh = PrimitiveMeshes.Cube(10f);
        var parts = new[]
        {
            new PreviewPart("SK_Test", mesh, null, FTransform.Identity) { Material = Body + ".MI_Test_Body" },
            new PreviewPart("SK_Door", mesh, null, FTransform.Identity) { Material = Body + ".MI_Test_Body", Attachment = "/Game/V/BPC_Test_Door_FrontLeft" },
            new PreviewPart("SK_Wheel", mesh, null, FTransform.Identity) { Material = Doors + ".MI_Test_Doors" },
        };
        var model = new PreviewModel("Test", parts, new Dictionary<string, TextureImage>());
        Assert.Equal("SK_Test, Door_FrontLeft", ModulePageViewModel.UsedBy(model, Body + ".MI_Test_Body"));
        Assert.Equal(string.Empty, ModulePageViewModel.UsedBy(model, "/Game/Other.Other"));
    }

    [Fact]
    public void ToneKeepsHueAndScalesTheLinearColour()
    {
        var c = Color.FromRgb(180, 90, 30);
        var l = PaintMaterialViewModel.ToLinear(c);
        var same = PaintMaterialViewModel.Tone(c, 1, 1);
        var half = PaintMaterialViewModel.Tone(c, 0.5, 1);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(l[i], same[i], 5);
            Assert.Equal(l[i] * 0.5f, half[i], 5);
        }
    }

    /// <summary>A two-tone car paint (colour A and B, metal, clear coat) with M_Car_01's roughness by name (stored 0.35).</summary>
    internal static PaintMaterialViewModel Paint(string package)
    {
        PaintValue Value(string path, TunableKind kind, string stock) => new(package, new Tunable("Default__MI", path, kind, stock), stock);
        var grey = TunableValue.Format(0.2f, 0.2f, 0.2f, 1f);
        return new PaintMaterialViewModel(package + "." + package[(package.LastIndexOf('/') + 1)..], "Body",
            Value("VectorParameterValues[0].ParameterValue", TunableKind.Color, grey),
            Value("VectorParameterValues[1].ParameterValue", TunableKind.Color, grey),
            Value("ScalarParameterValues[0].ParameterValue", TunableKind.Float, "0.2"),
            Value("ScalarParameterValues[1].ParameterValue", TunableKind.Float, "0.5"),
            roughness: Value(MaterialParameters.PathOf("ScalarParameterValues", PaintMaterialViewModel.RoughnessParameter), TunableKind.Float, "0.35"),
            roughnessB: Value(MaterialParameters.PathOf("ScalarParameterValues", PaintMaterialViewModel.RoughnessBParameter), TunableKind.Float, "0.35"));
    }

    private static float[] Written(PaintMaterialViewModel paint, PaintValue value) =>
        TunableValue.ParseFloats(paint.Changes().Single(c => ReferenceEquals(c.Value, value)).Text, 4);
}
