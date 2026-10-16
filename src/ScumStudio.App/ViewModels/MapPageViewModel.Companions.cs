using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Delete with everything attached" (owner, 2026-10-12: "If I delete a fire place, the fire stays burning in game: the
/// flames, the heat, the smoke, the fire light and the sound, without the object. When I delete it, everything of it must
/// be deleted"; Hektor before him: "if you delete a camp, fire animations remain behind"). Every delete asks
/// <see cref="Level.Editing.Companions"/> what belongs to the object: its child and attached actors, the mesh-less fire,
/// light, smoke and sound at its spot, a deleted part's own effect components. Only structural ones go at once; anything
/// else is listed first (<see cref="Companions"/>). Whatever goes, goes in one <see cref="BatchOp"/>, so one Ctrl+Z brings
/// it all back. Shift+Delete, or the setting off, deletes the object alone.
/// </summary>
public sealed partial class MapPageViewModel
{
    // Up to this many objects in up to this many actors the search runs at once on the UI thread (a click in a cell: about
    // 6 ms in a Debug build, see MapCompanionsDeleteTests); more objects or more loaded levels go to a worker.
    private const int InlineSeeds = 8;
    internal const int InlineActors = 4_000;

    private CancellationTokenSource? _companionSearch;

    /// <summary>The list of what a delete would take along, while it waits for Delete all / Only this object; else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompanions), nameof(HighlightIds))]
    private CompanionsPreviewViewModel? _companions;

    /// <summary>True while the list of what goes with a deleted object is shown.</summary>
    public bool HasCompanions => Companions is not null;

    /// <summary>What the viewport highlights: the kind selection, plus the companions while their list is open.</summary>
    public IReadOnlyCollection<uint> HighlightIds => Companions is { HighlightIds.Count: > 0 } open ? [.. KindSelectionIds, .. open.HighlightIds] : KindSelectionIds;

    /// <summary>The search running for a large delete (all of a kind on the island), for tests to wait on; else null.</summary>
    internal Task? CompanionSearch { get; private set; }

    /// <summary>
    /// A delete as an entry point builds it: the edits that remove the objects themselves (<see cref="Ops"/>, what Shift+Delete
    /// applies), what was picked (<see cref="Seeds"/>) and the levels to search.
    /// </summary>
    private sealed record DeleteRequest(string Name, IReadOnlyList<EditOp> Ops, IReadOnlyList<DeleteSeed> Seeds, IReadOnlyList<LevelDocument> Levels)
    {
        /// <summary>The journal title when <see cref="Ops"/> are several and nothing else goes.</summary>
        public string? Title { get; init; }

        /// <summary>The entry point's own toast for the objects alone.</summary>
        public Action<EditOp>? Notify { get; init; }

        /// <summary>What the entry point refreshes after the edit.</summary>
        public Action? Refresh { get; init; }

        /// <summary>For a part: the same delete for the whole object ("Delete the whole object").</summary>
        public DeleteRequest? Whole { get; init; }
    }

