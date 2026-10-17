using System.Globalization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Level.Spawns;

/// <summary>What kind of place a <see cref="SpawnPlace"/> is (one array of the island's level static data).</summary>
public enum SpawnPlaceKind
{
    /// <summary>A world vehicle spawn point (<c>VehicleSpawnPoints</c>): a car, bike, boat or plane of its group may appear here.</summary>
    Vehicle,

    /// <summary>A zombie / NPC spawn point (<c>EncounterLegacyCharacterSpawnPoints</c>, outdoor or indoor).</summary>
    Character,

    /// <summary>A tagged zombie / NPC spawn point (<c>EncounterTaggedCharacterSpawnPoints</c>).</summary>
    TaggedCharacter,

    /// <summary>A threat zone (<c>StaticZoneDescriptions</c>): an ellipse where its encounter settings spawn zombies and NPCs.</summary>
    Zone,

    /// <summary>A hunting area (<c>HuntingBiomes</c>): a circle where animals of its biome live.</summary>
    Animal,

    /// <summary>
    /// A group of zombie / NPC points (<c>EncounterGroupedCharacterSpawnPoints</c>: a box and the posts in it): where an
    /// encounter that spawns a group (<c>_isGroupSpawn</c>, as the armed NPC encounters do) puts its characters together.
    /// The place stands on its first post; <see cref="SpawnPlace.Points"/> holds them all.
    /// </summary>
    CharacterGroup,
}

/// <summary>
/// One spawn place of the island.
/// </summary>
/// <param name="Kind">Which array it lives in.</param>
/// <param name="Index">Index in that array of the stock data; -1 for a place the project added.</param>
/// <param name="Transform">Where (world space, centimetres).</param>
/// <param name="Group">
/// Vehicles: the level spawn group (<c>City</c>, <c>Default</c>, <c>Boat</c>, <c>CivilianAirplane</c> …); characters:
/// <c>Exterior</c> or <c>Interior</c>; zones: the zone settings (<c>MTZ_Settlement_Large</c>); animals: the biome.
/// </param>
/// <param name="SizeX">Zones: half the length along X (cm); animals: the radius; 0 otherwise.</param>
/// <param name="SizeY">Zones: half the width along Y (cm); animals: the radius; 0 otherwise.</param>
public sealed record SpawnPlace(SpawnPlaceKind Kind, int Index, FTransform Transform, string Group, float SizeX = 0f, float SizeY = 0f)
{
    /// <summary>The actor name of a place the project added (a copy or a new place); null for a stock place.</summary>
    public string? Name { get; init; }

    /// <summary>A <see cref="SpawnPlaceKind.CharacterGroup"/>'s posts (world space, centimetres; the first is where it stands); empty for other kinds.</summary>
    public IReadOnlyList<FVector> Points { get; init; } = [];

    /// <summary>The name of the place's actor in the map's document (<c>VehicleSpawn_00123</c>, <c>NewVehicleSpawn_0001</c>).</summary>
    public string ActorName => Name ?? SpawnPlaces.ActorName(Kind, Index);
}

/// <summary>
/// The island's spawn places: SCUM keeps them in one asset, <c>The_Island_LevelStaticData</c>, not in the levels. 3,491
/// world vehicle spawn points (with their level spawn group), 25,802 zombie / NPC points, tagged points, 294 threat zones
/// and 474 hunting areas. <see cref="ToLevelData"/> turns them into a level the map editor shows, picks and edits like any
/// other: every place is an actor (<see cref="ActorName"/>) with a root component at the place; a zone's or hunting area's
/// size is the root's scale (metres). <see cref="SpawnPlacesEditor"/> writes the edits back.
/// </summary>
public static class SpawnPlaces
{
    /// <summary>The asset that holds the spawn places, and the "level" path of their document.</summary>
    public const string StaticDataPath = "/Game/ConZ_Files/Maps/The_Island/The_Island_LevelStaticData";

    /// <summary>The most posts a new group of zombie / NPC points takes (the island's own groups hold 2 to 19).</summary>
    public const int MaxGroupPosts = 24;

