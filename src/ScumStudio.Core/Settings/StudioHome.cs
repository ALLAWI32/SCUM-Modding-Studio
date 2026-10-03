namespace ScumStudio.Core.Settings;

/// <summary>
/// Locates the per-user data folder that holds <c>settings.json</c> and the protected AES key.
/// </summary>
/// <remarks>
/// Default: <c>Environment.SpecialFolder.LocalApplicationData/ScumStudio</c>
/// (Windows <c>%LOCALAPPDATA%\ScumStudio</c>, Linux <c>~/.local/share/ScumStudio</c>, macOS
/// <c>~/Library/Application Support/ScumStudio</c>). The environment variable <see cref="EnvironmentVariable"/>
/// overrides it (tests, portable installs).
/// </remarks>
public static class StudioHome
{
    /// <summary>Environment variable that overrides the data folder.</summary>
    public const string EnvironmentVariable = "SCUMSTUDIO_HOME";

    /// <summary>Folder name below LocalApplicationData.</summary>
    public const string FolderName = "ScumStudio";

    /// <summary>
    /// Returns the data folder (absolute; not created). Uses <see cref="EnvironmentVariable"/> when set, otherwise
    /// LocalApplicationData, falling back to the user profile when the OS reports no LocalApplicationData.
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither the override nor any per-user folder is available.</exception>
    public static string GetDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath.Trim().Trim('"'));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            return Path.Combine(localAppData, FolderName);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            return Path.Combine(profile, "." + FolderName.ToLowerInvariant());
        }

        throw new InvalidOperationException(
            $"No per-user data folder is available; set the {EnvironmentVariable} environment variable.");
    }

    /// <summary>
    /// Creates <paramref name="directory"/> if needed. On Unix a newly created folder gets owner-only permissions
    /// (0700); existing folders are left as they are.
    /// </summary>
    public static string EnsureDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        return directory;
    }
}
