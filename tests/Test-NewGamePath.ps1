# End-to-end path check: title -> new game -> setup -> prologue -> main interface.
# Asserts button visibility semantics at each stage; stops before any in-game action.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SessionFile)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$client=Join-Path $PSScriptRoot '..\scripts\Invoke-Bridge.ps1'
$settings=Get-Content -LiteralPath $SessionFile -Raw | ConvertFrom-Json
$statePath=Join-Path $settings.artifactDirectory 'step-state.json'
$script:obs=$null
function Get-Obs { return (& $client -SessionFile $SessionFile -Action observe -Mode text | ConvertFrom-Json) }
function Active { param($o) return @($o.buttons | Where-Object { $_.active }) }
function Send([string]$kind,[string]$value,[string]$choice) {
 $params=@{SessionFile=$SessionFile;Action='act';ObservationId=$script:obs.observationId;ActionId=[Guid]::NewGuid().ToString();Mode='text';Kind=$kind}
 if($value -ne '') { $params.Value=$value }
 if($choice -ne '') { $params.ChoiceId=$choice }
 $r=& $client @params | ConvertFrom-Json
 if(-not $r.ok) { $r | ConvertTo-Json -Depth 6; throw ('Action refused: '+$r.code) }
 $script:obs=$r.observation
 [pscustomobject]@{observationId=$script:obs.observationId;state=$script:obs.state;input=$script:obs.input.kind;requestId=$script:obs.input.requestId;totalLines=$script:obs.totalLines} | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
}
function Pick([string]$value) {
 $c=@(Active $script:obs | Where-Object { $_.value -eq $value })
 if($c.Count -eq 0) { throw ('No active button with value '+$value) }
 return $c[0]
}
$checks=New-Object System.Collections.Generic.List[object]
function Check([string]$name,[bool]$ok,$details) { $checks.Add([pscustomobject]@{name=$name;passed=$ok;details=$details}); if(-not $ok) { throw ('Path assertion failed: '+$name) } }
$script:obs=Get-Obs
Check 'title_input_integer' ($script:obs.input.kind -eq 'IntValue') $script:obs.input.kind
Check 'title_four_active_buttons' ((Active $script:obs).Count -eq 4) @((Active $script:obs) | ForEach-Object {$_.value})
$ny=Pick '1'; Send 'choose' '' $ny.id
Check 'setup_screen_offers_100' (@(Active $script:obs | Where-Object {$_.value -eq '100'}).Count -eq 1) (Active $script:obs).Count
$ok=Pick '100'; Send 'choose' '' $ok.id
Check 'prologue_no_active_buttons' ((Active $script:obs).Count -eq 0 -and $script:obs.input.kind -eq 'EnterKey') $script:obs.input.kind
for($n=0;$n -lt 8;$n++) {
 if($script:obs.input.kind -ne 'EnterKey') { break }
 Send 'continue' '' ''
}
Check 'reached_value_prompt' ($script:obs.input.kind -eq 'IntValue') $script:obs.input.kind
$eye=Pick '100'; Send 'choose' '' $eye.id
Check 'confrontation_buttons' ((Active $script:obs).Count -eq 2) @((Active $script:obs) | ForEach-Object {$_.value})
$her=Pick '2'; Send 'choose' '' $her.id
Check 'capture_narration_has_no_buttons' ((Active $script:obs).Count -eq 0) (Active $script:obs).Count
for($n=0;$n -lt 12;$n++) {
 if($script:obs.input.kind -ne 'EnterKey') { break }
 Send 'continue' '' ''
}
$act=Active $script:obs
Check 'main_interface_has_many_buttons' ($script:obs.input.kind -eq 'IntValue' -and $act.Count -ge 15) $act.Count
$datePattern=[string][char]0x5E74+'[^'+[char]0x5E74+']*'+[char]0x6708+'[^'+[char]0x6708+']*'+[char]0x65E5
$dateLines=@($script:obs.lines | Where-Object { $_.text -match $datePattern })
Check 'main_interface_has_date_line' ($dateLines.Count -ge 1) (@($dateLines | Select-Object -Last 1).text)
Check 'history_buttons_filtered' ($script:obs.omittedInactiveButtons -ge 0 -and $act.Count -eq $act.Count) $script:obs.omittedInactiveButtons
$result=[pscustomobject]@{passed=$true;checks=@($checks.ToArray());mainButtons=@($act | ForEach-Object {"$($_.value):$($_.label.Trim())"});observationId=$script:obs.observationId}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $settings.artifactDirectory 'path-results.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 8
