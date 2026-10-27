using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;

namespace ScumStudio.Tests.App;

/// <summary>The labels of the stored values (items, weapons, vehicles) follow the UI language.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TunableLabelTests
{
    private static string In(string language, string name)
    {
        Loc.Instance.Language = language;
        try
        {
            return TunableRowViewModel.LocalizedLabel(name);
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
        }
    }

    [Fact]
    public void EnglishStaysTheHumanizedName()
    {
        Assert.Equal("Damage per shot", In("en", "DamagePerShot"));
        Assert.Equal("Max push force", In("en", "_maxPushForce"));
    }

    [Theory]
    [InlineData("ru", "DamagePerShot", "Урон за выстрел")]
    [InlineData("ru", "_damageOverTime", "Урон со временем")]
    [InlineData("de", "ShelfLife", "Haltbarkeit")]
    [InlineData("zh", "_noiseLevel", "噪音等级")]
    public void AnAllNameTranslationWinsOverTheWordByWordOne(string language, string name, string expected) =>
        Assert.Equal(expected, In(language, name));

    [Fact]
    public void OtherNamesAreComposedFromTheirWords()
    {
        Assert.Equal("Масса", In("ru", "Mass"));
        Assert.Equal("Макс. масса", In("ru", "MaxMass"));
        Assert.Equal("Radius", In("de", "Radius"));
        Assert.Equal("Max. Gewicht", In("de", "MaxWeight"));
    }

    [Fact]
    public void AcronymsAndNumbersStayAndNothingIsHalfTranslated()
    {
        Assert.Equal("Макс. RPM", In("ru", "MaxRPM"));
        // "frobnicate" is no word of the dictionary: the whole label stays English rather than half Russian.
        Assert.Equal("Damage frobnicate", In("ru", "DamageFrobnicate"));
    }

    [Fact]
    public void AnOmittedWordIsLeftOut()
    {
        // "of" has no word in Russian: "Max of damage" reads "Макс. урон".
        Assert.Equal("Макс. урон", In("ru", "MaxOfDamage"));
    }

    [Fact]
    public void NamesThatAreAlreadyWordsAreNotTouched()
    {
        var row = new TunableRowViewModel("p", new ScumStudio.Modding.Tuning.Tunable("e", "p", ScumStudio.Modding.Tuning.TunableKind.Float, "1") { Name = "Torque at 3250 rpm" }, "1");
        Loc.Instance.Language = "ru";
        try
        {
            Assert.Equal("Torque at 3250 rpm", row.Label);
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
        }
    }
}
