using System.Buffers.Binary;
using System.Globalization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Level.Spawns;

/// <summary>What to change in the island's spawn places.</summary>
public sealed record SpawnPlacesEditRequest
{
    /// <summary>Stock places to remove.</summary>
    public IReadOnlyCollection<(SpawnPlaceKind Kind, int Index)> Deleted { get; init; } = [];

    /// <summary>Stock places moved (root transform as the map shows it: place, turn, and for zones and areas the size in metres).</summary>
    public IReadOnlyDictionary<(SpawnPlaceKind Kind, int Index), TransformValue> Moved { get; init; } = new Dictionary<(SpawnPlaceKind, int), TransformValue>();

    /// <summary>New places: a copy of a stock place (its group and settings) at a transform.</summary>
    public IReadOnlyList<(SpawnPlaceKind Kind, int Source, TransformValue Transform)> Added { get; init; } = [];

    /// <summary>
    /// What stock places spawn (see <see cref="SpawnChoices"/>): a vehicle point's group (<c>City</c>), a zone's settings
    /// (<c>MTZ_Settlement_Large</c>) or a hunting area's biome (<c>BD_Village</c>). A copy of the place carries it.
    /// </summary>
    public IReadOnlyDictionary<(SpawnPlaceKind Kind, int Index), string> Choices { get; init; } = new Dictionary<(SpawnPlaceKind, int), string>();

    /// <summary>What added places spawn, by their index in <see cref="Added"/> (over their source's choice).</summary>
    public IReadOnlyDictionary<int, string> AddedChoices { get; init; } = new Dictionary<int, string>();

    /// <summary>New places (not copies: <see cref="AddSpawnPlaceOp"/>), each written with what it spawns.</summary>
    public IReadOnlyList<NewSpawnPlace> Created { get; init; } = [];

    /// <summary>Sub-zones added to stock threat zones (<see cref="AddSubZoneOp"/>), by the zone's number in its list.</summary>
    public IReadOnlyList<(int Zone, SubZone SubZone)> SubZones { get; init; } = [];

    /// <summary>True when nothing changes.</summary>
    public bool IsEmpty => Deleted.Count == 0 && Moved.Count == 0 && Added.Count == 0 && Choices.Count == 0 && Created.Count == 0 && SubZones.Count == 0;
}

/// <summary>A new spawn place for <see cref="SpawnPlacesEditor.Apply"/> (see <see cref="AddSpawnPlaceOp"/>).</summary>
/// <param name="Name">Its actor name (for the report).</param>
/// <param name="Kind">Which array it joins.</param>
/// <param name="Setting">What spawns there (<see cref="AddSpawnPlaceOp.Setting"/>, or the choice set on it later).</param>
/// <param name="Transform">Where, as the map holds it (a zone's or area's size in the scale, metres).</param>
/// <param name="Tags">A tagged point's gameplay tags (<see cref="AddSpawnPlaceOp.Tags"/>); null or empty = none.</param>
/// <param name="Points">A group's posts in the world (<see cref="AddSpawnPlaceOp.PostsAt"/>); null for other kinds.</param>
public sealed record NewSpawnPlace(string Name, SpawnPlaceKind Kind, string Setting, TransformValue Transform, IReadOnlyList<string>? Tags = null, IReadOnlyList<FVector>? Points = null);

/// <summary>
/// What the client export read of the stock places its edits name by number (audit F281): each list's length and where
/// each named place stands. A server export checks its own cook against it.
/// </summary>
/// <param name="Counts">The client cook's list lengths (<see cref="SpawnPlacesEditor.Counts"/>).</param>
/// <param name="Places">Where each stock place the edits name stands in the client cook.</param>
public sealed record SpawnPlacesReference(IReadOnlyDictionary<SpawnPlaceKind, int> Counts, IReadOnlyDictionary<(SpawnPlaceKind Kind, int Index), FVector> Places);

/// <summary>What <see cref="SpawnPlacesEditor.Apply"/> wrote.</summary>
/// <param name="Deleted">Places removed.</param>
/// <param name="Moved">Places moved or resized.</param>
/// <param name="Added">Places added.</param>
/// <param name="Warnings">Edits that could not be written.</param>
/// <param name="Chosen">Stock places whose choice of what spawns was written.</param>
/// <param name="Created">New places written (not copies).</param>
/// <param name="SubZones">Sub-zones written into stock threat zones.</param>
public sealed record SpawnPlacesReport(int Deleted, int Moved, int Added, IReadOnlyList<string> Warnings, int Chosen = 0, int Created = 0, int SubZones = 0);

/// <summary>
/// Writes spawn place edits into <c>The_Island_LevelStaticData</c>: each touched array is rebuilt from its stock entries
/// (kept, moved in place, or left out), then copies of stock entries at their new places, then the new places (laid out as
/// a stock entry, every field the map shows written from the edit); its count and the array and inner tag sizes follow. A
/// zone's bounding boxes (plain, activation, low-population activation) are recomputed from its ellipse, the way the stock
/// data has them. A zone's settings or a hunting area's biome the data does not import yet is imported when its package
/// exists (in the game files or among the project's copies).
/// </summary>
public static class SpawnPlacesEditor
{
    private static readonly string[] ZoneMembers = ["_semiXAxisSize", "_semiYAxisSize", "_boundingBox", "_zoneActivationBoundingBox", "_LPCZoneActivationBoundingBox"];

    // A zone's sub-zones (boxes with settings of their own: SCUM's FEncounterSubZoneData).
    private const string SubZoneData = "_subZoneData";

    // A zone's sub-zones and the points drones approach it from: world places around it.
    private static readonly string[] PlaceBound = [SubZoneData, "_flyingSpawnedCharacterApproachLocations"];

    // The members of a sub-zone and of each of its boxes (FEncounterSubZoneData, FEncounterSubZoneBoxData).
    private static readonly string[] SubZoneMembers = ["BoxDataArray", "SubZoneBox", "ShouldInstantlyLoadAllNecessaryClasses", "EncounterZoneData", "SubZonePriority"];
    private static readonly string[] SubZoneBoxMembers = ["AreaBox", "BoxRotation"];

    // The members of each list's entries (SCUM's FVehicleSpawnPointData, FEncounterLegacy/TaggedCharacterSpawnPoint,
    // FEncounterStaticZoneDescription, FHuntingBiomeDescription with its FBiomeParameters). A new place writes its place,
    // size, what spawns there and its tags, leaves the place-bound ones out, and keeps the rest as its template has them
    // (a zone's activation multipliers and low-population settings, an area's priority, spawning switch and visitor
    // distance); an entry with any other member (an id in a later game build) is never a template.
    private static readonly Dictionary<SpawnPlaceKind, string[]> KnownMembers = new()
    {
        [SpawnPlaceKind.Vehicle] = ["SpawnTransform", SpawnChoices.VehicleGroup],
        [SpawnPlaceKind.Character] = ["Type", "Transform"],
        [SpawnPlaceKind.TaggedCharacter] = ["Type", "Transform", "Tags"],
        [SpawnPlaceKind.Zone] = ["_transform", SpawnChoices.ZoneData, "_lpcZoneData", "_zoneActivationRadiusMultiplier", "_LPCZoneActivationRadiusMultiplier", .. ZoneMembers, .. PlaceBound],
        [SpawnPlaceKind.Animal] = ["Transform", "Radius", "PopulationParameters"],
        [SpawnPlaceKind.CharacterGroup] = ["GroupBounds", "SpawnTransforms", "Type"],
    };

    // A group's box stands this far around its posts, as the island's own groups have it.
    private const float GroupBoundsPadding = 5f;

    private static readonly string[] PopulationMembers = ["Priority", SpawnChoices.Biome, "bEnablePopulationSpawning", "VisitorDeactivationDistance"];

