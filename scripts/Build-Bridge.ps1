# Build the bridge host with the installed .NET Framework compiler.
# No downloads and no package installation; the compiler ships with Windows.
#
# Two runtime modes:
#   inplace - the default. Nothing is copied: the engine is loaded from the game
#             folder itself and runs with that folder as its working directory, which
#             is what a game with heavy assets needs. The game then writes its own
#             Sav/Debug/Dat, logs and configuration into the original directory.
#   copy    - the game folder is mirrored under runtime/<name>/ and the engine is
#             loaded from there, so a bridge session cannot touch the original saves.
[CmdletBinding()]
param(
 [string]$GameRoot,
 [string]$EngineExecutable,
 [string]$EngineAssemblyVersion,
 [string]$RuntimeDirectory,
 [ValidateSet('copy','inplace')][string]$RuntimeMode,
 [string]$ConfigPath,
 [switch]$Recreate,
 # inplace builds run the real folder, so the first snapshot of its Sav is kept under
 # the plugin state directory. `-RefreshSavBackup` forces a new one, `-NoSavBackup` skips.
 [switch]$RefreshSavBackup,
 [switch]$NoSavBackup
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'Common.ps1')

$settings=Get-EraDshSettings -GameRoot $GameRoot -EngineExecutable $EngineExecutable `
  -EngineAssemblyVersion $EngineAssemblyVersion -RuntimeDirectory $RuntimeDirectory `
  -RuntimeMode $RuntimeMode -ConfigPath $ConfigPath
$repositoryRoot=$settings.RepositoryRoot
$mode=$settings.RuntimeMode
$runtime=$settings.RuntimePath
$sourceEngine=$settings.EnginePath
$engineName=[IO.Path]::GetFileName($sourceEngine)
# copy mode loads a mirrored copy of the engine; inplace mode loads the game's own file.
$engine=if($mode -eq 'inplace') { $sourceEngine } else { Join-Path $runtime $engineName }
$hostPath=Join-Path $runtime 'EraBridgeHost.exe'
$markerPath=Join-Path $runtime '.erabridge-runtime.json'

# An inplace session writes the game's own Sav, and a mistake there is unrecoverable:
# a moved-aside directory that nobody restores, a cleanup between runs, a crash. Keep
# one snapshot per distinct state of that folder outside the game, then reuse it, so
# protection costs disk once instead of on every build.
function Save-EraDshSavSnapshot {
 param([string]$GameRoot,[switch]$Force,[switch]$Skip)
 if($Skip) { return $null }
 $sav=Join-Path $GameRoot 'Sav'
 if(-not(Test-Path -LiteralPath $sav -PathType Container)) { return $null }
 $files=@(Get-ChildItem -LiteralPath $sav -File -ErrorAction SilentlyContinue)
 if($files.Count -eq 0) { return $null }
 $bytes=($files | Measure-Object -Property Length -Sum).Sum
 if($bytes -gt 2GB) { Write-Warning ('Sav snapshot skipped: ' + [math]::Round($bytes/1MB) + ' MB exceeds the 2 GB snapshot limit'); return $null }
 $stateRoot=if($env:DSH_HOME){$env:DSH_HOME}else{Join-Path $env:USERPROFILE '.dsh'}
 $leaf=Split-Path -Leaf $GameRoot
 $seed=[Security.Cryptography.SHA256]::Create()
 try { $digest=$seed.ComputeHash([Text.Encoding]::UTF8.GetBytes($GameRoot.ToLowerInvariant())) } finally { $seed.Dispose() }
 $key=$leaf+'-'+([BitConverter]::ToString($digest,0,4).Replace('-','').ToLowerInvariant())
 $dir=Join-Path (Join-Path (Join-Path $stateRoot 'era-bridge') 'sav-snapshots') $key
 $markerPath=Join-Path $dir 'snapshot.json'
 $latest=($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc.Ticks
 $fingerprint=('{0}:{1}:{2}' -f $files.Count,$bytes,$latest)
 if(-not $Force -and (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
  try { $marker=Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $marker=$null }
  if($marker -and $marker.fingerprint -eq $fingerprint -and (Test-Path -LiteralPath (Join-Path $dir 'sav') -PathType Container)) {
   return [pscustomobject]@{ path=(Join-Path $dir 'sav'); fileCount=$files.Count; bytes=$bytes; reused=$true; capturedUtc=$marker.capturedUtc }
  }
 }
 [void](New-Item -ItemType Directory -Path $dir -Force)
 $target=Join-Path $dir 'sav'
 if(Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
 [void](New-Item -ItemType Directory -Path $target)
 foreach($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $file.Name) -Force }
 $captured=[DateTime]::UtcNow.ToString('o')
 [ordered]@{ gameRoot=$GameRoot; fingerprint=$fingerprint; fileCount=$files.Count; bytes=$bytes; capturedUtc=$captured } | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
 [pscustomobject]@{ path=$target; fileCount=$files.Count; bytes=$bytes; reused=$false; capturedUtc=$captured }
}

if(@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $hostPath}).Count) { throw 'Bridge host is running; close it before rebuilding' }

