using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;

namespace ScumStudio.Tests.App;

/// <summary>
/// zarkos6175: two copies of the app open and the project was not saved. A second start finds the first window's mutex,
/// asks it to come forward and quits; an update's new version waits for the old one to let go. Windows only (named events).
/// </summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public void TheFirstStartOwnsAndASecondDoesNotUntilTheFirstCloses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = NewName();
        var first = SingleInstance.Acquire(name, TimeSpan.Zero);
        Assert.NotNull(first);
        Assert.Null(OnOtherThread(() => SingleInstance.Acquire(name, TimeSpan.Zero)));

        first.Dispose();
        first.Dispose(); // twice is harmless
        Assert.True(OnOtherThread(() =>
        {
            using var again = SingleInstance.Acquire(name, TimeSpan.Zero);
            return again is not null;
        }));
    }

    [Fact]
    public void ASecondStartBringsTheOwnerForwardOnlyWhenItAnswers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = NewName();
        using var owner = SingleInstance.Acquire(name, TimeSpan.Zero)!;
        using var raised = new ManualResetEventSlim();
        var answer = true;
        owner.Listen(() =>
        {
            raised.Set();
            return answer;
        });

        Assert.True(SingleInstance.SignalOwner(name, TimeSpan.FromSeconds(2)));
        Assert.True(raised.IsSet);

        // Closing: the owner says no and the second start goes on (the project lock protects).
        answer = false;
        Assert.False(SingleInstance.SignalOwner(name, TimeSpan.FromMilliseconds(500)));
        Assert.False(SingleInstance.SignalOwner(NewName(), TimeSpan.FromMilliseconds(200)), "nobody owns that name");
    }

    [Fact]
    public void AnUpdateWaitsForTheOldVersionToLetGo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = NewName();
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var old = SingleInstance.Acquire(name, TimeSpan.Zero);
            held.Set();
            Thread.Sleep(300);
        });
        holder.Start();
        held.Wait();

        var clock = Stopwatch.StartNew();
        using var updated = SingleInstance.Acquire(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(updated);
        Assert.True(clock.ElapsedMilliseconds >= 250, $"owned after {clock.ElapsedMilliseconds} ms");
        holder.Join();
    }

    [Fact]
    public void AWindowThatDiedLeavesTheMutexToTheNextStart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = NewName();
        var dead = OnOtherThread(() => SingleInstance.Acquire(name, TimeSpan.Zero)); // the thread ends without letting go
        Assert.NotNull(dead);
        using var next = SingleInstance.Acquire(name, TimeSpan.Zero);
        Assert.NotNull(next);
        Assert.True(OnOtherThread(() =>
        {
            dead.Dispose(); // from a thread that does not own it: no throw, and the new owner keeps it
            return true;
        }));
        Assert.Null(OnOtherThread(() => SingleInstance.Acquire(name, TimeSpan.Zero)));
    }

    [Fact]
    public void ASecondStartQuitsOnceTheRunningWindowAnswered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = NewName();
        using var owner = SingleInstance.Acquire(name, TimeSpan.Zero)!;
        owner.Listen(() => true);
        var (taken, answered) = OnOtherThread(() => (ScumStudio.App.Program.TakeWindow(name, StartupOptions.Parse([]), out var yes), yes));
        Assert.True(answered);
        Assert.Null(taken);
    }

    [Fact]
    public void WithoutAnAnswerTheStartGoesOnAndOwnsOnceTheOldWindowClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = NewName();
        using var held = new ManualResetEventSlim();
        using var asked = new ManualResetEventSlim();
        var closing = new Thread(() =>
        {
            using var old = SingleInstance.Acquire(name, TimeSpan.Zero)!;
            old.Listen(() =>
            {
                asked.Set();
                return false; // its window has closed, the process has not ended yet
            });
            held.Set();
            asked.Wait();
            Thread.Sleep(300); // then it lets go
        });
        closing.Start();
        held.Wait();

        using var taken = ScumStudio.App.Program.TakeWindow(name, StartupOptions.Parse([]), out var answered);
        Assert.False(answered);
        Assert.NotNull(taken); // the next start brings this one forward
        closing.Join();
    }

    [Fact]
    public void AnUpdatesNewVersionWaitsForTheOldWindowWithoutAskingIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = NewName();
        using var held = new ManualResetEventSlim();
        var asked = false;
        var old = new Thread(() =>
        {
            using var window = SingleInstance.Acquire(name, TimeSpan.Zero)!;
            window.Listen(() => asked = true);
            held.Set();
            Thread.Sleep(300);
        });
        old.Start();
        held.Wait();

        using var taken = ScumStudio.App.Program.TakeWindow(name, StartupOptions.Parse(["--after-update", "1234"]), out var answered);
        Assert.NotNull(taken);
        Assert.False(answered);
        Assert.False(asked);
        old.Join();
    }

    [AvaloniaFact]
    public void TheRunningWindowComesForwardWhileItDownloadsOrInstallsAnUpdate()
    {
        // A download takes minutes: a start told no then would open a second window beside it (or, during the install,
        // run the old version from the file the update renames to .old, the file seviscache's update could not delete).
        using var ctx = AppTestContext.Create();
        using var shell = new MainWindowViewModel(ctx.Services) { IsUpdating = true };
        var window = new Window { DataContext = shell };
        window.Show();
        window.WindowState = WindowState.Minimized;
        ScumStudio.App.App.Attach(window);

        Assert.True(ScumStudio.App.App.ComeForward(), "the second start quits");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WindowState.Normal, window.WindowState);

        window.Close();
        Assert.False(ScumStudio.App.App.ComeForward(), "a closing window lets the next start open its own");
    }

    [Fact]
    public void AutomationRunsKeepTheirOwnWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = NewName();
        using var owner = SingleInstance.Acquire(name, TimeSpan.Zero)!;
        var asked = false;
        owner.Listen(() => asked = true);
        foreach (var args in new[] { new[] { "--screenshot", "shot.png" }, ["--mcp-port", "47130"] })
        {
            Assert.Null(ScumStudio.App.Program.TakeWindow(name, StartupOptions.Parse(args), out var answered));
            Assert.False(answered);
        }

        Assert.False(asked);
    }

    [Fact]
    public void AfterUpdateNamesTheOldProcess()
    {
        var options = StartupOptions.Parse(["--after-update", "1234"]);
        Assert.Equal(1234, options.AfterUpdatePid);
        Assert.False(options.HasWork); // a normal start: the last project reopens

        Assert.Null(StartupOptions.Parse(["--after-update", "-5"]).AfterUpdatePid);
        Assert.Null(StartupOptions.Parse(["--after-update", "12a"]).AfterUpdatePid);
        Assert.Null(StartupOptions.Parse(["--after-update"]).AfterUpdatePid);
    }

    private static string NewName() => "ScumStudio.Tests." + Guid.NewGuid().ToString("N");

    private static T OnOtherThread<T>(Func<T> work)
    {
        var result = default(T)!;
        var thread = new Thread(() => result = work());
        thread.Start();
        thread.Join();
        return result;
    }
}
