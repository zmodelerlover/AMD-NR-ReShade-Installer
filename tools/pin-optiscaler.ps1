#Requires -Version 7
<#
.SYNOPSIS
  Pins one OptiScaler release in payload.json from its release archive: the archive itself, and every
  file the app takes out of it.

.DESCRIPTION
  The payload pins the zip of a neural-amd-opti release AND each file extracted from it (name, path in
  the cache, size, SHA-256), and the app refuses anything that does not match. This computes all of it
  from the zip -- which has to be the very bytes attached to the GitHub release, so download it back
  from there rather than pointing this at a local build -- and writes it into
  releases.optiscaler[<version>].components.optiscaler, creating that release when it is not there.

  What goes into the extract list is everything PACKAGE_RELEASE.ps1 puts in the zip except what only
  its own setup reads: Licenses\, README.md, Setup*, Uninstall* and SHA256SUMS.txt at the root. The
  Agility runtime, OptiScaler\D3D12_OptiScaler\D3D12Core.dll, is kept as D3D12_OptiScaler/D3D12Core.dll:
  a payload path may be two levels deep at most, and Work.OptiDestination puts it back where it goes.
  Anything else deeper than that is refused: no app could read a manifest that pins it.

  Two checks before anything is written:
    - the zip's own SHA256SUMS.txt, when it has one, agrees with every hash computed here;
    - every file lands under a name the installers already out there (v0.5.0 and v0.5.1) accept.
      They read releases too and will offer this version, and a name they do not know fails their
      install with "Refusing to install an unmanaged filename". -AllowNewNames overrides that, for
      a release that really needs a new file -- and then only this app's newer builds can install it.

  A release carrying LmxxfNrRuntime.dll gets the lmxxf weights component of the newest release that
  has one, when it has none of its own. Nothing else in the release (the mochizuki components) is
  touched: tools/pin-mochizuki.ps1 writes those.

.EXAMPLE
  gh release download v0.4.0-amd-nr --repo MatheusFerreiraS/neural-amd-opti --pattern 'OptiScaler-*.zip' --dir $env:TEMP\opti
  .\tools\pin-optiscaler.ps1 -Zip $env:TEMP\opti\OptiScaler-0.4.0-amd-nr.zip -Published 2026-09-25
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Zip,
    [string] $Version = '',
    [string] $Published = (Get-Date -Format 'yyyy-MM-dd'),
    [string] $Url = '',
    [string] $Manifest = '',
    [switch] $AllowNewNames
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Manifest) { $Manifest = Join-Path $root 'payload\payload.json' }
function Fail($m) { Write-Host "FAIL  $m" -ForegroundColor Red; exit 1 }
function Good($m) { Write-Host "OK    $m" -ForegroundColor Green }
function Note($m) { Write-Host "      $m" -ForegroundColor DarkGray }

# Hashing through .NET rather than Get-FileHash: the cmdlet does not load when PSModulePath points
# at the other PowerShell's modules, which is how check-release.ps1 once failed.
function Sha256Stream($stream) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose() }
}
function Sha256File($path) {
    $s = [IO.File]::OpenRead($path)
    try { return Sha256Stream $s } finally { $s.Dispose() }
}

if (-not (Test-Path -LiteralPath $Zip)) { Fail "no archive at $Zip" }
$Zip = (Resolve-Path -LiteralPath $Zip).Path
$zipName = Split-Path -Leaf $Zip
if (-not $Version) {
    if ($zipName -notmatch '^OptiScaler-(.+)\.zip$') { Fail "cannot read a version out of $zipName; pass -Version" }
    $Version = $Matches[1]
}
if ($zipName -ne "OptiScaler-$Version.zip") { Fail "$zipName is not OptiScaler-$Version.zip, the name the release publishes" }
if ($Version -notmatch '^\d+\.\d+\.\d+') { Fail "unusable version '$Version'" }
if ($Published -notmatch '^\d{4}-\d{2}-\d{2}$') { Fail "-Published wants yyyy-MM-dd, got '$Published'" }
if (-not $Url) { $Url = "https://github.com/MatheusFerreiraS/neural-amd-opti/releases/download/v$Version/$zipName" }

