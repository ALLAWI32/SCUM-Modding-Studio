using System.Numerics;
using CUE4Parse.UE4.Objects.PhysicsEngine;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// <see cref="PieceCollision"/> and its BodySetup (owner: "the new bridge has no collision, the car falls through; make
/// sure everything has collision, exactly at the object"): boxes from the mesh, bent with the piece, written so the game
/// (and CUE4Parse, which reads the engine's layout) reads them back.
/// </summary>
public sealed class PieceCollisionTests
{
    // A closed box mesh (12 triangles) from min to max.
    private static (List<float> P, List<uint> I) Box(Vector3 min, Vector3 max, List<float>? p = null, List<uint>? i = null)
    {
        p ??= [];
        i ??= [];
        var first = (uint)(p.Count / 3);
        for (var k = 0; k < 8; k++)
        {
            p.AddRange([(k & 1) == 0 ? min.X : max.X, (k & 2) == 0 ? min.Y : max.Y, (k & 4) == 0 ? min.Z : max.Z]);
        }

        uint[] faces = [0, 1, 3, 0, 3, 2, 4, 7, 5, 4, 6, 7, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];
        i.AddRange(faces.Select(f => f + first));
        return (p, i);
    }

    private static MeshData Mesh((List<float> P, List<uint> I) m) => MeshData.Create("m", [.. m.P], [.. m.I]);

    [Fact]
    public void ADeckWithAPostAndACableBecomesBoxesWhereTheyAre()
    {
        // A 10 m x 8 m deck 50 cm thick, its surface at 0; a post standing on it; a cable 6 m above it.
        var m = Box(new Vector3(0, -400, -50), new Vector3(1000, 400, 0));
        Box(new Vector3(480, 360, 0), new Vector3(520, 400, 100), m.P, m.I);
        Box(new Vector3(0, -20, 600), new Vector3(1000, 20, 610), m.P, m.I);
        var boxes = PieceCollision.Columns(Mesh(m));

        // The deck: one wide flat slab, its top at the surface.
        var deck = boxes.Where(b => b.Center.Z < 0f).ToList();
        Assert.True(deck.Sum(b => b.Size.X * b.Size.Y) > 1000f * 800f * 0.95f, "the deck is covered");
        Assert.All(deck, b => Assert.Equal(0f, b.Center.Z + (b.Size.Z * 0.5f), 1f));

        // The post stands up from the deck where it is; the cable is a thin box high above, nothing in between.
        var post = Assert.Single(boxes, b => b.Center.Z + (b.Size.Z * 0.5f) is > 50f and < 300f);
        Assert.Equal(100f, post.Center.Z + (post.Size.Z * 0.5f), 1f);
        Assert.InRange(post.Center.X, 470f, 530f);
        var cable = boxes.Where(b => b.Center.Z > 300f).ToList();
        Assert.NotEmpty(cable);
        Assert.All(cable, b => Assert.InRange(b.Size.Z, 5f, 15f));
        Assert.DoesNotContain(boxes, b => b.Center.Z - (b.Size.Z * 0.5f) < 300f && b.Center.Z + (b.Size.Z * 0.5f) > 300f);
    }

    [Fact]
    public void ARoadSheetUnderAPylonLeavesTheWayOpen()
    {
        // Owner: "the pylon is like a hidden wall, the car can't pass". A deck, its road surface a sheet of its own 2 cm over
        // it (one-sided, looking up), and a pylon's crossbeam 5 m over the road: counted in pairs, the sheet and the beam's
        // underside made one wall between them.
        var m = Box(new Vector3(0, -400, -50), new Vector3(1000, 400, 0));
        var first = (uint)(m.P.Count / 3);
        m.P.AddRange([0, -400, 2, 1000, -400, 2, 0, 400, 2, 1000, 400, 2]);
        m.I.AddRange(new uint[] { 0, 3, 1, 0, 2, 3 }.Select(i => i + first)); // clockwise from above: it looks up
        Box(new Vector3(450, -400, 500), new Vector3(550, 400, 540), m.P, m.I);
        var boxes = PieceCollision.Columns(Mesh(m));

        Assert.DoesNotContain(boxes, b => b.Center.Z - (b.Size.Z * 0.5f) < 400f && b.Center.Z + (b.Size.Z * 0.5f) > 100f);
        Assert.All(boxes.Where(b => b.Center.Z < 100f), b => Assert.Equal(2f, b.Center.Z + (b.Size.Z * 0.5f), 1f)); // the road's top
        Assert.Contains(boxes, b => b.Center.Z is > 500f and < 540f); // the beam stays solid
    }

