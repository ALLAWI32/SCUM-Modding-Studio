using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScumStudio.Mcp.Protocol;

/// <summary>
/// One MCP tool: name, description, JSON Schema of its arguments, behaviour hints and the handler. See the MCP
/// specification, "Server features / Tools" (<c>tools/list</c>, <c>tools/call</c>).
/// </summary>
/// <param name="Name">Unique tool name (<c>snake_case</c>).</param>
/// <param name="Description">What the tool does, written for the AI (units, conventions, side effects).</param>
/// <param name="InputSchema">JSON Schema (<c>type: object</c>) of the arguments.</param>
/// <param name="Handler">Runs the tool.</param>
public sealed record McpTool(string Name, string Description, JsonObject InputSchema, Func<ToolCall, CancellationToken, Task<McpToolResult>> Handler)
{
    /// <summary>Human-readable title.</summary>
    public string? Title { get; init; }

    /// <summary>The tool does not modify anything (<c>readOnlyHint</c>).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The tool may delete or overwrite (<c>destructiveHint</c>); journaled edits are undoable but still reported.</summary>
    public bool Destructive { get; init; }

    /// <summary>Calling twice with the same arguments has no additional effect (<c>idempotentHint</c>).</summary>
    public bool Idempotent { get; init; }

    /// <summary>The tool reaches outside the studio (<c>openWorldHint</c>): file system writes, other programs.</summary>
    public bool OpenWorld { get; init; }

    /// <summary>The <c>tools/list</c> entry.</summary>
    public JsonObject ToListEntry()
    {
        var entry = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["inputSchema"] = InputSchema.DeepClone(),
            ["annotations"] = new JsonObject
            {
                ["title"] = Title ?? Name,
                ["readOnlyHint"] = ReadOnly,
                ["destructiveHint"] = Destructive,
                ["idempotentHint"] = Idempotent,
                ["openWorldHint"] = OpenWorld,
            },
        };
        if (Title is not null)
        {
            entry["title"] = Title;
        }

        return entry;
    }
}

/// <summary>Result of a tool call: content blocks (text, images), optional structured JSON, error flag.</summary>
public sealed class McpToolResult
{
    private readonly List<JsonObject> _content = [];

    /// <summary>Content blocks in order.</summary>
    public IReadOnlyList<JsonObject> Content => _content;

    /// <summary>Structured result (also serialized into a text block for older clients).</summary>
    public JsonNode? Structured { get; private set; }

    /// <summary>True when the tool failed (the message is in the text content, so the AI can correct itself).</summary>
    public bool IsError { get; private set; }

    /// <summary>One line for activity logs (<see cref="McpToolCalled.Summary"/>); default: the first line of the text.</summary>
    public string? Summary { get; set; }

    /// <summary>Sets <see cref="Summary"/> and returns this result.</summary>
    public McpToolResult WithSummary(string summary)
    {
        Summary = summary;
        return this;
    }

    /// <summary>A text result.</summary>
    public static McpToolResult Text(string text) => new McpToolResult().AddText(text);

    /// <summary>A JSON result: structured content plus its indented text.</summary>
    public static McpToolResult Json(object? value)
    {
        var node = value as JsonNode ?? JsonSerializer.SerializeToNode(value, McpJson.Indented);
        var result = new McpToolResult { Structured = node is JsonObject ? node : new JsonObject { ["result"] = node } };
        result.AddText(node?.ToJsonString(McpJson.Indented) ?? "null");
        return result;
    }

    /// <summary>An error result (the tool ran but could not do what was asked).</summary>
    public static McpToolResult Error(string message)
    {
        var result = new McpToolResult { IsError = true };
        result.AddText(message);
        return result;
    }

    /// <summary>Appends a text block.</summary>
    public McpToolResult AddText(string text)
    {
        _content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return this;
    }

