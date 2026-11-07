# Windows PowerShell 5.1; intentionally ASCII source.
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('EraProbe.Native' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
namespace EraProbe {
 public class WindowInfo {
  public long Handle; public uint ProcessId; public string ClassName; public string Text;
  public bool Visible; public bool Enabled; public int X; public int Y; public int Width; public int Height;
 }
 public class CaptureInfo {
  public bool PrintWindowSucceeded; public string WindowPath; public string ClientPath;
  public int Width; public int Height; public int ClientWidth; public int ClientHeight;
 }
 public static class Native {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hwnd, EnumProc callback, IntPtr data);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, StringBuilder lp, uint flags, uint timeout, out IntPtr result);
  [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", ExactSpelling=true, CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr SendMessageTimeoutString(IntPtr hwnd, uint msg, IntPtr wp, string lp, uint flags, uint timeout, out IntPtr result);
  public static string ReadText(IntPtr hwnd) {
   var b = new StringBuilder(32768); IntPtr result;
   if(SendMessageTimeout(hwnd, 0x000D, (IntPtr)b.Capacity, b, 2, 1200, out result)==IntPtr.Zero) return null;
   return b.ToString();
  }
  public static bool SetText(IntPtr hwnd, string text) {
   IntPtr result;
   return SendMessageTimeoutString(hwnd, 0x000C, IntPtr.Zero, text, 2, 1200, out result)!=IntPtr.Zero && result!=IntPtr.Zero;
  }
  public static WindowInfo Describe(IntPtr hwnd) {
   uint pid; GetWindowThreadProcessId(hwnd,out pid); var cls=new StringBuilder(512); GetClassName(hwnd,cls,cls.Capacity);
   RECT r; GetWindowRect(hwnd,out r);
   return new WindowInfo { Handle=hwnd.ToInt64(), ProcessId=pid, ClassName=cls.ToString(), Text=ReadText(hwnd),
    Visible=IsWindowVisible(hwnd), Enabled=IsWindowEnabled(hwnd), X=r.Left, Y=r.Top, Width=r.Right-r.Left, Height=r.Bottom-r.Top };
  }
  public static WindowInfo[] TopWindows(uint pid) {
   var list=new List<WindowInfo>();
   EnumWindows(delegate(IntPtr h,IntPtr d) { uint p; GetWindowThreadProcessId(h,out p); if(p==pid && IsWindowVisible(h)) list.Add(Describe(h)); return true; },IntPtr.Zero);
   return list.ToArray();
  }
  public static WindowInfo[] Children(IntPtr parent) {
   var list=new List<WindowInfo>();
   EnumChildWindows(parent,delegate(IntPtr h,IntPtr d) { list.Add(Describe(h)); return list.Count<200; },IntPtr.Zero);
   return list.ToArray();
  }
  public static CaptureInfo Capture(IntPtr hwnd,string windowPath,string clientPath) {
   if(!IsWindow(hwnd)||IsIconic(hwnd)) throw new InvalidOperationException("Window missing or minimized");
   RECT r,c; GetWindowRect(hwnd,out r); GetClientRect(hwnd,out c);
   int w=r.Right-r.Left,h=r.Bottom-r.Top;
   if(w<=0||h<=0||w>10000||h>10000) throw new InvalidOperationException("Invalid window dimensions");
   POINT origin=new POINT(); if(!ClientToScreen(hwnd,ref origin)) throw new InvalidOperationException("Client origin unavailable");
   using(var image=new Bitmap(w,h,PixelFormat.Format32bppArgb)) {
    bool ok;
    using(var g=Graphics.FromImage(image)) { g.Clear(Color.Magenta); var dc=g.GetHdc(); try { ok=PrintWindow(hwnd,dc,0); } finally { g.ReleaseHdc(dc); } }
    if(!ok) throw new InvalidOperationException("PrintWindow failed; no desktop capture fallback");
    image.Save(windowPath,ImageFormat.Png);
    var crop=new Rectangle(origin.X-r.Left,origin.Y-r.Top,c.Right-c.Left,c.Bottom-c.Top);
    if(crop.Width<=0||crop.Height<=0||crop.X<0||crop.Y<0||crop.Right>w||crop.Bottom>h) throw new InvalidOperationException("Client crop out of bounds");
    using(var client=image.Clone(crop,image.PixelFormat)) client.Save(clientPath,ImageFormat.Png);
    return new CaptureInfo { PrintWindowSucceeded=ok, WindowPath=windowPath,ClientPath=clientPath,Width=w,Height=h,ClientWidth=crop.Width,ClientHeight=crop.Height };
   }
  }
 }
}
'@
}
[void][EraProbe.Native]::SetProcessDPIAware()

function Get-EraAutomationTree([IntPtr]$WindowHandle) {
 $rows = New-Object System.Collections.Generic.List[object]
 try {
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.Condition]::TrueCondition)
  for ($i=0; $i -lt [Math]::Min($all.Count,120); $i++) {
   try {
    $e=$all.Item($i); $c=$e.Current; $text=$null; $value=$null; $textCut=$false
    $pattern=$null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern,[ref]$pattern)) {
     $text=$pattern.DocumentRange.GetText(12001)
     if ($text.Length -gt 12000) { $text=$text.Substring(0,12000); $textCut=$true }
    }
    $pattern=$null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)) { $value=$pattern.Current.Value }
    $rows.Add([pscustomobject]@{index=$i;name=$c.Name;class=$c.ClassName;automationId=$c.AutomationId;handle=$c.NativeWindowHandle;type=$c.ControlType.ProgrammaticName;enabled=$c.IsEnabled;offscreen=$c.IsOffscreen;patterns=@($e.GetSupportedPatterns() | ForEach-Object {$_.ProgrammaticName});text=$text;textTruncated=$textCut;value=$value})
   } catch { $rows.Add([pscustomobject]@{index=$i;error=$_.Exception.Message}) }
  }
  return [pscustomobject]@{total=$all.Count;omitted=[Math]::Max(0,$all.Count-120);elements=@($rows.ToArray())}
 } catch { return [pscustomobject]@{error=$_.Exception.Message;elements=@()} }
}
