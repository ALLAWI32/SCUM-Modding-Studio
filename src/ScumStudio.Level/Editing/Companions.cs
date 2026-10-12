using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Editing;

/// <summary>Something the user deletes: a whole actor, or one part (a stored mesh component) of an actor that stays.</summary>
/// <param name="Actor">The actor.</param>
/// <param name="Part">The part's component name, or null for the whole actor.</param>
public sealed record DeleteSeed(ActorRef Actor, string? Part = null);

/// <summary>Why a <see cref="Companion"/> goes with what is deleted.</summary>
public enum CompanionRule
{
    /// <summary>A child actor the level stores for one of the deleted actor's <c>ChildActorComponent</c>s.</summary>
    ChildActor,

    /// <summary>An actor attached (<c>AttachParent</c>) to the deleted actor, part or effect.</summary>
    Attached,

    /// <summary>A mesh-less light, particle, Niagara, sound, fire or smoke actor standing at the deleted thing.</summary>
    AtTheSpot,

    /// <summary>An effect component of the same actor that belongs to the deleted part (the fire of a house's fireplace).</summary>
    OwnEffect,

    /// <summary>A decal at the deleted thing (ash, soot, scorch marks).</summary>
    Decal,

    /// <summary>The other side of a water surface (the lake top and its <c>_FN</c> underside).</summary>
    WaterTwin,

    /// <summary>A volume overlapping the deleted thing; never on by default.</summary>
    Volume,
}

/// <summary>An actor (or one component of a kept actor) that belongs to something being deleted.</summary>
/// <param name="Actor">The actor.</param>
/// <param name="Component">The component of a kept actor that goes, or null for the whole actor.</param>
/// <param name="ClassName">Short class name of the actor or component.</param>
/// <param name="Rule">Why it goes.</param>
/// <param name="DistanceCm">Distance from its place to the deleted thing's bounds (0 inside).</param>
/// <param name="Locked">True when it cannot stay: structural, it would be left dangling.</param>
/// <param name="DefaultOn">True when it goes unless the user unticks it.</param>
public sealed record Companion(ActorRef Actor, string? Component, string ClassName, CompanionRule Rule, float DistanceCm, bool Locked, bool DefaultOn)
{
    /// <summary>The deleted actor it belongs to.</summary>
    public required ActorRef Seed { get; init; }

    /// <summary>A kept object nearer to it than the deleted thing: it is that object's, and stays unless the user ticks it.</summary>
    public ActorRef? BelongsTo { get; init; }

    /// <summary>An attached actor that draws a mesh of its own: unticked, it stays where it stands (detached in place).</summary>
    public bool HasOwnMesh { get; init; }
}

/// <summary>What a <see cref="CompanionNote"/> warns about.</summary>
public enum CompanionNoteKind
{
    /// <summary>The deleted thing spawns items: those players already have (or the server saved) stay.</summary>
    SpawnedItemsStay,

    /// <summary>An effect the actor's class creates (not stored in the level): it cannot go without the whole actor.</summary>
    ClassEffectStays,

    /// <summary>A volume mostly outside the deleted thing: shared with what stays, kept unless ticked.</summary>
    SharedKept,

    /// <summary>
    /// A trade post, spawner, volume or level actor attached to the deleted thing (<see cref="ActorRoles.IsNeverTakenAlong"/>):
    /// it stays where it stands, with what hangs on it, unless ticked.
    /// </summary>
    AttachedKept,
}

/// <summary>A warning about something that belongs to a deleted thing but does not go with it.</summary>
/// <param name="Kind">What it is about.</param>
/// <param name="Actor">The actor.</param>
/// <param name="Component">The component, or null.</param>
public sealed record CompanionNote(CompanionNoteKind Kind, ActorRef Actor, string? Component);

/// <summary>What <see cref="Companions.Find"/> found: the companions sorted by seed then distance, and the notes.</summary>
/// <param name="Companions">The companions.</param>
/// <param name="Notes">The warnings.</param>
public sealed record CompanionReport(IReadOnlyList<Companion> Companions, IReadOnlyList<CompanionNote> Notes)
{
    /// <summary>Nothing found.</summary>
    public static CompanionReport Empty { get; } = new([], []);
}

/// <summary>How far <see cref="Companions.Find"/> looks.</summary>
public sealed record CompanionOptions
{
    /// <summary>How far past the deleted thing's bounds sideways an effect still counts as its (cm).</summary>
    public float SideMarginCm { get; init; } = 150;

