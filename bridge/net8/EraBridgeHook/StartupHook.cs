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
    // Only observations actually returned to the caller enter this bounded map.
    // A delivery remembers a token/choices, never a historical authority snapshot.
    private sealed class Delivery
    {
        internal string Epoch;
        internal readonly Dictionary<string, string> Choices = new(StringComparer.Ordinal);
    }
    private static readonly Dictionary<string, Delivery> Recent = new(StringComparer.Ordinal);
    private static readonly Queue<string> RecentOrder = new();
    private static DateTime _cachedAt = DateTime.MinValue;
    private static object _epochRequest, _epochConsole;
    private static string _epochSignature, _inputEpoch, _consumedEpoch;
    private static long _inputSerial;
    private static readonly object UiGate = new();
    private static UiWork _uiWork;
    private static string _failure;
    private static int _imageNumber;
    private static int _idleTicks;

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
        _idleTicks++;
        if (_idleTicks <= 3)
        {
            Record("idle-tick", new { tick = _idleTicks, attached = _console != null, forms = Application.OpenForms.Count });
        }
        if (_console != null)
        {
            lock (Gate) if ((DateTime.UtcNow - _cachedAt).TotalMilliseconds < 100) return;
            try
            {
                RefreshTargets();
                Cache(SnapshotCore(new Dictionary<string, object> { ["mode"] = "text", ["limitLines"] = 200, ["history"] = false }));
            }
            catch { }
            return;
        }
        try
        {
            if (RefreshTargets())
            {
                Record("ready", new { window = _window.GetType().FullName, title = _window.Text, console = _consoleType.FullName, tick = _idleTicks });
                try
                {
                    Cache(SnapshotCore(new Dictionary<string, object> { ["mode"] = "text", ["limitLines"] = 200, ["history"] = false }));
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
                        using var document = JsonDocument.Parse(ReadRequest(reader));
                        var request = document.RootElement;
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

    private static bool Flag(Dictionary<string, object> value, string key) =>
        value != null && value.TryGetValue(key, out var flag) && flag is true;

    private static Deadline RequestDeadline(JsonElement request, int fallback, int maximum)
    {
        var budget = request.TryGetProperty("timeoutMs", out var value) ? value.GetInt32() : fallback;
        return new Deadline(Math.Clamp(budget, 1, maximum));
    }

    private static Dictionary<string, object> PendingSnapshot(string reason)
    {
        Dictionary<string, object> result;
        DateTime captured;
        lock (Gate)
        {
            result = _cached is null ? new() : new(_cached);
            captured = _cachedAt;
        }
        result["ok"] = true;
        result["sessionId"] = _sessionId;
        result["attached"] = _console != null && _window is { IsDisposed: false };
        result["ready"] = false;
        result["canAct"] = false;
        result["canAdvance"] = false;
        result["pending"] = true;
        result["phase"] = _failure != null ? "failed" : Flag(result, "attached") ? "loading" : "starting";
        result["readinessReason"] = reason;
        result["stale"] = true;
        result["observationAgeMs"] = captured == DateTime.MinValue ? -1L : (long)(DateTime.UtcNow - captured).TotalMilliseconds;
        // Never issue a usable input credential from a cache-only result.
        result.Remove("inputEpoch");
        if (_failure != null) result["failure"] = _failure;
        return result;
    }

    private static Dictionary<string, object> Observe(JsonElement request)
    {
        // Parse/copy before queuing: callbacks must not retain a JsonDocument lifetime.
        var options = OptionsFrom(request);
        var sampleOptions = new Dictionary<string, object>(options) { ["mode"] = "text" };
        var deadline = RequestDeadline(request, 6000, 8000);
        Dictionary<string, object> latest = null;
        while (!deadline.Expired)
        {
            var fresh = OnUi(() =>
            {
                var snapshot = SnapshotCore(sampleOptions);
                if (Flag(snapshot, "canAct") && !deadline.Expired) AddVisual(snapshot, options);
                return snapshot;
            }, deadline, 1000);
            if (Flag(fresh, "ok"))
            {
                latest = fresh;
                Cache(fresh);
                if (Flag(fresh, "canAct") || Convert.ToString(fresh["phase"]) == "failed")
                    return Deliver(fresh);
            }
            else if (Convert.ToString(fresh.GetValueOrDefault("code")) == "OUTCOME_UNCERTAIN")
            {
                // One in-flight sample owns the slot; never queue behind a busy UI.
                return PendingSnapshot("ui-sample-running");
            }
            if (!deadline.Expired) Thread.Sleep(Math.Min(75, deadline.Remaining));
        }
        if (latest != null)
        {
            latest = new(latest);
            latest["canAct"] = false;
            latest["canAdvance"] = false;
            latest["ready"] = false;
            latest["pending"] = true;
            latest["readinessReason"] = "observe-deadline";
            return Deliver(latest);
        }
        return PendingSnapshot("ui-unavailable-before-deadline");
    }

    private static void Cache(Dictionary<string, object> snapshot)
    {
        lock (Gate) { _cached = snapshot; _cachedAt = DateTime.UtcNow; }
    }

    private static void AddVisual(Dictionary<string, object> snapshot, Dictionary<string, object> options)
    {
        var mode = Convert.ToString(options.GetValueOrDefault("mode", "text"));
        if (mode == "visual" || mode == "both" || mode == "auto")
        {
            try { snapshot["screenshot"] = Capture(); }
            catch (Exception ex) { snapshot["screenshotError"] = Root(ex).Message; }
        }
        if (mode == "visual") { snapshot["text"] = null; snapshot["lines"] = null; }
    }

    private static string Dispatch(JsonElement request)
    {
        var action = GetString(request, "action");
        switch (action)
        {
            case "status":
            {
                var status = PendingSnapshot("fresh-observation-required");
                status.Remove("text"); status.Remove("lines"); status.Remove("buttons"); status.Remove("screenshot");
                status["exited"] = ExitedSafe();
                if (ExitedSafe()) status["phase"] = "exited";
                status["engineFamily"] = "core";
                status["launchMode"] = "startup-hook";
                status["gameDir"] = Str("gameRoot");
                status["processId"] = Environment.ProcessId;
                status["launched"] = true;
                status["requiresObservation"] = true;
                return Json(status);
            }
            case "close":
                return Json(new Dictionary<string, object> { ["ok"] = true, ["status"] = "normal_close_scheduled" });
            case "observe":
                return Json(Observe(request));
            case "act":
                return Act(request);
            case "advance":
                return Advance(request);
            default:
                return Json(Error("UNKNOWN_ACTION", "Use status, observe, act, or close"));
        }
    }

    // Each step is its own UI operation: timers/posted engine work get a turn
    // between inputs, and the one request deadline bounds the whole advance.
    private static string Advance(JsonElement request)
    {
        var options = OptionsFrom(request);
        var sampleOptions = new Dictionary<string, object>(options) { ["mode"] = "text" };
        var maxSteps = request.TryGetProperty("maxSteps", out var stepsValue) ? Math.Clamp(stepsValue.GetInt32(), 1, 200) : 20;
        var deadline = RequestDeadline(request, 10000, 10000);
        var steps = 0;
        var stopped = "deadline";
        Dictionary<string, object> snapshot = null;
        string submittedEpoch = null;
        var progress = System.Diagnostics.Stopwatch.StartNew();
        while (!deadline.Expired)
        {
            var step = OnUi(() =>
            {
                var current = SnapshotCore(sampleOptions);
                var waitingForEngine = Convert.ToString(current["state"]) is "Running" or "Initializing" or "Sleep";
                if (submittedEpoch != null && (Convert.ToString(current["inputEpoch"]) == submittedEpoch ||
                    (!Flag(current, "canAct") && waitingForEngine)))
                    return new() { ["ok"] = true, ["waitingForProgress"] = true, ["observation"] = current };
                if (!Flag(current, "canAdvance"))
                    return new() { ["ok"] = true, ["stop"] = Flag(current, "canAct") ? "input-required" : "not-waiting-input", ["observation"] = current };
                if (steps >= maxSteps)
                    return new() { ["ok"] = true, ["stop"] = "max-steps", ["observation"] = current };
                var press = _consoleType.GetMethod("PressEnterKey", AnyInstance, null, new[] { typeof(bool), typeof(string), typeof(bool) }, null);
                if (press is null) return Error("ADAPTER_MEMBER_MISSING", "PressEnterKey(bool,string,bool) is absent");
                if (deadline.Expired) return Error("CANCELLED_BEFORE_EXECUTION", "Advance deadline expired before input");
                _consumedEpoch = Convert.ToString(current["inputEpoch"]);
                press.Invoke(_console, new object[] { false, string.Empty, false });
                Interlocked.Increment(ref steps);
                return new() { ["ok"] = true, ["submittedEpoch"] = _consumedEpoch, ["observation"] = SnapshotCore(sampleOptions) };
            }, deadline, Math.Min(1000, deadline.Remaining));
            if (!Flag(step, "ok"))
            {
                stopped = Convert.ToString(step.GetValueOrDefault("code"));
                // If a step has started, it may finish after this response. Do not
                // make a repeatable-looking success out of an uncertain input.
                step["steps"] = Volatile.Read(ref steps);
                step["stoppedBecause"] = stopped;
                if (snapshot != null) step["observation"] = Deliver(snapshot);
                return Json(step);
            }
            snapshot = (Dictionary<string, object>)step["observation"];
            Cache(snapshot);
            if (step.TryGetValue("stop", out var stop)) { stopped = Convert.ToString(stop); break; }
            if (step.TryGetValue("submittedEpoch", out var sent))
            {
                submittedEpoch = Convert.ToString(sent);
                progress.Restart();
            }
            else if (Flag(step, "waitingForProgress") && progress.ElapsedMilliseconds >= 750)
            {
                stopped = "stalled";
                break;
            }
            if (!deadline.Expired) Thread.Sleep(Math.Min(25, deadline.Remaining));
        }
        if (snapshot is null) snapshot = PendingSnapshot("advance-deadline");
        else
        {
            // This optional final projection shares the same deadline, never adds
            // another long wait after all the steps have consumed the budget.
            if (!deadline.Expired)
            {
                var final = OnUi(() => { var x = SnapshotCore(sampleOptions); AddVisual(x, options); return x; }, deadline, Math.Min(300, deadline.Remaining));
                if (Flag(final, "ok")) snapshot = final;
            }
            Deliver(snapshot);
        }
        return Json(new Dictionary<string, object> { ["ok"] = true, ["steps"] = Volatile.Read(ref steps), ["stoppedBecause"] = stopped, ["observation"] = snapshot });
    }

    private static string Act(JsonElement request)
    {
        var options = OptionsFrom(request);
        var command = request.Clone();
        var deadline = RequestDeadline(request, 5000, 5000);
        var actionId = GetString(command, "actionId");
        if (!Guid.TryParse(actionId, out var parsed)) return Json(Error("INVALID_ACTION_ID", "A unique GUID actionId is required"));
        actionId = parsed.ToString("D");
        lock (Gate)
        {
            if (Actions.TryGetValue(actionId, out var prior))
                return Json(new Dictionary<string, object> { ["ok"] = false, ["code"] = "DUPLICATE_ACTION", ["action"] = new Dictionary<string, object>(prior) });
            if (Actions.Count >= 10000) return Json(Error("ACTION_LIMIT", "Session action limit reached"));
            Actions[actionId] = new() { ["actionId"] = actionId, ["state"] = "queued", ["submissionStarted"] = false };
        }
        Record("action-intent", new { actionId, observationId = GetString(command, "observationId"), kind = GetString(command, "kind") });
        var result = OnUi(() =>
        {
            lock (Gate) Actions[actionId]["state"] = "executing";
            Dictionary<string, object> answer;
            try { answer = ActCore(command, actionId, options, deadline); }
            catch (Exception ex)
            {
                bool submitted;
                lock (Gate) submitted = Flag(Actions[actionId], "submissionStarted");
                answer = Error(submitted ? "ACTION_OUTCOME_UNCERTAIN" : "ENGINE_ERROR", Root(ex).Message);
            }
            lock (Gate)
            {
                var code = Convert.ToString(answer.GetValueOrDefault("code"));
                Actions[actionId]["state"] = Flag(answer, "submitted") ? "completed" :
                    code == "CANCELLED_BEFORE_EXECUTION" ? "cancelled" :
                    code.Contains("UNCERTAIN", StringComparison.Ordinal) ? "uncertain" : "rejected";
                Actions[actionId]["result"] = answer;
            }
            Record("action-result", new { actionId, result = answer });
            return answer;
        }, deadline);
        lock (Gate)
        {
            var state = Convert.ToString(Actions[actionId]["state"]);
            if (state == "queued")
            {
                Actions[actionId]["state"] = "cancelled";
                Actions[actionId]["result"] = result;
            }
            else if (state == "executing")
            {
                Actions[actionId]["state"] = "uncertain";
                Actions[actionId]["result"] = result;
            }
        }
        if (result.TryGetValue("observation", out var returned) && returned is Dictionary<string, object> observation) Deliver(observation);
        if (result.TryGetValue("current", out var currentValue) && currentValue is Dictionary<string, object> current) Deliver(current);
        return Json(result);
    }

    private static Dictionary<string, object> ActCore(JsonElement request, string actionId, Dictionary<string, object> options, Deadline deadline)
    {
        // This callback is on the UI thread. Neither Recent nor the idle cache may
        // supply live state, an input contract, active choices, or permission.
        var current = SnapshotCore(new Dictionary<string, object> { ["mode"] = "text", ["limitLines"] = 200 });
        var claimed = GetString(request, "observationId");
        Delivery delivery;
        lock (Recent) Recent.TryGetValue(claimed, out delivery);
        var currentEpoch = Convert.ToString(current["inputEpoch"]);
        if (delivery is null || !string.Equals(delivery.Epoch, currentEpoch, StringComparison.Ordinal))
        {
            var stale = Error("STALE_OBSERVATION", "Observe the current input request before acting");
            stale["discriminator"] = delivery is null ? "unknown-delivered-observation" : "input-epoch-changed";
            stale["claimedInputEpoch"] = delivery?.Epoch;
            stale["currentInputEpoch"] = currentEpoch;
            stale["currentRequestId"] = ((Dictionary<string, object>)current["input"])["requestId"];
            stale["current"] = current;
            return stale;
        }
        if (!Flag(current, "canAct"))
            return new() { ["ok"] = false, ["code"] = "NOT_WAITING_INPUT", ["discriminator"] = "current-not-actionable", ["current"] = current };

        var input = (Dictionary<string, object>)current["input"];
        var inputKind = Convert.ToString(input["kind"]);
        var kind = GetString(request, "kind");
        var value = GetString(request, "value");
        if (kind == "choose")
        {
            if (!Flag(input, "needValue")) return Error("WRONG_INPUT_KIND", "Current request does not accept a value");
            if (!delivery.Choices.TryGetValue(GetString(request, "choiceId"), out var choiceToken))
                return Error("CHOICE_NOT_FOUND", "Choice was not delivered in this observation");
            // Match a full live semantic choice, not its old display position.
            var frame = ReadInputFrame((IList)_consoleType.GetProperty("DisplayLineList", AnyInstance).GetValue(_console));
            if (!frame.CanAct || frame.Epoch != currentEpoch) return Error("STALE_OBSERVATION", "Live choice request changed during validation");
            var chosen = frame.Buttons.Find(b => b.Active && b.Token == choiceToken);
            if (chosen is null) return Error("STALE_BUTTON", "Choice is absent from the current input request");
            if (chosen.Value.Length > 2048) return Error("CHOICE_VALUE_TOO_LONG", "Button value exceeds the action limit");
            value = chosen.Value;
        }
        else if (kind == "continue")
        {
            if (inputKind != "EnterKey" && inputKind != "AnyKey") return Error("WRONG_INPUT_KIND", "Continue requires an untimed EnterKey/AnyKey request");
            value = string.Empty;
        }
        else if (kind != "input") return Error("INVALID_ACTION_KIND", "Use choose, input, or continue");
        if (value.Length > 2048 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || value.StartsWith("@", StringComparison.Ordinal))
            return Error("INVALID_INPUT", "Input is bounded single-line text; debug commands are not exposed");
        if ((inputKind == "IntValue" || inputKind == "IntButton") && !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            return Error("INTEGER_REQUIRED", "Current request expects an integer");
        if (kind != "continue" && !ValueInput(inputKind)) return Error("WRONG_INPUT_KIND", "Use continue for key waits");
        var press = _consoleType.GetMethod("PressEnterKey", AnyInstance, null, new[] { typeof(bool), typeof(string), typeof(bool) }, null);
        if (press is null) return Error("ADAPTER_MEMBER_MISSING", "PressEnterKey(bool,string,bool) is absent");
        if (deadline.Expired) return Error("CANCELLED_BEFORE_EXECUTION", "Action deadline expired before input");
        lock (Gate) Actions[actionId]["submissionStarted"] = true;
        _consumedEpoch = currentEpoch;
        press.Invoke(_console, new object[] { false, value, false });
        var answer = new Dictionary<string, object> { ["ok"] = true, ["actionId"] = actionId, ["submitted"] = true, ["value"] = value };
        try
        {
            var afterOptions = new Dictionary<string, object>(options) { ["mode"] = "text" };
            var after = SnapshotCore(afterOptions);
            if (!deadline.Expired) AddVisual(after, options);
            Cache(after);
            answer["observationChanged"] = Convert.ToString(after["observationId"]) != Convert.ToString(current["observationId"]);
            answer["observation"] = after;
        }
        catch (Exception ex)
        {
            // A screenshot/projection failure after submission is not a failed key.
            answer["observationError"] = Root(ex).Message;
            answer["pending"] = true;
        }
        return answer;
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

    private static Dictionary<string, object> OptionsFrom(JsonElement request)
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
        var since = GetString(request, "sinceObservationId");
        if (since.Length > 0) options["since"] = since;
        if (request.TryGetProperty("sinceCursor", out var sinceCursor) && sinceCursor.ValueKind == JsonValueKind.Number)
        {
            options["cursor"] = sinceCursor.GetInt64();
        }
        var selectedMode = Convert.ToString(options["mode"]);
        if (selectedMode != "text" && selectedMode != "visual" && selectedMode != "both" && selectedMode != "auto")
            throw new ArgumentException("mode must be text, visual, both or auto");
        if (options.TryGetValue("limitLines", out var count) && (Convert.ToInt32(count) < 1 || Convert.ToInt32(count) > 200))
            throw new ArgumentOutOfRangeException("limitLines", "Use 1..200");
        if (options.TryGetValue("fromLine", out var start) && Convert.ToInt32(start) < 0) throw new ArgumentOutOfRangeException("fromLine");
        if (options.TryGetValue("cursor", out var cursor) && Convert.ToInt64(cursor) < 0) throw new ArgumentOutOfRangeException("sinceCursor");
        if (options.TryGetValue("noise", out var selectedNoise) && Convert.ToString(selectedNoise) != "raw" && Convert.ToString(selectedNoise) != "filter")
            throw new ArgumentException("noise must be raw or filter");
        return options;
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

        // Engine line numbers are absolute for the session: DeletedLines counts the
        // rows the engine has already dropped from the front of its buffer. A cursor is
        // expressed in that space so it stays meaningful after trimming.
        long deletedLines = 0;
        try
        {
            var deletedProperty = _consoleType.GetProperty("DeletedLines", AnyInstance);
            if (deletedProperty != null) deletedLines = Convert.ToInt64(deletedProperty.GetValue(_console) ?? 0L, CultureInfo.InvariantCulture);
            else
            {
                var deletedField = _consoleType.GetField("deletedLines", AnyInstance);
                if (deletedField != null) deletedLines = Convert.ToInt64(deletedField.GetValue(_console) ?? 0L, CultureInfo.InvariantCulture);
            }
        }
        catch { }
        if (deletedLines < 0) deletedLines = 0;

        var start = Math.Max(0, allLines.Count - limit);
        if (options.TryGetValue("fromLine", out var fromValue)) start = Math.Max(0, Math.Min(allLines.Count, Convert.ToInt32(fromValue)));

        var cursorExpired = false;
        if (options.TryGetValue("cursor", out var cursorValue))
        {
            var requested = Convert.ToInt64(cursorValue, CultureInfo.InvariantCulture);
            if (requested < deletedLines || requested > deletedLines + allLines.Count)
            {
                cursorExpired = true;
            }
            else
            {
                start = Math.Max(0, Math.Min(allLines.Count, (int)(requested - deletedLines)));
            }
        }

        var frame = ReadInputFrame(allLines);
        var state = frame.State;
        var input = frame.Input;
        var currentGeneration = frame.Generation;

        var lines = new List<object>();
        var buttons = new List<object>();
        var text = new StringBuilder();
        var displayed = new List<string>();
        var displayedIndex = new List<int>();
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
            displayed.Add(display);
            displayedIndex.Add(index);
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

        // Authorization/fingerprinting use every semantic button. The bounded
        // output is just a view and is deliberately independent of text filtering.
        foreach (var button in frame.Buttons)
        {
            if (!button.Active) { omittedInactive++; continue; }
            if (buttons.Count == 128) { buttons.RemoveAt(0); omittedButtons++; }
            buttons.Add(new Dictionary<string, object>
            {
                ["id"] = button.Id,
                ["label"] = button.Label.Length > 512 ? button.Label.Substring(0, 512) : button.Label,
                ["labelTruncated"] = button.Label.Length > 512,
                ["value"] = button.Value.Length > 2048 ? null : button.Value,
                ["valueTooLong"] = button.Value.Length > 2048,
                ["choosable"] = button.Value.Length <= 2048,
                ["active"] = button.Active,
                ["integer"] = button.Integer,
                ["generation"] = button.Generation,
                ["lineIndex"] = button.LineIndex,
            });
        }
        long rawTextLength = 0;
        foreach (var line in allLines)
        {
            rawTextLength += (line?.ToString()?.Length ?? 0) + 2L;
            if (rawTextLength > 8 * 1024 * 1024) throw new InvalidOperationException("Engine display buffer exceeds 8 Mi characters");
        }
        var fingerprint = HashParts(SnapshotParts(frame, allLines));
        var projection = HashParts(new[] { mode, limit.ToString(CultureInfo.InvariantCulture),
            Convert.ToString(options.GetValueOrDefault("fromLine"), CultureInfo.InvariantCulture),
            Convert.ToString(options.GetValueOrDefault("cursor"), CultureInfo.InvariantCulture), filterNoise.ToString() });

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["sessionId"] = _sessionId,
            ["observationId"] = _sessionId + ":" + fingerprint,
            ["capturedUtc"] = DateTime.UtcNow.ToString("o"),
            ["source"] = "engine-display-buffer",
            ["consistency"] = "same-ui-thread; input epoch independent of presentation",
            ["inputEpoch"] = frame.Epoch,
            ["projectionId"] = projection,
            ["attached"] = true,
            ["pending"] = !frame.CanAct,
            ["canAct"] = frame.CanAct,
            ["canAdvance"] = frame.CanAdvance,
            ["ready"] = frame.CanAct,
            ["phase"] = frame.Phase,
            ["readinessReason"] = frame.Reason,
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
            ["cursor"] = deletedLines + allLines.Count,
            ["nextCursor"] = deletedLines + Math.Min(allLines.Count, start + limit),
            ["cursorBase"] = deletedLines,
            ["cursorExpired"] = cursorExpired,
            ["totalLines"] = allLines.Count,
            ["fromLine"] = start,
            ["returnedLines"] = includeLines ? lines.Count : 0,
            ["omittedBefore"] = start,
            ["omittedAfter"] = Math.Max(0, allLines.Count - start - limit),
            ["rawTextLength"] = rawTextLength,
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
        var since = options.ContainsKey("since") ? Convert.ToString(options["since"]) : string.Empty;
        if (!string.IsNullOrEmpty(since))
        {
            ApplyDelta(result, since, displayed, displayedIndex);
        }
        if (!options.TryGetValue("history", out var history) || history is not false)
            RememberHistory(Convert.ToString(result["observationId"]) + ":" + projection, displayed);
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

    // ---- incremental observations ----
    //
    // A caller that already has an observation can ask for the difference instead of
    // the whole screen. The comparison runs over the lines as they were delivered
    // (after filtering and separator collapsing), so the indices match what the caller
    // saw. Observation ids hash the full buffer; input authorization is separately
    // checked against the live input epoch, regardless of a delta's text projection.
    private static readonly Dictionary<string, List<string>> DeltaHistory = new();
    private static readonly Queue<string> DeltaOrder = new();

    private static void RememberHistory(string id, List<string> displayed)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (DeltaHistory)
        {
            if (!DeltaHistory.ContainsKey(id)) DeltaOrder.Enqueue(id);
            DeltaHistory[id] = new List<string>(displayed);
            while (DeltaOrder.Count > 8) DeltaHistory.Remove(DeltaOrder.Dequeue());
        }
    }

    private static void ApplyDelta(Dictionary<string, object> result, string since, List<string> displayed, List<int> displayedIndex)
    {
        var delta = new Dictionary<string, object>();
        List<string> previous;
        var known = false;
        lock (DeltaHistory) known = DeltaHistory.TryGetValue(since + ":" + Convert.ToString(result["projectionId"]), out previous);

        if (!known)
        {
            delta["kind"] = "full";
            delta["sinceMissing"] = true;
            result["delta"] = delta;
            return;
        }

        var common = 0;
        while (common < previous.Count && common < displayed.Count && string.Equals(previous[common], displayed[common], StringComparison.Ordinal))
        {
            common++;
        }

        if (common == previous.Count && common == displayed.Count)
        {
            delta["kind"] = "none";
            result["text"] = null;
            result["lines"] = null;
            result["delta"] = delta;
            return;
        }

        var kind = common == previous.Count ? "append" : "changed";
        var tail = displayed.Count - common;
        const int cap = 200;
        var tailCharacters = displayed.Skip(common).Sum(line => (long)line.Length + 2);
        if (tail > cap || tailCharacters > 24000)
        {
            // Too much to send as a delta; the full text is already in the result.
            delta["kind"] = "full";
            delta["reason"] = "delta exceeds bounded line/character budget";
            result["delta"] = delta;
            return;
        }

        var appended = new List<object>();
        for (var i = common; i < displayed.Count; i++)
        {
            appended.Add(new Dictionary<string, object>
            {
                ["index"] = displayedIndex[i],
                ["text"] = displayed[i],
            });
        }

        delta["kind"] = kind;
        delta["fromIndex"] = displayedIndex.Count > common ? displayedIndex[common] : -1;
        delta["fromOrdinal"] = common;
        delta["removed"] = kind == "changed" ? previous.Count - common : 0;
        delta["lines"] = appended;
        result["delta"] = delta;
        result["text"] = null;
        result["lines"] = null;
    }

    private static Dictionary<string, object> Deliver(Dictionary<string, object> snapshot)
    {
        if (!Flag(snapshot, "canAct") || !snapshot.TryGetValue("observationId", out var idValue) ||
            !snapshot.TryGetValue("inputEpoch", out var epochValue)) return snapshot;
        var id = Convert.ToString(idValue);
        var delivery = new Delivery { Epoch = Convert.ToString(epochValue) };
        if (snapshot.TryGetValue("buttons", out var buttonsValue) && buttonsValue is List<object> buttons)
            foreach (Dictionary<string, object> button in buttons)
                if (Flag(button, "active") && Flag(button, "choosable"))
                    delivery.Choices[Convert.ToString(button["id"])] = ChoiceToken(Convert.ToString(button["generation"]), Flag(button, "integer"), Convert.ToString(button["value"]));
        lock (Recent)
        {
            if (!Recent.ContainsKey(id)) RecentOrder.Enqueue(id);
            Recent[id] = delivery;
            while (RecentOrder.Count > 64) Recent.Remove(RecentOrder.Dequeue());
        }
        return snapshot;
    }

    private sealed class ButtonInfo
    {
        internal string Id, Value, Generation, Label, Token;
        internal bool Integer, Active;
        internal int LineIndex;
    }

    private sealed class InputFrame
    {
        internal string State, Epoch, Phase, Reason;
        internal bool ContractValid, CanAct, CanAdvance;
        internal long Generation;
        internal Dictionary<string, object> Input;
        internal readonly List<ButtonInfo> Buttons = new();
    }

    private static bool TryRead(object target, string name, out object value)
    {
        value = null;
        if (target is null) return false;
        var type = target.GetType();
        var field = type.GetField(name, AnyInstance);
        if (field != null) { value = field.GetValue(target); return true; }
        var property = type.GetProperty(name, AnyInstance);
        if (property is null || property.GetIndexParameters().Length != 0) return false;
        value = property.GetValue(target);
        return true;
    }

    private static bool ValueInput(string kind) => kind == "IntValue" || kind == "StrValue" ||
        kind == "IntButton" || kind == "StrButton" || kind == "AnyValue";

    private static string ChoiceToken(string generation, bool integer, string value) =>
        HashParts(new[] { generation, integer ? "integer" : "text", value });

    private static InputFrame ReadInputFrame(IList allLines)
    {
        var missing = new List<string>();
        object Required(object target, string member)
        {
            if (!TryRead(target, member, out var value) || value is null) { missing.Add(member); return null; }
            return value;
        }
        var state = Convert.ToString(Required(_console, "state"));
        var request = Required(_console, "inputReq");
        var kind = Convert.ToString(Required(request, "InputType"));
        var id = Convert.ToString(Required(request, "ID"), CultureInfo.InvariantCulture);
        var oneValue = Required(request, "OneInput");
        var needValue = Required(request, "NeedValue");
        var limitValue = Required(request, "Timelimit");
        var systemValue = Required(request, "IsSystemInput");
        var timerValue = Required(_console, "IsRunningTimer");
        if (oneValue is not bool) missing.Add("OneInput:bool");
        if (needValue is not bool) missing.Add("NeedValue:bool");
        if (systemValue is not bool) missing.Add("IsSystemInput:bool");
        if (timerValue is not bool) missing.Add("IsRunningTimer:bool");
        var limit = limitValue is null ? 0L : Convert.ToInt64(limitValue, CultureInfo.InvariantCulture);
        var timed = limit > 0 || timerValue is true;
        var input = new Dictionary<string, object>
        {
            ["kind"] = string.IsNullOrEmpty(kind) ? "Void" : kind,
            ["requestId"] = id,
            ["oneInput"] = oneValue is true,
            ["needValue"] = needValue is true,
            ["timeLimit"] = limit,
            ["runningTimer"] = timerValue is true,
            ["systemInput"] = systemValue is true,
            ["timed"] = timed,
            ["timedUserInput"] = timed && ValueInput(kind),
        };
        var frame = new InputFrame { State = state, Input = input };
        var hasGeneration = TryRead(_console, "lastButtonGeneration", out var generation);
        if (hasGeneration) frame.Generation = Convert.ToInt64(generation, CultureInfo.InvariantCulture);
        for (var index = 0; index < allLines.Count; index++)
        {
            var line = allLines[index];
            var lineNo = TryRead(line, "LineNo", out var number) ? Convert.ToInt32(number, CultureInfo.InvariantCulture) : index;
            if (!TryRead(line, "Buttons", out var array) || array is not Array buttons) continue;
            for (var buttonIndex = 0; buttonIndex < buttons.Length; buttonIndex++)
            {
                var button = buttons.GetValue(buttonIndex);
                if (!TryRead(button, "IsButton", out var isButton) || isButton is not true) continue;
                var integerValue = Required(button, "IsInteger");
                if (integerValue is not bool) missing.Add("button.IsInteger:bool");
                var integer = integerValue is true;
                var raw = Convert.ToString(Required(button, integer ? "Input" : "Inputs"), CultureInfo.InvariantCulture) ?? string.Empty;
                var gen = Convert.ToString(Required(button, "Generation"), CultureInfo.InvariantCulture);
                var active = needValue is true && hasGeneration && gen == frame.Generation.ToString(CultureInfo.InvariantCulture);
                frame.Buttons.Add(new ButtonInfo
                {
                    Id = "b-" + lineNo + "-" + buttonIndex + "-" + gen, Value = raw,
                    Generation = gen, Label = button.ToString() ?? string.Empty, Integer = integer,
                    Active = active, LineIndex = index, Token = ChoiceToken(gen, integer, raw),
                });
            }
        }
        if (needValue is true && frame.Buttons.Count > 0 && !hasGeneration) missing.Add("lastButtonGeneration");
        frame.ContractValid = missing.Count == 0;
        input["contractValid"] = frame.ContractValid;
        if (missing.Count > 0) input["missingMembers"] = missing.Distinct(StringComparer.Ordinal).ToArray();
        // No display positions, labels, timer countdowns, pages, colors, or window
        // geometry enter this token. Identity also detects identical-looking requests.
        var signature = HashParts(new[] { state, kind, id, Convert.ToString(oneValue), Convert.ToString(needValue),
            limit.ToString(CultureInfo.InvariantCulture), Convert.ToString(systemValue), frame.ContractValid.ToString(),
            frame.Generation.ToString(CultureInfo.InvariantCulture) }.Concat(frame.Buttons.Where(b => b.Active).Select(b => b.Token).OrderBy(x => x, StringComparer.Ordinal)));
        if (!ReferenceEquals(request, _epochRequest) || !ReferenceEquals(_console, _epochConsole) || signature != _epochSignature)
        {
            _epochRequest = request; _epochConsole = _console; _epochSignature = signature;
            _inputEpoch = _sessionId + ":input:" + (++_inputSerial).ToString(CultureInfo.InvariantCulture) + ":" + signature;
        }
        frame.Epoch = _inputEpoch;
        var key = kind == "EnterKey" || kind == "AnyKey";
        var supported = ValueInput(kind) || key;
        var consistent = ValueInput(kind) ? needValue is true : key && needValue is false;
        frame.CanAct = frame.ContractValid && consistent && state == "WaitInput" && supported && (systemValue is false || ValueInput(kind)) &&
            (ValueInput(kind) || !timed) && frame.Epoch != _consumedEpoch;
        frame.CanAdvance = frame.CanAct && systemValue is false && key && needValue is false && !timed && !frame.Buttons.Any(b => b.Active);
        frame.Phase = state == "Initializing" || string.IsNullOrEmpty(state) ? "loading" :
            state == "Error" || state == "Quit" ? "failed" : frame.CanAct ? "waiting-input" : "running";
        frame.Reason = !frame.ContractValid ? "adapter-contract-missing" : state != "WaitInput" ? "engine-not-waiting" :
            systemValue is true && !ValueInput(kind) ? "system-wait" : !supported ? "unsupported-input-kind" : !consistent ? "inconsistent-input-contract" : key && timed ? "timed-key-wait" :
            frame.Epoch == _consumedEpoch ? "input-already-submitted" : "input-ready";
        // A null request during initialization is ordinary loading, not a broken adapter.
        if (!frame.ContractValid && request != null && state == "WaitInput") frame.Phase = "failed";
        return frame;
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

    private sealed class Deadline
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly int _budget;
        internal Deadline(int budget) { _budget = budget; }
        internal int Remaining => Math.Max(0, _budget - (int)_clock.ElapsedMilliseconds);
        internal bool Expired => Remaining <= 0;
    }

    private sealed class UiWork
    {
        // queued / running / completed / cancelled-before-execution
        internal int State;
        internal readonly TaskCompletionSource<Dictionary<string, object>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Dictionary<string, object> OnUi(Func<Dictionary<string, object>> operation, Deadline deadline, int sliceMs = int.MaxValue)
    {
        if (deadline.Expired) return Error("CANCELLED_BEFORE_EXECUTION", "Request deadline expired before queuing UI work");
        var window = _window;
        if (window is null || window.IsDisposed || _console is null) return Error("NOT_READY", "Engine UI is not attached yet");
        var work = new UiWork();
        lock (UiGate)
        {
            // Even a cancelled callback keeps the slot until it is drained. A busy
            // UI can therefore accumulate at most one bridge callback, not one per poll.
            if (_uiWork != null) return Error("UI_BUSY", "A prior UI callback has not drained; observe again");
            _uiWork = work;
        }
        void Release()
        {
            lock (UiGate) if (ReferenceEquals(_uiWork, work)) _uiWork = null;
        }
        try
        {
            window.BeginInvoke((Action)(() =>
            {
                Dictionary<string, object> result;
                if (Interlocked.CompareExchange(ref work.State, 1, 0) != 0)
                {
                    Release();
                    return; // caller cancelled while this callback was still queued
                }
                try
                {
                    if (deadline.Expired)
                    {
                        Interlocked.Exchange(ref work.State, 3);
                        result = Error("CANCELLED_BEFORE_EXECUTION", "Request deadline expired before UI execution");
                    }
                    else result = operation();
                }
                catch (Exception ex) { result = Error("ENGINE_ERROR", Root(ex).Message); }
                if (Volatile.Read(ref work.State) != 3) Interlocked.Exchange(ref work.State, 2);
                work.Completion.TrySetResult(result);
                Release();
            }));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref work.State, 3);
            Release();
            return Error("CANCELLED_BEFORE_EXECUTION", Root(ex).Message);
        }
        var waitMs = Math.Min(deadline.Remaining, Math.Max(1, sliceMs));
        if (waitMs > 0 && work.Completion.Task.Wait(waitMs)) return work.Completion.Task.Result;
        if (Interlocked.CompareExchange(ref work.State, 3, 0) == 0)
        {
            var cancelled = Error("CANCELLED_BEFORE_EXECUTION", "UI work cancelled before execution");
            work.Completion.TrySetResult(cancelled);
            return cancelled;
        }
        if (work.Completion.Task.IsCompleted) return work.Completion.Task.Result;
        return Error("OUTCOME_UNCERTAIN", "UI operation has started; do not repeat an input with a new actionId");
    }

    private static IEnumerable<string> SnapshotParts(InputFrame frame, IList allLines)
    {
        yield return frame.State;
        yield return frame.Epoch;
        yield return Json(frame.Input);
        yield return frame.CanAct.ToString();
        yield return allLines.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var line in allLines) { yield return "line"; yield return line?.ToString() ?? string.Empty; }
        foreach (var button in frame.Buttons)
        {
            yield return "button"; yield return button.Id; yield return button.Generation;
            yield return button.Value; yield return button.Integer.ToString(); yield return button.Label;
            yield return button.Active.ToString();
        }
    }

    private static string HashParts(IEnumerable<string> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            var bytes = Utf8.GetBytes(part ?? string.Empty);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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
