using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;

namespace ScumStudio.Level.Spawns;

/// <summary>A spawn place below the ground: where it is, the ground there and how deep it is (cm).</summary>
/// <param name="Place">The place (as the project has it).</param>
/// <param name="Z">Its height.</param>
/// <param name="Ground">The terrain's height under it.</param>
/// <param name="Depth">How far below the terrain it is (positive).</param>
public sealed record SpawnGroundFinding(SpawnPlace Place, float Z, float Ground, float Depth);

/// <summary>
/// Spawn points under the ground (seviscache: "simple shapes revealed spawners below the terrain, maybe that's why people
/// can't find vehicles"): the vehicle and zombie / NPC points of the island's static data that stand below the landscape,
/// and the edits that put them back on it. Indoor points (cellars, bunkers, tunnels) are below the terrain on purpose and
/// are left out, as are threat zones and hunting areas (areas, not points) and places where there is no terrain (sea).
/// </summary>
public static class SpawnGroundCheck
{
    /// <summary>A vehicle point this far below the terrain is buried (cm): a car is put down from a little above its point.</summary>
    public const float VehicleTolerance = 30f;

    /// <summary>A zombie / NPC point this far below the terrain is buried (cm).</summary>
    public const float CharacterTolerance = 50f;

    /// <summary>A snapped vehicle point stands this far above the terrain (cm).</summary>
    public const float VehicleLift = 20f;

    /// <summary>A snapped zombie / NPC point stands this far above the terrain (cm).</summary>
    public const float CharacterLift = 5f;

    /// <summary>The character group of the static data's indoor points (<c>EEncounterCharacterSpawnPointType::Interior</c>).</summary>
    public const string Interior = "Interior";

    /// <summary>Size of the squares the places are visited by (cm), so a tile of the ground is read once.</summary>
    private const float Square = 50_000f;

    /// <summary>
    /// The places of <paramref name="places"/> that <paramref name="keep"/> chooses (not deleted, inside the selected area,
    /// not under a bunker: <see cref="OutsideUnderground"/>) and that stand below the ground <paramref name="groundAt"/>
    /// gives (null: no terrain there). Visited square by square, so a tile cache reads each tile once; the findings come in
    /// that order. <paramref name="progress"/> gets the number of places checked.
    /// </summary>
    public static IReadOnlyList<SpawnGroundFinding> Run(IReadOnlyList<SpawnPlace> places, Func<SpawnPlace, bool> keep, Func<float, float, float?> groundAt, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(groundAt);
        var findings = new List<SpawnGroundFinding>();
        var done = 0;
        foreach (var place in places.Where(IsPoint).OrderBy(p => SquareOf(p.Transform.Translation.Y)).ThenBy(p => SquareOf(p.Transform.Translation.X)))
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 1024 == 0)
            {
                progress?.Report(done);
            }

            if (!keep(place))
            {
                continue;
            }

            var at = place.Transform.Translation;
            if (groundAt(at.X, at.Y) is { } ground && ground - at.Z > ToleranceOf(place.Kind))
            {
                findings.Add(new SpawnGroundFinding(place, at.Z, ground, ground - at.Z));
            }
        }

        progress?.Report(done);
        return findings;
    }

    /// <summary>
    /// The edits that put <paramref name="findings"/> (the ones the user kept ticked: a cellar the check cannot tell from a
    /// buried point stays where it is) back on the ground: one <see cref="SetTransformOp"/> per place actor of
    /// the static data, its height set to the ground plus <see cref="VehicleLift"/> or <see cref="CharacterLift"/>, its
    /// turn, its stored scale and the rest as the project has them (a place the project added: its own transform). Applied
    /// together in one <see cref="BatchOp"/>, one undo restores all.
    /// </summary>
    public static IReadOnlyList<EditOp> SnapOps(IEnumerable<SpawnGroundFinding> findings, EditState state)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(state);
        var ops = new List<EditOp>();
        foreach (var finding in findings.Where(f => IsPoint(f.Place)))
        {
            var target = new ActorRef(SpawnPlaces.StaticDataPath, finding.Place.ActorName);
            var t = finding.Place.Transform;
            var old = state.GetAddedTransform(target) ?? state.GetTransformOverride(target) ?? new TransformValue(t.Translation, t.Rotation.Rotator(), t.Scale3D);
            var lift = finding.Place.Kind == SpawnPlaceKind.Vehicle ? VehicleLift : CharacterLift;
            ops.Add(new SetTransformOp(target, old, old with { Location = old.Location with { Z = finding.Ground + lift } }));
        }

        return ops;
    }

    /// <summary>
    /// A <c>keep</c> for <see cref="Run"/> that leaves out the places inside an underground level's box (a TV bunker, the
    /// abandoned bunkers, tunnels: <paramref name="boxes"/>, from <see cref="UndergroundBoxes"/>).
    /// </summary>
    public static Func<SpawnPlace, bool> OutsideUnderground(IReadOnlyList<(FVector Min, FVector Max)> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        return place =>
        {
            var p = place.Transform.Translation;
            return !boxes.Any(b => b.Min.X <= p.X && p.X <= b.Max.X && b.Min.Y <= p.Y && p.Y <= b.Max.Y && b.Min.Z <= p.Z && p.Z <= b.Max.Z);
        };
    }

    /// <summary>
    /// The world boxes of the island's underground levels: the TV bunkers and every level whose name says bunker, tunnel,
    /// underground or mine, from their world tile bounds. Reads the level headers: call it on a worker.
    /// </summary>
    public static IReadOnlyList<(FVector Min, FVector Max)> UndergroundBoxes(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string[] words = ["Bunker", "Tunnel", "Underground", "Mine"];
        return WorldIndex.FromCatalog(catalog).Packages
            .Where(p => p.IsMap && (p.Kind == WorldPackageKind.TvBase || words.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Select(p => catalog.TryGetPackageFile(p.PackagePath, out var file) ? WorldTileInfo.TryRead(CookedPackage.Parse(file.Read(), [], null, p.PackagePath)) : null)
            .Where(t => t is { BoundsValid: true })
            .Select(t => (t!.BoundsMin, t.BoundsMax))
            .ToList();
    }

    /// <summary>True for a place the check is about: a vehicle point or an outdoor zombie / NPC point.</summary>
    private static bool IsPoint(SpawnPlace place) => place.Kind switch
    {
        SpawnPlaceKind.Vehicle => true,
        SpawnPlaceKind.Character or SpawnPlaceKind.TaggedCharacter => !string.Equals(place.Group, Interior, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static float ToleranceOf(SpawnPlaceKind kind) => kind == SpawnPlaceKind.Vehicle ? VehicleTolerance : CharacterTolerance;

    private static int SquareOf(float v) => (int)MathF.Floor(v / Square);
}
