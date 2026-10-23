using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Registry;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// r39 (Discord 2026-10-18): a clone of an item the game hides from its spawn commands (registry tag HiddenFromAdminCommands,
/// 246 of the game's items) is made to be spawned under its new name: the tag is left off the clone's own records (the owner's
/// package), while every other tag, the other copied packages' tags and the game's record stay as they are.
/// </summary>
public sealed class RegistryClonerSpawnableTests
{
    private const string Stove = "/Game/T/KitchenStove";
    private const string Copy = "/Game/T/MyStove";

    [Fact]
    public void TheHiddenTagIsLeftOffTheClonesPrimaryRecordsOnly()
    {
        var registry = SyntheticGame.Registry(
            new SyntheticGame.Record(Stove + ".KitchenStove", "Blueprint",
                [.. SyntheticGame.PrimaryTags("Item", "KitchenStove"), (RegistryCloner.HiddenFromAdminCommandsTag, AssetRegistryValue.Ansi("1")), ("Weight", AssetRegistryValue.Ansi("40"))]),
            new SyntheticGame.Record(Stove + ".KitchenStove_C", "BlueprintGeneratedClass",
                [.. SyntheticGame.PrimaryTags("Item", "KitchenStove"), (RegistryCloner.HiddenFromAdminCommandsTag, AssetRegistryValue.Ansi("1"))]),
            new SyntheticGame.Record(Stove + "_ES.KitchenStove_ES", "Blueprint",
                [.. SyntheticGame.PrimaryTags("LWObjectSetup", "KitchenStove_ES"), (RegistryCloner.HiddenFromAdminCommandsTag, AssetRegistryValue.Ansi("1"))]));
        var warnings = new List<string>();

        var added = RegistryCloner.Register(registry, new PackageMap([new(Stove, Copy), new(Stove + "_ES", Copy + "_ES")]), warnings, owner: Copy);

        Assert.Empty(warnings);
        Assert.Equal([Copy + ".MyStove", Copy + ".MyStove_C", Copy + "_ES.MyStove_ES"], added.Select(a => a.ObjectPath));
        var back = AssetRegistryFile.Parse(registry.Save());
        Dictionary<string, string> Tags(string objectPath) =>
            back.GetTags(back.Assets.Single(a => a.ObjectPath == objectPath)).ToDictionary(t => t.Key, t => t.Value.Text);
        var blueprint = Tags(Copy + ".MyStove");
        Assert.False(blueprint.ContainsKey(RegistryCloner.HiddenFromAdminCommandsTag));
        Assert.Equal(("Item", "MyStove", "40"), (blueprint["PrimaryAssetType"], blueprint["PrimaryAssetName"], blueprint["Weight"]));
        Assert.False(Tags(Copy + ".MyStove_C").ContainsKey(RegistryCloner.HiddenFromAdminCommandsTag));
        Assert.Equal("1", Tags(Copy + "_ES.MyStove_ES")[RegistryCloner.HiddenFromAdminCommandsTag]); // not the primary: as the template
        Assert.Equal("1", Tags(Stove + ".KitchenStove")[RegistryCloner.HiddenFromAdminCommandsTag]); // the game's record is untouched
    }

    [Fact]
    public void WithoutAnOwnerEveryTagIsCopied()
    {
        // A copy registered without an owner (a craftable's, a texture's) is not a spawnable clone: its tags are the template's.
        var registry = SyntheticGame.Registry(
            new SyntheticGame.Record(Stove + ".KitchenStove", "Blueprint",
                [.. SyntheticGame.PrimaryTags("Item", "KitchenStove"), (RegistryCloner.HiddenFromAdminCommandsTag, AssetRegistryValue.Ansi("1"))]));

        RegistryCloner.Register(registry, new PackageMap([new(Stove, Copy)]));

        var back = AssetRegistryFile.Parse(registry.Save());
        var tags = back.GetTags(back.Assets.Single(a => a.ObjectPath == Copy + ".MyStove")).ToDictionary(t => t.Key, t => t.Value.Text);
        Assert.Equal("1", tags[RegistryCloner.HiddenFromAdminCommandsTag]);
    }
}
