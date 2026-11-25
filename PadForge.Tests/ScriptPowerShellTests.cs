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
            AutomationLibraries.Save(AutomationLibraryKind.PowerShell, ModulePath(dir));
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
                Assert.Equal("refused PadForge refused devices: Automation's level is commands, too low for it. Raise it in Extras, Automation.", run.Expect("refused "));
                Assert.Equal("refused PadForge refused var: Automation's level is commands, too low for it. Raise it in Extras, Automation.", run.Expect("refused "));
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
