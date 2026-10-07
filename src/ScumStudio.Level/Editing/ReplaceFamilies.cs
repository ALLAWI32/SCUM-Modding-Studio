using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Export;

namespace ScumStudio.Level.Editing;

/// <summary>One thing an object can be replaced with (see <see cref="ReplaceFamilies"/>).</summary>
/// <param name="Name">Its package name (<c>SM_Road_Asphalt_01</c>).</param>
/// <param name="PackagePath">Its package path.</param>
/// <param name="IsBlueprint">True for a Blueprint class (a building), false for a static mesh.</param>
/// <param name="IsCurrent">True for the one the object has now.</param>
public sealed record ReplaceChoice(string Name, string PackagePath, bool IsBlueprint, bool IsCurrent)
{
    /// <summary>The object path: a mesh's <c>/Game/X/SM_A.SM_A</c>, a Blueprint's class <c>/Game/X/BP_A.BP_A_C</c>.</summary>
    public string ObjectPath => PackagePath + "." + Name + (IsBlueprint ? "_C" : string.Empty);
}

/// <summary>Where the alternatives of an object live: under <see cref="Folder"/>, and when <see cref="Segment"/> is set in a folder of that name below it (the foliage's trees of every biome).</summary>
/// <param name="Folder">The family's root folder.</param>
/// <param name="Segment">A folder name the path must pass through below the root, or null.</param>
public sealed record ReplaceFamily(string Folder, string? Segment = null)
{
    /// <summary>True when <paramref name="packagePath"/> belongs to the family.</summary>
    public bool Contains(string packagePath) =>
        packagePath.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase)
        && (Segment is null || packagePath[Folder.Length..].Contains("/" + Segment + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary><c>/Game/.../Road</c> or <c>/Game/.../Foliage/*/Trees</c>.</summary>
    public override string ToString() => Segment is null ? Folder : Folder + "/*/" + Segment;
}

/// <summary>
/// The Replace tool's "same family" rule (owner, FiveM-editor style: roads for a road piece, bridges for a bridge,
/// buildings of the same family for a building, walls for a wall, the foliage's trees for a tree): the candidates are
/// the packages of the same kind in the same family, name-sorted, the current one first and marked. The game's folders
/// decide: <c>Models/Road/**</c> (bridges included), <c>Models/Buildings/&lt;family&gt;/**</c>,
/// <c>Objects/Outdoor/Fence/**</c>, <c>Foliage/*/Trees</c> (every biome's trees, bushes or grass), else the parent folder.
/// </summary>
public static class ReplaceFamilies
{
    // ponytail: folder names (singular or plural) and how many segments below them make a family; the parent folder otherwise.
    private static readonly Dictionary<string, int> Anchors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Road"] = 0,
        ["Bridge"] = 0,
        ["Wall"] = 0,
        ["Fence"] = 0,
        ["Building"] = 1,
        ["Foliage"] = 1,
    };

    private static readonly HashSet<string> FoliageKinds = new(StringComparer.OrdinalIgnoreCase) { "Tree", "Trees", "Bush", "Bushes", "Grass", "Rock", "Rocks" };

    /// <summary>The family of <paramref name="path"/> (a package, object or class path).</summary>
    public static ReplaceFamily Family(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var package = AssetPaths.SplitObjectPath(path).PackagePath;
        var segments = package.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!Anchors.TryGetValue(segments[i].TrimEnd('s', 'S'), out var below) || i + below >= segments.Length - 1)
            {
                continue;
            }

            if (segments[i].Equals("Foliage", StringComparison.OrdinalIgnoreCase) && segments.Skip(i + 1).Take(segments.Length - i - 2).FirstOrDefault(FoliageKinds.Contains) is { } kind)
            {
                return new ReplaceFamily("/" + string.Join('/', segments[..(i + 1)]), kind);
            }

            return new ReplaceFamily("/" + string.Join('/', segments[..(i + below + 1)]));
        }

        return new ReplaceFamily(segments.Length > 1 ? "/" + string.Join('/', segments[..^1]) : "/");
    }

    /// <summary>
    /// What <paramref name="current"/> (a mesh object path or a Blueprint class path) can be replaced with among
    /// <paramref name="packages"/>: same kind, same family, no far-view models; sorted by name, the current one first.
    /// </summary>
    public static IReadOnlyList<ReplaceChoice> Candidates(string current, IEnumerable<DumpPackage> packages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(current);
        ArgumentNullException.ThrowIfNull(packages);
        var package = AssetPaths.SplitObjectPath(current).PackagePath;
        var blueprint = current.EndsWith("_C", StringComparison.Ordinal);
        var family = Family(current);
        var choices = packages
            .Where(p => IsKind(p.ClassName, blueprint) && family.Contains(p.PackagePath) && !FarModels.IsFarViewMesh(p.PackagePath) && !FarModels.IsUndersideMesh(p.PackagePath))
            .Select(p => new ReplaceChoice(p.PackagePath[(p.PackagePath.LastIndexOf('/') + 1)..], p.PackagePath, blueprint, string.Equals(p.PackagePath, package, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(c => c.PackagePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.IsCurrent ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (choices.Count > 0 && !choices[0].IsCurrent)
        {
            choices.Insert(0, new ReplaceChoice(package[(package.LastIndexOf('/') + 1)..], package, blueprint, true));
        }

        return choices;
    }

    private static bool IsKind(string className, bool blueprint) =>
        blueprint ? className is "Blueprint" or "BlueprintGeneratedClass" : className == "StaticMesh";

    /// <summary>
    /// The scale that fits a replacement to the object it replaces (owner: "length, height, area and curves of the road").
    /// A long piece (a road, bridge, wall or fence, <paramref name="isLong"/>) keeps its length and height: the new mesh is
    /// scaled along the long axis to the old length and in Z to the old height, its width stays unless it differs by more
    /// than 30 %. Anything else keeps the scale of 1 unless the new mesh's footprint is very different (more than twice or
    /// less than half as big), then it is scaled evenly to the old footprint.
    /// </summary>
    /// <param name="original">Bounds of the replaced mesh.</param>
    /// <param name="scale">The replaced object's scale.</param>
    /// <param name="candidate">Bounds of the new mesh.</param>
    /// <param name="isLong">True for a long piece.</param>
    public static FVector FitScale(BoundingBox original, FVector scale, BoundingBox candidate, bool isLong)
    {
        if (original.IsEmpty || candidate.IsEmpty)
        {
            return scale;
        }

        var old = original.Size * new Vector3(MathF.Abs(scale.X), MathF.Abs(scale.Y), MathF.Abs(scale.Z));
        var size = candidate.Size;
        static float Ratio(float wanted, float have) => have > 0.001f && wanted > 0.001f ? wanted / have : 1f;
        FVector fitted;
        if (isLong)
        {
            var alongY = old.Y > old.X;
            var length = alongY ? Ratio(old.Y, size.Y) : Ratio(old.X, size.X);
            var width = alongY ? Ratio(old.X, size.X) : Ratio(old.Y, size.Y);
            width = MathF.Abs(width - 1f) > 0.3f ? width : 1f;
            var height = Ratio(old.Z, size.Z);
            fitted = alongY ? new FVector(width, length, height) : new FVector(length, width, height);
        }
        else
        {
            var footprint = Ratio(MathF.Max(old.X, old.Y), MathF.Max(size.X, size.Y));
            fitted = footprint > 2f || footprint < 0.5f ? new FVector(footprint) : FVector.One;
        }

        // A mirrored piece (negative scale) stays mirrored.
        return new FVector(MathF.CopySign(fitted.X, scale.X), MathF.CopySign(fitted.Y, scale.Y), MathF.CopySign(fitted.Z, scale.Z));
    }
}
