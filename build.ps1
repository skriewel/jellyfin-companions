# Builds the plugin and packs a ready-to-install folder + zip into .\dist
param(
    [string]$Version = "1.0.3.0"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "Jellyfin.Plugin.Companions\Jellyfin.Plugin.Companions.csproj"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (Test-Path $userDotnet) { $dotnet = $userDotnet }

& $dotnet build $proj -c Release -nologo -p:AssemblyVersion=$Version -p:FileVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$dist = Join-Path $root "dist"
$folder = Join-Path $dist "Companions_$Version"
if (Test-Path $folder) { Remove-Item $folder -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $folder | Out-Null

Copy-Item (Join-Path $root "Jellyfin.Plugin.Companions\bin\Release\net10.0\Jellyfin.Plugin.Companions.dll") $folder

Copy-Item (Join-Path $root "images\logo.png") $folder

$meta = [ordered]@{
    category    = "General"
    changelog   = "Books: titles are also searched without a leading number or article (3 Body Problem -> Body Problem), so book servers that search literally find The Three-Body Problem even when Jellyfin knows no source author."
    description = "Links movies and series to their soundtrack in your music library and to the book or audiobook in Audiobookshelf, Kavita, Grimmory or BookOrbit."
    guid        = "5d0a3a1e-2e48-4358-b9a5-5942845aedb4"
    name        = "Companions"
    overview    = "Soundtracks, books and audiobooks for your movies"
    owner       = "skriewel"
    targetAbi   = "12.0.0.0"
    timestamp   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    version     = $Version
    status      = "Active"
    autoUpdate  = $true
    imagePath   = "logo.png"
    assemblies  = @("Jellyfin.Plugin.Companions.dll")
}
[IO.File]::WriteAllText((Join-Path $folder "meta.json"), ($meta | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

$zip = Join-Path $dist "Companions_$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force -Confirm:$false }
Compress-Archive -Path (Join-Path $folder "*") -DestinationPath $zip
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash
Write-Host "Done: $zip (MD5 $md5)"

& (Join-Path $root "update-manifest.ps1") -Version $Version
