using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Tuning;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Level.Spawns;

/// <summary>One group of NPC spawn posts and the edit that goes before it (a sentry robot's removal), if any.</summary>
/// <param name="Posts">Where the posts stand (world space, centimetres); the first one is where the group stands.</param>
/// <param name="Before">An edit recorded just before the group is added; null = none.</param>
public sealed record NpcPostGroup(IReadOnlyList<FVector> Posts, EditOp? Before = null);

/// <summary>
/// "Place NPCs": the plan behind both the Spawns page's NPC wizard and <see cref="NpcGuards"/> (a robot out, an NPC area in).
/// The game spawns its armed NPCs from threat zones, so the plan is the same everywhere: one group of spawn posts per
/// place, an area over the posts that has its own copy of the zone settings of the chosen NPC kind (every other encounter
/// weighs 0, one encounter at a time: the game's NPC-only areas), laid as a sub-zone of the smallest stock threat zone that
/// holds every post, else as a zone of its own. The user chooses where and who; no weight list is ever shown. What the
/// game does with it (1 to 3 NPCs while a player walks in, then a rest of 20 to 30 minutes) is not ours to change here.
/// </summary>
public static class NpcPlacement
{
    /// <summary>How far from the clicked point a wizard's posts stand (cm): 15 m.</summary>
    public const float DefaultRadius = 1500f;

    /// <summary>The most posts a group holds (the same as <see cref="SpawnPlaces.MaxGroupPosts"/>).</summary>
    public static int MaxPosts => SpawnPlaces.MaxGroupPosts;

    /// <summary>
    /// <paramref name="count"/> posts on a circle of <paramref name="radius"/> around <paramref name="center"/> (at the same
    /// height), spread evenly; one post stands on the centre.
    /// </summary>
    /// <param name="center">The point the user picked.</param>
    /// <param name="count">How many posts (1 to <see cref="MaxPosts"/>).</param>
    /// <param name="radius">The circle's radius (cm).</param>
    public static IReadOnlyList<FVector> PostsAround(FVector center, int count, float radius = DefaultRadius)
    {
        count = Math.Clamp(count, 1, MaxPosts);
        if (count == 1 || radius <= 0f)
        {
            return [center];
        }

        var posts = new List<FVector>(count);
        for (var k = 0; k < count; k++)
        {
            var angle = 2d * Math.PI * k / count;
            posts.Add(new FVector(center.X + (float)(Math.Cos(angle) * radius), center.Y + (float)(Math.Sin(angle) * radius), center.Z));
        }

        return posts;
    }

    /// <summary>
    /// The edits of one placement (one undo step): each group's <see cref="NpcPostGroup.Before"/> edit and the group of posts,
    /// the area's own settings (<paramref name="zoneCopy"/>, a copy of <paramref name="type"/>'s stock zone settings: every
    /// other encounter of <paramref name="zoneRows"/> weighs 0, and <see cref="NpcGuards.NpcOnlyValues"/> as the game's NPC-only
    /// settings have them), and the area over all the posts. The area is written the way the game's data has an NPC-only area
    /// inside a place: a sub-zone (<see cref="AddSubZoneOp"/>, priority <see cref="NpcGuards.SubZonePriority"/>) of the
    /// smallest stock threat zone of <paramref name="zones"/> that holds every post (r39: a second zone laid inside the
    /// place's own zone, which no zone of the island does, gave igor8802 no NPCs). Only where no stock zone holds every post
    /// is it a threat zone of its own.
    /// </summary>
    /// <param name="groups">The groups of posts, each with its edit to record first (null = none).</param>
    /// <param name="type">The NPCs to spawn.</param>
    /// <param name="zoneCopy">The copy of <see cref="NpcGuardType.ZonePackage"/> (<see cref="ZoneCopies.Plan"/>).</param>
    /// <param name="zoneRows">The encounter rows of the stock zone settings (<see cref="SpawnChoices.ForZone"/>, its level 0 list).</param>
    /// <param name="taken">Actor names the island data's document already has (the project's added places).</param>
    /// <param name="zones">The island's places as the project has them (<see cref="SpawnPlaces.WithEdits"/>); null or none = no stock zone to hold the area.</param>
    /// <param name="stockValues">The values the stock zone settings store, by property path (<see cref="NpcGuards.NpcOnlyValues"/>' old values); null = none stored.</param>
    /// <param name="tunes">Value edits recorded right after <paramref name="zoneCopy"/> (an own copy's other presets weighing 0, <see cref="NpcCopies"/>); null = none.</param>
    /// <exception cref="ArgumentException">No group, a group without posts, or the zone settings have no row for the encounter.</exception>
    public static IReadOnlyList<EditOp> Plan(IReadOnlyList<NpcPostGroup> groups, NpcGuardType type, CloneAssetOp zoneCopy, SpawnWeightList zoneRows, IEnumerable<string> taken,
        IReadOnlyList<SpawnPlace>? zones = null, IReadOnlyDictionary<string, string>? stockValues = null, IReadOnlyList<EditOp>? tunes = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(zoneCopy);
        ArgumentNullException.ThrowIfNull(zoneRows);
        ArgumentNullException.ThrowIfNull(taken);
        if (groups.Count == 0 || groups.Any(g => g.Posts.Count == 0))
        {
            throw new ArgumentException("No posts to place.", nameof(groups));
        }

        var own = zoneRows.Entries.Where(e => e.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase) && e.Path is not null).ToList();
        if (own.Count == 0 || !zoneRows.IsEditable)
        {
            throw new ArgumentException($"{zoneRows.Name} has no stored row for {type.Name}.", nameof(zoneRows));
        }

