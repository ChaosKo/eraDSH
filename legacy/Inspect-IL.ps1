# Read-only IL inspection for compatibility decisions; no engine methods invoked.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Common.ps1')
$enginePath=(Get-EraDshSettings).EnginePath
$resolver=[ResolveEventHandler]{ param($s,$e)
 try { return [Reflection.Assembly]::ReflectionOnlyLoad($e.Name) } catch {
  return [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) (($e.Name -split ',')[0]+'.dll')))
 }
}
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolver)
try {
 $a=[Reflection.Assembly]::ReflectionOnlyLoadFrom($enginePath)
 $codes=@{}
 foreach($f in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')) { $op=$f.GetValue($null); $codes[([int]$op.Value -band 65535)]=$op }
 $lines=New-Object System.Collections.Generic.List[string]
 foreach($spec in @(@('MinorShift.Emuera.Program','Main'),@('MinorShift.Emuera.GameView.EmueraConsole','PressEnterKey'),@('MinorShift.Emuera.MainWindow','PressEnterKey'))) {
  $m=$a.GetType($spec[0]).GetMethod($spec[1],[Reflection.BindingFlags]'Public,NonPublic,Static,Instance')
  $lines.Add('METHOD '+$spec[0]+'.'+$m.ToString())
  $bytes=$m.GetMethodBody().GetILAsByteArray(); $pos=0
  while($pos -lt $bytes.Length) {
   $offset=$pos; $code=[int]$bytes[$pos]; $pos++
   if($code -eq 254) { $code=65024+[int]$bytes[$pos]; $pos++ }
   $op=$codes[$code]; if($null -eq $op) { $lines.ToArray() | Set-Content -LiteralPath $OutputPath -Encoding UTF8; throw ('Unknown opcode {0:X4} at {1:X4}; partial listing saved' -f $code,$offset) }
   $operand=''; $n=0
   switch($op.OperandType.ToString()) {
    'InlineNone' { $n=0 }
    'ShortInlineI' { $n=1; $operand=[string]$bytes[$pos] }
    'ShortInlineVar' { $n=1; $operand=[string]$bytes[$pos] }
    'InlineVar' { $n=2; $operand=[string][BitConverter]::ToUInt16($bytes,$pos) }
    'ShortInlineBrTarget' { $n=1; $operand=[string]$bytes[$pos] }
    'InlineI' { $n=4; $operand=[string][BitConverter]::ToInt32($bytes,$pos) }
    'InlineBrTarget' { $n=4; $operand=[string][BitConverter]::ToInt32($bytes,$pos) }
    'ShortInlineR' { $n=4 }
    'InlineI8' { $n=8; $operand=[string][BitConverter]::ToInt64($bytes,$pos) }
    'InlineR' { $n=8 }
    'InlineSwitch' { $count=[BitConverter]::ToInt32($bytes,$pos); $n=4+4*$count; $operand='targets='+$count }
    default {
     $n=4; $token=[BitConverter]::ToInt32($bytes,$pos)
     try {
      if($op.OperandType.ToString() -eq 'InlineString') { $operand='"'+$m.Module.ResolveString($token)+'"' }
      else { $member=$m.Module.ResolveMember($token); $operand=$member.DeclaringType.FullName+' :: '+$member.ToString() }
     } catch { $operand='token='+$token+' '+$_.Exception.Message }
    }
   }
   $pos+=$n
   $lines.Add(('{0:X4} {1} {2}' -f $offset,$op.Name,$operand))
  }
 }
 $lines.ToArray() | Set-Content -LiteralPath $OutputPath -Encoding UTF8
 [pscustomobject]@{output=$OutputPath;lines=$lines.Count} | ConvertTo-Json
} finally { [AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver) }