# What the installers already published accept inside a game folder, besides the proxy name
# OptiScaler.dll goes in under: Engine.Allowed as of v0.5.1, and its three rules.
$knownNames = @(
    'OptiScaler.ini', 'LmxxfNrRuntime.dll',
    'OptiScaler/amd_fidelityfx_loader_dx12.dll', 'OptiScaler/amd_fidelityfx_upscaler_dx12.dll',
    'OptiScaler/amd_fidelityfx_framegeneration_dx12.dll', 'OptiScaler/amd_fidelityfx_denoiser_dx12.dll',
    'OptiScaler/amd_fidelityfx_vk.dll', 'OptiScaler/libxess.dll', 'OptiScaler/libxess_dx11.dll',
    'OptiScaler/libxess_fg.dll', 'OptiScaler/libxell.dll', 'OptiScaler/D3D12_OptiScaler/D3D12Core.dll',
    'experimental_lighting/GatherCS.cso', 'experimental_lighting/ResolveCS.cso'
)
function KnownToOlderApps($destination) {
    if ($destination -eq 'OptiScaler.dll') { return $true }
    if ($knownNames -ccontains $destination) { return $true }
    return $destination -cmatch '^(lmxxf-modules|native-game-tiled-assets)/[^/]+$' -or
           $destination -cmatch '^shaders/native_[^/]*\.hlsl$'
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$invalid = [IO.Path]::GetInvalidFileNameChars()
$extract = [System.Collections.Generic.List[object]]::new()
$sums = @{}
$unknown = @()
$archive = [IO.Compression.ZipFile]::OpenRead($Zip)
try {
    $sumsEntry = $archive.GetEntry('SHA256SUMS.txt')
    if ($sumsEntry) {
        $reader = [IO.StreamReader]::new($sumsEntry.Open())
        try {
            foreach ($line in ($reader.ReadToEnd() -split "`r?`n")) {
                if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') { $sums[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant() }
            }
        } finally { $reader.Dispose() }
    }

    foreach ($entry in $archive.Entries) {
        $full = $entry.FullName.Replace('\', '/')
        if ($full.EndsWith('/')) { continue }                        # a folder
        if ($full -like 'Licenses/*') { continue }
        if ($full -notmatch '/' -and ($full -eq 'README.md' -or $full -like 'Setup*' -or $full -like 'Uninstall*' -or $full -eq 'SHA256SUMS.txt')) { continue }

        $path = if ($full -eq 'OptiScaler/D3D12_OptiScaler/D3D12Core.dll') { 'D3D12_OptiScaler/D3D12Core.dll' } else { $full }
        $segments = $path.Split('/')
        if ($segments.Count -gt 2) { Fail "$full is deeper than a payload path may go; add a mapping to Work.OptiDestination and here first" }
        foreach ($s in $segments) {
            if ($s.Length -eq 0 -or $s.Length -gt 96 -or $s -in '.', '..' -or $s.IndexOfAny($invalid) -ge 0) { Fail "unusable name in the archive: $full" }
        }
        if (-not (KnownToOlderApps $full)) { $unknown += $full }

        $stream = $entry.Open()
        try { $sha = Sha256Stream $stream } finally { $stream.Dispose() }
        if ($sums.ContainsKey($full) -and $sums[$full] -ne $sha) { Fail "$full hashes to $sha, and the archive's SHA256SUMS.txt says $($sums[$full])" }
        $extract.Add([ordered]@{ name = $segments[-1]; path = $path; size = $entry.Length; sha256 = $sha })
    }
} finally { $archive.Dispose() }

if (-not ($extract | Where-Object { $_.path -eq 'OptiScaler.dll' })) { Fail "$zipName has no OptiScaler.dll at its root" }
if ($sums.Count -gt 0) { Good "the archive's own SHA256SUMS.txt agrees with every file it lists" }
else { Note "the archive has no SHA256SUMS.txt to compare against" }
if ($unknown.Count -gt 0) {
    Write-Host "      names the installers already published (v0.5.0, v0.5.1) refuse to install:" -ForegroundColor Yellow
    $unknown | ForEach-Object { Write-Host "        $_" -ForegroundColor Yellow }
    if (-not $AllowNewNames) { Fail "they would offer $Version and fail installing it. Rename, leave them out of the package, or pass -AllowNewNames knowingly" }
}
$extract = @($extract | Sort-Object { $_.path } -CaseSensitive)

$zipSha = Sha256File $Zip
$zipSize = (Get-Item -LiteralPath $Zip).Length
$component = [ordered]@{
    version   = $Version
    published = $Published
    files     = @([ordered]@{ name = $zipName; size = $zipSize; sha256 = $zipSha; url = $Url })
    extract   = $extract
}

$m = [IO.File]::ReadAllText($Manifest) | ConvertFrom-Json
if (-not $m.PSObject.Properties['releases']) { $m | Add-Member -NotePropertyName releases -NotePropertyValue ([pscustomobject]@{}) }
if (-not $m.releases.PSObject.Properties['optiscaler']) { $m.releases | Add-Member -NotePropertyName optiscaler -NotePropertyValue @() }
$releases = @($m.releases.optiscaler)
$release = $releases | Where-Object { $_.version -eq $Version } | Select-Object -First 1
if (-not $release) {
    $release = [pscustomobject]@{ version = $Version; components = [pscustomobject]@{} }
    $releases = @($release) + $releases
    Note "release $Version created under releases.optiscaler"
}
$old = $release.components.PSObject.Properties['optiscaler']
if ($old -and ($old.Value.files | Where-Object { $_.sha256 -ne ('0' * 64) -and $_.sha256 -ne $zipSha })) {
    Write-Host "      $Version was pinned to another archive before. If that one was ever published, this is a" -ForegroundColor Yellow
    Write-Host "      replaced asset under a published tag: publish a new version instead." -ForegroundColor Yellow
}
$release.components | Add-Member -NotePropertyName optiscaler -NotePropertyValue $component -Force

if (($extract | Where-Object { $_.path -eq 'LmxxfNrRuntime.dll' }) -and -not $release.components.PSObject.Properties['lmxxf-weights']) {
    $donor = $releases | Where-Object { $_.components.PSObject.Properties['lmxxf-weights'] } | Select-Object -First 1
    if (-not $donor) { Fail "$Version carries the lmxxf runtime and no release has its weights to lend" }
    $release.components | Add-Member -NotePropertyName 'lmxxf-weights' -NotePropertyValue $donor.components.'lmxxf-weights'
    Note "lmxxf weights taken from $($donor.version)"
}

# Newest first, the order the app offers them in.
$m.releases.optiscaler = @($releases | Sort-Object { [version](($_.version -split '-')[0]) } -Descending)
[IO.File]::WriteAllText($Manifest, ($m | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))

Good "$Manifest"
Note "optiscaler $Version`: $zipName, $zipSize bytes, sha256 $zipSha"
Note "$($extract.Count) files pinned inside it"
$left = foreach ($c in $release.components.PSObject.Properties) {
    if (@($c.Value.files) + @($c.Value.extract) | Where-Object { $_ -and $_.sha256 -eq ('0' * 64) }) { $c.Name }
}
if ($left) { Write-Host "      still placeholders in $Version`: $($left -join ', ') -- the app hides this version until they are pinned" -ForegroundColor Yellow }
else { Good "nothing in $Version is a placeholder any more" }
