using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Modding.Tuning;

/// <summary>
/// New rows in a cooked DataTable, each a copy of an existing row under a new name: a cloned melee weapon looks its
/// damage up in <c>WeaponDesc_Table</c> by its own name, so the clone needs a row of its own (its template's values).
/// </summary>
public static class DataTableEdits
{
    /// <summary>The game's melee weapon table (rows named after the weapon: <c>1H_KitchenKnife</c>).</summary>
    public const string WeaponDescTable = "/Game/ConZ_Files/Data/WeaponDesc_Table";

    /// <summary>Adds each (source row, new row) copy to the package's DataTable; a new row that exists already is left as it is.</summary>
    /// <exception cref="InvalidOperationException">The package has no DataTable or a source row is missing.</exception>
    public static PackageBytes AddRowCopies(CookedPackage package, IReadOnlyList<(string Source, string New)> copies)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(copies);
        var exportIndex = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.GetExportClassName(i) == "DataTable", -1);
        if (exportIndex < 0)
        {
            throw new InvalidOperationException($"{package.BasePath}: no DataTable.");
        }

        var names = package.Names.ToList();
        var wide = package.NameEntries.Select(n => n.IsWide).ToList();
        var payloads = Enumerable.Range(0, package.Exports.Count).Select(i => package.GetExportData(i).ToArray()).ToArray();
        foreach (var (source, name) in copies)
        {
            if (DataTableRows.Read(package, payloads[exportIndex], exportIndex, names).Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var index = names.IndexOf(name);
            if (index < 0)
            {
                index = names.Count;
                names.Add(name);
                wide.Add(!name.All(char.IsAscii));
            }

            payloads[exportIndex] = DataTableRows.AddRowCopy(package, payloads[exportIndex], exportIndex, source, new FNameRef(index, 0), names);
        }

        return PackageWriter.Build(PackageWriter.ToBuildInput(package) with
        {
            Names = names,
            NameIsWide = wide,
            ExportData = payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray(),
        });
    }
}
