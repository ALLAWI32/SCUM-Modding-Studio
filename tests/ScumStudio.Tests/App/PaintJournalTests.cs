using Avalonia.Media;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Apply paint and the project journal (audit F225, F226, F229): one journal step per apply, baselines that follow an Undo
/// or Redo while paint is pending, and the last armour kit chosen wins.
/// </summary>
public sealed class PaintJournalTests
{
    private const string Body = "/Game/Vehicles/Test/Materials/MI_Test_Body";
    private const string Doors = "/Game/Vehicles/Test/Materials/MI_Test_Doors";

    [Fact]
    public async Task ApplyPaintIsOneJournalStepAndOneProjectChange()
    {
        // F229: six changed values were six journal entries, six project changes and six reloads of the panel; one Ctrl+Z
        // took back one value (an armour half painted).
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Paint");
        using var page = new VehiclesPageViewModel(ctx.Services);
        var paints = new[] { Paint(Body), Paint(Doors) };
        page.Paints = paints;
        foreach (var paint in paints)
        {
            paint.Colour = Color.FromRgb(200, 20, 20);
            paint.Second = Color.FromRgb(20, 20, 200);
            paint.Metal = 0.9;
        }

        Assert.Equal(6, paints.Sum(p => p.Changes().Count()));
        page.PaintPendingCount = paints.Count(p => p.IsPending);
        var project = ctx.Services.Projects.Current!;
        var before = project.History.Count;
        var changes = 0;
        ctx.Services.Projects.Changed += (_, _) => changes++;

        page.ApplyPaintCommand.Execute(null);

        Assert.Equal(before + 1, project.History.Count);
        Assert.Equal(1, changes);
        Assert.Equal(6, project.State.AssetValueOverrides.Count);
        Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.Severity == ToastSeverity.Error);

