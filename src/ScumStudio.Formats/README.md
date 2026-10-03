# ScumStudio.Formats

In-house reader/writer for SCUM's cooked Unreal Engine 4.27 packages, a C# port of the Python toolchain
(`tools/ue4pkg.py`, `props.py`, `ue4write.py`, `assetreg.py`, and `tag_offsets` from `clone_vehicle.py`).
It needs no CUE4Parse or UAssetAPI calls.

| Type | Port of | Purpose |
|------|---------|---------|
| `Packages.CookedPackage` | `ue4pkg.Pkg` | Parses the `.uasset` summary, names (with both stored hashes), 28-byte imports and 104-byte exports. Loads `.uexp` and `.ubulk`. Provides `ResolveName`, `ResolveIndex` (`None` / `IMP:x` / `EXP:x`), `GetExportData`, `ReadPreloadDependencies`, `ReadAssetRegistryData` and `Describe()`. |
| `Packages.PackageWriter` | `ue4write.build_package` | Rebuilds a complete package from its tables and export payloads and recomputes every offset and size. |
| `Packages.NameHashes` | `strihash_deprecated`, `strcrc32`, `name_hashes` | Computes the two 16-bit hashes stored with each name. |
| `Packages.RoundTripVerifier` | `ue4write.roundtrip_check` | Reads a package, rebuilds it and compares the result byte by byte, reporting the first differing offset. |
| `Packages.PackagePatcher` | `clone_vehicle.tag_offsets` + same-size patches | Byte search for tags, plus writing a float, int, bool, FName, vector or object index at a recorded offset. |
| `Properties.PropertyReader` / `PropertyBlock` | `props.read_tagged` / `decode_value` / `fmt` | Parses FPropertyTag blocks into a typed value model. Every value records its export-relative offset. The block can be dumped as JSON or as indented text. |
| `AssetRegistry.AssetRegistryFile` | `assetreg.Registry` | Reads and writes `AssetRegistry.bin` (v8 FixedTags). You can list assets and tags, add or clone records, remove records, and save the file byte-identical. |
| `AssetRegistry.CityHash` | the `cityhash` module | CityHash64 v1.1.1, used for the name batch hashes. |

## Findings beyond the Python toolchain (verified on the fixture archive)

- **Non-case-preserving name hash.** Unreal hashes ANSI names with the `ANSICHAR` overload of
  `Strihash_DEPRECATED`, which folds one byte per character. `ue4write.py` folds two bytes per character, so
  the uassets it rebuilt differed in that hash (FORMAT_NOTES: "identical except possibly the 16-bit
  non-case-preserving name hash"). With the ANSI rule, all 506 stock packages under `orig/SCUM/Content`
  rebuild byte-exact.
  - Packages the Python toolchain wrote (`build_sep*`) carry its hash. `NameHashes.ComputePythonToolchain`
    recognises that hash.
  - `RoundTripVerifier` reports these packages as "identical with the stored name hashes".
- **CityHash64 version.** The registry uses CityHash64 **v1.1.1**. It differs from v1.1 for inputs longer
  than 64 bytes: in the 64-byte loop, the seed is `z + w.second` rather than `z + y`.
- **Transform properties are tagged.** A `Transform` property is a tagged struct (Translation, Rotation and
  Scale3D tags), not 10 raw floats (the known caveat in `props.py`). The native layout is kept only as a
  fallback.
- **Extra native structs.** Besides the props.py list, these structs are decoded natively: RichCurveKey,
  SimpleCurveKey, KeyHandleMap (0 bytes when cooked), SkeletalMeshSamplingLODBuiltData and
  SkeletalMeshSamplingRegionBuiltData, NavAgentSelector, PerQualityLevelInt, Box, Box2D, IntVector, Plane,
  SoftClassPath, DateTime, Timespan, FrameNumber and GameplayTagContainer. With these, every export of the stock
  packages and of all `build_sep*` trees decodes with no raw values.

## Offsets

`PropertyTag.ValueOffset`, `PropertyTag.Offset` and `PropertyValue.Offset` are relative to the export payload,
like `start` in props.py.

- Use `PropertyBlock.ToUExpOffset(x)` to get the `.uexp` file offset. The pitfall from FORMAT_NOTES still
  applies: `tag_offsets` returns uexp offsets, and a payload offset is that value minus
  `SerialOffset - TotalHeaderSize`.
- The value of a `BoolProperty` lives in the tag, at `BoolValueOffset`.
- `SizeFieldOffset` points at the tag's int32 size field. Fix it up after you insert into a struct.
