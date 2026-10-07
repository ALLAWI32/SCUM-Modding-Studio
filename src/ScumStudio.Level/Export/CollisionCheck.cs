using System.Globalization;
using System.Text.RegularExpressions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;

namespace ScumStudio.Level.Export;

/// <summary>A piece the game left without collision, as its log says.</summary>
/// <param name="Level">The level package, e.g. <c>/Game/ConZ_Files/Maps/The_Island/A_0_Dr_Tudman_Bridge</c>.</param>
/// <param name="Actor">The actor as the game named it (a piece the exporter split carries its <c>_Bent</c> / <c>_2</c> suffix).</param>
/// <param name="WhenUtc">When the game wrote the line.</param>
public sealed record CollisionFailure(string Level, string Actor, DateTime WhenUtc);

/// <summary>A mesh the export put somewhere (added, copied, moved, an instance added or moved), as the game will place it.</summary>
/// <param name="Label">How the report names it, e.g. <c>/Game/.../A_4_Farm_04: Cliff_02b_Added</c>.</param>
/// <param name="Mesh">Mesh object path.</param>
/// <param name="World">World transform.</param>
/// <param name="OwnCollision">
/// True when it collides as something the game placed (a copy, a moved original, an instance of the game's own foliage
/// component, an added actor given its source's profile); false when it collides as its mesh does by default.
/// </param>
public sealed record PlacedMesh(string Label, string Mesh, FTransform World, bool OwnCollision);

