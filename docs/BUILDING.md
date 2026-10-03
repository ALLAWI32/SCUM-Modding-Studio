# Building ScumStudio

ScumStudio is a normal .NET 8 solution (`ScumStudio.sln`). It builds unchanged in Visual Studio 2022
Community on Windows and with the .NET 8 SDK on Linux/macOS.

## Requirements (Windows)

| What | Version |
|---|---|
| Visual Studio 2022 **Community** (or Professional/Enterprise) | **17.8 or newer** (Help > About). 17.11+ recommended. |
| Workload | **.NET desktop development** (Visual Studio Installer > Modify > Workloads) |
| .NET SDK | **8.0.100 or newer 8.0.x** (installed by the workload; check with `dotnet --list-sdks`) |
| GPU | OpenGL 4.3+ driver for the 3D viewport (later stages) |

`global.json` asks for SDK `8.0.100` with `rollForward: latestMajor`: an 8.0.x SDK is used when installed, otherwise a newer
one (9/10) builds the .NET 8 projects. `RollForward=Major` (Directory.Build.props) lets the app, CLI and tests run on a newer
.NET runtime when .NET 8 is missing. The .NET 8 SDK stays the recommended setup.

## Build from the command line (recommended when Visual Studio freezes)

`scripts\build.ps1` (PowerShell 5.1 or 7) checks the SDK, stops leftover build servers, restores, builds and prints a clear
FAILED line with the reason. Options: `-Configuration Release`, `-Test [-Fixtures <dir>] [-MapSlice <dir>]`, `-Publish`
(self-contained single-file `dist\ScumStudio\ScumStudio.App.exe` + `scumstudio.exe`), `-Clean`, `-LowMemory`.
Run it with `powershell -ExecutionPolicy Bypass -File scripts\build.ps1`. If Visual Studio hangs or crashes while
building, close it, run `scripts\build.ps1 -Clean -LowMemory` and open the solution again.

## Open, build and run in Visual Studio 2022

1. Clone the repository (`git clone <repo-url>`), or File > Clone Repository in Visual Studio.
2. Open `ScumStudio.sln` (File > Open > Project/Solution).
3. Build > **Rebuild Solution**. NuGet packages restore automatically; expect **0 errors**.
4. `ScumStudio.App` is the first project in the solution and the default startup project; press **F5**
   to start the editor. (If VS chose another project: right-click `ScumStudio.App` > Set as Startup Project.)
5. The command line tool is `ScumStudio.Cli` (executable `scumstudio.exe`). To debug it with arguments:
   right-click `ScumStudio.Cli` > Properties > Debug > Open debug launch profiles UI > Command line arguments,
   e.g. `version`.

Build output does **not** go to `bin/` and `obj/` inside each project. The solution uses the .NET 8
"artifacts output" layout, so everything lands in one folder at the repository root:

```
artifacts/
  bin/ScumStudio.App/debug/ScumStudio.App.exe
  bin/ScumStudio.Cli/debug/scumstudio.exe
  bin/<Project>/release/...        (Release configuration)
  obj/...                          (intermediate files)
  publish/...                      (dotnet publish output)
```

`artifacts/` is git-ignored. Deleting it is the equivalent of a clean.

### Do not update the pinned NuGet packages

All package versions are pinned in `Directory.Packages.props` (Central Package Management). Do **not** use
"Update all" in the NuGet package manager:

- `CUE4Parse` must stay **1.2.2** and `CUE4Parse-Conversion` **1.2.1**. Every newer build
  (`1.2.2.2026xx`) targets .NET 10 only and fails with `NU1202 ... is not compatible with net8.0`.
- `Avalonia*` must stay on **11.3.22**. Avalonia 12 needs Roslyn 4.14 (Visual Studio 17.14+) and fails with
  `CS9057` / "InitializeComponent does not exist" on older compilers.

Known warning: `MSB3246` about the native `CUE4Parse-Natives.dll` that ships inside the CUE4Parse package. It is
harmless. (`SixLabors.ImageSharp` is pinned to 3.1.12, which has no open security advisories.)

