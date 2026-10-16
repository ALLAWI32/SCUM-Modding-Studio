namespace ScumStudio.Tests;

/// <summary>
/// Whether the tests run on a shared CI runner (GitHub Actions). A few tests assert that an operation finishes within a
/// wall-clock budget; those bounds are meaningful on a real machine but not on a slow, shared CI VM, so the timing part
/// of such a test is skipped when <see cref="On"/> is true. The test still runs its correctness checks there.
/// </summary>
internal static class Ci
{
    public static bool On =>
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null ||
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
}
