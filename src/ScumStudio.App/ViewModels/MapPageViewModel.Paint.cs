using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Brush paint mode (owner: "choose 5–10 tree types, rocks, bushes, any objects; sweep the circle and it plants them the
/// way the game's own forest looks, so I don't plant them one by one"). Holding the left button scatters the palette's
/// objects evenly inside the circle along the stroke (<see cref="BrushScatter"/>), no two closer than the spacing measured
/// at their feet, never on roads, water, rocks or (unless allowed) objects, turned at random and a little bigger or
/// smaller; hovering shows the spots a click plants (<see cref="PaintSpots"/>). A tree,
/// bush or rock whose mesh the nearest loaded landscape tile has as foliage becomes a new instance of that foliage
/// (chopped and collided like the stock ones), anything else a new mesh actor or a copy of a placed Blueprint. The stroke
/// is drawn as it goes and journaled when the button is let go: one History row that undoes together.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>The palette file in the project folder.</summary>
    public const string PaintPaletteFileName = "brush-palette.json";

    /// <summary>Map cells (<see cref="SupportCell"/>) per side of the ground blocks a stroke loads: 200 m.</summary>
    private const int SupportBlock = 10;

    /// <summary>Viewport ids of the stroke's not yet journaled actors (above the project's added ids).</summary>
    private const uint PreviewIdBase = 0xC000_0000u;

    /// <summary>How far above the ground a lake or river surface is looked for (cm): it lies over its bed.</summary>
    private const float WaterProbeAbove = 5000f;

    /// <summary>A water surface counts when it is more than this above the ground (cm): a dry bed's edge does not.</summary>
    private const float WaterProbeBelow = 10f;

    /// <summary>How far above the ground a road piece, rock or object top under a spot is looked for (cm).</summary>
    private const float ObjectProbeAbove = 300f;

    /// <summary>How far below the ground such a top still counts (cm): road pieces sink into it a little.</summary>
    private const float ObjectProbeBelow = 100f;

    /// <summary>
    /// What is less than this across (cm) neither keeps plants away nor stands under them (pebbles, debris); plants
    /// (grass, flowers) count from twice that.
    /// </summary>
    private const float SmallThing = 50f;

    /// <summary>Lattice spots whose ground a preview stroke keeps (its gathered map cells are bounded too) before it is made again.</summary>
    private const int PaintSitesKept = 250_000;

    private static readonly JsonSerializerOptions PaletteJson = new() { WriteIndented = true };

    private static readonly (PaintVerdict Verdict, string Key)[] PaintSkipKeys =
    [
        (PaintVerdict.Road, "Map.Paint.Skipped.Road"), (PaintVerdict.Water, "Map.Paint.Skipped.Water"), (PaintVerdict.Rock, "Map.Paint.Skipped.Rock"),
        (PaintVerdict.OnObject, "Map.Paint.Skipped.Object"), (PaintVerdict.NoGround, "Map.Paint.Skipped.NoGround"),
    ];

    private ObjectPicker? _paintPicker;
    private IReadOnlyList<ReplaceCandidate>? _paintables;
    private PaintStroke? _stroke;
    private PaintStroke? _dryStroke; // what the hover preview gathered, taken over by the click while nothing changed
    private ulong _paintSeed = BrushScatter.Mix((ulong)Environment.TickCount64); // the next stroke's lattice, new after each journaled one
    private int _paletteVersion;
    private Project? _paletteProject;
    private bool _loadingPalette;
    private PreparedLevelScene? _paintScene;
    private Dictionary<string, string?> _foliageCollision = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<LevelDocument, (Vector2 Min, Vector2 Max)> _tileBoxes = [];
    private readonly Dictionary<string, Task<(string Level, ActorRecord Actor, IReadOnlyList<ScenePlacement> Placements)?>> _paintSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Brush mode: false selects what the circle covers, true plants the palette (<see cref="PaintAt"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushOnGround))]
    private bool _brushPaint;

    /// <summary>Least distance between two planted objects (and to an existing one), metres.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaintSpacingText))]
    private double _paintSpacing = 1;

    /// <summary>
    /// The largest spacing (m): each planted object keeps its own random distance between <see cref="PaintSpacing"/> and this
    /// from its neighbours (owner: "between each tree 1, 2 or 3 m, random within these limits").
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaintSpacingText))]
    private double _paintSpacingMax = 3;

    /// <summary>
    /// Timer mode (owner: "a timer: every so many seconds it adds a tree where the brush is, wherever I move it"): while the
    /// button is held, <see cref="PaintOneAt"/> plants one object every <see cref="PaintEvery"/> seconds, moving or not.
    /// </summary>
    [ObservableProperty]
    private bool _paintTimed;

    /// <summary>Seconds between two objects in timer mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaintEveryText))]
    private double _paintEvery = 0.5;

    /// <summary>The timer's interval as text ("0.5 s").</summary>
    public string PaintEveryText => string.Create(CultureInfo.CurrentCulture, $"{PaintEvery:0.##} s");

    /// <summary>Plants come a little bigger or smaller (0.9–1.15; buildings and Blueprints always 1).</summary>
    [ObservableProperty]
    private bool _paintRandomSize = true;

    /// <summary>Each planted object is turned at random about its upright axis.</summary>
    [ObservableProperty]
    private bool _paintRandomTurn = true;

    /// <summary>Also plant on roofs, floors, bridges and other objects; off (default) only on bare ground.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushOnGround))]
    private bool _paintOnObjects;

    /// <summary>The spots a click at the hovered circle plants (true) or leaves out (false), at their feet; null with none to show.</summary>
    [ObservableProperty]
    private IReadOnlyList<(FVector At, bool Plants)>? _paintSpots;

    /// <summary>"12 planted · 3 left out" of the open or last stroke; empty before one.</summary>
    [ObservableProperty]
    private string _paintReportText = string.Empty;

    /// <summary>What a click plants and leaves out when the hovered circle has more spots than <see cref="PaintMarkCap"/>; else empty.</summary>
    [ObservableProperty]
    private string _paintMarksText = string.Empty;

    /// <summary>
    /// Spot marks handed to the view, at most (8 lines each): the preview tries every spot of the circle (a 100 m one has
    /// up to ~18k), and above this it marks an even share of them and says so (<see cref="PaintMarksText"/>).
    /// </summary>
    internal int PaintMarkCap { get; set; } = 6000;

    /// <summary>Strokes made so far, the hover preview's included (each gathers the ground under the circle anew).</summary>
    internal int PaintStrokesMade { get; private set; }

    /// <summary>True when the brush plants on bare ground only: the viewport lays the circle on the landscape.</summary>
    public bool BrushOnGround => BrushPaint && !PaintOnObjects;

    /// <summary>The spacing range as text ("1–3 m", or "3.5 m" when both ends are the same).</summary>
    public string PaintSpacingText => Math.Abs(PaintSpacingMax - PaintSpacing) < 0.05
        ? string.Create(CultureInfo.CurrentCulture, $"{PaintSpacing:0.#} m")
        : string.Create(CultureInfo.CurrentCulture, $"{PaintSpacing:0.#}–{PaintSpacingMax:0.#} m");

    /// <summary>What a stroke plants (picked from <see cref="PaintPicker"/>), saved with the project.</summary>
    public ObservableCollection<ReplaceCandidate> PaintPalette { get; } = [];

    /// <summary>True when the palette has something to plant.</summary>
    public bool HasPaintPalette => PaintPalette.Count > 0;

    /// <summary>"Objects (3)": the palette button's text.</summary>
    public string PaintPaletteText => Loc.F("Map.Paint.Palette", PaintPalette.Count);

    /// <summary>The card that adds to the palette: the selection's family first, then the game's trees, bushes and rocks.</summary>
    public ObjectPicker PaintPicker => _paintPicker ??= new ObjectPicker(OnPaintPicked);

    /// <summary>The game's trees, bushes and rocks (the paint picker's list after the selection's family).</summary>
    private IReadOnlyList<ReplaceCandidate> Paintables => _paintables ??= AssetDumper.Packages
        .Where(p => p.ClassName == "StaticMesh" && IsPaintable(p.PackagePath))
        .Select(p => p.PackagePath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(p => new ReplaceCandidate(new ReplaceChoice(TreeSwapRow.Leaf(p), p, false, false), LoadReplaceThumbnailAsync))
        .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    // ponytail: folder names of the game's plants and rocks; extend when the owner misses one.
    private static bool IsPaintable(string path) =>
        !FarModels.IsFarViewMesh(path)
        && ((path.Contains("/Foliage/", StringComparison.OrdinalIgnoreCase)
             && (path.Contains("/Trees/", StringComparison.OrdinalIgnoreCase) || path.Contains("/Bush/", StringComparison.OrdinalIgnoreCase))
             && !path.Contains("Debris", StringComparison.OrdinalIgnoreCase) && !path.Contains("Chunk", StringComparison.OrdinalIgnoreCase)
             && !path.Contains("Branch", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("_Menu", StringComparison.OrdinalIgnoreCase))
            || (path.Contains("/Landscape/Rocks/", StringComparison.OrdinalIgnoreCase) && !path.Contains("Cave_", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("Underwater", StringComparison.OrdinalIgnoreCase) && !path.Contains("/Cliff", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Fills the paint picker: the family of the selection (any mesh or Blueprint), then the trees, bushes and rocks; called when the menu opens.</summary>
    public void RefreshPaintCandidates()
    {
        var current = ReplaceMembers().Select(m => CurrentPathOf(m.Item, m.Instance)).FirstOrDefault(p => p is not null);
        var family = current is null ? [] : ReplaceFamilies.Candidates(current, AssetDumper.Packages)
            .Select(c => new ReplaceCandidate(c with { IsCurrent = false }, LoadReplaceThumbnailAsync)).ToList();
        var seen = family.Select(c => c.Choice.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PaintPicker.SetItems([.. family, .. Paintables.Where(p => !seen.Contains(p.Choice.PackagePath))]);
    }

    /// <summary>Adds <paramref name="candidate"/> to the palette (once).</summary>
    public void AddToPalette(ReplaceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (PaintPalette.Any(p => string.Equals(p.Choice.ObjectPath, candidate.Choice.ObjectPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        PaintPalette.Add(candidate);
        if (candidate.Choice.IsBlueprint)
        {
            PaintSourceOf(candidate.Choice); // the placed one to copy is found now, not in the middle of a stroke
        }
        else
        {
            EnsureMeshLoaded(candidate.Choice.ObjectPath); // its size for the ground fit, its model for a mesh actor
        }

        OnPaletteChanged();
    }

    [RelayCommand]
    private void RemovePaintItem(ReplaceCandidate? candidate)
    {
        if (candidate is not null && PaintPalette.Remove(candidate))
        {
            OnPaletteChanged();
        }
    }

    private void OnPaintPicked()
    {
        if (_paintPicker?.Selected is not { } picked)
        {
            return;
        }

        _paintPicker.Selected = null; // the card stays an "add" card
        AddToPalette(picked);
    }

    private void OnPaletteChanged()
    {
        _paletteVersion++;
        OnPropertyChanged(nameof(HasPaintPalette));
        OnPropertyChanged(nameof(PaintPaletteText));
        SavePaintPalette();
    }

    partial void OnPaintSpacingChanged(double value)
    {
        if (PaintSpacingMax < value)
        {
            PaintSpacingMax = value; // the range never turns round
        }

        SavePaintPalette();
    }

    partial void OnPaintSpacingMaxChanged(double value)
    {
        if (PaintSpacing > value)
        {
            PaintSpacing = value;
        }

        SavePaintPalette();
    }

    partial void OnPaintRandomSizeChanged(bool value) => SavePaintPalette();

    partial void OnPaintRandomTurnChanged(bool value) => SavePaintPalette();

    partial void OnPaintOnObjectsChanged(bool value) => SavePaintPalette();

    partial void OnBrushPaintChanged(bool value)
    {
        EndPaintStroke();
        if (value && PreparedScene is { } scene)
        {
            // The brush's map of the levels is made now, not at the first stroke (~80 ms with a farm and its tiles).
            _ = BrushIndexNow();
            PreparePaintScene(scene);
        }

        if (!value)
        {
            ShowPaintSpots(null);
            PaintReportText = string.Empty;
        }
    }

    partial void OnBrushSelectChanged(bool value)
    {
        EndPaintStroke();
        if (!value)
        {
            ShowPaintSpots(null);
        }
    }

    /// <summary>The palette file of the open project (null without one).</summary>
    private string? PaletteFile => _services.Projects.Current is { } project ? Path.Combine(project.DirectoryPath, PaintPaletteFileName) : null;

    private void SavePaintPalette()
    {
        if (_loadingPalette || PaletteFile is not { } file)
        {
            return;
        }

        try
        {
            var data = new PaintPaletteFile([.. PaintPalette.Select(p => p.Choice.ObjectPath)], PaintSpacing, PaintRandomSize, PaintRandomTurn, PaintSpacingMax, PaintOnObjects);
            File.WriteAllText(file, JsonSerializer.Serialize(data, PaletteJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("The brush palette could not be saved to {File}: {Message}", file, ex.Message);
        }
    }

    /// <summary>Reads the open project's palette when another project opened (called with every journal change).</summary>
    private void SyncPaintPalette()
    {
        var project = _services.Projects.Current;
        if (ReferenceEquals(project, _paletteProject))
        {
            return;
        }

        _paletteProject = project;
        _loadingPalette = true;
        try
        {
            PaintPalette.Clear();
            if (PaletteFile is { } file && File.Exists(file) && JsonSerializer.Deserialize<PaintPaletteFile>(File.ReadAllText(file)) is { } data)
            {
                foreach (var path in data.Items ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        AddToPalette(Paintables.FirstOrDefault(p => string.Equals(p.Choice.ObjectPath, path, StringComparison.OrdinalIgnoreCase)) ?? CandidateOf(path));
                    }
                }

                PaintSpacingMax = 20; // the max first, so the min is not pulled down on the way
                PaintSpacing = Math.Clamp(data.Spacing, 1, 20);
                PaintSpacingMax = Math.Clamp(data.SpacingMax > 0 ? data.SpacingMax : data.Spacing, PaintSpacing, 20); // older files: one spacing
                PaintRandomSize = data.RandomSize;
                PaintRandomTurn = data.RandomTurn;
                PaintOnObjects = data.OnObjects;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _services.Logger.LogWarning("The brush palette could not be read: {Message}", ex.Message);
        }
        finally
        {
            _loadingPalette = false;
            OnPropertyChanged(nameof(HasPaintPalette));
            OnPropertyChanged(nameof(PaintPaletteText));
        }
    }

    /// <summary>A palette entry for an object path (<c>/Game/X/SM_A.SM_A</c> or a Blueprint's <c>/Game/X/BP_A.BP_A_C</c>).</summary>
    private ReplaceCandidate CandidateOf(string objectPath)
    {
        var package = AssetPaths.SplitObjectPath(objectPath).PackagePath;
        return new ReplaceCandidate(new ReplaceChoice(TreeSwapRow.Leaf(package), package, objectPath.EndsWith("_C", StringComparison.Ordinal), false), LoadReplaceThumbnailAsync);
    }

    /// <summary>The placed Blueprint a palette entry copies, or null while it is looked up (started on the first ask) or when none is placed.</summary>
    private (string Level, ActorRecord Actor, IReadOnlyList<ScenePlacement> Placements)? PaintSourceOf(ReplaceChoice choice)
    {
        if (!_paintSources.TryGetValue(choice.PackagePath, out var task))
        {
            if (_services.Workspace.Catalog is not { } catalog)
            {
                return null;
            }

            _paintSources[choice.PackagePath] = task = Find();

            async Task<(string Level, ActorRecord Actor, IReadOnlyList<ScenePlacement> Placements)?> Find()
            {
                var index = await PlacedIndexAsync(catalog).ConfigureAwait(false);
                var found = await Task.Run(() => FirstReadable(catalog, index.Of(choice.PackagePath))).ConfigureAwait(false);
                if (found is null)
                {
                    _services.Notifications.Warning(Loc.T("Map.BlueprintNotPlaced"), Loc.F("Map.BlueprintNotPlacedDetail", choice.Name));
                }

                return found;
            }
        }

        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    /// <summary>
    /// One dab of a paint stroke: plants in the circle at <paramref name="centre"/> swept from <paramref name="from"/> (the
    /// stroke's previous dab; null starts there). The first dab of a stroke opens it, <see cref="EndPaintStroke"/> journals it.
    /// </summary>
    public void PaintAt(FVector centre, FVector? from = null)
    {
        var stroke = OpenPaintStroke();
        if (stroke.Kinds.Count == 0)
        {
            return; // nothing to plant (said once when the stroke began)
        }

        var start = from ?? centre;
        var (a, b) = (new Vector2(start.X, start.Y), new Vector2(centre.X, centre.Y));
        var radius = (float)BrushRadius * 100f;

        // A fast sweep is cut into pieces no longer than the circle is wide, so each piece scans only cells near the way.
        var pieces = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / (2f * radius)));
        var before = stroke.Planted;
        for (var i = 0; i < pieces; i++)
        {
            PaintPiece(stroke, Vector2.Lerp(a, b, i / (float)pieces), Vector2.Lerp(a, b, (i + 1) / (float)pieces), radius, centre.Z);
        }

        if (stroke.Planted > before)
        {
            ShowPaintPreview(stroke);
        }

        ReportPaint(stroke);
        PreviewPaintAt(centre);
    }

    /// <summary>
    /// One tick of the timer mode: one object at the free spot nearest <paramref name="centre"/> inside the circle (the
    /// spacing and the ground rules still apply); nothing when the circle is full, which is said once a stroke. It joins
    /// the open stroke, journaled by <see cref="EndPaintStroke"/>.
    /// </summary>
    public void PaintOneAt(FVector centre)
    {
        var stroke = OpenPaintStroke();
        if (stroke.Kinds.Count == 0)
        {
            return;
        }

        var radius = (float)BrushRadius * 100f;
        var c = new Vector2(centre.X, centre.Y);
        PrepareCircle(stroke, c, c, radius);
        stroke.Plane = PaintPlane(stroke, centre);
        var nearest = BrushScatter.Nearest(stroke.Seed, stroke.Cell, c, radius, stroke.LeastSpacing, stroke.Spacing, stroke.Tried);
        if (BrushScatter.Next(nearest, stroke.Tried, spot => TryPlant(stroke, spot, centre.Z, null)) is not null)
        {
            ShowPaintPreview(stroke);
        }
        else if (!stroke.SaidFull)
        {
            stroke.SaidFull = true;
            _services.Notifications.Info(Loc.T("Map.Paint"), Loc.T("Map.Paint.Full"));
        }

        ReportPaint(stroke);
        PreviewPaintAt(centre);
    }

    /// <summary>
    /// Shows in <see cref="PaintSpots"/> what a click (or the next timer tick) at <paramref name="centre"/> plants and leaves
    /// out: every spot of the circle with the same obstacles and ground rules, tried and taken back. What it gathered is
    /// kept for the click.
    /// </summary>
    public void PreviewPaintAt(FVector centre)
    {
        if (!BrushPaint || (_stroke ?? DryPaintStroke()) is not { Kinds.Count: > 0 } stroke)
        {
            ShowPaintSpots(null);
            return;
        }

        var spots = new List<(FVector At, bool Plants)>();
        var (planted, skipped) = (stroke.Index.Count, stroke.Skipped.Count);
        var radius = (float)BrushRadius * 100f;
        var c = new Vector2(centre.X, centre.Y);
        try
        {
            if (PaintTimed)
            {
                PrepareCircle(stroke, c, c, radius);
                stroke.Plane = PaintPlane(stroke, centre);
                foreach (var spot in BrushScatter.Nearest(stroke.Seed, stroke.Cell, c, radius, stroke.LeastSpacing, stroke.Spacing, stroke.Tried))
                {
                    if (TryPlant(stroke, spot, centre.Z, spots))
                    {
                        break; // one a tick
                    }
                }
            }
            else
            {
                PaintPiece(stroke, c, c, radius, centre.Z, spots);
            }
        }
        finally
        {
            stroke.Index.Rollback(planted);
            stroke.Skipped.Rollback(skipped);
        }

        ShowPaintSpots(spots);
    }

    /// <summary>Hands the preview's spots to the view: all of them, or an even share above <see cref="PaintMarkCap"/> (then said with the counts).</summary>
    private void ShowPaintSpots(List<(FVector At, bool Plants)>? spots)
    {
        if (spots is null || spots.Count <= PaintMarkCap)
        {
            PaintSpots = spots;
            PaintMarksText = string.Empty;
            return;
        }

        // The spots come in their random hash order (the timer's nearest first), so every n-th of each kind is an even share
        // of the circle; each kind has half the marks, so the timer's one green spot is never thinned out by red ones.
        var half = Math.Max(1, PaintMarkCap / 2);
        var plants = spots.Count(s => s.Plants);
        var (everyPlant, everyLeft) = (Math.Max(1, (plants + half - 1) / half), Math.Max(1, (spots.Count - plants + half - 1) / half));
        var (p, l) = (0, 0);
        PaintSpots = spots.Where(s => s.Plants ? p++ % everyPlant == 0 : l++ % everyLeft == 0).ToList();
        PaintMarksText = Loc.F("Map.Paint.Marks", plants, spots.Count - plants);
    }

    /// <summary>Journals the open stroke as one edit ("Planted N objects", what was left out and why); nothing when it planted nothing.</summary>
    public void EndPaintStroke()
    {
        if (_stroke is not { } stroke)
        {
            return;
        }

        _stroke = null;
        LogPaintBlocks(stroke);
        var left = LeftOutText(stroke);
        if (stroke.Ops.Count == 0)
        {
            if (left.Length > 0)
            {
                _services.Notifications.Info(Loc.T("Map.Paint"), left);
            }

            return;
        }

        if (ReferenceEquals(_services.Projects.Current, stroke.Project))
        {
            var title = Loc.F("Map.Paint.Done", stroke.Ops.Count);
            try
            {
                // The journal change refreshes the edits: its objects replace the stroke's preview.
                var entry = _services.Projects.Apply(new BatchOp(title, stroke.Ops));
                _paintSeed = BrushScatter.Mix(_paintSeed); // the next stroke's spots differ, so it can fill this one's gaps
                _services.Notifications.Info(title, entry.Op.Describe() + (left.Length > 0 ? " " + left : string.Empty) + Loc.T("Map.CtrlZUndoes"));
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                _services.Notifications.Error(Loc.T("Map.Paint"), ex.Message);
            }
        }

        RefreshEdits(); // nothing journaled: the preview goes
    }

    /// <summary>The open stroke, or a new one: the hover preview's when nothing changed since it was gathered.</summary>
    private PaintStroke OpenPaintStroke()
    {
        if (_stroke is { } open && !ReferenceEquals(open.Scene, PreparedScene))
        {
            EndPaintStroke(); // the loaded levels changed under the stroke
        }

        if (_stroke is null)
        {
            var dry = _dryStroke;
            _dryStroke = null;
            _stroke = dry is { Kinds.Count: > 0 } && dry.Key == PaintKeyNow() ? dry : BeginPaintStroke(quiet: false);
            ReportPaint(_stroke);
        }

        return _stroke;
    }

    /// <summary>The hover preview's stroke for the brush as it is now (made again when anything it depends on changed); null without levels, project or palette.</summary>
    private PaintStroke? DryPaintStroke()
    {
        // A palette Blueprint still looked up (or never found) is in the key by its finished lookups, so hovering does not
        // gather the ground again on every move while it is missing from the stroke.
        var key = PaintKeyNow();
        if (_dryStroke is { } dry && dry.Key == key && dry.Gathered.Count < 4096 && dry.Sites.Count < PaintSitesKept)
        {
            return dry;
        }

        return _dryStroke = key.Scene is { Documents.Count: > 0 } && key.Project is not null && PaintPalette.Count > 0 ? BeginPaintStroke(quiet: true) : null;
    }

    private PaintKey PaintKeyNow()
    {
        var project = _services.Projects.Current;
        var edits = project?.Journal.Applied;
        var sources = PaintPalette.Count(p => p.Choice.IsBlueprint && _paintSources.TryGetValue(p.Choice.PackagePath, out var t) && t.IsCompleted);
        return new PaintKey(PreparedScene, WorldBackdrop, project, edits?.Count ?? 0, edits is { Count: > 0 } ? edits[edits.Count - 1] : null, _paletteVersion,
            sources, PaintSpacing, PaintSpacingMax, BrushRadius, _paintSeed, PaintOnObjects, PaintRandomSize, PaintRandomTurn);
    }

    private void ReportPaint(PaintStroke stroke) => PaintReportText = Loc.F("Map.Paint.Report", stroke.Planted, stroke.Left.Values.Sum());

    /// <summary>"Left out: 12 on roads or paths, 3 in water." for the stroke's notification; empty when nothing was.</summary>
    private static string LeftOutText(PaintStroke stroke)
    {
        var parts = PaintSkipKeys.Where(k => stroke.Left.GetValueOrDefault(k.Verdict) > 0).Select(k => Loc.F(k.Key, stroke.Left[k.Verdict])).ToList();
        return parts.Count == 0 ? string.Empty : Loc.F("Map.Paint.Skipped", string.Join(", ", parts));
    }

    /// <summary>Logs the objects whose drawn box kept the most spots free and the meshes that left the most out (to tell later why a place stayed empty).</summary>
    private void LogPaintBlocks(PaintStroke stroke)
    {
        if (stroke.Blocks.Count > 0 || stroke.Deciders.Count > 0)
        {
            _services.Logger.LogInformation("Brush stroke: {Planted} planted, {Left} left out; boxes that kept the most spots free: {Blocks}; meshes under the most left out: {Meshes}",
                stroke.Planted, stroke.Left.Values.Sum(), Most(stroke.Blocks), Most(stroke.Deciders));
        }

        static string Most(Dictionary<string, int> counts) =>
            string.Join(", ", counts.OrderByDescending(b => b.Value).Take(3).Select(b => b.Key + " (" + b.Value.ToString(CultureInfo.InvariantCulture) + ")"));
    }

    private PaintStroke BeginPaintStroke(bool quiet)
    {
        PaintStrokesMade++;
        var scene = PreparedScene;
        var project = _services.Projects.Current;
        var kinds = new List<PaintKind>();
        (string Title, string Detail, bool Warning)? say = null;
        if (scene is not { Documents.Count: > 0 })
        {
            say = (Loc.T("Map.NoLevelLoaded"), Loc.T("Map.NoLevelLoadedDetail"), true);
        }
        else if (project is null)
        {
            say = (Loc.T("History.NoProject"), Loc.T("Map.NoProject.Adds"), true);
        }
        else if (PaintPalette.Count == 0)
        {
            say = (Loc.T("Map.Paint"), Loc.T("Map.Paint.Empty"), false);
        }
        else
        {
            PreparePaintScene(scene);
            foreach (var choice in PaintPalette.Select(p => p.Choice))
            {
                if (choice.IsBlueprint)
                {
                    if (PaintSourceOf(choice) is { } source)
                    {
                        kinds.Add(new PaintKind(choice.ObjectPath, true, true, false, null, null, source, PaintClass.Other));
                    }

                    continue; // not found yet: the next stroke has it
                }

                var mesh = choice.ObjectPath;
                EnsureMeshLoaded(mesh);
                kinds.Add(new PaintKind(mesh, false, IsPlant(mesh) || mesh.Contains("/Rocks/", StringComparison.OrdinalIgnoreCase),
                    !mesh.Contains("/Buildings/", StringComparison.OrdinalIgnoreCase), MeshBounds(mesh), _foliageCollision.GetValueOrDefault(mesh), null,
                    IsPlant(mesh) ? PlantClass(mesh, null) : PaintClass.Other));
            }
        }

        if (!quiet && say is { } message)
        {
            if (message.Warning)
            {
                _services.Notifications.Warning(message.Title, message.Detail);
            }
            else
            {
                _services.Notifications.Info(message.Title, message.Detail);
            }
        }

        var least = (float)Math.Clamp(PaintSpacing, 1, 20) * 100f;
        var spacing = MathF.Max(least, (float)Math.Clamp(PaintSpacingMax, 1, 20) * 100f); // the index cells hold the largest distance
        var radius = (float)BrushRadius * 100f;
        return new PaintStroke(project!, scene!, kinds, spacing, BrushScatter.CellSize(least, radius), _paintSeed, PaintRandomSize, PaintRandomTurn)
        {
            LeastSpacing = least,
            OnObjects = PaintOnObjects,
            Key = PaintKeyNow(),
            Target = scene is { Documents.Count: > 0 } ? NewObjectLevel(scene) : null,
        };
    }

    /// <summary>Per loaded scene: the collision profile of each foliage mesh and the ground box of each landscape tile.</summary>
    private void PreparePaintScene(PreparedLevelScene scene)
    {
        if (ReferenceEquals(_paintScene, scene))
        {
            return;
        }

        _paintScene = scene;
        _foliageCollision = new(StringComparer.OrdinalIgnoreCase);
        _tileBoxes = [];
        foreach (var item in _pristineActors)
        {
            foreach (var c in item.Actor.Components)
            {
                if (c is { IsInstanced: true, StaticMeshPath: { } mesh, CollisionProfile: { } profile })
                {
                    _foliageCollision.TryAdd(mesh, profile);
                }
            }

            if (!item.Level.Name.StartsWith("Landscape_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A tile's box: where its actors and foliage are (the terrain proxies sit on its corners, the foliage all over it).
            var box = _tileBoxes.TryGetValue(item.Level, out var b) ? b : (new Vector2(float.MaxValue), new Vector2(float.MinValue));
            foreach (var p in item.Actor.InstanceTransforms.Select(i => i.WorldTransform.Translation).Append(item.Actor.WorldTransform.Translation))
            {
                box = (Vector2.Min(box.Item1, new Vector2(p.X, p.Y)), Vector2.Max(box.Item2, new Vector2(p.X, p.Y)));
            }

            _tileBoxes[item.Level] = box;
        }
    }

    /// <summary>
    /// Plants along one piece of the stroke (a capsule from <paramref name="a"/> to <paramref name="b"/>): the lattice's spots
    /// the circle covers for the first time, in their hash order. With <paramref name="preview"/> nothing is planted or
    /// marked tried: the spots that would be go into it, all of them, in the click's order.
    /// </summary>
    private void PaintPiece(PaintStroke stroke, Vector2 a, Vector2 b, float radius, float markZ, List<(FVector At, bool Plants)>? preview = null)
    {
        PrepareCircle(stroke, a, b, radius);
        stroke.Plane = PaintPlane(stroke, new FVector(b.X, b.Y, markZ));
        foreach (var spot in BrushScatter.Sweep(stroke.Seed, stroke.Cell, a, b, radius, stroke.LeastSpacing, stroke.Spacing, stroke.Tried))
        {
            if (preview is null)
            {
                stroke.Tried.Add((spot.X, spot.Y));
            }

            TryPlant(stroke, spot, markZ, preview);
        }
    }

    /// <summary>Takes in the obstacles and the ground under the circle swept from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private void PrepareCircle(PaintStroke stroke, Vector2 a, Vector2 b, float radius)
    {
        GatherPaintObstacles(stroke, Vector2.Min(a, b) - new Vector2(radius + stroke.Spacing), Vector2.Max(a, b) + new Vector2(radius + stroke.Spacing));
        LoadSupports(stroke, Vector2.Min(a, b) - new Vector2(radius), Vector2.Max(a, b) + new Vector2(radius));
    }

    /// <summary>
    /// Plants the palette object of <paramref name="spot"/> there when its ground is free (<see cref="SiteOf"/>) and nothing
    /// stands within its distance of its foot; a spot left out (a road, water...) is counted once per plant it would have
    /// been. With <paramref name="preview"/> it is only listed there (and held in the spacing until the preview takes it back).
    /// <paramref name="markZ"/> only places the mark of a spot with no ground under it. With no landscape loaded at all the
    /// spacing is flat at any height: nothing tells the ground's heights apart there.
    /// </summary>
    private bool TryPlant(PaintStroke stroke, PaintSpot spot, float markZ, List<(FVector At, bool Plants)>? preview)
    {
        var kind = stroke.Kinds[(int)(BrushScatter.Mix(spot.Hash + 2) % (ulong)stroke.Kinds.Count)];
        var (verdict, foot, by) = SiteOf(stroke, spot);
        var window = float.IsNaN(stroke.Plane) ? SpacingIndex.Window(spot.Keep) : float.PositiveInfinity;
        if (verdict != PaintVerdict.NoGround && TooClose(stroke, spot.At, foot, spot.Keep, kind.Class, window, count: preview is null))
        {
            return false;
        }

        var at = new FVector(spot.At.X, spot.At.Y, float.IsNaN(foot) ? markZ : foot);
        if (verdict != PaintVerdict.Plant)
        {
            if (!stroke.Skipped.TooClose(spot.At, at.Z, stroke.LeastSpacing, PaintClass.Other))
            {
                stroke.Skipped.Add(new Vector3(at.X, at.Y, at.Z), PaintClass.Other);
                preview?.Add((at, false));
                if (preview is null)
                {
                    stroke.Left[verdict] = stroke.Left.GetValueOrDefault(verdict) + 1;
                    if (by is not null)
                    {
                        stroke.Deciders[by] = stroke.Deciders.GetValueOrDefault(by) + 1;
                    }
                }
            }

            return false;
        }

        if (preview is null)
        {
            return Plant(stroke, kind, spot, foot);
        }

        stroke.Index.Add(new Vector3(at.X, at.Y, at.Z), kind.Class);
        preview.Add((at, true));
        return true;
    }

    /// <summary>
    /// What is under <paramref name="spot"/> (kept per lattice cell for the stroke), the height its foot stands at (NaN with
    /// no ground) and the mesh that decided, if one did: the landscape, whose paint layers and the sea decide, then what a
    /// mesh right on it is (<see cref="SurfaceUnder"/>); off the loaded landscape, no ground. With no landscape loaded at
    /// all the stroke's <see cref="PaintStroke.Plane"/> is the ground (not kept: it follows the cursor).
    /// </summary>
    private (PaintVerdict Verdict, float Foot, string? By) SiteOf(PaintStroke stroke, PaintSpot spot)
    {
        var (x, y) = (spot.At.X, spot.At.Y);
        if (!float.IsNaN(stroke.Plane))
        {
            return SurfaceUnder(stroke, x, y, stroke.Plane);
        }

        if (stroke.Sites.TryGetValue((spot.X, spot.Y), out var known))
        {
            return known;
        }

        var field = stroke.Scene.HeightField is { } near && near.SampleHeight(x, y) is not null ? near : WorldBackdrop?.HeightField;
        var site = (Verdict: PaintVerdict.NoGround, Foot: float.NaN, By: (string?)null);
        if (field?.SampleHeight(x, y) is { } ground)
        {
            site = (BrushScatter.GroundVerdict(field.SampleLayers(x, y), ground, stroke.Scene.SeaLevelCm ?? WorldBackdrop?.SeaLevelCm), ground, null);
            if (site.Verdict == PaintVerdict.Plant)
            {
                site = SurfaceUnder(stroke, x, y, ground);
            }
        }

        stroke.Sites[(spot.X, spot.Y)] = site;
        return site;
    }

    /// <summary>
    /// The ground of a stroke with no landscape loaded at all (a level opened without its tile): the cursor's height, or
    /// the foot of the drawn mesh the cursor rests on, a canopy or a roof (<see cref="BrushScatter.Plane"/>). NaN while
    /// any landscape is loaded: the spots stand on it, never on the cursor's height (on a canopy that stacked trees).
    /// </summary>
    private float PaintPlane(PaintStroke stroke, FVector centre)
    {
        if (stroke.Scene.HeightField is not null || WorldBackdrop?.HeightField is not null)
        {
            return float.NaN;
        }

        var under = stroke.Supports.TryGetValue(SupportCellOf(centre.X, centre.Y), out var here)
            ? here.Where(s => centre.X >= s.Box.Min.X && centre.X <= s.Box.Max.X && centre.Y >= s.Box.Min.Y && centre.Y <= s.Box.Max.Y)
                .Select(s => (s.Box.Min.Z, s.Box.Max.Z, IsPlant(s.Mesh) ? s.World.Translation.Z : s.Box.Min.Z))
            : [];
        return BrushScatter.Plane(centre.Z, under);
    }

    /// <summary>
    /// What a drawn mesh right at <paramref name="ground"/> under (<paramref name="x"/>, <paramref name="y"/>) is: a road
    /// piece, water or a rock wins over an object; on an object (a roof, a floor) the foot goes onto its top when
    /// <see cref="PaintOnObjects"/> is on. Foliage and things under <see cref="SmallThing"/> across do not count (the
    /// spacing keeps plants apart, a pebble does not stop a tree). Plant with nothing there.
    /// </summary>
    private (PaintVerdict Verdict, float Foot, string? By) SurfaceUnder(PaintStroke stroke, float x, float y, float ground)
    {
        if (!stroke.Supports.TryGetValue(SupportCellOf(x, y), out var here))
        {
            return (PaintVerdict.Plant, ground, null);
        }

        PaintVerdict? found = null;
        var (top, by) = (float.MinValue, (string?)null);
        foreach (var s in here)
        {
            if (x < s.Box.Min.X || x > s.Box.Max.X || y < s.Box.Min.Y || y > s.Box.Max.Y || MathF.Max(s.Box.Size.X, s.Box.Size.Y) < SmallThing
                || BrushScatter.SurfaceVerdict(s.Mesh) is not { } kind)
            {
                continue;
            }

            // Water is looked for from high up (a lake's surface lies over its bed), anything else just above the ground.
            var water = kind == PaintVerdict.Water;
            var (from, lowest) = (ground + (water ? WaterProbeAbove : ObjectProbeAbove), ground - (water ? WaterProbeBelow : ObjectProbeBelow));
            if (s.Box.Min.Z > from || s.Box.Max.Z < lowest || SurfaceOf(s.Mesh, s.Bend)?.HighestBelow(s.World, x, y, from) is not { } z
                || (water ? z <= lowest : z < lowest))
            {
                continue;
            }

            if (found is null || kind < found || (kind == found && z > top))
            {
                (found, top, by) = (kind, z, s.Mesh);
            }
        }

        return found switch
        {
            null => (PaintVerdict.Plant, ground, null),
            PaintVerdict.OnObject when stroke.OnObjects => (PaintVerdict.Plant, top, null),
            _ => (found.Value, ground, by),
        };
    }

    /// <summary>
    /// What the ground under the stroke is made of (<see cref="SupportAt"/>), loaded in blocks of <see cref="SupportBlock"/>
    /// map cells as the stroke reaches them (the whole loaded scene at once cost 5–50 ms at every stroke's start).
    /// </summary>
    private void LoadSupports(PaintStroke stroke, Vector2 min, Vector2 max)
    {
        var (x0, y0) = SupportCellOf(min.X, min.Y);
        var (x1, y1) = SupportCellOf(max.X, max.Y);
        for (var by = (int)Math.Floor(y0 / (double)SupportBlock); by <= (int)Math.Floor(y1 / (double)SupportBlock); by++)
        {
            for (var bx = (int)Math.Floor(x0 / (double)SupportBlock); bx <= (int)Math.Floor(x1 / (double)SupportBlock); bx++)
            {
                if (stroke.SupportBlocks.Add((bx, by)))
                {
                    var (cx, cy) = (bx * SupportBlock, by * SupportBlock);
                    foreach (var (cell, supports) in Supports([], [], (cx, cy, cx + SupportBlock - 1, cy + SupportBlock - 1)))
                    {
                        stroke.Supports[cell] = supports; // the blocks' cells never overlap
                    }
                }
            }
        }
    }

    /// <summary>
    /// True when a plant or object stands closer to <paramref name="at"/> than <paramref name="keep"/> (cm, flat) within
    /// <paramref name="window"/> of the spot's foot: never the cursor's height, which on a canopy let trees stack.
    /// </summary>
    private static bool TooClose(PaintStroke stroke, Vector2 at, float foot, float keep, PaintClass kind, float window, bool count)
    {
        if (stroke.Index.TooClose(at, foot, keep, kind, window))
        {
            return true;
        }

        if (stroke.Boxes.TryGetValue(PaintCellOf(at, stroke.Spacing), out var boxes))
        {
            var probe = new BrushSweep(at, at, keep, foot - window, foot + window);
            foreach (var (outline, name) in boxes)
            {
                if (probe.Touches(outline))
                {
                    if (count)
                    {
                        stroke.Blocks[name] = stroke.Blocks.GetValueOrDefault(name) + 1;
                    }

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Takes in what stands in the stroke's way between <paramref name="min"/> and <paramref name="max"/> (map cells not
    /// taken in yet; on the first dab also what the project moved or added): a tree, bush or pin by its place, a rock, prop
    /// or building by what it draws. Grass and pebbles do not count.
    /// </summary>
    private void GatherPaintObstacles(PaintStroke stroke, Vector2 min, Vector2 max)
    {
        var index = BrushIndexNow();
        if (!stroke.Gathered.Contains((int.MinValue, int.MinValue)))
        {
            stroke.Gathered.Add((int.MinValue, int.MinValue));
            foreach (var (e, outline) in MovedBrushEntries(index))
            {
                AddObstacle(stroke, e, outline);
            }
        }

        var (moved, movedPieces) = (ActorTransforms, InstanceTransforms);
        var (x0, y0) = BrushCellOf(min.X, min.Y);
        var (x1, y1) = BrushCellOf(max.X, max.Y);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (!stroke.Gathered.Add((x, y)) || !index.Cells.TryGetValue((x, y), out var cell))
                {
                    continue;
                }

                foreach (var e in cell)
                {
                    if (e.Key is { } key ? !movedPieces.ContainsKey(key) : !moved.ContainsKey(e.Item.SelectableId))
                    {
                        AddObstacle(stroke, e, e.Outline);
                    }
                }
            }
        }
    }

    private void AddObstacle(PaintStroke stroke, BrushEntry e, BrushOutline outline)
    {
        if (e.Kind is BrushKind.Part or BrushKind.Segment || e.Item.IsDeleted || HiddenActorIds.Contains(e.Item.SelectableId)
            || (e.Key is { } key && HiddenInstanceKeys.Contains(key)))
        {
            return; // a part or road piece counts with its whole actor
        }

        var kind = PaintClass.Other;
        if (e.Kind == BrushKind.Instance)
        {
            var component = e.Item.Actor.FindComponent(e.Instance?.ComponentName ?? e.Key?.Component ?? string.Empty);
            var mesh = e.Instance?.StaticMeshPath ?? component?.StaticMeshPath;
            if (e.Instance is { } instance && mesh is not null && MeshBounds(mesh) is { IsEmpty: false } bounds)
            {
                var scale = instance.WorldTransform.Scale3D;
                var across = MathF.Max(bounds.Size.X * MathF.Abs(scale.X), bounds.Size.Y * MathF.Abs(scale.Y));
                if (across < (IsPlant(mesh) ? 2f * SmallThing : SmallThing))
                {
                    return; // grass, flowers, pebbles
                }

                if (!IsPlant(mesh))
                {
                    outline = BrushOutline.Of(instance.WorldTransform, bounds); // a rock by what it covers, a tree by its trunk
                }
            }

            kind = IsPlant(mesh) ? PlantClass(mesh!, component) : PaintClass.Other;
        }
        else if (e.Kind == BrushKind.Whole && e.Item.Actor.StaticMeshPath is { } mesh && IsPlant(mesh) && outline.Shapes is [var trunk, ..])
        {
            // A tree or bush standing as a mesh actor (the game's, or one an earlier stroke planted) keeps others off its
            // trunk, not off its whole crown: by its box the next stroke left every gap between them empty (owner, 3.1.0).
            var at = trunk.World.Translation;
            stroke.Index.Add(new Vector3(at.X, at.Y, at.Z), PlantClass(mesh, null));
            return;
        }

        if (outline.Shapes is null)
        {
            var at = outline.Box.Min;
            stroke.Index.Add(new Vector3(at.X, at.Y, at.Z), kind);
            return;
        }

        // A box is listed in every lattice cell within the spacing of it (one lookup per candidate); ponytail: a box wider
        // than ~600 m at 3 m spacing is left out, add a coarser grid if such ones should keep plants away.
        var s = stroke.Spacing;
        var (x0, y0) = PaintCellOf(new Vector2(outline.Box.Min.X - s, outline.Box.Min.Y - s), s);
        var (x1, y1) = PaintCellOf(new Vector2(outline.Box.Max.X + s, outline.Box.Max.Y + s), s);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 40_000)
        {
            return;
        }

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                (stroke.Boxes.TryGetValue((x, y), out var list) ? list : stroke.Boxes[(x, y)] = []).Add((outline, e.Item.Name));
            }
        }
    }

    /// <summary>A bush (the game's bush folder, or foliage the game clears like grass) or a tree, for the spacing.</summary>
    private static PaintClass PlantClass(string mesh, ComponentRecord? component) =>
        mesh.Contains("/Bush/", StringComparison.OrdinalIgnoreCase) || (component is not null && GrassClearing.IsClearedFoliage(component)) ? PaintClass.Bush : PaintClass.Tree;

    private static (int X, int Y) PaintCellOf(Vector2 at, float size) => ((int)MathF.Floor(at.X / size), (int)MathF.Floor(at.Y / size));

    /// <summary>
    /// Plants <paramref name="kind"/> at <paramref name="spot"/> with its foot at <paramref name="foot"/> (a plant's pivot,
    /// anything else the bottom of its box), turned and sized by the spot's hash; false with nowhere to add it.
    /// </summary>
    private bool Plant(PaintStroke stroke, PaintKind kind, PaintSpot spot, float foot)
    {
        var h = spot.Hash;
        var yaw = stroke.RandomTurn ? BrushScatter.Unit(h, 3) * 360f : 0f;
        var size = stroke.RandomSize && kind.Jitter ? 0.9f + (BrushScatter.Unit(h, 4) * 0.25f) : 1f;
        var world = new FTransform(new FRotator(0f, yaw, 0f), new FVector(spot.At.X, spot.At.Y, foot), new FVector(size));
        if (!kind.Pivot && kind.Bounds is { IsEmpty: false } bounds)
        {
            var bottom = world.TransformPosition(new FVector(bounds.Center.X, bounds.Center.Y, bounds.Min.Z));
            world = world with { Translation = world.Translation with { Z = world.Translation.Z + (foot - bottom.Z) } };
        }

        var project = stroke.Project;
        if (!kind.IsBlueprint && FoliageFor(stroke, spot.At, kind.Path) is { } holder)
        {
            // A new tree of the tile's own foliage: chopped and collided like the stock ones.
            var (item, component) = holder;
            var op = EditOpFactory.AddInstance(item.Level, item.Actor, component.Name, TransformValue.FromTransform(world.GetRelativeTransform(component.WorldTransform)), project.State, stroke.Instances);
            stroke.Ops.Add(op);
            stroke.PreviewInstances[InstanceKey.Of(item.SelectableId, op.Target.Component, op.Target.Index)] = world;
        }
        else if (stroke.Target is not { } target)
        {
            return false;
        }
        else if (kind.Source is { } source)
        {
            var reference = new ActorRef(source.Level, source.Actor.Name);
            _foreignPlacements[reference] = source.Placements;
            var name = EditOpFactory.UniqueActorName(target, ShortName(source.Actor.ClassPath) + "_Added", project.State, stroke.Actors);
            stroke.Ops.Add(new AddBlueprintActorOp(target.PackagePath, name, source.Actor.ClassPath, reference, TransformValue.FromTransform(world)));
            stroke.PreviewClones.Add(new ActorClone(PreviewIdBase + (uint)stroke.PreviewClones.Count, 0, world, name, Placements: source.Placements));
        }
        else
        {
            // Not foliage of the tile under it: a mesh actor that collides as that foliage does elsewhere (or as a standing tree, see the exporter).
            var op = EditOpFactory.AddStaticMeshActor(target, kind.Path, TransformValue.FromTransform(world), project.State, stroke.Actors) with { CollisionProfile = kind.Collision };
            stroke.Ops.Add(op);
            stroke.PreviewClones.Add(new ActorClone(PreviewIdBase + (uint)stroke.PreviewClones.Count, 0, world, op.NewName, kind.Path));
        }

        stroke.Planted++;
        stroke.Index.Add(new Vector3(spot.At.X, spot.At.Y, foot), kind.Class);
        return true;
    }

    /// <summary>The foliage component drawing <paramref name="mesh"/> in the loaded landscape tile nearest to <paramref name="at"/>, or null.</summary>
    private (ActorItemViewModel Item, ComponentRecord Component)? FoliageFor(PaintStroke stroke, Vector2 at, string mesh)
    {
        LevelDocument? tile = null;
        var best = (float.MaxValue, float.MaxValue);
        foreach (var (level, (min, max)) in _tileBoxes)
        {
            var rank = (Vector2.DistanceSquared(at, Vector2.Clamp(at, min, max)), Vector2.DistanceSquared(at, (min + max) * 0.5f));
            if (rank.CompareTo(best) < 0)
            {
                (tile, best) = (level, rank);
            }
        }

        if (tile is null)
        {
            return null;
        }

        var key = tile.PackagePath + "|" + mesh;
        if (!stroke.Foliage.TryGetValue(key, out var found))
        {
            found = _pristineActors.Where(a => ReferenceEquals(a.Level, tile))
                .SelectMany(a => a.Actor.Components.Where(c => c is { IsInstanced: true, IsSynthesized: false } && string.Equals(c.StaticMeshPath, mesh, StringComparison.OrdinalIgnoreCase))
                    .Select(c => ((ActorItemViewModel Item, ComponentRecord Component)?)(a, c)))
                .FirstOrDefault();
            stroke.Foliage[key] = found;
        }

        return found;
    }

    /// <summary>Draws what the stroke planted so far (the journal's own added objects stay as they are).</summary>
    private void ShowPaintPreview(PaintStroke stroke)
    {
        if (stroke.PreviewInstances.Count > 0)
        {
            var instances = new Dictionary<InstanceKey, FTransform>(InstanceTransforms);
            foreach (var (key, world) in stroke.PreviewInstances)
            {
                instances[key] = world;
            }

            InstanceTransforms = instances;
        }

        if (stroke.PreviewClones.Count > 0)
        {
            Clones = [.. Clones.Where(c => c.Id < PreviewIdBase), .. stroke.PreviewClones];
        }
    }

    /// <summary>The palette file: object paths and the brush settings.</summary>
    private sealed record PaintPaletteFile(List<string>? Items, double Spacing = 3.5, bool RandomSize = true, bool RandomTurn = true, double SpacingMax = 0,
        bool OnObjects = false);

    /// <summary>One palette object as a stroke plants it.</summary>
    /// <param name="Path">Mesh object path or Blueprint class path.</param>
    /// <param name="IsBlueprint">True for a Blueprint.</param>
    /// <param name="Pivot">True when it stands on its pivot (plants, rocks, Blueprints), false on the bottom of its box.</param>
    /// <param name="Jitter">True when it may come a little bigger or smaller (not buildings).</param>
    /// <param name="Bounds">Its mesh's bounds, when loaded.</param>
    /// <param name="Collision">How the game's foliage of this mesh collides (for a mesh actor), or null.</param>
    /// <param name="Source">The placed Blueprint a copy is made of.</param>
    /// <param name="Class">What it is to the spacing (a tree passes over bushes).</param>
    private sealed record PaintKind(string Path, bool IsBlueprint, bool Pivot, bool Jitter, BoundingBox? Bounds, string? Collision,
        (string Level, ActorRecord Actor, IReadOnlyList<ScenePlacement> Placements)? Source, PaintClass Class);

    /// <summary>What a stroke made now depends on: the preview's stroke is taken over by a click only while this is the same.</summary>
    private sealed record PaintKey(PreparedLevelScene? Scene, PreparedLevelScene? Backdrop, Project? Project, int Edits, JournalEntry? LastEdit, int Palette,
        int Sources, double Least, double Most, double Radius, ulong Seed, bool OnObjects, bool RandomSize, bool RandomTurn);

    /// <summary>An open paint stroke: what it planted (ops and preview), what it keeps away from and what it left out.</summary>
    private sealed record PaintStroke(Project Project, PreparedLevelScene Scene, List<PaintKind> Kinds, float Spacing, float Cell,
        ulong Seed, bool RandomSize, bool RandomTurn)
    {
        /// <summary>The ground under the stroke so far (see <see cref="LoadSupports"/>).</summary>
        public SupportGrid Supports { get; } = new();

        public HashSet<(int X, int Y)> SupportBlocks { get; } = [];

        /// <summary>Where new actors go (the first loaded level that is not a landscape tile).</summary>
        public LevelDocument? Target { get; init; }

        /// <summary>The smallest distance of the range (cm); <see cref="Spacing"/> is the largest.</summary>
        public float LeastSpacing { get; init; }

        /// <summary>Plants may stand on objects' tops (<see cref="PaintOnObjects"/> when it began).</summary>
        public bool OnObjects { get; init; }

        /// <summary>What it was made for (<see cref="PaintKeyNow"/>).</summary>
        public PaintKey? Key { get; init; }

        /// <summary>The timer found the circle full once (said once a stroke).</summary>
        public bool SaidFull { get; set; }

        /// <summary>The ground of the dab being planted while no landscape is loaded at all (<see cref="PaintPlane"/>); else NaN.</summary>
        public float Plane { get; set; } = float.NaN;

        public HashSet<(int X, int Y)> Tried { get; } = [];

        public HashSet<(int X, int Y)> Gathered { get; } = [];

        /// <summary>The feet of what it planted and of the plants and things it keeps away from.</summary>
        public SpacingIndex Index { get; } = new(Spacing);

        /// <summary>The spots it left out, one per plant they would have been (for the counts).</summary>
        public SpacingIndex Skipped { get; } = new(Spacing);

        public Dictionary<PaintVerdict, int> Left { get; } = [];

        /// <summary>What is under each lattice spot looked at, its foot's height and the mesh that decided (<see cref="SiteOf"/>).</summary>
        public Dictionary<(int X, int Y), (PaintVerdict Verdict, float Foot, string? By)> Sites { get; } = [];

        /// <summary>Spots each mesh left out (a road piece, a rock, an object), by mesh (logged when the stroke ends).</summary>
        public Dictionary<string, int> Deciders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<(int X, int Y), List<(BrushOutline Outline, string Name)>> Boxes { get; } = [];

        /// <summary>Spots each actor's box kept free, by actor name (logged when the stroke ends).</summary>
        public Dictionary<string, int> Blocks { get; } = new(StringComparer.Ordinal);

        public List<EditOp> Ops { get; } = [];

        public HashSet<InstanceRef> Instances { get; } = new(InstanceRef.Comparer);

        public HashSet<ActorRef> Actors { get; } = new(ActorRef.Comparer);

        public Dictionary<InstanceKey, FTransform> PreviewInstances { get; } = [];

        public List<ActorClone> PreviewClones { get; } = [];

        public Dictionary<string, (ActorItemViewModel Item, ComponentRecord Component)?> Foliage { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Planted { get; set; }
    }
}