/// <summary>
/// The collision check (owner: "if something has no collision, the program should find it and replace it itself, for
/// everyone who uses it"): on export every spline piece is verified to carry its boxes and its mesh's body guid; after
/// playing, the game's own log tells which pieces still got no collision, and those are exported straight next time (a
/// straight piece uses the collision the game ships for its mesh).
/// </summary>
public static partial class CollisionCheck
{
    // [2026.10.02-19.42.30:661][816]LogPhysics: Warning: UBodySetup::GetCookInfo: Triangle data from
    // '/Game/.../A_0_Dr_Tudman_Bridge.A_0_Dr_Tudman_Bridge:PersistentLevel.SM_DrTudmanBridge_0_Copy2.SplineMeshComponent0' invalid ...
    [GeneratedRegex(@"^\[(?<t>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}):\d+\].*(?:GetCookInfo|Attempt to build physics data|cannot be accessed at runtime).*?'?(?<level>/Game/[^.'\s]+)\.[^:'\s]+:PersistentLevel\.(?<actor>[^.'\s]+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex FailureLine();

    /// <summary>The pieces the game's log says got no collision, written after <paramref name="sinceUtc"/>.</summary>
    public static IReadOnlyList<CollisionFailure> FromGameLog(IEnumerable<string> lines, DateTime sinceUtc)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var failures = new List<CollisionFailure>();
        foreach (var line in lines)
        {
            if (FailureLine().Match(line) is not { Success: true } m
                || !DateTime.TryParseExact(m.Groups["t"].Value, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
                || when < sinceUtc)
            {
                continue;
            }

            var failure = new CollisionFailure(m.Groups["level"].Value, m.Groups["actor"].Value, when);
            if (!failures.Any(f => string.Equals(f.Level, failure.Level, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Actor, failure.Actor, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(failure);
            }
        }

        return failures;
    }

    /// <summary>
    /// The project actor a failed piece came from: the actor itself, or the one the exporter split into it (its
    /// <c>_2</c>.. piece number and the <c>_Bent</c> of a re-added level actor taken off). Null when the project does not
    /// shape it.
    /// </summary>
    public static ActorRef? ActorOf(CollisionFailure failure, EditState state)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(state);
        foreach (var name in Candidates(failure.Actor))
        {
            var actor = state.Bends.Keys.FirstOrDefault(a => string.Equals(a.Level, failure.Level, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Actor, name, StringComparison.OrdinalIgnoreCase));
            if (actor is not null)
            {
                return actor;
            }
        }

        return null;

        static IEnumerable<string> Candidates(string name)
        {
            yield return name;
            var unnumbered = PieceNumber().Replace(name, string.Empty);
            yield return unnumbered;
            if (unnumbered.EndsWith("_Bent", StringComparison.Ordinal))
            {
                yield return unnumbered[..^"_Bent".Length];
            }
        }
    }

    [GeneratedRegex(@"_\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex PieceNumber();

    /// <summary>
    /// Checks a rewritten level: each named spline piece's component points at a body of boxes and carries its mesh's body
    /// guid (without it the game rebuilds the collision from the mesh at load and fails). Returns what is wrong.
    /// </summary>
    public static IReadOnlyList<string> Verify(CookedPackage written, IEnumerable<string> pieces)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(pieces);
        var problems = new List<string>();
        foreach (var piece in pieces)
        {
            var actor = Enumerable.Range(0, written.Exports.Count).FirstOrDefault(i => written.ResolveName(written.Exports[i].ObjectName) == piece, -1);
            var component = actor < 0 ? -1 : Enumerable.Range(0, written.Exports.Count).FirstOrDefault(i => written.Exports[i].OuterIndex == actor + 1
                && written.GetExportClassName(i) == "SplineMeshComponent", -1);
            if (component < 0)
            {
                problems.Add($"{piece}: not written");
                continue;
            }

            var properties = written.ReadProperties(component);
            var body = Enumerable.Range(0, written.Exports.Count).FirstOrDefault(i => written.Exports[i].OuterIndex == component + 1
                && written.GetExportClassName(i) == "BodySetup", -1);
            if (properties.Find("BodySetup") is null || body < 0 || written.ReadProperties(body).Find("AggGeom") is null)
            {
                problems.Add($"{piece}: no collision written");
            }
            else if (properties.Find("CachedMeshBodySetupGuid") is null)
            {
                problems.Add($"{piece}: its mesh's body guid is missing, the game would rebuild (and lose) its collision");
            }
        }

        return problems;
    }

    /// <summary>A rock's open underside counts as open when it stands this far (cm) above the ground: a body fits under it.</summary>
    public const float OpenGap = 30f;

    /// <summary>
    /// The placed meshes players could get through or into (owner, B_4: "half my body is inside the rock", "when he is
    /// knocked out he falls under the rocks; from there he shoots others and nobody can hit him"): an added mesh whose
    /// own collision lets players (<c>Pawn</c>) or a knocked-out body (<c>PhysicsBody</c>) through, and a hollow rock
    /// whose open underside stands above the ground. SCUM's rocks and cliffs are shells open underneath that collide with
    /// their triangles, solid from outside only; the game sinks that edge into the ground (Landscape_B_4_2b: 155 of its 158
    /// coastal rocks all of it; the other 3, on the shore below zero height, up to 1.2 m). Lifted, a player walks or falls
    /// inside, sees and shoots out through the rock, and nobody outside can see or hit him. One report line each.
    /// </summary>
    /// <param name="placed">What the export placed.</param>
    /// <param name="meshes">What each mesh collides with (<see cref="BendSupport.Describe"/>).</param>
    /// <param name="ground">The ground height at a world X/Y, or null where unknown.</param>
    public static IReadOnlyList<string> Placed(IEnumerable<PlacedMesh> placed, Func<string, BendMesh?> meshes, Func<float, float, float?> ground)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(ground);
        var lines = new List<string>();
        foreach (var p in placed)
        {
            if (meshes(p.Mesh) is not { } info || MathF.Abs(p.World.Scale3D.X) < 0.001f)
            {
                continue; // unknown, or a deleted (collapsed) instance
            }

            var name = p.Mesh[(p.Mesh.LastIndexOf('.') + 1)..];
            if (!p.OwnCollision && info.LetsThrough is { Count: > 0 } through)
            {
                lines.Add(through.Count > 1
                    ? $"{p.Label}: {name} has no collision for players: they walk through it and a knocked-out player falls through it (collision check)."
                    : through[0] == "Pawn"
                        ? $"{p.Label}: {name} does not stop players: its collision lets them walk through it (collision check)."
                        : $"{p.Label}: {name} does not hold a knocked-out player: its collision lets bodies fall through it (collision check).");
            }

            if (info.Rim is not { Count: > 0 } rim)
            {
                continue;
            }

            var (open, known, highest) = (0, 0, float.MinValue);
            foreach (var local in rim)
            {
                var at = p.World.TransformPosition(local);
                if (ground(at.X, at.Y) is not { } floor)
                {
                    continue;
                }

                known++;
                highest = MathF.Max(highest, at.Z - floor);
                if (at.Z - floor > OpenGap)
                {
                    open++;
                }
            }

            if (open > 0)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{p.Label}: {name} is hollow and open underneath, and {open * 100 / known}% of that open edge stands above the ground (up to {highest / 100f:0.0} m): players can walk or fall inside the rock and shoot out of it unseen. Lower it into the ground until its lower edge is buried (collision check)."));
            }
        }

        return lines;
    }
}
