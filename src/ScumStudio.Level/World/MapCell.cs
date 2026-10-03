using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ScumStudio.Level.World;

/// <summary>
/// One of the 25 map cells of The_Island: a column letter (<c>A</c>, <c>B</c>, <c>C</c>, <c>D</c> or <c>Z</c>) and a
/// row (0-4). Sublevel names start with it (<c>A_0_Outpost</c>, <c>Landscape_B_2_3c</c>, <c>TV_Base_Z_1</c>).
/// </summary>
/// <param name="Column">Upper-case column letter.</param>
/// <param name="Row">Row, 0-4.</param>
public readonly record struct MapCell(char Column, int Row) : IComparable<MapCell>
{
    /// <summary>Valid column letters in sort order.</summary>
    public static IReadOnlyList<char> Columns { get; } = ['A', 'B', 'C', 'D', 'Z'];

    /// <summary>Number of rows per column.</summary>
    public const int RowCount = 5;

    /// <summary>All 25 cells, column-major (A_0, A_1, ... Z_4).</summary>
    public static IEnumerable<MapCell> All =>
        Columns.SelectMany(c => Enumerable.Range(0, RowCount).Select(r => new MapCell(c, r)));

    /// <summary>True when <see cref="Column"/> and <see cref="Row"/> name one of the 25 cells.</summary>
    public bool IsValid => IsValidColumn(Column) && Row is >= 0 and < RowCount;

    /// <summary>True when <paramref name="column"/> is one of <see cref="Columns"/> (case-insensitive).</summary>
    public static bool IsValidColumn(char column) => Columns.Contains(char.ToUpperInvariant(column));

    /// <summary>
    /// Parses <c>A_0</c>, <c>A0</c>, <c>a-0</c> or <c>A 0</c> (case-insensitive). Returns false for anything else or an
    /// out-of-range cell.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out MapCell cell)
    {
        cell = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        char column;
        char row;
        if (s.Length == 2)
        {
            (column, row) = (s[0], s[1]);
        }
        else if (s.Length == 3 && s[1] is '_' or '-' or ' ')
        {
            (column, row) = (s[0], s[2]);
        }
        else
        {
            return false;
        }

        if (!IsValidColumn(column) || row is < '0' or > '4')
        {
            return false;
        }

        cell = new MapCell(char.ToUpperInvariant(column), row - '0');
        return true;
    }

    /// <summary>Parses a cell (see <see cref="TryParse"/>).</summary>
    /// <exception cref="FormatException">Not a valid cell.</exception>
    public static MapCell Parse(string text) =>
        TryParse(text, out var cell)
            ? cell
            : throw new FormatException($"'{text}' is not a map cell (expected A_0 .. D_4 or Z_0 .. Z_4).");

    /// <inheritdoc />
    public int CompareTo(MapCell other)
    {
        var c = Column.CompareTo(other.Column);
        return c != 0 ? c : Row.CompareTo(other.Row);
    }

    /// <summary>The canonical <c>A_0</c> form.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Column}_{Row}");
}