    /// <summary>
    /// Deletes what <paramref name="request"/> picked and, unless <paramref name="alone"/> or the setting is off, what
    /// belongs to it: at once when only structural things go (child and attached actors), after the list otherwise.
    /// </summary>
    private async Task DeleteWithCompanionsAsync(DeleteRequest request, bool alone)
    {
        CloseCompanions();
        var state = _services.Projects.Current?.State;
        var seeds = state is null ? [] : request.Seeds.Where(s => !state.IsAdded(s.Actor)).ToList(); // a copy's things are copies the finder cannot see
        if (state is null || alone || seeds.Count == 0 || !_services.Settings.Load().Ui.DeleteWithCompanions)
        {
            Commit(request, []);
            return;
        }

        var levels = FinderLevels(request.Levels);
        var bounds = MeshBoundsSnapshot();
        CompanionReport report;
        if (SearchesInline(seeds.Count, levels))
        {
            var clock = Stopwatch.StartNew();
            try
            {
                report = Level.Editing.Companions.Find(levels, seeds, state, bounds, FinderOptions);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                report = SearchFailed(ex);
            }

            _services.Logger.LogDebug("Companions of {Count} object(s): {Found} in {Ms:0.0} ms", seeds.Count, report.Companions.Count, clock.Elapsed.TotalMilliseconds);
        }
        else
        {
            var search = new CancellationTokenSource();
            _companionSearch = search;
            LoadStatus = Loc.T("Map.Companions.Looking");
            try
            {
                // The whole island's kind can be thousands of objects in dozens of levels: a worker, and Esc or a new delete stops it.
                var snapshot = state.Clone(); // the project can take other edits while the worker reads it
                var running = Task.Run(() => Level.Editing.Companions.Find(levels, seeds, snapshot, bounds, FinderOptions, search.Token), search.Token);
                CompanionSearch = running;
                report = await running.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report = SearchFailed(ex);
            }
            finally
            {
                if (ReferenceEquals(_companionSearch, search))
                {
                    _companionSearch = null;
                    LoadStatus = string.Empty;
                }

                search.Dispose();
            }
        }

        var classEffects = request.Whole is not null && report.Notes.Any(n => n.Kind == CompanionNoteKind.ClassEffectStays); // only the whole object takes them
        if (report.Companions.All(c => c.Locked) && !classEffects)
        {
            Commit(request, report.Companions, report.Notes.Any(n => n.Kind == CompanionNoteKind.SpawnedItemsStay));
            return;
        }

        var rows = report.Companions.Select(c => new CompanionRowViewModel(c, ActorOf(c.Actor)?.SelectableId, LevelNameOf(c.Actor.Level, levels))).ToList();
        Companions = new CompanionsPreviewViewModel(request.Name, rows, classEffects, request.Seeds.Count)
        {
            OnDeleteAll = chosen => Commit(request, chosen),
            OnOnlyThis = () => Commit(request, []),
            OnWholeObject = request.Whole is { } whole ? () => _ = DeleteWithCompanionsAsync(whole, alone: false) : null,
            OnCleanSaved = OpenSavedItems,
            OnCancel = CloseCompanions,
        };
    }

    /// <summary>The delete of a whole actor (Delete on it, or on its last drawn part).</summary>
    private DeleteRequest WholeDelete(ActorItemViewModel item) =>
        new(item.Name, [new DeleteActorOp(item.Reference)], [new DeleteSeed(item.Reference)], PreparedScene?.Documents ?? [item.Level]);

    /// <summary>Applies the objects' own edits plus <paramref name="companions"/> as one journal step.</summary>
    private void Commit(DeleteRequest request, IReadOnlyList<Companion> companions, bool spawnedStay = false)
    {
        CloseCompanions();
        if (_services.Projects.Current?.State is not { } state || request.Ops.Count == 0)
        {
            return;
        }

        try
        {
            var (op, extra) = DeleteOp(request, companions, FinderLevels(request.Levels), state);
            var entry = _services.Projects.Apply(op);
            if (extra > 0)
            {
                _services.Notifications.Info(Loc.F("Map.Companions.Deleted", request.Name, extra), entry.Op.Describe() + Loc.T("Map.CtrlZUndoes"));
            }
            else if (request.Notify is { } notify)
            {
                notify(entry.Op);
            }
            else
            {
                _services.Notifications.Info(Loc.T("Map.Deleted"), entry.Op.Describe());
            }

            // An effect component with a kept part hanging under it stays (it would drop that part to the world's origin): said, never silent.
            var now = _services.Projects.Current?.State ?? state;
            var kept = companions.Where(c => c.Component is not null && !now.IsDeleted(c.Actor, c.Component)).Select(c => $"{c.Actor.Actor}.{c.Component}")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (kept.Count > 0)
            {
                _services.Notifications.Warning(Loc.T("Map.Companions.Title"), Loc.F("Map.Companions.Kept", string.Join(", ", kept)));
            }

            if (spawnedStay)
            {
                _services.Notifications.Warning(Loc.T("Map.Companions.Title"), Loc.T("Map.Companions.NoteSpawned"));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Loc.T("Map.DeleteFailed"), ex.Message);
        }

        if (request.Refresh is { } refresh)
        {
            refresh();
        }
        else
        {
            RefreshHiddenIds();
        }
    }

