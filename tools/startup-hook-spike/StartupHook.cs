// Spike: can we get into a bundled .NET 8 Emuera process through a startup hook?
//
// DOTNET_STARTUP_HOOKS runs StartupHook.Initialize before the application's Main,
// inside the application's own process, so the engine keeps its own apphost,
// dependency resolution and path semantics. This spike proves reachability and
// reads the title screen: it waits on Application.Idle until the console has
// lines, dumps them, then closes the window.

using System.Collections;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class StartupHook
{
    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AnyStatic =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly string LogPath =
        Environment.GetEnvironmentVariable("ERA_HOOK_LOG")
        ?? Path.Combine(Path.GetTempPath(), "era-bridge-hook.log");

    private static readonly object Gate = new();
    private static readonly DateTime Started = DateTime.UtcNow;
    private static DateTime _lastHeartbeat = DateTime.MinValue;
    private static bool _finished;

    private static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:O} [{Environment.ProcessId}] {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never take the engine down.
        }
    }

    public static void Initialize()
    {
        Log($"hook init: process={Environment.ProcessPath} cwd={Environment.CurrentDirectory}");
        try
        {
            Log($"hook: runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            Application.Idle += OnIdle;
            Log("hook: Application.Idle subscribed");
        }
        catch (Exception ex)
        {
            Log("hook init failed: " + ex);
        }
    }

    private static void OnIdle(object sender, EventArgs e)
    {
        if (_finished) return;
        var elapsed = (DateTime.UtcNow - Started).TotalSeconds;
        try
        {
            var window = FindMainWindow();
            if (window is null)
            {
                Heartbeat("idle: no MainWindow yet", elapsed);
                if (elapsed > 30) { Log("giving up: no window"); _finished = true; }
                return;
            }

            var console = window.GetType().GetField("console", AnyInstance)?.GetValue(window);
            if (console is null)
            {
                Heartbeat("idle: window without console", elapsed);
                return;
            }

            var consoleType = console.GetType();
            var state = consoleType.GetProperty("state", AnyInstance)?.GetValue(console);
            var lines = consoleType.GetProperty("DisplayLineList", AnyInstance)?.GetValue(console) as IEnumerable;
            var lineCount = lines is null ? 0 : Count(lines);

            var stateText = state?.ToString() ?? "";
            if ((stateText == "Initializing" || lineCount < 4) && elapsed < 45 && stateText != "Error")
            {
                Heartbeat($"idle: waiting for output (state={state} lines={lineCount})", elapsed);
                return;
            }

            _finished = true;
            var inputReq = consoleType.GetField("inputReq", AnyInstance)?.GetValue(console);
            Log($"window: type={window.GetType().FullName} title='{window.Text}' visible={window.Visible}");
            Log($"state={state} lines={lineCount} inputReq={(inputReq is null ? "null" : inputReq.GetType().FullName)}");
            if (inputReq is not null)
            {
                var reqType = inputReq.GetType();
                Log("input: " + string.Join(", ", new[]
                {
                    "InputType=" + reqType.GetField("InputType", AnyInstance)?.GetValue(inputReq),
                    "ID=" + reqType.GetField("ID", AnyInstance)?.GetValue(inputReq),
                    "OneInput=" + reqType.GetField("OneInput", AnyInstance)?.GetValue(inputReq),
                    "Timelimit=" + reqType.GetField("Timelimit", AnyInstance)?.GetValue(inputReq),
                    "NeedValue=" + reqType.GetProperty("NeedValue", AnyInstance)?.GetValue(inputReq),
                }));
            }

            var index = 0;
            foreach (var line in lines)
            {
                if (index >= 12) break;
                var text = line?.ToString() ?? "(null)";
                Log($"line[{index}]: {text.Replace("\r", string.Empty).Replace("\n", "\\n")}");
                var buttons = line?.GetType().GetProperty("Buttons", AnyInstance)?.GetValue(line) as Array;
                if (buttons is not null && buttons.Length > 0)
                {
                    for (var b = 0; b < buttons.Length && b < 4; b++)
                    {
                        var button = buttons.GetValue(b);
                        var bt = button?.GetType();
                        if (bt is null) continue;
                        Log($"  button[{b}]: name='{button}' isButton={bt.GetProperty("IsButton", AnyInstance)?.GetValue(button)} " +
                            $"isInteger={bt.GetProperty("IsInteger", AnyInstance)?.GetValue(button)} " +
                            $"input={bt.GetProperty("Input", AnyInstance)?.GetValue(button)} " +
                            $"inputs={bt.GetProperty("Inputs", AnyInstance)?.GetValue(button)} " +
                            $"generation={bt.GetProperty("Generation", AnyInstance)?.GetValue(button)}");
                    }
                }
                index++;
            }

            Log("closing window from hook");
            window.Close();
        }
        catch (Exception ex)
        {
            Log("idle failed: " + ex);
        }
    }

    private static void Heartbeat(string message, double elapsed)
    {
        if ((DateTime.UtcNow - _lastHeartbeat).TotalSeconds < 2) return;
        _lastHeartbeat = DateTime.UtcNow;
        Log($"{message} ({elapsed:F1}s)");
    }

    private static int Count(IEnumerable source)
    {
        var count = 0;
        foreach (var _ in source) count++;
        return count;
    }

    private static Form FindMainWindow()
    {
        foreach (Form form in Application.OpenForms)
        {
            var name = form.GetType().FullName ?? string.Empty;
            if (name.EndsWith("Forms.MainWindow", StringComparison.Ordinal) ||
                name.EndsWith("Emuera.MainWindow", StringComparison.Ordinal))
            {
                return form;
            }
        }
        return null;
    }
}
