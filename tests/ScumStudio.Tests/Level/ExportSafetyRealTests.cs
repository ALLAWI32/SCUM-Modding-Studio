using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Export;
using ScumStudio.Pak;
using ScumStudio.Pak.Server;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The export guards on the real game files (<c>SCUM_PAKS</c>, key from this PC's store; does nothing without them): the
/// client install has no server stub to give (D12), so a server pak with client events is refused there.
/// <c>SCUM_SERVER_PAKS</c> (a dedicated server's Paks) also checks the real stub. Copies of lights and effects (D15, D04):
/// <see cref="FxCopyRealTests"/>.
/// </summary>
public sealed class ExportSafetyRealTests(ITestOutputHelper output)
{
    [Fact]
    public void TheClientInstallHasNoServerStubSoClientEventsNeverPassAsOne()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        var key = AesKeyText.FromEnvironmentOrStore();
        var ex = Assert.Throws<ServerEventStubException>(() => ServerEventStubs.FromServerPaks(paks, key));
        output.WriteLine(ex.Message);
        using (var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = key }))
        {
            // The client's own Vehicle_Ignition_Start is the client cook: it is never taken for the server's stub.
            output.WriteLine(Assert.Throws<ServerEventStubException>(() => ServerEventStubs.FromCatalog(catalog)).Message);
        }

        if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is { Length: > 0 } serverPaks && Directory.Exists(serverPaks))
        {
            var (uasset, uexp) = ServerEventStubs.FromServerPaks(serverPaks, key).CreateStub("/Game/Mods/Test/WwiseAudio/Event/Test_Event");
            Assert.True(AkAudioEventStubBuilder.IsAkAudioEvent(uasset, uexp));
        }
    }
}
