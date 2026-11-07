# Report which executables a game folder offers and whether this adapter can drive
# them. Read-only: nothing is launched and nothing is written.
#
#   .\scripts\Get-EraDshEngines.ps1 -GameRoot 'D:\Games\SomeEraGame'
#
# Exit codes: 0 when at least one supported engine was found, 2 when none was.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [string]$ConfigPath
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'Common.ps1')

$root = [IO.Path]::GetFullPath($GameRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw ('game folder does not exist: ' + $root) }

$engines = @(Get-EraDshEngineCandidates -GameRoot $root)
$supported = @($engines | Where-Object { $_.Kind -eq 'framework' })

Write-Output ('game folder: ' + $root)
Write-Output ('executables: ' + $engines.Count + '  supported: ' + $supported.Count)
Write-Output ''
if ($engines.Count -eq 0) { Write-Output 'no executable found at the top level'; exit 2 }

$engines |
    Select-Object FileName, KindLabel,
        @{ Name = 'Version'; Expression = { if ($_.Version) { $_.Version } else { '-' } } },
        @{ Name = 'Tested'; Expression = { $_.Tested } } |
    Format-Table -AutoSize | Out-String -Width 200

if ($supported.Count -gt 1) {
    $tested = @($supported | Where-Object { $_.Tested })
    if ($tested.Count -eq 1) {
        Write-Output ('would select by default: ' + $tested[0].FileName + ' (tested version)')
    } else {
        Write-Output 'several supported builds; pick one with -EngineExecutable'
    }
} elseif ($supported.Count -eq 1) {
    Write-Output ('would select by default: ' + $supported[0].FileName +
        $(if ($supported[0].Tested) { ' (tested version)' } else { ' (unverified version; a warning will be printed)' }))
} else {
    Write-Output 'no .NET Framework Emuera build here; this adapter cannot drive these executables.'
    Write-Output 'See docs/engine-compatibility.md.'
    exit 2
}
exit 0
