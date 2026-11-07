# Report which executables a game folder offers and whether this adapter can drive
# them. Read-only: nothing is launched and nothing is written by this script.
#
#   .\scripts\Get-EraDshEngines.ps1 -GameRoot 'D:\Games\SomeEraGame'
#
# Two engine families are drivable:
#   framework - .NET Framework Emuera, loaded by reflection in EraBridgeHost.exe
#   core      - bundled .NET Emuera, driven through the startup-hook adapter
#
# Exit codes: 0 when at least one drivable engine was found, 2 when none was.
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
$supported = @($engines | Where-Object { $_.Kind -eq 'framework' -or $_.Kind -eq 'core' })

Write-Output ('game folder: ' + $root)
Write-Output ('executables: ' + $engines.Count + '  drivable: ' + $supported.Count)
Write-Output ''
if ($engines.Count -eq 0) { Write-Output 'no executable found at the top level'; exit 2 }

$engines |
    Select-Object FileName, KindLabel,
        @{ Name = 'Version'; Expression = { if ($_.Version) { $_.Version } else { '-' } } },
        @{ Name = 'Tested'; Expression = { $_.Tested } } |
    Format-Table -AutoSize | Out-String -Width 200

$framework = @($supported | Where-Object { $_.Kind -eq 'framework' })
$core = @($supported | Where-Object { $_.Kind -eq 'core' })

if ($supported.Count -gt 1) {
    $tested = @($framework | Where-Object { $_.Tested })
    if ($tested.Count -eq 1) {
        Write-Output ('would select by default: ' + $tested[0].FileName + ' (tested .NET Framework version)')
    } else {
        Write-Output 'several drivable builds; pick one with -EngineExecutable'
    }
} elseif ($supported.Count -eq 1) {
    $only = $supported[0]
    if ($only.Kind -eq 'framework') {
        Write-Output ('would select by default: ' + $only.FileName +
            $(if ($only.Tested) { ' (tested version)' } else { ' (unverified version; a warning will be printed)' }))
    } else {
        Write-Output ('would select by default: ' + $only.FileName + ' (bundled .NET build; needs the .NET 8 desktop runtime and the startup-hook adapter)')
    }
} else {
    Write-Output 'no Emuera engine here; only native tools and unrelated executables were found.'
    Write-Output 'See docs/engine-compatibility.md.'
    exit 2
}

if ($core.Count -gt 0) {
    Write-Output ''
    Write-Output ('bundled .NET builds present: ' + $core.Count + ' (driven through DOTNET_STARTUP_HOOKS, not reflection)')
}
exit 0