    /// <summary>The spawn place edits of <paramref name="state"/> (actors of the <see cref="SpawnPlaces.StaticDataPath"/> document).</summary>
    public static SpawnPlacesEditRequest Plan(EditState state, List<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var deleted = new HashSet<(SpawnPlaceKind, int)>();
        foreach (var actor in state.DeletedActors.Where(a => SpawnPlaces.IsStaticData(a.Level)))
        {
            if (SpawnPlaces.ParseName(actor.Actor) is { } stock)
            {
                deleted.Add(stock);
            }
        }

        var moved = new Dictionary<(SpawnPlaceKind, int), TransformValue>();
        foreach (var (actor, component, value) in state.TransformOverrides.Where(t => SpawnPlaces.IsStaticData(t.Actor.Level)))
        {
            if (component.Length == 0 && SpawnPlaces.ParseName(actor.Actor) is { } stock && !deleted.Contains(stock))
            {
                moved[stock] = value;
            }
        }

        var choices = new Dictionary<(SpawnPlaceKind, int), string>();
        foreach (var (actor, property, value) in state.SpawnChoices.Where(c => SpawnPlaces.IsStaticData(c.Actor.Level)))
        {
            if (SpawnPlaces.ParseName(actor.Actor) is { } stock && !deleted.Contains(stock) && SpawnChoices.PropertyOf(stock.Kind) == property)
            {
                choices[stock] = value;
            }
        }

        var addedChoices = new Dictionary<int, string>();
        var added = new List<(SpawnPlaceKind, int, TransformValue)>();
        var created = new List<NewSpawnPlace>();
        foreach (var (actor, root, transform, choice) in AddedPlaces(state))
        {
            switch (root)
            {
                case DuplicateActorOp copy when SpawnPlaces.ParseName(copy.Source.Actor) is { } stock:
                    if (choice is not null)
                    {
                        addedChoices[added.Count] = choice;
                    }

                    added.Add((stock.Kind, stock.Index, transform));
                    break;
                case AddSpawnPlaceOp place:
                    created.Add(new NewSpawnPlace(actor.Actor, place.Kind, choice ?? place.Setting, transform, place.Tags, place.Kind == SpawnPlaceKind.CharacterGroup ? place.PostsAt(transform) : null));
                    break;
                default:
                    warnings?.Add($"{actor.Actor}: neither a copy of a spawn place nor a new one; not written.");
                    break;
            }
        }

        var subZones = new List<(int, SubZone)>();
        foreach (var (zone, subZone) in state.SubZones.Where(s => SpawnPlaces.IsStaticData(s.Zone.Level)))
        {
            if (SpawnPlaces.ParseName(zone.Actor) is { Kind: SpawnPlaceKind.Zone } stock && !deleted.Contains(stock))
            {
                subZones.Add((stock.Index, subZone));
            }
            else
            {
                warnings?.Add($"{zone.Actor}: its sub-zone ({subZone}) goes with the removed zone; not written.");
            }
        }

        return new SpawnPlacesEditRequest { Deleted = deleted, Moved = moved, Added = added, Choices = choices, AddedChoices = addedChoices, Created = created, SubZones = subZones };
    }

