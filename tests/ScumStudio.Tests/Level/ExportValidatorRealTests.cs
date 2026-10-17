using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Modding.Cloning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Real game files: the export's package check (<see cref="ModPackageValidator"/>) over the kinds of edit players make
/// (owner 2026-10-16: the game crashed on leaving a server or closing it with a studio mod). Deleting actors other actors
/// point at, copying Blueprint actors between levels, duplicating, cloning a vehicle and an item: the export passes the
/// check, and a check of the game's own packages finds nothing.
/// </summary>
public sealed class ExportValidatorRealTests
{
    private const string Church = "/Game/ConZ_Files/Maps/The_Island/C_3_Church";
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    private const string Rager = "/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager";
    private const string Ak47 = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";

    private static readonly string[] Candidates =
    [
        "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01", "/Game/ConZ_Files/Maps/The_Island/C_3_Church", "/Game/ConZ_Files/Maps/The_Island/B_2_Bunker_01",
        "/Game/ConZ_Files/Maps/The_Island/Z_4_Prvo_Selo_01", "/Game/ConZ_Files/Maps/The_Island/A_0_Outpost", "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost",
        "/Game/ConZ_Files/Maps/The_Island/Z_3_Outpost", "/Game/ConZ_Files/Maps/The_Island/C_2_Outpost",
    ];

    [Fact]
    public async Task EditsPlayersMakeExportPastTheCheck()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var referenced = Candidates.Where(catalog.PackageExists).SelectMany(l => ReferencedActors(catalog, l).Take(4)).ToList();
        Assert.NotEmpty(referenced); // the game's levels have actors other actors point at (doors, switches, groups)

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-validator-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var project = await Project.CreateAsync(Path.Combine(dir, "project"), "CheckMe");
            foreach (var (level, actor, _) in referenced)
            {
                project.Apply(new DeleteActorOp(new ActorRef(level, actor)));
            }

            // Blueprint actors copied into another level, and a duplicate inside it.
            var church = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Church);
            var outside = church.Actors.Single(a => a.Name == "BP_CC_A_EXT_MASTER");
            project.Apply(new AddBlueprintActorOp(Farm, outside.Name + "_Added", outside.ClassPath, new ActorRef(Church, outside.Name),
                TransformValue.FromTransform(outside.WorldTransform with { Translation = new ScumStudio.Core.Mathematics.FVector(320_000f, -488_700f, 10_550f) })));
            var farm = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Farm);
            var house = farm.Actors.First(a => a.Kind == ActorKind.StaticMeshActor && a.Root is not null);
            project.Apply(new DuplicateActorOp(new ActorRef(Farm, house.Name), house.Name + "_Copy", house.Root!.Relative with { Location = house.Root.Relative.Location + new ScumStudio.Core.Mathematics.FVector(500, 0, 0) }));

            // A vehicle and an item cloned under new names.
            foreach (var (kind, template, plan) in new[]
                     {
                         ("vehicle", Rager, CloneFamilyPlanner.PlanVehicle(catalog, Rager, "CheckRager")),
                         ("weapon", Ak47, CloneFamilyPlanner.PlanItem(catalog, Ak47, "Weapon_AK47_Check")),
                     })
            {
                project.Apply(new CloneAssetOp(kind, template, plan.NewPrimary, plan.Packages.Select(p => new PackagePair(p.Key, p.Value)).ToList()));
            }

            var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), WritePak = false });

            Assert.NotNull(result.Validation);
            Assert.True(result.Validation.Passed, string.Join('\n', result.Validation.Problems));
            Assert.True(result.Validation.Packages > 100, result.Validation.Summary); // the levels and both clone families

            // A deleted vehicle trader takes its bike and car lifts along (no lift of the game is without its trade post).
            Assert.Contains(result.Warnings, w => w.Contains("BP_CarLift", StringComparison.Ordinal) && w.Contains("_assignedTradePost", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>Actors of <paramref name="level"/> another actor of it points at (not through a component of its own): (level, actor, referrer).</summary>
    private static IEnumerable<(string Level, string Actor, string Referrer)> ReferencedActors(AssetCatalog catalog, string level)
    {
        if (catalog.LoadPackage(level) is not Package package || package.ExportsLazy.Select(e => e.Value).OfType<ULevel>().FirstOrDefault() is not { } persistent)
        {
            yield break;
        }

        var actors = (persistent.Actors ?? []).Where(a => a is { IsExport: true }).Select(a => a!.Index).ToHashSet();
        var script = persistent.LevelScriptActor?.Index ?? 0;
        var seen = new HashSet<int>();
        foreach (var referrer in actors)
        {
            if (referrer == script || package.ExportsLazy[referrer - 1].Value is not { } obj)
            {
                continue;
            }

            foreach (var target in obj.Properties.SelectMany(t => Indices(t.Tag)).Where(i => i != referrer && actors.Contains(i) && i != script))
            {
                if (seen.Add(target))
                {
                    yield return (level, package.ExportMap[target - 1].ObjectName.Text, package.ExportMap[referrer - 1].ObjectName.Text);
                }
            }
        }
    }

    private static IEnumerable<int> Indices(FPropertyTagType? tag) => tag?.GenericValue switch
    {
        FPackageIndex index => [index.Index],
        UScriptArray array => array.Properties.SelectMany(Indices),
        FScriptStruct { StructType: FStructFallback fallback } => fallback.Properties.SelectMany(p => Indices(p.Tag)),
        _ => [],
    };
}
