using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.PhysicsEngine;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// "I drew a bridge with the studio and drove over it; I got too close to the edge and fell through. The bridge railing
/// has no collision" (Discord Blondiiile, 2026-10-16). Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class BridgeRailingRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Host = MapSlice.MapsPath + "Z_4_Road_Work_01";
    private const string RiverBridge = "/Game/ConZ_Files/Models/Road/RiverBridge/SM_RiverBridge_02.SM_RiverBridge_02";

    /// <summary>A road bridge drawn twice as long, straight: both railings block along the whole length, the deck is where it was.</summary>
    [Fact]
    public Task ADrawnStraightBridgeBlocksAtBothRailings() => Drawn(0f);

    /// <summary>The same bridge drawn bent 60 degrees: the railing boxes follow the curve.</summary>
    [Fact]
    public Task ADrawnBentBridgeBlocksAtBothRailings() => Drawn(60f);

    /// <summary>
    /// Draws <see cref="RiverBridge"/> as the Map does (Add object, then the Shape end handle dragged on to twice its length,
    /// bent <paramref name="degrees"/>), exports the client and the server cook, and reads each piece's collision back.
    /// </summary>
    private async Task Drawn(float degrees)
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var meshes = new BendSupport(catalog);
        var info = meshes.Describe(RiverBridge)!;
        Assert.Equal("SCUM_Solid_Wall", info.DefaultProfile); // what the game's own bridge collides as
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Railing.ssproj"), "Railing");
        var host = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Host);
        var at = host.Actors.First(a => a.Kind == ActorKind.StaticMeshActor).WorldTransform.Translation;
        var add = EditOpFactory.AddStaticMeshActor(host, RiverBridge, TransformValue.At(at.X, at.Y, at.Z + 3000f), project.State);
        project.Apply(add);
        project.Apply(new BendActorOp(add.Created, 0f, degrees, NewEnd: new SplineEnd(new FVector(info.Bounds.Size.X, 0f, 0f))));

        var options = new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false, BendMeshes = meshes.Describe };
        var client = await new ProjectExporter().ExportAsync(project, catalog, options);
        var server = await new ProjectExporter().ExportAsync(project, catalog, options with { OutputDirectory = temp.Combine("server") }, ProjectSourceRole.Server);
        foreach (var w in client.Warnings)
        {
            output.WriteLine("warning: " + w);
        }

        var pieces = Pieces(client.StagingDirectory, add.Created.Actor);
        Assert.Equal(2, pieces.Count);
        var mesh = MeshExtractor.ExtractStaticMesh(catalog.LoadObject<UStaticMesh>(RiverBridge), 0);
        var probes = RailingProbe.Probes(mesh);
        var problems = new List<string>();
        foreach (var piece in pieces)
        {
            Assert.True(piece.DefaultCollision, $"{piece.Name} collides as its mesh by default (the bridge's profile and channels)");
            var results = RailingProbe.RunBent(mesh, probes, piece.Boxes, piece.Spline, info.Bounds);
            output.WriteLine($"{piece.Name}: {piece.Boxes.Count} boxes; {RailingProbe.Summary(results)}");
            foreach (var side in new[] { -1, 1 })
            {
                Assert.True(results.Count(r => r.Probe.Side == side && r.Mesh is not null) >= 40, "the mesh has a railing on this side all along");
            }

            var (uncovered, walls) = RailingProbe.Problems(results);
            problems.AddRange(uncovered.Select(r => $"{piece.Name}: railing open at {r.Probe.Along:0} side {r.Probe.Side} height {r.Probe.Height:0}"));
            problems.AddRange(walls.Select(r => $"{piece.Name}: hidden wall at {r.Probe.Along:0} side {r.Probe.Side} height {r.Probe.Height:0}"));

            // The deck: where the mesh has a driving surface, the boxes' top is there (no step, no hole).
            var deck = RailingProbe.Deck(mesh, piece.Boxes, piece.Spline, info.Bounds);
            output.WriteLine($"   deck: {deck.Count} points, rise {deck.Min(d => d.Rise):0.0} .. {deck.Max(d => d.Rise):0.0} cm");
            problems.AddRange(deck.Where(d => MathF.Abs(d.Rise) > 6f).Select(d => $"{piece.Name}: deck at {d.At} is {d.Rise:0.0} cm off"));
        }

        foreach (var p in problems.GroupBy(p => p.Split(' ')[1]).SelectMany(g => g.Take(15)))
        {
            output.WriteLine("  " + p);
        }

        Assert.Empty(problems);

        // The server cook gets the same collision (built from the client's meshes: the server's have no geometry).
        var serverPieces = Pieces(server.StagingDirectory, add.Created.Actor);
        Assert.Equal(pieces.Select(p => p.Name), serverPieces.Select(p => p.Name));
        foreach (var (c, s) in pieces.Zip(serverPieces))
        {
            Assert.Equal(c.Boxes, s.Boxes);
        }
    }

    private sealed record Piece(string Name, SplineMeshParams Spline, IReadOnlyList<CollisionBox> Boxes, bool DefaultCollision);

    /// <summary>The written pieces of <paramref name="actor"/> in the staged host level: their curve and collision as the game reads them.</summary>
    private static List<Piece> Pieces(string staging, string actor)
    {
        using var written = AssetCatalog.OpenLoose(staging);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(written), Host);
        var setups = written.LoadPackage(Host).GetExports().OfType<UBodySetup>().ToList();
        var pieces = new List<Piece>();
        foreach (var a in document.Actors.Where(a => a.Name == actor || a.Name.StartsWith(actor + "_", StringComparison.Ordinal)).OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var component = a.FindComponent("SplineMeshComponent0");
            var setup = setups.Single(s => s.Outer?.Name == "SplineMeshComponent0" && s.Outer.Outer?.Name == a.Name);
            pieces.Add(new Piece(a.Name, component!.SplineMesh!, MeshCollision.Boxes(setup), setup.Outer!.GetOrDefault("bUseDefaultCollision", false)));
        }

        return pieces;
    }
}
