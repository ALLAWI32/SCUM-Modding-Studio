using System.Globalization;
using System.Text;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// cwhedgehog's skybridge on the real island (Discord, 2026-10-19: "all of my bridge segments are contained within a single
/// Actor, the bridge is only visible up close and disappears when viewed from a distance"): a chain of concrete deck slabs
/// from the C_0 nuclear plant 11.5 km east across five cells, laid twice as the Map lays copies: as new meshes in the slabs'
/// level (Extend, Paste with it selected) and as new instances of the slabs' instanced part (Ctrl+D, Paste of an instance).
/// Exported as 3.5.1 did and as now, each slab is read back from the written levels: the level it is in, that level's
/// streaming distance, the cull distance it carries, so the distance up to which its level is loaded and its own settings
/// do not cull it (whether the game also culls it by The_Island's cull distance volume is not checked in the game). Real
/// game files only (<c>SCUM_PAKS</c>; <c>SCUM_SERVER_PAKS</c> adds the dedicated server's cook, which must place and name
/// every slab alike).
/// </summary>
public sealed class FarDrawRealTests
{
    private const string Plant = MapSlice.MapsPath + "C_0_NuclearPlant_04_TurbineBuilding_Surroundings";
    private const string SlabActor = "SM_ConcreteFloorBlock_400x200x15ISMA"; // the plant's instanced deck slabs (100 m cull)
    private const int Segments = 24;
    private const float Step = 50_000f; // 500 m between slabs

    private static readonly Cue4ParseLevelReaderOptions Plain = new() { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = true };

    private static AssetCatalog Open(string paks) => AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [PaksFact]
    public async Task ASkybridgeAcrossTheIslandIsLoadedFromAKilometreInsteadOfAHundredMetres()
    {
        using var catalog = Open(RealPaks.Folder!);
        using var temp = new LevelTempDirectory();
        var plant = LevelDocument.Load(new Cue4ParseLevelReader(catalog, Plain), Plant);
        var slabs = plant.FindActor(SlabActor)!;
        var part = slabs.Components.First(c => c is { IsInstanced: true, IsSynthesized: false, Instances.Count: > 0 });
        var first = part.Instances[0] * part.WorldTransform;
        var mesh = part.StaticMeshPath!;

        using var project = Project.Create(temp.Combine("Skybridge.ssproj"), "Skybridge");
        var reserved = new HashSet<ActorRef>(ActorRef.Comparer);
        var instances = new HashSet<InstanceRef>(InstanceRef.Comparer);
        var ops = new List<EditOp>();
        for (var i = 1; i <= Segments; i++)
        {
            // 30 m up, one chain as new meshes (Extend) and one, 20 m beside it, as new instances (Ctrl+D).
            var at = first with { Translation = first.Translation + new FVector(i * Step, 0, 3000) };
            ops.Add(EditOpFactory.AddStaticMeshActor(plant, mesh, TransformValue.FromTransform(at), project.State, reserved) with { OverrideMaterials = part.OverrideMaterials });
            var beside = at with { Translation = at.Translation + new FVector(0, 2000, 0) };
            ops.Add(EditOpFactory.AddInstance(plant, slabs, part.Name, TransformValue.FromTransform(beside.GetRelativeTransform(part.WorldTransform)), project.State, instances));
        }

        project.Apply(new BatchOp("Skybridge", ops));

        var before = Measure(await new ProjectExporter { KeepPlacedInTheirLevel = true }.ExportAsync(project, catalog,
            new ExportOptions { OutputDirectory = temp.Combine("before"), WritePak = false }), catalog);
        var after = Measure(await new ProjectExporter().ExportAsync(project, catalog,
            new ExportOptions { OutputDirectory = temp.Combine("after"), WritePak = false }), catalog);
        Write("skybridge-client.txt", before, after);

        // 3.5.1: every slab in the plant's level (100 m streaming), the instances also under their part's 100 m cull; the
        // plant's area stretched 11.5 km over the bridge, so the whole plant loaded wherever the bridge went.
        Assert.Equal(2 * Segments, before.Slabs.Count);
        Assert.All(before.Slabs, s => Assert.Equal(Plant, s.Level));
        Assert.All(before.Slabs, s => Assert.True(s.SureCm <= 10_000f, s.ToString()));
        Assert.True(before.PlantSpanCm > Segments * Step, before.PlantSpanCm.ToString(CultureInfo.InvariantCulture));

        // Now: each slab out of the plant's reach is a mesh in the terrain tile under it (1,000 m, no cull distance); the few
        // still within the plant's 100 m stay as they were, and the plant's area is no longer stretched.
        Assert.Equal(2 * Segments, after.Slabs.Count);
        var moved = after.Slabs.Where(s => s.Level != Plant).ToList();
        Assert.True(moved.Count >= 2 * (Segments - 1), string.Join(Environment.NewLine, after.Slabs));
        Assert.All(moved, s =>
        {
            Assert.Contains("/Landscape_", s.Level, StringComparison.Ordinal);
            Assert.Equal("StaticMeshActor", s.Kind);
            Assert.Equal(0f, s.CullCm);
            Assert.Equal(100_000f, s.SureCm);
        });
        Assert.True(moved.Select(s => s.Level).Distinct().Count() >= 10, string.Join(", ", moved.Select(s => s.Level).Distinct())); // per sector
        Assert.True(after.PlantSpanCm < before.PlantSpanCm / 10f, after.PlantSpanCm.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(moved.Count, after.Result.FarPlaced.Count);

        // The dedicated server's cook places them the same way (players collide with the same slabs).
        if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is { Length: > 0 } serverPaks && Directory.Exists(serverPaks))
        {
            using var server = Open(serverPaks);
            var cooked = Measure(await new ProjectExporter().ExportAsync(project, server,
                new ExportOptions { OutputDirectory = temp.Combine("server"), WritePak = false, ClientCatalog = catalog }, ProjectSourceRole.Server), server);
            Write("skybridge-server.txt", null, cooked);
            Assert.Equal(after.Slabs.Select(s => (s.Level, s.Kind, s.SureCm, s.Name)), cooked.Slabs.Select(s => (s.Level, s.Kind, s.SureCm, s.Name)));
        }
    }

