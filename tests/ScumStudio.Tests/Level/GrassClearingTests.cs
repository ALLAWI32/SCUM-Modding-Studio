using System.Buffers.Binary;
using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;

namespace ScumStudio.Tests.Level;

/// <summary>"Clear grass under it": the footprint, the grass-sample and the cooked-layout math (see <see cref="GrassClearing"/>).</summary>
public sealed class GrassClearingTests
{
    private static readonly BoundingBox Slab = new(new Vector3(-200, -50, 0), new Vector3(200, 50, 20));

    [Fact]
    public void AFootprintIsTheMeshOutlineTurnedAndGrownByTheMargin()
    {
        // A 4 x 1 m slab turned 90 degrees at (1000, 0): 1 m along X, 4 m along Y.
        var c = new ComponentRecord(1, "Slab", "StaticMeshComponent", true, null, FTransform.Identity,
            new FTransform(new FRotator(0, 90, 0), new FVector(1000, 0, 0), FVector.One), "/Game/Slab.Slab", []);
        var actor = new ActorRecord(0, "StaticMeshActor_1", "/Script/Engine.StaticMeshActor", 1, [c], c.WorldTransform, ActorKind.StaticMeshActor, "/Game/Slab.Slab", []);
        var polygons = GrassClearing.Footprints(actor, _ => Slab, margin: 10f);

        var p = Assert.Single(polygons);
        Assert.True(GrassClearing.Covers(polygons, new Vector2(1000, 195)));
        Assert.True(GrassClearing.Covers(polygons, new Vector2(1055, -205))); // the margin's corner
        Assert.False(GrassClearing.Covers(polygons, new Vector2(1065, 0)));
        Assert.False(GrassClearing.Covers(polygons, new Vector2(1000, 215)));
        Assert.Equal(4, p.Length); // an upright box grown by a square stays a box
        Assert.Empty(GrassClearing.Footprints(actor, _ => null));
    }

    [Fact]
    public void ABentPieceGetsItsBentOutlineNotItsBox()
    {
        // A quarter circle of radius 1000 from (1000, 0) to (0, 1000): the box corner (1000, 1000) is not under it.
        var spline = new SplineMeshParams
        {
            StartPos = new FVector(1000, 0, 0), StartTangent = new FVector(0, 1571, 0),
            EndPos = new FVector(0, 1000, 0), EndTangent = new FVector(-1571, 0, 0),
        };
        var c = new ComponentRecord(1, "Road", "SplineMeshComponent", true, null, FTransform.Identity, FTransform.Identity, "/Game/Road.Road", []) { SplineMesh = spline };
        var actor = new ActorRecord(0, "Road_1", "/Script/Engine.StaticMeshActor", 1, [c], FTransform.Identity, ActorKind.StaticMeshActor, "/Game/Road.Road", []);
        var polygons = GrassClearing.Footprints(actor, _ => new BoundingBox(new Vector3(0, -100, 0), new Vector3(1000, 100, 10)), margin: 0f);

        Assert.Equal(16, polygons.Count);
        Assert.True(GrassClearing.Covers(polygons, new Vector2(707, 707)));
        Assert.True(GrassClearing.Covers(polygons, new Vector2(1050, 50)));
        Assert.False(GrassClearing.Covers(polygons, new Vector2(900, 900)));
        Assert.False(GrassClearing.Covers(polygons, new Vector2(500, 500)));
    }

    [Fact]
    public void TheClearedSamplesAreTheOnesWhoseQuadsTouchTheFootprintSoNoGrassGrowsInside()
    {
        const int stride = 8;
        Vector2[] square = [new(2.2f, 2.2f), new(2.8f, 2.2f), new(2.8f, 2.8f), new(2.2f, 2.8f)];
        var mask = GrassClearing.ClearedSamples(stride, [square]);
        Assert.Equal([(2, 2), (3, 2), (2, 3), (3, 3)], Enumerable.Range(0, mask.Length).Where(i => mask[i]).Select(i => (i % stride, i / stride)));

        // A turned slab over several quads: the game's bilinear grass weight is 0 everywhere inside, and samples two quads
        // away keep theirs.
        var slab = GrassClearing.Hull([new(1.3f, 3f), new(4f, 0.6f), new(6.1f, 3f), new(3.4f, 5.4f)]);
        mask = GrassClearing.ClearedSamples(stride, [slab]);
        var weights = mask.Select(m => m ? 0f : 1f).ToArray();
        var random = new Random(7);
        for (var n = 0; n < 2000; n++)
        {
            var p = new Vector2((float)random.NextDouble() * (stride - 1), (float)random.NextDouble() * (stride - 1));
            if (GrassClearing.Contains(slab, p))
            {
                Assert.Equal(0f, Bilinear(weights, stride, p));
            }
        }

        var grown = GrassClearing.Grow(slab, 2f);
        for (var i = 0; i < mask.Length; i++)
        {
            Assert.True(!mask[i] || GrassClearing.Contains(grown, new Vector2(i % stride, i / stride)), $"sample {i} is far from the slab");
        }
    }

