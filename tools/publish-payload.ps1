<#
.SYNOPSIS
  Publishes a payload set to a Hugging Face dataset repository and rewrites payload.json to match.

.DESCRIPTION
  Hugging Face is used because its addresses are stable AND overwritable: a file keeps the URL
  https://huggingface.co/datasets/<repo>/resolve/main/<name> across every upload. That is the whole
  point -- swapping a payload becomes "upload, edit the manifest", with no new build of the app,
  which is what an immutable host (catbox) could not give.

  What this does, in order:
    1. uploads every changed file to the dataset repo
    2. recomputes size and SHA-256 from the local bytes
    3. bumps the version of any component whose content changed, so the cache refetches
    4. rewrites payload/payload.json with the stable addresses
    5. uploads payload.json and api-db.json to the same repo
    6. points src/AmdNr.App/config.json at those two addresses
    7. downloads every published file back and checks it against its own hash

  Step 7 is the one that matters. Nothing is believed because it was uploaded.

.PARAMETER Repo
  The dataset repository, as <user>/<name>. Created if it does not exist.

.PARAMETER From
  A folder holding replacement payloads, by their real filenames. Only the files present there are
  uploaded; every other entry keeps what it already has. Omit to republish the manifest alone.

.PARAMETER SetVersion
  Explicit versions, as component=version (repeatable). Without it, a component whose content
  changed has the last number of its version incremented.

.PARAMETER Verify
  Check what is already published against the manifest and do nothing else.

.EXAMPLE
  .\tools\publish-payload.ps1 -Repo claudinhh/amd-nr -From C:\new-runtime
  .\tools\publish-payload.ps1 -Repo claudinhh/amd-nr -From C:\new -SetVersion runtime=0.2.18
  .\tools\publish-payload.ps1 -Repo claudinhh/amd-nr -Verify
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Repo,
    [string] $From,
    [string[]] $SetVersion = @(),
    [switch] $Verify
)

$ErrorActionPreference = 'Stop'
# hf writes progress and notes like "Removing 1 file(s) from commit that have not changed" to
# stderr, and under ErrorActionPreference = Stop a redirected stderr line becomes a terminating
# error in Windows PowerShell. That killed a publish between uploading the manifest and verifying
# it, which is the worst place to stop: everything was live and nothing had been checked. Every
# call goes through Hf below, which relaxes the preference for the call and leaves the verdict
# where it belongs -- the exit code, which every caller already tests.
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'payload\payload.json'
$apiDbPath    = Join-Path $root 'payload\api-db.json'
$configPath   = Join-Path $root 'src\AmdNr.App\config.json'
$base         = "https://huggingface.co/datasets/$Repo/resolve/main"

# Runs hf and reports its output through Note, without a line on stderr being mistaken for a
# failure. $LASTEXITCODE survives the function, so callers test it exactly as before.
# Named for the verb, not for the tool: a function called Hf shadows the hf executable itself --
# PowerShell does not care about the case -- and the call inside it then re-enters the function.
function Invoke-Hf {
    param([Parameter(ValueFromRemainingArguments = $true)] [object[]] $Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & hf.exe @Arguments 2>&1 | ForEach-Object { Note "$_" } }
    finally { $ErrorActionPreference = $previous }
}

function Fail($m) { Write-Host "FAIL  $m" -ForegroundColor Red; exit 1 }
function Note($m) { Write-Host "      $m" -ForegroundColor DarkGray }
function Step($m) { Write-Host "`n$m" -ForegroundColor Cyan }
function Good($m) { Write-Host "OK    $m" -ForegroundColor Green }