    /// <summary>One deck slab as written: its level, that level's streaming distance, its cull distance and what follows.</summary>
    private sealed record Slab(int Index, FVector At, string Level, string Kind, int StreamingCm, float CullCm, string Name)
    {
        /// <summary>
        /// The distance up to which its level is loaded and its own cull settings keep it (the table's "drawn for sure within";
        /// whether the game also culls by The_Island's cull distance volume is not checked in the game).
        /// </summary>
        public float SureCm => CullCm > 0f ? MathF.Min(CullCm, StreamingCm) : StreamingCm;

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{Index,2} x {At.X / 100f,9:0} m  {Level[(Level.LastIndexOf('/') + 1)..],-52} {Kind,-20} streams {StreamingCm / 100,5} m  cull {(CullCm > 0 ? (CullCm / 100f).ToString("0", CultureInfo.InvariantCulture) + " m" : "none"),6}  drawn for sure within {SureCm / 100f,5:0} m");
    }

    private sealed record Measured(ExportResult Result, IReadOnlyList<Slab> Slabs, float PlantSpanCm);

    /// <summary>Every deck slab of the export, read back from the written levels.</summary>
    private static Measured Measure(ExportResult result, AssetCatalog catalog)
    {
        var slabs = new List<Slab>();
        var plantSpan = 0f;
        foreach (var level in result.Levels)
        {
            var file = Path.Combine([result.StagingDirectory, .. level.VirtualPath.Split('/')]);
            var package = CookedPackage.Load(file);
            var tile = WorldTileInfo.TryRead(package)!;
            if (level.PackagePath == Plant)
            {
                plantSpan = tile.BoundsMax.X - tile.BoundsMin.X;
            }

            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var document = LevelDocument.Load(new Cue4ParseLevelReader(written, Plain), level.PackagePath);
            foreach (var name in level.Report.AddedActors)
            {
                var actor = document.FindActor(name)!;
                var root = actor.Root!;
                slabs.Add(new Slab(0, actor.WorldTransform.Translation, level.PackagePath, actor.ClassName, tile.Layer.StreamingDistance,
                    CullOf(package, name, root.Name), name));
            }

            if (document.FindActor(SlabActor) is { } stock)
            {
                var part = stock.Components.First(c => c is { IsInstanced: true, IsSynthesized: false, Instances.Count: > 0 });
                var stockCount = LevelDocument.Load(new Cue4ParseLevelReader(catalog, Plain), level.PackagePath).FindActor(SlabActor)!.FindComponent(part.Name)!.Instances.Count;
                foreach (var added in part.Instances.Skip(stockCount))
                {
                    slabs.Add(new Slab(0, (added * part.WorldTransform).Translation, level.PackagePath, "instance of " + part.ClassName, tile.Layer.StreamingDistance,
                        CullOf(package, SlabActor, part.Name), SlabActor));
                }
            }
        }

        if (plantSpan == 0f && FarPlacement.TileInfoOf(catalog, Plant) is { } stockTile)
        {
            plantSpan = stockTile.BoundsMax.X - stockTile.BoundsMin.X; // not written: its area as the game has it
        }

        // Numbered west to east (the meshes' chain and the instances' chain side by side).
        var ordered = slabs.OrderBy(s => s.At.X).ThenBy(s => s.At.Y).Select((s, i) => s with { Index = i + 1 }).ToList();
        return new Measured(result, ordered, plantSpan);
    }

    /// <summary>The distance a component is culled at in the game (cm; 0 = never): its cached max draw distance, or an instance end cull distance.</summary>
    private static float CullOf(CookedPackage package, string actor, string component)
    {
        for (var i = 0; i < package.Exports.Count; i++)
        {
            var outer = package.Exports[i].OuterIndex;
            if (outer <= 0 || package.GetObjectName(i + 1) != component || package.GetObjectName(outer) != actor)
            {
                continue;
            }

            var tags = package.ReadProperties(i).Properties;
            float F(string name) => tags.FirstOrDefault(t => t.Name == name)?.Value switch
            {
                FloatValue f => f.Value,
                IntValue n => n.Value,
                _ => 0f,
            };

            if (tags.FirstOrDefault(t => t.Name == "bNeverDistanceCull")?.BoolValue == true)
            {
                return 0f;
            }

            var cached = F("CachedMaxDrawDistance");
            var end = F("InstanceEndCullDistance");
            return cached > 0f && end > 0f ? MathF.Min(cached, end) : MathF.Max(cached, end);
        }

        return 0f;
    }

    /// <summary>The measurement as a table (for the report), when <c>FARDRAW_MEASURE_OUT</c> names a folder.</summary>
    private static void Write(string name, Measured? before, Measured after)
    {
        if (Environment.GetEnvironmentVariable("FARDRAW_MEASURE_OUT") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var sb = new StringBuilder();
        foreach (var (title, m) in new[] { ("3.5.1 (every copy in the level it was copied from)", before), ("now", after) })
        {
            if (m is null)
            {
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"== {title}: plant level's area {m.PlantSpanCm / 100f:0} m east-west; {m.Result.FarPlaced.Count} moved");
            foreach (var slab in m.Slabs)
            {
                sb.AppendLine(slab.ToString());
            }

            sb.AppendLine();
        }

        File.WriteAllText(Path.Combine(folder, name), sb.ToString());
    }
}
