# ScumStudio.Assets

Read-only facade over [CUE4Parse](https://github.com/FabianFG/CUE4Parse) 1.2.2 (+ CUE4Parse-Conversion 1.2.1) for SCUM's
cooked UE 4.27 content. The renderer, the editors and the `scumstudio asset ...` commands use it.

| Type | Purpose |
| --- | --- |
| `Catalog.AssetCatalog` | Opens a Paks folder, a single `.pak`, or loose extracted files; loads packages/objects by `/Game/...` path; lists exports; class queries; LRU package cache. |
| `Catalog.ScumFileProvider` | CUE4Parse provider that mounts loose folders as `SCUM/Content/...` (whatever the folder is called) and aliases the stock `SCUM/SCUM/Content` pak quirk. |
| `Catalog.PackageIndex` | Sorted package list + folder tree, with the main export's class read from package headers only. |
| `Meshes.MeshExtractor` | `UStaticMesh` / `USkeletalMesh` LOD to `Core.Geometry.MeshData`; per-LOD counts, bounds and material slots without converting vertices. |
| `Textures.TextureDecoder` / `PngWriter` | `UTexture2D` mip to RGBA8 (BC1/2/3/4/5/7 via managed AssetRipper decoders, BGRA8/RGBA8/G8/A8 directly, others via CUE4Parse-Conversion); PNG output via ImageSharp. |
| `Materials.MaterialInspector` | `UMaterialInstanceConstant` scalar/vector/texture parameters merged along the parent chain; base-colour texture and tint guesses. |
| `Export.GltfExporter` / `ObjExporter` | Minimal glTF 2.0 (`.gltf` + `.bin`) and Wavefront OBJ (+ `.mtl`) writers for `MeshData`. |

## Paths

Every API accepts object paths (`/Game/A/B.B`), package paths (`/Game/A/B`) and provider file paths
(`SCUM/Content/A/B.uasset`). A package path loads the export named like the package, or the first export when the asset
has another name (e.g. `SK_WolfsWagen_Dashboard` holds `SK_WolfsWagen_Dashboard_V1`). Returned paths are normalised to `/Game/...`.

## Axis conventions

* `MeshData` stays in **Unreal space**: centimetres, X forward, Y right, Z up (left-handed), exactly as cooked. Stock meshes
  have `dot(cross(v1 - v0, v2 - v0), normal) < 0` for their front faces (clockwise in this frame); nothing is reordered.
* UVs are as stored (origin top-left), the same as glTF. Normals are unpacked and re-normalised. Skeletal meshes are in bind pose.
* The exporters (default options) write `(x, z, y) * 0.01`: metres, right-handed, Y up, the same convention as UE Viewer (umodel)
  exports. That swap is a reflection, so the triangle order is kept and front faces come out counter-clockwise as glTF requires.
  With `ConvertToYUp = false` the UE axes are kept and each triangle is reversed instead. OBJ writes `1 - v` for V.

## Limitations

* CUE4Parse 1.2.2 inflates zlib-compressed pak entries through a native zlib-ng library that is not shipped. Stock SCUM paks and
  the mod paks built so far are stored uncompressed; for zlib paks call `CUE4Parse.Compression.ZlibHelper.Initialize(...)` first.
* Textures: BC6H/ASTC/ETC go through CUE4Parse-Conversion, whose BC6H path needs the native Detex library (Windows only).
* Base materials (`UMaterial`) are only resolved when their package is in the catalog (e.g. `M_Car_01` lives in the stock paks).

## Tests

`tests/ScumStudio.Tests/Assets/`: unit tests always run; tests over real files use `SCUM_FIXTURES` (see `docs/BUILDING.md`)
and are skipped when it is not set.
