using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScumStudio.Level.Model;
using ScumStudio.Level.Serialization;

namespace ScumStudio.Level.Editing;

/// <summary>
/// One edit of the world, stored as pure data in the project journal (JSONL, discriminated by <c>"op"</c>). Operations
/// address actors by (level package, actor name) so they can be replayed onto pristine client and server packages and
/// onto a new game build. Every operation has an exact <see cref="Inverse"/> (<c>op.Inverse().Inverse() == op</c>), which
/// the editor applies to its in-memory state on undo.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(DeleteActorOp), "deleteActor")]
[JsonDerivedType(typeof(RestoreActorOp), "restoreActor")]
[JsonDerivedType(typeof(DeleteAllOfKindOp), "deleteAllOfKind")]
[JsonDerivedType(typeof(RestoreAllOfKindOp), "restoreAllOfKind")]
[JsonDerivedType(typeof(DuplicateActorOp), "duplicateActor")]
[JsonDerivedType(typeof(SetTransformOp), "setTransform")]
[JsonDerivedType(typeof(SetInstanceTransformOp), "setInstanceTransform")]
[JsonDerivedType(typeof(DeleteInstanceOp), "deleteInstance")]
[JsonDerivedType(typeof(RestoreInstanceOp), "restoreInstance")]
[JsonDerivedType(typeof(AddStaticMeshActorOp), "addStaticMeshActor")]
[JsonDerivedType(typeof(AddBlueprintActorOp), "addBlueprintActor")]
[JsonDerivedType(typeof(RemoveAddedActorOp), "removeAddedActor")]
[JsonDerivedType(typeof(CloneAssetOp), "cloneAsset")]
[JsonDerivedType(typeof(RemoveAssetCloneOp), "removeAssetClone")]
[JsonDerivedType(typeof(SetAssetValueOp), "setAssetValue")]
[JsonDerivedType(typeof(ReplaceAssetOp), "replaceAsset")]
[JsonDerivedType(typeof(BatchOp), "batch")]
[JsonDerivedType(typeof(BendActorOp), "bendActor")]
[JsonDerivedType(typeof(SwaySegmentOp), "swaySegment")]
public abstract record EditOp
{
    /// <summary>The operation that exactly undoes this one.</summary>
    public abstract EditOp Inverse();

    /// <summary>Single-line JSON with the <c>"op"</c> discriminator first (the journal format).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, LevelJson.Compact);

    /// <summary>Parses an operation written by <see cref="ToJson"/>.</summary>
    /// <exception cref="JsonException">Invalid JSON, a missing or unknown <c>"op"</c>, or missing members.</exception>
    public static EditOp FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<EditOp>(json, LevelJson.Compact) ?? throw new JsonException("Empty edit operation.");
        }
        catch (NotSupportedException ex)
        {
            // System.Text.Json reports a missing type discriminator this way.
            throw new JsonException(ex.Message, ex);
        }
    }

    /// <summary>One-line human-readable summary for history lists.</summary>
    public abstract string Describe();

    /// <summary>Level packages whose content this operation changes (the export set).</summary>
    public abstract IReadOnlyList<string> GetTouchedLevels();

    /// <summary>The main actor the operation targets (for history lists), or null for bulk operations.</summary>
    public abstract ActorRef? GetPrimaryTarget();

    /// <summary>Non-level packages (vehicles, items) whose content this operation changes or creates.</summary>
    public virtual IReadOnlyList<string> GetTouchedAssets() => [];

    /// <summary>Short display name of an object or package path (<c>/Game/X/SM_Rock.SM_Rock</c> gives <c>SM_Rock</c>).</summary>
    protected static string Short(string path)
    {
        var p = path.TrimEnd('/');
        var slash = p.LastIndexOf('/');
        var name = slash < 0 ? p : p[(slash + 1)..];
        var dot = name.IndexOf('.');
        // A native class (/Script/SCUM.WorldItemSpawner) is named after the dot, an asset (/Game/X/SM_Rock.SM_Rock) before it.
        return dot < 0 ? name : p.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) ? name[(dot + 1)..] : name[..dot];
    }

    /// <summary>Formats a transform for summaries.</summary>
    protected static string Format(TransformValue t) =>
        string.Create(CultureInfo.InvariantCulture,
            $"({t.Location.X:0.##}, {t.Location.Y:0.##}, {t.Location.Z:0.##}) rot ({t.Rotation.Pitch:0.##}, {t.Rotation.Yaw:0.##}, {t.Rotation.Roll:0.##}) scale ({t.Scale.X:0.###}, {t.Scale.Y:0.###}, {t.Scale.Z:0.###})");

    /// <summary>Distinct levels (case-insensitive) in first-seen order.</summary>
    protected static IReadOnlyList<string> Distinct(IEnumerable<string> levels) =>
        levels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>Deletes an actor (and its components) from its level.</summary>
