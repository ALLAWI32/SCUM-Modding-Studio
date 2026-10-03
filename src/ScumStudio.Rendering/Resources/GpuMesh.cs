using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Rendering.Resources;

/// <summary>A material section of an uploaded LOD: an index range and the texture it is drawn with.</summary>
public sealed class GpuSection
{
    internal GpuSection(PreparedSection section, GpuTexture? texture, float alphaCutoff = 0f, Vector4? tint = null)
    {
        AlphaCutoff = alphaCutoff;
        Tint = tint ?? Vector4.One;
        HasOwnColour = tint is not null && texture is null;
        FirstIndex = (uint)section.FirstIndex;
        IndexCount = (uint)section.IndexCount;
        BaseVertex = (uint)section.BaseVertex;
        Material = section.Material;
        Texture = texture;
    }

    /// <summary>Opacity clip value of a masked material (pixels whose texture alpha is below it are discarded); 0 = opaque.</summary>
    public float AlphaCutoff { get; }

    /// <summary>Linear RGBA multiplied into the section (the colour of a material without a texture); white by default.</summary>
    public Vector4 Tint { get; }

    /// <summary>An untextured material with its own colour: drawn with <see cref="Tint"/> only, never with the mesh's default texture.</summary>
    public bool HasOwnColour { get; }

    /// <summary>First index in the mesh's index buffer.</summary>
    public uint FirstIndex { get; }

    /// <summary>Number of indices.</summary>
    public uint IndexCount { get; }

    /// <summary>Added to every index.</summary>
    public uint BaseVertex { get; }

    /// <summary>Material path or name the section was prepared with.</summary>
    public string Material { get; }

    /// <summary>The section's own texture (not owned), or null to draw with <see cref="GpuMesh.Texture"/>.</summary>
    public GpuTexture? Texture { get; set; }
}

/// <summary>One uploaded level of detail.</summary>
/// <param name="ScreenSize">See <see cref="PreparedLod.ScreenSize"/>.</param>
/// <param name="Sections">Its material sections.</param>
public sealed record GpuLod(float ScreenSize, GpuSection[] Sections);

/// <summary>
/// A mesh uploaded to the GPU: one VAO with an interleaved vertex buffer (position, normal, uv) and an index buffer
/// holding every LOD (see <see cref="Lods"/>), plus a per-instance attribute buffer (<see cref="InstanceData"/>) that
/// keeps the instances of the last <see cref="SetInstances"/> until the scene changes.
/// </summary>
public sealed class GpuMesh : IDisposable
{
    private readonly GL _gl;
    private bool _disposed;

    /// <summary>Uploads <paramref name="mesh"/>; sections draw with the texture of their material in <paramref name="materialTextures"/>, else <paramref name="texture"/>.</summary>
    public unsafe GpuMesh(GL gl, int id, PreparedMesh mesh, GpuTexture? texture = null, IReadOnlyDictionary<string, GpuTexture>? materialTextures = null,
        IReadOnlyDictionary<string, float>? materialAlphaCutoffs = null, IReadOnlyDictionary<string, Vector4>? materialTints = null)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        ArgumentNullException.ThrowIfNull(mesh);
        Id = id;
        Name = mesh.Name;
        Bounds = mesh.Bounds;
        IndexCount = mesh.Indices.Length;
        VertexCount = mesh.VertexCount;
        Texture = texture;
        Lods = mesh.Lods.Select(l => new GpuLod(l.ScreenSize, l.Sections.Select(s =>
            new GpuSection(s, materialTextures is not null && s.Material.Length > 0 && materialTextures.TryGetValue(s.Material, out var t) ? t : null,
                materialAlphaCutoffs is not null && materialAlphaCutoffs.TryGetValue(s.Material, out var c) ? c : 0f,
                materialTints is not null && materialTints.TryGetValue(s.Material, out var tint) ? tint : null)).ToArray())).ToArray();
        LodScreenSizes = Lods.Select(l => l.ScreenSize).ToArray();

        Vao = gl.GenVertexArray();
        gl.BindVertexArray(Vao);

        VertexBuffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, VertexBuffer);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, mesh.Vertices, BufferUsageARB.StaticDraw);
        const uint stride = PreparedMesh.Stride;
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        IndexBuffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, IndexBuffer);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, mesh.Indices, BufferUsageARB.StaticDraw);

        InstanceBuffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, InstanceBuffer);
        const uint istride = InstanceData.SizeInBytes;
        for (uint column = 0; column < 4; column++)
        {
            var location = 3 + column;
            gl.EnableVertexAttribArray(location);
            gl.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false, istride, (void*)(InstanceData.ModelOffset + (column * 16)));
            gl.VertexAttribDivisor(location, 1);
        }

        gl.EnableVertexAttribArray(7);
        gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false, istride, (void*)InstanceData.TintOffset);
        gl.VertexAttribDivisor(7, 1);
        gl.EnableVertexAttribArray(8);
        gl.VertexAttribIPointer(8, 2, VertexAttribIType.UnsignedInt, istride, (void*)InstanceData.PickCodeOffset);
        gl.VertexAttribDivisor(8, 1);
        gl.EnableVertexAttribArray(9);
        gl.VertexAttribPointer(9, 2, VertexAttribPointerType.Float, false, istride, (void*)InstanceData.SurfaceOffset);
        gl.VertexAttribDivisor(9, 1);

        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>Renderer-assigned mesh id (reported by picking).</summary>
    public int Id { get; }

    /// <summary>Mesh name.</summary>
    public string Name { get; }

    /// <summary>Local bounds in the renderer's GL world units.</summary>
    public BoundingBox Bounds { get; }

    /// <summary>Number of indices in the index buffer (all LODs; 3 per triangle).</summary>
    public int IndexCount { get; }

    /// <summary>Number of vertices (all LODs).</summary>
    public int VertexCount { get; }

    /// <summary>Levels of detail, finest first.</summary>
    public GpuLod[] Lods { get; }

    /// <summary>The <see cref="GpuLod.ScreenSize"/> of every LOD, for <see cref="Cameras.LodMath.ChooseLod"/>.</summary>
    public float[] LodScreenSizes { get; }

    /// <summary>Number of instance records uploaded by <see cref="SetInstances"/>.</summary>
    public int InstanceCount { get; private set; }

    /// <summary>Default albedo texture of sections without their own (not owned; dispose it separately).</summary>
    public GpuTexture? Texture { get; set; }

    /// <summary>Terrain only: the layer textures tiled over the baked ground near the camera (see <see cref="TerrainDetailTextures"/>).</summary>
    public TerrainDetailTextures? Detail { get; set; }

    /// <summary>Vertex array object.</summary>
    public uint Vao { get; }

    /// <summary>Interleaved vertex buffer.</summary>
    public uint VertexBuffer { get; }

    /// <summary>Index buffer (uint32).</summary>
    public uint IndexBuffer { get; }

    /// <summary>Per-instance attribute buffer.</summary>
    public uint InstanceBuffer { get; }

    /// <summary>Uploads <paramref name="instances"/> into <see cref="InstanceBuffer"/>, replacing the previous records.</summary>
    public unsafe void SetInstances(ReadOnlySpan<InstanceData> instances)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InstanceCount = instances.Length;
        if (instances.IsEmpty)
        {
            return;
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, InstanceBuffer);
        fixed (InstanceData* p = instances)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(instances.Length * InstanceData.SizeInBytes), p, BufferUsageARB.DynamicDraw);
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>Overwrites one record uploaded by <see cref="SetInstances"/> in place (drag previews); ignores indices out of range.</summary>
    public unsafe void UpdateInstance(int index, in InstanceData instance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)InstanceCount)
        {
            return;
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, InstanceBuffer);
        fixed (InstanceData* p = &instance)
        {
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, (nint)(index * InstanceData.SizeInBytes), InstanceData.SizeInBytes, p);
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>
    /// Executes <paramref name="commandCount"/> consecutive <see cref="DrawElementsIndirectCommand"/>s starting at
    /// <paramref name="firstCommand"/> in the buffer bound to <see cref="BufferTargetARB.DrawIndirectBuffer"/> (their index
    /// ranges name a LOD section, their base instances index <see cref="InstanceBuffer"/>). Leaves <see cref="Vao"/> bound.
    /// </summary>
    public unsafe void DrawIndirect(int firstCommand, int commandCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (commandCount <= 0 || IndexCount == 0)
        {
            return;
        }

        _gl.BindVertexArray(Vao);
        _gl.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, (void*)(firstCommand * DrawElementsIndirectCommand.SizeInBytes), (uint)commandCount, 0);
    }

    /// <summary>Deletes the GL objects (textures are not owned).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteVertexArray(Vao);
        _gl.DeleteBuffer(VertexBuffer);
        _gl.DeleteBuffer(IndexBuffer);
        _gl.DeleteBuffer(InstanceBuffer);
    }
}

/// <summary>One record of <c>glMultiDrawElementsIndirect</c> (the <c>DrawElementsIndirectCommand</c> layout of GL 4.3).</summary>
/// <param name="Count">Indices to draw.</param>
/// <param name="InstanceCount">Instances to draw.</param>
/// <param name="FirstIndex">First index.</param>
/// <param name="BaseVertex">Value added to every index.</param>
/// <param name="BaseInstance">First instance record (instanced attributes start there).</param>
[StructLayout(LayoutKind.Sequential, Size = SizeInBytes)]
public readonly record struct DrawElementsIndirectCommand(uint Count, uint InstanceCount, uint FirstIndex, uint BaseVertex, uint BaseInstance)
{
    /// <summary>Size of one command in bytes.</summary>
    public const int SizeInBytes = 20;
}
