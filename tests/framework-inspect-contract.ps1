# Read-only metadata/IL inspection; never invokes the engine entry point or methods.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EnginePath)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$resolver=[ResolveEventHandler]{param($s,$e) [Reflection.Assembly]::ReflectionOnlyLoad($e.Name)}
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolver)
try {
 $a=[Reflection.Assembly]::ReflectionOnlyLoadFrom((Resolve-Path -LiteralPath $EnginePath).Path)
 $flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
 $codes=@{}
 foreach($f in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')){$op=$f.GetValue($null);$codes[([int]$op.Value -band 65535)]=$op}
 foreach($spec in @(@('MinorShift.Emuera.GameView.EmueraConsole','MoveMouse'),@('MinorShift.Emuera.GameView.EmueraConsole','get_NewButtonGeneration'),@('MinorShift.Emuera.GameView.EmueraConsole','get_SelectingButton'),@('MinorShift.Emuera.GameView.EmueraConsole','ButtonIsSelected'),@('MinorShift.Emuera.GameView.EmueraConsole','newGeneration'),@('MinorShift.Emuera.GameView.EmueraConsole','UpdateGeneration'),@('MinorShift.Emuera.GameView.EmueraConsole','WaitInput'),@('MinorShift.Emuera.GameView.EmueraConsole','get_IsRunningTimer'),@('MinorShift.Emuera.GameProc.InputRequest','get_NeedValue'),@('MinorShift.Emuera.GameProc.Process','setWaitInput'))) {
  $t=$a.GetType($spec[0],$true);$m=$t.GetMethod($spec[1],$flags)
  if(-not $m){throw ('Missing method '+($spec -join '.'))}
  Write-Output ('METHOD '+$t.FullName+'.'+$m.Name)
  $bytes=$m.GetMethodBody().GetILAsByteArray();$pos=0
  while($pos -lt $bytes.Length){
   $offset=$pos;$code=[int]$bytes[$pos];$pos++
   if($code -eq 254){$code=65024+[int]$bytes[$pos];$pos++}
   $op=$codes[$code];$operand='';$n=0
   switch($op.OperandType.ToString()){
    'InlineNone'{$n=0}
    'ShortInlineI'{$n=1;$operand=[string]$bytes[$pos]}
    'ShortInlineVar'{$n=1;$operand=[string]$bytes[$pos]}
    'ShortInlineBrTarget'{$n=1;$operand=[string]$bytes[$pos]}
    'InlineVar'{$n=2;$operand=[string][BitConverter]::ToUInt16($bytes,$pos)}
    'InlineI'{$n=4;$operand=[string][BitConverter]::ToInt32($bytes,$pos)}
    'InlineBrTarget'{$n=4;$operand=[string][BitConverter]::ToInt32($bytes,$pos)}
    'InlineI8'{$n=8;$operand=[string][BitConverter]::ToInt64($bytes,$pos)}
    'ShortInlineR'{$n=4}
    'InlineR'{$n=8}
    'InlineSwitch'{$count=[BitConverter]::ToInt32($bytes,$pos);$n=4+4*$count;$operand='targets='+$count}
    default{$n=4;$token=[BitConverter]::ToInt32($bytes,$pos);if($op.OperandType.ToString() -eq 'InlineString'){$operand=$m.Module.ResolveString($token)}else{$member=$m.Module.ResolveMember($token);$operand=$member.DeclaringType.FullName+' :: '+$member.ToString()}}
   }
   $pos+=$n;Write-Output ('{0:X4} {1} {2}' -f $offset,$op.Name,$operand)
  }
 }
} finally {[AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver)}
