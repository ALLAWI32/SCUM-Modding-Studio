namespace ScumStudio.Modding.ServerMods;

/// <summary>
/// The dedicated server's <c>SCUM\Binaries\Win64</c> folder (where <c>SCUMServer.exe</c> lives) and the UE4SS parts next to it.
/// UE4SS runs Lua mods inside the server process, so a Lua mod can react to the game (a player logs in, dies, chats) and call
/// its functions. It is the community's framework (github.com/UE4SS-RE/RE-UE4SS), installed here only as files.
/// </summary>
public sealed class ServerInstall
{
    private ServerInstall(string win64) => Win64 = win64;

    /// <summary>The folder with <c>SCUMServer.exe</c>.</summary>
    public string Win64 { get; }

    /// <summary>UE4SS's own folder.</summary>
    public string Ue4ssFolder => Path.Combine(Win64, "ue4ss");

    /// <summary>Where each Lua mod has a folder.</summary>
    public string ModsFolder => Path.Combine(Ue4ssFolder, "Mods");

    /// <summary>The server's settings folder (<c>SCUM\Saved\Config\WindowsServer</c>): the ini files and the loot customization.</summary>
    public string ConfigFolder => Path.GetFullPath(Path.Combine(Win64, "..", "..", "Saved", "Config", "WindowsServer"));

    /// <summary>The proxy DLL the game loads UE4SS through.</summary>
    public string ProxyDll => Path.Combine(Win64, "dwmapi.dll");

    /// <summary>The proxy is parked under this name while UE4SS is switched off.</summary>
    public string ParkedProxyDll => ProxyDll + ".off";

    /// <summary>UE4SS's settings file.</summary>
    public string SettingsFile => Path.Combine(Ue4ssFolder, "UE4SS-settings.ini");

    /// <summary>UE4SS's log: what its Lua mods print, and why a mod did not load.</summary>
    public string LogFile => Path.Combine(Ue4ssFolder, "UE4SS.log");

    /// <summary>UE4SS is in place (its DLL and the proxy), ready or not.</summary>
    public bool HasUe4ss => File.Exists(Path.Combine(Ue4ssFolder, "UE4SS.dll")) && (File.Exists(ProxyDll) || File.Exists(ParkedProxyDll));

    /// <summary>UE4SS is installed and switched on.</summary>
    public bool IsUe4ssActive => HasUe4ss && File.Exists(ProxyDll);

    /// <summary>
    /// Finds the server from any folder inside it or above it: the server's <c>Paks</c> folder, <c>Content</c>, <c>SCUM</c>, its
    /// <c>Win64</c> folder or the folder of the whole server install. Null when no <c>SCUMServer.exe</c> is found.
    /// </summary>
    public static ServerInstall? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string? folder;
        try
        {
            folder = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (var i = 0; i < 6 && folder is not null; i++, folder = Path.GetDirectoryName(folder))
        {
            foreach (var candidate in new[] { folder, Path.Combine(folder, "Binaries", "Win64"), Path.Combine(folder, "SCUM", "Binaries", "Win64") })
            {
                if (File.Exists(Path.Combine(candidate, "SCUMServer.exe")))
                {
                    return new ServerInstall(candidate);
                }
            }
        }

        return null;
    }
}
