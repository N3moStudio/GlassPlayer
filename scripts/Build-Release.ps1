param(
    [ValidateSet('arm64','x64')][string]$Architecture = 'arm64',
    [string]$Version = '0.3.2',
    [string]$DotNet = 'dotnet',
    [string]$InnoCompiler,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repoRoot
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$') { throw 'Invalid release version' }
$rid = "win-$Architecture"
$packageRoot = Join-Path $repoRoot "artifacts\release-$Version-$Architecture"
if (Test-Path -LiteralPath $packageRoot) { throw 'Use a fresh publish directory to avoid including stale files.' }
& $DotNet publish GlassPlayer/GlassPlayer.csproj -c Release -r $rid --self-contained true -p:Version=$Version -p:RestoreLockedMode=true -o $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
foreach ($file in @('LICENSE','README.md','CREDITS.md','CHANGELOG.md','THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $packageRoot
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $packageRoot -Recurse
# Never distribute machine-specific credentials, even if a developer edited the local file.
[IO.File]::WriteAllText((Join-Path $packageRoot 'config.json'), "{`n  `"SPOTIFY_CLIENT_ID`": `"`"`n}`n")
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PublishDir $packageRoot -Architecture $Architecture
$output = Join-Path $repoRoot 'artifacts\installers'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zipPath = Join-Path $output "GlassPlayer-$Version-windows-$Architecture-portable.zip"
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath
if (-not $SkipInstaller) {
    if (-not $InnoCompiler) {
        $candidates = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
        $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $InnoCompiler) { throw 'Inno Setup 6.5+ is required to create the installer; GitHub Windows runners include it.' }
    & $InnoCompiler "/DAppVersion=$Version" "/DArch=$Architecture" "/DPublishDir=$packageRoot" "/DOutputDir=$output" (Join-Path $repoRoot 'installer\GlassPlayer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
}
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Directory $output
