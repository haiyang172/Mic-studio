[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if (!$cmake) { throw 'The native components need CMake and Visual Studio C++ with Windows SDK. The Windows CI builds them independently of local .NET tooling.' }
$destination = Join-Path $workspace 'artifacts\native\bin'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$projects = @(
    @{ Name='apo'; Source='src\MicStudio.Apo.Native'; Library='MicStudioApo.dll' },
    @{ Name='echo'; Source='src\MicStudio.Echo.Native'; Library='MicStudioEcho.dll' }
)
foreach ($project in $projects) {
    $source = Join-Path $workspace $project.Source
    $build = Join-Path $workspace ('artifacts\native\cmake\' + $project.Name)
    & $cmake.Source -S $source -B $build -G 'Visual Studio 17 2022' -A x64 -DBUILD_TESTING=ON
    if ($LASTEXITCODE -ne 0) { throw "Native configuration failed: $($project.Name)" }
    & $cmake.Source --build $build --config $Configuration --parallel 4
    if ($LASTEXITCODE -ne 0) { throw "Native compilation failed: $($project.Name)" }
    & ctest --test-dir $build -C $Configuration --output-on-failure
    if ($LASTEXITCODE -ne 0) { throw "Native signal/COM checks failed: $($project.Name)" }
    $library = Join-Path $build ($Configuration + '\' + $project.Library)
    if (!(Test-Path -LiteralPath $library)) { throw "Native library missing: $library" }
    Copy-Item -LiteralPath $library -Destination (Join-Path $destination $project.Library)
    $license = Join-Path $build 'SpeexDSP-COPYING.txt'
    if (Test-Path -LiteralPath $license) { Copy-Item -LiteralPath $license -Destination $destination }
}
Write-Output 'Native components and offline tests passed. No component was registered, bound to an endpoint, or played through a speaker.'
