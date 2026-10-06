using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Salvador (Discord): "When I duplicate an openable door and place it somewhere else, it doesn't open in the game." The
/// saloon's door is a child actor of the building (its ChildActorComponent spawned it): a duplicate in the same level must
/// not stay that component's child (the component still spawns the original), while a copy of the whole saloon keeps its
/// own door linked both ways. Real game files only.
/// </summary>
public sealed class DoorCopyRealTests
{
    private const string Saloon = "/Game/ConZ_Files/Maps/The_Island/A_0_Outpost_Ext_Saloon";
    private const string Building = "BP_SaloonOutpost_2";
    private const string DoorSlot = "BP_SingleDoorSaloonOutpost_Flip";
    private const string Door = "BP_SingleDoorSaloonOutpost_Flip_GEN_VARIABLE_BP_SingleDoorSaloonOutpost_Flip_C_CAT_0";

    [Fact]
    public async Task ADuplicatedDoorLeavesTheBuildingsComponentAndACopiedBuildingKeepsItsDoor()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        var key = AesKeyText.FromEnvironmentOrStore();
        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = key });
        var before = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Saloon);
        var building = before.FindActor(Building)!;
        var slot = building.FindComponent(DoorSlot)!;
        var door = before.FindActor(Door)!;
        Assert.Equal(slot.ExportIndex, door.ParentComponent);
        Assert.Equal(slot.ExportIndex, door.Root!.AttachParent);
        Assert.True(door.FindComponent("Door Mesh")!.IsNativeSubobject); // the leaf is the door class's own

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-doors-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var project = await Project.CreateAsync(Path.Combine(dir, "project"), "Doors");
            var aside = door.Root.Relative with { Location = door.Root.Relative.Location + new FVector(0f, 300f, 0f) };
            project.Apply(new DuplicateActorOp(new ActorRef(Saloon, Door), Door + "_Copy", aside));
            project.Apply(new DuplicateActorOp(new ActorRef(Saloon, Building), Building + "_Copy", building.Root!.Relative));
            var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), WritePak = false });
            Assert.DoesNotContain(result.Warnings, w => w.Contains("_Copy", StringComparison.Ordinal));

            using var written = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = key, LooseOverlays = [result.StagingDirectory] });
            var after = LevelDocument.Load(new Cue4ParseLevelReader(written), Saloon);

            // The door's copy: no child of the saloon's component, still attached to it (its place is relative to it).
            var copy = after.FindActor(Door + "_Copy")!;
            Assert.Equal(door.ClassPath, copy.ClassPath);
            Assert.Null(copy.ParentComponent);
            Assert.Equal(slot.ExportIndex, copy.Root!.AttachParent);
            Assert.True(copy.Root.Relative.IsNearlyEqual(aside), copy.Root.Relative.ToString());

            // The original is untouched: still the component's child actor.
            var original = after.FindActor(Door)!;
            Assert.Equal(slot.ExportIndex, original.ParentComponent);
            Assert.Equal(slot.ExportIndex, original.Root!.AttachParent);
            Assert.Equal(original.ExportIndex, after.FindActor(Building)!.FindComponent(DoorSlot)!.ChildActor);

            // The saloon's copy spawns a door of its own, linked both ways.
            var newSlot = after.FindActor(Building + "_Copy")!.FindComponent(DoorSlot)!;
            var newDoor = after.FindActor(newSlot.ChildActor!.Value)!;
            Assert.NotEqual(original.ExportIndex, newDoor.ExportIndex);
            Assert.NotEqual(copy.ExportIndex, newDoor.ExportIndex);
            Assert.Equal(door.ClassPath, newDoor.ClassPath);
            Assert.Equal(newSlot.ExportIndex, newDoor.ParentComponent);
            Assert.Equal(newSlot.ExportIndex, newDoor.Root!.AttachParent);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
