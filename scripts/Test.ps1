[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localSdk = Join-Path $workspace '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localSdk) { $dotnet = $localSdk }
else {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $installed) { throw 'Install the SDK specified in global.json before running source tests.' }
    $dotnet = $installed.Source
}
$projects = @(
    'tests\MicStudio.Core.Tests\MicStudio.Core.Tests.csproj',
    'tests\MicStudio.Apo.Tests\MicStudio.Apo.Tests.csproj',
    'tests\MicStudio.WindowsAudio.Tests\MicStudio.WindowsAudio.Tests.csproj',
    'tests\MicStudio.Components.Tests\MicStudio.Components.Tests.csproj'
)
Push-Location $workspace
try {
    & $dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'The SDK specified in global.json is unavailable.' }
    # Verify the reviewed original component bytes and signer without executing its installer.
    & (Join-Path $PSScriptRoot 'Fetch-Components.ps1')
    $originalComponent = Join-Path $workspace '.tools\components\VBCABLE_Driver_Pack45.zip'
    foreach ($project in $projects) {
        if (!(Test-Path -LiteralPath $project)) {
            throw "Required test project is missing: $project"
        }
        & $dotnet restore $project --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "Test dependency restore failed: $project" }
        if ($project -like '*WindowsAudio.Tests*') { & $dotnet run --project $project -c $Configuration --no-restore -- --tests-only }
        elseif ($project -like '*Components.Tests*') { & $dotnet run --project $project -c $Configuration --no-restore -- $originalComponent }
        else { & $dotnet run --project $project -c $Configuration --no-restore }
        if ($LASTEXITCODE -ne 0) { throw "Verification failed: $project" }
    }
    $setupProject = 'src\MicStudio.Setup\MicStudio.Setup.csproj'
    & $dotnet restore $setupProject --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Native setup dependency restore failed.' }
    & $dotnet build $setupProject -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Native setup source build failed.' }
    $reportDirectory = Join-Path $workspace '.tools\test-results'
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $setupReport = Join-Path $reportDirectory 'setup-self-test.json'
    $setupExe = Join-Path $workspace "src\MicStudio.Setup\bin\$Configuration\net10.0-windows\MicStudio.Setup.exe"
    $setupCheck = Start-Process -FilePath $setupExe -ArgumentList @('--self-test', ('"' + $setupReport + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($setupCheck.ExitCode -ne 0 -or !(Test-Path -LiteralPath $setupReport)) { throw 'Native setup source self-test failed.' }
    $result = Get-Content -LiteralPath $setupReport -Raw | ConvertFrom-Json
    if ($result.passed -lt 1 -or $result.passed -ne $result.checks.Count -or $result.changedSystemFiles) { throw 'Native setup source self-test report is invalid.' }
    Write-Host ("Native setup synthetic checks passed: " + $result.passed + '. Installation was not performed.')
    Write-Host 'Automated verification passed. Live microphone, APO response, routing to other applications and listening acceptance remain separate manual checks.'
}
finally { Pop-Location }
