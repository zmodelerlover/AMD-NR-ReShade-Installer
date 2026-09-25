#Requires -Version 7
<#
.SYNOPSIS
  Pins the mochizuki runtime of one OptiScaler release in payload.json from a folder of its final
  files, and builds the archive that gets uploaded.

.DESCRIPTION
  -From is laid out the way a game folder receives it:

      MochizukiNrRuntime.dll
      dlssnr-amd\dlssnr.bin                  the model
      dlssnr-amd\prewarm\manifest.txt        the prewarm list, made for these shaders
      dlssnr-amd\shaders\...                 files, and one level of folders under it

  Anything else at the top of -From (OptiScaler.dll, notes, a build's .lib, .exp and obj\) is ignored.

  -License is DLSSNR-AMD's MIT licence (third_party\mochizuki\LICENSE in neural-amd-opti). The
  archive ships code built from DLSSNR-AMD, so it carries its notice, as
  dlssnr-amd\LICENSE-DLSSNR-AMD.txt: beside the shaders and never among them, because the prewarm
  list is checked against every file in shaders\. What this does:

    1. refuses what the runtime writes while a game runs (pipeline.cache, *.tmp, *.log) and what the
       app could not install (anything deeper, a folder name with a dot, a name over 96 characters);
    2. checks the prewarm list was made for these shaders: the file count on its "shaders" line;
    3. writes <Out>\mochizuki-<version>.zip -- the runtime, the prewarm list, the licence and the
       shaders under the payload paths the app keeps them at (dlssnr-amd.shaders.runtime/x.spv for
       dlssnr-amd\shaders\runtime\x.spv, see Work.MochizukiDestination), in ordinal order and with a
       fixed timestamp, so the same files always make the same archive on any machine;
    4. rewrites releases.optiscaler[<version>].components.mochizuki (the archive and every file in it)
       and mochizuki-model (dlssnr.bin, loose) in payload.json;
    5. prints the upload command for each file it pinned, at the path its url names.

  The model has an address of its own per model version, because published addresses are
  overwritable and versions that coexist need names that coexist. The same bytes keep the version
  already pinned; other bytes need -ModelVersion.

.EXAMPLE
  .\tools\pin-mochizuki.ps1 -From <release build>\candidate\runtime -License <neural-amd-opti>\third_party\mochizuki\LICENSE
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $From,
    [string] $License = '',
    [string] $Version = '0.4.0-amd-nr',
    [string] $ModelVersion = '',
    [string] $Out = '',
    [string] $Manifest = '',
    [string] $Repo = 'zmodelerlover/amd-nr'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Manifest) { $Manifest = Join-Path $root 'payload\payload.json' }
if (-not $Out) { $Out = Join-Path $root "publish-mochizuki-$Version" }   # publish-*/ is ignored by git
$base = "https://huggingface.co/datasets/$Repo/resolve/main"
$zero = '0' * 64
function Fail($m) { Write-Host "FAIL  $m" -ForegroundColor Red; exit 1 }
function Good($m) { Write-Host "OK    $m" -ForegroundColor Green }
function Note($m) { Write-Host "      $m" -ForegroundColor DarkGray }
function Sha256File($path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $s = [IO.File]::OpenRead($path)
    try { return [Convert]::ToHexString($sha.ComputeHash($s)).ToLowerInvariant() } finally { $s.Dispose(); $sha.Dispose() }
}

if (-not (Test-Path -LiteralPath $From -PathType Container)) { Fail "$From is not a folder" }
$From = (Resolve-Path -LiteralPath $From).Path
$runtime = Join-Path $From 'MochizukiNrRuntime.dll'
$data = Join-Path $From 'dlssnr-amd'
$model = Join-Path $data 'dlssnr.bin'
$prewarm = Join-Path $data 'prewarm\manifest.txt'
$shaders = Join-Path $data 'shaders'
foreach ($need in $runtime, $model, $prewarm) { if (-not (Test-Path -LiteralPath $need -PathType Leaf)) { Fail "$need is missing" } }
if (-not (Test-Path -LiteralPath $shaders -PathType Container)) { Fail "$shaders is missing" }
$ignored = Get-ChildItem -LiteralPath $From -Force | Where-Object { $_.Name -notin 'MochizukiNrRuntime.dll', 'dlssnr-amd' }
if ($ignored) { Note "ignored at the top of -From: $(($ignored | ForEach-Object Name) -join ', ')" }

# DLSSNR-AMD is MIT: whoever passes its code on passes its notice on with it.
if (-not $License) {
    Fail "pass -License with DLSSNR-AMD's LICENSE (third_party\mochizuki\LICENSE in neural-amd-opti): the archive ships code built from it and must carry its notice"
}
if (-not (Test-Path -LiteralPath $License -PathType Leaf)) { Fail "$License is missing" }
$License = (Resolve-Path -LiteralPath $License).Path
$notice = [IO.File]::ReadAllText($License)
if ($notice -notmatch 'MIT License' -or $notice -notmatch 'mochizuki0323' -or $notice -notmatch 'Permission is hereby granted') {
    Fail "$License is not DLSSNR-AMD's MIT licence: it lacks 'MIT License', 'mochizuki0323' or the permission notice"
}

