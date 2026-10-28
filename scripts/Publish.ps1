[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localSdk = Join-Path $workspace '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localSdk) { $dotnet = $localSdk }
else {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $installed) { throw 'Install the SDK specified in global.json before publishing.' }
    $dotnet = $installed.Source
}
$dist = [IO.Path]::GetFullPath((Join-Path $workspace 'dist'))
$published = [IO.Path]::GetFullPath((Join-Path $dist 'MicStudio-win-x64'))
$zip = Join-Path $dist 'MicStudio-win-x64.zip'
# Deletion is limited to this fixed generated directory, checked in this shell.
if (!$published.StartsWith($workspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $published -ne (Join-Path $workspace 'dist\MicStudio-win-x64') -or
    !(Test-Path -LiteralPath (Join-Path $workspace 'src\MicStudio.App\MicStudio.App.csproj'))) { throw 'Unexpected publishing workspace.' }
foreach ($candidate in @($dist, $published)) {
    if (Test-Path -LiteralPath $candidate) {
        $item = Get-Item -LiteralPath $candidate -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Publishing refuses a linked directory: $candidate" }
    }
}
Push-Location $workspace
try {
    if (!$SkipTests) { & (Join-Path $PSScriptRoot 'Test.ps1') -Configuration $Configuration }
    if (Test-Path -LiteralPath $published) { Remove-Item -LiteralPath $published -Recurse -Force }
    New-Item -ItemType Directory -Path $published -Force | Out-Null
    & $dotnet publish 'src\MicStudio.App\MicStudio.App.csproj' -c $Configuration -r win-x64 --self-contained true -o $published -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publication failed.' }
    & (Join-Path $PSScriptRoot 'Fetch-Components.ps1')
    $component = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'components.lock.json') -Raw | ConvertFrom-Json).vbCable
    $componentOutput = Join-Path $published 'components'
    New-Item -ItemType Directory -Path $componentOutput -Force | Out-Null
    $driverArchive = Join-Path $componentOutput $component.packageName
    Copy-Item -LiteralPath (Join-Path $workspace ('.tools\components\' + $component.packageName)) -Destination $driverArchive
    $driverFolder = Join-Path $componentOutput 'VBCABLE'
    Expand-Archive -LiteralPath $driverArchive -DestinationPath $driverFolder
    $officialInstaller = Join-Path $driverFolder $component.installerX64.file
    if ((Get-FileHash -LiteralPath $officialInstaller -Algorithm SHA256).Hash -ne $component.installerX64.sha256) { throw 'Bundled official installer hash mismatch.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $officialInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*BUREL VINCENT*') { throw 'The bundled official installer signature is not valid on this machine.' }
    Copy-Item -LiteralPath (Join-Path $workspace 'docs\third-party\VB-CABLE-NOTICE.txt') -Destination (Join-Path $componentOutput 'VB-CABLE-NOTICE.txt')
    foreach ($document in @('README.md', 'THIRD_PARTY.md')) {
        $source = Join-Path $workspace $document
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $published }
    }
    $validation = Join-Path $workspace 'docs\validation\test-plan.md'
    if (Test-Path -LiteralPath $validation) { Copy-Item -LiteralPath $validation -Destination (Join-Path $published 'VALIDATION.md') }
    $installerDoc = Join-Path $workspace 'docs\architecture\installer.md'
    if (Test-Path -LiteralPath $installerDoc) { Copy-Item -LiteralPath $installerDoc -Destination (Join-Path $published 'INSTALLER.md') }
    $componentDoc = Join-Path $workspace 'docs\architecture\components-vbcable.md'
    if (Test-Path -LiteralPath $componentDoc) { Copy-Item -LiteralPath $componentDoc -Destination (Join-Path $published 'COMPONENTS.md') }
    # Preserve license/notice texts supplied by the actual Microsoft and NAudio packages.
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
    $noticeRoot = Join-Path $published 'licenses'
    New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
    foreach ($package in @('naudio.core', 'naudio.wasapi', 'microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
        $packageRoot = Join-Path $nugetRoot $package
        if (!(Test-Path -LiteralPath $packageRoot)) { continue }
        foreach ($version in Get-ChildItem -LiteralPath $packageRoot -Directory) {
            foreach ($name in @('LICENSE', 'LICENSE.txt', 'LICENSE.TXT', 'license.txt', 'ThirdPartyNotices.txt', 'THIRD-PARTY-NOTICES.TXT')) {
                $notice = Join-Path $version.FullName $name
                if (Test-Path -LiteralPath $notice) { Copy-Item -LiteralPath $notice -Destination (Join-Path $noticeRoot ($package + '-' + $version.Name + '-' + $name)) -Force }
            }
        }
    }
    if (!(Test-Path -LiteralPath (Join-Path $published 'MicStudio.exe'))) { throw 'The expected native application executable was not produced.' }
    Compress-Archive -Path (Join-Path $published '*') -DestinationPath $zip -Force
    $checksums = @()
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  MicStudio-win-x64.zip"
    $setupProject = Join-Path $workspace 'src\MicStudio.Setup\MicStudio.Setup.csproj'
    if (Test-Path -LiteralPath $setupProject) {
        $installerOutput = Join-Path $dist 'installer'
        & $dotnet publish $setupProject -c $Configuration -r win-x64 --self-contained true -o $installerOutput "-p:PayloadZip=$zip" -p:RestoreLockedMode=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw 'Native setup publication failed.' }
        $setup = Join-Path $dist 'MicStudio.Setup.exe'
        Copy-Item -LiteralPath (Join-Path $installerOutput 'MicStudio.Setup.exe') -Destination $setup -Force
        $setupReport = Join-Path $dist 'setup-self-test.json'
        $selfTest = Start-Process -FilePath $setup -ArgumentList @('--self-test', ('"' + $setupReport + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($selfTest.ExitCode -ne 0 -or !(Test-Path -LiteralPath $setupReport)) { throw 'Native setup self-test failed.' }
        $selfTestResult = Get-Content -LiteralPath $setupReport -Raw | ConvertFrom-Json
        if ($selfTestResult.passed -lt 1 -or $selfTestResult.passed -ne $selfTestResult.checks.Count -or $selfTestResult.changedSystemFiles) { throw 'Setup self-test reported unexpected behavior.' }
        $payloadReport = Join-Path $dist 'setup-payload-validation.json'
        $payloadTest = Start-Process -FilePath $setup -ArgumentList @('--validate-payload', ('"' + $payloadReport + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($payloadTest.ExitCode -ne 0 -or !(Test-Path -LiteralPath $payloadReport)) { throw 'Native setup payload validation failed.' }
        $payloadResult = Get-Content -LiteralPath $payloadReport -Raw | ConvertFrom-Json
        if (!$payloadResult.PayloadPresent -or !$payloadResult.AppExecutableFound) { throw 'Native setup is missing its expected embedded application.' }
        $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksums += "$setupHash  MicStudio.Setup.exe"
        Write-Host "Native per-user setup: $setup"
        Write-Host 'Setup self-test and read-only payload validation passed; installation was not performed.'
    }
    [IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), (($checksums -join "`n") + "`n"), [Text.Encoding]::ASCII)
    Write-Host "Portable Windows x64 package: $zip"
    Write-Host "SHA-256: $hash"
}
finally { Pop-Location }