        var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        string Name(SpawnPlaceKind kind)
        {
            var name = SpawnPlaces.NewName(kind, names);
            names.Add(name);
            return name;
        }

        var ops = new List<EditOp>();
        var all = new List<FVector>();
        foreach (var group in groups)
        {
            if (group.Before is not null)
            {
                ops.Add(group.Before);
            }

            var posts = group.Posts;
            all.AddRange(posts);
            ops.Add(new AddSpawnPlaceOp(Name(SpawnPlaceKind.CharacterGroup), SpawnPlaceKind.CharacterGroup, NpcGuards.PostType,
                new TransformValue(posts[0], FRotator.Zero, FVector.One), Points: posts.Select(p => p - posts[0]).ToList()));
        }

        // The area's own settings: only the chosen NPC encounter keeps a weight. An own copy of the NPC (NpcCopies) goes in the
        // same clone; its value edits follow it, as they name packages the clone creates.
        ops.Add(zoneCopy);
        ops.AddRange(tunes ?? []);
        var export = zoneCopy.NewPrimary[(zoneCopy.NewPrimary.LastIndexOf('/') + 1)..];
        foreach (var row in zoneRows.Entries.Where(e => e.Path is not null))
        {
            var mine = own.Contains(row);
            var weight = mine ? (row.Weight > 0f ? row.Weight : 100f) : 0f;
            if (weight != row.Weight)
            {
                ops.Add(new SetAssetValueOp(zoneCopy.NewPrimary, export, row.Path!, nameof(TunableKind.Float), TunableValue.Format(row.Weight), TunableValue.Format(weight)));
            }
        }

        // As the game's NPC-only settings: one encounter at a time (a value the stock settings do not store is added).
        foreach (var (path, kind, value) in NpcGuards.NpcOnlyValues)
        {
            var old = stockValues?.GetValueOrDefault(path) ?? string.Empty;
            if (!TunableValue.AreEqual(kind, old, value))
            {
                ops.Add(new SetAssetValueOp(zoneCopy.NewPrimary, export, path, kind.ToString(), old, value));
            }
        }

        var (min, max) = (new FVector(all.Min(p => p.X), all.Min(p => p.Y), all.Min(p => p.Z)), new FVector(all.Max(p => p.X), all.Max(p => p.Y), all.Max(p => p.Z)));
        var center = (min + max) * 0.5f;
        var margin = NpcGuards.ZoneMargin;
        var smallest = NpcGuards.MinZoneHalfSize;

        // The area as a sub-zone of the smallest stock zone that holds every post: the posts' box and a margin.
        if ((zones ?? []).Where(z => z is { Kind: SpawnPlaceKind.Zone, Index: >= 0, Name: null } && all.All(p => SpawnChoices.ZonesAt([z], p).Count > 0))
                .OrderBy(z => z.SizeX * z.SizeY).FirstOrDefault() is { } parent)
        {
            var reach = new FVector(MathF.Max(((max.X - min.X) * 0.5f) + margin, smallest), MathF.Max(((max.Y - min.Y) * 0.5f) + margin, smallest),
                ((max.Z - min.Z) * 0.5f) + margin);
            ops.Add(new AddSubZoneOp(new ActorRef(SpawnPlaces.StaticDataPath, parent.ActorName), new SubZone(center - reach, center + reach, zoneCopy.NewPrimary, NpcGuards.SubZonePriority)));
            return ops;
        }

        // No stock zone there: a zone of its own. A zone is an ellipse: through the corners of the posts' box (its half sizes
        // times the square root of 2), and a margin.
        var half = new FVector(MathF.Max(((max.X - min.X) * 0.5f * MathF.Sqrt(2f)) + margin, smallest), MathF.Max(((max.Y - min.Y) * 0.5f * MathF.Sqrt(2f)) + margin, smallest), 1f);
        ops.Add(new AddSpawnPlaceOp(Name(SpawnPlaceKind.Zone), SpawnPlaceKind.Zone, zoneCopy.NewPrimary,
            new TransformValue(center, FRotator.Zero, new FVector(half.X / 100f, half.Y / 100f, 1f)))); // a zone's size is its scale in metres
        return ops;
    }
}
