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
/// objects inside the circle along the stroke, no two closer than the spacing and none on an existing object, each on
/// the ground or surface under it (<see cref="SupportAt"/>), turned at random and a little bigger or smaller. A tree,
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

    private static readonly JsonSerializerOptions PaletteJson = new() { WriteIndented = true };

    private ObjectPicker? _paintPicker;
    private IReadOnlyList<ReplaceCandidate>? _paintables;
    private PaintStroke? _stroke;
    private Project? _paletteProject;
    private bool _loadingPalette;
    private PreparedLevelScene? _paintScene;
    private Dictionary<string, string?> _foliageCollision = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<LevelDocument, (Vector2 Min, Vector2 Max)> _tileBoxes = [];
    private readonly Dictionary<string, Task<(string Level, ActorRecord Actor, IReadOnlyList<ScenePlacement> Placements)?>> _paintSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Brush mode: false selects what the circle covers, true plants the palette (<see cref="PaintAt"/>).</summary>
    [ObservableProperty]
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

    partial void OnBrushPaintChanged(bool value)
    {
        EndPaintStroke();
        if (value && PreparedScene is { } scene)
        {
            // The brush's map of the levels is made now, not at the first stroke (~80 ms with a farm and its tiles).
            _ = BrushIndexNow();
            PreparePaintScene(scene);
        }
    }

    partial void OnBrushSelectChanged(bool value) => EndPaintStroke();

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
            var data = new PaintPaletteFile([.. PaintPalette.Select(p => p.Choice.ObjectPath)], PaintSpacing, PaintRandomSize, PaintRandomTurn, PaintSpacingMax);
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
        if (_stroke is { } open && !ReferenceEquals(open.Scene, PreparedScene))
        {
            EndPaintStroke(); // the loaded levels changed under the stroke
        }

        var stroke = _stroke ??= BeginPaintStroke(centre);
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
            var (t0, t1) = (i / (float)pieces, (i + 1) / (float)pieces);
            PaintPiece(stroke, Vector2.Lerp(a, b, t0), Vector2.Lerp(a, b, t1), start.Z + ((centre.Z - start.Z) * t0), start.Z + ((centre.Z - start.Z) * t1), radius);
        }

        if (stroke.Planted > before)
        {
            ShowPaintPreview(stroke);
        }
    }

    /// <summary>
    /// One tick of the timer mode: one object at a random free spot inside the circle at <paramref name="centre"/> (the
    /// spacing still applies; nothing when the circle is full). It joins the open stroke, journaled by <see cref="EndPaintStroke"/>.
    /// </summary>
    public void PaintOneAt(FVector centre)
    {
        if (_stroke is { } open && !ReferenceEquals(open.Scene, PreparedScene))
        {
            EndPaintStroke(); // the loaded levels changed under the stroke
        }

        var stroke = _stroke ??= BeginPaintStroke(centre);
        if (stroke.Kinds.Count == 0)
        {
            return;
        }

        var radius = (float)BrushRadius * 100f;
        var c = new Vector2(centre.X, centre.Y);
        GatherPaintObstacles(stroke, c - new Vector2(radius + stroke.Spacing), c + new Vector2(radius + stroke.Spacing));
        LoadSupports(stroke, c - new Vector2(radius), c + new Vector2(radius));
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var h = Mix(stroke.Seed ^ ((ulong)++stroke.Ticks * 0x9E3779B97F4A7C15UL));
            var at = c + (new Vector2(MathF.Cos(Unit(h, 0) * MathF.Tau), MathF.Sin(Unit(h, 0) * MathF.Tau)) * (radius * MathF.Sqrt(Unit(h, 1))));
            var keep = stroke.LeastSpacing + ((stroke.Spacing - stroke.LeastSpacing) * Unit(h, 7));
            if (!TooClose(stroke, at, centre.Z, keep))
            {
                Plant(stroke, at, centre.Z, h);
                ShowPaintPreview(stroke);
                return;
            }
        }
    }

    /// <summary>Journals the open stroke as one edit ("Planted N objects"); nothing when it planted nothing.</summary>
    public void EndPaintStroke()
    {
        if (_stroke is not { } stroke)
        {
            return;
        }

        _stroke = null;
        if (stroke.Ops.Count == 0)
        {
            return;
        }

        if (ReferenceEquals(_services.Projects.Current, stroke.Project))
        {
            var title = Loc.F("Map.Paint.Done", stroke.Ops.Count);
            try
            {
                // The journal change refreshes the edits: its objects replace the stroke's preview.
                var entry = _services.Projects.Apply(new BatchOp(title, stroke.Ops));
                _services.Notifications.Info(title, entry.Op.Describe() + Loc.T("Map.CtrlZUndoes"));
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                _services.Notifications.Error(Loc.T("Map.Paint"), ex.Message);
            }
        }

        RefreshEdits(); // nothing journaled: the preview goes
    }

    private PaintStroke BeginPaintStroke(FVector at)
    {
        var scene = PreparedScene;
        var project = _services.Projects.Current;
        var kinds = new List<PaintKind>();
        if (scene is not { Documents.Count: > 0 })
        {
            _services.Notifications.Warning(Loc.T("Map.NoLevelLoaded"), Loc.T("Map.NoLevelLoadedDetail"));
        }
        else if (project is null)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.NoProject.Adds"));
        }
        else if (PaintPalette.Count == 0)
        {
            _services.Notifications.Info(Loc.T("Map.Paint"), Loc.T("Map.Paint.Empty"));
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
                        kinds.Add(new PaintKind(choice.ObjectPath, true, true, false, null, null, source));
                    }

                    continue; // not found yet: the next stroke has it
                }

                var mesh = choice.ObjectPath;
                EnsureMeshLoaded(mesh);
                kinds.Add(new PaintKind(mesh, false, IsPlant(mesh) || mesh.Contains("/Rocks/", StringComparison.OrdinalIgnoreCase),
                    !mesh.Contains("/Buildings/", StringComparison.OrdinalIgnoreCase), MeshBounds(mesh), _foliageCollision.GetValueOrDefault(mesh), null));
            }
        }

        var least = (float)Math.Clamp(PaintSpacing, 1, 20) * 100f;
        var spacing = MathF.Max(least, (float)Math.Clamp(PaintSpacingMax, 1, 20) * 100f); // the index cells hold the largest distance
        var radius = (float)BrushRadius * 100f;
        var seed = Mix(((ulong)BitConverter.SingleToUInt32Bits(at.X) << 32) | BitConverter.SingleToUInt32Bits(at.Y));
        return new PaintStroke(project!, scene!, kinds, spacing, MathF.Max(least / 2f, 2f * radius / 150f), MathF.Max(radius, 1000f), seed,
            PaintRandomSize, PaintRandomTurn)
        {
            LeastSpacing = least,
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

    /// <summary>Plants along one piece of the stroke (a capsule from <paramref name="a"/> to <paramref name="b"/>).</summary>
    private void PaintPiece(PaintStroke stroke, Vector2 a, Vector2 b, float za, float zb, float radius)
    {
        GatherPaintObstacles(stroke, Vector2.Min(a, b) - new Vector2(radius + stroke.Spacing), Vector2.Max(a, b) + new Vector2(radius + stroke.Spacing));
        LoadSupports(stroke, Vector2.Min(a, b) - new Vector2(radius), Vector2.Max(a, b) + new Vector2(radius));

        // The stroke's lattice: one candidate place per cell, fixed by the stroke's seed, each tried once (when the circle
        // first covers it); the new ones are tried in a random order so no row or column pattern shows.
        var cell = stroke.Cell;
        var (min, max) = (Vector2.Min(a, b) - new Vector2(radius), Vector2.Max(a, b) + new Vector2(radius));
        var fresh = new List<(ulong Hash, Vector2 At, float Z)>();
        for (var y = (int)MathF.Floor(min.Y / cell); y <= (int)MathF.Floor(max.Y / cell); y++)
        {
            for (var x = (int)MathF.Floor(min.X / cell); x <= (int)MathF.Floor(max.X / cell); x++)
            {
                if (stroke.Tried.Contains((x, y)))
                {
                    continue;
                }

                var h = Mix(stroke.Seed ^ Mix(((ulong)(uint)x << 32) | (uint)y));
                var at = new Vector2((x + Unit(h, 0)) * cell, (y + Unit(h, 1)) * cell);
                var ab = b - a;
                var t = ab.LengthSquared() < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(at - a, ab) / ab.LengthSquared(), 0f, 1f);
                if (Vector2.DistanceSquared(at, a + (ab * t)) > radius * radius)
                {
                    continue; // not under the circle yet
                }

                stroke.Tried.Add((x, y));
                fresh.Add((h, at, za + ((zb - za) * t)));
            }
        }

        fresh.Sort((p, q) => p.Hash.CompareTo(q.Hash));
        foreach (var (h, at, z) in fresh)
        {
            // Each place keeps its own distance in the range, so the gaps vary like a grown forest's.
            var keep = stroke.LeastSpacing + ((stroke.Spacing - stroke.LeastSpacing) * Unit(h, 7));
            if (!TooClose(stroke, at, z, keep))
            {
                Plant(stroke, at, z, h);
            }
        }
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

    /// <summary>True when a planted or existing object is closer to <paramref name="at"/> than <paramref name="keep"/> (cm).</summary>
    private static bool TooClose(PaintStroke stroke, Vector2 at, float z, float keep)
    {
        var (cx, cy) = PaintCellOf(at, stroke.Spacing); // cells as large as the largest spacing: the 3x3 around holds every neighbour
        var s = keep;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (stroke.Points.TryGetValue((cx + dx, cy + dy), out var points))
                {
                    foreach (var p in points)
                    {
                        if (Vector2.DistanceSquared(new Vector2(p.X, p.Y), at) < s * s && MathF.Abs(p.Z - z) < stroke.Height)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        if (stroke.Boxes.TryGetValue((cx, cy), out var boxes))
        {
            var probe = new BrushSweep(at, at, s, z - stroke.Height, z + stroke.Height);
            foreach (var outline in boxes)
            {
                if (probe.Touches(outline))
                {
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

        if (e is { Kind: BrushKind.Instance, Instance: { StaticMeshPath: { } mesh } instance } && MeshBounds(mesh) is { IsEmpty: false } bounds)
        {
            var scale = instance.WorldTransform.Scale3D;
            var across = MathF.Max(bounds.Size.X * MathF.Abs(scale.X), bounds.Size.Y * MathF.Abs(scale.Y));
            if (across < (IsPlant(mesh) ? 100f : 50f))
            {
                return; // grass, flowers, pebbles
            }

            if (!IsPlant(mesh))
            {
                outline = BrushOutline.Of(instance.WorldTransform, bounds); // a rock by what it covers, a tree by its trunk
            }
        }

        if (outline.Shapes is null)
        {
            var at = outline.Box.Min;
            AddPoint(stroke, new Vector3(at.X, at.Y, at.Z));
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
                (stroke.Boxes.TryGetValue((x, y), out var list) ? list : stroke.Boxes[(x, y)] = []).Add(outline);
            }
        }
    }

    private static void AddPoint(PaintStroke stroke, Vector3 at)
    {
        var key = PaintCellOf(new Vector2(at.X, at.Y), stroke.Spacing);
        (stroke.Points.TryGetValue(key, out var list) ? list : stroke.Points[key] = []).Add(at);
    }

    private static (int X, int Y) PaintCellOf(Vector2 at, float size) => ((int)MathF.Floor(at.X / size), (int)MathF.Floor(at.Y / size));

    /// <summary>Plants one palette object at <paramref name="at"/>: on the ground under it, turned and sized by its hash.</summary>
    private void Plant(PaintStroke stroke, Vector2 at, float z, ulong h)
    {
        var kind = stroke.Kinds[(int)(Mix(h + 2) % (ulong)stroke.Kinds.Count)];
        var yaw = stroke.RandomTurn ? Unit(h, 3) * 360f : 0f;
        var size = stroke.RandomSize && kind.Jitter ? 0.9f + (Unit(h, 4) * 0.25f) : 1f;
        var world = new FTransform(new FRotator(0f, yaw, 0f), new FVector(at.X, at.Y, GroundAt(at.X, at.Y) ?? z), new FVector(size));
        world = Planted(world, kind.Bounds, kind.Pivot, stroke.Supports) ?? world;
        var project = stroke.Project;
        if (!kind.IsBlueprint && FoliageFor(stroke, at, kind.Path) is { } holder)
        {
            // A new tree of the tile's own foliage: chopped and collided like the stock ones.
            var (item, component) = holder;
            var op = EditOpFactory.AddInstance(item.Level, item.Actor, component.Name, TransformValue.FromTransform(world.GetRelativeTransform(component.WorldTransform)), project.State, stroke.Instances);
            stroke.Ops.Add(op);
            stroke.PreviewInstances[InstanceKey.Of(item.SelectableId, op.Target.Component, op.Target.Index)] = world;
        }
        else if (stroke.Target is not { } target)
        {
            return;
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
        AddPoint(stroke, new Vector3(world.Translation.X, world.Translation.Y, world.Translation.Z));
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

    private static ulong Mix(ulong x)
    {
        // splitmix64
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>A number in [0, 1) from <paramref name="h"/> (a different one per <paramref name="i"/>).</summary>
    private static float Unit(ulong h, int i) => (Mix(h + (ulong)i) >> 40) / (float)(1 << 24);

    /// <summary>The palette file: object paths and the brush settings.</summary>
    private sealed record PaintPaletteFile(List<string>? Items, double Spacing = 3.5, bool RandomSize = true, bool RandomTurn = true, double SpacingMax = 0);

    /// <summary>One palette object as a stroke plants it.</summary>
    /// <param name="Path">Mesh object path or Blueprint class path.</param>
    /// <param name="IsBlueprint">True for a Blueprint.</param>
    /// <param name="Pivot">True when it stands on its pivot (plants, rocks, Blueprints), false on the bottom of its box.</param>
    /// <param name="Jitter">True when it may come a little bigger or smaller (not buildings).</param>
    /// <param name="Bounds">Its mesh's bounds, when loaded.</param>
    /// <param name="Collision">How the game's foliage of this mesh collides (for a mesh actor), or null.</param>
    /// <param name="Source">The placed Blueprint a copy is made of.</param>
    private sealed record PaintKind(string Path, bool IsBlueprint, bool Pivot, bool Jitter, BoundingBox? Bounds, string? Collision,
        (string Level, ActorRecord Actor, IReadOnlyList<ScenePlacement> Placements)? Source);

    /// <summary>An open paint stroke: what it planted (ops and preview) and what it keeps away from.</summary>
    private sealed record PaintStroke(Project Project, PreparedLevelScene Scene, List<PaintKind> Kinds, float Spacing, float Cell, float Height,
        ulong Seed, bool RandomSize, bool RandomTurn)
    {
        /// <summary>The ground under the stroke so far (see <see cref="LoadSupports"/>).</summary>
        public SupportGrid Supports { get; } = new();

        public HashSet<(int X, int Y)> SupportBlocks { get; } = [];

        /// <summary>Where new actors go (the first loaded level that is not a landscape tile).</summary>
        public LevelDocument? Target { get; init; }

        /// <summary>The smallest distance of the range (cm); <see cref="Spacing"/> is the largest.</summary>
        public float LeastSpacing { get; init; }

        /// <summary>Timer ticks so far (each draws new random spots).</summary>
        public long Ticks { get; set; }

        public HashSet<(int X, int Y)> Tried { get; } = [];

        public HashSet<(int X, int Y)> Gathered { get; } = [];

        public Dictionary<(int X, int Y), List<Vector3>> Points { get; } = [];

        public Dictionary<(int X, int Y), List<BrushOutline>> Boxes { get; } = [];

        public List<EditOp> Ops { get; } = [];

        public HashSet<InstanceRef> Instances { get; } = new(InstanceRef.Comparer);

        public HashSet<ActorRef> Actors { get; } = new(ActorRef.Comparer);

        public Dictionary<InstanceKey, FTransform> PreviewInstances { get; } = [];

        public List<ActorClone> PreviewClones { get; } = [];

        public Dictionary<string, (ActorItemViewModel Item, ComponentRecord Component)?> Foliage { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Planted { get; set; }
    }
}
