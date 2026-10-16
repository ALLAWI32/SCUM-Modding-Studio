using System.Globalization;
using System.Text;

namespace ScumStudio.App.Services;

/// <summary>
/// The dedicated server's <c>ServerSettings.ini</c> (<c>SCUM\Saved\Config\WindowsServer</c>, written by the server on its
/// first start): <c>scum.Key=value</c> lines in <c>[Sections]</c>. Saving changes only the values of the keys given; every
/// other line (comments, order, unknown keys, line endings, a byte order mark) stays byte for byte as it was. Before the
/// first save ever, the file is kept as <c>ServerSettings.ini.bak</c> (the server owner's original, never overwritten);
/// before the first save of each session, as <c>ServerSettings.ini.session.bak</c> (the state before this session's edits).
/// </summary>
public sealed class ServerSettingsFile
{
    /// <summary>The file name.</summary>
    public const string FileName = "ServerSettings.ini";

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private readonly Dictionary<string, string> _values;
    private bool _backedUp;

    private ServerSettingsFile(string path, Dictionary<string, string> values)
    {
        FilePath = path;
        _values = values;
    }

    /// <summary>Full path of the file.</summary>
    public string FilePath { get; }

    /// <summary>Every <c>key=value</c> read (keys as written, e.g. <c>scum.MaxAllowedPuppets</c>).</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>
    /// <c>ServerSettings.ini</c> of the server whose Paks folder is <paramref name="serverPaks"/>
    /// (<c>...\SCUM\Content\Paks</c> → <c>...\SCUM\Saved\Config\WindowsServer\ServerSettings.ini</c>); null when unknown.
    /// </summary>
    public static string? PathFor(string? serverPaks)
    {
        if (string.IsNullOrWhiteSpace(serverPaks))
        {
            return null;
        }

        var scum = Directory.GetParent(Path.GetFullPath(serverPaks).TrimEnd('\\', '/'))?.Parent;
        return scum is null ? null : Path.Combine(scum.FullName, "Saved", "Config", "WindowsServer", FileName);
    }

    /// <summary>Reads <paramref name="path"/>.</summary>
    public static ServerSettingsFile Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (line, _) in Lines(Read(path).Text))
        {
            if (Split(line) is { } kv)
            {
                values[kv.Key] = kv.Value;
            }
        }

        return new ServerSettingsFile(path, values);
    }

    /// <summary>The value of <paramref name="key"/>, or null.</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary>The key as the file writes it for <paramref name="name"/>: <c>scum.Name</c> or a bare <c>Name</c>; null when absent.</summary>
    public string? Find(string name) =>
        _values.ContainsKey("scum." + name) ? _values.Keys.First(k => k.Equals("scum." + name, StringComparison.OrdinalIgnoreCase))
        : _values.ContainsKey(name) ? _values.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))
        : null;

    /// <summary>
    /// Writes <paramref name="changes"/> (key → new value text) into the file: an existing key's value is replaced on its
    /// own line, a missing one is appended after the last value of <paramref name="section"/> (created at the end when absent).
    /// </summary>
    public void Save(IReadOnlyDictionary<string, string> changes, string section = "World")
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        if (!_backedUp)
        {
            if (!File.Exists(FilePath + ".bak"))
            {
                File.Copy(FilePath, FilePath + ".bak");
            }

            File.Copy(FilePath, FilePath + ".session.bak", overwrite: true);
            _backedUp = true;
        }

        var (bom, text) = Read(FilePath);
        var lines = Lines(text).ToList();
        var newline = lines.Select(l => l.End).FirstOrDefault(e => e.Length > 0) ?? "\r\n";
        var left = new Dictionary<string, string>(changes, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            var (line, end) = lines[i];
            if (Split(line) is { } kv && left.Remove(kv.Key, out var value))
            {
                // Everything up to the '=' (and the spaces after it) stays as written.
                var eq = line.IndexOf('=', StringComparison.Ordinal) + 1;
                while (eq < line.Length && line[eq] is ' ' or '\t')
                {
                    eq++;
                }

                lines[i] = (line[..eq] + value, end);
            }
        }

        if (left.Count > 0)
        {
            var header = "[" + section + "]";
            var at = lines.FindIndex(l => l.Line.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                EndLast(lines, newline);
                lines.Add((header, newline));
                at = lines.Count - 1;
            }

            var last = at;
            for (var i = at + 1; i < lines.Count && !lines[i].Line.TrimStart().StartsWith('['); i++)
            {
                last = lines[i].Line.Trim().Length > 0 ? i : last;
            }

            if (last == lines.Count - 1)
            {
                EndLast(lines, newline);
            }

            lines.InsertRange(last + 1, left.Select(kv => (kv.Key + "=" + kv.Value, newline)));
        }

        var bytes = new UTF8Encoding(false).GetBytes(string.Concat(lines.Select(l => l.Line + l.End)));
        File.WriteAllBytes(FilePath, bom ? [.. Bom, .. bytes] : bytes);
        foreach (var (key, value) in changes)
        {
            _values[key] = value;
        }
    }

    /// <summary>Formats a number the way the server writes it (<c>1.000000</c> for floats).</summary>
    public static string Format(double value, bool isInteger) =>
        isInteger ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.000000", CultureInfo.InvariantCulture);

    private static (bool Bom, string Text) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.AsSpan().StartsWith(Bom);
        return (bom, new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)));
    }

    /// <summary>The lines with their own endings (<c>\r\n</c>, <c>\n</c>, <c>\r</c>, or none for a last line without one).</summary>
    private static IEnumerable<(string Line, string End)> Lines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                var end = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : text[i].ToString();
                yield return (text[start..i], end);
                i += end.Length - 1;
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return (text[start..], string.Empty);
        }
    }

    private static void EndLast(List<(string Line, string End)> lines, string newline)
    {
        if (lines.Count > 0 && lines[^1].End.Length == 0)
        {
            lines[^1] = (lines[^1].Line, newline);
        }
    }

    private static (string Key, string Value)? Split(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] is '[' or ';' or '#')
        {
            return null;
        }

        var eq = text.IndexOf('=', StringComparison.Ordinal);
        return eq > 0 ? (text[..eq].Trim(), text[(eq + 1)..].Trim()) : null;
    }
}
