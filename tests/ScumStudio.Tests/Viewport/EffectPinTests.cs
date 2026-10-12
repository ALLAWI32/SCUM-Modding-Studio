using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Mesh-less effect actors are orange Effect pins (<see cref="SpawnMarkers.IsPinOnly"/>): a Wwise sound is one too, so the
/// fire's sound beside a deleted fireplace can be seen and picked.
/// </summary>
public sealed class EffectPinTests
{
    [Fact]
    public void AWwiseAmbientSoundIsAnEffectPin()
    {
        var sound = Actor("Fire_Sound_1", "/Script/AkAudio.AkAmbientSound", "AkAmbientSound", ActorKind.Other, Component(6, "AkComponent0", "AkComponent"));

        Assert.True(SpawnMarkers.IsPinOnly(sound, out var effect));
        Assert.True(effect);
    }

    [Fact]
    public void APointLightIsStillAnEffectPin()
    {
        var light = Actor("PointLight_1", "/Script/Engine.PointLight", "PointLight", ActorKind.Light, Component(6, "LightComponent0", "PointLightComponent"));

        Assert.True(SpawnMarkers.IsPinOnly(light, out var effect));
        Assert.True(effect);
    }

    [Fact]
    public void AStaticMeshActorIsNoPin()
    {
        var mesh = Actor("StaticMeshActor_1", "/Script/Engine.StaticMeshActor", "StaticMeshActor", ActorKind.StaticMeshActor,
            Component(6, "StaticMeshComponent0", "StaticMeshComponent", "/Game/ConZ_Files/Props/Camp/SM_Fireplace_01.SM_Fireplace_01"));

        Assert.False(SpawnMarkers.IsPinOnly(mesh, out var effect));
        Assert.False(effect);
    }

    private static ComponentRecord Component(int index, string name, string className, string? mesh = null) =>
        new(index, name, className, true, null, FTransform.Identity, new FTransform(new FVector(100, 0, 0)), mesh, []);

    private static ActorRecord Actor(string name, string classPath, string className, ActorKind kind, ComponentRecord root) =>
        new(5, name, classPath, root.ExportIndex, [root], root.WorldTransform, kind, root.StaticMeshPath, []) { ClassName = className };
}
