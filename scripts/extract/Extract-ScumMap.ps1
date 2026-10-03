<#
.SYNOPSIS
  Extracts SCUM map data (levels, terrain, textures, water, foliage, buildings/props) from the game's paks with repak,
  in named batches, by exact file list, or per map cell — resumable, with a manifest per run.

.DESCRIPTION
  Run on the owner's PC (PowerShell 5.1 or 7). Nothing is uploaded by this script. The AES key is read from the key
  file only; it is passed to repak/scumstudio in memory and never written anywhere (do not add -Verbose echoes of it).

  Batches (-Batch):
    Index            only build/refresh the pak listing cache (first step; lists the ~101 paks once, a few minutes)
    TerrainLook      landscape layer textures + landscape master material/RVT config + shared layer infos (small, ~600 files)
    Water            ocean/lake/river/water materials and meshes (~350 files)
    Levels           every level package of The_Island (all sublevels, landscape tiles, HLOD) WITHOUT _BuiltData (~4,600 files)
    LevelsBuiltData  the _BuiltData packages (baked lighting; optional, ~1,800 files)
    Foliage          trees, bushes, grass meshes/materials/textures (~4,400 files)
    Rocks            landscape rock meshes (~950 files)
    Roads            road meshes and decals (~600 files)
    Distant          Landscape/Distant_Models far-view proxies (~18,000 files; only after everything else)
    Cell             exact closure of one map cell (-Cell A_0): uses scumstudio.exe `level refs` on the paks to list every
                     file the cell's sublevels reference (meshes, Blueprints, materials, textures, built data), then extracts it
    Server           the level packages of The_Island from the SERVER paks (needed for server mod paks) -> <Out>\server
    PathList         extract the exact entry paths in -PathList (one per line, e.g. from `scumstudio level refs ... --files`)
    Folders          extract everything under the -Folders prefixes you pass (wildcards * allowed)

.EXAMPLE
  .\Extract-ScumMap.ps1 -Batch Index
  .\Extract-ScumMap.ps1 -Batch TerrainLook
  .\Extract-ScumMap.ps1 -Batch Cell -Cell B_2
  .\Extract-ScumMap.ps1 -Batch PathList -PathList .\lists\my_files.txt
  .\Extract-ScumMap.ps1 -Batch Folders -Folders "SCUM/Content/ConZ_Files/Models/Buildings/Prison/","SCUM/Content/ConZ_Files/Models/Objects/Indoor/*"

.NOTES
  Output layout: <Out>\client\SCUM\Content\...  and  <Out>\server\SCUM\Content\...  (the same layout the game paks use,
  which is what ScumStudio's loose-folder source and SCUM_MAP_SLICE expect). Manifests: <Out>\manifests\<batch>_<time>.txt.
  Exit code 0 = everything requested was extracted; 3 = some requested entries were not found in any pak (see manifest).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet('Index', 'TerrainLook', 'Water', 'Levels', 'LevelsBuiltData', 'Foliage', 'Rocks', 'Roads', 'Distant', 'Cell', 'Server', 'PathList', 'Folders')]
  [string]$Batch,

  [string]$Cell,
  [string]$PathList,
  [string[]]$Folders,

  [string]$Out        = "$env:USERPROFILE\Desktop\scum-mods\map_full",
  [string]$ClientPaks = "C:\Program Files (x86)\Steam\steamapps\common\SCUM\SCUM\Content\Paks",
  [string]$ServerPaks = "C:\SCUMServer\SCUM\Content\Paks",            # <-- folder next to SCUMServer.exe -> SCUM\Content\Paks
  [string]$KeyFile    = "$env:USERPROFILE\Desktop\scum-mods\scum_aes_key.txt",              # a text file holding the key (never commit it)
  [string]$Repak      = "$env:USERPROFILE\.cargo\bin\repak.exe",
  [string]$Scumstudio = "",                                             # scumstudio.exe (artifacts\bin\ScumStudio.Cli\debug\scumstudio.exe); auto-detected when empty
  [string]$ListCache  = "",                                             # default: <Out>\pak_lists
  [int]$BatchSize     = 200
)

