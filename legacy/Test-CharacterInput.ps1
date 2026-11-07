# Retired historical probe (P0 / synthetic character input). Kept as a pointer only.
#
# This probe typed into the engine input box through window messages and assumed
# numeric option 2 on the title screen was a text-entry path. The in-process
# bridge later showed that assumption was false for the version this project was
# developed against, and that window-level input could not reliably commit a
# choice. The correction is documented in TEST-REPORT.md.
#
# The executable body was removed during release preparation because it depended
# on one specific game build and could not be made portable. It remains runnable
# as a deliberate failure so that an old command line cannot silently proceed.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][ValidateSet('stage','submit')][string]$Mode,
 [Parameter(Mandatory=$true)][string]$RunDirectory
)
$ErrorActionPreference='Stop'
throw 'Retired historical probe: numeric option 2 was not a valid title choice. Use the in-process bridge.'