# --- 1. only what ships, only where the app can put it ------------------------------------------
$invalid = [IO.Path]::GetInvalidFileNameChars()
function Plain($name, $what) {
    if ($name.Length -eq 0 -or $name.Length -gt 96 -or $name -in '.', '..' -or $name.IndexOfAny($invalid) -ge 0) { Fail "unusable $what name: '$name'" }
}
function Written($name) { $name -eq 'pipeline.cache' -or $name -like '*.tmp' -or $name -like '*.log' }

$top = Get-ChildItem -LiteralPath $data -Force
foreach ($item in $top) {
    $ok = ($item.PSIsContainer -and $item.Name -in 'prewarm', 'shaders') -or (-not $item.PSIsContainer -and $item.Name -eq 'dlssnr.bin')
    if (-not $ok) { Fail "dlssnr-amd\$($item.Name) is not part of a release$(if (Written $item.Name) { ': the runtime wrote it while a game ran' })" }
}
$extra = Get-ChildItem -LiteralPath (Join-Path $data 'prewarm') -Force | Where-Object { $_.Name -ne 'manifest.txt' }
if ($extra) { Fail "dlssnr-amd\prewarm holds more than manifest.txt: $(($extra | ForEach-Object Name) -join ', ')" }

$files = [System.Collections.Generic.List[object]]::new()   # payload path -> file on disk
$files.Add([pscustomobject]@{ Path = 'MochizukiNrRuntime.dll'; File = $runtime })
$files.Add([pscustomobject]@{ Path = 'dlssnr-amd.prewarm/manifest.txt'; File = $prewarm })
$files.Add([pscustomobject]@{ Path = 'dlssnr-amd/LICENSE-DLSSNR-AMD.txt'; File = $License })
$shaderCount = 0
foreach ($item in Get-ChildItem -LiteralPath $shaders -Force) {
    if (-not $item.PSIsContainer) {
        if (Written $item.Name) { Fail "dlssnr-amd\shaders\$($item.Name) was written by the runtime, it does not ship" }
        Plain $item.Name 'file'
        $files.Add([pscustomobject]@{ Path = "dlssnr-amd.shaders/$($item.Name)"; File = $item.FullName })
        $shaderCount++
        continue
    }
    Plain $item.Name 'folder'
    if ($item.Name.Contains('.')) { Fail "dlssnr-amd\shaders\$($item.Name): a folder name with a dot cannot be spelled in a payload path" }
    foreach ($inner in Get-ChildItem -LiteralPath $item.FullName -Force) {
        if ($inner.PSIsContainer) { Fail "dlssnr-amd\shaders\$($item.Name)\$($inner.Name) is deeper than the app installs" }
        if (Written $inner.Name) { Fail "dlssnr-amd\shaders\$($item.Name)\$($inner.Name) was written by the runtime, it does not ship" }
        Plain $inner.Name 'file'
        $files.Add([pscustomobject]@{ Path = "dlssnr-amd.shaders.$($item.Name)/$($inner.Name)"; File = $inner.FullName })
        $shaderCount++
    }
}
foreach ($f in $files) { if ($f.Path.Length -gt 128) { Fail "payload path over 128 characters: $($f.Path)" } }
# Ordinal, not Sort-Object's culture order: the archive's bytes must not depend on the machine's language.
$files.Sort([Comparison[object]] { param($a, $b) [string]::CompareOrdinal($a.Path, $b.Path) })
$files = @($files)

# --- 2. the prewarm list is for these shaders ----------------------------------------------------
$head = (Get-Content -LiteralPath $prewarm -TotalCount 4) -join "`n"
if ($head -notmatch '(?m)^mochizuki-prewarm \d+') { Fail "$prewarm is not a mochizuki prewarm list" }
if ($head -notmatch '(?m)^shaders [0-9a-f]+ (\d+)$') { Fail "$prewarm has no shaders line" }
if ([int]$Matches[1] -ne $shaderCount) {
    Fail "the prewarm list was made for $($Matches[1]) shader files and dlssnr-amd\shaders has $shaderCount. The runtime would ignore it; make it again from these shaders"
}
Good "prewarm list made for these $shaderCount shader files"