$ErrorActionPreference = 'Stop'
$content = 'SCUM/Content/ConZ_Files/'

# ---------------------------------------------------------------- presets
$presets = @{
  TerrainLook = @{ Include = @(
      "$($content)Landscape/LandscapeTextures/", "$($content)Landscape/Textures/", "$($content)Landscape/VT_Landscape/",
      "$($content)Landscape/Procedural_Generation/", "$($content)Landscape/Sky/", "$($content)Landscape/Background/",
      "$($content)Landscape/Distant_Landscape/", "$($content)Maps/The_Island/*sharedassets/*"); Exclude = @(); Source = 'client' }
  Water       = @{ Include = @("$($content)Water/", "$($content)Landscape/Ocean/", "$($content)Landscape/Lake/", "$($content)Landscape/Rivers/"); Exclude = @(); Source = 'client' }
  Levels      = @{ Include = @("$($content)Maps/The_Island/"); Exclude = @("*_BuiltData.*"); Source = 'client' }
  LevelsBuiltData = @{ Include = @("$($content)Maps/The_Island/*_BuiltData.*"); Exclude = @(); Source = 'client' }
  Foliage     = @{ Include = @("$($content)Foliage/"); Exclude = @(); Source = 'client' }
  Rocks       = @{ Include = @("$($content)Landscape/Rocks/"); Exclude = @(); Source = 'client' }
  Roads       = @{ Include = @("$($content)Models/Road/", "$($content)Decals/"); Exclude = @(); Source = 'client' }
  Distant     = @{ Include = @("$($content)Landscape/Distant_Models/"); Exclude = @(); Source = 'client' }
  Server      = @{ Include = @("$($content)Maps/The_Island/"); Exclude = @("*_BuiltData.*"); Source = 'server' }
}

# ---------------------------------------------------------------- checks
if (-not (Test-Path $Repak))   { Write-Error "repak.exe not found at $Repak"; exit 1 }
if (-not (Test-Path $KeyFile)) { Write-Error "key file not found at $KeyFile"; exit 1 }
$key = ((Get-Content $KeyFile -Raw).Trim()) -replace '^0x', ''
if ($key.Length -ne 64) { Write-Error "the key file should hold 64 hex characters (optionally 0x-prefixed)"; exit 1 }
if ([string]::IsNullOrEmpty($ListCache)) { $ListCache = Join-Path $Out 'pak_lists' }
New-Item -ItemType Directory -Force -Path $Out, $ListCache, (Join-Path $Out 'manifests'), (Join-Path $Out 'lists') | Out-Null

function Get-PakListing([string]$paksDir, [string]$tag) {
  # Returns a hashtable: entry path -> pak file. Listings are cached per pak (delete <ListCache>\<tag>_<pak>.txt to refresh).
  if (-not (Test-Path $paksDir)) { Write-Warning "Paks folder not found: $paksDir"; return @{} }
  $map = @{}
  $paks = Get-ChildItem "$paksDir\*.pak" | Sort-Object Name
  $n = 0
  foreach ($pak in $paks) {
    $n++
    $cache = Join-Path $ListCache ("{0}_{1}.txt" -f $tag, $pak.BaseName)
    if (-not (Test-Path $cache) -or (Get-Item $cache).LastWriteTime -lt $pak.LastWriteTime) {
      Write-Progress -Activity "Listing $tag paks" -Status $pak.Name -PercentComplete (100 * $n / $paks.Count)
      $lines = & $Repak -a $key list $pak.FullName 2>$null
      if ($LASTEXITCODE -ne 0) { Write-Warning "repak could not list $($pak.Name) (encrypted with another key, or not a pak)"; $lines = @() }
      Set-Content -Path $cache -Value $lines -Encoding UTF8
    }
    foreach ($line in (Get-Content $cache)) {
      $p = $line.Trim()
      if ($p.Length -gt 0 -and -not $map.ContainsKey($p)) { $map[$p] = $pak.FullName }
    }
  }
  Write-Progress -Activity "Listing $tag paks" -Completed
  $index = Join-Path $Out ("pak_index_{0}.txt" -f $tag)
  $map.GetEnumerator() | Sort-Object Key | ForEach-Object { "{0}`t{1}" -f (Split-Path $_.Value -Leaf), $_.Key } | Set-Content -Path $index -Encoding UTF8
  Write-Host ("{0}: {1} paks, {2} entries (index: {3})" -f $tag, $paks.Count, $map.Count, $index)
  return $map
}

