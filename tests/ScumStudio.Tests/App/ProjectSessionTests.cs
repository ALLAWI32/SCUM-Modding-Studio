using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// zarkos6175 (#9): "two copies of the app open, the project was not saved". The second window could not open the project
/// (a raw sharing-violation message) and then refused every edit; opening the open project again failed the same way.
/// </summary>
[Collection(LanguageCollection.Name)] // compares localized texts
public sealed class ProjectSessionTests
{
    [AvaloniaFact]
    public async Task OpeningTheOpenProjectAgainKeepsIt()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var path = ctx.Combine("projects", "Again.ssproj");
        Project.Create(path, "Again").Dispose();
        var projects = ctx.Services.Projects;

        var first = await projects.OpenAsync(path);
        var again = await projects.OpenAsync(Path.Combine(path, "project.json")); // a file in it, as the picker gives
        var trailing = await projects.OpenAsync(path + Path.DirectorySeparatorChar);

        Assert.Same(first, again);
        Assert.Same(first, trailing);
        Assert.Same(first, projects.Current);
        projects.Apply(new DeleteActorOp(new ActorRef(EditOpTests.Outpost, "Crate_1")));
        Assert.True(projects.CanUndo);
    }

    [AvaloniaFact]
    public async Task AProjectOpenElsewhereSaysSoAndKeepsTheCurrentOne()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the journal's share mode is the lock until the project lock is merged; elsewhere it is advisory
        }

        using var ctx = AppTestContext.Create(inline: false);
        var mine = ctx.Combine("projects", "Mine.ssproj");
        var other = ctx.Combine("projects", "Other.ssproj");
        Project.Create(mine, "Mine").Dispose();
        Project.Create(other, "Other").Dispose();
        var projects = ctx.Services.Projects;
        await projects.OpenAsync(mine);

        // Another window has Other open: it holds the journal as the journal writer does.
        using (new FileStream(Path.Combine(other, Project.JournalFileName), FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var ex = await Assert.ThrowsAsync<IOException>(() => projects.OpenAsync(other));
            Assert.Equal(Loc.T("Project.Locked"), ex.Message);
        }

        Assert.Equal(Path.GetFullPath(mine), projects.Current?.DirectoryPath);
        projects.Apply(new DeleteActorOp(new ActorRef(EditOpTests.Outpost, "Crate_1")));
        Assert.True(projects.CanUndo);
    }

    /// <summary>After an update the old version may still hold the project for a moment: the new one waits for it.</summary>
    [AvaloniaFact]
    public async Task AProjectFreedWhileWaitingOpens()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var ctx = AppTestContext.Create(inline: false);
        var path = ctx.Combine("projects", "Wait.ssproj");
        Project.Create(path, "Wait").Dispose();
        var holder = new FileStream(Path.Combine(path, Project.JournalFileName), FileMode.Open, FileAccess.Write, FileShare.Read);
        var release = Task.Delay(700).ContinueWith(_ => holder.Dispose(), TaskScheduler.Default);

        var project = await ctx.Services.Projects.OpenAsync(path, TimeSpan.FromSeconds(10));

        await release;
        Assert.Same(project, ctx.Services.Projects.Current);
    }
}
