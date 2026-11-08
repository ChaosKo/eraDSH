// Synthetic Framework bridge regression: never loads or starts a game engine.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FrameworkFixture {
 public class Request {
  public long ID=1;public string InputType="IntValue";public bool OneInput=false,IsSystemInput=false;public long Timelimit=-1;
  public bool NeedValue { get { return InputType=="IntValue"||InputType=="StrValue"||InputType=="IntButton"||InputType=="StrButton"||InputType=="AnyValue"; } }
 }
 public class Button {
  public bool IsButton {get;set;} public bool IsInteger {get;set;} public long Input {get;set;} public string Inputs {get;set;} public long Generation {get;set;}
  public string Label="choice";
  public Button(long value,long generation) { IsButton=true;IsInteger=true;Input=value;Inputs="";Generation=generation; }
  public override string ToString() { return Label; }
 }
 public class Line {
  public int LineNo;public string Text;public Button[] Buttons {get;set;}
  public Line(int number,string text,params Button[] buttons) { LineNo=number;Text=text;Buttons=buttons; }
  public override string ToString() { return Text; }
 }
 public class Console {
  public string state="WaitInput";public object inputReq=new Request();public ArrayList displayLineList=new ArrayList();
  public int lastButtonGeneration=1,newButtonGeneration=2;public bool TimerRunning=false;
  public bool IsRunningTimer { get { return TimerRunning; } }
  public long DeletedLines {get;set;}
  public int Submissions,Reads;public string LastValue;public Action<Console> AfterSubmit;
  public void GetDisplayStrings(StringBuilder b) { Reads++;foreach(Line line in displayLineList)b.AppendLine(line.Text); }
  public void PressEnterKey(bool a,string value,bool b) { Submissions++;LastValue=value;if(AfterSubmit!=null)AfterSubmit(this); }
 }
 public class MissingRequest { public string InputType="IntValue";public bool OneInput=false,IsSystemInput=false;public long Timelimit=-1;public bool NeedValue { get { return true; } } }
 public class MissingTimerConsole {
  public string state="WaitInput";public object inputReq=new Request();public ArrayList displayLineList=new ArrayList();public int lastButtonGeneration=1,newButtonGeneration=2;
  public void GetDisplayStrings(StringBuilder b) { b.Append("test"); }
  public int Submissions;public void PressEnterKey(bool a,string value,bool b) {Submissions++;}
 }
}

