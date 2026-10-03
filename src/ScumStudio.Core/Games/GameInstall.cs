namespace ScumStudio.Core.Games;

/// <summary>Which SCUM executable an install belongs to.</summary>
public enum GameInstallKind
{
    /// <summary>The game client (Steam app 513710, <c>SCUM.exe</c>, WindowsNoEditor cook).</summary>
    Client,

    /// <summary>The dedicated server (Steam app 3792580, <c>SCUMServer.exe</c>, WindowsServer cook).</summary>
    DedicatedServer,
}

/// <summary>A SCUM client or dedicated-server installation found by <see cref="GameLocator"/>.</summary>
/// <param name="Kind">Client or dedicated server.</param>
/// <param name="InstallDirectory">Folder containing the <c>SCUM</c> project folder (e.g. <c>...\steamapps\common\SCUM</c>).</param>
/// <param name="PaksDirectory">The <c>SCUM\Content\Paks</c> folder.</param>
/// <param name="ExecutablePath"><c>SCUM\Binaries\Win64\SCUM.exe</c> or <c>SCUMServer.exe</c>, when present.</param>
/// <param name="Source">How it was found, for display (e.g. "Steam library D:\SteamLibrary (appmanifest_513710.acf)").</param>
public sealed record GameInstall(
    GameInstallKind Kind,
    string InstallDirectory,
    string PaksDirectory,
    string? ExecutablePath,
    string Source)
{
    /// <summary>Name of the mods sub-folder of Paks used by the launcher and the modded server.</summary>
    public const string ModsFolderName = "~mods";

    /// <summary><c>Paks\~mods</c> (may not exist yet).</summary>
    public string ModsDirectory => Path.Combine(PaksDirectory, ModsFolderName);
}