    /// <summary>
    /// The journal step for the objects' own edits plus <paramref name="companions"/> (<see cref="EditOpFactory.DeleteWithCompanions"/>:
    /// the actor deletes are applied to a copy first, so an effect never fails on what hangs under it; a locked child actor
    /// is left to the export) and how many companions it takes. Should that fail, the objects go alone: the delete is never lost.
    /// </summary>
    private (EditOp Op, int Extra) DeleteOp(DeleteRequest request, IReadOnlyList<Companion> companions, IReadOnlyList<LevelDocument> levels, EditState state)
    {
        var own = request.Ops.Count == 1 ? request.Ops[0] : new BatchOp(request.Title ?? request.Name, request.Ops);
        if (companions.Count == 0)
        {
            return (own, 0);
        }

        try
        {
            if (EditOpFactory.DeleteWithCompanions(levels, request.Ops, companions, state, request.Name) is not BatchOp batch || batch.Ops.Count == request.Ops.Count)
            {
                return (own, 0); // all of it already gone, or kept by what hangs under it
            }

            var things = batch.Ops.Skip(request.Ops.Count).Sum(o => o is DeleteComponentsOp d ? d.Components.Count : 1);
            return (new BatchOp(Loc.F("Map.Companions.BatchTitle", request.Name, things), batch.Ops), things);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogError(ex, "What belongs to {Name} could not be deleted with it", request.Name);
            _services.Notifications.Warning(Loc.T("Map.Companions.Title"), ex.Message);
            return (own, 0);
        }
    }

    /// <summary>What one level deletes: actor names (any case) and the export indices of deleted components of kept actors.</summary>
    internal sealed class LevelDeletions
    {
        public HashSet<string> Actors { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<int> Components { get; } = [];
    }

    internal sealed record HiddenByDeletions(LevelDeletions Deletions, IReadOnlyList<ActorRef> Hidden);

    // Documents never change: what one level's deletions hide is kept until those deletions change (one level per edit).
    private readonly ConditionalWeakTable<LevelDocument, HiddenByDeletions> _dependents = new();

    /// <summary>
    /// The actors the game loses with <paramref name="deletions"/> (<see cref="Attachments.Dependents"/>, the export's rule):
    /// stored child actors, and attached actors that draw nothing. A level whose deletions are the same as at the last call
    /// is answered from <paramref name="cache"/>.
    /// </summary>
    internal static IEnumerable<ActorRef> HiddenWith(IReadOnlyDictionary<LevelDocument, LevelDeletions> deletions, ConditionalWeakTable<LevelDocument, HiddenByDeletions> cache)
    {
        foreach (var (level, gone) in deletions)
        {
            if (!cache.TryGetValue(level, out var known) || !known.Deletions.Actors.SetEquals(gone.Actors) || !known.Deletions.Components.SetEquals(gone.Components))
            {
                var hidden = Attachments.Dependents(level, gone.Actors, gone.Components)
                    .Where(d => d.Kind == DependentKind.ChildActor || !d.HasDrawnMesh)
                    .Select(d => new ActorRef(level.PackagePath, d.Actor.Name))
                    .ToList();
                cache.AddOrUpdate(level, known = new HiddenByDeletions(gone, hidden));
            }

            foreach (var actor in known.Hidden)
            {
                yield return actor;
            }
        }
    }

    /// <summary>A search that failed costs only the companions: logged and shown, and the objects go alone.</summary>
    private CompanionReport SearchFailed(Exception ex)
    {
        _services.Logger.LogError(ex, "Looking for what belongs to the deleted objects failed");
        _services.Notifications.Warning(Loc.T("Map.Companions.Title"), ex.Message);
        return CompanionReport.Empty;
    }

    /// <summary>True when the search for <paramref name="seeds"/> objects in <paramref name="levels"/> is quick enough for the UI thread.</summary>
    internal static bool SearchesInline(int seeds, IReadOnlyList<LevelDocument> levels) =>
        seeds <= InlineSeeds && levels.Sum(l => l.Actors.Count) <= InlineActors;

    /// <summary>Closes the list and stops a search still running.</summary>
    private void CloseCompanions()
    {
        _companionSearch?.Cancel();
        Companions = null;
    }

