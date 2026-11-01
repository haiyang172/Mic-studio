[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$native = Join-Path $workspace 'artifacts\native\bin'
foreach ($name in @('MicStudioApo.dll', 'MicStudioEcho.dll', 'SpeexDSP-COPYING.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $native $name))) { throw "Build the native components before staging: $name" }
}
$application = Join-Path $workspace "src\MicStudio.App\bin\$Configuration\net10.0-windows"
$tests = Join-Path $workspace "tests\MicStudio.WindowsAudio.Tests\bin\$Configuration\net10.0-windows"
foreach ($target in @($application, $tests)) {
    if (!(Test-Path -LiteralPath $target)) { throw "Build the managed projects before staging: $target" }
    Copy-Item -LiteralPath (Join-Path $native 'MicStudioEcho.dll') -Destination $target
}
$component = Join-Path $application 'components\MicStudioAPO'
New-Item -ItemType Directory -Path $component -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $native 'MicStudioApo.dll') -Destination $component
Write-Output 'Native files staged for offline tests and UI inspection. No component was installed or registered.'
