// eraDSH adapter for .NET 8 Emuera builds (Emuera.NET / Lazyloading EE).
//
// These builds ship as single-file bundles: the exe is a native apphost and the
// managed assemblies live inside it, so a host process cannot load the engine by
// reflection. Instead this assembly is injected into the engine's own process as
// a startup hook (DOTNET_STARTUP_HOOKS), which keeps the apphost, dependency
// resolution and path semantics untouched.
//
// Once injected it speaks the same named-pipe protocol as the .NET Framework
// host, so scripts/Invoke-Bridge.ps1 and the era_bridge tool work unchanged.

using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

internal static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            Bridge.Start();
        }
        catch (Exception ex)
        {
            // A startup hook must never take the engine down; record and continue.
            Bridge.LogFailure(ex);
        }
    }
}

internal static class Bridge
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, object>> Actions = new();

    private static JsonElement _settings;
    private static string _sessionId;
    private static string _artifactDir;
    private static string _logPath;
    private static Form _window;
    private static object _console;
    private static Type _consoleType;
    private static bool _serving;
    private static Dictionary<string, object> _cached;
    private static readonly Dictionary<string, Dictionary<string, object>> Recent = new();
    private static readonly Queue<string> RecentOrder = new();
    private static DateTime _cachedAt = DateTime.MinValue;
    private static string _failure;
    private static int _imageNumber;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    internal static void LogFailure(Exception ex)
    {
        try { WriteLineSafe(_logPath ?? Path.Combine(Path.GetTempPath(), "era-bridge-hook-failure.log"), ex.ToString()); } catch { }
    }

    internal static void Start()
    {
        var settingsPath = Environment.GetEnvironmentVariable("ERA_BRIDGE_SETTINGS");
        if (string.IsNullOrEmpty(settingsPath) || !File.Exists(settingsPath))
        {
            throw new InvalidOperationException("ERA_BRIDGE_SETTINGS is not set to an existing session file");
        }

        _settings = JsonDocument.Parse(File.ReadAllText(settingsPath, Utf8)).RootElement.Clone();
        _sessionId = Str("sessionId");
        _artifactDir = Path.GetFullPath(Str("artifactDirectory"));
        _logPath = Path.Combine(_artifactDir, "hook.log");
        Directory.CreateDirectory(_artifactDir);
        Record("hook-start", new { process = Environment.ProcessPath, runtime = Environment.Version.ToString() });

        if (Str("launchMode") != "startup-hook")
        {
            throw new InvalidOperationException("session was not built for the startup-hook launcher");
        }

        Application.Idle += OnIdle;
        if (!_serving)
        {
            _serving = true;
            var server = new Thread(Serve) { IsBackground = true, Name = "eraDSH hook IPC" };
            server.Start();
            Record("ipc-started", new { pipe = Str("pipeName") });
        }
    }

    private static void OnIdle(object sender, EventArgs e)
    {
        if (_console != null)
        {
            try
            {
                RefreshTargets();
                _cached = SnapshotCore(new Dictionary<string, object> { ["mode"] = "text", ["limitLines"] = 200 });
                _cachedAt = DateTime.UtcNow;
            }
            catch { }
            return;
        }
        try
        {
            if (RefreshTargets())
            {
                Record("ready", new { window = _window.GetType().FullName, title = _window.Text, console = _consoleType.FullName });
                try
                {
                    _cached = SnapshotCore(new Dictionary<string, object> { ["mode"] = "text", ["limitLines"] = 200 });
                    _cachedAt = DateTime.UtcNow;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _failure = ex.ToString();
            LogFailure(ex);
        }
    }

    // ---- protocol ----

    private static void Serve()
    {
        while (!ExitedSafe())
        {
            try
            {
                using var pipe = CreatePipe();
                pipe.WaitForConnection();
                var close = false;
                try
                {
                    using var reader = new StreamReader(pipe, Utf8, false, 8192, true);
                    using var writer = new StreamWriter(pipe, Utf8, 8192, true)
                    {
                        AutoFlush = false,
                    };
                    string response;
                    try
                    {
                        var request = JsonDocument.Parse(ReadRequest(reader)).RootElement;
                        if (request.GetProperty("token").GetString() != Str("token"))
                        {
                            response = Json(Error("UNAUTHORIZED", "Local session credential required"));
                        }
                        else
                        {
                            close = request.TryGetProperty("action", out var action) && action.GetString() == "close";
                            response = Dispatch(request);
                        }
                    }
                    catch (Exception ex)
                    {
                        response = Json(Error("REQUEST_ERROR", Root(ex).Message));
                    }
                    writer.WriteLine(response);
                    writer.Flush();
                }
                catch (Exception ex)
                {
                    Record("connection-error", new { error = Root(ex).Message });
                }
                if (close) { CloseWindow(); return; }
            }
            catch (Exception ex)
            {
                Record("ipc-error", new { error = Root(ex).Message });
                Thread.Sleep(200);
            }
        }
    }

    private static System.IO.Pipes.NamedPipeServerStream CreatePipe()
    {
        var sid = WindowsIdentity.GetCurrent().User;
        var security = new System.IO.Pipes.PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(sid, System.IO.Pipes.PipeAccessRights.FullControl, AccessControlType.Allow));
        var server = System.IO.Pipes.NamedPipeServerStreamAcl.Create(
            Str("pipeName"),
            System.IO.Pipes.PipeDirection.InOut,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous,
            8192,
            8192,
            security);
        return server;
    }

    private static string ReadRequest(StreamReader reader)
    {
        var text = new StringBuilder(1024);
        var buffer = new char[1024];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var remaining = 5000 - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0) throw new IOException("Request read timed out");
            var task = reader.ReadAsync(buffer, 0, buffer.Length);
            if (!task.Wait(remaining)) throw new IOException("Request read timed out");
            var count = task.Result;
            if (count == 0) throw new IOException("Request ended before newline");
            var newline = Array.IndexOf(buffer, '\n', 0, count);
            var used = newline < 0 ? count : newline;
            if (text.Length + used > 32768) throw new IOException("Request exceeds 32768 characters");
            text.Append(buffer, 0, used);
            if (newline >= 0) return text.ToString().TrimEnd('\r');
        }
    }

    private static bool IsSteadySnapshot(Dictionary<string, object> snapshot)
    {
        if (snapshot is null) return false;
        var state = Convert.ToString(snapshot["state"]);
        if (string.IsNullOrEmpty(state) || state == "Initializing" || state == "Running" || state == "Sleep")
        {
            return false;
        }
        // Short TWAIT/TINPUT fade loops (e.g. OCG2 title fade-in) set timeLimit > 0 and run a timer
        // while NeedValue is false; wait for them to finish so observe lands on the real menu.
        if (snapshot.TryGetValue("input", out var inputObj) && inputObj is Dictionary<string, object> input)
        {
            var timeLimit = input.TryGetValue("timeLimit", out var tl) ? Convert.ToInt64(tl, CultureInfo.InvariantCulture) : -1;
            var needValue = input.TryGetValue("needValue", out var nv) && nv is bool b && b;
            var runningTimer = input.TryGetValue("runningTimer", out var rt) && rt is bool rtb && rtb;
            if ((runningTimer || (timeLimit > 0 && timeLimit <= 2000)) && !needValue)
            {
                return false;
            }
        }
        return true;
    }

    private static string Dispatch(JsonElement request)
    {
        var action = GetString(request, "action");
        switch (action)
        {
            case "status":
                return Json(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["sessionId"] = _sessionId,
                    ["ready"] = _console != null,
                    ["exited"] = false,
                    ["failure"] = _failure,
                    ["engineFamily"] = "core",
                    ["launchMode"] = "startup-hook",
                    ["gameDir"] = Str("gameRoot"),
                    ["processId"] = Environment.ProcessId,
                });
            case "close":
                return Json(new Dictionary<string, object> { ["ok"] = true, ["status"] = "normal_close_scheduled" });
            case "observe":
            {
                Record("observe-request", request.GetRawText());
                var budget = Environment.GetEnvironmentVariable("ERA_BRIDGE_READY_MS") is { Length: > 0 } ms && int.TryParse(ms, out var parsed) ? parsed : 120000;
                var waited = System.Diagnostics.Stopwatch.StartNew();
                // Wait on the idle-refreshed cache until the engine is in a steady input
                // state, then build a fresh snapshot so every option is honoured.
                while (waited.ElapsedMilliseconds < budget)
                {
                    if (IsSteadySnapshot(CachedSnapshot())) break;
                    Thread.Sleep(80);
                }
                if (!IsSteadySnapshot(CachedSnapshot()))
                {
                    return Json(CachedSnapshot() ?? Error("NOT_READY", "engine did not reach a steady input state in time"));
                }
                return Json(Snapshot(request));
            }
            case "act":
                return Act(request);
            default:
                return Json(Error("UNKNOWN_ACTION", "Use status, observe, act, or close"));
        }
    }

    private static string Act(JsonElement request)
    {
        var actionId = GetString(request, "actionId");
        if (!Guid.TryParse(actionId, out var parsed)) return Json(Error("INVALID_ACTION_ID", "A unique GUID actionId is required"));
        actionId = parsed.ToString("D");
        lock (Gate)
        {
            if (Actions.TryGetValue(actionId, out var prior))
            {
                return Json(new Dictionary<string, object> { ["ok"] = false, ["code"] = "DUPLICATE_ACTION", ["action"] = prior });
            }
            if (Actions.Count >= 10000) return Json(Error("ACTION_LIMIT", "Session action limit reached"));
            Actions[actionId] = new Dictionary<string, object> { ["actionId"] = actionId, ["state"] = "queued" };
        }

        Record("action-intent", new
        {
            actionId,
            observationId = GetString(request, "observationId"),
            kind = GetString(request, "kind"),
            value = GetString(request, "value"),
            choiceId = GetString(request, "choiceId"),
        });

        var result = OnUi(() =>
        {
            lock (Gate) Actions[actionId]["state"] = "executing";
            try
            {
                var answer = ActCore(request, actionId);
                lock (Gate)
                {
                    Actions[actionId]["state"] = "completed";
                    Actions[actionId]["result"] = answer;
                }
                Record("action-result", new { actionId, result = answer });
                return answer;
            }
            catch (Exception ex)
            {
                var answer = Error("ACTION_OUTCOME_UNCERTAIN", Root(ex).Message);
                lock (Gate) Actions[actionId]["state"] = "uncertain";
                Record("action-result", new { actionId, result = answer });
                return answer;
            }
        });
        return Json(result);
    }

    private static Dictionary<string, object> ActCore(JsonElement request, string actionId)
    {
        var claimed = GetString(request, "observationId");
        Dictionary<string, object> known;
        lock (Recent) Recent.TryGetValue(claimed, out known);
        if (known is null)
        {
            var fallback = CachedSnapshot();
            if (fallback != null && (string)fallback["observationId"] == claimed) known = fallback;
        }
        var current = known ?? CachedSnapshot() ?? SnapshotCore(new Dictionary<string, object> { ["mode"] = "text", ["limitLines"] = 200 });
        if (known is null && claimed != (string)current["observationId"])
        {
            return new Dictionary<string, object> { ["ok"] = false, ["code"] = "STALE_OBSERVATION", ["current"] = current };
        }
        if ((string)current["state"] != "WaitInput")
        {
            return new Dictionary<string, object> { ["ok"] = false, ["code"] = "NOT_WAITING_INPUT", ["current"] = current };
        }

        var input = (Dictionary<string, object>)current["input"];
        var inputKind = (string)input["kind"];
        var kind = GetString(request, "kind");
        var value = GetString(request, "value");

        if (kind == "choose")
        {
            if (!(bool)input["needValue"]) return Error("WRONG_INPUT_KIND", "Current request does not accept a button; use continue");
            Dictionary<string, object> chosen = null;
            foreach (var entry in (List<object>)current["buttons"])
            {
                var button = (Dictionary<string, object>)entry;
                if ((string)button["id"] == GetString(request, "choiceId")) { chosen = button; break; }
            }
            if (chosen is null) return Error("CHOICE_NOT_FOUND", "Choice is absent from the current observation");
            if (!(bool)chosen["active"]) return Error("STALE_BUTTON", "Button belongs to an older screen generation; observe again");
            if (!(bool)chosen["choosable"]) return Error("CHOICE_VALUE_TOO_LONG", "Button value exceeds the action limit");
            value = (string)chosen["value"];
        }
        else if (kind == "continue")
        {
            if (inputKind != "EnterKey" && inputKind != "AnyKey" && inputKind != "PrimitiveMouseKey")
            {
                return Error("WRONG_INPUT_KIND", "Continue requires an EnterKey/AnyKey request");
            }
            value = string.Empty;
        }
        else if (kind != "input")
        {
            return Error("INVALID_ACTION_KIND", "Use choose, input, or continue");
        }

        if (value.Length > 2048 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || value.StartsWith("@", StringComparison.Ordinal))
        {
            return Error("INVALID_INPUT", "Input is bounded single-line text; debug commands are not exposed");
        }
        if ((inputKind == "IntValue" || inputKind == "IntButton") && !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            return Error("INTEGER_REQUIRED", "Current request expects an integer");
        }
        if (kind != "continue" && inputKind != "IntValue" && inputKind != "IntButton" && inputKind != "StrValue" && inputKind != "StrButton" && inputKind != "AnyValue")
        {
            return Error("WRONG_INPUT_KIND", "Use continue for key waits");
        }

        var press = _consoleType.GetMethod("PressEnterKey", AnyInstance, null, new[] { typeof(bool), typeof(string), typeof(bool) }, null);
        if (press is null) return Error("ADAPTER_MEMBER_MISSING", "EmueraConsole.PressEnterKey(bool,string,bool) is absent in this build");
        press.Invoke(_console, new object[] { false, value, false });

        var mode = GetString(request, "mode") is { Length: > 0 } m ? m : "text";
        var limit = request.TryGetProperty("limitLines", out var ll) ? ll.GetInt32() : 80;
        var after = SnapshotCore(new Dictionary<string, object> { ["mode"] = mode, ["limitLines"] = limit, ["includeLines"] = false });
        _cached = after;
        _cachedAt = DateTime.UtcNow;

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["actionId"] = actionId,
            ["submitted"] = true,
            ["value"] = value,
            ["observationChanged"] = (string)after["observationId"] != (string)current["observationId"],
            ["observation"] = after,
        };
    }

    private static Dictionary<string, object> CachedSnapshot()
    {
        var cached = _cached;
        var fresh = cached != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 5;
        return fresh ? cached : null;
    }

    private static Dictionary<string, object> Snapshot(JsonElement request)
    {
        var options = new Dictionary<string, object>
        {
            ["mode"] = GetString(request, "mode") is { Length: > 0 } mode ? mode : "text",
        };
        if (request.TryGetProperty("limitLines", out var limit)) options["limitLines"] = limit.GetInt32();
        if (request.TryGetProperty("fromLine", out var from)) options["fromLine"] = from.GetInt32();
        if (request.TryGetProperty("includeLines", out var includeLines) &&
            (includeLines.ValueKind == JsonValueKind.True || includeLines.ValueKind == JsonValueKind.False))
        {
            options["includeLines"] = includeLines.GetBoolean();
        }
        var noise = GetString(request, "noise");
        if (noise.Length > 0) options["noise"] = noise;
        return OnUi(() => SnapshotCore(options));
    }

    // Era engines print a wall of loading chatter at startup. The wording is
    // localized ("読み込み中", "读取 ABL.CSV 中", "Loading ..."), so a line counts as
    // noise when it names a script/data file AND carries a progress word, or when it
    // starts with a progress word outright. Counts are reported, never silent.
    private static bool IsLoadNoise(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 260) return false;

        var startsWithProgress =
            t.StartsWith("読み込み", StringComparison.Ordinal) ||
            t.StartsWith("ロード", StringComparison.Ordinal) ||
            t.StartsWith("读取", StringComparison.Ordinal) ||
            t.StartsWith("讀取", StringComparison.Ordinal) ||
            t.StartsWith("加载", StringComparison.Ordinal) ||
            t.StartsWith("載入", StringComparison.Ordinal) ||
            t.StartsWith("Now Loading", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("Loading", StringComparison.OrdinalIgnoreCase);
        if (startsWithProgress) return true;

        var lower = t.ToLowerInvariant();
        var hasFileExtension = lower.Contains(".erb") || lower.Contains(".erh") || lower.Contains(".csv");
        if (!hasFileExtension) return false;

        var hasProgressWord =
            t.Contains("读取") || t.Contains("讀取") || t.Contains("加载") || t.Contains("載入") ||
            t.Contains("読み込み") || t.Contains("ロード") || lower.Contains("loading");
        var hasProgressSuffix = t.EndsWith("中・・・", StringComparison.Ordinal) ||
                                t.EndsWith("中...", StringComparison.Ordinal) ||
                                t.EndsWith("中…", StringComparison.Ordinal);
        return hasProgressWord || hasProgressSuffix;
    }

    // Era screens are separated by a long run of one character; keep the fact, drop the bytes.
    private static string CollapseSeparator(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 40) return text;
        var t = text.TrimEnd();
        if (t.Length < 40 || char.IsWhiteSpace(t[0])) return text;
        var first = t[0];
        for (var i = 1; i < t.Length; i++) if (t[i] != first) return text;
        return first + " ×" + t.Length.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, object> SnapshotCore(Dictionary<string, object> options)
    {
        if (_console is null) throw new InvalidOperationException("engine console is not attached yet");

        var mode = options.TryGetValue("mode", out var m) ? (string)m : "text";
        var limit = options.TryGetValue("limitLines", out var l) ? Convert.ToInt32(l) : 80;
        if (limit < 1 || limit > 200) throw new ArgumentOutOfRangeException(nameof(limit), "Use 1..200");

        var linesProperty = _consoleType.GetProperty("DisplayLineList", AnyInstance);
        if (linesProperty is null) throw new InvalidOperationException("DisplayLineList is absent in this build");
        var allLines = (IList)linesProperty.GetValue(_console) ?? new List<object>();

        var start = Math.Max(0, allLines.Count - limit);
        if (options.TryGetValue("fromLine", out var fromValue)) start = Math.Max(0, Math.Min(allLines.Count, Convert.ToInt32(fromValue)));

        var state = _consoleType.GetProperty("state", AnyInstance)?.GetValue(_console)?.ToString() ?? "unknown";
        var request = _consoleType.GetField("inputReq", AnyInstance)?.GetValue(_console);
        var input = DescribeInput(request);
        var needValue = input.TryGetValue("needValue", out var nv) && nv is bool b && b;

        // First pass across all display lines: determine the latest button generation on screen,
        // matching EraBridgeHost.cs semantics (newButtonGeneration is the *next* generation counter).
        long currentGeneration = 0;
        for (var idx = 0; idx < allLines.Count; idx++)
        {
            var lineObj = allLines[idx];
            var arr = lineObj?.GetType().GetProperty("Buttons", AnyInstance)?.GetValue(lineObj) as Array;
            if (arr is null) continue;
            for (var bi = 0; bi < arr.Length; bi++)
            {
                var btn = arr.GetValue(bi);
                if (btn is null) continue;
                var bt = btn.GetType();
                if (!(bool)(bt.GetProperty("IsButton", AnyInstance)?.GetValue(btn) ?? false)) continue;
                var gen = Convert.ToInt64(bt.GetProperty("Generation", AnyInstance)?.GetValue(btn) ?? 0L, CultureInfo.InvariantCulture);
                if (gen > currentGeneration) currentGeneration = gen;
            }
        }
        var currentGenStr = currentGeneration.ToString(CultureInfo.InvariantCulture);

        var lines = new List<object>();
        var buttons = new List<object>();
        var text = new StringBuilder();
        var omittedInactive = 0;
        var omittedButtons = 0;
        var filteredNoise = 0;
        var collapsed = 0;
        var characterBudget = 24000;
        var textCut = false;
        var includeLines = options.ContainsKey("includeLines") && Convert.ToBoolean(options["includeLines"], CultureInfo.InvariantCulture);
        var filterNoise = !options.ContainsKey("noise") || !string.Equals(Convert.ToString(options["noise"]), "raw", StringComparison.OrdinalIgnoreCase);

        for (var index = start; index < allLines.Count && index < start + limit; index++)
        {
            var line = allLines[index];
            var lineNo = Convert.ToInt32(line?.GetType().GetField("LineNo", AnyInstance)?.GetValue(line) ?? index);
            var original = line?.ToString() ?? string.Empty;
            if (filterNoise && IsLoadNoise(original))
            {
                filteredNoise++;
                continue;
            }
            var display = filterNoise ? CollapseSeparator(original) : original;
            if (!string.Equals(display, original, StringComparison.Ordinal)) collapsed++;
            if (characterBudget > 0)
            {
                var available = Math.Max(0, characterBudget - 2);
                var clipped = display.Length > available ? display.Substring(0, available) : display;
                var cut = clipped.Length < display.Length;
                text.Append(clipped).Append("\r\n");
                characterBudget -= clipped.Length + 2;
                textCut |= cut;
                if (includeLines)
                {
                    lines.Add(new Dictionary<string, object>
                    {
                        ["index"] = index,
                        ["lineNo"] = lineNo,
                        ["text"] = clipped,
                        ["truncated"] = cut,
                    });
                }
            }
            else
            {
                textCut = true;
            }
        }

        // Collect active buttons across all display lines so buttons above the text window are not lost.
        for (var index = 0; index < allLines.Count; index++)
        {
            var line = allLines[index];
            var lineNo = Convert.ToInt32(line?.GetType().GetField("LineNo", AnyInstance)?.GetValue(line) ?? index);
            if (filterNoise && IsLoadNoise(line?.ToString() ?? string.Empty)) continue;
            var lineButtons = line?.GetType().GetProperty("Buttons", AnyInstance)?.GetValue(line) as Array;
            if (lineButtons is null) continue;
            for (var buttonIndex = 0; buttonIndex < lineButtons.Length; buttonIndex++)
            {
                var button = lineButtons.GetValue(buttonIndex);
                if (button is null) continue;
                var type = button.GetType();
                if (!(bool)(type.GetProperty("IsButton", AnyInstance)?.GetValue(button) ?? false)) continue;

                var isInteger = (bool)(type.GetProperty("IsInteger", AnyInstance)?.GetValue(button) ?? false);
                var rawValue = isInteger
                    ? Convert.ToString(type.GetProperty("Input", AnyInstance)?.GetValue(button), CultureInfo.InvariantCulture) ?? string.Empty
                    : Convert.ToString(type.GetProperty("Inputs", AnyInstance)?.GetValue(button), CultureInfo.InvariantCulture) ?? string.Empty;
                var generation = Convert.ToString(type.GetProperty("Generation", AnyInstance)?.GetValue(button), CultureInfo.InvariantCulture) ?? "0";
                var active = needValue && string.Equals(generation, currentGenStr, StringComparison.Ordinal);
                if (!active) { omittedInactive++; continue; }

                if (buttons.Count == 128)
                {
                    buttons.RemoveAt(0);
                    omittedButtons++;
                }

                var label = button.ToString() ?? string.Empty;
                buttons.Add(new Dictionary<string, object>
                {
                    ["id"] = "b-" + lineNo + "-" + buttonIndex + "-" + generation,
                    ["label"] = label.Length > 512 ? label.Substring(0, 512) : label,
                    ["labelTruncated"] = label.Length > 512,
                    ["value"] = rawValue.Length > 2048 ? null : rawValue,
                    ["valueTooLong"] = rawValue.Length > 2048,
                    ["choosable"] = rawValue.Length <= 2048,
                    ["active"] = active,
                    ["integer"] = isInteger,
                    ["generation"] = generation,
                    ["lineIndex"] = index,
                });
            }
        }

        var fingerprint = Hash(string.Join("|", new[]
        {
            state,
            Json(input),
            text.ToString(),
            string.Join(",", buttons.ConvertAll(x => Json(x))),
            currentGenStr,
        }));

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["sessionId"] = _sessionId,
            ["observationId"] = _sessionId + ":" + fingerprint,
            ["capturedUtc"] = DateTime.UtcNow.ToString("o"),
            ["source"] = "engine-display-buffer",
            ["consistency"] = "same-ui-thread; animation frames are not revision-tracked",
            ["engineFamily"] = "core",
            ["state"] = state,
            ["input"] = input,
            ["title"] = _window?.Text ?? string.Empty,
            ["text"] = mode == "visual" ? null : text.ToString(),
            ["lines"] = (mode == "visual" || !includeLines) ? null : lines,
            ["buttons"] = buttons,
            ["buttonGeneration"] = currentGeneration,
            ["omittedInactiveButtons"] = omittedInactive,
            ["omittedButtons"] = omittedButtons,
            ["filteredNoiseLines"] = filteredNoise,
            ["collapsedSeparators"] = collapsed,
            ["totalLines"] = allLines.Count,
            ["fromLine"] = start,
            ["returnedLines"] = includeLines ? lines.Count : 0,
            ["omittedBefore"] = start,
            ["omittedAfter"] = Math.Max(0, allLines.Count - start - limit),
            ["rawTextLength"] = text.Length,
            ["textTruncated"] = textCut,
            ["textCharacterBudget"] = 24000,
            ["window"] = _window is null ? null : new Dictionary<string, object>
            {
                ["handle"] = _window.Handle.ToInt64(),
                ["clientWidth"] = _window.ClientSize.Width,
                ["clientHeight"] = _window.ClientSize.Height,
            },
        };
        if (mode == "visual" || mode == "both" || mode == "auto")
        {
            result["screenshot"] = Capture();
        }
        Remember(result);
        return result;
    }

    private static object Capture()
    {
        if (_window is null || _window.IsDisposed) throw new InvalidOperationException("Window is not available for screenshot");
        if (IsIconic(_window.Handle)) throw new InvalidOperationException("Window is minimized; no screenshot claimed");
        GetWindowRect(_window.Handle, out var r);
        GetClientRect(_window.Handle, out var c);
        var origin = new Point();
        ClientToScreen(_window.Handle, ref origin);
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0 || w > 10000 || h > 10000) throw new InvalidOperationException("Invalid capture dimensions");
        var name = "screen-" + Interlocked.Increment(ref _imageNumber).ToString("D5", CultureInfo.InvariantCulture) + ".png";
        var path = Path.Combine(_artifactDir, name);
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        bool ok;
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Magenta);
            var dc = g.GetHdc();
            try { ok = PrintWindow(_window.Handle, dc, 0); }
            finally { g.ReleaseHdc(dc); }
        }
        if (!ok) throw new InvalidOperationException("PrintWindow failed");
        var crop = new Rectangle(origin.X - r.Left, origin.Y - r.Top, c.Right - c.Left, c.Bottom - c.Top);
        if (crop.Width <= 0 || crop.Height <= 0 || crop.X < 0 || crop.Y < 0 || crop.Right > w || crop.Bottom > h)
        {
            throw new InvalidOperationException("Invalid client crop");
        }
        using (var client = bitmap.Clone(crop, bitmap.PixelFormat))
        {
            client.Save(path, ImageFormat.Png);
        }
        return new Dictionary<string, object>
        {
            ["path"] = path,
            ["width"] = crop.Width,
            ["height"] = crop.Height,
            ["region"] = "game-client",
            ["method"] = "PrintWindow",
            ["visualValidity"] = "requires-viewing",
        };
    }

    private static void Remember(Dictionary<string, object> snapshot)
    {
        var id = Convert.ToString(snapshot["observationId"]);
        if (string.IsNullOrEmpty(id)) return;
        lock (Recent)
        {
            if (!Recent.ContainsKey(id)) RecentOrder.Enqueue(id);
            Recent[id] = snapshot;
            while (RecentOrder.Count > 16) Recent.Remove(RecentOrder.Dequeue());
        }
    }

    private static Dictionary<string, object> DescribeInput(object request)
    {
        var runningTimer = false;
        try
        {
            if (_console != null)
            {
                runningTimer = _consoleType.GetProperty("IsRunningTimer", AnyInstance)?.GetValue(_console) is bool rt && rt;
            }
        }
        catch { }

        if (request is null)
        {
            return new Dictionary<string, object>
            {
                ["kind"] = "Void",
                ["requestId"] = "",
                ["oneInput"] = false,
                ["needValue"] = false,
                ["timeLimit"] = 0L,
                ["runningTimer"] = runningTimer,
                ["systemInput"] = false,
            };
        }
        var type = request.GetType();
        return new Dictionary<string, object>
        {
            ["kind"] = type.GetField("InputType", AnyInstance)?.GetValue(request)?.ToString() ?? "Void",
            ["requestId"] = Convert.ToString(type.GetField("ID", AnyInstance)?.GetValue(request), CultureInfo.InvariantCulture) ?? "",
            ["oneInput"] = type.GetField("OneInput", AnyInstance)?.GetValue(request) is bool one && one,
            ["needValue"] = type.GetProperty("NeedValue", AnyInstance)?.GetValue(request) is bool need && need,
            ["timeLimit"] = Convert.ToInt64(type.GetField("Timelimit", AnyInstance)?.GetValue(request) ?? 0L, CultureInfo.InvariantCulture),
            ["runningTimer"] = runningTimer,
            ["systemInput"] = type.GetField("IsSystemInput", AnyInstance)?.GetValue(request) is bool sys && sys,
        };
    }

    // ---- helpers ----

    private static bool RefreshTargets()
    {
        try
        {
            foreach (Form form in Application.OpenForms)
            {
                var full = form.GetType().FullName ?? string.Empty;
                if (!full.EndsWith("Forms.MainWindow", StringComparison.Ordinal) && !full.EndsWith("MinorShift.Emuera.MainWindow", StringComparison.Ordinal)) continue;
                if (form.IsDisposed || !form.IsHandleCreated) continue;
                var console = form.GetType().GetField("console", AnyInstance)?.GetValue(form);
                if (console is null) continue;
                _window = form; _console = console; _consoleType = console.GetType();
                return true;
            }
        }
        catch (Exception ex) { _failure = ex.ToString(); }
        return false;
    }

    private static Dictionary<string, object> OnUi(Func<Dictionary<string, object>> operation)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 120000)
        {
            RefreshTargets();
            if (_failure != null) return Error("NOT_READY", _failure);
            if (_console != null)
            {
                var stateNow = _consoleType.GetProperty("state", AnyInstance)?.GetValue(_console)?.ToString();
                if (!string.IsNullOrEmpty(stateNow) && stateNow != "Initializing") break;
            }
            Thread.Sleep(100);
        }
        if (_window is null || _window.IsDisposed) return Error("NOT_READY", _failure ?? "engine window is not ready");
        if (_console is null) return Error("NOT_READY", "engine console is not attached yet");
        if (string.Equals(_consoleType.GetProperty("state", AnyInstance)?.GetValue(_console)?.ToString(), "Initializing", StringComparison.Ordinal))
        {
            return Error("NOT_READY", "engine is still initializing; observe again in a moment");
        }
        Dictionary<string, object> result = null;
        Exception failure = null;
        var done = new ManualResetEventSlim();
        var queuedAt = DateTime.UtcNow;
        _window.BeginInvoke((Action)(() =>
        {
            try { result = operation(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); Record("ui-done", new { queuedMs = (int)(DateTime.UtcNow - queuedAt).TotalMilliseconds }); }
        }));
        if (!done.Wait(120000))
        {
            Record("ui-timeout", new { waitedMs = wait.ElapsedMilliseconds });
            return Error("OUTCOME_UNCERTAIN", "UI operation is running; do not repeat the action");
        }
        if (failure != null) return Error("ENGINE_ERROR", Root(failure).Message);
        return result;
    }

    private static void CloseWindow()
    {
        try
        {
            if (_window is not null && !_window.IsDisposed) _window.BeginInvoke((Action)(() => _window.Close()));
        }
        catch { }
    }

    private static bool ExitedSafe() => _window is not null && _window.IsDisposed;

    private static string Str(string name) =>
        _settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : string.Empty;

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : string.Empty;

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static Dictionary<string, object> Error(string code, string message) =>
        new() { ["ok"] = false, ["code"] = code, ["message"] = message };

    private static Exception Root(Exception ex)
    {
        while ((ex is TargetInvocationException || ex is AggregateException) && ex.InnerException != null) ex = ex.InnerException;
        return ex;
    }

    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Utf8.GetBytes(value))).ToLowerInvariant();
    }

    private static void Record(string kind, object value)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(_artifactDir, "host-audit.jsonl"),
                    Json(new { utc = DateTime.UtcNow.ToString("o"), kind, value }) + "\n",
                    Utf8);
            }
            catch { }
        }
    }

    private static void WriteLineSafe(string path, string text)
    {
        try { File.WriteAllText(path, text, Utf8); } catch { }
    }
}
