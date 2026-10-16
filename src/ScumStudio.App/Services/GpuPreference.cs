using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ScumStudio.App.Services;

/// <summary>Where Windows keeps its per-app graphics preferences (a seam for tests).</summary>
public interface IGpuPreferenceStore
{
    /// <summary>The value stored for <paramref name="executable"/>, or null.</summary>
    string? Read(string executable);

    /// <summary>Stores <paramref name="value"/> for <paramref name="executable"/>.</summary>
    void Write(string executable, string value);
}

/// <summary>
/// "Use the high-performance GPU" (Settings › Graphics card): on a laptop with two GPUs, Windows' own per-app preference
/// (Settings › System › Display › Graphics) for this executable, set only when the user clicks the button.
/// </summary>
/// <remarks>
/// Native apps ask for the fast GPU by exporting <c>NvOptimusEnablement</c> and <c>AmdPowerXpressRequestHighPerformance</c>
/// from their executable. A .NET app cannot: its executable is the stock apphost and managed code exports no data symbols
/// (DllExport covers functions, not globals). The supported way is the value Windows 10 1803 and later read for every
/// graphics API from <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>, named by the executable's full path, with
/// <c>GpuPreference=2;</c> (high performance). It applies when the app starts, so after a restart.
/// </remarks>
public static class GpuPreference
{
    /// <summary>The key under HKEY_CURRENT_USER.</summary>
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>The preference Windows reads for "High performance".</summary>
    public const int HighPerformance = 2;

    /// <summary>True on Windows 10 1803 (build 17134) and later, where Windows reads the preference.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134);

    /// <summary>This process's executable (the value's name), or null.</summary>
    public static string? Executable => Environment.ProcessPath;

    /// <summary>The <c>GpuPreference</c> in <paramref name="value"/> (0 = let Windows decide when there is none).</summary>
    public static int Parse(string? value)
    {
        foreach (var pair in (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[0], "GpuPreference", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[1], out var preference))
            {
                return preference;
            }
        }

        return 0;
    }

    /// <summary>
    /// <paramref name="value"/> with <c>GpuPreference</c> set to <paramref name="preference"/>; the other settings Windows keeps in
    /// the same value (e.g. <c>SwapEffectUpgradeEnable=1;</c>) stay as they are.
    /// </summary>
    public static string With(string? value, int preference)
    {
        var pairs = (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
            .Append($"GpuPreference={preference}");
        return string.Join(string.Empty, pairs.Select(p => p + ";"));
    }

    /// <summary>True when Windows is told to run <paramref name="executable"/> on the high-performance GPU.</summary>
    public static bool IsHighPerformance(IGpuPreferenceStore store, string executable)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Parse(store.Read(executable)) == HighPerformance;
    }

    /// <summary>Tells Windows to run <paramref name="executable"/> on the high-performance GPU (from its next start).</summary>
    public static void SetHighPerformance(IGpuPreferenceStore store, string executable)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(executable);
        store.Write(executable, With(store.Read(executable), HighPerformance));
    }
}

/// <summary>The preferences in the current user's registry.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryGpuPreferenceStore : IGpuPreferenceStore
{
    /// <inheritdoc />
    public string? Read(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(GpuPreference.KeyPath);
        return key?.GetValue(executable) as string;
    }

    /// <inheritdoc />
    public void Write(string executable, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GpuPreference.KeyPath, writable: true);
        key.SetValue(executable, value, RegistryValueKind.String);
    }
}