    /// <summary>
    /// The places the project added to the static data and kept, in the order the export appends them (by actor name): the
    /// actor, the edit its chain of copies starts from (a copy of a stock place or a <see cref="AddSpawnPlaceOp"/>), where it
    /// stands, and what it spawns when that was chosen on it or on a copy in between (null = as where the chain starts).
    /// </summary>
    public static IEnumerable<(ActorRef Actor, EditOp Root, TransformValue Transform, string? Choice)> AddedPlaces(EditState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var (actor, op) in state.AddedActors.Where(a => SpawnPlaces.IsStaticData(a.Key.Level)).OrderBy(a => a.Key.Actor, StringComparer.Ordinal).ToList())
        {
            if (state.IsDeleted(actor) || state.GetAddedTransform(actor) is not { } transform)
            {
                continue;
            }

            // A copy of a copy is a copy of the place the chain starts from.
            var chain = new List<ActorRef> { actor };
            var root = op;
            while (root is DuplicateActorOp d && SpawnPlaces.ParseName(d.Source.Actor) is null && state.AddedActors.TryGetValue(d.Source, out var parent) && chain.Count < 64)
            {
                chain.Add(d.Source);
                root = parent;
            }

            var kind = root switch
            {
                DuplicateActorOp copy when SpawnPlaces.ParseName(copy.Source.Actor) is { } stock => stock.Kind,
                AddSpawnPlaceOp place => place.Kind,
                _ => (SpawnPlaceKind?)null,
            };
            var property = kind is { } k ? SpawnChoices.PropertyOf(k) : null;
            yield return (actor, root, transform, property is null ? null : chain.Select(a => state.GetSpawnChoice(a, property)).FirstOrDefault(c => c is not null));
        }
    }

    /// <summary>
    /// <paramref name="request"/> with every stock vehicle and zombie / NPC spawn point that stands inside one of the solid
    /// <paramref name="volumes"/> (where it stands after the request's moves) removed too (owner: "nothing may spawn inside
    /// the rocks I build"). A hollow shell keeps the points in its rooms (audit F099: a placed hangar emptied its floor).
    /// Threat zones and hunting areas are areas, not points, and stay; so do the places the project added (the user put them
    /// there). <paramref name="blocked"/> counts the removed points, <paramref name="names"/> gets their actor names.
    /// </summary>
    public static SpawnPlacesEditRequest Block(SpawnPlacesEditRequest request, IReadOnlyList<SpawnPlace> places, IReadOnlyList<Export.GrassClearing.Volume> volumes, out int blocked, List<string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(volumes);
        var deleted = request.Deleted.ToHashSet();
        var moved = new Dictionary<(SpawnPlaceKind, int), TransformValue>(request.Moved);
        var solid = volumes.Where(v => v.Solid).ToList();
        blocked = 0;
        foreach (var place in places.Where(p => solid.Count > 0 && p.Kind is SpawnPlaceKind.Vehicle or SpawnPlaceKind.Character or SpawnPlaceKind.TaggedCharacter))
        {
            var key = (place.Kind, place.Index);
            if (deleted.Contains(key))
            {
                continue;
            }

            var at = moved.TryGetValue(key, out var to) ? to.Location : place.Transform.Translation;
            if (Export.GrassClearing.Inside(solid, at))
            {
                deleted.Add(key);
                moved.Remove(key);
                names?.Add(place.ActorName);
                blocked++;
            }
        }

        return blocked == 0 ? request : request with { Deleted = deleted, Moved = moved };
    }

    /// <summary>How many places of each kind the static data <paramref name="package"/> holds (its arrays' lengths).</summary>
    public static IReadOnlyDictionary<SpawnPlaceKind, int> Counts(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var block = package.ReadProperties(SpawnPlaces.StaticDataExport(package));
        return Enum.GetValues<SpawnPlaceKind>().ToDictionary(k => k, k => block.Find(SpawnPlaces.ArrayOf(k))?.Value is ArrayValue a ? a.Items.Count : 0);
    }

    /// <summary>
    /// Where a cook's arrays differ in length from the client's (<c>VehicleSpawnPoints 3491 / 3490</c>), or null when they
    /// match. The edits name places by their number in the client's arrays; with other lengths they would hit other places.
    /// </summary>
    public static string? CountMismatch(IReadOnlyDictionary<SpawnPlaceKind, int> client, IReadOnlyDictionary<SpawnPlaceKind, int> other)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(other);
        var differing = Enum.GetValues<SpawnPlaceKind>()
            .Where(k => client.GetValueOrDefault(k) != other.GetValueOrDefault(k))
            .Select(k => string.Create(CultureInfo.InvariantCulture, $"{SpawnPlaces.ArrayOf(k)} {client.GetValueOrDefault(k)} / {other.GetValueOrDefault(k)}"))
            .ToList();
        return differing.Count == 0 ? null : string.Join(", ", differing);
    }

    /// <summary>
    /// What a server export checks its cook against (audit F281): the lengths of the client's lists in <paramref name="package"/>
    /// and where each stock place <paramref name="request"/> names by its number (removed, moved, re-chosen, copied) stands.
    /// </summary>
    public static SpawnPlacesReference Reference(CookedPackage package, SpawnPlacesEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(request);
        var named = Numbered(request).ToHashSet();
        var places = SpawnPlaces.Read(package).Where(p => named.Contains((p.Kind, p.Index))).ToDictionary(p => (p.Kind, p.Index), p => p.Transform.Translation);
        return new SpawnPlacesReference(Counts(package), places);
    }

    /// <summary>
    /// <paramref name="request"/> for another cook of the island (<paramref name="cook"/>, a server's): whole when its lists
    /// have the client's lengths and every stock place the request names by number stands where it stands in the client's
    /// (<paramref name="client"/>); else, and without a client reference, only its new places, with a warning saying why
    /// (audit F281: a server cook of another order would get the removals and moves on other places).
    /// </summary>
    public static SpawnPlacesEditRequest ForOtherCook(SpawnPlacesEditRequest request, SpawnPlacesReference? client, CookedPackage cook, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(cook);
        ArgumentNullException.ThrowIfNull(warnings);
        var named = Numbered(request).Distinct().ToList();
        if (named.Count == 0)
        {
            return request;
        }

        string? why;
        if (client is null)
        {
            why = "the client's island data was not read in this export, so the server's could not be checked against it";
        }
        else if (CountMismatch(client.Counts, Counts(cook)) is { } counts)
        {
            why = $"the server's island data does not hold the same lists as the client's ({counts}; client / server)";
        }
        else
        {
            var stands = SpawnPlaces.Read(cook).ToDictionary(p => (p.Kind, p.Index), p => p.Transform.Translation);
            var elsewhere = named
                .Where(k => !client.Places.TryGetValue(k, out var c) || !stands.TryGetValue(k, out var s) || FVector.Distance(c, s) > 1f)
                .Select(k => SpawnPlaces.ActorName(k.Kind, k.Index))
                .ToList();
            why = elsewhere.Count == 0 ? null
                : string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", elsewhere.Take(5))}{(elsewhere.Count > 5 ? $" and {elsewhere.Count - 5} more" : string.Empty)} do not stand in the server's island data where they stand in the client's");
        }

        if (why is null)
        {
            return request;
        }

        warnings.Add($"Spawn places: {why}. The edits name stock places by their number in the client's lists, so none of the removed, moved, re-chosen or copied stock places and no sub-zone of a stock zone were written into this pak (new places were): export client and server together, from game files of the same build.");
        return new SpawnPlacesEditRequest { Created = request.Created };
    }

    /// <summary>Every stock place <paramref name="request"/> names by its number in a list.</summary>
    private static IEnumerable<(SpawnPlaceKind Kind, int Index)> Numbered(SpawnPlacesEditRequest request) =>
        request.Deleted.Concat(request.Moved.Keys).Concat(request.Choices.Keys).Concat(request.Added.Select(a => (a.Kind, a.Source)))
            .Concat(request.SubZones.Select(s => (SpawnPlaceKind.Zone, s.Zone)));

    /// <summary>
    /// Applies <paramref name="request"/> to the static data <paramref name="package"/>. A zone's settings or a hunting
    /// area's biome the data does not import yet is imported only when <paramref name="packageExists"/> says its package is
    /// in the game files or among the project's copies (a missing one would load as nothing in the game); without it, none is.
    /// </summary>
    public static (PackageBytes Bytes, SpawnPlacesReport Report) Apply(CookedPackage package, SpawnPlacesEditRequest request, Func<string, bool>? packageExists = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(request);
        var exportIndex = SpawnPlaces.StaticDataExport(package);
        var payload = package.GetExportData(exportIndex).ToArray();
        var block = package.ReadProperties(exportIndex);
        var tables = new Tables(package, exportIndex, packageExists);
        var warnings = new List<string>();
        int deleted = 0, moved = 0, added = 0, chosen = 0, created = 0, subZones = 0;

        var kinds = request.Deleted.Select(d => d.Kind).Concat(request.Moved.Keys.Select(m => m.Kind)).Concat(request.Added.Select(a => a.Kind))
            .Concat(request.Choices.Keys.Select(c => c.Kind)).Concat(request.Created.Select(c => c.Kind)).Concat(request.SubZones.Select(_ => SpawnPlaceKind.Zone)).Distinct();
        var arrays = kinds.Select(k => (Kind: k, Tag: block.Find(SpawnPlaces.ArrayOf(k)))).ToList();
        warnings.AddRange(arrays.Where(a => a.Tag is not { Value: ArrayValue { InnerTag: not null } }).Select(a => $"{SpawnPlaces.ArrayOf(a.Kind)}: not in the island data; its edits were not written."));
        foreach (var (kind, tag) in arrays.Where(a => a.Tag is { Value: ArrayValue { InnerTag: not null } }).OrderByDescending(a => a.Tag!.Offset)) // from the back: the offsets in front stay valid
        {
            var array = (ArrayValue)tag!.Value;
            var count = array.Items.Count;

            // Places are named by their number in the list the map read (audit F281: another cook or game build may differ).
            foreach (var index in request.Deleted.Where(d => d.Kind == kind).Select(d => d.Index)
                         .Concat(request.Moved.Keys.Where(m => m.Kind == kind).Select(m => m.Index))
                         .Concat(request.Choices.Keys.Where(c => c.Kind == kind).Select(c => c.Index))
                         .Concat(request.SubZones.Where(_ => kind == SpawnPlaceKind.Zone).Select(s => s.Zone))
                         .Where(i => i < 0 || i >= count).Distinct().Order())
            {
                warnings.Add($"{SpawnPlaces.ActorName(kind, index)}: the island data has {count} {SpawnPlaces.ArrayOf(kind)}; not written (read from another game build?).");
            }

            var items = new List<byte[]>(count + 16);
            for (var i = 0; i < count; i++)
            {
                var item = (StructValue)array.Items[i];
                if (request.Deleted.Contains((kind, i)))
                {
                    deleted++;
                    continue;
                }

                var bytes = payload.AsSpan(item.Offset, item.Size).ToArray();
                if (request.Moved.TryGetValue((kind, i), out var value) && Place(bytes, kind, item, value, -item.Offset, SpawnPlaces.ActorName(kind, i), warnings))
                {
                    moved++;
                }

                if (request.Choices.TryGetValue((kind, i), out var choice) && Choose(tables, bytes, kind, item, choice, -item.Offset, SpawnPlaces.ActorName(kind, i), warnings))
                {
                    chosen++;
                }

                // Sub-zones go last: they change the length of the zone.
                if (kind == SpawnPlaceKind.Zone && request.SubZones.Where(s => s.Zone == i).Select(s => s.SubZone).ToList() is { Count: > 0 } subs
                    && AddSubZones(tables, payload, array, bytes, item, subs, SpawnPlaces.ActorName(kind, i), warnings) is { } grown)
                {
                    bytes = grown;
                    subZones += subs.Count;
                }

                items.Add(bytes);
            }

            for (var a = 0; a < request.Added.Count; a++)
            {
                var (addedKind, source, transform) = request.Added[a];
                if (addedKind != kind)
                {
                    continue;
                }

                var name = $"{SpawnPlaces.ActorName(kind, source)} (copy)";
                if (source < 0 || source >= count)
                {
                    warnings.Add($"{name}: no such stock place to copy.");
                    continue;
                }

                var item = (StructValue)array.Items[source];
                var bytes = payload.AsSpan(item.Offset, item.Size).ToArray();
                if (!Place(bytes, kind, item, transform, -item.Offset, name, warnings))
                {
                    continue; // a copy where its source stands is no copy
                }

                if ((request.AddedChoices.TryGetValue(a, out var own) ? own : request.Choices.GetValueOrDefault((kind, source))) is { } carried)
                {
                    Choose(tables, bytes, kind, item, carried, -item.Offset, name, warnings);
                }

                items.Add(bytes);
                added++;
            }

            foreach (var place in request.Created.Where(c => c.Kind == kind))
            {
                if (Create(tables, payload, array, place, warnings) is { } bytes)
                {
                    items.Add(bytes);
                    created++;
                }
            }

            payload = ReplaceStructArray(payload, tag, array, items);
        }

        var data = Enumerable.Range(0, package.Exports.Count).Select(i => i == exportIndex ? payload : package.GetExportData(i)).ToArray();
        var input = PackageWriter.ToBuildInput(package) with { ExportData = data };
        if (tables.Changed)
        {
            input = input with { Names = tables.Names, NameIsWide = tables.Wide, Imports = tables.Imports, Exports = tables.Exports, PreloadDependencies = tables.Preload };
        }

        return (PackageWriter.Build(input), new SpawnPlacesReport(deleted, moved, added, warnings, chosen, created, subZones));
    }

    /// <summary>
    /// The bytes of the stock zone <paramref name="item"/> (<paramref name="bytes"/>, its place and settings already written)
    /// with <paramref name="subZones"/> after its own sub-zones. Each is laid out as the first whole sub-zone of the island
    /// data (the game's NPC-only ones look the same: one list of boxes, the box around them, the settings, the priority and
    /// the class-loading switch), then written: one box (not turned) and the same box around it, its settings, its
    /// priority, the class-loading switch off. Null, with a warning, when the zone stores no sub-zone list, no stock
    /// sub-zone can serve or the settings cannot be imported: the zone then stays as the game has it.
    /// </summary>
    private static byte[]? AddSubZones(Tables tables, byte[] payload, ArrayValue zones, byte[] bytes, StructValue item, IReadOnlyList<SubZone> subZones, string name, List<string> warnings)
    {
        var shift = -item.Offset;
        if (SpawnPlaces.Member(item, SubZoneData) is not { Value: ArrayValue { InnerTag: not null } own } tag)
        {
            warnings.Add($"{name}: the island data stores no sub-zone list for this zone; its {subZones.Count} new sub-zone(s) were not written.");
            return null;
        }

        var template = zones.Items.OfType<StructValue>()
            .SelectMany(z => SpawnPlaces.Member(z, SubZoneData)?.Value is ArrayValue a ? a.Items.OfType<StructValue>() : [])
            .FirstOrDefault(IsSubZoneTemplate);
        if (template is null)
        {
            warnings.Add($"{name}: no sub-zone of the island data stores every member a new one gets ({string.Join(", ", SubZoneMembers)}); its {subZones.Count} new sub-zone(s) were not written.");
            return null;
        }

        var items = own.Items.OfType<StructValue>().Select(s => bytes.AsSpan(s.Offset + shift, s.Size).ToArray()).ToList();
        foreach (var subZone in subZones)
        {
            var made = payload.AsSpan(template.Offset, template.Size).ToArray();
            var at = -template.Offset;
            if (tables.Import(subZone.Setting, "EncounterZoneData", out var problem) is not { } settings)
            {
                warnings.Add($"{name}: sub-zone settings {subZone.Setting}: {problem}; not written.");
                return null;
            }

            // The same-size members first, then the list of boxes (its length may change).
            BinaryPrimitives.WriteInt32LittleEndian(made.AsSpan(((ObjectValue)SpawnPlaces.Member(template, "EncounterZoneData")!.Value).Offset + at), settings);
            BinaryPrimitives.WriteInt32LittleEndian(made.AsSpan(((IntValue)SpawnPlaces.Member(template, "SubZonePriority")!.Value).Offset + at), subZone.Priority);
            made[SpawnPlaces.Member(template, "ShouldInstantlyLoadAllNecessaryClasses")!.BoolValueOffset + at] = 0;
            WriteBox(made, (BoxValue)SpawnPlaces.Member(template, "SubZoneBox")!.Value, subZone.Min, subZone.Max, at);

            var boxes = SpawnPlaces.Member(template, "BoxDataArray")!;
            var first = (StructValue)((ArrayValue)boxes.Value).Items[0];
            var box = payload.AsSpan(first.Offset, first.Size).ToArray();
            WriteBox(box, (BoxValue)SpawnPlaces.Member(first, "AreaBox")!.Value, subZone.Min, subZone.Max, -first.Offset);
            Floats(box, ((RotatorValue)SpawnPlaces.Member(first, "BoxRotation")!.Value).Offset - first.Offset, 0f, 0f, 0f);
            items.Add(ReplaceStructArray(made, boxes, (ArrayValue)boxes.Value, [box], at));
        }

        return ReplaceStructArray(bytes, tag, own, items, shift);
    }

    /// <summary>True when <paramref name="subZone"/> stores every member a new sub-zone gets, each of the type written, and nothing else.</summary>
    private static bool IsSubZoneTemplate(StructValue subZone) =>
        subZone.Properties.Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(SubZoneMembers.Order(StringComparer.Ordinal))
        && SpawnPlaces.Member(subZone, "BoxDataArray")?.Value is ArrayValue { InnerTag: not null, Items: [StructValue first, ..] }
        && first.Properties.Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(SubZoneBoxMembers.Order(StringComparer.Ordinal))
        && SpawnPlaces.Member(first, "AreaBox")?.Value is BoxValue && SpawnPlaces.Member(first, "BoxRotation")?.Value is RotatorValue
        && SpawnPlaces.Member(subZone, "SubZoneBox")?.Value is BoxValue && SpawnPlaces.Member(subZone, "EncounterZoneData")?.Value is ObjectValue
        && SpawnPlaces.Member(subZone, "SubZonePriority")?.Value is IntValue
        && SpawnPlaces.Member(subZone, "ShouldInstantlyLoadAllNecessaryClasses") is { Type: "BoolProperty", BoolValueOffset: >= 0 };

    /// <summary>Writes a box (its two corners, and valid) into item bytes (offsets shifted by <paramref name="shift"/>).</summary>
    private static void WriteBox(byte[] bytes, BoxValue box, FVector min, FVector max, int shift)
    {
        Floats(bytes, box.Offset + shift, min.X, min.Y, min.Z, max.X, max.Y, max.Z);
        if (box.Size >= 25)
        {
            bytes[box.Offset + shift + 24] = 1;
        }
    }

    /// <summary>
    /// The bytes of a new element of <paramref name="array"/> for <paramref name="place"/>: laid out as the first stock
    /// element that stores every field a new place gets and no member this version does not know (a zone of the same
    /// settings, a hunting area of the same biome, a tagged point that stores tags when there is one), then each of those
    /// written from <paramref name="place"/>: where, the size, what spawns there and a tagged point's tags. A zone leaves its
    /// template's sub-zones and drone approach points behind (they stand around the template). Null, with a warning, when
    /// no element can serve or a field cannot be written: a new place never keeps the template's.
    /// </summary>
    private static byte[]? Create(Tables tables, byte[] payload, ArrayValue array, NewSpawnPlace place, List<string> warnings)
    {
        var kind = place.Kind;
        if (kind == SpawnPlaceKind.CharacterGroup)
        {
            return CreateGroup(tables, payload, array, place, warnings);
        }

        var whole = array.Items.OfType<StructValue>().Where(i => IsTemplate(kind, i)).ToList();
        var templates = whole.Where(i => UnknownMembers(kind, i).Count == 0).ToList();
        var setting = SpawnPlaces.SettingName(place.Setting);
        var tags = place.Tags ?? [];
        var template = kind switch
        {
            SpawnPlaceKind.Zone or SpawnPlaceKind.Animal => templates.FirstOrDefault(i => SpawnPlaces.Describe(kind, 0, i)?.Group == setting),
            SpawnPlaceKind.TaggedCharacter => templates.FirstOrDefault(i => SpawnPlaces.Member(i, "Tags")?.Value is ArrayValue),
            _ => null,
        } ?? templates.FirstOrDefault();
        if (template is null)
        {
            warnings.Add(whole.Count == 0
                ? $"{place.Name}: no entry of {SpawnPlaces.ArrayOf(kind)} stores a whole place to lay a new one out from; not written."
                : $"{place.Name}: the entries of {SpawnPlaces.ArrayOf(kind)} hold {string.Join(", ", UnknownMembers(kind, whole[0]))}, which this version does not know (a new place would take it from a stock place); not written.");
            return null;
        }

        var bytes = payload.AsSpan(template.Offset, template.Size).ToArray();
        var shift = -template.Offset;
        if (!Place(bytes, kind, template, place.Transform, shift, place.Name, warnings) || !(kind is SpawnPlaceKind.Character or SpawnPlaceKind.TaggedCharacter
                ? WriteType(tables.Package, bytes, template, place.Setting, shift, place.Name, warnings)
                : Choose(tables, bytes, kind, template, place.Setting, shift, place.Name, warnings)))
        {
            return null;
        }

        // Members that change the element's length go last, from the back (the offsets in front stay valid).
        var splices = new List<(int Start, int End, byte[] With)>();
        switch (kind)
        {
            case SpawnPlaceKind.Zone:
                // A zone whose settings no stock zone uses (the project's own copy) spawns by them at a low player count too,
                // not by the low-population settings of an unrelated zone.
                if (SpawnPlaces.Describe(kind, 0, template)?.Group != setting && SpawnPlaces.Member(template, "_lpcZoneData")?.Value is ObjectValue lpc
                    && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(lpc.Offset + shift)) != 0 && SpawnPlaces.Member(template, SpawnChoices.ZoneData)?.Value is ObjectValue zone)
                {
                    bytes.AsSpan(zone.Offset + shift, 4).CopyTo(bytes.AsSpan(lpc.Offset + shift));
                }

                splices.AddRange(PlaceBound.Select(m => SpawnPlaces.Member(template, m)).OfType<PropertyTag>().Select(t => (t.Offset, t.EndOffset, Array.Empty<byte>())));
                break;
            case SpawnPlaceKind.TaggedCharacter when SpawnPlaces.Member(template, "Tags") is { Value: ArrayValue } stored:
                if (TagContainer(tables.Package, tags, place.Name, warnings) is not { } value)
                {
                    return null;
                }

                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(stored.SizeFieldOffset + shift), value.Length);
                splices.Add((stored.ValueOffset, stored.EndOffset, value));
                break;
            case SpawnPlaceKind.TaggedCharacter when tags.Count > 0:
                warnings.Add($"{place.Name}: no entry of {SpawnPlaces.ArrayOf(kind)} stores tags to lay its tags out from; not written.");
                return null;
        }

        foreach (var (start, end, with) in splices.OrderByDescending(s => s.Start))
        {
            var spliced = new byte[bytes.Length - (end - start) + with.Length];
            bytes.AsSpan(0, start + shift).CopyTo(spliced);
            with.CopyTo(spliced, start + shift);
            bytes.AsSpan(end + shift).CopyTo(spliced.AsSpan(start + shift + with.Length));
            bytes = spliced;
        }

        return bytes;
    }

    /// <summary>The members of <paramref name="item"/> this version does not know (<see cref="KnownMembers"/>; a hunting area's population parameters too).</summary>
    internal static List<string> UnknownMembers(SpawnPlaceKind kind, StructValue item)
    {
        var unknown = item.Properties.Select(p => p.Name).Where(n => !KnownMembers[kind].Contains(n, StringComparer.Ordinal)).ToList();
        if (kind == SpawnPlaceKind.Animal && SpawnPlaces.Member(item, "PopulationParameters")?.Value is StructValue population)
        {
            unknown.AddRange(population.Properties.Select(p => p.Name).Where(n => !PopulationMembers.Contains(n, StringComparer.Ordinal)).Select(n => "PopulationParameters." + n));
        }

        return unknown;
    }

    /// <summary>A <c>GameplayTagContainer</c>'s value for <paramref name="tags"/> (the count, then each tag's FName); null, with a warning, for a tag the island data does not name (no new FName).</summary>
    private static byte[]? TagContainer(CookedPackage package, IReadOnlyList<string> tags, string name, List<string> warnings)
    {
        var value = new byte[4 + (8 * tags.Count)];
        BinaryPrimitives.WriteInt32LittleEndian(value, tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            FNameRef tag;
            try
            {
                tag = package.FindName(tags[i]);
            }
            catch (KeyNotFoundException)
            {
                warnings.Add($"{name}: tag '{tags[i]}': the island data has no such tag; not written.");
                return null;
            }

            BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + (8 * i)), tag.Index);
            BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8 + (8 * i)), tag.Number);
        }

        return value;
    }

    /// <summary>True when <paramref name="item"/> stores every field <see cref="Create"/> writes (no field of a new place is left as the template has it).</summary>
    private static bool IsTemplate(SpawnPlaceKind kind, StructValue item)
    {
        if (kind == SpawnPlaceKind.CharacterGroup)
        {
            return SpawnPlaces.Member(item, "GroupBounds")?.Value is BoxValue && SpawnPlaces.Member(item, "Type")?.Value is EnumValue
                && SpawnPlaces.Member(item, "SpawnTransforms")?.Value is ArrayValue { InnerTag: not null, Items: [StructValue first, ..] } posts
                && posts.Items.All(p => p is StructValue)
                && SpawnPlaces.Member(first, "Rotation")?.Value is QuatValue && SpawnPlaces.Member(first, "Translation")?.Value is VectorValue
                && SpawnPlaces.Member(first, "Scale3D")?.Value is VectorValue;
        }

        if (SpawnPlaces.Member(item, TransformOf(kind))?.Value is not StructValue t || SpawnPlaces.Member(t, "Rotation")?.Value is not QuatValue
            || SpawnPlaces.Member(t, "Translation")?.Value is not VectorValue || SpawnPlaces.Member(t, "Scale3D")?.Value is not VectorValue)
        {
            return false;
        }

        return kind switch
        {
            SpawnPlaceKind.Vehicle => SpawnPlaces.Member(item, SpawnChoices.VehicleGroup)?.Value is StructValue g && SpawnPlaces.Member(g, "TagName")?.Value is NameValue,
            SpawnPlaceKind.Character or SpawnPlaceKind.TaggedCharacter => SpawnPlaces.Member(item, "Type")?.Value is EnumValue,
            SpawnPlaceKind.Zone => SpawnPlaces.Member(item, SpawnChoices.ZoneData)?.Value is ObjectValue && ZoneMembers.All(m => SpawnPlaces.Member(item, m) is not null),
            _ => SpawnPlaces.Member(item, "Radius")?.Value is FloatValue && SpawnPlaces.Member(item, "PopulationParameters")?.Value is StructValue p
                 && SpawnPlaces.Member(p, SpawnChoices.Biome)?.Value is ObjectValue,
        };
    }

    /// <summary>
    /// Writes what a place spawns into its item bytes (offsets shifted by <paramref name="shift"/>), the same size: a
    /// vehicle point's group tag (an FName of the package's name table: a group the game does not know spawns nothing, so
    /// no name is added) or a zone's settings / a hunting area's biome (an import, added when a path names one the data
    /// does not import yet: a zone's own copy of its settings). False, with a warning, when it cannot be written.
    /// </summary>
    private static bool Choose(Tables tables, byte[] bytes, SpawnPlaceKind kind, StructValue item, string choice, int shift, string name, List<string> warnings)
    {
        switch (kind)
        {
            case SpawnPlaceKind.Vehicle when SpawnPlaces.Member(item, SpawnChoices.VehicleGroup)?.Value is StructValue tag && SpawnPlaces.Member(tag, "TagName")?.Value is NameValue group:
                var full = SpawnChoices.GroupTagPrefix + SpawnPlaces.SettingName(choice);
                if (!tables.Package.Names.Contains(full, StringComparer.Ordinal))
                {
                    warnings.Add($"{name}: vehicle group '{choice}': the island data has no such group; not written.");
                    return false;
                }

                var nameRef = tables.Package.FindName(full);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(group.Offset + shift), nameRef.Index);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(group.Offset + shift + 4), nameRef.Number);
                return true;
            case SpawnPlaceKind.Zone when SpawnPlaces.Member(item, SpawnChoices.ZoneData)?.Value is ObjectValue zone:
                return WriteImport(zone, "EncounterZoneData");
            case SpawnPlaceKind.Animal when SpawnPlaces.Member(item, "PopulationParameters")?.Value is StructValue p && SpawnPlaces.Member(p, SpawnChoices.Biome)?.Value is ObjectValue biome:
                return WriteImport(biome, "BiomeData");
            default:
                warnings.Add($"{name}: '{choice}' cannot be chosen in {SpawnPlaces.ArrayOf(kind)}; not written.");
                return false;
        }

        bool WriteImport(ObjectValue value, string className)
        {
            if (tables.Import(choice, className, out var problem) is not { } index)
            {
                warnings.Add($"{name}: {className} '{choice}': {problem}; not written.");
                return false;
            }

            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(value.Offset + shift), index);
            return true;
        }
    }

    /// <summary>Writes a zombie / NPC point's type (<c>Exterior</c>, <c>Interior</c>): a value of its enum the island data names (no new FName).</summary>
    private static bool WriteType(CookedPackage package, byte[] bytes, StructValue item, string type, int shift, string name, List<string> warnings)
    {
        var e = (EnumValue)SpawnPlaces.Member(item, "Type")!.Value;
        var full = e.Value.LastIndexOf("::", StringComparison.Ordinal) is var at and >= 0 ? e.Value[..(at + 2)] + type : type;
        if (!package.Names.Contains(full, StringComparer.Ordinal))
        {
            warnings.Add($"{name}: '{type}' is not a zombie / NPC point type of the island data; not written.");
            return false;
        }

        var nameRef = package.FindName(full);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(e.Offset + shift), nameRef.Index);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(e.Offset + shift + 4), nameRef.Number);
        return true;
    }

    /// <summary>
    /// <paramref name="payload"/> with the array-of-structs <paramref name="tag"/> holding <paramref name="items"/> (each
    /// the bytes of one tagged struct element): the count, the inner struct tag (its size = all items) and the items; the
    /// tag's size follows. Offsets after the tag move; read the result again before patching further. With
    /// <paramref name="shift"/>, <paramref name="payload"/> is the bytes of one element and the offsets are shifted by it.
    /// </summary>
    internal static byte[] ReplaceStructArray(byte[] payload, PropertyTag tag, ArrayValue array, IReadOnlyList<byte[]> items, int shift = 0)
    {
        var inner = array.InnerTag ?? throw new ArgumentException("Not an array of structs.", nameof(array));
        var itemsLength = items.Sum(b => b.Length);
        var header = payload.AsSpan(inner.Offset + shift, inner.ValueOffset - inner.Offset).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(inner.SizeFieldOffset - inner.Offset), itemsLength);
        var value = new byte[4 + header.Length + itemsLength];
        BinaryPrimitives.WriteInt32LittleEndian(value, items.Count);
        header.CopyTo(value, 4);
        var at = 4 + header.Length;
        foreach (var b in items)
        {
            b.CopyTo(value, at);
            at += b.Length;
        }

        var result = new byte[payload.Length - tag.Size + value.Length];
        payload.AsSpan(0, tag.ValueOffset + shift).CopyTo(result);
        value.CopyTo(result, tag.ValueOffset + shift);
        payload.AsSpan(tag.EndOffset + shift).CopyTo(result.AsSpan(tag.ValueOffset + shift + value.Length));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(tag.SizeFieldOffset + shift), value.Length);
        return result;
    }

    /// <summary>
    /// Writes where a spawn point is into the bytes of its struct element (offsets shifted by <paramref name="shift"/>): a
    /// tagged <c>Transform</c> member (an item spawner marker: rotation, translation, scale) or else the element's first
    /// vector member (a sentry's <c>LocationRelativeToSentry</c>: the location only). Null when written, else why nothing
    /// was: a turn, place or scale the element has no member for is not dropped silently (audit F107).
    /// </summary>
    internal static string? WritePoint(byte[] bytes, StructValue item, TransformValue value, int shift)
    {
        if (SpawnPlaces.Member(item, "Transform")?.Value is StructValue t)
        {
            var (r, p, s) = (SpawnPlaces.Member(t, "Rotation")?.Value as QuatValue, SpawnPlaces.Member(t, "Translation")?.Value as VectorValue, SpawnPlaces.Member(t, "Scale3D")?.Value as VectorValue);
            if (Missing(value, r is not null, p is not null, s is not null) is { } missing)
            {
                return $"{missing} not stored in the game data; not written";
            }

            WriteTransform(bytes, value, r, p, s, shift);
            return null;
        }

        if (item.Properties.Select(m => m.Value).OfType<VectorValue>().FirstOrDefault() is { } location)
        {
            Floats(bytes, location.Offset + shift, value.Location.X, value.Location.Y, value.Location.Z);
            return null;
        }

        return "the element has no location to write";
    }

    /// <summary>
    /// Writes a place's transform (a point's scale too: audit F439; a zone's or area's size) into its item bytes (offsets
    /// shifted by <paramref name="shift"/>). False, with a warning and nothing written, when a part that differs from what
    /// is stored has no member to hold it (audit F107: the turn of a point that stores no rotation was dropped silently).
    /// </summary>
    private static bool Place(byte[] bytes, SpawnPlaceKind kind, StructValue item, TransformValue value, int shift, string name, List<string> warnings)
    {
        if (kind == SpawnPlaceKind.CharacterGroup)
        {
            return PlaceGroup(bytes, item, value, shift, name, warnings);
        }

        if (SpawnPlaces.Member(item, TransformOf(kind))?.Value is not StructValue t)
        {
            warnings.Add($"{name}: its place is not stored in the game data; not written.");
            return false;
        }

        var area = kind is SpawnPlaceKind.Zone or SpawnPlaceKind.Animal;
        var (r, p) = (SpawnPlaces.Member(t, "Rotation")?.Value as QuatValue, SpawnPlaces.Member(t, "Translation")?.Value as VectorValue);
        var s = area ? null : SpawnPlaces.Member(t, "Scale3D")?.Value as VectorValue; // an area's scale is its size
        var sized = kind switch
        {
            SpawnPlaceKind.Zone => ZoneMembers.Take(2).All(m => SpawnPlaces.Member(item, m)?.Value is FloatValue),
            SpawnPlaceKind.Animal => SpawnPlaces.Member(item, "Radius")?.Value is FloatValue,
            _ => true,
        };
        var missing = string.Join(", ", new[] { Missing(value, r is not null, p is not null, s is not null || area), sized ? null : "size" }.OfType<string>());
        if (missing.Length > 0)
        {
            warnings.Add($"{name}: {missing} not stored in the game data; not written.");
            return false;
        }

        if (kind == SpawnPlaceKind.Zone && (Turned(value.Rotation.Pitch) || Turned(value.Rotation.Roll)))
        {
            warnings.Add($"{name}: a threat zone only turns about the vertical in the game; its pitch and roll do nothing.");
        }

        WriteTransform(bytes, value, r, p, s, shift);
        var (sizeX, sizeY) = SpawnPlaces.SizeOf(value.Scale);
        if (kind == SpawnPlaceKind.Animal && SpawnPlaces.Member(item, "Radius")?.Value is FloatValue radius)
        {
            Floats(bytes, radius.Offset + shift, MathF.Max(sizeX, sizeY));
        }

        if (kind != SpawnPlaceKind.Zone)
        {
            return true;
        }

        Set(item, "_semiXAxisSize", sizeX);
        Set(item, "_semiYAxisSize", sizeY);
        var activation = Get(item, "_zoneActivationRadiusMultiplier", 1f);
        var lpc = Get(item, "_LPCZoneActivationRadiusMultiplier", 1f);
        Box("_boundingBox", 1f);
        Box("_zoneActivationBoundingBox", activation);
        Box("_LPCZoneActivationBoundingBox", lpc);
        return true;

        void Set(StructValue sv, string member, float v)
        {
            if (SpawnPlaces.Member(sv, member)?.Value is FloatValue f)
            {
                Floats(bytes, f.Offset + shift, v);
            }
        }

        static float Get(StructValue sv, string member, float fallback) => SpawnPlaces.Member(sv, member)?.Value is FloatValue f ? f.Value : fallback;

        // The axis-aligned box around the zone's ellipse (turned by its yaw), flat at the zone's height.
        void Box(string member, float multiplier)
        {
            if (SpawnPlaces.Member(item, member)?.Value is not BoxValue box)
            {
                return;
            }

            var yaw = value.Rotation.Yaw * MathF.PI / 180f;
            var (a, b) = (sizeX * multiplier, sizeY * multiplier);
            var ex = MathF.Sqrt((a * MathF.Cos(yaw) * a * MathF.Cos(yaw)) + (b * MathF.Sin(yaw) * b * MathF.Sin(yaw)));
            var ey = MathF.Sqrt((a * MathF.Sin(yaw) * a * MathF.Sin(yaw)) + (b * MathF.Cos(yaw) * b * MathF.Cos(yaw)));
            var c = value.Location;
            Floats(bytes, box.Offset + shift, c.X - ex, c.Y - ey, c.Z, c.X + ex, c.Y + ey, c.Z);
        }
    }

    /// <summary>
    /// Moves a group of zombie / NPC points (a stock group moved or copied) to <paramref name="value"/>: the place stands on
    /// the first post, so every post keeps where it is relative to the first, turned with it, and the group's box follows.
    /// The bytes keep their size.
    /// </summary>
    private static bool PlaceGroup(byte[] bytes, StructValue item, TransformValue value, int shift, string name, List<string> warnings)
    {
        var posts = SpawnPlaces.Member(item, "SpawnTransforms")?.Value is ArrayValue a ? a.Items.OfType<StructValue>().ToList() : [];
        var stored = SpawnPlaces.GroupPosts(item);
        if (posts.Count == 0 || stored.Count != posts.Count || SpawnPlaces.Member(item, "GroupBounds")?.Value is not BoxValue box)
        {
            warnings.Add($"{name}: its posts are not stored in the game data; not written.");
            return false;
        }

        var to = value.ToTransform();
        var turn = to.Rotation * stored[0].Rotation.Inverse();
        var moved = SpawnPlaces.MovePosts(stored.Select(p => p.Translation).ToList(), stored[0], to);
        for (var i = 0; i < posts.Count; i++)
        {
            var (r, p, s) = (SpawnPlaces.Member(posts[i], "Rotation")?.Value as QuatValue, SpawnPlaces.Member(posts[i], "Translation")?.Value as VectorValue, SpawnPlaces.Member(posts[i], "Scale3D")?.Value as VectorValue);
            WriteTransform(bytes, new TransformValue(moved[i], (turn * stored[i].Rotation).GetNormalized().Rotator(), stored[i].Scale3D), r, p, s, shift);
        }

        WriteGroupBounds(bytes, box, moved, shift);
        return true;
    }

    /// <summary>
    /// The bytes of a new group of zombie / NPC points: laid out as a stock group of the same type (else the first whole
    /// one), its posts replaced by <see cref="NewSpawnPlace.Points"/> (each turned to face the next, the last as the one
    /// before it), its type and its box (the posts and <see cref="GroupBoundsPadding"/> around them) written. Null, with a
    /// warning, when no stock group can serve or a value cannot be written.
    /// </summary>
    private static byte[]? CreateGroup(Tables tables, byte[] payload, ArrayValue array, NewSpawnPlace place, List<string> warnings)
    {
        var kind = SpawnPlaceKind.CharacterGroup;
        var points = place.Points ?? [];
        if (points.Count == 0 || points.Count > SpawnPlaces.MaxGroupPosts)
        {
            warnings.Add($"{place.Name}: a group needs 1 to {SpawnPlaces.MaxGroupPosts} posts ({points.Count}); not written.");
            return null;
        }

        var whole = array.Items.OfType<StructValue>().Where(i => IsTemplate(kind, i)).ToList();
        var templates = whole.Where(i => UnknownMembers(kind, i).Count == 0).ToList();
        var template = templates.FirstOrDefault(i => SpawnPlaces.Describe(kind, 0, i)?.Group == place.Setting) ?? templates.FirstOrDefault();
        if (template is null)
        {
            warnings.Add(whole.Count == 0
                ? $"{place.Name}: no entry of {SpawnPlaces.ArrayOf(kind)} stores a whole group to lay a new one out from; not written."
                : $"{place.Name}: the entries of {SpawnPlaces.ArrayOf(kind)} hold {string.Join(", ", UnknownMembers(kind, whole[0]))}, which this version does not know; not written.");
            return null;
        }

        var bytes = payload.AsSpan(template.Offset, template.Size).ToArray();
        var shift = -template.Offset;
        if (!WriteType(tables.Package, bytes, template, place.Setting, shift, place.Name, warnings))
        {
            return null;
        }

        WriteGroupBounds(bytes, (BoxValue)SpawnPlaces.Member(template, "GroupBounds")!.Value, points, shift);

        // The posts: copies of the template's first post, each written; then the array goes in place of the template's.
        var tag = SpawnPlaces.Member(template, "SpawnTransforms")!;
        var stored = (ArrayValue)tag.Value;
        var first = (StructValue)stored.Items[0];
        var (r, p, s) = ((QuatValue)SpawnPlaces.Member(first, "Rotation")!.Value, (VectorValue)SpawnPlaces.Member(first, "Translation")!.Value, (VectorValue)SpawnPlaces.Member(first, "Scale3D")!.Value);
        var posts = new List<byte[]>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var post = payload.AsSpan(first.Offset, first.Size).ToArray();
            var ahead = i + 1 < points.Count ? points[i + 1] - points[i] : i > 0 ? points[i] - points[i - 1] : FVector.Zero;
            var yaw = ahead.X * ahead.X + ahead.Y * ahead.Y > 1f ? MathF.Atan2(ahead.Y, ahead.X) * 180f / MathF.PI : place.Transform.Rotation.Yaw;
            WriteTransform(post, new TransformValue(points[i], new FRotator(0f, yaw, 0f), FVector.One), r, p, s, -first.Offset);
            posts.Add(post);
        }

        var inner = stored.InnerTag!;
        var header = payload.AsSpan(inner.Offset, inner.ValueOffset - inner.Offset).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(inner.SizeFieldOffset - inner.Offset), posts.Sum(b => b.Length));
        var value = new byte[4 + header.Length + posts.Sum(b => b.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(value, posts.Count);
        header.CopyTo(value, 4);
        var at = 4 + header.Length;
        foreach (var b in posts)
        {
            b.CopyTo(value, at);
            at += b.Length;
        }

        var result = new byte[bytes.Length - tag.Size + value.Length];
        bytes.AsSpan(0, tag.ValueOffset + shift).CopyTo(result);
        value.CopyTo(result, tag.ValueOffset + shift);
        bytes.AsSpan(tag.EndOffset + shift).CopyTo(result.AsSpan(tag.ValueOffset + shift + value.Length));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(tag.SizeFieldOffset + shift), value.Length);
        return result;
    }

    /// <summary>Writes a group's box: around <paramref name="posts"/>, <see cref="GroupBoundsPadding"/> wider each way, valid.</summary>
    private static void WriteGroupBounds(byte[] bytes, BoxValue box, IReadOnlyList<FVector> posts, int shift)
    {
        Floats(bytes, box.Offset + shift,
            posts.Min(q => q.X) - GroupBoundsPadding, posts.Min(q => q.Y) - GroupBoundsPadding, posts.Min(q => q.Z) - GroupBoundsPadding,
            posts.Max(q => q.X) + GroupBoundsPadding, posts.Max(q => q.Y) + GroupBoundsPadding, posts.Max(q => q.Z) + GroupBoundsPadding);
        if (box.Size >= 25)
        {
            bytes[box.Offset + shift + 24] = 1;
        }
    }

    /// <summary>The parts of <paramref name="value"/> that differ from what an absent member reads as (no turn, the origin, scale 1) and have no member; null when none.</summary>
    private static string? Missing(TransformValue value, bool hasRotation, bool hasTranslation, bool hasScale)
    {
        var missing = new List<string>(3);
        if (!hasRotation && (Turned(value.Rotation.Pitch) || Turned(value.Rotation.Yaw) || Turned(value.Rotation.Roll)))
        {
            missing.Add("rotation");
        }

        if (!hasTranslation && value.Location.Size() > 0.01f)
        {
            missing.Add("translation");
        }

        if (!hasScale && FVector.Distance(value.Scale, FVector.One) > 1e-4f)
        {
            missing.Add("scale");
        }

        return missing.Count == 0 ? null : string.Join(", ", missing);
    }

    private static bool Turned(float degrees) => MathF.Abs(MathF.IEEERemainder(degrees, 360f)) > 0.01f;

    private static void WriteTransform(byte[] bytes, TransformValue value, QuatValue? rotation, VectorValue? translation, VectorValue? scale, int shift)
    {
        if (rotation is not null)
        {
            var q = value.ToTransform().Rotation;
            Floats(bytes, rotation.Offset + shift, q.X, q.Y, q.Z, q.W);
        }

        if (translation is not null)
        {
            Floats(bytes, translation.Offset + shift, value.Location.X, value.Location.Y, value.Location.Z);
        }

        if (scale is not null)
        {
            Floats(bytes, scale.Offset + shift, value.Scale.X, value.Scale.Y, value.Scale.Z);
        }
    }

    private static string TransformOf(SpawnPlaceKind kind) => kind switch
    {
        SpawnPlaceKind.Vehicle => "SpawnTransform",
        SpawnPlaceKind.Zone => "_transform",
        _ => "Transform",
    };

    internal static void Floats(byte[] bytes, int offset, params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + (i * 4)), values[i]);
        }
    }

    /// <summary>The static data's name, import, export and preload tables while a choice adds an import.</summary>
    private sealed class Tables(CookedPackage package, int exportIndex, Func<string, bool>? packageExists)
    {
        private readonly List<string> _addedNames = [];

        public CookedPackage Package { get; } = package;

        public List<string> Names { get; } = package.Names.ToList();

        public List<bool> Wide { get; } = package.NameEntries.Select(n => n.IsWide).ToList();

        public List<ImportEntry> Imports { get; } = package.Imports.ToList();

        public List<ExportEntry> Exports { get; } = package.Exports.ToList();

        public List<int> Preload { get; } = package.ReadPreloadDependencies().ToList();

        public bool Changed => Imports.Count != Package.Imports.Count;

        /// <summary>
        /// The import of <paramref name="setting"/> (an asset name the data imports, or its package or object path) of class
        /// <paramref name="className"/>, added with its package when a path names one the data does not import: the class
        /// package is that of the data's own imports of the class, and the static data lists it as created before it is
        /// serialized, as the cooker does. Null with <paramref name="problem"/> when it cannot be found or added, or when
        /// the package is not known to exist (an undone copy or a typo would load as nothing in the client and the server).
        /// </summary>
        public int? Import(string setting, string className, out string problem)
        {
            problem = string.Empty;
            var name = SpawnPlaces.SettingName(setting);
            var path = PackageOf(setting);
            FNameRef? classPackage = null;
            for (var i = 0; i < Imports.Count; i++)
            {
                var im = Imports[i];
                if (!Is(im.ClassName, className))
                {
                    continue;
                }

                classPackage ??= im.ClassPackage;
                if (Is(im.ObjectName, name) && (path is null || (im.OuterIndex < 0 && Is(Imports[-im.OuterIndex - 1].ObjectName, path))))
                {
                    return -(i + 1);
                }
            }

            if (path is null)
            {
                problem = "the island data does not use it (its package path adds it)";
                return null;
            }

            if (packageExists?.Invoke(path) != true)
            {
                problem = packageExists is null ? "its package was not checked against the game files, so it is not added" : $"{path} is neither in the game files nor one of the project's copies";
                return null;
            }

            if (classPackage is not { } cp)
            {
                problem = $"the island data has no {className} to take its class from";
                return null;
            }

            var outer = Export.LevelPackageEditor.GetOrAddImport(Imports, Names, Wide, _addedNames, "/Script/CoreUObject", "Package", 0, path);
            var index = Export.LevelPackageEditor.GetOrAddImport(Imports, Names, Wide, _addedNames, cp.Format(Names), className, outer, name);
            AddCreateBeforeSerialize(index);
            return index;
        }

        /// <summary>The package path of a setting given as a path (<c>/Game/…/MTZ_Village</c> of <c>/Game/…/MTZ_Village.MTZ_Village</c>), or null for a bare name.</summary>
        private static string? PackageOf(string setting)
        {
            var slash = setting.LastIndexOf('/');
            if (slash < 0)
            {
                return null;
            }

            var dot = setting.IndexOf('.', slash);
            return dot < 0 ? setting : setting[..dot];
        }

        /// <summary>Lists <paramref name="dependency"/> last among the static data's create-before-serialize dependencies; the lists after it move up by one.</summary>
        private void AddCreateBeforeSerialize(int dependency)
        {
            var export = Exports[exportIndex];
            int at;
            if (export.FirstExportDependency < 0)
            {
                at = Preload.Count;
                export = export with { FirstExportDependency = at };
            }
            else
            {
                at = export.FirstExportDependency + export.SerializationBeforeSerializationDependencies + export.CreateBeforeSerializationDependencies;
                for (var i = 0; i < Exports.Count; i++)
                {
                    if (i != exportIndex && Exports[i].FirstExportDependency >= at)
                    {
                        Exports[i] = Exports[i] with { FirstExportDependency = Exports[i].FirstExportDependency + 1 };
                    }
                }
            }

            Preload.Insert(at, dependency);
            Exports[exportIndex] = export with { CreateBeforeSerializationDependencies = export.CreateBeforeSerializationDependencies + 1 };
        }

        private bool Is(FNameRef name, string text) =>
            name.Index >= 0 && name.Index < Names.Count && string.Equals(name.Format(Names), text, StringComparison.OrdinalIgnoreCase);
    }
}
