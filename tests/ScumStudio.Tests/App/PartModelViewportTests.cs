using System.Numerics;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;
using static ScumStudio.Tests.Modding.PartsSynthetic;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord 2026-10-19 (defolt_0999): "I replaced all four wheels with Zil wheels. When I saw that nothing had changed in
/// the editor …". The Parts tab's "Other model…" on a clone's wheel: the 3D view draws the new model at once, fitted where
/// the game's wheel is (the game's hidden), the panel names both models, a model whose size cannot be read is refused,
/// and Ctrl+Z puts the game's wheel back. A synthetic car on the Vehicles page; the models stand in as boxes of the real
/// meshes' cooked bounds (Rager wheel, ZIL-131 prop wheel).
/// </summary>
public sealed class PartModelViewportTests
{
    private const string NewMesh = "/Game/T/SK_New";
    private const string GameWheel = "/Game/T/SM_Wheel.SM_Wheel";
    private const string Zil = "/Game/T/SM_Zil.SM_Zil";
    private const string Odd = "/Game/T/SM_Odd.SM_Odd";
    private static readonly string StockWheel = Attachments + "/BPC_Car_Wheel_FrontLeft";
    private static readonly string CloneWheel = Attachments + "/BPC_New_Wheel_FrontLeft";
    private static readonly BoundingBox RagerWheel = new(new Vector3(-45.09463f, -13.68107f, -45.08939f), new Vector3(45.09461f, 18.867487f, 45.08939f));
    private static readonly BoundingBox ZilWheel = new(new Vector3(-27.024754f, -56.687115f, 0.13589859f), new Vector3(17.763256f, 60.48354f, 117.111206f));
    private static readonly FTransform WheelBone = new(new FVector(178.77f, -83.52f, 14.49f));

    [Fact]
    public async Task AnotherWheelModelShowsFittedOnTheWheelAndCtrlZPutsTheGamesBack()
    {
        using var temp = new LevelTempDirectory();
        var clone = await WriteCarAsync(temp);
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Wheels");
        ctx.Services.Projects.Apply(clone);
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.PreviewLoaderOverride = (_, _) => Task.FromResult<PreviewModel?>(Car());
        page.PartModelLoaderOverride = m => m == Zil ? Box("SM_Zil", ZilWheel) : null;
        page.MeshBoundsOverride = m => m == GameWheel ? RagerWheel : m == Zil ? ZilWheel : null;
        Assert.True(await page.OpenLooseFolderAsync(temp.Path));
        Assert.True(await page.SelectAsync("BPC_New"));
        await page.PreviewCompletion;
        await page.StudioPartsCompletion;
        page.StudioTab = StudioTab.Parts;
        var wheel = page.Preview!.Parts.ToList().FindIndex(p => p.Attachment == StockWheel);
        Assert.True(wheel >= 0);

        page.OnStudioPick(wheel);
        Assert.Equal("Wheel FrontLeft", page.StudioPartName);
        Assert.Equal(Loc.T("Studio.Parts.WheelNote"), page.StudioPartNote);
        Assert.Equal(Loc.F("Studio.Parts.Model", "SM_Wheel"), page.StudioPartModel);

        // "Other model…": one journal step; the ZIL wheel is drawn by the clone's wheel where the game's wheel is, the game's hidden.
        var history = ctx.Services.Projects.Current!.History.Count;
        Assert.True(page.SwapPartMesh(Zil));
        Assert.Equal(history + 1, ctx.Services.Projects.Current!.History.Count);
        await page.StudioPartsCompletion;
        var drawn = page.Preview!.Parts.Select((p, i) => (Part: p, Index: i)).Where(p => p.Part.Attachment == CloneWheel).ToList();
        var shown = Assert.Single(drawn);
        Assert.True(page.StudioPartOverrides.TryGetValue(wheel, out var game) && game.Hidden, "the game's wheel is hidden");
        Assert.False(page.StudioPartOverrides.ContainsKey(shown.Index)); // a wheel cannot be moved: drawn where the fit puts it
        var placed = BoundingBox.FromPositions(Transformed(shown.Part));
        var expected = PartModelFit.Apply(WheelBone, RagerWheel);
        Assert.True(Vector3.Distance(expected.Center, placed.Center) < 0.01f, $"{placed.Center} vs {expected.Center}");
        Assert.Equal(expected.Size.X, placed.Size.X, 0.05f); // 90.19 cm across, as the game's wheel
        Assert.True(placed.Size.Y < 40f, $"axle along Y, {placed.Size.Y} cm wide");
        Assert.Equal(Loc.F("Studio.Parts.WheelModelFitted", "SM_Zil", "SM_Wheel"), page.StudioPartModel);

        // Clicking the new model picks the same wheel.
        page.OnStudioPick(shown.Index);
        Assert.Equal("Wheel FrontLeft", page.StudioPartName);
        Assert.Equal(Loc.F("Studio.Parts.WheelModelFitted", "SM_Zil", "SM_Wheel"), page.StudioPartModel);

        // A model whose size cannot be read is refused, with the reason; nothing is journaled.
        Assert.False(page.SwapPartMesh(Odd));
        Assert.Equal(Loc.T("Studio.Parts.ModelNoSize"), page.StudioPartNote);
        Assert.Equal(history + 1, ctx.Services.Projects.Current!.History.Count);

        // Ctrl+Z: the game's wheel again.
        ctx.Services.Projects.Undo();
        await page.StudioPartsCompletion;
        Assert.DoesNotContain(page.Preview!.Parts, p => p.Attachment == CloneWheel);
        Assert.False(page.StudioPartOverrides.ContainsKey(wheel));
        page.OnStudioPick(wheel);
        Assert.Equal(Loc.F("Studio.Parts.Model", "SM_Wheel"), page.StudioPartModel);
    }