function Select-Entries($listing, [string[]]$include, [string[]]$exclude) {
  $wanted = New-Object System.Collections.Generic.List[string]
  foreach ($p in $listing.Keys) {
    $hit = $false
    foreach ($inc in $include) {
      if ($inc.Contains('*')) { if ($p -like $inc) { $hit = $true; break } }
      elseif ($p.StartsWith($inc, [System.StringComparison]::OrdinalIgnoreCase)) { $hit = $true; break }
    }
    if (-not $hit) { continue }
    foreach ($exc in $exclude) { if ($p -like $exc) { $hit = $false; break } }
    if ($hit) { $wanted.Add($p) }
  }
  return ,$wanted
}

function Find-Scumstudio {
  if ($Scumstudio -and (Test-Path $Scumstudio)) { return $Scumstudio }
  $here = Split-Path -Parent $PSCommandPath
  foreach ($candidate in @(
      (Join-Path $here '..\..\artifacts\bin\ScumStudio.Cli\debug\scumstudio.exe'),
      (Join-Path $here '..\..\artifacts\bin\ScumStudio.Cli\release\scumstudio.exe'))) {
    if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
  }
  $cmd = Get-Command scumstudio -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  return $null
}

function Extract-Entries($listing, [System.Collections.Generic.List[string]]$wanted, [string]$outDir, [string]$manifest) {
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  $byPak = @{}
  $skipped = 0
  $notFound = New-Object System.Collections.Generic.List[string]
  foreach ($p in $wanted) {
    $target = Join-Path $outDir ($p -replace '/', '\')
    if ((Test-Path $target) -and (Get-Item $target).Length -gt 0) { $skipped++; continue }   # resumable
    if (-not $listing.ContainsKey($p)) { $notFound.Add($p); continue }
    $pak = $listing[$p]
    if (-not $byPak.ContainsKey($pak)) { $byPak[$pak] = New-Object System.Collections.Generic.List[string] }
    $byPak[$pak].Add($p)
  }
  $done = 0
  $total = ($byPak.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
  if (-not $total) { $total = 0 }
  foreach ($pak in ($byPak.Keys | Sort-Object)) {
    $entries = $byPak[$pak]
    for ($i = 0; $i -lt $entries.Count; $i += $BatchSize) {
      $slice = $entries[$i..([Math]::Min($i + $BatchSize - 1, $entries.Count - 1))]
      $repakArgs = @('-a', $key, 'unpack', $pak, '-o', $outDir, '-f')
      foreach ($e in $slice) { $repakArgs += '-i'; $repakArgs += $e }
      & $Repak @repakArgs | Out-Null
      if ($LASTEXITCODE -ne 0) { Write-Warning ("repak unpack reported an error for {0} (batch at {1})" -f (Split-Path $pak -Leaf), $i) }
      $done += $slice.Count
      Write-Progress -Activity "Extracting to $outDir" -Status ("{0}/{1}  {2}" -f $done, $total, (Split-Path $pak -Leaf)) -PercentComplete ($(if ($total) { 100 * $done / $total } else { 100 }))
    }
  }
  Write-Progress -Activity "Extracting to $outDir" -Completed
  $present = 0; $bytes = 0
  foreach ($p in $wanted) {
    $target = Join-Path $outDir ($p -replace '/', '\')
    if (Test-Path $target) { $present++; $bytes += (Get-Item $target).Length }
  }
  @(
    "batch: $Batch", "when: $(Get-Date -Format u)", "out: $outDir",
    "requested: $($wanted.Count)", "already present (skipped): $skipped", "extracted now: $done",
    "present after run: $present", "not found in any pak: $($notFound.Count)", ("size: {0:N0} MB" -f ($bytes / 1MB)), "",
    "# not found:"
  ) + $notFound | Set-Content -Path $manifest -Encoding UTF8
  Write-Host ("=> {0}: requested {1}, extracted {2}, skipped {3} (already there), present {4}, not found {5}, {6:N0} MB. Manifest: {7}" -f `
      $Batch, $wanted.Count, $done, $skipped, $present, $notFound.Count, ($bytes / 1MB), $manifest)
  return $notFound.Count
}

# ---------------------------------------------------------------- main
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$manifest = Join-Path $Out ("manifests\{0}{1}_{2}.txt" -f $Batch, $(if ($Cell) { "_$Cell" } else { '' }), $stamp)

if ($Batch -eq 'Index') {
  Get-PakListing $ClientPaks 'client' | Out-Null
  if (Test-Path $ServerPaks) { Get-PakListing $ServerPaks 'server' | Out-Null }
  exit 0
}

$sourceTag = 'client'
if ($Batch -eq 'Server') { $sourceTag = 'server' }
$paksDir = $(if ($sourceTag -eq 'server') { $ServerPaks } else { $ClientPaks })
if (-not (Test-Path $paksDir)) { Write-Error "Paks folder not found: $paksDir"; exit 1 }
$listing = Get-PakListing $paksDir $sourceTag
$outDir = Join-Path $Out $sourceTag

switch ($Batch) {
  'Cell' {
    if (-not $Cell) { Write-Error "-Cell A_0 .. D_4 / Z_0 .. Z_4 is required with -Batch Cell"; exit 1 }
    $exe = Find-Scumstudio
    if (-not $exe) { Write-Error "scumstudio.exe not found: build ScumStudio.sln first or pass -Scumstudio <path>"; exit 1 }
    $listFile = Join-Path $Out ("lists\cell_{0}_files.txt" -f $Cell)
    $missingFile = Join-Path $Out ("lists\cell_{0}_missing.txt" -f $Cell)
    Write-Host "Computing the file closure of cell $Cell with scumstudio level refs (reads the paks directly; a few minutes) ..."
    $env:SCUMSTUDIO_AES_KEY = $key      # process-local; never printed by scumstudio
    try { & $exe level refs $paksDir --cell $Cell --files $listFile --missing $missingFile --top 15 }
    finally { Remove-Item Env:\SCUMSTUDIO_AES_KEY -ErrorAction SilentlyContinue }
    if (-not (Test-Path $listFile)) { Write-Error "scumstudio did not write $listFile"; exit 1 }
    $wanted = New-Object System.Collections.Generic.List[string]
    foreach ($l in (Get-Content $listFile)) { if ($l.Trim()) { $wanted.Add($l.Trim()) } }
    $missing = Extract-Entries $listing $wanted $outDir $manifest
    exit $(if ($missing -gt 0) { 3 } else { 0 })
  }
  'PathList' {
    if (-not $PathList -or -not (Test-Path $PathList)) { Write-Error "-PathList <file> is required and must exist"; exit 1 }
    $wanted = New-Object System.Collections.Generic.List[string]
    foreach ($l in (Get-Content $PathList)) { if ($l.Trim()) { $wanted.Add($l.Trim()) } }
    $missing = Extract-Entries $listing $wanted $outDir $manifest
    exit $(if ($missing -gt 0) { 3 } else { 0 })
  }
  'Folders' {
    if (-not $Folders) { Write-Error "-Folders <prefix>[,<prefix>] is required with -Batch Folders"; exit 1 }
    $wanted = Select-Entries $listing $Folders @()
    $missing = Extract-Entries $listing $wanted $outDir $manifest
    exit $(if ($missing -gt 0) { 3 } else { 0 })
  }
  default {
    $preset = $presets[$Batch]
    $wanted = Select-Entries $listing $preset.Include $preset.Exclude
    if ($wanted.Count -eq 0) { Write-Warning "No pak entries matched the $Batch preset — check the pak folder and the listing cache."; exit 3 }
    $missing = Extract-Entries $listing $wanted $outDir $manifest
    exit $(if ($missing -gt 0) { 3 } else { 0 })
  }
}
