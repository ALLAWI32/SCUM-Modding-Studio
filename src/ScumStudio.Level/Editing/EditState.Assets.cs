using System.Globalization;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Level.Editing;

/// <summary>A net value override of a vehicle/item package.</summary>
/// <param name="Package">Package path.</param>
/// <param name="Export">Export key.</param>
/// <param name="Path">Property path.</param>
/// <param name="ValueKind">Value type name.</param>
/// <param name="Base">Value before the first edit (the stock or freshly cloned value).</param>
/// <param name="Current">Current value.</param>
public sealed record AssetValueOverride(string Package, string Export, string Path, string ValueKind, string Base, string Current);

/// <content>Vehicle/item clones and value overrides (Vehicles and Weapons modules).</content>
public sealed partial class EditState
{
    private readonly Dictionary<string, CloneAssetOp> _clones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Package, string Export, string Path), AssetValueOverride> _values = new(ValueKeyComparer.Instance);
    private readonly Dictionary<string, string> _replacements = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clones that exist, keyed by their new primary package.</summary>
    public IReadOnlyCollection<CloneAssetOp> AssetClones => _clones.Values;

    private void CopyAssets(EditState copy)
    {
        foreach (var (key, value) in _clones)
        {
            copy._clones[key] = value;
        }

        foreach (var (key, value) in _values)
        {
            copy._values[key] = value;
        }

        foreach (var (key, value) in _replacements)
        {
            copy._replacements[key] = value;
        }
    }

    /// <summary>Every net value override.</summary>
    public IReadOnlyCollection<AssetValueOverride> AssetValueOverrides => _values.Values;

    /// <summary>Packages the mod creates or overrides: the packages of every clone plus every package with a value override.</summary>
    public IReadOnlyList<string> ChangedAssets =>
        _clones.Values.SelectMany(c => c.Packages.Select(p => p.New))
            .Concat(_values.Values.Select(v => v.Package))
            .Concat(_replacements.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Stock packages drawn as another one (see <see cref="ReplaceAssetOp"/>): package → its replacement.</summary>
    public IReadOnlyDictionary<string, string> AssetReplacements => _replacements;

    /// <summary>What replaces <paramref name="package"/>, or null when it is the game's own.</summary>
    public string? GetReplacement(string package) => _replacements.GetValueOrDefault(package);

    /// <summary>The clone that created <paramref name="packagePath"/> (any package of the family), or null.</summary>
    public CloneAssetOp? FindCloneOf(string packagePath) =>
        _clones.Values.FirstOrDefault(c => c.Packages.Any(p => string.Equals(p.New, packagePath, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The current overridden value of a property, or null when it has the stock/cloned value.</summary>
    public AssetValueOverride? GetAssetValue(string package, string export, string path) =>
        _values.TryGetValue((package, export, path), out var v) ? v : null;

    /// <summary>Value overrides of one package.</summary>
    public IReadOnlyList<AssetValueOverride> GetAssetValues(string package) =>
        _values.Values.Where(v => string.Equals(v.Package, package, StringComparison.OrdinalIgnoreCase)).ToList();

    private string? ValidateAsset(EditOp op)
    {
        switch (op)
        {
            case CloneAssetOp clone:
                if (clone.Packages.Count == 0 || !string.Equals(clone.Packages[0].New, clone.NewPrimary, StringComparison.OrdinalIgnoreCase))
                {
                    return "A clone needs its primary package first.";
                }

                if (_clones.ContainsKey(clone.NewPrimary))
                {
                    return $"{clone.NewPrimary} already exists.";
                }

                var taken = _clones.Values.SelectMany(c => c.Packages.Select(p => p.New)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return clone.Packages.FirstOrDefault(p => taken.Contains(p.New)) is { } clash
                    ? $"{clash.New} is already used by another clone."
                    : null;
            case RemoveAssetCloneOp remove:
                if (!_clones.TryGetValue(remove.NewPrimary, out var existing))
                {
                    return $"{remove.NewPrimary} is not a clone.";
                }

                if (!existing.Equals(remove.Original))
                {
                    return $"{remove.NewPrimary} was created by a different operation.";
                }

                return existing.Packages.Any(p => GetAssetValues(p.New).Count > 0)
                    ? $"{remove.NewPrimary} still has edited values; reset them first."
                    : null;
            case SetAssetValueOp set:
                if (TunableValue.AreEqual(ParseKind(set.ValueKind), set.Old, set.New))
                {
                    return "The value is unchanged.";
                }

                if (_values.TryGetValue((set.Package, set.Export, set.Path), out var current)
                    && !TunableValue.AreEqual(ParseKind(set.ValueKind), current.Current, set.Old))
                {
                    return $"{set.Path} of {set.Package} is not at the expected value (edit is out of date).";
                }

                return null;
            case ReplaceAssetOp replace:
                if (string.Equals(replace.Package, replace.With, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{replace.Package} cannot replace itself.";
                }

                if (!string.Equals(GetReplacement(replace.Package), replace.Old, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{replace.Package} is not replaced as expected (edit is out of date).";
                }

                return string.Equals(replace.Old, replace.With, StringComparison.OrdinalIgnoreCase) ? "Nothing changes." : null;
            default:
                return $"Unsupported operation {op.GetType().Name}.";
        }
    }

    private void ApplyAsset(EditOp op)
    {
        switch (op)
        {
            case CloneAssetOp clone:
                _clones[clone.NewPrimary] = clone;
                break;
            case RemoveAssetCloneOp remove:
                _clones.Remove(remove.NewPrimary);
                break;
            case SetAssetValueOp set:
                var key = (set.Package, set.Export, set.Path);
                var kind = ParseKind(set.ValueKind);
                if (!_values.TryGetValue(key, out var entry))
                {
                    _values[key] = new AssetValueOverride(set.Package, set.Export, set.Path, set.ValueKind, set.Old, set.New);
                }
                else if (TunableValue.AreEqual(kind, entry.Base, set.New))
                {
                    _values.Remove(key);
                }
                else
                {
                    _values[key] = entry with { Current = set.New };
                }

                break;
            case ReplaceAssetOp replace:
                if (replace.With is null)
                {
                    _replacements.Remove(replace.Package);
                }
                else
                {
                    _replacements[replace.Package] = replace.With;
                }

                break;
        }
    }

    private IEnumerable<string> DescribeAssets() =>
        _clones.Values.Select(c => $"clone {c.Template.ToLowerInvariant()} -> {c.NewPrimary.ToLowerInvariant()} ({c.Packages.Count})")
            .Concat(_values.Values.Select(v => string.Create(CultureInfo.InvariantCulture,
                $"value {v.Package.ToLowerInvariant()}|{v.Export}|{v.Path} {v.Base} -> {v.Current}")))
            .Concat(_replacements.Select(r => $"replace {r.Key.ToLowerInvariant()} -> {r.Value.ToLowerInvariant()}"));

    private static TunableKind ParseKind(string kind) =>
        Enum.TryParse<TunableKind>(kind, ignoreCase: true, out var k) ? k : TunableKind.Text;

    private sealed class ValueKeyComparer : IEqualityComparer<(string Package, string Export, string Path)>
    {
        public static readonly ValueKeyComparer Instance = new();

        public bool Equals((string Package, string Export, string Path) x, (string Package, string Export, string Path) y) =>
            string.Equals(x.Package, y.Package, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Export, y.Export, StringComparison.Ordinal)
            && string.Equals(x.Path, y.Path, StringComparison.Ordinal);

        public int GetHashCode((string Package, string Export, string Path) obj) =>
            HashCode.Combine(obj.Package.ToUpperInvariant(), obj.Export, obj.Path);
    }
}
