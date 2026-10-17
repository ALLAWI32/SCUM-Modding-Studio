using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "read 9172 ms (6337 MB allocated)" for one streaming step, then the app stalls while the collector cleans up.
/// CUE4Parse read an imported package again for every import it resolved, so the 16,677 objects of the military base
/// (TV_Base_A_3_Abandoned) read their Blueprints' packages thousands of times: 9.2 GB and 10 s for one level. Inside a
/// <see cref="ScumFileProvider.ImportScope"/> (every level read and prepare opens one) each is read once. Real game files
/// only (<c>SCUM_PAKS</c>).
/// </summary>
[Collection(TimingCollection.Name)] // the allocation count is the whole process's
public sealed class ImportScopeRealTests(ITestOutputHelper output)
{
    private const string Base = "/Game/ConZ_Files/Maps/The_Island/TV_Base_A_3_Abandoned";

    [Fact]
    public void TheMilitaryBaseIsReadWithoutGigabytesOfGarbage()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore()! });
        var before = GC.GetTotalAllocatedBytes(true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var data = new Cue4ParseLevelReader(catalog).ReadLevel(Base);
        var mb = (GC.GetTotalAllocatedBytes(true) - before) >> 20;
        output.WriteLine($"{Base}: {data.Exports.Count:N0} exports, {data.SynthesizedComponents.Count:N0} components from Blueprints, {mb:N0} MB allocated, {clock.Elapsed.TotalMilliseconds:N0} ms (9,154 MB and 10 s before)");
        // What the levels read to, for comparing with a build without the scope (the same game files give the same lines).
        var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true, ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles };
        var reader = new Cue4ParseLevelReader(catalog);
        foreach (var level in new[] { Base, "/Game/ConZ_Files/Maps/The_Island/A_3_TV_Base_Entrance", "/Game/ConZ_Files/Maps/The_Island/A_4_Blato_01" })
        {
            var read = reader.ReadLevel(level);
            var text = System.Text.Json.JsonSerializer.Serialize(new { read.Exports, read.SynthesizedComponents, read.Warnings, read.ActorIndices }, json);
            output.WriteLine($"fingerprint {level}: {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16]} ({text.Length:N0} chars)");
        }

        Assert.True(data.Exports.Count > 10_000, $"{data.Exports.Count} exports");
        Assert.InRange(mb, 0, 2_000);
    }
}