    /// <summary>Synthetic class names of the place actors (kept by <see cref="KindOfClass"/>).</summary>
    public static string ClassOf(SpawnPlaceKind kind) => kind switch
    {
        SpawnPlaceKind.Vehicle => "VehicleSpawnPlace",
        SpawnPlaceKind.Character => "CharacterSpawnPlace",
        SpawnPlaceKind.TaggedCharacter => "TaggedCharacterSpawnPlace",
        SpawnPlaceKind.Zone => "ThreatZonePlace",
        SpawnPlaceKind.CharacterGroup => "CharacterGroupPlace",
        _ => "HuntingAreaPlace",
    };

    /// <summary>The kind of a place actor class, or null for any other class.</summary>
    public static SpawnPlaceKind? KindOfClass(string className) => className switch
    {
        "VehicleSpawnPlace" => SpawnPlaceKind.Vehicle,
        "CharacterSpawnPlace" => SpawnPlaceKind.Character,
        "TaggedCharacterSpawnPlace" => SpawnPlaceKind.TaggedCharacter,
        "ThreatZonePlace" => SpawnPlaceKind.Zone,
        "HuntingAreaPlace" => SpawnPlaceKind.Animal,
        "CharacterGroupPlace" => SpawnPlaceKind.CharacterGroup,
        _ => null,
    };

    /// <summary>The array of the static data that holds <paramref name="kind"/>.</summary>
    public static string ArrayOf(SpawnPlaceKind kind) => kind switch
    {
        SpawnPlaceKind.Vehicle => "VehicleSpawnPoints",
        SpawnPlaceKind.Character => "EncounterLegacyCharacterSpawnPoints",
        SpawnPlaceKind.TaggedCharacter => "EncounterTaggedCharacterSpawnPoints",
        SpawnPlaceKind.Zone => "StaticZoneDescriptions",
        SpawnPlaceKind.CharacterGroup => "EncounterGroupedCharacterSpawnPoints",
        _ => "HuntingBiomes",
    };

