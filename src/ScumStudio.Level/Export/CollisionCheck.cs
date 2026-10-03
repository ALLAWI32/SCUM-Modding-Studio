using System.Globalization;
using System.Text.RegularExpressions;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;

namespace ScumStudio.Level.Export;

/// <summary>A piece the game left without collision, as its log says.</summary>
/// <param name="Level">The level package, e.g. <c>/Game/ConZ_Files/Maps/The_Island/A_0_Dr_Tudman_Bridge</c>.</param>
/// <param name="Actor">The actor as the game named it (a piece the exporter split carries its <c>_Bent</c> / <c>_2</c> suffix).</param>
/// <param name="WhenUtc">When the game wrote the line.</param>
public sealed record CollisionFailure(string Level, string Actor, DateTime WhenUtc);

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
}
