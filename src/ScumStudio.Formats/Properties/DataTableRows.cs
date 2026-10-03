using System.Buffers.Binary;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Formats.Properties;

/// <summary>One row of a cooked DataTable: its name and tagged properties (offsets relative to the export payload).</summary>
/// <param name="Name">Row name.</param>
/// <param name="Offset">Where the row (its FName) starts in the payload.</param>
/// <param name="End">Just after the row's terminating <c>None</c>.</param>
/// <param name="Properties">The row struct's tagged members.</param>
public sealed record DataTableRow(string Name, int Offset, int End, IReadOnlyList<PropertyTag> Properties)
{
    /// <summary>Every member with a path (<see cref="PropertyBlock.EnumerateAll"/>).</summary>
    public IEnumerable<(string Path, PropertyTag Tag)> EnumerateAll() =>
        new PropertyBlock { Properties = Properties }.EnumerateAll();
}

/// <summary>
/// The rows of a cooked <c>DataTable</c> export (UE 4.27 <c>UDataTable::Serialize</c>): after the tagged block, the
/// object's guid flag (int32, then a guid when set), the row count, then each row as its FName and tagged members up to
/// <c>None</c>. Rows are what the game looks values up in (SCUM's melee damage: <c>WeaponDesc_Table</c>).
/// </summary>
public static class DataTableRows
{
    /// <summary>The edit path of member <paramref name="member"/> of row <paramref name="row"/> (<c>Rows[1H_KitchenKnife].Damage</c>).</summary>
    public static string PathOf(string row, string member) => $"Rows[{row}].{member}";

    /// <summary>The rows of export <paramref name="exportIndex"/> (empty when it is not a DataTable).</summary>
    public static IReadOnlyList<DataTableRow> Read(CookedPackage package, int exportIndex)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.GetExportClassName(exportIndex) == "DataTable" ? Read(package, package.GetExportData(exportIndex).ToArray(), exportIndex) : [];
    }

    /// <summary>The rows in a DataTable export payload (e.g. an edited copy whose name table <paramref name="names"/> has grown).</summary>
    /// <exception cref="FormatException">The payload does not hold readable rows.</exception>
    public static IReadOnlyList<DataTableRow> Read(CookedPackage package, byte[] payload, int exportIndex = -1, IReadOnlyList<string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(payload);
        names ??= package.Names;
        var (countAt, _) = Layout(package, payload, exportIndex);
        var r = new ByteReader(payload) { Position = countAt };
        var count = r.I32();
        if (count < 0 || count > payload.Length)
        {
            throw new FormatException($"{package.BasePath}: implausible DataTable row count {count}.");
        }

        var reader = new PropertyReader(names, package.ResolveIndex, payload, 0);
        var rows = new List<DataTableRow>(count);
        for (var i = 0; i < count; i++)
        {
            var start = r.Position;
            var name = r.FName().Format(names);
            var props = reader.ReadTaggedList(r);
            rows.Add(new DataTableRow(name, start, r.Position, props));
        }

        return rows;
    }

    /// <summary>
    /// The payload with a row added as a copy of <paramref name="sourceRow"/> (after the last row);
    /// the row name is <paramref name="newRowName"/> in the (possibly grown) name table <paramref name="names"/>.
    /// </summary>
    /// <param name="package">The package the payload belongs to.</param>
    /// <param name="payload">The DataTable export's payload.</param>
    /// <param name="exportIndex">The DataTable export.</param>
    /// <param name="sourceRow">The row to copy.</param>
    /// <param name="newRowName">The copy's row name.</param>
    /// <param name="names">The package's name table.</param>
    /// <param name="edit">Changes the copied row's bytes before it goes in (offsets: the source row's tag offsets minus its <see cref="DataTableRow.Offset"/>).</param>
    /// <exception cref="InvalidOperationException">The source row is missing or the new row exists.</exception>
    public static byte[] AddRowCopy(CookedPackage package, byte[] payload, int exportIndex, string sourceRow, FNameRef newRowName, IReadOnlyList<string> names,
        Func<byte[], DataTableRow, byte[]>? edit = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(names);
        var newRow = newRowName.Format(names);
        var rows = Read(package, payload, exportIndex, names);
        var source = rows.FirstOrDefault(r => string.Equals(r.Name, sourceRow, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{package.BasePath}: no row '{sourceRow}' to copy.");
        if (rows.Any(r => string.Equals(r.Name, newRow, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{package.BasePath}: row '{newRow}' exists already.");
        }

        var entry = payload.AsSpan(source.Offset, source.End - source.Offset).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(entry, newRowName.Index);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), newRowName.Number);
        if (edit is not null)
        {
            entry = edit(entry, source);
        }
        var at = rows[^1].End;
        var result = new byte[payload.Length + entry.Length];
        payload.AsSpan(0, at).CopyTo(result);
        entry.CopyTo(result, at);
        payload.AsSpan(at).CopyTo(result.AsSpan(at + entry.Length));
        var (countAt, _) = Layout(package, payload, exportIndex);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(countAt), rows.Count + 1);
        return result;
    }

    /// <summary>Where the row count is, after the tagged block and the guid flag.</summary>
    private static (int CountAt, int BlockEnd) Layout(CookedPackage package, byte[] payload, int exportIndex)
    {
        var block = PropertyReader.ReadPayload(package, payload, exportIndex);
        var r = new ByteReader(payload) { Position = block.EndOffset };
        if (r.I32() != 0)
        {
            r.Skip(16); // the object's guid
        }

        return (r.Position, block.EndOffset);
    }
}
