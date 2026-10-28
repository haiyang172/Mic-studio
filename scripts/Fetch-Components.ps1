[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'components.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1) { throw 'Unsupported bundled component manifest.' }
$component = $lock.vbCable
if ($component.url -ne 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip' -or $component.packageName -ne 'VBCABLE_Driver_Pack45.zip') { throw 'Unexpected component source.' }
$cache = [IO.Path]::GetFullPath((Join-Path $workspace '.tools\components'))
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache $component.packageName
if (!(Test-Path -LiteralPath $archive)) {
    $temporary = Join-Path $cache ($component.packageName + '.download-' + [Guid]::NewGuid().ToString('N'))
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $component.url -OutFile $temporary -TimeoutSec 60
        $downloadHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
        if ($downloadHash -ne $component.sha256 -or (Get-Item -LiteralPath $temporary).Length -ne $component.bytes) { throw 'Official component bytes have changed. Review the source and update the lock deliberately; do not run this download.' }
        Move-Item -LiteralPath $temporary -Destination $archive
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $component.sha256 -or (Get-Item -LiteralPath $archive).Length -ne $component.bytes) { throw 'The cached component does not match the pinned original archive.' }
Write-Host "Verified original VB-CABLE Pack45 archive: $archive"
Write-Host 'No driver was executed or installed.'