    /// <summary>How far above (cm): flames, smoke and lights hang over a fire.</summary>
    public float UpMarginCm { get; init; } = 400;

    /// <summary>How far below (cm).</summary>
    public float DownMarginCm { get; init; } = 50;

    /// <summary>Nothing farther from the deleted thing's bounds is taken (cm).</summary>
    public float MaxDistanceCm { get; init; } = 1000;

    /// <summary>World bounds of a volume actor, or null when unknown; volumes are offered only when this gives bounds.</summary>
    public Func<ActorRecord, BoundingBox?>? VolumeBounds { get; init; }
}

/// <summary>
/// What belongs to an object being deleted (the owner: "If I delete a fire place, the fire stays burning in game: the
/// flames, the heat, the smoke, the fire light and the sound, without the object"): its stored child actors and attached
/// actors, the mesh-less fire, light, smoke, sound and decal actors at its spot, the effect components of a deleted part,
/// and a water surface's other side. A kept object nearer to a candidate keeps it. Pure; one pass over the actors.
/// </summary>
public static class Companions
{
    private const float CellCm = 1000f;
    private const float FallbackHalfCm = 100f;
    private const float TwinToleranceCm = 1f;
    private const double WholeVolumeShare = 0.6;
    private const int MaxCellsPerBox = 4096;
    private static readonly string[] WaterTokens = ["Water", "Lake", "River", "Stream", "Creek", "Waterfall", "Foam", "Mist", "Splash"];

    /// <summary>
    /// The companions of <paramref name="seeds"/> in <paramref name="levels"/> (pristine documents; <paramref name="state"/>
    /// says what is already deleted or collapsed). <paramref name="meshBounds"/> gives a mesh's local bounds (null: a 2 m
    /// cube stands in).
    /// </summary>
    public static CompanionReport Find(
        IReadOnlyList<LevelDocument> levels,
        IReadOnlyList<DeleteSeed> seeds,
        EditState state,
        Func<string, BoundingBox?>? meshBounds,
        CompanionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(state);
        options ??= new CompanionOptions();
        var docs = new Dictionary<string, LevelDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in levels)
        {
            docs.TryAdd(level.PackagePath, level);
        }

        var found = new Search(docs, state, meshBounds, options);
        found.AddSeeds(seeds);
        if (found.Seeds.Count == 0)
        {
            return CompanionReport.Empty;
        }

