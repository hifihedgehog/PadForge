using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A client of the Automation pipe for tests: lines in order, events
    /// apart, each read with a time limit.
    /// </summary>
    internal sealed class AutomationTestClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly BlockingCollection<string> _lines = new();
        private readonly BlockingCollection<string> _events = new();
        private readonly Task _reader;

        public AutomationTestClient(string pipeName, System.Security.Principal.TokenImpersonationLevel? level = null)
        {
            // A level given opens the pipe the way the libraries do, at the
            // identification level, which the server's token read takes.
            _pipe = level is { } asked
                ? new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, asked)
                : new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(5000);
            _reader = Task.Run(() =>
            {
                try
                {
                    using var reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("e ", StringComparison.Ordinal)) _events.Add(line);
                        else _lines.Add(line);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                finally
                {
                    _lines.CompleteAdding();
                    _events.CompleteAdding();
                }
            });
        }

        public void Send(string line) => SendRaw(Encoding.UTF8.GetBytes(line + "\n"));

        /// <summary>Test seam: runs once a write has returned, where a
        /// sender's thread can lose its time slice. Null otherwise.</summary>
        public Action AfterWrite;

        /// <summary>Writes the bytes. A pipe's write has gone to the pipe
        /// when it returns, and nothing follows it: a Flush would only read
        /// the stream's state again, which the reader thread sets to broken
        /// the moment PadForge answers a line with a close, so a sender whose
        /// thread ran late was failed for a line that had been sent.</summary>
        public void SendRaw(byte[] bytes)
        {
            _pipe.Write(bytes, 0, bytes.Length);
            AfterWrite?.Invoke();
        }

        /// <summary>The reader's end, for the test of the sender
        /// itself.</summary>
        public Task Reader => _reader;

        /// <summary>The next line that is not an event, or null.</summary>
        public string Next(int ms = 5000)
            => _lines.TryTake(out string line, ms) ? line : null;

        /// <summary>The next event, or null.</summary>
        public string Event(int ms = 5000)
            => _events.TryTake(out string line, ms) ? line : null;

        /// <summary>Events that wait now, without waiting.</summary>
        public List<string> Drain(int settleMs = 100)
        {
            Thread.Sleep(settleMs);
            var list = new List<string>();
            while (_events.TryTake(out string line)) list.Add(line);
            return list;
        }

        /// <summary>Sends a request and gives the lines of its answer, rows
        /// and the final ok or err.</summary>
        public List<string> Ask(string line, int ms = 5000)
        {
            Send(line);
            string tag = line.StartsWith("@", StringComparison.Ordinal) ? line.Split(' ')[0] + " " : string.Empty;
            var answer = new List<string>();
            while (true)
            {
                string next = Next(ms);
                Assert.True(next != null, "no answer to " + line + " after " + string.Join(" | ", answer));
                answer.Add(next);
                string body = tag.Length > 0 && next.StartsWith(tag, StringComparison.Ordinal) ? next.Substring(tag.Length) : next;
                if (body == "ok" || body.StartsWith("ok ", StringComparison.Ordinal) || body.StartsWith("err ", StringComparison.Ordinal))
                    return answer;
            }
        }

        public string Final(string line) => Ask(line).Last();

        /// <summary>Whether PadForge closed the connection.</summary>
        public bool ClosedWithin(int ms) => _reader.Wait(ms);

        public void Dispose()
        {
            try { _pipe.Dispose(); } catch { }
            try { _reader.Wait(2000); } catch { }
        }
    }

    /// <summary>The engine for tests: an input manager that is never
    /// started, whose device list is the test's own.</summary>
    internal sealed class AutomationTestEngine : IAutomationEngine
    {
        public readonly InputManager Manager;
        public readonly List<string> Calls = new();

        public AutomationTestEngine(InputManager manager) => Manager = manager;

        public void Register(ProgramDevice device)
        {
            lock (Calls) Calls.Add("register " + device.Name);
            Manager.RegisterProgramDevice(device);
        }

        public void Unregister(ProgramDevice device)
        {
            lock (Calls) Calls.Add("unregister " + device.Name);
            Manager.UnregisterProgramDevice(device);
        }

        public string Outputs(int number) => number == 1
            ? InputService.OutputsLine(VirtualControllerType.Xbox,
                new Gamepad { Buttons = Gamepad.A, LeftTrigger = 65535, ThumbLX = -32768 }, default, default, default, default)
            : null;
    }

    /// <summary>A host on pipe names of the test's own, with a device list,
    /// an engine and a clean store of holds and values.</summary>
    internal sealed class AutomationPipeRig : IDisposable
    {
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly string _profile = SettingsManager.ActiveProfileId;

        public readonly string PipeName = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
        public readonly RecordingAutomationTarget Target = new();
        public readonly InlineAutomationUi Ui = new();
        public readonly AutomationHost Host;
        public readonly AutomationTestEngine Engine;
        private readonly List<AutomationTestClient> _clients = new();

        public AutomationPipeRig(AutomationLevel level = AutomationLevel.SendInputs, bool engine = true)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            ScriptValues.ResetForTest();
            ControlHolds.Publish(null);
            Host = TestConsent.AllowAll(new AutomationHost(Target, Ui, "PadForge.Tests.Control." + Guid.NewGuid().ToString("N"),
                automationPipeName: PipeName));
            Host.Sessions.Devices.OfflineAfterMs = 300;
            if (engine)
            {
                Engine = new AutomationTestEngine(new InputManager());
                Host.AttachEngine(Engine);
            }
            Host.Apply(true, level);
            Assert.Equal(ExternalControlService.PipeState.Serving, Host.AutomationPipeState);
        }

        public AutomationTestClient Connect()
        {
            var client = new AutomationTestClient(PipeName);
            _clients.Add(client);
            return client;
        }

        /// <summary>From here every connection stands for a process of its
        /// own, with this test's image: a test has one process, and the
        /// sessions count by process.</summary>
        public void EachConnectionItsOwnProcess()
        {
            string path = Environment.ProcessPath;
            int next = 100000;
            Host.AutomationPipe.ProcessOf = _ => AutomationClientProcess.ForTest(Interlocked.Increment(ref next), path);
        }

        /// <summary>A connected client whose hello was answered.</summary>
        public AutomationTestClient Open(string name = "Test Script", string extra = "")
        {
            var client = Connect();
            string answer = client.Final("hello name=\"" + name + "\" kind=script" + extra);
            Assert.StartsWith("ok padforge=", answer);
            return client;
        }

        public UserDevice AddDevice(int capType, string name = "Pad", bool online = true, int buttons = 16, int axes = 6, int hats = 1)
        {
            var state = new CustomInputState();
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), ProductName = name, IsOnline = online };
            ud.LoadCapabilities(axes, buttons, hats, capType);
            ud.InputState = state;
            ud.ReadInputState = state;
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        public static string IdOf(UserDevice ud) => AutomationDevices.IdOf(ud.InstanceGuid);

        public void Pass(params UserDevice[] devices) => Host.Sessions.Watch.OnPass(devices, devices.Length);

        public static async Task Until(Func<bool> condition, int ms = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.ElapsedMilliseconds < ms, "the wait ran out");
                await Task.Delay(20);
            }
        }

        public void Dispose()
        {
            foreach (var client in _clients) client.Dispose();
            Host.Dispose();
            Engine?.Manager.Dispose();
            ControlHolds.Publish(null);
            ScriptValues.ResetForTest();
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            SettingsManager.SlotCreated = _created;
            SettingsManager.ActiveProfileId = _profile;
        }
    }

    /// <summary>
    /// The Automation pipe (#597): the line protocol, the session and its
    /// limits, identity from the pipe's token, events from the poll thread,
    /// devices a program adds, holds, named values, the lease and the level.
    /// Every pipe has a name of the test's own.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationPipeTests
    {
        // ── The line ──

        [Theory]
        [InlineData("plain")]
        [InlineData("two words")]
        [InlineData("a \"quote\" inside")]
        [InlineData("back\\slash and \\\" both")]
        [InlineData("key=value")]
        [InlineData("")]
        [InlineData("ünïcødé ✓")]
        public void QuotedText_ReadsBackAsItWent(string text)
        {
            var r = AutomationLine.Parse("verb name=" + AutomationLine.Quote(text) + " " + AutomationLine.Word(text) + " tail");
            Assert.Null(r.Error);
            Assert.Equal(text, r.Get("name"));
            Assert.Equal(text, r.Bare()[0]);
            Assert.Equal("tail", r.Bare()[1]);
        }

        [Fact]
        public void ALine_ReadsItsTagVerbWordsAndJson()
        {
            var r = AutomationLine.Parse("@7 DECLARE key=pad name=\"My Pad\" {\"buttons\": [\"A B\"]}");
            Assert.Equal("@7", r.Tag);
            Assert.Equal("declare", r.Verb);
            Assert.Equal("pad", r.Get("key"));
            Assert.Equal("My Pad", r.Get("name"));
            Assert.Equal("{\"buttons\": [\"A B\"]}", r.Json);

            Assert.Equal("bad-quote", AutomationLine.Parse("own x name=\"open").Error);
            Assert.Equal("bad-tag", AutomationLine.Parse("@!x version").Error);
            Assert.Equal("bad-tag", AutomationLine.Parse("@" + new string('1', 17) + " version").Error);
            Assert.Equal("empty", AutomationLine.Parse("@3").Error);
        }

        [Fact]
        public void TheCommandSetsAnswers_TakeThisPipesForm()
        {
            Assert.Equal("ok on", AutomationLine.FromCommandSet("ok on"));
            Assert.StartsWith("err not-allowed ", AutomationLine.FromCommandSet("error not-allowed"));
            Assert.StartsWith("err internal ", AutomationLine.FromCommandSet(null));
        }

        // ── hello ──

        [Fact]
        public void Hello_OpensTheSession_AndNothingElseDoes()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var early = rig.Connect();
            Assert.StartsWith("err hello-first ", early.Final("devices"));
            Assert.True(early.ClosedWithin(3000));

            using var client = rig.Connect();
            string ok = client.Final("hello name=\"Lap Timer\" kind=script");
            Assert.Matches(@"^ok padforge=\S+ protocol=1 level=read session=\d+$", ok);
            Assert.StartsWith("err bad-argument ", client.Final("hello name=again"));
            Assert.Equal(1, rig.Host.Sessions.Count);
            var listed = Assert.Single(rig.Host.Clients.Snapshot());
            Assert.Equal("Lap Timer", listed.Name);
            Assert.Equal(AutomationClientKind.Script, listed.Kind);
            Assert.Equal(Environment.ProcessId, listed.ProcessId);

            using var future = rig.Connect();
            Assert.StartsWith("err unsupported-protocol ", future.Final("hello name=x protocol=2"));
        }

        [Fact]
        public void TheLaterPartsHellos_AnswerNotAvailableUntilTheirDelegatesAreSet()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var mcp = rig.Connect();
            Assert.StartsWith("err not-available ", mcp.Final("hello name=x kind=mcp"));
            using var plugin = rig.Connect();
            Assert.StartsWith("err not-available ", plugin.Final("hello name=x kind=plugin token=abc"));
            using var script = rig.Open();
            Assert.StartsWith("err not-available ", script.Final("call find_settings {}"));
        }

        [Fact]
        public async Task AnAssistantsHello_HandsTheRestOfTheStreamToItsDelegate()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var got = new TaskCompletionSource<string>();
            rig.Host.McpSession = async (peer, stream, stopping) =>
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                got.TrySetResult(peer.Name + ":" + await reader.ReadLineAsync());
                var reply = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}\n");
                await stream.WriteAsync(reply, stopping);
                await stream.FlushAsync(stopping);
                await Task.Delay(500, stopping);
            };
            using var client = rig.Connect();
            // The protocol's first bytes ride in the same write as the hello.
            client.SendRaw(Encoding.UTF8.GetBytes("hello name=Assistant kind=mcp\n{\"method\":\"initialize\"}\n"));
            Assert.StartsWith("ok padforge=", client.Next());
            Assert.Equal("Assistant:{\"method\":\"initialize\"}", await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("{\"jsonrpc\":\"2.0\"}", client.Next());
        }

        [Fact]
        public void APluginsHello_TakesTheNameItsTokenWasGivenFor()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            rig.Host.AcceptPluginToken = (token, pid) => token == "right" && pid == Environment.ProcessId
                ? new AutomationPluginGrant("Wheel Bridge") : null;
            using var wrong = rig.Connect();
            Assert.StartsWith("err denied ", wrong.Final("hello name=x kind=plugin token=wrong"));
            using var plugin = rig.Connect();
            Assert.StartsWith("ok ", plugin.Final("hello name=claimed kind=plugin token=right"));
            // A plugin declares and drives its own devices at any level.
            string declared = plugin.Final("declare key=w name=\"Wheel\" {\"axes\":[\"Steer\"]}");
            Assert.StartsWith("ok id=", declared);
            var entry = rig.Host.Sessions.Devices.Find(declared.Substring(6));
            Assert.Equal("plugin", entry.Kind);
            Assert.Equal("Wheel Bridge", entry.Program);
            // And reads nothing the level does not allow.
            Assert.StartsWith("err not-allowed ", plugin.Final("devices"));
        }

        [Fact]
        public void Tags_RideEveryLineOfTheirAnswer()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            rig.AddDevice(InputDeviceType.Gamepad, "One");
            rig.AddDevice(InputDeviceType.Keyboard, "Two");
            using var client = rig.Open();
            var answer = client.Ask("@a7 devices");
            Assert.Equal(3, answer.Count);
            Assert.All(answer, line => Assert.StartsWith("@a7 ", line));
            Assert.Equal("@a7 ok", answer[2]);
            Assert.StartsWith("@x err unknown-command ", client.Final("@x frobnicate"));
            Assert.StartsWith("err bad-tag ", client.Final("@!x version"));
        }

        [Fact]
        public void AMalformedLine_IsAnsweredAndTheSessionGoesOn()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var client = rig.Open();
            Assert.StartsWith("err bad-quote ", client.Final("caps \"open"));
            Assert.StartsWith("err unknown-command ", client.Final("frobnicate"));
            Assert.StartsWith("err unknown-device ", client.Final("read nosuchid"));
            Assert.StartsWith("ok ", client.Final("version"));
        }

        // ── Levels ──

        public static IEnumerable<object[]> VerbsAndLevels() => new[]
        {
            new object[] { "state", AutomationLevel.Commands },
            new object[] { "query", AutomationLevel.Commands },
            new object[] { "devices", AutomationLevel.Read },
            new object[] { "caps {id}", AutomationLevel.Read },
            new object[] { "read {id}", AutomationLevel.Read },
            new object[] { "outputs 1", AutomationLevel.Read },
            new object[] { "sub devices", AutomationLevel.Read },
            new object[] { "sub {id} buttons", AutomationLevel.Read },
            new object[] { "unsub all", AutomationLevel.Read },
            new object[] { "declare key=pad name=\"Pad\" {\"buttons\":[\"A\"]}", AutomationLevel.SendInputs },
            new object[] { "own {id} b1=1", AutomationLevel.SendInputs },
            new object[] { "rel {id} all", AutomationLevel.SendInputs },
            new object[] { "var speed=1", AutomationLevel.SendInputs },
        };

        [Theory]
        [MemberData(nameof(VerbsAndLevels))]
        public void EveryVerb_RunsAtItsLevel_AndIsRefusedUnderIt(string line, AutomationLevel needed)
        {
            foreach (AutomationLevel level in Enum.GetValues(typeof(AutomationLevel)))
            {
                using var rig = new AutomationPipeRig(level);
                var pad = rig.AddDevice(InputDeviceType.Gamepad);
                using var client = rig.Open();
                string answer = client.Final(line.Replace("{id}", AutomationPipeRig.IdOf(pad)));
                if (level < needed) Assert.StartsWith("err not-allowed ", answer);
                else Assert.StartsWith("ok", answer);
            }
        }

        [Fact]
        public void TheVerbsAProgramOwns_AreRefusedUnderSendInputs_AndRunAtIt()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            string id = client.Final("declare key=pad name=\"Pad\" {\"buttons\":[\"A\"]}").Substring(6);
            Assert.Equal("@1 ok", client.Final("@1 s " + id + " b0=1"));
            Assert.Equal("ok", client.Final("gone " + id));
            rig.Host.Apply(true, AutomationLevel.Read);
            Assert.StartsWith("@2 err not-allowed ", client.Final("@2 s " + id + " b0=1"));
            Assert.StartsWith("err not-allowed ", client.Final("gone " + id));
        }

        [Fact]
        public async Task ALevelDroppingMidSession_IsHeard_AndLetsGoOfWhatItNoLongerAllows()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            using var client = rig.Open();
            string id = client.Final("declare key=pad name=\"Pad\" {\"buttons\":[\"A\"]}").Substring(6);
            Assert.Equal("ok", client.Final("own " + AutomationPipeRig.IdOf(pad) + " b2=1"));
            Assert.Equal("ok", client.Final("var lap=3"));
            Assert.Equal("ok", client.Final("sub " + AutomationPipeRig.IdOf(pad) + " buttons"));
            Assert.NotNull(ControlHolds.Current);

            rig.Host.Apply(true, AutomationLevel.Read);
            Assert.Equal("e level read", client.Event());
            Assert.Null(ControlHolds.Current);
            Assert.Equal(0f, ScriptValues.Read("lap"));
            // Nobody assigned the device, so it went with its row.
            Assert.Null(rig.Host.Sessions.Devices.Find(id));
            Assert.StartsWith("err not-allowed ", client.Final("own " + AutomationPipeRig.IdOf(pad) + " b2=1"));
            Assert.True(rig.Host.Sessions.Watch.Hooked, "Read still follows");

            rig.Host.Apply(true, AutomationLevel.Commands);
            Assert.Equal("e level commands", client.Event());
            Assert.False(rig.Host.Sessions.Watch.Hooked);
            Assert.StartsWith("err not-allowed ", client.Final("devices"));

            rig.Host.Apply(false, AutomationLevel.Commands);
            Assert.Equal("e level off", client.Event());
            Assert.True(client.ClosedWithin(3000));
            await AutomationPipeRig.Until(() => rig.Host.Sessions.Count == 0);
        }

        // ── Limits ──

        [Fact]
        public void TwelveScriptSessions_AreTaken_AndTheThirteenthIsRefused_ForFourAreKeptForPlugins()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            rig.EachConnectionItsOwnProcess();
            for (int i = 0; i < AutomationSessions.MostSessions - AutomationSessions.KeptForPlugins; i++) rig.Open("Script " + i);
            using var extra = rig.Connect();
            Assert.StartsWith("err too-many ", extra.Final("hello name=extra"));
            Assert.True(extra.ClosedWithin(3000));
        }

        [Fact]
        public void ALine_RunsTo8192Bytes_AndADeclareTo65536()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            // "@1 beat" is 7 bytes, so these lines are 8,192 and 8,193.
            Assert.Equal("@1 ok", client.Final("@1 beat" + new string(' ', AutomationLine.LongestLine - 7)));
            Assert.StartsWith("@2 err too-long ", client.Final("@2 beat" + new string(' ', AutomationLine.LongestLine - 6)));

            var names = Enumerable.Range(0, 256).Select(i => "\"Button number " + i + " with a long name for its index\"");
            string declare = "declare key=wide name=\"Wide\" {\"buttons\":[" + string.Join(",", names) + "]}";
            Assert.InRange(Encoding.UTF8.GetByteCount(declare), AutomationLine.LongestLine + 1, AutomationLine.LongestDeclare);
            Assert.StartsWith("ok id=", client.Final(declare));
            string tooBig = "declare key=big name=\"Big\" {\"buttons\":[\"" + new string('x', AutomationLine.LongestDeclare) + "\"]}";
            Assert.StartsWith("err too-long ", client.Final(tooBig));
            Assert.StartsWith("ok ", client.Final("version"));
        }

        [Fact]
        public void EightDevicesASession_And32InAll()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            rig.EachConnectionItsOwnProcess();
            var clients = Enumerable.Range(0, 5).Select(i => rig.Open("Maker " + i)).ToList();
            for (int i = 0; i < ProgramDevices.MostPerSession; i++)
                Assert.StartsWith("ok id=", clients[0].Final("declare key=d" + i + " name=\"D\" {}"));
            Assert.StartsWith("err too-many ", clients[0].Final("declare key=d9 name=\"D\" {}"));
            // Declaring one again is no new device.
            Assert.StartsWith("ok id=", clients[0].Final("declare key=d0 name=\"D\" {}"));
            for (int c = 1; c < 4; c++)
                for (int i = 0; i < ProgramDevices.MostPerSession; i++)
                    Assert.StartsWith("ok id=", clients[c].Final("declare key=d" + i + " name=\"D\" {}"));
            Assert.Equal(ProgramDevices.MostInAll, rig.Host.Sessions.Devices.Live);
            Assert.StartsWith("err too-many ", clients[4].Final("declare key=d0 name=\"D\" {}"));
        }

        [Fact]
        public void A256thSubscription_IsTheLast()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open();
            for (int axis = 0; axis < 32; axis++)
                for (int k = 0; k < 8; k++)
                    Assert.Equal("ok", client.Final("sub " + id + " axis=" + axis + " above=" + (1000 + k)));
            // A crossing on the same axis takes the place of the one before,
            // so these are 32 so far.
            for (int i = 0; i < 224; i++)
            {
                var other = rig.AddDevice(InputDeviceType.Gamepad);
                Assert.Equal("ok", client.Final("sub " + AutomationPipeRig.IdOf(other) + " buttons"));
            }
            var last = rig.AddDevice(InputDeviceType.Gamepad);
            Assert.StartsWith("err too-many ", client.Final("sub " + AutomationPipeRig.IdOf(last) + " buttons"));
        }

        [Fact]
        public void TheLineRate_TakesA1000HzStream_AndCutsABurstToTheRate()
        {
            var rate = new AutomationLineRate(AutomationSession.LinesPerSecond, 0);
            // Ten seconds of a device driven at 1,000 Hz with a beat a second.
            for (long ms = 0; ms < 10000; ms++)
            {
                Assert.True(rate.Take(ms));
                if (ms % 1000 == 0) Assert.True(rate.Take(ms));
            }
            var burst = new AutomationLineRate(AutomationSession.LinesPerSecond, 0);
            int taken = 0;
            for (int i = 0; i < AutomationSession.LinesPerSecond * 2; i++) if (burst.Take(0)) taken++;
            Assert.Equal(AutomationSession.LinesPerSecond, taken);
            Assert.True(burst.Take(1000));
        }

        [Fact]
        public void LinesPastTheRate_AreRefused()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var client = rig.Open();
            var burst = new StringBuilder();
            for (int i = 0; i < AutomationSession.LinesPerSecond + 2000; i++) burst.Append("@").Append(i).Append(" beat\n");
            client.SendRaw(Encoding.UTF8.GetBytes(burst.ToString()));
            int ok = 0, limited = 0;
            for (int i = 0; i < AutomationSession.LinesPerSecond + 2000; i++)
            {
                string line = client.Next();
                Assert.NotNull(line);
                if (line.EndsWith(" ok", StringComparison.Ordinal)) ok++;
                else if (line.Contains(" err rate-limited ", StringComparison.Ordinal)) limited++;
            }
            Assert.True(limited > 0, "a burst over the rate was refused in part");
            Assert.Equal(AutomationSession.LinesPerSecond + 2000, ok + limited);
        }

        // ── Identity ──

        [Fact]
        public void AClientOfTheSameUser_IsTaken_AndReadFromItsToken()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            AutomationClientToken? seen = null;
            rig.Host.AutomationPipe.ReadToken = server => seen = AutomationClientIdentity.Read(server);
            using var client = rig.Open();
            Assert.NotNull(seen);
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            Assert.Equal(me.User.Value, seen.Value.UserSid);
            Assert.Equal(Process.GetCurrentProcess().SessionId, seen.Value.SessionId);
            Assert.True(seen.Value.Integrity >= AutomationClientToken.MediumIntegrity);
            Assert.False(seen.Value.AppContainer);
        }

        /// <summary>The cases a test bench cannot make: another Windows
        /// session, low integrity, an app container, another user, no token.
        /// The seam stands the token's facts in.</summary>
        [Theory]
        [InlineData("other-session")]
        [InlineData("low-integrity")]
        [InlineData("app-container")]
        [InlineData("other-user")]
        [InlineData("no-token")]
        public void TheTokensChecks_RefuseWhatTheyShould(string refusal)
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var desktop = rig.Host.AutomationPipe.Desktop;
            var good = new AutomationClientToken(desktop.Server, desktop.Session, 0x2000, false);
            AutomationClientToken? token = refusal switch
            {
                "other-session" => good with { SessionId = desktop.Session + 1 },
                "low-integrity" => good with { Integrity = 0x1000 },
                "app-container" => good with { AppContainer = true },
                "other-user" => good with { UserSid = "S-1-5-21-1-2-3-1001" },
                _ => null,
            };
            Assert.Null(AutomationClientIdentity.Judge(good, desktop.Desktop, desktop.Server, desktop.Session));
            Assert.Equal(refusal, AutomationClientIdentity.Judge(token, desktop.Desktop, desktop.Server, desktop.Session));
            rig.Host.AutomationPipe.ReadToken = _ => token;
            using var client = rig.Connect();
            // The token is read once the first line is in: Windows lets a
            // server impersonate its client only after a read.
            client.Send("hello name=x");
            string line = client.Next();
            Assert.StartsWith("err refused ", line);
            Assert.EndsWith(refusal, line);
            Assert.True(client.ClosedWithin(3000));
            Assert.Equal(0, rig.Host.Sessions.Count);
        }

        /// <summary>A line PadForge answers and then closes on is sent and its
        /// answer read, however late the sender's thread runs after its
        /// write: here it waits until the reader has seen the close. The
        /// reader marks the stream broken on some of those closes and not
        /// others, so twenty connections are made.</summary>
        [Fact]
        public void ALineAnsweredWithAClose_IsSent_HoweverLateTheSendersThreadRuns()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            for (int i = 0; i < 20; i++)
            {
                using var client = rig.Connect();
                client.AfterWrite = () => Assert.True(client.Reader.Wait(5000), "PadForge did not close the connection");
                client.Send("hello name=x protocol=2");
                Assert.StartsWith("err unsupported-protocol ", client.Next());
                Assert.True(client.ClosedWithin(3000));
            }
        }

        [Fact]
        public void AWindowHandle_IsTakenOnlyFromItsOwnProcess()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var client = rig.Connect();
            // The desktop window belongs to another process.
            IntPtr desktop = GetDesktopWindow();
            Assert.StartsWith("err bad-argument ", client.Final("hello name=x hwnd=" + desktop.ToInt64()));
            Assert.StartsWith("ok ", client.Final("hello name=x"));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        // ── Every device kind ──

        public static IEnumerable<object[]> EveryDeviceType()
            => typeof(InputDeviceType).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(int))
                .Select(f => new object[] { f.Name, (int)f.GetRawConstantValue() });

        /// <summary>The owner's ruling: every input device is readable at the
        /// Read level, keyboards and mice included. A kind added to
        /// InputDeviceType with no word of its own fails here.</summary>
        [Theory]
        [MemberData(nameof(EveryDeviceType))]
        public void EveryDeviceKind_IsListedAndRead(string name, int capType)
        {
            string word = AutomationDevices.TypeWord(capType);
            Assert.NotEqual("device", word);
            Assert.Single(EveryDeviceType(), row => AutomationDevices.TypeWord((int)row[1]) == word);

            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var ud = rig.AddDevice(capType, name);
            ud.InputState.Buttons[3] = true;
            ud.InputState.Axis[1] = 1234;
            string id = AutomationPipeRig.IdOf(ud);
            using var client = rig.Open();
            var rows = client.Ask("devices");
            Assert.Contains(rows, r => r.StartsWith("device id=" + id + " type=" + word + " online=1 name=\"" + name + "\"", StringComparison.Ordinal));
            string state = client.Final("read " + id);
            Assert.StartsWith("ok b=0800 a=0,1234,", state);
            Assert.StartsWith("ok", client.Final("caps " + id));
            Assert.Equal("ok", client.Final("sub " + id + " buttons"));
        }

        [Fact]
        public void AShortId_IsTheSameForADeviceEveryRun_AndNamesNothingPrivate()
        {
            var g = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
            string id = AutomationDevices.IdOf(g);
            Assert.Matches("^[a-z2-7]{8}$", id);
            Assert.Equal(id, AutomationDevices.IdOf(Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")));
            Assert.DoesNotContain(g.ToString("N").Substring(0, 8), id);
        }

        [Fact]
        public void Devices_NameAProductNeverTheUsersOwnName()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var ud = rig.AddDevice(InputDeviceType.Gamepad, "Wireless Controller");
            ud.DisplayName = "Dad's pad";
            ud.SerialNumber = "AA:BB:CC:DD:EE:FF";
            ud.DevicePath = @"\\?\hid#vid_054c&pid_0ce6#7&1234";
            using var client = rig.Open();
            string all = string.Join("\n", client.Ask("devices").Concat(client.Ask("caps " + AutomationPipeRig.IdOf(ud))));
            Assert.Contains("Wireless Controller", all);
            Assert.DoesNotContain("Dad", all);
            Assert.DoesNotContain("AA:BB", all);
            Assert.DoesNotContain("vid_054c", all);
        }

        [Fact]
        public void Outputs_SayWhatAControllerSends()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var client = rig.Open();
            Assert.Equal("ok type=xbox buttons=1000 lt=65535 rt=0 lx=-32768 ly=0 rx=0 ry=0", client.Final("outputs 1"));
            Assert.StartsWith("err unknown-slot ", client.Final("outputs 2"));
            Assert.StartsWith("err bad-argument ", client.Final("outputs 17"));
            rig.Host.DetachEngine();
            Assert.StartsWith("err engine-stopped ", client.Final("outputs 1"));
        }

        // ── Events ──

        [Fact]
        public void AButtonEdge_AndAHatChange_AreEvents()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Keyboard, "Keys", buttons: 256);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open();
            Assert.Equal("ok", client.Final("sub " + id + " buttons"));
            Assert.Equal("ok", client.Final("sub " + id + " hats"));
            rig.Pass(pad);
            pad.ReadInputState.Buttons[200] = true;
            pad.ReadInputState.Povs[0] = 9000;
            rig.Pass(pad);
            Assert.Equal("e b " + id + " 200 1", client.Event());
            Assert.Equal("e h " + id + " 0 9000", client.Event());
            pad.ReadInputState.Buttons[200] = false;
            rig.Pass(pad);
            Assert.Equal("e b " + id + " 200 0", client.Event());
            Assert.Empty(client.Drain());
        }

        [Fact]
        public void AnAxisCrossing_IsWorkedOutWithHysteresis()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open();
            Assert.Equal("ok", client.Final("sub " + id + " axis=5 above=40000 hyst=2000"));
            var axis = pad.ReadInputState.Axis;
            foreach (int v in new[] { 0, 39999, 40000, 41000, 38500, 39000, 37999, 38500, 40000 })
            {
                axis[5] = v;
                rig.Pass(pad);
            }
            Assert.Equal(new[] { "e x " + id + " 5 1", "e x " + id + " 5 0", "e x " + id + " 5 1" }, client.Drain(300));
        }

        [Fact]
        public void AnAxisStream_IsCoalescedToTheRateAsked()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open();
            Assert.Equal("ok", client.Final("sub " + id + " axes 20"));
            rig.Pass(pad);
            // 50 ms of passes at 1,000 Hz with a changing stick: at 20 a
            // second, one or two lines, and each is the newest value then.
            var clock = Stopwatch.StartNew();
            int value = 0;
            while (clock.ElapsedMilliseconds < 50)
            {
                pad.ReadInputState.Axis[0] = ++value;
                rig.Pass(pad);
                Thread.Sleep(1);
            }
            var events = client.Drain(300);
            Assert.InRange(events.Count, 1, 2);
            Assert.All(events, e => Assert.StartsWith("e a " + id + " 0 ", e));
            // A slider is axis 24 and on.
            pad.ReadInputState.Sliders[1] = 777;
            Thread.Sleep(60);
            rig.Pass(pad);
            Assert.Contains("e a " + id + " 25 777", client.Drain(300));
        }

        [Fact]
        public void TheWriter_KeepsOnlyTheNewestValueOfAnAxisWhileAWriteIsPending()
        {
            var outbox = new AutomationOutbox(Stream.Null, () => { });
            var slots = new AxisStreamSlots("dev12345");
            outbox.Axis(slots, 3, 100);
            outbox.Axis(slots, 3, 200);
            outbox.Axis(slots, 3, 300);
            outbox.Axis(slots, 4, 1);
            Assert.Equal(2, outbox.Waiting);
            Assert.Equal(300, slots.Value[3]);
        }

        [Fact]
        public async Task ASlowReader_IsCutOff_WithoutSlowingThePass()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            // A client that subscribes and then never reads.
            using var pipe = new NamedPipeClientStream(".", rig.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5000);
            var hello = Encoding.UTF8.GetBytes("hello name=slow\nsub " + id + " buttons\n");
            pipe.Write(hello, 0, hello.Length);
            await AutomationPipeRig.Until(() => rig.Host.Sessions.Watch.Hooked);
            rig.Pass(pad);
            long slowest = 0;
            var clock = new Stopwatch();
            // Each pass is timed as the poll thread runs it: at AboveNormal
            // (InputManager's poll thread), so a busy PC's programs at normal
            // priority do not take the thread away mid-pass, and without the
            // time a garbage collection held every thread of the process,
            // which no pass of PadForge's causes or can avoid. Measured under
            // a load of one busy process per core, the passes past 20 ms were
            // a collection's pause or a thread at normal priority waiting its
            // turn, never the pass's own work.
            Assert.Contains("Name = \"PadForge.InputManager\",\r\n                IsBackground = true,\r\n                Priority = ThreadPriority.AboveNormal",
                ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.cs").Replace("\r\n", "\n").Replace("\n", "\r\n"), StringComparison.Ordinal);
            var priority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            try
            {
                for (int i = 0; i < 60000 && rig.Host.Sessions.Count > 0; i++)
                {
                    pad.ReadInputState.Buttons[i % 32] = !pad.ReadInputState.Buttons[i % 32];
                    var paused = GC.GetTotalPauseDuration();
                    clock.Restart();
                    rig.Pass(pad);
                    long took = clock.ElapsedTicks - (long)((GC.GetTotalPauseDuration() - paused).TotalSeconds * Stopwatch.Frequency);
                    slowest = Math.Max(slowest, took);
                }
            }
            finally
            {
                Thread.CurrentThread.Priority = priority;
            }
            await AutomationPipeRig.Until(() => rig.Host.Sessions.Count == 0);
            Assert.True(slowest * 1000 / Stopwatch.Frequency < 20, "the slowest pass took " + slowest * 1000 / Stopwatch.Frequency + " ms");
        }

        [Fact]
        public void AQuietPass_AllocatesNothing()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            var keys = rig.AddDevice(InputDeviceType.Keyboard);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open();
            client.Final("sub " + id + " buttons");
            client.Final("sub " + id + " hats");
            client.Final("sub " + id + " axes 1000");
            client.Final("sub " + id + " axis=2 above=30000");
            client.Final("sub devices");
            var devices = new[] { pad, keys };
            for (int i = 0; i < 50; i++) rig.Host.Sessions.Watch.OnPass(devices, 2);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) rig.Host.Sessions.Watch.OnPass(devices, 2);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Fact]
        public void DevicesComingAndGoing_AreEvents()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            var mouse = rig.AddDevice(InputDeviceType.Mouse);
            using var client = rig.Open();
            Assert.Equal("ok", client.Final("sub devices"));
            rig.Pass(pad);
            rig.Pass(pad, mouse);
            Assert.Equal("e dev " + AutomationPipeRig.IdOf(mouse) + " online", client.Event());
            pad.IsOnline = false;
            rig.Pass(pad, mouse);
            Assert.Equal("e dev " + AutomationPipeRig.IdOf(pad) + " offline", client.Event());
            rig.Host.DetachEngine();
            Assert.Equal("e dev " + AutomationPipeRig.IdOf(mouse) + " offline", client.Event());
        }

        [Fact]
        public async Task TheEngineAndTheProfile_AreEventsForTheirFollowers()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var engine = AutomationEngineState.Running;
            rig.Host.EngineState = () => engine;
            rig.Host.ProfileName = () => SettingsManager.ActiveProfileId == null ? null : "Racing";
            SettingsManager.ActiveProfileId = null;
            using var client = rig.Open();
            Assert.Equal("ok", client.Final("sub engine"));
            Assert.Equal("ok", client.Final("sub profile"));
            await Task.Delay(300);
            engine = AutomationEngineState.Stopped;
            Assert.Equal("e engine stopped", client.Event());
            SettingsManager.ActiveProfileId = "p1";
            await AutomationPipeRig.Until(() => rig.Ui.Posted.Count > 0);
            rig.Ui.Posted[0]();
            Assert.Equal("e profile \"Racing\"", client.Event());
        }

        // ── Devices a program adds ──

        private const string Shape = "{\"buttons\":[\"Fire\",{\"index\":9,\"name\":\"Boost\"}],"
            + "\"axes\":[{\"name\":\"Steer\"},{\"index\":2,\"name\":\"Gas\",\"rest\":0}],"
            + "\"sliders\":[\"Throttle\"],\"hats\":[\"View\"],\"gyro\":true,\"touchpads\":[2],\"battery\":true,"
            + "\"feedback\":[\"trigrumble\",\"light\",\"player\"]}";

        [Fact]
        public void ADeclaredDevice_HasItsShapeAndNames()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open("Sim Bridge");
            string id = client.Final("declare key=rig name=\"Sim Wheel\" " + Shape).Substring(6);
            var ud = SettingsManager.FindDeviceByInstanceGuid(rig.Host.Sessions.Devices.Find(id).Guid);
            Assert.NotNull(ud);
            Assert.True(ud.IsOnline);
            Assert.Equal(InputDeviceType.Program, ud.CapType);
            Assert.Equal("Sim Wheel", ud.ProductName);
            Assert.StartsWith("program://", ud.DevicePath);
            Assert.Equal(new[] { 0, 9 }, ud.CapButtonIndices);
            Assert.Equal(new[] { 0, 2 }, ud.CapAxisIndices);
            Assert.True(ud.HasGyro);
            Assert.False(ud.HasAccel);
            Assert.True(ud.HasTouchpad);
            Assert.True(ud.HasRumbleTriggers);
            Assert.True(ud.HasForceFeedback, "trigger rumble brings rumble with it");
            // The names the mapping pickers show.
            Assert.False(MappingDisplayResolver.UseRawNumberedNaming(ud));
            var names = ud.DeviceObjects.Select(o => o.Name).ToList();
            Assert.Equal(new[] { "Steer", "Gas", "Throttle", "Fire", "Boost", "View" }, names);
            Assert.Contains(ud.DeviceObjects, o => o.IsSlider && o.InputIndex == 0 && o.Name == "Throttle");
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(ud.CapType));
            Assert.IsAssignableFrom<IRelayedFeedbackDevice>(ud.Device);

            var caps = client.Ask("caps " + id);
            Assert.Contains("axis index=0 name=\"Steer\" rest=32768", caps);
            Assert.Contains("axis index=2 name=\"Gas\" rest=0", caps);
            Assert.Contains("axis index=24 name=\"Throttle\" rest=0", caps);
            Assert.Contains("button index=9 name=\"Boost\"", caps);
            Assert.Contains("hat index=0 name=\"View\"", caps);
            Assert.Contains("touchpad index=0 fingers=2", caps);
            Assert.Contains("battery", caps);
            Assert.Contains("feedback rumble=1 trigrumble=1 light=1 player=1", caps);
        }

        /// <summary>The new device family's number is pinned, and it joins
        /// every list a family joins.</summary>
        [Fact]
        public void TheProgramTypeIsPinned_AndJoinsEverySiblingList()
        {
            Assert.Equal(46, InputDeviceType.Program);
            Assert.Equal("", DeviceTypeGlyph.For(InputDeviceType.Program));
            Assert.Equal("Program", DeviceTypeGlyph.KeyFor(InputDeviceType.Program));
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.DeviceType_Program, DeviceTypeGlyph.NameFor("Program"));
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.Program));
            var row = new DeviceRowViewModel { DeviceTypeKey = "Program", DevicePath = "program://abcd2345" };
            Assert.True(row.IsInternalVirtual);
            Assert.False(row.ShowSubmitMapping);
            Assert.False(row.CountsAsDevice);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.DeviceType_Program, row.DeviceType);
            Assert.False(MappingDisplayResolver.UseRawNumberedNaming(new UserDevice { CapType = InputDeviceType.Program }));
            var device = new ProgramDevice(Guid.NewGuid(), Guid.NewGuid(), "program://abcd2345", "Pad", new ProgramDeviceShape());
            Assert.True(InputService.IsOwnSchemeWrapper(device));
            Assert.Equal(InputDeviceType.Program, device.GetInputDeviceType());
            Assert.Equal("program", AutomationDevices.TypeWord(InputDeviceType.Program));
        }

        [Theory]
        [InlineData("{\"buttons\":[{\"index\":256}]}")]
        [InlineData("{\"axes\":[{\"rest\":70000}]}")]
        [InlineData("{\"hats\":[\"a\",\"b\",\"c\",\"d\",\"e\"]}")]
        [InlineData("{\"touchpads\":[11]}")]
        [InlineData("{\"feedback\":[\"smell\"]}")]
        [InlineData("{\"buttons\":[\"A\",{\"index\":0}]}")]
        [InlineData("{\"wings\":true}")]
        [InlineData("[1,2]")]
        [InlineData("{not json")]
        public void ADeclarationThatDoesNotRead_IsRefused(string json)
        {
            Assert.Null(ProgramDeviceDeclaration.Parse(json, out string error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void ADevicesGuids_AreStable_AndItsProductIsItsOwn()
        {
            Guid a = ProgramDeviceDeclaration.InstanceGuid("script", "Sim Bridge", "rig");
            Assert.Equal(a, ProgramDeviceDeclaration.InstanceGuid("script", "Sim Bridge", "rig"));
            Assert.NotEqual(a, ProgramDeviceDeclaration.InstanceGuid("script", "Sim Bridge", "rig2"));
            Assert.NotEqual(a, ProgramDeviceDeclaration.InstanceGuid("plugin", "Sim Bridge", "rig"));
            Assert.NotEqual(ProgramDeviceDeclaration.ProductGuid("script", "Sim Bridge", "rig"),
                ProgramDeviceDeclaration.ProductGuid("script", "Sim Bridge", "rig2"));
        }

        [Fact]
        public async Task SeveralControls_AreSetAsOneChange()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            string id = client.Final("declare key=rig name=\"Sim\" " + Shape).Substring(6);
            var device = rig.Host.Sessions.Devices.Find(id).Device;
            var seen = new List<(int, int)>();
            var reader = Task.Run(() =>
            {
                for (int i = 0; i < 200000; i++)
                {
                    var s = device.PeekState();
                    seen.Add((s.Axis[0], s.Axis[2]));
                }
            });
            for (int v = 0; v < 200; v++) client.Send("s " + id + " a0=" + (1000 + v) + " a2=" + (1000 + v));
            Assert.Equal("@1 ok", client.Final("@1 s " + id + " b9=1 a24=5000 h0=27000 gyro=0.5,1,-1 t0.1=0.25,0.75,1 tc0=1 bat=80 charging=1"));
            await reader;
            Assert.All(seen, pair => Assert.True(pair.Item1 == pair.Item2 || pair.Item1 == 32768, pair.ToString()));
            var state = device.PeekState();
            Assert.True(state.Buttons[9]);
            Assert.Equal(5000, state.Sliders[0]);
            Assert.Equal(27000, state.Povs[0]);
            Assert.Equal(new[] { 0.5f, 1f, -1f }, state.Gyro);
            Assert.True(state.Touchpads[0].FingerDown[1]);
            Assert.Equal(0.75f, state.Touchpads[0].FingerY[1]);
            Assert.True(state.Touchpads[0].Clicked);
            Assert.True(state.Buttons[16], "the first pad's click rides button 16");
            Assert.Equal(80, state.BatteryPercent);
            Assert.True(state.BatteryCharging);
            // A control the device does not have is left out.
            Assert.Equal("@2 ok", client.Final("@2 s " + id + " b5=1 a7=100"));
            Assert.False(device.PeekState().Buttons[5]);
            Assert.StartsWith("err bad-argument ", client.Final("s " + id + " b0=2"));
            Assert.StartsWith("err bad-argument ", client.Final("s " + id + " wings=1"));
            using var other = rig.Open("Other");
            Assert.StartsWith("err not-owner ", other.Final("s " + id + " b0=1"));
        }

        [Fact]
        public async Task ADevice_GoesOfflineAtSessionEnd_WithItsOutputsAtRest()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var client = rig.Open();
            string id = client.Final("declare key=rig name=\"Sim\" " + Shape).Substring(6);
            var entry = rig.Host.Sessions.Devices.Find(id);
            var ud = SettingsManager.FindDeviceByInstanceGuid(entry.Guid);
            var setting = new UserSetting { InstanceGuid = entry.Guid, MapTo = 0 };
            lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(setting);
            setting.OutputState = new Gamepad { Buttons = Gamepad.A };
            Assert.Equal("@1 ok", client.Final("@1 s " + id + " b0=1 a0=60000"));
            Assert.True(entry.Device.PeekState().Buttons[0]);

            client.Dispose();
            await AutomationPipeRig.Until(() => rig.Host.Sessions.Count == 0);
            // At rest at once, and still online for a program that comes back.
            Assert.False(entry.Device.PeekState().Buttons[0]);
            Assert.Equal(32768, entry.Device.PeekState().Axis[0]);
            Assert.True(ud.IsOnline);
            // Going offline marks the row offline first and then puts its
            // mapped outputs at rest (InputManager.NeutralizeMappedOutputsFor),
            // so the engine stops writing them before they are cleared. The
            // test waits on the outputs themselves.
            await AutomationPipeRig.Until(() => !ud.IsOnline);
            await AutomationPipeRig.Until(() => setting.OutputState.Buttons == 0);
            Assert.Equal(0, setting.OutputState.Buttons);
            // The row stays, as any disconnected device's does.
            Assert.NotNull(SettingsManager.FindDeviceByInstanceGuid(entry.Guid));
        }

        [Fact]
        public async Task AProgramThatDropsAndComesBack_KeepsOneDevice_AndItsMappings()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var first = rig.Open("Bridge");
            string id = first.Final("declare key=rig name=\"Sim\" " + Shape).Substring(6);
            var entry = rig.Host.Sessions.Devices.Find(id);
            var setting = new UserSetting { InstanceGuid = entry.Guid, MapTo = 3 };
            lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(setting);
            entry.Device.SetRumble(1000, 2000);
            entry.Device.SetLed(255, 128, 0);
            Assert.Equal("e rumble " + id + " 1000 2000", first.Event());
            Assert.Equal("e led " + id + " ff8000", first.Event());
            first.Dispose();
            await AutomationPipeRig.Until(() => rig.Host.Sessions.Count == 0);

            // Back inside the wait: the same device, never offline.
            using var again = rig.Open("Bridge");
            Assert.Equal("ok id=" + id, again.Final("declare key=rig name=\"Sim Wheel\" {\"buttons\":[\"Fire\"],\"feedback\":[\"rumble\",\"light\"]}"));
            Assert.Equal("e rumble " + id + " 1000 2000", again.Event());
            Assert.Equal("e led " + id + " ff8000", again.Event());
            await Task.Delay(500);
            var ud = SettingsManager.FindDeviceByInstanceGuid(entry.Guid);
            Assert.True(ud.IsOnline);
            Assert.Equal("Sim Wheel", ud.ProductName);
            Assert.Equal(new[] { 0 }, ud.CapButtonIndices);
            Assert.Equal(3, setting.MapTo);
            Assert.DoesNotContain("unregister Sim", rig.Engine.Calls);
            Assert.Single(SettingsManager.UserDevices.Items, d => d.InstanceGuid == entry.Guid);

            // Another program may not take a key that is in use.
            using var thief = rig.Open("Bridge");
            Assert.StartsWith("err busy ", thief.Final("declare key=rig name=\"Sim\" {}"));
        }

        [Fact]
        public void Feedback_ArrivesAsEvents()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            string id = client.Final("declare key=rig name=\"Sim\" " + Shape).Substring(6);
            var device = rig.Host.Sessions.Devices.Find(id).Device;
            Assert.True(device.SetRumble(10, 20));
            Assert.True(device.SetTriggerRumble(30, 40));
            device.SetLed(1, 2, 3);
            device.SetPlayerNumber(2);
            device.SetPlayerNumber(2);
            Assert.Equal("e rumble " + id + " 10 20", client.Event());
            Assert.Equal("e trigrumble " + id + " 30 40", client.Event());
            Assert.Equal("e led " + id + " 010203", client.Event());
            Assert.Equal("e player " + id + " 2", client.Event());
            Assert.Empty(client.Drain());

            // Trigger rumble reaches the device through the engine's gate,
            // which no longer asks for a gamepad handle it does not have.
            var ffb = new ForceFeedbackState();
            ffb.StopDeviceForces(device);
            Assert.Equal("e rumble " + id + " 0 0", client.Event());
            Assert.Equal("e trigrumble " + id + " 0 0", client.Event());
            var ud = SettingsManager.FindDeviceByInstanceGuid(device.InstanceGuid);
            ffb.SetDeviceForces(ud, device, new PadSetting(),
                new Vibration { LeftMotorSpeed = 1000, RightMotorSpeed = 2000, LeftTriggerMotorSpeed = 300, RightTriggerMotorSpeed = 400 });
            var sent = client.Drain(300);
            Assert.Contains(sent, e => e.StartsWith("e rumble " + id + " ", StringComparison.Ordinal) && e != "e rumble " + id + " 0 0");
            Assert.Contains("e trigrumble " + id + " 300 400", sent);
        }

        [Fact]
        public void Gone_TakesTheDeviceOfflineAtOnce_AndAnAssignedOneKeepsItsRow()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            string id = client.Final("declare key=rig name=\"Sim\" {}").Substring(6);
            var guid = rig.Host.Sessions.Devices.Find(id).Guid;
            // The user assigned it: a row of its mappings stands.
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = guid, MapTo = 0 });
            Assert.Equal("ok", client.Final("gone " + id));
            Assert.False(SettingsManager.FindDeviceByInstanceGuid(guid).IsOnline);
            Assert.StartsWith("err not-owner ", client.Final("gone " + id));
        }

        [Fact]
        public void ADeviceDeclaredWhileTheEngineIsStopped_JoinsItAtItsStart()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs, engine: false);
            using var client = rig.Open();
            string id = client.Final("declare key=rig name=\"Sim\" " + Shape).Substring(6);
            Assert.Contains(client.Ask("devices"), r => r.StartsWith("device id=" + id + " type=program online=0", StringComparison.Ordinal));
            Assert.StartsWith("ok b=", client.Final("read " + id));
            using var manager = new InputManager();
            var engine = new AutomationTestEngine(manager);
            rig.Host.AttachEngine(engine);
            Assert.Equal(new[] { "register Sim" }, engine.Calls);
            Assert.True(SettingsManager.FindDeviceByInstanceGuid(rig.Host.Sessions.Devices.Find(id).Guid).IsOnline);
        }

        // ── Holds ──

        [Fact]
        public void AHold_IsAppliedWhereMappingsRead_AndTheDevicesOwnStateStays()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var wrapper = new WebControllerDevice(Guid.NewGuid().ToString(), "Phone");
            wrapper.SetConnected(true);
            var ud = new UserDevice { InstanceGuid = wrapper.InstanceGuid, Device = wrapper, InputState = new CustomInputState(), IsOnline = true, CapType = InputDeviceType.Gamepad };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            wrapper.UpdateButton(0, true);
            using var client = rig.Open();
            Assert.Equal("ok", client.Final("own " + AutomationPipeRig.IdOf(ud) + " b0=0 b5=1 a1=100 h0=18000"));

            typeof(InputManager).GetMethod("UpdateInputStates", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(rig.Engine.Manager, null);
            Assert.False(ud.InputState.Buttons[0]);
            Assert.True(ud.InputState.Buttons[5]);
            Assert.Equal(100, ud.InputState.Axis[1]);
            Assert.Equal(18000, ud.InputState.Povs[0]);
            Assert.True(ud.ReadInputState.Buttons[0], "the device's own state is the press");
            Assert.False(ud.ReadInputState.Buttons[5]);
        }

        [Fact]
        public void ASecondHolder_IsRefused()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var first = rig.Open("First");
            using var second = rig.Open("Second");
            Assert.Equal("ok", first.Final("own " + id + " b3=1"));
            Assert.StartsWith("err busy ", second.Final("own " + id + " b2=1 b3=0"));
            Assert.DoesNotContain(ControlHolds.Current[pad.InstanceGuid].Buttons, b => b.Index == 2);
            Assert.Equal("ok", first.Final("own " + id + " b3=0"));
            Assert.StartsWith("err bad-argument ", first.Final("own " + id + " b3=2"));
            Assert.StartsWith("err unknown-device ", first.Final("own nosuchid b3=1"));
        }

        [Fact]
        public async Task AHold_EndsWithRel_TheSession_TheLease_AndTheLevel()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);

            // rel
            using (var client = rig.Open())
            {
                Assert.Equal("ok", client.Final("own " + id + " b1=1 a0=5"));
                Assert.Equal("ok", client.Final("rel " + id + " b1"));
                Assert.DoesNotContain(ControlHolds.Current[pad.InstanceGuid].Buttons, b => b.Index == 1);
                Assert.Equal("ok", client.Final("rel " + id + " all"));
                Assert.Null(ControlHolds.Current);
            }

            // The session's end
            var leaving = rig.Open();
            Assert.Equal("ok", leaving.Final("own " + id + " b1=1"));
            Assert.Equal("ok bye", leaving.Final("bye"));
            await AutomationPipeRig.Until(() => ControlHolds.Current == null);
            leaving.Dispose();

            // The lease
            using (var hung = rig.Open("Hung", " lease=250"))
            {
                Assert.Equal("ok", hung.Final("own " + id + " b1=1"));
                Assert.NotNull(ControlHolds.Current);
                Assert.Equal("e lease lapsed", hung.Event(3000));
                Assert.Null(ControlHolds.Current);
            }

            // The level
            using (var client = rig.Open())
            {
                Assert.Equal("ok", client.Final("own " + id + " b1=1"));
                rig.Host.Apply(true, AutomationLevel.Read);
                Assert.Null(ControlHolds.Current);
            }
        }

        [Fact]
        public async Task TheLease_RestsDevicesUntilTheProgramSpeaks_AndAReaderHasNone()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var reader = rig.Open("Reader", " lease=250");
            Assert.Equal("ok", reader.Final("devices"));
            using var driver = rig.Open("Driver", " lease=250");
            string id = driver.Final("declare key=rig name=\"Sim\" " + Shape).Substring(6);
            var device = rig.Host.Sessions.Devices.Find(id).Device;
            Assert.Equal("@1 ok", driver.Final("@1 s " + id + " b0=1"));
            Assert.Equal("e lease lapsed", driver.Event(3000));
            Assert.False(device.PeekState().Buttons[0]);
            Assert.True(device.IsAttached, "a lapsed program's device stays online");
            Assert.Equal("@2 ok", driver.Final("@2 s " + id + " b0=1"));
            Assert.True(device.PeekState().Buttons[0]);
            await Task.Delay(500);
            Assert.DoesNotContain("e lease lapsed", reader.Drain());
            Assert.StartsWith("err bad-argument ", rig.Connect().Final("hello name=x lease=100"));
        }

        // ── Named values ──

        [Fact]
        public void ANamedValue_IsReadByACustomExpressionTrigger_AndReadsZeroOnceItsProgramIsGone()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var variable = new MacroExpressionVariable { Spec = "val:speed" };
            Assert.Equal(MacroTriggerSource.ScriptValue, variable.Source);
            Assert.Equal("speed", variable.ScriptValue);
            Assert.True(variable.IsBound);
            Assert.Equal("val:speed", variable.Spec);
            Assert.Equal(3, (int)MacroTriggerSource.ScriptValue);
            // Device Connection (#600 item 43), appended after it.
            Assert.Equal(4, (int)MacroTriggerSource.DeviceConnection);
            Assert.Equal(MacroTriggerSource.DeviceConnection, Enum.GetValues<MacroTriggerSource>().Max());

            var read = typeof(InputManager).GetMethod("ReadExpressionVariable", BindingFlags.NonPublic | BindingFlags.Instance);
            float Read() => (float)read.Invoke(rig.Engine.Manager, new object[] { variable, default(Gamepad), 0 });
            Assert.Equal(0f, Read());

            var client = rig.Open();
            Assert.Equal("ok", client.Final("var speed=42.5 gear=3"));
            Assert.Equal(42.5f, Read());
            Assert.Equal(3f, ScriptValues.Read("GEAR"));
            Assert.Contains("speed", ScriptValues.Names());
            Assert.StartsWith("err bad-argument ", client.Final("var 9lives=1"));
            Assert.StartsWith("err bad-argument ", client.Final("var speed=fast"));
            Assert.Equal("ok bye", client.Final("bye"));
            client.Dispose();
            SpinWait.SpinUntil(() => rig.Host.Sessions.Count == 0, 3000);
            Assert.Equal(0f, Read());
        }

        [Fact]
        public void TheExpressionEditor_OffersEveryNameAProgramSet()
        {
            ScriptValues.ResetForTest();
            try
            {
                var items = ScriptValueNames.Items;
                Assert.True(ScriptValues.Set("boostLevel", 1, 99));
                Assert.True(ScriptValues.Set("BOOSTLEVEL", 2, 99));
                // The list is the interface thread's, where the name is added.
                Assert.True(SpinWait.SpinUntil(() => items.Contains("boostLevel"), 3000));
                Thread.Sleep(100);
                Assert.Single(items, n => string.Equals(n, "boostLevel", StringComparison.OrdinalIgnoreCase));
                Assert.False(ScriptValues.Set("has:colon", 1, 99));
                Assert.False(ScriptValues.Set("pipe|name", 1, 99));
                Assert.False(ScriptValues.Set("x", float.NaN, 99));
                var variable = new MacroExpressionVariable { Source = MacroTriggerSource.ScriptValue };
                Assert.False(variable.IsBound);
                Assert.Equal(string.Empty, variable.Spec);
                variable.ScriptValue = "boostLevel";
                Assert.Equal("boostLevel", variable.DisplaySummary);
                Assert.True(MacroExpressionVariable.CanResetSetting(nameof(MacroExpressionVariable.ScriptValue)));
                variable.ResetSettingCommand.Execute(nameof(MacroExpressionVariable.ScriptValue));
                Assert.Equal(string.Empty, variable.ScriptValue);
            }
            finally
            {
                ScriptValues.ResetForTest();
            }
        }

        // ── Activity ──

        [Fact]
        public void AStreamOfSetLines_IsOneActivityEntryWithItsCount()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open("Streamer");
            string id = client.Final("declare key=rig name=\"Sim\" {\"axes\":[\"X\"]}").Substring(6);
            for (int i = 0; i < 500; i++) client.Send("s " + id + " a0=" + i);
            Assert.StartsWith("ok", client.Final("version"));
            var entries = rig.Host.Activity.Snapshot();
            var set = Assert.Single(entries, e => e.Asked.Kind == AutomationAskKind.Set);
            Assert.Equal(500, set.Asked.Count);
            Assert.Equal("Streamer", set.Program.Name);
            Assert.Equal(Environment.ProcessId, set.Program.ProcessId);
            Assert.Contains(entries, e => e.Asked.Kind == AutomationAskKind.Connected);
            Assert.Contains(entries, e => e.Asked.Kind == AutomationAskKind.Declare && e.Asked.Argument == "Sim");
            Assert.Contains("500", set.Asked.Text);
        }

        // ── The app's own pipe ──

        [Fact]
        public void TheAppsOwnAutomationPipe_IsNeverServedByATest()
        {
            Assert.Equal("PadForge.Automation", AutomationPipe.PipeName);
            Assert.True(AutomationPipe.OffLimitsForTest);
            foreach (string name in new[] { null, "", AutomationPipe.PipeName, "padforge.automation" })
            {
                using var pipe = new AutomationPipe((_, _, _) => Task.CompletedTask, name);
                Assert.Equal(ExternalControlService.PipeState.Stopped, pipe.Start());
            }
        }

        [Fact]
        public void ANameAnotherProgramHolds_IsSaidAndTriedAgain()
        {
            string name = "PadForge.Tests.Taken." + Guid.NewGuid().ToString("N");
            using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances);
            using var pipe = new AutomationPipe((_, _, _) => Task.CompletedTask, name);
            Assert.Equal(ExternalControlService.PipeState.NameTaken, pipe.Start());
            var none = Array.Empty<AutomationClient>();
            var one = new[] { new AutomationClient(1, "Playnite", AutomationClientKind.Script, 10, "Playnite.DesktopApp.exe", DateTimeOffset.Now) };
            string taken = MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Serving, ExternalControlService.PipeState.NameTaken, none);
            string failed = MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Serving, ExternalControlService.PipeState.Failed, none);
            Assert.Contains(AutomationPipe.PipeName, taken);
            Assert.Contains(AutomationPipe.PipeName, failed);
            Assert.DoesNotContain(ExternalControlService.PipeName, taken);
            Assert.NotEqual(taken, failed);
            // The Automation pipe's refusal is said in place of the programs
            // connected over the control pipe.
            Assert.Equal(taken, MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Serving, ExternalControlService.PipeState.NameTaken, one));
            // With the Automation pipe serving, the line names the programs.
            Assert.Contains("Playnite", MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Serving, ExternalControlService.PipeState.Serving, one));
            // A control pipe that is stopped or refused says so, whatever the
            // Automation pipe does.
            Assert.Equal(MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Stopped, ExternalControlService.PipeState.Stopped, none),
                MainWindow.AutomationStatusLine(ExternalControlService.PipeState.Stopped, ExternalControlService.PipeState.NameTaken, none));
            Assert.Contains(ExternalControlService.PipeName,
                MainWindow.AutomationStatusLine(ExternalControlService.PipeState.NameTaken, ExternalControlService.PipeState.NameTaken, none));
        }
    }
}
