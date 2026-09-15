<#
    Fills the app's cache from archives you already have, instead of downloading them.

    This is how the app is tested before the content repository exists, and it is also what an
    offline install would use. Every file is checked against payload.json on the way in, by the same
    SHA-256 the app would have checked after a download -- so a cache seeded this way is
    indistinguishable from one that was downloaded, and a wrong archive is refused here rather than
    at install time.

    Example:
      .\tools\seed-cache.ps1 `
          -RuntimeZip  "$env:USERPROFILE\Desktop\dlss5-runtime-v0.2.17.zip" `
          -ExtrasArchive "$env:USERPROFILE\Desktop\x86_Extras.7z" `
          -ReleaseZip  "$env:TEMP\dlss5-neural-amd-v0.5.0.zip"
#>
[CmdletBinding()]
param(
    # dlss5-runtime-v0.2.17.zip: dlssnr_amd_pass1.dll + dlssnr_on_amd_weights.bin
    [string]$RuntimeZip,
    # x86_Extras.7z: the pinned 32-bit ReShade (dxgi.dll) + d3d8to9.dll
    [string]$ExtrasArchive,
    # dlss5-neural-amd-v0.5.0.zip from the add-on's release: the add-on and the bridge pair
    [string]$ReleaseZip,
    [string]$Manifest,
    # $Home is a read-only automatic variable in PowerShell, hence the name.
    [string]$AppHome
)

$ErrorActionPreference = 'Stop'
if (-not $Manifest) { $Manifest = Join-Path (Split-Path -Parent $PSCommandPath) '..\payload\payload.json' }
if (-not $AppHome)  { $AppHome  = if ($env:AMDNR_HOME) { $env:AMDNR_HOME } else { Join-Path $env:APPDATA 'AmdNrInstaller' } }
$payload = Get-Content -Raw $Manifest | ConvertFrom-Json
$cache = Join-Path $AppHome 'cache'
$staging = Join-Path $env:TEMP ("amdnr-seed-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

function Expand-Archive7z {
    param([string]$Archive, [string]$Into)
    if ($Archive.ToLower().EndsWith('.7z')) {
        $exe = @(
            "$env:ProgramFiles\7-Zip\7z.exe",
            "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $exe) { throw "7-Zip is needed to read $Archive but 7z.exe was not found." }
        & $exe x -y -o"$Into" "$Archive" | Out-Null
    }
    else {
        Expand-Archive -Path $Archive -DestinationPath $Into -Force
    }
}

# Unpack whatever was given into one pile; the manifest decides what is actually wanted.
foreach ($archive in @($RuntimeZip, $ExtrasArchive, $ReleaseZip)) {
    if ($archive -and (Test-Path $archive)) {
        Write-Host "unpacking $(Split-Path -Leaf $archive)"
        Expand-Archive7z -Archive $archive -Into $staging
    }
}

$seeded = 0
$missing = @()

foreach ($name in $payload.components.PSObject.Properties.Name) {
    $component = $payload.components.$name
    $target = Join-Path $cache (Join-Path $name $component.version)

    foreach ($file in $component.files) {
        $relative = if ($file.path) { $file.path } else { $file.name }
        $destination = Join-Path $target ($relative -replace '/', '\')

        if ((Test-Path $destination) -and (Get-Item $destination).Length -eq $file.size) {
            $have = (Get-FileHash $destination -Algorithm SHA256).Hash.ToLower()
            if ($have -eq $file.sha256) { continue }   # already there and already right
        }

        # The archives have their own layouts, so match on the file name wherever it landed.
        $source = Get-ChildItem -Path $staging -Recurse -File -Filter $file.name -ErrorAction SilentlyContinue |
                  Where-Object { $_.Length -eq $file.size } | Select-Object -First 1
        if (-not $source) { $missing += "$name/$($file.name)"; continue }

        $have = (Get-FileHash $source.FullName -Algorithm SHA256).Hash.ToLower()
        if ($have -ne $file.sha256) {
            $missing += "$name/$($file.name) (SHA-256 mismatch: got $have)"
            continue
        }

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item $source.FullName $destination -Force
        Write-Host ("  ok  {0}/{1}" -f $name, $relative)
        $seeded++
    }
}

Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "$seeded file(s) placed in $cache"
if ($missing.Count -gt 0) {
    Write-Host "not seeded:" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    Write-Host "The app downloads whatever is missing once the content repository is up." -ForegroundColor Yellow
}
