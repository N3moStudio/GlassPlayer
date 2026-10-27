param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($source in (Get-Content (Join-Path $PSScriptRoot 'upstream-sources.json') -Raw | ConvertFrom-Json)) {
    if ($source.file -notmatch '^[A-Za-z0-9.-]+$') { throw 'Unexpected source archive name' }
    $destination = Join-Path $OutputDirectory $source.file
    Invoke-WebRequest -Uri $source.url -OutFile $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $source.sha256) { throw "Source archive checksum mismatch: $($source.file)" }
    [IO.File]::WriteAllText(($destination + '.sha256'), "$($source.sha256)  $($source.file)`n")
}
