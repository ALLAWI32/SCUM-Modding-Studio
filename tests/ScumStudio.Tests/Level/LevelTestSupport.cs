namespace ScumStudio.Tests.Level;

/// <summary>A deterministic clock for journal/project tests.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public static ManualClock At2026() => new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A temporary folder deleted on dispose.</summary>
internal sealed class LevelTempDirectory : IDisposable
{
    public LevelTempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scumstudio-level-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