$sourceHash=(Get-FileHash -LiteralPath $sourceEngine -Algorithm SHA256).Hash.ToLowerInvariant()

# ---------------------------------------------------------------------------
# Bundled .NET build (Emuera.NET / Lazyloading EE): the exe carries its assemblies
# inside an apphost, so reflection cannot load it and no mirror is useful. The
# adapter is injected into the engine's own process as a startup hook instead.
# ---------------------------------------------------------------------------
if($settings.EngineKind -eq 'core') {
 $hookProject=Join-Path $repositoryRoot 'bridge\net8\EraBridgeHook'
 $hookPath=Join-Path $hookProject 'bin\Release\net8.0-windows\EraBridgeHook.dll'
 if(@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $sourceEngine}).Count) {
  throw ('The engine is already running from ' + $sourceEngine + '; close it before rebuilding')
 }
 $hookStampPath=Join-Path $hookProject 'bin\Release\net8.0-windows\bridge-source.sha256'
 $hookSourceStamp=((Get-FileHash -LiteralPath (Join-Path $hookProject 'StartupHook.cs') -Algorithm SHA256).Hash+':'+(Get-FileHash -LiteralPath (Join-Path $hookProject 'EraBridgeHook.csproj') -Algorithm SHA256).Hash)
 $previousStamp=if(Test-Path -LiteralPath $hookStampPath){[IO.File]::ReadAllText($hookStampPath)}else{''}
 if(-not(Test-Path -LiteralPath $hookPath -PathType Leaf) -or $previousStamp -ne $hookSourceStamp) {
  $dotnet=Get-Command dotnet -ErrorAction SilentlyContinue
  if(-not $dotnet) {
   throw ('This engine is a bundled .NET build and needs the .NET SDK once to build its adapter. Install the .NET SDK, or build ' + $hookProject + ' elsewhere and place EraBridgeHook.dll at ' + $hookPath)
  }
  Write-Output 'building the .NET 8 startup-hook adapter (first use or source changed)'
  & dotnet build $hookProject -c Release --nologo -v q | Out-Host
  if($LASTEXITCODE -ne 0 -or -not(Test-Path -LiteralPath $hookPath -PathType Leaf)) { throw 'Failed to build the .NET 8 startup-hook adapter' }
  [IO.File]::WriteAllText($hookStampPath,$hookSourceStamp)
 }
 if(@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $sourceEngine}).Count) {
  throw ('The engine is already running from ' + $sourceEngine + '; close it before rebuilding')
 }
 if($mode -ne 'inplace') {
  Write-Warning 'A bundled .NET engine always runs from the game folder; copy mode does not apply and inplace was used.'
  $mode='inplace'
 }
 $runtime=Join-Path $repositoryRoot 'runtime\host-core'
 $hostPath=$sourceEngine
 $markerPath=Join-Path $runtime '.erabridge-runtime.json'
 [void](New-Item -ItemType Directory -Path $runtime -Force)
 [ordered]@{
  runtimeMode='inplace'
  engineFamily='core'
  launchMode='startup-hook'
  gameRoot=$settings.GameRoot
  engineExecutable=$engineName
  engineSha256=$sourceHash
  hookPath=$hookPath
  createdUtc=[DateTime]::UtcNow.ToString('o')
  bridgeVersion='0.3.0'
 } | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8

 $sessionId=[Guid]::NewGuid().ToString('N')
 $artifact=Join-Path $repositoryRoot ('artifacts\bridge-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+$sessionId.Substring(0,8))
 [void](New-Item -ItemType Directory -Path $artifact)
 $savBackup=Save-EraDshSavSnapshot -GameRoot $settings.GameRoot -Force:$RefreshSavBackup -Skip:$NoSavBackup
 if($savBackup) { Write-Output ('Sav snapshot: ' + $savBackup.path + ' (' + $savBackup.fileCount + ' files, ' + [math]::Round($savBackup.bytes/1MB,1) + ' MB, reused=' + $savBackup.reused.ToString().ToLowerInvariant() + ')') }
 $bytes=New-Object byte[] 32
 $rng=[Security.Cryptography.RandomNumberGenerator]::Create(); try { $rng.GetBytes($bytes) } finally {$rng.Dispose()}
 $settingsFile=[ordered]@{
  sessionId=$sessionId
  pipeName=('eraDSH-'+$sessionId)
  token=[Convert]::ToBase64String($bytes)
  launchMode='startup-hook'
  engineFamily='core'
  runtimeMode='inplace'
  hookPath=$hookPath
  hostPath=$sourceEngine
  enginePath=$sourceEngine
  engineSha256=$sourceHash
  engineExecutable=$engineName
  gameRoot=$settings.GameRoot
  runtimeDirectory=$runtime
  artifactDirectory=$artifact
  responseTimeoutSeconds=120
  savBackupPath=if($savBackup){$savBackup.path}else{''}
  savBackupFiles=if($savBackup){$savBackup.fileCount}else{0}
  savBackupCapturedUtc=if($savBackup){$savBackup.capturedUtc}else{''}
 }
 $settingsPath=Join-Path $artifact 'session.json'
 $settingsFile | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
 # Keep the artifact history bounded; a prune failure must never fail a good build.
 try {
  & (Join-Path $repositoryRoot 'scripts\Prune-Artifacts.ps1') -RepositoryRoot $repositoryRoot -Keep 20 -MaxMegabytes 100 -CurrentSession $artifact | Out-Null
 } catch {
  Write-Warning ('artifact prune skipped: ' + $_.Exception.Message)
 }
 [pscustomobject]@{
  built=$true
  engineKind='core'
  launchMode='startup-hook'
  hookPath=$hookPath
  hostPath=$sourceEngine
  sessionFile=$settingsPath
  artifactDirectory=$artifact
  runtimeMode='inplace'
  runtimeDirectory=$runtime
  gameRoot=$settings.GameRoot
  gameRootSource=$settings.GameRootSource
  enginePath=$sourceEngine
  engineExecutable=$engineName
  engineSource=$settings.EngineSource
  engineSha256=$sourceHash
  engineAssemblyVersion=''
  engineVerified=$false
  configPath=$settings.ConfigPath
  originalSavesCopied=$false
  savBackupPath=if($savBackup){$savBackup.path}else{''}
  savBackupFiles=if($savBackup){$savBackup.fileCount}else{0}
  savBackupBytes=if($savBackup){$savBackup.bytes}else{0}
  savBackupReused=if($savBackup){$savBackup.reused}else{$false}
 } | ConvertTo-Json
 return
}

