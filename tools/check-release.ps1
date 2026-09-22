<#
    Run this before publishing, and after, against whatever is live.

    v0.3.0 went out with <Version>0.2.0</Version> still in the csproj. The auto updater compares
    the newest GitHub release tag against the running assembly's own version, so an exe that
    under-reports itself is offered the same update for ever: download, replace, restart, still
    older than the tag, offer again. Nobody sees a crash, the version in the corner just never
    changes, and every existing install re-downloads 49 MB on every launch.

    Three things have to agree and nothing in the build knew that:

      1. src/AmdNr.App/AmdNr.App.csproj   <Version>
      2. the published exe's own FileVersion
      3. the GitHub release tag it is attached to

    This checks all three, and checks the asset on the release is byte for byte the exe that was
    built rather than a stale upload left behind by a re-publish.

    .EXAMPLE
      ./tools/check-release.ps1
      ./tools/check-release.ps1 -Exe publish-v0.3.0/AMD-NR-ReShade-Installer.exe
#>
param(
    [string]$Exe = '',
    [string]$Repo = 'zmodelerlover/AMD-NR-ReShade-Installer'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fail = $false
function Bad([string]$m) { Write-Host "  FAIL  $m" -ForegroundColor Red; $script:fail = $true }
function Ok([string]$m)  { Write-Host "  ok    $m" -ForegroundColor Green }

# 1. what the project says it is
$csproj = Join-Path $root 'src/AmdNr.App/AmdNr.App.csproj'
$declared = ([regex]'<Version>([^<]+)</Version>').Match((Get-Content $csproj -Raw)).Groups[1].Value
if (-not $declared) { Bad 'the csproj declares no <Version>; the exe would report 1.0.0'; exit 1 }
Write-Host "csproj <Version>: $declared"

# 2. what the built exe says it is
if (-not $Exe) { $Exe = Join-Path $root "publish-v$declared/AMD-NR-ReShade-Installer.exe" }
if (Test-Path $Exe) {
    $built = [version](Get-Item $Exe).VersionInfo.FileVersion
    if ($built.ToString(3) -eq $declared) { Ok "the built exe reports $declared" }
    else { Bad "the built exe reports $($built.ToString(3)), the csproj says $declared" }
} else {
    Write-Host "  skip  no exe at $Exe; run dotnet publish first" -ForegroundColor Yellow
}

# 3. what the newest release is tagged, and what is actually attached to it
$latest = gh api "repos/$Repo/releases/latest" --jq '.tag_name' 2>$null
if (-not $latest) {
    Write-Host '  skip  could not reach GitHub' -ForegroundColor Yellow
} else {
    $tag = $latest.TrimStart('v')
    if ($tag -eq $declared) { Ok "the newest release is tagged v$tag, which matches" }
    else { Bad "the newest release is tagged v$tag but the csproj says $declared. The updater would offer that release for ever, because the exe it hands out still calls itself $declared." }

    if (Test-Path $Exe) {
        $want = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLower()
        # `gh release download`, not `gh api ... > file`: in Windows PowerShell the `>` operator
        # is a TEXT redirect, so it re-encodes the bytes on the way out and every binary compared
        # that way looks corrupted. This check reported a stale asset that was not stale.
        $dir = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        try {
            gh release download $latest --repo $Repo --pattern 'AMD-NR-ReShade-Installer.exe' --dir $dir 2>$null
            $got = (Get-FileHash (Join-Path $dir 'AMD-NR-ReShade-Installer.exe') -Algorithm SHA256).Hash.ToLower()
            if ($got -eq $want) { Ok 'the asset on the release is the exe that was built' }
            else { Bad 'the asset on the release is NOT the exe that was built; a stale upload is live' }
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($fail) { Write-Host "`nDo not publish." -ForegroundColor Red; exit 1 }
Write-Host "`nVersions agree." -ForegroundColor Green
