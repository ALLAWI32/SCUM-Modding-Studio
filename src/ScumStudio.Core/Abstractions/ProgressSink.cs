using System.Diagnostics;

namespace ScumStudio.Core.Abstractions;

/// <summary>Factory and helpers for <see cref="IProgressSink"/>.</summary>
public static class ProgressSink
{
    /// <summary>A sink that ignores every report.</summary>
    public static IProgressSink Null { get; } = new NullSink();

    /// <summary>Creates a sink that forwards every report to <paramref name="onReport"/>.</summary>
    public static IProgressSink Create(Action<ProgressUpdate> onReport)
    {
        ArgumentNullException.ThrowIfNull(onReport);
        return new DelegateSink(onReport);
    }

    /// <summary>
    /// Adapts an <see cref="IProgress{T}"/>; with <see cref="Progress{T}"/> created on the UI thread this marshals
    /// reports to that thread.
    /// </summary>
    public static IProgressSink FromProgress(IProgress<ProgressUpdate> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return new DelegateSink(progress.Report);
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> so it receives at most one report per <paramref name="interval"/>, except that the
    /// first report, every change of step and every completed step (<c>current &gt;= total</c>) always pass.
    /// </summary>
    public static IProgressSink Throttle(this IProgressSink inner, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return inner is NullSink ? inner : new ThrottledSink(inner, interval);
    }

    /// <summary>Reports <paramref name="step"/> with unknown total.</summary>
    public static void ReportIndeterminate(this IProgressSink sink, string step) => sink.Report(step, 0, 0);

    /// <summary>
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled, otherwise
    /// reports. Convenient as the single per-item call inside a processing loop.
    /// </summary>
    public static void ReportOrThrowIfCancelled(
        this IProgressSink sink, string step, long current, long total, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sink.Report(step, current, total);
    }

    private sealed class NullSink : IProgressSink
    {
        public void Report(string step, long current, long total)
        {
        }
    }

    private sealed class DelegateSink(Action<ProgressUpdate> onReport) : IProgressSink
    {
        public void Report(string step, long current, long total) => onReport(new ProgressUpdate(step, current, total));
    }

    private sealed class ThrottledSink : IProgressSink
    {
        private readonly IProgressSink _inner;
        private readonly long _intervalTicks;
        private readonly object _gate = new();
        private string? _lastStep;
        private long _lastTimestamp;

        public ThrottledSink(IProgressSink inner, TimeSpan interval)
        {
            _inner = inner;
            _intervalTicks = (long)(Math.Max(0d, interval.TotalSeconds) * Stopwatch.Frequency);
        }

        public void Report(string step, long current, long total)
        {
            var now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                var force = _lastStep is null || !string.Equals(step, _lastStep, StringComparison.Ordinal) ||
                            (total > 0 && current >= total);
                if (!force && now - _lastTimestamp < _intervalTicks)
                {
                    return;
                }

                _lastStep = step;
                _lastTimestamp = now;
            }

            _inner.Report(step, current, total);
        }
    }
}
