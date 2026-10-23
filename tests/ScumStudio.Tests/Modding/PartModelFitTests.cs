using System.Numerics;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Level;
using static ScumStudio.Tests.Modding.PartsSynthetic;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Discord 2026-10-19 (defolt_0999): ZIL wheels on a Rager clone were huge in the game, one lay behind the truck and one
/// stood in the bed. A new model on a part is fitted where the part's own model is (<see cref="PartModelFit"/>) and written
/// as the part's <c>MeshSetup.Offset</c>. The boxes are the cooked bounds of the real meshes (SCUM 1.3.4): the Rager's
/// wheels and the ZIL-131 prop wheel <c>SM_Wheel_01</c> (pivot at the bottom of the tyre, axle along X).
/// </summary>
public sealed class PartModelFitTests
{
    private static readonly BoundingBox RagerFrontLeft = new(new Vector3(-45.09463f, -13.68107f, -45.08939f), new Vector3(45.09461f, 18.867487f, 45.08939f));
    private static readonly BoundingBox RagerFrontRight = new(new Vector3(-45.094612f, -18.02111f, -45.08939f), new Vector3(45.094627f, 14.527458f, 45.08939f));
    private static readonly BoundingBox ZilWheel = new(new Vector3(-27.024754f, -56.687115f, 0.13589859f), new Vector3(17.763256f, 60.48354f, 117.111206f));

    [Fact]
    public void AZilWheelSitsOnTheRagerAxleAtTheRagerWheelsSize()
    {
        foreach (var (rager, outward) in new[] { (RagerFrontLeft, -1f), (RagerFrontRight, 1f) })
        {
            var fit = PartModelFit.Fit(rager, ZilWheel, wheel: true);
            Assert.NotNull(fit);
            var placed = PartModelFit.Apply(fit.Value, ZilWheel);

            // Centred on the axle bone like the game's wheel, 90.19 cm across (not 117), its thin side along Y (the axle).
            Near(rager.Center, placed.Center, 0.01f);
            Assert.Equal(90.19f, MathF.Max(placed.Size.X, placed.Size.Z), 0.02f);
            Assert.Equal(placed.Size.X, placed.Size.Z, 0.5f);
            Assert.Equal(44.79f * 90.19f / 117.17f, placed.Size.Y, 0.05f);
            Assert.Equal(0.7697f, fit.Value.Scale3D.X, 0.001f);

            // Turned a quarter about the vertical: the ZIL wheel's pivot side (+X, its hub face) faces out of the car.
            var hub = fit.Value.Rotation.RotateVector(FVector.Forward);
            Near(new Vector3(0, outward, 0), hub.ToVector3(), 1e-4f);
        }
    }

    [Fact]
    public void WithoutTheFitTheZilWheelHangs58CmOffTheAxle()
    {
        // What the game drew before: the model's own pivot on the wheel bone. Its centre is 58.6 cm above the axle; as the
        // wheel turns, the model swings round the axle (a quarter turn later it lies flat beside the hub on the ground).
        Assert.Equal(58.62f, ZilWheel.Center.Z, 0.01f);
        Assert.Equal(117.17f / 90.18f, ZilWheel.Size.Y / RagerFrontLeft.Size.Z, 0.01f);
        Assert.Equal(0, PartModelFit.AxleOf(ZilWheel.Size));
        Assert.Equal(1, PartModelFit.AxleOf(RagerFrontLeft.Size));
    }

    [Fact]
    public void AnotherPartKeepsItsTurnAndTakesTheLargestSize()
    {
        var door = new BoundingBox(new Vector3(-50, -5, 0), new Vector3(50, 5, 100));
        var crate = new BoundingBox(new Vector3(0, 0, 0), new Vector3(40, 40, 20));
        var fit = PartModelFit.Fit(door, crate, wheel: false)!.Value;
        Assert.Equal(FQuat.Identity, fit.Rotation);
        var placed = PartModelFit.Apply(fit, crate);
        Near(door.Center, placed.Center, 1e-3f);
        Assert.Equal(100f, MathF.Max(placed.Size.X, MathF.Max(placed.Size.Y, placed.Size.Z)), 1e-3f);

        // A round model on a part that is not a wheel is not turned either; a wheel model on a square part is not turned.
        Assert.Equal(FQuat.Identity, PartModelFit.Fit(RagerFrontLeft, ZilWheel, wheel: false)!.Value.Rotation);
        Assert.Equal(FQuat.Identity, PartModelFit.Fit(new BoundingBox(Vector3.Zero, new Vector3(10)), ZilWheel, wheel: true)!.Value.Rotation);
    }

