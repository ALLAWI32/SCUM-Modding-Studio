using System.Globalization;
using System.Text;

namespace ScumStudio.App.Services;

/// <summary>
/// The dedicated server's <c>ServerSettings.ini</c> (<c>SCUM\Saved\Config\WindowsServer</c>, written by the server on its
/// first start): <c>scum.Key=value</c> lines in <c>[Sections]</c>. Saving changes only the values of the keys given and keeps
/// every other line (comments, order, unknown keys) as it was; the file is backed up once as <c>ServerSettings.ini.bak</c>.
/// </summary>
public sealed class ServerSettingsFile
{
    /// <summary>The file name.</summary>
    public const string FileName = "ServerSettings.ini";

    private readonly Dictionary<string, string> _values;

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
        foreach (var line in File.ReadAllLines(path))
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

    /// <summary>
    /// Writes <paramref name="changes"/> (key → new value text) into the file: an existing key's value is replaced on its
    /// own line, a missing one is appended to <paramref name="section"/> (created at the end when absent).
    /// </summary>
    public void Save(IReadOnlyDictionary<string, string> changes, string section = "World")
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        var backup = FilePath + ".bak";
        if (!File.Exists(backup))
        {
            File.Copy(FilePath, backup);
        }

        var lines = File.ReadAllLines(FilePath).ToList();
        var left = new Dictionary<string, string>(changes, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            if (Split(lines[i]) is { } kv && left.Remove(kv.Key, out var value))
            {
                lines[i] = kv.Key + "=" + value;
            }
        }

        if (left.Count > 0)
        {
            var header = "[" + section + "]";
            var at = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                lines.Add(header);
                at = lines.Count - 1;
            }

            var end = at + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('['))
            {
                end++;
            }

            lines.InsertRange(end, left.Select(kv => kv.Key + "=" + kv.Value));
        }

        File.WriteAllText(FilePath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        foreach (var (key, value) in changes)
        {
            _values[key] = value;
        }
    }

    /// <summary>Formats a number the way the server writes it (<c>1.000000</c> for floats).</summary>
    public static string Format(double value, bool isInteger) =>
        isInteger ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.000000", CultureInfo.InvariantCulture);

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