        // One Ctrl+Z takes the whole paint back.
        ctx.Services.Projects.Undo();
        Assert.DoesNotContain(project.State.AssetValueOverrides, v => v.Current != v.Base);
    }

    [Fact]
    public async Task ARedoWhilePaintIsPendingKeepsApplyWorkingOnTheRedoneValues()
    {
        // F225: paint red, apply, Ctrl+Z, pick blue, Ctrl+Y: the panel kept the stock baseline, so Apply sent "stock → blue"
        // over the redone red and every value was refused (edit is out of date), with Apply and Discard greyed out.
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Paint");
        using var page = new VehiclesPageViewModel(ctx.Services);
        var paint = Paint(Body);
        page.ShowPaints([paint]);
        var state = ctx.Services.Projects.Current!.State;
        string Stored() => state.GetAssetValue(Body, "Default__MI", paint.ColourValue.Tunable.Path)?.Current ?? paint.ColourValue.Tunable.Value;

        paint.Colour = Color.FromRgb(220, 0, 0);
        page.ApplyPaintCommand.Execute(null);
        var red = Stored();
        Assert.False(paint.IsPending);

        page.UndoCommand.Execute(null);
        Assert.Equal(paint.ColourValue.Tunable.Value, Stored());
        Assert.Equal(PaintMaterialViewModel.ToSrgb(paint.ColourValue.Tunable.Value), paint.Colour); // the panel follows an unchanged paint

        paint.Colour = Color.FromRgb(0, 0, 220);
        Assert.Equal(1, page.PaintPendingCount);
        page.RedoCommand.Execute(null);
        Assert.Equal(red, Stored());
        Assert.Equal(Color.FromRgb(0, 0, 220), paint.Colour); // what was being chosen stays
        Assert.Equal(red, paint.ColourValue.Committed); // on top of what is applied now
        Assert.True(page.ApplyPaintCommand.CanExecute(null));
        Assert.True(page.DiscardPaintCommand.CanExecute(null));

        page.ApplyPaintCommand.Execute(null);
        Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.Severity == ToastSeverity.Error);
        Assert.Equal(PaintMaterialViewModel.Tone(Color.FromRgb(0, 0, 220), 1, 1).Z, TunableValue.ParseFloats(Stored(), 4)[2], 3);
        Assert.Equal(0, page.PaintPendingCount);
    }

    [FixturesFact]
    public async Task TwoQuickArmourSwitchesEndOnTheLastOne()
    {
        // F226: Light then Heavy while Light still loads; Heavy finished first, then Light's late result replaced it.
        using var ctx = AppTestContext.Create();
        using var page = new VehiclesPageViewModel(ctx.Services);
        Assert.True(await page.OpenLooseFolderAsync(FixturePaths.OrigRoot));
        Assert.True(await page.SelectAsync("BPC_WolfsWagen"));
        await page.PreviewCompletion;
        Assert.NotNull(page.Preview);

        var light = new TaskCompletionSource<PreviewModel?>();
        var heavy = new TaskCompletionSource<PreviewModel?>();
        page.PreviewLoaderOverride = (_, kit) => kit == "ArmorLight" ? light.Task : heavy.Task;
        var choices = new[] { new PaintArmourChoice(string.Empty), new PaintArmourChoice("ArmorLight"), new PaintArmourChoice("ArmorHeavy") };
        page.ArmourChoices = choices;
        page.Armour = choices[1];
        var lightLoad = page.PreviewCompletion;
        page.Armour = choices[2];
        var heavyLoad = page.PreviewCompletion;

        heavy.SetResult(new PreviewModel("Heavy", [], new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>()));
        await heavyLoad;
        light.SetResult(new PreviewModel("Light", [], new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>()));
        await lightLoad;

        Assert.Equal("Heavy", page.Preview!.Name);
        Assert.Same(choices[2], page.Armour);
        Assert.False(page.IsPreviewLoading);
    }

    [Fact]
    public void AValueThatOnlyDiffersInFormIsNoChange()
    {
        // Review 3.5.1: Apply paint leaves out a value whose text only differs in form from the recorded one, but the panel
        // still counted it as a change: after an apply that recorded the real ones it stayed pending, Apply still enabled.
        PaintValue Value(string path, TunableKind kind, string stock) => new(Body, new Tunable("Default__MI", path, kind, stock), stock);
        var paint = new PaintMaterialViewModel(Body + ".MI_Test_Body", "Body", Value("VectorParameterValues[0].ParameterValue", TunableKind.Color, TunableValue.Format(0.2f, 0.2f, 0.2f, 1f)), null, null, null,
            roughness: Value("ScalarParameterValues[2].ParameterValue", TunableKind.Float, "0.5"), roughnessB: Value("ScalarParameterValues[3].ParameterValue", TunableKind.Float, "0.510"));
        Assert.False(paint.IsPending);

        paint.Roughness = 0.51; // colour B already has it, written another way
        var change = Assert.Single(paint.Changes());
        Assert.Equal(("ScalarParameterValues[2].ParameterValue", "0.51"), (change.Value.Tunable.Path, change.Text));
        Assert.True(paint.IsPending);
    }

    /// <summary>A car paint with a colour, a second colour, metal and clear coat, all at their stock values.</summary>
    internal static PaintMaterialViewModel Paint(string package)
    {
        PaintValue Value(string path, TunableKind kind, string stock) => new(package, new Tunable("Default__MI", path, kind, stock), stock);
        var grey = TunableValue.Format(0.2f, 0.2f, 0.2f, 1f);
        return new PaintMaterialViewModel(package + "." + package[(package.LastIndexOf('/') + 1)..], "Body",
            Value("VectorParameterValues[0].ParameterValue", TunableKind.Color, grey),
            Value("VectorParameterValues[1].ParameterValue", TunableKind.Color, grey),
            Value("ScalarParameterValues[0].ParameterValue", TunableKind.Float, "0.2"),
            Value("ScalarParameterValues[1].ParameterValue", TunableKind.Float, "0.5"));
    }
}
