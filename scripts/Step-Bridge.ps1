# Compact step helper for supervised interface tests.
# Prints only what a decision needs; full JSON stays in the artifact directory.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$SessionFile,
 [Parameter(Mandatory=$true)][ValidateSet('observe','choose','input','continue')][string]$Do,
 [string]$ChoiceId,
 [string]$Value,
 [int]$Tail=10
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$client=Join-Path $PSScriptRoot 'Invoke-Bridge.ps1'
$settings=Get-Content -LiteralPath $SessionFile -Raw | ConvertFrom-Json
$statePath=Join-Path $settings.artifactDirectory 'step-state.json'
if($Do -eq 'observe') {
 $r=& $client -SessionFile $SessionFile -Action observe -Mode text -LimitLines 200 | ConvertFrom-Json
} else {
 if(-not(Test-Path -LiteralPath $statePath)) { throw 'No recorded observation; run observe first' }
 $state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
 $params=@{SessionFile=$SessionFile;Action='act';ObservationId=$state.observationId;ActionId=[Guid]::NewGuid().ToString();Mode='text'}
 if($Do -eq 'choose') { $params.Kind='choose';$params.ChoiceId=$ChoiceId }
 elseif($Do -eq 'input') { $params.Kind='input';$params.Value=$Value }
 else { $params.Kind='continue' }
 $r=& $client @params | ConvertFrom-Json
 if(-not $r.ok) { $r | ConvertTo-Json -Depth 8; throw ('Step refused: '+$r.code) }
 $r=$r.observation
}
$r | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $settings.artifactDirectory ('step-'+[DateTime]::UtcNow.ToString('HHmmssfff')+'.json')) -Encoding UTF8
[pscustomobject]@{observationId=$r.observationId;state=$r.state;input=$r.input.kind;requestId=$r.input.requestId;totalLines=$r.totalLines} | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
'--- tail ---'
$rows=@($r.lines)
$start=[Math]::Max(0,$rows.Count-$Tail)
for($i=$start;$i -lt $rows.Count;$i++) { Write-Output ('{0,3}: {1}' -f $rows[$i].lineNo,$rows[$i].text) }
'--- buttons ---'
foreach($b in $r.buttons) { Write-Output ('{0} | {1} | value={2} gen={3}' -f $b.id,$b.label,$b.value,$b.generation) }
