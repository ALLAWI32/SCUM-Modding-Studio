using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;

namespace ScumStudio.Tests.Level;

/// <summary>Owner: "make the ground snowy, a desert, autumn or all grass; change the map's trees". Asset replacements.</summary>
public sealed class GroundLookTests
{
    [Fact]
    public void ALookIsReplacementsThatSwitchAndUndo()
    {
        var state = new EditState();
        Assert.Equal(GroundLook.Game, GroundLooks.Current(state));

        var snow = new BatchOp("Snow", GroundLooks.Edits(state, GroundLook.Snow, _ => true));
        state.Apply(snow);
        Assert.Equal(GroundLook.Snow, GroundLooks.Current(state));
        Assert.Equal("/Game/ConZ_Files/Materials/Snow/T_GroundSnow_03_D", state.GetReplacement("/Game/ConZ_Files/Landscape/LandscapeTextures/T_Grass_Continental_D"));
        Assert.Null(state.GetReplacement("/Game/ConZ_Files/Landscape/Textures/T_Asphalt_D")); // roads stay
        Assert.Contains("/Game/ConZ_Files/Landscape/LandscapeTextures/T_Grass_Continental_D", state.ChangedAssets);
        Assert.False(state.IsEmpty);

        // Another look replaces the first one's packages; what it leaves goes back to the game's own.
        var desert = new BatchOp("Desert", GroundLooks.Edits(state, GroundLook.Desert, _ => true));
        state.Apply(desert);
        Assert.Equal(GroundLook.Desert, GroundLooks.Current(state));
        Assert.Null(state.GetReplacement("/Game/ConZ_Files/Landscape/LandscapeTextures/T_Beach_Sand_D")); // the sand itself

        state.Apply(desert.Inverse());
        Assert.Equal(GroundLook.Snow, GroundLooks.Current(state));
        state.Apply(snow.Inverse());
        Assert.True(state.IsEmpty);

        // A package the game no longer has is left out.
        Assert.DoesNotContain(GroundLooks.Edits(state, GroundLook.Grass, p => !p.EndsWith("T_Field_D", StringComparison.Ordinal)), o => o.Package.EndsWith("T_Field_D", StringComparison.Ordinal));
    }

    [Fact]
    public void ATreeSwapIsJournaledValidatedAndExported()
    {
        const string oak = "/Game/ConZ_Files/Foliage/Continental/Trees/Oak/Oak_01";
        const string pine = "/Game/ConZ_Files/Foliage/Continental/Trees/Pine/Pine_01";
        var swap = new ReplaceAssetOp(oak, null, pine);
        var line = Journal.SerializeRecord(new JournalRecord { Seq = 1, Type = JournalRecordType.Edit, Edit = swap });
        Assert.Contains("\"replaceAsset\"", line, StringComparison.Ordinal);
        Assert.Equal(swap, Journal.DeserializeRecord(line).Edit);

        var state = new EditState();
        Assert.NotNull(state.Validate(new ReplaceAssetOp(oak, null, oak))); // not with itself
        Assert.NotNull(state.Validate(new ReplaceAssetOp(oak, pine, null))); // out of date
        state.Apply(swap);
        Assert.Equal(pine, ProjectExporter.BuildAssetRequest(state).Replacements[oak]);
        Assert.Equal("Replace Oak_01 with Pine_01", swap.Describe());
        state.Apply(swap.Inverse());
        Assert.True(ProjectExporter.BuildAssetRequest(state).IsEmpty);
    }
}
