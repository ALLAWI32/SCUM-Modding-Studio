using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner (2026-10-12): "If I delete a fire place, the fire stays burning in game: the flames, the heat, the smoke, the fire
/// light and the sound, without the object. When I delete it, EVERYTHING of it must be deleted." From the delete to the
/// written level: the companions found, one journal step, the export plan, the rewritten package read back.
/// </summary>
public sealed class DeleteCompanionsExportTests
{
    private const string Level = SyntheticCampLevel.LevelPath;

    [Fact]
    public void DeletingTheFireplaceRemovesItsFireLightSmokeSoundAndDecalFromTheWrittenLevel()
    {
        using var temp = new LevelTempDirectory();
        var document = Camp(temp);
        var fireplace = new ActorRef(Level, "SM_Fireplace_1");
        var rockBefore = document.FindActor("Rock_1")!.WorldTransform.Translation;
        var state = new EditState();

        // The rock beside it draws a mesh of its own: unticked, it stays where it stands.
        var found = Companions.Find([document], [new DeleteSeed(fireplace)], state, null);
        var taken = found.Companions.Where(c => c.DefaultOn && c.Actor.Actor != "Rock_1").ToList();
        Assert.Equal(new[] { "Ash_Decal_1", "FireLight_1", "Fire_Sound_1", "Glow_1", "P_Fire_1" }, taken.Select(c => c.Actor.Actor).Order(StringComparer.Ordinal));
        var batch = EditOpFactory.DeleteWithCompanions([document], [new DeleteActorOp(fireplace)], taken, state, "Delete SM_Fireplace_1 and what belongs to it");
        state.Apply(batch);

        var warnings = new List<string>();
        var request = ProjectExporter.PlanLevel(state, Level, document, warnings);
        var (bytes, report) = LevelPackageEditor.Apply(Pristine(), request);
        Assert.Empty(report.Warnings);
        Assert.DoesNotContain(warnings, w => w.Contains("delete it with the object", StringComparison.Ordinal)); // nothing left at the spot

        string[] gone = ["SM_Fireplace_1", "FireLight_1", "P_Fire_1", "Fire_Sound_1", "Ash_Decal_1", "Glow_1"];
        Assert.Equal(gone.Order(StringComparer.Ordinal), report.RemovedActors.Order(StringComparer.Ordinal));
        var listed = LevelPackageEditor.ReadActorList(CookedPackage.Parse(bytes.UAsset, bytes.UExp)).Select(a => a.Name).ToList();
        Assert.DoesNotContain(listed, n => n is not null && gone.Contains(n));
        var written = ReadBack(temp, bytes);
        Assert.All(gone, n => Assert.Null(written.FindActor(n)));
        foreach (var kept in new[] { "BP_House_1", "Camp_Ambience", "Lamp_1", "DirectionalLight_1", "Rock_1" })
        {
            Assert.NotNull(written.FindActor(kept));
        }

        // The rock stood on the fireplace: taken off it, where it stood.
        var rock = written.FindActor("Rock_1")!;
        Assert.Null(rock.Root!.AttachParent);
        Assert.True(FVector.Distance(rock.WorldTransform.Translation, rockBefore) < 0.5f, $"{rock.WorldTransform.Translation} vs {rockBefore}");

        // One undo puts it all back: the export deletes nothing.
        state.Apply(batch.Inverse());
        Assert.True(ProjectExporter.PlanLevel(state, Level, document, []).IsEmpty);
    }

