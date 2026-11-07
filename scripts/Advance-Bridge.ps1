# Advance EnterKey pages until the input kind or the button set changes.
# Stops on the first such change; never sends a choice by itself.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$SessionFile,
 [int]$MaxSteps=15,
 [int]$Tail=4
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$client=Join-Path $PSScriptRoot 'Invoke-Bridge.ps1'
$settings=Get-Content -LiteralPath $SessionFile -Raw | ConvertFrom-Json
$statePath=Join-Path $settings.artifactDirectory 'step-state.json'
$state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
function Signature($o) { return ($o.input.kind + '|' + (($o.buttons | ForEach-Object {$_.id}) -join ',')) }
$baseline=& $client -SessionFile $SessionFile -Action observe -Mode text -LimitLines 200 | ConvertFrom-Json
$previous=Signature $baseline
for($n=1;$n -le $MaxSteps;$n++) {
 $params=@{SessionFile=$SessionFile;Action='act';Kind='continue';ObservationId=$state.observationId;ActionId=[Guid]::NewGuid().ToString();Mode='text'}
 $r=& $client @params | ConvertFrom-Json
 if(-not $r.ok) { $r | ConvertTo-Json -Depth 6; throw ('Advance refused: '+$r.code) }
 $obs=$r.observation
 $changed=(Signature $obs) -ne $previous
 $previous=Signature $obs
 $state=[pscustomobject]@{observationId=$obs.observationId;state=$obs.state;input=$obs.input.kind;requestId=$obs.input.requestId;totalLines=$obs.totalLines}
 $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
 $rows=@($obs.lines);$start=[Math]::Max(0,$rows.Count-$Tail)
 $tailText=($rows[$start..($rows.Count-1)] | ForEach-Object {$_.text}) -join ' / '
 Write-Output ('{0,2}. {1} [{2}] {3}' -f $n,$obs.input.kind,$obs.requestId,$tailText)
 if($changed) { Write-Output '--- button/type change ---'; foreach($b in $obs.buttons) { Write-Output ('{0} | {1} | value={2} gen={3}' -f $b.id,$b.label,$b.value,$b.generation) }; break }
}
