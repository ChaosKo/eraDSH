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
 [string]$OutputPath
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$SessionFile=(Resolve-Path -LiteralPath $SessionFile).Path
$settings=Get-Content -LiteralPath $SessionFile -Raw | ConvertFrom-Json
$instancePath=Join-Path $settings.artifactDirectory 'bridge-instance.json'
function Assert-HostIdentity {
 if(-not(Test-Path -LiteralPath $instancePath)) { throw 'No instance record; run open first' }
 $instance=Get-Content -LiteralPath $instancePath -Raw | ConvertFrom-Json
 $p=Get-Process -Id $instance.processId -ErrorAction Stop
 if($p.Path -ne $settings.hostPath -or $p.StartTime.ToUniversalTime().ToString('o') -ne $instance.startTimeUtc) { throw 'Host process identity mismatch' }
 return $p
}
if($Action -eq 'open') {
 if(Test-Path -LiteralPath $instancePath) { throw 'Session already launched; use a fresh build session rather than reusing observation identity' }
 if(@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $settings.hostPath}).Count) { throw 'This runtime already has a bridge host' }
 $quoted='"'+$SessionFile+'"'
 if($settings.launchMode -eq 'startup-hook') {
  # The engine is a bundled .NET app: start it as itself, with the adapter injected
  # through DOTNET_STARTUP_HOOKS so its apphost, dependencies and paths stay native.
  $psi=New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName=$settings.enginePath
  $psi.WorkingDirectory=$settings.gameRoot
  $psi.UseShellExecute=$false
  $psi.EnvironmentVariables['DOTNET_STARTUP_HOOKS']=$settings.hookPath
  $psi.EnvironmentVariables['ERA_BRIDGE_SETTINGS']=$SessionFile
  $p=[System.Diagnostics.Process]::Start($psi)
 } else {
  $p=Start-Process -FilePath $settings.hostPath -ArgumentList $quoted -WorkingDirectory $settings.runtimeDirectory -PassThru
 }
 [pscustomobject]@{processId=$p.Id;startTimeUtc=$p.StartTime.ToUniversalTime().ToString('o');hostPath=$settings.hostPath;sessionId=$settings.sessionId} | ConvertTo-Json | Set-Content -LiteralPath $instancePath -Encoding UTF8
} else { $p=Assert-HostIdentity }
$request=[ordered]@{token=$settings.token;action=$Action;mode=$Mode;limitLines=$LimitLines}
if($IncludeLines) { $request.includeLines=$true }
if($SinceObservationId) { $request.sinceObservationId=$SinceObservationId }
if($SinceCursor -ge 0) { $request.sinceCursor=$SinceCursor }
if($Action -eq 'advance') { $request.maxSteps=$MaxSteps }
if($Noise -ne 'filter') { $request.noise=$Noise }
if($Action -eq 'open') { $request.action='observe' }
if($FromLine -ge 0) { $request.fromLine=$FromLine }
if($Action -eq 'act') {
 if([string]::IsNullOrWhiteSpace($ObservationId)) { throw 'act requires an observation ID' }
 if([string]::IsNullOrWhiteSpace($ActionId)) { $ActionId=[Guid]::NewGuid().ToString() }
 $request.observationId=$ObservationId;$request.kind=$Kind;$request.value=$Value;$request.choiceId=$ChoiceId;$request.actionId=$ActionId
}
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$settings.pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
$reader=$null;$writer=$null
try {
 $pipe.Connect(8000)
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
 $waitMs=if($settings.responseTimeoutSeconds){$settings.responseTimeoutSeconds*1000}else{40000}
 if(-not $read.Wait($waitMs)) { throw ('Response timed out. Do not resend the action; actionId='+$ActionId) }
 $response=$read.Result
 if([string]::IsNullOrEmpty($response)) { throw 'Host closed without a response; inspect its artifacts, do not retry actions' }
 # Validate before publishing; no credential appears in normal results.
 $parsed=$response | ConvertFrom-Json
 $pretty=$parsed | ConvertTo-Json -Depth 30
 if($OutputPath) {
  $target=[IO.Path]::GetFullPath($OutputPath)
  $base=[IO.Path]::GetFullPath($settings.artifactDirectory).TrimEnd('\')+'\'
  if(-not $target.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'OutputPath must be inside this session artifact directory' }
  $pretty | Set-Content -LiteralPath $target -Encoding UTF8
 }
 Write-Output $pretty
} finally {
 if($null -ne $writer) { $writer.Dispose() }
 if($null -ne $reader) { $reader.Dispose() }
 $pipe.Dispose()
}
