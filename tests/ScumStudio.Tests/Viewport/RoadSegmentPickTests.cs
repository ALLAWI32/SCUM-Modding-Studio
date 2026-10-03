using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner report: clicking a bridge selected it together with the whole road (every spline piece of a landscape spline
/// proxy shares one actor). Each spline mesh piece gets its own key so a click picks that piece alone.
/// </summary>
public sealed class RoadSegmentPickTests
{
    [Fact]
    public void EverySplinePieceHasItsOwnKey()
    {
        var b = new FakeLevelBuilder("/Game/Maps/Roads");
        var proxy = b.Actor("LandscapeStreamingProxy_0", "/Script/Landscape.LandscapeStreamingProxy");
        var root = b.Component(proxy, "RootComponent0");
        b.Root(proxy, root);
        b.Component(proxy, "SplineMeshComponent_0", "SplineMeshComponent", attachTo: root, mesh: "/Game/Roads/SM_Asphalt.SM_Asphalt");
        b.Component(proxy, "SplineMeshComponent_1", "SplineMeshComponent", attachTo: root, mesh: "/Game/Roads/SM_Asphalt.SM_Asphalt");
        b.Component(proxy, "Sign", "StaticMeshComponent", attachTo: root, mesh: "/Game/Roads/SM_Sign.SM_Sign");
        var data = b.Build();
        var spline = new SplineMeshParams { StartTangent = new FVector(100, 0, 0), EndPos = new FVector(100, 0, 0), EndTangent = new FVector(100, 0, 0) };
        var exports = data.Exports.Select(e => e.ClassName == "SplineMeshComponent" ? e with { SplineMesh = spline } : e).ToArray();

        var keys = LevelScenePreparer.CollectPlacements(LevelDocument.FromData(data with { Exports = exports }), 0)
            .ToDictionary(p => p.Component!.Name, p => p.InstanceKey);

        Assert.Equal(InstanceKey.Segment, keys["SplineMeshComponent_0"]!.Value.InstanceIndex);
        Assert.NotEqual(keys["SplineMeshComponent_0"], keys["SplineMeshComponent_1"]);
        Assert.Equal(InstanceKey.Part, keys["Sign"]!.Value.InstanceIndex); // a part: a click picks it alone only in part mode
    }
}
