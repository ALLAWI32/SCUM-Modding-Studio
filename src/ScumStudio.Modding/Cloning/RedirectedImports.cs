using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Modding.Cloning;

/// <summary>
/// A redirect (<see cref="ClonePlan.Redirects"/>) points a clone's imports of one package at another package. When that one
/// is another class (the large cabinet's element given the fridge to spawn), the cooker's list of the old class's default
/// subobjects comes along: the element imports the cabinet item's <c>BuriableComponent</c>, <c>ThrowingComponent</c>,
/// <c>ChestPaintJobItem_GEN_VARIABLE</c> and <c>SkinnableBaseBuildingElementItem_GEN_VARIABLE</c>, which the fridge has no
/// object for, so after the redirect they name nothing (the game would get null there, and the export's package check
/// refuses the mod). Nothing in the package refers to such an import (no property, outer, export or preload dependency
/// holds its index: checked byte by byte below). It is rewritten in place to the default object of its own native class
/// (<c>/Script/SCUM.Default__BuriableChestItemComponent</c>), which always exists: no index moves and no payload changes.
/// </summary>
public static class RedirectedImports
{
    /// <summary>
    /// Rewrites, in <paramref name="clone"/>, every import of an object that <paramref name="exists"/> says its package lacks
    /// (false; null = unknown, left alone) and that nothing refers to. Returns the package unchanged when there is none.
    /// </summary>
    /// <param name="clone">The cloned package (its redirects applied).</param>
    /// <param name="exists">For an object path <c>/Game/Pkg.Outer.Inner</c>: true when that package has it, false when it has not, null when unknown.</param>
    /// <param name="rewritten">The object paths rewritten, each as <c>old -> new</c>.</param>
    public static PackageBytes? Settle(CookedPackage clone, Func<string, bool?> exists, out IReadOnlyList<string> rewritten)
    {
        ArgumentNullException.ThrowIfNull(clone);
        ArgumentNullException.ThrowIfNull(exists);
        var done = new List<string>();
        rewritten = done;
        var missing = new List<int>();
        for (var i = 0; i < clone.Imports.Count; i++)
        {
            var import = clone.Imports[i];
            var path = clone.GetFullPath(-(i + 1));
            if (import.OuterIndex != 0 && path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)
                && clone.ResolveName(import.ClassPackage).StartsWith("/Script/", StringComparison.Ordinal)
                && exists(path) == false)
            {
                missing.Add(i);
            }
        }

        if (missing.Count == 0)
        {
            return null;
        }

        var referenced = Referenced(clone);
        var input = PackageWriter.ToBuildInput(clone);
        var names = input.Names.ToList();
        var wide = (input.NameIsWide ?? input.Names.Select(_ => false).ToArray()).ToList();
        var imports = input.Imports.ToList();
        FNameRef Name(string text)
        {
            var (plain, number) = FNameRef.Split(text);
            var at = names.IndexOf(plain);
            if (at < 0)
            {
                at = names.Count;
                names.Add(plain);
                wide.Add(!plain.All(char.IsAscii));
            }

            return new FNameRef(at, number);
        }

        foreach (var i in missing.Where(i => !referenced.Contains(-(i + 1))))
        {
            var old = imports[i];
            var classPackage = clone.ResolveName(old.ClassPackage);
            var className = clone.ResolveName(old.ClassName);
            var package = imports.FindIndex(im => im.OuterIndex == 0 && string.Equals(im.ClassName.Format(names), "Package", StringComparison.Ordinal)
                                                  && string.Equals(im.ObjectName.Format(names), classPackage, StringComparison.OrdinalIgnoreCase));
            if (package < 0)
            {
                imports.Add(new ImportEntry(Name("/Script/CoreUObject"), Name("Package"), 0, Name(classPackage)));
                package = imports.Count - 1;
            }

            imports[i] = new ImportEntry(old.ClassPackage, old.ClassName, -(package + 1), Name("Default__" + className));
            done.Add($"{clone.GetFullPath(-(i + 1))} -> {classPackage}.Default__{className}");
        }

        if (done.Count == 0)
        {
            return null;
        }

        var bytes = PackageWriter.Build(input with { Names = names, NameIsWide = wide, NameHashOverride = null, Imports = imports });
        var check = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, clone.BasePath);
        if (check.Exports.Count != clone.Exports.Count || Enumerable.Range(0, clone.Exports.Count).Any(e => !check.GetExportData(e).Span.SequenceEqual(clone.GetExportData(e).Span)))
        {
            throw new InvalidOperationException($"{clone.BasePath}: rewriting the imports the redirect left without an object changed an export's data.");
        }

        return bytes;
    }

    // Every FPackageIndex the package holds outside the import entries' own names: import outers, the export table, the
    // preload dependencies, and any 4 bytes of any export's data that read as an import index (a false match only keeps an
    // import as it is).
    private static HashSet<int> Referenced(CookedPackage package)
    {
        var held = new HashSet<int>();
        foreach (var import in package.Imports)
        {
            held.Add(import.OuterIndex);
        }

        foreach (var export in package.Exports)
        {
            held.UnionWith([export.ClassIndex, export.SuperIndex, export.TemplateIndex, export.OuterIndex]);
        }

        held.UnionWith(package.ReadPreloadDependencies());
        var lowest = -package.Imports.Count;
        for (var e = 0; e < package.Exports.Count; e++)
        {
            var data = package.GetExportData(e).Span;
            for (var at = 0; at + 4 <= data.Length; at++)
            {
                var value = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
                if (value < 0 && value >= lowest)
                {
                    held.Add(value);
                }
            }
        }

        return held;
    }
}
