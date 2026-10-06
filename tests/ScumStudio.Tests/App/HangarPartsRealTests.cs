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

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (and Igor before him): "I clicked the hangar to raise only the building, and everything inside rose with it; I want
/// to move the door, move the whole house, move or copy the things inside". Real game files only (<c>SCUM_PAKS</c>, key
/// from this PC's store): the airfield hangar, part picking on by default, and the exported level read back.
/// </summary>
public sealed class HangarPartsRealTests
{
    private const string Airfield = "/Game/ConZ_Files/Maps/The_Island/A_4_Airfield";
    private const string Door = "BP_Door_Airplane_Hangar_GEN_VARIABLE_BP_Door_Airplane_Hangar_C_CAT_467";

    [Fact]
    public async Task TheHangarShellIsRaisedAloneAShelfIsCopiedOutAndTheDoorMoves()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        var key = AesKeyText.FromEnvironmentOrStore()!;
        ctx.Services.Keys.Set(key);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Hangar");
        await map.LoadLevelsAsync([Airfield]);
        var journal = ctx.Services.Projects.Current!.Journal;

        var hangar = map.AllActors.Single(a => a.Name == "BP_Airplane_Hangar2_2");
        var shell = InstanceKey.Of(hangar.SelectableId, "SM_Airplane_Hangar", InstanceKey.Part);
        Assert.True(map.PickParts); // on from the start: a click on the shell picks the shell
        Assert.Contains(map.PreparedScene!.Placements, p => p.InstanceKey == shell); // what the click reports

        // 1. The shell alone goes up 5 m; the shelves, crates and the building itself stay.
        map.SelectedInstanceKey = shell;
        map.SelectedActorId = hangar.SelectableId;
        Assert.True(map.HasSelectedPart);
        var shellWorld = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(hangar.SelectableId, shellWorld with { Translation = shellWorld.Translation + new FVector(0f, 0f, 500f) });
        var raise = Assert.IsType<SetTransformOp>(Assert.Single(journal.Applied).Op);
        Assert.Equal("SM_Airplane_Hangar", raise.Component);
        Assert.Equal(shellWorld.Translation.Z + 500f, map.InstanceTransforms[shell].Translation.Z, 1f);
        Assert.Empty(map.ActorTransforms);

        // 2. One click back to the whole building.
        map.SelectWholeCommand.Execute(null);
        Assert.Null(map.SelectedInstanceKey);
        Assert.False(map.HasSelectedPart);
        Assert.Same(hangar, map.SelectedActor);

        // 3. A shelf from inside is copied and pasted outside as an object of its own.
        var shelf = InstanceKey.Of(hangar.SelectableId, "StaticMesh21", InstanceKey.Part);
        map.SelectedInstanceKey = shelf;
        map.SelectedActorId = hangar.SelectableId;
        map.AimPointProvider = () => new FVector(shellWorld.Translation.X + 5000f, shellWorld.Translation.Y, shellWorld.Translation.Z);
        map.CopySelectedCommand.Execute(null);
        map.PasteCommand.Execute(null);
        var paste = Assert.IsType<AddStaticMeshActorOp>(journal.Applied[^1].Op);
        Assert.EndsWith(".SM_Storage_Shelves_03", paste.StaticMesh, StringComparison.Ordinal);

        // 4. A door (a child actor of the hangar) slides 2 m on its own.
        var door = map.AllActors.Single(a => a.Name == Door);
        map.SelectedInstanceKey = null;
        map.SelectedActorId = door.SelectableId;
        var doorWorld = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(door.SelectableId, doorWorld with { Translation = doorWorld.Translation + new FVector(200f, 0f, 0f) });
        var slide = Assert.IsType<SetTransformOp>(journal.Applied[^1].Op);
        Assert.Equal(Door, slide.Target.Actor);
        Assert.Null(slide.Component);
        Assert.DoesNotContain(hangar.SelectableId, map.ActorTransforms.Keys);

