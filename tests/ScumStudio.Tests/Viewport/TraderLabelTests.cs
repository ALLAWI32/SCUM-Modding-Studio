using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

public sealed class TraderLabelTests
{
    [Theory]
    [InlineData("B_4_Armory", "Armorer", "Trader Armorer B_4")]
    [InlineData("A_0_Trader", "GeneralGoods", "Trader General goods A_0")]
    [InlineData("Z_3_Master_Hunter", "MasterHunter", "Trader Master hunter Z_3")]
    [InlineData("Banker01", "", "Trader Bank")]
    public void CardsReadTraderTypeAndSector(string name, string type, string expected) =>
        Assert.Equal(expected, SpawnMarkers.TraderLabel(new TraderMarker(FTransform.Identity, name, type, string.Empty, string.Empty)));
}
