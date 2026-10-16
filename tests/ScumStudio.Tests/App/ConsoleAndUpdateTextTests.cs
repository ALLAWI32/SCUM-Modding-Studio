using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;

namespace ScumStudio.Tests.App;

/// <summary>D20 (the console would not open after errors) and D11 (the update error text cut off), both seviscache's.</summary>
public sealed class ConsoleAndUpdateTextTests
{
    /// <summary>
    /// D20: after the Not-a-Number errors the saved camera held NaN; JSON refused it and every save threw, so opening the
    /// console stopped before the panel was told to show. A save never throws now, a NaN view is not kept and a listener
    /// that throws does not stop the others.
    /// </summary>
    [Fact]
    public void TheViewStateSavesWhateverHappened()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-uistate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UiStateStore(dir, NullLogger.Instance);
            store.Update(u => u with { MapView = new MapView(100f, 200f, 300f, 10f, -20f) });
            var heard = 0;
            store.Changed += _ => throw new InvalidOperationException("a broken view");
            store.Changed += _ => heard++;

            var saved = store.Update(u => u with { MapView = new MapView(float.NaN, 0f, 0f, 0f, 0f), LogPanelOpen = true });

            Assert.Equal(1, heard);
            Assert.True(saved.LogPanelOpen);
            Assert.Equal(new MapView(100f, 200f, 300f, 10f, -20f), saved.MapView); // the last good view
            Assert.Contains("\"logPanelOpen\": true", File.ReadAllText(Path.Combine(dir, UiStateStore.FileName)), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>D11: "La actualización falló:" and nothing after: the reason is never empty and keeps the inner messages.</summary>
    [Fact]
    public void AnUpdateFailureAlwaysSaysWhy()
    {
        Assert.Equal("IOException", MainWindowViewModel.FailureReason(new IOException(string.Empty)));
        Assert.Equal("Access to the path 'C:\\x\\ScumStudio.App.exe.old' is denied.",
            MainWindowViewModel.FailureReason(new UnauthorizedAccessException("  Access to the path 'C:\\x\\ScumStudio.App.exe.old' is denied.\r\n")));
        Assert.Equal("The SSL connection could not be established. — Unable to read data from the transport connection.",
            MainWindowViewModel.FailureReason(new HttpRequestException("The SSL connection could not be established.", new IOException("Unable to read data from the transport connection."))));
    }
}