        // 5. Salvador (Discord): "When I duplicate an openable door and place it somewhere else, it doesn't open in the game."
        // A click on the door's leaf takes the door even in part mode (its class is what opens), so Duplicate copies the door,
        // not a plain mesh of its leaf; the hangar's own parts stay parts.
        var leaf = map.PreparedScene!.Placements.Single(p => p.Actor.Name == Door && p.Component?.Name == "Door Mesh");
        Assert.True(leaf.Component!.IsNativeSubobject);
        Assert.False(hangar.Actor.FindComponent("SM_Airplane_Hangar")!.IsNativeSubobject);
        Assert.False(hangar.Actor.FindComponent("StaticMesh21")!.IsNativeSubobject);
        Assert.Equal(shell, map.PreparedScene!.Placements.First(p => p.InstanceKey == shell).PickKey(map.PickParts));
        Assert.Null(leaf.PickKey(map.PickParts));
        map.SelectedInstanceKey = leaf.PickKey(map.PickParts);
        map.SelectedActorId = leaf.SelectableId;
        Assert.Same(door, map.SelectedActor);
        map.DuplicateSelectedCommand.Execute(null);
        var doorCopy = Assert.IsType<DuplicateActorOp>(journal.Applied[^1].Op);
        Assert.Equal(Door, doorCopy.Source.Actor);

        // 6. A project that hid the leaf as a part (scale 0) before this still draws it hidden: its key did not change.
        var leafKey = InstanceKey.Of(door.SelectableId, "Door Mesh", InstanceKey.Part);
        Assert.Equal(leafKey, leaf.InstanceKey);
        var leafRelative = leaf.Component.Relative;
        ctx.Services.Projects.Apply(new SetTransformOp(door.Reference, leafRelative, leafRelative with { Scale = FVector.Zero }, "Door Mesh"));
        Assert.Equal(0f, map.InstanceTransforms[leafKey].Scale3D.X, 3);

        // The mod: only the shell's component moved, the shelf is where it was, the copy and the door are written.
        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        using var written = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = key, LooseOverlays = [result.StagingDirectory] });
        var before = LevelDocument.Load(new Cue4ParseLevelReader(ctx.Services.Workspace.Catalog!, new Cue4ParseLevelReaderOptions()), Airfield);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(written, new Cue4ParseLevelReaderOptions()), Airfield);
        var (oldHangar, newHangar) = (before.FindActor(hangar.Name)!, after.FindActor(hangar.Name)!);
        Assert.Equal(oldHangar.Components.Count, newHangar.Components.Count);
        Assert.Equal(oldHangar.Root!.WorldTransform.Translation, newHangar.Root!.WorldTransform.Translation);
        Assert.Equal(oldHangar.FindComponent("SM_Airplane_Hangar")!.WorldTransform.Translation.Z + 500f, newHangar.FindComponent("SM_Airplane_Hangar")!.WorldTransform.Translation.Z, 1f);
        Assert.Equal(oldHangar.FindComponent("StaticMesh21")!.WorldTransform.Translation, newHangar.FindComponent("StaticMesh21")!.WorldTransform.Translation);
        Assert.Equal(before.FindActor(Door)!.Root!.WorldTransform.Translation.X + 200f, after.FindActor(Door)!.Root!.WorldTransform.Translation.X, 1f);
        Assert.Contains(after.Actors, a => a.Name == paste.NewName && a.StaticMeshPath is { } m && m.EndsWith(".SM_Storage_Shelves_03", StringComparison.Ordinal));

        // The door's copy is a door, no child of the hangar's component (that one spawns the original), attached to it still.
        var (oldDoor, copiedDoor) = (before.FindActor(Door)!, after.FindActor(doorCopy.NewName)!);
        Assert.Equal(oldDoor.ClassPath, copiedDoor.ClassPath);
        Assert.Null(copiedDoor.ParentComponent);
        Assert.Equal(oldDoor.Root!.AttachParent, copiedDoor.Root!.AttachParent);
        Assert.Equal(oldDoor.ParentComponent, after.FindActor(Door)!.ParentComponent);
    }
}
