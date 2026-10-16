using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// D03: the log line the Map writes at each streaming step (GraphicsStatus.Streamed) counts what the studio holds on the
/// graphics card; the counts must follow every upload, replacement and release exactly, or the trail before a crash lies.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class GpuLiveCountTests
{
    [GlFact]
    public void TheCountsFollowEveryUploadSharpeningAndRelease()
    {
        using var gl = GlHarness.Create(32, 32);
        var (textures, textureBytes) = GpuTexture.Live;
        var (meshes, meshBytes) = GpuMesh.Live;

        var small = gl.Renderer.CreateTexture(4, 4, new byte[4 * 4 * 4]);
        Assert.Equal((textures + 1, textureBytes + small.EstimatedBytes), GpuTexture.Live);
        small.TakeOver(GlStrictValidationTests.Compressed(gl.Renderer, CompressedFormat.Bc1, 64, 64, srgb: true)); // sharpened in place
        Assert.Equal((textures + 1, textureBytes + small.EstimatedBytes), GpuTexture.Live);

        var cube = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
        var gpu = gl.Renderer.Meshes[cube.Id];
        var vertexAndIndexBytes = ((long)gpu.VertexCount * PreparedMesh.FloatsPerVertex + gpu.IndexCount) * sizeof(float);
        Assert.Equal((meshes + 1, meshBytes + vertexAndIndexBytes), GpuMesh.Live);
        gpu.SetInstances(new InstanceData[10]);
        Assert.Equal(meshBytes + vertexAndIndexBytes + (10 * InstanceData.SizeInBytes), GpuMesh.Live.Bytes);
        gpu.SetInstances(new InstanceData[3]);
        Assert.Equal(meshBytes + vertexAndIndexBytes + (3 * InstanceData.SizeInBytes), GpuMesh.Live.Bytes);

        gl.Renderer.RemoveMesh(cube);
        small.Dispose();
        small.Dispose(); // twice: counted once
        Assert.Equal((textures, textureBytes), GpuTexture.Live);
        Assert.Equal((meshes, meshBytes), GpuMesh.Live);
    }
}