/// <param name="Target">The actor.</param>
public sealed record DeleteActorOp(ActorRef Target) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => new RestoreActorOp(Target);

    /// <inheritdoc />
    public override string Describe() => $"Delete {Target}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Target.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Target;
}

/// <summary>Cancels a <see cref="DeleteActorOp"/> (the actor is kept again).</summary>
/// <param name="Target">The actor.</param>
public sealed record RestoreActorOp(ActorRef Target) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => new DeleteActorOp(Target);

    /// <inheritdoc />
    public override string Describe() => $"Restore {Target}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Target.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Target;
}

/// <summary>
/// Deletes every actor (and optionally every ISM instance) of the same mesh or class within a scope. The targets are
/// resolved when the operation is created (see <see cref="EditOpFactory"/>) and stored, so replay is deterministic.
/// </summary>
/// <param name="Match">The criterion.</param>
/// <param name="Scope">Where it was applied (informational).</param>
/// <param name="Actors">Actors deleted.</param>
/// <param name="Instances">ISM/HISM instances deleted.</param>
public sealed record DeleteAllOfKindOp(KindMatch Match, EditScope Scope, IReadOnlyList<ActorRef> Actors, IReadOnlyList<InstanceRef> Instances) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => new RestoreAllOfKindOp(Match, Scope, Actors, Instances);

    /// <inheritdoc />
    public override string Describe() =>
        $"Delete all of {Short(Match.Path)} ({Match.By}) in {Scope}: {Actors.Count} actor(s), {Instances.Count} instance(s)";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => Distinct(Actors.Select(a => a.Level).Concat(Instances.Select(i => i.Level)));

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => null;

    /// <inheritdoc />
    public bool Equals(DeleteAllOfKindOp? other) =>
        other is not null && Match == other.Match && Scope == other.Scope
        && Actors.SequenceEqual(other.Actors) && Instances.SequenceEqual(other.Instances);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Match, Scope, Actors.Count, Instances.Count);
}

/// <summary>Cancels a <see cref="DeleteAllOfKindOp"/> (every target is kept again).</summary>
/// <param name="Match">The criterion.</param>
/// <param name="Scope">Where it was applied.</param>
/// <param name="Actors">Actors restored.</param>
/// <param name="Instances">Instances restored.</param>
public sealed record RestoreAllOfKindOp(KindMatch Match, EditScope Scope, IReadOnlyList<ActorRef> Actors, IReadOnlyList<InstanceRef> Instances) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => new DeleteAllOfKindOp(Match, Scope, Actors, Instances);

    /// <inheritdoc />
    public override string Describe() =>
        $"Restore all of {Short(Match.Path)} ({Match.By}) in {Scope}: {Actors.Count} actor(s), {Instances.Count} instance(s)";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => Distinct(Actors.Select(a => a.Level).Concat(Instances.Select(i => i.Level)));

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => null;

    /// <inheritdoc />
    public bool Equals(RestoreAllOfKindOp? other) =>
        other is not null && Match == other.Match && Scope == other.Scope
        && Actors.SequenceEqual(other.Actors) && Instances.SequenceEqual(other.Instances);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Match, Scope, Actors.Count, Instances.Count);
}

/// <summary>Duplicates an actor (with its components) inside its own level under a new unique name.</summary>
/// <param name="Source">The actor to copy.</param>
/// <param name="NewName">Name of the copy (unique in the level).</param>
/// <param name="Transform">Relative transform of the copy's root component.</param>
public sealed record DuplicateActorOp(ActorRef Source, string NewName, TransformValue Transform) : EditOp
{
    /// <summary>The new actor.</summary>
    [JsonIgnore]
    public ActorRef Created => new(Source.Level, NewName);

    /// <inheritdoc />
    public override EditOp Inverse() => new RemoveAddedActorOp(Created, this);

    /// <inheritdoc />
    public override string Describe() => $"Duplicate {Source} as {NewName} at {Format(Transform)}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Source.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Created;
}

/// <summary>Sets the relative transform of an actor's root component (or of a named component).</summary>
/// <param name="Target">The actor.</param>
/// <param name="Old">Transform before the edit.</param>
/// <param name="New">Transform after the edit.</param>
/// <param name="Component">Component name; null for the root component.</param>
public sealed record SetTransformOp(ActorRef Target, TransformValue Old, TransformValue New, string? Component = null) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => this with { Old = New, New = Old };

    /// <inheritdoc />
    public override string Describe() => $"Transform {Target}{(Component is null ? string.Empty : "." + Component)} to {Format(New)}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Target.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Target;
}