public static class FrameworkRegression {
 static Type Host;static Form Window;static int Checks;
 static readonly BindingFlags Flags=BindingFlags.Static|BindingFlags.NonPublic;
 static Dictionary<string,object> Map(params object[] pairs) { var result=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)result[(string)pairs[i]]=pairs[i+1];return result; }
 static object Invoke(string name,params object[] args) { try {return Host.GetMethod(name,Flags).Invoke(null,args);}catch(TargetInvocationException ex){throw ex.InnerException;} }
 static void Set(string name,object value) {Host.GetField(name,Flags).SetValue(null,value);}
 static Dictionary<string,object> Dict(object o) {return (Dictionary<string,object>)o;}
 static string Str(Dictionary<string,object> d,string key) {object o;return d.TryGetValue(key,out o)&&o!=null?o.ToString():"";}
 static bool Yes(Dictionary<string,object> d,string key) {object o;return d.TryGetValue(key,out o)&&o is bool&&(bool)o;}
 static void Check(string name,bool pass) {if(!pass)throw new Exception("FAILED: "+name);Checks++;System.Console.WriteLine("PASS "+name);}
 static FrameworkFixture.Console Fresh(string kind="IntValue") {
  var c=new FrameworkFixture.Console();((FrameworkFixture.Request)c.inputReq).InputType=kind;c.displayLineList.Add(new FrameworkFixture.Line(0,"menu",new FrameworkFixture.Button(7,1)));Set("ConsoleObject",c);Set("Failure",null);Set("Exited",false);return c;
 }
 static Dictionary<string,object> Snap(params object[] pairs) {var q=Map("mode","text","limitLines",80);for(int i=0;i<pairs.Length;i+=2)q[(string)pairs[i]]=pairs[i+1];return Dict(Invoke("Snapshot",q));}
 static Dictionary<string,object> Act(Dictionary<string,object> observation,string kind="input",string value="7",string choice=null) {
  return Dict(Invoke("Act",Map("observationId",Str(observation,"observationId"),"kind",kind,"value",value,"choiceId",choice??"","actionId",Guid.NewGuid().ToString())));
 }
 static string FirstChoice(Dictionary<string,object> d) {return Str(Dict(((List<object>)d["buttons"])[0]),"id");}
 static Dictionary<string,object> OnPumpedThread(Func<Dictionary<string,object>> operation) {
  Dictionary<string,object> result=null;Exception failure=null;var thread=new Thread(delegate(){try{result=operation();}catch(Exception ex){failure=ex;}});thread.Start();
  var clock=Stopwatch.StartNew();while(thread.IsAlive&&clock.ElapsedMilliseconds<10000){Application.DoEvents();Thread.Sleep(1);}if(!thread.Join(1000))throw new Exception("test worker hung");if(failure!=null)throw failure;return result;
 }
 static void MainTests() {
  var c=Fresh();var first=Snap();Check("initial_ready_contract",Yes(first,"attached")&&Yes(first,"canAct")&&Yes(first,"ready")&&!Yes(first,"pending")&&Str(first,"phase")=="waiting-input");
  ((FrameworkFixture.Line)c.displayLineList[0]).Text="decorative text changed";((FrameworkFixture.Line)c.displayLineList[0]).Buttons[0].Label="font/label changed";Window.ClientSize=new Size(731,413);
  var repaint=Snap();Check("presentation_changes_keep_input_epoch",Str(first,"inputEpoch")==Str(repaint,"inputEpoch")&&Str(first,"displayHash")!=Str(repaint,"displayHash"));
  Check("delivered_old_frame_survives_repaint",Yes(Act(first),"submitted")&&c.Submissions==1);
  Check("submit_consumes_old_input_epoch",!Yes(Act(first),"submitted")&&c.Submissions==1&&Yes(Snap(),"pending"));
  c.inputReq=new FrameworkFixture.Request();Check("new_request_unblocks_after_submit",Yes(Snap(),"canAct"));

  c=Fresh();first=Snap();c.inputReq=new FrameworkFixture.Request();Check("request_object_identity_rejects_old_token",Str(Act(first),"code")=="STALE_OBSERVATION"&&c.Submissions==0);
  c=Fresh();first=Snap();((FrameworkFixture.Request)c.inputReq).ID++;Check("request_id_rejects_old_token",Str(Act(first),"code")=="STALE_OBSERVATION"&&c.Submissions==0);
  c=Fresh();first=Snap();((FrameworkFixture.Request)c.inputReq).OneInput=true;Check("request_semantics_reject_old_token",Str(Act(first),"code")=="STALE_OBSERVATION"&&c.Submissions==0);
  c=Fresh();first=Snap();c.lastButtonGeneration=2;Check("authoritative_generation_rejects_old_token",Str(Act(first),"code")=="STALE_OBSERVATION"&&c.Submissions==0);
  c=Fresh();first=Snap();((FrameworkFixture.Line)c.displayLineList[0]).Buttons[0].Input=8;Check("effective_button_values_reject_old_token",Str(Act(first),"code")=="STALE_OBSERVATION"&&c.Submissions==0);
  c=Fresh();first=Snap();c.state="Running";Check("running_rejects_old_wait_token",Str(Act(first),"code")=="NOT_WAITING_INPUT"&&c.Submissions==0);
  c.state="WaitInput";var next=Snap();Check("observed_state_transition_rotates_epoch",Str(next,"inputEpoch")!=Str(first,"inputEpoch"));

  c=Fresh();for(int n=1;n<350;n++)c.displayLineList.Add(new FrameworkFixture.Line(n,"body "+n));
  first=Snap("limitLines",1,"fromLine",349);Check("buttons_are_not_text_page_limited",((List<object>)first["buttons"]).Count==1);
  string choice=FirstChoice(first);((FrameworkFixture.Line)c.displayLineList[0]).LineNo=500;Window.ClientSize=new Size(800,500);
  Check("choice_survives_line_reflow_and_200_line_tail",Yes(Act(first,"choose","",choice),"submitted")&&c.LastValue=="7");
  c=Fresh();first=Snap("limitLines",1,"fromLine",0,"noise","raw");var page=Snap("limitLines",80,"fromLine",0,"noise","filter");
  Check("input_epoch_is_projection_independent",Str(first,"inputEpoch")==Str(page,"inputEpoch")&&Str(first,"observationId")==Str(page,"observationId"));
  c=Fresh();c.displayLineList.Add(new FrameworkFixture.Line(1,"inactive future",new FrameworkFixture.Button(99,100)));
  first=Snap();Check("generation_is_last_not_buffer_max",((List<object>)first["buttons"]).Count==1&&Str(Dict(((List<object>)first["buttons"])[0]),"value")=="7");
  Check("valid_choice_works_despite_higher_inactive_generation",Yes(Act(first,"choose","",FirstChoice(first)),"submitted"));

  c=Fresh();c.state="Initializing";first=Snap();Check("loading_snapshot_is_ordinary_pending",Yes(first,"ok")&&Yes(first,"pending")&&!Yes(first,"ready")&&!Yes(first,"canAct")&&Str(first,"phase")=="loading");
  Check("loading_act_zero_input",!Yes(Act(first),"submitted")&&c.Submissions==0);
  var advance=OnPumpedThread(delegate{return Dict(Invoke("Advance",Map("timeoutMs",500,"maxSteps",20,"mode","text")));});
  Check("loading_advance_zero_input",Convert.ToInt32(advance["steps"])==0&&c.Submissions==0);
  var status=OnPumpedThread(delegate{return Dict(Invoke("Status",Map("timeoutMs",500)));});
  Check("status_ready_means_actionable_not_window",Yes(status,"attached")&&!Yes(status,"ready")&&Yes(status,"pending"));

  c=Fresh("EnterKey");((FrameworkFixture.Request)c.inputReq).Timelimit=1000;c.TimerRunning=true;first=Snap();
  Check("timed_key_not_actionable",!Yes(first,"canAct")&&Yes(first,"pending")&&!Yes(Act(first,"continue",""),"submitted"));
  advance=OnPumpedThread(delegate{return Dict(Invoke("Advance",Map("timeoutMs",500,"maxSteps",20)));});
  Check("advance_does_not_skip_timed_wait",Convert.ToInt32(advance["steps"])==0&&c.Submissions==0&&Str(advance,"stoppedBecause")=="timed-wait");
  c=Fresh();((FrameworkFixture.Request)c.inputReq).Timelimit=1000;c.TimerRunning=true;first=Snap();
  Check("timed_value_decision_remains_actionable",Yes(first,"canAct")&&Yes(Dict(first["input"]),"timed")&&Yes(Act(first),"submitted"));
  c=Fresh();((FrameworkFixture.Request)c.inputReq).Timelimit=1000;c.TimerRunning=false;first=Snap();Check("expired_timed_value_is_not_actionable",!Yes(first,"canAct")&&!Yes(Act(first),"submitted")&&c.Submissions==0);
  c=Fresh("EnterKey");((FrameworkFixture.Request)c.inputReq).IsSystemInput=true;first=Snap();Check("system_key_zero_input",!Yes(first,"canAct")&&!Yes(Act(first,"continue",""),"submitted")&&c.Submissions==0);
  c=Fresh();((FrameworkFixture.Request)c.inputReq).IsSystemInput=true;first=Snap();Check("system_value_menu_is_actionable",Yes(first,"canAct")&&Yes(Act(first),"submitted")&&c.Submissions==1);
  c=Fresh();first=Snap();Set("Failure","synthetic failure");Check("failure_never_authorizes_input",!Yes(Snap(),"canAct")&&!Yes(Act(first),"submitted")&&c.Submissions==0);
  c=Fresh("PrimitiveMouseKey");first=Snap();Check("unsupported_input_fails_closed",!Yes(first,"canAct")&&!Yes(Dict(first["input"]),"supported")&&!Yes(Act(first),"submitted"));
  foreach(string kind in new[]{"IntValue","StrValue","IntButton","StrButton","AnyValue","EnterKey","AnyKey"}) {
   c=Fresh(kind);first=Snap();var reply=Act(first,kind=="EnterKey"||kind=="AnyKey"?"continue":"input",kind=="EnterKey"||kind=="AnyKey"?"":"7");Check("supported_input_"+kind,Yes(first,"canAct")&&Yes(reply,"submitted")&&c.Submissions==1);
  }
  c=Fresh();first=Snap();Check("integer_validation_preserved",Str(Act(first,"input","not-an-int"),"code")=="INTEGER_REQUIRED"&&c.Submissions==0);
  c=Fresh("StrValue");first=Snap();Check("debug_command_rejected",Str(Act(first,"input","@DEBUG"),"code")=="INVALID_INPUT"&&c.Submissions==0);
  Check("multiline_rejected",Str(Act(first,"input","a\nb"),"code")=="INVALID_INPUT"&&c.Submissions==0);
  Check("oversized_input_rejected",Str(Act(first,"input",new string('x',2049)),"code")=="INVALID_INPUT"&&c.Submissions==0);
  c=Fresh();c.inputReq=new FrameworkFixture.MissingRequest();first=Snap();Check("missing_request_id_fails_closed",Str(first,"code")=="ADAPTER_CONTRACT_MISSING"&&!Yes(first,"canAct")&&!Yes(Act(first),"submitted")&&c.Submissions==0);
  var missing=new FrameworkFixture.MissingTimerConsole();Set("ConsoleObject",missing);first=Snap();Check("missing_timer_contract_fails_closed",Str(first,"code")=="ADAPTER_CONTRACT_MISSING"&&!Yes(first,"canAct"));

  c=Fresh("EnterKey");c.AfterSubmit=delegate(FrameworkFixture.Console x){var r=(FrameworkFixture.Request)x.inputReq;x.inputReq=new FrameworkFixture.Request { ID=r.ID+1,InputType=x.Submissions<3?"EnterKey":"IntValue" };};
  advance=OnPumpedThread(delegate{return Dict(Invoke("Advance",Map("timeoutMs",1500,"maxSteps",20)));});
  Check("advance_yields_and_stops_at_decision",Convert.ToInt32(advance["steps"])==3&&c.Submissions==3&&Str(advance,"stoppedBecause")=="input-required");
  c=Fresh("EnterKey");c.AfterSubmit=delegate(FrameworkFixture.Console x){x.state="Running";};first=Snap();
  var dispatch=OnPumpedThread(delegate{return Dict(Invoke("Dispatch",Map("action","act","observationId",Str(first,"observationId"),"kind","continue","value","","actionId",Guid.NewGuid().ToString(),"mode","text","timeoutMs",1000)));});
  Check("post_submit_observation_may_be_pending",Yes(dispatch,"submitted")&&Yes(Dict(dispatch["observation"]),"pending")&&!Yes(Dict(dispatch["observation"]),"canAct"));

  c=Fresh();int reads=c.Reads;Dictionary<string,object> pending=null;var clock=Stopwatch.StartNew();
  var blocked=new Thread(delegate(){pending=Dict(Invoke("Observe",Map("mode","text","timeoutMs",80)));});blocked.Start();Check("observe_busy_returns_within_budget",blocked.Join(1000)&&clock.ElapsedMilliseconds<1000&&Yes(pending,"ok")&&Yes(pending,"pending"));
  Application.DoEvents();Check("cancelled_observation_has_no_late_sampling",c.Reads==reads);
  first=Snap();int submissions=c.Submissions;Dictionary<string,object> cancelled=null;string actionId=Guid.NewGuid().ToString();
  blocked=new Thread(delegate(){cancelled=Dict(Invoke("Dispatch",Map("action","act","observationId",Str(first,"observationId"),"kind","input","value","7","actionId",actionId,"timeoutMs",80)));});blocked.Start();Check("queued_action_times_out_cancelled",blocked.Join(1000)&&Str(cancelled,"code")=="CANCELLED_BEFORE_EXECUTION");
  Application.DoEvents();Check("cancelled_action_has_no_late_input",c.Submissions==submissions);
  var duplicate=Dict(Invoke("Dispatch",Map("action","act","observationId",Str(first,"observationId"),"kind","input","value","7","actionId",actionId)));
  Check("cancelled_action_ledger_and_idempotency",Str(duplicate,"code")=="DUPLICATE_ACTION"&&Str(Dict(duplicate["action"]),"state")=="cancelled");
  c=Fresh();c.state="Initializing";clock=Stopwatch.StartNew();advance=OnPumpedThread(delegate{return Dict(Invoke("Advance",Map("timeoutMs",100,"maxSteps",200)));});
  Check("advance_budget_not_multiplied_by_max_steps",clock.ElapsedMilliseconds<1500&&c.Submissions==0);

  c=Fresh();c.displayLineList.Add(new FrameworkFixture.Line(900,"┌──────────────┐"));c.displayLineList.Add(new FrameworkFixture.Line(901,"│ [1] menu     │",new FrameworkFixture.Button(1,7)));c.displayLineList.Add(new FrameworkFixture.Line(902,"└──────────────┘"));c.displayLineList.Add(new FrameworkFixture.Line(903,"└──────────────┘"));
  var fullView=OnPumpedThread(delegate{return Dict(Invoke("Dispatch",Map("action","observe","mode","text","limitLines",80,"render","full","timeoutMs",500)));});
  var compactView=OnPumpedThread(delegate{return Dict(Invoke("Dispatch",Map("action","observe","mode","text","limitLines",80,"render","compact","timeoutMs",500)));});
  Check("compact_view_shrinks_and_keeps_identity",Str(compactView,"observationId")==Str(fullView,"observationId")&&Str(compactView,"text").Length<Str(fullView,"text").Length&&Str(compactView,"text").Contains("▦(")&&!compactView.ContainsKey("displayHash")&&!compactView.ContainsKey("window"));
  Check("compact_view_buttons_keep_authority",Yes(Act(compactView,"choose","",FirstChoice(compactView)),"submitted")&&c.Submissions==1);

  c=Fresh();var held=OnPumpedThread(delegate{return Dict(Invoke("Dispatch",Map("action","observe","mode","text","limitLines",80,"render","compact","timeoutMs",500)));});
  c.displayLineList.Add(new FrameworkFixture.Line(910,"appended narration line"));
  var acted=OnPumpedThread(delegate{return Dict(Invoke("Dispatch",Map("action","act","observationId",Str(held,"observationId"),"kind","input","value","7","actionId",Guid.NewGuid().ToString(),"mode","text","render","compact","timeoutMs",500)));});
  var actedView=Dict(acted["observation"]);var actedDelta=Dict(actedView["delta"]);
  Check("act_replies_with_a_delta_not_the_whole_window",Str(actedDelta,"kind")=="append"&&actedView.ContainsKey("text")&&actedView["text"]==null);

  var run=Fresh("EnterKey");
  // Fresh() seeds a menu line with a button; narration needs a screen that only waits.
  run.displayLineList.Clear();
  for(int n=1;n<20;n++)run.displayLineList.Add(new FrameworkFixture.Line(200+n,"body "+n));
  run.AfterSubmit=delegate(FrameworkFixture.Console k){
   k.displayLineList.Add(new FrameworkFixture.Line(300+k.Submissions,"step "+k.Submissions));
   // A real engine answers an Enter with a new input request; without that rotation
   // Advance correctly stops as "pending" after the first press.
   ((FrameworkFixture.Request)k.inputReq).ID++;};

  var runResult=OnPumpedThread(delegate{return Dict(Invoke("Advance",Map("maxSteps",3,"timeoutMs",2000,"mode","text","render","compact")));});
  var runView=Dict(runResult["observation"]);var runDelta=Dict(runView["delta"]);
  Check("advance_replies_with_a_delta_not_the_whole_window",Convert.ToInt32(runResult["steps"])==3&&Str(runDelta,"kind")=="append"&&runView.ContainsKey("text")&&runView["text"]==null);

 }
 [STAThread] public static int Main(string[] args) {
  try {
   Host=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("EraDSH.Host",true);Window=new Form();Window.CreateControl();var handle=Window.Handle;
   Set("Window",Window);Set("SessionId","framework-regression");Set("ArtifactDir",Path.GetFullPath(args[1]));Directory.CreateDirectory(args[1]);
   ((ManualResetEvent)Host.GetField("Ready",Flags).GetValue(null)).Set();
   MainTests();System.Console.WriteLine("PASS total="+Checks+" engineEntryPointInvoked=false");return 0;
  } catch(Exception ex) {System.Console.Error.WriteLine(ex);return 1;} finally {if(Window!=null)Window.Dispose();}
 }
}
