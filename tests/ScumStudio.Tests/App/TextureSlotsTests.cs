using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Textures;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Formats;
using ScumStudio.Tests.Level;
using ScumStudio.Tests.Modding;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Textures panel (m7ammed: "tools for editing materials and importing textures"): a clone's paint material lists its
/// texture slots, and Replace is one journal step carrying the new pixels, the material's parameter and its streaming entry.
/// </summary>
public sealed class TextureSlotsTests
{
    private const string Material = "/Game/Tests/Materials/MI_Body";

    private static readonly TextureReplacePlan Plan = new(
        "/Game/Tests/Clone/Textures/T_CarGold_Body_Gold_DM", "T_CarGold_Body_Gold_DM", SyntheticTexture.Path + ".T_Body_D", "T_Body_D",
        "/Game/Tests/Materials/MI_Body_Gold", "MI_Body_Gold", "Diffuse Map", "textures/0a1b.png", 256, 256);

    [Fact]
    public void AMaterialListsItsTextureSlots()
    {
        using var temp = new LevelTempDirectory();
        SyntheticTexture.WriteLoose(temp.Path, SyntheticTexture.Path);
        SyntheticTexture.WriteLoose(temp.Path, Material, TextureWriterTests.Material(Material), null);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var slot = Assert.Single(ModulePageViewModel.ReadTextureSlots(catalog, Material + ".MI_Body"));
        Assert.Equal(new TextureSlot("Diffuse Map", SyntheticTexture.Path + ".T_Body_D", 256, 256, "PF_DXT1", false), slot);
        Assert.Empty(ModulePageViewModel.ReadTextureSlots(catalog, "/Game/Tests/Materials/MI_Missing.MI_Missing"));
    }

    [Fact]
    public void ReplaceIsOneStepWithThePixelsTheParameterAndTheStreamingEntry()
    {
        var state = new EditState();
        var op = ModulePageViewModel.TextureReplaceOp(state, Plan);

        Assert.Equal(3, op.Ops.Count);
        var sets = op.Ops.Cast<SetAssetValueOp>().ToList();
        Assert.Equal((Plan.TexturePackage, TextureWrites.PixelsPath, string.Empty, TextureWrites.Value(Plan.TemplatePath, Plan.File)),
            (sets[0].Package, sets[0].Path, sets[0].Old, sets[0].New));
        Assert.Equal((Plan.MaterialPackage, MaterialParameters.PathOf("TextureParameterValues", "Diffuse Map"), Plan.TemplatePath, Plan.TexturePackage + ".T_CarGold_Body_Gold_DM"),
            (sets[1].Package, sets[1].Path, sets[1].Old, sets[1].New));
        Assert.Equal((Plan.MaterialPackage, MaterialTextureStreaming.PathOf("T_Body_D"), "T_Body_D", "T_CarGold_Body_Gold_DM"),
            (sets[2].Package, sets[2].Path, sets[2].Old, sets[2].New));
        Assert.DoesNotContain(sets, s => s.Package == SyntheticTexture.Path || s.Package == Material); // no stock package is edited

        state.Apply(op);
        Assert.Equal(3, state.AssetValueOverrides.Count);
        state.Apply(op.Inverse()); // Ctrl+Z
        Assert.Empty(state.AssetValueOverrides);
    }

    [Fact]
    public void GameTextureUndoesAReplaceAndASecondReplaceStartsFromTheFirst()
    {
        var state = new EditState();
        Assert.Null(ModulePageViewModel.TextureResetOp(state, Plan));
        state.Apply(ModulePageViewModel.TextureReplaceOp(state, Plan));

        var second = ModulePageViewModel.TextureReplaceOp(state, Plan with { File = "textures/ffee.png" });
        Assert.Equal(TextureWrites.Value(Plan.TemplatePath, Plan.File), ((SetAssetValueOp)second.Ops[0]).Old);
        state.Apply(second);
        Assert.Equal(3, state.AssetValueOverrides.Count);

        state.Apply(ModulePageViewModel.TextureResetOp(state, Plan)!);
        Assert.Empty(state.AssetValueOverrides);
    }

    [Fact]
    public async Task AStockItemIsRefused()
    {
        using var ctx = AppTestContext.Create();
        using var page = new VehiclesPageViewModel(ctx.Services);
        var picture = new TextureImage("livery", 4, 4, new byte[64], "PF_R8G8B8A8", 0, 4, 4, true, false);

        Assert.False(await page.ReplaceTextureAsync(Material + ".MI_Body", "Diffuse Map", picture));
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Message == Loc.T("Studio.Textures.CloneFirst"));
        Assert.Empty(page.TextureSlots);
    }
}
