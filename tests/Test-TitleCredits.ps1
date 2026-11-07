# Supervised integration test for the isolated Maou title screen.
# It toggles the credits display (verified TITLE.ERB branch 9), writes ONLY isolated GLOBAL,
# and toggles back. Never use this test against an unrelated game/version.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SessionFile)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$settings=Get-Content -LiteralPath $SessionFile -Raw | ConvertFrom-Json
$client=Join-Path $PSScriptRoot '..\scripts\Invoke-Bridge.ps1'
$checks=New-Object System.Collections.Generic.List[object]
function Assert-Check([string]$Name,[bool]$Pass,$Details) {
 $checks.Add([pscustomobject]@{name=$Name;passed=$Pass;details=$Details})
 if(-not $Pass) { throw ('Integration assertion failed: '+$Name) }
}
function Request([hashtable]$Parameters,[string]$FileName) {
 $Parameters.SessionFile=$SessionFile
 $Parameters.OutputPath=Join-Path $settings.artifactDirectory $FileName
 $text=& $client @Parameters
 return ($text | ConvertFrom-Json)
}
try {
 $before=Request @{Action='observe';Mode='both'} 'test-before.json'
 Assert-Check 'engine_text_source' ($before.ok -and $before.source -eq 'engine-display-buffer') $before.source
 Assert-Check 'waiting_integer' ($before.state -eq 'WaitInput' -and $before.input.kind -eq 'IntValue') $before.input
 $choice=@($before.buttons | Where-Object {$_.value -eq '9'})
 Assert-Check 'known_credits_button' ($choice.Count -eq 1 -and $before.text -match '\u9996\u5e2d\u4ee3\u7801\u541b') @($choice | Select-Object id,value,label)
 $actionId=[Guid]::NewGuid().ToString()
 $collapsed=Request @{Action='act';Kind='choose';ChoiceId=$choice[0].id;ObservationId=$before.observationId;ActionId=$actionId;Mode='both'} 'test-collapse.json'
 Assert-Check 'engine_consumed_choice' ($collapsed.ok -and $collapsed.submitted -and $collapsed.observationChanged) @{submitted=$collapsed.submitted;changed=$collapsed.observationChanged}
 Assert-Check 'credits_actually_collapsed' ($collapsed.observation.text -notmatch '\u9996\u5e2d\u4ee3\u7801\u541b' -and $collapsed.observation.rawTextLength -lt $before.rawTextLength) @{beforeLines=$before.totalLines;afterLines=$collapsed.observation.totalLines}
 $duplicate=Request @{Action='act';Kind='choose';ChoiceId=$choice[0].id;ObservationId=$before.observationId;ActionId=$actionId} 'test-duplicate.json'
 Assert-Check 'duplicate_action_refused' (-not $duplicate.ok -and $duplicate.code -eq 'DUPLICATE_ACTION') $duplicate.code
 $equivalent=([Guid]$actionId).ToString('N').ToUpperInvariant()
 $formatted=Request @{Action='act';Kind='input';Value='9';ObservationId=$collapsed.observation.observationId;ActionId=$equivalent} 'test-duplicate-formatted.json'
 Assert-Check 'equivalent_guid_format_refused' (-not $formatted.ok -and $formatted.code -eq 'DUPLICATE_ACTION') $formatted.code
 $stale=Request @{Action='act';Kind='input';Value='9';ObservationId=$before.observationId;ActionId=[Guid]::NewGuid().ToString()} 'test-stale.json'
 Assert-Check 'stale_observation_refused' (-not $stale.ok -and $stale.code -eq 'STALE_OBSERVATION') $stale.code
 $stable=Request @{Action='observe';Mode='text'} 'test-after-rejections.json'
 Assert-Check 'rejections_have_no_game_effect' ($stable.observationId -ceq $collapsed.observation.observationId) $stable.observationId
 $page=Request @{Action='observe';Mode='text';LimitLines=3;FromLine=0} 'test-page.json'
 Assert-Check 'bounded_paging' ($page.returnedLines -eq 3 -and $page.omittedAfter -gt 0 -and $page.observationId -ceq $stable.observationId) @{returned=$page.returnedLines;omittedAfter=$page.omittedAfter}
 $wrong=Request @{Action='act';Kind='continue';ObservationId=$stable.observationId;ActionId=[Guid]::NewGuid().ToString()} 'test-wrong-kind.json'
 Assert-Check 'input_type_guard' (-not $wrong.ok -and $wrong.code -eq 'WRONG_INPUT_KIND') $wrong.code
 $debug=Request @{Action='act';Kind='input';Value='@OUTPUT';ObservationId=$stable.observationId;ActionId=[Guid]::NewGuid().ToString()} 'test-debug-denied.json'
 Assert-Check 'arbitrary_debug_input_denied' (-not $debug.ok -and $debug.code -eq 'INVALID_INPUT') $debug.code
 $restoreChoice=@($stable.buttons | Where-Object {$_.value -eq '9'})
 $restored=Request @{Action='act';Kind='choose';ChoiceId=$restoreChoice[0].id;ObservationId=$stable.observationId;ActionId=[Guid]::NewGuid().ToString();Mode='both'} 'test-restored.json'
 Assert-Check 'credits_restored' ($restored.ok -and $restored.observation.text -match '\u9996\u5e2d\u4ee3\u7801\u541b') @{lines=$restored.observation.totalLines;request=$restored.observation.input.requestId}
 $result=[pscustomobject]@{passed=$true;checks=@($checks.ToArray());sessionId=$settings.sessionId;isolatedGlobalFileCreated=(Test-Path -LiteralPath (Join-Path $settings.runtimeDirectory 'Sav\global.sav'));screenshots=@($before.screenshot.path,$collapsed.observation.screenshot.path,$restored.observation.screenshot.path)}
 $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $settings.artifactDirectory 'integration-results.json') -Encoding UTF8
 $result | ConvertTo-Json -Depth 10
} catch {
 [pscustomobject]@{passed=$false;checks=@($checks.ToArray());error=$_.Exception.Message} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $settings.artifactDirectory 'integration-results.json') -Encoding UTF8
 throw
}
