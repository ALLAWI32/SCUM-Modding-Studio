using System.Text.Json.Serialization;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Level.Editing;

/// <summary>
/// What the project changed on one actor besides where it stands (see <see cref="EditState.EditsOf(ActorRef)"/>), each
/// with its value before (<c>Base</c>) and now (<c>Current</c>): what a copy of it must carry along. Duplicate, Paste,
/// Extend, prefabs and the MCP place the copy with its create op and replay these on it (<see cref="OpsOn"/>), so a copy
/// is bent, scaled, re-meshed, looted and pinned as its source is (Discord, seviscache: "Duplicate gives a copy without
/// that change"). Plain JSON: a prefab file keeps it with each part.
/// </summary>
public sealed record ActorEdits
{
    /// <summary>Nothing changed.</summary>
    public static ActorEdits None { get; } = new();

    /// <summary>The actor's bend, handles, ends and legs (see <see cref="BendActorOp"/>), or null when straight.</summary>
    public BendValue? Bend { get; init; }

    /// <summary>Moved, turned or scaled parts (never the root: the copy's create op places that).</summary>
    public IReadOnlyList<PartEdit> Parts { get; init; } = [];

    /// <summary>Components (or the root) drawing another mesh (see <see cref="ReplaceMeshOp"/>).</summary>
    public IReadOnlyList<MeshEdit> Meshes { get; init; } = [];

    /// <summary>Spline pieces pushed sideways (see <see cref="SwaySegmentOp"/>).</summary>
    public IReadOnlyList<SwayEdit> Sways { get; init; } = [];

    /// <summary>Spawner point arrays moved, added to or cut (see <see cref="SetSpawnPointsOp"/>).</summary>
    public IReadOnlyList<PointsEdit> Points { get; init; } = [];

    /// <summary>Loot of mesh components (see <see cref="SetLootOp"/>).</summary>
    public IReadOnlyList<LootEdit> Loot { get; init; } = [];

    /// <summary>"Clear grass under it" as set (see <see cref="SetClearGrassOp"/>), or null for the default.</summary>
    public bool? ClearGrass { get; init; }

    /// <summary>"No loot inside" (see <see cref="SetNoLootOp"/>): a copy of a placed object without loot has none either.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NoLoot { get; init; }

    /// <summary>Instances of its instanced components deleted, moved or added.</summary>
    public IReadOnlyList<InstanceEdit> Instances { get; init; } = [];

    /// <summary>True when nothing is changed.</summary>
    [JsonIgnore]
    public bool IsEmpty => Bend is null && Parts.Count == 0 && Meshes.Count == 0 && Sways.Count == 0 && Points.Count == 0 && Loot.Count == 0
                           && ClearGrass is null && !NoLoot && Instances.Count == 0;

    /// <summary>
    /// The edits that give <paramref name="created"/> (a copy, placed in the same batch before them) these changes. The
    /// old values are the source's, so the copy's undo and journal replay are exact. With
    /// <paramref name="rootMeshAndLootBaked"/> the copy is an <see cref="AddStaticMeshActorOp"/>: its mesh and loot are
    /// already the source's now and it has no other component, so only its bend and grass setting are left to give it.
    /// </summary>
    public IReadOnlyList<EditOp> OpsOn(ActorRef created, bool rootMeshAndLootBaked)
    {
        ArgumentNullException.ThrowIfNull(created);
        var ops = new List<EditOp>();
        if (Bend is { IsStraight: false } bend)
        {
            ops.Add(new BendActorOp(created, 0f, bend.Degrees, NewSway1: bend.Sway1, NewSway2: bend.Sway2, NewStart: bend.Start, NewEnd: bend.End, NewLegs: bend.Legs));
        }

        if (ClearGrass is { } grass)
        {
            ops.Add(new SetClearGrassOp(created, null, grass));
        }

        if (NoLoot)
        {
            ops.Add(new SetNoLootOp(created, false, true));
        }

        if (rootMeshAndLootBaked)
        {
            return ops;
        }

        ops.AddRange(Parts.Select(p => new SetTransformOp(created, p.Base, p.Current, p.Component)));
        ops.AddRange(Meshes.Select(m => new ReplaceMeshOp(created, m.Component, m.Base, m.Current)));
        ops.AddRange(Sways.Select(s => new SwaySegmentOp(created, s.Component, 0f, 0f, s.Shape.Sway1, s.Shape.Sway2, NewStart: s.Shape.Start, NewEnd: s.Shape.End)));
        ops.AddRange(Points.Select(p => new SetSpawnPointsOp(created, p.Component, p.Array, p.Base, p.Current)));
        ops.AddRange(Loot.Select(l => new SetLootOp(created, l.Component, l.Base, l.Current)));

        // An added instance first, then its move, then its delete: each is valid once the one before it is applied.
        InstanceRef At(InstanceEdit i) => new(created.Level, created.Actor, i.Component, i.Index);
        ops.AddRange(Instances.Where(i => i.Added is not null).Select(i => new AddInstanceOp(At(i), i.Added!.Value)));
        ops.AddRange(Instances.Where(i => i.Base is not null && i.Current is not null).Select(i => new SetInstanceTransformOp(At(i), i.Base!.Value, i.Current!.Value)));
        ops.AddRange(Instances.Where(i => i.Deleted).Select(i => new DeleteInstanceOp(At(i))));
        return ops;
    }
}

/// <summary>A part moved, turned or scaled (relative to its parent).</summary>
/// <param name="Component">The component's name.</param>
/// <param name="Base">Its transform before the project's edits.</param>
/// <param name="Current">Its transform now.</param>
public sealed record PartEdit(string Component, TransformValue Base, TransformValue Current);

/// <summary>A component drawing another mesh.</summary>
/// <param name="Component">The component's name, or null for the root.</param>
/// <param name="Base">The mesh it draws in the level.</param>
/// <param name="Current">The mesh it draws now.</param>
public sealed record MeshEdit(string? Component, string Base, string Current);

/// <summary>A spline piece's handles and ends.</summary>
/// <param name="Component">The SplineMeshComponent's name.</param>
/// <param name="Shape">Its shape now (as made = straight).</param>
public sealed record SwayEdit(string Component, BendValue Shape);

/// <summary>A spawner's point array.</summary>
/// <param name="Component">The component storing it, or null for the actor's own.</param>
/// <param name="Array">The array property.</param>
/// <param name="Base">The points the level stores.</param>
/// <param name="Current">The points now.</param>
public sealed record PointsEdit(string? Component, string Array, IReadOnlyList<SpawnPoint> Base, IReadOnlyList<SpawnPoint> Current);

/// <summary>A mesh component's loot.</summary>
/// <param name="Component">The component, or null for the root.</param>
/// <param name="Base">Its loot in the level.</param>
/// <param name="Current">Its loot now.</param>
public sealed record LootEdit(string? Component, LootSetting Base, LootSetting Current);

/// <summary>One instance of an instanced component: deleted, moved (<see cref="Base"/> to <see cref="Current"/>) or added (<see cref="Added"/>).</summary>
/// <param name="Component">The instanced component.</param>
/// <param name="Index">The instance's index (an added one's is past the stored ones).</param>
public sealed record InstanceEdit(string Component, int Index)
{
    /// <summary>True when it is deleted.</summary>
    public bool Deleted { get; init; }

    /// <summary>Where a moved instance stood before (component space).</summary>
    public TransformValue? Base { get; init; }

    /// <summary>Where a moved instance stands now.</summary>
    public TransformValue? Current { get; init; }

    /// <summary>Where an added instance was added.</summary>
    public TransformValue? Added { get; init; }
}
