[CmdletBinding()]
param([Parameter(Mandatory)][long]$RunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$HeadRevision)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ($RunId -le 0) { throw 'A real workflow run ID is required.' }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cache = Join-Path $workspace '.tools\native-ci'
$destination = Join-Path $workspace 'artifacts\native\bin'
foreach ($folder in @($cache, $destination)) {
    if ((Test-Path -LiteralPath $folder) -and ((Get-Item -LiteralPath $folder -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Native artifact paths cannot be links.' }
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
}
$lines = @('protocol=https', 'host=github.com', '') | & git -c credential.interactive=false credential fill 2>$null
if ($LASTEXITCODE -ne 0) { throw 'The existing GitHub Git credential is unavailable.' }
$credential = @{}
foreach ($line in $lines) { $at = $line.IndexOf('='); if ($at -gt 0) { $credential[$line.Substring(0, $at)] = $line.Substring($at + 1) } }
if (!$credential.ContainsKey('password')) { throw 'The existing GitHub Git credential is incomplete.' }
$headers = @{ Authorization='Bearer ' + $credential['password']; Accept='application/vnd.github+json'; 'User-Agent'='MicStudio-native-verification' }
try {
    $base = 'https://api.github.com/repos/haiyang172/Mic-studio'
    $run = Invoke-RestMethod -Uri "$base/actions/runs/$RunId" -Headers $headers
    if ($run.head_sha -ne $HeadRevision) { throw 'Workflow source revision does not match the reviewed commit.' }
    $artifacts = Invoke-RestMethod -Uri "$base/actions/runs/$RunId/artifacts" -Headers $headers
    $matchingArtifacts = @($artifacts.artifacts | Where-Object { $_.name -eq 'MicStudio-native-x64' -and !$_.expired })
    if ($matchingArtifacts.Count -ne 1) { throw 'Exactly one native artifact must be available for the reviewed run.' }
    $artifact = $matchingArtifacts[0]
    $digestMatch = [regex]::Match($artifact.digest, '^sha256:([0-9a-f]{64})$')
    if (!$digestMatch.Success) { throw 'GitHub did not provide a native artifact digest.' }
    $expected = $digestMatch.Groups[1].Value
    $archive = Join-Path $cache ($RunId.ToString() + '.zip')
    Invoke-WebRequest -Uri "$base/actions/artifacts/$($artifact.id)/zip" -Headers $headers -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Downloaded native artifact digest mismatch.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $allowed = @('MicStudioApo.dll', 'MicStudioEcho.dll', 'SpeexDSP-COPYING.txt')
        if ($zip.Entries.Count -ne $allowed.Count) { throw 'Unexpected native artifact file count.' }
        foreach ($name in $allowed) {
            $entry = @($zip.Entries | Where-Object { $_.FullName -ceq $name })
            if ($entry.Count -ne 1 -or $entry[0].Length -gt 32MB -or $entry[0].Length -le 0) { throw "Unexpected native artifact entry: $name" }
        }
        foreach ($name in $allowed) { [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($name), (Join-Path $destination $name), $true) }
    }
    finally { $zip.Dispose() }
    [pscustomobject]@{ RunId=$RunId; HeadRevision=$HeadRevision; ArtifactId=$artifact.id; ArchiveSha256=$expected;
        Files=@(Get-ChildItem -LiteralPath $destination -File | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $cache ($RunId.ToString() + '.json')) -Encoding utf8
    Write-Output "Verified native artifact for $HeadRevision; no component was installed or registered."
}
finally { $headers.Clear(); $credential.Clear(); $lines = $null }
