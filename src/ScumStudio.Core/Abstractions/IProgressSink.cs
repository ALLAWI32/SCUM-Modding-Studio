namespace ScumStudio.Core.Abstractions;

/// <summary>
/// Receives progress of a long-running operation (pak indexing, level load, export). Cancellation is not part of
/// this contract: long operations take a <see cref="CancellationToken"/> next to the sink.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe and cheap; producers may call <see cref="Report"/> from worker threads and
/// many times per second (wrap a UI sink with <see cref="ProgressSink.Throttle"/>). A sink must not throw.
/// </remarks>
public interface IProgressSink
{
    /// <summary>Reports that <paramref name="current"/> of <paramref name="total"/> units of <paramref name="step"/> are done.</summary>
    /// <param name="step">Short human-readable description of the current step (e.g. "Indexing paks").</param>
    /// <param name="current">Units completed so far (0..total).</param>
    /// <param name="total">Total units for the step; zero or negative means unknown (indeterminate).</param>
    void Report(string step, long current, long total);
}

/// <summary>A progress report as a value (for <see cref="IProgress{T}"/> adapters and tests).</summary>
/// <param name="Step">Step description.</param>
/// <param name="Current">Units completed.</param>
/// <param name="Total">Total units; zero or negative means indeterminate.</param>
public readonly record struct ProgressUpdate(string Step, long Current, long Total)
{
    /// <summary>True when <see cref="Total"/> is unknown.</summary>
    public bool IsIndeterminate => Total <= 0;

    /// <summary>Completed fraction in [0, 1], or null when indeterminate.</summary>
    public double? Fraction => IsIndeterminate ? null : Math.Clamp((double)Current / Total, 0d, 1d);

    /// <summary>True when the step is complete (<c>Current &gt;= Total &gt; 0</c>).</summary>
    public bool IsComplete => !IsIndeterminate && Current >= Total;
}
