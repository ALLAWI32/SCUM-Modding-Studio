using System.Text.Json;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Blueprint actors: construction-script (SCS) components, mesh component recognition, child actors and synthesized
/// components. The real-data tests read the map slice (<c>SCUM_MAP_SLICE</c>, A_0 Outpost client cook) and are skipped
/// without it; the model tests run everywhere.
/// </summary>
public sealed class BlueprintExpansionTests
{
    private const string SaloonLevel = MapSlice.MapsPath + "A_0_Outpost_Ext_Saloon";
    private const string ExteriorLevel = MapSlice.MapsPath + "A_0_Outpost_Exterior";
    private const string SaloonFolder = "/Game/ConZ_Files/Models/Buildings/Outpost/SaloonOutpost/";
    private const string SaloonBlueprint = SaloonFolder + "BP_SaloonOutpost";
    private const string DoorClass = SaloonFolder + "MeshesAndProps/BP_SingleDoorSaloonOutpost_Flip.BP_SingleDoorSaloonOutpost_Flip_C";
    private const string DoorMesh = SaloonFolder + "MeshesAndProps/SM_SingleDoorRoomSaloonOutpostRooFlip.SM_SingleDoorRoomSaloonOutpostRooFlip";
    private const string DoorComponent = "BP_SingleDoorSaloonOutpost_Flip";
    private const float Tolerance = 0.05f;

