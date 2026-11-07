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
  static object OnUI(Func<object> op) {
   if(!Ready.WaitOne(12000)||Window==null||Window.IsDisposed)return Error("NOT_READY",Failure??"Engine has no ready window");
   var work=new Work();
   Window.BeginInvoke((Action)delegate {
    if(Interlocked.CompareExchange(ref work.State,1,0)!=0)return;
    try { work.Completion.SetResult(op()); }catch(Exception ex){work.Completion.SetResult(Error("ENGINE_ERROR",Root(ex).Message));}
    finally { Interlocked.Exchange(ref work.State,2); }
   });
   if(work.Completion.Task.Wait(15000))return work.Completion.Task.Result;
   bool cancelled=Interlocked.CompareExchange(ref work.State,3,0)==0;
   return Error(cancelled?"CANCELLED_BEFORE_EXECUTION":"OUTCOME_UNCERTAIN",cancelled?"UI request cancelled before execution":"UI operation is running; do not repeat the action");
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
   string action=S(request,"action");
   if(action=="status")return Map("ok",true,"sessionId",SessionId,"ready",Window!=null,"exited",Exited,"failure",Failure,"engineAssembly",Engine.FullName,"runtimeMode",S(Settings,"runtimeMode","copy"),"gameDir",GameDir,"runtimeDirectory",RuntimeDir,"processId",System.Diagnostics.Process.GetCurrentProcess().Id);
   if(action=="close")return Map("ok",true,"status","normal_close_scheduled");
   if(action=="observe")return OnUI(delegate{return Snapshot(request);});
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
   var result=OnUI(delegate {
    lock(Gate)Actions[actionId]["state"]="executing";
    object answer;
    try { answer=Act(request);lock(Gate){Actions[actionId]["state"]="completed";Actions[actionId]["result"]=answer;} }
    catch(Exception ex){answer=Error("ACTION_OUTCOME_UNCERTAIN",Root(ex).Message);lock(Gate){Actions[actionId]["state"]="uncertain";Actions[actionId]["result"]=answer;} }
    Record("action-result",Map("actionId",actionId,"result",answer));return answer;
   });
   return result;
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
   if(tail>200) {
    delta["kind"]="full";delta["reason"]="delta exceeds 200 lines";result["delta"]=delta;return;
   }
   delta["kind"]=common==previous.Count?"append":"changed";
   delta["fromOrdinal"]=common;
   delta["fromIndex"]=common<indices.Count?indices[common]:-1;
   delta["removed"]=common==previous.Count?0:previous.Count-common;
   delta["lines"]=DeltaLines(displayed,indices,common);
   result["delta"]=delta;result["text"]=null;result["lines"]=null;
  }

  static Dictionary<string,object> Snapshot(Dictionary<string,object> request) {
   int limit=I(request,"limitLines",80);if(limit<1||limit>200)throw new ArgumentOutOfRangeException("limitLines","Use 1..200");
   string mode=S(request,"mode","text");if(mode!="text"&&mode!="visual"&&mode!="both"&&mode!="auto")throw new ArgumentException("Invalid observation mode");
   var buffer=new StringBuilder(1024,8*1024*1024);Call(ConsoleObject,"GetDisplayStrings",new Type[]{typeof(StringBuilder)},buffer);
   if(buffer.Length>8*1024*1024)throw new InvalidOperationException("Engine display buffer exceeds the 8 Mi-character internal limit");
   string full=buffer.ToString();string state=Convert.ToString(Field(ConsoleObject,"state"));object req=Field(ConsoleObject,"inputReq");
   var input=Map("kind",req==null?"Void":Convert.ToString(Field(req,"InputType")),"requestId",req==null?"":Convert.ToString(Field(req,"ID"),CultureInfo.InvariantCulture),"oneInput",req!=null&&(bool)Field(req,"OneInput"),"needValue",req!=null&&(bool)Prop(req,"NeedValue"),"timeLimit",req==null?0:Field(req,"Timelimit"),"systemInput",req!=null&&(bool)Field(req,"IsSystemInput"));
   bool needValue=req!=null&&(bool)Prop(req,"NeedValue");
   var list=(IList)Field(ConsoleObject,"displayLineList");int start=Math.Max(0,list.Count-limit);if(request.ContainsKey("fromLine"))start=Math.Max(0,Math.Min(list.Count,I(request,"fromLine",0)));
   // A cursor is expressed in the engine's absolute line space (deleted + buffered)
   // so it keeps meaning when the engine trims old rows.
   long deletedLines=0;
   var deletedProperty=ConsoleObject.GetType().GetProperty("DeletedLines",Members);
   if(deletedProperty!=null)deletedLines=Convert.ToInt64(deletedProperty.GetValue(ConsoleObject,null)??0L,CultureInfo.InvariantCulture);
   else { var deletedField=ConsoleObject.GetType().GetField("deletedLines",Members); if(deletedField!=null)deletedLines=Convert.ToInt64(deletedField.GetValue(ConsoleObject)??0L,CultureInfo.InvariantCulture); }
   bool cursorExpired=false;
   if(request.ContainsKey("sinceCursor")) {
    var requested=Convert.ToInt64(request["sinceCursor"],CultureInfo.InvariantCulture);
    if(requested<deletedLines||requested>deletedLines+list.Count)cursorExpired=true;
    else start=Math.Max(0,Math.Min(list.Count,(int)(requested-deletedLines)));
   }
   int nextGeneration=Convert.ToInt32(Field(ConsoleObject,"newButtonGeneration"),CultureInfo.InvariantCulture);
   int currentGeneration=0;
   foreach(object line in list)foreach(object b in (Array)Prop(line,"Buttons"))if((bool)Prop(b,"IsButton"))currentGeneration=Math.Max(currentGeneration,Convert.ToInt32(Prop(b,"Generation"),CultureInfo.InvariantCulture));
   bool includeInactive=request.ContainsKey("includeInactive")&&Convert.ToBoolean(request["includeInactive"],CultureInfo.InvariantCulture);
   // lines[] duplicates text one-for-one; act responses skip it unless the caller asks.
   bool includeLines=request.ContainsKey("includeLines")&&Convert.ToBoolean(request["includeLines"],CultureInfo.InvariantCulture);
   bool filterNoise=!request.ContainsKey("noise")||!string.Equals(S(request,"noise","filter"),"raw",StringComparison.OrdinalIgnoreCase);
   var lines=new List<object>();var text=new StringBuilder();var buttons=new List<object>();int omittedButtons=0;int omittedInactive=0;int filteredNoise=0;int collapsed=0;bool textCut=false;int characterBudget=24000;
   var displayed=new List<string>();var displayedIndex=new List<int>();
   for(int n=start;n<list.Count&&n<start+limit;n++) {
    object line=list[n];int lineNo=Convert.ToInt32(Field(line,"LineNo"));string original=line.ToString();
    if(filterNoise&&IsLoadNoise(original)) {
     filteredNoise++;
     continue;
    }
    var display=filterNoise?CollapseSeparator(original):original;
    if(!string.Equals(display,original,StringComparison.Ordinal))collapsed++;
    displayed.Add(display);displayedIndex.Add(n);
    if(characterBudget>0) {
     int available=Math.Max(0,characterBudget-2);string value=Clip(display,available);bool cut=value.Length<display.Length;
     text.Append(value);text.Append("\r\n");characterBudget-=value.Length+2;textCut|=cut;
     if(includeLines)lines.Add(Map("index",n,"lineNo",lineNo,"text",value,"truncated",cut));
    }else textCut=true;
    var items=(Array)Prop(line,"Buttons");
    for(int j=0;j<items.Length;j++) {
     object b=items.GetValue(j);if(!(bool)Prop(b,"IsButton"))continue;
     bool integer=(bool)Prop(b,"IsInteger");string val=integer?Convert.ToString(Prop(b,"Input"),CultureInfo.InvariantCulture):Convert.ToString(Prop(b,"Inputs"));val=val??"";
     string generation=Convert.ToString(Prop(b,"Generation"),CultureInfo.InvariantCulture),label=b.ToString();bool valueTooLong=val.Length>2048;
     bool active=needValue&&string.Equals(generation,currentGeneration.ToString(CultureInfo.InvariantCulture),StringComparison.Ordinal);
     if(!active&&!includeInactive){omittedInactive++;continue;}
     if(buttons.Count==128){buttons.RemoveAt(0);omittedButtons++;}
     buttons.Add(Map("id","b-"+lineNo+"-"+j+"-"+generation,"label",Clip(label,512),"labelTruncated",label.Length>512,"value",valueTooLong?null:val,"valueTooLong",valueTooLong,"choosable",!valueTooLong,"active",active,"integer",integer,"generation",generation,"lineIndex",n));
    }
   }
   string fingerprint=ObservationHash(state,input,full,list,currentGeneration);
   var result=Map("ok",true,"sessionId",SessionId,"observationId",SessionId+":"+fingerprint,"capturedUtc",DateTime.UtcNow.ToString("o"),"source","engine-display-buffer","consistency","same-ui-thread; animation frames are not revision-tracked","state",state,"input",input,"title",Clip(Window.Text,512),"text",mode=="visual"?null:text.ToString(),"lines",(mode=="visual"||!includeLines)?null:lines,"buttons",buttons,"buttonGeneration",currentGeneration,"omittedInactiveButtons",omittedInactive,"omittedButtons",omittedButtons,"filteredNoiseLines",filteredNoise,"collapsedSeparators",collapsed,"cursor",deletedLines+list.Count,"nextCursor",deletedLines+Math.Min(list.Count,start+limit),"cursorBase",deletedLines,"cursorExpired",cursorExpired,"totalLines",list.Count,"fromLine",start,"returnedLines",includeLines?lines.Count:0,"omittedBefore",start,"omittedAfter",Math.Max(0,list.Count-start-limit),"rawTextLength",full.Length,"textTruncated",textCut,"textCharacterBudget",24000,"window",Map("handle",Window.Handle.ToInt64(),"clientWidth",Window.ClientSize.Width,"clientHeight",Window.ClientSize.Height));
   if(mode=="visual"||mode=="both"||mode=="auto")result["screenshot"]=Capture();
   if(request.ContainsKey("sinceObservationId")) {
    var since=Convert.ToString(request["sinceObservationId"]);
    if(!string.IsNullOrEmpty(since))ApplyDelta(result,since,displayed,displayedIndex);
   }
   RememberHistory(Convert.ToString(result["observationId"]),displayed);
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
   var current=Snapshot(Map("mode","text","limitLines",200));
   if(S(request,"observationId")!=S(current,"observationId"))return Map("ok",false,"code","STALE_OBSERVATION","current",current);
   if(S(current,"state")!="WaitInput")return Map("ok",false,"code","NOT_WAITING_INPUT","current",current);
   var input=(Dictionary<string,object>)current["input"];string inputKind=S(input,"kind"),kind=S(request,"kind"),value=S(request,"value");
   if(kind=="choose") {
    if(!(bool)input["needValue"])return Error("WRONG_INPUT_KIND","Current request does not accept a button; use continue");
    Dictionary<string,object> chosen=null;
    foreach(object row in (List<object>)current["buttons"]) {var b=(Dictionary<string,object>)row;if(S(b,"id")==S(request,"choiceId")){chosen=b;break;}}
    if(chosen==null)return Error("CHOICE_NOT_FOUND","Choice is absent from the current observation");if(!(bool)chosen["active"])return Error("STALE_BUTTON","Button belongs to an older screen generation; observe again");if(!(bool)chosen["choosable"])return Error("CHOICE_VALUE_TOO_LONG","Button value exceeds the action limit");value=S(chosen,"value");
   } else if(kind=="continue") {
    if(inputKind!="EnterKey"&&inputKind!="AnyKey")return Error("WRONG_INPUT_KIND","Continue requires an EnterKey/AnyKey request");value="";
   } else if(kind!="input")return Error("INVALID_ACTION_KIND","Use choose, input, or continue");
   if(value.Length>2048||value.IndexOfAny(new char[]{'\r','\n','\0'})>=0||value.StartsWith("@",StringComparison.Ordinal))return Error("INVALID_INPUT","Input is bounded single-line text; debug commands are not exposed");
   long number;if(inputKind=="IntValue"&&!Int64.TryParse(value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out number))return Error("INTEGER_REQUIRED","Current request expects an integer");
   if(kind!="continue"&&inputKind!="IntValue"&&inputKind!="StrValue")return Error("WRONG_INPUT_KIND","Use continue for key waits");
   // Exact verified signature, dispatched on the engine UI thread. No window focus required.
   Call(ConsoleObject,"PressEnterKey",new Type[]{typeof(bool),typeof(string),typeof(bool)},false,value,false);
   var after=Snapshot(Map("mode",S(request,"mode","text"),"limitLines",80,"includeLines",false));
   return Map("ok",true,"actionId",S(request,"actionId"),"submitted",true,"value",value,"observationChanged",S(after,"observationId")!=S(current,"observationId"),"observation",after);
  }
 }
}
