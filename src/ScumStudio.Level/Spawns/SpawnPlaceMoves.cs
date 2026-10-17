using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Spawns;

/// <summary>
/// One spawn place moved or turned by hand (owner: "click a single vehicle spawn point, drag it, rotate it, nudge it with
/// the keys"): the <see cref="SetTransformOp"/> on its actor of the static data, built from the transform the project has
/// for it (a place the project added: its own), so the Spawns map and the Map page edit the same value and Export mod
/// writes it like any other move. A zone's or hunting area's size rides in the scale (metres, as the map holds it) and is
/// kept; only the place and the turn change.
/// </summary>
public static class SpawnPlaceMoves
{
    /// <summary>The transform the map's document holds for <paramref name="place"/> as read (see <see cref="SpawnPlaces.ToLevelData"/>).</summary>
    public static TransformValue MapValue(SpawnPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        var t = place.Transform;
        var scale = place.Kind is SpawnPlaceKind.Zone or SpawnPlaceKind.Animal
            ? new FVector(MathF.Max(place.SizeX, 1f) / 100f, MathF.Max(place.SizeY, 1f) / 100f, 1f)
            : t.Scale3D;
        return new TransformValue(t.Translation, t.Rotation.Rotator(), scale);
    }

    /// <summary>The transform the project has for <paramref name="place"/>: an added place's own, a moved one's, else as read.</summary>
    public static TransformValue Current(SpawnPlace place, EditState state)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(state);
        var actor = new ActorRef(SpawnPlaces.StaticDataPath, place.ActorName);
        return state.GetAddedTransform(actor) ?? state.GetTransformOverride(actor) ?? MapValue(place);
    }

    /// <summary>
    /// The edit that puts <paramref name="place"/> at (<paramref name="x"/>, <paramref name="y"/>) cm, at height
    /// <paramref name="z"/> (null keeps its height) and turned to <paramref name="yaw"/> degrees (null keeps its turn); null
    /// when nothing would change.
    /// </summary>
    public static SetTransformOp? Move(SpawnPlace place, EditState state, float x, float y, float? z = null, float? yaw = null)
    {
        var old = Current(place, state);
        var rotation = yaw is { } turn ? old.Rotation with { Yaw = UeMath.NormalizeAxis(turn) } : old.Rotation;
        var value = old with { Location = new FVector(x, y, z ?? old.Location.Z), Rotation = rotation };
        return value == old ? null : new SetTransformOp(new ActorRef(SpawnPlaces.StaticDataPath, place.ActorName), old, value);
    }

    /// <summary>The edit that turns <paramref name="place"/> by <paramref name="degrees"/> about the vertical axis where it stands; null for 0.</summary>
    public static SetTransformOp? Turn(SpawnPlace place, EditState state, float degrees)
    {
        var old = Current(place, state);
        return Move(place, state, old.Location.X, old.Location.Y, null, old.Rotation.Yaw + degrees);
    }

    /// <summary>
    /// True when a moved <paramref name="place"/> goes onto the terrain: every kind but an indoor zombie / NPC point (it stands
    /// on a building's floor, not on the terrain under it).
    /// </summary>
    public static bool SnapsToGround(SpawnPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        return !string.Equals(place.Group, SpawnGroundCheck.Interior, StringComparison.OrdinalIgnoreCase)
            || place.Kind is SpawnPlaceKind.Vehicle or SpawnPlaceKind.Zone or SpawnPlaceKind.Animal;
    }

    /// <summary>
    /// The height (cm) <paramref name="place"/> takes on terrain at <paramref name="ground"/>: a little above it (vehicles
    /// <see cref="SpawnGroundCheck.VehicleLift"/>, the others <see cref="SpawnGroundCheck.CharacterLift"/>), as a new place and
    /// the ground fix put them; null when the ground is not known or the place does not snap (<see cref="SnapsToGround"/>).
    /// </summary>
    public static float? GroundHeight(SpawnPlace place, float? ground) =>
        ground is { } g && SnapsToGround(place) ? g + (place.Kind == SpawnPlaceKind.Vehicle ? SpawnGroundCheck.VehicleLift : SpawnGroundCheck.CharacterLift) : null;
}
