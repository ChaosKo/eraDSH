# Read-only CLR metadata inspection. Does not execute the game entry point.
[CmdletBinding()]
param([string]$OutputPath=(Join-Path $PSScriptRoot '..\artifacts\engine-metadata.json'))
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Common.ps1')
$path=(Get-EraDshSettings).EnginePath
$resolver=[ResolveEventHandler]{
 param($sender,$eventArgs)
 try { return [Reflection.Assembly]::ReflectionOnlyLoad($eventArgs.Name) }
 catch {
  $short=($eventArgs.Name -split ',')[0]+'.dll'
  $framework=Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) $short
  if(Test-Path -LiteralPath $framework) { return [Reflection.Assembly]::ReflectionOnlyLoadFrom($framework) }
  throw
 }
}
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolver)
try {
 $a=[Reflection.Assembly]::ReflectionOnlyLoadFrom($path)
 $loaderErrors=@()
 try { $allTypes=@($a.GetTypes()) }
 catch [Reflection.ReflectionTypeLoadException] {
  $loaderErrors=@($_.Exception.LoaderExceptions | ForEach-Object {$_.Message})
  $allTypes=@($_.Exception.Types | Where-Object {$null -ne $_})
 }
 $flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance,DeclaredOnly'
 $selected=@($allTypes | Where-Object {$_.Name -match '^(Program|MainWindow|EmueraConsole|ConsoleDisplayLine|ConsoleButtonString|InputRequest|InputType|ConsoleState|PrintStringBuffer|StringPart|ConsolePart|AConsoleDisplayPart)$'})
 $rows=@($selected | ForEach-Object {
  [pscustomobject]@{type=$_.FullName;isEnum=$_.IsEnum;fields=@($_.GetFields($flags) | ForEach-Object {$_.FieldType.FullName+' '+$_.Name});properties=@($_.GetProperties($flags) | ForEach-Object {$_.PropertyType.FullName+' '+$_.Name});methods=@($_.GetMethods($flags) | ForEach-Object {$_.ToString()})}
 })
 $result=[pscustomobject]@{assembly=$a.FullName;runtime=$a.ImageRuntimeVersion;entryPoint=$a.EntryPoint.ToString();loaderErrors=$loaderErrors;typeNames=@($allTypes | ForEach-Object {$_.FullName});selectedTypes=$rows}
 $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
 $result | Select-Object assembly,runtime,entryPoint,loaderErrors,selectedTypes | ConvertTo-Json -Depth 10
} finally { [AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver) }
