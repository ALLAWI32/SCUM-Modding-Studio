using System.Text;

namespace ScumStudio.Core.Games;

/// <summary>
/// A node of Valve's text KeyValues format (VDF/ACF), as used by Steam's <c>libraryfolders.vdf</c> and
/// <c>appmanifest_*.acf</c>. A node is either a leaf (<see cref="Value"/> set) or a section (<see cref="Children"/>).
/// </summary>
/// <remarks>
/// Parser support: quoted and unquoted tokens, escape sequences <c>\\ \" \n \t \r</c>, <c>//</c> comments and
/// platform conditionals such as <c>[$WIN32]</c> (ignored). Key lookups are case-insensitive, as in Steam.
/// </remarks>
public sealed class KeyValuesNode
{
    /// <summary>Creates a node.</summary>
    public KeyValuesNode(string key, string? value, IReadOnlyList<KeyValuesNode>? children = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Value = value;
        Children = children ?? [];
    }

    /// <summary>Key text.</summary>
    public string Key { get; }

    /// <summary>Value of a leaf; null for a section.</summary>
    public string? Value { get; }

    /// <summary>Children of a section (empty for a leaf).</summary>
    public IReadOnlyList<KeyValuesNode> Children { get; }

    /// <summary>True for a section (<c>"key" { ... }</c>).</summary>
    public bool IsSection => Value is null;

    /// <summary>First child with <paramref name="key"/> (case-insensitive), or null.</summary>
    public KeyValuesNode? Child(string key) =>
        Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Value of the first leaf child with <paramref name="key"/>, or null.</summary>
    public string? GetValue(string key) => Child(key)?.Value;

    /// <summary>
    /// Parses KeyValues text. Returns a synthetic root (empty key) whose children are the top-level entries.
    /// </summary>
    /// <exception cref="FormatException">The text is malformed.</exception>
    public static KeyValuesNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        return new KeyValuesNode(string.Empty, null, parser.ParseEntries(nested: false));
    }

    /// <summary>Reads and parses a KeyValues file (UTF-8).</summary>
    /// <exception cref="FormatException">The file is malformed.</exception>
    public static KeyValuesNode ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <inheritdoc />
    public override string ToString() => IsSection ? $"\"{Key}\" {{ {Children.Count} entries }}" : $"\"{Key}\" \"{Value}\"";

    private enum TokenKind
    {
        End,
        Open,
        Close,
        Text,
    }

    private readonly record struct Token(TokenKind Kind, string Text, bool Quoted, int Position)
    {
        public bool IsConditional => Kind == TokenKind.Text && !Quoted && Text.StartsWith('[') && Text.EndsWith(']');
    }

    private sealed class Parser(string text)
    {
        private int _pos;
        private Token? _peeked;

        public List<KeyValuesNode> ParseEntries(bool nested)
        {
            var entries = new List<KeyValuesNode>();
            while (true)
            {
                var token = Next();
                switch (token.Kind)
                {
                    case TokenKind.End when nested:
                        throw new FormatException($"Unexpected end of KeyValues text: missing '}}' (offset {token.Position}).");
                    case TokenKind.End:
                        return entries;
                    case TokenKind.Close when nested:
                        return entries;
                    case TokenKind.Close:
                        throw new FormatException($"Unexpected '}}' at offset {token.Position}.");
                    case TokenKind.Open:
                        throw new FormatException($"Unexpected '{{' at offset {token.Position}; a key was expected.");
                }

                var key = token.Text;
                SkipConditional();
                var next = Next();
                if (next.Kind == TokenKind.Open)
                {
                    entries.Add(new KeyValuesNode(key, null, ParseEntries(nested: true)));
                }
                else if (next.Kind == TokenKind.Text)
                {
                    entries.Add(new KeyValuesNode(key, next.Text));
                    SkipConditional();
                }
                else
                {
                    throw new FormatException($"Key \"{key}\" at offset {token.Position} has no value.");
                }
            }
        }

        private void SkipConditional()
        {
            var peek = Peek();
            if (peek.IsConditional)
            {
                Next();
            }
        }

        private Token Peek() => _peeked ??= Read();

        private Token Next()
        {
            if (_peeked is { } peeked)
            {
                _peeked = null;
                return peeked;
            }

            return Read();
        }

        private Token Read()
        {
            SkipWhitespaceAndComments();
            if (_pos >= text.Length)
            {
                return new Token(TokenKind.End, string.Empty, false, _pos);
            }

            var start = _pos;
            var c = text[_pos];
            if (c == '{')
            {
                _pos++;
                return new Token(TokenKind.Open, "{", false, start);
            }

            if (c == '}')
            {
                _pos++;
                return new Token(TokenKind.Close, "}", false, start);
            }

            if (c == '"')
            {
                _pos++;
                var sb = new StringBuilder();
                while (_pos < text.Length && text[_pos] != '"')
                {
                    var ch = text[_pos++];
                    if (ch == '\\' && _pos < text.Length)
                    {
                        var escaped = text[_pos++];
                        sb.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            _ => escaped,
                        });
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }

                if (_pos >= text.Length)
                {
                    throw new FormatException($"Unterminated string starting at offset {start}.");
                }

                _pos++;
                return new Token(TokenKind.Text, sb.ToString(), true, start);
            }

            while (_pos < text.Length && !char.IsWhiteSpace(text[_pos]) && text[_pos] is not ('{' or '}' or '"'))
            {
                _pos++;
            }

            return new Token(TokenKind.Text, text[start.._pos], false, start);
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < text.Length)
            {
                if (char.IsWhiteSpace(text[_pos]) || text[_pos] == '\uFEFF')
                {
                    _pos++;
                }
                else if (text[_pos] == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/')
                {
                    while (_pos < text.Length && text[_pos] != '\n')
                    {
                        _pos++;
                    }
                }
                else
                {
                    return;
                }
            }
        }
    }
}
