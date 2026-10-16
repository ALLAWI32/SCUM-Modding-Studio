using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using Xunit.Abstractions;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// D15 (seviscache: "I got a corrupted data error when I launched the game" after copying light emitters and
/// particle/Niagara Blueprints) and D04 (thezombie_97: the game crashes as soon as he goes to Samobor after editing it):
/// real lights, fires, smoke, Cascade and Niagara effects, sounds and the Blueprints that hold them, copied through the
/// app's own path (a paste into another town is an <see cref="AddBlueprintActorOp"/>, a duplicate a
/// <see cref="DuplicateActorOp"/>, then the exporter) and read back strictly (<see cref="CopyReadBack"/>). The export runs
/// with the copy guard off, so what the copier writes is what is checked; the guard must let these copies through as well.
/// Real game files only (SCUM_PAKS and this PC's key store); only the game itself can give the final word.
/// </summary>
public sealed class FxCopyRealTests(FxCopyRealTests.Game game, ITestOutputHelper output) : IClassFixture<FxCopyRealTests.Game>
{
    private const string Maps = "/Game/ConZ_Files/Maps/The_Island/";
    private const string Town = Maps + "A_3_Farm_01"; // another town than every sample's
    private static readonly FVector TownSpot = new(320_000f, -488_700f, 10_550f);

    [PaksTheory]
    [InlineData("D_4_Samobor_00", "SM_Reflector_On_Blueprint2")] // Samobor's street lamp: a spot light and NS_Moths_Lamp with its user parameters
    [InlineData("A_1_TV_Base_Entrance", "MBP_Fire_PointLight_Child2_2")] // a fire: flames (Cascade), its point light, its Ak sound, heat
    [InlineData("A_0_Outpost_Exterior_02", "BP_Barrel_FirePit10")] // a barrel fire whose light stores its LightGuid
    [InlineData("D_4_Samobor_04", "P_Ambient_Fog_10")] // a Cascade emitter actor (fog)
    [InlineData("D_1_Halloween_Hut/D_1_Halloween_Hut", "PointLight10_14")] // a plain point light
    [InlineData("D4_Airport_Zeljava", "SpotLight10")] // a plain spot light
    [InlineData("D_4_Samobor_04_Interior_01", "Tavern_SCUM_Neon_Light")] // a plain rect light
    [InlineData("D_4_Samobor_05_Block_02", "BP_Debris_Tire_Stack_7")] // burning tyres: fire and smoke, a light, heat, instanced tyres
    [InlineData("D_4_Samobor_01", "BP_Monument_Spring_A_2")] // a spring: its sound, two water effects, a water source
    [InlineData("AbandonedCity_PripyatLike/C_0_AbandonedCity_02a_CityHall_Ext", "NS_Flies_Exposed_9")] // a Niagara actor with redirected user parameters
    [InlineData("Safe_Zone_D_2", "BP_DiscoLight04_White_2")] // a light whose meshes carry LODData with zero build data ids (they stay zero)
    [InlineData("AbandonedCity_PripyatLike/C_0_AbandonedCity_04a_Hotel_00_Int", "BP_MarinaHotel_OPT3")] // a hotel: 5 Niagara effects with curve data interfaces, child actors
    [InlineData("B_1_Prkno_01", "BP_Garage_02_03_RW")] // a garage whose door mesh has baked lighting (one of the island's 65 non-zero MapBuildDataIds)
    [InlineData("A_0_Outpost_Exterior_02", "BP_Industrial_Lamp_02_ON_GEN_VARIABLE_BP_Industrial_Lamp_02_ON_C_CAT_0")] // a lamp a building spawned, 13 user parameters
    public async Task PastedIntoAnotherTownTheyReadBackExactly(string level, string actor)
    {
        var source = Maps + level;
        var record = game.Document(source).FindActor(actor) ?? throw new InvalidOperationException($"{actor} is not in {source}");
        var copy = actor + "_Added";
        var placed = record.WorldTransform with { Translation = TownSpot };
        await CheckAsync(source, actor, Town, copy, new AddBlueprintActorOp(Town, copy, record.ClassPath, new ActorRef(source, actor), TransformValue.FromTransform(placed)));
    }

