using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A pipe server a test scripts line by line, for what PadForge's own
    /// host cannot be made to do on cue: an answer held back, an event of a
    /// form the server writes only in another state, a line split inside a
    /// character. Every answer a test gives it is copied from the server line
    /// that writes it. The pipe is the test user's own, which the libraries
    /// take for a pipe name other than PadForge's.
    /// </summary>
    internal sealed class ScriptedHost : IDisposable
    {
        /// <summary>A line the host heard, with its tag apart.</summary>
        internal sealed record Heard(string Tag, string Line, long Ms)
        {
            public string Verb => Line.Split(' ')[0];
        }

        public readonly string PipeName = "PadForge.Tests.Scripted." + Guid.NewGuid().ToString("N");
        public readonly BlockingCollection<Heard> Lines = new();
        public readonly List<Heard> All = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly object _writeLock = new();
        private NamedPipeServerStream _current;
        private int _connections;

        /// <summary>What the host answers a line with, untagged: the rows and
        /// the final line, or null to leave it unanswered for the test. The
        /// default answers hello as AutomationSession.Hello does and every
        /// other line ok.</summary>
        public Func<Heard, string[]> Answer;

        /// <summary>The hello's answer, as AutomationSession.Hello writes
        /// it.</summary>
        public string HelloAnswer = "ok padforge=6.0.0 protocol=1 session=1 level=send-inputs units=plain";

        public int Connections => Volatile.Read(ref _connections);

        public ScriptedHost(bool start = true)
        {
            if (start) Start();
        }

        public void Start() => Task.Run(Serve);

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                // PadForge reads and writes a pipe on two tasks. This host
                // answers from its reader, so the pipe has buffers: without
                // them a write waits for the other side's read, and
                // AutoHotkey reads and writes on one thread.
                var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
                try { await server.WaitForConnectionAsync(_stop.Token); }
                catch (Exception)
                {
                    server.Dispose();
                    return;
                }
                lock (_writeLock) _current = server;
                Interlocked.Increment(ref _connections);
                await Read(server);
            }
        }

        private async Task Read(NamedPipeServerStream server)
        {
            var buffer = new byte[65536];
            var line = new List<byte>();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    int n = await server.ReadAsync(buffer, 0, buffer.Length, _stop.Token);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        if (buffer[i] != (byte)'\n')
                        {
                            line.Add(buffer[i]);
                            continue;
                        }
                        string text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                        line.Clear();
                        On(text);
                    }
                }
            }
            catch (Exception) { }
            lock (_writeLock) if (_current == server) _current = null;
            try { server.Dispose(); } catch (Exception) { }
        }

        private void On(string text)
        {
            string tag = null;
            if (text.StartsWith("@", StringComparison.Ordinal))
            {
                int space = text.IndexOf(' ');
                tag = space < 0 ? text : text.Substring(0, space);
                text = space < 0 ? string.Empty : text.Substring(space + 1);
            }
            var heard = new Heard(tag, text, _clock.ElapsedMilliseconds);
            lock (All) All.Add(heard);
            Lines.Add(heard);
            if (heard.Verb == "beat") return;
            string[] answer = Answer != null ? Answer(heard) : Default(heard);
            if (answer == null) return;
            foreach (string a in answer) Reply(tag, a);
        }

        public string[] Default(Heard heard)
            => heard.Verb == "hello" ? new[] { HelloAnswer } : heard.Verb is "bye" or "s" or "own" or "rel" or "set" ? null : new[] { "ok" };

        /// <summary>A line to the script, with the request's tag in front
        /// when it has one.</summary>
        public void Reply(string tag, string line) => Write(Encoding.UTF8.GetBytes((tag == null ? line : tag + " " + line) + "\n"));

        public void Write(byte[] bytes)
        {
            lock (_writeLock)
            {
                var server = _current;
                if (server == null) return;
                try
                {
                    server.Write(bytes, 0, bytes.Length);
                    server.Flush();
                }
                catch (IOException) { }
            }
        }

        /// <summary>Closes the script's connection, as PadForge closing
        /// does. It closes the connection the host holds, which it keeps once
        /// its accept returns, after the script's connect does: a test that
        /// drops before the host heard a line waits for
        /// <see cref="Connections"/> first.</summary>
        public void Drop()
        {
            lock (_writeLock)
            {
                try { _current?.Dispose(); } catch (Exception) { }
                _current = null;
            }
        }

        /// <summary>The next line the host hears whose verb is
        /// <paramref name="verb"/>.</summary>
        public Heard Next(string verb, int ms = 30000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
                if (Lines.TryTake(out var heard, 100) && heard.Verb == verb) return heard;
            Assert.Fail("the host heard no " + verb + " line in " + ms + " ms. Heard: "
                + string.Join(" | ", Snapshot().Select(h => h.Line)));
            return null;
        }

        public List<Heard> Snapshot()
        {
            lock (All) return All.ToList();
        }

        public void Dispose()
        {
            _stop.Cancel();
            Drop();
        }
    }

    /// <summary>
    /// The three script libraries where they once parted from PadForge's
    /// pipe or from each other: words the line splits, a rest that is not
    /// zero, a hat's rest and directions, a declared name as PadForge makes
    /// it, a refusal in PadForge's words, a level that drops and comes back,
    /// a controller removed or found again, and a waiting user. Each runs the
    /// real interpreter against PadForge's own host on a pipe of the test's
    /// own, or against a scripted pipe where the host cannot be made to do a
    /// thing on cue.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ScriptLibraryEdgeTests
    {
        // ── Runs ──

        private static ScriptRun PowerShell(string dir, string body, string pipe)
            => ScriptPowerShellTests.Start(dir, ScriptPowerShellTests.Script(dir, body), pipe);

        private static ScriptRun Python(string dir, string body, string pipe)
            => ScriptPythonTests.Start(ScriptPythonTests.Script(dir, body), pipe);

        private static ScriptRun AutoHotkey(string dir, string body, string pipe)
            => ScriptAutoHotkeyTests.Start(dir, ScriptAutoHotkeyTests.Script(dir, body), pipe);

        // ── A key and a tool's name are words of the line ──

        [InterpreterFact("powershell")]
        public void PowerShell_AControllerKeyWithASpace_IsRefused_AndNotCutAtTheSpace()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = PowerShell(dir, """
                    try { $c = Add-PadForgeController 'my pad' 'My Pad' -Buttons A; Log "result added $($c.Id)" }
                    catch { Log "result refused $($_.Exception.Message)" }
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                Assert.Equal("result refused PadForge refused declare: key is 1 to 64 letters, digits, _ . or - (bad-argument).",
                    run.Expect("result "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_AControllerKeyWithASpace_IsRefused_AndNotCutAtTheSpace()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = AutoHotkey(dir, """

                    try {
                        c := PadForge.AddController("my pad", "My Pad", {Buttons: ["A"]})
                        Log("result added " c.Id)
                    } catch as e {
                        Log("result refused " e.Message)
                    }
                    ExitApp()
                    """, rig.PipeName);
                Assert.Equal("result refused PadForge refused declare: key is 1 to 64 letters, digits, _ . or - (bad-argument).",
                    run.Expect("result "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [Fact]
        public async Task AnAssistantsAddDevice_WithAKeyOfTwoWords_IsRefused_AndNotCutAtTheSpace()
        {
            using var rig = new AssistantRig(AutomationLevel.SendInputs);
            var client = await rig.ConnectAsync();
            var added = await client.CallToolAsync("add_device", new Dictionary<string, object>
            {
                ["key"] = "my pad",
                ["name"] = "My Pad",
                ["buttons"] = new[] { "A" },
            });
            Assert.True(added.IsError == true, AssistantRig.Text(added));
            Assert.Contains("Key is 1 to 64 letters, digits, _ . or -.", AssistantRig.Text(added));
        }

        /// <summary>A host whose call runs a stub: the tool's name as PadForge
        /// read it from the line, and press the one tool it has.</summary>
        private static ConcurrentQueue<string> StubTools(AutomationPipeRig rig)
        {
            var names = new ConcurrentQueue<string>();
            rig.Host.Call = (call, ct) =>
            {
                names.Enqueue(call.Tool);
                return Task.FromResult(call.Tool == "press"
                    ? AutomationCallResult.Success("{\"pressed\":true}")
                    : AutomationCallResult.Failure("unknown-tool", "no tool has that name"));
            };
            return names;
        }

        [InterpreterFact("powershell")]
        public void PowerShell_AToolNameWithASpace_ReachesPadForgeWhole()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var names = StubTools(rig);
                using var run = PowerShell(dir, """
                    try { Log ("result " + (Invoke-PadForgeTool 'press now' '{}')) }
                    catch { Log "result refused $($_.Exception.Message)" }
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                Assert.Equal("result refused PadForge refused call: no tool has that name (unknown-tool).", run.Expect("result "));
                Assert.Equal(new[] { "press now" }, names.ToArray());
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_AToolNameWithASpace_ReachesPadForgeWhole()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var names = StubTools(rig);
                using var run = AutoHotkey(dir, """

                    try
                        Log("result " PadForge.Tool("press now", "{}"))
                    catch as e
                        Log("result refused " e.Message)
                    ExitApp()
                    """, rig.PipeName);
                Assert.Equal("result refused PadForge refused call: no tool has that name (unknown-tool).", run.Expect("result "));
                Assert.Equal(new[] { "press now" }, names.ToArray());
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── An axis's rest, and a hat's ──

        [Fact]
        public void AnAxisValue_OfTheWordRest_ReadsAsTheAxissOwnRest()
        {
            Assert.Null(PlainUnits.ReadAxis("rest", 32767, out int stick));
            Assert.Equal(32767, stick);
            Assert.Null(PlainUnits.ReadAxis("REST", 0, out int trigger));
            Assert.Equal(0, trigger);
            Assert.Null(PlainUnits.ReadAxis(" Rest ", 65535, out int pedal));
            Assert.Equal(65535, pedal);
            Assert.NotNull(PlainUnits.ReadAxis("rested", 0, out _));
            Assert.NotNull(PlainUnits.ReadAxis(null, 0, out _));

            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
            using var client = rig.Open("Rest", " units=plain lease=10000");
            Assert.Equal("ok", client.Final("own " + AutomationPipeRig.IdOf(pad) + " LeftX=rest"));
        }

        private const string RestPadPowerShell = """
            $ctl = Add-PadForgeController restpad 'Rest Pad' -Sliders @{ name = 'Pedal'; rest = 100 } -Hats DPad
            Log "id $($ctl.Id)"
            $ctl.Pedal = 30
            $ctl.DPad = 90
            Log "set $($ctl.Pedal) $($ctl.DPad)"
            Wait-PadForge -Seconds 0.5
            $ctl.Pedal = $null
            $ctl.DPad = 'rest'
            Log "rest $($ctl.Pedal) $($ctl.DPad)"
            Wait-PadForge -Seconds 30
            """;

        private const string RestPadPython = """
            ctl = padforge.add_controller("restpad", "Rest Pad", sliders=[{"name": "Pedal", "rest": 100}], hats=["DPad"])
            log("id", ctl.id)
            ctl.Pedal = 30
            ctl.DPad = 90
            log("set", ctl.Pedal, ctl.DPad)
            time.sleep(0.5)
            ctl.Pedal = None
            ctl.DPad = "rest"
            log("rest", ctl.Pedal, ctl.DPad)
            padforge.wait(30)
            """;

        private const string RestPadAutoHotkey = """

            ctl := PadForge.AddController("restpad", "Rest Pad", {Sliders: [{name: "Pedal", rest: 100}], Hats: ["DPad"]})
            Log("id " ctl.Id)
            ctl.Pedal := 30
            ctl.DPad := 90
            Log("set " ctl.Pedal " " ctl.DPad)
            Sleep(500)
            ctl.Pedal := ""
            ctl.DPad := "rest"
            Log("rest " ctl.Pedal " " ctl.DPad)
            Persistent
            """;

        /// <summary>A slider declared at rest 100 sits at the top of its
        /// travel when the script lets go of it, and a hat set to rest is at
        /// rest, both on PadForge's device and in the script's own read.</summary>
        private static async Task ControllerRests(ScriptRun run, AutomationPipeRig rig)
        {
            string id = run.Expect("id ").Substring(3);
            var entry = rig.Host.Sessions.Devices.Find(id);
            Assert.NotNull(entry);
            Assert.Equal("set 30 90", run.Expect("set "));
            await AutomationPipeRig.Until(() => entry.Device.PeekState().Povs[0] == 9000);
            Assert.Equal(PlainUnits.AxisToRaw(30, 65535), entry.Device.PeekState().Sliders[0]);
            Assert.Equal("rest 100 -1", run.Expect("rest "));
            await AutomationPipeRig.Until(() => entry.Device.PeekState().Povs[0] == -1);
            Assert.Equal(65535, entry.Device.PeekState().Sliders[0]);
        }

        [InterpreterFact("powershell")]
        public async Task PowerShell_AControllersSliderAndHat_GoToTheirOwnRests()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = PowerShell(dir, RestPadPowerShell, rig.PipeName);
                run.Expect("module ", 60000);
                await ControllerRests(run, rig);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public async Task Python_AControllersSliderAndHat_GoToTheirOwnRests()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = Python(dir, RestPadPython, rig.PipeName);
                await ControllerRests(run, rig);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public async Task AutoHotkey_AControllersSliderAndHat_GoToTheirOwnRests()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = AutoHotkey(dir, RestPadAutoHotkey, rig.PipeName);
                await ControllerRests(run, rig);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A refusal in PadForge's words ──

        /// <summary>The hello's refusal while Automation is off and the pipe
        /// serves plugins alone, as AutomationSession.Hello writes it.</summary>
        private const string PluginsAlone = "err not-allowed Automation is off, and the pipe serves plugins alone";

        private const string PluginsAloneWhy = "PadForge refused hello: Automation is off, and the pipe serves plugins alone (not-allowed).";

        private static ScriptedHost PluginsAloneHost()
        {
            var host = new ScriptedHost(start: false);
            host.Answer = h => h.Verb == "hello" ? new[] { PluginsAlone } : host.Default(h);
            host.Start();
            return host;
        }

        [InterpreterFact("powershell")]
        public void PowerShell_AHelloRefusedWhileOnlyPluginsAreServed_SaysWhyInPadForgesWords()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var host = PluginsAloneHost();
                using var run = PowerShell(dir, """
                    $c = Get-PadForgeConnection
                    Log "why $([int]$c.Connected) $($c.Why)"
                    """, host.PipeName);
                run.Expect("module ", 60000);
                Assert.Equal("why 0 " + PluginsAloneWhy, run.Expect("why "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public void Python_AHelloRefusedWhileOnlyPluginsAreServed_SaysWhyInPadForgesWords()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var host = PluginsAloneHost();
                using var run = Python(dir, """
                    log("why", int(padforge.connected), padforge.why)
                    """, host.PipeName);
                Assert.Equal("why 0 " + PluginsAloneWhy, run.Expect("why "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_AHelloRefusedWhileOnlyPluginsAreServed_SaysWhyInPadForgesWords()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = PluginsAloneHost();
                using var run = AutoHotkey(dir, """

                    Log("why " PadForge.Connected " " PadForge.Why)
                    ExitApp()
                    """, host.PipeName);
                Assert.Equal("why 0 " + PluginsAloneWhy, run.Expect("why "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        /// <summary>A line past the program's level, refused with PadForge's
        /// default words, is told as the program's level, which the user's
        /// cap or the user's answer set. Closing the connection says the
        /// script closed it.</summary>
        [InterpreterFact("autohotkey")]
        public void AutoHotkey_ALinePastTheLevel_IsToldAsTheProgramsLevel_AndACloseAsTheScripts()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.Commands);
                using var run = AutoHotkey(dir, """

                    try {
                        PadForge.Devices()
                        Log("refused nothing")
                    } catch as e {
                        Log("refused " e.Message)
                    }
                    PadForge.Default.Close()
                    Log("closed " PadForge.Connected " " PadForge.Why)
                    ExitApp()
                    """, rig.PipeName);
                Assert.Equal("refused PadForge refused devices: this program works at the Commands level, which does not allow it.", run.Expect("refused "));
                Assert.Equal("closed 0 The script closed the connection.", run.Expect("closed "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        /// <summary>PadForge closes the pipe while it holds the answer to the
        /// restore's first line: the script is not connected and says
        /// why.</summary>
        [InterpreterFact("autohotkey")]
        public void AutoHotkey_APipeClosedDuringTheRestore_LeavesTheClosedLineInWhy()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                host.Answer = h =>
                {
                    if (h.Line != "sub devices") return host.Default(h);
                    Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        host.Drop();
                    });
                    return null;
                };
                host.Start();
                using var run = AutoHotkey(dir, """

                    pf := PadForge.Default
                    Log("after " pf.Connected " " pf.Why)
                    ExitApp()
                    """, host.PipeName);
                Assert.Equal("after 0 PadForge closed the connection. The library tries again.", run.Expect("after "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A rest given as text ──

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_AnAxisRestGivenAsAPercent_IsDeclared_AndMakesAStick()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = AutoHotkey(dir, """

                    ctl := PadForge.AddController("textrest", "Text Rest", {Axes: [{name: "Steer", rest: "50%"}], Sliders: [{name: "Gas", rest: " 25 % "}]})
                    Log("declared " (ctl.Id != "") " " ctl.Steer " " ctl.Gas)
                    ExitApp()
                    """, rig.PipeName);
                Assert.Equal("declared 1 0 25", run.Expect("declared "));
                var shape = rig.Host.Sessions.Devices.All().Single(e => e.Key == "textrest").Device.Shape;
                Assert.Equal(PlainUnits.AxisToRaw(50, 0), shape.Axes.Single(a => a.Index == 0).Rest);
                Assert.Equal(PlainUnits.AxisToRaw(25, 0), shape.Axes.Single(a => a.Index == ProgramAxis.FirstSlider).Rest);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A declared name as PadForge makes it ──

        /// <summary>The names PadForge gives a declaration's controls, as
        /// <c>caps</c> says them in script=: kind, index and name, one entry
        /// each, in order.</summary>
        private static string ServerNames(AutomationPipeRig rig, string key)
        {
            var shape = rig.Host.Sessions.Devices.All().Single(e => e.Key == key).Device.Shape;
            return string.Join("|", ScriptNames.Declared(shape).Select(c => c.Kind + ":" + c.Index + ":" + c.Name)
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        /// <summary>The library's own names, as the script logs them: a line
        /// "names " and kind:index:name entries.</summary>
        private static string LibraryNames(string line)
            => string.Join("|", line.Substring(6).Split('|', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal));

        // Awkward labels: names that differ only in case, numbered and short
        // forms, words of the pipe, a touchpad's word, a hat's direction, a
        // leading digit, empty and blank names, a label past 64 characters
        // whose letters run past the 64th, one with leading spaces, a format
        // character, a run of characters outside the Basic Multilingual
        // Plane, more than 32 letters, and a name a later axis also takes.
        private const string PythonLabels = """
            buttons = ["A", "a", "Left Pedal", "left pedal", "Button 3", "b2", "Gyro", "Rest", "Hat Up", "t0", "tc1", "9lives", "", "   ",
                       "Left" + "." * 70 + "B", " " * 10 + "Alpha" + "." * 50 + "Beta", "Soft­pedal", "\U0001F600" * 31 + "Zed",
                       "abcdefghij" * 4, "Steer"]
            """;

        private static void AssertSameNames(ScriptRun run, AutomationPipeRig rig, string key)
        {
            string line = run.Expect("names ");
            Assert.Equal(ServerNames(rig, key), LibraryNames(line));
        }

        [InterpreterFact("python")]
        public void Python_AControllersNames_AreTheNamesPadForgeGivesThem()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = Python(dir, PythonLabels + "\n" + """
                    ctl = padforge.add_controller("names", "Names", buttons=buttons, axes=[{"name": "Steer"}, {"index": 5, "name": "Gas", "rest": 0}],
                                                  sliders=["Throttle", "Rest"], hats=["DPad", {"index": 3, "name": "Far"}])
                    kinds = {"button": "b", "axis": "a", "slider": "a", "hat": "h"}
                    log("names " + "|".join("%s:%d:%s" % (kinds[c.kind], c.index, c.name) for c in ctl.controls.values()))
                    """, rig.PipeName);
                AssertSameNames(run, rig, "names");
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("powershell")]
        public void PowerShell_AControllersNames_AreTheNamesPadForgeGivesThem()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = PowerShell(dir, """
                    $emoji = [char]::ConvertFromUtf32(0x1F600) * 31
                    $buttons = @('A', 'a', 'Left Pedal', 'left pedal', 'Button 3', 'b2', 'Gyro', 'Rest', 'Hat Up', 't0', 'tc1', '9lives', '', '   ',
                        ('Left' + ('.' * 70) + 'B'), ((' ' * 10) + 'Alpha' + ('.' * 50) + 'Beta'), ('Soft' + [char]0xAD + 'pedal'), ($emoji + 'Zed'),
                        ('abcdefghij' * 4), 'Steer')
                    $ctl = Add-PadForgeController names 'Names' -Buttons $buttons -Axes @{ name = 'Steer' }, @{ index = 5; name = 'Gas'; rest = 0 } `
                        -Sliders 'Throttle', 'Rest' -Hats 'DPad', @{ index = 3; name = 'Far' }
                    $kinds = @{ button = 'b'; axis = 'a'; slider = 'a'; hat = 'h' }
                    $parts = foreach ($c in $ctl.Controls.Values) { '{0}:{1}:{2}' -f $kinds[$c.Kind], $c.Index, $c.Name }
                    Log ('names ' + ($parts -join '|'))
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                AssertSameNames(run, rig, "names");
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_AControllersNames_AreTheNamesPadForgeGivesThem()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = AutoHotkey(dir, """

                    Times(text, n) {
                        out := ""
                        loop n
                            out .= text
                        return out
                    }
                    buttons := ["A", "a", "Left Pedal", "left pedal", "Button 3", "b2", "Gyro", "Rest", "Hat Up", "t0", "tc1", "9lives", "", "   "
                        , "Left" Times(".", 70) "B", Times(" ", 10) "Alpha" Times(".", 50) "Beta", "Soft" Chr(0xAD) "pedal", Times(Chr(0x1F600), 31) "Zed"
                        , Times("abcdefghij", 4), "Steer"]
                    ctl := PadForge.AddController("names", "Names", {Buttons: buttons, Axes: [{name: "Steer"}, {index: 5, name: "Gas", rest: 0}]
                        , Sliders: ["Throttle", "Rest"], Hats: ["DPad", {index: 3, name: "Far"}]})
                    kinds := Map("button", "b", "axis", "a", "slider", "a", "hat", "h")
                    parts := ""
                    for name, c in ctl.Controls
                        parts .= kinds[c.Kind] ":" c.Index ":" c.Name "|"
                    Log("names " parts)
                    ExitApp()
                    """, rig.PipeName);
                AssertSameNames(run, rig, "names");
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A slider the device's caps did not name ──

        /// <summary>A host with one pad whose caps name an axis and a slider
        /// without script= and list no second slider, rows as
        /// AutomationDevices writes them, and then an event for each
        /// slider.</summary>
        private static ScriptedHost SliderHost()
        {
            var host = new ScriptedHost(start: false);
            host.Answer = h =>
            {
                if (h.Line == "devices")
                    return new[] { "device id=sl1 type=gamepad online=1 name=\"Slider Pad\" buttons=0 axes=1 sliders=1 hats=0 gyro=0 accel=0 touchpads=0", "ok" };
                if (h.Line == "caps sl1")
                    return new[] { "axis index=0 name=\"Axis 0\" rest=32768 script=LeftX", "axis index=24 name=\"Slider 0\" rest=0", "ok" };
                if (h.Line == "sub sl1 state")
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        host.Reply(null, "e a sl1 24 40");
                        host.Reply(null, "e a sl1 25 60");
                    });
                    return new[] { "ok" };
                }
                return host.Default(h);
            };
            host.Start();
            return host;
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_ASliderTheCapsDidNotName_IsCalledSliderInItsEvents()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = SliderHost();
                using var run = AutoHotkey(dir, """

                    Log("start")
                    pad := PadForge.Device("gamepad")
                    Log("found " pad.Id " " PadForge.Default.Errors.Length)
                    pad.OnChange("Slider0", (v, n) => Log("change " n " " v))
                    pad.OnChange("Slider1", (v, n) => Log("change " n " " v))
                    Log("registered " pad.Id)
                    Persistent
                    """, host.PipeName);
                Assert.Equal("registered sl1", run.Expect("registered ", 20000));
                Assert.Equal("change Slider0 40", run.Expect("change "));
                Assert.Equal("change Slider1 60", run.Expect("change "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("powershell")]
        public void PowerShell_ASliderTheCapsDidNotName_IsCalledSliderInItsEvents()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var host = SliderHost();
                using var run = PowerShell(dir, """
                    $pad = Get-PadForgeDevice gamepad
                    $pad.OnChange('Slider0', { param($v, $n) Log "change $n $v" })
                    $pad.OnChange('Slider1', { param($v, $n) Log "change $n $v" })
                    Wait-PadForge -Seconds 30
                    """, host.PipeName);
                run.Expect("module ", 60000);
                Assert.Equal("change Slider0 40", run.Expect("change "));
                Assert.Equal("change Slider1 60", run.Expect("change "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── The pipe's short form, and a change callback made before the
        // device is found ──

        [InterpreterFact("powershell")]
        public async Task PowerShell_APadsShortForms_ReadItsControls_AndAChangeCallbackFiledEarlyRuns()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = PowerShell(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    $pad.OnChange('Button0', { param($v, $n) Log "change $n $v" })
                    Log 'waiting'
                    while (-not $pad.Connected) { Wait-PadForge -Seconds 0.02 }
                    Log "short $($pad.a0) $($pad.b0) $($pad.h0)"
                    Wait-PadForge -Seconds 30
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                run.Expect("waiting");
                await ShortFormsAndAnEarlyCallback(run, rig);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public async Task AutoHotkey_APadsShortForms_ReadItsControls()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = AutoHotkey(dir, """

                    pad := PadForge.Device("wireless")
                    pad.OnChange("Button0", (v, n) => Log("change " n " " v))
                    Log("waiting")
                    SetTimer(Look, 20)
                    Look() {
                        global pad
                        if !pad.Connected
                            return
                        SetTimer(Look, 0)
                        Log("short " pad.a0 " " (pad.b0 ? "True" : "False") " " pad.h0)
                    }
                    Persistent
                    """, rig.PipeName);
                run.Expect("waiting");
                await ShortFormsAndAnEarlyCallback(run, rig, autoHotkey: true);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        /// <summary>The pad comes after the script asked for it, with the
        /// left stick full right, A pressed and the D-pad right: a0, b0 and
        /// h0 read them, and the change callback the script filed under
        /// Button0 before the pad was found runs when A is let go.</summary>
        private static async Task ShortFormsAndAnEarlyCallback(ScriptRun run, AutomationPipeRig rig, bool autoHotkey = false)
        {
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
            pad.ReadInputState.Axis[0] = 65535;
            pad.ReadInputState.Axis[1] = 32768;
            pad.ReadInputState.Buttons[0] = true;
            pad.ReadInputState.Povs[0] = 9000;
            pad.InputState = pad.ReadInputState;
            using var loop = new PassLoop(rig, 1000, pad);
            string shortLine = run.Expect("short ", 15000);
            Assert.True(shortLine == "short 100 True 90", shortLine + Environment.NewLine + run.Seen);
            loop.Raw[0].Buttons[0] = false;
            Assert.Equal(autoHotkey ? "change A 0" : "change A False", run.Expect("change ", 10000));
            await Task.CompletedTask;
        }

        // ── A waiting user, and a level that drops and comes back ──

        /// <summary>The hello's answer while the user is asked about the
        /// program, as AutomationSession.HelloAnswer writes it.</summary>
        private const string HelloWaiting = "ok padforge=6.0.0 protocol=1 level=commands wants=send-inputs consent=waiting session=1 units=plain";

        /// <summary>A host that asks the user: the restore's sub devices is
        /// answered once the user allowed the program, after the e level the
        /// grant sends (AutomationSessions.Raise), and the devices line is
        /// never answered. Returns the times of the first two devices
        /// lines.</summary>
        private static (long First, long Second) TimesOfTwoUnansweredFinds(ScriptedHost host)
        {
            host.HelloAnswer = HelloWaiting;
            host.Answer = h =>
            {
                if (h.Line == "devices") return null;
                if (h.Line == "sub devices")
                {
                    host.Reply(null, "e level send-inputs");
                    return new[] { "ok" };
                }
                return host.Default(h);
            };
            host.Start();
            var first = host.Next("devices", 20000);
            var second = host.Next("devices", 20000);
            return (first.Ms, second.Ms);
        }

        [InterpreterFact("powershell")]
        public void PowerShell_TheUsersAnswer_EndsTheLongWait_ForTheRestoresNextLine()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                using var run = PowerShell(dir, """
                    $one = Get-PadForgeDevice 'first pad'
                    $two = Get-PadForgeDevice 'second pad'
                    Log 'ready'
                    Wait-PadForge -Seconds 60
                    """, host.PipeName);
                run.Expect("module ", 60000);
                run.Expect("ready");
                var (first, second) = TimesOfTwoUnansweredFinds(host);
                Assert.InRange(second - first, 9000, 13000);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_TheUsersAnswer_EndsTheLongWait_ForTheRestoresNextLine()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                using var run = AutoHotkey(dir, """

                    one := PadForge.Device("first pad")
                    two := PadForge.Device("second pad")
                    Log("ready")
                    Persistent
                    """, host.PipeName);
                run.Expect("ready");
                var (first, second) = TimesOfTwoUnansweredFinds(host);
                Assert.InRange(second - first, 9000, 13000);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        /// <summary>The user drops Automation's level to Commands and raises
        /// it again: PadForge let go of what the script followed and the
        /// controller it added, and the script follows its pad and drives its
        /// controller again.</summary>
        private static async Task FollowsAgainAfterTheLevelComesBack(ScriptRun run, AutomationPipeRig rig, string dir)
        {
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
            using var loop = new PassLoop(rig, 1000, pad);
            run.Expect("ready", 20000);
            rig.Host.Apply(true, AutomationLevel.Commands);
            run.Expect("level commands", 10000);
            await Task.Delay(300);
            rig.Host.Apply(true, AutomationLevel.SendInputs);
            run.Expect("level send-inputs", 10000);
            await Task.Delay(1000);
            loop.Raw[0].Buttons[0] = true;
            run.Expect("a pressed", 10000);
            await ScriptAutoHotkeyTests.Eventually(() =>
            {
                var entry = rig.Host.Sessions.Devices.All().SingleOrDefault(e => e.Key == "lvl");
                return entry != null && entry.Device.PeekState().Buttons[0];
            }, dir, run, 10000);
        }

        [InterpreterFact("powershell")]
        public async Task PowerShell_ALevelThatDropsAndComesBack_FollowsThePadAndDrivesTheController()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = PowerShell(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    $ctl = Add-PadForgeController lvl 'Level Pad' -Buttons Fire
                    Register-PadForgeLevel { param($l) Log "level $l" }
                    while (-not $pad.Connected) { Wait-PadForge -Seconds 0.02 }
                    Log 'ready'
                    while ($true) {
                        Wait-PadForge -Seconds 0.02
                        if ($pad.A) { Log 'a pressed'; $ctl.Fire = $true; break }
                    }
                    Wait-PadForge -Seconds 30
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                await FollowsAgainAfterTheLevelComesBack(run, rig, dir);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public async Task Python_ALevelThatDropsAndComesBack_FollowsThePadAndDrivesTheController()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = Python(dir, """
                    pad = padforge.device("wireless")
                    ctl = padforge.add_controller("lvl", "Level Pad", buttons=["Fire"])
                    padforge.on_level(lambda l: log("level", l))
                    while not pad.connected:
                        time.sleep(0.02)
                    log("ready")
                    while not pad.A:
                        time.sleep(0.02)
                    log("a pressed")
                    ctl.Fire = True
                    padforge.wait(30)
                    """, rig.PipeName);
                await FollowsAgainAfterTheLevelComesBack(run, rig, dir);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public async Task AutoHotkey_ALevelThatDropsAndComesBack_FollowsThePadAndDrivesTheController()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = AutoHotkey(dir, """

                    pad := PadForge.Device("wireless")
                    ctl := PadForge.AddController("lvl", "Level Pad", {Buttons: ["Fire"]})
                    PadForge.OnLevel((l) => Log("level " l))
                    ready := false
                    SetTimer(Look, 20)
                    Look() {
                        global pad, ctl, ready
                        if !pad.Connected
                            return
                        if !ready {
                            ready := true
                            Log("ready")
                        }
                        if pad.A {
                            SetTimer(Look, 0)
                            Log("a pressed")
                            ctl.Fire := true
                        }
                    }
                    Persistent
                    """, rig.PipeName);
                await FollowsAgainAfterTheLevelComesBack(run, rig, dir);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A restore step refused, a controller's connection through two
        // restarts, and a controller removed ──

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_ARestoreStepPadForgeRefuses_LeavesTheNextStepsToRun()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                host.HelloAnswer = "ok padforge=6.0.0 protocol=1 level=read session=1 units=plain";
                host.Answer = h =>
                {
                    if (h.Verb == "declare") return new[] { "err not-allowed the Automation level does not allow this" };
                    if (h.Line == "devices")
                        return new[] { "device id=rd1 type=gamepad online=1 name=\"Read Pad\" buttons=1 axes=0 sliders=0 hats=0 gyro=0 accel=0 touchpads=0", "ok" };
                    if (h.Line == "caps rd1") return new[] { "button index=0 name=\"Button 0\" script=A", "ok" };
                    if (h.Line == "sub rd1 state")
                    {
                        Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            host.Reply(null, "e state rd1 b=01 a=0 h=-1");
                        });
                        return new[] { "ok" };
                    }
                    return host.Default(h);
                };
                using var run = AutoHotkey(dir, """

                    ctl := PadForge.AddController("refused", "Refused", {Buttons: ["Fire"]})
                    pad := PadForge.Device("gamepad")
                    Log("ready")
                    SetTimer(Look, 50)
                    Look() {
                        global pad
                        if !pad.Connected
                            return
                        SetTimer(Look, 0)
                        Log("found " pad.Id " " pad.A " " PadForge.Default.Errors.Length)
                    }
                    Persistent
                    """, host.PipeName);
                run.Expect("ready");
                host.Start();
                Assert.Equal("found rd1 1 1", run.Expect("found ", 15000));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        /// <summary>PadForge restarts twice: the controller's OnConnect hears
        /// each loss and each return.</summary>
        private static async Task TwoRestarts(string pipe, Func<ScriptRun> start)
        {
            ScriptRun run;
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                run = start();
                run.Expect("ready", 60000);
                await Task.Delay(300);
            }
            using (run)
            {
                Assert.Equal("connect False", run.Expect("connect ", 10000));
                using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
                {
                    Assert.Equal("connect True", run.Expect("connect ", 10000));
                    await Task.Delay(300);
                }
                Assert.Equal("connect False", run.Expect("connect ", 10000));
                using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
                    Assert.Equal("connect True", run.Expect("connect ", 10000));
            }
        }

        [InterpreterFact("powershell")]
        public async Task PowerShell_AControllersOnConnect_HearsEveryLossAndReturn_ThroughTwoRestarts()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                await TwoRestarts(pipe, () => PowerShell(dir, """
                    $ctl = Add-PadForgeController twice 'Twice' -Buttons Fire
                    $ctl.OnConnect({ param($c) Log "connect $c" })
                    Log 'ready'
                    while ($true) { Wait-PadForge -Seconds 0.05 }
                    """, pipe));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public async Task AutoHotkey_AControllersOnConnect_HearsEveryLossAndReturn_ThroughTwoRestarts()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                await TwoRestarts(pipe, () => AutoHotkey(dir, """

                    ctl := PadForge.AddController("twice", "Twice", {Buttons: ["Fire"]})
                    ctl.OnConnect((c) => Log("connect " (c ? "True" : "False")))
                    Log("ready")
                    Persistent
                    """, pipe));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        /// <summary>A removed controller is not connected, hears its loss, and
        /// a later set sends nothing PadForge would refuse.</summary>
        private static void Removed(ScriptRun run)
        {
            Assert.Equal("before True", run.Expect("before "));
            Assert.Equal("connect False", run.Expect("connect "));
            Assert.Equal("after False 0", run.Expect("after "));
        }

        [InterpreterFact("powershell")]
        public void PowerShell_ARemovedController_IsNotConnected_AndSendsNothing()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = PowerShell(dir, """
                    $ctl = Add-PadForgeController gone 'Gone' -Buttons Fire
                    $ctl.OnConnect({ param($c) Log "connect $c" })
                    Log "before $($ctl.Connected)"
                    $ctl.Remove()
                    Wait-PadForge -Seconds 0.2
                    $ctl.Fire = $true
                    Wait-PadForge -Seconds 0.5
                    Log "after $($ctl.Connected) $((Get-PadForgeConnection).Errors.Count)"
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                Removed(run);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public void Python_ARemovedController_IsNotConnected_AndSendsNothing()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = Python(dir, """
                    ctl = padforge.add_controller("gone", "Gone", buttons=["Fire"])
                    ctl.on_connect(lambda c: log("connect", c))
                    log("before", ctl.connected)
                    ctl.remove()
                    time.sleep(0.2)
                    ctl.Fire = True
                    time.sleep(0.5)
                    log("after", ctl.connected, len(padforge.errors))
                    """, rig.PipeName);
                Removed(run);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_ARemovedController_IsNotConnected_AndSendsNothing()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = AutoHotkey(dir, """

                    ctl := PadForge.AddController("gone", "Gone", {Buttons: ["Fire"]})
                    ctl.OnConnect((c) => Log("connect " (c ? "True" : "False")))
                    Log("before " (ctl.Connected ? "True" : "False"))
                    ctl.Remove()
                    Sleep(200)
                    ctl.Fire := true
                    Sleep(500)
                    PadForge.Default._Drain()
                    Log("after " (ctl.Connected ? "True" : "False") " " PadForge.Default.Errors.Length)
                    ExitApp()
                    """, rig.PipeName);
                Removed(run);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A lease event that is not a lapse ──

        /// <summary>A host that hears the script's controller, then tells it
        /// e lease renewed, a word the server does not write today, and then
        /// e lease lapsed as AutomationSessions writes it: only the lapse
        /// sends what the script set again.</summary>
        private static void OnlyALapseSendsAgain(ScriptedHost host)
        {
            host.Answer = h => h.Verb == "declare" ? new[] { "ok id=ls1" } : host.Default(h);
            host.Start();
            host.Next("declare", 20000);
            var set = host.Next("s", 10000);
            Assert.Equal("s ls1 Fire=1", set.Line);
            host.Reply(null, "e lease renewed");
            Thread.Sleep(1000);
            Assert.DoesNotContain(host.Snapshot(), h => h.Verb == "s" && h.Ms > set.Ms);
            host.Reply(null, "e lease lapsed");
            Assert.Equal("s ls1 Fire=1", host.Next("s", 5000).Line);
        }

        [InterpreterFact("python")]
        public void Python_OnlyALapsedLease_SendsTheControllerAgain()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                using var run = Python(dir, """
                    ctl = padforge.add_controller("lease", "Lease", buttons=["Fire"])
                    while not ctl.connected:
                        time.sleep(0.02)
                    ctl.Fire = True
                    padforge.wait(30)
                    """, host.PipeName);
                OnlyALapseSendsAgain(host);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_OnlyALapsedLease_SendsTheControllerAgain()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                using var run = AutoHotkey(dir, """

                    ctl := PadForge.AddController("lease", "Lease", {Buttons: ["Fire"]})
                    SetTimer(Press, 20)
                    Press() {
                        global ctl
                        if !ctl.Connected
                            return
                        SetTimer(Press, 0)
                        ctl.Fire := true
                    }
                    Persistent
                    """, host.PipeName);
                OnlyALapseSendsAgain(host);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A controller's own reads: a hat set by its directions, and an
        // axis's halves ──

        private static void ControllerReads(ScriptRun run)
        {
            Assert.Equal("hat 45 number", run.Expect("hat "));
            Assert.Equal("halves 80 0", run.Expect("halves "));
            Assert.Equal("halves 0 80", run.Expect("halves "));
        }

        [InterpreterFact("powershell")]
        public void PowerShell_AControllersHatAndHalves_ReadAsADevicesDo()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = PowerShell(dir, """
                    $ctl = Add-PadForgeController reads 'Reads' -Axes LeftX -Hats DPad
                    $ctl.DPad = 'Up+Right'
                    Log "hat $($ctl.DPad) $(if ($ctl.DPad -is [double]) { 'number' } else { $ctl.DPad.GetType().Name })"
                    $ctl.LeftX = 80
                    Log "halves $($ctl.LeftXPlus) $($ctl.LeftXMinus)"
                    $ctl.LeftX = -80
                    Log "halves $($ctl.LeftXPlus) $($ctl.LeftXMinus)"
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                ControllerReads(run);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public void Python_AControllersHatAndHalves_ReadAsADevicesDo()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = Python(dir, """
                    ctl = padforge.add_controller("reads", "Reads", axes=["LeftX"], hats=["DPad"])
                    ctl.DPad = "Up+Right"
                    log("hat", ctl.DPad, "number" if isinstance(ctl.DPad, (int, float)) else type(ctl.DPad).__name__)
                    ctl.LeftX = 80
                    log("halves", ctl.LeftXPlus, ctl.LeftXMinus)
                    ctl.LeftX = -80
                    log("halves", ctl.LeftXPlus, ctl.LeftXMinus)
                    """, rig.PipeName);
                ControllerReads(run);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_AControllersHatAndHalves_ReadAsADevicesDo()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = AutoHotkey(dir, """

                    ctl := PadForge.AddController("reads", "Reads", {Axes: ["LeftX"], Hats: ["DPad"]})
                    ctl.DPad := "Up+Right"
                    Log("hat " ctl.DPad " " (ctl.DPad is Number ? "number" : Type(ctl.DPad)))
                    ctl.LeftX := 80
                    Log("halves " ctl.LeftXPlus " " ctl.LeftXMinus)
                    ctl.LeftX := -80
                    Log("halves " ctl.LeftXPlus " " ctl.LeftXMinus)
                    ExitApp()
                    """, rig.PipeName);
                ControllerReads(run);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A claim refused at Read ──

        /// <summary>At Read a claim is refused as any line past the level is
        /// (<c>err not-allowed</c>), and each library words it by the level
        /// the program works at: the hotkey that leaves the axis to the game
        /// still fires, and the refused ones never do.</summary>
        private static async Task ClaimsRefusedAtRead(Func<AutomationPipeRig, ScriptRun> start)
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
            pad.ReadInputState.Axis[0] = 32768;
            pad.InputState = pad.ReadInputState;
            using var loop = new PassLoop(rig, 1000, pad);
            using var run = start(rig);
            Assert.Equal("refused PadForge refused sub: this program works at the Read level, which does not allow it.", run.Expect("refused ", 60000));
            Assert.Equal("refused PadForge refused sub: this program works at the Read level, which does not allow it.", run.Expect("refused "));
            run.Expect("ready");
            // A new line starts from the pass after it is followed, which
            // reads where the axis is: the axis moves after that pass.
            await Task.Delay(500);
            loop.Raw[0].Axis[0] = 65535;
            loop.Raw[0].Buttons[0] = true;
            Assert.Equal("hk pass", run.Expect("hk ", 10000));
            await Task.Delay(1500);
            Assert.DoesNotContain(run.Drain(), l => l.StartsWith("hk ", StringComparison.Ordinal));
        }

        [InterpreterFact("powershell")]
        public async Task PowerShell_AClaimRefusedAtRead_LeavesTheHotkeyBeforeIt()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                await ClaimsRefusedAtRead(rig => PowerShell(dir, """
                    Connect-PadForge -Level read | Out-Null
                    $pad = Get-PadForgeDevice 'wireless'
                    while (-not $pad.Connected) { Wait-PadForge -Seconds 0.02 }
                    $pad.Hotkey('~LeftX > 80%', { Log 'hk pass' })
                    try { $pad.Hotkey('LeftX > 80%', { Log 'hk claim' }) } catch { Log "refused $($_.Exception.Message)" }
                    try { $pad.Hotkey('A', { Log 'hk a' }) } catch { Log "refused $($_.Exception.Message)" }
                    Log 'ready'
                    Wait-PadForge -Seconds 30
                    """, rig.PipeName));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public async Task Python_AClaimRefusedAtRead_LeavesTheHotkeyBeforeIt()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                await ClaimsRefusedAtRead(rig => Python(dir, """
                    padforge.connect(level="read")
                    pad = padforge.device("wireless")
                    while not pad.connected:
                        time.sleep(0.02)
                    pad.hotkey("~LeftX > 80%", lambda: log("hk pass"))
                    def saying(words):
                        return lambda: log(words)
                    for name, said in (("LeftX > 80%", "hk claim"), ("A", "hk a")):
                        try:
                            pad.hotkey(name, saying(said))
                        except padforge.PadForgeError as e:
                            log("refused", e)
                    log("ready")
                    padforge.wait(30)
                    """, rig.PipeName));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public async Task AutoHotkey_AClaimRefusedAtRead_LeavesTheHotkeyBeforeIt()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                await ClaimsRefusedAtRead(rig => AutoHotkey(dir, """

                    PadForge.Connect("", "read")
                    pad := PadForge.Device("wireless")
                    while !pad.Connected
                        Sleep(20)
                    pad.Hotkey("~LeftX > 80%", (*) => Log("hk pass"))
                    try pad.Hotkey("LeftX > 80%", (*) => Log("hk claim"))
                    catch as e
                        Log("refused " e.Message)
                    try pad.Hotkey("A", (*) => Log("hk a"))
                    catch as e
                        Log("refused " e.Message)
                    Log("ready")
                    Persistent
                    """, rig.PipeName));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A pad that waits on an unplugged device ──

        /// <summary>The only gamepad PadForge knows is unplugged when the
        /// script asks for one, and then another gamepad connects: the pad
        /// follows the one that is connected.</summary>
        private static void FollowsTheGamepadThatConnects(Func<AutomationPipeRig, ScriptRun> start)
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var unplugged = rig.AddDevice(InputDeviceType.Gamepad, "Old Controller", online: false);
            using var run = start(rig);
            Assert.Equal("first " + AutomationPipeRig.IdOf(unplugged) + " False", run.Expect("first ", 60000));
            var plugged = rig.AddDevice(InputDeviceType.Gamepad, "New Controller");
            using var loop = new PassLoop(rig, 1000, plugged);
            Assert.Equal("found " + AutomationPipeRig.IdOf(plugged), run.Expect("found ", 10000));
        }

        [InterpreterFact("powershell")]
        public void PowerShell_APadWaitingOnAnUnpluggedDevice_FollowsTheGamepadThatConnects()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                FollowsTheGamepadThatConnects(rig => PowerShell(dir, """
                    $pad = Get-PadForgeDevice gamepad
                    Log "first $($pad.Id) $($pad.Connected)"
                    while (-not $pad.Connected) { Wait-PadForge -Seconds 0.02 }
                    Log "found $($pad.Id)"
                    """, rig.PipeName));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public void Python_APadWaitingOnAnUnpluggedDevice_FollowsTheGamepadThatConnects()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                FollowsTheGamepadThatConnects(rig => Python(dir, """
                    pad = padforge.device("gamepad")
                    log("first", pad.id, pad.connected)
                    while not pad.connected:
                        time.sleep(0.02)
                    log("found", pad.id)
                    """, rig.PipeName));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_APadWaitingOnAnUnpluggedDevice_FollowsTheGamepadThatConnects()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                FollowsTheGamepadThatConnects(rig => AutoHotkey(dir, """

                    pad := PadForge.Device("gamepad")
                    Log("first " pad.Id " " (pad.Connected ? "True" : "False"))
                    SetTimer(Look, 20)
                    Look() {
                        global pad
                        if !pad.Connected
                            return
                        SetTimer(Look, 0)
                        Log("found " pad.Id)
                    }
                    Persistent
                    """, rig.PipeName));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A sequence after a tap ──

        /// <summary>A tap of B for 500 ms, then at once a sequence of A and X
        /// for 50 ms each: A is held its 50 ms on PadForge's clock, whatever
        /// the tap before it.</summary>
        private static void EachStepHeldItsMs(Func<AutomationPipeRig, ScriptRun> start)
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
            using var loop = new PassLoop(rig, 1000, pad);
            long aOn = -1, aOff = -1;
            loop.Hook = pass =>
            {
                var state = pad.InputState;
                if (state == null) return;
                if (state.Buttons[0] && aOn < 0) aOn = pass;
                else if (!state.Buttons[0] && aOn >= 0 && aOff < 0) aOff = pass;
            };
            using var run = start(rig);
            run.Expect("sent", 60000);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref aOff) < 0 && clock.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            loop.Hook = null;
            Assert.True(aOff > 0, "A was never let go");
            // 50 passes at 1,000 a second, with room for a loaded PC, whose
            // pass thread can stall and catch up: a tap before the sequence
            // stretched A to about 550.
            Assert.InRange(aOff - aOn, 25, 200);
        }

        [InterpreterFact("powershell")]
        public void PowerShell_ASequenceAfterATap_HoldsEachStepItsMs()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                EachStepHeldItsMs(rig => PowerShell(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    while (-not $pad.Connected) { Wait-PadForge -Seconds 0.02 }
                    $pad.Tap('B', 500)
                    $pad.Sequence('A', 50, 'X', 50)
                    Log 'sent'
                    Wait-PadForge -Seconds 30
                    """, rig.PipeName));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public void Python_ASequenceAfterATap_HoldsEachStepItsMs()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                EachStepHeldItsMs(rig => Python(dir, """
                    pad = padforge.device("wireless")
                    while not pad.connected:
                        time.sleep(0.02)
                    pad.tap("B", 500)
                    pad.sequence("A", 50, "X", 50)
                    log("sent")
                    padforge.wait(30)
                    """, rig.PipeName));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_ASequenceAfterATap_HoldsEachStepItsMs()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                EachStepHeldItsMs(rig => AutoHotkey(dir, """

                    pad := PadForge.Device("wireless")
                    while !pad.Connected
                        Sleep(20)
                    pad.Tap("B", 500)
                    pad.Sequence("A", 50, "X", 50)
                    Log("sent")
                    Persistent
                    """, rig.PipeName));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── Pairs with one half missing ──

        private static async Task OddPairsSendNothing(ScriptRun run, UserDevice pad)
        {
            Assert.Equal("odd sequence takes names and times in pairs.", run.Expect("odd ", 60000));
            Assert.Equal("odd hold takes names and values in pairs.", run.Expect("odd "));
            run.Expect("done");
            await Task.Delay(300);
            Assert.False(pad.InputState.Buttons[0]);
            Assert.False(pad.InputState.Buttons[1]);
            Assert.Equal(32768, pad.InputState.Axis[0]);
        }

        [InterpreterFact("powershell")]
        public async Task PowerShell_ASequenceOrHoldWithAnOddCount_ThrowsAndSendsNothing()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
                pad.ReadInputState.Axis[0] = 32768;
                pad.ReadInputState.Axis[1] = 32768;
                pad.InputState = pad.ReadInputState;
                using var loop = new PassLoop(rig, 1000, pad);
                using var run = PowerShell(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    while (-not $pad.Connected) { Wait-PadForge -Seconds 0.02 }
                    try { $pad.Sequence('A', 50, 'B'); Log 'odd nothing' } catch { Log "odd $($_.Exception.Message)" }
                    try { $pad.Hold('LeftX', 50, 'LeftY'); Log 'odd nothing' } catch { Log "odd $($_.Exception.Message)" }
                    Log 'done'
                    Wait-PadForge -Seconds 30
                    """, rig.PipeName);
                await OddPairsSendNothing(run, pad);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public async Task AutoHotkey_ASequenceOrHoldWithAnOddCount_ThrowsAndSendsNothing()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
                pad.ReadInputState.Axis[0] = 32768;
                pad.ReadInputState.Axis[1] = 32768;
                pad.InputState = pad.ReadInputState;
                using var loop = new PassLoop(rig, 1000, pad);
                using var run = AutoHotkey(dir, """

                    pad := PadForge.Device("wireless")
                    while !pad.Connected
                        Sleep(20)
                    try {
                        pad.Sequence("A", 50, "B")
                        Log("odd nothing")
                    } catch as e {
                        Log("odd " e.Message)
                    }
                    try {
                        pad.Hold("LeftX", 50, "LeftY")
                        Log("odd nothing")
                    } catch as e {
                        Log("odd " e.Message)
                    }
                    Log("done")
                    Persistent
                    """, rig.PipeName);
                await OddPairsSendNothing(run, pad);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── Commands only, and an axis given as a percent ──

        [InterpreterFact("powershell")]
        public async Task PowerShell_ConnectsAtCommands_AndTakesAnAxisAsAPercent()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
                using var loop = new PassLoop(rig, 1000, pad);
                using var run = PowerShell(dir, """
                    try { $c = Connect-PadForge -Level commands; Log "level $($c.Level)" } catch { Log "level refused $($_.Exception.Message)" }
                    Disconnect-PadForge
                    $pad = Get-PadForgeDevice 'wireless'
                    while (-not $pad.Connected) { Wait-PadForge -Seconds 0.02 }
                    try { $pad.LeftX = '50%'; Log 'axis held' } catch { Log "axis $($_.Exception.Message)" }
                    Wait-PadForge -Seconds 30
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                Assert.Equal("level commands", run.Expect("level "));
                Assert.Equal("axis held", run.Expect("axis "));
                await AutomationPipeRig.Until(() => pad.InputState.Axis[0] == PlainUnits.AxisToRaw(50, 32768));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public async Task AutoHotkey_TakesAnAxisAsAPercent()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
                using var loop = new PassLoop(rig, 1000, pad);
                using var run = AutoHotkey(dir, """

                    pad := PadForge.Device("wireless")
                    while !pad.Connected
                        Sleep(20)
                    pad.LeftX := " 50% "
                    Log("axis held")
                    Persistent
                    """, rig.PipeName);
                run.Expect("axis held", 20000);
                await AutomationPipeRig.Until(() => pad.InputState.Axis[0] == PlainUnits.AxisToRaw(50, 32768));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── Errors kept to the last 100 ──

        /// <summary>500 untagged refusals, as PadForge answers lines a script
        /// sends without a tag (AutomationLine.Error), the last of another
        /// kind.</summary>
        private static void FiveHundredRefusals(ScriptedHost host)
        {
            host.Start();
            host.Next("sub", 20000);
            Thread.Sleep(500);
            for (int i = 0; i < 499; i++) host.Reply(null, "err not-owner the device belongs to another program");
            host.Reply(null, "err rate-limited too many lines a second");
        }

        [InterpreterFact("python")]
        public void Python_ErrorsKeepTheLast100()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                using var run = Python(dir, """
                    while not padforge.connected:
                        time.sleep(0.02)
                    while not padforge.errors or "rate-limited" not in padforge.errors[-1]:
                        time.sleep(0.02)
                    log("errors", len(padforge.errors), padforge.errors[-1])
                    """, host.PipeName);
                FiveHundredRefusals(host);
                Assert.Equal("errors 100 err rate-limited too many lines a second", run.Expect("errors "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_ErrorsKeepTheLast100()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                using var run = AutoHotkey(dir, """

                    SetTimer(Look, 20)
                    Look() {
                        errors := PadForge.Default.Errors
                        if (errors.Length = 0 || !InStr(errors[errors.Length], "rate-limited"))
                            return
                        SetTimer(Look, 0)
                        Log("errors " errors.Length " " errors[errors.Length])
                    }
                    Persistent
                    """, host.PipeName);
                FiveHundredRefusals(host);
                Assert.Equal("errors 100 err rate-limited too many lines a second", run.Expect("errors "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        /// <summary>At Read a line on an axis is followed, and a claim of the
        /// same line is refused without taking the first one away.</summary>
        [Fact]
        public void AtRead_ALineOnAnAxis_IsHeard_AndARefusedClaimLeavesIt()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
            pad.ReadInputState.Axis[0] = 32768;
            pad.InputState = pad.ReadInputState;
            using var loop = new PassLoop(rig, 1000, pad);
            using var client = rig.Open("Lines", " units=plain lease=10000");
            string id = AutomationPipeRig.IdOf(pad);
            string line = client.Final("sub " + id + " LeftX above=80%");
            Assert.StartsWith("ok line=", line);
            Assert.StartsWith("err not-allowed ", client.Final("sub " + id + " LeftX above=80% claim"));
            Thread.Sleep(200);
            client.Drain();
            loop.Raw[0].Axis[0] = 65535;
            string seen = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 3000)
            {
                string e = client.Event(200);
                if (e != null && e.StartsWith("e x ", StringComparison.Ordinal)) { seen = e; break; }
            }
            Assert.Equal("e x " + id + " 0 1 " + line.Substring(8), seen);
        }

        // ── A bad extra, a replayed light, a bad hold, a write on a closed
        // pipe ──

        [InterpreterFact("python")]
        public void Python_ABadGyroValue_IsRefused_AndWhatTheScriptSetComesBackAfterARestart()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                host.Answer = h => h.Verb == "declare" ? new[] { "ok id=gy1" } : host.Default(h);
                using var run = Python(dir, """
                    ctl = padforge.add_controller("gyro", "Gyro", buttons=["A"], gyro=True)
                    while not ctl.connected:
                        time.sleep(0.02)
                    try:
                        ctl.gyro = (1, 2)
                        log("bad nothing")
                    except Exception as e:
                        log("bad", type(e).__name__)
                    ctl.A = True
                    log("set")
                    padforge.wait(30)
                    """, host.PipeName);
                host.Start();
                Assert.Equal("bad ValueError", run.Expect("bad ", 20000));
                run.Expect("set");
                Assert.Equal("s gy1 A=1", host.Next("s", 5000).Line);
                host.Drop();
                host.Next("declare", 10000);
                Assert.Equal("s gy1 A=1", host.Next("s", 5000).Line);
                Assert.DoesNotContain("error ", run.Seen);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public void Python_ALightReplayedWithTheDeclaresAnswer_ReachesItsHandler()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                // Declare answers, then replays what the device was last
                // told, as AutomationSessions.Declare does for a program
                // that comes back.
                host.Answer = h =>
                {
                    if (h.Verb != "declare") return host.Default(h);
                    host.Reply(h.Tag, "ok id=lt1");
                    host.Reply(null, "e led lt1 ff8000");
                    return null;
                };
                using var run = Python(dir, """
                    # The declare's answer is taken a moment late, as a busy
                    # thread takes it.
                    _row = padforge.Controller._row
                    def _late_row(self, p):
                        time.sleep(0.3)
                        _row(self, p)
                    padforge.Controller._row = _late_row
                    ctl = padforge.add_controller("lit", "Lit", buttons=["A"], feedback=["light"])
                    ctl.on_light(lambda rgb: log("light", rgb))
                    log("ready")
                    padforge.wait(30)
                    """, host.PipeName);
                run.Expect("ready", 20000);
                host.Start();
                Assert.Equal("light ff8000", run.Expect("light ", 10000));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        /// <summary>A hold the wire cannot take and a good Set, both made
        /// before the pad is found: the Set reaches the pad, and a restart of
        /// PadForge finds the script connected again.</summary>
        private static async Task ABadHoldDoesNotStopTheRest(string pipe, string dir, Func<ScriptRun> start)
        {
            ScriptRun run;
            using (var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe))
            {
                using var empty = new PassLoop(rig, 1000);
                run = start();
                run.Expect("waiting", 60000);
                empty.Dispose();
                var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
                using var loop = new PassLoop(rig, 1000, pad);
                run.Expect("found", 15000);
                await ScriptAutoHotkeyTests.Eventually(() => pad.InputState.Buttons[0], dir, run);
            }
            using (run)
            {
                run.Expect("gone", 15000);
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs, pipeName: pipe);
                var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
                using var loop = new PassLoop(rig, 1000, pad);
                run.Expect("back", 15000);
            }
        }

        [InterpreterFact("powershell")]
        public async Task PowerShell_ABadHoldMadeBeforeThePadIsFound_LeavesTheSetToBeSent()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                await ABadHoldDoesNotStopTheRest(pipe, dir, () => PowerShell(dir, """
                    $pad = Get-PadForgeDevice 'wireless'
                    $pf = Get-PadForgeConnection
                    $pad.LeftX = 'abc'
                    $pad.Set('A', $true)
                    Log 'waiting'
                    while (-not $pad.Connected) { Wait-PadForge -Seconds 0.02 }
                    Log 'found'
                    while ($pf.Connected) { Wait-PadForge -Seconds 0.02 }
                    Log 'gone'
                    while (-not $pf.Connected) { Wait-PadForge -Seconds 0.02 }
                    Log 'back'
                    Wait-PadForge -Seconds 30
                    """, pipe));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public async Task Python_ABadHoldMadeBeforeThePadIsFound_LeavesTheSetToBeSent_AndTheScriptReconnects()
        {
            string dir = ScriptPythonTests.NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                await ABadHoldDoesNotStopTheRest(pipe, dir, () => Python(dir, """
                    pad = padforge.device("wireless")
                    pad.LeftX = [10]
                    pad.set("A", True)
                    log("waiting")
                    while not pad.connected:
                        time.sleep(0.02)
                    log("found")
                    while padforge.connected:
                        time.sleep(0.02)
                    log("gone")
                    while not padforge.connected:
                        time.sleep(0.02)
                    log("back")
                    padforge.wait(30)
                    """, pipe));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public async Task AutoHotkey_ABadHoldMadeBeforeThePadIsFound_LeavesTheSetToBeSent()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            string pipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            try
            {
                await ABadHoldDoesNotStopTheRest(pipe, dir, () => AutoHotkey(dir, """

                    pad := PadForge.Device("wireless")
                    pad.LeftX := [10]
                    pad.Set("A", true)
                    Log("waiting")
                    stage := 0
                    SetTimer(Look, 20)
                    Look() {
                        global pad, stage
                        if (stage = 0 && pad.Connected)
                            stage := 1, Log("found")
                        else if (stage = 1 && !PadForge.Connected)
                            stage := 2, Log("gone")
                        else if (stage = 2 && PadForge.Connected)
                            stage := 3, Log("back")
                    }
                    Persistent
                    """, pipe));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public void Python_AWriteOnAPipePadForgeClosed_IsTheLibrarysClosedError()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var host = new ScriptedHost();
                using var run = Python(dir, """
                    pipe = padforge._Pipe(os.environ["PADFORGE_PIPE"], 2.0)
                    log("open")
                    # The test says when the host has closed its end.
                    flag = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dropped")
                    while not os.path.exists(flag):
                        time.sleep(0.02)
                    try:
                        pipe.write(b"beat\n")
                        log("write nothing")
                    except padforge.PadForgeError as e:
                        log("write", e.code)
                    except OSError as e:
                        log("write OSError", e.winerror)
                    """, host.PipeName);
                run.Expect("open", 20000);
                // The script logs open once its connect returns, and the
                // host keeps the connection only when its accept's
                // continuation runs: a drop before then closes nothing.
                var held = System.Diagnostics.Stopwatch.StartNew();
                while (host.Connections == 0 && held.ElapsedMilliseconds < 10000) Thread.Sleep(10);
                Assert.Equal(1, host.Connections);
                host.Drop();
                Thread.Sleep(200);
                File.WriteAllText(Path.Combine(dir, "dropped"), string.Empty);
                Assert.Equal("write closed", run.Expect("write "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── AutoHotkey: a button from any number, the static AddController's
        // arrays, a character split across two reads, a controller's
        // extras ──

        [InterpreterFact("autohotkey")]
        public async Task AutoHotkey_AButtonHeldAtTwo_IsPressed_AndTheStaticAddControllerTakesItsArrays()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
                using var loop = new PassLoop(rig, 1000, pad);
                using var run = AutoHotkey(dir, """

                    pad := PadForge.Device("wireless")
                    while !pad.Connected
                        Sleep(20)
                    pad.A := 2
                    pad.B := 0.5
                    ctl := PadForge.AddController("arrays", "Arrays", ["Fire"], ["Steer"])
                    Log("declared " (ctl.Id != ""))
                    Persistent
                    """, rig.PipeName);
                Assert.Equal("declared 1", run.Expect("declared ", 20000));
                await AutomationPipeRig.Until(() => pad.InputState.Buttons[0] && pad.InputState.Buttons[1]);
                var shape = rig.Host.Sessions.Devices.All().Single(e => e.Key == "arrays").Device.Shape;
                Assert.Single(shape.Buttons);
                Assert.Single(shape.Axes);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_ALineSplitInsideACharacter_IsReadWhole()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = new ScriptedHost();
                using var run = AutoHotkey(dir, """

                    PadForge.OnProfile((name) => Log("profile " name))
                    Log("ready")
                    Persistent
                    """, host.PipeName);
                run.Expect("ready", 20000);
                host.Next("sub", 5000);
                // An event as AutomationSessions writes a profile's name,
                // in two writes that part inside the Ü.
                byte[] line = Encoding.UTF8.GetBytes("e profile Ünïcödé\n");
                int split = Array.IndexOf(line, (byte)0xC3) + 1;
                host.Write(line.Take(split).ToArray());
                Thread.Sleep(300);
                host.Write(line.Skip(split).ToArray());
                Assert.Equal("profile Ünïcödé", run.Expect("profile "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_AControllersBatteryChargingAndMotion_ReadBackWhatWasSet()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var run = AutoHotkey(dir, """

                    wheel := PadForge.AddController("extras", "Extras", {Buttons: ["Fire"], Battery: true, Gyro: true})
                    wheel.Battery := 100
                    wheel.Charging := true
                    wheel.Gyro := [1, 2, 3]
                    g := wheel.Gyro
                    Log("extras " wheel.Battery " " wheel.Charging " " g[1] "," g[2] "," g[3])
                    ExitApp()
                    """, rig.PipeName);
                Assert.Equal("extras 100 1 1,2,3", run.Expect("extras ", 20000));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A hat from numeric text ──

        private const string HatWires = "wires 90deg 45.5deg -1 -1 Up+Right";

        [InterpreterFact("python")]
        public void Python_AHatFromNumericText_IsDegrees()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var run = Python(dir, """
                    log("wires", *(padforge._wire("h", v) for v in ("90", " 45.5 ", "-1", "rest", "Up + Right")))
                    """, "PadForge.Tests.Nothing." + Guid.NewGuid().ToString("N"));
                Assert.Equal(HatWires, run.Expect("wires "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_AHatFromNumericText_IsDegrees()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var run = AutoHotkey(dir, """

                    out := "wires"
                    for v in ["90", " 45.5 ", "-1", "rest", "Up + Right"]
                        out .= " " PadForge.Wire("h", v)
                    Log(out)
                    ExitApp()
                    """, "PadForge.Tests.Nothing." + Guid.NewGuid().ToString("N"));
                Assert.Equal(HatWires, run.Expect("wires "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── Feedback numbers, a snapshot's word, a tool that asks the user and
        // a tool's JSON over several lines ──

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_FeedbackNumbers_AreNumbers_AndALightStaysItsDigits()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                host.Answer = h =>
                {
                    if (h.Verb != "declare") return host.Default(h);
                    Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        // As AutomationSessions relays a game's feedback.
                        host.Reply(null, "e rumble fb1 100 200");
                        host.Reply(null, "e led fb1 001122");
                    });
                    return new[] { "ok id=fb1" };
                };
                host.Start();
                using var run = AutoHotkey(dir, """

                    ctl := PadForge.AddController("feel", "Feel", {Buttons: ["A"], Feedback: ["rumble", "light"]})
                    ctl.OnRumble((l, h) => Log("rumble " Type(l) " " l " " h))
                    ctl.OnLight((rgb) => Log("light " Type(rgb) " " rgb))
                    Persistent
                    """, host.PipeName);
                Assert.Equal("rumble Integer 100 200", run.Expect("rumble ", 20000));
                Assert.Equal("light String 001122", run.Expect("light "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("powershell")]
        public void PowerShell_ASnapshot_GivesWhatPadForgeSaysOfIt()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                // The pipe answers a program's snapshot with the word that it
                // waits for the user's review (ProblemRecorder.WaitsWord).
                using var run = PowerShell(dir, """
                    Log "snapshot $(Save-PadForgeSnapshot)"
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                Assert.Equal("snapshot waiting-for-review", run.Expect("snapshot "));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        /// <summary>A tool that asks the user, who answers after 11 seconds,
        /// past the library's usual 10: the stub gives back the arguments it
        /// was called with, which the script wrote over several lines.</summary>
        private static void SlowEcho(AutomationPipeRig rig)
        {
            rig.Host.Call = async (call, ct) =>
            {
                await Task.Delay(11000, ct);
                return AutomationCallResult.Success(call.Arguments);
            };
        }

        private const string EchoAnswer = "tool {\"a\": 1,    \"b\": 2 }";

        [InterpreterFact("powershell")]
        public void PowerShell_AToolTheUserAnswersLate_AndJsonOverSeveralLines_Answer()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                SlowEcho(rig);
                using var run = PowerShell(dir, """
                    $json = "{`"a`": 1,`r`n  `"b`": 2`n}"
                    try { Log "tool $(Invoke-PadForgeTool echo $json)" } catch { Log "tool refused $($_.Exception.Message)" }
                    """, rig.PipeName);
                run.Expect("module ", 60000);
                Assert.Equal(EchoAnswer, run.Expect("tool ", 30000));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("python")]
        public void Python_AToolTheUserAnswersLate_AndJsonOverSeveralLines_Answer()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                SlowEcho(rig);
                using var run = Python(dir, """
                    try:
                        log("tool", padforge.tool("echo", '{"a": 1,\r\n  "b": 2\n}'))
                    except padforge.PadForgeError as e:
                        log("tool refused", e)
                    """, rig.PipeName);
                Assert.Equal(EchoAnswer, run.Expect("tool ", 30000));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_AToolTheUserAnswersLate_AndJsonOverSeveralLines_Answer()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                SlowEcho(rig);
                using var run = AutoHotkey(dir, """

                    try
                        Log("tool " PadForge.Tool("echo", '{"a": 1,`r`n  "b": 2`n}'))
                    catch as e
                        Log("tool refused " e.Message)
                    ExitApp()
                    """, rig.PipeName);
                Assert.Equal(EchoAnswer, run.Expect("tool ", 30000));
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        // ── A pad that follows an unplugged device, and devices that come
        // and go ──

        /// <summary>A host with one gamepad, unplugged, rows as
        /// AutomationDevices writes them. Once the script is ready a mouse
        /// comes online three times: the pad follows the same device each
        /// time, so its state and its axis line are subscribed once.</summary>
        private static void FollowsItsDeviceOnce(ScriptedHost host, ScriptRun run)
        {
            host.Answer = h =>
            {
                if (h.Line == "devices")
                    return new[] { "device id=gp1 type=gamepad online=0 name=\"Old Pad\" buttons=0 axes=1 sliders=0 hats=0 gyro=0 accel=0 touchpads=0", "ok" };
                if (h.Line == "caps gp1") return new[] { "axis index=0 name=\"Axis 0\" rest=32768 script=LeftX", "ok" };
                if (h.Line.StartsWith("sub gp1 LeftX ", StringComparison.Ordinal)) return new[] { "ok line=" + (100 + host.Snapshot().Count) };
                return host.Default(h);
            };
            host.Start();
            run.Expect("ready", 60000);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!host.Snapshot().Any(h => h.Line.StartsWith("sub gp1 LeftX ", StringComparison.Ordinal)))
            {
                Assert.True(clock.ElapsedMilliseconds < 20000, "the script never made its hotkey's line");
                Thread.Sleep(50);
            }
            for (int i = 0; i < 3; i++)
            {
                host.Reply(null, "e dev ms" + i + " online");
                Thread.Sleep(400);
            }
            Thread.Sleep(1500);
            var heard = host.Snapshot();
            Assert.Single(heard, h => h.Line == "sub gp1 state");
            Assert.Single(heard, h => h.Line.StartsWith("sub gp1 LeftX ", StringComparison.Ordinal));
        }

        [InterpreterFact("python")]
        public void Python_APadFollowingAnUnpluggedDevice_FollowsItOnce_AsOtherDevicesCome()
        {
            string dir = ScriptPythonTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                using var run = Python(dir, """
                    pad = padforge.device("gamepad")
                    pad.hotkey("LeftX > 80%", lambda: log("hk"))
                    log("ready")
                    padforge.wait(30)
                    """, host.PipeName);
                FollowsItsDeviceOnce(host, run);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("powershell")]
        public void PowerShell_APadFollowingAnUnpluggedDevice_FollowsItOnce_AsOtherDevicesCome()
        {
            string dir = ScriptPowerShellTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                using var run = PowerShell(dir, """
                    $pad = Get-PadForgeDevice gamepad
                    $pad.Hotkey('LeftX > 80%', { Log 'hk' })
                    Log 'ready'
                    Wait-PadForge -Seconds 30
                    """, host.PipeName);
                FollowsItsDeviceOnce(host, run);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }

        [InterpreterFact("autohotkey")]
        public void AutoHotkey_APadFollowingAnUnpluggedDevice_FollowsItOnce_AsOtherDevicesCome()
        {
            string dir = ScriptAutoHotkeyTests.NewFolder();
            try
            {
                using var host = new ScriptedHost(start: false);
                using var run = AutoHotkey(dir, """

                    pad := PadForge.Device("gamepad")
                    pad.Hotkey("LeftX > 80%", (*) => Log("hk"))
                    Log("ready")
                    Persistent
                    """, host.PipeName);
                FollowsItsDeviceOnce(host, run);
            }
            finally { ScriptAutoHotkeyTests.TryDelete(dir); }
        }
    }
}