    [MapSliceFact]
    public void SaloonBlueprintExposesItsBuildingMeshes()
    {
        using var catalog = MapSlice.Open();
        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), SaloonLevel);
        var saloon = doc.FindActor("BP_SaloonOutpost_2")!;
        Assert.Equal(ActorKind.Blueprint, saloon.Kind);
        Assert.Equal(SaloonBlueprint + ".BP_SaloonOutpost_C", saloon.ClassPath);

        var building = saloon.Components
            .Where(c => c.IsStaticMeshComponent && c.StaticMeshPath?.StartsWith(SaloonFolder, StringComparison.Ordinal) == true)
            .ToList();
        Assert.True(building.Count >= 10, $"only {building.Count} saloon meshes");
        Assert.All(building, c => Assert.True(
            FVector.Distance(c.WorldTransform.Translation, saloon.WorldTransform.Translation) < 5000,
            $"{c.Name} is {FVector.Distance(c.WorldTransform.Translation, saloon.WorldTransform.Translation)} cm from the actor"));
        var main = saloon.FindComponent("SM_SaloonBuilding_Part1_Outpost")!;
        Assert.StartsWith(SaloonFolder, main.StaticMeshPath, StringComparison.Ordinal);
        Assert.True(main.WorldTransform.Equals(saloon.WorldTransform, Tolerance), $"{main.WorldTransform} vs {saloon.WorldTransform}");

        // SCUM's InteriorStaticMeshComponent (a StaticMeshComponent subclass unknown to CUE4Parse) is a mesh component.
        var interior = saloon.Components.Where(c => c.ClassName == "InteriorStaticMeshComponent").ToList();
        Assert.Equal(7, interior.Count);
        Assert.All(interior, c =>
        {
            Assert.True(c.IsStaticMeshComponent);
            Assert.True(c.IsSceneComponent);
            Assert.NotNull(c.StaticMeshPath);
        });

        // The environment description mesh is hidden (bVisible = False on its template); lights and child actors are not meshes.
        Assert.False(saloon.FindComponent("EnvironmentDescription")!.IsVisible);
        Assert.True(main.IsVisible);
        Assert.False(saloon.FindComponent(DoorComponent)!.IsStaticMeshComponent);

        // The level stores every SCS component of a placed Blueprint (53 nodes; the unused DefaultSceneRootNode of
        // the door Blueprint is not resurrected), so nothing is synthesized here.
        Assert.Equal(53, saloon.Components.Count);
        Assert.Equal(0, saloon.SynthesizedComponentCount);
        Assert.Empty(doc.Data.SynthesizedComponents);
        Assert.NotNull(saloon.StaticMeshPath);
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("construction script node", StringComparison.Ordinal));
    }

    [MapSliceFact]
    public void OutpostExteriorKeepsItsActorsAndItsBlueprintsHaveMeshes()
    {
        using var catalog = MapSlice.Open();
        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), ExteriorLevel);
        Assert.Equal(510, doc.Actors.Count);
        Assert.Equal(554, doc.InstanceCount);

        var blueprints = doc.Actors.Where(a => a.Kind == ActorKind.Blueprint).ToList();
        var present = blueprints.Where(a => catalog.PackageExists(AssetPaths.SplitObjectPath(a.ClassPath).PackagePath)).ToList();
        Assert.True(present.Count >= 10, $"only {present.Count} Blueprint actors with their package in the slice");
        Assert.All(present, a =>
        {
            Assert.Contains(a.Components, c => c.IsStaticMeshComponent && c.StaticMeshPath is not null);
            Assert.NotNull(a.StaticMeshPath);
        });

        // Blueprints whose package is not in the slice are reported once, not silently dropped.
        if (present.Count < blueprints.Count)
        {
            Assert.Single(doc.Warnings, w => w.Contains("Blueprint class(es) are not in the source", StringComparison.Ordinal));
        }

        // Same actor graph without expansion (nothing is missing from this level).
        var flat = LevelDocument.Load(
            new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false }),
            ExteriorLevel);
        Assert.Equal(doc.Actors.Sum(a => a.Components.Count), flat.Actors.Sum(a => a.Components.Count));
        Assert.Equal(doc.InstanceCount, flat.InstanceCount);
    }

    [MapSliceFact]
    public void SaloonDoorChildActorIsLinkedAndPlacedAtItsComponent()
    {
        using var catalog = MapSlice.Open();
        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), SaloonLevel);
        var saloon = doc.FindActor("BP_SaloonOutpost_2")!;
        var cac = saloon.FindComponent(DoorComponent)!;
        Assert.Equal("ChildActorComponent", cac.ClassName);
        Assert.Equal(DoorClass, cac.ChildActorClassPath);
        Assert.NotNull(cac.ChildActor);

        // The cooked level stores the spawned door as its own actor (<component>_GEN_VARIABLE_<class>_CAT_<n>).
        var door = doc.FindActor(cac.ChildActor.Value)!;
        Assert.Equal("BP_SingleDoorSaloonOutpost_Flip_GEN_VARIABLE_BP_SingleDoorSaloonOutpost_Flip_C_CAT_0", door.Name);
        Assert.Equal(cac.ExportIndex, door.ParentComponent);
        var mesh = Assert.Single(door.Components, c => c.StaticMeshPath is not null);
        Assert.Equal(DoorMesh, mesh.StaticMeshPath);
        Assert.True(mesh.WorldTransform.Equals(cac.WorldTransform, Tolerance), $"{mesh.WorldTransform} vs {cac.WorldTransform}");

        // ... so it is not expanded a second time inside the saloon.
        Assert.DoesNotContain(saloon.Components, c => c.Name.StartsWith(DoorComponent + "/", StringComparison.Ordinal));

        using var json = JsonDocument.Parse(doc.ToJson());
        var actors = json.RootElement.GetProperty("actors").EnumerateArray().ToList();
        var cacJson = actors.Single(a => a.GetProperty("name").GetString() == saloon.Name)
            .GetProperty("components").EnumerateArray().Single(c => c.GetProperty("name").GetString() == DoorComponent);
        Assert.Equal(door.Name, cacJson.GetProperty("childActor").GetString());
        Assert.Equal(DoorClass, cacJson.GetProperty("childActorClass").GetString());
        Assert.Equal(saloon.Name + "." + DoorComponent,
            actors.Single(a => a.GetProperty("name").GetString() == door.Name).GetProperty("parentComponent").GetString());
    }

    /// <summary>
    /// A level that stores only the saloon actor and its root: the reader rebuilds the other 52 components from the
    /// Blueprint's construction script and spawns the door child actor, matching what the real level stores.
    /// </summary>
    [MapSliceFact]
    public void SynthesizesConstructionScriptAndChildActorComponentsTheLevelDoesNotStore()
    {
        using var catalog = MapSlice.Open();
        var real = LevelDocument.Load(new Cue4ParseLevelReader(catalog), SaloonLevel);
        var saloon = real.FindActor("BP_SaloonOutpost_2")!;
        var realDoorMesh = real.FindActor(saloon.FindComponent(DoorComponent)!.ChildActor!.Value)!.FindComponent("Door Mesh")!;

        using var temp = new LevelTempDirectory();
        const string levelName = "A_0_SynthSaloon";
        SyntheticLevels.WriteBlueprintOnlyLevel(temp.Path, levelName, SaloonBlueprint, "BP_SaloonOutpost_Synth",
            saloon.Root!.Relative.Location, saloon.Root.Relative.Rotation);
        using var overlay = MapSlice.Open(temp.Path);
        var doc = LevelDocument.Load(new Cue4ParseLevelReader(overlay), MapSlice.MapsPath + levelName);
        var actor = doc.FindActor("BP_SaloonOutpost_Synth")!;
        Assert.Equal(ActorKind.Blueprint, actor.Kind);
        Assert.Equal("DefaultSceneRoot", actor.Root!.Name);
        Assert.False(actor.Root.IsSynthesized);
        Assert.True(actor.WorldTransform.Equals(saloon.WorldTransform, Tolerance));

        var compared = 0;
        foreach (var expected in saloon.Components.Where(c => c.Name != "DefaultSceneRoot"))
        {
            var c = actor.FindComponent(expected.Name);
            Assert.True(c is not null, $"{expected.Name} was not synthesized");
            Assert.True(c.IsSynthesized);
            Assert.True(c.UsesTemplateValues);
            Assert.True(c.ExportIndex < -1);
            Assert.EndsWith(":" + expected.Name + "_GEN_VARIABLE", c.TemplatePath, StringComparison.Ordinal);
            Assert.Equal(expected.ClassName, c.ClassName);
            Assert.Equal(expected.IsStaticMeshComponent, c.IsStaticMeshComponent);
            Assert.Equal(expected.IsVisible, c.IsVisible);
            Assert.Equal(expected.ChildActorClassPath, c.ChildActorClassPath);
            Assert.Equal(expected.ParentName(saloon), c.ParentName(actor));
            if (!expected.PropertyNames.Contains("StaticMesh"))
            {
                Assert.Equal(expected.StaticMeshPath, c.StaticMeshPath);
            }

            // The level may store transform deltas; where it does not, the template must place the component identically.
            if (!expected.PropertyNames.Any(p => p.StartsWith("Relative", StringComparison.Ordinal) || p.StartsWith("bAbsolute", StringComparison.Ordinal)))
            {
                Assert.True(c.WorldTransform.Equals(expected.WorldTransform, Tolerance),
                    $"{c.Name}: {c.WorldTransform.Translation} vs {expected.WorldTransform.Translation}");
                compared++;
            }
        }

        Assert.True(compared >= 40, $"only {compared} transforms compared");

        // The door child actor (class package in the slice) is spawned into the saloon: root snapped to the component.
        var cac = actor.FindComponent(DoorComponent)!;
        var doorRoot = actor.FindComponent(DoorComponent + "/Root")!;
        Assert.True(doorRoot.IsSynthesized);
        Assert.Equal(cac.ExportIndex, doorRoot.AttachParent);
        var doorMesh = actor.FindComponent(DoorComponent + "/Door Mesh")!;
        Assert.Equal(DoorMesh, doorMesh.StaticMeshPath);
        Assert.Equal(doorRoot.ExportIndex, doorMesh.AttachParent);
        Assert.True(doorMesh.WorldTransform.Equals(realDoorMesh.WorldTransform, Tolerance),
            $"{doorMesh.WorldTransform.Translation} vs {realDoorMesh.WorldTransform.Translation}");

        // The tire stacks' class is not in the slice: reported, not expanded.
        Assert.Contains(doc.Warnings, w => w.Contains("BP_Debris_Tire_Stack_01_NoSmoke_C", StringComparison.Ordinal));
        Assert.DoesNotContain(actor.Components, c => c.Name.StartsWith("BP_Debris_Tire_Stack_01_NoSmoke/", StringComparison.Ordinal));
        Assert.Equal(saloon.Components.Count + 2, actor.Components.Count);
        Assert.Equal(actor.Components.Count - 1, actor.SynthesizedComponentCount);

        // Every mesh of the real saloon (plus the stored door) is placed by the synthesized one.
        Assert.Equal(
            saloon.Components.Count(c => c.StaticMeshPath is not null) + 1,
            actor.Components.Count(c => c.StaticMeshPath is not null));

        // JSON marks them; edits refuse them; without expansion only the stored root remains.
        using var json = JsonDocument.Parse(doc.ToJson());
        var jsonActor = json.RootElement.GetProperty("actors").EnumerateArray().Single(a => a.GetProperty("name").GetString() == actor.Name);
        Assert.Equal(actor.SynthesizedComponentCount, jsonActor.GetProperty("synthesizedCount").GetInt32());
        var jsonDoor = jsonActor.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("name").GetString() == DoorComponent + "/Door Mesh");
        Assert.True(jsonDoor.GetProperty("synthesized").GetBoolean());
        Assert.Equal(DoorComponent + "/Root", jsonDoor.GetProperty("parent").GetString());
        Assert.Throws<ArgumentException>(() => EditOpFactory.SetTransform(doc, actor, TransformValue.Identity, component: "SM_SaloonBuilding_Part1_Outpost"));
        EditOpFactory.SetTransform(doc, actor, TransformValue.Identity); // the stored root can be moved

        var flat = LevelDocument.Load(new Cue4ParseLevelReader(overlay, new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false }), MapSlice.MapsPath + levelName);
        Assert.Single(flat.FindActor(actor.Name)!.Components);
    }

    [MapSliceFact]
    public void ReadsInheritedComponentOverridesOfDerivedBlueprints()
    {
        // The direction signs derive from BP_BaseSign_C (not in the slice) and override two of its components.
        using var catalog = MapSlice.Open();
        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), MapSlice.MapsPath + "A_0_Outpost_Exterior_02");
        var signs = doc.Actors.Where(a => a.ClassName.StartsWith("BP_Sign_OUT_", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(signs);
        Assert.All(signs, s =>
        {
            Assert.Equal(0, s.SynthesizedComponentCount);
            Assert.Contains(s.Components, c => c.StaticMeshPath?.Contains("/Direction_Signs/", StringComparison.Ordinal) == true);
        });
        Assert.Contains(doc.Warnings, w => w.Contains("BP_BaseSign_C", StringComparison.Ordinal));
    }

    /// <summary>Synthesized components in <see cref="LevelData"/> (no game data needed).</summary>
    [Fact]
    public void SynthesizedComponentsJoinTheirActorWithSyntheticIds()
    {
        const string walls = "/Game/X/SM_Walls.SM_Walls";
        const string proxy = "/Game/X/SM_Proxy.SM_Proxy";
        const string door = "/Game/X/SM_Door.SM_Door";
        var b = new FakeLevelBuilder("/Game/ConZ_Files/Maps/The_Island/A_0_Synth");
        var house = b.Actor("BP_House_C_1", "/Game/X/BP_House.BP_House_C", blueprint: true);
        var root = b.Component(house, "DefaultSceneRoot", location: new FVector(1000, 0, 0), rotation: new FRotator(0, 90, 0));
        b.Root(house, root);
        var data = b.Build() with
        {
            SynthesizedComponents =
            [
                Synthesized(-2, "Proxy", house, root, proxy, new FVector(0, 0, 0)) with { IsVisible = false },
                Synthesized(-3, "Walls", house, root, walls, new FVector(100, 0, 0)),
                Synthesized(-4, "Door", house, -3, null, new FVector(0, 0, 10)) with
                {
                    ClassName = "ChildActorComponent",
                    ChildActorClassPath = "/Game/X/BP_Door.BP_Door_C",
                    IsStaticMeshComponent = false,
                },
                Synthesized(-5, "Door/Door Mesh", house, -4, door, FVector.Zero),
            ],
        };

        var doc = LevelDocument.FromData(data);
        var actor = doc.FindActor("BP_House_C_1")!;
        Assert.Equal(5, actor.Components.Count);
        Assert.Equal(4, actor.SynthesizedComponentCount);
        var wallsRecord = actor.FindComponent("Walls")!;
        Assert.True(wallsRecord.IsSynthesized);
        Assert.Equal(-3, wallsRecord.ExportIndex);
        Assert.Equal(root, wallsRecord.AttachParent);
        Assert.Equal("/Game/X/BP_House.BP_House_C:Walls_GEN_VARIABLE", wallsRecord.TemplatePath);
        Assert.True(new FVector(1000, 100, 0).Equals(wallsRecord.WorldTransform.Translation, 1e-3f), wallsRecord.WorldTransform.ToString());
        var doorMesh = actor.FindComponent("Door/Door Mesh")!;
        Assert.Equal(-4, doorMesh.AttachParent);
        Assert.True(new FVector(1000, 100, 10).Equals(doorMesh.WorldTransform.Translation, 1e-3f), doorMesh.WorldTransform.ToString());

        // Representative mesh: the first visible, non-instanced mesh hanging off the root (the hidden proxy is skipped).
        Assert.Equal(walls, actor.StaticMeshPath);
        Assert.False(EditOpFactory.Matches(actor, new KindMatch(MatchBy.StaticMesh, walls)));

        using var json = JsonDocument.Parse(doc.ToJson());
        var jsonActor = json.RootElement.GetProperty("actors")[0];
        Assert.Equal(4, jsonActor.GetProperty("synthesizedCount").GetInt32());
        var components = jsonActor.GetProperty("components").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.False(components["DefaultSceneRoot"].TryGetProperty("synthesized", out _));
        Assert.True(components["Walls"].GetProperty("synthesized").GetBoolean());
        Assert.Equal("/Game/X/BP_House.BP_House_C:Walls_GEN_VARIABLE", components["Walls"].GetProperty("template").GetString());
        Assert.False(components["Proxy"].GetProperty("visible").GetBoolean());
        Assert.False(components["Walls"].TryGetProperty("visible", out _));
        Assert.Equal("Door", components["Door/Door Mesh"].GetProperty("parent").GetString());
        Assert.Equal("/Game/X/BP_Door.BP_Door_C", components["Door"].GetProperty("childActorClass").GetString());

        Assert.Throws<ArgumentException>(() => EditOpFactory.SetTransform(doc, actor, TransformValue.Identity, component: "Walls"));
        Assert.Equal(TransformValue.Identity, EditOpFactory.SetTransform(doc, actor, TransformValue.Identity).New);
    }

    private static LevelExportData Synthesized(int id, string name, int actor, int parent, string? mesh, FVector location) => new()
    {
        Index = id,
        Name = name,
        ClassName = "InteriorStaticMeshComponent",
        ClassPath = "/Script/SCUM.InteriorStaticMeshComponent",
        OuterIndex = actor,
        TemplatePath = "/Game/X/BP_House.BP_House_C:" + name + "_GEN_VARIABLE",
        IsLoaded = true,
        IsComponent = true,
        IsSceneComponent = true,
        IsStaticMeshComponent = true,
        IsSynthesized = true,
        UsesTemplateValues = true,
        RelativeLocation = location,
        AttachParent = parent,
        StaticMesh = mesh,
    };
}

/// <summary>Helpers comparing components across two documents by their parents' names.</summary>
internal static class ComponentRecordTestExtensions
{
    /// <summary>Name of the component's parent within <paramref name="actor"/> (null when unattached).</summary>
    public static string? ParentName(this ComponentRecord component, ActorRecord actor) =>
        component.AttachParent is { } p ? actor.Components.FirstOrDefault(c => c.ExportIndex == p)?.Name : null;
}
