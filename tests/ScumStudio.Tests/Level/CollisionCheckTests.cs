using ScumStudio.App.Services;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The collision check (owner: "if something has no collision the program should find it and replace it itself, for
/// everyone"): the game's log lines, the pieces they name, the export-time check, and the next export making them straight.
/// </summary>
public sealed class CollisionCheckTests
{
    private const string Bridge = "/Game/ConZ_Files/Maps/The_Island/A_0_Dr_Tudman_Bridge";

    // As SCUM wrote them on the owner's PC (client log, single player), plus lines that are not about collision.
    private static readonly string[] Log =
    [
        "[2026.10.02-19.42.26:663][816]LogNet: Browse: /Game/ConZ_Files/Maps/The_Island/The_Island?profile=5",
        "[2026.10.02-19.42.30:661][816]LogStaticMesh: Warning: UStaticMesh::GetPhysicsTriMeshData: Triangle data from 'StaticMesh /Game/ConZ_Files/Models/Road/DrTudmanBridge/DrT_B_Meshes/SM_Tudman_Bridge_01.SM_Tudman_Bridge_01' cannot be accessed at runtime on a mesh without CPU access",
        "[2026.10.02-19.42.30:661][816]LogPhysics: Warning: UBodySetup::GetCookInfo: Triangle data from '/Game/ConZ_Files/Maps/The_Island/A_0_Dr_Tudman_Bridge.A_0_Dr_Tudman_Bridge:PersistentLevel.SM_DrTudmanBridge_0_Copy2.SplineMeshComponent0' invalid (0 verts, 0 indices).",
        "[2026.10.02-19.42.30:662][816]LogPhysics: Warning: UBodySetup::GetCookInfo: Triangle data from '/Game/ConZ_Files/Maps/The_Island/A_0_Dr_Tudman_Bridge.A_0_Dr_Tudman_Bridge:PersistentLevel.SM_DrTudmanBridge_0_Copy24_3.SplineMeshComponent0' invalid (0 verts, 0 indices).",
        "[2026.10.02-19.42.30:662][816]LogPhysics: Warning: UBodySetup::GetCookInfo: Triangle data from '/Game/ConZ_Files/Maps/The_Island/A_0_Dr_Tudman_Bridge.A_0_Dr_Tudman_Bridge:PersistentLevel.ConcreteBlock002_1_7_Bent.SplineMeshComponent0' invalid (0 verts, 0 indices).",
        "[2026.10.02-19.42.30:662][816]LogPhysics: Warning: UBodySetup::GetCookInfo: Triangle data from '/Game/ConZ_Files/Maps/The_Island/A_0_Dr_Tudman_Bridge.A_0_Dr_Tudman_Bridge:PersistentLevel.SM_DrTudmanBridge_0_Copy2.SplineMeshComponent0' invalid (0 verts, 0 indices).",
    ];

    private static readonly string[] Shapes = ["SM_DrTudmanBridge_0_Copy2", "SM_DrTudmanBridge_0_Copy24", "ConcreteBlock002_1_7", "SM_Other"];

    private static EditState Shaped()
    {
        var state = new EditState();
        foreach (var actor in Shapes)
        {
            state.Apply(new BendActorOp(new ActorRef(Bridge, actor), 0f, 20f));
        }

        return state;
    }

    [Fact]
    public void TheGameLogNamesThePiecesWithoutCollision()
    {
        var failures = CollisionCheck.FromGameLog(Log, new DateTime(2026, 10, 2, 19, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new[] { "SM_DrTudmanBridge_0_Copy2", "SM_DrTudmanBridge_0_Copy24_3", "ConcreteBlock002_1_7_Bent" }, failures.Select(f => f.Actor));
        Assert.All(failures, f => Assert.Equal(Bridge, f.Level));
        Assert.Empty(CollisionCheck.FromGameLog(Log, new DateTime(2026, 10, 2, 20, 0, 0, DateTimeKind.Utc))); // before the last export: not this export's doing

        // Back to the project's actors: a repeated piece's number and a re-added level actor's _Bent come off.
        var state = Shaped();
        Assert.Equal(new[] { "SM_DrTudmanBridge_0_Copy2", "SM_DrTudmanBridge_0_Copy24", "ConcreteBlock002_1_7" }, failures.Select(f => CollisionCheck.ActorOf(f, state)!.Actor));
    }

    [Fact]
    public void AMarkedPieceIsExportedStraightAndAPieceWithoutItsMeshGuidIsCaught()
    {
        var state = Shaped();
        var bounds = new BoundingBox(new System.Numerics.Vector3(0, -400, -50), new System.Numerics.Vector3(1000, 400, 0));
        var mesh = new BendMesh(bounds, null, null, new FGuid(1, 2, 3, 4));
        var warnings = new List<string>();
        ProjectExporter.PlanLevel(state, Bridge, null, warnings, null, null, _ => mesh, [new ActorRef(Bridge, "SM_Other")]);

        // Not shaped in the game any more: the actor is not re-added as a spline piece, and the report says why.
        Assert.Contains(warnings, w => w.Contains("SM_Other", StringComparison.Ordinal) && w.Contains("straight", StringComparison.Ordinal));

        // The export-time check: a piece written without its mesh's body guid, or without boxes, is caught.
        var synthetic = SyntheticLevels.BuildLevel();
        var package = CookedPackage.Parse(synthetic.UAsset, synthetic.UExp);
        var spline = BendShape.For(bounds, FVector.One, 30f);
        var boxes = PieceCollision.Bend([new ScumStudio.Assets.Meshes.CollisionBox(new FVector(500, 0, -25), FRotator.Zero, new FVector(1000, 800, 50))], spline, bounds);
        var (bytes, _) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            StaticMeshAdds =
            [
                new StaticMeshActorAdd("Good_Bent", SyntheticLevels.RockPackage + ".SM_Rock", TransformValue.Identity, spline, boxes, new FGuid(1, 2, 3, 4)),
                new StaticMeshActorAdd("NoGuid_Bent", SyntheticLevels.RockPackage + ".SM_Rock", TransformValue.Identity, spline, boxes),
                new StaticMeshActorAdd("NoBoxes_Bent", SyntheticLevels.RockPackage + ".SM_Rock", TransformValue.Identity, spline),
            ],
        });
        var problems = CollisionCheck.Verify(CookedPackage.Parse(bytes.UAsset, bytes.UExp), ["Good_Bent", "NoGuid_Bent", "NoBoxes_Bent"]);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("NoGuid_Bent", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("NoBoxes_Bent", StringComparison.Ordinal));
    }

    [Fact]
    public void AfterPlayingTheNextExportReplacesWhatTheGameLeftWithoutCollision()
    {
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Bridge.ssproj"), "Bridge");
        foreach (var actor in Shapes)
        {
            project.Apply(new BendActorOp(new ActorRef(Bridge, actor), 0f, 20f));
        }

        var log = temp.Combine("SCUM.log");
        File.WriteAllLines(log, Log);

        // Never exported by this version: an old log says nothing about it.
        Assert.Empty(CollisionDoctor.Check(project, [log]).Found);

        // Exported, then played: the game's lines after the export mark the pieces, and the marks stay for later exports.
        CollisionDoctor.Exported(project);
        var stamp = DateTime.UtcNow.AddMinutes(1).ToString("yyyy.MM.dd-HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllLines(log, Log.Select(l => "[" + stamp + l[20..]));
        var (straight, found) = CollisionDoctor.Check(project, [log]);
        Assert.Equal(3, found.Count);
        Assert.Equal(3, straight.Count);
        Assert.Equal(3, CollisionDoctor.Check(project, []).Straight.Count);

        // A piece shaped again is tried bent again.
        var copy2 = new ActorRef(Bridge, "SM_DrTudmanBridge_0_Copy2");
        project.Apply(new BendActorOp(copy2, 20f, 35f));
        Assert.DoesNotContain(CollisionDoctor.Check(project, []).Straight, a => ActorRef.Comparer.Equals(a, copy2));
    }
}
