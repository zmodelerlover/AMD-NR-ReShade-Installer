# Every check a commit has to pass, in one run: the build, the tests, the line limit, and with -Ui
# the headless render of every window, which fails when a scroller cannot reach its own end.
#
#   powershell -ExecutionPolicy Bypass -File tools\gate.ps1 [-Ui]
#
# Nothing here opens a window or touches the real %AppData%: the tests and the render both run
# against a throwaway AMDNR_HOME.

param([switch]$Ui)

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$fail = 0

function Gate($name, [scriptblock]$run, $okPattern) {
    $o = & $run 2>&1 | Out-String
    $ok = ($LASTEXITCODE -eq 0) -and ($o -match $okPattern)
    if (-not $ok) { $script:fail++ }
    $verdict = if ($ok) { 'OK' } else { "FAIL`n" + (($o -split "`n") | Select-Object -Last 30 | Out-String) }
    '{0,-12} {1}' -f $name, $verdict
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('amdnr-gate-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:AMDNR_HOME = $scratch

Gate 'build' { dotnet build AmdNrInstaller.slnx -c Debug -nologo -v q } ' 0 Erro| 0 Error'
Gate 'test' { dotnet test AmdNrInstaller.slnx --no-build -nologo -v q } 'Aprovado!|Passed!'
Gate 'line-limit' { powershell -NoProfile -ExecutionPolicy Bypass -File tools\line-limit.ps1 } 'OK:'
if ($Ui) {
    $shots = Join-Path $scratch 'shots'
    Gate 'ui' { dotnet run --project tools\uishot -- $shots } 'every scroll reaches its end'
    "screenshots: $shots"
}

"FAILED GATES: $fail"
exit $fail
