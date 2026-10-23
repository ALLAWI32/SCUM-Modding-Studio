using Avalonia.Media.Imaging;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Tests.App;

/// <summary>
/// The owner's "Base Cabin 03" (2026-10-17): a craftable made Furniture from a game base element switches in one click to
/// that element's own family with the kind warning, and its "Needs a flag", "Repair cost %" and "Place in front"
/// (experimental) round-trip through the file.
/// </summary>
[Collection(LanguageCollection.Name)] // compares texts of the UI language
public sealed class CraftBuildingRowTests
{
    private const string Cabin = "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Base_Cabin_03";
    private const string Shelter = "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Base_Shelter";

    [Fact]
    public void TheOwnersFurnitureCabinBecomesTheGamesCabinInOneClick()
    {
        var saved = new Craftable
        {
            Name = "Base Cabin 03",
            Token = "SS_Base_Cabin_03",
            Source = Cabin,
            Mesh = "/Game/ConZ_Files/Models/Objects/Outdoor/Base_Building_Elements/Wooden_Cabin/SM_Wooden_Cabin_Closed_Windows_03",
            Kind = CraftKind.Furniture,
            Category = "CraftingCategory.BaseBuilding.Flags",
            SizeMeters = 15,
            Ingredients = [new("CI_Plank", 50), new("CI_Group_Logs", 35), new("CI_Sand_Bag", 10)],
            Exported = true,
        };
        var row = new CraftableRowViewModel(saved, _ => { }, NoPicture) { MayBeExported = true };
        Assert.False(row.CanBeLikeGame); // the family is read on a worker first
        Assert.True(row.IsLargeTemplate);

        row.SetFamily(Family(Cabin, borrowed: true), null);
        Assert.True(row.CanBeLikeGame);
        Assert.Equal(Loc.F("Craftables.Building.LikeGame", "BP_Base_Cabin_03"), row.LikeGameText);
        Assert.Contains(CraftKind.Element, row.Kinds);
        Assert.Null(row.KindWarning);

        row.BuildLikeGameCommand.Execute(null);
        Assert.Equal(CraftKind.Element, row.Kind);
        Assert.False(row.CanBeLikeGame);
        Assert.False(row.IsLargeTemplate); // the game's own placement now
        Assert.True(row.BorrowsRecipe);
        Assert.Equal(Loc.T("Craftables.KindChange.Warning"), row.KindWarning); // what players built changes behaviour

        // Same asset names and recipe; the element is the game's cabin.
        var model = row.ToModel();
        Assert.Equal("SS_Base_Cabin_03", model.Token);
        Assert.Equal(Cabin, model.Element!.Template);
        Assert.Equal(CraftUse.Build, model.Use);
        Assert.Equal("CraftingCategory.BaseBuilding.Flags", model.Category);
        Assert.Equal(3, model.Ingredients.Count);
        Assert.Null(model.NeedsFlag);
        Assert.Null(model.RepairCostPercent);
    }

    [Fact]
    public void NeedsAFlagAndRepairCostRoundTrip()
    {
        var row = new CraftableRowViewModel(new Craftable { Name = "Cabin", Source = Cabin, Mesh = Cabin, Kind = CraftKind.Element, Element = new ElementSettings { Template = Cabin } }, _ => { }, NoPicture);
        row.SetFamily(Family(Cabin, borrowed: true), null);
        Assert.True(row.GameNeedsFlag); // the cabin stores no _canBePlacedWithoutFlag: the C++ default needs a flag
        Assert.True(row.NeedsFlag);
        Assert.Equal(Loc.F("Craftables.Element.Stock", "✓"), row.NeedsFlagStock);
        Assert.Equal(100m, row.RepairCost);

        row.NeedsFlag = false;
        row.RepairCost = 250;
        var model = row.ToModel();
        Assert.False(model.NeedsFlag);
        Assert.Equal(250f, model.RepairCostPercent);

        row.NeedsFlag = true; // back to the game's: nothing is written
        row.RepairCost = 100;
        Assert.Null(row.ToModel().NeedsFlag);
        Assert.Null(row.ToModel().RepairCostPercent);
        row.RepairCost = 5000;
        Assert.Equal(1000m, row.RepairCost);

        var again = new CraftableRowViewModel(model, _ => { }, NoPicture);
        again.SetFamily(Family(Cabin, borrowed: true), null);
        Assert.False(again.NeedsFlag);
        Assert.Equal(250m, again.RepairCost);

        // The shelter stores _canBePlacedWithoutFlag true: the game's answer is "no flag"; the per-level list leaves it to the switch.
        var shelter = new CraftableRowViewModel(new Craftable { Name = "Shelter", Source = Shelter, Mesh = Shelter, Kind = CraftKind.Element, Element = new ElementSettings { Template = Shelter } }, _ => { }, NoPicture);
        shelter.SetFamily(Family(Shelter, borrowed: false, withoutFlag: true), null);
        Assert.False(shelter.GameNeedsFlag);
        Assert.False(shelter.NeedsFlag);
        Assert.DoesNotContain(shelter.Element!.Levels[0].Values, v => v.Key == "_canBePlacedWithoutFlag");
    }

