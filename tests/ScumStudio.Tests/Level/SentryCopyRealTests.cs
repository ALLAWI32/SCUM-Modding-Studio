using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Pak;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Discord igor8802: "When duplicating robots, the robots do not appear." The game spawns a sentry only from a spawner its
/// level's guarded zone manager lists (<c>_sentrySpawners</c>), so a copied spawner joins that list, and pasted into a level
/// without a manager it brings a manager of its own. Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class SentryCopyRealTests
{
    private const string Maps = "/Game/ConZ_Files/Maps/The_Island/";

    [Fact]
    public void ACopiedSentrySpawnerIsListedByAGuardedZoneManager()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var barracks = Load(catalog, Maps + "D_0_Military_Barracks");
        var station = Load(catalog, Maps + "B_3_Gas_Station");
        var there = new TransformValue(new FVector(-861946.6f, 503722.1f, 60305.04f), FRotator.Zero, FVector.One);

        // Duplicated in its own level: the barracks' manager lists it after its eight stock spawners.
        var (bytes, report) = LevelPackageEditor.Apply(barracks, new LevelEditRequest { Copies = [new ActorCopy("SentrySpawner_0", "SentrySpawner_0_Copy", there)] });
        Assert.Empty(report.Warnings);
        var listed = Listed(CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, Maps + "D_0_Military_Barracks"), "BP_GuardedZoneManager_2");
        Assert.Equal([.. Enumerable.Range(0, 8).Select(i => $"SentrySpawner_{i}"), "SentrySpawner_0_Copy"], listed);

        // Pasted into a level with no sentries: a copy of the barracks' manager comes along, an actor of the level listing only it.
        (bytes, report) = LevelPackageEditor.Apply(station, new LevelEditRequest { ForeignCopies = [new ForeignActorCopy(barracks, "SentrySpawner_0", "SentrySpawner2_Added", there)] });
        Assert.Empty(report.Warnings);
        var pasted = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, Maps + "B_3_Gas_Station");
        Assert.Equal(["SentrySpawner2_Added"], Listed(pasted, "BP_GuardedZoneManager_2_SentrySpawner2_Added"));
        Assert.Contains(LevelPackageEditor.ReadActorList(pasted), a => a.Name == "BP_GuardedZoneManager_2_SentrySpawner2_Added");
    }

    /// <summary>The spawners a manager lists, each also one of its create-before-serialize dependencies (as the cooker writes them).</summary>
    private static List<string> Listed(CookedPackage package, string manager)
    {
        var index = package.Exports.ToList().FindIndex(e => package.ResolveName(e.ObjectName) == manager);
        Assert.True(index >= 0, manager);
        var spawners = ((ArrayValue)package.ReadProperties(index).Find("_sentrySpawners")!.Value).Items.OfType<ObjectValue>().Select(o => o.Index).ToList();
        var entry = package.Exports[index];
        var createBeforeSerialize = package.ReadPreloadDependencies()
            .Skip(entry.FirstExportDependency + entry.SerializationBeforeSerializationDependencies).Take(entry.CreateBeforeSerializationDependencies).ToHashSet();
        Assert.Subset(createBeforeSerialize, spawners.ToHashSet());
        return spawners.Select(i => package.ResolveName(package.Exports[i - 1].ObjectName)).ToList();
    }

    private static CookedPackage Load(AssetCatalog catalog, string level)
    {
        Assert.True(catalog.TryGetPackageFile(level, out var file));
        return CookedPackage.Parse(file.Read(), catalog.Provider.Files[file.Path[..file.Path.LastIndexOf('.')] + ".uexp"].Read(), null, level);
    }
}
