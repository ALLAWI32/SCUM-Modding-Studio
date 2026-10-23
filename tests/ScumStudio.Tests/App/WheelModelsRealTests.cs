using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Export;
using ScumStudio.Modding;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;
using UeQuat = CUE4Parse.UE4.Objects.Core.Math.FQuat;
using UeVector = CUE4Parse.UE4.Objects.Core.Math.FVector;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord #bug-reports 2026-10-19, defolt_0999 on 3.5.0: "I just replaced all four wheels with Zil wheels. When I saw that
/// nothing had changed in the editor, I decided to just check what it looked like in the game" (a Rager clone with huge
/// ZIL wheels, one lying behind the truck, one standing in the bed). The same steps on the real game files (<c>SCUM_PAKS</c>,
/// key from this PC's store) through the Vehicles page: a Rager clone, each wheel picked in the 3D view, "zil wheel"
/// searched, the first model (the ZIL-131 wheel <c>SM_Wheel_01</c>) put on with "Other model…". The 3D view shows it at
/// once where the Rager's wheel is; the exported wheels carry it with a <c>MeshSetup.Offset</c> that puts its geometry
/// (LOD 0 vertices) on the Rager wheel's: same centre on the axle bone, same diameter, axle along Y; the bone, the hit mesh
/// and the wheel's driving data stay the Rager's. The server cook (<c>SCUM_SERVER_PAKS</c>) writes the same offset. How the
/// game draws it rolling only the game shows.
/// </summary>
public sealed class WheelModelsRealTests(ITestOutputHelper output)
{
    private const string Zil = "/Game/ConZ_Files/Models/Vehicles/Zil_Truck/ZIL131/Props/SM_Wheel_01.SM_Wheel_01";
    private const string RagerAttachments = "/Game/ConZ_Files/Vehicles/Car/Rager/Attachments/";
    private static readonly string[] Wheels = ["FrontLeft", "FrontRight", "BackLeft", "BackRight"];

