using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Modding.Tuning;

/// <summary>A row to add to a DataTable: a copy of <paramref name="Source"/> named <paramref name="New"/>.</summary>
/// <param name="Source">The row copied (its values are the new row's).</param>
/// <param name="New">The new row's name.</param>
public sealed record RowCopy(string Source, string New)
{
    /// <summary>A soft class/object member pointed elsewhere (<c>TradeableClass</c> → the clone's class), or null.</summary>
    public (string Member, string Path)? SoftPath { get; init; }

    /// <summary>A text member replaced (<c>TradingEntryCaption</c> → the clone's name in the trade menu), or null.</summary>
    public (string Member, string Text)? Caption { get; init; }
}

/// <summary>
/// New rows in a cooked DataTable, each a copy of an existing row under a new name: a cloned melee weapon looks its
/// damage up in <c>WeaponDesc_Table</c> by its own name, and a clone sold by the traders needs a row of its own in
/// <c>Table_TradeableDesc</c> (its class and its name in the trade menu).
/// </summary>
public static class DataTableEdits
{
    /// <summary>The game's melee weapon table (rows named after the weapon: <c>1H_KitchenKnife</c>).</summary>
    public const string WeaponDescTable = "/Game/ConZ_Files/Data/WeaponDesc_Table";

    /// <summary>What the traders sell (rows named after the class: <c>BPC_Rager_C</c>, <c>Weapon_AK47_C</c>).</summary>
    public const string TradeableTable = "/Game/ConZ_Files/Economy/Table_TradeableDesc";

    /// <summary>Adds each (source row, new row) copy; a new row that exists already is left as it is.</summary>
    /// <exception cref="InvalidOperationException">The package has no DataTable or a source row is missing.</exception>
    public static PackageBytes AddRowCopies(CookedPackage package, IReadOnlyList<(string Source, string New)> copies) =>
        AddRowCopies(package, copies.Select(c => new RowCopy(c.Source, c.New)).ToList());

    /// <summary>Adds each row copy (with its class path and caption changed when given); a new row that exists already is left as it is.</summary>
    /// <exception cref="InvalidOperationException">The package has no DataTable or a source row is missing.</exception>
    public static PackageBytes AddRowCopies(CookedPackage package, IReadOnlyList<RowCopy> copies)
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
        FNameRef Name(string name)
        {
            var index = names.IndexOf(name);
            if (index < 0)
            {
                index = names.Count;
                names.Add(name);
                wide.Add(!name.All(char.IsAscii));
            }

            return new FNameRef(index, 0);
        }

        var payloads = Enumerable.Range(0, package.Exports.Count).Select(i => package.GetExportData(i).ToArray()).ToArray();
        foreach (var copy in copies)
        {
            if (DataTableRows.Read(package, payloads[exportIndex], exportIndex, names).Any(r => string.Equals(r.Name, copy.New, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var rowName = Name(copy.New);
            var soft = copy.SoftPath is { } s ? Name(s.Path) : (FNameRef?)null;
            payloads[exportIndex] = DataTableRows.AddRowCopy(package, payloads[exportIndex], exportIndex, copy.Source, rowName, names, (entry, source) =>
            {
                // Same-size edits first (they move nothing), then the caption, which changes the row's length.
                if (copy.SoftPath is { } path && soft is { } pathName
                    && source.Properties.FirstOrDefault(t => t.Name == path.Member && t.Value is SoftObjectValue) is { } softTag)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(softTag.ValueOffset - source.Offset), pathName.Index);
                    BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(softTag.ValueOffset - source.Offset + 4), pathName.Number);
                }

                if (copy.Caption is { } caption && source.Properties.FirstOrDefault(t => t.Name == caption.Member && t.Value is TextValue) is { } textTag)
                {
                    var text = TunablePatcher.EncodeInvariantText(caption.Text);
                    var at = textTag.ValueOffset - source.Offset;
                    var result = new byte[entry.Length - textTag.Size + text.Length];
                    entry.AsSpan(0, at).CopyTo(result);
                    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(textTag.SizeFieldOffset - source.Offset), text.Length);
                    text.CopyTo(result, at);
                    entry.AsSpan(at + textTag.Size).CopyTo(result.AsSpan(at + text.Length));
                    entry = result;
                }

                return entry;
            });
        }

        return PackageWriter.Build(PackageWriter.ToBuildInput(package) with
        {
            Names = names,
            NameIsWide = wide,
            ExportData = payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray(),
        });
    }
}