    [Fact]
    public void TheGrassMapsAreFoundInTheCookedLayoutAndOnlyTheyAreZeroed()
    {
        const int stride = 3;
        var payload = new List<byte>(new byte[10]); // stand-in for the tagged properties
        void Int(int v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            payload.AddRange(b);
        }

        Int(0); // no object guid
        Int(2);
        Int(stride * stride);
        payload.AddRange(Enumerable.Repeat((byte)0x77, stride * stride * 2));
        Int(2);
        Int(-5);
        Int(stride * stride);
        var first = payload.Count;
        payload.AddRange(Enumerable.Repeat((byte)200, stride * stride));
        Int(-6);
        Int(stride * stride);
        payload.AddRange(Enumerable.Repeat((byte)100, stride * stride));
        Int(1); // bCooked
        Int(0);
        var bytes = payload.ToArray();
        var original = (byte[])bytes.Clone();

        var maps = GrassClearing.ReadGrassMaps(bytes, 10, stride);
        Assert.NotNull(maps);
        Assert.Equal([new GrassClearing.GrassMap(-5, first, 9), new GrassClearing.GrassMap(-6, first + 17, 9)], maps);
        var mask = new bool[9];
        mask[4] = mask[5] = true;
        Assert.Equal(4, GrassClearing.Clear(bytes, maps, mask));
        var changed = Enumerable.Range(0, bytes.Length).Where(i => bytes[i] != original[i]).ToList();
        Assert.Equal([first + 4, first + 5, first + 21, first + 22], changed);
        Assert.All(changed, i => Assert.Equal(0, bytes[i]));

        Assert.Null(GrassClearing.ReadGrassMaps(bytes, 10, 4)); // other component size
        Assert.Null(GrassClearing.ReadGrassMaps(bytes, 14, stride)); // not at the end of the properties
    }

    [Fact]
    public void BushesAndGrassGoTreesAndRocksStay()
    {
        static ComponentRecord Foliage(string cls, string mesh) =>
            new(1, "F", cls, true, null, FTransform.Identity, FTransform.Identity, mesh, [FTransform.Identity]) { IsInstanced = true };
        Assert.True(GrassClearing.IsClearedFoliage(Foliage("FoliageInstancedBush", "/Game/ConZ_Files/Foliage/Mediterranean/Bush/Acacia_01/Acacia_01.Acacia_01")));
        Assert.True(GrassClearing.IsClearedFoliage(Foliage("FoliageInstancedGrass", "/Game/X/Lavender_01.Lavender_01")));
        Assert.True(GrassClearing.IsClearedFoliage(Foliage("FoliageInstancedStaticMeshComponent", "/Game/ConZ_Files/Foliage/Mediterranean/Bush/Cactus/SM_Cactus_01.SM_Cactus_01")));
        Assert.False(GrassClearing.IsClearedFoliage(Foliage("FoliageInstancedTree", "/Game/ConZ_Files/Foliage/Continental/Trees/Scots_Pine/V2/ScotsPine_01.ScotsPine_01")));
        Assert.False(GrassClearing.IsClearedFoliage(Foliage("FoliageInstancedStaticMeshComponent", "/Game/ConZ_Files/Landscape/Rocks/Coastal_Rock_08/Coastal_Rock_08b.Coastal_Rock_08b")));
        Assert.False(GrassClearing.IsClearedFoliage(Foliage("InstancedStaticMeshComponent", "/Game/ConZ_Files/Foliage/Bush/X.X")));
    }

    [Fact]
    public void TheSettingIsOnForAddedObjectsOffForMovedOnesJournaledAndUndone()
    {
        var state = new EditState();
        var added = new AddStaticMeshActorOp("/Game/L", "Bridge_1", "/Game/Bridge.Bridge", TransformValue.Identity);
        var stock = new ActorRef("/Game/L", "StaticMeshActor_3");
        state.Apply(added);
        Assert.True(state.ClearsGrass(added.Created));
        Assert.False(state.ClearsGrass(stock));

        var off = new SetClearGrassOp(added.Created, null, false);
        var on = new SetClearGrassOp(stock, null, true);
        state.Apply(off);
        state.Apply(on);
        Assert.False(state.ClearsGrass(added.Created));
        Assert.True(state.ClearsGrass(stock));
        Assert.NotNull(state.Validate(off)); // out of date now
        Assert.Equal(off, EditOp.FromJson(off.ToJson()));

        state.Apply(on.Inverse());
        state.Apply(off.Inverse());
        Assert.Null(state.GetClearGrass(added.Created));
        Assert.True(state.ClearsGrass(added.Created));
        Assert.False(state.ClearsGrass(stock));
        Assert.True(state.Clone().ClearsGrass(added.Created));
    }

    private static float Bilinear(float[] w, int stride, Vector2 p)
    {
        var (x1, y1) = ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));
        var (x2, y2) = (Math.Min(x1 + 1, stride - 1), Math.Min(y1 + 1, stride - 1));
        var (fx, fy) = (p.X - x1, p.Y - y1);
        float At(int x, int y) => w[(y * stride) + x];
        return float.Lerp(float.Lerp(At(x1, y1), At(x2, y1), fx), float.Lerp(At(x1, y2), At(x2, y2), fx), fy);
    }
}