    [AvaloniaPaksFact]
    public async Task ZilWheelsOnARagerCloneSitOnTheRagersWheelsInTheViewAndInTheMod()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder! });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Zil wheels");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "ZILWheels";
            await vehicles.CreateCloneAsync();
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.Preview is not null && vehicles.HasPaints, TimeSpan.FromSeconds(60)));
            vehicles.StudioTab = StudioTab.Parts;
            var catalog = ctx.Services.Workspace.Catalog!;
            var state = ctx.Services.Projects.Current!.State;
            var clone = state.FindCloneOf(vehicles.SelectedItem!.PackagePath)!;
            string CloneOf(string wheel) => clone.Packages.Single(p => p.Old == RagerAttachments + "BPC_Rager_Wheel_" + wheel).New;

            // Nothing replaced yet: a painted part (the clone's own copy of its mesh) still names the game's model.
            await vehicles.StudioPartsCompletion;
            Assert.Empty(vehicles.StudioPartOverrides);
            Assert.DoesNotContain(vehicles.Preview!.Parts, p => clone.Packages.Any(c => c.New == p.Attachment));
            vehicles.OnStudioPick(vehicles.Preview!.Parts.ToList().FindIndex(p => p.Attachment == RagerAttachments + "BPC_Rager_Door_FrontLeft"));
            output.WriteLine($"door: '{vehicles.StudioPartModel}'; painted copies: {string.Join(", ", clone.Packages.Where(p => p.New.Contains("/Meshes/", StringComparison.Ordinal)).Select(p => p.New))}");
            Assert.StartsWith(ScumStudio.App.Localization.Loc.F("Studio.Parts.Model", string.Empty), vehicles.StudioPartModel, StringComparison.Ordinal);
            Assert.Contains("_Rager_Door_FrontLeft", vehicles.StudioPartModel, StringComparison.Ordinal);
            Assert.DoesNotContain("ZILWheels", vehicles.StudioPartModel, StringComparison.Ordinal);

            // The game's wheels where the 3D view draws them (vehicle space), before anything changes.
            var game = Wheels.ToDictionary(w => w, w => WorldBounds(vehicles.Preview!.Parts.Where(p => p.Attachment == RagerAttachments + "BPC_Rager_Wheel_" + w)));
            foreach (var (wheel, box) in game)
            {
                output.WriteLine($"Rager {wheel}: centre {box.Center}, size {box.Size}");
            }

            // Each wheel: pick it on the car, search "zil wheel", take the first model, "Other model…".
            foreach (var wheel in Wheels)
            {
                var index = vehicles.Preview!.Parts.ToList().FindIndex(p => p.Attachment == RagerAttachments + "BPC_Rager_Wheel_" + wheel);
                Assert.True(index >= 0, wheel);
                vehicles.OnStudioPick(index);
                Assert.False(vehicles.CanEditPart); // a wheel cannot be moved …
                vehicles.PartMeshSearch = "zil wheel";
                Assert.Equal(Zil, vehicles.PartMeshChoices[0]); // … the model the report's user got first
                vehicles.SelectedPartMesh = vehicles.PartMeshChoices[0];
                var history = ctx.Services.Projects.Current!.History.Count;
                vehicles.SwapPartToSelectedMeshCommand.Execute(null);
                Assert.Equal(history + 1, ctx.Services.Projects.Current!.History.Count);
                Assert.Contains("SM_Wheel_01", vehicles.StudioPartModel, StringComparison.Ordinal);
                Assert.Contains("SM_Rager_Wheel_" + wheel, vehicles.StudioPartModel, StringComparison.Ordinal);
                output.WriteLine($"{wheel}: '{vehicles.StudioPartNote}' / '{vehicles.StudioPartModel}'");
            }

            // The 3D view: each clone wheel draws the ZIL wheel on the Rager wheel's place and size; the Rager's are hidden.
            await vehicles.StudioPartsCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => Wheels.All(w => vehicles.Preview!.Parts.Any(p => p.Attachment == CloneOf(w))), TimeSpan.FromSeconds(60)), "the new wheels are drawn");
            await vehicles.StudioPartsCompletion;
            foreach (var wheel in Wheels)
            {
                var shown = WorldBounds(vehicles.Preview!.Parts.Where(p => p.Attachment == CloneOf(wheel)));
                output.WriteLine($"view {wheel}: centre {shown.Center}, size {shown.Size}");
                Assert.True(Vector3.Distance(game[wheel].Center, shown.Center) < 1f, $"{wheel}: {shown.Center} vs {game[wheel].Center}");
                Assert.Equal(Largest(game[wheel].Size), Largest(shown.Size), 1f);
                Assert.True(shown.Size.Y < shown.Size.X / 2, $"{wheel}: the axle runs along Y ({shown.Size})");
                var stock = vehicles.Preview!.Parts.Select((p, i) => (p, i)).Where(x => x.p.Attachment == RagerAttachments + "BPC_Rager_Wheel_" + wheel).Select(x => x.i).ToList();
                Assert.All(stock, i => Assert.True(vehicles.StudioPartOverrides.TryGetValue(i, out var o) && o.Hidden, $"{wheel}: the Rager's own wheel is hidden"));
            }

            // The mod: each clone wheel takes the ZIL model with an offset that puts its geometry on the Rager wheel's.
            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, catalog, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            output.WriteLine(string.Join("\n", result.Warnings));
            Assert.DoesNotContain(result.Warnings, w => w.Contains("could not be read", StringComparison.Ordinal) || w.Contains("edits skipped", StringComparison.Ordinal));
            var zil = Vertices(catalog, Zil);
            using (var written = AssetCatalog.OpenLoose(result.StagingDirectory))
            {
                foreach (var wheel in Wheels)
                {
                    var package = CloneOf(wheel);
                    var cdo = written.LoadPackage(package).GetExports().Single(e => e.Name.StartsWith("Default__", StringComparison.Ordinal));
                    var setup = cdo.GetOrDefault<FStructFallback>("MeshSetup");
                    Assert.Equal(Zil, setup.GetOrDefault<FSoftObjectPath>("Mesh").AssetPathName.Text);
                    Assert.Equal("b_Wheel_" + wheel, cdo.GetOrDefault<FName>("ParentSocket").Text); // still on the wheel's bone
                    var offset = Offset(setup.GetOrDefault<FStructFallback>("Offset"));
                    var rager = Vertices(catalog, "/Game/ConZ_Files/Models/Vehicles2/Pickup/Meshes/SM_Rager_Wheel_" + wheel + ".SM_Rager_Wheel_" + wheel);
                    var placed = BoundingBox.FromPositions(zil.SelectMany(v => Floats(offset.TransformPosition(v))).ToArray());
                    var own = BoundingBox.FromPositions(rager.SelectMany(Floats).ToArray());
                    output.WriteLine($"mod {wheel}: offset {offset}; ZIL placed centre {placed.Center} size {placed.Size}; Rager centre {own.Center} size {own.Size}");
                    Assert.True(Vector3.Distance(own.Center, placed.Center) < 0.5f, $"{wheel}: centre {placed.Center} vs {own.Center}");
                    Assert.Equal(Largest(own.Size), Largest(placed.Size), 0.5f);
                    Assert.True(placed.Size.Y < 40f && placed.Size.X > 85f && placed.Size.Z > 85f, $"{wheel}: axle along Y ({placed.Size})");
                    Assert.True(offset.Scale3D.X is > 0.7f and < 0.8f, $"{wheel}: scale {offset.Scale3D}");

                    // The hit mesh and the wheel's driving data are the Rager's own (the radius lives on the wheel class).
                    var query = cdo.GetOrDefault<FStructFallback>("QueryMeshSetup").GetOrDefault<FSoftObjectPath>("Mesh").AssetPathName.Text;
                    Assert.EndsWith("/SM_Rager_Wheel_" + wheel + "_Query.SM_Rager_Wheel_" + wheel + "_Query", query, StringComparison.Ordinal);
                    Assert.Null(cdo.GetOrDefault<FStructFallback>("WheelData")?.GetOrDefault<float?>("Radius"));
                }
            }

            // The server cook writes the same offsets.
            if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is { Length: > 0 } serverPaks && Directory.Exists(serverPaks))
            {
                using var server = AssetCatalog.OpenPaks(serverPaks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
                var client = AssetModBuilder.Build(catalog, ProjectExporter.BuildAssetRequest(state) with { WriteRegistry = false });
                var cooked = AssetModBuilder.Build(server, ProjectExporter.BuildAssetRequest(state) with { WriteRegistry = false });
                foreach (var wheel in Wheels)
                {
                    FTransform? Of(AssetModBuildResult r) => r.Packages.Single(p => p.PackagePath == CloneOf(wheel)) is var b
                        ? VehicleAttachmentEdits.OffsetOf(ScumStudio.Formats.Packages.CookedPackage.Parse(b.Bytes.UAsset, b.Bytes.UExp, b.UBulk, b.PackagePath))
                        : null;
                    Assert.True(Of(cooked)!.Value.Equals(Of(client)!.Value, 1e-5f), wheel);
                }

                output.WriteLine("server cook: same offsets");
            }

            // Ctrl+Z takes the last wheel's ZIL model off in the view.
            ctx.Services.Projects.Undo();
            await vehicles.StudioPartsCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => !vehicles.Preview!.Parts.Any(p => p.Attachment == CloneOf("BackRight")), TimeSpan.FromSeconds(30)));
            Assert.Contains(vehicles.Preview!.Parts, p => p.Attachment == CloneOf("BackLeft"));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>The world-space bounds of preview parts (each vertex through its part's transform).</summary>
    private static BoundingBox WorldBounds(IEnumerable<PreviewPart> parts)
    {
        var box = BoundingBox.Empty;
        foreach (var part in parts)
        {
            for (var i = 0; i < part.Mesh.Positions.Length; i += 3)
            {
                box = box.Include(part.Transform.TransformPosition(new FVector(part.Mesh.Positions[i], part.Mesh.Positions[i + 1], part.Mesh.Positions[i + 2])).ToVector3());
            }
        }

        return box;
    }

    /// <summary>The LOD 0 vertices of a mesh of the game (its real geometry, not its cooked bounds).</summary>
    private static List<FVector> Vertices(AssetCatalog catalog, string objectPath)
    {
        var data = MeshExtractor.Extract(catalog.LoadObject<UObject>(objectPath));
        return Enumerable.Range(0, data.VertexCount).Select(i => new FVector(data.Positions[i * 3], data.Positions[i * 3 + 1], data.Positions[i * 3 + 2])).ToList();
    }

    private static FTransform Offset(FStructFallback? offset)
    {
        Assert.NotNull(offset);
        var t = offset.GetOrDefault<UeVector>("Translation");
        var q = offset.GetOrDefault<UeQuat>("Rotation");
        var s = offset.GetOrDefault<UeVector>("Scale3D");
        return new FTransform(new FQuat(q.X, q.Y, q.Z, q.W), new FVector(t.X, t.Y, t.Z), new FVector(s.X, s.Y, s.Z));
    }

    private static float[] Floats(FVector v) => [v.X, v.Y, v.Z];

    private static float Largest(Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));
}
