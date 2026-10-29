param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Version = '0.3.0'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$') { throw 'Invalid release version' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$sourceRoot = Join-Path (Split-Path -Parent $outputRoot) "dependency-sources-$Version"
if (Test-Path -LiteralPath $sourceRoot) { throw 'Use a fresh source staging directory.' }
New-Item -ItemType Directory -Path $sourceRoot | Out-Null
foreach ($source in (Get-Content (Join-Path $PSScriptRoot 'upstream-sources.json') -Raw | ConvertFrom-Json)) {
    if ($source.file -notmatch '^[A-Za-z0-9.-]+$') { throw 'Unexpected source archive name' }
    $destination = Join-Path $sourceRoot $source.file
    Invoke-WebRequest -Uri $source.url -OutFile $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $source.sha256) { throw "Source archive checksum mismatch: $($source.file)" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'upstream-sources.json') -Destination $sourceRoot
$repoRoot = Split-Path -Parent $PSScriptRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $sourceRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses\LGPL-2.1.txt') -Destination $sourceRoot
[IO.File]::WriteAllText((Join-Path $sourceRoot 'README.txt'), @"
GlassPlayer $Version - dependency source archives

This ZIP contains the original VLC and LibVLCSharp source archives corresponding
to the bundled audio libraries. Extract each archive to inspect or rebuild it.
VLC's contrib directory contains dependency recipes and build instructions.
upstream-sources.json records the exact download URLs and SHA-256 hashes.
The included notices and LGPL text describe attribution and licensing.

GlassPlayer's own source is available from its GitHub release tag:
https://github.com/N3moStudio/GlassPlayer/tree/v$Version

These sources are for developers; they are not needed to install GlassPlayer.
"@)
$bundle = Join-Path $outputRoot "GlassPlayer-$Version-dependency-sources.zip"
Compress-Archive -Path (Join-Path $sourceRoot '*') -DestinationPath $bundle -CompressionLevel NoCompression
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $outputRoot