        found.AddStructural();
        found.Scan(cancellationToken);
        return found.Report();
    }

    private sealed record SeedInfo(int Order, DeleteSeed Seed, LevelDocument Document, ActorRecord Actor, ComponentRecord? Part, List<BoundingBox> Boxes, List<BoundingBox> Grown, bool Water);

    private sealed record Candidate(ActorRef Actor, ActorRecord Record, CompanionRule Rule, SeedInfo Seed, float Distance, Vector3 Point, bool DefaultOn)
    {
        public ActorRef? AttachedTo { get; init; }
    }

    private sealed record Kept(ActorRef Owner, BoundingBox Box);

    private sealed class Search(Dictionary<string, LevelDocument> docs, EditState state, Func<string, BoundingBox?>? meshBounds, CompanionOptions options)
    {
        private readonly Dictionary<(int X, int Y), List<(SeedInfo Seed, int Box)>> _seedCells = [];
        private readonly List<(SeedInfo Seed, int Box)> _largeSeeds = [];
        private readonly Dictionary<(int X, int Y), List<Kept>> _keptCells = [];
        private readonly List<Kept> _largeKept = [];
        private readonly HashSet<ActorRef> _wholeSeeds = new(ActorRef.Comparer);
        private readonly Dictionary<ActorRef, HashSet<string>> _partSeeds = new(ActorRef.Comparer);
        private readonly HashSet<ActorRef> _structural = new(ActorRef.Comparer);
        private readonly Dictionary<string, List<(SeedInfo Seed, bool Underside, Vector3 At)>> _water = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Companion> _companions = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Candidate> _candidates = [];
        private readonly List<CompanionNote> _notes = [];
        private readonly Dictionary<SeedInfo, HashSet<int>> _goneComponents = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<LevelDocument, IReadOnlyDictionary<int, ActorRecord>> _owners = [];

        public List<SeedInfo> Seeds { get; } = [];

        public void AddSeeds(IReadOnlyList<DeleteSeed> seeds)
        {
            for (var i = 0; i < seeds.Count; i++)
            {
                var seed = seeds[i];
                if (!docs.TryGetValue(seed.Actor.Level, out var doc) || doc.FindActor(seed.Actor.Actor) is not { } actor)
                {
                    continue;
                }

                var part = seed.Part is { Length: > 0 } name ? actor.FindComponent(name) : null;
                if (seed.Part is { Length: > 0 } && part is null)
                {
                    continue;
                }

                List<ComponentRecord> meshes = part is not null ? [part] : actor.Components.Where(c => ActorRoles.IsDrawnMesh(actor, c)).ToList();
                if (meshes.Count == 0)
                {
                    meshes = actor.Components.Where(c => c.StaticMeshPath is not null).ToList();
                }

                var boxes = new List<BoundingBox>();
                foreach (var c in meshes)
                {
                    boxes.Add(BoxOf(c));
                }

                // A mesh-less seed (an effect pin deleted on its own) has a place but no body: nothing stands at its spot as its own.
                var body = boxes.Count > 0;
                if (!body)
                {
                    var at = (part?.WorldTransform ?? actor.WorldTransform).Translation.ToVector3();
                    boxes.Add(new BoundingBox(at, at));
                }

                var water = meshes.Any(c => c.StaticMeshPath is { } p && IsWaterMesh(p));
                var info = new SeedInfo(i, seed, doc, actor, part, boxes, boxes.Select(Grow).ToList(), water);
                Seeds.Add(info);
                var key = new ActorRef(doc.PackagePath, actor.Name);
                if (part is null)
                {
                    _wholeSeeds.Add(key);
                }
                else
                {
                    (_partSeeds.TryGetValue(key, out var parts) ? parts : _partSeeds[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(part.Name);
                }

                for (var b = 0; body && b < info.Grown.Count; b++)
                {
                    Insert(_seedCells, _largeSeeds, info.Grown[b], (info, b));
                }

                foreach (var c in meshes.Where(c => c.StaticMeshPath is { } p && IsWaterMesh(p) && !c.IsInstanced))
                {
                    var (twinBase, underside) = TwinKey(c.StaticMeshPath!);
                    (_water.TryGetValue(twinBase, out var list) ? list : _water[twinBase] = []).Add((info, underside, c.WorldTransform.Translation.ToVector3()));
                }

                if (part is null ? actor.Components.Any(c => c.SpawnMarkers.Count > 0) : part.SpawnMarkers.Count > 0)
                {
                    _notes.Add(new CompanionNote(CompanionNoteKind.SpawnedItemsStay, seed.Actor, part?.Name));
                }
            }

            // A part of an actor that goes whole anyway takes nothing of its own.
            foreach (var info in Seeds.Where(s => s.Part is not null && !_wholeSeeds.Contains(new ActorRef(s.Document.PackagePath, s.Actor.Name))))
            {
                AddOwnEffects(info);
            }
        }

        // The effect components of the same actor that belong to a deleted part: attached under it, or standing in its box
        // nearer to it than to any other part that stays.
        private void AddOwnEffects(SeedInfo info)
        {
            var actor = info.Actor;
            var part = info.Part!;
            var key = new ActorRef(info.Document.PackagePath, actor.Name);
            var gone = new HashSet<int>();
            if (part.ExportIndex >= 0 && !part.IsSynthesized)
            {
                gone.Add(part.ExportIndex);
            }

            var deletedParts = _partSeeds[key];
            var others = actor.Components
                .Where(c => c != part && ActorRoles.IsDrawnMesh(actor, c) && !deletedParts.Contains(c.Name) && !IsCollapsed(key, c))
                .Select(BoxOf).ToList();
            foreach (var c in actor.Components)
            {
                if (c == part || c.ExportIndex == actor.RootComponent || !ActorRoles.IsEffectComponent(c))
                {
                    continue;
                }

                var at = c.WorldTransform.Translation.ToVector3();
                var distance = Distance(info.Boxes, at, out var own);
                var under = IsUnder(actor, c, part);
                if (!under && !(info.Grown.Any(g => g.Contains(at)) && others.All(o => !Nearer(o, at, distance, own))))
                {
                    continue;
                }

                if (c.IsSynthesized || c.ExportIndex < 0 || c.IsNativeSubobject)
                {
                    _notes.Add(new CompanionNote(CompanionNoteKind.ClassEffectStays, key, c.Name));
                    continue;
                }

                gone.Add(c.ExportIndex);
                Add(new Companion(key, c.Name, c.ClassName, CompanionRule.OwnEffect, distance, Locked: under, DefaultOn: true) { Seed = info.Seed.Actor });
            }

            _goneComponents[info] = gone;
        }

        // Stored child actors and attached actors, through Attachments, per level.
        public void AddStructural()
        {
            foreach (var group in Seeds.GroupBy(s => s.Document))
            {
                var doc = group.Key;
                var whole = new Dictionary<string, SeedInfo>(StringComparer.OrdinalIgnoreCase);
                var byComponent = new Dictionary<int, SeedInfo>();
                foreach (var s in group)
                {
                    if (s.Part is null)
                    {
                        whole.TryAdd(s.Actor.Name, s);
                    }
                    else
                    {
                        foreach (var index in _goneComponents.GetValueOrDefault(s) ?? [])
                        {
                            byComponent.TryAdd(index, s);
                        }
                    }
                }

                var seedOf = new Dictionary<string, SeedInfo>(whole, StringComparer.OrdinalIgnoreCase);
                var stays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in Attachments.Dependents(doc, new HashSet<string>(whole.Keys, StringComparer.OrdinalIgnoreCase), new HashSet<int>(byComponent.Keys)))
                {
                    var seed = byComponent.TryGetValue(d.ViaComponent, out var viaPart) && !whole.ContainsKey(d.Parent.Name) ? viaPart
                        : seedOf.TryGetValue(d.Parent.Name, out var parentSeed) ? parentSeed : null;
                    if (seed is null)
                    {
                        continue;
                    }

                    seedOf.TryAdd(d.Actor.Name, seed);
                    var key = new ActorRef(doc.PackagePath, d.Actor.Name);
                    _structural.Add(key);
                    if (stays.Contains(d.Parent.Name))
                    {
                        stays.Add(d.Actor.Name); // what hangs on an actor that stays is that actor's
                        continue;
                    }

                    if (state.IsDeleted(key) || _wholeSeeds.Contains(key))
                    {
                        continue;
                    }

                    // A trade post, spawner or volume hung on the deleted thing is offered, never taken by default: a swim
                    // volume hung on it fills it and goes.
                    if (d.Kind == DependentKind.Attached && ActorRoles.IsNeverTakenAlong(doc, d.Actor))
                    {
                        var fills = d.Actor.WaterVolume;
                        Add(new Companion(key, null, d.Actor.ClassName, CompanionRule.Attached,
                            Distance(seed.Boxes, d.Actor.WorldTransform.Translation.ToVector3(), out _), Locked: false, DefaultOn: fills)
                        {
                            Seed = seed.Seed.Actor,
                            HasOwnMesh = d.HasDrawnMesh,
                        });
                        if (!fills)
                        {
                            stays.Add(d.Actor.Name);
                            _notes.Add(new CompanionNote(CompanionNoteKind.AttachedKept, key, null));
                        }

                        continue;
                    }

                    var ownMesh = d.Kind == DependentKind.Attached && d.HasDrawnMesh;
                    Add(new Companion(key, null, d.Actor.ClassName, d.Kind == DependentKind.ChildActor ? CompanionRule.ChildActor : CompanionRule.Attached,
                        Distance(seed.Boxes, d.Actor.WorldTransform.Translation.ToVector3(), out _), Locked: !ownMesh, DefaultOn: true)
                    {
                        Seed = seed.Seed.Actor,
                        HasOwnMesh = d.HasDrawnMesh,
                    });
                }
            }
        }

        // One pass over every actor of every level: the kept drawn objects near the seeds, the effects at their spot, the
        // water twins and the volumes.
        public void Scan(CancellationToken cancellationToken)
        {
            var n = 0;
            foreach (var doc in docs.Values)
            {
                foreach (var actor in doc.Actors)
                {
                    if ((++n & 4095) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var key = new ActorRef(doc.PackagePath, actor.Name);
                    if (_wholeSeeds.Contains(key) || _structural.Contains(key) || state.IsDeleted(key))
                    {
                        continue;
                    }

                    var twin = _water.Count > 0 ? FindTwin(key, actor) : null;
                    if (ActorRoles.HasDrawnMesh(actor))
                    {
                        AddKept(key, actor, twin);
                    }
                    else if (actor.Kind == ActorKind.Volume)
                    {
                        AddVolume(key, actor);
                    }
                    else if (IsCandidate(doc, actor, out var decal))
                    {
                        AddCandidate(key, doc, actor, decal);
                    }
                }
            }

            foreach (var c in _candidates)
            {
                var owner = c.AttachedTo ?? OwnerOf(c);
                Add(new Companion(c.Actor, null, c.Record.ClassName, c.Rule, c.Distance, Locked: false, DefaultOn: owner is null && c.DefaultOn)
                {
                    Seed = c.Seed.Seed.Actor,
                    BelongsTo = owner,
                });
            }
        }

        public CompanionReport Report()
        {
            var order = Seeds.GroupBy(s => s.Seed.Actor, ActorRef.Comparer).ToDictionary(g => g.Key, g => g.Min(s => s.Order), ActorRef.Comparer);
            var companions = _companions.Values
                .OrderBy(c => order.GetValueOrDefault(c.Seed))
                .ThenBy(c => c.DistanceCm)
                .ThenBy(c => c.Actor.Level, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Actor.Actor, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Component ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new CompanionReport(companions, _notes.Distinct().ToList());
        }

        private void AddKept(ActorRef key, ActorRecord actor, string? twinComponent)
        {
            if (twinComponent is { Length: 0 } || actor.ClassName.StartsWith("Landscape", StringComparison.Ordinal) || actor.ClassName == "InstancedFoliageActor")
            {
                return;
            }

            _partSeeds.TryGetValue(key, out var deletedParts);
            foreach (var c in actor.Components)
            {
                if (!ActorRoles.IsDrawnMesh(actor, c) || deletedParts?.Contains(c.Name) == true || IsCollapsed(key, c)
                    || string.Equals(c.Name, twinComponent, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var box = BoxOf(c);
                if (NearSeeds(box))
                {
                    Insert(_keptCells, _largeKept, box, new Kept(key, box));
                }
            }
        }

        private void AddVolume(ActorRef key, ActorRecord actor)
        {
            if (ActorRoles.IsStructural(actor.ClassName) || ActorRoles.IsGlobalEffect(actor.ClassName) || options.VolumeBounds?.Invoke(actor) is not { IsEmpty: false } bounds)
            {
                return;
            }

            SeedInfo? best = null;
            SeedInfo? bestWater = null;
            var (bestShare, bestWaterShare) = (-1d, -1d);
            var water = new List<BoundingBox>();
            foreach (var (seed, b) in SeedsOver(bounds))
            {
                var share = Share(bounds, seed.Grown[b]);
                if (share > bestShare)
                {
                    (best, bestShare) = (seed, share);
                }

                if (seed.Water && share > 0)
                {
                    water.Add(seed.Grown[b]);
                    if (share > bestWaterShare)
                    {
                        (bestWater, bestWaterShare) = (seed, share);
                    }
                }
            }

            if (best is null || bestShare <= 0)
            {
                return;
            }

            // A swim volume lies under its lake's surface: it goes with the lake when the deleted water covers most of its footprint.
            var fills = actor.WaterVolume && bestWater is not null && Footprint(bounds, water) >= WholeVolumeShare;
            var owner = fills ? bestWater! : best;
            Add(new Companion(key, null, actor.ClassName, CompanionRule.Volume, owner.Boxes.Min(b => BoxDistance(b, bounds)), Locked: false, DefaultOn: fills)
            {
                Seed = owner.Seed.Actor,
            });
            if (!fills && bestShare < WholeVolumeShare)
            {
                _notes.Add(new CompanionNote(CompanionNoteKind.SharedKept, key, null));
            }
        }

        // A mesh-less, instance-less effect or decal actor that is one object's (not the level's, not a trader, spawner or volume).
        private static bool IsCandidate(LevelDocument doc, ActorRecord actor, out bool decal)
        {
            decal = false;
            if (actor.RootComponent is null || actor.InstanceTransforms.Count > 0 || actor.Components.Any(c => c.IsInstanced) || ActorRoles.IsNeverTakenAlong(doc, actor))
            {
                return false;
            }

            decal = ActorRoles.IsDecal(actor);
            return decal || actor.Kind == ActorKind.Light || ActorRoles.IsEffectClass(actor.ClassName) || actor.Components.Any(c => ActorRoles.IsEffectClass(c.ClassName));
        }

        private void AddCandidate(ActorRef key, LevelDocument doc, ActorRecord actor, bool decal)
        {
            var at = (actor.Root?.WorldTransform ?? actor.WorldTransform).Translation.ToVector3();
            var waterish = IsWaterish(actor);
            Candidate? best = null;
            foreach (var (seed, b) in SeedsAt(at))
            {
                if (!seed.Grown[b].Contains(at))
                {
                    continue;
                }

                var distance = Distance(seed.Boxes, at, out _);
                var on = !seed.Water || waterish;
                if (distance <= options.MaxDistanceCm
                    && (best is null || (on && !best.DefaultOn) || (on == best.DefaultOn && (distance < best.Distance || (distance == best.Distance && seed.Order < best.Seed.Order)))))
                {
                    best = new Candidate(key, actor, decal ? CompanionRule.Decal : CompanionRule.AtTheSpot, seed, distance, at, on);
                }
            }

            if (best is null)
            {
                return;
            }

            // Attached to a kept actor (a lamp post's light): that actor's, whatever stands nearer.
            if (actor.Root?.AttachParent is { } parent && actor.Components.All(c => c.ExportIndex != parent))
            {
                var owners = _owners.TryGetValue(doc, out var known) ? known : _owners[doc] = Attachments.ComponentOwners(doc);
                if (owners.TryGetValue(parent, out var owner))
                {
                    best = best with { AttachedTo = new ActorRef(doc.PackagePath, owner.Name) };
                }
            }

            _candidates.Add(best);
        }

        // The ownership guard: the nearest kept drawn object that is nearer to the candidate than its seed (or around it and
        // smaller than the seed).
        private ActorRef? OwnerOf(Candidate c)
        {
            var own = c.Seed.Boxes.MinBy(b => BoxDistanceToPoint(b, c.Point));
            ActorRef? owner = null;
            var best = float.MaxValue;
            var reach = new BoundingBox(c.Point - new Vector3(c.Distance), c.Point + new Vector3(c.Distance));
            foreach (var k in Near(_keptCells, _largeKept, reach))
            {
                var d = BoxDistanceToPoint(k.Box, c.Point);
                if (Nearer(k.Box, c.Point, c.Distance, own) && d < best)
                {
                    (owner, best) = (k.Owner, d);
                }
            }

            return owner;
        }

        // The water surface's other side at the same place: "" when it is the actor itself, a component name when it is
        // one part of a kept actor, null when there is none.
        private string? FindTwin(ActorRef key, ActorRecord actor)
        {
            _partSeeds.TryGetValue(key, out var deletedParts);
            foreach (var c in actor.Components)
            {
                if (c.StaticMeshPath is not { } path || c.IsInstanced || !IsWaterMesh(path) || deletedParts?.Contains(c.Name) == true)
                {
                    continue;
                }

                var (twinBase, underside) = TwinKey(path);
                if (!_water.TryGetValue(twinBase, out var sides))
                {
                    continue;
                }

                var at = c.WorldTransform.Translation.ToVector3();
                foreach (var (seed, seedUnderside, seedAt) in sides)
                {
                    if (seedUnderside == underside || Vector3.Distance(at, seedAt) > TwinToleranceCm)
                    {
                        continue;
                    }

                    var whole = actor.InstanceTransforms.Count == 0 && actor.Components.All(o => o == c || o.StaticMeshPath is null);
                    Add(new Companion(key, whole ? null : c.Name, whole ? actor.ClassName : c.ClassName, CompanionRule.WaterTwin, 0, Locked: true, DefaultOn: true)
                    {
                        Seed = seed.Seed.Actor,
                    });
                    return whole ? string.Empty : c.Name;
                }
            }

            return null;
        }

        private void Add(Companion companion)
        {
            var key = companion.Actor.Level + "|" + companion.Actor.Actor + "|" + companion.Component;
            if (!_companions.TryGetValue(key, out var old) || Rank(companion.Rule) < Rank(old.Rule))
            {
                _companions[key] = companion;
            }
        }

        private static int Rank(CompanionRule rule) => rule switch
        {
            CompanionRule.ChildActor => 0,
            CompanionRule.Attached => 1,
            CompanionRule.WaterTwin => 2,
            CompanionRule.OwnEffect => 3,
            _ => 4,
        };

        private bool IsCollapsed(ActorRef key, ComponentRecord component) =>
            state.GetTransformOverride(key, component.Name) is { } t && MathF.Abs(t.Scale.X) < 1e-4f && MathF.Abs(t.Scale.Y) < 1e-4f && MathF.Abs(t.Scale.Z) < 1e-4f;

        private BoundingBox BoxOf(ComponentRecord component)
        {
            if (component.StaticMeshPath is { } mesh && meshBounds?.Invoke(mesh) is { IsEmpty: false } local)
            {
                if (component.IsInstanced && component.Instances.Count > 0)
                {
                    var union = BoundingBox.Empty;
                    foreach (var instance in component.Instances)
                    {
                        var (lo, hi) = FarModels.Around(instance * component.WorldTransform, local);
                        union = union.Union(new BoundingBox(lo.ToVector3(), hi.ToVector3()));
                    }

                    return union;
                }

                var (min, max) = FarModels.Around(component.WorldTransform, local);
                return new BoundingBox(min.ToVector3(), max.ToVector3());
            }

            var at = component.WorldTransform.Translation.ToVector3();
            return new BoundingBox(at - new Vector3(FallbackHalfCm), at + new Vector3(FallbackHalfCm));
        }

        private BoundingBox Grow(BoundingBox box) => new(
            box.Min - new Vector3(options.SideMarginCm, options.SideMarginCm, options.DownMarginCm),
            box.Max + new Vector3(options.SideMarginCm, options.SideMarginCm, options.UpMarginCm));

        private bool NearSeeds(BoundingBox box)
        {
            if (_largeSeeds.Count > 0)
            {
                return true;
            }

            var (x0, y0) = CellOf(box.Min.X - CellCm, box.Min.Y - CellCm);
            var (x1, y1) = CellOf(box.Max.X + CellCm, box.Max.Y + CellCm);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerBox)
            {
                return true;
            }

            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    if (_seedCells.ContainsKey((x, y)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private IEnumerable<(SeedInfo Seed, int Box)> SeedsAt(Vector3 at) =>
            _seedCells.TryGetValue(CellOf(at.X, at.Y), out var cell) ? cell.Concat(_largeSeeds) : _largeSeeds;

        private IEnumerable<(SeedInfo Seed, int Box)> SeedsOver(BoundingBox box) => Near(_seedCells, _largeSeeds, box).Distinct();

        private static IEnumerable<T> Near<T>(Dictionary<(int X, int Y), List<T>> cells, List<T> large, BoundingBox box)
        {
            var (x0, y0) = CellOf(box.Min.X, box.Min.Y);
            var (x1, y1) = CellOf(box.Max.X, box.Max.Y);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerBox)
            {
                return cells.Values.SelectMany(v => v).Concat(large);
            }

            var hits = new List<T>(large);
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    if (cells.TryGetValue((x, y), out var cell))
                    {
                        hits.AddRange(cell);
                    }
                }
            }

            return hits;
        }

        private static void Insert<T>(Dictionary<(int X, int Y), List<T>> cells, List<T> large, BoundingBox box, T item)
        {
            var (x0, y0) = CellOf(box.Min.X, box.Min.Y);
            var (x1, y1) = CellOf(box.Max.X, box.Max.Y);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerBox)
            {
                large.Add(item);
                return;
            }

            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    (cells.TryGetValue((x, y), out var cell) ? cell : cells[(x, y)] = []).Add(item);
                }
            }
        }

        private static (int X, int Y) CellOf(float x, float y) => ((int)MathF.Floor(x / CellCm), (int)MathF.Floor(y / CellCm));
    }

    // True when the kept box `other` holds the point rather than the deleted thing's box `own` at `distance`: nearer, or
    // around it and smaller than `own` (a kept house's walls round a fireplace never take the fireplace's fire).
    private static bool Nearer(BoundingBox other, Vector3 at, float distance, BoundingBox own)
    {
        var d = BoxDistanceToPoint(other, at);
        return d == 0 ? Volume(other) < Volume(own) : d < distance;
    }

    private static float Distance(List<BoundingBox> boxes, Vector3 at, out BoundingBox nearest)
    {
        nearest = boxes[0];
        var best = float.MaxValue;
        foreach (var b in boxes)
        {
            var d = BoxDistanceToPoint(b, at);
            if (d < best)
            {
                (best, nearest) = (d, b);
            }
        }

        return best;
    }

    private static float BoxDistanceToPoint(BoundingBox box, Vector3 at) => Vector3.Distance(at, Vector3.Clamp(at, box.Min, box.Max));

    private static float BoxDistance(BoundingBox a, BoundingBox b)
    {
        var gap = Vector3.Max(Vector3.Zero, Vector3.Max(a.Min - b.Max, b.Min - a.Max));
        return gap.Length();
    }

    private static float Volume(BoundingBox box) => box.IsEmpty ? 0 : box.Size.X * box.Size.Y * box.Size.Z;

    // Share of `volume` inside `area` (1 for a flat volume that touches it).
    private static double Share(BoundingBox volume, BoundingBox area)
    {
        var lo = Vector3.Max(volume.Min, area.Min);
        var hi = Vector3.Min(volume.Max, area.Max);
        if (lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z)
        {
            return 0;
        }

        var whole = (double)Volume(volume);
        return whole <= 0 ? 1 : (double)(hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z) / whole;
    }

    // Share of `volume`'s ground footprint (X, Y) that `areas` cover together, overlaps counted once. Past 64 areas their
    // shares are summed (an overlap may count twice).
    private static double Footprint(BoundingBox volume, List<BoundingBox> areas)
    {
        var rects = new List<(float X0, float Y0, float X1, float Y1)>();
        foreach (var a in areas)
        {
            var (x0, y0) = (MathF.Max(a.Min.X, volume.Min.X), MathF.Max(a.Min.Y, volume.Min.Y));
            var (x1, y1) = (MathF.Min(a.Max.X, volume.Max.X), MathF.Min(a.Max.Y, volume.Max.Y));
            if (x0 <= x1 && y0 <= y1)
            {
                rects.Add((x0, y0, x1, y1));
            }
        }

        var whole = (double)(volume.Max.X - volume.Min.X) * (volume.Max.Y - volume.Min.Y);
        if (rects.Count == 0 || whole <= 0)
        {
            return rects.Count > 0 ? 1 : 0;
        }

        if (rects.Count > 64)
        {
            return Math.Min(1, rects.Sum(r => (double)(r.X1 - r.X0) * (r.Y1 - r.Y0)) / whole);
        }

        var xs = rects.SelectMany(r => new[] { r.X0, r.X1 }).Distinct().Order().ToList();
        var ys = rects.SelectMany(r => new[] { r.Y0, r.Y1 }).Distinct().Order().ToList();
        var covered = 0d;
        for (var i = 0; i + 1 < xs.Count; i++)
        {
            for (var j = 0; j + 1 < ys.Count; j++)
            {
                var (cx, cy) = ((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2);
                if (rects.Any(r => r.X0 <= cx && cx <= r.X1 && r.Y0 <= cy && cy <= r.Y1))
                {
                    covered += (double)(xs[i + 1] - xs[i]) * (ys[j + 1] - ys[j]);
                }
            }
        }

        return covered / whole;
    }

    private static bool IsUnder(ActorRecord actor, ComponentRecord component, ComponentRecord part)
    {
        var parent = component.AttachParent;
        for (var depth = 0; parent is { } p && depth < 64; depth++)
        {
            if (p == part.ExportIndex)
            {
                return true;
            }

            parent = actor.Components.FirstOrDefault(c => c.ExportIndex == p)?.AttachParent;
        }

        return false;
    }

    private static bool IsWaterMesh(string path) =>
        path.Contains("/Landscape/Lake", StringComparison.OrdinalIgnoreCase) || path.Contains("/Landscape/River", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Landscape/Stream", StringComparison.OrdinalIgnoreCase) || FarModels.IsUndersideMesh(path);

    private static bool IsWaterish(ActorRecord actor) =>
        WaterTokens.Any(t => actor.ClassName.Contains(t, StringComparison.OrdinalIgnoreCase) || actor.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
            || actor.Components.Any(c => c.ClassName.Contains(t, StringComparison.OrdinalIgnoreCase)));

    // The water surface's family name and side: LakeWaterSurface_C_1_FN (or _Down) is the underside of LakeWaterSurface_C_1.
    private static (string Base, bool Underside) TwinKey(string meshPath)
    {
        var name = meshPath[(meshPath.LastIndexOf('/') + 1)..];
        if (name.IndexOf('.', StringComparison.Ordinal) is var dot and >= 0)
        {
            name = name[..dot];
        }

        if (name.EndsWith("_FN", StringComparison.OrdinalIgnoreCase))
        {
            return (name[..^3], true);
        }

        return name.EndsWith("_Down", StringComparison.OrdinalIgnoreCase) ? (name[..^5], true) : (name, false);
    }
}
