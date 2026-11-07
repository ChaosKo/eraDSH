# Pick one active button by its engine value (or label regex) and act.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$SessionFile,
 [string]$Value,
 [string]$Label,
 [int]$Tail=12
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$client=Join-Path $PSScriptRoot 'Invoke-Bridge.ps1'
$settings=Get-Content -LiteralPath $SessionFile -Raw | ConvertFrom-Json
$statePath=Join-Path $settings.artifactDirectory 'step-state.json'
$cur=& $client -SessionFile $SessionFile -Action observe -Mode text | ConvertFrom-Json
if(-not $cur.ok) { throw 'observe failed' }
$candidates=@($cur.buttons | Where-Object { $_.active })
if($PSBoundParameters.ContainsKey('Value')) { $candidates=@($candidates | Where-Object { $_.value -eq $Value }) }
if($PSBoundParameters.ContainsKey('Label')) { $candidates=@($candidates | Where-Object { $_.label -match $Label }) }
if($candidates.Count -eq 0) { throw ('No active button matched. Active values: '+(($cur.buttons|Where-Object {$_.active}|ForEach-Object {$_.value}) -join ',')) }
$pick=$candidates[0]
$r=& $client -SessionFile $SessionFile -Action act -Kind choose -ChoiceId $pick.id -ObservationId $cur.observationId -ActionId ([Guid]::NewGuid().ToString()) -Mode text | ConvertFrom-Json
if(-not $r.ok) { $r | ConvertTo-Json -Depth 6; throw ('Choose refused: '+$r.code) }
$obs=$r.observation
[pscustomobject]@{observationId=$obs.observationId;state=$obs.state;input=$obs.input.kind;requestId=$obs.input.requestId;totalLines=$obs.totalLines} | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
Write-Output ('chose value={0} label={1}' -f $pick.value,$pick.label)
'--- tail ---'
$rows=@($obs.lines);$start=[Math]::Max(0,$rows.Count-$Tail)
for($i=$start;$i -lt $rows.Count;$i++) { Write-Output ('{0,3}: {1}' -f $rows[$i].lineNo,$rows[$i].text) }
'--- buttons ---'
foreach($b in ($obs.buttons | Where-Object {$_.active})) { Write-Output ('{0} | {1} | value={2}' -f $b.id,$b.label,$b.value) }
