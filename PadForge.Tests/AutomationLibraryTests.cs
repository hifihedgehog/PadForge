using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// <summary>A fact that runs only where its interpreter is on this PC,
    /// and says which one is missing when it is not.</summary>
    public sealed class InterpreterFactAttribute : FactAttribute
    {
        public InterpreterFactAttribute(string interpreter)
        {
            if (AutomationLibraryTests.Interpreter(interpreter) == null)
                Skip = interpreter + " is not on this PC, so its library is not run against a host.";
        }
    }

    /// <summary>
    /// The script libraries PadForge carries (#597): each is saved where the
    /// user says, opens with what it is, and works against a real host on a
    /// pipe name of the test's own. The scripts under test send no key and
    /// move no pointer. The AutoHotkey script sets OnError so an error writes
    /// its line and exits instead of leaving a dialog open, and every process
    /// a test starts is ended by it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationLibraryTests
    {
        internal const string AutoHotkeyExe = @"C:\tmp\padforge6-build\auto-probe\ahk\v2\AutoHotkey64.exe";

        /// <summary>The interpreter's path, or null when this PC has
        /// none.</summary>
        internal static string Interpreter(string which)
        {
            switch (which)
            {
                case "autohotkey":
                    return File.Exists(AutoHotkeyExe) ? AutoHotkeyExe : null;
                case "powershell":
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe");
                    return File.Exists(path) ? path : null;
                }
                default:
                    return FindPython();
            }
        }

        private static string _python;

        private static string FindPython()
        {
            if (_python != null) return _python.Length == 0 ? null : _python;
            var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';')
                .Where(dir => dir.Length > 0 && !dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                .Select(dir => Path.Combine(dir.Trim(), "python.exe"))
                .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Python\bin\python.exe"));
            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(path, "-I -c \"import sys,_winapi;print(sys.version_info[0])\"")
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    if (text.Trim() == "3") return _python = path;
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
            _python = string.Empty;
            return null;
        }

        private static string NewFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Libraries." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ── Carried and saved ──

        [Fact]
        public void EveryLibrary_IsCarried_AndSavedWhereTheUserSays()
        {
            Assert.Equal(new[] { "PadForge.ahk", "padforge.py", "PadForge.psm1" }, AutomationLibraries.All.Select(l => l.FileName));
            string dir = NewFolder();
            try
            {
                foreach (var library in AutomationLibraries.All)
                {
                    Assert.False(string.IsNullOrWhiteSpace(library.Name));
                    Assert.EndsWith("|*" + Path.GetExtension(library.FileName), library.Filter);
                    string path = Path.Combine(dir, library.FileName);
                    File.WriteAllText(path, "old");
                    AutomationLibraries.Save(library.Kind, path);
                    byte[] saved = File.ReadAllBytes(path);
                    Assert.Equal(AutomationLibraries.Read(library.Kind), saved);
                    string text = Encoding.UTF8.GetString(saved);
                    // Each opens with what it is, the protocol it speaks and
                    // one example.
                    string head = text.Substring(0, Math.Min(text.Length, 1200));
                    Assert.Contains("PadForge Automation for", head);
                    Assert.Contains("protocol 1", head);
                    Assert.Contains("Example", head);
                    Assert.DoesNotContain("\t", text);
                }
                Assert.Throws<ArgumentException>(() => AutomationLibraries.Save(AutomationLibraryKind.Python, " "));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void EveryLibrary_SaysWhichItIs_WhenPadForgeIsClosedOffOrTooLow()
        {
            foreach (var library in AutomationLibraries.All)
            {
                string text = Encoding.UTF8.GetString(AutomationLibraries.Read(library.Kind));
                Assert.Contains("PadForge is not running.", text);
                Assert.Contains("PadForge is running, but Automation is off. Turn it on in Extras, Automation.", text);
                Assert.Contains("too low for it. Raise it in Extras, Automation.", text);
                Assert.Contains("PadForge.Instance", text);
            }
        }

        // ── Against a real host ──

        /// <summary>A script's run: its lines as they come, from its output
        /// or from the file it writes.</summary>
        private sealed class Run : IDisposable
        {
            private readonly Process _process;
            private readonly BlockingCollection<string> _lines = new();
            private readonly string _file;
            private readonly CancellationTokenSource _stop = new();
            private readonly StringBuilder _seen = new();

            public Run(string exe, string arguments, string outputFile = null)
            {
                _file = outputFile;
                var start = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = outputFile == null,
                    RedirectStandardError = outputFile == null,
                };
                if (outputFile == null) start.StandardOutputEncoding = Encoding.UTF8;
                start.Environment["PYTHONIOENCODING"] = "utf-8";
                _process = Process.Start(start);
                if (outputFile == null)
                {
                    _process.OutputDataReceived += (_, e) => { if (e.Data != null) _lines.Add(e.Data); };
                    _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _lines.Add("stderr: " + e.Data); };
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                }
                else
                {
                    Task.Run(async () =>
                    {
                        int read = 0;
                        while (!_stop.IsCancellationRequested)
                        {
                            try
                            {
                                if (File.Exists(_file))
                                {
                                    // Shared for writing, so the script's
                                    // own handle on the file is never
                                    // refused while this reads.
                                    string text;
                                    using (var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                                        text = reader.ReadToEnd();
                                    int end = text.LastIndexOf('\n');
                                    if (end >= read)
                                    {
                                        foreach (string line in text.Substring(read, end + 1 - read).Split('\n'))
                                            if (line.Length > 0) _lines.Add(line.TrimEnd('\r'));
                                        read = end + 1;
                                    }
                                }
                            }
                            catch (IOException) { }
                            await Task.Delay(50);
                        }
                    });
                }
            }

            /// <summary>The next line that starts with
            /// <paramref name="prefix"/>. A line of an error ends the
            /// wait.</summary>
            public string Expect(string prefix, int ms = 30000)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < ms)
                {
                    if (!_lines.TryTake(out string line, 200)) continue;
                    _seen.AppendLine(line);
                    if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
                    Assert.False(line.StartsWith("error ", StringComparison.Ordinal), "the script failed: " + line + "\n" + _seen);
                }
                Assert.Fail("no line starting \"" + prefix + "\" in " + ms + " ms. Seen:\n" + _seen);
                return null;
            }

            public void Dispose()
            {
                _stop.Cancel();
                try
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
                catch (InvalidOperationException) { }
                _process.Dispose();
            }
        }

        /// <summary>The steps every library takes against the host, and what
        /// the host sees of them.</summary>
        private static void Drive(AutomationPipeRig rig, UserDevice pad, Run run, string controllerKey, string valueName, double value)
        {
            Assert.Contains("Nothing serves the pipe", run.Expect("closed "));
            Assert.Matches(@"^hello send-inputs \d+$", run.Expect("hello "));
            string id = AutomationPipeRig.IdOf(pad);
            Assert.Equal("found " + id + " gamepad Pad", run.Expect("found "));
            string controller = run.Expect("controller ").Substring("controller ".Length);
            Assert.Equal("read 1 777", run.Expect("read "));
            run.Expect("ready");

            // What the host holds now: the hold, the value, the controller's
            // state and the profile asked for.
            Assert.Contains(ControlHolds.Current[pad.InstanceGuid].Buttons, b => b.Index == 2 && b.Down);
            Assert.Equal(value, ScriptValues.Read(valueName), 3);
            var entry = rig.Host.Sessions.Devices.Find(controller);
            Assert.Equal(controllerKey, entry.Key);
            var state = entry.Device.PeekState();
            Assert.True(state.Buttons[1]);
            Assert.Equal(40000, state.Axis[0]);
            Assert.Contains(rig.Target.Calls, c => c.Contains("Racing", StringComparison.Ordinal));

            // A button of the device it follows, and the game's rumble.
            rig.Pass(pad);
            pad.ReadInputState.Buttons[3] = true;
            rig.Pass(pad);
            Assert.StartsWith("button 3 1", run.Expect("button "));
            entry.Device.SetRumble(5, 6);
            Assert.Equal("rumble 5 6", run.Expect("rumble "));
        }

        private static UserDevice Pad(AutomationPipeRig rig)
        {
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Pad");
            pad.InputState.Buttons[5] = true;
            pad.InputState.Axis[1] = 777;
            return pad;
        }

        private const string PythonScript = """
            import sys, time
            sys.path.insert(0, sys.argv[1])
            import padforge

            def out(*parts):
                print(*parts, flush=True)

            pipe, mode = sys.argv[2], sys.argv[3]
            try:
                padforge.connect("Nobody", pipe=pipe + ".nothing", timeout=1)
            except padforge.PadForgeError as e:
                out("closed", e)
            pf = padforge.connect("Python Test", pipe=pipe, lease=1000)
            if mode == "refuse":
                try:
                    pf.devices()
                except padforge.PadForgeError as e:
                    out("refused", e.code, e)
                pf.close()
                sys.exit(0)
            out("hello", pf.level, pf.session)
            pad = pf.find("gamepad")
            out("found", pad.id, pad.type, pad.name)
            pf.on_button(pad, 3, lambda down: out("button", 3, int(down)))
            ctl = pf.add_controller("pykey", "Py Pad", buttons=["A", "B"], axes=[{"name": "X"}], feedback=["rumble"])
            ctl.on_rumble(lambda low, high: out("rumble", low, high))
            out("controller", ctl.id)
            ctl.press(1)
            ctl.set(a0=40000)
            ctl.send()
            pf.hold(pad, b2=1)
            pf.set_value("pyspeed", 12.5)
            # The reader thread waits in its read now. A request must still go
            # out and come back.
            time.sleep(0.3)
            started = time.monotonic()
            state = pf.read(pad)
            out("elapsed", int((time.monotonic() - started) * 1000))
            out("read", int(state.button(5)), state.axes[1])
            pf.switch_profile("Racing")
            out("ready")
            pf.run(30)
            """;

        [InterpreterFact("python")]
        public void ThePythonLibrary_WorksAgainstAHost()
        {
            string dir = NewFolder();
            try
            {
                AutomationLibraries.Save(AutomationLibraryKind.Python, Path.Combine(dir, "padforge.py"));
                string script = Path.Combine(dir, "test_script.py");
                File.WriteAllText(script, PythonScript);
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                using var run = new Run(Interpreter("python"), $"-I \"{script}\" \"{dir}\" {rig.PipeName} full");
                Drive(rig, pad, run, "pykey", "pyspeed", 12.5);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        [InterpreterFact("python")]
        public void ThePythonLibrary_ABlockedReadDoesNotStallAWrite()
        {
            string dir = NewFolder();
            try
            {
                AutomationLibraries.Save(AutomationLibraryKind.Python, Path.Combine(dir, "padforge.py"));
                string script = Path.Combine(dir, "test_script.py");
                File.WriteAllText(script, PythonScript);
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                Pad(rig);
                using var run = new Run(Interpreter("python"), $"-I \"{script}\" \"{dir}\" {rig.PipeName} full");
                int ms = int.Parse(run.Expect("elapsed ").Substring(8));
                Assert.InRange(ms, 0, 2000);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        [InterpreterFact("python")]
        public void ThePythonLibrary_SaysTheLevelIsTooLow()
        {
            string dir = NewFolder();
            try
            {
                AutomationLibraries.Save(AutomationLibraryKind.Python, Path.Combine(dir, "padforge.py"));
                string script = Path.Combine(dir, "test_script.py");
                File.WriteAllText(script, PythonScript);
                using var rig = new AutomationPipeRig(AutomationLevel.Commands);
                using var run = new Run(Interpreter("python"), $"-I \"{script}\" \"{dir}\" {rig.PipeName} refuse");
                string line = run.Expect("refused ");
                Assert.Contains("not-allowed", line);
                Assert.Contains("level is commands, too low for it", line);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private const string PowerShellScript = """
            param($Lib, $Pipe, $Mode)
            $ErrorActionPreference = 'Stop'
            function Out-Line([string]$text) { [Console]::Out.WriteLine($text); [Console]::Out.Flush() }
            Import-Module (Join-Path $Lib 'PadForge.psm1') -DisableNameChecking
            try { Connect-PadForge -Name 'Nobody' -Pipe ($Pipe + '.nothing') | Out-Null } catch { Out-Line ('closed ' + $_.Exception.Message) }
            $c = Connect-PadForge -Name 'PowerShell Test' -Pipe $Pipe -Lease 1000
            if ($Mode -eq 'refuse') {
                try { Get-PadForgeDevice | Out-Null } catch { Out-Line ('refused ' + $_.Exception.Message) }
                Disconnect-PadForge
                exit 0
            }
            Out-Line ("hello $($c.Level) $($c.Session)")
            $pad = Find-PadForgeDevice gamepad
            Out-Line ("found $($pad.Id) $($pad.Type) $($pad.Name)")
            Register-PadForgeButton -Device $pad -Button 3 -Action { param($down) [Console]::Out.WriteLine("button 3 $([int]$down)"); [Console]::Out.Flush() }
            $ctl = New-PadForgeController -Key 'pskey' -Name 'PS Pad' -Buttons 'A', 'B' -Axes @(@{ name = 'X' }) -Feedback rumble
            Register-PadForgeRumble -Controller $ctl -Action { param($low, $high) [Console]::Out.WriteLine("rumble $low $high"); [Console]::Out.Flush() }
            Out-Line ("controller $($ctl.Id)")
            $ctl.Press(1)
            $ctl.Set('a0', 40000)
            $ctl.Send()
            Set-PadForgeHold $pad @{ b2 = 1 }
            Set-PadForgeValue -Name psspeed -Value 3.25
            $state = Read-PadForgeDevice $pad
            Out-Line ("read $([int]$state.Buttons[5]) $($state.Axes[1])")
            Switch-PadForgeProfile -Name 'Racing' | Out-Null
            Out-Line 'ready'
            Wait-PadForge -Seconds 30
            """;

        [InterpreterFact("powershell")]
        public void ThePowerShellLibrary_WorksAgainstAHost()
        {
            string dir = NewFolder();
            try
            {
                AutomationLibraries.Save(AutomationLibraryKind.PowerShell, Path.Combine(dir, "PadForge.psm1"));
                string script = Path.Combine(dir, "test_script.ps1");
                File.WriteAllText(script, PowerShellScript, new UTF8Encoding(true));
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                using var run = new Run(Interpreter("powershell"),
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" \"{dir}\" {rig.PipeName} full");
                Drive(rig, pad, run, "pskey", "psspeed", 3.25);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        [InterpreterFact("powershell")]
        public void ThePowerShellLibrary_SaysTheLevelIsTooLow()
        {
            string dir = NewFolder();
            try
            {
                AutomationLibraries.Save(AutomationLibraryKind.PowerShell, Path.Combine(dir, "PadForge.psm1"));
                string script = Path.Combine(dir, "test_script.ps1");
                File.WriteAllText(script, PowerShellScript, new UTF8Encoding(true));
                using var rig = new AutomationPipeRig(AutomationLevel.Commands);
                using var run = new Run(Interpreter("powershell"),
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" \"{dir}\" {rig.PipeName} refuse");
                Assert.Contains("Nothing serves the pipe", run.Expect("closed "));
                Assert.Contains("level is commands, too low for it", run.Expect("refused "));
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static string AutoHotkeyScript(string library, string mode) => $$"""
            #Requires AutoHotkey v2.0
            #SingleInstance Off
            #NoTrayIcon
            #Include {{library}}
            ; One handle on the output, open for the run and shared, so the
            ; test reading it never makes a write fail. FileAppend opens the
            ; file for itself each time and is refused while another
            ; process reads it. Setting Pos writes the buffer through.
            global out := FileOpen(A_Args[1], "a", "UTF-8")
            Log(text) {
                out.Write(text "`n")
                out.Pos := out.Pos
            }
            OnError(Failed)
            Failed(e, mode) {
                Log("error " e.Message " line " e.Line)
                ExitApp(1)
                return 1
            }
            SetTimer(() => ExitApp(), -40000)
            try PadForge("Nobody", A_Args[2] ".nothing")
            catch as e
                Log("closed " e.Message)
            pf := PadForge("AutoHotkey Test", A_Args[2], 1000)
            if ("{{mode}}" = "refuse") {
                try pf.Devices()
                catch as e
                    Log("refused " e.Message)
                pf.Close()
                ExitApp(0)
            }
            Log("hello " pf.Level " " pf.Session)
            pad := pf.Find("gamepad")
            Log("found " pad.Id " " pad.Type " " pad.Name)
            pf.OnButton(pad, 3, (down) => Log("button 3 " (down ? 1 : 0) " mirror " pf.Button(pad, 3)))
            ctl := pf.AddController("ahkkey", "AHK Pad", ["A", "B"], [{name: "X"}], [], [], ["rumble"])
            ctl.OnRumble((low, high) => Log("rumble " low " " high))
            Log("controller " ctl.Id)
            ctl.Press(1)
            ctl.Set("a0", 40000)
            ctl.Send()
            pf.Hold(pad, "b2=1")
            pf.SetValue("ahkspeed", 7)
            state := pf.Read(pad)
            Log("read " state.Buttons[6] " " state.Axes[2])
            pf.SwitchProfile("Racing")
            Log("ready")
            Persistent
            """;

        [InterpreterFact("autohotkey")]
        public void TheAutoHotkeyLibrary_WorksAgainstAHost_WokenByItsWindowMessage()
        {
            string dir = NewFolder();
            try
            {
                string library = Path.Combine(dir, "PadForge.ahk");
                AutomationLibraries.Save(AutomationLibraryKind.AutoHotkey, library);
                string script = Path.Combine(dir, "test_script.ahk");
                File.WriteAllText(script, AutoHotkeyScript(library, "full"), new UTF8Encoding(true));
                string output = Path.Combine(dir, "out.txt");
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = Pad(rig);
                using var run = new Run(Interpreter("autohotkey"), $"/ErrorStdOut \"{script}\" \"{output}\" {rig.PipeName}", output);
                Drive(rig, pad, run, "ahkkey", "ahkspeed", 7);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        [InterpreterFact("autohotkey")]
        public void TheAutoHotkeyLibrary_SaysTheLevelIsTooLow()
        {
            string dir = NewFolder();
            try
            {
                string library = Path.Combine(dir, "PadForge.ahk");
                AutomationLibraries.Save(AutomationLibraryKind.AutoHotkey, library);
                string script = Path.Combine(dir, "test_script.ahk");
                File.WriteAllText(script, AutoHotkeyScript(library, "refuse"), new UTF8Encoding(true));
                string output = Path.Combine(dir, "out.txt");
                using var rig = new AutomationPipeRig(AutomationLevel.Commands);
                using var run = new Run(Interpreter("autohotkey"), $"/ErrorStdOut \"{script}\" \"{output}\" {rig.PipeName}", output);
                Assert.Contains("Nothing serves the pipe", run.Expect("closed "));
                Assert.Contains("level is commands, too low for it", run.Expect("refused "));
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TryDelete(string dir)
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
    }
}