    /// <summary>The levels searched: <paramref name="levels"/>, with all of WaterSplines in place of the part of it shown.</summary>
    private List<LevelDocument> FinderLevels(IReadOnlyList<LevelDocument> levels)
    {
        var water = _waterDocument;
        var result = levels.Where(l => water is null || !string.Equals(l.PackagePath, water.PackagePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (water is not null)
        {
            result.Add(water);
        }

        return result;
    }

    /// <summary><see cref="MeshBounds"/> over the meshes loaded now, safe on a worker (the scene and the extra list are replaced, never changed).</summary>
    private Func<string, BoundingBox?> MeshBoundsSnapshot()
    {
        var meshes = PreparedScene?.Meshes;
        var extra = new Dictionary<string, BoundingBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ExtraMeshes)
        {
            extra.TryAdd(m.Asset.MeshPath, m.Asset.Mesh.Bounds);
        }

        return path => meshes?.TryGetValue(path, out var asset) == true ? asset.Mesh.Bounds : extra.TryGetValue(path, out var box) ? box : null;
    }

    // Volumes are offered (never ticked) only when their shape's bounds were read.
    private static readonly CompanionOptions FinderOptions = new() { VolumeBounds = VolumeBounds };

    /// <summary>World box of a volume from its root's shape or brush bounds (<see cref="ComponentRecord.LocalBounds"/>), or null.</summary>
    private static BoundingBox? VolumeBounds(ActorRecord actor)
    {
        if (actor.Root is not { LocalBounds: { IsEmpty: false } local } root)
        {
            return null;
        }

        var box = BoundingBox.Empty;
        for (var corner = 0; corner < 8; corner++)
        {
            var p = new FVector((corner & 1) == 0 ? local.Min.X : local.Max.X, (corner & 2) == 0 ? local.Min.Y : local.Max.Y, (corner & 4) == 0 ? local.Min.Z : local.Max.Z);
            box = box.Include(root.WorldTransform.TransformPosition(p).ToVector3());
        }

        return box;
    }

    private static string LevelNameOf(string packagePath, IReadOnlyList<LevelDocument> levels) =>
        levels.FirstOrDefault(l => string.Equals(l.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase))?.Name ?? packagePath[(packagePath.LastIndexOf('/') + 1)..];
}

/// <summary>The list of what goes with a deleted object (see <see cref="MapPageViewModel.Companions"/>).</summary>
public sealed partial class CompanionsPreviewViewModel : ObservableObject
{
    private readonly int _objects;

    internal CompanionsPreviewViewModel(string objectName, IReadOnlyList<CompanionRowViewModel> rows, bool hasClassEffects, int objects)
    {
        ObjectName = objectName;
        Rows = rows;
        HasClassEffects = hasClassEffects;
        _objects = objects;
        foreach (var row in rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CompanionRowViewModel.IsChecked))
                {
                    OnPropertyChanged(nameof(DeleteAllText));
                }
            };
        }
    }

    /// <summary>What is deleted: the object's name, or "N objects".</summary>
    public string ObjectName { get; }

    /// <summary>One row per companion: ticked rows go with the object.</summary>
    public IReadOnlyList<CompanionRowViewModel> Rows { get; }

    /// <summary>True when an effect comes from the class and goes only with the whole object.</summary>
    public bool HasClassEffects { get; }

    /// <summary>True when a row belongs to something kept (greyed, unticked).</summary>
    public bool HasShared => Rows.Any(r => r.Companion.BelongsTo is not null);

    /// <summary>"Delete all (N)": the objects plus the ticked rows.</summary>
    public string DeleteAllText => Loc.F("Map.Companions.DeleteAll", (_objects + Checked.Count).ToString(CultureInfo.CurrentCulture));

    /// <summary>The ticked companions.</summary>
    public IReadOnlyList<Companion> Checked => Rows.Where(r => r.IsChecked).Select(r => r.Companion).ToList();

    /// <summary>The loaded companions the viewport highlights while the list is open.</summary>
    public IReadOnlyCollection<uint> HighlightIds => Rows.Where(r => r.SelectableId is not null).Select(r => r.SelectableId!.Value).ToHashSet();

    internal Action<IReadOnlyList<Companion>>? OnDeleteAll { get; init; }

    internal Action? OnOnlyThis { get; init; }

    internal Action? OnWholeObject { get; init; }

    internal Action? OnCancel { get; init; }

    internal Action? OnCleanSaved { get; init; }

    /// <summary>Deletes the object and every ticked row (Enter).</summary>
    [RelayCommand]
    private void DeleteAll() => OnDeleteAll?.Invoke(Checked);

    /// <summary>Deletes the object alone.</summary>
    [RelayCommand]
    private void OnlyThis() => OnOnlyThis?.Invoke();

    /// <summary>Deletes the whole object the part belongs to (its class's effects go with it).</summary>
    [RelayCommand]
    private void WholeObject() => OnWholeObject?.Invoke();

    /// <summary>Opens Clean saved items: what an item spawner already spawned is in the save, not in the mod (D02).</summary>
    [RelayCommand]
    private void CleanSaved() => OnCleanSaved?.Invoke();

    /// <summary>Deletes nothing (Esc).</summary>
    [RelayCommand]
    private void Cancel() => OnCancel?.Invoke();
}

