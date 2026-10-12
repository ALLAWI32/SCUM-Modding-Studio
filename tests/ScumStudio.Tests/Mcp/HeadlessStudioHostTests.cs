using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Mcp.Studio;
using ScumStudio.Tests.Level;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.Mcp;

/// <summary>The headless MCP host's project handling (no game files needed).</summary>
public sealed class HeadlessStudioHostTests
{
    [Fact]
    public async Task OpeningTheOpenProjectAgainKeepsIt()
    {
        // open_project is idempotent: an AI that opens the project the host already has keeps it. Opening it a second
        // time was refused by the project's own lock (one writer per project, #9), on Linux too.
        using var temp = new LevelTempDirectory();
        using var host = new HeadlessStudioHost();
        var project = await host.CreateProjectAsync(temp.Combine("projects"), "Mod", CancellationToken.None);
        host.Apply(new DeleteActorOp(new ActorRef(Outpost, "A")));

        Assert.Same(project, await host.OpenProjectAsync(project.DirectoryPath, CancellationToken.None));
        Assert.Same(project, await host.OpenProjectAsync(Path.Combine(project.DirectoryPath, Project.ManifestFileName), CancellationToken.None));
        Assert.Same(project, await host.OpenProjectAsync(project.DirectoryPath + Path.DirectorySeparatorChar, CancellationToken.None));
        if (OperatingSystem.IsWindows())
        {
            Assert.Same(project, await host.OpenProjectAsync(project.DirectoryPath.ToUpperInvariant(), CancellationToken.None));
        }

        host.Apply(new DeleteActorOp(new ActorRef(Outpost, "B")));
        Assert.Throws<ProjectLockedException>(() => Project.Open(project.DirectoryPath)); // still held by the host

        // Another project replaces it and releases its lock.
        var otherDir = temp.Combine("Other.ssproj");
        Project.Create(otherDir, "Other").Dispose();
        Assert.NotSame(project, await host.OpenProjectAsync(otherDir, CancellationToken.None));
        using var reopened = Project.Open(project.DirectoryPath);
        Assert.Equal(2, reopened.Journal.Applied.Count);
    }
}
