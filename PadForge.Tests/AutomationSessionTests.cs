using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// One session of the Automation pipe from its hello to its end: what a
    /// hello keeps, how long a line may run, what ends a session, and the
    /// answers a tool's line gets.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationSessionTests
    {
        [Fact]
        public void AHelloRefusedWithThePipeOpen_LeavesNothingForTheNextHello()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.ChangeSettings);
            var client = rig.Connect();
            Assert.StartsWith("err bad-argument ", client.Final("hello units=plain level=change-settings lease=5000 reach=bogus"));
            string ok = client.Final("hello kind=script");
            Assert.StartsWith("ok padforge=", ok);
            Assert.DoesNotContain("units=plain", ok);
            var session = rig.Host.Sessions.Snapshot().Single();
            Assert.Null(session.Wanted);
            Assert.False(session.Outbox.Plain);
            Assert.Equal(AutomationSession.ScriptLeaseMs, session.LeaseMs);
        }

        [Fact]
        public void ACallOrSettingLine_RunsPastTheLongestLine_AndStopsAtTheLongestJsonLine()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var client = rig.Open();
            string call = "@1 call import_profile {\"content_base64\":\"" + new string('A', 10 * 1024) + "\"}";
            Assert.DoesNotContain("too-long", client.Final(call));
            string setting = "@2 setting choice key=k kind=choice {\"choices\":[\"" + new string('c', 10 * 1024) + "\"]}";
            Assert.DoesNotContain("too-long", client.Final(setting));
            string tooBig = "@3 call import_profile {\"content_base64\":\"" + new string('A', AutomationLine.LongestJsonLine) + "\"}";
            Assert.StartsWith("@3 err too-long ", client.Final(tooBig));
            Assert.StartsWith("@4 err too-long ", client.Final("@4 beat" + new string(' ', AutomationLine.LongestLine)));
        }

        [Fact]
        public async Task AFaultInALine_EndsTheSession_AndLetsGoOfIt()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            using var client = rig.Open();
            Assert.Equal(1, rig.Host.Sessions.Count);
            rig.Host.Sessions.AfterTheLevelCheck = () => throw new InvalidOperationException("a fault of the test's own");
            client.Send("own " + AutomationPipeRig.IdOf(pad) + " b0=1");
            await AutomationPipeRig.Until(() => rig.Host.Sessions.Count == 0);
            Assert.Empty(rig.Host.Clients.Snapshot());
        }

        [Fact]
        public void AToolsLine_ReadsItsOwnAnswer_ForAScriptsCallToo()
        {
            using var rig = new AssistantRig(AutomationLevel.SendInputs);
            using var first = new AutomationTestClient(rig.PipeName);
            Assert.StartsWith("ok padforge=", first.Final("hello name=\"First\" kind=script"));
            string id = first.Final("declare key=k name=\"Held Pad\" {\"buttons\":[\"A\"]}").Substring(6);
            Assert.Equal("@1 ok", first.Final("@1 own " + id + " b0=1"));
            using var second = new AutomationTestClient(rig.PipeName);
            Assert.StartsWith("ok padforge=", second.Final("hello name=\"Second\" kind=script"));
            string held = second.Final("@1 call hold_control {\"device\":\"" + id + "\",\"control\":\"b0\",\"value\":true}");
            Assert.StartsWith("@1 err busy ", held);
            // The tool's own line was never written to the script.
            Assert.DoesNotContain(second.Drain(), l => l.StartsWith(AutomationSession.CaptureTag + " ", StringComparison.Ordinal));
        }

        [Fact]
        public void ANetworkCallersLine_SaysTodayOnlyOnTheDayItsKeyWasMade()
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 25, 23, 0, 0));
            var made = new DateTimeOffset(2026, 11, 25, 23, 0, 0, offset);
            var now = made.AddMinutes(30);
            string name = Guid.NewGuid().ToString("N");
            using var host = new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(), "PadForge.Tests.Control." + name,
                now: () => now, automationPipeName: "PadForge.Tests.Automation." + name);
            var session = new AutomationSession(host.Sessions, new MemoryStream(), null, 1, CancellationToken.None, () => 0);
            session.AsksFromNetwork("Office PC", "10.0.0.2", made);
            var s = PadForge.Resources.Strings.Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            Assert.Equal(string.Format(culture, s.Net_Asks_Today_Format, "Office PC", "10.0.0.2", made.ToString("d", culture)), session.AskerLine);
            now = made.AddMinutes(75);
            Assert.Equal(string.Format(culture, s.Net_Asks_Format, "Office PC", "10.0.0.2", made.ToString("d", culture)), session.AskerLine);
        }
        // ── The verbs ──

        [Fact]
        public void Maximize_IsACommandsVerb_OnTheAutomationPipe()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            using var client = rig.Open();
            Assert.Equal("@1 ok", client.Final("@1 maximize"));
            Assert.Contains("maximize", rig.Target.Calls);
        }

        [Fact]
        public void ACallAndMaximize_DoNotWaitForTheCard_WhenTheirLevelIsAllowed()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            TestConsent.Empty(rig.Host);
            using var client = new AutomationTestClient(rig.PipeName);
            Assert.Contains("consent=waiting", client.Final("hello name=\"Waiter\" kind=script level=read"));
            Assert.StartsWith("@1 ok", client.Ask("@1 call status {}", 10000).Last());
            Assert.Equal("@2 ok", client.Ask("@2 maximize", 10000).Last());
            Assert.Single(rig.Host.Approvals.Pending);
        }

        [Fact]
        public void AClaimRefused_ByItsLevelOrReach_IsAnsweredAsOwnIs()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var reader = rig.Open();
            Assert.StartsWith("@1 err not-allowed ", reader.Final("@1 sub " + id + " A claim"));
            rig.Host.Apply(true, AutomationLevel.SendInputs);
            using var own = rig.Open("Own Only", " reach=own");
            Assert.StartsWith("@2 err outside-ask ", own.Final("@2 sub " + id + " A claim"));
        }

        [Fact]
        public void AReachNarrowed_BetweenTheCheckAndTheChange_SetsAndHoldsNothing()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open();
            var session = rig.Host.Sessions.Snapshot().Single();
            NarrowOnce(rig, session.Who.Key);
            Assert.StartsWith("@1 err outside-reach ", client.Final("@1 var speed=1"));
            Assert.DoesNotContain("speed", ScriptValues.Names());
            rig.Host.Sessions.SetReach(session.Who.Key, null);
            NarrowOnce(rig, session.Who.Key);
            Assert.StartsWith("@2 err outside-reach ", client.Final("@2 own " + id + " b0=1"));
            Assert.Equal(0, rig.Host.Sessions.HoldsOf(session));
        }

        private static void NarrowOnce(AutomationPipeRig rig, string key)
        {
            int once = 0;
            rig.Host.Sessions.AfterTheLevelCheck = () =>
            {
                if (Interlocked.Exchange(ref once, 1) == 0) rig.Host.Sessions.SetReach(key, ProgramReach.OwnDevices);
            };
        }

        [Fact]
        public void APluginBelowTheLevel_LetsGoOfOtherDevices_AndKeepsItsOwn()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            rig.Host.AcceptPluginToken = (token, pid) => token == "right" ? new AutomationPluginGrant("Bridge") : null;
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            var keyboard = rig.AddDevice(InputDeviceType.Keyboard, "Keys");
            using var plugin = rig.Connect();
            Assert.StartsWith("ok ", plugin.Final("hello name=x kind=plugin token=right"));
            string own = plugin.Final("declare key=w name=\"Wheel\" {\"axes\":[\"Steer\"]}").Substring(6);
            Assert.StartsWith("ok", plugin.Final("sub " + AutomationPipeRig.IdOf(keyboard) + " buttons"));
            Assert.StartsWith("ok", plugin.Final("own " + AutomationPipeRig.IdOf(pad) + " b0=1"));
            var session = rig.Host.Sessions.Snapshot().Single(s => s.IsPlugin);
            Assert.Equal(1, rig.Host.Sessions.HoldsOf(session));
            Assert.Equal(1, rig.Host.Sessions.SubscriptionsOf(session));
            rig.Host.Apply(true, AutomationLevel.Commands);
            Assert.Equal(0, rig.Host.Sessions.HoldsOf(session));
            Assert.Equal(0, rig.Host.Sessions.SubscriptionsOf(session));
            // Its own device stays its own.
            Assert.Same(session, rig.Host.Sessions.Devices.Find(own).Owner);
        }

        // ── The consent flow (#600) ──

        private const string FlowPython = @"C:\Python314\python.exe";
        private const string FlowScript = @"C:\Tools\flow_bridge.py";

        private static (AutomationPipeRig Rig, AutomationConsent Consent) FlowRig(AutomationLevel level = AutomationLevel.ChangeSettings)
        {
            var rig = new AutomationPipeRig(level);
            var consent = TestConsent.Empty(rig.Host);
            int next = 524200;
            rig.Host.AutomationPipe.ProcessOf = _ => AutomationClientProcess.ForTest(Interlocked.Increment(ref next), FlowPython);
            AutomationConsent.CommandLineSeam = _ => "\"" + FlowPython + "\" " + FlowScript;
            return (rig, consent);
        }

        private static string FlowKey => AutomationConsent.KeyOf("script", FlowPython, FlowScript);

        [Fact]
        public async Task TwoConnectionsOnOneCard_EachWorkAtNoMoreThanTheCardAsked()
        {
            var (rig, consent) = FlowRig();
            using var _ = rig;
            try
            {
                var first = rig.Connect();
                first.Final("hello name=\"Two\" kind=script level=read");
                var second = rig.Connect();
                second.Final("hello name=\"Two\" kind=script level=change-settings");
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, true));
                await AutomationPipeRig.Until(() => consent.Programs.Count == 1);
                Assert.Equal(AutomationLevel.Read, consent.Programs[0].Level);
                foreach (var session in rig.Host.Sessions.Snapshot())
                    Assert.Equal(AutomationLevel.Read, rig.Host.Sessions.LevelOf(session));
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public async Task ACardNeverPut_ForTooManyWaiting_AsksAgainOnceThereIsRoom()
        {
            var (rig, consent) = FlowRig();
            using var _ = rig;
            try
            {
                var waiting = new System.Collections.Generic.List<Task<ApprovalAnswer>>();
                for (int i = 0; i < AutomationApprovals.MostPerProgram; i++)
                    waiting.Add(rig.Host.Approvals.AskAsync(new ApprovalRequest(new AutomationProgram("Other", 7, "python.exe", FlowPython, null), "other", Array.Empty<string>())));
                var fourth = rig.Connect();
                Assert.DoesNotContain("consent=waiting", fourth.Final("hello name=\"Fourth\" kind=script level=read"));
                foreach (var pending in rig.Host.Approvals.Pending) rig.Host.Approvals.Answer(pending.Id, false);
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 0);
                var again = rig.Connect();
                Assert.Contains("consent=waiting", again.Final("hello name=\"Fourth\" kind=script level=read"));
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public async Task AConnectionWhileTheAllowIsKept_JoinsTheAnswer_AndPutsNoSecondCard()
        {
            var (rig, consent) = FlowRig();
            using var _ = rig;
            try
            {
                var first = rig.Connect();
                first.Final("hello name=\"Joiner\" kind=script level=read");
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                AutomationTestClient second = null;
                int once = 0;
                rig.Host.Sessions.AfterTheConsentAnswer = () =>
                {
                    if (Interlocked.Exchange(ref once, 1) != 0) return;
                    second = rig.Connect();
                    second.Final("hello name=\"Joiner\" kind=script level=read");
                };
                Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, true));
                await AutomationPipeRig.Until(() => consent.Programs.Count == 1 && rig.Host.Sessions.Count == 2);
                await Task.Delay(200);
                Assert.Empty(rig.Host.Approvals.Pending);
                await AutomationPipeRig.Until(() => rig.Host.Sessions.Snapshot().All(s => rig.Host.Sessions.LevelOf(s) == AutomationLevel.Read));
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
                rig.Host.Sessions.AfterTheConsentAnswer = null;
            }
        }

        [Fact]
        public async Task ARemoveBetweenTheStoresReadAndTheGrant_LeavesTheSessionAtCommands()
        {
            var (rig, consent) = FlowRig();
            using var _ = rig;
            try
            {
                consent.Allow(new AllowedProgram(FlowKey, "Removed", "script", FlowPython, FlowScript, null, AutomationLevel.Read));
                int once = 0;
                rig.Host.Sessions.BetweenTheStoreAndTheGrant = () =>
                {
                    if (Interlocked.Exchange(ref once, 1) != 0) return;
                    Task.Run(() => rig.Host.Sessions.Revoke(FlowKey)).Wait(300);
                };
                var client = rig.Connect();
                client.Final("hello name=\"Removed\" kind=script level=read");
                var session = rig.Host.Sessions.Snapshot().Single();
                await AutomationPipeRig.Until(() => consent.Programs.Count == 0);
                await AutomationPipeRig.Until(() => rig.Host.Sessions.LevelOf(session) == AutomationLevel.Commands, 2000);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
                rig.Host.Sessions.BetweenTheStoreAndTheGrant = null;
            }
        }

        [Fact]
        public async Task AnAllowedProgramsRow_ReadsItsOwnAsk_WithTheConnectionsFirstChange()
        {
            var (rig, consent) = FlowRig();
            using var _ = rig;
            try
            {
                consent.Allow(new AllowedProgram(FlowKey, "Own Ask", "script", FlowPython, FlowScript, null, AutomationLevel.ChangeSettings));
                var page = new AutomationConsentForPage(consent, rig.Host.Sessions, rig.Host.Clients);
                bool? seen = null;
                page.Changed += (s, e) => seen ??= page.Allowed.Single().AsksOwnNow;
                var client = rig.Connect();
                Assert.Contains(" level=read ", client.Final("hello name=\"Own Ask\" kind=script level=read reach=own"));
                await AutomationPipeRig.Until(() => seen != null);
                Assert.True(seen);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public async Task TheResumeSecretsOfEndedSessions_GoAfterTheirGrace()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            rig.Host.Sessions.ResumeKeep = TimeSpan.Zero;
            for (int i = 0; i < 300; i++)
            {
                using var client = rig.Connect();
                client.Final("hello name=\"Looper\" kind=script resume=" + i.ToString("D16"));
                client.Final("bye");
            }
            await AutomationPipeRig.Until(() => rig.Host.Sessions.Count == 0);
            await AutomationPipeRig.Until(() => rig.Host.Sessions.ResumesKept == 0);
        }

        // ── Who a connection is (#600) ──

        [Fact]
        public void AScriptNamedRelative_IsNamedAsItsCommandLineWroteIt()
        {
            try
            {
                AutomationConsent.CommandLineSeam = _ => "python.exe bot.py";
                Assert.Equal("bot.py", AutomationConsent.ScriptOf(1, "python.exe"));
                AutomationConsent.CommandLineSeam = _ => "pwsh.exe -File tools\\run.ps1";
                Assert.Equal(@"tools\run.ps1", AutomationConsent.ScriptOf(1, "pwsh.exe"));
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public void ARunWithNoScriptFile_IsKeyedByTheWordsAfterItsInterpreter()
        {
            const string pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
            try
            {
                var process = AutomationClientProcess.ForTest(4250, pwsh);
                AutomationConsent.CommandLineSeam = _ => "\"" + pwsh + "\" -Command a";
                var first = AutomationConsent.Identify(process, "script", "ToolA");
                AutomationConsent.CommandLineSeam = _ => "\"" + pwsh + "\" -EncodedCommand b";
                var second = AutomationConsent.Identify(process, "script", "ToolA");
                Assert.NotEqual(first.Key, second.Key);
                Assert.Equal("-Command a", first.Script);
                Assert.Equal("-EncodedCommand b", second.Script);
                // A command's words keep their case in the key.
                AutomationConsent.CommandLineSeam = _ => "\"" + pwsh + "\" -Command A";
                Assert.NotEqual(first.Key, AutomationConsent.Identify(process, "script", "ToolA").Key);
                // A long command is named by its start, and keyed by all of it.
                string longer = new string('x', 5000);
                AutomationConsent.CommandLineSeam = _ => "\"" + pwsh + "\" -EncodedCommand " + longer + "1";
                var third = AutomationConsent.Identify(process, "script", "ToolA");
                AutomationConsent.CommandLineSeam = _ => "\"" + pwsh + "\" -EncodedCommand " + longer + "2";
                var fourth = AutomationConsent.Identify(process, "script", "ToolA");
                Assert.NotEqual(third.Key, fourth.Key);
                Assert.True(third.Script.Length < 300);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public void AHelperWhoseStarterIsGone_IsAnUnknownProgram_NeverPadForgeItself()
        {
            try
            {
                AutomationConsent.ParentSeam = _ => 0;
                var helper = AutomationClientProcess.ForTest(424310, Environment.ProcessPath);
                var who = AutomationConsent.Identify(helper, "script", null);
                Assert.Equal("command-line", who.Kind);
                Assert.Null(who.Path);
                Assert.True(who.Unknown);
                Assert.NotEqual(AutomationConsent.KeyOf("command-line", Environment.ProcessPath, null), who.Key);
                var detail = AutomationSessions.ConsentDetail(who, null);
                var s = PadForge.Resources.Strings.Strings.Instance;
                Assert.Contains(s.Automation_Consent_StartedByUnknown, detail);
                Assert.DoesNotContain(detail, d => d.Contains(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(s.Automation_Consent_AsksOnce, detail);
            }
            finally
            {
                AutomationConsent.ParentSeam = null;
            }
        }

        [Fact]
        public async Task DontAllowForAProgramNobodyCanName_IsNotKept_SoALaterProcessOfItsIdIsAsked()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.ChangeSettings);
            var consent = TestConsent.Empty(rig.Host);
            try
            {
                // A helper whose starter is gone, then a later one Windows
                // gives the same process ID once the first has ended.
                AutomationConsent.ParentSeam = _ => 0;
                rig.Host.AutomationPipe.ProcessOf = _ => AutomationClientProcess.ForTest(424320, Environment.ProcessPath);
                var first = rig.Connect();
                Assert.Contains("consent=waiting", first.Final("hello kind=script level=read"));
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, false));
                // A line past Commands waits for the answer to be kept.
                Assert.StartsWith("@1 err not-allowed", first.Final("@1 devices"));
                first.Dispose();
                await AutomationPipeRig.Until(() => rig.Host.Sessions.Count == 0);
                var later = rig.Connect();
                Assert.Contains("consent=waiting", later.Final("hello kind=script level=read"));
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                Assert.Empty(consent.Programs);
            }
            finally
            {
                AutomationConsent.ParentSeam = null;
            }
        }

        [Fact]
        public void TheRealParentOfThisProcess_StartedBeforeIt_AndIsRead()
        {
            int parent = AutomationConsent.ParentOf(Environment.ProcessId, out string path);
            Assert.True(parent > 0);
            Assert.False(string.IsNullOrEmpty(path));
        }

        [Fact]
        public void AStartWithAYoungerOrEqualTime_IsNotBeforeTheChilds()
        {
            Assert.True(AutomationConsent.StartedBefore(100, 200));
            Assert.False(AutomationConsent.StartedBefore(300, 200));
            Assert.False(AutomationConsent.StartedBefore(200, 200));
        }

        [Fact]
        public async Task AStarterYoungerThanItsHelper_IsAnUnknownProgram_KeyedByTheHelpersId_AndNoRowIsKept()
        {
            int helper = Environment.ProcessId;
            var process = AutomationClientProcess.ForTest(helper, Environment.ProcessPath);
            using var rig = new AutomationPipeRig(AutomationLevel.ChangeSettings);
            var consent = TestConsent.Empty(rig.Host);
            try
            {
                // The test host's parent may be an interpreter, whose command
                // line the test host never reads.
                AutomationConsent.CommandLineSeam = _ => null;
                // Read as Windows keeps them, the parent is older and is the
                // program that started the helper.
                int parent = AutomationConsent.ParentOf(helper, out string parentPath);
                Assert.True(parent > 0);
                var named = AutomationConsent.Identify(process, "script", null);
                Assert.False(named.Unknown);
                Assert.Equal(parentPath, named.Path);
                // The parent's ID now names a process that started after the
                // helper: Windows gave it to a later program.
                AutomationConsent.StartSeam = id => id == helper ? 1000 : 2000;
                Assert.Equal(0, AutomationConsent.ParentOf(helper, out string younger));
                Assert.Null(younger);
                var who = AutomationConsent.Identify(process, "script", null);
                Assert.True(who.Unknown);
                Assert.Null(who.Path);
                Assert.Equal(AutomationConsent.KeyOf("command-line", "?" + helper, null), who.Key);
                // One that started at the same moment is no parent either.
                AutomationConsent.StartSeam = _ => 1000;
                Assert.Equal(0, AutomationConsent.ParentOf(helper, out _));
                // Allowed on its card, it works at the level for this
                // connection, and no row is kept.
                AutomationConsent.StartSeam = id => id == helper ? 1000 : 2000;
                rig.Host.AutomationPipe.ProcessOf = _ => process;
                var client = rig.Connect();
                Assert.Contains("consent=waiting", client.Final("hello kind=script level=read"));
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, true));
                var session = rig.Host.Sessions.Snapshot().Single();
                await AutomationPipeRig.Until(() => rig.Host.Sessions.LevelOf(session) == AutomationLevel.Read);
                Assert.Equal(who.Key, session.Who.Key);
                Assert.True(session.Who.Unknown);
                Assert.Empty(consent.Programs);
            }
            finally
            {
                AutomationConsent.StartSeam = null;
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public async Task AHelperWhoseStarterHasEnded_IsAnUnknownProgram_OnTheRealPath()
        {
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
            int starter;
            var start = new System.Diagnostics.ProcessStartInfo(cmd, "/d /c start \"\" /b \"" + ping + "\" -n 60 127.0.0.1 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // The starter's handle is closed before the read: an open handle
            // keeps an ended process open by its ID.
            using (var starting = System.Diagnostics.Process.Start(start))
            {
                starter = starting.Id;
                Assert.True(starting.WaitForExit(10000));
            }
            int helper = ChildOf(starter, "PING.EXE");
            Assert.True(helper > 0, "the starter's child was not found");
            try
            {
                Assert.Equal(0, AutomationConsent.ParentOf(helper, out string path));
                Assert.Null(path);
                var process = AutomationClientProcess.ForTest(helper, Environment.ProcessPath);
                var who = AutomationConsent.Identify(process, "script", null);
                Assert.True(who.Unknown);
                Assert.Null(who.Path);
                Assert.Equal(AutomationConsent.KeyOf("command-line", "?" + helper, null), who.Key);
                // Allowed on its card, it works at the level for this
                // connection, and no row is kept.
                using var rig = new AutomationPipeRig(AutomationLevel.ChangeSettings);
                var consent = TestConsent.Empty(rig.Host);
                rig.Host.AutomationPipe.ProcessOf = _ => process;
                var client = rig.Connect();
                Assert.Contains("consent=waiting", client.Final("hello kind=script level=read"));
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, true));
                var session = rig.Host.Sessions.Snapshot().Single();
                await AutomationPipeRig.Until(() => rig.Host.Sessions.LevelOf(session) == AutomationLevel.Read);
                Assert.Equal(who.Key, session.Who.Key);
                Assert.Empty(consent.Programs);
            }
            finally
            {
                try
                {
                    using var left = System.Diagnostics.Process.GetProcessById(helper);
                    left.Kill();
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }

        /// <summary>The process of an image whose recorded parent is
        /// <paramref name="parent"/>, read from a process snapshot, which
        /// keeps a parent's ID after the parent ends. 0 when none.</summary>
        private static int ChildOf(int parent, string image)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == new IntPtr(-1)) return 0;
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32W>() };
                for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                    if (entry.th32ParentProcessID == (uint)parent && string.Equals(entry.szExeFile, image, StringComparison.OrdinalIgnoreCase))
                        return (int)entry.th32ProcessID;
                return 0;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        private const uint TH32CS_SNAPPROCESS = 0x2;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [Fact]
        public void AnMcpHelloFromAnotherProgram_IsNamedByItsOwnPath_WithNoStartedByLine()
        {
            var who = AutomationConsent.Identify(AutomationClientProcess.ForTest(1, @"C:\x\client.exe"), "assistant", "client");
            Assert.False(who.ThroughHelper);
            var detail = AutomationSessions.ConsentDetail(who, null);
            var s = PadForge.Resources.Strings.Strings.Instance;
            Assert.DoesNotContain(detail, d => d.StartsWith(string.Format(s.Automation_Consent_Started_Format, string.Empty), StringComparison.Ordinal));
            Assert.Contains(@"C:\x\client.exe", detail);
        }

        [Fact]
        public void AnAllowedProgramsRow_NamesTheSignerReadAtItsLastConnection()
        {
            var (rig, consent) = FlowRig();
            using var _ = rig;
            try
            {
                consent.Allow(new AllowedProgram(FlowKey, "Signed Once", "script", FlowPython, FlowScript, "Example Corp", AutomationLevel.Read));
                var page = new AutomationConsentForPage(consent, rig.Host.Sessions, rig.Host.Clients);
                Assert.EndsWith("Example Corp.", page.Allowed.Single().Who);
                var client = rig.Connect();
                client.Final("hello name=\"Signed Once\" kind=script level=read");
                Assert.EndsWith(PadForge.Resources.Strings.Strings.Instance.Automation_Consent_NotSigned, page.Allowed.Single().Who);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public void AReachNarrowed_BetweenAClaimsCheckAndItsLock_ClaimsNothing()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            using var client = rig.Open();
            var session = rig.Host.Sessions.Snapshot().Single();
            int calls = 0;
            rig.Host.Sessions.AfterTheLevelCheck = () =>
            {
                // The second call is the one past the claim's own checks.
                if (Interlocked.Increment(ref calls) == 2) rig.Host.Sessions.SetReach(session.Who.Key, ProgramReach.OwnDevices);
            };
            Assert.StartsWith("@1 err outside-reach ", client.Final("@1 sub " + AutomationPipeRig.IdOf(pad) + " A claim"));
            Assert.Equal(0, rig.Host.Sessions.HeldAndClaimedBy(session));
        }

        [Fact]
        public async Task AConnectionWhileTheRowsReachIsSet_WorksWithinTheNewReach()
        {
            var (rig, consent) = FlowRig();
            using var _ = rig;
            try
            {
                consent.Allow(new AllowedProgram(FlowKey, "Narrowed", "script", FlowPython, FlowScript, null, AutomationLevel.Read));
                var page = new AutomationConsentForPage(consent, rig.Host.Sessions, rig.Host.Clients);
                AutomationTestClient late = null;
                page.BetweenTheSessionsAndTheStore = () =>
                {
                    late = rig.Connect();
                    late.Final("hello name=\"Narrowed\" kind=script level=read");
                };
                page.SetReach(FlowKey, ProgramReach.OwnDevices);
                await AutomationPipeRig.Until(() => rig.Host.Sessions.Count == 1);
                var session = rig.Host.Sessions.Snapshot().Single();
                Assert.NotNull(session.Reach);
                Assert.False(session.HeldByItsAsk);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        // ── The file's list, Activity's runs, the pool's shares ──

        [Fact]
        public async Task AListLoadedFromTheFile_LowersTheOpenSessionsOfWhatItNoLongerAllows()
        {
            var (rig, consent) = FlowRig();
            using var _ = rig;
            try
            {
                consent.Allow(new AllowedProgram(FlowKey, "Reloaded", "script", FlowPython, FlowScript, null, AutomationLevel.ChangeSettings));
                var client = rig.Connect();
                Assert.Contains(" level=change-settings ", client.Final("hello name=\"Reloaded\" kind=script level=change-settings"));
                var session = rig.Host.Sessions.Snapshot().Single();
                EventArgs seen = null;
                consent.Changed += (s, e) => seen = e;
                consent.Load(Array.Empty<AllowedProgramData>());
                Assert.Equal(AutomationLevel.Commands, rig.Host.Sessions.LevelOf(session));
                Assert.Same(AutomationConsent.FromFile, seen);
                var levels = client.Drain(300).Where(e => e.StartsWith("e level", StringComparison.Ordinal)).ToList();
                Assert.Equal("e level commands", levels.Last());
                // The window's listener leaves a load's change unsaved.
                string listener = OffLimitsSource.Read("PadForge.App/MainWindow.Assistant.cs");
                Assert.Contains("ReferenceEquals(e, AutomationConsent.FromFile)", listener);
                await Task.CompletedTask;
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public async Task ARunThatPausedAsAnotherAskCame_IsWrittenOnce_AndTheNewRunWhole()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            using var client = rig.Open("Runner");
            var session = rig.Host.Sessions.Snapshot().Single();
            var first = new AutomationAsk(AutomationAskKind.Unknown, "first");
            var second = new AutomationAsk(AutomationAskKind.Unknown, "second");
            int once = 0;
            rig.Host.Sessions.BeforeTheRunsFlush = () =>
            {
                if (Interlocked.Exchange(ref once, 1) == 0) rig.Host.Sessions.Record(session, second, "ok", true);
            };
            rig.Host.Sessions.Record(session, first, "ok", true);
            await AutomationPipeRig.Until(() => Volatile.Read(ref once) == 1, 5000);
            rig.Host.Sessions.Record(session, second, "ok", true);
            await AutomationPipeRig.Until(() => rig.Host.Activity.Snapshot().Any(e => e.Asked.Argument == "second"), 5000);
            await Task.Delay(AutomationSessions.RunGapMs + 300);
            var runs = rig.Host.Activity.Snapshot().Where(e => e.Asked.Argument is "first" or "second").ToList();
            Assert.Equal(new[] { "first", "second" }, runs.Select(e => e.Asked.Argument));
            Assert.Equal(2, runs[1].Asked.Count);
        }

        [Fact]
        public void TheWebAndNetworkShare_FillsAlone_AndLocalProgramsKeepTheirs()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            var sessions = rig.Host.Sessions;
            for (int i = 0; i < AutomationSessions.MostThroughHelper; i++)
            {
                var web = AutomationSession.ForTest(sessions, i % 2 == 0 ? AutomationClientKind.Web : AutomationClientKind.Network, 900000 + i);
                Assert.True(sessions.Open(web, out _));
            }
            var another = AutomationSession.ForTest(sessions, AutomationClientKind.Web, 900100);
            Assert.False(sessions.Open(another, out string refusal));
            Assert.StartsWith("err too-many ", refusal);
            Assert.Contains("web", refusal);
            using var local = rig.Open("Local Launcher");
        }

    }
}