/// <summary>One thing that goes with a deleted object: what it is, why, how far, in which level.</summary>
public sealed partial class CompanionRowViewModel : ObservableObject
{
    internal CompanionRowViewModel(Companion companion, uint? selectableId, string levelName)
    {
        Companion = companion;
        SelectableId = selectableId;
        LevelName = levelName;
        _isChecked = companion.Locked || (companion.DefaultOn && companion.BelongsTo is null);
    }

    /// <summary>What the finder found.</summary>
    public Companion Companion { get; }

    /// <summary>The loaded actor's id (highlighted), or null outside the loaded levels.</summary>
    internal uint? SelectableId { get; }

    /// <summary>Actor name, with the component for an effect of a kept actor.</summary>
    public string Name => Companion.Component is { } c ? $"{Companion.Actor.Actor}.{c}" : Companion.Actor.Actor;

    /// <summary>Short class name.</summary>
    public string ClassName => Companion.ClassName;

    /// <summary>Why it goes, or what it belongs to.</summary>
    public string RuleText => Companion.BelongsTo is { } owner ? Loc.F("Map.Companions.BelongsTo", owner.Actor) : Loc.T(Companion.Rule switch
    {
        CompanionRule.ChildActor => "Map.Companions.Rule.ChildActor",
        CompanionRule.Attached => "Map.Companions.Rule.Attached",
        CompanionRule.OwnEffect => "Map.Companions.Rule.OwnEffect",
        CompanionRule.Decal => "Map.Companions.Rule.Decal",
        CompanionRule.WaterTwin => "Map.Companions.Rule.WaterTwin",
        CompanionRule.Volume => "Map.Companions.Rule.Volume",
        _ => "Map.Companions.Rule.AtTheSpot",
    });

    /// <summary>Distance from the deleted object in metres.</summary>
    public string DistanceText => Loc.F("Map.Companions.Distance", (Companion.DistanceCm / 100f).ToString("0.0", CultureInfo.CurrentCulture));

    /// <summary>The level it is in.</summary>
    public string LevelName { get; }

    /// <summary>The kind icon.</summary>
    public string IconKey => Companion.Rule switch
    {
        CompanionRule.ChildActor or CompanionRule.Attached => "Icon.Blueprint",
        CompanionRule.Decal => "Icon.Image",
        CompanionRule.WaterTwin => "Icon.Layers",
        CompanionRule.Volume => "Icon.Cube3d",
        _ when ClassName.Contains("Sound", StringComparison.Ordinal) || ClassName.StartsWith("Ak", StringComparison.Ordinal)
               || ClassName.Contains("Audio", StringComparison.Ordinal) => "Icon.Sound",
        _ => "Icon.Sun",
    };

    /// <summary>A locked row always goes; the others can be unticked (or ticked, for one that belongs to something kept).</summary>
    public bool CanToggle => !Companion.Locked;

    /// <summary>Greyed: it belongs to something kept and stays.</summary>
    public bool IsGreyed => Companion.BelongsTo is not null && !IsChecked;

    /// <summary>Ticked: it goes with the object.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGreyed))]
    private bool _isChecked;
}