    /// <summary>True for the static data path (its "level" is written by <see cref="SpawnPlacesEditor"/>).</summary>
    public static bool IsStaticData(string packagePath) => string.Equals(packagePath, StaticDataPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The actor name of a stock place.</summary>
    public static string ActorName(SpawnPlaceKind kind, int index) => kind switch
    {
        SpawnPlaceKind.Vehicle => "VehicleSpawn_",
        SpawnPlaceKind.Character => "ZombieSpawn_",
        SpawnPlaceKind.TaggedCharacter => "TaggedSpawn_",
        SpawnPlaceKind.Zone => "ThreatZone_",
        SpawnPlaceKind.CharacterGroup => "NpcGroup_",
        _ => "HuntingArea_",
    } + index.ToString("00000", CultureInfo.InvariantCulture);

    /// <summary>
    /// A free name for a new place of <paramref name="kind"/> (<c>NewVehicleSpawn_0001</c>): not in <paramref name="taken"/>
    /// and never one <see cref="ParseName"/> reads as a stock place.
    /// </summary>
    public static string NewName(SpawnPlaceKind kind, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var prefix = "New" + ActorName(kind, 0)[..^5];
        for (var n = 1; ; n++)
        {
            var name = prefix + n.ToString("0000", CultureInfo.InvariantCulture);
            if (!used.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>The stock place an actor name stands for (kind and index), or null for an added copy or another name.</summary>
    public static (SpawnPlaceKind Kind, int Index)? ParseName(string actorName)
    {
        ArgumentNullException.ThrowIfNull(actorName);
        foreach (var kind in Enum.GetValues<SpawnPlaceKind>())
        {
            var prefix = ActorName(kind, 0)[..^5];
            if (actorName.Length == prefix.Length + 5 && actorName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(actorName.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                return (kind, index);
            }
        }

        return null;
    }

    /// <summary>Reads every place of the static data package (the export named <c>The_Island_LevelStaticData</c>).</summary>
    public static IReadOnlyList<SpawnPlace> Read(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var block = package.ReadProperties(StaticDataExport(package));
        var places = new List<SpawnPlace>();
        foreach (var kind in Enum.GetValues<SpawnPlaceKind>())
        {
            if (block.Find(ArrayOf(kind))?.Value is not ArrayValue array)
            {
                continue;
            }

            for (var i = 0; i < array.Items.Count; i++)
            {
                if (array.Items[i] is StructValue item && Describe(kind, i, item) is { } place)
                {
                    places.Add(place);
                }
            }
        }

        return places;
    }

    /// <summary>The export holding the data (class <c>LevelStaticData</c>).</summary>
    internal static int StaticDataExport(CookedPackage package)
    {
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (package.GetExportClassName(i) == "LevelStaticData")
            {
                return i;
            }
        }

        throw new InvalidDataException($"{package.BasePath} has no LevelStaticData export.");
    }

    /// <summary>The place one array item describes, or null when it lacks a transform.</summary>
    internal static SpawnPlace? Describe(SpawnPlaceKind kind, int index, StructValue item)
    {
        switch (kind)
        {
            case SpawnPlaceKind.Vehicle:
                return TransformOf(item, "SpawnTransform") is { } vt
                    ? new SpawnPlace(kind, index, vt, TagLeaf(Member(item, "VehicleLevelSpawnGroup")?.Value is StructValue tag ? NameOf(tag, "TagName") : null))
                    : null;
            case SpawnPlaceKind.Character or SpawnPlaceKind.TaggedCharacter:
                return TransformOf(item, "Transform") is { } ct
                    ? new SpawnPlace(kind, index, ct, Member(item, "Type")?.Value is EnumValue e ? e.Value[(e.Value.LastIndexOf(':') + 1)..] : string.Empty)
                    : null;
            case SpawnPlaceKind.Zone:
                return TransformOf(item, "_transform") is { } zt
                    ? new SpawnPlace(kind, index, zt, ImportName(Member(item, "_zoneData")?.Value), Float(item, "_semiXAxisSize"), Float(item, "_semiYAxisSize"))
                    : null;
            case SpawnPlaceKind.CharacterGroup:
                var posts = GroupPosts(item);
                return posts.Count > 0
                    ? new SpawnPlace(kind, index, posts[0], Member(item, "Type")?.Value is EnumValue g ? g.Value[(g.Value.LastIndexOf(':') + 1)..] : string.Empty) { Points = posts.Select(p => p.Translation).ToList() }
                    : null;
            default:
                var biome = Member(item, "PopulationParameters")?.Value is StructValue p ? ImportName(Member(p, "BiomeData")?.Value) : string.Empty;
                var radius = Float(item, "Radius");
                return TransformOf(item, "Transform") is { } at ? new SpawnPlace(kind, index, at, biome, radius, radius) : null;
        }
    }

    /// <summary>
    /// The places as a level for the map editor: one actor per place (class <see cref="ClassOf"/>, class path = its group)
    /// with a root component at the place. <paramref name="keep"/> chooses which places (null = all).
    /// </summary>
    public static LevelData ToLevelData(IEnumerable<SpawnPlace> places, Func<SpawnPlace, bool>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(places);
        var exports = new List<LevelExportData>();
        var actors = new List<int>();
        foreach (var place in places.Where(p => keep?.Invoke(p) ?? true))
        {
            var actor = exports.Count;
            var root = actor + 1;
            var t = place.Transform;
            // A point shows the scale it stores, so a move writes back what was there (the export writes a point's scale).
            var scale = place.Kind is SpawnPlaceKind.Zone or SpawnPlaceKind.Animal
                ? new FVector(MathF.Max(place.SizeX, 1f) / 100f, MathF.Max(place.SizeY, 1f) / 100f, 1f)
                : t.Scale3D;
            exports.Add(new LevelExportData
            {
                Index = actor,
                Name = place.ActorName,
                ClassName = ClassOf(place.Kind),
                ClassPath = place.Group,
                IsLoaded = true,
                RootComponent = root,
            });
            exports.Add(new LevelExportData
            {
                Index = root,
                Name = "Place",
                ClassName = "SceneComponent",
                ClassPath = "/Script/Engine.SceneComponent",
                OuterIndex = actor,
                IsLoaded = true,
                IsComponent = true,
                IsSceneComponent = true,
                RelativeLocation = t.Translation,
                RelativeRotation = t.Rotation.Rotator(),
                RelativeScale3D = scale,
            });
            actors.Add(actor);
        }

        return new LevelData { PackagePath = StaticDataPath, Exports = exports, ActorIndices = actors };
    }

    /// <summary>The places of the game files in <paramref name="catalog"/> (none when the static data is not there).</summary>
    public static IReadOnlyList<SpawnPlace> ReadFrom(Assets.Catalog.AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.TryGetPackageFile(StaticDataPath, out _) ? Read(Modding.Catalog.ModdableAssets.ReadPackage(catalog, StaticDataPath)) : [];
    }

    /// <summary>The rectangle (UE cm) around the actors of <paramref name="documents"/>, grown by <paramref name="margin"/>; null without actors.</summary>
    public static (float MinX, float MinY, float MaxX, float MaxY)? AreaAround(IEnumerable<LevelDocument> documents, float margin)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var spots = documents.SelectMany(d => d.Actors).Select(a => a.WorldTransform.Translation).Where(t => t != default).ToList();
        return spots.Count == 0 ? null
            : (spots.Min(s => s.X) - margin, spots.Min(s => s.Y) - margin, spots.Max(s => s.X) + margin, spots.Max(s => s.Y) + margin);
    }

    /// <summary>
    /// The places over <paramref name="areas"/> (a zone or hunting area when any of it is there) as a level document; null
    /// when none are.
    /// </summary>
    public static LevelDocument? Over(IEnumerable<SpawnPlace> places, IReadOnlyCollection<(float MinX, float MinY, float MaxX, float MaxY)> areas)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(areas);
        bool Inside(SpawnPlace p)
        {
            var at = p.Transform.Translation;
            var reach = MathF.Max(p.SizeX, p.SizeY);
            return areas.Any(a => at.X + reach >= a.MinX && at.X - reach <= a.MaxX && at.Y + reach >= a.MinY && at.Y - reach <= a.MaxY);
        }

        var data = ToLevelData(places, Inside);
        return data.ActorIndices.Count == 0 ? null : LevelDocument.FromData(data);
    }

    /// <summary>A zone's or hunting area's size from its actor's root scale (metres per axis, as <see cref="ToLevelData"/> sets it).</summary>
    public static (float SizeX, float SizeY) SizeOf(FVector scale) => (MathF.Abs(scale.X) * 100f, MathF.Abs(scale.Y) * 100f);

    /// <summary>
    /// The places as the project has them (what the export writes, for the Spawns page and the map): <paramref name="stock"/>
    /// without the deleted ones, moved, resized and with what they spawn now, then the copies and the new places in the
    /// order the export appends them (<see cref="SpawnPlace.Index"/> -1, <see cref="SpawnPlace.Name"/> their actor name).
    /// </summary>
    public static IReadOnlyList<SpawnPlace> WithEdits(IReadOnlyList<SpawnPlace> stock, Editing.EditState state)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(state);
        var result = new List<SpawnPlace>(stock.Count + 16);
        var byIndex = new Dictionary<(SpawnPlaceKind, int), SpawnPlace>();
        foreach (var place in stock)
        {
            byIndex[(place.Kind, place.Index)] = place;
            var actor = new Editing.ActorRef(StaticDataPath, place.ActorName);
            if (state.IsDeleted(actor))
            {
                continue;
            }

            var edited = state.GetTransformOverride(actor) is { } value
                ? At(place.Kind, place.Group, value, place.Transform.Scale3D) with { Index = place.Index, Points = MovePosts(place.Points, place.Transform, value.ToTransform()) }
                : place;
            result.Add(SpawnChoices.PropertyOf(place.Kind) is { } property && state.GetSpawnChoice(actor, property) is { } choice
                ? edited with { Group = SettingName(choice) }
                : edited);
        }

        var copies = new List<SpawnPlace>();
        var created = new List<SpawnPlace>();
        foreach (var (actor, root, transform, choice) in SpawnPlacesEditor.AddedPlaces(state))
        {
            switch (root)
            {
                case Editing.DuplicateActorOp copy when ParseName(copy.Source.Actor) is { } source && byIndex.TryGetValue(source, out var original):
                    // As the export: the source's own choice is carried while the source is kept.
                    var stockActor = new Editing.ActorRef(StaticDataPath, original.ActorName);
                    var group = choice ?? (SpawnChoices.PropertyOf(source.Kind) is { } property && !state.IsDeleted(stockActor)
                        ? state.GetSpawnChoice(stockActor, property)
                        : null);
                    copies.Add(At(source.Kind, group ?? original.Group, transform, original.Transform.Scale3D) with { Name = actor.Actor, Points = MovePosts(original.Points, original.Transform, transform.ToTransform()) });
                    break;
                case Editing.AddSpawnPlaceOp place:
                    created.Add(At(place.Kind, choice ?? place.Setting, transform) with { Name = actor.Actor, Points = place.PostsAt(transform) });
                    break;
            }
        }

        // Each array as the export writes it: its stock places, then the copies, then the new places.
        return Enum.GetValues<SpawnPlaceKind>()
            .SelectMany(k => result.Where(p => p.Kind == k).Concat(copies.Where(p => p.Kind == k)).Concat(created.Where(p => p.Kind == k)))
            .ToList();
    }

    /// <summary>
    /// A place of <paramref name="kind"/> spawning <paramref name="setting"/> at <paramref name="value"/> as the map holds it
    /// (a zone's or area's size in the scale, metres); <paramref name="storedScale"/> is the scale a zone or area stores.
    /// </summary>
    public static SpawnPlace At(SpawnPlaceKind kind, string setting, Model.TransformValue value, FVector? storedScale = null)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (kind is not (SpawnPlaceKind.Zone or SpawnPlaceKind.Animal))
        {
            return new SpawnPlace(kind, -1, value.ToTransform(), SettingName(setting));
        }

        var (sizeX, sizeY) = SizeOf(value.Scale);
        var transform = new FTransform(value.Rotation, value.Location, storedScale ?? FVector.One);
        return kind == SpawnPlaceKind.Animal
            ? new SpawnPlace(kind, -1, transform, SettingName(setting), MathF.Max(sizeX, sizeY), MathF.Max(sizeX, sizeY))
            : new SpawnPlace(kind, -1, transform, SettingName(setting), sizeX, sizeY);
    }

