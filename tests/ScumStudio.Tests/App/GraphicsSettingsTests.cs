using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Rendering.Context;
using Xunit;

namespace ScumStudio.Tests.App;

/// <summary>D03: Settings › Graphics card's decisions (what it warns about, Windows' GPU choice) without a GPU or the registry.</summary>
public sealed class GraphicsSettingsTests
{
    private const string Exe = @"C:\Games\ScumStudio\ScumStudio.App.exe";

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("GpuPreference=2;", 2)]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;", 1)]
    [InlineData("SwapEffectUpgradeEnable=1;", 0)]
    public void ThePreferenceIsReadFromWindowsValue(string? value, int expected) => Assert.Equal(expected, GpuPreference.Parse(value));

    [Theory]
    [InlineData(null, "GpuPreference=2;")]
    [InlineData("GpuPreference=1;", "GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    [InlineData("GpuPreference=0;SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    public void HighPerformanceKeepsWindowsOtherSettings(string? before, string after) =>
        Assert.Equal(after, GpuPreference.With(before, GpuPreference.HighPerformance));

    [Fact]
    public void TheButtonWritesHighPerformanceForThisExecutableOnly()
    {
        var store = new FakeStore { [@"C:\Other\game.exe"] = "GpuPreference=1;", [Exe] = "SwapEffectUpgradeEnable=1;" };
        Assert.False(GpuPreference.IsHighPerformance(store, Exe));
        GpuPreference.SetHighPerformance(store, Exe);
        Assert.True(GpuPreference.IsHighPerformance(store, Exe));
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", store[Exe]);
        Assert.Equal("GpuPreference=1;", store[@"C:\Other\game.exe"]);
    }

    [Fact]
    public void AFailedFrameDropsThe32FeaturesFirstAndOnlyForGraphicsErrors()
    {
        var gl = new InvalidOperationException("OpenGL error after rendering the scene: InvalidOperation");
        // r37: the temporal pass (steady foliage) is new too, so it goes with the 3.2 features.
        Assert.Equal(new[] { GraphicsFeature.Multisampling, GraphicsFeature.AlphaToCoverage, GraphicsFeature.TemporalStabilization }, GraphicsStatus.FeaturesToDrop(gl, 1));
        Assert.Equal(new[] { GraphicsFeature.Multisampling, GraphicsFeature.AlphaToCoverage, GraphicsFeature.TemporalStabilization, GraphicsFeature.CompressedTextures },
            GraphicsStatus.FeaturesToDrop(gl, 2));
        Assert.Empty(GraphicsStatus.FeaturesToDrop(new KeyNotFoundException("a clone's id"), 3));
    }

    [Fact]
    public void AdviceWarnsAboutBuiltInChipsSoftwareAndFallbacks()
    {
        var rtx = new GlInfo("NVIDIA Corporation", "NVIDIA GeForce RTX 5080/PCIe/SSE2", "4.6.0 NVIDIA 616.92", 4, 6, true);
        var amd = rtx with { Vendor = "ATI Technologies Inc.", Renderer = "AMD Radeon RX 9070 XT", Version = "4.6.0 Core Profile Context 25.9.1" };
        var apu = amd with { Renderer = "AMD Radeon(TM) Graphics" };
        var gdi = rtx with { Vendor = "Microsoft Corporation", Renderer = "GDI Generic", Version = "1.1.0" };
        Assert.Equal(GraphicsAdvice.None, GraphicsStatus.Advice(rtx, []));
        Assert.Equal(GraphicsAdvice.None, GraphicsStatus.Advice(amd, []));
        Assert.Equal(GraphicsAdvice.FallbackUsed, GraphicsStatus.Advice(amd, [GraphicsFeature.Multisampling]));
        Assert.Equal(GraphicsAdvice.Integrated, GraphicsStatus.Advice(apu, []));
        Assert.Equal(GraphicsAdvice.Software, GraphicsStatus.Advice(gdi, []));
        Assert.Equal(GraphicsAdvice.None, GraphicsStatus.Advice(null, []));
    }

    [Fact]
    public void TheWarningNamesEveryFeatureTurnedOffInTheUiLanguage()
    {
        GraphicsFeature[] off = [GraphicsFeature.Multisampling, GraphicsFeature.CompressedTextures];
        var text = SettingsPageViewModel.GraphicsWarningText(GraphicsAdvice.FallbackUsed | GraphicsAdvice.Integrated, off);
        Assert.Contains(Loc.T("Settings.Graphics.Integrated"), text, StringComparison.Ordinal);
        Assert.Contains(Loc.T("Settings.Graphics.Feature.Multisampling"), text, StringComparison.Ordinal);
        Assert.Contains(Loc.T("Settings.Graphics.Feature.CompressedTextures"), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T("Settings.Graphics.Feature.AlphaToCoverage"), text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, SettingsPageViewModel.GraphicsWarningText(GraphicsAdvice.None, []));
    }

    [Fact]
    public void EveryLanguageHasTheGraphicsCardStrings()
    {
        // Loc falls back to English for a missing key: read each language's own embedded table instead.
        var keys = new[]
        {
            "Settings.Graphics", "Settings.Graphics.Caption", "Settings.Graphics.InUse", "Settings.Graphics.InUse.Tip", "Settings.Graphics.Unknown",
            "Settings.Graphics.StartFailed", "Settings.Graphics.Integrated", "Settings.Graphics.Software", "Settings.Graphics.Fallback",
            "Settings.Graphics.Feature.Multisampling", "Settings.Graphics.Feature.AlphaToCoverage", "Settings.Graphics.Feature.CompressedTextures",
            "Settings.Graphics.HighPerformance", "Settings.Graphics.HighPerformance.Tip", "Settings.Graphics.HighPerformance.Done",
            "Settings.Graphics.HighPerformance.Already", "Settings.Graphics.HighPerformance.Failed", "Settings.Graphics.HighPerformance.Unsupported",
            "Settings.Graphics.FallbackToast", "Settings.Graphics.FallbackToast.Message", "Viewport.GraphicsRecovered", "Viewport.GraphicsStopped",
        };
        var tables = typeof(Loc).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("Strings.", StringComparison.Ordinal)).ToList();
        Assert.Equal(10, tables.Count);
        foreach (var table in tables)
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream(table)!;
            using var json = System.Text.Json.JsonDocument.Parse(stream);
            foreach (var key in keys)
            {
                Assert.True(json.RootElement.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 }, $"{table} lacks {key}");
                var english = key.EndsWith("InUse", StringComparison.Ordinal) ? 3 : key is "Settings.Graphics.StartFailed" or "Settings.Graphics.Fallback"
                    or "Settings.Graphics.HighPerformance.Failed" or "Settings.Graphics.FallbackToast.Message" or "Viewport.GraphicsRecovered" or "Viewport.GraphicsStopped" ? 1 : 0;
                for (var i = 0; i < english; i++)
                {
                    Assert.Contains("{" + i + "}", value.GetString()!, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void TheUiThreadCompositorIsATestSwitchOffByDefault()
    {
        // D03: one thread for every GL context of the studio, for AMD testers only; the app keeps WGL first either way.
        var before = Environment.GetEnvironmentVariable(ScumStudio.App.Program.UiThreadCompositorVariable);
        try
        {
            bool OnUiThread(string? value)
            {
                Environment.SetEnvironmentVariable(ScumStudio.App.Program.UiThreadCompositorVariable, value);
                var options = ScumStudio.App.Program.Win32Options([]);
                Assert.Equal(Avalonia.Win32RenderingMode.Wgl, options.RenderingMode.First());
                return options.ShouldRenderOnUIThread;
            }

            Assert.False(OnUiThread(null));
            Assert.False(OnUiThread("0"));
            Assert.True(OnUiThread("1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScumStudio.App.Program.UiThreadCompositorVariable, before);
        }
    }

    private sealed class FakeStore : Dictionary<string, string>, IGpuPreferenceStore
    {
        public FakeStore()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public string? Read(string executable) => TryGetValue(executable, out var value) ? value : null;

        public void Write(string executable, string value) => this[executable] = value;
    }
}