    [Fact]
    public void DeletingTheHousesFireplacePartNeutralizesFireFireLightFireSoundOnly()
    {
        using var temp = new LevelTempDirectory();
        var document = Camp(temp);
        var house = document.FindActor("BP_House_1")!;
        var state = new EditState();

        var found = Companions.Find([document], [new DeleteSeed(new ActorRef(Level, house.Name), "Fireplace")], state, null);
        var taken = found.Companions.Where(c => c.DefaultOn).ToList();
        Assert.Equal(new[] { "Fire", "FireLight", "FireSound" }, taken.Select(c => c.Component).Order(StringComparer.Ordinal));
        var part = EditOpFactory.SetTransform(document, house, house.FindComponent("Fireplace")!.Relative with { Scale = FVector.Zero }, state, "Fireplace");
        state.Apply(EditOpFactory.DeleteWithCompanions([document], [part], taken, state, "Delete Fireplace and what belongs to it"));

        var request = ProjectExporter.PlanLevel(state, Level, document, []);
        Assert.Empty(request.DeleteActors); // the walls stand: the house stays
        Assert.Equal(new[] { ("BP_House_1", "Fire"), ("BP_House_1", "FireLight"), ("BP_House_1", "FireSound") }, request.DeleteComponents);
        var pristine = Pristine();
        var (bytes, report) = LevelPackageEditor.Apply(pristine, request);
        Assert.Empty(report.Warnings);
        Assert.Equal(new[] { "BP_House_1.Fire", "BP_House_1.FireLight", "BP_House_1.FireSound" }, report.NeutralizedComponents);

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        int Export(CookedPackage p, string name) => Enumerable.Range(0, p.Exports.Count)
            .Single(i => p.ResolveName(p.Exports[i].ObjectName) == name && p.ResolveIndex(p.Exports[i].OuterIndex).EndsWith("BP_House_1", StringComparison.Ordinal));
        foreach (var name in new[] { "Fire", "FireLight", "FireSound" })
        {
            Assert.Equal("Object", edited.GetExportClassName(Export(edited, name)));
            Assert.Equal("/Script/CoreUObject.Default__Object", edited.GetFullPath(edited.Exports[Export(edited, name)].TemplateIndex));
        }

        foreach (var name in new[] { "Walls", "PorchLight", "DefaultSceneRoot" })
        {
            Assert.Equal(pristine.GetExportBytes(Export(pristine, name)), edited.GetExportBytes(Export(edited, name)));
        }

        var written = ReadBack(temp, bytes).FindActor("BP_House_1")!;
        Assert.Equal(new[] { "DefaultSceneRoot", "Walls", "Fireplace", "PorchLight" }, written.Components.Where(c => !c.IsSynthesized).Select(c => c.Name));
    }

    [Fact]
    public void TheOutpostFireBarrelDeletedAloneLeavesNoEffectWithinTwoMetres()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var levels = new[] { "B_4_Outpost", "B_4_Outpost_Exterior_01", "B_4_Outpost_Exterior_02", "B_4_Outpost_Ext_BossHouse" }
            .Select(n => "/Game/ConZ_Files/Maps/The_Island/" + n)
            .Where(p => catalog.TryGetPackageFile(p, out _))
            .Select(p => LevelDocument.Load(new Cue4ParseLevelReader(catalog), p))
            .ToList();
        var document = levels.First(d => d.FindActor("BP_Barrel_FirePit24") is not null);
        var barrel = new ActorRef(document.PackagePath, "BP_Barrel_FirePit24");
        var at = document.FindActor(barrel.Actor)!.WorldTransform.Translation;
        var meshes = new BendSupport(catalog);
        var state = new EditState();

        var found = Companions.Find(levels, [new DeleteSeed(barrel)], state, m => meshes.Describe(m)?.Bounds);
        state.Apply(EditOpFactory.DeleteWithCompanions(levels, [new DeleteActorOp(barrel)], found.Companions.Where(c => c.DefaultOn && !c.HasOwnMesh).ToList(), state, "Delete the barrel"));
        foreach (var level in levels)
        {
            var request = ProjectExporter.PlanLevel(state, level.PackagePath, level, [], null, null, meshes.Describe);
            var left = level.Actors.Where(a => !request.DeleteActors.Contains(a.Name, StringComparer.OrdinalIgnoreCase)
                                               && ActorRoles.IsEffectClass(a.ClassName) && !ActorRoles.IsGlobalEffect(a.ClassName) && !ActorRoles.HasDrawnMesh(a)
                                               && FVector.Distance(a.WorldTransform.Translation, at) < 200f)
                .Select(a => a.Name);
            Assert.Empty(left);
        }
    }

    private static LevelDocument Camp(LevelTempDirectory temp)
    {
        var game = temp.Combine("game");
        SyntheticCampLevel.WriteCampContent(game);
        using var catalog = AssetCatalog.OpenLoose(game);
        return LevelDocument.Load(new Cue4ParseLevelReader(catalog), Level);
    }

    private static CookedPackage Pristine()
    {
        var built = SyntheticCampLevel.Build();
        return CookedPackage.Parse(built.UAsset, built.UExp);
    }

    // The rewritten level in a loose folder of its own, read back through CUE4Parse.
    private static LevelDocument ReadBack(LevelTempDirectory temp, PackageBytes bytes)
    {
        var root = temp.Combine("written");
        bytes.WriteAsync(Path.Combine([root, .. (SyntheticLevels.MapsFolder + "/" + SyntheticCampLevel.LevelName).Split('/')]), ".umap").GetAwaiter().GetResult();
        using var catalog = AssetCatalog.OpenLoose(root);
        return LevelDocument.Load(new Cue4ParseLevelReader(catalog), Level);
    }
}
