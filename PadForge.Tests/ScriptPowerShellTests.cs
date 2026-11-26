using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    /// The PowerShell library's object model against Windows PowerShell 5.1
    /// and a host on a pipe of the test's own (#600 items 21 to 25 and 27):
    /// the copy a read comes from, holds, timed steps, hotkeys in
    /// AutoHotkey's forms, a controller of the script's own, a script that
    /// starts before PadForge and carries on through a restart, a level too
    /// low, and every example. PowerShell has no command of its own that
    /// sends a key or moves the mouse, and no script here calls one: the
    /// examples are scanned for the calls that would.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ScriptPowerShellTests
    {
        internal static string Exe => AutomationLibraryTests.Interpreter("powershell");

        /// <summary>Calls that reach Windows' input, which no example may
        /// make.</summary>
        internal static readonly Regex ReachesInput = new(
            @"SendInput|keybd_event|mouse_event|SendKeys|ctypes|Add-Type|\[System\.Windows\.Forms|wscript",
            RegexOptions.IgnoreCase);

        /// <summary>A folder whose Modules folder holds the library, where
        /// <c>Import-Module PadForge</c> finds it as it finds Documents'
        /// after an install.</summary>
        internal static string NewFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.PowerShell." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "Modules", "PadForge"));
            LibraryFiles.Write(AutomationLibraryKind.PowerShell, ModulePath(dir));
            return dir;
        }

        internal static string ModulePath(string dir) => Path.Combine(dir, "Modules", "PadForge", "PadForge.psm1");

        /// <summary>A script that stops at its first error and writes it, and
        /// writes its lines to its standard output.</summary>
        internal const string Prelude = """
            $ErrorActionPreference = 'Stop'
            function Log([string]$Text) { [Console]::Out.WriteLine($Text) }
            trap { Log ('error ' + $_.Exception.Message + ' line ' + $_.InvocationInfo.ScriptLineNumber); exit 1 }
            Import-Module PadForge
            Log ('module ' + (Get-Module PadForge).Path)
            """;

        internal static string Script(string dir, string body, string name = "script.ps1")
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, Prelude + "\n" + body + "\n", new UTF8Encoding(true));
            return path;
        }

        /// <summary>Windows PowerShell on the script, with the test's pipe and
        /// the test's copy of the library first on the module path, and none
        /// of PowerShell 7's modules.</summary>
        internal static ScriptRun Start(string dir, string script, string pipe)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string modules = string.Join(";", Path.Combine(dir, "Modules"), Path.Combine(programs, "WindowsPowerShell", "Modules"),
                Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "Modules"));
            return new ScriptRun(Exe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"", null, pipe,
                new Dictionary<string, string> { ["PSModulePath"] = modules });
        }

        private static void ExpectModule(ScriptRun run, string dir)
            => Assert.Equal("module " + ModulePath(dir), run.Expect("module ", 60000), ignoreCase: true);

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

        /// <summary>A declared controller's names are the server's own
        /// (ScriptNames.Declared): a button declared "DPad Up" beside a hat
        /// "DPad" keeps Button0, since DPadUp is the hat's up.</summary>
        [InterpreterFact("powershell")]
        public void ADeclaredControllersNames_AreTheServersOwn()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    $ctl = Add-PadForgeController names 'Names Pad' -Buttons 'DPad Up', Fire -Axes @{ name = 'Steer' } -Hats DPad
                    Log ('names ' + (($ctl.Controls.Keys | Sort-Object) -join ' '))
                    exit 0
                    """);
                using var run = Start(dir, script, pipe);
                ExpectModule(run, dir);
                Assert.Equal("names Button0 DPad Fire Steer", run.Expect("names ", 60000));
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        // ── Items 21 to 25: the object model ──

        [InterpreterFact("powershell")]
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
                    $pad = Get-PadForgeDevice 'wireless'
                    for ($i = 0; -not $pad.Connected -and $i -lt 500; $i++) { Start-Sleep -Milliseconds 10 }
                    Log "found $($pad.Id) $($pad.Type) $($pad.Name) $($pad.Connected)"
                    Log "read $($pad.A) $($pad.LeftX) $($pad.RightTrigger) $($pad.DPad) $($pad.DPadUp) $($pad.HatRight) $($pad.HatDown) $($pad.LeftXPlus) $($pad.LeftXMinus) $($pad.Button[0]) $($pad.Axis[0]) $($pad['B'])"
                    $heard = { param($name) Log "hk $name" }
                    $pad.Hotkey('B', $heard)
                    $pad.Hotkey('~X', $heard)
                    $pad.Hotkey('~Y up', $heard)
                    $pad.Hotkey('HatDown', $heard)
                    $pad.Hotkey('LeftX > 80%', $heard)
                    $pad.Hotkey('LeftX > 80% up', $heard)
                    $pad.Hotkey('LeftX < -80%', $heard)
                    $pad.Hotkey('RightTriggerPlus > 50%', $heard)
                    foreach ($bad in 'LeftX', 'Nothing', 'A > 50%', '!!', 'DPad') {
                        try { $pad.Hotkey($bad, $heard) } catch { Log "bad $($_.Exception.Message)" }
                    }
                    $pad.OnChange('RightX', { param($v, $n) Log "change $n $v" })
                    $pad.OnConnect({ param($c) Log "connect $([int]$c)" })
                    $pad.LeftShoulder = $true
                    $pad.Set('RightY', 40)
                    $ctl = Add-PadForgeController pspad 'PS Pad' -Buttons Fire, Jump -Axes @{ name = 'Steer' } -Gyro -Touchpads 2 -Battery `
                        -Feedback rumble, trigrumble, trigfx, light, player
                    $ctl.OnRumble({ param($l, $h) Log "rumble $l $h" })
                    $ctl.OnTriggerRumble({ param($l, $r) Log "trigrumble $l $r" })
                    $ctl.OnTriggerEffects({ param($l, $r) Log "trigfx $l $r" })
                    $ctl.OnLight({ param($c) Log "light $c" })
                    $ctl.OnPlayer({ param($n) Log "player $n" })
                    $ctl.Fire = $true
                    $ctl.Steer = 50
                    $ctl.Gyro = 1, 2, 3
                    $ctl.Battery = 80
                    $ctl.Touch(0, 0, 0.25, 0.75)
                    Log "controller $($ctl.Id) $($ctl.Fire) $($ctl.Steer)"
                    Set-PadForgeValue psspeed 7
                    foreach ($d in Get-PadForgeDevice) {
                        if ($d.Id -eq $pad.Id) { Log "listed $($d.Controls.ContainsKey('LeftX')) $($d.Controls['RightTrigger'].Kind)" }
                    }
                    # The rig serves no assistant tools: the refusal shows the
                    # call went out.
                    try { Log "tool $(Invoke-PadForgeTool status)" } catch { Log "tool $($_.Exception.Message)" }
                    Log 'ready'
                    Wait-PadForge
                    """);
                using var run = Start(dir, script, rig.PipeName);
                // The engine runs: the script's whole state arrives.
                using (var loop = new PassLoop(rig, 1000, pad))
                {
                    ExpectModule(run, dir);
                    Assert.Equal("found " + id + " gamepad Wireless Controller True", run.Expect("found "));
                    Assert.Equal("read True -100 100 45 True True False 0 100 True -100 False", run.Expect("read "));
                    Assert.Equal("bad An axis or slider hotkey needs a limit, such as LeftX > 80%: LeftX", run.Expect("bad "));
                    Assert.Equal("bad No such control: Nothing", run.Expect("bad "));
                    Assert.Equal("bad Only an axis or a slider takes a limit: A > 50%", run.Expect("bad "));
                    Assert.Equal("bad Not a control: !!", run.Expect("bad "));
                    Assert.Equal("bad A hat's directions are its hotkeys, such as DPadUp: DPad", run.Expect("bad "));
                    var said = run.Expect("controller ").Split(' ');
                    string controller = said[1];
                    Assert.Equal(new[] { "True", "50" }, said.Skip(2));

                    Assert.Equal("listed True axis", run.Expect("listed "));
                    Assert.StartsWith("tool PadForge refused call: ", run.Expect("tool "));
                    run.Expect("ready");

                    // What the host holds: a hold, a set, the controller's
                    // state, the value.
                    await AutomationPipeRig.Until(() => pad.InputState.Buttons[4]);
                    Assert.Equal(PlainUnits.AxisToRaw(40, 32768), pad.InputState.Axis[4]);
                    Assert.Equal(7, ScriptValues.Read("psspeed"), 3);
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
                    // The right trigger's upper half: down, then past it.
                    loop.Raw[0].Axis[5] = 0;
                    await Task.Delay(50);
                    loop.Raw[0].Axis[5] = 65535;
                    Assert.Equal("hk RightTriggerPlus > 50%", run.Expect("hk "));
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
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        [InterpreterFact("powershell")]
        public async Task AScript_StartsBeforePadForge_AndCarriesOnThroughARestart_WithItsHoldsClaimsAndValuesBack()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    $ctl = Add-PadForgeController restart 'Restart Pad' -Buttons Fire
                    $pad.Y = $true
                    $ctl.Fire = $true
                    Set-PadForgeValue restartvalue 3
                    $pad.Hotkey('B', { param($n) Log "hk $n" })
                    $pf = Get-PadForgeConnection
                    Log "waiting $([int]$pf.Connected) $($pf.Why)"
                    $was = $false
                    while ($true) {
                        # Start-Sleep holds up the script's thread: the beat
                        # and the way back go on beside it.
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                        if ($pf.Connected -ne $was) {
                            $was = $pf.Connected
                            Log "connected $([int]$was)"
                        }
                    }
                    """);
                using var run = Start(dir, script, pipe);
                ExpectModule(run, dir);
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
                ScriptAutoHotkeyTests.TryDelete(dir);
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
        [InterpreterFact("powershell")]
        public async Task BeforeItsDeviceIsFound_HoldsAndSetsWait_AndTapsAndSequencesAreRefused()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    $ctl = Add-PadForgeController early 'Early Pad' -Buttons Fire
                    function Attempt([string]$What, [scriptblock]$Do) {
                        try {
                            & $Do
                            Log "$What ok"
                        } catch {
                            Log "$What $($_.Exception.Message)"
                        }
                    }
                    Attempt 'hold' { $pad.Hold('X', $true, 'LeftX', 50) }
                    Attempt 'set' { $pad.Set('B', $true) }
                    # A Set given back, and a Set a hold takes the place of.
                    $pad.Set('LeftShoulder', $true)
                    $pad.Release('LeftShoulder')
                    $pad.Set('RightShoulder', $true)
                    $pad.RightShoulder = $false
                    Attempt 'tap' { $pad.Tap('A', 50) }
                    Attempt 'sequence' { $pad.Sequence('A', 50, 'B', 50) }
                    Attempt 'ctl tap' { $ctl.Tap('Fire', 50) }
                    Attempt 'ctl sequence' { $ctl.Sequence('Fire', 50) }
                    $pf = Get-PadForgeConnection
                    Log 'waiting'
                    while (-not $pf.Connected) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                    }
                    # PadForge runs, and no device it has is the pad.
                    Attempt 'absent tap' { $pad.Tap('A', 50) }
                    Attempt 'absent sequence' { $pad.Sequence('A', 50) }
                    Attempt 'absent set' { $pad.Set('Y', $true) }
                    Log 'absent done'
                    while ($true) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                    }
                    """);
                using var run = Start(dir, script, pipe);
                ExpectModule(run, dir);
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
                Assert.DoesNotContain("error ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A Tap or a Sequence on a device PadForge has but that is not
        /// connected is refused by PadForge as it handles the line, in a
        /// read's words, and the library raises the refusal (#600 item 21).
        /// The library does not judge the connection itself. A hold made
        /// meanwhile waits for the device.
        /// </summary>
        [InterpreterFact("powershell")]
        public async Task ATapOnADeviceThatIsNotConnected_IsRefusedByPadForge()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                string script = Script(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    function Attempt([string]$What, [scriptblock]$Do) {
                        try {
                            & $Do
                            Log "$What ok"
                        } catch {
                            Log "$What $($_.Exception.Message)"
                        }
                    }
                    while (-not $pad.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    Log 'seen online'
                    while ($pad.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    # PadForge has the pad, and it is not connected.
                    Attempt 'offline tap' { $pad.Tap('A', 300) }
                    Attempt 'offline sequence' { $pad.Sequence('A', 50, 'B', 50) }
                    Attempt 'offline hold' { $pad.X = $true }
                    Log 'offline done'
                    while (-not $pad.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    Attempt 'online tap' { $pad.Tap('A', 300) }
                    while ($true) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                    }
                    """);
                using var run = Start(dir, script, rig.PipeName);
                ExpectModule(run, dir);
                await ScriptAutoHotkeyTests.TapsWhileTheDeviceIsNotConnected(pad, run, dir);
                Assert.DoesNotContain("error ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// While PadForge restarts and the pipe is down, a found device's
        /// Set waits as a hold does and is sent once when the device is
        /// found again, and a Tap or a Sequence, on the device or on the
        /// script's own controller, throws PadForge's not-connected error
        /// (#600 items 22 and 24).
        /// </summary>
        [InterpreterFact("powershell")]
        public async Task WhilePadForgeRestarts_ASetWaits_AndATapOrASequenceIsRefused()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    $ctl = Add-PadForgeController gap 'Gap Pad' -Buttons Fire
                    $pf = Get-PadForgeConnection
                    function Attempt([string]$What, [scriptblock]$Do) {
                        try {
                            & $Do
                            Log "$What ok"
                        } catch {
                            Log "$What $($_.Exception.Message)"
                        }
                    }
                    while (-not $pad.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    $pad.X = $true
                    Log 'seen online'
                    while ($pf.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    # PadForge restarts: the pipe is down.
                    Attempt 'gap set' { $pad.Set('B', $true) }
                    Attempt 'gap tap' { $pad.Tap('A', 50) }
                    Attempt 'gap sequence' { $pad.Sequence('A', 50) }
                    Attempt 'gap ctl tap' { $ctl.Tap('Fire', 50) }
                    Attempt 'gap ctl sequence' { $ctl.Sequence('Fire', 50) }
                    Log 'gap done'
                    while ($true) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                    }
                    """);
                using var run = Start(dir, script, pipe);
                ExpectModule(run, dir);
                await ScriptAutoHotkeyTests.WhilePadForgeRestarts(pipe, dir, run, Pad);
                Assert.DoesNotContain("error ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A device from Get-PadForgeDevice with no name, a Devices() list,
        /// is the same object as one from Get-PadForgeDevice by its ID
        /// (#600 item 21): its connection stays current while the script
        /// only lists it, and on its first hold, Set or Tap it follows
        /// itself, so the hold, the Set and the Tap reach the pad, its
        /// controls are read, and its connection stays current after.
        /// </summary>
        [InterpreterFact("powershell")]
        public async Task ADeviceFromDevices_FollowsItselfOnItsFirstHold_AndStaysCurrent()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                using var loop = new PassLoop(rig, 1000, pad);
                string script = Script(dir, """
                    $listed = Get-PadForgeDevice | Where-Object { $_.Name -like '*Wireless*' } | Select-Object -First 1
                    Log "listed $([int]$listed.Connected)"
                    while ($listed.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    Log 'listed offline'
                    while (-not $listed.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    Log 'listed online'
                    $listed.X = $true
                    $listed.Set('B', $true)
                    $listed.Tap('A', 300)
                    $same = [object]::ReferenceEquals((Get-PadForgeDevice $listed.Id), $listed)
                    Log "same $([int]$same) $([int]($listed.Controls.Count -gt 0))"
                    while ($listed.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    Log 'followed offline'
                    while ($true) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                    }
                    """);
                using var run = Start(dir, script, rig.PipeName);
                ExpectModule(run, dir);
                await ScriptAutoHotkeyTests.ListedThenFollowed(pad, run, dir);
                Assert.DoesNotContain("error ", run.Seen);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A device from Get-PadForgeDevice's list that the script has not
        /// used yet, while PadForge restarts, does what a found device does
        /// (#600 items 21 and 24): its controls are not read yet, so Hold and
        /// Set wait under the names given, and Tap and Sequence throw
        /// PadForge's not-connected error. PadForge comes back with the same
        /// pad: it is found again, and X and LeftX are held and B set.
        /// </summary>
        [InterpreterFact("powershell")]
        public async Task ADeviceFromDevices_WhilePadForgeRestarts_WaitsOrRefusesAsAFoundOneDoes()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    $listed = Get-PadForgeDevice | Where-Object { $_.Name -like '*Wireless*' } | Select-Object -First 1
                    Log "listed $($listed.Id)"
                    $pf = Get-PadForgeConnection
                    function Attempt([string]$What, [scriptblock]$Do) {
                        try {
                            & $Do
                            Log "$What ok"
                        } catch {
                            Log "$What $($_.Exception.Message)"
                        }
                    }
                    while ($pf.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    # PadForge restarts, and the script has not used the listed
                    # device yet.
                    Attempt 'gap hold' { $listed.Hold('X', $true, 'LeftX', 50) }
                    Attempt 'gap set' { $listed.Set('B', $true) }
                    Attempt 'gap tap' { $listed.Tap('A', 50) }
                    Attempt 'gap sequence' { $listed.Sequence('A', 50) }
                    Log 'gap done'
                    while ($true) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                    }
                    """);
                await ScriptAutoHotkeyTests.ListedWhilePadForgeRestarts(pipe, dir, () =>
                {
                    var run = Start(dir, script, pipe);
                    ExpectModule(run, dir);
                    return run;
                }, Pad);
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
        [InterpreterFact("powershell")]
        public async Task AFoundDevice_MissingWhenPadForgeComesBack_IsFoundWhenItConnects()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    $pf = Get-PadForgeConnection
                    while (-not $pad.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    $pad.X = $true
                    Log 'found'
                    while ($pf.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    Log 'gone'
                    while (-not $pf.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    Log 'back'
                    while ($true) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                    }
                    """);
                await ScriptAutoHotkeyTests.FoundAgainWhenItConnects(pipe, dir, () =>
                {
                    var run = Start(dir, script, pipe);
                    ExpectModule(run, dir);
                    return run;
                }, Pad);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A device from Get-PadForgeDevice's list the script never uses
        /// keeps its connection true through a restart (#600 items 21 and
        /// 24): not connected while PadForge is gone, connected again when
        /// PadForge comes back with the pad.
        /// </summary>
        [InterpreterFact("powershell")]
        public async Task AnUnusedListedDevice_IsNotConnectedWhilePadForgeIsGone_AndIsAgainAfter()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    $listed = Get-PadForgeDevice | Where-Object { $_.Name -like '*Wireless*' } | Select-Object -First 1
                    $pf = Get-PadForgeConnection
                    Log "listed $([int]$listed.Connected)"
                    while ($pf.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    Log "gap $([int]$listed.Connected)"
                    while (-not $pf.Connected) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    $end = (Get-Date).AddSeconds(5)
                    while (-not $listed.Connected -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 20; Wait-PadForge -Seconds 0.01 }
                    Log "back $([int]$listed.Connected)"
                    while ($true) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                    }
                    """);
                await ScriptAutoHotkeyTests.UnusedListedThroughARestart(pipe, dir, () =>
                {
                    var run = Start(dir, script, pipe);
                    ExpectModule(run, dir);
                    return run;
                }, Pad);
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>
        /// A device from Get-PadForgeDevice's list follows itself on its
        /// first use of any kind (#600 item 21): a read, a hotkey, a change
        /// callback that streams an axis, as well as a hold, Set, Tap or
        /// Sequence. Three listed pads, each first used one way: the read
        /// pad's A reads live, the hotkey pad's B fires its hotkey, and the
        /// change pad's LeftX calls its callback.
        /// </summary>
        [InterpreterFact("powershell")]
        public async Task ADeviceFromDevices_FollowsItselfOnARead_AHotkeyOrAChangeCallback()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pads = ScriptAutoHotkeyTests.ThreePads(rig);
                using var loop = new PassLoop(rig, 1000, pads);
                string script = Script(dir, """
                    $missing = Get-PadForgeDevice 'no such pad'
                    Log "missing $(if ($null -eq $missing.A) { 'nothing' } else { 'something' })"
                    $all = Get-PadForgeDevice
                    $readPad = $all | Where-Object { $_.Name -eq 'Read Pad' } | Select-Object -First 1
                    $hotkeyPad = $all | Where-Object { $_.Name -eq 'Hotkey Pad' } | Select-Object -First 1
                    $changePad = $all | Where-Object { $_.Name -eq 'Change Pad' } | Select-Object -First 1
                    $hotkeyPad.Hotkey('B', { Log 'hk B' })
                    $changePad.OnChange('LeftX', { param($value) Log "change LeftX $value" })
                    Log 'ready'
                    while ($readPad.A -ne $true) { Start-Sleep -Milliseconds 10; Wait-PadForge -Seconds 0.01 }
                    Log "read A $([int]$readPad.Connected)"
                    while ($true) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                    }
                    """);
                using var run = Start(dir, script, rig.PipeName);
                ExpectModule(run, dir);
                await ScriptAutoHotkeyTests.FirstUsesOfThreeListedPads(loop, run, dir);
                Assert.DoesNotContain("error ", run.Seen);
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
        /// Release() gives everything back. On the script's own controller a
        /// direction goes into the hat it set.
        /// </summary>
        [InterpreterFact("powershell")]
        public async Task AHatsDirections_AreSetOnTheirOwn_AndComeBackAfterARestart()
        {
            string dir = NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                string script = Script(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    $ctl = Add-PadForgeController dirs 'Directions Pad' -Buttons Fire -Hats View
                    # What is not held is given back with no error, before the
                    # device is found and after.
                    $pad.DPadLeft = $null
                    $pad.Release('RightTrigger')
                    $pad.DPadUp = $true
                    $pad.DPadRight = $true
                    $ctl.ViewUp = $true
                    $ctl.ViewRight = $true
                    $ctl.ViewUp = $false
                    Log "ctl $([int]$ctl.ViewUp) $([int]$ctl.ViewRight) $($ctl.View)"
                    $pad.Hotkey('B', { $pad.DPadUp = $null })
                    $pad.Hotkey('X', { $pad.DPad = 'Down' })
                    $pad.Hotkey('Y', { $pad.Release() })
                    $pad.Hotkey('LeftShoulder', { $pad.DPadLeft = $null; $pad.Release('RightTrigger'); Log 'given back' })
                    # A direction through every way of setting: Hold with
                    # $null giving back, Set, Tap and Sequence, on the device
                    # and on the script's own controller.
                    $pad.Hotkey('Button5', { $pad.Hold('DPadDown', $true, 'LeftX', 50); $ctl.Hold('ViewDown', $true); Log 'held' })
                    $pad.Hotkey('Button6', { $pad.Hold('DPadDown', $null, 'LeftX', $null); $ctl.Hold('ViewDown', $null); Log 'released' })
                    $pad.Hotkey('Button7', { $pad.Set('DPadUp', $true); Log 'set' })
                    $pad.Hotkey('Button8', { $pad.Tap('DPadLeft', 400); $ctl.Tap('ViewUp', 400); Log 'tapped' })
                    $pad.Hotkey('Button9', { $pad.Sequence('DPadUp', 300, 'DPadRight', 300); $ctl.Sequence('ViewDown', 300); Log 'sequenced' })
                    $pf = Get-PadForgeConnection
                    Log 'waiting'
                    $was = $false
                    while ($true) {
                        Start-Sleep -Milliseconds 30
                        Wait-PadForge -Seconds 0.02
                        if ($pf.Connected -ne $was) {
                            $was = $pf.Connected
                            Log "connected $([int]$was)"
                        }
                    }
                    """);
                using var run = Start(dir, script, pipe);
                ExpectModule(run, dir);
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
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        [InterpreterFact("powershell")]
        public void ARequestPastTheLevel_IsRefused_InTheLibrarysWords()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.Commands);
                string script = Script(dir, """
                    $pf = Get-PadForgeConnection
                    Log "level $($pf.Level) $([int]$pf.Connected)"
                    try { Get-PadForgeDevice | Out-Null; Log 'refused nothing' } catch { Log "refused $($_.Exception.Message)" }
                    try { Set-PadForgeValue speed 1; Log 'refused nothing' } catch { Log "refused $($_.Exception.Message)" }
                    Log 'done'
                    """);
                using var run = Start(dir, script, rig.PipeName);
                ExpectModule(run, dir);
                Assert.Equal("level commands 1", run.Expect("level "));
                Assert.Equal("refused PadForge refused devices: this program works at the Commands level, which does not allow it.", run.Expect("refused "));
                Assert.Equal("refused PadForge refused var: this program works at the Commands level, which does not allow it.", run.Expect("refused "));
                run.Expect("done");
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        /// <summary>Connect-PadForge -Reach own asks PadForge to hold the
        /// script to its own devices (#600 DESIGN-2 N3): a command is refused
        /// in PadForge's words, and a read still answers.</summary>
        [InterpreterFact("powershell")]
        public void AScriptThatAsksForItsOwnDevices_IsRefusedACommand_AndStillReads()
        {
            string dir = NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                string script = Script(dir, """
                    $pf = Connect-PadForge 'Steer Assist' -Reach own
                    Log "level $($pf.Level) $([int]$pf.Connected)"
                    Log "state $([int](-not [string]::IsNullOrEmpty((Get-PadForgeState))))"
                    try { Switch-PadForgeProfile Racing; Log 'refused nothing' } catch { Log "refused $($_.Exception.Message)" }
                    Log 'done'
                    """);
                using var run = Start(dir, script, rig.PipeName);
                ExpectModule(run, dir);
                Assert.Equal("level send-inputs 1", run.Expect("level "));
                Assert.Equal("state 1", run.Expect("state "));
                Assert.Equal("refused PadForge refused activate: " + AutomationSessions.OutsideAskWords + " (outside-ask).", run.Expect("refused "));
                run.Expect("done");
            }
            finally
            {
                ScriptAutoHotkeyTests.TryDelete(dir);
            }
        }

        // ── Item 27: every example in the docs runs ──

        [InterpreterFact("powershell")]
        public async Task EveryPowerShellExample_RunsAgainstAHost_AndCallsNothingThatReachesInput()
        {
            var examples = ScriptAutoHotkeyTests.Examples(".ps1").ToList();
            Assert.Equal(ScriptAutoHotkeyTests.Examples(".ahk").Select(Path.GetFileNameWithoutExtension),
                examples.Select(Path.GetFileNameWithoutExtension));
            // The library's own example, from its head, runs too.
            string head = Encoding.UTF8.GetString(AutomationLibraries.Read(AutomationLibraryKind.PowerShell));
            var lines = head.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int from = lines.IndexOf("Example:") + 2;
            var own = lines.Skip(from).TakeWhile(l => l.StartsWith("    ", StringComparison.Ordinal)).Select(l => l.Substring(4)).ToList();
            Assert.True(own.Count >= 3, "the head's example");
            string ownFile = Path.Combine(Path.GetTempPath(), "PadForge.Tests.HeadExample." + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(ownFile, string.Join("\n", own) + "\n");
            examples.Add(ownFile);
            try
            {
                foreach (string example in examples)
                {
                    string text = File.ReadAllText(example);
                    Assert.DoesNotMatch(ReachesInput, text);
                    Assert.Contains("Import-Module PadForge", text);
                    string dir = NewFolder();
                    try
                    {
                        using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                        var pad = Pad(rig);
                        string script = Path.Combine(dir, Path.GetFileName(example));
                        File.Copy(example, script);
                        using var loop = new PassLoop(rig, 500, pad);
                        using var run = Start(dir, script, rig.PipeName);
                        await AutomationPipeRig.Until(() => rig.Host.Sessions.Count > 0, 30000);
                        // A while with the pad moving under it, pressed, let
                        // go and pressed again, so every hotkey and reader of
                        // the example runs after the script made it.
                        for (int step = 0; step < 3; step++)
                        {
                            bool on = step != 1;
                            loop.Raw[0].Buttons[0] = on;
                            loop.Raw[0].Buttons[2] = on;
                            loop.Raw[0].Buttons[5] = on;
                            loop.Raw[0].Axis[0] = on ? 65535 : 0;
                            loop.Raw[0].Axis[3] = on ? 65535 : 32768;
                            loop.Raw[0].Axis[2] = on ? 65535 : 0;
                            loop.Raw[0].Axis[5] = on ? 65535 : 0;
                            await Task.Delay(500);
                        }
                        var said = run.Drain();
                        Assert.False(run.HasExited, Path.GetFileName(example) + " ended:\n" + run.Seen);
                        Assert.DoesNotContain(said, l => l.StartsWith("stderr: ", StringComparison.Ordinal) || l.StartsWith("error ", StringComparison.Ordinal));
                    }
                    finally
                    {
                        ScriptAutoHotkeyTests.TryDelete(dir);
                    }
                }
            }
            finally
            {
                File.Delete(ownFile);
            }
        }
    }
}