/// <summary>
/// Bends a single-mesh actor (a wall, a bridge, a road piece) along its length: <see cref="New"/> degrees from one end to
/// the other, negative to the left, positive to the right, 0 = straight. The middle stays where it is and the length is
/// kept; the game draws the result as a <c>SplineMeshActor</c> (see <see cref="Model.BendShape"/>).
/// </summary>
/// <param name="Target">The actor.</param>
/// <param name="Old">Bend before the edit, degrees.</param>
/// <param name="New">Bend after the edit, degrees (-180..180).</param>
/// <param name="OldSway1">Sideways push of the first handle before the edit, cm (see <see cref="SplineSway"/>).</param>
/// <param name="OldSway2">Sideways push of the second handle before the edit, cm.</param>
/// <param name="NewSway1">Sideways push of the first handle after the edit, cm (positive right).</param>
/// <param name="NewSway2">Sideways push of the second handle after the edit, cm.</param>
/// <param name="OldStart">The start's edit before (see <see cref="SplineEnd"/>).</param>
/// <param name="OldEnd">The end's edit before.</param>
/// <param name="NewStart">The start moved, turned or widened after the edit.</param>
/// <param name="NewEnd">The end after the edit.</param>
/// <param name="OldLegs">How much longer the legs were before, cm (see <see cref="BendValue.Legs"/>).</param>
/// <param name="NewLegs">How much longer the legs are after the edit, cm.</param>
public sealed record BendActorOp(ActorRef Target, float Old, float New, float OldSway1 = 0f, float OldSway2 = 0f, float NewSway1 = 0f, float NewSway2 = 0f,
    SplineEnd OldStart = default, SplineEnd OldEnd = default, SplineEnd NewStart = default, SplineEnd NewEnd = default, float OldLegs = 0f, float NewLegs = 0f) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => this with
    {
        Old = New, New = Old, OldSway1 = NewSway1, NewSway1 = OldSway1, OldSway2 = NewSway2, NewSway2 = OldSway2,
        OldStart = NewStart, NewStart = OldStart, OldEnd = NewEnd, NewEnd = OldEnd, OldLegs = NewLegs, NewLegs = OldLegs,
    };

    /// <summary>The shape after the edit.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BendValue NewValue => new(New, NewSway1, NewSway2, NewStart, NewEnd, NewLegs);

    /// <summary>The shape before the edit.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BendValue OldValue => new(Old, OldSway1, OldSway2, OldStart, OldEnd, OldLegs);

    /// <inheritdoc />
    public override string Describe() => (NewSway1 == 0f && NewSway2 == 0f
        ? FormattableString.Invariant($"Bend {Target} to {New:0.#} deg")
        : FormattableString.Invariant($"Bend {Target} to {New:0.#} deg, handles {NewSway1 / 100f:0.##} m / {NewSway2 / 100f:0.##} m"))
        + NewValue.DescribeEnds();

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Target.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Target;
}

/// <summary>
/// Pushes one spline mesh piece of a road, rail or bridge sideways with its two handles (see <see cref="SplineSway"/>);
/// the piece's ends stay where they are. The game rebuilds the piece's collision when the level loads.
/// </summary>
/// <param name="Target">The actor holding the piece (a landscape streaming proxy for roads).</param>
/// <param name="Component">The SplineMeshComponent's name.</param>
/// <param name="Old1">First handle's push before the edit, cm.</param>
/// <param name="Old2">Second handle's push before the edit, cm.</param>
/// <param name="New1">First handle's push after the edit, cm (positive right).</param>
/// <param name="New2">Second handle's push after the edit, cm.</param>
/// <param name="OldStart">The start's edit before (see <see cref="SplineEnd"/>).</param>
/// <param name="OldEnd">The end's edit before.</param>
/// <param name="NewStart">The start after the edit (moved, turned, widened).</param>
/// <param name="NewEnd">The end after the edit.</param>
public sealed record SwaySegmentOp(ActorRef Target, string Component, float Old1, float Old2, float New1, float New2,
    SplineEnd OldStart = default, SplineEnd OldEnd = default, SplineEnd NewStart = default, SplineEnd NewEnd = default) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => this with { Old1 = New1, New1 = Old1, Old2 = New2, New2 = Old2, OldStart = NewStart, NewStart = OldStart, OldEnd = NewEnd, NewEnd = OldEnd };

    /// <summary>The piece's shape after the edit (no bend of its own: <see cref="BendValue.Degrees"/> is 0).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BendValue NewValue => new(0f, New1, New2, NewStart, NewEnd);

    /// <summary>The piece's shape before the edit.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BendValue OldValue => new(0f, Old1, Old2, OldStart, OldEnd);

    /// <inheritdoc />
    public override string Describe() => FormattableString.Invariant($"Bend {Target}.{Component}: handles {New1 / 100f:0.##} m / {New2 / 100f:0.##} m") + NewValue.DescribeEnds();

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Target.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Target;
}

