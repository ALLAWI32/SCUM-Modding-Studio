using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace ScumStudio.Modding.ServerMods;

/// <summary>How serious a finding of <see cref="LuaCheck"/> is.</summary>
public enum LuaFindingLevel
{
    /// <summary>The script cannot run (syntax) or names a game function that does not exist.</summary>
    Error,

    /// <summary>Something the owner's server punished before.</summary>
    Warning,
}

/// <summary>One finding about a script.</summary>
/// <param name="Level">Error or warning.</param>
/// <param name="Line">1-based line, or 0 when it is about the whole script.</param>
/// <param name="Code">Stable key of the finding (for the message text).</param>
/// <param name="Detail">What was found (a name or the parser's message).</param>
public sealed record LuaFinding(LuaFindingLevel Level, int Line, string Code, string Detail);

/// <summary>
/// Looks over a server Lua script before it goes to the server: the syntax (MoonSharp parses it, nothing runs), the game
/// functions it names against the server's class dump, and the mistakes that crashed or froze the owner's server.
/// </summary>
public static partial class LuaCheck
{
    /// <summary>
    /// Checks <paramref name="code"/>. <paramref name="knownFunctions"/> is the set of <c>/Script/SCUM.Class:Function</c> paths of
    /// the class dump (null skips that check).
    /// </summary>
    public static IReadOnlyList<LuaFinding> Run(string code, ISet<string>? knownFunctions = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        var findings = new List<LuaFinding>();
        try
        {
            new Script().LoadString(code, null, "mod");
        }
        catch (SyntaxErrorException ex)
        {
            findings.Add(new LuaFinding(LuaFindingLevel.Error, LineOf(ex.DecoratedMessage), "Syntax", ex.DecoratedMessage ?? ex.Message));
        }

        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            var number = i + 1;
            if (knownFunctions is not null)
            {
                foreach (Match m in GamePath().Matches(line))
                {
                    if (!knownFunctions.Contains(m.Value))
                    {
                        findings.Add(new LuaFinding(LuaFindingLevel.Error, number, "UnknownFunction", m.Value));
                    }
                }
            }

            if (line.Contains("format", StringComparison.Ordinal) && FormatInteger().IsMatch(line))
            {
                findings.Add(new LuaFinding(LuaFindingLevel.Warning, number, "FormatInteger", "%d"));
            }

            if (SoftPointerWrite().IsMatch(line))
            {
                findings.Add(new LuaFinding(LuaFindingLevel.Warning, number, "SoftPointer", "TradeableClass"));
            }

            if (MessageSet().IsMatch(line))
            {
                findings.Add(new LuaFinding(LuaFindingLevel.Warning, number, "HideChat", "Message:set"));
            }
        }

        if (HasAny(code, "FindAllOf") && HasAny(code, "LoopAsync") && !HasAny(code, "ExecuteInGameThread"))
        {
            findings.Add(new LuaFinding(LuaFindingLevel.Warning, 0, "LoopOutsideGameThread", "FindAllOf"));
        }

        if (HasAny(code, "RegisterHook") && PlayerInHook(code))
        {
            findings.Add(new LuaFinding(LuaFindingLevel.Warning, 0, "PlayerInHook", "RegisterHook"));
        }

        return findings;
    }

    /// <summary>The <c>/Script/...:Function</c> paths a class dump holds (the lines that start with "Function ").</summary>
    public static HashSet<string> ReadDump(IEnumerable<string> dumpLines)
    {
        ArgumentNullException.ThrowIfNull(dumpLines);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in dumpLines)
        {
            if (line.StartsWith("Function ", StringComparison.Ordinal))
            {
                set.Add(line[9..].Trim());
            }
        }

        return set;
    }

    /// <summary>
    /// A hook whose body calls a player function right away (SendHUDMessageToClient, GetOuter, GetController) without a delay
    /// anywhere in the script: the native call on a player that is not ready crashed the server.
    /// </summary>
    private static bool PlayerInHook(string code) =>
        !HasAny(code, "ExecuteWithDelay") && PlayerCall().IsMatch(code);

    private static bool HasAny(string code, string word) => code.Contains(word, StringComparison.Ordinal);

    private static int LineOf(string? message)
    {
        var m = message is null ? null : LineNumber().Match(message);
        return m is { Success: true } && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    private static string StripComment(string line)
    {
        var at = line.IndexOf("--", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    [GeneratedRegex(@"/Script/[A-Za-z0-9_]+\.[A-Za-z0-9_]+:[A-Za-z0-9_]+")]
    private static partial Regex GamePath();

    [GeneratedRegex(@"%[-+ #0-9]*d")]
    private static partial Regex FormatInteger();

    [GeneratedRegex(@"\.TradeableClass\s*=(?!=)")]
    private static partial Regex SoftPointerWrite();

    [GeneratedRegex(@"\bMessage\s*:\s*set\s*\(")]
    private static partial Regex MessageSet();

    [GeneratedRegex(@"\((\d+),")]
    private static partial Regex LineNumber();

    [GeneratedRegex(@":(SendHUDMessageToClient|GetOuter|GetController)\(")]
    private static partial Regex PlayerCall();
}
