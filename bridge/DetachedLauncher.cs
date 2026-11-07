// Compiled by Windows PowerShell Add-Type. Never inherits the tool client's pipes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace EraDSH {
 public sealed class LaunchReceipt {
  public int ProcessId;
  public string StartTimeUtc;
 }
 public static class DetachedLauncher {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  struct StartupInfo {
   public int cb; public string reserved; public string desktop; public string title;
   public uint x,y,xSize,ySize,xChars,yChars,fill,flags;
   public ushort showWindow,reservedBytes; public IntPtr reservedPointer,input,output,error;
  }
  [StructLayout(LayoutKind.Sequential)]
  struct ProcessInfo { public IntPtr process,thread; public uint processId,threadId; }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
   IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
   ref StartupInfo startup, out ProcessInfo process);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

  // Windows CommandLineToArgvW / CRT quoting, including trailing backslashes.
  static string Quote(string value) {
   var text=new StringBuilder("\""); int slashes=0;
   foreach(char c in value ?? "") {
    if(c=='\\') { slashes++;continue; }
    if(c=='\"') { text.Append('\\',slashes*2+1);text.Append(c);slashes=0;continue; }
    text.Append('\\',slashes);slashes=0;text.Append(c);
   }
   text.Append('\\',slashes*2);text.Append('"');return text.ToString();
  }
  public static LaunchReceipt Start(string executable, string[] arguments, string directory, IDictionary overrides) {
   var environment=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   foreach(DictionaryEntry item in Environment.GetEnvironmentVariables()) environment[(string)item.Key]=(string)item.Value;
   if(overrides!=null) foreach(DictionaryEntry item in overrides) environment[(string)item.Key]=(string)item.Value;
   var block=new StringBuilder();
   foreach(var item in environment) block.Append(item.Key).Append('=').Append(item.Value).Append('\0');
   block.Append('\0');
   var command=new StringBuilder(Quote(executable));
   foreach(string argument in arguments) command.Append(' ').Append(Quote(argument));
   var startup=new StartupInfo();startup.cb=Marshal.SizeOf(typeof(StartupInfo));
   var env=Marshal.StringToHGlobalUni(block.ToString());ProcessInfo process;
   try {
    // CREATE_UNICODE_ENVIRONMENT | DETACHED_PROCESS. No STARTF_USESTDHANDLES,
    // no inherited handles, no console lifetime or stdout dependency on PowerShell.
    if(!CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,false,0x408,env,directory,ref startup,out process))
     throw new Win32Exception(Marshal.GetLastWin32Error());
   } finally { Marshal.FreeHGlobal(env); }
   try {
    long created,exited,kernel,user;
    if(!GetProcessTimes(process.process,out created,out exited,out kernel,out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
    return new LaunchReceipt { ProcessId=checked((int)process.processId),StartTimeUtc=DateTime.FromFileTimeUtc(created).ToString("o") };
   } finally { CloseHandle(process.thread);CloseHandle(process.process); }
  }
 }
}
