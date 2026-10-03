using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Core.Settings;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Streaming: in whole-island mode the levels around the camera load as it flies (every POI, TV base and landscape tile
/// whose bounds come within the quality's radius), not one sector at a time. Levels read, meshes loaded, spline pieces
/// bent and tiles baked are kept (<see cref="LevelPrepareCache"/>, plus the documents here), so moving on only reads
/// what is new; the viewport keeps the uploaded meshes too (<see cref="GpuMeshCache"/>).
/// </summary>
public sealed partial class MapPageViewModel
{
    private readonly LevelPrepareCache _prepareCache = new();
    private readonly Dictionary<string, (LevelDocument Document, int Used)> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _readGate = new();
    private Cue4ParseLevelReader? _reader;
    private AssetCatalog? _readerCatalog;
    private LevelData? _waterData;
    private int _readGeneration;
    private IReadOnlyList<string> _streamed = [];

    /// <summary>3D view quality (saved in the settings): streaming radius, object distance, LOD detail, texture size.</summary>
    [ObservableProperty]
    private RenderQuality _renderQuality = RenderQuality.Balanced;

    /// <summary>The presets for the quality picker.</summary>
    public IReadOnlyList<RenderQuality> RenderQualities { get; } = Enum.GetValues<RenderQuality>();

    /// <summary>The profile of <see cref="RenderQuality"/>.</summary>
    public RenderQualityProfile Quality => RenderQualityProfile.For(RenderQuality);

    partial void OnRenderQualityChanged(RenderQuality value)
    {
        OnPropertyChanged(nameof(Quality));
        if (_services.Settings.Load().Ui.RenderQuality != value)
        {
            _services.UpdateSettings(s => s with { Ui = s.Ui with { RenderQuality = value } });
        }
    }

    /// <summary>Follows a quality chosen on the Settings page.</summary>
    private void OnSettingsChangedQuality(object? sender, EventArgs e) =>
        _services.Dispatcher.Invoke(() => RenderQuality = _services.Settings.Load().Ui.RenderQuality);

    /// <summary>
    /// The map sublevels (POI, TV base, misc, landscape tile; not Pripyat or island-wide data) whose tile bounds come within
    /// <paramref name="radius"/> (cm, 3D, so flying high loads nothing) of <paramref name="camera"/>; non-landscape first.
    /// A level the project built on far outside its own bounds also counts by where those edits are
    /// (<paramref name="edits"/>: level → box around them), so a long new bridge never unloads under the camera.
    /// </summary>
    public static List<string> LevelsAround(WorldIndex world, FVector camera, float radius, IReadOnlyDictionary<string, (FVector Min, FVector Max)>? edits = null) =>
        world.Packages
            .Where(p => p.IsMap && p.Cell is not null && p.Kind is WorldPackageKind.Poi or WorldPackageKind.Landscape or WorldPackageKind.TvBase or WorldPackageKind.Misc
                        && p.Tile is { BoundsValid: true } t
                        && (DistanceSquared(t.BoundsMin, t.BoundsMax, camera) <= radius * radius
                            || (edits?.TryGetValue(p.PackagePath, out var e) == true && DistanceSquared(e.Min, e.Max, camera) <= radius * radius)))
            .OrderBy(p => p.Kind == WorldPackageKind.Landscape)
            .ThenBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.PackagePath)
            .ToList();

    /// <summary>
    /// Per level, the box around the places of the actors the project added there or moved (world positions from the
    /// journal). A place under 500 m from the world origin is taken as relative to a parent and left out.
    /// </summary>
    internal static Dictionary<string, (FVector Min, FVector Max)> EditedBounds(EditState state)
    {
        var boxes = new Dictionary<string, (FVector Min, FVector Max)>(StringComparer.OrdinalIgnoreCase);
        void Add(ActorRef actor, TransformValue? value)
        {
            // ponytail: a place relative to a parent (a bridge's fence copy) cannot be told apart without the level; such
            // copies stay near their parent, inside the level's own bounds.
            if (value is not { } v || v.Location.Size2D() < 50_000f)
            {
                return;
            }

            var p = v.Location;
            boxes[actor.Level] = boxes.TryGetValue(actor.Level, out var box) ? (FVector.Min(box.Min, p), FVector.Max(box.Max, p)) : (p, p);
        }

        foreach (var (actor, op) in state.AddedActors)
        {
            Add(actor, state.GetAddedTransform(actor) ?? op switch
            {
                AddStaticMeshActorOp mesh => mesh.Transform,
                DuplicateActorOp copy => copy.Transform,
                AddBlueprintActorOp blueprint => blueprint.Transform,
                _ => null,
            });
        }

        foreach (var (actor, component, value) in state.TransformOverrides)
        {
            if (component.Length == 0)
            {
                Add(actor, value);
            }
        }

        return boxes;
    }

    private static float DistanceSquared(FVector min, FVector max, FVector p)
    {
        var dx = MathF.Max(0f, MathF.Max(min.X - p.X, p.X - max.X));
        var dy = MathF.Max(0f, MathF.Max(min.Y - p.Y, p.Y - max.Y));
        var dz = MathF.Max(0f, MathF.Max(min.Z - p.Z, p.Z - max.Z));
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>
    /// The levels to show for a camera at <paramref name="camera"/>: everything within the radius, plus what is already
    /// shown and still within 1.5 times it (so hovering at the edge does not load and unload the same level).
    /// </summary>
    internal static List<string> StreamingSet(WorldIndex world, FVector camera, float radius, IReadOnlyCollection<string> shown,
        IReadOnlyDictionary<string, (FVector Min, FVector Max)>? edits = null)
    {
        var keep = LevelsAround(world, camera, radius * 1.5f, edits);
        var near = LevelsAround(world, camera, radius, edits).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return keep.Where(p => near.Contains(p) || shown.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Whole-island mode: loads the levels around the camera when one comes within the radius that is not shown yet
    /// (called about twice a second). Levels that fell behind are dropped with that next load, not on their own: every load
    /// re-uploads the scene, and dropping alone made a second hitch right after the first (owner: "entered a sector, lag;
    /// left it, lag again").
    /// </summary>
    private void StreamAround(WorldIndex world, FVector camera)
    {
        var edits = _services.Projects.Current?.State is { } state ? EditedBounds(state) : null;
        var wanted = StreamingSet(world, camera, Quality.StreamRadiusCm, _streamed, edits);
        if (wanted.Count == 0 || wanted.All(p => _streamed.Contains(p, StringComparer.OrdinalIgnoreCase)) || IsLoadingLevel)
        {
            return; // nothing near (over the sea, high up), nothing new, or the previous set is still loading
        }

        var added = wanted.Count(p => !_streamed.Contains(p, StringComparer.OrdinalIgnoreCase));
        _streamed = wanted;
        _loadedCell = CellAt(world, camera.X, camera.Y);
        WorldStatus = Localization.Loc.F("Map.World.Streaming", added);
        LevelLoadCompletion = LoadLevelsAsync(wanted, landscapeStep: 4, seaPlane: false)
            .ContinueWith(_ => WorldStatus = Localization.Loc.F("Map.World.Around", wanted.Count), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// The documents of <paramref name="packagePaths"/> (read once, reused while recent) plus the rivers and lakes over
    /// their landscape tiles. Worker thread; one read at a time.
    /// </summary>
    private List<LevelDocument> ReadDocuments(AssetCatalog catalog, WorldIndex? world, IReadOnlyList<string> packagePaths, CancellationToken ct)
    {
        lock (_readGate)
        {
            if (!ReferenceEquals(_readerCatalog, catalog))
            {
                _reader = new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions(), _services.Logger);
                _readerCatalog = catalog;
                _documents.Clear();
                _waterData = null;
            }

            _readGeneration++;
            var documents = new List<LevelDocument>(packagePaths.Count + 1);
            foreach (var path in packagePaths)
            {
                ct.ThrowIfCancellationRequested();
                if (!_documents.TryGetValue(path, out var entry))
                {
                    entry = (LevelDocument.Load(_reader!, path, ct), 0);
                }

                _documents[path] = entry with { Used = _readGeneration };
                documents.Add(entry.Document);
            }

            if (world?.Find("WaterSplines") is { IsMap: true } water && !packagePaths.Contains(water.PackagePath, StringComparer.OrdinalIgnoreCase))
            {
                _waterData ??= _reader!.ReadLevel(water.PackagePath, ct);
                if (WaterOver(_waterData, world, packagePaths) is { } rivers)
                {
                    documents.Add(rivers);
                }
            }

            foreach (var old in _documents.Where(d => d.Value.Used < _readGeneration - LevelPrepareCache.KeepGenerations).Select(d => d.Key).ToList())
            {
                _documents.Remove(old);
            }

            return documents;
        }
    }
}
