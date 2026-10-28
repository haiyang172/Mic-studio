[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$application = Join-Path $workspace "src\MicStudio.App\bin\$Configuration\net10.0-windows\MicStudio.exe"
if (!(Test-Path -LiteralPath $application)) { throw 'Build the native application before UI verification.' }
if ([string]::IsNullOrEmpty($OutputDirectory)) { $OutputDirectory = Join-Path $workspace 'artifacts\p1-ui' }
$report = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $report -Force | Out-Null
$process = Start-Process -FilePath $application -ArgumentList @('--ui-smoke', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'The isolated UI check exceeded 60 seconds.' }
if ($process.ExitCode -ne 0) {
    $errorFile = Join-Path $report 'error.txt'
    if (Test-Path -LiteralPath $errorFile) { Write-Output ([IO.File]::ReadAllText($errorFile)) }
    throw 'The isolated UI check failed.'
}
$controls = Get-Content -LiteralPath (Join-Path $report 'control-events.json') -Raw | ConvertFrom-Json
$layout = Get-Content -LiteralPath (Join-Path $report 'layout-report.json') -Raw | ConvertFrom-Json
if ($controls.Passed -lt 32 -or $controls.Passed -ne $controls.Checks.Count -or $layout.Results.Count -ne 27 -or
    @($layout.Results | Where-Object { $_.RightOverflow -or !$_.ScrollReachable }).Count -ne 0) { throw 'UI verification report is incomplete.' }
Write-Output ("UI verified: {0} control checks, {1} layouts; no audio opened; isolated workspace only." -f $controls.Passed, $layout.Results.Count)
