using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord (2026-10-07, a police station): "a duplicated locker appears open in game", "copied lootable boxes are only
/// shapes, nobody can loot them", "I moved two military crates together; in game one was floating outside the station".
/// Every kind of lootable object, from three levels: a police weapon locker (an <c>ItemContainer</c> actor, top level and
/// as a building's child actor), military crate piles (mesh actors, one attached to the other), a building's closet and a
/// file cabinet (parts of a Blueprint building). Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class LootContainersRealTests(ITestOutputHelper output)
{
    private const string Samobor = "/Game/ConZ_Files/Maps/The_Island/D_4_Samobor_02_PoliceStation";
    private const string Ext = "/Game/ConZ_Files/Maps/The_Island/AbandonedCity_PripyatLike/C_0_AbandonedCity_06a_PoliceStation_Ext";
    private const string Int = "/Game/ConZ_Files/Maps/The_Island/AbandonedCity_PripyatLike/C_0_AbandonedCity_06a_PoliceStation_Int";
    private const string ChildLocker = "BPLockpick_Weapon_Locker_Police15_GEN_VARIABLE_BPLockpick_Weapon_Locker_Police_C_CAT_179";
    private const string Locker = "BPLockpick_Weapon_Locker_Police2";
    private const string Building = "BP_Police_Station_2a_NoLadder_3";
    private const string Lower = "SM_Military_CratePile_247";
    private const string Upper = "SM_Military_CratePile_243"; // attached to the lower pile's mesh

    [Fact]
    public async Task CopiesStayLootableLootEditsAreWrittenAndCratesMovedTogetherStayTogether()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var ctx = AppTestContext.Create();
        var key = AesKeyText.FromEnvironmentOrStore()!;
        ctx.Services.Keys.Set(key);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Loot");
        await map.LoadLevelsAsync([Samobor, Ext, Int]);
        var journal = ctx.Services.Projects.Current!.Journal;
        ActorItemViewModel Find(string level, string name) => map.AllActors.Single(a => a.Level.PackagePath == level && a.Name == name);

        // What the level reader sees: lockers are ItemContainers searched through their mesh's examine data.
        var childLocker = Find(Samobor, ChildLocker);
        var locker = Find(Int, Locker);
        var building = Find(Samobor, Building);
        Assert.True(childLocker.Actor.IsItemContainer);
        Assert.True(locker.Actor.IsItemContainer);
        Assert.False(building.Actor.IsItemContainer);
        var lockerLoot = locker.Actor.Components.First(c => c.LootPresets.Count > 0).LootPresets;
        Assert.Contains(lockerLoot, p => p.Contains("/SpawnerPresets2/", StringComparison.Ordinal) && p.Contains("Examine_", StringComparison.Ordinal));
        var closet = building.Actor.Components.First(c => c.LootPresets.Count > 0 && c.StaticMeshPath?.Contains("Closet", StringComparison.OrdinalIgnoreCase) == true && !c.IsSynthesized);
        output.WriteLine($"locker {string.Join(", ", lockerLoot)}; closet {closet.Name} {string.Join(", ", closet.LootPresets)}");

        // 1. A click on a locker's mesh takes the locker even in part mode: Duplicate copies the container, not a plain mesh.
        map.PickParts = true;
        var lockerMesh = map.PreparedScene!.Placements.First(p => p.Actor.Name == ChildLocker && p.Component is { IsStaticMeshComponent: true });
        Assert.Null(lockerMesh.PickKey(parts: true));
        map.SelectedInstanceKey = null;
        map.SelectedActorId = childLocker.SelectableId;
        map.DuplicateSelectedCommand.Execute(null);
        var childCopy = Assert.IsType<DuplicateActorOp>(journal.Applied[^1].Op);
        map.SelectedActorId = locker.SelectableId;
        map.DuplicateSelectedCommand.Execute(null);
        var lockerCopy = Assert.IsType<DuplicateActorOp>(journal.Applied[^1].Op);

        // 2. A building's searchable closet copied out as a part: a mesh actor that carries the closet's loot.
        map.SelectedInstanceKey = InstanceKey.Of(building.SelectableId, closet.Name, InstanceKey.Part);
        map.SelectedActorId = building.SelectableId;
        Assert.True(map.HasLootEditor);
        Assert.True(map.IsLootable);
        Assert.Equal(closet.LootPresets, map.LootPresets.Select(p => p.Path));
        map.DuplicateSelectedCommand.Execute(null);
        var closetCopy = Assert.IsType<AddStaticMeshActorOp>(journal.Applied[^1].Op);
        Assert.Equal(closet.LootPresets, closetCopy.Loot);

        // 3. The loot editor: the lower crate pile becomes decoration, the locker's copy gets another preset added (undo/redo).
        var lower = Find(Ext, Lower);
        var upper = Find(Ext, Upper);
        map.SelectedInstanceKey = null;
        map.SelectedActorId = lower.SelectableId;
        Assert.True(map.HasLootEditor);
        var crateLoot = map.LootPresets.Select(p => p.Path).ToList();
        map.IsLootable = false;
        Assert.IsType<SetLootOp>(journal.Applied[^1].Op);
        Assert.False(map.IsLootable);
        ctx.Services.Projects.Undo();
        map.SelectedActorId = upper.SelectableId;
        map.SelectedActorId = lower.SelectableId;
        Assert.Equal(crateLoot.Count > 0, map.IsLootable);
        ctx.Services.Projects.Redo();
        var extra = map.LootChoices.First(c => c.Name.StartsWith("Examine_Military_Crate", StringComparison.Ordinal));
        map.SelectedActorId = map.AllActors.Single(a => a.Name == lockerCopy.NewName).SelectableId;
        map.SelectedLootChoice = extra;
        map.AddLootPresetCommand.Execute(null);
        Assert.Equal([.. lockerLoot, extra.Path], map.LootPresets.Select(p => p.Path));

        // 4. The two crate piles moved together by one drag (the upper one is attached to the lower one).
        Assert.Equal(lower.Actor.Root!.ExportIndex, upper.Actor.Root!.AttachParent);
        map.SelectedActor = lower;
        map.ToggleGroup(upper.SelectableId, null);
        var start = map.SelectedRootWorld!.Value;
        var motion = new FVector(300f, 200f, 0f);
        map.ApplyDraggedTransform(lower.SelectableId, start with { Translation = start.Translation + motion });
        Assert.Equal(upper.Actor.WorldTransform.Translation.X + motion.X, map.ActorTransforms[upper.SelectableId].Translation.X, 1f); // the editor shows it moved once

        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        foreach (var warning in result.Warnings)
        {
            output.WriteLine("warning: " + warning);
        }

        using var written = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = key, LooseOverlays = [result.StagingDirectory] });
        var reader = new Cue4ParseLevelReader(written, new Cue4ParseLevelReaderOptions());
        var (samobor, ext, inside) = (LevelDocument.Load(reader, Samobor), LevelDocument.Load(reader, Ext), LevelDocument.Load(reader, Int));

        // The copies are containers with the source's loot; the child locker's copy is nobody's child (it begins play).
        var childRead = samobor.FindActor(childCopy.NewName)!;
        Assert.True(childRead.IsItemContainer);
        Assert.Null(childRead.ParentComponent);
        Assert.Equal(childLocker.Actor.Components.First(c => c.LootPresets.Count > 0).LootPresets, childRead.Components.First(c => c.LootPresets.Count > 0).LootPresets);
        var lockerRead = inside.FindActor(lockerCopy.NewName)!;
        Assert.True(lockerRead.IsItemContainer);
        Assert.Equal([.. lockerLoot, extra.Path], lockerRead.Components.First(c => c.LootPresets.Count > 0).LootPresets);
        Assert.Equal(lockerLoot, inside.FindActor(Locker)!.Components.First(c => c.LootPresets.Count > 0).LootPresets); // the original keeps its own
        Assert.Equal(closet.LootPresets, samobor.FindActor(closetCopy.NewName)!.Root!.LootPresets);

        // The decoration crate has no examine entries left; both piles stand where the editor showed them.
        Assert.Empty(ext.FindActor(Lower)!.Root!.LootPresets);
        Assert.True(ext.FindActor(Lower)!.WorldTransform.Translation.Equals(lower.Actor.WorldTransform.Translation + motion, 1f));
        var upperRead = ext.FindActor(Upper)!;
        Assert.True(upperRead.WorldTransform.Translation.Equals(upper.Actor.WorldTransform.Translation + motion, 1f), upperRead.WorldTransform.Translation.ToString());
    }
}
