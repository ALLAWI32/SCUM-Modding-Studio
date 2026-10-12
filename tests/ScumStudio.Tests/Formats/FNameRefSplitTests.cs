using ScumStudio.Formats;

namespace ScumStudio.Tests.Formats;

/// <summary>The UE 4.27 <c>ParseNumber</c> rule every writer uses: <c>X_2</c> is the name "X" with number 3.</summary>
public sealed class FNameRefSplitTests
{
    [Theory]
    [InlineData("X_2", "X", 3)]
    [InlineData("X_0", "X", 1)]
    [InlineData("CR_Improvised_Bow_20", "CR_Improvised_Bow", 21)]
    [InlineData("/Game/A/B_2.B_2", "/Game/A/B_2.B", 3)]
    [InlineData("X_2147483646", "X", int.MaxValue)]
    public void ATrailingNumberSplits(string value, string text, int number) =>
        Assert.Equal((text, number), FNameRef.Split(value));

    [Theory]
    [InlineData("X_02")]
    [InlineData("_02")]
    [InlineData("X_0x")]
    [InlineData("X_")]
    [InlineData("X2")]
    [InlineData("X_12345678901")]
    [InlineData("X_2147483647")]
    [InlineData("")]
    public void OtherNamesStayWhole(string value) => Assert.Equal((value, 0), FNameRef.Split(value));

    [Fact]
    public void ASplitNameFormatsBackToTheSameText()
    {
        foreach (var value in new[] { "X_2", "X_0", "/Game/A/ConcreteBlock002_1", "Rager_02", "AK47" })
        {
            var (text, number) = FNameRef.Split(value);
            Assert.Equal(value, new FNameRef(0, number).Format([text]));
        }
    }
}