/// <summary>A bent actor's (or road piece's) shape: the bend in degrees, the two handles' sideways pushes in cm, and its ends.</summary>
/// <param name="Degrees">Bend end to end (negative left).</param>
/// <param name="Sway1">First handle, cm (positive right).</param>
/// <param name="Sway2">Second handle, cm.</param>
/// <param name="Start">The start moved, turned or widened.</param>
/// <param name="End">The end moved, turned or widened.</param>
/// <param name="Legs">How much longer its legs are, cm: its foot goes this much lower and its top stays (a tower's foot under the water; see <see cref="BendShape.Legs"/>).</param>
public readonly record struct BendValue(float Degrees, float Sway1 = 0f, float Sway2 = 0f, SplineEnd Start = default, SplineEnd End = default, float Legs = 0f)
{
    /// <summary>True when the actor is straight, its ends as made and its legs as long as made.</summary>
    public bool IsStraight => MathF.Abs(Degrees) < 0.001f && MathF.Abs(Sway1) < 0.01f && MathF.Abs(Sway2) < 0.01f && Start.IsNone && End.IsNone && !HasLegs;

    /// <summary>True when the legs are made longer (or shorter): the object is then drawn upright along a vertical spline.</summary>
    public bool HasLegs => MathF.Abs(Legs) >= 0.5f;

    /// <summary>True when both shapes are the same within rounding.</summary>
    public bool IsNearly(BendValue other) =>
        MathF.Abs(Degrees - other.Degrees) < 0.001f && MathF.Abs(Sway1 - other.Sway1) < 0.01f && MathF.Abs(Sway2 - other.Sway2) < 0.01f
        && Start.IsNearly(other.Start) && End.IsNearly(other.End) && MathF.Abs(Legs - other.Legs) < 0.05f;

    /// <summary>True when every value is a usable number.</summary>
    public bool IsValid => float.IsFinite(Degrees) && float.IsFinite(Sway1) && float.IsFinite(Sway2) && Start.IsValid && End.IsValid
        && float.IsFinite(Legs) && MathF.Abs(Legs) < 1_000_000f;

    /// <summary>The ends' (and legs') edits for the history list ("" when they are as made).</summary>
    public string DescribeEnds()
    {
        static string One(string name, SplineEnd e) => e.IsNone ? string.Empty
            : FormattableString.Invariant($", {name} moved {e.Move.Size() / 100f:0.##} m, {(1f + e.Grow) * 100f:0}% wide");
        return One("start", Start) + One("end", End) + (HasLegs ? FormattableString.Invariant($", legs {Legs / 100f:+0.##;-0.##} m") : string.Empty);
    }
}

/// <summary>Sets the component-space transform of one ISM/HISM instance.</summary>
/// <param name="Target">The instance.</param>
/// <param name="Old">Transform before the edit.</param>
/// <param name="New">Transform after the edit.</param>
public sealed record SetInstanceTransformOp(InstanceRef Target, TransformValue Old, TransformValue New) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => this with { Old = New, New = Old };

    /// <inheritdoc />
    public override string Describe() => $"Transform instance {Target} to {Format(New)}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Target.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Target.ActorRef;
}

/// <summary>Deletes one ISM/HISM instance.</summary>
/// <param name="Target">The instance (pristine index).</param>
public sealed record DeleteInstanceOp(InstanceRef Target) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => new RestoreInstanceOp(Target);

    /// <inheritdoc />
    public override string Describe() => $"Delete instance {Target}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Target.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Target.ActorRef;
}

/// <summary>Cancels a <see cref="DeleteInstanceOp"/>.</summary>
/// <param name="Target">The instance.</param>
public sealed record RestoreInstanceOp(InstanceRef Target) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => new DeleteInstanceOp(Target);

    /// <inheritdoc />
    public override string Describe() => $"Restore instance {Target}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Target.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Target.ActorRef;
}

