# Retired historical probe (P0 / external-window route). Kept as a pointer only.
#
# This probe assumed numeric option 2 on the title screen opened an information
# page. The in-process bridge later showed that the title screen only accepts
# 0/1/8/9 for the version this project was developed against, so the assumption
# was wrong and the probe is retired. The correction is documented in
# TEST-REPORT.md; use scripts/Invoke-Bridge.ps1 with engine-derived buttons.
#
# The executable body was removed during release preparation because it depended
# on one specific game build and could not be made portable. It remains runnable
# as a deliberate failure so that an old command line cannot silently proceed.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference='Stop'
throw 'Retired historical probe: title option 2 was misidentified. Use scripts/Invoke-Bridge.ps1 with engine-derived buttons.'
