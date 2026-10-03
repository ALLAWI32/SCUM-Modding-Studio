using ScumStudio.Assets.Catalog;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The optional map slice: a client cook of one area of The_Island (its sublevels plus the Blueprint and mesh packages
/// they use), extracted as loose files. Never part of the repository: point the <c>SCUM_MAP_SLICE</c> environment variable
/// at the folder that contains <c>SCUM/Content</c> (or at <c>SCUM</c> or <c>Content</c> itself). Tests that need it use
/// <see cref="MapSliceFactAttribute"/> and are skipped, not failed, without it.
/// </summary>
internal static class MapSlice
{
    /// <summary>Name of the environment variable holding the slice folder.</summary>
    public const string EnvironmentVariable = "SCUM_MAP_SLICE";

    /// <summary>Package path of The_Island's sublevels.</summary>
    public const string MapsPath = "/Game/ConZ_Files/Maps/The_Island/";

    /// <summary>The slice folder from <c>SCUM_MAP_SLICE</c>, or null when unset/empty.</summary>
    public static string? Root
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim().Trim('"'));
        }
    }

    /// <summary>Skip text when the slice is unavailable; null when it is available.</summary>
    public static string? SkipReason =>
        Root is not { } root
            ? $"{EnvironmentVariable} is not set (optional map slice with SCUM/Content/ConZ_Files/Maps/The_Island)."
            : !Directory.Exists(root)
                ? $"{EnvironmentVariable} points to a missing directory."
                : AssetCatalog.FindLooseProjectRoot(root) is null
                    ? $"{EnvironmentVariable} has no SCUM/Content folder."
                    : null;

    /// <summary>Opens the slice as a loose catalog, with optional loose overlays layered on top.</summary>
    public static AssetCatalog Open(params string[] overlays) =>
        AssetCatalog.OpenLoose(
            SkipReason is null ? Root! : throw new InvalidOperationException(SkipReason),
            new AssetCatalogOptions { LooseOverlays = overlays });
}

/// <summary>A <see cref="FactAttribute"/> skipped when the map slice (<c>SCUM_MAP_SLICE</c>) is unavailable.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class MapSliceFactAttribute : FactAttribute
{
    public MapSliceFactAttribute()
    {
        if (MapSlice.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
