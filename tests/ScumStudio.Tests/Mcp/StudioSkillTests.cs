using ScumStudio.Mcp.Skill;
using ScumStudio.Mcp.Studio;

namespace ScumStudio.Tests.Mcp;

/// <summary>The embedded Claude Code skill: valid front matter, names real tools, installs as SKILL.md.</summary>
public sealed class StudioSkillTests
{
    [Fact]
    public void SkillHasFrontMatterAndInstalls()
    {
        Assert.StartsWith("---\nname: scumstudio\ndescription: ", StudioSkill.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        foreach (var tool in new[] { "get_status", "open_source", "create_project", "show_levels", "screenshot", "delete_all_of_kind", "move_actor", "clone_item", "export_mod", "undo" })
        {
            Assert.Contains("`" + tool, StudioSkill.Text, StringComparison.Ordinal);
            Assert.Contains(tool, StudioTools.Instructions + " undo screenshot export_mod", StringComparison.Ordinal);
        }

        var folder = Path.Combine(Path.GetTempPath(), "scumstudio-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = StudioSkill.Install(folder);
            Assert.Equal(Path.Combine(folder, "SKILL.md"), path);
            Assert.Equal(StudioSkill.Text, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
