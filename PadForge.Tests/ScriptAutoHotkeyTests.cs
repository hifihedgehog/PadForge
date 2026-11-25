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

        /// <summary>A device from <c>PadForge.Devices()</c> carries its
        /// controls and whether PadForge has it, as one from
        /// <c>PadForge.Device()</c> does (#600 item 21): the script lists a
        /// pad PadForge reads and one that is not connected, and follows
        /// neither.</summary>
        [InterpreterFact("autohotkey")]
        public void ADeviceFromDevices_CarriesItsControlsAndWhetherItIsConnected()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                Pad(rig);
                rig.AddDevice(InputDeviceType.Gamepad, "Spare Pad", online: false);
                string script = Script(dir, """
                    for d in PadForge.Devices() {
                        c := d.Controls
                        Log("listed " d.Name " " (d.Connected ? 1 : 0) " " (c.Count > 0 ? 1 : 0)
                            " " (c.Has("LeftX") ? c["LeftX"].Kind : "none")
                            " " (c.Has("RightTrigger") ? c["RightTrigger"].Kind : "none")
                            " " (c.Has("DPad") ? c["DPad"].Kind : "none")
                            " " (c.Has("B") ? c["B"].Index : "none"))
                    }
                    Log("done")
                    ExitApp
                    """);
                using var run = Start(dir, script, rig.PipeName);
                Assert.Equal("listed Wireless Controller 1 1 axis axis hat 1", run.Expect("listed "));
                Assert.Equal("listed Spare Pad 0 1 axis axis hat 1", run.Expect("listed "));
                run.Expect("done");
                Assert.DoesNotContain("error", run.Seen);
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

        /// <summary>
        /// Before its device is found, one rule in every library (#600
        /// items 21, 22 and 24): a hold, a Hold of several and a Set wait
        /// under the control's name and apply when the device is found. A
        /// Tap or a Sequence is refused with the not-connected error:
        /// PadForge's own while PadForge does not run, and a read's of an
        /// absent device while PadForge runs without the device. The
        /// script's own controller is refused the same before PadForge
        /// takes its declaration.
        /// </summary>
        [InterpreterFact("autohotkey")]
        public async Task BeforeItsDeviceIsFound_HoldsAndSetsWait_AndTapsAndSequencesAreRefused()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad := PadForge.Device("wireless")
                    ctl := PadForge.AddController("early", "Early Pad", {Buttons: ["Fire"]})
                    Attempt(what, fn) {
                        try {
                            fn()
                            Log(what " ok")
                        } catch as e {
                            Log(what " " e.Message)
                        }
                    }
                    Attempt("hold", () => pad.Hold("X", true, "LeftX", 50))
                    Attempt("set", () => pad.Set("B", true))
                    ; A Set given back, and a Set a hold takes the place of.
                    pad.Set("LeftShoulder", true)
                    pad.Release("LeftShoulder")
                    pad.Set("RightShoulder", true)
                    pad.RightShoulder := false
                    Attempt("tap", () => pad.Tap("A", 50))
                    Attempt("sequence", () => pad.Sequence("A", 50, "B", 50))
                    Attempt("ctl tap", () => ctl.Tap("Fire", 50))
                    Attempt("ctl sequence", () => ctl.Sequence("Fire", 50))
                    Log("waiting")
                    SetTimer(Watch, 20)
                    Watch() {
                        if !PadForge.Connected
                            return
                        SetTimer(Watch, 0)
                        ; PadForge runs, and no device it has is the pad.
                        Attempt("absent tap", () => pad.Tap("A", 50))
                        Attempt("absent sequence", () => pad.Sequence("A", 50))
                        Attempt("absent set", () => pad.Set("Y", true))
                        Log("absent done")
                    }
                    Persistent
                    """);
                using var run = Start(dir, script, pipe);
                string closed = "PadForge is not connected: Nothing serves the pipe " + pipe + ".";
                Assert.Equal("hold ok", run.Expect("hold "));
                Assert.Equal("set ok", run.Expect("set "));
                Assert.Equal("tap " + closed, run.Expect("tap "));
                Assert.Equal("sequence " + closed, run.Expect("sequence "));
                Assert.Equal("ctl tap " + closed, run.Expect("ctl tap "));
                Assert.Equal("ctl sequence " + closed, run.Expect("ctl sequence "));
                run.Expect("waiting");

                // PadForge runs, without the pad.
                using (new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
                {
                    Assert.Equal("absent tap PadForge refused tap: the device is not connected (offline).", run.Expect("absent tap ", 15000));
                    Assert.Equal("absent sequence PadForge refused sequence: the device is not connected (offline).", run.Expect("absent sequence "));
                    Assert.Equal("absent set ok", run.Expect("absent set "));
                    run.Expect("absent done");
                }

                await AfterTheDeviceIsFound(pipe, dir, run, Pad);
                Assert.DoesNotContain("error", run.Seen);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>
        /// A Tap or a Sequence on a device PadForge has but that is not
        /// connected is refused by PadForge as it handles the line, in a
        /// read's words, and the library raises the refusal (#600 item 21).
        /// The library does not judge the connection itself. A hold made
        /// meanwhile waits for the device.
        /// </summary>
        [InterpreterFact("autohotkey")]
        public async Task ATapOnADeviceThatIsNotConnected_IsRefusedByPadForge()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                string script = Script(dir, """
                    pad := PadForge.Device("wireless")
                    Attempt(what, fn) {
                        try {
                            fn()
                            Log(what " ok")
                        } catch as e {
                            Log(what " " e.Message)
                        }
                    }
                    SetTimer(Watch, 20)
                    Watch() {
                        static seen := 0
                        if (seen = 0 && pad.Connected) {
                            seen := 1
                            Log("seen online")
                        } else if (seen = 1 && !pad.Connected) {
                            seen := 2
                            ; PadForge has the pad, and it is not connected.
                            Attempt("offline tap", () => pad.Tap("A", 300))
                            Attempt("offline sequence", () => pad.Sequence("A", 50, "B", 50))
                            Attempt("offline hold", () => pad.X := true)
                            Log("offline done")
                        } else if (seen = 2 && pad.Connected) {
                            seen := 3
                            Attempt("online tap", () => pad.Tap("A", 300))
                        }
                    }
                    Persistent
                    """);
                using var run = Start(dir, script, rig.PipeName);
                await TapsWhileTheDeviceIsNotConnected(pad, run, dir);
                Assert.DoesNotContain("error", run.Seen);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>
        /// While PadForge restarts and the pipe is down, a found device's
        /// Set waits as a hold does and is sent once when the device is
        /// found again, and a Tap or a Sequence, on the device or on the
        /// script's own controller, throws PadForge's not-connected error
        /// (#600 items 22 and 24).
        /// </summary>
        [InterpreterFact("autohotkey")]
        public async Task WhilePadForgeRestarts_ASetWaits_AndATapOrASequenceIsRefused()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad := PadForge.Device("wireless")
                    ctl := PadForge.AddController("gap", "Gap Pad", {Buttons: ["Fire"]})
                    Attempt(what, fn) {
                        try {
                            fn()
                            Log(what " ok")
                        } catch as e {
                            Log(what " " e.Message)
                        }
                    }
                    SetTimer(Watch, 20)
                    Watch() {
                        static seen := 0
                        if (seen = 0 && pad.Connected) {
                            seen := 1
                            pad.X := true
                            Log("seen online")
                        } else if (seen = 1 && !PadForge.Connected) {
                            seen := 2
                            ; PadForge restarts: the pipe is down.
                            Attempt("gap set", () => pad.Set("B", true))
                            Attempt("gap tap", () => pad.Tap("A", 50))
                            Attempt("gap sequence", () => pad.Sequence("A", 50))
                            Attempt("gap ctl tap", () => ctl.Tap("Fire", 50))
                            Attempt("gap ctl sequence", () => ctl.Sequence("Fire", 50))
                            Log("gap done")
                        }
                    }
                    Persistent
                    """);
                using var run = Start(dir, script, pipe);
                await WhilePadForgeRestarts(pipe, dir, run, Pad);
                Assert.DoesNotContain("error", run.Seen);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>
        /// A device from Devices() is the same object as one from Device()
        /// (#600 item 21): its connection stays current while the script
        /// only lists it, and on its first hold, Set or Tap it follows
        /// itself, so the hold, the Set and the Tap reach the pad, its
        /// controls are read, and its connection stays current after.
        /// </summary>
        [InterpreterFact("autohotkey")]
        public async Task ADeviceFromDevices_FollowsItselfOnItsFirstHold_AndStaysCurrent()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                string script = Script(dir, """
                    for d in PadForge.Devices()
                        if InStr(d.Name, "Wireless")
                            listed := d
                    Log("listed " (listed.Connected ? 1 : 0))
                    SetTimer(Watch, 20)
                    Watch() {
                        static seen := 0
                        if (seen = 0 && !listed.Connected) {
                            seen := 1
                            Log("listed offline")
                        } else if (seen = 1 && listed.Connected) {
                            seen := 2
                            Log("listed online")
                            listed.X := true
                            listed.Set("B", true)
                            listed.Tap("A", 300)
                            Log("same " (PadForge.Device(listed.Id) == listed ? 1 : 0) " " (listed.Controls.Count > 0 ? 1 : 0))
                        } else if (seen = 2 && !listed.Connected) {
                            seen := 3
                            Log("followed offline")
                        }
                    }
                    Persistent
                    """);
                using var run = Start(dir, script, rig.PipeName);
                await ListedThenFollowed(pad, run, dir);
                Assert.DoesNotContain("error", run.Seen);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>The C# side of the three languages' tests of a device
        /// from Devices(). The pad goes and comes back while the script only
        /// lists it, and the script sees both. Then the script holds X, sets
        /// B and taps A on it: all three reach the pad, Device() of its ID
        /// gives the same object, and its controls are read. The pad goes
        /// again, and the script, which follows it now, sees it. The pad goes
        /// as soon as the script has listed it, which may be before
        /// PadForge's first pass after the script came to follow devices:
        /// that pass tells it gone (AutomationWatch.Baseline).</summary>
        internal static async Task ListedThenFollowed(UserDevice pad, ScriptRun run, string dir)
        {
            Assert.Equal("listed 1", run.Expect("listed ", 15000));
            pad.IsOnline = false;
            run.Expect("listed offline", 10000);
            pad.IsOnline = true;
            run.Expect("listed online", 10000);
            await Eventually(() => pad.InputState.Buttons[2] && pad.InputState.Buttons[1], dir, run);
            await Eventually(() => pad.InputState.Buttons[0], dir, run);
            await Eventually(() => !pad.InputState.Buttons[0], dir, run);
            Assert.Equal("same 1 1", run.Expect("same "));
            pad.IsOnline = false;
            run.Expect("followed offline", 10000);
        }

        /// <summary>
        /// A device from Devices() follows itself on its first use of any
        /// kind (#600 item 21): a read, a hotkey, a change callback that
        /// streams an axis, as well as a hold, Set, Tap or Sequence. Three
        /// listed pads, each first used one way: the read pad's A reads
        /// live, the hotkey pad's B fires its hotkey, and the change pad's
        /// LeftX calls its callback.
        /// </summary>
        [InterpreterFact("autohotkey")]
        public async Task ADeviceFromDevices_FollowsItselfOnARead_AHotkeyOrAChangeCallback()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pads = ThreePads(rig);
                using var loop = new PassLoop(rig, 1000, pads);
                string script = Script(dir, """
                    missing := PadForge.Device("no such pad")
                    Log("missing " (missing.A = "" ? "nothing" : "something"))
                    for d in PadForge.Devices() {
                        if (d.Name = "Read Pad")
                            readPad := d
                        else if (d.Name = "Hotkey Pad")
                            hotkeyPad := d
                        else if (d.Name = "Change Pad")
                            changePad := d
                    }
                    hotkeyPad.Hotkey("B", (*) => Log("hk B"))
                    changePad.OnChange("LeftX", (value, *) => Log("change LeftX " value))
                    Log("ready")
                    SetTimer(Watch, 10)
                    Watch() {
                        global readPad
                        if (readPad.A = true) {
                            SetTimer(Watch, 0)
                            Log("read A " (readPad.Connected ? 1 : 0))
                        }
                    }
                    Persistent
                    """);
                using var run = Start(dir, script, rig.PipeName);
                await FirstUsesOfThreeListedPads(loop, run, dir);
                Assert.DoesNotContain("error", run.Seen);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>Three gamepads at rest, named for how the script first
        /// uses each: Read Pad, Hotkey Pad and Change Pad.</summary>
        internal static UserDevice[] ThreePads(AutomationPipeRig rig)
        {
            var pads = new[] { "Read Pad", "Hotkey Pad", "Change Pad" }.Select(name =>
            {
                var pad = rig.AddDevice(InputDeviceType.Gamepad, name);
                pad.ReadInputState.Axis[2] = 0;
                pad.ReadInputState.Axis[5] = 0;
                pad.ReadInputState.Axis[0] = 32768;
                pad.ReadInputState.Axis[1] = 32768;
                pad.ReadInputState.Axis[3] = 32768;
                pad.ReadInputState.Axis[4] = 32768;
                pad.ReadInputState.Povs[0] = -1;
                pad.InputState = pad.ReadInputState;
                return pad;
            }).ToArray();
            return pads;
        }

        /// <summary>The C# side of the three languages' tests of a listed
        /// device's first use. A device no pad matches reads nothing, as one
        /// that is not connected does. A on the read pad reads pressed in
        /// the script's read loop, and the pad reads connected then. B on the
        /// hotkey pad fires its hotkey. LeftX on the change pad, pushed full
        /// right, calls its change callback with 100.</summary>
        internal static async Task FirstUsesOfThreeListedPads(PassLoop loop, ScriptRun run, string dir)
        {
            Assert.Equal("missing nothing", run.Expect("missing ", 15000));
            run.Expect("ready", 15000);
            await Task.Delay(100);
            loop.Raw[0].Buttons[0] = true;
            Assert.Equal("read A 1", run.Expect("read A ", 10000));
            loop.Raw[1].Buttons[1] = true;
            Assert.Equal("hk B", run.Expect("hk ", 10000));
            loop.Raw[2].Axis[0] = 65535;
            Assert.Equal("change LeftX 100", run.Expect("change LeftX 100", 10000));
        }

        /// <summary>
        /// A device from Devices() the script never uses keeps its
        /// connection true through a restart (#600 items 21 and 24): not
        /// connected while PadForge is gone, connected again when PadForge
        /// comes back with the pad.
        /// </summary>
        [InterpreterFact("autohotkey")]
        public async Task AnUnusedListedDevice_IsNotConnectedWhilePadForgeIsGone_AndIsAgainAfter()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    for d in PadForge.Devices()
                        if InStr(d.Name, "Wireless")
                            listed := d
                    Log("listed " (listed.Connected ? 1 : 0))
                    SetTimer(Watch, 20)
                    Watch() {
                        static seen := 0, since := 0
                        global listed
                        if (seen = 0 && !PadForge.Connected) {
                            seen := 1
                            Log("gap " (listed.Connected ? 1 : 0))
                        } else if (seen = 1 && PadForge.Connected) {
                            seen := 2
                            since := A_TickCount
                        } else if (seen = 2 && (listed.Connected || A_TickCount - since > 5000)) {
                            seen := 3
                            Log("back " (listed.Connected ? 1 : 0))
                        }
                    }
                    Persistent
                    """);
                await UnusedListedThroughARestart(pipe, dir, () => Start(dir, script, pipe), Pad);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>The C# side of the three languages' tests of a listed
        /// device the script never uses, through a restart. PadForge runs
        /// with the pad, and the script lists it connected. PadForge goes:
        /// the device is not connected. PadForge comes back with a pad of
        /// the same script ID: the device is connected again.</summary>
        internal static async Task UnusedListedThroughARestart(string pipe, string dir, Func<ScriptRun> start, Func<AutomationPipeRig, UserDevice> padOf)
        {
            Guid guid;
            ScriptRun run;
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                var pad = padOf(rig);
                guid = pad.InstanceGuid;
                using var loop = new PassLoop(rig, 1000, pad);
                run = start();
                Assert.Equal("listed 1", run.Expect("listed ", 15000));
            }
            using (run)
            {
                Assert.Equal("gap 0", run.Expect("gap ", 15000));
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe);
                var pad = padOf(rig);
                pad.InstanceGuid = guid;
                using var loop = new PassLoop(rig, 1000, pad);
                Assert.Equal("back 1", run.Expect("back ", 20000));
                await Task.CompletedTask;
                Assert.DoesNotContain("error ", run.Seen);
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
        }

        /// <summary>
        /// A device the script found, missing from PadForge's list when
        /// PadForge comes back, is found when it connects after (#600 items
        /// 22 and 24), and what the script held on it is put back.
        /// </summary>
        [InterpreterFact("autohotkey")]
        public async Task AFoundDevice_MissingWhenPadForgeComesBack_IsFoundWhenItConnects()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad := PadForge.Device("wireless")
                    SetTimer(Watch, 20)
                    Watch() {
                        static seen := 0
                        if (seen = 0 && pad.Connected) {
                            seen := 1
                            pad.X := true
                            Log("found")
                        } else if (seen = 1 && !PadForge.Connected) {
                            seen := 2
                            Log("gone")
                        } else if (seen = 2 && PadForge.Connected) {
                            seen := 3
                            Log("back")
                        }
                    }
                    Persistent
                    """);
                await FoundAgainWhenItConnects(pipe, dir, () => Start(dir, script, pipe), Pad);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>The C# side of the three languages' tests of a device
        /// missing when PadForge comes back. PadForge runs with the pad, and
        /// the script holds X on it. PadForge goes, and comes back without
        /// the pad: the script's restore finds none. Then the pad connects,
        /// and X is held on it. The passes run from before the script comes
        /// back, so PadForge's first look at the online devices has none,
        /// and the pad is told as one that connects.</summary>
        internal static async Task FoundAgainWhenItConnects(string pipe, string dir, Func<ScriptRun> start, Func<AutomationPipeRig, UserDevice> padOf)
        {
            ScriptRun run;
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                var pad = padOf(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                run = start();
                run.Expect("found", 15000);
                await Eventually(() => pad.InputState.Buttons[2], dir, run);
            }
            using (run)
            {
                run.Expect("gone", 15000);
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe);
                using (var empty = new PassLoop(rig, 1000))
                {
                    run.Expect("back", 15000);
                    await Task.Delay(1000);
                    long passes = empty.Passes;
                    await Eventually(() => empty.Passes > passes + 100, dir, run);
                }
                var pad = padOf(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                await Eventually(() => pad.InputState.Buttons[2], dir, run, 15000);
                Assert.DoesNotContain("error ", run.Seen);
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
        }

        /// <summary>
        /// A device from Devices() that the script has not used yet, while
        /// PadForge restarts, does what a found device does (#600 items 21
        /// and 24): its controls are not read yet, so Hold and Set wait under
        /// the names given, and Tap and Sequence throw PadForge's
        /// not-connected error. PadForge comes back with the same pad: it is
        /// found again, and X and LeftX are held and B set.
        /// </summary>
        [InterpreterFact("autohotkey")]
        public async Task ADeviceFromDevices_WhilePadForgeRestarts_WaitsOrRefusesAsAFoundOneDoes()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    for d in PadForge.Devices()
                        if InStr(d.Name, "Wireless")
                            listed := d
                    Log("listed " listed.Id)
                    Attempt(what, fn) {
                        try {
                            fn()
                            Log(what " ok")
                        } catch as e {
                            Log(what " " e.Message)
                        }
                    }
                    SetTimer(Watch, 20)
                    Watch() {
                        global listed
                        if PadForge.Connected
                            return
                        SetTimer(Watch, 0)
                        ; PadForge restarts, and the script has not used the
                        ; listed device yet.
                        Attempt("gap hold", () => listed.Hold("X", true, "LeftX", 50))
                        Attempt("gap set", () => listed.Set("B", true))
                        Attempt("gap tap", () => listed.Tap("A", 50))
                        Attempt("gap sequence", () => listed.Sequence("A", 50))
                        Log("gap done")
                    }
                    Persistent
                    """);
                await ListedWhilePadForgeRestarts(pipe, dir, () => Start(dir, script, pipe), Pad);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>The C# side of the three languages' tests of a listed
        /// device while PadForge restarts. PadForge runs with the pad and
        /// the script lists it. PadForge goes: Hold and Set wait, Tap and
        /// Sequence are refused. PadForge runs again with a pad of the same
        /// script ID: the listed device is found again, X and LeftX are
        /// held and B set.</summary>
        internal static async Task ListedWhilePadForgeRestarts(string pipe, string dir, Func<ScriptRun> start, Func<AutomationPipeRig, UserDevice> padOf)
        {
            Guid guid;
            ScriptRun run;
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                var pad = padOf(rig);
                guid = pad.InstanceGuid;
                using var loop = new PassLoop(rig, 1000, pad);
                run = start();
                Assert.Equal("listed " + AutomationPipeRig.IdOf(pad), run.Expect("listed ", 15000));
            }
            using (run)
            {
                Assert.Equal("gap hold ok", run.Expect("gap hold ", 15000));
                Assert.Equal("gap set ok", run.Expect("gap set "));
                Assert.StartsWith("gap tap PadForge is not connected: ", run.Expect("gap tap "));
                Assert.StartsWith("gap sequence PadForge is not connected: ", run.Expect("gap sequence "));
                run.Expect("gap done");
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe);
                var pad = padOf(rig);
                pad.InstanceGuid = guid;
                using var loop = new PassLoop(rig, 1000, pad);
                await Eventually(() => pad.InputState.Buttons[2] && pad.InputState.Axis[0] == PlainUnits.AxisToRaw(50, 32768)
                    && pad.InputState.Buttons[1], dir, run, 15000);
                Assert.DoesNotContain("error ", run.Seen);
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
        }

        /// <summary>The C# side of the three languages' tests of a restart.
        /// PadForge runs with the pad, and the script holds X once it sees
        /// the pad. PadForge goes: the script's Set of B waits, and its Taps
        /// and Sequences are refused as PadForge not connected. PadForge
        /// runs again: X is held and B set on the pad found again. PadForge
        /// restarts once more: X is held and B, sent once, is not.</summary>
        internal static async Task WhilePadForgeRestarts(string pipe, string dir, ScriptRun run, Func<AutomationPipeRig, UserDevice> padOf)
        {
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                var pad = padOf(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                run.Expect("seen online", 15000);
                await Eventually(() => pad.InputState.Buttons[2], dir, run);
            }
            Assert.Equal("gap set ok", run.Expect("gap set ", 15000));
            Assert.StartsWith("gap tap PadForge is not connected: ", run.Expect("gap tap "));
            Assert.StartsWith("gap sequence PadForge is not connected: ", run.Expect("gap sequence "));
            Assert.StartsWith("gap ctl tap PadForge is not connected: ", run.Expect("gap ctl tap "));
            Assert.StartsWith("gap ctl sequence PadForge is not connected: ", run.Expect("gap ctl sequence "));
            run.Expect("gap done");
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                var pad = padOf(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                await Eventually(() => pad.InputState.Buttons[2] && pad.InputState.Buttons[1], dir, run, 15000);
            }
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                var pad = padOf(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                await Eventually(() => pad.InputState.Buttons[2], dir, run, 15000);
                await Task.Delay(200);
                Assert.False(pad.InputState.Buttons[1], "a Set is sent once, not again after a second restart");
            }
        }

        /// <summary>The C# side of the three languages' tests of a tap on a
        /// device PadForge has but that is not connected. The script says
        /// when it has seen the pad connected, the pad goes, and the
        /// script's Tap and Sequence are refused in a read's words while
        /// its hold of X waits and is held. The pad comes back: a Tap is
        /// taken, presses A and lets it go.</summary>
        internal static async Task TapsWhileTheDeviceIsNotConnected(UserDevice pad, ScriptRun run, string dir)
        {
            run.Expect("seen online", 15000);
            pad.IsOnline = false;
            Assert.Equal("offline tap PadForge refused tap: the device is not connected (offline).", run.Expect("offline tap "));
            Assert.Equal("offline sequence PadForge refused sequence: the device is not connected (offline).", run.Expect("offline sequence "));
            Assert.Equal("offline hold ok", run.Expect("offline hold "));
            run.Expect("offline done");
            await Eventually(() => pad.InputState.Buttons[2], dir, run);
            Assert.False(pad.InputState.Buttons[0], "a refused tap holds nothing");
            pad.IsOnline = true;
            Assert.Equal("online tap ok", run.Expect("online tap ", 15000));
            await Eventually(() => pad.InputState.Buttons[0], dir, run);
            await Eventually(() => !pad.InputState.Buttons[0], dir, run);
        }

        /// <summary>The rest of the three languages' tests of a device not
        /// found yet, once the script has PadForge without the pad behind
        /// it. PadForge runs again with the pad: the holds made before are
        /// put on it, X and LeftX held, and B and Y set. The waiting Sets go
        /// in one line, so the LeftShoulder Set given back before and the
        /// RightShoulder Set a hold took the place of stay out of it, while
        /// the pad's own RightShoulder is down. PadForge restarts once more:
        /// the holds come back, and a Set, put once, does not.</summary>
        internal static async Task AfterTheDeviceIsFound(string pipe, string dir, ScriptRun run, Func<AutomationPipeRig, UserDevice> padOf)
        {
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                var pad = padOf(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                loop.Raw[0].Buttons[5] = true;
                await Eventually(() => pad.InputState.Buttons[2] && pad.InputState.Axis[0] == PlainUnits.AxisToRaw(50, 32768), dir, run, 15000);
                await Eventually(() => pad.InputState.Buttons[1] && pad.InputState.Buttons[3], dir, run);
                Assert.False(pad.InputState.Buttons[4], "a Set given back before the device was found is not put");
                Assert.False(pad.InputState.Buttons[5], "the hold that came after a Set stands over it");
            }
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                var pad = padOf(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                await Eventually(() => pad.InputState.Buttons[2], dir, run, 15000);
                await Task.Delay(200);
                Assert.False(pad.InputState.Buttons[1] || pad.InputState.Buttons[3], "a Set is put once, not again after a restart");
            }
        }

        /// <summary>
        /// A hat's direction is set on its own (#600 items 3 and 21):
        /// DPadUp and DPadRight held before PadForge runs read Up and Right
        /// together once it does, and again after a restart. Given back,
        /// Up leaves Right. The whole hat held is what Right goes over,
        /// and Release gives everything back. On the script's own
        /// controller a direction goes into the hat it set.
        /// </summary>
        [InterpreterFact("autohotkey")]
        public async Task AHatsDirections_AreSetOnTheirOwn_AndComeBackAfterARestart()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad := PadForge.Device("wireless")
                    ctl := PadForge.AddController("dirs", "Directions Pad", {Buttons: ["Fire"], Hats: ["View"]})
                    ; What is not held is given back with no error, before
                    ; the device is found and after.
                    pad.DPadLeft := ""
                    pad.Release("RightTrigger")
                    pad.DPadUp := true
                    pad.DPadRight := true
                    ctl.ViewUp := true
                    ctl.ViewRight := true
                    ctl.ViewUp := false
                    Log("ctl " ctl.ViewUp " " ctl.ViewRight " " ctl.View)
                    pad.Hotkey("B", (*) => pad.DPadUp := "")
                    pad.Hotkey("X", (*) => pad.DPad := "Down")
                    pad.Hotkey("Y", (*) => pad.Release())
                    pad.Hotkey("LeftShoulder", (*) => (pad.DPadLeft := "", pad.Release("RightTrigger"), Log("given back")))
                    ; A direction through every way of setting: Hold with
                    ; "" giving back, Set, Tap and Sequence, on the device
                    ; and on the script's own controller.
                    pad.Hotkey("Button5", (*) => (pad.Hold("DPadDown", true, "LeftX", 50), ctl.Hold("ViewDown", true), Log("held")))
                    pad.Hotkey("Button6", (*) => (pad.Hold("DPadDown", "", "LeftX", ""), ctl.Hold("ViewDown", ""), Log("released")))
                    pad.Hotkey("Button7", (*) => (pad.Set("DPadUp", true), Log("set")))
                    pad.Hotkey("Button8", (*) => (pad.Tap("DPadLeft", 400), ctl.Tap("ViewUp", 400), Log("tapped")))
                    pad.Hotkey("Button9", (*) => (pad.Sequence("DPadUp", 300, "DPadRight", 300), ctl.Sequence("ViewDown", 300), Log("sequenced")))
                    Log("waiting")
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
                Assert.Equal("ctl 0 1 90", run.Expect("ctl "));
                run.Expect("waiting");
                for (int round = 0; round < 2; round++)
                {
                    using var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe);
                    var pad = Pad(rig);
                    using var loop = new PassLoop(rig, 1000, pad);
                    Assert.Equal("connected 1", run.Expect("connected ", 15000));
                    await Eventually(() => pad.InputState.Povs[0] == 4500, dir, run, 10000);
                    var mine = rig.Host.Sessions.Devices.All().Single(e => e.Key == "dirs");
                    await Eventually(() => mine.Device.PeekState().Povs[0] == 9000, dir, run);
                    if (round == 1)
                    {
                        loop.Raw[0].Buttons[1] = true;
                        await Eventually(() => pad.InputState.Povs[0] == 9000, dir, run);
                        loop.Raw[0].Buttons[1] = false;
                        loop.Raw[0].Buttons[2] = true;
                        await Eventually(() => pad.InputState.Povs[0] == 13500, dir, run);
                        loop.Raw[0].Buttons[2] = false;
                        loop.Raw[0].Buttons[3] = true;
                        await Eventually(() => pad.InputState.Povs[0] == -1, dir, run);
                        loop.Raw[0].Buttons[4] = true;
                        Assert.Equal("given back", run.Expect("given back"));
                        loop.Raw[0].Buttons[4] = false;
                        await DirectionsEveryWay(loop, pad, mine, s => run.Expect(s), dir, run);
                    }
                    rig.Dispose();
                    Assert.Equal("connected 0", run.Expect("connected "));
                }
                Assert.DoesNotContain("error", run.Seen);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>A hat's direction through every way a script sets one,
        /// pressed from its hotkeys on Button5 to Button9, with the pad's hat
        /// at rest and the script's own controller's at Right before them:
        /// Hold with a direction, and "" in Hold giving it back, on the pad
        /// and on the controller, Set until the device's own Up moves, and
        /// Tap and Sequence on PadForge's clock, on both. The three
        /// languages' direction tests share it.</summary>
        internal static async Task DirectionsEveryWay(PassLoop loop, UserDevice pad, ProgramDevices.Entry mine,
            Func<string, string> expect, string dir, ScriptRun run)
        {
            int Hat() => pad.InputState.Povs[0];
            int Own() => mine.Device.PeekState().Povs[0];
            void Press(int button, string said)
            {
                loop.Raw[0].Buttons[button] = true;
                Assert.Equal(said, expect(said));
                loop.Raw[0].Buttons[button] = false;
            }

            Press(5, "held");
            await Eventually(() => Hat() == 18000 && pad.InputState.Axis[0] == PlainUnits.AxisToRaw(50, 32768), dir, run);
            await Eventually(() => Own() == 13500, dir, run);
            Press(6, "released");
            await Eventually(() => Hat() == -1 && pad.InputState.Axis[0] == 32768, dir, run);
            await Eventually(() => Own() == 9000, dir, run);

            Press(7, "set");
            await Eventually(() => Hat() == 0, dir, run);
            loop.Raw[0].Povs[0] = 0;
            await Task.Delay(50);
            loop.Raw[0].Povs[0] = -1;
            await Eventually(() => Hat() == -1, dir, run);

            Press(8, "tapped");
            await Eventually(() => Hat() == 27000 && Own() == 4500, dir, run);
            await Eventually(() => Hat() == -1 && Own() == 9000, dir, run);
            Press(9, "sequenced");
            await Eventually(() => Hat() == 0 && Own() == 13500, dir, run);
            await Eventually(() => Hat() == 9000, dir, run);
            await Eventually(() => Hat() == -1 && Own() == 9000, dir, run);
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