    [Fact]
    public async Task AnotherDoorModelFollowsTheDoorsMoves()
    {
        using var temp = new LevelTempDirectory();
        var clone = await WriteCarAsync(temp);
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Doors");
        ctx.Services.Projects.Apply(clone);
        using var page = new VehiclesPageViewModel(ctx.Services);
        var door = new BoundingBox(new Vector3(-50, -5, 0), new Vector3(50, 5, 100));
        page.PreviewLoaderOverride = (_, _) => Task.FromResult<PreviewModel?>(Car());
        page.PartModelLoaderOverride = m => m == "/Game/T/SK_Hood.SK_Hood" ? Box("SK_Hood", ZilWheel) : null;
        page.MeshBoundsOverride = m => m == "/Game/T/SK_Door.SK_Door" ? door : m == "/Game/T/SK_Hood.SK_Hood" ? ZilWheel : null;
        Assert.True(await page.OpenLooseFolderAsync(temp.Path));
        Assert.True(await page.SelectAsync("BPC_New"));
        await page.PreviewCompletion;
        page.StudioTab = StudioTab.Parts;
        var index = page.Preview!.Parts.ToList().FindIndex(p => p.Attachment == Attachments + "/BPC_Car_Door_FrontLeft");
        await page.StudioPartsCompletion;
        page.OnStudioPick(index);
        Assert.True(page.CanEditPart);
        Assert.Equal(Loc.F("Studio.Parts.Model", "SK_Door"), page.StudioPartModel); // the clone's painted copy is the game's door model
        Assert.Empty(page.StudioPartOverrides);
        Assert.True(page.SwapPartMesh("/Game/T/SK_Hood.SK_Hood"));
        await page.StudioPartsCompletion;
        var shown = page.Preview!.Parts.ToList().FindIndex(p => p.Attachment == Attachments + "/BPC_New_Door_FrontLeft");
        Assert.True(shown >= 0);
        Assert.Equal(FQuat.Identity, page.Preview!.Parts[shown].Transform.Rotation); // not a wheel: not turned
        Assert.True(page.StudioPartOverrides[index].Hidden);

        // A drag of the door moves its new model (the game's stays hidden); the step moves both.
        Assert.True(page.MovePart(index, new FTransform(new FVector(10, 0, 0)), commit: false));
        Assert.Equal(10f, page.StudioPartOverrides[shown].Delta.Translation.X, 1e-3f);
        Assert.True(page.StudioPartOverrides[index].Hidden);
        Assert.True(page.MovePart(index, new FTransform(new FVector(10, 0, 0)), commit: true));
        await page.StudioPartsCompletion;
        Assert.Equal(10f, page.StudioPartOverrides[shown].Delta.Translation.X, 1e-3f);
        Assert.True(page.StudioPartOverrides[index].Hidden);
    }

