# Client for the local eraDSH prototype. Run in Windows PowerShell 5.1.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$SessionFile,
 [Parameter(Mandatory=$true)][ValidateSet('open','status','observe','act','advance','close')][string]$Action,
 [ValidateSet('text','visual','both','auto')][string]$Mode='text',
 [ValidateRange(1,200)][int]$LimitLines=80,
 [int]$FromLine=-1,
 [string]$ObservationId,
 [ValidateSet('choose','input','continue')][string]$Kind='input',
 [string]$Value='',
 [string]$ChoiceId,
 [string]$ActionId,
 [switch]$IncludeLines,
 [string]$SinceObservationId,
 [long]$SinceCursor = -1,
 [int]$MaxSteps = 20,
 [ValidateSet('filter','raw')][string]$Noise='filter',
 [ValidateSet('full','compact')][string]$Render='full',
 [string]$OutputPath
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$SessionFile=(Resolve-Path -LiteralPath $SessionFile).Path
$settings=Get-Content -LiteralPath $SessionFile -Raw -Encoding UTF8 | ConvertFrom-Json
$instancePath=Join-Path $settings.artifactDirectory 'bridge-instance.json'
function Assert-HostIdentity {
 if(-not(Test-Path -LiteralPath $instancePath)) { throw 'No instance record; run open first' }
 $instance=Get-Content -LiteralPath $instancePath -Raw -Encoding UTF8 | ConvertFrom-Json
 $p=Get-Process -Id $instance.processId -ErrorAction Stop
 if($p.Path -ne $settings.hostPath -or $p.StartTime.ToUniversalTime().ToString('o') -ne $instance.startTimeUtc) { throw 'Host process identity mismatch' }
 return $p
}
function Write-Response($Result) {
 $pretty=$Result | ConvertTo-Json -Depth 30
 if($OutputPath) {
  $target=[IO.Path]::GetFullPath($OutputPath)
  $base=[IO.Path]::GetFullPath($settings.artifactDirectory).TrimEnd('\')+'\'
  if(-not $target.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'OutputPath must be inside this session artifact directory' }
  $pretty | Set-Content -LiteralPath $target -Encoding UTF8
 }
 Write-Output $pretty
}
if($Action -eq 'open') {
 if(Test-Path -LiteralPath $instancePath) { throw 'Session already launched; use a fresh build session rather than reusing observation identity' }
 if(@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $settings.hostPath}).Count) { throw 'This runtime already has a bridge host' }
 if(-not ('EraDSH.DetachedLauncher' -as [type])) {
  Add-Type -Path (Join-Path $PSScriptRoot '..\bridge\DetachedLauncher.cs')
 }
 $environment=@{}
 $arguments=[string[]]@($SessionFile)
 $executable=$settings.hostPath
 $directory=$settings.runtimeDirectory
 if($settings.launchMode -eq 'startup-hook') {
  $executable=$settings.enginePath;$directory=$settings.gameRoot;$arguments=[string[]]@()
  $environment['DOTNET_STARTUP_HOOKS']=$settings.hookPath
  $environment['ERA_BRIDGE_SETTINGS']=$SessionFile
 }
 $receipt=[EraDSH.DetachedLauncher]::Start($executable,$arguments,$directory,$environment)
 [pscustomobject]@{processId=$receipt.ProcessId;startTimeUtc=$receipt.StartTimeUtc;hostPath=$settings.hostPath;sessionId=$settings.sessionId} | ConvertTo-Json | Set-Content -LiteralPath $instancePath -Encoding UTF8
 # A launch receipt is deliberately not an observation. Do not connect to IPC here:
 # neither a busy UI nor an absent startup hook may hold open's result hostage.
 Write-Response ([ordered]@{ok=$true;sessionId=$settings.sessionId;launched=$true;processId=$receipt.ProcessId;attached=$false;ready=$false;canAct=$false;pending=$true;phase='starting';launchMode=$settings.launchMode})
 return
}
if(Test-Path -LiteralPath $instancePath) {
 $record=Get-Content -LiteralPath $instancePath -Raw -Encoding UTF8 | ConvertFrom-Json
 if($record.sessionId -ne $settings.sessionId) { throw 'Instance record belongs to a different session' }
 $existing=Get-Process -Id $record.processId -ErrorAction SilentlyContinue
 if($null -eq $existing) {
  Write-Response ([ordered]@{ok=($Action -in @('status','close'));code='ENGINE_EXITED';status='already-exited';sessionId=$settings.sessionId;launched=$true;processId=$record.processId;exited=$true;attached=$false;ready=$false;canAct=$false;pending=$false;phase='exited';submitted=$false;actionId=$ActionId})
  return
 }
}
$p=Assert-HostIdentity
$request=[ordered]@{token=$settings.token;action=$Action;mode=$Mode;limitLines=$LimitLines}
if($IncludeLines) { $request.includeLines=$true }
if($SinceObservationId) { $request.sinceObservationId=$SinceObservationId }
if($SinceCursor -ge 0) { $request.sinceCursor=$SinceCursor }
if($Action -eq 'advance') { $request.maxSteps=$MaxSteps }
if($Noise -ne 'filter') { $request.noise=$Noise }
if($Render -ne 'full') { $request.render=$Render }
# One bounded host budget; the client transport has a small additional allowance.
$request.timeoutMs=if($Action -in @('act','advance')){15000}else{10000}
if($FromLine -ge 0) { $request.fromLine=$FromLine }
if($Action -eq 'act') {
 if([string]::IsNullOrWhiteSpace($ObservationId)) { throw 'act requires an observation ID' }
 if([string]::IsNullOrWhiteSpace($ActionId)) { $ActionId=[Guid]::NewGuid().ToString() }
 $request.observationId=$ObservationId;$request.kind=$Kind;$request.value=$Value;$request.choiceId=$ChoiceId;$request.actionId=$ActionId
}
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$settings.pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
$reader=$null;$writer=$null
try {
 try { $pipe.Connect(1500) } catch [TimeoutException] {
  if($Action -in @('observe','status')) {
   Write-Response ([ordered]@{ok=$true;sessionId=$settings.sessionId;launched=$true;processId=$p.Id;attached=$false;ready=$false;canAct=$false;pending=$true;phase='starting';reason='ipc-not-ready'})
   return
  }
  Write-Response ([ordered]@{ok=$false;code='NOT_READY';submitted=$false;pending=$true;canAct=$false;phase='starting';actionId=$ActionId;message='IPC is not ready; no input was sent'})
  return
 }
 # Authenticate the server process BEFORE sending this session's credential.
 if(-not ('EraDSH.PipePeer' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace EraDSH { public static class PipePeer {
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
 public static uint ServerPid(SafePipeHandle pipe) { uint pid;if(!GetNamedPipeServerProcessId(pipe,out pid))throw new Win32Exception(Marshal.GetLastWin32Error());return pid; }
} }
'@
 }
 $serverPid=[EraDSH.PipePeer]::ServerPid($pipe.SafePipeHandle)
 if($serverPid -ne $p.Id) { throw 'Named-pipe server PID does not match the launched host; no credential sent' }
 [void](Assert-HostIdentity)
 $encoding=[Text.UTF8Encoding]::new($false)
 $reader=[IO.StreamReader]::new($pipe,$encoding,$false,8192,$true)
 $writer=[IO.StreamWriter]::new($pipe,$encoding,8192,$true)
 $json=$request | ConvertTo-Json -Compress -Depth 8
 if($json.Length -gt 32768) { throw 'Request is too large' }
 $writer.WriteLine($json);$writer.Flush()
 $read=$reader.ReadLineAsync()
 $waitMs=if($settings.responseTimeoutSeconds){[Math]::Min(20000,[Math]::Max(1000,$settings.responseTimeoutSeconds*1000))}else{20000}
 if(-not $read.Wait($waitMs)) { throw ('Response timed out. Do not resend the action with a new actionId; actionId='+$ActionId) }
 $response=$read.Result
 if([string]::IsNullOrEmpty($response)) { throw 'Host closed without a response; inspect its artifacts, do not retry actions' }
 # Validate before publishing; no credential appears in normal results.
 Write-Response ($response | ConvertFrom-Json)
} finally {
 if($null -ne $writer) { $writer.Dispose() }
 if($null -ne $reader) { $reader.Dispose() }
 $pipe.Dispose()
}
