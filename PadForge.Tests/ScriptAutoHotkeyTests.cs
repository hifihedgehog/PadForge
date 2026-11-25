using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The AutoHotkey library's object model against a real AutoHotkey 2.0.32
    /// and a host on a pipe of the test's own (#600 items 21 to 25 and 27):
    /// how fast a hotkey answers, the copy a read comes from, holds, timed
    /// steps, hotkeys in AutoHotkey's own forms, a controller of the script's
    /// own, and a script that starts before PadForge and carries on through a
    /// restart. Every key and mouse move a script makes goes to a recording
    /// seam (<see cref="Seam"/>), never to the desktop.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ScriptAutoHotkeyTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _output;

        public ScriptAutoHotkeyTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

        internal static string Exe => AutomationLibraryTests.Interpreter("autohotkey");

        /// <summary>
        /// The recording seam: AutoHotkey v2 lets a script define a function
        /// of a built-in's name, and the script's calls reach it in place of
        /// the built-in (checked with 2.0.32). Each writes what it would have
        /// sent to the run's record, one line each.
        /// </summary>
        internal const string Seam = """
            ; What the script would send, written to a file instead.
            global __recordFile := FileOpen(EnvGet("PADFORGE_RECORD"), "a", "UTF-8")
            __Record(text) {
                global __recordFile
                __recordFile.Write(text "`n")
                __recordFile.Pos := __recordFile.Pos
            }
            Send(keys) => __Record("send " keys)
            SendInput(keys) => __Record("send " keys)
            SendEvent(keys) => __Record("send " keys)
            SendPlay(keys) => __Record("send " keys)
            SendText(text) => __Record("text " text)
            Click(options*) {
                text := ""
                for o in options
                    text .= " " o
                __Record("click" text)
            }
            MouseClick(options*) {
                text := ""
                for o in options
                    text .= " " o
                __Record("click" text)
            }
            MouseMove(x, y, speed := "", relative := "") => __Record("move " Round(x) " " Round(y) (relative = "" ? "" : " " relative))
            MouseClickDrag(options*) => __Record("drag")
            ControlSend(keys, options*) => __Record("send " keys)
            ControlSendText(text, options*) => __Record("text " text)
            ToolTip(text := "", options*) => __Record("tooltip " text)
            TrayTip(text := "", options*) => __Record("traytip " text)
            MsgBox(text := "", options*) => (__Record("msgbox " text), "OK")
            """;

        /// <summary>What an example may not call: the seam records none of
        /// these, and a hotkey of the keyboard (<c>F1::</c>) would take a key
        /// from the desktop while a test runs.</summary>
        internal static readonly System.Text.RegularExpressions.Regex Unseamed = new(
            @"(?<!\.)\b(ControlClick|DllCall|Run|RunWait|SendMessage|PostMessage|BlockInput|Hotkey)\s*\(|^\s*[^;\s].*::",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>The examples the scripting docs show, from the
        /// repository.</summary>
        internal static string[] Examples(string extension)
            => Directory.GetFiles(RepoPath("PadForge.App", "Resources", "Automation", "Examples"), "*" + extension)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();

        internal static string RepoPath(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }

        internal static string NewFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Scripts." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "Lib"));
            AutomationLibraries.Save(AutomationLibraryKind.AutoHotkey, Path.Combine(dir, "Lib", "PadForge.ahk"));
            return dir;
        }

        /// <summary>A script in its own folder, with the library in the
        /// folder's Lib (so <c>#Include &lt;PadForge&gt;</c> finds it there,
        /// as it finds Documents\AutoHotkey\Lib after an install) and the
        /// seam and a Log function ahead of it.</summary>
        internal static string Script(string dir, string body, string name = "script.ahk")
        {
            string text = "#Requires AutoHotkey v2.0\n#SingleInstance Off\n#NoTrayIcon\n"
                + Seam + "\n"
                + "global __out := FileOpen(EnvGet(\"PADFORGE_OUT\"), \"a\", \"UTF-8\")\n"
                + "Log(text) {\n    global __out\n    __out.Write(text \"`n\")\n    __out.Pos := __out.Pos\n}\n"
                + "OnError((e, *) => (Log(\"error \" e.Message \" line \" e.Line \" \" e.Extra \" \" StrReplace(SubStr(e.Stack, 1, 400), \"`n\", \" / \")), ExitApp(1), 1))\n"
                + "SetTimer(() => ExitApp(), -60000)\n"
                + "#Include <PadForge>\n"
                + body;
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, text, new UTF8Encoding(true));
            return path;
        }

        internal static ScriptRun Start(string dir, string script, string pipe)
        {
            string output = Path.Combine(dir, "out.txt");
            return new ScriptRun(Exe, "/ErrorStdOut \"" + script + "\"", output, pipe,
                new Dictionary<string, string> { ["PADFORGE_OUT"] = output, ["PADFORGE_RECORD"] = Path.Combine(dir, "record.txt") });
        }

        internal static List<string> Record(string dir)
        {
            string path = Path.Combine(dir, "record.txt");
            if (!File.Exists(path)) return new List<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r').TrimStart('\uFEFF')).Where(l => l.Length > 0).ToList();
        }

        /// <summary>Waits for a condition, and says what the script wrote and
        /// sent when it does not come.</summary>
        internal static async Task Eventually(Func<bool> condition, string dir, ScriptRun run, int ms = 5000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > ms)
                {
                    run.Drain();
                    Assert.Fail("the wait ran out. Sent: " + string.Join(" | ", Record(dir)) + Environment.NewLine + "Seen:" + Environment.NewLine + run.Seen);
                }
                await Task.Delay(20);
            }
        }

        internal static void TryDelete(string dir)
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    Directory.Delete(dir, true);
                    return;
                }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }

        private static UserDevice Pad(AutomationPipeRig rig)
        {
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
            pad.ReadInputState.Axis[2] = 0;
            pad.ReadInputState.Axis[5] = 0;
            pad.ReadInputState.Axis[0] = 32768;
            pad.ReadInputState.Axis[1] = 32768;
            pad.ReadInputState.Axis[3] = 32768;
            pad.ReadInputState.Axis[4] = 32768;
            pad.ReadInputState.Povs[0] = -1;
            pad.InputState = pad.ReadInputState;
            return pad;
        }

        // ── Items 21 to 25: the object model ──

        [InterpreterFact("autohotkey")]
        public async Task TheObjectModel_ReadsHoldsTimesAndTakesHotkeys_AndAControllerOfItsOwnHearsEveryFeedback()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                // What the device holds before the script starts: A pressed,
                // the left stick full left, the right trigger full, the D-pad
                // up and right.
                pad.ReadInputState.Buttons[0] = true;
                pad.ReadInputState.Axis[0] = 0;
                pad.ReadInputState.Axis[5] = 65535;
                pad.ReadInputState.Povs[0] = 4500;
                string id = AutomationPipeRig.IdOf(pad);
                string script = Script(dir, """
                    pad := PadForge.Device("wireless")
                    while !pad.Connected && A_Index < 200
                        Sleep(10)
                    Log("found " pad.Id " " pad.Type " " pad.Name " " pad.Connected)
                    Log("read " pad.A " " pad.LeftX " " pad.RightTrigger " " pad.DPad " " pad.DPadUp " " pad.HatRight " " pad.HatDown " " pad.LeftXPlus " " pad.LeftXMinus " " pad.Button[0] " " pad.Axis[0] " " pad["B"])
                    pad.Hotkey("B", Heard)
                    pad.Hotkey("~X", Heard)
                    pad.Hotkey("~Y up", Heard)
                    pad.Hotkey("HatDown", Heard)
                    pad.Hotkey("LeftX > 80%", Heard)
                    pad.Hotkey("LeftX > 80% up", Heard)
                    pad.Hotkey("LeftX < -80%", Heard)
                    pad.Hotkey("RightTriggerPlus > 50%", Heard)
                    Heard(name) => Log("hk " name)
                    for bad in ["LeftX", "Nothing", "A > 50%", "!!", "DPad"]
                        try pad.Hotkey(bad, Heard)
                        catch as e
                            Log("bad " e.Message)
                    pad.OnChange("RightX", (v, n) => Log("change " n " " v))
                    pad.OnConnect((c) => Log("connect " (c ? 1 : 0)))
                    pad.LeftShoulder := true
                    pad.Set("RightY", 40)
                    ctl := PadForge.AddController("ahkpad", "AHK Pad", {Buttons: ["Fire", "Jump"], Axes: [{name: "Steer"}], Gyro: true, Touchpads: [2], Battery: true, Feedback: ["rumble", "trigrumble", "trigfx", "light", "player"]})
                    ctl.OnRumble((l, h) => Log("rumble " l " " h))
                    ctl.OnTriggerRumble((l, r) => Log("trigrumble " l " " r))
                    ctl.OnTriggerEffects((l, r) => Log("trigfx " l " " r))
                    ctl.OnLight((c) => Log("light " c))
                    ctl.OnPlayer((n) => Log("player " n))
                    ctl.Fire := true
                    ctl.Steer := 50
                    ctl.Gyro := [1, 2, 3]
                    ctl.Battery := 80
                    ctl.Touch(0, 0, 0.25, 0.75)
                    Log("controller " ctl.Id " " ctl.Fire " " ctl.Steer)
                    PadForge.SetValue("ahkspeed", 7)
                    for d in PadForge.Devices()
                        if (d.Id = pad.Id)
                            Log("listed " d.Controls.Has("LeftX") " " d.Controls["RightTrigger"].Kind)
                    ; The rig serves no assistant tools: the refusal shows the
                    ; call went out.
                    try Log("tool " PadForge.Tool("status"))
                    catch as e
                        Log("tool " e.Message)
                    Log("ready")
                    F13:: pad.Tap("Start", 60)
                    Persistent
                    """);
                using var run = Start(dir, script, rig.PipeName);
                // The engine runs: the script's whole state arrives.
                using (var loop = new PassLoop(rig, 1000, pad))
                {
                    Assert.Equal("found " + id + " gamepad Wireless Controller 1", run.Expect("found "));
                    Assert.Equal("read 1 -100 100 45 1 1 0 0 100 1 -100 0", run.Expect("read "));
                    Assert.Equal("bad An axis or slider hotkey needs a limit, such as LeftX > 80%: LeftX", run.Expect("bad "));
                    Assert.Equal("bad No such control: Nothing", run.Expect("bad "));
                    Assert.Equal("bad Only an axis or a slider takes a limit: A > 50%", run.Expect("bad "));
                    Assert.Equal("bad Not a control: !!", run.Expect("bad "));
                    Assert.Equal("bad A hat's directions are its hotkeys, such as DPadUp: DPad", run.Expect("bad "));
                    var said = run.Expect("controller ").Split(' ');
                    string controller = said[1];
                    Assert.Equal(new[] { "1", "50" }, said.Skip(2));

                    Assert.Equal("listed 1 axis", run.Expect("listed "));
                    Assert.StartsWith("tool PadForge refused call: ", run.Expect("tool "));
                    run.Expect("ready");

                    // What the host holds: a hold, a set, the controller's
                    // state, the value.
                    await AutomationPipeRig.Until(() => pad.InputState.Buttons[4]);
                    Assert.Equal(PlainUnits.AxisToRaw(40, 32768), pad.InputState.Axis[4]);
                    Assert.Equal(7, ScriptValues.Read("ahkspeed"), 3);
                    var entry = rig.Host.Sessions.Devices.Find(controller);
                    var state = entry.Device.PeekState();
                    Assert.True(state.Buttons[0]);
                    Assert.Equal(PlainUnits.AxisToRaw(50, 32768), state.Axis[0]);
                    Assert.Equal(new[] { 1f, 2f, 3f }, state.Gyro);
                    Assert.Equal(80, state.BatteryPercent);
                    Assert.True(state.Touchpads[0].FingerDown[0]);

                    // A claimed button: the script hears it, the game does
                    // not. "~X" and "~Y up" leave theirs to the game.
                    loop.Raw[0].Buttons[1] = true;
                    loop.Raw[0].Buttons[2] = true;
                    loop.Raw[0].Buttons[3] = true;
                    Assert.Equal("hk B", run.Expect("hk "));
                    Assert.Equal("hk ~X", run.Expect("hk "));
                    await AutomationPipeRig.Until(() => pad.InputState.Buttons[2] && pad.InputState.Buttons[3]);
                    Assert.False(pad.InputState.Buttons[1]);
                    loop.Raw[0].Buttons[3] = false;
                    Assert.Equal("hk ~Y up", run.Expect("hk "));

                    // A hat's direction, claimed: the game keeps Right.
                    loop.Raw[0].Povs[0] = 13500;
                    Assert.Equal("hk HatDown", run.Expect("hk "));
                    await AutomationPipeRig.Until(() => pad.InputState.Povs[0] == 9000);

                    // Lines on both sides of the stick, and its way back.
                    loop.Raw[0].Axis[0] = 65535;
                    Assert.Equal("hk LeftX > 80%", run.Expect("hk "));
                    await AutomationPipeRig.Until(() => pad.InputState.Axis[0] == 32768);
                    loop.Raw[0].Axis[0] = 32768;
                    Assert.Equal("hk LeftX > 80% up", run.Expect("hk "));
                    loop.Raw[0].Axis[0] = 0;
                    Assert.Equal("hk LeftX < -80%", run.Expect("hk "));
                    loop.Raw[0].Axis[5] = 0;
                    loop.Raw[0].Axis[5] = 65535;
                    loop.Raw[0].Axis[3] = 65535;
                    Assert.StartsWith("change RightX 100", run.Expect("change "));

                    // Feedback to the script's own controller.
                    entry.Device.SetRumble(5, 6);
                    Assert.Equal("rumble 5 6", run.Expect("rumble "));
                    entry.Device.SetTriggerRumble(7, 8);
                    Assert.Equal("trigrumble 7 8", run.Expect("trigrumble "));
                    var left = new byte[11]; left[0] = 0x21; left[1] = 3;
                    var right = new byte[11]; right[0] = 0x26;
                    entry.Device.SetTriggerEffects(left, right);
                    Assert.Equal("trigfx 2103000000000000000000 2600000000000000000000", run.Expect("trigfx "));
                    entry.Device.SetLed(255, 0, 16);
                    Assert.Equal("light ff0010", run.Expect("light "));
                    entry.Device.SetPlayerNumber(2);
                    Assert.Equal("player 2", run.Expect("player "));
                }

                // The engine stops passing the device: it is gone.
                rig.Pass();
                Assert.Equal("connect 0", run.Expect("connect "));
            }
            finally
            {
                TryDelete(dir);
            }
        }

        [InterpreterFact("autohotkey")]
        public async Task AScript_StartsBeforePadForge_AndCarriesOnThroughARestart_WithItsHoldsClaimsAndValuesBack()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad := PadForge.Device("wireless")
                    ctl := PadForge.AddController("restart", "Restart Pad", {Buttons: ["Fire"]})
                    pad.Y := true
                    ctl.Fire := true
                    PadForge.SetValue("restartvalue", 3)
                    pad.Hotkey("B", (n) => Log("hk " n))
                    Log("waiting " PadForge.Connected " " PadForge.Why)
                    SetTimer(Watch, 50)
                    Watch() {
                        static was := false
                        if (PadForge.Connected != was) {
                            was := PadForge.Connected
                            Log("connected " (was ? 1 : 0))
                        }
                    }
                    Persistent
                    """);
                using var run = Start(dir, script, pipe);
                Assert.Equal("waiting 0 Nothing serves the pipe " + pipe + ".", run.Expect("waiting "));

                for (int round = 0; round < 2; round++)
                {
                    using var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe);
                    var pad = Pad(rig);
                    using var loop = new PassLoop(rig, 1000, pad);
                    Assert.Equal("connected 1", run.Expect("connected ", 15000));
                    await AutomationPipeRig.Until(() => pad.InputState.Buttons[3], 10000);
                    await AutomationPipeRig.Until(() => ScriptValues.Read("restartvalue") == 3, 5000);
                    var mine = rig.Host.Sessions.Devices.All().Single(e => e.Key == "restart");
                    await AutomationPipeRig.Until(() => mine.Device.PeekState().Buttons[0], 5000);
                    loop.Raw[0].Buttons[1] = true;
                    Assert.Equal("hk B", run.Expect("hk "));
                    await Task.Delay(50);
                    Assert.False(pad.InputState.Buttons[1], "the claim is back");
                    loop.Raw[0].Buttons[1] = false;
                    // PadForge closes.
                    rig.Dispose();
                    Assert.Equal("connected 0", run.Expect("connected "));
                }
            }
            finally
            {
                TryDelete(dir);
            }
        }

        // ── Item 27: a real script's keys and mouse moves, recorded ──

        [InterpreterFact("autohotkey")]
        public async Task ARealScriptsKeysAndMouseMoves_FromControllerInput_GoToTheRecordingSeam()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                string script = Script(dir, """
                    pad := PadForge.Device("gamepad")
                    pad.Hotkey("A", (*) => Send("{Space}"))
                    pad.Hotkey("DPadUp", (*) => Send("w"))
                    pad.Hotkey("LeftX > 80%", (*) => Send("{Right down}"))
                    pad.Hotkey("LeftX > 80% up", (*) => Send("{Right up}"))
                    pad.Hotkey("RightShoulder", (*) => Click())
                    SetTimer(Aim, 10)
                    Aim() {
                        x := pad.RightX
                        if (x != "" && Abs(x) > 10)
                            MouseMove(x / 10, 0, 0, "R")
                    }
                    Log("ready")
                    Persistent
                    """);
                using var loop = new PassLoop(rig, 1000, pad);
                using var run = Start(dir, script, rig.PipeName);
                run.Expect("ready");
                await Task.Delay(100);
                loop.Raw[0].Buttons[0] = true;
                loop.Raw[0].Povs[0] = 0;
                loop.Raw[0].Axis[0] = 65535;
                loop.Raw[0].Buttons[5] = true;
                loop.Raw[0].Axis[3] = 65535;
                await Eventually(() => Record(dir).Any(l => l.StartsWith("move ", StringComparison.Ordinal)), dir, run);
                loop.Raw[0].Axis[0] = 32768;
                loop.Raw[0].Axis[3] = 32768;
                await Eventually(() => Record(dir).Contains("send {Right up}"), dir, run);
                var record = Record(dir);
                Assert.Contains("send {Space}", record);
                Assert.Contains("send w", record);
                Assert.Contains("send {Right down}", record);
                Assert.Contains("click", record);
                Assert.Contains("move 10 0 R", record);
                Assert.DoesNotContain("error", run.Seen);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        // ── Item 27: every example in the docs runs ──

        [InterpreterFact("autohotkey")]
        public async Task EveryAutoHotkeyExample_RunsAgainstAHost_AndCallsNothingTheSeamDoesNotRecord()
        {
            var examples = Examples(".ahk").ToList();
            Assert.True(examples.Count >= 6, "the examples are in Resources/Automation/Examples");
            // The library's own example, from its head, runs too.
            string head = Encoding.UTF8.GetString(AutomationLibraries.Read(AutomationLibraryKind.AutoHotkey));
            var lines = head.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int from = lines.IndexOf("; Example:") + 2;
            var own = lines.Skip(from).TakeWhile(l => l.StartsWith(";   ", StringComparison.Ordinal)).Select(l => l.Substring(4));
            string ownFile = Path.Combine(Path.GetTempPath(), "PadForge.Tests.HeadExample." + Guid.NewGuid().ToString("N") + ".ahk");
            File.WriteAllText(ownFile, string.Join("\n", own) + "\n");
            examples.Add(ownFile);
            foreach (string example in examples)
            {
                string text = File.ReadAllText(example);
                Assert.DoesNotMatch(Unseamed, text.Replace("PadForge.Tool(", string.Empty));
                Assert.Contains("#Include <PadForge>", text);
                string dir = NewFolder();
                try
                {
                    using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                    var pad = Pad(rig);
                    File.Copy(example, Path.Combine(dir, Path.GetFileName(example)));
                    string script = Path.Combine(dir, "run.ahk");
                    File.WriteAllText(script, "#Requires AutoHotkey v2.0\n#SingleInstance Off\n#NoTrayIcon\n" + Seam
                        + "\nglobal __out := FileOpen(EnvGet(\"PADFORGE_OUT\"), \"a\", \"UTF-8\")\n"
                        + "OnError((e, *) => (__out.Write(\"error \" e.Message \" line \" e.Line \"`n\"), __out.Pos := __out.Pos, ExitApp(1), 1))\n"
                        + "SetTimer(() => (__out.Write(\"alive`n\"), __out.Pos := __out.Pos), -1500)\n"
                        + "#Include " + Path.GetFileName(example) + "\n", new UTF8Encoding(true));
                    using var loop = new PassLoop(rig, 500, pad);
                    using var run = Start(dir, script, rig.PipeName);
                    await Eventually(() => rig.Host.Sessions.Count > 0, dir, run, 10000);
                    // A while with the pad moving under it: every hotkey and
                    // reader of the example runs.
                    loop.Raw[0].Buttons[0] = true;
                    loop.Raw[0].Buttons[2] = true;
                    loop.Raw[0].Buttons[5] = true;
                    loop.Raw[0].Axis[0] = 65535;
                    loop.Raw[0].Axis[3] = 65535;
                    loop.Raw[0].Axis[2] = 65535;
                    loop.Raw[0].Axis[5] = 65535;
                    Assert.Equal("alive", run.Expect("alive", 10000));
                    Assert.DoesNotContain("error ", run.Seen);
                }
                finally
                {
                    TryDelete(dir);
                }
            }
        }

        // ── Item 27: how fast a real script answers ──

        [InterpreterFact("autohotkey")]
        public async Task ARealScript_AnswersAButton_WithinAMedianOf3Passes_At1000PassesASecond()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                string id = AutomationPipeRig.IdOf(pad);
                string script = Script(dir, $$"""
                    pad := PadForge.Device("{{id}}")
                    pad.Hotkey("A", (*) => pad.B := true)
                    pad.Hotkey("A up", (*) => pad.B := "")
                    Log("ready")
                    Persistent
                    """);
                using var loop = new PassLoop(rig, 1000, pad);
                using var run = Start(dir, script, rig.PipeName);
                run.Expect("ready");

                // 40 presses and 40 releases: the passes from the pass that
                // reads A's change to the pass whose mappings read B's.
                var answers = new List<long>();
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                long changedAt = 0, waitUntil = loop.Passes + 50;
                int phase = 0, changes = 0;
                loop.Hook = pass =>
                {
                    if (pass < waitUntil || done.Task.IsCompleted) return;
                    bool b = pad.InputState.Buttons[1];
                    if (phase == 0)
                    {
                        loop.Raw[0].Buttons[0] = true;
                        changedAt = pass;
                        phase = 1;
                    }
                    else if (phase == 1 && b)
                    {
                        answers.Add(pass - 1 - changedAt);
                        loop.Raw[0].Buttons[0] = false;
                        changedAt = pass;
                        phase = 2;
                    }
                    else if (phase == 2 && !b)
                    {
                        answers.Add(pass - 1 - changedAt);
                        phase = 0;
                        waitUntil = pass + 20;
                        if (++changes == 40) done.TrySetResult(true);
                    }
                    if (pass - changedAt > 2000) done.TrySetException(new TimeoutException("no answer after 2000 passes, phase " + phase));
                };
                await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Equal(80, answers.Count);
                answers.Sort();
                double median = (answers[39] + answers[40]) / 2.0;
                _output.WriteLine("median " + median + " passes, " + string.Join(",", answers));
                Assert.True(median <= 3, "median " + median + " passes: " + string.Join(",", answers));
                // The claim held: the game never read A pressed.
                Assert.DoesNotContain("error", run.Seen);
            }
            finally
            {
                TryDelete(dir);
            }
        }
    }
}
