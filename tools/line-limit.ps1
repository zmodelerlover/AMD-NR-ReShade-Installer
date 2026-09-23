# Hold every source file of our own to 500 lines, and never let a grandfathered one grow.
#
# The window's code-behind reached 1638 lines, the engine's install flow 1232: a file only ever gets
# one more method, and nothing stopped either. This is the stop.
#
#   powershell -ExecutionPolicy Bypass -File tools\line-limit.ps1
#
# Files that were already over the limit when this check arrived are listed, with their length at
# the time, in tools\line-limit-allow.json. That list is a ratchet: an entry may only go down. A
# listed file that grows fails, and so does one that has come under the limit or no longer exists,
# so the entry has to be deleted rather than left behind to excuse the next file with that name.
#
# Scanned: tracked files under src\, tests\ and tools\. Data (json, md, txt) and binaries are not code.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$allowPath = Join-Path $PSScriptRoot 'line-limit-allow.json'
$limit = 500
$skipped = @('.md', '.json', '.txt', '.csv', '.png', '.ico')

$allow = @{}
(Get-Content -Raw -Encoding UTF8 $allowPath | ConvertFrom-Json).PSObject.Properties |
    ForEach-Object { $allow[$_.Name] = [int]$_.Value }

$lengths = @{}
foreach ($name in (& git -C $root ls-files -- src tests tools)) {
    if ($skipped -contains [IO.Path]::GetExtension($name).ToLowerInvariant()) { continue }
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root $name))
    if ($bytes -contains 0) { continue }
    $count = ($bytes | Where-Object { $_ -eq 10 }).Count
    if ($bytes.Length -gt 0 -and $bytes[-1] -ne 10) { $count++ }
    $lengths[$name] = $count
}

$failures = @()
foreach ($name in ($lengths.Keys | Sort-Object)) {
    $lines = $lengths[$name]
    if ($allow.ContainsKey($name)) {
        if ($lines -gt $allow[$name]) {
            $failures += "${name}: $lines lines, grew past the $($allow[$name]) it is allowed in line-limit-allow.json; it may only shrink"
        }
    } elseif ($lines -gt $limit) {
        $failures += "${name}: $lines lines, over the limit of $limit"
    }
}
foreach ($name in ($allow.Keys | Sort-Object)) {
    if (-not $lengths.ContainsKey($name)) {
        $failures += "${name}: listed in line-limit-allow.json but no longer exists; delete the entry"
    } elseif ($lengths[$name] -le $limit) {
        $failures += "${name}: $($lengths[$name]) lines, now within the limit of $limit; delete its entry from line-limit-allow.json"
    }
}

foreach ($f in $failures) { Write-Output "FAIL $f" }
if ($failures.Count -gt 0) { exit 1 }
Write-Output "OK: $($lengths.Count) files within $limit lines, $($allow.Count) grandfathered and not grown"
exit 0