## Tests

- Visual Studio: Test > Test Explorer > Run All. All tests pass; tests that need the SCUM fixture archive show
  as **Skipped**.
- Command line: `dotnet test ScumStudio.sln` (from the repository root).

### Running the fixture tests (optional)

Some tests read real cooked SCUM files from an extracted copy of the modding archive
(`SCUM_Mod_Build`: the folder that contains `orig/`, `orig_extra/`, `build_sep*/` and the mod `*.pak` files).
The archive is never committed. Point the tests at it with the environment variable **`SCUM_FIXTURES`**:

Windows (PowerShell, current session only):

```powershell
$env:SCUM_FIXTURES = "D:\Modding\SCUM_Mod_Build"
dotnet test ScumStudio.sln
```

Windows (permanent, then restart Visual Studio so Test Explorer sees it):

```powershell
setx SCUM_FIXTURES "D:\Modding\SCUM_Mod_Build"
```

Alternatively, create a `.runsettings` file (not committed) and select it in Test Explorer
(Settings > Configure Run Settings > Select Solution Wide runsettings File):

```xml
<RunSettings>
  <RunConfiguration>
    <EnvironmentVariables>
      <SCUM_FIXTURES>D:\Modding\SCUM_Mod_Build</SCUM_FIXTURES>
    </EnvironmentVariables>
  </RunConfiguration>
</RunSettings>
```

Linux/macOS:

```bash
SCUM_FIXTURES=/path/to/SCUM_Mod_Build scripts/test.sh
```

When the variable is unset or points to a missing folder, fixture tests are skipped automatically.
Test code gets paths through `tests/ScumStudio.Tests/Fixtures/FixturePaths.cs` and marks such tests with
`[FixturesFact]` / `[FixturesTheory]`.

## The AES key

The stock SCUM paks are encrypted. ScumStudio asks for the key at runtime and stores it protected with
Windows DPAPI (current user) under `%LOCALAPPDATA%`. On Linux development machines it falls back to a
user-only (0600) file and logs a warning. The key is never stored in the repository, test files, docs or logs.

## Command line (Windows or Linux)

```bash
dotnet build ScumStudio.sln                 # Debug build of everything
dotnet build ScumStudio.sln -c Release      # Release build
dotnet test  ScumStudio.sln                 # all tests
dotnet run --project src/ScumStudio.Cli -- version
dotnet run --project src/ScumStudio.App
```

Self-contained single-file Windows build of the editor (works from Linux too):

```bash
dotnet publish src/ScumStudio.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Linux development

Wrappers in `scripts/` run from the repository root and prefer the Microsoft SDK in `/opt/dotnet-ms`
(with `DOTNET_ROOT` set), falling back to `dotnet` on `PATH` (e.g. Ubuntu's `dotnet-sdk-8.0` package).
Extra arguments are passed through:

```bash
scripts/build.sh                      # dotnet build ScumStudio.sln
scripts/build.sh -c Release
scripts/test.sh --filter "FullyQualifiedName~Core"
DOTNET=dotnet scripts/build.sh        # force the SDK on PATH
```

Redirecting build output (e.g. to keep several checkouts or parallel builds apart):

```bash
scripts/build.sh -p:ArtifactsPath=/tmp/my-artifacts
scripts/test.sh  -p:ArtifactsPath=/tmp/my-artifacts
# dotnet run ignores -p for its launch step; use the environment variable instead:
ArtifactsPath=/tmp/my-artifacts dotnet run --project src/ScumStudio.Cli -- version
```

Running the Avalonia app without a display (CI, containers): install `xvfb` and use

```bash
xvfb-run -a -s "-screen 0 1280x800x24" dotnet run --project src/ScumStudio.App
```

Mesa's llvmpipe provides OpenGL 4.5 core under Xvfb for offscreen rendering tests.

## Continuous integration

`.github/workflows/build.yml` restores, builds (Release) and tests the solution on `windows-latest` and
`ubuntu-latest` with the .NET 8 SDK. `SCUM_FIXTURES` is not set there, so fixture tests are skipped.
