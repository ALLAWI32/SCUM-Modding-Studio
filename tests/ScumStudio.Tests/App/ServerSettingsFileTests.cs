using System.Text;
using ScumStudio.App.Services;

namespace ScumStudio.Tests.App;

/// <summary>
/// <see cref="ServerSettingsFile"/> saving: only the values of the keys given change; every other byte of the server's file
/// (comments, spacing, line endings, a byte order mark, a last line without an ending) stays as the admin wrote it.
/// </summary>
public sealed class ServerSettingsFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "scumstudio-ini-tests", Guid.NewGuid().ToString("N"));

    public ServerSettingsFileTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void ReplacesInPlace()
    {
        var path = Write("[World]\r\nscum.MaxAllowedPuppets=200\r\nscum.MaxAllowedDrones=4\r\n");
        var file = ServerSettingsFile.Load(path);

        file.Save(new Dictionary<string, string> { ["scum.MaxAllowedDrones"] = "7" });

        Assert.Equal("[World]\r\nscum.MaxAllowedPuppets=200\r\nscum.MaxAllowedDrones=7\r\n", Read(path));
        Assert.Equal("7", ServerSettingsFile.Load(path).Get("scum.MaxAllowedDrones"));
    }

    [Fact]
    public void AppendsAMissingKeyToItsSection()
    {
        var path = Write("[General]\r\nscum.ServerName=Test\r\n[World]\r\nscum.MaxAllowedPuppets=200\r\n\r\n[Vehicles]\r\nscum.RagerMaxAmount=10\r\n");

        ServerSettingsFile.Load(path).Save(new Dictionary<string, string> { ["scum.AllowSectorRespawn"] = "False" });

        // After the section's last value, before the blank line that ends it.
        Assert.Equal("[General]\r\nscum.ServerName=Test\r\n[World]\r\nscum.MaxAllowedPuppets=200\r\nscum.AllowSectorRespawn=False\r\n\r\n[Vehicles]\r\nscum.RagerMaxAmount=10\r\n", Read(path));
    }

    [Fact]
    public void CreatesTheSection()
    {
        var path = Write("[General]\r\nscum.ServerName=Test");

        ServerSettingsFile.Load(path).Save(new Dictionary<string, string> { ["scum.MaxAllowedDrones"] = "2" });

        Assert.Equal("[General]\r\nscum.ServerName=Test\r\n[World]\r\nscum.MaxAllowedDrones=2\r\n", Read(path));
    }

    [Fact]
    public void KeepsCrlf()
    {
        var crlf = Write("[World]\r\nscum.A=1\r\nscum.B=2\r\n");
        var lf = Write("[World]\nscum.A=1\nscum.B=2\n", "lf.ini");

        ServerSettingsFile.Load(crlf).Save(new Dictionary<string, string> { ["scum.A"] = "3", ["scum.C"] = "4" });
        ServerSettingsFile.Load(lf).Save(new Dictionary<string, string> { ["scum.A"] = "3", ["scum.C"] = "4" });

        Assert.Equal("[World]\r\nscum.A=3\r\nscum.B=2\r\nscum.C=4\r\n", Read(crlf));
        Assert.Equal("[World]\nscum.A=3\nscum.B=2\nscum.C=4\n", Read(lf));
    }

    [Fact]
    public void OtherLinesStayByteIdentical()
    {
        // A byte order mark, a comment, spaces around '=', a tab, an unknown key, mixed case, no ending on the last line.
        var text = "; my server\r\n[World]\r\nscum.MaxAllowedPuppets = 200\r\nscum.PuppetHealthMultiplier=\t1.5\r\nMyOwnKey=keep me\r\nscum.EnableLootPuppetHorde=true";
        var path = Path.Combine(_folder, ServerSettingsFile.FileName);
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]);

        ServerSettingsFile.Load(path).Save(new Dictionary<string, string> { ["scum.MaxAllowedPuppets"] = "500" });

        var expected = text.Replace("scum.MaxAllowedPuppets = 200", "scum.MaxAllowedPuppets = 500", StringComparison.Ordinal);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(expected)], File.ReadAllBytes(path));
    }

    [Fact]
    public void WritesTheBakOnce()
    {
        const string original = "[World]\r\nscum.MaxAllowedDrones=4\r\n";
        var path = Write(original);
        var file = ServerSettingsFile.Load(path);

        file.Save(new Dictionary<string, string> { ["scum.MaxAllowedDrones"] = "5" });
        file.Save(new Dictionary<string, string> { ["scum.MaxAllowedDrones"] = "6" });

        // The session backup is the file as it was before this session's first save, not the state between two saves.
        Assert.Equal(original, Read(path + ".bak"));
        Assert.Equal(original, Read(path + ".session.bak"));
        Assert.Equal("[World]\r\nscum.MaxAllowedDrones=6\r\n", Read(path));

        // A later session keeps the owner's original as .bak; only its own starting point goes to .session.bak.
        var next = ServerSettingsFile.Load(path);
        next.Save(new Dictionary<string, string> { ["scum.MaxAllowedDrones"] = "7" });

        Assert.Equal(original, Read(path + ".bak"));
        Assert.Equal("[World]\r\nscum.MaxAllowedDrones=6\r\n", Read(path + ".session.bak"));
        Assert.Equal("[World]\r\nscum.MaxAllowedDrones=7\r\n", Read(path));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // a temp folder left behind is harmless
        }
    }

    private string Write(string text, string name = ServerSettingsFile.FileName)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        return path;
    }

    private static string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));
}