$assemblyVersion=$settings.EngineAssemblyVersion
if(-not $settings.EngineVerified) {
 Write-Warning ('Engine ' + $engineName + ' is assembly version ' + $assemblyVersion + ', which this adapter has not been exercised against. Missing engine members will fail at startup with a host error.')
}

if($Recreate -and (Test-Path -LiteralPath $runtime)) { Remove-Item -LiteralPath $runtime -Recurse -Force }

if($mode -eq 'inplace') {
 # Nothing is copied. The runtime directory only holds the built host executable,
 # and the engine runs from the game folder, so the game keeps its own assets,
 # saves, logs and configuration in place.
 if(-not(Test-Path -LiteralPath $runtime)) { [void](New-Item -ItemType Directory -Path $runtime) }
 if(-not(Test-Path -LiteralPath $sourceEngine -PathType Leaf)) { throw ('Engine is missing: ' + $sourceEngine) }
 Write-Warning ('inplace runtime: the engine runs from ' + $settings.GameRoot + ' and the game writes its own Sav/Debug/Dat, logs and configuration into that directory.')
 $marker=[ordered]@{
  runtimeMode='inplace'
  gameRoot=$settings.GameRoot
  engineExecutable=$engineName
  engineSha256=$sourceHash
  engineAssemblyVersion=$assemblyVersion
  createdUtc=[DateTime]::UtcNow.ToString('o')
  bridgeVersion='0.2.0'
 }
 $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
} else {
 if(Test-Path -LiteralPath $runtime) {
  if(-not(Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw ('Runtime directory exists but is not managed by this bridge: ' + $runtime + '. Use -Recreate to replace it.') }
  $marker=Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if(-not (Test-EraDshSamePath -Left $marker.gameRoot -Right $settings.GameRoot)) {
   throw ('Runtime directory was created for a different game folder (' + $marker.gameRoot + '): ' + $runtime + '. Use -Recreate to replace it.')
  }
  if($marker.engineExecutable -ne $engineName) {
   throw ('Runtime directory holds ' + $marker.engineExecutable + ' but this build selected ' + $engineName + ': ' + $runtime + '. Use -Recreate to switch engines.')
  }
 } else {
  [void](New-Item -ItemType Directory -Path $runtime)
  Copy-Item -LiteralPath $sourceEngine -Destination $engine
  # Copy the game's own files, but never this checkout (it can live inside the game
  # folder) and never the directories the bridge owns as isolated state.
  foreach($item in Get-ChildItem -LiteralPath $settings.GameRoot -Force) {
   if($item.Name -in @('Sav','Debug','Dat')) { continue }
   if(Test-EraDshSamePath -Left $item.FullName -Right $repositoryRoot) { continue }
   Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $runtime $item.Name) -Recurse -Force
  }
  foreach($name in @('Sav','Debug','Dat')) { [void](New-Item -ItemType Directory -Path (Join-Path $runtime $name)) }
  $marker=[ordered]@{
   runtimeMode='copy'
   gameRoot=$settings.GameRoot
   engineExecutable=$engineName
   engineSha256=$sourceHash
   engineAssemblyVersion=$assemblyVersion
   createdUtc=[DateTime]::UtcNow.ToString('o')
   bridgeVersion='0.2.0'
  }
  $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
 }
 if(-not(Test-Path -LiteralPath $engine -PathType Leaf)) { throw ('Isolated engine copy is missing: ' + $engine + '. Use -Recreate to rebuild it.') }
 if((Get-FileHash -LiteralPath $engine -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) { throw 'Isolated engine copy does not match the configured engine file' }
}

$frameworkRoot=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compiler=Join-Path $frameworkRoot 'csc.exe'
if(-not(Test-Path -LiteralPath $compiler)) {
 $frameworkRoot=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
 $compiler=Join-Path $frameworkRoot 'csc.exe'
}
if(-not(Test-Path -LiteralPath $compiler)) { throw 'Installed .NET Framework compiler not found (csc.exe)' }

& $compiler /nologo /target:winexe /platform:x86 /optimize+ /warn:4 `
 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
 /reference:System.Web.Extensions.dll /reference:System.Core.dll `
 ('/out:'+$hostPath) (Join-Path $repositoryRoot 'bridge\EraBridgeHost.cs')
if($LASTEXITCODE -ne 0) { throw ('Compiler failed: '+$LASTEXITCODE) }

$config='<?xml version="1.0" encoding="utf-8"?><configuration><startup useLegacyV2RuntimeActivationPolicy="true"><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.0" /></startup></configuration>'
[IO.File]::WriteAllText(($hostPath+'.config'),$config,(New-Object Text.UTF8Encoding($false)))

$sessionId=[Guid]::NewGuid().ToString('N')
$artifact=Join-Path $repositoryRoot ('artifacts\bridge-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+$sessionId.Substring(0,8))
[void](New-Item -ItemType Directory -Path $artifact)
# copy mode keeps the game's Sav untouched, so only an inplace build needs a snapshot.
$savBackup=if($mode -eq 'inplace') { Save-EraDshSavSnapshot -GameRoot $settings.GameRoot -Force:$RefreshSavBackup -Skip:$NoSavBackup } else { $null }
if($savBackup) { Write-Output ('Sav snapshot: ' + $savBackup.path + ' (' + $savBackup.fileCount + ' files, ' + [math]::Round($savBackup.bytes/1MB,1) + ' MB, reused=' + $savBackup.reused.ToString().ToLowerInvariant() + ')') }
$bytes=New-Object byte[] 32
$rng=[Security.Cryptography.RandomNumberGenerator]::Create(); try { $rng.GetBytes($bytes) } finally {$rng.Dispose()}
$settingsFile=[ordered]@{
 sessionId=$sessionId
 pipeName=('eraDSH-'+$sessionId)
 token=[Convert]::ToBase64String($bytes)
 runtimeMode=$mode
 enginePath=$engine
 engineSha256=$sourceHash
 engineAssemblyVersion=$assemblyVersion
 engineAssemblyName='Emuera'
 sourceEnginePath=$sourceEngine
 gameRoot=$settings.GameRoot
 runtimeDirectory=$runtime
 artifactDirectory=$artifact
 hostPath=$hostPath
 savBackupPath=if($savBackup){$savBackup.path}else{''}
 savBackupFiles=if($savBackup){$savBackup.fileCount}else{0}
 savBackupCapturedUtc=if($savBackup){$savBackup.capturedUtc}else{''}
}
$settingsPath=Join-Path $artifact 'session.json'
$settingsFile | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
# Keep the artifact history bounded; a prune failure must never fail a good build.
try {
 & (Join-Path $repositoryRoot 'scripts\Prune-Artifacts.ps1') -RepositoryRoot $repositoryRoot -Keep 20 -MaxMegabytes 100 -CurrentSession $artifact | Out-Null
} catch {
 Write-Warning ('artifact prune skipped: ' + $_.Exception.Message)
}

[pscustomobject]@{
 built=$true
 engineKind='framework'
 launchMode='reflection'
 hostPath=$hostPath
 sessionFile=$settingsPath
 artifactDirectory=$artifact
 runtimeMode=$mode
 runtimeDirectory=$runtime
 gameRoot=$settings.GameRoot
 gameRootSource=$settings.GameRootSource
 enginePath=$engine
 engineExecutable=$engineName
 engineSource=$settings.EngineSource
 engineSha256=$sourceHash
 engineAssemblyVersion=$assemblyVersion
 engineVerified=$settings.EngineVerified
 configPath=$settings.ConfigPath
 originalSavesCopied=$false
 savBackupPath=if($savBackup){$savBackup.path}else{''}
 savBackupFiles=if($savBackup){$savBackup.fileCount}else{0}
 savBackupBytes=if($savBackup){$savBackup.bytes}else{0}
 savBackupReused=if($savBackup){$savBackup.reused}else{$false}
} | ConvertTo-Json