    [Fact]
    public void PlaceInFrontIsOffButSuggestedForABigBuildingAndRoundTrips()
    {
        var cabin = new CraftableRowViewModel(new Craftable { Name = "Cabin", Source = Cabin, Mesh = Cabin, Kind = CraftKind.Element, SizeMeters = 15, Element = new ElementSettings { Template = Cabin } }, _ => { }, NoPicture);
        Assert.False(cabin.PlaceInFront); // off by default: the game's placement
        Assert.True(cabin.InFrontSuggested);
        Assert.Equal(Loc.F("Craftables.Building.InFront.Suggested", 15f.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)), cabin.InFrontSuggestion);
        Assert.Equal(8.5m, cabin.InFrontMeters); // half of 15 m + 1 m
        Assert.Null(cabin.ToModel().InFront);

        cabin.PlaceInFront = true;
        Assert.False(cabin.InFrontSuggested);
        Assert.Equal(new InFrontSettings(), cabin.ToModel().InFront); // automatic distance, +X

        cabin.InFrontMeters = 11.04m;
        cabin.InFrontAxisIndex = (int)InFrontAxis.MinusY;
        var model = cabin.ToModel();
        Assert.Equal(new InFrontSettings(11f, InFrontAxis.MinusY), model.InFront);
        Assert.Equal(11f, CraftablesPlanner.InFrontMeters(model));

        cabin.InFrontMeters = 8.5m; // the automatic value again: stored as automatic
        Assert.Null(cabin.ToModel().InFront!.Meters);
        cabin.InFrontMeters = 99;
        Assert.Equal(30m, cabin.InFrontMeters);

        var again = new CraftableRowViewModel(model, _ => { }, NoPicture);
        Assert.True(again.PlaceInFront);
        Assert.Equal(11m, again.InFrontMeters);
        Assert.Equal((int)InFrontAxis.MinusY, again.InFrontAxisIndex);
        again.PlaceInFront = false;
        Assert.Null(again.ToModel().InFront);

        // A chair-sized thing: not suggested, automatically 0 m (the game's placement even when switched on).
        var chair = new CraftableRowViewModel(new Craftable { Name = "Stool", Source = Cabin, Mesh = Cabin, Kind = CraftKind.Furniture, SizeMeters = 0.8f }, _ => { }, NoPicture);
        Assert.True(chair.InFrontOffered);
        Assert.False(chair.InFrontSuggested);
        Assert.Equal(0m, chair.InFrontMeters);

        // Not offered where the Large Log Shed's recipe places it with its check drawn in (the player may stand inside the
        // ghost): large furniture, and an element the game ships no recipe for; a saved switch is then shown off.
        var big = new CraftableRowViewModel(new Craftable { Name = "Hall", Source = Cabin, Mesh = Cabin, Kind = CraftKind.Furniture, SizeMeters = 15, InFront = new InFrontSettings(18f) }, _ => { }, NoPicture);
        Assert.True(big.IsLargeTemplate);
        Assert.False(big.InFrontOffered);
        Assert.False(big.PlaceInFront);
        Assert.False(big.InFrontSuggested);
        Assert.Equal(Loc.T("Craftables.Building.Large"), big.LargeTemplateCaption);

        // A station of 15 m: the same (its check is drawn in too), but it keeps the workbench's recipe, and the caption says so
        // (review 3.5.1: it promised the Large Log Shed's recipe).
        var forge = new CraftableRowViewModel(new Craftable { Name = "Forge", Source = Cabin, Mesh = Cabin, Kind = CraftKind.Station, SizeMeters = 15, InFront = new InFrontSettings(5f) }, _ => { }, NoPicture);
        Assert.True(forge.IsLargeTemplate);
        Assert.False(forge.InFrontOffered);
        Assert.False(forge.PlaceInFront);
        Assert.Equal(Loc.T("Craftables.Building.LargeStation"), forge.LargeTemplateCaption);
        Assert.DoesNotContain("Log Shed", forge.LargeTemplateCaption, StringComparison.Ordinal);
        var borrowed = new CraftableRowViewModel(new Craftable { Name = "Cabin", Source = Cabin, Mesh = Cabin, Kind = CraftKind.Element, SizeMeters = 15, Element = new ElementSettings { Template = Cabin } }, _ => { }, NoPicture);
        borrowed.SetFamily(Family(Cabin, borrowed: true), null);
        Assert.True(borrowed.BorrowsRecipe);
        Assert.False(borrowed.InFrontOffered);
        Assert.False(borrowed.InFrontSuggested);
    }

    private static ElementFamily Family(string element, bool borrowed, bool withoutFlag = false)
    {
        var cdo = $"Default__{element[(element.LastIndexOf('/') + 1)..]}_C";
        List<Tunable> values = [new(cdo, "_healthPoints", TunableKind.Float, "3000") { Name = "_healthPoints" }, new(cdo, "_hoursToDecay", TunableKind.Float, "672") { Name = "_hoursToDecay" }];
        if (withoutFlag)
        {
            values.Add(new(cdo, "_canBePlacedWithoutFlag", TunableKind.Bool, "true") { Name = "_canBePlacedWithoutFlag" });
        }

        var recipe = borrowed ? CraftablesPlanner.LargeBuildingRecipe : CraftablesPlanner.FurnitureRecipe;
        return new ElementFamily(element, recipe, "BaseBuildingComponent", null, [new ElementLevel(recipe, element, values)], false)
        {
            BorrowsRecipe = borrowed,
        };
    }

    private static Task<Bitmap?> NoPicture(Craftable craftable, CancellationToken cancellationToken) => Task.FromResult<Bitmap?>(null);
}
