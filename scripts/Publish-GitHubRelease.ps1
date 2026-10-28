[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+[-.a-zA-Z0-9]*$')][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory)][string]$NotesFile,
    [switch]$Prerelease
)
$ErrorActionPreference = 'Stop'
$repository = 'haiyang172/Mic-studio'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $workspace 'dist'
$assets = @('MicStudio-win-x64.zip', 'MicStudio.Setup.exe', 'SHA256SUMS.txt')
foreach ($name in $assets) {
    if (!(Test-Path -LiteralPath (Join-Path $dist $name) -PathType Leaf)) { throw "Missing release asset: $name" }
}
if (!(Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw 'The release notes file is required.' }
# Reuse the user's existing GitHub Git credential; never log or persist the secret.
$credentialLines = @('protocol=https', 'host=github.com', '') | & git -c credential.interactive=false credential fill 2>$null
if ($LASTEXITCODE -ne 0) { throw 'No existing non-interactive GitHub Git credential is available.' }
$credential = @{}
foreach ($line in $credentialLines) {
    $separator = $line.IndexOf('=')
    if ($separator -gt 0) { $credential[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
}
if (!$credential.ContainsKey('password')) { throw 'The existing GitHub credential is incomplete.' }
$headers = @{ Authorization = 'Bearer ' + $credential['password']; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'MicStudio-release' }
try {
    $existing = $null
    try { $existing = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$repository/releases/tags/$Tag" -Headers $headers }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
    if ($null -ne $existing) { throw "Release $Tag already exists; this script never replaces a release or its assets." }
    $body = @{ tag_name = $Tag; target_commitish = $Commit; name = "Mic Studio $Tag"; body = [IO.File]::ReadAllText([IO.Path]::GetFullPath($NotesFile)); draft = $true; prerelease = [bool]$Prerelease }
    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repository/releases" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json)))
    $upload = $release.upload_url -replace '\{.*$', ''
    if ($upload -notmatch '^https://uploads\.github\.com/repos/haiyang172/Mic-studio/releases/[0-9]+/assets$') { throw 'GitHub returned an unexpected upload destination.' }
    foreach ($name in $assets) {
        $asset = Invoke-RestMethod -Method Post -Uri ($upload + '?name=' + [Uri]::EscapeDataString($name)) -Headers $headers -ContentType 'application/octet-stream' -InFile (Join-Path $dist $name)
        if ($asset.state -ne 'uploaded' -or $asset.size -ne (Get-Item -LiteralPath (Join-Path $dist $name)).Length) { throw "Asset upload did not complete: $name. Release remains a draft." }
        Write-Host "Uploaded $name ($($asset.size) bytes)"
    }
    $published = Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$repository/releases/$($release.id)" -Headers $headers -ContentType 'application/json' -Body '{"draft":false}'
    Write-Host ('Published release: ' + $published.html_url)
}
finally {
    $headers.Clear(); $credential.Clear(); $credentialLines = $null
}
