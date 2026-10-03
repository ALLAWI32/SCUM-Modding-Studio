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
}
