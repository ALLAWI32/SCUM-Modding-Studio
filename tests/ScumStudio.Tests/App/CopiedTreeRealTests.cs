using System.Buffers.Binary;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Hektor (Discord): a copied tree could not be chopped. A tree duplicated on the Map page is a new instance of its own
/// foliage component: the export appends it to <c>PerInstanceSMData</c> (reorder table and custom floats grown, the
/// cooked draw copy dropped so the game rebuilds its tree), and the game's reader counts one tree more. Real game files
/// only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class CopiedTreeRealTests
{
    private const string Level = "/Game/ConZ_Files/Maps/The_Island/Landscape_A_4_1c";
    private const string Oaks = "FoliageInstancedTree_25"; // 9 instances

    private readonly ITestOutputHelper _output;

    public CopiedTreeRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ADuplicatedTreeIsExportedAsOneMoreInstanceOfItsFoliage()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Trees");
        await map.LoadLevelsAsync([Level]);
        var project = ctx.Services.Projects.Current!;

        var foliage = map.AllActors.Single(a => a.Name == "InstancedFoliageActor_0");
        var pristine = foliage.Actor.InstanceTransforms.Where(i => i.ComponentName == Oaks).OrderBy(i => i.InstanceIndex).ToList();
        Assert.Equal(9, pristine.Count);
        map.SelectedInstanceKey = InstanceKey.Of(foliage.SelectableId, Oaks, 2);
        map.SelectedActorId = foliage.SelectableId;
        Assert.True(map.HasSelectedInstance);

        map.DuplicateSelectedCommand.Execute(null);
        var add = Assert.IsType<AddInstanceOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(9, add.Target.Index);
        var copy = InstanceKey.Of(foliage.SelectableId, Oaks, 9);
        Assert.Equal(copy, map.SelectedInstanceKey);
        Assert.True(map.HasSelectedInstance);
        Assert.DoesNotContain(map.AllActors, a => a.IsAdded); // no mesh actor copy

        // Moved 3 m along +Y on its own index.
        var world = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(foliage.SelectableId, world with { Translation = world.Translation + new FVector(0f, 300f, 0f) });
        var move = Assert.IsType<SetInstanceTransformOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(9, move.Target.Index);
        var local = move.New.ToTransform();
        _output.WriteLine($"copy of {Oaks}[2] at {local.Translation} (component space), world {map.InstanceTransforms[copy].Translation}");

        var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        Assert.Equal(1, Assert.Single(result.Levels).Report.AddedInstances);

        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var package = ModdableAssets.ReadPackage(written, Level);
        var index = Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName) == Oaks);
        var payload = package.GetExportData(index).ToArray();
        var block = package.ReadProperties(index);

        // The array grew by one: the stored nine first, the copy last.
        var known = pristine.Select(p => p.LocalTransform).Append(local).ToList();
        var (offset, size) = LevelPackageEditor.FindInstanceArray(payload, block.EndOffset, known) ?? throw new InvalidOperationException("instance array not found");
        var arrayEnd = offset + (size * 10);

        // The custom floats grew with it, the reorder table has a draw index for it, and the draw copy is gone.
        var floats = block.Find("NumCustomDataFloats")?.Value is IntValue n ? n.Value : 0;
        Assert.Equal(10 * floats, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(arrayEnd + 4)));
        var reorder = block.Find("InstanceReorderTable")?.Value as ArrayValue;
        _output.WriteLine($"{Oaks}: {floats} custom float(s) per instance, reorder table {(reorder is null ? "absent" : reorder.Items.Count + " entries")}, NumBuiltInstances {block.Find("NumBuiltInstances")?.Value}");
        if (reorder is not null)
        {
            Assert.Equal(10, reorder.Items.Count);
            Assert.Equal(9, Assert.IsType<IntValue>(reorder.Items[^1]).Value);
        }

        Assert.Null(InstanceRenderData.Layout(payload, arrayEnd, 9));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(arrayEnd + 8 + (4 * 10 * floats))));

        // The game's reader counts ten oaks, the last one where the copy was dragged to.
        var component = written.LoadPackage(Level).GetExports().OfType<UInstancedStaticMeshComponent>().Single(c => c.Name == Oaks);
        Assert.Equal(10, component.PerInstanceSMData!.Length);
        var t = component.PerInstanceSMData[9].TransformData;
        Assert.Equal(local.Translation.X, (float)t.Translation.X, 1f);
        Assert.Equal(local.Translation.Y, (float)t.Translation.Y, 1f);
        Assert.Equal(local.Translation.Z, (float)t.Translation.Z, 1f);
        Assert.Equal(pristine[2].LocalTransform.Translation.X, (float)component.PerInstanceSMData[2].TransformData.Translation.X, 1f);

        // Delete takes the copy out of the project again.
        map.DeleteSelectedCommand.Execute(null);
        Assert.IsType<RemoveAddedInstanceOp>(project.Journal.Applied[^1].Op);
        Assert.DoesNotContain(copy, map.InstanceTransforms.Keys);
    }
}