    /// <summary>The synthetic car (body, door, wheel parts), its clone "New" with its own mesh, and two models to choose.</summary>
    private static async Task<CloneAssetOp> WriteCarAsync(LevelTempDirectory temp)
    {
        async Task Write(string package, PackageBytes bytes) => await bytes.WriteAsync(temp.Combine(["SCUM", "Content", .. package["/Game/".Length..].Split('/')]));
        await Write(Folder + "/BPC_Car", Vehicle());
        await Write(MeshPath, Mesh(withSocket: false));
        await Write(SkeletonPath, Skeleton());
        await Write(Attachments + "/BPC_Car_Body_Top", Part("Body_Top", "s_Body_Top", "/Game/T/SK_Top.SK_Top", [Class("BPC_Car_RoofRack")], [Class("BPC_Car_Door_FrontLeft")]));
        await Write(Attachments + "/BPC_Car_RoofRack", Part("RoofRack", "s_Body_Top", "/Game/T/SM_Rack.SM_Rack"));
        await Write(Attachments + "/BPC_Car_Door_FrontLeft", Part("Door_FrontLeft", "s_Door_FrontLeft", "/Game/T/SK_Door.SK_Door"));
        await Write(StockWheel, Part("Wheel_FrontLeft", "b_Wheel_FrontLeft", GameWheel));
        await Write(PresetPath, Preset("Body_Top", "RoofRack", "Door_FrontLeft"));
        foreach (var (mesh, cls) in new[] { ("SM_Wheel", "StaticMesh"), ("SM_Zil", "StaticMesh"), ("SM_Odd", "StaticMesh"), ("SK_Door", "SkeletalMesh"), ("SK_Hood", "SkeletalMesh") })
        {
            var p = new SyntheticLevelPackage();
            p.SetPayload(p.Export(mesh, p.ScriptClass(Engine, cls), 0, flags: 0x3), p.Properties());
            await Write("/Game/T/" + mesh, p.Build());
        }

        var pairs = new[] { "BPC_Car_Body_Top", "BPC_Car_RoofRack", "BPC_Car_Door_FrontLeft", "BPC_Car_Wheel_FrontLeft" }
            .Select(l => new PackagePair($"{Attachments}/{l}", $"{Attachments}/{l.Replace("Car", "New", StringComparison.Ordinal)}"))
            .Prepend(new PackagePair(MeshPath, NewMesh))
            .Prepend(new PackagePair("/Game/T/SK_Door", "/Game/T/SK_NewDoor")) // the door wears paint: the clone has its own copy of its mesh
            .Prepend(new PackagePair(Folder + "/BPC_Car", Folder + "/BPC_New"))
            .Append(new PackagePair(PresetPath, PresetPath.Replace("CarManual", "NewManual", StringComparison.Ordinal)))
            .ToList();
        return new CloneAssetOp("vehicle", Folder + "/BPC_Car", Folder + "/BPC_New", pairs);
    }

    /// <summary>The car as the 3D view gets it: the body, the front-left door on its socket, the wheel on its bone.</summary>
    private static PreviewModel Car() => new("BPC_Car",
    [
        new PreviewPart("SK_Car", Box("SK_Car", new BoundingBox(new Vector3(-250, -100, 0), new Vector3(250, 100, 120))).Parts[0].Mesh, null, FTransform.Identity),
        new PreviewPart("SK_Door", Box("SK_Door", new BoundingBox(new Vector3(-50, -5, 0), new Vector3(50, 5, 100))).Parts[0].Mesh, null, new FTransform(new FVector(87.7f, -86.9f, 49.7f)))
        {
            Attachment = Attachments + "/BPC_Car_Door_FrontLeft",
        },
        new PreviewPart("SM_Wheel", Box("SM_Wheel", RagerWheel).Parts[0].Mesh, null, WheelBone) { Attachment = StockWheel },
    ], new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>());

    /// <summary>A model that is a box of <paramref name="bounds"/> (its eight corners, twelve triangles).</summary>
    private static PreviewModel Box(string name, BoundingBox bounds)
    {
        var positions = new float[24];
        for (var i = 0; i < 8; i++)
        {
            positions[i * 3] = (i & 1) == 0 ? bounds.Min.X : bounds.Max.X;
            positions[i * 3 + 1] = (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y;
            positions[i * 3 + 2] = (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z;
        }

        uint[] indices = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];
        return new PreviewModel(name, [new PreviewPart(name, MeshData.Create(name, positions, indices), null, FTransform.Identity)], new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>());
    }

    private static float[] Transformed(PreviewPart part)
    {
        var result = new float[part.Mesh.Positions.Length];
        for (var i = 0; i < result.Length; i += 3)
        {
            var p = part.Transform.TransformPosition(new FVector(part.Mesh.Positions[i], part.Mesh.Positions[i + 1], part.Mesh.Positions[i + 2]));
            (result[i], result[i + 1], result[i + 2]) = (p.X, p.Y, p.Z);
        }

        return result;
    }

    private static PackageBytes Vehicle()
    {
        var p = new SyntheticLevelPackage();
        var mesh = p.ObjectImport(MeshPath, Engine, "SkeletalMesh", "SK_Car");
        var cls = p.ClassExport("BPC_Car_C", p.ScriptClass(Engine, "BlueprintGeneratedClass"), p.ScriptClass("/Script/SCUM", "Vehicle"));
        var cdo = p.Export("Default__BPC_Car_C", p.ScriptClass(Engine, "Actor"), 0, flags: 0x39);
        p.SetPayload(cls, p.Properties());
        p.SetPayload(cdo, p.Properties(t => t.Object("SkeletalMesh", mesh)));
        return p.Build();
    }
}
