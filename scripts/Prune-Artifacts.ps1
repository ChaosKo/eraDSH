[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$Keep = 20,
    [int]$MaxMegabytes = 100,
    [string]$CurrentSession = '',
    [switch]$WhatIf
)
# Keep the artifact directory bounded without destroying the run in progress.
# Two thresholds, both must hold: at most -Keep sessions, and at most -MaxMegabytes
# in total. The oldest sessions are removed first; the current session is never
# removed even if it alone exceeds the budget.
$ErrorActionPreference = 'Stop'
$artifacts = Join-Path $RepositoryRoot 'artifacts'
if (-not (Test-Path -LiteralPath $artifacts)) { return }

$sessions = @(Get-ChildItem -LiteralPath $artifacts -Directory -Filter 'bridge-*' |
    Sort-Object LastWriteTime -Descending)

$removed = New-Object System.Collections.Generic.List[object]
$kept = 0
$totalBytes = 0

foreach ($session in $sessions) {
    $bytes = (Get-ChildItem -LiteralPath $session.FullName -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum
    if ($null -eq $bytes) { $bytes = 0 }

    $isCurrent = $CurrentSession -and ($session.FullName -eq [IO.Path]::GetFullPath($CurrentSession))
    $overCount = $kept -ge $Keep
    $overSize = ($totalBytes + $bytes) -gt ($MaxMegabytes * 1MB) -and $kept -gt 0
    if (-not $isCurrent -and ($overCount -or $overSize)) {
        $removed.Add([pscustomobject]@{ path = $session.FullName; mb = [math]::Round($bytes / 1MB, 2) })
        if (-not $WhatIf) { Remove-Item -LiteralPath $session.FullName -Recurse -Force }
        continue
    }
    $kept++
    $totalBytes += $bytes
}

[pscustomobject]@{
    kept = $kept
    removed = $removed.Count
    keptMegabytes = [math]::Round($totalBytes / 1MB, 1)
    whatIf = [bool]$WhatIf
    removedSessions = @($removed | ForEach-Object { Split-Path $_.path -Leaf })
} | ConvertTo-Json -Depth 4
