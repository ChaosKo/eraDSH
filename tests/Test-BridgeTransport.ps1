# Transport rejection tests against the already running, isolated host.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SessionFile)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$settings=Get-Content -LiteralPath $SessionFile -Raw | ConvertFrom-Json
$client=Join-Path $PSScriptRoot '..\scripts\Invoke-Bridge.ps1'
$checks=New-Object System.Collections.Generic.List[object]
function Assert-Test([string]$Name,[bool]$Pass) { $checks.Add([pscustomobject]@{name=$Name;passed=$Pass});if(-not $Pass){throw ('Transport assertion failed: '+$Name)} }
function Send-Raw([string]$Line) {
 $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$settings.pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
 $reader=$null;$writer=$null
 try {
  $pipe.Connect(8000);$utf8=[Text.UTF8Encoding]::new($false)
  $reader=[IO.StreamReader]::new($pipe,$utf8,$false,8192,$true);$writer=[IO.StreamWriter]::new($pipe,$utf8,8192,$true)
  $write=$writer.WriteLineAsync($Line);if(-not $write.Wait(8000)){throw 'Raw test write timed out'}
  $flush=$writer.FlushAsync();if(-not $flush.Wait(8000)){throw 'Raw test flush timed out'}
  $read=$reader.ReadLineAsync();if(-not $read.Wait(15000)){throw 'Raw test response timed out'}
  return ($read.Result | ConvertFrom-Json)
 } finally {if($writer){$writer.Dispose()};if($reader){$reader.Dispose()};$pipe.Dispose()}
}
$wrong=Send-Raw '{"token":"deliberately-invalid","action":"close"}'
Assert-Test 'invalid_credential_close_rejected' (-not $wrong.ok -and $wrong.code -eq 'UNAUTHORIZED')
$status=(& $client -SessionFile $SessionFile -Action status) | ConvertFrom-Json
Assert-Test 'host_survives_invalid_credential' ($status.ok -and $status.ready)
$large=Send-Raw ('a'*33000)
Assert-Test 'oversized_request_rejected' (-not $large.ok -and $large.code -eq 'REQUEST_ERROR')
$status=(& $client -SessionFile $SessionFile -Action status) | ConvertFrom-Json
Assert-Test 'host_survives_oversized_request' ($status.ok -and $status.ready)
$result=[pscustomobject]@{passed=$true;checks=@($checks.ToArray())}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $settings.artifactDirectory 'transport-results.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 8
