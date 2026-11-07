# Synthetic boundary tests. Loads host code but NEVER invokes the engine entry point.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$HostPath,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Web.Extensions
Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Text;
namespace EraBounds {
 public class Input {
  public string InputType="StrValue";public long ID=1;public bool OneInput=false;
   public bool NeedValue {get{return true;}}
  public long Timelimit=-1;public bool IsSystemInput=false;
 }
 public class Button {
  public bool IsButton {get;set;} public bool IsInteger {get;set;}
  public long Input {get;set;} public string Inputs {get;set;} public long Generation {get;set;}
  public Button(){IsButton=true;IsInteger=false;Input=9;Inputs=new string('v',3000);Generation=1;}
  public override string ToString(){return new string('b',1000);}
 }
 public class Line {
  public int LineNo;public string Text;public Button[] Buttons {get;set;}
  public Line(int n,string text,Button[] buttons){LineNo=n;Text=text;Buttons=buttons;}
  public override string ToString(){return Text;}
 }
 public class Console {
  public string state="WaitInput";public Input inputReq=new Input();public ArrayList displayLineList=new ArrayList();public int newButtonGeneration=1,lastButtonGeneration=1;
   public bool IsRunningTimer {get{return false;}}
  public Console(){var buttons=new Button[200];for(int i=0;i<buttons.Length;i++)buttons[i]=new Button();displayLineList.Add(new Line(0,new string('x',60000),new Button[0]));displayLineList.Add(new Line(1,"short",buttons));}
  public void GetDisplayStrings(StringBuilder b){foreach(Line line in displayLineList)b.AppendLine(line.Text);}
 }
}
'@
$a=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $HostPath).Path)
$hostType=$a.GetType('EraDSH.Host',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$form=New-Object Windows.Forms.Form
$console=New-Object EraBounds.Console
$hostType.GetField('Window',$flags).SetValue($null,$form)
$hostType.GetField('ConsoleObject',$flags).SetValue($null,$console.PSObject.BaseObject)
$hostType.GetField('SessionId',$flags).SetValue($null,'synthetic-unit-test')
$checks=New-Object System.Collections.Generic.List[object]
function Assert-Test([string]$Name,[bool]$Pass) { $checks.Add([pscustomobject]@{name=$Name;passed=$Pass});if(-not $Pass){throw ('Boundary assertion failed: '+$Name)} }
function Invoke-Private([string]$Name,[object[]]$Arguments) {
 $raw=New-Object 'object[]' $Arguments.Length
 for($n=0;$n -lt $Arguments.Length;$n++) { if($null -ne $Arguments[$n]) { $raw[$n]=$Arguments[$n].PSObject.BaseObject } }
 return $hostType.GetMethod($Name,$flags).Invoke($null,$raw)
}
function Observe([int]$Start=0,[int]$Limit=80) {
 $q=New-Object 'System.Collections.Generic.Dictionary[string,object]';$q['mode']='text';$q['noise']='raw';$q['fromLine']=$Start;$q['limitLines']=$Limit
 $items=New-Object 'object[]' 1;$items[0]=$q
 return (Invoke-Private 'Snapshot' $items)
}
try {
 $obs=Observe
 Assert-Test 'text_character_budget' ($obs['text'].Length -le 24000)
 Assert-Test 'text_truncation_reported' ([bool]$obs['textTruncated'])
 Assert-Test 'button_count_budget' ($obs['buttons'].Count -eq 128 -and $obs['omittedButtons'] -eq 72)
 Assert-Test 'button_label_budget' ($obs['buttons'][0]['label'].Length -eq 512 -and $obs['buttons'][0]['labelTruncated'])
 Assert-Test 'oversized_button_not_selectable' ($obs['buttons'][0]['valueTooLong'] -and -not $obs['buttons'][0]['choosable'] -and $null -eq $obs['buttons'][0]['value'])
 $page=Observe 1 1
 Assert-Test 'revision_independent_of_page' ($obs['observationId'] -ceq $page['observationId'])
 $console.displayLineList[1].Buttons[0].Inputs='changed-hidden-button-value'
 $changed=Observe
 Assert-Test 'revision_covers_hidden_button_metadata' ($changed['observationId'] -cne $obs['observationId'])
 $console.lastButtonGeneration=2
 $inactive=Observe
 Assert-Test 'inactive_buttons_excluded' ($inactive['buttons'].Count -eq 0 -and $inactive['omittedInactiveButtons'] -eq 200)
 $console.lastButtonGeneration=1
 $argsList=New-Object 'object[]' 1;$argsList[0]=@{large=('z'*(5*1024*1024))}
 $fallback=(Invoke-Private 'SerializeResponse' $argsList) | ConvertFrom-Json
 Assert-Test 'oversize_response_fallback' (-not $fallback.ok -and $fallback.code -eq 'RESPONSE_LIMIT')
 $largeBytes=[Text.Encoding]::UTF8.GetBytes(('a'*33000)+"`n")
 $stream=[IO.MemoryStream]::new($largeBytes);$reader=[IO.StreamReader]::new($stream)
 $requestRejected=$false
 try { $argsList[0]=$reader;[void](Invoke-Private 'ReadRequest' $argsList) } catch { $requestRejected=$_.Exception.ToString().Contains('32768') } finally {$reader.Dispose();$stream.Dispose()}
 Assert-Test 'request_read_hard_limit' $requestRejected
 $stream=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes("{}`n"));$reader=[IO.StreamReader]::new($stream)
 try { $argsList[0]=$reader;$normal=Invoke-Private 'ReadRequest' $argsList } finally {$reader.Dispose();$stream.Dispose()}
 Assert-Test 'normal_request_after_boundary_error' ($normal -ceq '{}')
 $normalObs=Observe
 Assert-Test 'observation_after_boundary_error' ($normalObs['ok'])
 $result=[pscustomobject]@{passed=$true;engineEntryPointInvoked=$false;checks=@($checks.ToArray())}
 $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
 $result | ConvertTo-Json -Depth 8
} finally {$form.Dispose()}
