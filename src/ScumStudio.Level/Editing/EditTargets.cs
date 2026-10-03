using System.Globalization;
using System.Text.Json.Serialization;

namespace ScumStudio.Level.Editing;

/// <summary>
/// Identifies an actor across levels: level package path plus actor object name. Actor names are unique inside a
/// level (their outer is <c>PersistentLevel</c>) and survive package rewrites, unlike export indices.
/// </summary>
/// <param name="Level">Level package path, e.g. <c>/Game/ConZ_Files/Maps/The_Island/A_0_Outpost</c>.</param>
/// <param name="Actor">Actor object name, e.g. <c>StaticMeshActor_12</c>.</param>
public sealed record ActorRef(string Level, string Actor)
{
    /// <summary>Level name (last path segment).</summary>
    [JsonIgnore]
    public string LevelName => Level[(Level.LastIndexOf('/') + 1)..];

    /// <summary>Case-insensitive comparer (UE names are case-insensitive).</summary>
    public static IEqualityComparer<ActorRef> Comparer { get; } = new IgnoreCaseComparer();

    /// <inheritdoc />
    public override string ToString() => $"{LevelName}/{Actor}";

    private sealed class IgnoreCaseComparer : IEqualityComparer<ActorRef>
    {
        public bool Equals(ActorRef? x, ActorRef? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null
                && StringComparer.OrdinalIgnoreCase.Equals(x.Level, y.Level)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Actor, y.Actor));

        public int GetHashCode(ActorRef obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Level), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Actor));
    }
}

/// <summary>
/// Identifies one instance of an instanced static mesh component. <paramref name="Index"/> is the instance's position in
/// the component's <c>PerInstanceSMData</c> in the <b>pristine</b> package; deleting instances never renumbers the others
/// (deleted slots are removed only when the package is rewritten at export).
/// </summary>
/// <param name="Level">Level package path.</param>
/// <param name="Actor">Owning actor name.</param>
/// <param name="Component">Component name within the actor.</param>
/// <param name="Index">Instance index.</param>
public sealed record InstanceRef(string Level, string Actor, string Component, int Index)
{
    /// <summary>The owning actor.</summary>
    [JsonIgnore]
    public ActorRef ActorRef => new(Level, Actor);

    /// <summary>Case-insensitive comparer.</summary>
    public static IEqualityComparer<InstanceRef> Comparer { get; } = new IgnoreCaseComparer();

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{ActorRef}/{Component}[{Index}]");

    private sealed class IgnoreCaseComparer : IEqualityComparer<InstanceRef>
    {
        public bool Equals(InstanceRef? x, InstanceRef? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.Index == y.Index
                && StringComparer.OrdinalIgnoreCase.Equals(x.Level, y.Level)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Actor, y.Actor)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Component, y.Component));

        public int GetHashCode(InstanceRef obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Level),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Actor),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Component),
                obj.Index);
    }
}

/// <summary>What <see cref="KindMatch"/> compares.</summary>
public enum MatchBy
{
    /// <summary>The static mesh object path (StaticMeshActors, mesh roots and ISM instances).</summary>
    StaticMesh,

    /// <summary>The actor class path (e.g. a Blueprint <c>..._C</c> class).</summary>
    Class,
}

/// <summary>"Same kind" criterion for <see cref="DeleteAllOfKindOp"/>.</summary>
/// <param name="By">Mesh or class.</param>
/// <param name="Path">Object path of the mesh or class (package path and short forms are accepted when matching).</param>
public sealed record KindMatch(MatchBy By, string Path)
{
    /// <inheritdoc />
    public override string ToString() => $"{(By == MatchBy.StaticMesh ? "mesh" : "class")} {Path}";
}

/// <summary>Extent of a bulk operation.</summary>
public enum ScopeKind
{
    /// <summary>One level package.</summary>
    Level,

    /// <summary>All sublevels of a map cell (e.g. <c>A_0</c>).</summary>
    Cell,

    /// <summary>Every sublevel of The_Island.</summary>
    Island,
}

/// <summary>Where a bulk operation applied (informational; the resolved targets are stored on the operation).</summary>
/// <param name="Kind">Scope kind.</param>
/// <param name="Value">Level package path for <see cref="ScopeKind.Level"/>, cell (<c>A_0</c>) for <see cref="ScopeKind.Cell"/>, null for the island.</param>
public sealed record EditScope(ScopeKind Kind, string? Value = null)
{
    /// <summary>The whole island.</summary>
    public static EditScope Island { get; } = new(ScopeKind.Island);

    /// <summary>One level.</summary>
    public static EditScope ForLevel(string levelPackagePath) => new(ScopeKind.Level, levelPackagePath);

    /// <summary>One map cell.</summary>
    public static EditScope ForCell(World.MapCell cell) => new(ScopeKind.Cell, cell.ToString());

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        ScopeKind.Level => "level " + (Value is null ? "?" : Value[(Value.LastIndexOf('/') + 1)..]),
        ScopeKind.Cell => "cell " + Value,
        _ => "island",
    };
}
