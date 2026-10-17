using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The hole tool (owner, 2026-10-16: "pick a square or a circle on the ground, make it bigger or smaller, press Delete,
/// and it opens a hole in the ground, like the game's own bunker entrances"): Landscape menu, then the ground. The outline
/// follows the cursor, a click (or drag) puts it down, the wheel sizes it on the 1.5 m landscape grid, Delete (or the
/// button) cuts it as a <see cref="TerrainHoleOp"/>. Experimental (Settings): the export opens the collision everywhere and
/// the drawn ground only where the component already has the visibility layer (green on the Map; amber: collision only).
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>How far around the hole the places where it can be seen are outlined (2 km).</summary>
    private const float HolePlaceRadiusCm = 200_000f;

    /// <summary>Colour of the hole about to be cut.</summary>
    internal static readonly Vector4 HolePreviewColor = new(1f, 0.2f, 0.15f, 1f);

    /// <summary>Colour of the holes already cut (their quads, crossed).</summary>
    internal static readonly Vector4 HoleCutColor = new(0.55f, 0.05f, 0.05f, 1f);

    /// <summary>Outline of ground where the game draws a hole too (the success token's green).</summary>
    internal static readonly Vector4 HoleVisibleColor = new(0.553f, 0.639f, 0.357f, 1f);

    /// <summary>Outline of ground where a hole opens the collision only (the warning token's amber).</summary>
    internal static readonly Vector4 HoleCollisionOnlyColor = new(0.831f, 0.647f, 0.227f, 1f);

    private readonly HashSet<string> _holeWarningSeen = new(StringComparer.OrdinalIgnoreCase);
    private Task<IReadOnlyList<(string Tile, Vector2 Min, Vector2 Max)>>? _holeTiles;
    private AssetCatalog? _holeTilesCatalog;
    private Task<IReadOnlyList<TerrainHoleLayers.Place>>? _holePlaces;
    private AssetCatalog? _holePlacesCatalog;
    private IReadOnlyList<TerrainHoleLayers.Place>? _holePlacesReady;
    private Vector2? _holeHover;
    private Vector2? _holePlaced;

    /// <summary>The warning list is open (the hole tool's first use in this project); nothing is cut until it is accepted.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CutHoleCommand))]
    private bool _holeWarningOpen;

    /// <summary>"Do not show again" ticked on the warning list (remembered for this project when accepted).</summary>
    [ObservableProperty]
    private bool _holeWarningDontShow;

    /// <summary>What the hole under the cursor does in the game, or null before the places are known or the cursor reached the ground.</summary>
    [ObservableProperty]
    private string? _holePlaceText;

    /// <summary>The game draws the hole under the cursor (green).</summary>
    [ObservableProperty]
    private bool _holePlaceVisible;

    /// <summary>The hole under the cursor opens the collision only, all or part of it (amber).</summary>
    [ObservableProperty]
    private bool _holePlaceCollisionOnly;

    /// <summary>The experimental hole tool is switched on in Settings.</summary>
    [ObservableProperty]
    private bool _holesEnabled;

    /// <summary>The hole tool is in use: the brush events place the hole instead of selecting or planting.</summary>
    [ObservableProperty]
    private bool _holeTool;

    /// <summary>Round hole (else square).</summary>
    [ObservableProperty]
    private bool _holeCircle;

    /// <summary>The hole's side or diameter in metres (snapped to whole 1.5 m quads when cut).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoleSizeText))]
    private double _holeSize = 4.5;

    /// <summary>Turn of a square hole (degrees).</summary>
    [ObservableProperty]
    private double _holeYaw;

    /// <summary>The outlines drawn over the ground: the hole about to be cut and the ones cut.</summary>
    [ObservableProperty]
    private IReadOnlyList<GroundOutline>? _holeOutlines;

    /// <summary>The project's holes (the view's picks fall through them).</summary>
    [ObservableProperty]
    private IReadOnlyList<TerrainHole>? _cutHoles;

    /// <summary>The size as cut, e.g. <c>4.5 m</c>.</summary>
    public string HoleSizeText => string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(Math.Round(HoleSize / 1.5), 1, 50) * 1.5:0.#} m");

    /// <summary>Number of holes the project cuts, for the menu.</summary>
    public string HoleCountText => Loc.F("Map.Hole.Count", CutHoles?.Count ?? 0);

    /// <summary>The warning list (see <see cref="TerrainHoleWarning"/>).</summary>
    public IReadOnlyList<string> HoleWarnings => TerrainHoleWarning.Items();

    partial void OnHolesEnabledChanged(bool value)
    {
        if (!value)
        {
            HoleTool = false;
        }
    }

    partial void OnHoleToolChanged(bool value)
    {
        _holePlaced = null;
        if (value)
        {
            BrushSelect = false;
            _ = HoleTiles(); // read the tiles' bounds while the user aims
            _ = LoadHolePlacesAsync(); // and where a hole can be seen
            var key = TerrainHoleWarning.KeyOf(_services.Projects.Current);
            if (!_holeWarningSeen.Contains(key) && !TerrainHoleWarning.IsDismissed(_services.UiState.Current, key))
            {
                HoleWarningDontShow = false;
                HoleWarningOpen = true;
            }
        }
        else
        {
            HoleWarningOpen = false;
        }

        OnPropertyChanged(nameof(ViewportBrushMode));
        OnPropertyChanged(nameof(BrushOnGround));
        RefreshHoleOutlines();
        CutHoleCommand.NotifyCanExecuteChanged();
    }

    partial void OnHoleCircleChanged(bool value) => RefreshHoleOutlines();

    partial void OnHoleSizeChanged(double value) => RefreshHoleOutlines();

    partial void OnHoleYawChanged(double value) => RefreshHoleOutlines();

    /// <summary>The cursor is over the ground at <paramref name="at"/>: the outline follows it until one is put down.</summary>
    public void HoverHole(FVector at)
    {
        _holeHover = new Vector2(at.X, at.Y);
        if (_holePlaced is null)
        {
            RefreshHoleOutlines();
            CutHoleCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A click or drag on the ground puts the hole down at <paramref name="at"/>.</summary>
    public void PlaceHole(FVector at)
    {
        _holePlaced = new Vector2(at.X, at.Y);
        RefreshHoleOutlines();
        CutHoleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The wheel: one quad (1.5 m) bigger or smaller a notch.</summary>
    public void ResizeHole(float notches) => HoleSize = Math.Clamp(HoleSize + (Math.Sign(notches) * TerrainHole.QuadCm / 100.0), TerrainHole.MinSize / 100.0, TerrainHole.MaxSize / 100.0);

    /// <summary>The hole as it would be cut now (snapped to the grid), or null before the cursor reached the ground.</summary>
    internal TerrainHole? PendingHole(string? tile = null) => HoleTool && (_holePlaced ?? _holeHover) is { } c
        ? new TerrainHole(tile ?? string.Empty, HoleCircle ? TerrainHoleShape.Circle : TerrainHoleShape.Square, c.X, c.Y, (float)(HoleSize * 100.0), (float)HoleYaw).Snapped()
        : null;

    private bool CanCutHole() => !HoleWarningOpen && PendingHole() is not null;

    /// <summary>"I understand" on the warning list: not shown again in this project until the app restarts, or never with "Do not show again".</summary>
    [RelayCommand]
    private void AcceptHoleWarning()
    {
        var key = TerrainHoleWarning.KeyOf(_services.Projects.Current);
        _holeWarningSeen.Add(key);
        if (HoleWarningDontShow)
        {
            TerrainHoleWarning.Dismiss(_services.UiState, key);
        }

        HoleWarningOpen = false;
    }

    /// <summary>Cuts the hole (Delete, or the button): one edit, one undo.</summary>
    [RelayCommand(CanExecute = nameof(CanCutHole))]
    private async Task CutHoleAsync()
    {
        if (PendingHole() is not { } pending)
        {
            return;
        }

        var tiles = await HoleTiles().ConfigureAwait(true);
        if (TerrainHoles.TileAt(tiles, pending.X, pending.Y) is not { } tile)
        {
            _services.Notifications.Warning(Loc.T("Map.Hole.Title"), Loc.T("Map.Hole.OffLandscape"));
            return;
        }

        try
        {
            var entry = _services.Projects.Apply(new TerrainHoleOp(pending with { Tile = tile }));
            _services.Notifications.Info(Loc.T("Map.Hole.Title"), entry.Op.Describe());
            _holePlaced = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Loc.T("Map.Hole.Title"), ex.Message);
        }

        RefreshHoles();
    }

    /// <summary>Fills the last hole cut back in (one edit; Ctrl+Z undoes it like any other).</summary>
    [RelayCommand]
    private void FillLastHole()
    {
        if (_services.Projects.Current?.State.TerrainHoles is not [.., var last])
        {
            return;
        }

        try
        {
            var entry = _services.Projects.Apply(new TerrainHoleOp(last, Cut: false));
            _services.Notifications.Info(Loc.T("Map.Hole.Title"), entry.Op.Describe());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Loc.T("Map.Hole.Title"), ex.Message);
        }

        RefreshHoles();
    }

    /// <summary>Follows the project (an edit, undo or redo) and the Settings switch.</summary>
    private void RefreshHoles()
    {
        HolesEnabled = _services.Settings.Load().Ui.TerrainHolesExperimental;
        var holes = _services.Projects.Current?.State.TerrainHoles;
        if (holes is null ? CutHoles is { Count: > 0 } : CutHoles is null || !holes.SequenceEqual(CutHoles))
        {
            CutHoles = holes?.ToList() ?? [];
            OnPropertyChanged(nameof(HoleCountText));
        }

        RefreshHoleOutlines();
    }

    private void RefreshHoleOutlines()
    {
        var outlines = new List<GroundOutline>();
        foreach (var hole in CutHoles ?? [])
        {
            outlines.AddRange(QuadLines(hole, HoleCutColor));
        }

        var pending = PendingHole();
        if (pending is not null)
        {
            outlines.Add(new GroundOutline(pending.Outline(), HolePreviewColor));
            outlines.AddRange(QuadLines(pending, HolePreviewColor with { W = 0.45f }));
            if (_holePlacesReady is { } places)
            {
                // The ground of every component near the hole where the game draws holes (green), and of the one under it when
                // the game does not (amber).
                foreach (var place in places)
                {
                    var (dx, dy) = (MathF.Max(0f, MathF.Max(place.Min.X - pending.X, pending.X - place.Max.X)), MathF.Max(0f, MathF.Max(place.Min.Y - pending.Y, pending.Y - place.Max.Y)));
                    if (place.Visible ? (dx * dx) + (dy * dy) <= HolePlaceRadiusCm * HolePlaceRadiusCm : place.Contains(pending.X, pending.Y))
                    {
                        outlines.Add(new GroundOutline(
                            [place.Min, new(place.Max.X, place.Min.Y), place.Max, new(place.Min.X, place.Max.Y), place.Min],
                            place.Visible ? HoleVisibleColor : HoleCollisionOnlyColor));
                    }
                }
            }
        }

        HoleOutlines = outlines;
        UpdateHolePlace(pending);
    }

    /// <summary>Tells, before the cut, whether the game will draw the hole under the cursor or only open its collision.</summary>
    private void UpdateHolePlace(TerrainHole? pending)
    {
        string? text = null;
        var (visible, collisionOnly) = (false, false);
        if (pending is not null)
        {
            if (_holePlacesReady is not { } places)
            {
                text = Loc.T("Map.Hole.Place.Checking");
            }
            else
            {
                var (min, max) = pending.Bounds;
                var under = places.Where(p => p.Min.X < max.X && min.X < p.Max.X && p.Min.Y < max.Y && min.Y < p.Max.Y).ToList();
                (visible, collisionOnly) = (under.Count > 0 && under.All(p => p.Visible), under.Any(p => !p.Visible));
                text = under.Count == 0 ? null
                    : visible ? Loc.T("Map.Hole.Place.Visible")
                    : under.Any(p => p.Visible) ? Loc.T("Map.Hole.Place.Partly")
                    : Loc.T("Map.Hole.Place.CollisionOnly");
            }
        }

        HolePlaceText = text;
        HolePlaceVisible = visible;
        HolePlaceCollisionOnly = collisionOnly;
    }

    /// <summary>Reads, once per game catalog and in the background, which components can show a hole (see <see cref="TerrainHoleLayers.Scan"/>).</summary>
    private async Task LoadHolePlacesAsync()
    {
        var catalog = _services.Workspace.Catalog;
        if (_holePlaces is null || !ReferenceEquals(catalog, _holePlacesCatalog))
        {
            _holePlacesCatalog = catalog;
            _holePlacesReady = null;
            _holePlaces = catalog is null
                ? Task.FromResult<IReadOnlyList<TerrainHoleLayers.Place>>([])
                : Task.Run<IReadOnlyList<TerrainHoleLayers.Place>>(() => TerrainHoleLayers.Scan(catalog));
        }

        var task = _holePlaces;
        try
        {
            var places = await task.ConfigureAwait(true);
            if (ReferenceEquals(task, _holePlaces) && _holePlacesReady is null)
            {
                _holePlacesReady = places;
                RefreshHoleOutlines();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ObjectDisposedException)
        {
            _holePlacesReady = [];
        }
    }

    /// <summary>Tests: the places as the scan of a catalog would give them.</summary>
    internal void UseHolePlaces(IReadOnlyList<TerrainHoleLayers.Place> places)
    {
        _holePlacesCatalog = _services.Workspace.Catalog;
        _holePlaces = Task.FromResult(places);
        _holePlacesReady = places;
        RefreshHoleOutlines();
    }

    /// <summary>The quads a hole cuts: the edges of the cut area and one diagonal across each quad.</summary>
    internal static IEnumerable<GroundOutline> QuadLines(TerrainHole hole, Vector4 color)
    {
        const float q = TerrainHole.QuadCm;
        var quads = hole.WorldQuads().ToHashSet();
        foreach (var (x, y) in quads)
        {
            var (x0, y0, x1, y1) = (x * q, y * q, (x + 1) * q, (y + 1) * q);
            yield return new GroundOutline([new(x0, y0), new(x1, y1)], color);
            if (!quads.Contains((x, y - 1)))
            {
                yield return new GroundOutline([new(x0, y0), new(x1, y0)], color);
            }

            if (!quads.Contains((x, y + 1)))
            {
                yield return new GroundOutline([new(x0, y1), new(x1, y1)], color);
            }

            if (!quads.Contains((x - 1, y)))
            {
                yield return new GroundOutline([new(x0, y0), new(x0, y1)], color);
            }

            if (!quads.Contains((x + 1, y)))
            {
                yield return new GroundOutline([new(x1, y0), new(x1, y1)], color);
            }
        }
    }

    private Task<IReadOnlyList<(string Tile, Vector2 Min, Vector2 Max)>> HoleTiles()
    {
        var catalog = _services.Workspace.Catalog;
        if (_holeTiles is null || !ReferenceEquals(catalog, _holeTilesCatalog))
        {
            _holeTilesCatalog = catalog;
            _holeTiles = catalog is null
                ? Task.FromResult<IReadOnlyList<(string, Vector2, Vector2)>>([])
                : Task.Run(() => TerrainHoles.Tiles(catalog));
        }

        return _holeTiles;
    }
}
