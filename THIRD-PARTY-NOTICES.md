# Third-party notices

ScumStudio links against the following open-source packages (versions pinned in
`Directory.Packages.props`). Each remains under its own licence; no source code from these projects is copied
into this repository.

| Package | Purpose | Licence |
|---|---|---|
| [CUE4Parse](https://github.com/FabianFG/CUE4Parse) 1.2.2, CUE4Parse-Conversion 1.2.1 | reading Unreal Engine pak files and cooked assets (meshes, textures, levels) | Apache-2.0 |
| [UAssetAPI](https://github.com/atenfyr/UAssetAPI) 1.1.0 | reading/writing cooked `.uasset`/`.umap` packages | MIT |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) 11.3.22 (+ Desktop, Themes.Fluent, Controls.ColorPicker, Fonts.Inter, Diagnostics, Headless) | cross-platform desktop UI | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.3.2 | MVVM helpers | MIT |
| [Silk.NET](https://github.com/dotnet/Silk.NET) 2.23.0 (OpenGL, Core, Maths, Windowing) | OpenGL bindings and offscreen window contexts | MIT |
| [System.CommandLine](https://github.com/dotnet/command-line-api) 2.0.0-beta4 | command-line parsing | MIT |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) | PNG encoding of decoded textures and render snapshots | Six Labors Split License (Apache-2.0 terms apply to open-source / small-revenue use; see the project's licence page) |
| Microsoft.Extensions.Logging.Abstractions, System.Security.Cryptography.ProtectedData | logging facade, Windows DPAPI | MIT |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | tests only | Apache-2.0 / MIT |

Transitive dependencies of CUE4Parse (Newtonsoft.Json, K4os LZ4, ZstdSharp, Oodle.NET, Zlib-ng.NET, BouncyCastle,
Serilog, Blake3, LZMA-SDK) keep their respective licences (MIT / Apache-2.0 / BSD).

Ideas (not code) were taken from FModel/Snooper (GPL-3.0) and CodeWalker for the editor UX; no GPL code is included.

## Fonts (bundled)

These font files ship inside the app (`src/ScumStudio.App/Assets/Fonts/`, embedded as Avalonia resources). They are the
official, unmodified TTFs, downloaded from the projects' GitHub repositories (via the jsDelivr mirror), and are
licensed under the [SIL Open Font License 1.1](src/ScumStudio.App/Assets/Fonts/OFL.txt). They must stay unmodified
(no subsetting, renaming or format conversion): "Plex" is a Reserved Font Name.

| Font | Files | Source | Licence |
|---|---|---|---|
| IBM Plex Sans, IBM Plex Sans Condensed, IBM Plex Mono, IBM Plex Sans Arabic (Regular, Medium, SemiBold) | `IBMPlex*.ttf` (12 files) | [IBM/plex](https://github.com/IBM/plex) `packages/plex-*/fonts/complete/ttf/` — Copyright © 2017 IBM Corp. with Reserved Font Name "Plex" | OFL-1.1 (`OFL.txt`) |
| Saira Stencil One | `SairaStencilOne-Regular.ttf` | [google/fonts](https://github.com/google/fonts) `ofl/sairastencilone/` — Copyright 2019 The Saira Stencil Project Authors | OFL-1.1 (`OFL-SairaStencilOne.txt`) |

SHA-256 of the bundled files:

```
98fbd727aae340b236955879dabed4d991aac9e8e90b3b2a67ce4a59221cc97c  IBMPlexMono-Medium.ttf
7c6fbddca4b700be918f5f6183d9bd4464fa427fe435f0b480d77fe2bb8c5a43  IBMPlexMono-Regular.ttf
f04d7c488ddf7d1fa99f2574efc3406ea4cbe17bb1af3a1ab960f84d0c96a172  IBMPlexMono-SemiBold.ttf
331c8639d7598b2cde62a911a71db195e30cb655cd6bdf2e324a7e984955f907  IBMPlexSans-Medium.ttf
975dcda37d80f038dcd143c22e33ca2d97a0cc5a929aace1c749153b0fe1afa5  IBMPlexSans-Regular.ttf
a20caf8286023a6a7a85e40b1d2a4ae9fc3e3b1f9eda8f4c542dd4986af67bb1  IBMPlexSans-SemiBold.ttf
eef162792cf2a6ba5af7943af9f86843b1286ab67aaf615c18c07fd8e97a4a90  IBMPlexSansArabic-Medium.ttf
8e0f1046c736bf939d4939ee3ae0116acf61cbcd6592deae7656761627080981  IBMPlexSansArabic-Regular.ttf
1d4ab8b6ebadbb9d85f2ecdcb7cb0bb672420a8ff3388ab4f69d7b98056562aa  IBMPlexSansArabic-SemiBold.ttf
0c6b79ef4a7de76ffc6bf2091980e5f81a1a87ac3967566bc98aae0baa518f54  IBMPlexSansCondensed-Medium.ttf
87aaee6ba9d2f80b8a3c1f6669e8c3d478b28b482f1d804917b3894694ccc12b  IBMPlexSansCondensed-Regular.ttf
acc2b1e3b9597ee13b08b767f845bd038fe120cbee806859af7c01b60fe6e9e6  IBMPlexSansCondensed-SemiBold.ttf
781496fdaf8e04cf6741b31025f6b4ba84f66021b097a8e0d85cbea2180cf223  SairaStencilOne-Regular.ttf
```

SCUM and its assets are the property of Gamepires / Jagex. This tool reads the user's own installed game files and
never redistributes them; the game's pak AES key is never stored in this repository.
