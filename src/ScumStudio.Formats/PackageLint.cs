using ScumStudio.Formats.Packages;

namespace ScumStudio.Formats;

/// <summary>
/// FName checks on a written package that payload identity cannot see. A package imported under two FNames
/// ("X_2" number 0 and "X" number 3) is loaded twice by the game, which crashed it at checkpoint 46; a name the cooker
/// would have split (<see cref="FNameRef.Split"/>) but that is stored whole is a different FName from the one the game asks for.
/// </summary>
public static class PackageLint
{
    /// <summary>What is wrong with <paramref name="package"/>'s FNames (empty when nothing is).</summary>
    public static IReadOnlyList<string> Check(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var names = package.Names;
        var findings = new List<string>();
        var where = package.BasePath ?? "package";

        var packages = package.Imports
            .Where(im => im.OuterIndex == 0 && string.Equals(im.ClassName.Format(names), "Package", StringComparison.Ordinal))
            .GroupBy(im => im.ObjectName.Format(names), StringComparer.OrdinalIgnoreCase);
        foreach (var group in packages)
        {
            // FNames compare without case: ("X", 3) and ("x", 3) are one FName, ("X_2", 0) and ("X", 3) are two.
            if (group.Select(im => (Text: names[im.ObjectName.Index].ToUpperInvariant(), im.ObjectName.Number)).Distinct().Count() > 1)
            {
                findings.Add($"{where}: {group.Key} is imported under {group.Count()} different FNames; the game would load it twice.");
            }
        }

        var objectNames = package.Imports.Select(im => (Kind: "import", Name: im.ObjectName))
            .Concat(package.Exports.Select(e => (Kind: "export", Name: e.ObjectName)));
        foreach (var (kind, name) in objectNames)
        {
            if (name.Number != 0 || name.Index < 0 || name.Index >= names.Count)
            {
                continue;
            }

            var (text, number) = FNameRef.Split(names[name.Index]);
            if (number != 0)
            {
                findings.Add($"{where}: the {kind} {names[name.Index]} is stored whole with number 0; the cooker stores it as {text} number {number}.");
            }
        }

        return findings.Distinct(StringComparer.Ordinal).ToList();
    }
}
