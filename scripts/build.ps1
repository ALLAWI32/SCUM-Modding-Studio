<#
.SYNOPSIS
  Builds, tests and packages ScumStudio from the command line (Windows PowerShell 5.1 or PowerShell 7).

.DESCRIPTION
  Use this when Visual Studio freezes or crashes while building: the command line build uses the same
  compiler without the IDE (designers, live analysis, test discovery) and is usually faster.

    scripts\build.ps1                          # Debug build of the whole solution
    scripts\build.ps1 -Configuration Release   # Release build
    scripts\build.ps1 -Test -Fixtures D:\scum-mods\SCUM_Mod_Build -MapSlice D:\scum-mods\map_slice\client
    scripts\build.ps1 -Publish                 # ready-to-run app + CLI in dist\ScumStudio (no .NET install needed)
    scripts\build.ps1 -Publish -Zip -Version 0.1.0   # ... plus dist\SCUM-Modding-Studio-0.1.0-win-x64.zip (what a release ships)
    scripts\build.ps1 -Clean -LowMemory        # fresh build, one project at a time (for PCs with little RAM)

  Output: artifacts\bin\<Project>\<debug|release>\  (and dist\ScumStudio\ with -Publish).
  The game's AES key is never needed to build or test.

.PARAMETER Configuration
  Debug (default) or Release.
.PARAMETER Test
  Run the test suite after building. Tests that need the game data are skipped unless -Fixtures / -MapSlice
  (or the SCUM_FIXTURES / SCUM_MAP_SLICE environment variables) point at the extracted archives.
.PARAMETER Publish
  Also publish self-contained single-file builds for Windows x64 into dist\ScumStudio.
.PARAMETER Zip
  With -Publish: also pack dist\ScumStudio (without debug symbols and XML docs) with README, CHANGELOG and the
  third-party notices into dist\SCUM-Modding-Studio-<version>-win-x64.zip.
.PARAMETER Version
  Version stamped into the published binaries and the zip name (default: the one in Directory.Build.props).
.PARAMETER Clean
  Delete artifacts\ (all build output) first.
.PARAMETER LowMemory
  Build one project at a time without the compiler server (slower, but uses far less memory).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $Test,
    [switch] $Publish,
    [switch] $Zip,
    [string] $Version,
    [switch] $Clean,
    [switch] $LowMemory,
    [string] $Fixtures,
    [string] $MapSlice
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step([string] $message) { Write-Host ''; Write-Host "==> $message" -ForegroundColor Cyan }
function Fail([string] $message) { Write-Host ''; Write-Host "FAILED: $message" -ForegroundColor Red; exit 1 }
function Invoke-Dotnet([string[]] $arguments, [string] $whatFailed) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { Fail $whatFailed }
}

# --- 1. Prerequisites -----------------------------------------------------------------------------
Step 'Checking the .NET SDK'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "'dotnet' was not found. Install the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8), then open a NEW terminal."
}
$sdks = @(& dotnet --list-sdks)
Write-Host ($sdks -join [Environment]::NewLine)
$supported = $sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 }
if (-not $supported) {
    Fail 'No .NET SDK 8 or newer is installed. Run: winget install Microsoft.DotNet.SDK.8'
}
if (-not ($sdks | Where-Object { $_ -match '^8\.' })) {
    Write-Warning 'No .NET 8 SDK found; building with a newer SDK (allowed by global.json). Recommended: winget install Microsoft.DotNet.SDK.8'
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# A crashed Visual Studio can leave MSBuild/compiler server processes that keep files in artifacts\ locked.
Step 'Stopping leftover build servers'
& dotnet build-server shutdown | Out-Null

if ($Clean) {
    Step 'Deleting artifacts\ (all previous build output)'
    if (Test-Path artifacts) { Remove-Item -Recurse -Force artifacts }
}

$buildFlags = @('-nologo', '-clp:Summary')
if ($LowMemory) {
    $buildFlags += @('-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false')
} else {
    $buildFlags += '-m'
}

# --- 2. Restore + build ---------------------------------------------------------------------------
Step 'Restoring NuGet packages'
Invoke-Dotnet (@('restore', 'ScumStudio.sln') + $buildFlags) 'NuGet restore failed. Check the internet connection (nuget.org) and try again.'

Step "Building ScumStudio.sln ($Configuration)"
Invoke-Dotnet (@('build', 'ScumStudio.sln', '-c', $Configuration, '--no-restore') + $buildFlags) "The build failed. Scroll up to the FIRST line that contains 'error' - that is the real cause."

# --- 3. Tests -------------------------------------------------------------------------------------
if ($Test) {
    if ($Fixtures) { $env:SCUM_FIXTURES = (Resolve-Path $Fixtures).Path }
    if ($MapSlice) { $env:SCUM_MAP_SLICE = (Resolve-Path $MapSlice).Path }
    $fixturesText = if ($env:SCUM_FIXTURES) { $env:SCUM_FIXTURES } else { '(not set - fixture tests are skipped)' }
    $sliceText = if ($env:SCUM_MAP_SLICE) { $env:SCUM_MAP_SLICE } else { '(not set - map tests are skipped)' }
    Step "Running tests`n    SCUM_FIXTURES  = $fixturesText`n    SCUM_MAP_SLICE = $sliceText"
    Invoke-Dotnet @('test', 'ScumStudio.sln', '-c', $Configuration, '--no-build', '-nologo') 'Some tests failed - the failing test names and messages are listed above.'
}

# --- 4. Publish -----------------------------------------------------------------------------------
if ($Publish) {
    $dist = Join-Path (Join-Path $root 'dist') 'ScumStudio'
    Step "Publishing self-contained single-file builds (win-x64) to $dist"
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    $publishFlags = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-o', $dist, '-nologo')
    if ($Version) { $publishFlags += "-p:Version=$Version" }
    Invoke-Dotnet (@('publish', 'src/ScumStudio.App/ScumStudio.App.csproj') + $publishFlags) 'Publishing the app failed.'
    Invoke-Dotnet (@('publish', 'src/ScumStudio.Cli/ScumStudio.Cli.csproj') + $publishFlags) 'Publishing the CLI failed.'
    Write-Host "Ready to run: $dist\ScumStudio.App.exe   (CLI: $dist\scumstudio.exe)" -ForegroundColor Green

    if ($Zip) {
        if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version }
        $staging = Join-Path (Join-Path $root 'dist') "SCUM-Modding-Studio-$Version-win-x64"
        $zipFile = "$staging.zip"
        Step "Packing $zipFile"
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
        Copy-Item $dist $staging -Recurse
        Get-ChildItem $staging -Recurse -Include *.pdb, *.xml | Remove-Item -Force
        Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $staging
        if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $staging }
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipFile
        Remove-Item $staging -Recurse -Force
        Write-Host "Release zip: $zipFile" -ForegroundColor Green
    }
}

Step 'Done'
$folder = $Configuration.ToLowerInvariant()
Write-Host "App:  artifacts\bin\ScumStudio.App\$folder\ScumStudio.App.exe" -ForegroundColor Green
Write-Host "CLI:  artifacts\bin\ScumStudio.Cli\$folder\scumstudio.exe" -ForegroundColor Green
