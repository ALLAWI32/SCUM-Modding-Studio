namespace ScumStudio.Mcp.Skill;

/// <summary>
/// The Claude Code skill (<c>SKILL.md</c>, embedded) that teaches an AI how to work with this server: workflow, units,
/// verification with screenshots and the owner's rules. Installed per user so every Claude Code session knows it.
/// </summary>
public static class StudioSkill
{
    private const string ResourceName = "ScumStudio.Mcp.Skill.SKILL.md";

    /// <summary>The skill text.</summary>
    public static string Text { get; } = Read();

    /// <summary>Claude Code's per-user skill folder for this skill: <c>~/.claude/skills/scumstudio</c>.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "scumstudio");

    /// <summary>Writes <c>SKILL.md</c> into <paramref name="folder"/> (default <see cref="DefaultFolder"/>) and returns its path.</summary>
    public static string Install(string? folder = null)
    {
        folder = Path.GetFullPath(folder ?? DefaultFolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "SKILL.md");
        File.WriteAllText(path, Text);
        return path;
    }

    private static string Read()
    {
        using var stream = typeof(StudioSkill).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded skill " + ResourceName + " is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
