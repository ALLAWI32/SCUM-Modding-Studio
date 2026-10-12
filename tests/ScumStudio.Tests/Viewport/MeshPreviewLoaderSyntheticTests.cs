using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.IO;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner: "there are trees that do not show in 3D … the object itself, its colour". What <see cref="MeshPreviewLoader"/>
/// resolves for a foliage type, a Blueprint component's override materials, a child Blueprint's inherited component
/// override, a child actor and an attachment's soft mesh, on synthetic cooked packages read back through CUE4Parse. The
/// meshes are not in these packages, so the resolution is checked, not the drawing (that is <see cref="PreviewRealTests"/>).
/// </summary>
public sealed class MeshPreviewLoaderSyntheticTests
{
    private const string Engine = "/Script/Engine";

    [Fact]
    public void FoliageTypeResolvesItsMeshAndOverrides()
    {
        using var temp = new LevelTempDirectory();
        var p = new SyntheticLevelPackage();
        var mesh = p.ObjectImport("/Game/T/SM_Tree", Engine, "StaticMesh", "SM_Tree");
        var leaves = p.ObjectImport("/Game/T/MI_Leaves", Engine, "MaterialInstanceConstant", "MI_Leaves");
        var type = p.Export("Tree_FT", p.ScriptClass("/Script/Foliage", "FoliageType_InstancedStaticMesh"), 0);
        p.SetPayload(type, p.Properties(t =>
        {
            t.Object("Mesh", mesh);
            t.ObjectArray("OverrideMaterials", leaves);
        }));
        Write(temp, "Tree_FT", p);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var loader = new MeshPreviewLoader(catalog) { TextureSize = 0 };

        var resolved = loader.ResolveFoliageType("/Game/T/Tree_FT.Tree_FT");
        Assert.Equal("/Game/T/SM_Tree.SM_Tree", resolved.Mesh);
        Assert.Equal(new string?[] { "/Game/T/MI_Leaves.MI_Leaves" }, resolved.OverrideMaterials);

        // The tree's mesh is not among these packages: the preview names it (before: "is a FoliageType…, not a mesh").
        var error = Assert.Throws<InvalidDataException>(() => loader.Load("/Game/T/Tree_FT.Tree_FT", "/Game/T/Tree_FT", "FoliageType_InstancedStaticMesh"));
        Assert.Contains("SM_Tree", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlueprintComponentOverrideMaterialsAreUsed()
    {
        using var temp = new LevelTempDirectory();
        WriteRedBox(temp);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var component = Assert.Single(new MeshPreviewLoader(catalog).ResolveComponents("/Game/T/BP_Red"));
        Assert.Equal("/Game/T/SM_Box.SM_Box", component.Mesh);
        Assert.Equal(new string?[] { "/Game/T/M_Red.M_Red" }, component.OverrideMaterials!);
    }

    [Fact]
    public void ChildBlueprintIchTemplateWins()
    {
        using var temp = new LevelTempDirectory();
        var (parent, parentClass, _) = NewBlueprint("BP_Parent");
        var meshA = parent.ObjectImport("/Game/T/SM_A", Engine, "StaticMesh", "SM_A");
        ScsNode(parent, parentClass, "Mesh", "StaticMeshComponent", t => t.Object("StaticMesh", meshA));
        Write(temp, "BP_Parent", parent);

        // The child Blueprint changes the inherited component's mesh through its InheritableComponentHandler.
        var (child, childClass, _) = NewBlueprint("BP_Child", "/Game/T/BP_Parent");
        var meshB = child.ObjectImport("/Game/T/SM_B", Engine, "StaticMesh", "SM_B");
        var handler = child.Export("InheritableComponentHandler", child.ScriptClass(Engine, "InheritableComponentHandler"), childClass);
        var template = child.Export("Mesh_GEN_VARIABLE", child.ScriptClass(Engine, "StaticMeshComponent"), childClass);
        child.SetPayload(template, child.Properties(t => t.Object("StaticMesh", meshB), w => w.I32(0)));
        child.SetPayload(handler, child.Properties(t => t.StructArray("Records", "ComponentOverrideRecord", r =>
        {
            r.Object("ComponentTemplate", template);
            r.TaggedStruct("ComponentKey", "ComponentKey", k => k.NameValue("SCSVariableName", "Mesh"));
        })));
        Write(temp, "BP_Child", child);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var component = Assert.Single(new MeshPreviewLoader(catalog).ResolveComponents("/Game/T/BP_Child"));
        Assert.Equal("/Game/T/SM_B.SM_B", component.Mesh); // before: the parent's SM_A
    }

    [Fact]
    public void ChildActorComponentBringsItsClassMeshes()
    {
        using var temp = new LevelTempDirectory();
        WriteRedBox(temp);
        var (holder, holderClass, _) = NewBlueprint("BP_Holder");
        var red = holder.ObjectImport("/Game/T/BP_Red", Engine, "BlueprintGeneratedClass", "BP_Red_C");
        ScsNode(holder, holderClass, "Seat", "ChildActorComponent", t =>
        {
            t.Object("ChildActorClass", red);
            t.Vector("RelativeLocation", 100, 0, 0);
        });
        Write(temp, "BP_Holder", holder);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var component = Assert.Single(new MeshPreviewLoader(catalog).ResolveComponents("/Game/T/BP_Holder"));
        Assert.Equal("/Game/T/SM_Box.SM_Box", component.Mesh);
        Assert.Equal(new string?[] { "/Game/T/M_Red.M_Red" }, component.OverrideMaterials!);
        Assert.Equal(100f, component.Transform.Translation.X, 3);
    }

    [Fact]
    public void InstancedComponentDrawsEveryInstance()
    {
        // A fence Blueprint's instanced posts: one mesh per instance where the instance sits (before: one at the origin).
        using var temp = new LevelTempDirectory();
        var (p, cls, _) = NewBlueprint("BP_Fence");
        var post = p.ObjectImport("/Game/T/SM_Post", Engine, "StaticMesh", "SM_Post");
        Scs(p, cls, new ScsPart("Posts", "InstancedStaticMeshComponent", t => t.Object("StaticMesh", post), InstancesAt(0f, 200f, 400f)));
        Write(temp, "BP_Fence", p);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var components = new MeshPreviewLoader(catalog).ResolveComponents("/Game/T/BP_Fence");
        Assert.Equal(new[] { 0f, 200f, 400f }, components.Select(c => MathF.Round(c.Transform.Translation.X)));
        Assert.All(components, c => Assert.Equal("/Game/T/SM_Post.SM_Post", c.Mesh));
    }

    [Fact]
    public void InstancesNeverStarveLaterComponents()
    {
        // A wall Blueprint lists 1100 instanced bricks before its door: the door must still be drawn (the walk's cap of
        // 1024 components counted every instance part, so the bricks used it up).
        using var temp = new LevelTempDirectory();
        var (p, cls, _) = NewBlueprint("BP_Wall");
        var brick = p.ObjectImport("/Game/T/SM_Brick", Engine, "StaticMesh", "SM_Brick");
        var door = p.ObjectImport("/Game/T/SM_Door", Engine, "StaticMesh", "SM_Door");
        Scs(p, cls,
            new ScsPart("Bricks", "InstancedStaticMeshComponent", t => t.Object("StaticMesh", brick), InstancesAt([.. Enumerable.Range(0, 1100).Select(i => i * 10f)])),
            new ScsPart("Door", "StaticMeshComponent", t => t.Object("StaticMesh", door)));
        Write(temp, "BP_Wall", p);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var components = new MeshPreviewLoader(catalog).ResolveComponents("/Game/T/BP_Wall");
        Assert.Equal(1100, components.Count(c => c.Mesh == "/Game/T/SM_Brick.SM_Brick"));
        Assert.Single(components, c => c.Mesh == "/Game/T/SM_Door.SM_Door");
    }

    [Fact]
    public void InstancesShareOneBudgetPerBlueprint()
    {
        // Two instanced components of 3000 each: 4096 instances in all (before: 4096 each, and the Map's merged stand-in
        // copied every one), and the component after them is still drawn.
        using var temp = new LevelTempDirectory();
        var (p, cls, _) = NewBlueprint("BP_Stack");
        var plank = p.ObjectImport("/Game/T/SM_Plank", Engine, "StaticMesh", "SM_Plank");
        var box = p.ObjectImport("/Game/T/SM_Box", Engine, "StaticMesh", "SM_Box");
        var many = InstancesAt([.. Enumerable.Range(0, 3000).Select(i => i * 1f)]);
        Scs(p, cls,
            new ScsPart("PlanksA", "InstancedStaticMeshComponent", t => t.Object("StaticMesh", plank), many),
            new ScsPart("PlanksB", "InstancedStaticMeshComponent", t => t.Object("StaticMesh", plank), many),
            new ScsPart("Box", "StaticMeshComponent", t => t.Object("StaticMesh", box)));
        Write(temp, "BP_Stack", p);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var components = new MeshPreviewLoader(catalog).ResolveComponents("/Game/T/BP_Stack");
        Assert.Equal(4096, components.Count(c => c.Mesh == "/Game/T/SM_Plank.SM_Plank"));
        Assert.Single(components, c => c.Mesh == "/Game/T/SM_Box.SM_Box");
    }

    [Fact]
    public void ChildActorCycleStops()
    {
        // BP_Loop's child actor is BP_Loop itself: its own box once, no endless (or 4-deep) nesting.
        using var temp = new LevelTempDirectory();
        var (p, cls, _) = NewBlueprint("BP_Loop");
        var box = p.ObjectImport("/Game/T/SM_Box", Engine, "StaticMesh", "SM_Box");
        Scs(p, cls,
            new ScsPart("Self", "ChildActorComponent", t => t.Object("ChildActorClass", cls)),
            new ScsPart("Box", "StaticMeshComponent", t => t.Object("StaticMesh", box)));
        Write(temp, "BP_Loop", p);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var component = Assert.Single(new MeshPreviewLoader(catalog).ResolveComponents("/Game/T/BP_Loop"));
        Assert.Equal("/Game/T/SM_Box.SM_Box", component.Mesh);
    }

    [Fact]
    public void FoliageActorTypeResolvesItsBlueprint()
    {
        // A FoliageType_Actor paints a Blueprint (ActorClass), not a mesh.
        using var temp = new LevelTempDirectory();
        WriteRedBox(temp);
        var p = new SyntheticLevelPackage();
        var actor = p.ObjectImport("/Game/T/BP_Red", Engine, "BlueprintGeneratedClass", "BP_Red_C");
        var type = p.Export("Bush_FT", p.ScriptClass("/Script/Foliage", "FoliageType_Actor"), 0);
        p.SetPayload(type, p.Properties(t => t.Object("ActorClass", actor)));
        Write(temp, "Bush_FT", p);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var loader = new MeshPreviewLoader(catalog) { TextureSize = 0 };

        var resolved = loader.ResolveFoliageType("/Game/T/Bush_FT.Bush_FT");
        Assert.Null(resolved.Mesh);
        Assert.Equal("/Game/T/BP_Red.BP_Red_C", resolved.ActorClass);
        Assert.Equal("/Game/T/SM_Box.SM_Box", Assert.Single(loader.ResolveComponents("/Game/T/BP_Red")).Mesh);

        // SM_Box is not among these packages: the error names the actor (before: "names no mesh").
        var error = Assert.Throws<InvalidDataException>(() => loader.Load("/Game/T/Bush_FT.Bush_FT", "/Game/T/Bush_FT", "FoliageType_Actor"));
        Assert.Contains("BP_Red", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindMeshPathKeepsToTheMeshTheItemShows()
    {
        // The Map's loot stand-ins come from FindMeshPath: an item naming another mesh (its magazine, its dropped
        // variant) on its defaults has no mesh of its own and keeps its icon.
        using var temp = new LevelTempDirectory();
        var (card, _, cdo) = NewBlueprint("BP_KeyCard");
        var magazine = card.ObjectImport("/Game/T/SM_Magazine", Engine, "StaticMesh", "SM_Magazine");
        card.SetPayload(cdo, card.Properties(t =>
        {
            t.Object("_droppedModel", magazine);
            t.SoftObject("_attachedMesh", "/Game/T/SK_Strap.SK_Strap");
        }));
        Write(temp, "BP_KeyCard", card);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        Assert.Null(new MeshPreviewLoader(catalog).FindMeshPath("/Game/T/BP_KeyCard")); // before: SM_Magazine
    }

    [Fact]
    public void AttachmentSoftMeshIsFound()
    {
        // SCUM's vehicle parts (BPC_WolfsWagen_Door_FrontLeft) name their mesh only in the soft MeshSetup.Mesh.
        using var temp = new LevelTempDirectory();
        var (door, _, cdo) = NewBlueprint("BPC_Door");
        door.SetPayload(cdo, door.Properties(t => t.TaggedStruct("MeshSetup", "VehicleAttachmentMeshSetup", m => m.SoftObject("Mesh", "/Game/T/SK_Door.SK_Door"))));
        Write(temp, "BPC_Door", door);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        Assert.Equal("/Game/T/SK_Door.SK_Door", new MeshPreviewLoader(catalog).FindMeshPath("/Game/T/BPC_Door")); // before: null
    }

    [Fact]
    public void GroupSectionsUsesSlotIndex()
    {
        // Two slots with the same material: only the second is overridden (the Map's F157 mix-up keyed them by path).
        var data = MeshData.Create("SM", new float[9], [0, 1, 2, 0, 1, 2], sections:
        [
            new MeshSection("/Game/M_A.M_A", 0, 3) { SlotIndex = 0 },
            new MeshSection("/Game/M_A.M_A", 3, 3) { SlotIndex = 1 },
        ]);

        var groups = MeshPreviewLoader.GroupSections(data, [null, "/Game/M_B.M_B", "/Game/M_C.M_C"]);
        Assert.Equal(new[] { "/Game/M_A.M_A", "/Game/M_B.M_B" }, groups.Select(g => g.Material));
        Assert.All(groups, g => Assert.Single(g.Sections));
        Assert.Equal(new[] { "/Game/M_A.M_A" }, MeshPreviewLoader.GroupSections(data, null).Select(g => g.Material));
    }

    /// <summary>BP_Red: one construction-script component drawing SM_Box with M_Red on its first slot.</summary>
    private static void WriteRedBox(LevelTempDirectory temp)
    {
        var (p, cls, _) = NewBlueprint("BP_Red");
        var box = p.ObjectImport("/Game/T/SM_Box", Engine, "StaticMesh", "SM_Box");
        var red = p.ObjectImport("/Game/T/M_Red", Engine, "Material", "M_Red");
        ScsNode(p, cls, "Box", "StaticMeshComponent", t =>
        {
            t.Object("StaticMesh", box);
            t.ObjectArray("OverrideMaterials", red);
        });
        Write(temp, "BP_Red", p);
    }

    /// <summary>A cooked Blueprint package: the class export (parent: an engine actor or <paramref name="parentPackage"/>'s class) and its CDO.</summary>
    private static (SyntheticLevelPackage Package, int Class, int Cdo) NewBlueprint(string name, string? parentPackage = null)
    {
        var p = new SyntheticLevelPackage();
        var super = parentPackage is null
            ? p.ScriptClass(Engine, "Actor")
            : p.ObjectImport(parentPackage, Engine, "BlueprintGeneratedClass", parentPackage[(parentPackage.LastIndexOf('/') + 1)..] + "_C");
        var cls = p.ClassExport(name + "_C", p.ScriptClass(Engine, "BlueprintGeneratedClass"), super);
        var cdo = p.Export("Default__" + name + "_C", p.ScriptClass(Engine, "Actor"), 0);
        p.SetPayload(cls, p.Properties());
        p.SetPayload(cdo, p.Properties());
        return (p, cls, cdo);
    }

    /// <summary>A construction-script component: its variable, engine class, tagged properties and native data (none by default).</summary>
    private sealed record ScsPart(string Variable, string Class, Action<SyntheticLevelPackage.TagWriter> Template, Action<ByteWriter>? Native = null);

    /// <summary>A construction-script root node of <paramref name="cls"/> for <paramref name="variable"/> and its component template.</summary>
    private static void ScsNode(SyntheticLevelPackage p, int cls, string variable, string componentClass, Action<SyntheticLevelPackage.TagWriter> template) =>
        Scs(p, cls, new ScsPart(variable, componentClass, template));

    /// <summary>The construction script of <paramref name="cls"/>: one root node per part, in order, each with its component template.</summary>
    private static void Scs(SyntheticLevelPackage p, int cls, params ScsPart[] parts)
    {
        var scs = p.Export("SimpleConstructionScript_0", p.ScriptClass(Engine, "SimpleConstructionScript"), cls);
        var roots = new List<int>();
        for (var i = 0; i < parts.Length; i++)
        {
            var node = p.Export("SCS_Node_" + i, p.ScriptClass(Engine, "SCS_Node"), scs);
            var component = p.Export(parts[i].Variable + "_GEN_VARIABLE", p.ScriptClass(Engine, parts[i].Class), cls);
            var variable = parts[i].Variable;
            p.SetPayload(node, p.Properties(t =>
            {
                t.Object("ComponentTemplate", component);
                t.NameValue("InternalVariableName", variable);
            }));
            p.SetPayload(component, p.Properties(parts[i].Template, parts[i].Native ?? (w => w.I32(0))));
            roots.Add(node);
        }

        p.SetPayload(scs, p.Properties(t => t.ObjectArray("RootNodes", [.. roots])));
    }

    /// <summary>An instanced static mesh component's native data: one instance at each X (cooked FMatrix bulk array), no custom data.</summary>
    private static Action<ByteWriter> InstancesAt(params float[] xs) => w =>
    {
        w.I32(0); // LODData
        w.I32(0); // bCooked
        w.I32(64); // PerInstanceSMData: element size (FMatrix), count, matrices
        w.I32(xs.Length);
        foreach (var x in xs)
        {
            var m = new FTransform(new FVector(x, 0, 0)).ToMatrixWithScale();
            foreach (var v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
            {
                w.F32(v);
            }
        }

        w.I32(4); // PerInstanceSMCustomData: element size, count
        w.I32(0);
    };

    private static void Write(LevelTempDirectory temp, string name, SyntheticLevelPackage package) =>
        package.Build().WriteAsync(temp.Combine("SCUM", "Content", "T", name), ".uasset").GetAwaiter().GetResult();
}