    /// <summary>
    /// A group's posts (<paramref name="points"/>, world) after the group moved from where it stood (<paramref name="from"/>,
    /// its first post) to <paramref name="to"/>: each keeps its place relative to the first, turned with it (scale ignored).
    /// </summary>
    public static IReadOnlyList<FVector> MovePosts(IReadOnlyList<FVector> points, FTransform from, FTransform to)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points.Select(p => to.TransformPositionNoScale(from.Rotation.Inverse().RotateVector(p - from.Translation))).ToList();
    }

    /// <summary>The asset name a setting stands for: <c>MTZ_Village</c> for itself or for its package path (<c>/Game/…/MTZ_Village</c>, <c>….MTZ_Village</c>).</summary>
    public static string SettingName(string setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var leaf = setting[(setting.LastIndexOf('/') + 1)..];
        return leaf[(leaf.IndexOf('.') + 1)..];
    }

    /// <summary>The posts of a group of zombie / NPC points (its <c>SpawnTransforms</c>), in stored order.</summary>
    internal static List<FTransform> GroupPosts(StructValue item) =>
        Member(item, "SpawnTransforms")?.Value is ArrayValue posts ? posts.Items.OfType<StructValue>().Select(ToTransform).ToList() : [];

    private static FTransform? TransformOf(StructValue item, string name) => Member(item, name)?.Value is StructValue t ? ToTransform(t) : null;

    private static FTransform ToTransform(StructValue t)
    {
        // Normalized: the island data stores some turns as not-quite-unit quaternions, and their angles came out a little off,
        // so every copy of a tilted zombie place stood a bit more crooked (Discord report).
        var q = Member(t, "Rotation")?.Value is QuatValue r ? new FQuat(r.X, r.Y, r.Z, r.W).GetNormalized() : FQuat.Identity;
        var p = Member(t, "Translation")?.Value is VectorValue v ? new FVector(v.X, v.Y, v.Z) : FVector.Zero;
        var s = Member(t, "Scale3D")?.Value is VectorValue sc ? new FVector(sc.X, sc.Y, sc.Z) : FVector.One;
        return new FTransform(q, p, s);
    }

    internal static PropertyTag? Member(StructValue value, string name) => value.Properties.FirstOrDefault(p => p.Name == name);

    private static float Float(StructValue item, string name) => Member(item, name)?.Value is FloatValue f ? f.Value : 0f;

    private static string? NameOf(StructValue value, string name) => Member(value, name)?.Value is NameValue n ? n.Value : null;

    private static string TagLeaf(string? tag) => tag is null ? string.Empty : tag.StartsWith("VehicleLevelSpawnGroup.", StringComparison.Ordinal) ? tag["VehicleLevelSpawnGroup.".Length..] : tag;

    private static string ImportName(PropertyValue? value) => value is ObjectValue o && o.Reference.Length > 0 ? o.Reference[(o.Reference.IndexOf(':') + 1)..] : string.Empty;
}