    /// <summary>Appends an image block (base64).</summary>
    public McpToolResult AddImage(byte[] data, string mimeType = "image/png")
    {
        ArgumentNullException.ThrowIfNull(data);
        _content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mimeType });
        return this;
    }

    /// <summary>The <c>tools/call</c> result object.</summary>
    public JsonObject ToJson()
    {
        var content = new JsonArray();
        foreach (var block in _content)
        {
            content.Add(block.DeepClone());
        }

        var result = new JsonObject { ["content"] = content, ["isError"] = IsError };
        if (Structured is not null && !IsError)
        {
            result["structuredContent"] = Structured.DeepClone();
        }

        return result;
    }
}

/// <summary>A tool argument is missing or invalid; reported to the AI as a tool error so it can retry.</summary>
public sealed class ToolArgumentException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ToolArgumentException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ToolArgumentException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ToolArgumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The arguments of one <c>tools/call</c>, with typed accessors that throw <see cref="ToolArgumentException"/>.</summary>
public sealed class ToolCall
{
    private readonly JsonElement _arguments;

    /// <summary>Creates the call.</summary>
    public ToolCall(string name, JsonElement arguments)
    {
        Name = name;
        _arguments = arguments.ValueKind == JsonValueKind.Object ? arguments : default;
    }

    /// <summary>Tool name.</summary>
    public string Name { get; }

    /// <summary>True when the argument is present and not null.</summary>
    public bool Has(string name) => TryGet(name, out _);

    /// <summary>A required string.</summary>
    public string RequireString(string name) =>
        GetString(name) is { Length: > 0 } s ? s : throw new ToolArgumentException($"Argument '{name}' is required.");

    /// <summary>An optional string.</summary>
    public string? GetString(string name)
    {
        if (!TryGet(name, out var e))
        {
            return null;
        }

        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
            _ => throw new ToolArgumentException($"Argument '{name}' must be a string."),
        };
    }

    /// <summary>An optional integer.</summary>
    public int GetInt(string name, int defaultValue)
    {
        if (!TryGet(name, out var e))
        {
            return defaultValue;
        }

        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v))
        {
            return v;
        }

        if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
        {
            return v;
        }

        throw new ToolArgumentException($"Argument '{name}' must be an integer.");
    }

    /// <summary>An optional number.</summary>
    public double? GetNumber(string name)
    {
        if (!TryGet(name, out var e))
        {
            return null;
        }

        if (e.ValueKind == JsonValueKind.Number)
        {
            return e.GetDouble();
        }

        if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }

        throw new ToolArgumentException($"Argument '{name}' must be a number.");
    }

    /// <summary>An optional boolean.</summary>
    public bool GetBool(string name, bool defaultValue)
    {
        if (!TryGet(name, out var e))
        {
            return defaultValue;
        }

        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(e.GetString(), out var b) => b,
            _ => throw new ToolArgumentException($"Argument '{name}' must be true or false."),
        };
    }

    /// <summary>An optional list of strings (a single string is accepted as a one-item list).</summary>
    public IReadOnlyList<string> GetStrings(string name)
    {
        if (!TryGet(name, out var e))
        {
            return [];
        }

        if (e.ValueKind == JsonValueKind.String)
        {
            return [e.GetString()!];
        }

        if (e.ValueKind != JsonValueKind.Array)
        {
            throw new ToolArgumentException($"Argument '{name}' must be a list of strings.");
        }

        return e.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString()! : throw new ToolArgumentException($"Argument '{name}' must be a list of strings.")).ToList();
    }

    /// <summary>
    /// An optional 3-vector: <c>[x, y, z]</c> or <c>{"x":…, "y":…, "z":…}</c> (rotators: <c>[pitch, yaw, roll]</c> or
    /// <c>{"pitch":…, "yaw":…, "roll":…}</c>).
    /// </summary>
    public (float X, float Y, float Z)? GetVector3(string name)
    {
        if (!TryGet(name, out var e))
        {
            return null;
        }

        if (e.ValueKind == JsonValueKind.Array)
        {
            var values = e.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetSingle() : throw new ToolArgumentException($"Argument '{name}' must hold numbers.")).ToArray();
            return values.Length == 3 ? (values[0], values[1], values[2]) : throw new ToolArgumentException($"Argument '{name}' needs exactly 3 numbers.");
        }

        if (e.ValueKind == JsonValueKind.Object)
        {
            float Read(params string[] keys)
            {
                foreach (var key in keys)
                {
                    foreach (var p in e.EnumerateObject())
                    {
                        if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number)
                        {
                            return p.Value.GetSingle();
                        }
                    }
                }

                throw new ToolArgumentException($"Argument '{name}' needs '{keys[0]}'.");
            }

            var isRotator = e.EnumerateObject().Any(p => p.Name.Equals("yaw", StringComparison.OrdinalIgnoreCase));
            return isRotator ? (Read("pitch"), Read("yaw"), Read("roll")) : (Read("x"), Read("y"), Read("z"));
        }

        throw new ToolArgumentException($"Argument '{name}' must be [x, y, z].");
    }

    /// <summary>The raw argument (for lists of objects).</summary>
    public JsonElement? GetElement(string name) => TryGet(name, out var e) ? e : null;

    private bool TryGet(string name, out JsonElement element)
    {
        element = default;
        if (_arguments.ValueKind != JsonValueKind.Object || !_arguments.TryGetProperty(name, out element))
        {
            return false;
        }

        return element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}

