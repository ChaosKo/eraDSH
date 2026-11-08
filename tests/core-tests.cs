// Independent, package-free fake-engine regression harness. Never starts a game.
// Run: dotnet run --project tests/core-tests.csproj -c Release
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace CoreFake.Forms
{
    public sealed class MainWindow : Form
    {
        internal ConsoleModel console;
        public MainWindow() { ShowInTaskbar = false; Opacity = 0; Text = "eraDSH fake core tests"; }
    }

    public sealed class InputRequest
    {
        public string InputType = "IntValue";
        public long ID = 1;
        public bool OneInput, IsSystemInput;
        public long Timelimit = -1;
        public bool NeedValue => InputType is "IntValue" or "StrValue" or "IntButton" or "StrButton" or "AnyValue";
    }

    public sealed class IncompleteRequest
    {
        public string InputType = "IntValue";
        public long ID = 1;
        public bool OneInput, IsSystemInput;
        public long Timelimit = -1;
    }

    public sealed class Button
    {
        public bool IsButton => true;
        public bool IsInteger { get; set; } = true;
        public long Input { get; set; } = 1;
        public string Inputs { get; set; } = "one";
        public long Generation { get; set; } = 3;
        public string Label = "choice";
        public override string ToString() => Label;
    }

    public sealed class Line
    {
        public int LineNo;
        public string Text = "line";
        public Button[] Buttons { get; set; } = Array.Empty<Button>();
        public int DelayMs;
        public override string ToString() { if (DelayMs > 0) Thread.Sleep(DelayMs); return Text; }
    }

    public sealed class ConsoleModel
    {
        public string state { get; set; } = "WaitInput";
        public object inputReq = new InputRequest();
        public bool IsRunningTimer { get; set; }
        public long lastButtonGeneration = 3;
        public long DeletedLines => 0;
        public ArrayList DisplayLineList { get; } = new();
        public int PressCount;
        public string LastValue;
        public Action OnPress;
        public void PressEnterKey(bool a, string value, bool b)
        {
            PressCount++;
            LastValue = value;
            if (OnPress != null) OnPress();
            else inputReq = new InputRequest { ID = ((InputRequest)inputReq).ID + 1 };
        }
    }
}

namespace CoreTests
{
    internal static class Program
    {
        private static readonly Type BridgeType = typeof(Bridge);
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly List<string> Passed = new();
        private static CoreFake.Forms.MainWindow Window;
        private static CoreFake.Forms.ConsoleModel Model;
        private static int UiExceptions;
        private static Exception Failure;

        [STAThread]
        private static int Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => { Interlocked.Increment(ref UiExceptions); Failure = e.Exception; };
            Window = new();
            Window.Shown += (_, _) => Task.Run(() =>
            {
                try { Run(); }
                catch (Exception ex) { Failure = ex; }
                finally { Window.BeginInvoke((Action)(() => Window.Close())); }
            });
            Application.Run(Window);
            System.Console.WriteLine(JsonSerializer.Serialize(new { passed = Failure == null, count = Passed.Count, checks = Passed, uiExceptions = UiExceptions, failure = Failure?.ToString(), realEngineStarted = false }));
            return Failure == null ? 0 : 1;
        }

