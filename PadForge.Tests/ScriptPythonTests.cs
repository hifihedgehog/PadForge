using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Python library's object model against a real Python and a host on
    /// a pipe of the test's own (#600 items 21 to 25 and 27): reads from the
    /// copy, holds, timed steps, hotkeys in AutoHotkey's forms, a controller
    /// of the script's own, a script that starts before PadForge and carries
    /// on through a restart, a level too low, and every example. Python has
    /// no built-in that sends a key or moves the mouse, and the examples are
    /// scanned for any call that would reach Windows' input.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ScriptPythonTests
    {
        internal static string Exe => AutomationLibraryTests.Interpreter("python");

        /// <summary>What every script under test starts with: the library's
        /// folder on the path (isolated mode leaves the script's own folder
        /// off it), a log on standard output, an error as one line and the
        /// end of the run, and an end after 60 seconds whatever
        /// happens.</summary>
        internal const string Head = """
            import os, sys, threading, time, traceback
            sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "lib"))
            def log(*parts):
                print(" ".join(str(p) for p in parts), flush=True)
            def s(v):
                return "1" if v is True else "0" if v is False else "" if v is None else str(v)
            def _fail(kind, value, tb):
                log("error " + " / ".join(traceback.format_exception(kind, value, tb)).replace("\n", " "))
                os._exit(1)
            sys.excepthook = _fail
            _end = threading.Timer(60, os._exit, (0,))
            _end.daemon = True
            _end.start()
            import padforge

            """;

        /// <summary>What an example may not call: each reaches Windows'
        /// input, or loads what could.</summary>
        internal static readonly string[] Unsafe =
            { "SendInput", "keybd_event", "mouse_event", "SendKeys", "ctypes", "Add-Type", "[System.Windows.Forms", "wscript" };

        internal static string NewFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Python." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "lib"));
            AutomationLibraries.Save(AutomationLibraryKind.Python, Path.Combine(dir, "lib", "padforge.py"));
            return dir;
        }

        internal static string Script(string dir, string body, string name = "script.py")
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, Head + body + "\n", new UTF8Encoding(false));
            return path;
        }

        /// <summary>Runs a script isolated (-I), with UTF-8 and unbuffered
        /// output, PADFORGE_PIPE naming the test's pipe.</summary>
        internal static ScriptRun Start(string script, string pipe)
        {
            Assert.False(string.IsNullOrEmpty(pipe));
            return new ScriptRun(Exe, "-I -X utf8 -u \"" + script + "\"", pipe: pipe);
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

        [InterpreterFact("python")]
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
                    pad = padforge.device("wireless")
                    for _ in range(200):
                        if pad.connected:
                            break
                        time.sleep(0.01)
                    log("found", pad.id, pad.type, pad.name, s(pad.connected))
                    log("read", s(pad.A), s(pad.LeftX), s(pad.RightTrigger), s(pad.DPad), s(pad.DPadUp), s(pad.HatRight),
                        s(pad.HatDown), s(pad.LeftXPlus), s(pad.LeftXMinus), s(pad.button[0]), s(pad.axis[0]), s(pad["B"]))
                    def heard(name):
                        log("hk", name)
                    for key in ["B", "~X", "~Y up", "HatDown", "LeftX > 80%", "LeftX > 80% up", "LeftX < -80%",
                                "RightTriggerPlus > 50%"]:
                        pad.hotkey(key, heard)
                    for bad in ["LeftX", "Nothing", "A > 50%", "!!", "DPad"]:
                        try:
                            pad.hotkey(bad, heard)
                        except ValueError as e:
                            log("bad", e)
                    pad.on_change("RightX", lambda v, n: log("change", n, v))
                    pad.on_connect(lambda c: log("connect", 1 if c else 0))
                    pad.LeftShoulder = True
                    pad.set("RightY", 40)
                    ctl = padforge.add_controller("pypad", "Python Pad", buttons=["Fire", "Jump"], axes=[{"name": "Steer"}],
                                                  gyro=True, touchpads=[2], battery=True,
                                                  feedback=["rumble", "trigrumble", "trigfx", "light", "player"])
                    ctl.on_rumble(lambda l, h: log("rumble", l, h))
                    ctl.on_trigger_rumble(lambda l, r: log("trigrumble", l, r))
                    ctl.on_trigger_effects(lambda l, r: log("trigfx", l, r))
                    ctl.on_light(lambda c: log("light", c))
                    ctl.on_player(lambda n: log("player", n))
                    ctl.Fire = True
                    ctl.Steer = 50
                    ctl.gyro = (1, 2, 3)
                    ctl.battery = 80
                    ctl.touch(0, 0, 0.25, 0.75)
                    log("controller", ctl.id, s(ctl.Fire), s(ctl.Steer))
                    padforge.set_value("pyspeed", 7)
                    for d in padforge.devices():
                        if d.id == pad.id:
                            log("listed", s("LeftX" in d.controls), d.controls["RightTrigger"].kind)
                    # The rig serves no assistant tools: the refusal shows the
                    # call went out.
                    try:
                        log("tool", padforge.tool("status"))
                    except padforge.PadForgeError as e:
                        log("tool", e)
                    log("ready")
                    padforge.wait()
                    """);
                using var run = Start(script, rig.PipeName);
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
                    Assert.Equal(7, ScriptValues.Read("pyspeed"), 3);
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
                Assert.Equal("connect 0", run.Expect("connect 0"));
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        [InterpreterFact("python")]
        public async Task AScript_StartsBeforePadForge_AndCarriesOnThroughARestart_WithItsHoldsClaimsAndValuesBack()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad = padforge.device("wireless")
                    ctl = padforge.add_controller("restart", "Restart Pad", buttons=["Fire"])
                    pad.Y = True
                    ctl.Fire = True
                    padforge.set_value("restartvalue", 3)
                    pad.hotkey("B", lambda n: log("hk", n))
                    log("waiting", s(padforge.connected), padforge.why)
                    was = False
                    while True:
                        now = padforge.connected
                        if now != was:
                            was = now
                            log("connected", 1 if now else 0)
                        time.sleep(0.05)
                    """);
                using var run = Start(script, pipe);
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
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// Before its device is found, one rule in every library (#600
        /// items 21, 22 and 24): a hold, a hold of several and a set wait
        /// under the control's name and apply when the device is found. A
        /// tap or a sequence is refused with the not-connected error:
        /// PadForge's own while PadForge does not run, and a read's of an
        /// absent device while PadForge runs without the device. The
        /// script's own controller is refused the same before PadForge
        /// takes its declaration.
        /// </summary>
        [InterpreterFact("python")]
        public async Task BeforeItsDeviceIsFound_HoldsAndSetsWait_AndTapsAndSequencesAreRefused()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad = padforge.device("wireless")
                    ctl = padforge.add_controller("early", "Early Pad", buttons=["Fire"])
                    def attempt(what, fn):
                        try:
                            fn()
                            log(what, "ok")
                        except Exception as e:
                            log(what, str(e))
                    attempt("hold", lambda: pad.hold("X", True, "LeftX", 50))
                    attempt("set", lambda: pad.set("B", True))
                    # A set given back, and a set a hold takes the place of.
                    pad.set("LeftShoulder", True)
                    pad.release("LeftShoulder")
                    pad.set("RightShoulder", True)
                    pad.RightShoulder = False
                    attempt("tap", lambda: pad.tap("A", 50))
                    attempt("sequence", lambda: pad.sequence("A", 50, "B", 50))
                    attempt("ctl tap", lambda: ctl.tap("Fire", 50))
                    attempt("ctl sequence", lambda: ctl.sequence("Fire", 50))
                    log("waiting")
                    while not padforge.connected:
                        time.sleep(0.02)
                    # PadForge runs, and no device it has is the pad.
                    attempt("absent tap", lambda: pad.tap("A", 50))
                    attempt("absent sequence", lambda: pad.sequence("A", 50))
                    attempt("absent set", lambda: pad.set("Y", True))
                    log("absent done")
                    while True:
                        time.sleep(0.05)
                    """);
                using var run = Start(script, pipe);
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

                await ScriptAutoHotkeyTests.AfterTheDeviceIsFound(pipe, dir, run, Pad);
                Assert.DoesNotContain("stderr: ", run.Seen);
                Assert.DoesNotContain("error ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A tap or a sequence on a device PadForge has but that is not
        /// connected is refused by PadForge as it handles the line, in a
        /// read's words, and the library raises the refusal (#600 item 21).
        /// The library does not judge the connection itself. A hold made
        /// meanwhile waits for the device.
        /// </summary>
        [InterpreterFact("python")]
        public async Task ATapOnADeviceThatIsNotConnected_IsRefusedByPadForge()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                string script = Script(dir, """
                    pad = padforge.device("wireless")
                    def attempt(what, fn):
                        try:
                            fn()
                            log(what, "ok")
                        except Exception as e:
                            log(what, str(e))
                    def hold_x():
                        pad.X = True
                    while not pad.connected:
                        time.sleep(0.02)
                    log("seen online")
                    while pad.connected:
                        time.sleep(0.02)
                    # PadForge has the pad, and it is not connected.
                    attempt("offline tap", lambda: pad.tap("A", 300))
                    attempt("offline sequence", lambda: pad.sequence("A", 50, "B", 50))
                    attempt("offline hold", hold_x)
                    log("offline done")
                    while not pad.connected:
                        time.sleep(0.02)
                    attempt("online tap", lambda: pad.tap("A", 300))
                    while True:
                        time.sleep(0.05)
                    """);
                using var run = Start(script, rig.PipeName);
                await ScriptAutoHotkeyTests.TapsWhileTheDeviceIsNotConnected(pad, run, dir);
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// While PadForge restarts and the pipe is down, a found device's
        /// set waits as a hold does and is sent once when the device is
        /// found again, and a tap or a sequence, on the device or on the
        /// script's own controller, raises PadForge's not-connected error
        /// (#600 items 22 and 24).
        /// </summary>
        [InterpreterFact("python")]
        public async Task WhilePadForgeRestarts_ASetWaits_AndATapOrASequenceIsRefused()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad = padforge.device("wireless")
                    ctl = padforge.add_controller("gap", "Gap Pad", buttons=["Fire"])
                    def attempt(what, fn):
                        try:
                            fn()
                            log(what, "ok")
                        except Exception as e:
                            log(what, str(e))
                    while not pad.connected:
                        time.sleep(0.02)
                    pad.X = True
                    log("seen online")
                    while padforge.connected:
                        time.sleep(0.02)
                    # PadForge restarts: the pipe is down.
                    attempt("gap set", lambda: pad.set("B", True))
                    attempt("gap tap", lambda: pad.tap("A", 50))
                    attempt("gap sequence", lambda: pad.sequence("A", 50))
                    attempt("gap ctl tap", lambda: ctl.tap("Fire", 50))
                    attempt("gap ctl sequence", lambda: ctl.sequence("Fire", 50))
                    log("gap done")
                    while True:
                        time.sleep(0.05)
                    """);
                using var run = Start(script, pipe);
                await ScriptAutoHotkeyTests.WhilePadForgeRestarts(pipe, dir, run, Pad);
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A device from devices() is the same object as one from device()
        /// (#600 item 21): its connection stays current while the script
        /// only lists it, and on its first hold, set or tap it follows
        /// itself, so the hold, the set and the tap reach the pad, its
        /// controls are read, and its connection stays current after.
        /// </summary>
        [InterpreterFact("python")]
        public async Task ADeviceFromDevices_FollowsItselfOnItsFirstHold_AndStaysCurrent()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                string script = Script(dir, """
                    listed = next(d for d in padforge.devices() if "Wireless" in d.name)
                    log("listed", s(listed.connected))
                    while listed.connected:
                        time.sleep(0.02)
                    log("listed offline")
                    while not listed.connected:
                        time.sleep(0.02)
                    log("listed online")
                    listed.X = True
                    listed.set("B", True)
                    listed.tap("A", 300)
                    log("same", s(padforge.device(listed.id) is listed), s(len(listed.controls) > 0))
                    while listed.connected:
                        time.sleep(0.02)
                    log("followed offline")
                    while True:
                        time.sleep(0.05)
                    """);
                using var run = Start(script, rig.PipeName);
                await ScriptAutoHotkeyTests.ListedThenFollowed(pad, run, dir);
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A device from devices() that the script has not used yet, while
        /// PadForge restarts, does what a found device does (#600 items 21
        /// and 24): its controls are not read yet, so hold and set wait under
        /// the names given, and tap and sequence raise PadForge's
        /// not-connected error. PadForge comes back with the same pad: it is
        /// found again, and X and LeftX are held and B set.
        /// </summary>
        [InterpreterFact("python")]
        public async Task ADeviceFromDevices_WhilePadForgeRestarts_WaitsOrRefusesAsAFoundOneDoes()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    listed = next(d for d in padforge.devices() if "Wireless" in d.name)
                    log("listed", listed.id)
                    def attempt(what, fn):
                        try:
                            fn()
                            log(what, "ok")
                        except Exception as e:
                            log(what, str(e))
                    while padforge.connected:
                        time.sleep(0.02)
                    # PadForge restarts, and the script has not used the listed
                    # device yet.
                    attempt("gap hold", lambda: listed.hold("X", True, "LeftX", 50))
                    attempt("gap set", lambda: listed.set("B", True))
                    attempt("gap tap", lambda: listed.tap("A", 50))
                    attempt("gap sequence", lambda: listed.sequence("A", 50))
                    log("gap done")
                    while True:
                        time.sleep(0.05)
                    """);
                await ScriptAutoHotkeyTests.ListedWhilePadForgeRestarts(pipe, dir, () => Start(script, pipe), Pad);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A device the script found, missing from PadForge's list when
        /// PadForge comes back, is found when it connects after (#600 items
        /// 22 and 24), and what the script held on it is put back.
        /// </summary>
        [InterpreterFact("python")]
        public async Task AFoundDevice_MissingWhenPadForgeComesBack_IsFoundWhenItConnects()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad = padforge.device("wireless")
                    while not pad.connected:
                        time.sleep(0.02)
                    pad.X = True
                    log("found")
                    while padforge.connected:
                        time.sleep(0.02)
                    log("gone")
                    while not padforge.connected:
                        time.sleep(0.02)
                    log("back")
                    while True:
                        time.sleep(0.05)
                    """);
                await ScriptAutoHotkeyTests.FoundAgainWhenItConnects(pipe, dir, () => Start(script, pipe), Pad);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A device from devices() the script never uses keeps its
        /// connection true through a restart (#600 items 21 and 24): not
        /// connected while PadForge is gone, connected again when PadForge
        /// comes back with the pad.
        /// </summary>
        [InterpreterFact("python")]
        public async Task AnUnusedListedDevice_IsNotConnectedWhilePadForgeIsGone_AndIsAgainAfter()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    listed = next(d for d in padforge.devices() if "Wireless" in d.name)
                    log("listed", s(listed.connected))
                    while padforge.connected:
                        time.sleep(0.02)
                    log("gap", s(listed.connected))
                    while not padforge.connected:
                        time.sleep(0.02)
                    end = time.monotonic() + 5
                    while not listed.connected and time.monotonic() < end:
                        time.sleep(0.02)
                    log("back", s(listed.connected))
                    while True:
                        time.sleep(0.05)
                    """);
                await ScriptAutoHotkeyTests.UnusedListedThroughARestart(pipe, dir, () => Start(script, pipe), Pad);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A device from devices() follows itself on its first use of any
        /// kind (#600 item 21): a read, a hotkey, a change callback that
        /// streams an axis, as well as a hold, set, tap or sequence. Three
        /// listed pads, each first used one way: the read pad's A reads
        /// live, the hotkey pad's B fires its hotkey, and the change pad's
        /// LeftX calls its callback.
        /// </summary>
        [InterpreterFact("python")]
        public async Task ADeviceFromDevices_FollowsItselfOnARead_AHotkeyOrAChangeCallback()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pads = ScriptAutoHotkeyTests.ThreePads(rig);
                using var loop = new PassLoop(rig, 1000, pads);
                string script = Script(dir, """
                    missing = padforge.device("no such pad")
                    log("missing", "nothing" if missing.A is None else "something")
                    listed = {d.name: d for d in padforge.devices()}
                    read_pad, hotkey_pad, change_pad = listed["Read Pad"], listed["Hotkey Pad"], listed["Change Pad"]
                    hotkey_pad.hotkey("B", lambda: log("hk B"))
                    change_pad.on_change("LeftX", lambda value: log("change LeftX", s(value)))
                    log("ready")
                    while read_pad.A is not True:
                        time.sleep(0.01)
                    log("read A", s(read_pad.connected))
                    while True:
                        time.sleep(0.05)
                    """);
                using var run = Start(script, rig.PipeName);
                await ScriptAutoHotkeyTests.FirstUsesOfThreeListedPads(loop, run, dir);
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A hat's direction is set on its own (#600 items 3 and 21):
        /// DPadUp and DPadRight held before PadForge runs read Up and Right
        /// together once it does, and again after a restart. Given back,
        /// Up leaves Right. The whole hat held is what Right goes over, and
        /// release() gives everything back. On the script's own controller a
        /// direction goes into the hat it set.
        /// </summary>
        [InterpreterFact("python")]
        public async Task AHatsDirections_AreSetOnTheirOwn_AndComeBackAfterARestart()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    pad = padforge.device("wireless")
                    ctl = padforge.add_controller("dirs", "Directions Pad", buttons=["Fire"], hats=["View"])
                    # What is not held is given back with no error, before the
                    # device is found and after.
                    pad.DPadLeft = None
                    pad.release("RightTrigger")
                    pad.DPadUp = True
                    pad.DPadRight = True
                    ctl.ViewUp = True
                    ctl.ViewRight = True
                    ctl.ViewUp = False
                    log("ctl", s(ctl.ViewUp), s(ctl.ViewRight), s(ctl.View))
                    def give_up(n):
                        pad.DPadUp = None
                    def hat_down(n):
                        pad.DPad = "Down"
                    pad.hotkey("B", give_up)
                    pad.hotkey("X", hat_down)
                    pad.hotkey("Y", lambda n: pad.release())
                    def give_back(n):
                        pad.DPadLeft = None
                        pad.release("RightTrigger")
                        log("given back")
                    pad.hotkey("LeftShoulder", give_back)
                    # A direction through every way of setting: hold with None
                    # giving back, set, tap and sequence, on the device and on
                    # the script's own controller.
                    def held(n):
                        pad.hold("DPadDown", True, "LeftX", 50)
                        ctl.hold("ViewDown", True)
                        log("held")
                    def released(n):
                        pad.hold("DPadDown", None, "LeftX", None)
                        ctl.hold("ViewDown", None)
                        log("released")
                    def set_up(n):
                        pad.set("DPadUp", True)
                        log("set")
                    def tapped(n):
                        pad.tap("DPadLeft", 400)
                        ctl.tap("ViewUp", 400)
                        log("tapped")
                    def sequenced(n):
                        pad.sequence("DPadUp", 300, "DPadRight", 300)
                        ctl.sequence("ViewDown", 300)
                        log("sequenced")
                    pad.hotkey("Button5", held)
                    pad.hotkey("Button6", released)
                    pad.hotkey("Button7", set_up)
                    pad.hotkey("Button8", tapped)
                    pad.hotkey("Button9", sequenced)
                    log("waiting")
                    was = False
                    while True:
                        now = padforge.connected
                        if now != was:
                            was = now
                            log("connected", 1 if now else 0)
                        time.sleep(0.05)
                    """);
                using var run = Start(script, pipe);
                Assert.Equal("ctl 0 1 90", run.Expect("ctl "));
                run.Expect("waiting");
                for (int round = 0; round < 2; round++)
                {
                    using var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe);
                    var pad = Pad(rig);
                    using var loop = new PassLoop(rig, 1000, pad);
                    Assert.Equal("connected 1", run.Expect("connected ", 15000));
                    await AutomationPipeRig.Until(() => pad.InputState.Povs[0] == 4500, 10000);
                    var mine = rig.Host.Sessions.Devices.All().Single(e => e.Key == "dirs");
                    await AutomationPipeRig.Until(() => mine.Device.PeekState().Povs[0] == 9000, 5000);
                    if (round == 1)
                    {
                        loop.Raw[0].Buttons[1] = true;
                        await AutomationPipeRig.Until(() => pad.InputState.Povs[0] == 9000);
                        loop.Raw[0].Buttons[1] = false;
                        loop.Raw[0].Buttons[2] = true;
                        await AutomationPipeRig.Until(() => pad.InputState.Povs[0] == 13500);
                        loop.Raw[0].Buttons[2] = false;
                        loop.Raw[0].Buttons[3] = true;
                        await AutomationPipeRig.Until(() => pad.InputState.Povs[0] == -1);
                        loop.Raw[0].Buttons[4] = true;
                        Assert.Equal("given back", run.Expect("given back"));
                        loop.Raw[0].Buttons[4] = false;
                        await ScriptAutoHotkeyTests.DirectionsEveryWay(loop, pad, mine, s => run.Expect(s), dir, run);
                    }
                    rig.Dispose();
                    Assert.Equal("connected 0", run.Expect("connected "));
                }
                Assert.DoesNotContain("stderr: ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        // ── A level too low ──

        [InterpreterFact("python")]
        public void ARequestPastTheLevel_RaisesPadForgesRefusal_InWords()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.Commands);
                string script = Script(dir, """
                    log("level", padforge.connected, padforge.level)
                    try:
                        padforge.devices()
                    except padforge.PadForgeError as e:
                        log("refused", e.code, e)
                    try:
                        padforge.add_controller("low", "Low Pad", buttons=["Fire"])
                    except padforge.PadForgeError as e:
                        log("refused", e.code, e)
                    padforge.wait()
                    """);
                using var run = Start(script, rig.PipeName);
                Assert.Equal("level True commands", run.Expect("level "));
                Assert.Equal("refused not-allowed PadForge refused devices: Automation's level is commands, too low for it. Raise it in Extras, Automation.",
                    run.Expect("refused "));
                Assert.Equal("refused not-allowed PadForge refused declare: Automation's level is commands, too low for it. Raise it in Extras, Automation.",
                    run.Expect("refused "));
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        // ── A reach of its own devices (#600 DESIGN-2 N3) ──

        /// <summary>connect(reach="own") asks PadForge to hold the script to
        /// its own devices: a command is refused in PadForge's words, and a
        /// read still answers.</summary>
        [InterpreterFact("python")]
        public void AScriptThatAsksForItsOwnDevices_IsRefusedACommand_AndStillReads()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                string script = Script(dir, """
                    padforge.connect("Steer Assist", reach="own")
                    log("level", padforge.connected, padforge.level)
                    log("state", bool(padforge.state()))
                    try:
                        padforge.switch_profile("Racing")
                        log("refused nothing")
                    except padforge.PadForgeError as e:
                        log("refused", e.code, e)
                    padforge.wait()
                    """);
                using var run = Start(script, rig.PipeName);
                Assert.Equal("level True send-inputs", run.Expect("level "));
                Assert.Equal("state True", run.Expect("state "));
                Assert.Equal("refused outside-ask PadForge refused activate: " + AutomationSessions.OutsideAskWords + " (outside-ask).",
                    run.Expect("refused "));
                Assert.DoesNotContain("error ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        // ── Item 27: every example in the docs runs ──

        /// <summary>The examples the scripting docs show, and the library
        /// head's own, as files.</summary>
        private static List<string> AllExamples()
        {
            var examples = ScriptAutoHotkeyTests.Examples(".py").ToList();
            Assert.True(examples.Count >= 6, "the examples are in Resources/Automation/Examples");
            string head = Encoding.UTF8.GetString(AutomationLibraries.Read(AutomationLibraryKind.Python));
            var lines = head.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int from = lines.IndexOf("Example:") + 2;
            Assert.True(from > 1, "the library's head has an example");
            var own = lines.Skip(from).TakeWhile(l => l.Length == 0 || l.StartsWith("    ", StringComparison.Ordinal))
                .Select(l => l.Length == 0 ? l : l.Substring(4)).ToList();
            string ownFile = Path.Combine(Path.GetTempPath(), "PadForge.Tests.HeadExample." + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(ownFile, string.Join("\n", own).TrimEnd() + "\n");
            examples.Add(ownFile);
            return examples;
        }

        /// <summary>Runs an example for 1.5 seconds against a host with the
        /// pad moving under it, from a runner that puts the library on the
        /// path and says "alive" with the library's errors at the end.</summary>
        private static async Task<string> RunExample(string example)
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                string copy = Path.Combine(dir, Path.GetFileName(example));
                File.Copy(example, copy);
                string runner = Script(dir, $$"""
                    import runpy
                    def _alive():
                        log("alive " + " | ".join(padforge.errors))
                    _timer = threading.Timer(1.5, _alive)
                    _timer.daemon = True
                    _timer.start()
                    runpy.run_path({{"r\"" + copy + "\""}}, run_name="__main__")
                    """, "run.py");
                using var loop = new PassLoop(rig, 500, pad);
                using var run = Start(runner, rig.PipeName);
                await AutomationPipeRig.Until(() => rig.Host.Sessions.Count > 0, 10000);
                // A while with the pad moving under it: every hotkey and
                // reader of the example runs. The pad moves and comes back
                // every 100 passes, so a change also comes after the script
                // follows the pad, whenever that is.
                loop.Hook = pass =>
                {
                    bool on = pass / 100 % 2 == 1;
                    loop.Raw[0].Buttons[0] = on;
                    loop.Raw[0].Buttons[2] = on;
                    loop.Raw[0].Buttons[5] = on;
                    loop.Raw[0].Axis[0] = on ? 65535 : 32768;
                    loop.Raw[0].Axis[3] = on ? 65535 : 32768;
                    loop.Raw[0].Axis[2] = on ? 65535 : 0;
                    loop.Raw[0].Axis[5] = on ? 65535 : 0;
                };
                string alive = run.Expect("alive", 10000);
                await Task.Delay(100);
                run.Drain();
                return alive + "\n" + run.Seen;
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        [InterpreterFact("python")]
        public async Task EveryPythonExample_RunsAgainstAHost_AndCallsNothingThatReachesWindowsInput()
        {
            foreach (string example in AllExamples())
            {
                string text = File.ReadAllText(example);
                foreach (string word in Unsafe)
                    Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), Path.GetFileName(example) + " names " + word);
                Assert.Contains("import padforge", text);
                string seen = await RunExample(example);
                Assert.True(seen.StartsWith("alive \n", StringComparison.Ordinal), Path.GetFileName(example) + ": " + seen);
                Assert.DoesNotContain("error ", seen);
                Assert.DoesNotContain("stderr: ", seen);
            }
        }

        /// <summary>The positive control for the test above: an example
        /// whose callback fails is seen failing.</summary>
        [InterpreterFact("python")]
        public async Task AnExampleWhoseCallbackFails_IsSeenFailing()
        {
            string bad = Path.Combine(Path.GetTempPath(), "PadForge.Tests.BadExample." + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(bad, "import padforge\n\npad = padforge.device(\"gamepad\")\npad.hotkey(\"A\", lambda: 1 / 0)\npadforge.wait()\n");
            try
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => RunExample(bad));
                Assert.Contains("ZeroDivisionError", error.Message);
            }
            finally
            {
                File.Delete(bad);
            }
        }
    }
}