    [Fact]
    public void NothingFitsAnEmptyOrFlatBox()
    {
        Assert.Null(PartModelFit.Fit(BoundingBox.Empty, ZilWheel, wheel: true));
        Assert.Null(PartModelFit.Fit(RagerFrontLeft, new BoundingBox(Vector3.One, Vector3.One), wheel: true));
        Assert.True(PartModelFit.IsWheelSocket("b_Wheel_FrontLeft"));
        Assert.True(PartModelFit.IsWheelSocket("b_Wheel_R"));
        Assert.False(PartModelFit.IsWheelSocket("s_Door_FrontLeft"));
        Assert.False(PartModelFit.IsWheelSocket(null));
    }

    [Fact]
    public void TheEditTextRoundTrips()
    {
        var t = new FTransform(FQuat.FromAxisAngle(FVector.Up, MathF.PI / 2), new FVector(1.5f, -2.25f, 58.6f), new FVector(0.7697f));
        var text = PartModelFit.Format(t);
        Assert.Equal(3, text.Split('|').Length);
        Assert.True(PartModelFit.Parse(text).Equals(t, 1e-6f), text);
        Assert.Throws<FormatException>(() => PartModelFit.Parse("1, 2, 3"));
    }

    [Fact]
    public void ASwappedWheelGetsItsOffsetEditAndAnAddedPartDoesNot()
    {
        const string zil = "/Game/T/SM_Zil.SM_Zil";
        const string own = "/Game/T/SM_Wheel.SM_Wheel";
        var wheel = Parse(Part("Wheel_FrontLeft", "b_Wheel_FrontLeft", own), "Wheel_FrontLeft");
        var bounds = new Dictionary<string, BoundingBox> { [own] = RagerFrontLeft, [zil] = ZilWheel };
        BoundingBox? BoundsOf(string path) => bounds.TryGetValue(path, out var b) ? b : null;
        var swap = new TunableEdit("Default__BPC_Car_Wheel_FrontLeft_C", VehicleAttachmentEdits.MeshPath, zil);

        var (edit, problem) = PartModelFit.OffsetEdit(wheel, [swap], BoundsOf);
        Assert.Null(problem);
        Assert.NotNull(edit);
        Assert.Equal((swap.Export, VehicleAttachmentEdits.OffsetPath), (edit.Export, edit.Path));
        Assert.True(PartModelFit.Parse(edit.Value).Equals(PartModelFit.Fit(RagerFrontLeft, ZilWheel, wheel: true)!.Value, 1e-5f), edit.Value);

        // Nothing to add: no model edit, the part's own model back, or an offset already given.
        Assert.Null(PartModelFit.OffsetEdit(wheel, [], BoundsOf).Edit);
        Assert.Null(PartModelFit.OffsetEdit(wheel, [swap with { Value = own }], BoundsOf).Edit);
        Assert.Null(PartModelFit.OffsetEdit(wheel, [swap, new(swap.Export, VehicleAttachmentEdits.OffsetPath, PartModelFit.Format(FTransform.Identity))], BoundsOf).Edit);

        // A model whose size is unknown: no offset, and the reason for the export's warning.
        var (none, why) = PartModelFit.OffsetEdit(wheel, [swap with { Value = "/Game/T/SM_Gone.SM_Gone" }], BoundsOf);
        Assert.Null(none);
        Assert.Contains("SM_Gone", why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOffsetIsWrittenIntoMeshSetupAndTheGameReaderReadsIt()
    {
        var fit = PartModelFit.Fit(RagerFrontLeft, ZilWheel, wheel: true)!.Value;
        var part = Parse(Part("Wheel_FrontLeft", "b_Wheel_FrontLeft", "/Game/T/SM_Wheel.SM_Wheel"), "Wheel_FrontLeft");
        Assert.Null(VehicleAttachmentEdits.OffsetOf(part));
        var bytes = VehicleParts.Apply(part,
        [
            new("Default__BPC_Car_Wheel_FrontLeft_C", VehicleAttachmentEdits.MeshPath, "/Game/T/SM_Zil.SM_Zil"),
            new("Default__BPC_Car_Wheel_FrontLeft_C", VehicleAttachmentEdits.OffsetPath, PartModelFit.Format(fit)),
        ]);
        var after = Parse(bytes, "Wheel_FrontLeft");
        Assert.True(VehicleAttachmentEdits.OffsetOf(after)!.Value.Equals(fit, 1e-5f));
        Assert.Equal("/Game/T/SM_Zil.SM_Zil", VehicleAttachmentEdits.MeshOf(after));
        Assert.Equal("b_Wheel_FrontLeft", VehicleAttachmentEdits.ParentSocketOf(after));

        // Written again (another model later): the offset is replaced, not added twice.
        var again = Parse(VehicleParts.Apply(after, [new("Default__BPC_Car_Wheel_FrontLeft_C", VehicleAttachmentEdits.OffsetPath, PartModelFit.Format(FTransform.Identity))]), "Wheel_FrontLeft");
        Assert.True(VehicleAttachmentEdits.OffsetOf(again)!.Value.Equals(FTransform.Identity, 1e-6f));

        using var temp = new LevelTempDirectory();
        await bytes.WriteAsync(temp.Combine("SCUM", "Content", "ConZ_Files", "Vehicles", "Car", "Car", "Attachments", "BPC_Car_Wheel_FrontLeft"));
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var cdo = catalog.LoadPackage(Attachments + "/BPC_Car_Wheel_FrontLeft").GetExportOrNull("Default__BPC_Car_Wheel_FrontLeft_C", StringComparison.Ordinal);
        Assert.NotNull(cdo);
        var setup = cdo.GetOrDefault<FStructFallback>("MeshSetup");
        Assert.Equal("/Game/T/SM_Zil.SM_Zil", setup.GetOrDefault<FSoftObjectPath>("Mesh").AssetPathName.Text);
        Assert.Equal(5f, setup.GetOrDefault<float>("Mass"));
        var offset = setup.GetOrDefault<FStructFallback>("Offset");
        Assert.NotNull(offset);
        var t = offset.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("Translation");
        var q = offset.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FQuat>("Rotation");
        var s = offset.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("Scale3D");
        Assert.Equal((fit.Translation.X, fit.Translation.Y, fit.Translation.Z), (t.X, t.Y, t.Z));
        Assert.Equal((fit.Rotation.X, fit.Rotation.Y, fit.Rotation.Z, fit.Rotation.W), (q.X, q.Y, q.Z, q.W));
        Assert.Equal((fit.Scale3D.X, fit.Scale3D.Y, fit.Scale3D.Z), (s.X, s.Y, s.Z));
    }

    [Fact]
    public void AnOffsetThePartAlreadyHasIsReplacedWhole()
    {
        // Like the Kinglet's weapon mounts: MeshSetup.Offset stores its Translation only.
        var p = new SyntheticLevelPackage();
        var cls = p.ClassExport("BPC_Car_Mount_C", p.ScriptClass(Engine, "BlueprintGeneratedClass"), p.ScriptClass("/Script/SCUM", "VehicleAttachment"));
        var cdo = p.Export("Default__BPC_Car_Mount_C", p.ScriptClass(Engine, "Actor"), 0, flags: 0x39);
        p.SetPayload(cls, p.Properties());
        p.SetPayload(cdo, p.Properties(t =>
        {
            t.NameValue("ParentSocket", "s_Mount");
            t.TaggedStruct("MeshSetup", "VehicleAttachmentMeshSetup", m =>
            {
                m.SoftObject("Mesh", "/Game/T/SM_Mount.SM_Mount");
                m.TaggedStruct("Offset", "Transform", o => o.Vector("Translation", 2.35f, 13.08f, 0.98f));
                m.Float("Mass", 1);
            });
            t.Float("_health", 50);
        }));
        var mount = Parse(p.Build(), "Mount");
        var stock = VehicleAttachmentEdits.OffsetOf(mount)!.Value;
        Assert.True(stock.Equals(new FTransform(new FVector(2.35f, 13.08f, 0.98f)), 1e-5f));

        var turned = new FTransform(FQuat.FromAxisAngle(FVector.Up, 0.5f), new FVector(1, 2, 3), new FVector(0.5f));
        var after = Parse(VehicleParts.Apply(mount, [new("Default__BPC_Car_Mount_C", VehicleAttachmentEdits.OffsetPath, PartModelFit.Format(turned))]), "Mount");
        Assert.True(VehicleAttachmentEdits.OffsetOf(after)!.Value.Equals(turned, 1e-5f));
        Assert.Equal("/Game/T/SM_Mount.SM_Mount", VehicleAttachmentEdits.MeshOf(after));
        Assert.Equal(50f, (after.ReadProperties(1).Find("_health")?.Value as ScumStudio.Formats.Properties.FloatValue)?.Value);
        Assert.Equal(1f, ((after.ReadProperties(1).Find("MeshSetup")?.Value as ScumStudio.Formats.Properties.StructValue)?.Find("Mass")?.Value as ScumStudio.Formats.Properties.FloatValue)?.Value);
    }

    private static CookedPackage Parse(PackageBytes bytes, string part) =>
        CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, Attachments + "/BPC_Car_" + part);

    private static void Near(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");
}
