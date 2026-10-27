using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScumStudio.Modding.ServerMods;

/// <summary>One Lua mod of the server.</summary>
/// <param name="Name">Folder name.</param>
/// <param name="Enabled">The mod is switched on in mods.json.</param>
/// <param name="ScriptPath">Its <c>Scripts\main.lua</c>.</param>
public sealed record ServerMod(string Name, bool Enabled, string ScriptPath);

/// <summary>
/// The Lua mods of a server's UE4SS: each is a folder <c>Mods\&lt;Name&gt;\Scripts\main.lua</c> and a line in <c>mods.json</c> and
/// <c>mods.txt</c> (UE4SS reads both). The server reloads a mod by itself when its main.lua is saved.
/// </summary>
public sealed partial class ServerModStore
{
    private static readonly string[] StockMods =
        ["CheatManagerEnablerMod", "ConsoleCommandsMod", "ConsoleEnablerMod", "SplitScreenMod", "LineTraceMod", "BPML_GenericFunctions", "BPModLoaderMod"];

    private const string KeybindsMod = "Keybinds";

    private readonly ServerInstall _server;

    /// <summary>Creates the store over <paramref name="server"/>.</summary>
    public ServerModStore(ServerInstall server) => _server = server ?? throw new ArgumentNullException(nameof(server));

    private string JsonFile => Path.Combine(_server.ModsFolder, "mods.json");

    private string TextFile => Path.Combine(_server.ModsFolder, "mods.txt");

    /// <summary>True for a mod name UE4SS accepts as a folder and a line (letters, digits, _ and -).</summary>
    public static bool IsValidName(string? name) => name is { Length: > 0 and <= 40 } && ValidName().IsMatch(name);

    /// <summary>The mods that have a main.lua, with their on/off state, the stock mods of UE4SS left out.</summary>
    public IReadOnlyList<ServerMod> List()
    {
        if (!Directory.Exists(_server.ModsFolder))
        {
            return [];
        }

        var states = ReadStates();
        return
        [
            .. Directory.EnumerateDirectories(_server.ModsFolder)
                .Select(d => (Name: Path.GetFileName(d), Script: Path.Combine(d, "Scripts", "main.lua")))
                .Where(m => File.Exists(m.Script) && !StockMods.Contains(m.Name, StringComparer.OrdinalIgnoreCase) && m.Name != KeybindsMod && m.Name != "shared")
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new ServerMod(m.Name, states.GetValueOrDefault(m.Name), m.Script)),
        ];
    }

    /// <summary>Writes the mod (its main.lua) and switches it on. A mod that is running reloads at once.</summary>
    /// <exception cref="ArgumentException">The name is not valid.</exception>
    public ServerMod Save(string name, string code)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("A mod name is letters, digits, _ and - (at most 40).", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(code);
        var script = Path.Combine(_server.ModsFolder, name, "Scripts", "main.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, code, new System.Text.UTF8Encoding(false));
        SetEnabled(name, true);
        return new ServerMod(name, true, script);
    }

    /// <summary>The text of a mod's main.lua, or null.</summary>
    public string? Read(string name) => IsValidName(name) && File.Exists(ScriptOf(name)) ? File.ReadAllText(ScriptOf(name)) : null;

    /// <summary>Deletes the mod's folder and its lines in mods.json and mods.txt.</summary>
    public void Remove(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("Not a valid mod name.", nameof(name));
        }

        var folder = Path.Combine(_server.ModsFolder, name);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Rewrite(states => states.Remove(name));
    }

    /// <summary>Switches a mod on or off (the server picks it up when it starts, or at once for a saved main.lua).</summary>
    public void SetEnabled(string name, bool enabled)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("Not a valid mod name.", nameof(name));
        }

        Rewrite(states => states[name] = enabled);
    }

    /// <summary>Switches off the stock mods UE4SS ships (a console and cheat menus have no use on a server and one of them hangs it).</summary>
    public void DisableStockMods() => Rewrite(states =>
    {
        foreach (var stock in StockMods)
        {
            states[stock] = false;
        }

        states[KeybindsMod] = true;
    });

    private string ScriptOf(string name) => Path.Combine(_server.ModsFolder, name, "Scripts", "main.lua");

    private Dictionary<string, bool> ReadStates()
    {
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(JsonFile))
        {
            try
            {
                foreach (var node in JsonNode.Parse(File.ReadAllText(JsonFile)) as JsonArray ?? [])
                {
                    if (node?["mod_name"]?.GetValue<string>() is { } name)
                    {
                        states[name] = node["mod_enabled"]?.GetValue<bool>() ?? false;
                    }
                }
            }
            catch (JsonException)
            {
                // A damaged file is rewritten from mods.txt below.
            }
        }

        if (File.Exists(TextFile))
        {
            foreach (var line in File.ReadAllLines(TextFile))
            {
                var m = ModsTxtLine().Match(line);
                if (m.Success)
                {
                    states.TryAdd(m.Groups[1].Value, m.Groups[2].Value == "1");
                }
            }
        }

        return states;
    }

    private void Rewrite(Action<Dictionary<string, bool>> change)
    {
        Directory.CreateDirectory(_server.ModsFolder);
        var states = ReadStates();
        change(states);

        var array = new JsonArray();
        foreach (var (name, enabled) in states)
        {
            array.Add(new JsonObject { ["mod_name"] = name, ["mod_enabled"] = enabled });
        }

        File.WriteAllText(JsonFile, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Keybinds must stay the last line, "do not move up".
        var lines = states.Where(p => p.Key != KeybindsMod).Select(p => $"{p.Key} : {(p.Value ? 1 : 0)}").ToList();
        lines.Add(string.Empty);
        lines.Add("; Built-in keybinds, do not move up!");
        lines.Add($"{KeybindsMod} : {(states.GetValueOrDefault(KeybindsMod, true) ? 1 : 0)}");
        File.WriteAllText(TextFile, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^\s*([A-Za-z0-9_-]+)\s*:\s*([01])\s*$")]
    private static partial Regex ModsTxtLine();
}
