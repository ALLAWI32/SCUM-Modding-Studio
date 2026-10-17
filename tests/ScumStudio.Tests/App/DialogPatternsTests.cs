using ScumStudio.App.Services;

namespace ScumStudio.Tests.App;

/// <summary>
/// The file pickers' patterns. The Economy page's Import passed ".json", which became "*..json" and hid
/// EconomyOverride.json in the Windows picker (Игорь, #bug-reports 2026-10-17: "The economy file is not visible
/// because of the extra dot").
/// </summary>
public sealed class DialogPatternsTests
{
    [Theory]
    [InlineData("json")]
    [InlineData(".json")]
    [InlineData("*.json")]
    [InlineData(" .json ")]
    public void AnExtensionWithOrWithoutItsDotGivesOnePattern(string extension) =>
        Assert.Equal(["*.json"], AvaloniaDialogService.PickerPatterns(extension));

    [Fact]
    public void SeveralExtensionsKeepTheirOrder() =>
        Assert.Equal(["*.png", "*.jpg", "*.jpeg", "*.svg"], AvaloniaDialogService.PickerPatterns("png;.jpg;*.jpeg;svg"));

    [Fact]
    public void AnEmptyPartIsDropped() =>
        Assert.Equal(["*.fbx", "*.obj"], AvaloniaDialogService.PickerPatterns("fbx;;.obj;"));

    [Theory]
    [InlineData(".ssprefab", "ssprefab")]
    [InlineData("*.gltf", "gltf")]
    [InlineData("png", "png")]
    public void TheSavePickersDefaultExtensionHasNoDot(string extension, string bare) =>
        Assert.Equal(bare, AvaloniaDialogService.BareExtension(extension));

    [Fact]
    public void TheEconomyImportFindsEconomyOverrideJson()
    {
        // The pattern the Economy page asks for must match the game's own file name.
        var pattern = Assert.Single(AvaloniaDialogService.PickerPatterns(".json"));
        Assert.True(System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, "EconomyOverride.json"));
    }
}
