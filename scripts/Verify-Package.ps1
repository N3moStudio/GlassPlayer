param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][ValidateSet('arm64','x64')][string]$Architecture
)
$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path -LiteralPath $PublishDir).Path
$expectedMachine = if ($Architecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
function Assert-Machine([string]$RelativePath) {
    $path = Join-Path $packageRoot $RelativePath
    $stream = [IO.File]::OpenRead($path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3C
        $header = $reader.ReadInt32()
        $stream.Position = $header
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE file: $RelativePath" }
        $machine = $reader.ReadUInt16()
        if ($machine -ne $expectedMachine) { throw "Wrong native architecture in $RelativePath`: $machine" }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
foreach ($file in @('GlassPlayer.exe','coreclr.dll','WebView2Loader.dll',"libvlc\win-$Architecture\libvlc.dll", "libvlc\win-$Architecture\libvlccore.dll")) {
    Assert-Machine $file
}
$config = Get-Content (Join-Path $packageRoot 'config.json') -Raw | ConvertFrom-Json
if (-not [string]::IsNullOrEmpty($config.SPOTIFY_CLIENT_ID)) { throw 'Release config must have a blank Spotify Client ID' }
$unexpected = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object {
    $_.Name -match '^(session\.bin|settings\.json|audio\.log|crash\.log|Cookies|Login Data)$' -or $_.Extension -eq '.pdb'
}
if ($unexpected) { throw 'Release contains local account, debug, or diagnostic files' }
foreach ($name in @('LICENSE','CREDITS.md','THIRD-PARTY-NOTICES.md','README.md','CHANGELOG.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $name))) { throw "Missing release document: $name" }
}
Write-Output "PASS clean $Architecture package; application, runtime, and VLC have the expected native architecture"