# --- 3. the archive --------------------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$zipName = "mochizuki-$Version.zip"
$zipPath = Join-Path $Out $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$stamp = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$extract = [System.Collections.Generic.List[object]]::new()
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        $entry = $zip.CreateEntry($f.Path, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $stamp
        $sink = $entry.Open()
        $source = [IO.File]::OpenRead($f.File)
        try { $source.CopyTo($sink) } finally { $source.Dispose(); $sink.Dispose() }
        $extract.Add([ordered]@{ name = [IO.Path]::GetFileName($f.Path); path = $f.Path; size = (Get-Item -LiteralPath $f.File).Length; sha256 = Sha256File $f.File })
    }
} finally { $zip.Dispose(); $stream.Dispose() }
$zipSha = Sha256File $zipPath
$zipSize = (Get-Item -LiteralPath $zipPath).Length
Good "$zipPath  ($($files.Count) files, $zipSize bytes)"

# --- 4. payload.json -------------------------------------------------------------------------------
$m = [IO.File]::ReadAllText($Manifest) | ConvertFrom-Json
$releases = @($m.releases.optiscaler)
$release = $releases | Where-Object { $_.version -eq $Version } | Select-Object -First 1
if (-not $release) { Fail "releases.optiscaler has no $Version. Run tools/pin-optiscaler.ps1 with its archive first" }

$modelSha = Sha256File $model
$modelSize = (Get-Item -LiteralPath $model).Length
$pinned = $releases | ForEach-Object { $_.components.PSObject.Properties['mochizuki-model'] } | Where-Object { $_ } |
    ForEach-Object { $_.Value } | Select-Object -First 1
if ($ModelVersion) {
    if ($pinned -and $pinned.version -eq $ModelVersion -and $pinned.files[0].sha256 -ne $modelSha) {
        Fail "model version $ModelVersion is already pinned to other bytes; a published address never gets new bytes"
    }
    $modelComponent = [ordered]@{ version = $ModelVersion; files = @([ordered]@{
        name = 'dlssnr.bin'; path = 'dlssnr-amd/dlssnr.bin'; size = $modelSize; sha256 = $modelSha
        url = "$base/mochizuki/model-$ModelVersion/dlssnr.bin" }) }
} elseif ($pinned -and $pinned.files[0].sha256 -eq $modelSha) {
    $modelComponent = $pinned
    Note "dlssnr.bin is the model already pinned as version $($pinned.version)"
} elseif ($pinned) {
    Fail "dlssnr.bin is not the model pinned as version $($pinned.version). Pass -ModelVersion with a new version for it"
} else {
    $modelComponent = [ordered]@{ version = '1'; files = @([ordered]@{
        name = 'dlssnr.bin'; path = 'dlssnr-amd/dlssnr.bin'; size = $modelSize; sha256 = $modelSha
        url = "$base/mochizuki/model-1/dlssnr.bin" }) }
}

$old = $release.components.PSObject.Properties['mochizuki']
if ($old -and ($old.Value.files | Where-Object { $_.sha256 -ne $zero -and $_.sha256 -ne $zipSha })) {
    Write-Host "      mochizuki $Version was pinned to another archive before. If that one was ever uploaded and" -ForegroundColor Yellow
    Write-Host "      its payload.json published, the same address now serves other bytes: publish a new version instead." -ForegroundColor Yellow
}
$component = [ordered]@{
    version = $Version
    files   = @([ordered]@{ name = $zipName; size = $zipSize; sha256 = $zipSha; url = "$base/mochizuki/$Version/$zipName" })
    extract = $extract.ToArray()
}
$release.components | Add-Member -NotePropertyName mochizuki -NotePropertyValue $component -Force
$release.components | Add-Member -NotePropertyName 'mochizuki-model' -NotePropertyValue $modelComponent -Force
[IO.File]::WriteAllText($Manifest, ($m | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
Good "$Manifest"
Note "mochizuki $Version`: $zipName $zipSha"
Note "mochizuki-model $($modelComponent.version): dlssnr.bin $modelSize bytes $modelSha"

$sums = @("$zipSha *$zipName", "$modelSha *dlssnr.bin")
[IO.File]::WriteAllLines((Join-Path $Out 'SHA256SUMS.txt'), $sums)

# --- 5. where each goes ----------------------------------------------------------------------------
$modelInRepo = $modelComponent.files[0].url.Substring($base.Length + 1)
Write-Host "`nUpload, before payload.json:" -ForegroundColor Cyan
Write-Host "  hf upload $Repo `"$zipPath`" mochizuki/$Version/$zipName --repo-type dataset --commit-message `"mochizuki $Version`: runtime, shaders, prewarm list and licence`""
Write-Host "  hf upload $Repo `"$model`" $modelInRepo --repo-type dataset --commit-message `"mochizuki model $($modelComponent.version)`""
$left = foreach ($c in $release.components.PSObject.Properties) {
    if (@($c.Value.files) + @($c.Value.extract) | Where-Object { $_ -and $_.sha256 -eq $zero }) { $c.Name }
}
if ($left) { Write-Host "`n      still placeholders in $Version`: $($left -join ', ') -- the app hides this version until they are pinned" -ForegroundColor Yellow }
else { Good "nothing in $Version is a placeholder any more" }