        private static void Check(string name, bool pass)
        {
            if (!pass) throw new InvalidOperationException("Failed: " + name);
            Passed.Add(name);
        }
        private static object Call(string name, params object[] arguments)
        {
            try { return BridgeType.GetMethod(name, PrivateStatic).Invoke(null, arguments); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        private static void Set(string name, object value) => BridgeType.GetField(name, PrivateStatic).SetValue(null, value);
        private static object Get(string name) => BridgeType.GetField(name, PrivateStatic).GetValue(null);
        private static T Ui<T>(Func<T> callback) => (T)Window.Invoke(callback);
        private static void Ui(Action callback) => Window.Invoke(callback);
        private static void Clear(string name) => Get(name).GetType().GetMethod("Clear").Invoke(Get(name), null);
        private static JsonElement Request(object value) => JsonSerializer.SerializeToElement(value);
        private static string S(Dictionary<string, object> snapshot, string key) => Convert.ToString(snapshot[key]);
        private static bool B(Dictionary<string, object> snapshot, string key) => snapshot.TryGetValue(key, out var v) && v is true;
        private static Dictionary<string, object> Snap(Dictionary<string, object> options = null) => Ui(() => (Dictionary<string, object>)Call("SnapshotCore", options ?? new() { ["mode"] = "text", ["limitLines"] = 80 }));
        private static Dictionary<string, object> Observe(int budget = 1000) => (Dictionary<string, object>)Call("Observe", Request(new { mode = "text", timeoutMs = budget, limitLines = 80 }));
        private static JsonElement Act(string observationId, string kind = "input", string value = "1", string choiceId = "", string actionId = null, int budget = 1000)
            => JsonDocument.Parse((string)Call("Act", Request(new { observationId, kind, value, choiceId, actionId = actionId ?? Guid.NewGuid().ToString(), timeoutMs = budget, mode = "text" }))).RootElement.Clone();
        private static JsonElement Advance(int steps = 20, int budget = 2000)
            => JsonDocument.Parse((string)Call("Advance", Request(new { maxSteps = steps, timeoutMs = budget, mode = "text" }))).RootElement.Clone();
        private static void Reset(string kind = "IntValue", int buttons = 1)
        {
            Ui(() =>
            {
                Model = new();
                Model.inputReq = new CoreFake.Forms.InputRequest { InputType = kind };
                Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 10, Text = "menu", Buttons = Enumerable.Range(0, buttons).Select(i => new CoreFake.Forms.Button { Input = i + 1 }).ToArray() });
                Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 11, Text = "Loading fake.erb" });
                Window.console = Model;
                Set("_window", Window); Set("_console", Model); Set("_consoleType", Model.GetType());
                Set("_sessionId", "fake-core"); Set("_settings", Request(new { gameRoot = "fake" }));
                Set("_cached", null); Set("_cachedAt", DateTime.MinValue); Set("_failure", null);
                Set("_epochRequest", null); Set("_epochConsole", null); Set("_epochSignature", null);
                Set("_inputEpoch", null); Set("_consumedEpoch", null); Set("_inputSerial", 0L);
                Clear("Recent"); Clear("RecentOrder"); Clear("Actions"); Clear("DeltaHistory"); Clear("DeltaOrder");
            });
        }

        private static void Run()
        {
            Reset();
            var first = Observe();
            Check("real-wait-input-is-actionable", B(first, "canAct") && !B(first, "pending") && B(first, "attached") && S(first, "phase") == "waiting-input");
            var originalId = S(first, "observationId");
            var originalEpoch = S(first, "inputEpoch");
            var page = Snap(new() { ["mode"] = "text", ["limitLines"] = 1, ["fromLine"] = 1, ["noise"] = "raw" });
            Check("fingerprint-independent-of-page-and-filter", originalId == S(page, "observationId") && originalEpoch == S(page, "inputEpoch"));
            Ui(() => { ((CoreFake.Forms.Line)Model.DisplayLineList[0]).Text = "animated menu"; Window.Width += 1; });
            var redraw = Snap();
            Check("redraw-changes-presentation-not-input-epoch", originalId != S(redraw, "observationId") && originalEpoch == S(redraw, "inputEpoch"));
            var choice = (Dictionary<string, object>)((List<object>)first["buttons"])[0];
            var selected = Act(originalId, "choose", choiceId: S(choice, "id"));
            Check("delivered-old-presentation-authorizes-same-live-input", selected.GetProperty("submitted").GetBoolean() && Model.PressCount == 1);
            var stale = Act(originalId);
            Check("old-request-rejected-even-when-history-retained", stale.GetProperty("code").GetString() == "STALE_OBSERVATION" && stale.GetProperty("discriminator").GetString() == "input-epoch-changed" && Model.PressCount == 1);

            Reset();
            first = Observe();
            Ui(() => Model.state = "Running");
            stale = Act(S(first, "observationId"));
            Check("historical-wait-input-cannot-authorize-running-engine", !stale.GetProperty("ok").GetBoolean() && Model.PressCount == 0);
            Reset();
            first = Observe();
            Ui(() => Model.inputReq = new CoreFake.Forms.InputRequest { ID = 1 });
            stale = Act(S(first, "observationId"));
            Check("identical-looking-new-request-invalidates-token", stale.GetProperty("code").GetString() == "STALE_OBSERVATION" && Model.PressCount == 0);
            Reset();
            first = Observe();
            Clear("Recent"); Clear("RecentOrder");
            stale = Act(S(first, "observationId"));
            Check("cache-cannot-authorize-undelivered-observation", stale.GetProperty("discriminator").GetString() == "unknown-delivered-observation" && Model.PressCount == 0);

            Reset(buttons: 150);
            first = Observe();
            Check("button-output-bounded", ((List<object>)first["buttons"]).Count == 128 && Convert.ToInt32(first["omittedButtons"]) == 22);
            Ui(() => ((CoreFake.Forms.Line)Model.DisplayLineList[0]).Buttons[0].Input = 999);
            redraw = Snap();
            Check("omitted-button-affects-full-fingerprint-and-input-token", S(first, "observationId") != S(redraw, "observationId") && S(first, "inputEpoch") != S(redraw, "inputEpoch"));
            Reset();
            Ui(() => Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 12, Text = "retained non-current buttons", Buttons = new[] { new CoreFake.Forms.Button { Generation = 999 } } }));
            first = Observe();
            Check("engine-last-generation-not-buffer-maximum", Convert.ToInt64(first["buttonGeneration"]) == 3 && ((List<object>)first["buttons"]).Count == 1);
            Ui(() => Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 13, Text = "a" + new string('x', 29998) + "b" }));
            first = Snap();
            Ui(() => ((CoreFake.Forms.Line)Model.DisplayLineList[3]).Text = new string('x', 29999) + "z");
            redraw = Snap();
            Check("truncated-tail-affects-full-fingerprint", B(first, "textTruncated") && S(first, "observationId") != S(redraw, "observationId"));

            Reset();
            Ui(() => Model.inputReq = new CoreFake.Forms.IncompleteRequest());
            first = Observe();
            Check("missing-input-contract-fails-closed", !B(first, "canAct") && S(first, "phase") == "failed" && S(first, "readinessReason") == "adapter-contract-missing");
            Reset();
            Ui(() => { ((CoreFake.Forms.InputRequest)Model.inputReq).Timelimit = 2000; Model.IsRunningTimer = true; });
            first = Observe();
            Check("timed-user-value-input-remains-actionable", B(first, "canAct") && !B(first, "canAdvance") && B((Dictionary<string, object>)first["input"], "timedUserInput"));
            Check("timed-user-value-can-submit", Act(S(first, "observationId"), value: "9").GetProperty("submitted").GetBoolean() && Model.LastValue == "9");
            Reset("EnterKey", 0);
            Ui(() => { ((CoreFake.Forms.InputRequest)Model.inputReq).Timelimit = 1000; Model.IsRunningTimer = true; });
            first = Observe(50);
            var advanced = Advance(budget: 200);
            Check("timed-key-animation-not-actionable-or-advanced", !B(first, "canAct") && B(first, "pending") && advanced.GetProperty("steps").GetInt32() == 0 && Model.PressCount == 0);
            Reset("EnterKey", 0);
            Ui(() => ((CoreFake.Forms.InputRequest)Model.inputReq).IsSystemInput = true);
            first = Observe(50);
            advanced = Advance(budget: 200);
            Check("system-wait-not-actionable-or-advanced", !B(first, "canAct") && advanced.GetProperty("steps").GetInt32() == 0 && Model.PressCount == 0);
            Reset();
            Ui(() => ((CoreFake.Forms.InputRequest)Model.inputReq).IsSystemInput = true);
            first = Observe();
            Check("system-value-title-menu-still-allows-explicit-act", B(first, "canAct") && !B(first, "canAdvance") && Act(S(first, "observationId"), value: "1").GetProperty("submitted").GetBoolean() && Model.PressCount == 1);
            Reset("EnterKey", 0);
            Ui(() => Model.DisplayLineList.Clear());
            Check("genuine-empty-key-wait-is-actionable", B(Observe(), "canAct"));

            Reset("EnterKey", 0);
            Ui(() => Model.OnPress = () => Window.BeginInvoke((Action)(() =>
            {
                Model.inputReq = new CoreFake.Forms.InputRequest { ID = Model.PressCount + 1, InputType = Model.PressCount >= 3 ? "IntValue" : "EnterKey" };
            })));
            advanced = Advance();
            Check("advance-yields-ui-between-steps-and-stops-at-decision", advanced.GetProperty("steps").GetInt32() == 3 && Model.PressCount == 3 && advanced.GetProperty("stoppedBecause").GetString() == "input-required");
            Reset("EnterKey", 0);
            Ui(() => Model.OnPress = () => { });
            advanced = Advance();
            Check("ignored-key-stalls-without-repeated-input", advanced.GetProperty("steps").GetInt32() == 1 && advanced.GetProperty("stoppedBecause").GetString() == "stalled" && Model.PressCount == 1);

            Reset();
            first = Observe();
            var blocked = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            Window.BeginInvoke((Action)(() => { blocked.Set(); release.Wait(); }));
            blocked.Wait();
            var watch = Stopwatch.StartNew();
            first = Observe(100);
            var pendingWork = Get("_uiWork");
            Check("observe-deadline-bounded-on-blocked-ui", watch.ElapsedMilliseconds < 500 && B(first, "pending") && !B(first, "canAct"));
            Observe(100);
            Check("blocked-ui-does-not-accumulate-sample-callbacks", pendingWork != null && ReferenceEquals(pendingWork, Get("_uiWork")));
            release.Set(); Ui(() => { });
            Check("cancelled-sample-drains-without-disposed-callback", Get("_uiWork") == null && UiExceptions == 0);

            Reset();
            first = Observe();
            blocked = new(); release = new();
            Window.BeginInvoke((Action)(() => { blocked.Set(); release.Wait(); }));
            blocked.Wait();
            var actionId = Guid.NewGuid().ToString();
            var cancelled = Act(S(first, "observationId"), actionId: actionId, budget: 75);
            release.Set(); Ui(() => { });
            var actions = (Dictionary<string, Dictionary<string, object>>)Get("Actions");
            Check("queued-action-cancelled-never-executes-late", cancelled.GetProperty("code").GetString() == "CANCELLED_BEFORE_EXECUTION" && Model.PressCount == 0 && Convert.ToString(actions[actionId]["state"]) == "cancelled");
            Check("duplicate-cancelled-action-not-requeued", Act(S(first, "observationId"), actionId: actionId).GetProperty("code").GetString() == "DUPLICATE_ACTION" && Model.PressCount == 0);

            Reset();
            first = Observe();
            Ui(() => Model.OnPress = () => { Thread.Sleep(200); Model.inputReq = new CoreFake.Forms.InputRequest { ID = 2 }; });
            actionId = Guid.NewGuid().ToString();
            watch.Restart();
            var uncertain = Act(S(first, "observationId"), actionId: actionId, budget: 50);
            Check("running-action-deadline-is-uncertain-not-retryable", watch.ElapsedMilliseconds < 180 && uncertain.GetProperty("code").GetString() == "OUTCOME_UNCERTAIN");
            Ui(() => { });
            actions = (Dictionary<string, Dictionary<string, object>>)Get("Actions");
            Check("late-running-action-settles-ledger-exactly-once", Model.PressCount == 1 && Convert.ToString(actions[actionId]["state"]) == "completed");
            Check("completed-action-idempotency-retained", Act(S(first, "observationId"), actionId: actionId).GetProperty("code").GetString() == "DUPLICATE_ACTION" && Model.PressCount == 1);

            Reset();
            Ui(() => ((CoreFake.Forms.Line)Model.DisplayLineList[0]).DelayMs = 80);
            watch.Restart();
            first = Observe(30);
            Check("running-sample-returns-pending-within-deadline", watch.ElapsedMilliseconds < 150 && B(first, "pending") && !B(first, "canAct"));
            Ui(() => { });
            Check("late-sample-is-not-delivered-as-input-authority", ((IDictionary)Get("Recent")).Count == 0 && Get("_uiWork") == null && UiExceptions == 0);

            Reset();
            Ui(() => ((CoreFake.Forms.Line)Model.DisplayLineList[0]).Text = "a" + new string('x', 30000));
            first = Snap();
            Ui(() => ((CoreFake.Forms.Line)Model.DisplayLineList[0]).Text = "b" + new string('y', 30000));
            redraw = Snap(new() { ["mode"] = "text", ["limitLines"] = 80, ["since"] = S(first, "observationId") });
            Check("delta-cannot-bypass-character-budget", S((Dictionary<string, object>)redraw["delta"], "kind") == "full" && Convert.ToString(redraw["text"]).Length <= 24000);

            Reset("EnterKey", 0);
            Ui(() => Model.OnPress = () =>
            {
                Model.state = "Running";
                var timer = new System.Windows.Forms.Timer { Interval = 60 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop(); timer.Dispose();
                    Model.state = "WaitInput";
                    Model.inputReq = new CoreFake.Forms.InputRequest { ID = Model.PressCount + 1, InputType = Model.PressCount >= 2 ? "IntValue" : "EnterKey" };
                };
                timer.Start();
            });
            advanced = Advance();
            Check("advance-waits-for-asynchronous-running-transition", advanced.GetProperty("steps").GetInt32() == 2 && advanced.GetProperty("stoppedBecause").GetString() == "input-required");

            Reset();
            first = Observe();
            Ui(() => ((CoreFake.Forms.Line)Model.DisplayLineList[0]).DelayMs = 60);
            actionId = Guid.NewGuid().ToString();
            uncertain = Act(S(first, "observationId"), actionId: actionId, budget: 30);
            Ui(() => { });
            actions = (Dictionary<string, Dictionary<string, object>>)Get("Actions");
            Check("deadline-during-validation-cancels-before-input", uncertain.GetProperty("code").GetString() == "OUTCOME_UNCERTAIN" && Model.PressCount == 0 && Convert.ToString(actions[actionId]["state"]) == "cancelled");

            Reset();
            var status = JsonDocument.Parse((string)Call("Dispatch", Request(new { action = "status" }))).RootElement;
            Check("status-is-cheap-and-does-not-authorize-cache", !status.GetProperty("canAct").GetBoolean() && status.GetProperty("pending").GetBoolean() && status.GetProperty("requiresObservation").GetBoolean());
            Check("all-late-callbacks-safe", UiExceptions == 0);

            // Compact view is a presentation projection of a real duel screen captured
            // from eraOCG2: it must shrink the text, keep what a decision reads, and
            // leave observation identity and button authorization untouched.
            Reset();
            var fixture = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "ocg-duel-screen.txt"));
            Ui(() =>
            {
                Model.DisplayLineList.Clear();
                for (var i = 0; i < fixture.Length; i++)
                    Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 100 + i, Text = fixture[i] });
            });
            var fullScreen = Snap(new() { ["mode"] = "text", ["limitLines"] = 200 });
            var compactScreen = Snap(new() { ["mode"] = "text", ["limitLines"] = 200, ["render"] = "compact" });
            var fullText = Convert.ToString(fullScreen["text"]);
            var compactText = Convert.ToString(compactScreen["text"]);
            Check("compact-keeps-observation-identity", S(fullScreen, "observationId") == S(compactScreen, "observationId") && S(fullScreen, "inputEpoch") == S(compactScreen, "inputEpoch"));
            Check("compact-shrinks-a-real-duel-screen", compactText.Length < fullText.Length * 0.6 && Convert.ToInt32(compactScreen["collapsedLayoutLines"]) >= 20);
            Check("compact-preserves-card-names-and-scores", compactText.Contains("特莱恩") && compactText.Contains("玩家基本分") && compactText.Contains("虫惑之园") && compactText.Contains("MAGIC / TRAPS"));
            Check("compact-folds-repeated-neighbours", compactText.Contains(" ×") && Convert.ToInt32(compactScreen["mergedRepeatedLines"]) > 0);
            Check("full-view-keeps-diagnostics", fullScreen.ContainsKey("window") && fullScreen.ContainsKey("consistency") && fullScreen.ContainsKey("buttonGeneration"));

            Reset();
            Ui(() => { Model.DisplayLineList.Clear(); Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 30, Text = "┌──────────────┐ 攻击", Buttons = new[] { new CoreFake.Forms.Button { Input = 7, Label = "攻击" } } }); });
            var compactChoice = JsonDocument.Parse((string)Call("Dispatch", Request(new { action = "observe", mode = "text", limitLines = 80, render = "compact", timeoutMs = 500 }))).RootElement;
            var slimButton = compactChoice.GetProperty("buttons")[0];
            Check("compact-buttons-drop-diagnostics", slimButton.TryGetProperty("id", out _) && slimButton.TryGetProperty("label", out _) && slimButton.TryGetProperty("integer", out _) && !slimButton.TryGetProperty("generation", out _) && !slimButton.TryGetProperty("lineIndex", out _) && !slimButton.TryGetProperty("valueTooLong", out _));
            Check("compact-response-drops-metadata", !compactChoice.TryGetProperty("window", out _) && !compactChoice.TryGetProperty("consistency", out _) && !compactChoice.TryGetProperty("projectionId", out _) && compactChoice.TryGetProperty("observationId", out _) && compactChoice.TryGetProperty("input", out _));
            Check("compact-button-still-authorizes", Act(compactChoice.GetProperty("observationId").GetString(), "choose", choiceId: slimButton.GetProperty("id").GetString()).GetProperty("submitted").GetBoolean() && Model.PressCount == 1);

            // The envelope matters as much as the text: a full 25-button menu carries
            // generation, value and line index per button plus every diagnostic field.
            Reset();
            Ui(() =>
            {
                Model.DisplayLineList.Clear();
                Model.DisplayLineList.Add(new CoreFake.Forms.Line
                {
                    LineNo = 40,
                    Text = "menu",
                    Buttons = Enumerable.Range(0, 25).Select(i => new CoreFake.Forms.Button { Input = 500 + i, Label = "choice " + i }).ToArray(),
                });
            });
            var fullEnvelope = (string)Call("Dispatch", Request(new { action = "observe", mode = "text", limitLines = 80, render = "full", timeoutMs = 500 }));
            var compactEnvelope = (string)Call("Dispatch", Request(new { action = "observe", mode = "text", limitLines = 80, render = "compact", timeoutMs = 500 }));
            Check("compact-envelope-shrinks", compactEnvelope.Length < fullEnvelope.Length * 0.6);

            // EraFL shapes: a long blank run stays blank, layout tags keep only identity,
            // and a button label loses its padding.
            Reset();
            Ui(() =>
            {
                Model.DisplayLineList.Clear();
                Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 50, Text = "<img src='FACE_4' height='200px' width='200px'><div xpos='768' width='200px' height='200px'>" });
                for (var i = 0; i < 6; i++) Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 51 + i, Text = "                    " });
                Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 60, Text = "  [      帮助]", Buttons = new[] { new CoreFake.Forms.Button { Input = 59, Label = "  [      帮助]" } } });
            });
            var eraflView = JsonDocument.Parse((string)Call("Dispatch", Request(new { action = "observe", mode = "text", limitLines = 80, render = "compact", timeoutMs = 500 }))).RootElement;
            var eraflText = eraflView.GetProperty("text").GetString();
            Check("compact-tags-keep-identity-only", eraflText.Contains("⟨img FACE_4⟩") && eraflText.Contains("⟨div⟩") && !eraflText.Contains("height=") && !eraflText.Contains("</div>"));
            Check("compact-blank-runs-stay-blank", !eraflText.Contains(" ×6") && Convert.ToInt32(eraflView.GetProperty("mergedRepeatedLines").GetInt32()) >= 5);
            Check("compact-labels-drop-padding", eraflView.GetProperty("buttons")[0].GetProperty("label").GetString() == " [ 帮助]");

            // A cumulative buffer must not re-send the whole window after an action: the
            // reply delta-carries only what changed, since the caller already holds it.
            Reset();
            var before = JsonDocument.Parse((string)Call("Dispatch", Request(new { action = "observe", mode = "text", limitLines = 80, render = "compact", timeoutMs = 500 }))).RootElement;
            Ui(() => Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 90, Text = "appended narration line" }));
            var after = JsonDocument.Parse((string)Call("Dispatch", Request(new
            {
                action = "act",
                observationId = before.GetProperty("observationId").GetString(),
                kind = "input",
                value = "1",
                actionId = Guid.NewGuid().ToString(),
                mode = "text",
                render = "compact",
                timeoutMs = 500,
            }))).RootElement.GetProperty("observation");
            var deltaKind = after.GetProperty("delta").GetProperty("kind").GetString();
            var firstAppended = after.GetProperty("delta").GetProperty("lines")[0].GetProperty("text").GetString();
            var textReplacedByDelta = !after.TryGetProperty("text", out var repliedText) || repliedText.ValueKind == JsonValueKind.Null;
            Check("act-replies-with-a-delta-not-the-whole-window", deltaKind == "append" && firstAppended == "appended narration line" && textReplacedByDelta);

            // A long Enter run must not re-send the window it started from either.
            Reset("EnterKey", 0);
            Ui(() =>
            {
                for (var i = 0; i < 20; i++) Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 200 + i, Text = "body " + i });
                Model.OnPress = () =>
                {
                    Model.DisplayLineList.Add(new CoreFake.Forms.Line { LineNo = 300 + Model.PressCount, Text = "step " + Model.PressCount });
                    // A real engine answers an Enter with a new input request; without
                    // that rotation Advance correctly reports "stalled" instead.
                    ((CoreFake.Forms.InputRequest)Model.inputReq).ID++;
                };
            });
            var advanceRun = JsonDocument.Parse((string)Call("Dispatch", Request(new { action = "advance", maxSteps = 3, timeoutMs = 2000, mode = "text", render = "compact" }))).RootElement;
            var advanceView = advanceRun.GetProperty("observation");
            var advanceDelta = advanceView.GetProperty("delta");
            Check("advance-replies-with-a-delta-not-the-whole-window",
                advanceRun.GetProperty("steps").GetInt32() == 3 &&
                advanceDelta.GetProperty("kind").GetString() == "append" &&
                advanceDelta.GetProperty("lines").GetArrayLength() == 3 &&
                advanceView.GetProperty("text").ValueKind == JsonValueKind.Null);
        }
    }
}
