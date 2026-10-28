[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localSdk = Join-Path $workspace '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localSdk) { $dotnet = $localSdk }
else {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $installed) { throw 'Install the .NET SDK specified in global.json, or place it in .tools/dotnet. The published application does not require an SDK.' }
    $dotnet = $installed.Source
}
Push-Location $workspace
try {
    & $dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'The SDK specified in global.json is unavailable.' }
    & $dotnet restore 'MicStudio.sln' --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & $dotnet build 'MicStudio.sln' -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Mic Studio build failed.' }
    Write-Host "Built src\MicStudio.App\bin\$Configuration\net10.0-windows\MicStudio.exe"
    Write-Host 'Also built Core, APO, WindowsAudio, Components, native Setup and all console verification projects.'
}
finally { Pop-Location }
