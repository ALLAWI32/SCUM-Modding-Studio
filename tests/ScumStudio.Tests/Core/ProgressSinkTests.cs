using ScumStudio.Core.Abstractions;

namespace ScumStudio.Tests.Core;

public sealed class ProgressSinkTests
{
    [Fact]
    public void DelegateSinkForwardsUpdates()
    {
        var updates = new List<ProgressUpdate>();
        var sink = ProgressSink.Create(updates.Add);
        sink.Report("Indexing", 5, 10);
        sink.ReportIndeterminate("Scanning");

        Assert.Equal(2, updates.Count);
        Assert.Equal(0.5, updates[0].Fraction);
        Assert.False(updates[0].IsComplete);
        Assert.True(updates[1].IsIndeterminate);
        Assert.Null(updates[1].Fraction);
        Assert.Equal(1.0, new ProgressUpdate("x", 12, 10).Fraction);
        Assert.True(new ProgressUpdate("x", 10, 10).IsComplete);
    }

    [Fact]
    public void ThrottleKeepsFirstStepChangesAndCompletion()
    {
        var updates = new List<ProgressUpdate>();
        var sink = ProgressSink.Create(updates.Add).Throttle(TimeSpan.FromHours(1));
        for (var i = 0; i <= 100; i++)
        {
            sink.Report("Reading", i, 100);
        }

        sink.Report("Writing", 0, 3);
        sink.Report("Writing", 1, 3);
        sink.Report("Writing", 3, 3);

        Assert.Equal(
            [new ProgressUpdate("Reading", 0, 100), new ProgressUpdate("Reading", 100, 100), new ProgressUpdate("Writing", 0, 3), new ProgressUpdate("Writing", 3, 3)],
            updates);

        var unthrottled = new List<ProgressUpdate>();
        var zero = ProgressSink.Create(unthrottled.Add).Throttle(TimeSpan.Zero);
        zero.Report("a", 1, 10);
        zero.Report("a", 2, 10);
        Assert.Equal(2, unthrottled.Count);
    }

    [Fact]
    public void AdaptsIProgressAndCancellation()
    {
        var progress = new SynchronousProgress();
        var sink = ProgressSink.FromProgress(progress);
        sink.ReportOrThrowIfCancelled("Step", 1, 2, CancellationToken.None);
        Assert.Equal(new ProgressUpdate("Step", 1, 2), Assert.Single(progress.Updates));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => sink.ReportOrThrowIfCancelled("Step", 2, 2, cts.Token));
        Assert.Single(progress.Updates);

        ProgressSink.Null.Report("ignored", 1, 1);
        Assert.Same(ProgressSink.Null, ProgressSink.Null.Throttle(TimeSpan.FromSeconds(1)));
    }

    private sealed class SynchronousProgress : IProgress<ProgressUpdate>
    {
        public List<ProgressUpdate> Updates { get; } = [];

        public void Report(ProgressUpdate value) => Updates.Add(value);
    }
}
