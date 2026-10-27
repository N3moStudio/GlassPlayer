param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
$lines = @(Get-ChildItem -LiteralPath $root -File | Where-Object Name -NE 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
})
[IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS.txt'), ($lines -join "`n") + "`n")
