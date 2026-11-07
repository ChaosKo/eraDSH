# Windows PowerShell 5.1; no global keyboard injection or desktop capture.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][ValidateSet('prepare','launch','observe','edit','enter','close','verify')][string]$Mode,
 [Parameter(Mandatory=$true)][string]$RunDirectory
)
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Common.ps1')
$bridgeSettings=Get-EraDshSettings
$devRoot=$bridgeSettings.RepositoryRoot
$gameRoot=$bridgeSettings.GameRoot
$expectedExe=$bridgeSettings.EnginePath
$run=[IO.Path]::GetFullPath($RunDirectory)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $devRoot 'artifacts'))
if (-not $run.StartsWith($artifactRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Run directory must be inside the repository artifacts directory' }
$baselinePath=Join-Path $run 'baseline.json'
$instancePath=Join-Path $run 'instance.json'
function Save-Json($Value,[string]$Path) { $Value | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Get-Manifest {
 $files=@(Get-ChildItem -LiteralPath $gameRoot -File | Where-Object {$_.Extension -in @('.config','.log')})
 foreach($name in @('Sav','Debug')) { $path=Join-Path $gameRoot $name; if(Test-Path -LiteralPath $path) { $files+=@(Get-ChildItem -LiteralPath $path -File -Recurse -Force) } }
 @($files | Sort-Object FullName -Unique | ForEach-Object { [pscustomobject]@{path=$_.FullName.Substring($gameRoot.Length+1);length=$_.Length;lastWriteUtc=$_.LastWriteTimeUtc.ToString('o');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
}
function Get-OwnedProcess {
 if(-not(Test-Path -LiteralPath $instancePath)) { throw 'No launched instance recorded' }
 $identity=Get-Content -LiteralPath $instancePath -Raw | ConvertFrom-Json
 $p=Get-Process -Id $identity.processId -ErrorAction Stop
 if($p.Path -ne $expectedExe -or $p.StartTime.ToUniversalTime().ToString('o') -ne $identity.startTimeUtc) { throw 'Process identity mismatch' }
 return $p
}
function Get-MainHandle($Process) {
 $Process.Refresh()
 $h=$Process.MainWindowHandle
 if($h -eq [IntPtr]::Zero) { throw 'No main window handle' }
 [uint32]$ownerId=0
 [void][EraProbe.Native]::GetWindowThreadProcessId($h,[ref]$ownerId)
 if($ownerId -ne $Process.Id) { throw 'Window owner mismatch' }
 return $h
}
function Get-SingleEdit($Process,[IntPtr]$MainHandle) {
 $tops=@([EraProbe.Native]::TopWindows([uint32]$Process.Id))
 if($tops.Count -ne 1 -or -not [EraProbe.Native]::IsWindowEnabled($MainHandle)) { throw 'Modal or multiple top-level windows; input refused' }
 $edits=@([EraProbe.Native]::Children($MainHandle) | Where-Object {($_.ClassName -like 'WindowsForms10.EDIT*' -or $_.ClassName -like 'WindowsForms10.RichEdit*') -and $_.Visible -and $_.Enabled})
 if($edits.Count -ne 1) { throw ('Expected exactly one enabled WinForms Edit/RichEdit; found '+$edits.Count) }
 return [IntPtr]$edits[0].Handle
}
if($Mode -eq 'prepare') {
 if(Test-Path -LiteralPath $run) { throw 'Run directory already exists; use a fresh one' }
 [void](New-Item -ItemType Directory -Path $run)
 $backup=Join-Path $run 'root-backup'; [void](New-Item -ItemType Directory -Path $backup)
 $existing=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $expectedExe} | Select-Object ProcessId,Name,ExecutablePath)
 if($existing.Count -gt 0) { throw 'Existing target process detected; refusing launch test' }
 $manifest=@(Get-Manifest)
 foreach($file in $manifest | Where-Object {$_.path -notmatch '[\\/]'}) { Copy-Item -LiteralPath (Join-Path $gameRoot $file.path) -Destination (Join-Path $backup $file.path) }
 Save-Json ([pscustomobject]@{recordedUtc=[DateTime]::UtcNow.ToString('o');gameRoot=$gameRoot;exe=$expectedExe;exeSha256=(Get-FileHash -LiteralPath $expectedExe -Algorithm SHA256).Hash;fileVersion=(Get-Item -LiteralPath $expectedExe).VersionInfo.FileVersion;existingProcesses=$existing;manifest=$manifest;scope='Root .config/.log plus recursive Sav and Debug; not a complete game-directory snapshot'}) $baselinePath
 [pscustomobject]@{mode=$Mode;baseline=$baselinePath;files=$manifest.Count;saveFiles=@($manifest | Where-Object {$_.path -like 'Sav\*'}).Count} | ConvertTo-Json
 exit 0
}
if(-not(Test-Path -LiteralPath $baselinePath)) { throw 'Run prepare first' }
if($Mode -eq 'verify') {
 $baseline=Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
 $after=@(Get-Manifest); $changes=New-Object System.Collections.Generic.List[object]
 $beforeByPath=@{}; foreach($f in $baseline.manifest) { $beforeByPath[$f.path]=$f }
 $afterByPath=@{}; foreach($f in $after) { $afterByPath[$f.path]=$f }
 foreach($path in @(@($beforeByPath.Keys)+@($afterByPath.Keys) | Sort-Object -Unique)) {
  $b=$beforeByPath[$path]; $a=$afterByPath[$path]
  if($null -eq $b -or $null -eq $a -or $b.sha256 -ne $a.sha256 -or $b.lastWriteUtc -ne $a.lastWriteUtc) { $changes.Add([pscustomobject]@{path=$path;contentChanged=($null -eq $b -or $null -eq $a -or $b.sha256 -ne $a.sha256);before=$b;after=$a}) }
 }
 $result=[pscustomobject]@{verifiedUtc=[DateTime]::UtcNow.ToString('o');changes=@($changes.ToArray());saveContentChanges=@($changes.ToArray() | Where-Object {$_.path -like 'Sav\*' -and $_.contentChanged}).Count;manifest=$after;remainingTargetProcesses=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $expectedExe} | Select-Object ProcessId,ExecutablePath)}
 $name='verification-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')+'.json'; Save-Json $result (Join-Path $run $name)
 $result | Select-Object verifiedUtc,changes,saveContentChanges,remainingTargetProcesses | ConvertTo-Json -Depth 8
 exit 0
}
. (Join-Path $PSScriptRoot 'NativeWindow.ps1')
if($Mode -eq 'launch') {
 if(Test-Path -LiteralPath $instancePath) { throw 'Instance record exists; refusing duplicate launch' }
 if(@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $expectedExe}).Count -gt 0) { throw 'Target already running' }
 $prior=[EraProbe.Native]::GetForegroundWindow().ToInt64()
 $p=Start-Process -FilePath $expectedExe -WorkingDirectory $gameRoot -PassThru
 Save-Json ([pscustomobject]@{processId=$p.Id;startTimeUtc=$p.StartTime.ToUniversalTime().ToString('o');exe=$expectedExe;priorForegroundHandle=$prior;launchedUtc=[DateTime]::UtcNow.ToString('o')}) $instancePath
 $idle=$false
 try { $idle=$p.WaitForInputIdle(20000) } catch { }
 $p.Refresh()
 $clock=[Diagnostics.Stopwatch]::StartNew()
 while(-not $p.HasExited -and $p.MainWindowHandle -eq [IntPtr]::Zero -and $clock.ElapsedMilliseconds -lt 20000) { Start-Sleep -Milliseconds 100; $p.Refresh() }
 [pscustomobject]@{mode=$Mode;processId=$p.Id;exited=$p.HasExited;inputIdle=$idle;windowReady=($p.MainWindowHandle -ne [IntPtr]::Zero);mainWindowHandle=$p.MainWindowHandle.ToInt64();mainWindowTitle=$p.MainWindowTitle;identity=$instancePath} | ConvertTo-Json
 exit 0
}
$p=Get-OwnedProcess
$h=Get-MainHandle $p
if($Mode -eq 'observe') {
 $stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'); $prefix=Join-Path $run ('observe-'+$stamp)
 $before=[EraProbe.Native]::Describe($h)
 $tops=@([EraProbe.Native]::TopWindows([uint32]$p.Id))
 $children=@([EraProbe.Native]::Children($h))
 $uia=Get-EraAutomationTree $h
 $capture=$null; $captureError=$null
 try { $capture=[EraProbe.Native]::Capture($h,$prefix+'-window.png',$prefix+'-client.png') } catch { $captureError=$_.Exception.Message }
 $after=[EraProbe.Native]::Describe($h)
 $snapshot=[pscustomobject]@{observationId=('p0-'+$stamp);capturedUtc=[DateTime]::UtcNow.ToString('o');processId=$p.Id;source='external-gui-probe';atomic=$false;inputState='unknown';before=$before;after=$after;topWindows=$tops;children=$children;uia=$uia;capture=$capture;captureError=$captureError}
 Save-Json $snapshot ($prefix+'.json')
 $snapshot | Select-Object observationId,processId,before,children,uia,capture,captureError | ConvertTo-Json -Depth 10
 exit 0
}
if($Mode -eq 'edit') {
 $edit=Get-SingleEdit $p $h; $before=[EraProbe.Native]::ReadText($edit); $during=$null; $restored=$false; $accepted=$false
 if($null -eq $before) { throw 'Cannot read input before modification' }
 try {
  $accepted=[EraProbe.Native]::SetText($edit,'731')
  $during=[EraProbe.Native]::ReadText($edit)
 } finally {
  $restoreAccepted=[EraProbe.Native]::SetText($edit,$before)
  $restored=($restoreAccepted -and [EraProbe.Native]::ReadText($edit) -ceq $before)
 }
 $result=[pscustomobject]@{mode=$Mode;processId=$p.Id;editHandle=$edit.ToInt64();before=$before;during=$during;messageAccepted=$accepted;textMatched=($during -ceq '731');restored=$restored;submitted=$false;mechanism='WM_SETTEXT with bounded SendMessageTimeout; no Enter sent'}
 Save-Json $result (Join-Path $run 'edit-test.json'); $result | ConvertTo-Json
 if(-not $restored) { throw 'Input restoration failed' }
 exit 0
}
if($Mode -eq 'enter') {
 $edit=Get-SingleEdit $p $h
 if([EraProbe.Native]::ReadText($edit) -cne '') { throw 'Nonempty input; refusing Enter' }
 $down=[EraProbe.Native]::PostMessage($edit,0x0100,[IntPtr]13,[IntPtr]0x001C0001)
 $up=[EraProbe.Native]::PostMessage($edit,0x0101,[IntPtr]13,[IntPtr](-1071906815))
 $result=[pscustomobject]@{mode=$Mode;processId=$p.Id;editHandle=$edit.ToInt64();keyDownQueued=$down;keyUpQueued=$up;effectVerified=$false;note='Must observe separately; no automatic retry'}
 Save-Json $result (Join-Path $run ('enter-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')+'.json')); $result | ConvertTo-Json
 exit 0
}
if($Mode -eq 'close') {
 $requested=$p.CloseMainWindow(); $exited=$p.WaitForExit(8000)
 $result=[pscustomobject]@{mode=$Mode;processId=$p.Id;normalCloseRequested=$requested;exited=$exited;forced=$false}
 Save-Json $result (Join-Path $run 'close.json'); $result | ConvertTo-Json
 if(-not $exited) { throw 'Normal close did not finish; no force-kill attempted' }
 exit 0
}
