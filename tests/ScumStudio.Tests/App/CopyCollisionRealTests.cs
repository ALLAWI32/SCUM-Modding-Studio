using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "the trees I copy have no collision, and almost anything I copy". A copied tree became a StaticMeshActor that
/// collides as its mesh does by default, and SCUM's tree meshes default to SCUM_Foliage (players and cars pass); the
/// game's tree foliage collides as SCUM_TreeStump. Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class CopyCollisionRealTests
{
    private const string Level = "/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1b";

    [Fact]
    public async Task ACopiedTreeCollidesAsTheTreeAndAnOldCopyIsRepaired()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Trees");
        await map.LoadLevelsAsync([Level]);

        // The reader sees how the tree foliage collides (its mesh's own default, SCUM_Foliage, would let players through).
        var tree = map.AllActors.SelectMany(a => a.Actor.Components).First(c => c.Name.StartsWith("FoliageInstancedTree", StringComparison.Ordinal)
            && c.StaticMeshPath?.EndsWith(".CupressusTall_01", StringComparison.Ordinal) == true);
        Assert.Equal("SCUM_TreeStump", tree.CollisionProfile);

        // Duplicating a cypress the map draws adds an instance to the foliage it stands in (it collides as the foliage does;
        // CopiedTreeRealTests reads the grown array back).
        var (foliage, instance) = map.AllActors.SelectMany(a => a.Actor.InstanceTransforms.Select(i => (Item: a, Instance: i)))
            .First(x => x.Instance.StaticMeshPath?.EndsWith(".CupressusTall_01", StringComparison.Ordinal) == true);
        var source = foliage.Actor.FindComponent(instance.ComponentName)!;
        map.SelectedInstanceKey = InstanceKey.Of(foliage.SelectableId, instance.ComponentName, instance.InstanceIndex);
        map.SelectedActorId = foliage.SelectableId;
        map.DuplicateSelectedCommand.Execute(null);
        var copy = Assert.IsType<AddInstanceOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op);
        Assert.Equal(instance.ComponentName, copy.Target.Component);

        // A mesh-actor copy made by an older version (no profile recorded) of the same tree: repaired to the standing-tree profile.
        var at = source.WorldTransform.Translation + new ScumStudio.Core.Mathematics.FVector(500f, 0f, 0f);
        var old = new AddStaticMeshActorOp(Level, "CupressusTall_01_Old", instance.StaticMeshPath!, new ScumStudio.Level.Model.TransformValue(at, new ScumStudio.Core.Mathematics.FRotator(0f, 0f, 0f), ScumStudio.Core.Mathematics.FVector.One));
        ctx.Services.Projects.Apply(old);

        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        Assert.True(written.TryLoadPackage(Level, out var package));
        foreach (var (name, profileName) in new[] { (old.NewName, ProjectExporter.StandingTree) })
        {
            var component = package.GetExports().Single(e => e.Name == "StaticMeshComponent0" && e.Outer?.Name == name);
            Assert.False(component.GetOrDefault("bUseDefaultCollision", true));
            Assert.True(component.TryGetValue(out FStructFallback body, "BodyInstance"));
            Assert.True(body.TryGetValue(out FName profile, "CollisionProfileName"));
            Assert.Equal(profileName, profile.Text);
        }
    }
}