function Sha256($path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

# The last number in a version, incremented: 0.2.17 -> 0.2.18, and 1 -> 2.
function BumpVersion($version) {
    if ($version -match '^(.*?)(\d+)$') { return $Matches[1] + ([int]$Matches[2] + 1) }
    return "$version-2"
}

if (-not (Test-Path $manifestPath)) { Fail "no payload.json at $manifestPath" }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

# Every component in the manifest: the ones under components, and the ones each version under
# releases carries (OptiScaler 0.2.0 and the lmxxf weights live there, out of v0.4.0's sight).
# The objects are the manifest's own, so what the loops below change is what gets written back.
function Get-AllComponents {
    foreach ($n in $manifest.components.PSObject.Properties.Name) {
        [pscustomobject]@{ Name = $n; Component = $manifest.components.$n; Release = $null }
    }
    if ($manifest.PSObject.Properties['releases']) {
        foreach ($key in $manifest.releases.PSObject.Properties.Name) {
            foreach ($release in @($manifest.releases.$key)) {
                foreach ($n in $release.components.PSObject.Properties.Name) {
                    [pscustomobject]@{ Name = $n; Component = $release.components.$n; Release = "$key $($release.version)" }
                }
            }
        }
    }
}

# --- Verify-only ------------------------------------------------------------------------------
if ($Verify) {
    Step "Checking every published file against the manifest"
    $bad = 0
    foreach ($entry in Get-AllComponents) {
        $name = $entry.Name
        foreach ($f in $entry.Component.files) {
            $tmp = New-TemporaryFile
            try {
                Invoke-WebRequest -Uri $f.url -OutFile $tmp -MaximumRedirection 5 | Out-Null
                $got = Sha256 $tmp
                $size = (Get-Item $tmp).Length
                if ($got -eq $f.sha256 -and $size -eq $f.size) { Good "$name/$($f.name)  ($size bytes)" }
                else {
                    Write-Host "FAIL  $name/$($f.name)" -ForegroundColor Red
                    Note "want $($f.sha256)  $($f.size) bytes"
                    Note "got  $got  $size bytes"
                    $bad++
                }
            } catch {
                Write-Host "FAIL  $name/$($f.name): $($_.Exception.Message)" -ForegroundColor Red
                $bad++
            } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        }
    }
    if ($bad -gt 0) { Fail "$bad file(s) do not match the manifest" }
    Good "every published file matches"
    exit 0
}

# --- Preconditions ----------------------------------------------------------------------------
# A pin of 64 zeros stands for bytes that do not exist yet (a release being prepared: see
# tools/pin-optiscaler.ps1 and tools/pin-mochizuki.ps1). This app hides such a release, but the
# installers already out there do not know the rule: published, it would be offered to all of them
# and fail every install.
Step "Checking for placeholder pins"
$placeholders = foreach ($entry in Get-AllComponents) {
    foreach ($f in @($entry.Component.files) + @($entry.Component.extract)) {
        if ($f -and $f.sha256 -eq ('0' * 64)) { "$($entry.Name)$(if ($entry.Release) { " in $($entry.Release)" })" }
    }
}
if ($placeholders) { Fail "placeholder pins left in: $(($placeholders | Select-Object -Unique) -join ', '). Pin them first" }
Good "no placeholder pins"

Step "Checking the Hugging Face login"
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$who = (& hf.exe auth whoami 2>&1 | Out-String).Trim()
$ErrorActionPreference = $previousPreference
if ($LASTEXITCODE -ne 0 -or $who -match 'Not logged in') {
    Fail "not logged in. Run:  hf auth login    (a write token from huggingface.co/settings/tokens)"
}
Note "as $($who -split "`n" | Select-Object -First 1)"

# --public so the app reads it with no token at all; --exist-ok so re-running is not an error.
Invoke-Hf repos create $Repo --repo-type dataset --public --exist-ok
if ($LASTEXITCODE -ne 0) { Fail "could not create or reach the dataset repo $Repo" }
Note "dataset repo: https://huggingface.co/datasets/$Repo"

$explicit = @{}
foreach ($pair in $SetVersion) {
    $bits = $pair -split '=', 2
    if ($bits.Count -ne 2) { Fail "SetVersion wants component=version, got '$pair'" }
    $explicit[$bits[0]] = $bits[1]
}

# --- Upload what changed ----------------------------------------------------------------------
$uploaded = [System.Collections.Generic.List[string]]::new()

foreach ($entry in Get-AllComponents) {
    $componentName = $entry.Name
    $component = $entry.Component
    $changed = $false

    # payload.sha256 pins the 32-bit pair on its own, and the installer checks each binary against
    # its line there: a new pair sent up beside the old sums file breaks every 32-bit install, and
    # payload.json would not show it. Checked before anything is uploaded.
    if ($componentName -eq 'bridge' -and $From) {
        $pair = @($component.files | Where-Object { $_.name -ne 'payload.sha256' })
        if ($pair | Where-Object { Test-Path -LiteralPath (Join-Path $From $_.name) }) {
            $sums = Join-Path $From 'payload.sha256'
            if (-not (Test-Path -LiteralPath $sums)) { Fail "-From has part of the 32-bit pair but no payload.sha256 for it" }
            $pinned = @{}
            foreach ($line in Get-Content -LiteralPath $sums) {
                if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') { $pinned[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant() }
            }
            foreach ($f in $pair) {
                $given = Join-Path $From $f.name
                $expect = if (Test-Path -LiteralPath $given) { Sha256 $given } else { $f.sha256 }
                if ($pinned[$f.name] -ne $expect) { Fail "payload.sha256 does not pin the $($f.name) being published" }
            }
        }
    }

    foreach ($f in $component.files) {
        # ReShade is fetched from reshade.me exactly as a person would download it, and never mirrored.
        if ($f.url -and $f.url -notlike "*huggingface.co*" -and $f.url -notlike "*catbox.moe*") {
            Note "$componentName/$($f.name): left at $($f.url)"
            continue
        }

        $local = if ($From) { Join-Path $From $f.name } else { $null }
        if (-not $local -or -not (Test-Path -LiteralPath $local)) {
            # Not being replaced. It still has to move to the stable host if it is not there yet.
            if ($f.url -like "*huggingface.co*") { continue }
            $relative = if ($f.PSObject.Properties['path'] -and $f.path) { $f.path } else { $f.name }
            $cached = Join-Path $env:APPDATA "AmdNrInstaller\cache\$componentName\$($component.version)\$($relative -replace '/', '\')"
            if (-not (Test-Path -LiteralPath $cached)) {
                Note "$componentName/$($f.name): not given and not cached, leaving at $($f.url)"
                continue
            }
            $local = $cached
        }

        $size = (Get-Item -LiteralPath $local).Length
        $sha  = Sha256 $local
        if ($sha -ne $f.sha256) { $changed = $true }

        # Where it goes in the dataset: where its address already points, when that is this dataset --
        # the mochizuki files live under mochizuki/<version>/ -- and otherwise its name at the root.
        $inRepo = if ($f.url -and $f.url.StartsWith("$base/")) { $f.url.Substring($base.Length + 1) } else { $f.name }
        Step "Uploading $($f.name) as $inRepo  ($([math]::Round($size/1MB,2)) MB)"
        Invoke-Hf upload $Repo $local $inRepo --repo-type dataset --commit-message "payload: $inRepo"
        if ($LASTEXITCODE -ne 0) { Fail "upload of $($f.name) failed" }

        # Keep the immutable copy as a mirror: it costs nothing and it is a second address that
        # already works. Nothing is trusted past its hash, so a mirror can be anywhere.
        # Every mirror the file already had stays too while the bytes are the same ones: the GitHub
        # release copies were being dropped by a re-upload that changed nothing.
        $mirrors = @()
        if ($sha -eq $f.sha256) {
            if ($f.url -and $f.url -like "*catbox.moe*") { $mirrors += $f.url }
            if ($f.PSObject.Properties['mirrors']) { $mirrors += @($f.mirrors) }
            $mirrors = @($mirrors | Select-Object -Unique)
        }

        $f.size   = $size
        $f.sha256 = $sha
        $f | Add-Member -NotePropertyName url -NotePropertyValue "$base/$inRepo" -Force
        if ($mirrors.Count -gt 0) { $f | Add-Member -NotePropertyName mirrors -NotePropertyValue $mirrors -Force }
        else { $f.PSObject.Properties.Remove('mirrors') }
        $uploaded.Add("$componentName/$($f.name)")
    }

    # The cache is keyed by component and version, so new bytes under an unchanged version would be
    # read as a corrupt cache by everyone who already has the old one. A release's own component
    # keeps the release's version: the app refuses a release whose component says otherwise.
    if ($entry.Release -and $entry.Release.StartsWith("$componentName ")) {
        if ($changed) { Fail "$componentName in release $($entry.Release) changed; publish it as a new release instead" }
    } elseif ($explicit.ContainsKey($componentName)) {
        $component.version = $explicit[$componentName]
        Note "$componentName version set to $($component.version)"
    } elseif ($changed) {
        $component.version = BumpVersion $component.version
        Note "$componentName content changed, version bumped to $($component.version)"
    }
}

# --- Rewrite and publish the manifests ----------------------------------------------------------
Step "Writing payload.json"
$json = $manifest | ConvertTo-Json -Depth 12
Set-Content -LiteralPath $manifestPath -Value $json -Encoding utf8 -NoNewline
Good "$manifestPath"

Step "Uploading payload.json and api-db.json"
Invoke-Hf upload $Repo $manifestPath 'payload.json' --repo-type dataset --commit-message 'payload manifest'
if ($LASTEXITCODE -ne 0) { Fail 'upload of payload.json failed' }
if (Test-Path $apiDbPath) {
    Invoke-Hf upload $Repo $apiDbPath 'api-db.json' --repo-type dataset --commit-message 'graphics API database'
    if ($LASTEXITCODE -ne 0) { Fail 'upload of api-db.json failed' }
}

Step "Pointing config.json at the stable addresses"
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$config.Payload | Add-Member -NotePropertyName ManifestUrl -NotePropertyValue "$base/payload.json" -Force
$config.Payload | Add-Member -NotePropertyName ApiDbUrl    -NotePropertyValue "$base/api-db.json" -Force
Set-Content -LiteralPath $configPath -Value ($config | ConvertTo-Json -Depth 8) -Encoding utf8 -NoNewline
Good "$configPath"

# --- The only step that proves anything ---------------------------------------------------------
Step "Downloading every published file back and checking its hash"
& $PSCommandPath -Repo $Repo -Verify

Write-Host ""
Good "published: $($uploaded.Count) file(s)"
foreach ($u in $uploaded) { Note $u }
Write-Host ""
Note "These addresses are stable. Swapping a payload again is this same command --"
Note "no new build of the app, because config.json does not change."
