using System.Text;
using ScumStudio.Formats.AssetRegistry;

namespace ScumStudio.Tests.Formats;

public sealed class CityHashTests
{
    // Reference values from the CityHash v1.1 reference implementation (the `cityhash` Python module used by assetreg.py).
    [Theory]
    [InlineData(0, 0x9ae16a3b2f90404fUL)]
    [InlineData(1, 0xb3454265b6df75e3UL)]
    [InlineData(3, 0x2d5b0cbcc48fdc6bUL)]
    [InlineData(4, 0x7b803bc75cee3292UL)]
    [InlineData(7, 0xed26868869003b0aUL)]
    [InlineData(8, 0x93b52af6d9b92820UL)]
    [InlineData(15, 0xa3a2d5a9cab38c6bUL)]
    [InlineData(16, 0xf496a3696582095eUL)]
    [InlineData(17, 0x1f0c64814df23a1aUL)]
    [InlineData(31, 0x08b2aebb06a4ade5UL)]
    [InlineData(32, 0x9636decce0f119bcUL)]
    [InlineData(33, 0x8f3c5b7695a8cdaeUL)]
    [InlineData(63, 0x083aa050941c1932UL)]
    [InlineData(64, 0x3fa09861f2041e72UL)]
    [InlineData(65, 0x3f05b53618eef03eUL)]
    [InlineData(100, 0xa55dee65de428689UL)]
    [InlineData(127, 0x32e63483c2988d9eUL)]
    [InlineData(128, 0xac846330ebf74e5bUL)]
    [InlineData(129, 0xf1ea93f5d57b6049UL)]
    [InlineData(200, 0x0877fbf99e2f0ef6UL)]
    public void MatchesReferenceVectors(int length, ulong expected)
    {
        var s = new string(Enumerable.Range(0, length).Select(i => (char)('a' + (i * 7 % 26))).ToArray());
        Assert.Equal(expected, CityHash.CityHash64(Encoding.ASCII.GetBytes(s)));
    }

    [Fact]
    public void NameHashIsCaseInsensitive()
    {
        Assert.Equal(
            AssetRegistryFile.ComputeNameHash("/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen"),
            AssetRegistryFile.ComputeNameHash("/game/conz_files/vehicles/car/wolfswagen/bpc_wolfswagen"));
    }
}
