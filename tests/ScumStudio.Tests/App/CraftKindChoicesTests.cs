using Avalonia.Media.Imaging;
using ScumStudio.App.ViewModels;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Tests.App;

/// <summary>The Kind box follows the row's kinds (ui-craft-categories built it once; craft2 adds Element when the family is read), and the picked picture is saved.</summary>
public sealed class CraftKindChoicesTests
{
    private const string Bed = "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Bed_Improvised";

    [Fact]
    public void TheKindBoxOffersElementOnceTheFamilyIsKnown()
    {
        var furniture = new Craftable { Name = "Bed Improvised", Source = Bed, Mesh = "/Game/A/SM_Bed", Kind = CraftKind.Furniture };
        var row = new CraftableRowViewModel(furniture, _ => { }, (_, _) => Task.FromResult<Bitmap?>(null));
        Assert.DoesNotContain(CraftKind.Element, row.Kinds);
        row.SetFamily(new ElementFamily(Bed, "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_Bed_Improvised", "BaseBuildingComponent", null,
            [new ElementLevel("CR_Bed_Improvised", Bed, [new Tunable("Default__BP_Bed_Improvised_C", "_healthPoints", TunableKind.Float, "500") { Name = "_healthPoints" }])], true), null);
        Assert.Contains(CraftKind.Element, row.Kinds);
        Assert.Equal(row.Kinds, row.KindChoices.Select(c => (CraftKind)c.Value));
    }

    [Fact]
    public void AnElementRowShowsElementInTheKindBox()
    {
        var element = new Craftable { Name = "Bed", Source = Bed, Mesh = "/Game/A/SM_Bed", Kind = CraftKind.Element, Element = new ElementSettings { Template = Bed }, Use = CraftUse.Build };
        var row = new CraftableRowViewModel(element, _ => { }, (_, _) => Task.FromResult<Bitmap?>(null));
        Assert.Contains(CraftChoice.Of(CraftKind.Element), row.KindChoices);
        Assert.Equal(CraftChoice.Of(CraftKind.Element), row.KindChoice);
        Assert.False(string.IsNullOrEmpty(row.KindChoice.Label));
    }

    [Fact]
    public void ThePickedPictureIsSavedAndResetGivesItsOwn()
    {
        var chair = new Craftable { Name = "Chair", Source = "/Game/A/SM_Chair", Mesh = "/Game/A/SM_Chair", Kind = CraftKind.Furniture };
        var saved = 0;
        var row = new CraftableRowViewModel(chair, _ => saved++, (_, _) => Task.FromResult<Bitmap?>(null));
        row.Icon = " /Game/ConZ_Files/Items/Item_Icons/ICO_Chair ";
        Assert.Equal("/Game/ConZ_Files/Items/Item_Icons/ICO_Chair", row.ToModel().Icon);
        Assert.True(saved > 0);
        row.ResetIconCommand.Execute(null);
        Assert.Null(row.ToModel().Icon);
    }
}