    [PaksTheory]
    [InlineData("D_4_Samobor_00", "SM_Reflector_On_Blueprint2")]
    [InlineData("A_0_Outpost_Exterior_02", "BP_Barrel_FirePit10")]
    [InlineData("D_1_Halloween_Hut/D_1_Halloween_Hut", "PointLight10_14")]
    public async Task DuplicatedInTheirOwnLevelTheyReadBackExactly(string level, string actor)
    {
        var source = Maps + level;
        var record = game.Document(source).FindActor(actor) ?? throw new InvalidOperationException($"{actor} is not in {source}");
        var copy = actor + "_Copy";
        var moved = record.WorldTransform with { Translation = record.WorldTransform.Translation + new FVector(300, 0, 0) };
        await CheckAsync(source, actor, source, copy, new DuplicateActorOp(new ActorRef(source, actor), copy, TransformValue.FromTransform(moved)));
    }

    [PaksFact]
    public async Task AVolumeIsStillNotPastedIntoAnotherTown()
    {
        // Samobor's atmosphere (a post process volume): its brush shape (Model) holds the source level's indices.
        const string source = Maps + "D_4_Samobor_00_Atmosphere";
        const string actor = "PP_Samobor";
        var record = game.Document(source).FindActor(actor)!;
        Assert.Equal(CopyRefusalReason.NativeData, LevelPackageEditor.RefusedCopy(record, game.Document(source), otherLevel: true)?.Reason);
        Assert.Equal(CopyRefusalReason.NativeData, LevelPackageEditor.CheckCopy(game.Package(source), actor, otherLevel: true)?.Reason);

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-fxcopy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var project = await Project.CreateAsync(Path.Combine(dir, "project"), "FxCopy");
            project.Apply(new AddBlueprintActorOp(Town, actor + "_Added", record.ClassPath, new ActorRef(source, actor), TransformValue.FromTransform(record.WorldTransform with { Translation = TownSpot })));
            var result = await new ProjectExporter().ExportAsync(project, game.Catalog!, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), WritePak = false });
            var warning = Assert.Single(result.Warnings, w => w.Contains(actor + "_Added' was not created", StringComparison.Ordinal));
            Assert.Contains("(Model) holds native data", warning, StringComparison.Ordinal);
            output.WriteLine(warning);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private async Task CheckAsync(string source, string actor, string level, string copy, EditOp op)
    {
        var catalog = game.Catalog!;
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-fxcopy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var project = await Project.CreateAsync(Path.Combine(dir, "project"), "FxCopy");
            project.Apply(op);
            ExportResult result;
            using (LevelPackageEditor.WithoutCopyGuard())
            {
                result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), WritePak = false });
            }

            var umap = Directory.EnumerateFiles(result.StagingDirectory, level[(level.LastIndexOf('/') + 1)..] + ".umap", SearchOption.AllDirectories).Single();
            var notes = new List<string>();
            var problems = CopyReadBack.Check(catalog, game.Package(source), actor, level, await File.ReadAllBytesAsync(umap), await File.ReadAllBytesAsync(Path.ChangeExtension(umap, ".uexp")), copy, notes);
            notes.ForEach(output.WriteLine);
            problems.AddRange(result.Warnings.Where(w => w.Contains(copy, StringComparison.Ordinal) && !w.Contains("were cleared", StringComparison.Ordinal)));
            if (LevelPackageEditor.CheckCopy(game.Package(source), actor, otherLevel: !string.Equals(source, level, StringComparison.Ordinal)) is { } refusal)
            {
                problems.Add("the copy guard refuses it: " + refusal.Describe());
            }

            foreach (var line in problems)
            {
                output.WriteLine(line);
            }

            Assert.True(problems.Count == 0, $"{actor} -> {copy}: {problems.Count} problem(s); the first: {problems.FirstOrDefault()}");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>The game files, opened once for the class, and the levels it read.</summary>
    public sealed class Game : IDisposable
    {
        private readonly Dictionary<string, CookedPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LevelDocument> _documents = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The catalog, or null without the real game files (the tests are skipped then).</summary>
        public AssetCatalog? Catalog { get; } = RealPaks.Folder is { } paks ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() }) : null;

        /// <summary>The pristine level package.</summary>
        public CookedPackage Package(string level)
        {
            lock (_packages)
            {
                if (!_packages.TryGetValue(level, out var package))
                {
                    Assert.True(Catalog!.TryGetPackageFile(level, out var file), level);
                    var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(Catalog, file);
                    _packages[level] = package = CookedPackage.Parse(uasset, uexp, ubulk, level);
                }

                return package;
            }
        }

        /// <summary>The level as the Map reads it.</summary>
        public LevelDocument Document(string level)
        {
            lock (_documents)
            {
                if (!_documents.TryGetValue(level, out var document))
                {
                    _documents[level] = document = LevelDocument.Load(new Cue4ParseLevelReader(Catalog!), level);
                }

                return document;
            }
        }

        public void Dispose() => Catalog?.Dispose();
    }
}
