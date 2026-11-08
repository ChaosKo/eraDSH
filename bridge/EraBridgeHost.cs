// eraDSH prototype: version-pinned, same-process adapter; no injection into existing processes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace EraDSH {
 public static class Host {
  static readonly BindingFlags Members=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
  static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
  static readonly object Gate=new object();
  static readonly ManualResetEvent Ready=new ManualResetEvent(false);
  static readonly Dictionary<string,Dictionary<string,object>> Actions=new Dictionary<string,Dictionary<string,object>>();
  static Dictionary<string,object> Settings;
  static Assembly Engine;
  static volatile Form Window;
  static volatile bool Exited;
  static string Failure,SessionId,RuntimeDir,GameDir,ArtifactDir;
  static object ConsoleObject;
  static Thread Server;
  static int ImageNumber;
  // UI-thread-owned input identity. Presentation changes never authorize or revoke input.
  static object EpochOwner,EpochRequest,SubmittedOwner,SubmittedRequest;
  static string EpochSemantic,EpochState,CurrentInputEpoch,SubmittedSemantic;
  static long EpochNumber;
  static bool AwaitingTransition,EpochRefreshEligible;
  static object EpochRefreshSource;
  static string EpochRefreshSemantic;
  sealed class Delivered { public string Epoch;public readonly Dictionary<string,string> Choices=new Dictionary<string,string>(); }
  static readonly Dictionary<string,Delivered> DeliveredObservations=new Dictionary<string,Delivered>();
  static readonly Queue<string> DeliveredOrder=new Queue<string>();
  sealed class Control {
   public object Request;public string State,Epoch,Semantic;public long Generation,NextGeneration;
   public bool CanAct,Timed,SystemInput;public IList Lines;
   public Dictionary<string,object> Input;public readonly List<Dictionary<string,object>> Buttons=new List<Dictionary<string,object>>();
  }
  static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength=4*1024*1024,RecursionLimit=50 }; }
  static Dictionary<string,object> Map(params object[] pairs) { var d=new Dictionary<string,object>(); for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d; }
  static string S(Dictionary<string,object>d,string key,string fallback="") { object v; return d.TryGetValue(key,out v)&&v!=null?Convert.ToString(v,CultureInfo.InvariantCulture):fallback; }
  static int I(Dictionary<string,object>d,string key,int fallback) { object v; return d.TryGetValue(key,out v)?Convert.ToInt32(v,CultureInfo.InvariantCulture):fallback; }
  static object Field(object o,string name) { var f=o.GetType().GetField(name,Members); if(f==null)throw new MissingFieldException(o.GetType().FullName,name);return f.GetValue(o); }
  static object Prop(object o,string name) { var p=o.GetType().GetProperty(name,Members); if(p==null)throw new MissingMemberException(o.GetType().FullName,name);return p.GetValue(o,null); }
  static object Call(object o,string name,Type[] signature,params object[] args) { var m=o.GetType().GetMethod(name,Members,null,signature,null); if(m==null)throw new MissingMethodException(o.GetType().FullName,name);return m.Invoke(o,args); }
  static string Hash(string s) { using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(Utf8.GetBytes(s))).Replace("-","").ToLowerInvariant(); }
  static string FileHash(string path) { using(var h=SHA256.Create())using(var f=File.OpenRead(path))return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant(); }
  static string NormalDir(string p) { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar; }
  static void Record(string kind,object value) { lock(Gate)File.AppendAllText(Path.Combine(ArtifactDir,"host-audit.jsonl"),Json().Serialize(Map("utc",DateTime.UtcNow.ToString("o"),"kind",kind,"value",value))+"\n",Utf8); }
  static object Error(string code,string message) { return Map("ok",false,"code",code,"message",Clip(message,2048)); }
  // Supported Framework builds expose some of these as properties and others as fields.
  static object Member(object o,string name) {
   if(o==null)throw new MissingMemberException("Null owner for "+name);
   var t=o.GetType();var f=t.GetField(name,Members);if(f!=null)return f.GetValue(o);
   var p=t.GetProperty(name,Members);if(p!=null&&p.GetIndexParameters().Length==0)return p.GetValue(o,null);
   throw new MissingMemberException(t.FullName,name);
  }
  static bool ValueKind(string kind) { return kind=="IntValue"||kind=="StrValue"||kind=="IntButton"||kind=="StrButton"||kind=="AnyValue"; }
  static bool IntegerKind(string kind) { return kind=="IntValue"||kind=="IntButton"; }
  static bool KeyKind(string kind) { return kind=="EnterKey"||kind=="AnyKey"; }
  static int Budget(Dictionary<string,object> request,int fallback,int maximum) { return Math.Max(1,Math.Min(maximum,I(request,"timeoutMs",fallback))); }
  static Dictionary<string,object> Lifecycle(Control c) {
   bool attached=Window!=null&&!Window.IsDisposed&&ConsoleObject!=null;
   string phase=Exited?"exited":Failure!=null?"failed":!attached?"starting":c==null?"loading":c.CanAct?"waiting-input":c.State=="Initializing"||c.State=="Init"?"loading":c.State=="Error"?"failed":c.State=="Quit"?"exited":"running";
   bool can=attached&&c!=null&&c.CanAct&&phase=="waiting-input";
   return Map("attached",attached,"phase",phase,"pending",!can&&phase!="failed"&&phase!="exited","canAct",can,"ready",can,"inputEpoch",c==null?null:c.Epoch);
  }
  static void AddLifecycle(Dictionary<string,object> result,Control c) { foreach(var pair in Lifecycle(c))result[pair.Key]=pair.Value; }
  static Dictionary<string,object> Pending(string reason) {
   var result=Map("ok",true,"sessionId",SessionId,"state",Exited?"Exited":"Initializing","reason",reason);
   AddLifecycle(result,null);return result;
  }
  static Dictionary<string,object> ContractError(Exception ex) {
   var result=Map("ok",false,"code","ADAPTER_CONTRACT_MISSING","message",Clip(Root(ex).Message,2048));
   AddLifecycle(result,null);result["phase"]="failed";result["pending"]=false;return result;
  }
  static Control ReadControl() {
   var c=new Control();c.State=Convert.ToString(Member(ConsoleObject,"state"));c.Request=Member(ConsoleObject,"inputReq");
   c.Lines=Member(ConsoleObject,"displayLineList") as IList;if(c.Lines==null)throw new MissingMemberException("displayLineList is not IList");
   c.Generation=Convert.ToInt64(Member(ConsoleObject,"lastButtonGeneration"),CultureInfo.InvariantCulture);
   c.NextGeneration=Convert.ToInt64(Member(ConsoleObject,"newButtonGeneration"),CultureInfo.InvariantCulture);
   string kind=c.Request==null?"Void":Convert.ToString(Member(c.Request,"InputType"));
   bool needValue=c.Request!=null&&Convert.ToBoolean(Member(c.Request,"NeedValue"),CultureInfo.InvariantCulture);
   c.SystemInput=c.Request!=null&&Convert.ToBoolean(Member(c.Request,"IsSystemInput"),CultureInfo.InvariantCulture);
   long limit=c.Request==null?0:Convert.ToInt64(Member(c.Request,"Timelimit"),CultureInfo.InvariantCulture);
   bool runningTimer=Convert.ToBoolean(Member(ConsoleObject,"IsRunningTimer"),CultureInfo.InvariantCulture);
   c.Timed=limit>0||runningTimer;
   c.Input=Map("kind",kind,"requestId",c.Request==null?"":Convert.ToString(Member(c.Request,"ID"),CultureInfo.InvariantCulture),"oneInput",c.Request!=null&&Convert.ToBoolean(Member(c.Request,"OneInput"),CultureInfo.InvariantCulture),"needValue",needValue,"timeLimit",limit,"systemInput",c.SystemInput,"runningTimer",runningTimer,"timed",c.Timed,"supported",ValueKind(kind)||KeyKind(kind));
   var keys=new SortedSet<string>(StringComparer.Ordinal);
   for(int n=0;n<c.Lines.Count;n++) {
    object line=c.Lines[n];var items=Member(line,"Buttons") as Array;if(items==null)throw new MissingMemberException("Buttons is not an array");
    for(int j=0;j<items.Length;j++) {
     object b=items.GetValue(j);if(b==null||!Convert.ToBoolean(Member(b,"IsButton"),CultureInfo.InvariantCulture))continue;
     bool integer=Convert.ToBoolean(Member(b,"IsInteger"),CultureInfo.InvariantCulture);long generation=Convert.ToInt64(Member(b,"Generation"),CultureInfo.InvariantCulture);
     string value=Convert.ToString(Member(b,integer?"Input":"Inputs"),CultureInfo.InvariantCulture)??"";
     // 1.821 and 1.824 MoveMouse both compare Generation with lastButtonGeneration.
     bool active=c.State=="WaitInput"&&needValue&&generation==c.Generation&&(!IntegerKind(kind)||integer);
     string key=Json().Serialize(new object[]{generation,integer,value});if(active)keys.Add(key);
     string id="b-"+Convert.ToString(Member(line,"LineNo"),CultureInfo.InvariantCulture)+"-"+j+"-"+generation;
     c.Buttons.Add(Map("id",id,"label",Clip(b.ToString(),512),"labelTruncated",b.ToString().Length>512,"value",value.Length>2048?null:value,"valueTooLong",value.Length>2048,"choosable",value.Length<=2048,"active",active,"integer",integer,"generation",generation.ToString(CultureInfo.InvariantCulture),"lineIndex",n,"key",key));
    }
   }
   c.Semantic=Hash(Json().Serialize(new object[]{c.Input,c.Generation,c.NextGeneration,new List<string>(keys)}));
   if(AwaitingTransition&&(!object.ReferenceEquals(SubmittedOwner,ConsoleObject)||!object.ReferenceEquals(SubmittedRequest,c.Request)||SubmittedSemantic!=c.Semantic||c.State!="WaitInput"))AwaitingTransition=false;
   object refreshSource=null;string refreshSemantic=null;bool refresh=false;
   // MeiQ ANIMATION_ANYKEY loops TINPUT[S] 50 with -999 (or |) until user input.
   // Only this unconsumed sentinel loop may renew request IDs/objects without a new decision.
   if(!AwaitingTransition&&c.State=="WaitInput"&&!c.SystemInput&&runningTimer&&limit==50&&(kind=="IntValue"||kind=="StrValue"))try {
    bool hasDefault=Convert.ToBoolean(Member(c.Request,"HasDefValue"),CultureInfo.InvariantCulture);
    string sentinel=Convert.ToString(Member(c.Request,kind=="IntValue"?"DefIntValue":"DefStrValue"),CultureInfo.InvariantCulture);
    if(hasDefault&&(sentinel=="-999"||kind=="StrValue"&&sentinel=="|")) {
     refreshSource=Member(Member(ConsoleObject,"emuera"),"getCurrentLine");
     if(refreshSource!=null) {
      var plainRows=new List<string>();foreach(object line in c.Lines)plainRows.Add(line.ToString());
      var semantics=new Dictionary<string,object>(c.Input);semantics.Remove("requestId");
      refreshSemantic=Hash(Json().Serialize(new object[]{semantics,c.Generation,c.NextGeneration,new List<string>(keys),plainRows}));refresh=true;
     }
    }
   } catch(MissingMemberException) { /* Unknown variants retain strict request identity. */ }
   bool continuous=refresh&&EpochRefreshEligible&&object.ReferenceEquals(EpochOwner,ConsoleObject)&&EpochState=="WaitInput"&&object.ReferenceEquals(EpochRefreshSource,refreshSource)&&EpochRefreshSemantic==refreshSemantic;
   bool sourceChanged=(refresh||EpochRefreshEligible)&&(refresh!=EpochRefreshEligible||!object.ReferenceEquals(EpochRefreshSource,refreshSource)||EpochRefreshSemantic!=refreshSemantic);
   if(!continuous&&(!object.ReferenceEquals(EpochOwner,ConsoleObject)||!object.ReferenceEquals(EpochRequest,c.Request)||EpochSemantic!=c.Semantic||EpochState!=c.State||sourceChanged))
    CurrentInputEpoch=SessionId+":input:"+(++EpochNumber).ToString(CultureInfo.InvariantCulture);
   EpochOwner=ConsoleObject;EpochRequest=c.Request;EpochSemantic=c.Semantic;EpochState=c.State;
   EpochRefreshEligible=refresh;EpochRefreshSource=refreshSource;EpochRefreshSemantic=refreshSemantic;
   c.Epoch=CurrentInputEpoch;
   c.CanAct=!Exited&&Failure==null&&c.State=="WaitInput"&&c.Request!=null&&(ValueKind(kind)&&needValue||KeyKind(kind)&&!needValue&&!c.Timed&&!c.SystemInput)&&!AwaitingTransition;
   if(ValueKind(kind)&&c.Timed&&!runningTimer)c.CanAct=false; // expired timer must not accept a late decision
   return c;
  }
  static void ConsumeInput(Control c) {
   SubmittedOwner=ConsoleObject;SubmittedRequest=c.Request;SubmittedSemantic=c.Semantic;AwaitingTransition=true;
   EpochRefreshEligible=false;EpochRefreshSource=null;EpochRefreshSemantic=null;
   CurrentInputEpoch=SessionId+":input:"+(++EpochNumber).ToString(CultureInfo.InvariantCulture);
  }
  static void RememberDelivered(Dictionary<string,object> result,Control c) {
   if(!c.CanAct)return;string id=S(result,"observationId");var d=new Delivered { Epoch=c.Epoch };
   foreach(var b in (List<object>)result["buttons"]) { var shown=(Dictionary<string,object>)b;foreach(var live in c.Buttons)if(S(live,"id")==S(shown,"id")&&(bool)live["active"])d.Choices[S(shown,"id")]=S(live,"key"); }
   lock(Gate) { if(!DeliveredObservations.ContainsKey(id))DeliveredOrder.Enqueue(id);DeliveredObservations[id]=d;while(DeliveredOrder.Count>128)DeliveredObservations.Remove(DeliveredOrder.Dequeue()); }
  }

  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect r);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h,out Rect r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h,ref Point p);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }

  [STAThread] public static int Main(string[] args) {
   if(args.Length!=1)return 2;
   try {
    Settings=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(args[0],Utf8));
    RuntimeDir=NormalDir(S(Settings,"runtimeDirectory"));ArtifactDir=Path.GetFullPath(S(Settings,"artifactDirectory"));
    SessionId=S(Settings,"sessionId");Directory.CreateDirectory(ArtifactDir);
    string enginePath=Path.GetFullPath(S(Settings,"enginePath"));
    // copy mode: the engine is a mirror inside the runtime directory and the host must
    // run from that same directory. inplace mode: the engine is the game's own file,
    // the host keeps its executable elsewhere, and the game folder is the working
    // directory so the engine resolves its own assets, saves and logs there.
    bool inplace=S(Settings,"runtimeMode","copy")=="inplace";
    if(inplace) {
     GameDir=NormalDir(S(Settings,"gameRoot"));
     if(!Directory.Exists(GameDir))throw new InvalidOperationException("Game folder is missing: "+GameDir);
     if(!enginePath.StartsWith(GameDir,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Inplace engine must be inside the game folder");
    } else {
     if(!enginePath.StartsWith(RuntimeDir,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Engine must be inside isolated runtime");
     if(NormalDir(AppDomain.CurrentDomain.BaseDirectory)!=RuntimeDir)throw new InvalidOperationException("Host must run from isolated runtime");
     GameDir=RuntimeDir;
    }
    if(FileHash(enginePath)!=S(Settings,"engineSha256").ToLowerInvariant())throw new InvalidOperationException("Engine SHA256 mismatch");
    Directory.SetCurrentDirectory(GameDir);SetProcessDPIAware();
    Engine=Assembly.LoadFrom(enginePath);
    // The adapter depends on engine members that are not part of a stable contract,
    // so the accepted identity comes from the session settings instead of a constant.
    string expectedName=S(Settings,"engineAssemblyName","Emuera"),expectedVersion=S(Settings,"engineAssemblyVersion");
    if(expectedVersion.Length==0)throw new InvalidOperationException("Session settings do not declare engineAssemblyVersion");
    var identity=Engine.GetName();
    if(identity.Name!=expectedName||identity.Version.ToString()!=expectedVersion)throw new NotSupportedException("Unverified engine: found "+identity.FullName+", expected "+expectedName+" "+expectedVersion+"; adapter refused");
    // This old build obtains its directory/name from Sys. Keep its own version identity.
    var sys=Engine.GetType("MinorShift._Library.Sys",true);
    var exeDir=sys.GetField("ExeDir",Members);var exeName=sys.GetField("ExeName",Members);
    if(exeDir==null||exeName==null)throw new MissingFieldException("Sys path contract unavailable");
    exeDir.SetValue(null,GameDir);exeName.SetValue(null,Path.GetFileName(enginePath));
    Record("startup",Map("sessionId",SessionId,"engineAssembly",Engine.FullName,"engineSha256",FileHash(enginePath),"runtimeMode",S(Settings,"runtimeMode","copy"),"gameDir",GameDir,"runtimeDirectory",RuntimeDir,"processId",System.Diagnostics.Process.GetCurrentProcess().Id));
    Application.Idle+=OnIdle;
    Server=new Thread(Serve);Server.IsBackground=true;Server.Name="eraDSH local IPC";Server.Start();
    Engine.EntryPoint.Invoke(null,new object[]{new string[0]});
    return Failure==null?0:1;
   } catch(Exception ex) {
    Failure=Root(ex).ToString();
    if(ArtifactDir!=null)File.WriteAllText(Path.Combine(ArtifactDir,"host-failure.txt"),Failure,Utf8);
    return 1;
   } finally { Exited=true;Ready.Set();if(Server!=null)Server.Join(1000); }
  }
  static Exception Root(Exception ex) { while((ex is TargetInvocationException||ex is AggregateException)&&ex.InnerException!=null)ex=ex.InnerException;return ex; }
  static void OnIdle(object sender,EventArgs e) {
   if(Window!=null)return;
   foreach(Form f in Application.OpenForms)if(f.GetType().FullName=="MinorShift.Emuera.MainWindow") {
    if(Failure!=null){Ready.Set();f.Close();return;}
    object c=Field(f,"console");if(c==null)continue;
    string actual=Convert.ToString(Engine.GetType("MinorShift.Emuera.Program").GetProperty("ExeDir",Members).GetValue(null,null));
    if(NormalDir(actual)!=GameDir) { Failure="Runtime directory isolation mismatch";Ready.Set();f.Close();return; }
    ConsoleObject=c;Window=f;Ready.Set();Record("ready",Map("title",f.Text,"runtimeDirectory",actual));return;
   }
  }
  sealed class Work { public int State;public readonly TaskCompletionSource<object> Completion=new TaskCompletionSource<object>(); }
  static object OnUI(Func<object> op,int timeoutMs=1500) {
   if(!Ready.WaitOne(0)||Window==null||Window.IsDisposed||ConsoleObject==null)return Error("NOT_READY",Failure??"Engine has no attached window");
   var work=new Work();
   try { Window.BeginInvoke((Action)delegate {
    if(Interlocked.CompareExchange(ref work.State,1,0)!=0)return;
    try { work.Completion.SetResult(op()); }
    catch(Exception ex) { var root=Root(ex);work.Completion.SetResult(root is MissingMemberException?ContractError(root):Error("ENGINE_ERROR",root.Message)); }
    finally { Interlocked.Exchange(ref work.State,2); }
   }); } catch(InvalidOperationException) { return Error("NOT_READY","Engine window is unavailable"); }
   if(work.Completion.Task.Wait(Math.Max(1,Math.Min(timeoutMs,5000))))return work.Completion.Task.Result;
   bool cancelled=Interlocked.CompareExchange(ref work.State,3,0)==0;
   return Error(cancelled?"CANCELLED_BEFORE_EXECUTION":"OUTCOME_UNCERTAIN",cancelled?"UI request cancelled before execution":"UI operation started; do not repeat the action");
  }
  static Dictionary<string,object> Observe(Dictionary<string,object> request) {
   var answer=(Dictionary<string,object>)OnUI(delegate{return Snapshot(request);},Budget(request,1500,5000));
   string code=S(answer,"code");
   if(code=="NOT_READY"||code=="CANCELLED_BEFORE_EXECUTION"||code=="OUTCOME_UNCERTAIN")return Pending("engine-busy");
   return answer;
  }
  static Dictionary<string,object> Status(Dictionary<string,object> request) {
   var answer=(Dictionary<string,object>)OnUI(delegate { var c=ReadControl();var status=Map("ok",true,"state",c.State);AddLifecycle(status,c);return status; },Budget(request,500,1000));
   string code=S(answer,"code");if(code=="NOT_READY"||code=="CANCELLED_BEFORE_EXECUTION"||code=="OUTCOME_UNCERTAIN")answer=Pending("engine-busy");
   answer["sessionId"]=SessionId;answer["exited"]=Exited;answer["failure"]=Failure;answer["engineAssembly"]=Engine==null?null:Engine.FullName;answer["engineFamily"]="framework";answer["runtimeMode"]=Settings==null?"copy":S(Settings,"runtimeMode","copy");answer["gameDir"]=GameDir;answer["runtimeDirectory"]=RuntimeDir;answer["processId"]=System.Diagnostics.Process.GetCurrentProcess().Id;
   return answer;
  }
  [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length;public IntPtr Descriptor;public int Inherit; }
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern Microsoft.Win32.SafeHandles.SafePipeHandle CreateNamedPipe(string name,uint openMode,uint pipeMode,uint instances,uint outSize,uint inSize,uint timeout,ref SecurityAttributes attributes);
  static NamedPipeServerStream NewPipe(PipeSecurity security) {
   byte[] descriptor=security.GetSecurityDescriptorBinaryForm();var pinned=GCHandle.Alloc(descriptor,GCHandleType.Pinned);
   try {
    var attributes=new SecurityAttributes { Length=Marshal.SizeOf(typeof(SecurityAttributes)),Descriptor=pinned.AddrOfPinnedObject(),Inherit=0 };
    // FIRST_PIPE_INSTANCE + OVERLAPPED + DUPLEX; reject remote clients.
    var handle=CreateNamedPipe("\\\\.\\pipe\\"+S(Settings,"pipeName"),0x40080003,8,1,8192,8192,0,ref attributes);
    if(handle.IsInvalid){handle.Dispose();throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
    try { return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle); }catch{handle.Dispose();throw;}
   }finally{pinned.Free();}
  }
  static string ReadRequest(StreamReader reader) {
   var text=new StringBuilder(1024);var chunk=new char[1024];var clock=System.Diagnostics.Stopwatch.StartNew();
   while(true) {
    int remaining=5000-(int)clock.ElapsedMilliseconds;if(remaining<=0)throw new IOException("Request read timed out");
    var task=reader.ReadAsync(chunk,0,chunk.Length);if(!task.Wait(remaining))throw new IOException("Request read timed out");
    int count=task.Result;if(count==0)throw new IOException("Request ended before newline");
    int newline=Array.IndexOf(chunk,'\n',0,count);int used=newline<0?count:newline;
    if(text.Length+used>32768)throw new IOException("Request exceeds 32768 characters");
    text.Append(chunk,0,used);if(newline>=0)return text.ToString().TrimEnd('\r');
   }
  }
  static string SerializeResponse(object response) {
   try { string json=Json().Serialize(response);if(Utf8.GetByteCount(json)>2*1024*1024)throw new InvalidOperationException("Response exceeds 2 MiB");return json; }
   catch(Exception){return Json().Serialize(Error("RESPONSE_LIMIT","Response exceeded the bounded output budget; request a smaller page"));}
  }
  static void Serve() {
   try {
    var sid=WindowsIdentity.GetCurrent().User;var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);security.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
    while(!Exited)using(var pipe=NewPipe(security)) {
     pipe.WaitForConnection();bool close=false;
     try {
      using(var reader=new StreamReader(pipe,Utf8,false,8192,true))using(var writer=new StreamWriter(pipe,Utf8,8192,true)) {
       object response;
       try {
        var request=Json().Deserialize<Dictionary<string,object>>(ReadRequest(reader));
        if(!String.Equals(S(request,"token"),S(Settings,"token"),StringComparison.Ordinal))response=Error("UNAUTHORIZED","Local session credential required");
        else { close=S(request,"action")=="close";response=Dispatch(request); }
       }catch(Exception ex){response=Error("REQUEST_ERROR",Root(ex).Message);}
       var write=writer.WriteLineAsync(SerializeResponse(response));if(!write.Wait(5000))throw new IOException("Response write timed out");
       var flush=writer.FlushAsync();if(!flush.Wait(5000))throw new IOException("Response flush timed out");
      }
     }catch(Exception ex){Record("connection-error",Root(ex).Message);}
     if(close) { if(Window!=null&&!Window.IsDisposed)Window.BeginInvoke((Action)delegate{Window.Close();});return; }
    }
   } catch(Exception ex) { if(!Exited){Failure=Root(ex).ToString();File.WriteAllText(Path.Combine(ArtifactDir,"host-failure.txt"),Failure,Utf8);Record("ipc-error",Failure);if(Window!=null&&!Window.IsDisposed)Window.BeginInvoke((Action)delegate{Window.Close();});} }
  }
  static object Dispatch(Dictionary<string,object> request) {
   return CompactResult(DispatchCore(request),string.Equals(S(request,"render","full"),"compact",StringComparison.OrdinalIgnoreCase));
  }
  static object DispatchCore(Dictionary<string,object> request) {
   string action=S(request,"action");
   if(action=="status")return Status(request);
   if(action=="close")return Map("ok",true,"status","normal_close_scheduled");
   if(action=="observe")return Observe(request);
   if(action=="advance")return Advance(request);
   if(action!="act")return Error("UNKNOWN_ACTION","Use status, observe, act, or close");
   string actionId=S(request,"actionId");Guid parsed;
   if(!Guid.TryParse(actionId,out parsed))return Error("INVALID_ACTION_ID","A unique GUID actionId is required");
   actionId=parsed.ToString("D");request["actionId"]=actionId;
   lock(Gate) {
    Dictionary<string,object> prior;if(Actions.TryGetValue(actionId,out prior))return Map("ok",false,"code","DUPLICATE_ACTION","action",new Dictionary<string,object>(prior));
    if(Actions.Count>=10000)return Error("ACTION_LIMIT","Session action limit reached");
    Actions[actionId]=Map("actionId",actionId,"state","queued");
   }
   Record("action-intent",Map("actionId",actionId,"observationId",S(request,"observationId"),"kind",S(request,"kind"),"value",S(request,"value"),"choiceId",S(request,"choiceId")));
   int actionBudget=Budget(request,6000,6000);var actionClock=System.Diagnostics.Stopwatch.StartNew();
   var result=(Dictionary<string,object>)OnUI(delegate {
    lock(Gate)Actions[actionId]["state"]="executing";
    object answer;
    try { answer=Act(request);lock(Gate){Actions[actionId]["state"]="completed";Actions[actionId]["result"]=answer;} }
    catch(Exception ex){answer=Error("ACTION_OUTCOME_UNCERTAIN",Root(ex).Message);lock(Gate){Actions[actionId]["state"]="uncertain";Actions[actionId]["result"]=answer;} }
    Record("action-result",Map("actionId",actionId,"result",answer));return answer;
   },Math.Min(actionBudget,5000));
   lock(Gate) {
    if(S(Actions[actionId],"state")=="queued") { Actions[actionId]["state"]=S(result,"code")=="CANCELLED_BEFORE_EXECUTION"||S(result,"code")=="NOT_READY"?"cancelled":"uncertain";Actions[actionId]["result"]=result; }
    else if(S(Actions[actionId],"state")=="executing"&&S(result,"code")=="OUTCOME_UNCERTAIN") { Actions[actionId]["state"]="uncertain";Actions[actionId]["result"]=result; }
   }
   if(result.ContainsKey("submitted")&&Convert.ToBoolean(result["submitted"],CultureInfo.InvariantCulture)) {
    // Yield to the message loop before sampling the result; never promise synchronous readiness.
    int remaining=actionBudget-(int)actionClock.ElapsedMilliseconds;
    var after=remaining>0?Observe(PostActionOptions(request,Math.Min(remaining,750))):Pending("post-action-budget");
    result["observation"]=after;result["observationChanged"]=S(after,"observationId")!=S(request,"observationId");
   }
   return result;
  }
  // A cumulative display buffer re-sends old screens on every step. The caller already
  // holds the screen it acted on, so the reply carries only what the action added or
  // rewrote; an unknown base falls back to the full text inside SnapshotCore.
  static Dictionary<string,object> PostActionOptions(Dictionary<string,object> request,int timeoutMs) {
   var options=Map("mode",S(request,"mode","text"),"render",S(request,"render","full"),"limitLines",I(request,"limitLines",80),"timeoutMs",timeoutMs);
   bool compact=string.Equals(S(request,"render","full"),"compact",StringComparison.OrdinalIgnoreCase);
   string claimed=S(request,"observationId");
   if(compact&&!string.IsNullOrEmpty(claimed))options["sinceObservationId"]=claimed;
   return options;
  }
  static string Clip(string text,int length) { return text!=null&&text.Length>length?text.Substring(0,length):text; }
  static void DigestPart(StreamWriter writer,string value) { value=value??"";writer.Write(value.Length.ToString(CultureInfo.InvariantCulture));writer.Write(':');writer.Write(value); }
  static string ObservationHash(string state,object input,string full,IList list,int currentGeneration) {
   using(var sha=SHA256.Create())using(var stream=new CryptoStream(Stream.Null,sha,CryptoStreamMode.Write)) {
    using(var writer=new StreamWriter(stream,Utf8,8192,true)) {
     DigestPart(writer,state);DigestPart(writer,Json().Serialize(input));DigestPart(writer,full);DigestPart(writer,Window.ClientSize.ToString());DigestPart(writer,currentGeneration.ToString(CultureInfo.InvariantCulture));
     foreach(object line in list)foreach(object b in (Array)Prop(line,"Buttons"))if((bool)Prop(b,"IsButton")) {
      DigestPart(writer,Convert.ToString(Field(line,"LineNo"),CultureInfo.InvariantCulture));DigestPart(writer,Convert.ToString(Prop(b,"Generation"),CultureInfo.InvariantCulture));
      bool integer=(bool)Prop(b,"IsInteger");DigestPart(writer,integer?"integer":"text");DigestPart(writer,Convert.ToString(Prop(b,integer?"Input":"Inputs"),CultureInfo.InvariantCulture));
     }
    }
    stream.FlushFinalBlock();return BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
   }
  }
  // Era engines print a wall of loading chatter at startup. The wording is localized
  // ("読み込み中", "读取 ABL.CSV 中", "Loading ..."), so a line counts as noise when it
  // names a script/data file AND carries a progress word, or starts with one outright.
  static bool IsLoadNoise(string text) {
   if(string.IsNullOrEmpty(text))return false;
   var t=text.Trim();
   if(t.Length==0||t.Length>260)return false;
   if(t.StartsWith("読み込み",StringComparison.Ordinal)||t.StartsWith("ロード",StringComparison.Ordinal)||
      t.StartsWith("读取",StringComparison.Ordinal)||t.StartsWith("讀取",StringComparison.Ordinal)||
      t.StartsWith("加载",StringComparison.Ordinal)||t.StartsWith("載入",StringComparison.Ordinal)||
      t.StartsWith("Now Loading",StringComparison.OrdinalIgnoreCase)||t.StartsWith("Loading",StringComparison.OrdinalIgnoreCase))return true;
   // EraFL and other large scripts print their compile summary as plain prose.
   bool compileSummary=t.StartsWith("正在构造用户定义函数列表",StringComparison.Ordinal)||
    t.StartsWith("正在验证脚本语法",StringComparison.Ordinal)||
    t.StartsWith("非注释行数",StringComparison.Ordinal)||
    (t.StartsWith("正在",StringComparison.Ordinal)&&
     (t.IndexOf("関数",StringComparison.Ordinal)>=0||t.IndexOf("函数",StringComparison.Ordinal)>=0||t.IndexOf("構文",StringComparison.Ordinal)>=0||t.IndexOf("语法",StringComparison.Ordinal)>=0||t.IndexOf("読み込",StringComparison.Ordinal)>=0||t.IndexOf("读取",StringComparison.Ordinal)>=0));
   if(compileSummary)return true;
   var lower=t.ToLowerInvariant();
   if(lower.IndexOf(".erb",StringComparison.Ordinal)<0&&lower.IndexOf(".erh",StringComparison.Ordinal)<0&&lower.IndexOf(".csv",StringComparison.Ordinal)<0)return false;
   if(t.IndexOf("读取",StringComparison.Ordinal)>=0||t.IndexOf("讀取",StringComparison.Ordinal)>=0||
      t.IndexOf("加载",StringComparison.Ordinal)>=0||t.IndexOf("載入",StringComparison.Ordinal)>=0||
      t.IndexOf("読み込み",StringComparison.Ordinal)>=0||t.IndexOf("ロード",StringComparison.Ordinal)>=0||
      lower.IndexOf("loading",StringComparison.Ordinal)>=0)return true;
   return t.EndsWith("中・・・",StringComparison.Ordinal)||t.EndsWith("中...",StringComparison.Ordinal)||t.EndsWith("中…",StringComparison.Ordinal);
  }

  // Era screens are separated by a long run of one character; keep the fact, drop
  // the bytes.
  static string CollapseSeparator(string text) {
   if(string.IsNullOrEmpty(text)||text.Length<40)return text;
   var t=text.TrimEnd();
   if(t.Length<40||char.IsWhiteSpace(t[0]))return text;
   var first=t[0];
   for(int i=1;i<t.Length;i++)if(t[i]!=first)return text;
   return first+" ×"+t.Length.ToString(CultureInfo.InvariantCulture);
  }

  // ---- presentation compaction (render=compact) ----
  // An Era screen is mostly layout: rules, frames, empty card slots. Those bytes are
  // re-sent on every observation and carry almost no decision content. Compact view
  // rewrites only what the caller reads:
  //   * a decoration-only line becomes "▦(<width>)", so the layout scale survives;
  //   * a long run of one decoration character becomes "<char>×N";
  //   * identical neighbours fold into "<line> ×N".
  // Observation identity, button ids and engine line numbers are untouched: the
  // projection is presentation, and noise=raw / render=full still return everything.
  const string DecorationChars=" \u00A0\u3000─━│┃┌┐└┘├┤┬┴┼╭╮╯╰═║╔╗╚╝╠╣╦╩╬-‐‑‒–—―_=＝＿~～·・‥…．.／/＼\\｜|△▽▲▼◆◇○●◎☆★※＊*";
  static bool IsDecorationChar(char c) { return char.IsWhiteSpace(c)||DecorationChars.IndexOf(c)>=0; }
  static bool IsDecorationLine(string text) {
   for(int i=0;i<text.Length;i++)if(!IsDecorationChar(text[i]))return false;
   return true;
  }
  static string CompressRuns(string text,out bool compressed) {
   compressed=false;
   if(string.IsNullOrEmpty(text)||text.Length<8)return text;
   var builder=new StringBuilder(text.Length);int index=0;
   while(index<text.Length) {
    char c=text[index];int end=index+1;
    while(end<text.Length&&text[end]==c)end++;
    int length=end-index;
    if(length>=8&&IsDecorationChar(c)) {
     builder.Append(c).Append('×').Append(length.ToString(CultureInfo.InvariantCulture));compressed=true;
    } else builder.Append(text,index,length);
    index=end;
   }
   return builder.ToString();
  }
  // Rewrites the delivered line page into its compact projection. Engine indices ride
  // along so a cursor still points at the same absolute row; counters are reported.
  static void CompactDisplay(List<string> source,List<int> sourceIndex,
      out List<string> target,out List<int> targetIndex,
      out int decorationLines,out int mergedLines,out int compressedRuns) {
   decorationLines=0;mergedLines=0;compressedRuns=0;
   var rendered=new List<string>(source.Count);
   foreach(var line in source) {
    if(line.Trim().Length==0)rendered.Add(string.Empty);
    else if(IsDecorationLine(line)) {
     rendered.Add("▦("+line.TrimEnd().Length.ToString(CultureInfo.InvariantCulture)+")");decorationLines++;
    } else {
     bool compressed;var shortLine=CompressRuns(CompactTags(line),out compressed);if(compressed)compressedRuns++;rendered.Add(shortLine);
    }
   }
   target=new List<string>(rendered.Count);targetIndex=new List<int>(rendered.Count);
   int position=0;
   while(position<rendered.Count) {
    string line=rendered[position];int run=position+1;
    while(run<rendered.Count&&string.Equals(rendered[run],line,StringComparison.Ordinal))run++;
    int count=run-position;
    targetIndex.Add(sourceIndex[position]);
    mergedLines+=count-1;
    // A folded run of blank lines stays blank: " ×37" marks content that is not there,
    // and the count is already reported by mergedRepeatedLines.
    if(line.Length==0)target.Add(string.Empty);
    else target.Add(count>1?line+" ×"+count.ToString(CultureInfo.InvariantCulture):line);
    position=run;
   }
  }
  // Era layout tags describe geometry, not content: "height='600px' width='640px'" is
  // re-sent on every screen and never changes a decision. Keep the identity of a
  // picture, drop the placement, and drop closing tags outright.
  static readonly string[] LayoutTags=new string[]{"img","div","span","shape","font","br","p","b","i","ruby","rt","table","tr","td"};
  static string CompactTags(string text) {
   if(text.IndexOf('<')<0)return text;
   var builder=new StringBuilder(text.Length);int index=0;
   while(index<text.Length) {
    int open=text.IndexOf('<',index);
    if(open<0){builder.Append(text,index,text.Length-index);break;}
    builder.Append(text,index,open-index);
    int close=text.IndexOf('>',open+1);
    if(close<0||close-open>200){builder.Append(text,open,text.Length-open);break;}
    string tag=text.Substring(open+1,close-open-1).Trim();bool known;
    builder.Append(CompactTag(tag,out known));
    if(!known)builder.Append(text,open,close-open+1);
    index=close+1;
   }
   return builder.ToString();
  }
  static string CompactTag(string tag,out bool known) {
   known=false;
   if(tag.Length==0)return string.Empty;
   bool closing=tag[0]=='/';
   string body=closing?tag.Substring(1).Trim():tag;
   int space=body.IndexOfAny(new char[]{' ','\t'});
   string name=(space<0?body:body.Substring(0,space)).Trim().ToLowerInvariant();
   if(name.Length==0||Array.IndexOf(LayoutTags,name)<0)return string.Empty;
   known=true;
   if(closing)return string.Empty;
   if(name=="img") {
    string src=TagAttribute(body,"src");
    return src.Length>0?"⟨img "+src+"⟩":"⟨img⟩";
   }
   return "⟨"+name+"⟩";
  }
  static string TagAttribute(string tag,string attribute) {
   string key=attribute+"=";int at=tag.IndexOf(key,StringComparison.OrdinalIgnoreCase);
   if(at<0)return string.Empty;
   int value=at+key.Length;if(value>=tag.Length)return string.Empty;
   char quote=tag[value];
   if(quote=='\''||quote=='"') {int end=tag.IndexOf(quote,value+1);return end<0?string.Empty:tag.Substring(value+1,end-value-1);}
   int stop=tag.IndexOf(' ',value);return (stop<0?tag.Substring(value):tag.Substring(value,stop-value)).Trim();
  }
  // Output-only projection: drops fields a decision never reads and keeps the few
  // button facts an action needs. Delivery is recorded before this runs, so trimming
  // the reply cannot weaken authorization.
  static readonly string[] DiagnosticFields=new string[] {
   "displayHash","consistency","source","engineFamily","projectionId","buttonGeneration",
   "rawTextLength","characterBudget","textCharacterBudget","window","capturedUtc","sessionId",
   "returnedLines","omittedAfter","cursorBase","ready",
  };
  static bool Flag(object value) { return value is bool&&(bool)value; }
  static string CompactLabel(string label) {
   if(string.IsNullOrEmpty(label)||label.Length<3)return label;
   var builder=new StringBuilder(label.Length);int index=0;
   while(index<label.Length) {
    char c=label[index];
    if(char.IsWhiteSpace(c)) {
     while(index<label.Length&&char.IsWhiteSpace(label[index]))index++;
     builder.Append(' ');continue;
    }
    builder.Append(c);index++;
   }
   return builder.ToString();
  }
  static Dictionary<string,object> CompactView(Dictionary<string,object> snapshot) {
   if(snapshot==null)return null;
   var output=new Dictionary<string,object>(snapshot,StringComparer.Ordinal);
   foreach(var field in DiagnosticFields)output.Remove(field);
   object buttonsValue;
   if(output.TryGetValue("buttons",out buttonsValue)&&buttonsValue is List<object>) {
    var slim=new List<object>();
    foreach(var entry in (List<object>)buttonsValue) {
     var button=entry as Dictionary<string,object>;
     if(button==null){slim.Add(entry);continue;}
     object pressed;button.TryGetValue("integer",out pressed);bool integer=Flag(pressed);
     object value;button.TryGetValue("value",out value);
     var shown=new Dictionary<string,object> { { "id", button.ContainsKey("id")?button["id"]:null }, { "label", CompactLabel(Convert.ToString(button.ContainsKey("label")?button["label"]:null)) } };
     if(integer)shown["integer"]=true;
     object choosable;button.TryGetValue("choosable",out choosable);
     if(!Flag(choosable))shown["choosable"]=false;
     object active;button.TryGetValue("active",out active);
     if(!Flag(active))shown["active"]=false;
     if(integer&&value is string&&((string)value).Length>0&&((string)value).Length<=12)shown["value"]=value;
     slim.Add(shown);
    }
    output["buttons"]=slim;
   }
   object inputValue;
   if(output.TryGetValue("input",out inputValue)&&inputValue is Dictionary<string,object>) {
    var input=(Dictionary<string,object>)inputValue;var compacted=new Dictionary<string,object>();
    object kind;input.TryGetValue("kind",out kind);compacted["kind"]=kind;
    object flag;
    if(input.TryGetValue("needValue",out flag)&&Flag(flag))compacted["needValue"]=true;
    if(input.TryGetValue("timed",out flag)&&Flag(flag))compacted["timed"]=true;
    if(input.TryGetValue("systemInput",out flag)&&Flag(flag))compacted["systemInput"]=true;
    object limit;
    if(input.TryGetValue("timeLimit",out limit)&&Convert.ToInt64(limit,CultureInfo.InvariantCulture)>0)compacted["timeLimit"]=limit;
    if(input.TryGetValue("contractValid",out flag)&&flag is bool&&!(bool)flag)compacted["contractValid"]=false;
    object missing;
    if(input.TryGetValue("missingMembers",out missing))compacted["missingMembers"]=missing;
    output["input"]=compacted;
   }
   foreach(var nested in new string[]{"observation","current"}) {
    object value;
    if(output.TryGetValue(nested,out value)&&value is Dictionary<string,object>)output[nested]=CompactView((Dictionary<string,object>)value);
   }
   return output;
  }
  static object CompactResult(object answer,bool compact) {
   if(!compact||answer==null)return answer;
   var snapshot=answer as Dictionary<string,object>;
   if(snapshot==null||(!snapshot.ContainsKey("text")&&!snapshot.ContainsKey("observation")))return answer;
   return CompactView(snapshot);
  }

  // ---- incremental observations ----
  // A caller that already holds an observation can ask for the difference. The
  // comparison runs over the lines as delivered (filtering already applied), so the
  // indices match what the caller saw; the observation id still hashes the whole
  // buffer, so a delta never weakens the stale check for actions.
  static readonly Dictionary<string,List<string>> DeltaHistory=new Dictionary<string,List<string>>();
  static readonly Queue<string> DeltaOrder=new Queue<string>();

  static void RememberHistory(string id,List<string> displayed) {
   if(string.IsNullOrEmpty(id))return;
   lock(DeltaHistory) {
    if(!DeltaHistory.ContainsKey(id))DeltaOrder.Enqueue(id);
    DeltaHistory[id]=new List<string>(displayed);
    while(DeltaOrder.Count>8)DeltaHistory.Remove(DeltaOrder.Dequeue());
   }
  }

  static List<object> DeltaLines(List<string> displayed,List<int> indices,int fromOrdinal) {
   var list=new List<object>();
   for(int i=fromOrdinal;i<displayed.Count;i++)list.Add(Map("index",indices[i],"text",displayed[i]));
   return list;
  }

  static void ApplyDelta(Dictionary<string,object> result,string since,List<string> displayed,List<int> indices) {
   var delta=new Dictionary<string,object>();
   List<string> previous=null;
   lock(DeltaHistory)DeltaHistory.TryGetValue(since,out previous);
   if(previous==null) {
    delta["kind"]="full";delta["sinceMissing"]=true;result["delta"]=delta;return;
   }
   int common=0;
   while(common<previous.Count&&common<displayed.Count&&string.Equals(previous[common],displayed[common],StringComparison.Ordinal))common++;
   if(common==previous.Count&&common==displayed.Count) {
    delta["kind"]="none";result["text"]=null;result["lines"]=null;result["delta"]=delta;return;
   }
   int tail=displayed.Count-common;
   // A delta carries per-line JSON overhead, so it only wins while the reply is mostly
   // old screen. Once the change covers most of the window the plain text is smaller.
   if(tail>200||tail*5>=displayed.Count*3) {
    delta["kind"]="full";
    delta["reason"]=tail>200?"delta exceeds 200 lines":"delta covers most of the window";
    result["delta"]=delta;return;
   }
   delta["kind"]=common==previous.Count?"append":"changed";
   delta["fromOrdinal"]=common;
   delta["fromIndex"]=common<indices.Count?indices[common]:-1;
   delta["removed"]=common==previous.Count?0:previous.Count-common;
   delta["lines"]=DeltaLines(displayed,indices,common);
   result["delta"]=delta;result["text"]=null;result["lines"]=null;
  }

  // Each narration step is one short UI operation, with a message-loop turn between steps.
  static Dictionary<string,object> AdvanceStep() {
   Control c;try { c=ReadControl(); }catch(MissingMemberException ex) { return ContractError(ex); }
   if(!c.CanAct)return Map("ok",true,"submitted",false,"stoppedBecause",c.Timed?"timed-wait":"not-waiting-input");
   if(!KeyKind(S(c.Input,"kind"))||(bool)c.Input["needValue"])return Map("ok",true,"submitted",false,"stoppedBecause","input-required");
   ConsumeInput(c);Call(ConsoleObject,"PressEnterKey",new[]{typeof(bool),typeof(string),typeof(bool)},false,"",false);
   return Map("ok",true,"submitted",true);
  }
  static Dictionary<string,object> Advance(Dictionary<string,object> request) {
   int maxSteps=Math.Max(1,Math.Min(200,I(request,"maxSteps",20))),budget=Budget(request,8000,8000),steps=0;
   var clock=System.Diagnostics.Stopwatch.StartNew();string stopped="max-steps";
   Dictionary<string,object> snapshot=null;
   // The screen the caller is looking at when it asks for narration. The final
   // projection is expressed relative to it, so a long Enter run does not re-send the
   // whole window it started from.
   string initialId=null;
   // Sample the screen before the first Enter: that is what the caller is looking at,
   // so the final delta must be measured from it, not from a post-press screen.
   var start=Observe(Map("mode",S(request,"mode","text"),"render",S(request,"render","full"),"limitLines",I(request,"limitLines",80),"timeoutMs",Math.Min(budget,750)));
   if(start!=null) {initialId=S(start,"observationId");snapshot=start;}
   while(steps<maxSteps) {
    int remaining=budget-(int)clock.ElapsedMilliseconds;if(remaining<=0){stopped="pending";break;}
    var step=(Dictionary<string,object>)OnUI(delegate { return AdvanceStep(); },Math.Min(remaining,1000));
    if(!step.ContainsKey("submitted")||!Convert.ToBoolean(step["submitted"],CultureInfo.InvariantCulture)) {
     stopped=S(step,"stoppedBecause",S(step,"code","not-waiting-input"));break;
    }
    steps++;
    remaining=budget-(int)clock.ElapsedMilliseconds;if(remaining<=0){stopped="pending";break;}
    snapshot=Observe(Map("mode","text","render",S(request,"render","full"),"limitLines",I(request,"limitLines",80),"timeoutMs",Math.Min(remaining,750)));
    if(!snapshot.ContainsKey("canAct")||!Convert.ToBoolean(snapshot["canAct"],CultureInfo.InvariantCulture)){stopped="pending";break;}
   }
   int left=budget-(int)clock.ElapsedMilliseconds;
   if(left>0) {
    var finalOptions=Map("mode",S(request,"mode","text"),"render",S(request,"render","full"),"limitLines",I(request,"limitLines",80),"timeoutMs",Math.Min(left,750));
    bool compact=string.Equals(S(request,"render","full"),"compact",StringComparison.OrdinalIgnoreCase);
    if(compact&&!string.IsNullOrEmpty(initialId))finalOptions["sinceObservationId"]=initialId;
    snapshot=Observe(finalOptions);
   }
   if(snapshot==null)snapshot=Pending("advance-budget");
   return Map("ok",true,"steps",steps,"stoppedBecause",stopped,"observation",snapshot);
  }

  static Dictionary<string,object> Snapshot(Dictionary<string,object> request) {
   try { return SnapshotCore(request); }catch(MissingMemberException ex) { return ContractError(ex); }
  }
  static Dictionary<string,object> SnapshotCore(Dictionary<string,object> request) {
   int limit=I(request,"limitLines",80);if(limit<1||limit>200)throw new ArgumentOutOfRangeException("limitLines","Use 1..200");
   string mode=S(request,"mode","text");if(mode!="text"&&mode!="visual"&&mode!="both"&&mode!="auto")throw new ArgumentException("Invalid observation mode");
   var buffer=new StringBuilder(1024,8*1024*1024);Call(ConsoleObject,"GetDisplayStrings",new Type[]{typeof(StringBuilder)},buffer);
   if(buffer.Length>8*1024*1024)throw new InvalidOperationException("Engine display buffer exceeds the 8 Mi-character internal limit");
   var control=ReadControl();string full=buffer.ToString(),state=control.State;var input=control.Input;
   var list=control.Lines;int start=Math.Max(0,list.Count-limit);if(request.ContainsKey("fromLine"))start=Math.Max(0,Math.Min(list.Count,I(request,"fromLine",0)));
   // A cursor is expressed in the engine's absolute line space (deleted + buffered)
   // so it keeps meaning when the engine trims old rows.
   long deletedLines=0;
   var deletedProperty=ConsoleObject.GetType().GetProperty("DeletedLines",Members);
   if(deletedProperty!=null)deletedLines=Convert.ToInt64(deletedProperty.GetValue(ConsoleObject,null)??0L,CultureInfo.InvariantCulture);
   else { var deletedField=ConsoleObject.GetType().GetField("deletedLines",Members); if(deletedField!=null)deletedLines=Convert.ToInt64(deletedField.GetValue(ConsoleObject)??0L,CultureInfo.InvariantCulture); }
   if(deletedLines<0)deletedLines=0;
   bool cursorExpired=false;
   if(request.ContainsKey("sinceCursor")) {
    var requested=Convert.ToInt64(request["sinceCursor"],CultureInfo.InvariantCulture);
    if(requested<deletedLines||requested>deletedLines+list.Count)cursorExpired=true;
    else start=Math.Max(0,Math.Min(list.Count,(int)(requested-deletedLines)));
   }
   int currentGeneration=Convert.ToInt32(control.Generation,CultureInfo.InvariantCulture);
   bool includeInactive=request.ContainsKey("includeInactive")&&Convert.ToBoolean(request["includeInactive"],CultureInfo.InvariantCulture);
   // lines[] duplicates text one-for-one; act responses skip it unless the caller asks.
   bool includeLines=request.ContainsKey("includeLines")&&Convert.ToBoolean(request["includeLines"],CultureInfo.InvariantCulture);
   bool filterNoise=!request.ContainsKey("noise")||!string.Equals(S(request,"noise","filter"),"raw",StringComparison.OrdinalIgnoreCase);
   bool compactView=string.Equals(S(request,"render","full"),"compact",StringComparison.OrdinalIgnoreCase);
   var lines=new List<object>();var text=new StringBuilder();var buttons=new List<object>();int omittedButtons=0;int omittedInactive=0;int filteredNoise=0;int collapsed=0;bool textCut=false;int characterBudget=24000;
   var displayed=new List<string>();var displayedIndex=new List<int>();
   for(int n=start;n<list.Count&&n<start+limit;n++) {
    object line=list[n];string original=line.ToString();
    if(filterNoise&&IsLoadNoise(original)) {
     filteredNoise++;
     continue;
    }
    var display=filterNoise?CollapseSeparator(original):original;
    if(!string.Equals(display,original,StringComparison.Ordinal))collapsed++;
    displayed.Add(display);displayedIndex.Add(n);
   }
   // Compaction runs before the text page and the delta so every projection agrees on
   // the same line sequence, with the engine index still attached to each line.
   int layoutLines=0,mergedLines=0,compressedRuns=0;
   if(compactView) {
    List<string> compacted;List<int> compactedIndex;
    CompactDisplay(displayed,displayedIndex,out compacted,out compactedIndex,out layoutLines,out mergedLines,out compressedRuns);
    displayed=compacted;displayedIndex=compactedIndex;
   }
   for(int ordinal=0;ordinal<displayed.Count;ordinal++) {
    string display=displayed[ordinal];
    if(characterBudget<=0){textCut=true;continue;}
    int available=Math.Max(0,characterBudget-2);string value=Clip(display,available);bool cut=value.Length<display.Length;
    text.Append(value);text.Append("\r\n");characterBudget-=value.Length+2;textCut|=cut;
    if(includeLines)lines.Add(Map("index",displayedIndex[ordinal],"lineNo",Convert.ToInt32(Field(list[displayedIndex[ordinal]],"LineNo")),"text",value,"truncated",cut));
   }
   // Options are independent of the text page and the text character budget.
   foreach(var entry in control.Buttons) {
    if(!(bool)entry["active"]&&!includeInactive){omittedInactive++;continue;}
    if(buttons.Count==128){buttons.RemoveAt(0);omittedButtons++;}
    var shown=new Dictionary<string,object>(entry);shown.Remove("key");buttons.Add(shown);
   }
   string fingerprint=ObservationHash(state,input,full,list,currentGeneration);
   string view=S(request,"render","full");
   var result=Map("ok",true,"sessionId",SessionId,"observationId",SessionId+":"+Hash(fingerprint+":"+control.Epoch),"displayHash",fingerprint,"capturedUtc",DateTime.UtcNow.ToString("o"),"source","engine-display-buffer","consistency","same-ui-thread; animation frames are not revision-tracked","state",state,"input",input,"title",Clip(Window.Text,512),"text",mode=="visual"?null:text.ToString(),"lines",(mode=="visual"||!includeLines)?null:lines,"buttons",buttons,"buttonGeneration",currentGeneration,"omittedInactiveButtons",omittedInactive,"omittedButtons",omittedButtons,"filteredNoiseLines",filteredNoise,"collapsedSeparators",collapsed,"collapsedLayoutLines",layoutLines,"mergedRepeatedLines",mergedLines,"compressedRuns",compressedRuns,"cursor",deletedLines+list.Count,"nextCursor",deletedLines+Math.Min(list.Count,start+limit),"cursorBase",deletedLines,"cursorExpired",cursorExpired,"totalLines",list.Count,"fromLine",start,"returnedLines",includeLines?lines.Count:0,"omittedBefore",start,"omittedAfter",Math.Max(0,list.Count-start-limit),"rawTextLength",full.Length,"textTruncated",textCut,"textCharacterBudget",24000,"window",Map("handle",Window.Handle.ToInt64(),"clientWidth",Window.ClientSize.Width,"clientHeight",Window.ClientSize.Height));
   AddLifecycle(result,control);
   if(mode=="visual"||mode=="both"||mode=="auto")result["screenshot"]=Capture();
   if(request.ContainsKey("sinceObservationId")) {
    var since=Convert.ToString(request["sinceObservationId"]);
    if(!string.IsNullOrEmpty(since))ApplyDelta(result,since+":"+view,displayed,displayedIndex);
   }
   RememberHistory(Convert.ToString(result["observationId"])+":"+view,displayed);RememberDelivered(result,control);
   return result;
  }
  static object Capture() {
   if(IsIconic(Window.Handle))throw new InvalidOperationException("Window is minimized; no screenshot claimed");
   Rect r,c;GetWindowRect(Window.Handle,out r);GetClientRect(Window.Handle,out c);Point origin=new Point();ClientToScreen(Window.Handle,ref origin);
   int w=r.Right-r.Left,h=r.Bottom-r.Top;if(w<=0||h<=0||w>10000||h>10000)throw new InvalidOperationException("Invalid capture dimensions");
   string name="screen-"+Interlocked.Increment(ref ImageNumber).ToString("D5")+".png";string path=Path.Combine(ArtifactDir,name);
   using(var bitmap=new Bitmap(w,h,PixelFormat.Format32bppArgb)) {
    bool ok;using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Magenta);var dc=g.GetHdc();try{ok=PrintWindow(Window.Handle,dc,0);}finally{g.ReleaseHdc(dc);}}
    if(!ok)throw new InvalidOperationException("PrintWindow failed");
    var crop=new Rectangle(origin.X-r.Left,origin.Y-r.Top,c.Right-c.Left,c.Bottom-c.Top);
    if(crop.Width<=0||crop.Height<=0||crop.X<0||crop.Y<0||crop.Right>w||crop.Bottom>h)throw new InvalidOperationException("Invalid client crop");
    using(var client=bitmap.Clone(crop,bitmap.PixelFormat))client.Save(path,ImageFormat.Png);
    return Map("path",path,"width",crop.Width,"height",crop.Height,"region","game-client","method","PrintWindow","visualValidity","requires-viewing");
   }
  }
  static object Act(Dictionary<string,object> request) {
   Control c;try { c=ReadControl(); }catch(MissingMemberException ex) { return ContractError(ex); }
   var live=Lifecycle(c);live["state"]=c.State;live["input"]=c.Input;
   if(!c.CanAct)return Map("ok",false,"code","NOT_WAITING_INPUT","current",live);
   Delivered delivered;lock(Gate)DeliveredObservations.TryGetValue(S(request,"observationId"),out delivered);
   if(delivered==null||delivered.Epoch!=c.Epoch)return Map("ok",false,"code","STALE_OBSERVATION","current",live);
   string inputKind=S(c.Input,"kind"),kind=S(request,"kind"),value=S(request,"value");
   if(kind=="choose") {
    if(!(bool)c.Input["needValue"])return Error("WRONG_INPUT_KIND","Current request does not accept a button; use continue");
    string key;if(!delivered.Choices.TryGetValue(S(request,"choiceId"),out key))return Error("CHOICE_NOT_FOUND","Choice was not delivered with this observation");
    Dictionary<string,object> chosen=null;foreach(var b in c.Buttons)if((bool)b["active"]&&S(b,"key")==key){chosen=b;break;}
    if(chosen==null)return Error("STALE_BUTTON","Choice is not valid for the live input request");
    if(!(bool)chosen["choosable"])return Error("CHOICE_VALUE_TOO_LONG","Button value exceeds the action limit");value=S(chosen,"value");
   } else if(kind=="continue") {
    if(!KeyKind(inputKind)||c.Timed||c.SystemInput)return Error("WRONG_INPUT_KIND","Continue requires an untimed user EnterKey/AnyKey request");value="";
   } else if(kind!="input")return Error("INVALID_ACTION_KIND","Use choose, input, or continue");
   if(value.Length>2048||value.IndexOfAny(new char[]{'\r','\n','\0'})>=0||value.StartsWith("@",StringComparison.Ordinal))return Error("INVALID_INPUT","Input is bounded single-line text; debug commands are not exposed");
   long number;if(IntegerKind(inputKind)&&!Int64.TryParse(value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out number))return Error("INTEGER_REQUIRED","Current request expects an integer");
   if(kind!="continue"&&!ValueKind(inputKind))return Error("WRONG_INPUT_KIND","Use continue for key waits");
   // Validate and submit atomically on the UI thread. Do not sample a post-action screen here.
   ConsumeInput(c);Call(ConsoleObject,"PressEnterKey",new Type[]{typeof(bool),typeof(string),typeof(bool)},false,value,false);
   return Map("ok",true,"actionId",S(request,"actionId"),"submitted",true,"value",value,"inputEpoch",c.Epoch);
  }
 }
}
