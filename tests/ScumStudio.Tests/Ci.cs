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

/// <summary>
/// Tests that hold one operation to a wall-clock budget of a few milliseconds, or count what the whole process allocates
/// (<see cref="GC.GetTotalAllocatedBytes"/> counts every thread). They run in this non-parallel collection, after the
/// parallel ones, so the measurement is the operation's and not the other tests': the Vehicles/Weapons paint bake
/// (ModulePaintTextureTests) took 9 ms alone and 65 ms next to the whole suite, and reading the military base allocated
/// 535 MB alone and "6,243 MB" next to the other real-file Level tests (ImportScopeRealTests).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Frame-budget timing";
}
