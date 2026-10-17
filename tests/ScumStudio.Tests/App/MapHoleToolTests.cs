using System.Numerics;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Tests.Assets;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>The Map's hole tool: the setting, the outline, the wheel, the journal, and picks falling through the holes.</summary>
public sealed class MapHoleToolTests
{
    private const string Tile = "/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1";

    [Fact]
    public async Task TheHoleToolFollowsTheSettingTheWheelAndTheJournal()
    {
        using var ctx = AppTestContext.Create();
        using var map = new MapPageViewModel(ctx.Services);
        Assert.False(map.HolesEnabled); // experimental: off until switched on in Settings
        ctx.Services.UpdateSettings(s => s with { Ui = s.Ui with { TerrainHolesExperimental = true } });
        Assert.True(map.HolesEnabled);

        map.NewProjectName = "Bunker";
        Assert.True(await map.CreateProjectAsync(ctx.Combine("projects")));
        map.BrushSelect = true;
        map.HoleTool = true;
        Assert.True(map.HoleWarningOpen); // the first use in this project shows the warning list
        map.AcceptHoleWarningCommand.Execute(null);
        Assert.False(map.BrushSelect); // one tool under the cursor
        Assert.True(map.ViewportBrushMode);
        Assert.True(map.BrushOnGround);
        Assert.False(map.CutHoleCommand.CanExecute(null)); // the cursor has not reached the ground yet

        map.HoverHole(new FVector(1010f, -40f, 50f));
        Assert.True(map.CutHoleCommand.CanExecute(null));
        map.ResizeHole(1f);
        Assert.Equal(6.0, map.HoleSize, 3);
        Assert.Equal("6 m", map.HoleSizeText);
        var pending = map.PendingHole()!;
        Assert.Equal((1050f, 0f, 600f), (pending.X, pending.Y, pending.Size)); // on the 1.5 m grid
        Assert.Contains(map.HoleOutlines!, o => o.Color == MapPageViewModel.HolePreviewColor && o.Points.Count == 5);

        // A click puts it down: hovering elsewhere no longer moves it.
        map.PlaceHole(new FVector(-3000f, 3000f, 0f));
        map.HoverHole(new FVector(9000f, 9000f, 0f));
        Assert.Equal((-3000f, 3000f), (map.PendingHole()!.X, map.PendingHole()!.Y));

        // No landscape here (no game files in this test): the cut is refused and nothing is journaled.
        await map.CutHoleCommand.ExecuteAsync(null);
        Assert.Empty(map.CutHoles!);
        Assert.Empty(map.Session.History);

        // A hole in the journal shows crossed in dark red; undo, redo and Fill follow it.
        map.Session.Apply(new TerrainHoleOp(pending with { Tile = Tile }));
        Assert.Single(map.CutHoles!);
        Assert.Equal(16 + 16, map.HoleOutlines!.Count(o => o.Color == MapPageViewModel.HoleCutColor)); // 4 x 4 quads: 16 diagonals, 16 rim edges
        map.UndoCommand.Execute(null);
        Assert.Empty(map.CutHoles!);
        map.RedoCommand.Execute(null);
        Assert.Single(map.CutHoles!);
        map.FillLastHoleCommand.Execute(null);
        Assert.Empty(map.CutHoles!);
        Assert.StartsWith("Fill square hole 6 m", map.Session.History[0].Summary, StringComparison.Ordinal);

        ctx.Services.UpdateSettings(s => s with { Ui = s.Ui with { TerrainHolesExperimental = false } });
        Assert.False(map.HoleTool);
    }

    [Fact]
    public void TheViewsQuadsAreTheOnesTheExportCuts()
    {
        // A component of SCUM's island: 150 cm quads starting on the world grid.
        var grid = new TerrainHoles.Grid(new Vector2(-889950f - (99 * 150f), -459000f - (178 * 150f)), new Vector2(150f, 0f), new Vector2(0f, 150f), 254);
        foreach (var hole in new[]
                 {
                     new TerrainHole(Tile, TerrainHoleShape.Square, -889555f, -458787f, 400f),
                     new TerrainHole(Tile, TerrainHoleShape.Square, -889555f, -458787f, 900f, 30f),
                     new TerrainHole(Tile, TerrainHoleShape.Circle, -889000f, -458000f, 1050f),
                 })
        {
            var exported = TerrainHoles.Quads(grid, [hole]).Select(q => ((int)MathF.Floor(grid.World(q.X + 0.5f, q.Y + 0.5f).X / 150f), (int)MathF.Floor(grid.World(q.X + 0.5f, q.Y + 0.5f).Y / 150f))).ToHashSet();
            Assert.True(exported.SetEquals(hole.WorldQuads()), hole.ToString());
            Assert.NotEmpty(exported);
        }
    }

    [Fact]
    public void PicksFallThroughAHole()
    {
        var flat = LandscapeExtractorTests.Ramp(20, 150f, Vector2.Zero, (_, _) => 500f);
        var field = new TerrainHeightField([(flat, null)]);
        var hole = new TerrainHole(Tile, TerrainHoleShape.Square, 1500f, 1500f, 450f);
        Assert.Equal(500f, field.SampleHeight(1500f, 1500f)!.Value, 2);
        field.Holes = (x, y) => TerrainHole.CutsQuadAt([hole], x, y);
        Assert.Null(field.SampleHeight(1500f, 1500f));
        Assert.Equal(500f, field.SurfaceHeight(1500f, 1500f)!.Value, 2); // the rim is still drawn on the ground
        Assert.Equal(500f, field.SampleHeight(1500f, 1800f)!.Value, 2); // two quads away
        Assert.Null(field.Raycast(new Vector3(1500f, 1500f, 5000f), -Vector3.UnitZ)); // straight down: nothing to hit

        // A slanted pick that meets the ground inside the hole goes through it; without the hole it lands there.
        var (from, along) = (new Vector3(1100f, 1500f, 600f), new Vector3(1f, 0f, -0.5f));
        Assert.Null(field.Raycast(from, along));
        field.Holes = null;
        Assert.Equal(1300f, field.Raycast(from, along)!.Value.Position.X, 1);
    }
}
