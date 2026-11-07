# P0-only menu investigation. Not a general game action API.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][ValidateSet('open-file','observe','dismiss')][string]$Action,
 [Parameter(Mandatory=$true)][string]$RunDirectory
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'NativeWindow.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Common.ps1')
$enginePath=(Get-EraDshSettings).EnginePath
$identity=Get-Content -LiteralPath (Join-Path $RunDirectory 'instance.json') -Raw | ConvertFrom-Json
$p=Get-Process -Id $identity.processId
if($p.Path -ne $enginePath -or $p.StartTime.ToUniversalTime().ToString('o') -ne $identity.startTimeUtc) { throw 'Instance identity mismatch' }
$h=$p.MainWindowHandle
if(-not [EraProbe.Native]::IsWindow($h)) { throw 'Main window unavailable' }
$stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')
$prefix=Join-Path $RunDirectory ('menu-'+$Action+'-'+$stamp)
if($Action -eq 'open-file') {
 if([EraProbe.Native]::ReadText($h) -cne 'ERA_MAOU_EX 0.92') { throw 'Unexpected title' }
 $menus=@([EraProbe.Native]::Children($h) | Where-Object {$_.Text -eq 'menuStrip1' -and $_.Visible -and $_.Enabled})
 if($menus.Count -ne 1) { throw 'Expected one menu strip' }
 $menu=$menus[0]
 if($menu.Width -lt 500 -or $menu.Height -lt 20) { throw 'Unexpected menu geometry' }
 # First menu location verified from the title-screen screenshot.
 $x=[int][Math]::Round($menu.Height); $y=[int][Math]::Round($menu.Height/2)
 $lp=[IntPtr](($y -shl 16) -bor $x)
 $down=[EraProbe.Native]::PostMessage([IntPtr]$menu.Handle,0x0201,[IntPtr]1,$lp)
 $up=[EraProbe.Native]::PostMessage([IntPtr]$menu.Handle,0x0202,[IntPtr]0,$lp)
 $result=[pscustomobject]@{action=$Action;processId=$p.Id;menuHandle=$menu.Handle;x=$x;y=$y;downQueued=$down;upQueued=$up;effectVerified=$false}
} elseif($Action -eq 'dismiss') {
 $targets=@([EraProbe.Native]::TopWindows([uint32]$p.Id) | Where-Object {$_.Handle -ne $h.ToInt64()})
 $targets+=@([EraProbe.Native]::Children($h) | Where-Object {$_.Text -eq 'menuStrip1'})
 foreach($t in $targets) {
  [void][EraProbe.Native]::PostMessage([IntPtr]$t.Handle,0x0100,[IntPtr]27,[IntPtr]0x00010001)
  [void][EraProbe.Native]::PostMessage([IntPtr]$t.Handle,0x0101,[IntPtr]27,[IntPtr](-1073676287))
 }
 [void][EraProbe.Native]::PostMessage($h,0x001F,[IntPtr]0,[IntPtr]0)
 $result=[pscustomobject]@{action=$Action;processId=$p.Id;targets=@($targets | Select-Object Handle,ClassName);effectVerified=$false}
} else {
 $tops=@([EraProbe.Native]::TopWindows([uint32]$p.Id))
 $rows=@()
 foreach($t in $tops) {
  $capture=$null; $captureError=$null
  try { $capture=[EraProbe.Native]::Capture([IntPtr]$t.Handle,($prefix+'-'+$t.Handle+'-window.png'),($prefix+'-'+$t.Handle+'-client.png')) } catch { $captureError=$_.Exception.Message }
  $rows+=[pscustomobject]@{window=$t;uia=(Get-EraAutomationTree ([IntPtr]$t.Handle));capture=$capture;captureError=$captureError}
 }
 $result=[pscustomobject]@{action=$Action;processId=$p.Id;windows=$rows}
}
$result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath ($prefix+'.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 12
