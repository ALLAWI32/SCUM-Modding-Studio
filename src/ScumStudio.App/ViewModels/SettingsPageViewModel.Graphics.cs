using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Rendering.Context;

namespace ScumStudio.App.ViewModels;

/// <summary>Settings › Graphics card (D03): the GPU the 3D views draw with, what was turned off for it, and Windows' GPU choice.</summary>
public sealed partial class SettingsPageViewModel
{
    private bool _highPerformanceJustSet;

    /// <summary>The GPU in use: renderer, maker and OpenGL version (or why none is known).</summary>
    [ObservableProperty]
    private string _gpuInUse = string.Empty;

    /// <summary>What to know about the GPU: a built-in chip, no driver, features turned off (empty when nothing).</summary>
    [ObservableProperty]
    private string _gpuWarning = string.Empty;

    /// <summary>True when <see cref="GpuWarning"/> has something to say.</summary>
    [ObservableProperty]
    private bool _hasGpuWarning;

    /// <summary>What the high-performance GPU button did or why it cannot (empty before a click).</summary>
    [ObservableProperty]
    private string _highPerformanceStatus = string.Empty;

    /// <summary>True while the high-performance GPU button can be clicked.</summary>
    [ObservableProperty]
    private bool _canUseHighPerformanceGpu;

    /// <summary>Where the GPU choice is written (the current user's registry; tests set a fake).</summary>
    internal IGpuPreferenceStore? GpuPreferences { get; set; } = OperatingSystem.IsWindows() ? new RegistryGpuPreferenceStore() : null;

    /// <summary>The executable the GPU choice is for (this process's; tests set one).</summary>
    internal string? GpuExecutable { get; set; } = GpuPreference.Executable;

    /// <summary>
    /// The warning lines of Settings › Graphics card for <paramref name="advice"/> with <paramref name="off"/> turned off, in
    /// the UI language (empty when there is nothing to say).
    /// </summary>
    internal static string GraphicsWarningText(GraphicsAdvice advice, IEnumerable<GraphicsFeature> off)
    {
        var lines = new List<string>();
        if (advice.HasFlag(GraphicsAdvice.Software))
        {
            lines.Add(Loc.T("Settings.Graphics.Software"));
        }

        if (advice.HasFlag(GraphicsAdvice.Integrated))
        {
            lines.Add(Loc.T("Settings.Graphics.Integrated"));
        }

        if (advice.HasFlag(GraphicsAdvice.FallbackUsed))
        {
            lines.Add(Loc.F("Settings.Graphics.Fallback", string.Join(", ", off.Select(GraphicsStatus.FeatureName))));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Tells Windows to run the studio on the high-performance GPU (from the next start); never done without this click.</summary>
    [RelayCommand]
    private void UseHighPerformanceGpu()
    {
        if (GpuPreferences is not { } store || GpuExecutable is not { Length: > 0 } exe)
        {
            return;
        }

        try
        {
            GpuPreference.SetHighPerformance(store, exe);
            _highPerformanceJustSet = true;
            _services.Logger.LogInformation("Graphics: Windows will run {Executable} on the high-performance GPU (the user's choice in Settings)", exe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            HighPerformanceStatus = Loc.F("Settings.Graphics.HighPerformance.Failed", ex.Message);
            CanUseHighPerformanceGpu = true;
            return;
        }

        RefreshGraphics();
    }

    private void StartGraphics()
    {
        GraphicsStatus.Changed += OnGraphicsChanged;
        RefreshGraphics();
    }

    private void StopGraphics() => GraphicsStatus.Changed -= OnGraphicsChanged;

    private void OnGraphicsChanged() => _services.Dispatcher.Post(RefreshGraphics);

    /// <summary>Re-reads the GPU, its fallbacks and the GPU choice (also after the UI language changed).</summary>
    internal void RefreshGraphics()
    {
        var gpu = GraphicsStatus.Gpu;
        GpuInUse = gpu is not null
            ? Loc.F("Settings.Graphics.InUse", gpu.Renderer, gpu.Vendor, gpu.Version)
            : GraphicsStatus.StartError is { } error ? Loc.F("Settings.Graphics.StartFailed", error) : Loc.T("Settings.Graphics.Unknown");
        var off = GraphicsStatus.Fallbacks.Snapshot().Keys.OrderBy(f => f).ToList();
        GpuWarning = GraphicsWarningText(GraphicsStatus.Advice(gpu, off), off);
        HasGpuWarning = GpuWarning.Length > 0;

        bool set;
        try
        {
            set = GpuPreferences is { } store && GpuExecutable is { Length: > 0 } exe && GpuPreference.IsHighPerformance(store, exe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            set = false;
        }

        (CanUseHighPerformanceGpu, HighPerformanceStatus) = (GpuPreference.IsSupported && GpuPreferences is not null && GpuExecutable is { Length: > 0 }, set) switch
        {
            (false, _) => (false, Loc.T("Settings.Graphics.HighPerformance.Unsupported")),
            (true, true) => (false, Loc.T(_highPerformanceJustSet ? "Settings.Graphics.HighPerformance.Done" : "Settings.Graphics.HighPerformance.Already")),
            _ => (true, string.Empty),
        };
    }
}
