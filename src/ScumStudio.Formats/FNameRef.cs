namespace ScumStudio.Formats;

/// <summary>
/// A serialized FName: index into the package name table plus instance number
/// (0 = no suffix; n &gt; 0 = displayed as <c>Base_{n-1}</c>).
/// </summary>
/// <param name="Index">Name table index.</param>
/// <param name="Number">Instance number (stored value, i.e. suffix + 1).</param>
public readonly record struct FNameRef(int Index, int Number = 0)
{
    /// <summary>
    /// Formats a name the way <c>ue4pkg.Pkg.nm</c> does: the base string, or <c>base_{Number-1}</c> when numbered.
    /// Out-of-range indices yield <c>&lt;bad i&gt;</c>.
    /// </summary>
    public string Format(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var baseName = Index >= 0 && Index < names.Count ? names[Index] : $"<bad {Index}>";
        return Number == 0 ? baseName : $"{baseName}_{Number - 1}";
    }

    /// <summary>
    /// Resolves a display string to an FName reference against a name table (port of the <c>NI</c> helper in
    /// <c>ue4write.py build_package</c>): an exact match wins; otherwise <c>Foo_3</c> maps to (<c>Foo</c>, 4)
    /// when <c>Foo</c> exists and the suffix has no leading zero.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The name is not in the table.</exception>
    public static FNameRef Resolve(IReadOnlyDictionary<string, int> nameIndex, string name)
    {
        ArgumentNullException.ThrowIfNull(nameIndex);
        ArgumentNullException.ThrowIfNull(name);
        if (nameIndex.TryGetValue(name, out var exact))
        {
            return new FNameRef(exact, 0);
        }

        var underscore = name.LastIndexOf('_');
        if (underscore >= 0)
        {
            var head = name[..underscore];
            var tail = name[(underscore + 1)..];
            if (tail.Length > 0 && tail.All(char.IsAsciiDigit) && !(tail != "0" && tail.StartsWith('0'))
                && nameIndex.TryGetValue(head, out var baseIndex)
                && int.TryParse(tail, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                return new FNameRef(baseIndex, n + 1);
            }
        }

        throw new KeyNotFoundException($"Name '{name}' is not in the package name table.");
    }

    /// <summary>
    /// <c>FName</c>'s split of a trailing number (UE 4.27 <c>ParseNumber</c>): "_" then up to 10 digits, not starting with 0
    /// unless it is the only digit, below int.MaxValue; the stored number is that value + 1 (0 = no number). The cooker
    /// stores <c>ConcreteBlock002_1</c> as ("ConcreteBlock002", 2): a writer that adds the whole text with number 0 makes a
    /// different FName for the same object.
    /// </summary>
    public static (string Text, int Number) Split(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[value.Length - 1 - digits]))
        {
            digits++;
        }

        var first = value.Length - digits;
        if (digits == 0 || digits >= value.Length || digits > 10 || value[first - 1] != '_' || (digits > 1 && value[first] == '0')
            || !long.TryParse(value.AsSpan(first), out var n) || n >= int.MaxValue)
        {
            return (value, 0);
        }

        return (value[..(first - 1)], (int)n + 1);
    }
}
