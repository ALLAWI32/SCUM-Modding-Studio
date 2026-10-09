# ScumStudio.Rendering

Silk.NET OpenGL 4.3 core renderer for the viewport, asset thumbnails and `scumstudio render ...`. It does not depend on
Avalonia: hosts hand it an `IGlContext`. Tests and the CLI use a hidden-window context (`OffscreenGlContext`), which also
runs under `xvfb-run` with Mesa llvmpipe on Linux.

| Type | Purpose |
| --- | --- |
| `Context.IGlContext` | `GL Gl`, `(int W, int H) Size`, `MakeCurrent()`. |
| `Context.OffscreenGlContext` | Hidden GLFW window (Silk.NET.Windowing), GL 4.3 core. `Create` throws `GlContextUnavailableException`; `TryCreate` returns the reason. |
| `Context.ProcAddressGlContext` | Wraps a context owned by someone else, from a `GetProcAddress` function (Avalonia `GlInterface.GetProcAddress`). |
| `SceneRenderer` | Owns programs and uploaded meshes. `AddMesh(MeshData, MeshSpace)` returns a `MeshHandle`. `Render(target, scene, camera)` does instanced draws, frustum culling, grid and selection highlight. `Pick(target, scene, camera, x, y)` returns a `PickResult` (mesh id, `SelectableId`, node, depth, world position). |
| `Resources.GpuMesh` / `GpuTexture` / `PreparedMesh` | VAO with interleaved position/normal/uv, a uint32 index buffer and a per-instance buffer (`InstanceData`: model matrix, tint, pick code, flags; attribute locations 3..8, divisor 1). Textures are RGBA8, `SRGB8_ALPHA8` for colour data, with mipmaps, or the game's BC1/2/3/5/7 blocks uploaded as they are (`GpuTexture.FromCompressed`). A section may have a normal map (`GpuSection.NormalMap`, UE/DirectX style, tangent frame from screen derivatives) and a roughness range. |
| `Targets.RenderTarget` | Resizable colour FBO (RGBA8 + 32-bit float depth) and ID FBO (R32UI + depth). `ReadColorRgba()` returns rows top first. `ReadPick(x, y)` reads one pick pixel. `BlitTo(fbo, w, h)` presents the image into a host framebuffer. |
| `Cameras.FlyCamera` / `Frustum` | Fly camera: position, yaw/pitch (UE conventions), fov, near/far. `Rotate(dx, dy)` takes mouse deltas and `Update(FlyInput, dt)` takes WASD key state. Also `Orbit`, `Frame(bounds)`, `ScreenRay`, `Unproject`, and GL or reverse-Z projections. `Frustum` extracts planes and does AABB tests. |
| `SceneGraph.Scene` / `SceneNode` / `SceneBatcher` | Retained graph: each node has a mesh handle, local transform, `SelectableId`, `Visible`, `Selected`, `Tint` and children. `SceneBatcher` flattens a scene into one batch per mesh, culls against the frustum and assigns pick codes. |
| `Imaging.ImageExport` | Saves RGBA8 to PNG and loads images through ImageSharp. |
| `Snapshots.MeshSnapshot` | Renders one mesh to an image (used by the CLI and for thumbnails). |
| `Procedural.PrimitiveMeshes` / `CubeGridScene` | Cube, plane, checkerboard and the instanced cube-grid test scene. |
| `Import.ObjReader` | Wavefront OBJ to `MeshData`. This is the fallback input that needs no game files. |

## Conventions

* **World.** The render world is right-handed and Y-up (`Core.Mathematics.UeToGl`). With `MeshSpace.Unreal` (the default
  for `MeshExtractor` output), the upload swaps Y and Z and keeps the index order. It also keeps centimetres unless
  `unitScale` says otherwise. Place instances with `UeToGl.ModelMatrix(transform)`.
* **Matrices.** Matrices are `System.Numerics.Matrix4x4` in the row-vector convention (`world = local * parentWorld`,
  `clip = world * view * proj`). They are uploaded untransposed, which is the column-vector matrix GLSL expects.
* **Depth.** When the driver has `glClipControl` (GL 4.5 / ARB_clip_control), the renderer uses reverse-Z: a float depth
  buffer cleared to 0, `GL_GREATER`, near = 1 and far = 0. This keeps precision across a 15 km map in centimetres.
  Frustum culling always uses the plain GL projection. The renderer resets clip control to `NEGATIVE_ONE_TO_ONE` after
  each pass.
* **Colour.** Shading is done in linear space. Albedo textures are sampled with hardware sRGB decode. The shaders encode
  sRGB themselves into a plain RGBA8 target, so read-back pixels and blits are display-ready whatever the driver does
  with sRGB reads.
* **Picking.** Picking renders only the requested pixel (scissor) into the R32UI buffer. The pick code is a 1-based
  running index over that pass's batches. Nodes with `SelectableId == 0` write 0: they occlude but cannot be picked.
* **Threads.** Silk.NET resolves GL entry points lazily from the current context. Every GL call, including `Dispose`
  of targets, meshes and the renderer, must run on the thread where the context is current. Do not `await` between GL
  calls: read the pixels back, dispose, then write files asynchronously.

## Hosting in Avalonia (`OpenGlControlBase`)

```csharp
protected override void OnOpenGlInit(GlInterface gl)
{
    _context = new ProcAddressGlContext(gl.GetProcAddress, () => (_pixelWidth, _pixelHeight));
    _renderer = new SceneRenderer(_context);
    _target = _renderer.CreateTarget();
}

protected override void OnOpenGlRender(GlInterface gl, int fb)
{
    _target.Resize(_pixelWidth, _pixelHeight);
    _renderer.Render(_target, _scene, _camera);
    _target.BlitTo((uint)fb, _pixelWidth, _pixelHeight);
}

protected override void OnOpenGlDeinit(GlInterface gl)
{
    _target.Dispose();
    _renderer.Dispose();
}
```

On Windows, request WGL (`Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Wgl] }`) so the control gets a
desktop GL 4.x context rather than ANGLE/GLES.

## Native GLFW

`OffscreenGlContext` preloads `runtimes/<rid>/native/{glfw3.dll | libglfw.so.3 | libglfw.3.dylib}` from the
Ultz.Native.GLFW package. Silk.NET's own probing does not look in those folders for RID-less builds, and without the
preload `Window.Create` fails with "Couldn't find a suitable window platform".

## Tests

`tests/ScumStudio.Tests/Rendering`: GL tests use `[GlFact]`. The first test probes for a GL 4.3 context, and the GL
tests skip when there is none: no display, a Windows runner with only the GDI 1.1 driver, or `SCUMSTUDIO_SKIP_GL=1`.
On Linux, run them with:

```
xvfb-run -a -s "-screen 0 1280x800x24" dotnet test --filter "FullyQualifiedName~ScumStudio.Tests.Rendering"
```