    [Fact]
    public void BoxesBendWithThePiece()
    {
        var deck = new BoundingBox(new Vector3(0, -400, -50), new Vector3(1000, 400, 0));
        var boxes = new List<CollisionBox> { new(new FVector(500, 0, -25), FRotator.Zero, new FVector(1000, 800, 50)) };

        // Straight: the same slab, one box.
        var straight = Assert.Single(PieceCollision.Bend(boxes, BendShape.For(deck, FVector.One, 0f), deck));
        Assert.Equal(1000f, straight.Size.X, 1f);
        Assert.Equal(800f, straight.Size.Y, 1f);
        Assert.True(FVector.Distance(straight.Center, new FVector(500, 0, -25)) < 0.5f);

        // Bent a quarter turn: a piece every 4 degrees at most, turned along the curve.
        var spline = BendShape.For(deck, FVector.One, 90f);
        var bent = PieceCollision.Bend(boxes, spline, deck);
        Assert.InRange(bent.Count, 23, 32);
        Assert.True(MathF.Abs(bent[^1].Rotation.Yaw - bent[0].Rotation.Yaw) > 60f, "the pieces turn along the curve");
        Level(spline, deck, bent);

        // A hump (both push handles raised 3 m): the same level road where the game draws it.
        var hump = SplineEnds.Shape(BendShape.For(deck, FVector.One, 0f), 0f, 0f, new SplineEnd(Lift: 300f), new SplineEnd(Lift: 300f));
        Level(hump, deck, PieceCollision.Bend(boxes, hump, deck));

        // A 30 m road pushed into an S that climbs 3 m (a curve that climbs twists across: its edges are cut finer).
        var road = new BoundingBox(new Vector3(0, -400, -50), new Vector3(3000, 400, 0));
        var s = SplineEnds.Shape(BendShape.For(road, FVector.One, 0f), 300f, -300f, default, new SplineEnd(Lift: 300f));
        Level(s, road, PieceCollision.Bend([new CollisionBox(new FVector(1500, 0, -25), FRotator.Zero, new FVector(3000, 800, 50))], s, road));
    }

    // Owner: "the car's wheel drops then comes back; the road should be level". Across and along the deck as the game
    // draws it: 1.5 cm under the surface is always inside a box (a wheel's ray finds no gap), 1.5 cm over it never is.
    private static void Level(SplineMeshParams spline, BoundingBox deck, List<CollisionBox> bent)
    {
        for (var i = 1; i < 400; i++)
        {
            var x = i * deck.Max.X / 400f;
            var slice = SplineMeshDeformer.CalcSliceTransform(spline, deck, x);
            foreach (var y in new[] { -399f, -200f, 0f, 200f, 399f })
            {
                var under = slice.TransformPosition(new FVector(0, y, -1.5f));
                var over = slice.TransformPosition(new FVector(0, y, 1.5f));
                Assert.True(bent.Any(b => Inside(b, under)), $"a gap under the road at x {x}, y {y}");
                Assert.False(bent.Any(b => Inside(b, over)), $"a bump over the road at x {x}, y {y}");
            }
        }

        static bool Inside(CollisionBox box, FVector point)
        {
            var local = box.Rotation.Quaternion().UnrotateVector(point - box.Center);
            return MathF.Abs(local.X) <= box.Size.X * 0.5f && MathF.Abs(local.Y) <= box.Size.Y * 0.5f && MathF.Abs(local.Z) <= box.Size.Z * 0.5f;
        }
    }

    [Fact]
    public async Task ABentPiecesBodySetupReadsBackInTheEnginesLayout()
    {
        var synthetic = SyntheticLevels.BuildLevel();
        var package = CookedPackage.Parse(synthetic.UAsset, synthetic.UExp);
        var at = new TransformValue(new FVector(5, 6, 7), new FRotator(0, 30, 0), FVector.One);
        var bounds = new BoundingBox(new Vector3(0, -400, -50), new Vector3(1000, 400, 0));
        var spline = BendShape.For(bounds, FVector.One, 45f);
        var collision = PieceCollision.Bend([new CollisionBox(new FVector(500, 0, -25), FRotator.Zero, new FVector(1000, 800, 50))], spline, bounds);
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            StaticMeshAdds = [new StaticMeshActorAdd("Deck_Bent", SyntheticLevels.RockPackage + ".SM_Rock", at, spline, collision, new ScumStudio.Formats.FGuid(1, 2, 3, 4))],
        });
        Assert.Empty(report.Warnings);

        // Our reader: the body is a subobject of the component, which points at it.
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var component = Enumerable.Range(0, edited.Exports.Count).Single(i => edited.ResolveName(edited.Exports[i].ObjectName) == "SplineMeshComponent0"
            && edited.ResolveIndex(edited.Exports[i].OuterIndex).EndsWith("Deck_Bent", StringComparison.Ordinal));
        var body = Enumerable.Range(0, edited.Exports.Count).Single(i => edited.GetExportClassName(i) == "BodySetup" && edited.Exports[i].OuterIndex == component + 1);
        Assert.Equal("BodySetup_0", edited.ResolveName(edited.Exports[body].ObjectName));
        var props = edited.ReadProperties(component);
        Assert.NotNull(props.Find("BodySetup"));
        Assert.NotNull(props.Find("bUseDefaultCollision")); // collides like the placed mesh (its profile, wheels included)
        Assert.NotNull(props.Find("CachedMeshBodySetupGuid")); // the mesh's body guid: the game keeps our boxes instead of rebuilding

        // CUE4Parse reads the engine's UBodySetup layout (tagged properties, guid, bCooked): the boxes come back.
        using var temp = new LevelTempDirectory();
        var basePath = temp.Combine("SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "A_0_TestLevel");
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        await bytes.WriteAsync(basePath, ".umap");
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var setup = catalog.LoadPackage(SyntheticLevels.LevelPath).GetExports().OfType<UBodySetup>().Single();
        Assert.Equal(collision.Count, setup.AggGeom!.BoxElems.Length);
        Assert.Equal(collision[0].Size.X, setup.AggGeom.BoxElems[0].X, 0.01f);
        Assert.Equal(collision[0].Center.Y, setup.AggGeom.BoxElems[0].Center.Y, 0.01f);
        Assert.False(setup.BodySetupGuid.A == 0 && setup.BodySetupGuid.B == 0);
    }
}
