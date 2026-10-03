namespace ScumStudio.Tests.Fixtures;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped when the SCUM fixture archive is unavailable
/// (<c>SCUM_FIXTURES</c> unset or pointing to a missing folder), so a plain Visual Studio test run stays green.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FixturesFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute; sets <see cref="FactAttribute.Skip"/> when fixtures are missing.</summary>
    public FixturesFactAttribute()
    {
        if (FixturePaths.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A <see cref="TheoryAttribute"/> that is skipped when the SCUM fixture archive is unavailable.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FixturesTheoryAttribute : TheoryAttribute
{
    /// <summary>Creates the attribute; sets <see cref="FactAttribute.Skip"/> when fixtures are missing.</summary>
    public FixturesTheoryAttribute()
    {
        if (FixturePaths.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
