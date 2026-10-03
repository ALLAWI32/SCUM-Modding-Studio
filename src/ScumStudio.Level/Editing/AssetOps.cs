using ScumStudio.Level.Model;

namespace ScumStudio.Level.Editing;

/// <summary>One package of a clone: the stock package and the new package path it is copied to.</summary>
/// <param name="Old">Stock package path.</param>
/// <param name="New">New package path.</param>
public sealed record PackagePair(string Old, string New);

/// <summary>
/// Clones a stock vehicle or item family under a new name (Vehicles/Weapons modules). The packages are rename-clones of
/// the pristine ones (see <c>ScumStudio.Modding.Cloning</c>); the exporter builds them and registers them in
/// <c>AssetRegistry.bin</c>. Its inverse is <see cref="RemoveAssetCloneOp"/>.
/// </summary>
/// <param name="Kind">What was cloned: <c>vehicle</c>, <c>weapon</c>, <c>magazine</c>, <c>ammo</c> or <c>projectile</c>.</param>
/// <param name="Template">The stock primary package.</param>
/// <param name="NewPrimary">The new primary package.</param>
/// <param name="Packages">Every cloned package, the primary first.</param>
public sealed record CloneAssetOp(string Kind, string Template, string NewPrimary, IReadOnlyList<PackagePair> Packages) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => new RemoveAssetCloneOp(NewPrimary, this);

    /// <inheritdoc />
    public override string Describe() => $"Clone {Kind} {Short(Template)} as {Short(NewPrimary)} ({Packages.Count} package{(Packages.Count == 1 ? string.Empty : "s")})";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [];

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedAssets() => Packages.Select(p => p.New).ToArray();

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => null;

    /// <inheritdoc />
    public bool Equals(CloneAssetOp? other) =>
        other is not null
        && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
        && string.Equals(Template, other.Template, StringComparison.OrdinalIgnoreCase)
        && string.Equals(NewPrimary, other.NewPrimary, StringComparison.OrdinalIgnoreCase)
        && Packages.SequenceEqual(other.Packages);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, NewPrimary.ToUpperInvariant(), Packages.Count);
}

/// <summary>Removes a clone made by <see cref="CloneAssetOp"/>; its inverse is that original operation.</summary>
/// <param name="NewPrimary">The clone's primary package.</param>
/// <param name="Original">The operation that created it.</param>
public sealed record RemoveAssetCloneOp(string NewPrimary, EditOp Original) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => Original;

    /// <inheritdoc />
    public override string Describe() => $"Remove clone {Short(NewPrimary)}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [];

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedAssets() => Original.GetTouchedAssets();

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => null;
}

/// <summary>
/// Sets one stored value of a vehicle/item package (a <c>Tunable</c>: damage, rate of fire, wheel radius, weight, caption…).
/// Values are invariant text; <paramref name="Old"/> is the value before this edit. The package is a stock package
/// (overridden by the mod) or a package created by a <see cref="CloneAssetOp"/>.
/// </summary>
/// <param name="Package">Package path.</param>
/// <param name="Export">Export key (object name, <c>#n</c> for repeats).</param>
/// <param name="Path">Property path inside the export.</param>
/// <param name="ValueKind">Value type name (<c>Float</c>, <c>Int</c>, <c>Bool</c>, <c>Enum</c>, <c>Text</c>, …).</param>
/// <param name="Old">Value before the edit.</param>
/// <param name="New">Value after the edit.</param>
public sealed record SetAssetValueOp(string Package, string Export, string Path, string ValueKind, string Old, string New) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => this with { Old = New, New = Old };

    /// <inheritdoc />
    public override string Describe() => $"Set {Short(Package)} {Path} = {New} (was {Old})";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [];

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedAssets() => [Package];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => null;
}
