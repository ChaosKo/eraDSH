// A sleeping engine substitute. No game code, UI, saves, or IPC are used.
using System;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using System.Collections.Generic;
class LaunchFixture {
 static void Main(string[] args) {
  var session=args.Length>0?args[0]:Environment.GetEnvironmentVariable("ERA_BRIDGE_SETTINGS");
  var serializer=new JavaScriptSerializer();
  var settings=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(session));
  File.WriteAllText(Path.Combine((string)settings["artifactDirectory"],"fixture.json"),serializer.Serialize(new {
   sessionFile=session, directory=Environment.CurrentDirectory, hook=Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS"),
   arguments=args, processId=System.Diagnostics.Process.GetCurrentProcess().Id
  }));
  try { Console.WriteLine("fixture output must never become tool output"); } catch(IOException) { }
  Thread.Sleep(20000);
 }
}