/// <summary>Adds a new <c>StaticMeshActor</c> showing <paramref name="StaticMesh"/>.</summary>
/// <param name="Level">Destination level package.</param>
/// <param name="NewName">Name of the new actor (unique in the level).</param>
/// <param name="StaticMesh">Static mesh object path, e.g. <c>/Game/ConZ_Files/Models/X/SM_Y.SM_Y</c>.</param>
/// <param name="Transform">World (root relative) transform of the new actor.</param>
public sealed record AddStaticMeshActorOp(string Level, string NewName, string StaticMesh, TransformValue Transform) : EditOp
{
    /// <summary>The new actor.</summary>
    [JsonIgnore]
    public ActorRef Created => new(Level, NewName);

    /// <summary>
    /// The collision profile of what it was copied from (a tree's foliage <c>SCUM_TreeStump</c>, a bush's <c>NoCollision</c>),
    /// or null to collide as its mesh does by default. A tree mesh's own default (<c>SCUM_Foliage</c>) lets players walk
    /// through: the owner's copied trees had no collision.
    /// </summary>
    public string? CollisionProfile { get; init; }

    /// <inheritdoc />
    public override EditOp Inverse() => new RemoveAddedActorOp(Created, this);

    /// <inheritdoc />
    public override string Describe() => $"Add {Short(StaticMesh)} as {Created} at {Format(Transform)}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Created;
}

/// <summary>
/// Adds a Blueprint actor by transplanting an existing instance of the class (<paramref name="Source"/>, possibly in
/// another level) with its component deltas.
/// </summary>
/// <param name="Level">Destination level package.</param>
/// <param name="NewName">Name of the new actor (unique in the level).</param>
/// <param name="ClassPath">Blueprint class path, e.g. <c>/Game/X/BP_Lamp.BP_Lamp_C</c>.</param>
/// <param name="Source">Existing actor to copy exports from.</param>
/// <param name="Transform">Root relative transform of the new actor.</param>
public sealed record AddBlueprintActorOp(string Level, string NewName, string ClassPath, ActorRef Source, TransformValue Transform) : EditOp
{
    /// <summary>The new actor.</summary>
    [JsonIgnore]
    public ActorRef Created => new(Level, NewName);

    /// <summary>
    /// For a copy of a world item spawner: the item class it spawns instead of its source's
    /// (<c>/Game/ConZ_Files/Items/X/Asian_Chest.Asian_Chest_C</c>), or null to spawn the same. Items are never placed as
    /// actors, so any item is placed this way.
    /// </summary>
    public string? Item { get; init; }

    /// <inheritdoc />
    public override EditOp Inverse() => new RemoveAddedActorOp(Created, this);

    /// <inheritdoc />
    public override string Describe() => $"Add {Short(ClassPath)}{(Item is null ? string.Empty : $" ({Short(Item)})")} as {Created} (from {Source}) at {Format(Transform)}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Created;
}

/// <summary>
/// Several edits applied, undone and listed as one step (a village an AI built, a planted forest). The edits must not
/// depend on each other: each is valid on the state before the batch.
/// </summary>
/// <param name="Title">What the batch did, for history lists ("Plant 240 trees").</param>
/// <param name="Ops">The edits, in order.</param>
public sealed record BatchOp(string Title, IReadOnlyList<EditOp> Ops) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => new BatchOp(Title, Ops.Reverse().Select(o => o.Inverse()).ToList());

    /// <inheritdoc />
    public override string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Title} ({Ops.Count} edit(s))");

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => Distinct(Ops.SelectMany(o => o.GetTouchedLevels()).Order(StringComparer.OrdinalIgnoreCase));

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedAssets() => Distinct(Ops.SelectMany(o => o.GetTouchedAssets()).Order(StringComparer.OrdinalIgnoreCase));

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Ops.Count == 1 ? Ops[0].GetPrimaryTarget() : null;

    /// <inheritdoc />
    public bool Equals(BatchOp? other) => other is not null && Title == other.Title && Ops.SequenceEqual(other.Ops);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Title, Ops.Count);
}

/// <summary>
/// Removes an actor created by <see cref="DuplicateActorOp"/>, <see cref="AddStaticMeshActorOp"/> or
/// <see cref="AddBlueprintActorOp"/>; its inverse is that original operation.
/// </summary>
/// <param name="Target">The added actor.</param>
/// <param name="Original">The operation that added it.</param>
public sealed record RemoveAddedActorOp(ActorRef Target, EditOp Original) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => Original;

    /// <inheritdoc />
    public override string Describe() => $"Remove added {Target}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Target.Level];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => Target;
}
