# Build the bridge host with the installed .NET Framework compiler.
# No downloads and no package installation; the compiler ships with Windows.
#
# The bridge never runs the user's game folder directly. It makes an isolated
# copy of that folder under runtime/<name>/ and loads the engine from there, so
# saves, logs and configuration written by a bridge session cannot land in the
# original game directory.
[CmdletBinding()]
param(
 [string]$GameRoot,
 [string]$EngineExecutable,
 [string]$EngineAssemblyVersion,
 [string]$RuntimeDirectory,
 [string]$ConfigPath,
 [switch]$Recreate
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'Common.ps1')

$settings=Get-EraDshSettings -GameRoot $GameRoot -EngineExecutable $EngineExecutable `
  -EngineAssemblyVersion $EngineAssemblyVersion -RuntimeDirectory $RuntimeDirectory -ConfigPath $ConfigPath
$repositoryRoot=$settings.RepositoryRoot
$runtime=$settings.RuntimePath
$sourceEngine=$settings.EnginePath
$engineName=[IO.Path]::GetFileName($sourceEngine)
$engine=Join-Path $runtime $engineName
$hostPath=Join-Path $runtime 'EraBridgeHost.exe'
$markerPath=Join-Path $runtime '.erabridge-runtime.json'

if(@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $hostPath}).Count) { throw 'Bridge host is running; close it before rebuilding' }

$sourceHash=(Get-FileHash -LiteralPath $sourceEngine -Algorithm SHA256).Hash.ToLowerInvariant()
$assemblyVersion=$settings.EngineAssemblyVersion
if(-not $settings.EngineVerified) {
 Write-Warning ('Engine ' + $engineName + ' is assembly version ' + $assemblyVersion + ', which this adapter has not been exercised against. Missing engine members will fail at startup with a host error.')
}

if($Recreate -and (Test-Path -LiteralPath $runtime)) { Remove-Item -LiteralPath $runtime -Recurse -Force }

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
  gameRoot=$settings.GameRoot
  engineExecutable=$engineName
  engineSha256=$sourceHash
  engineAssemblyVersion=$assemblyVersion
  createdUtc=[DateTime]::UtcNow.ToString('o')
  bridgeVersion='0.1.0'
 }
 $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
}

if(-not(Test-Path -LiteralPath $engine -PathType Leaf)) { throw ('Isolated engine copy is missing: ' + $engine + '. Use -Recreate to rebuild it.') }
if((Get-FileHash -LiteralPath $engine -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) { throw 'Isolated engine copy does not match the configured engine file' }

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
$bytes=New-Object byte[] 32
$rng=[Security.Cryptography.RandomNumberGenerator]::Create(); try { $rng.GetBytes($bytes) } finally {$rng.Dispose()}
$settingsFile=[ordered]@{
 sessionId=$sessionId
 pipeName=('eraDSH-'+$sessionId)
 token=[Convert]::ToBase64String($bytes)
 enginePath=$engine
 engineSha256=$sourceHash
 engineAssemblyVersion=$assemblyVersion
 engineAssemblyName='Emuera'
 sourceEnginePath=$sourceEngine
 gameRoot=$settings.GameRoot
 runtimeDirectory=$runtime
 artifactDirectory=$artifact
 hostPath=$hostPath
}
$settingsPath=Join-Path $artifact 'session.json'
$settingsFile | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8

[pscustomobject]@{
 built=$true
 hostPath=$hostPath
 sessionFile=$settingsPath
 artifactDirectory=$artifact
 runtimeDirectory=$runtime
 gameRoot=$settings.GameRoot
 gameRootSource=$settings.GameRootSource
 engineExecutable=$engineName
 engineSource=$settings.EngineSource
 engineSha256=$sourceHash
 engineAssemblyVersion=$assemblyVersion
 engineVerified=$settings.EngineVerified
 configPath=$settings.ConfigPath
 originalSavesCopied=$false
} | ConvertTo-Json