/// <summary>Fluent builder of tool input schemas (JSON Schema draft 2020-12 subset used by MCP clients).</summary>
public sealed class ToolSchema
{
    private readonly JsonObject _properties = [];
    private readonly JsonArray _required = [];

    /// <summary>A new object schema.</summary>
    public static ToolSchema Object() => new();

    /// <summary>Adds a string property.</summary>
    public ToolSchema String(string name, string description, bool required = false, IEnumerable<string>? choices = null)
    {
        var p = new JsonObject { ["type"] = "string", ["description"] = description };
        if (choices is not null)
        {
            p["enum"] = new JsonArray(choices.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        }

        return Add(name, p, required);
    }

    /// <summary>Adds an integer property.</summary>
    public ToolSchema Integer(string name, string description, bool required = false, int? minimum = null, int? maximum = null)
    {
        var p = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (minimum is { } min)
        {
            p["minimum"] = min;
        }

        if (maximum is { } max)
        {
            p["maximum"] = max;
        }

        return Add(name, p, required);
    }

    /// <summary>Adds a number property.</summary>
    public ToolSchema Number(string name, string description, bool required = false) =>
        Add(name, new JsonObject { ["type"] = "number", ["description"] = description }, required);

    /// <summary>Adds a boolean property.</summary>
    public ToolSchema Boolean(string name, string description, bool required = false) =>
        Add(name, new JsonObject { ["type"] = "boolean", ["description"] = description }, required);

    /// <summary>Adds a list-of-strings property.</summary>
    public ToolSchema Strings(string name, string description, bool required = false) =>
        Add(name, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description }, required);

    /// <summary>Adds a 3-number property (<c>[x, y, z]</c>).</summary>
    public ToolSchema Vector(string name, string description, bool required = false) =>
        Add(name, new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "number" },
            ["minItems"] = 3,
            ["maxItems"] = 3,
            ["description"] = description,
        }, required);

    /// <summary>Adds a list-of-objects property with the given item schema.</summary>
    public ToolSchema Objects(string name, string description, ToolSchema item, bool required = false) =>
        Add(name, new JsonObject { ["type"] = "array", ["items"] = item.Build(), ["description"] = description }, required);

    /// <summary>The schema object.</summary>
    public JsonObject Build()
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = _properties.DeepClone() };
        if (_required.Count > 0)
        {
            schema["required"] = _required.DeepClone();
        }

        return schema;
    }

    private ToolSchema Add(string name, JsonObject property, bool required)
    {
        _properties[name] = property;
        if (required)
        {
            _required.Add(name);
        }

        return this;
    }
}
