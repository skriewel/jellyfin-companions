# Adds (or replaces) the entry for one version in manifest.json, the Jellyfin plugin repository file.
# Uses the package built by build.ps1 in .\dist and the GitHub release URL for that version.
param(
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$repoUrl = "https://github.com/skriewel/jellyfin-companions"
$zip = Join-Path $root "dist\Companions_$Version.zip"
$metaFile = Join-Path $root "dist\Companions_$Version\meta.json"
if (-not (Test-Path $zip) -or -not (Test-Path $metaFile)) { throw "Package for $Version not found in dist - run build.ps1 first" }

$meta = Get-Content $metaFile -Raw -Encoding UTF8 | ConvertFrom-Json
$entry = [ordered]@{
    version   = $Version
    changelog = $meta.changelog
    targetAbi = $meta.targetAbi
    sourceUrl = "$repoUrl/releases/download/v$Version/Companions_$Version.zip"
    checksum  = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
    timestamp = $meta.timestamp
}

$manifestFile = Join-Path $root "manifest.json"
$versions = New-Object System.Collections.ArrayList
if (Test-Path $manifestFile) {
    $old = Get-Content $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($v in @($old)[0].versions) {
        if ($v.version -ne $Version) {
            [void]$versions.Add([ordered]@{
                version = $v.version; changelog = $v.changelog; targetAbi = $v.targetAbi
                sourceUrl = $v.sourceUrl; checksum = $v.checksum; timestamp = $v.timestamp
            })
        }
    }
}
[void]$versions.Insert(0, $entry)
$sorted = New-Object System.Collections.ArrayList
foreach ($v in ($versions | Sort-Object { [version]$_.version } -Descending)) { [void]$sorted.Add($v) }

$package = [ordered]@{
    guid        = $meta.guid
    name        = $meta.name
    description = $meta.description
    overview    = $meta.overview
    owner       = "skriewel"
    category    = $meta.category
    imageUrl    = "https://raw.githubusercontent.com/skriewel/jellyfin-companions/main/images/logo.png"
    versions    = $sorted
}
$json = ConvertTo-Json -InputObject @($package) -Depth 10
[IO.File]::WriteAllText($manifestFile, $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "manifest.json: $($meta.name) $Version ($($entry.checksum))"
