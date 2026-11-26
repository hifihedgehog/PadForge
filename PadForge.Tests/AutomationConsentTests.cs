using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Gives a test's host a consent list that allows every
    /// program, so a test of something else raises no question card
    /// (#600).</summary>
    internal static class TestConsent
    {
        internal static AutomationHost AllowAll(AutomationHost host)
        {
            host.Sessions.Consent = new AutomationConsent { AllowsEveryProgramForTest = true };
            return host;
        }

        /// <summary>A host whose consent list starts empty, as a first run's.</summary>
        internal static AutomationConsent Empty(AutomationHost host)
        {
            var consent = new AutomationConsent();
            host.Sessions.Consent = consent;
            return consent;
        }
    }

    /// <summary>
    /// Consent per program (#600): a program that connects at Read or above
    /// for the first time waits for the user's answer on the question card,
    /// which names its path, its signer and for a script its interpreter and
    /// script file. The hello names the level it wants. An answer is kept
    /// in PadForge.xml, a program asks once, a declined or unanswered one
    /// raises no second card while it reconnects, and removing a program
    /// drops its open sessions to Commands. Commands asks nothing.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationConsentTests
    {
        private const string Python = @"C:\Python314\python.exe";
        private const string Script = @"C:\Tools\telemetry_bridge.py";

        private static (AutomationPipeRig Rig, AutomationConsent Consent) RigWithScript(AutomationLevel level = AutomationLevel.ChangeSettings, TimeSpan? wait = null)
        {
            var rig = new AutomationPipeRig(level);
            var consent = TestConsent.Empty(rig.Host);
            int next = 424200;
            rig.Host.AutomationPipe.ProcessOf = _ => AutomationClientProcess.ForTest(Interlocked.Increment(ref next), Python);
            AutomationConsent.CommandLineSeam = _ => "\"" + Python + "\" -u " + Script + " --fast";
            return (rig, consent);
        }

        [Fact]
        public async Task AScriptAtRead_WaitsOnTheCard_ThatNamesItsInterpreterScriptAndSigner_AndWorksOnceAllowed()
        {
            var (rig, consent) = RigWithScript();
            using var _ = rig;
            try
            {
                var client = rig.Connect();
                string hello = client.Final("hello name=\"Telemetry Bridge\" kind=script level=read");
                // It works at Commands while the card asks.
                Assert.Contains(" level=commands ", hello);
                Assert.Contains(" wants=read consent=waiting", hello);
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                var pending = rig.Host.Approvals.Pending[0];
                Assert.Equal(ApprovalKind.Consent, pending.Request.Kind);
                Assert.Contains("Telemetry Bridge", pending.Request.Title);
                Assert.Contains(PadForge.Resources.Strings.Strings.Instance.Extras_Automation_Level_Read, pending.Request.Title);
                Assert.Contains(pending.Request.Detail, d => d.Contains("python.exe", StringComparison.Ordinal) && d.Contains(Script, StringComparison.Ordinal));
                // The card's From line names the interpreter's path and its
                // signer, so the detail does not say it twice.
                Assert.DoesNotContain(PadForge.Resources.Strings.Strings.Instance.Automation_Consent_NotSigned, pending.Request.Detail);
                Assert.Contains(PadForge.Resources.Strings.Strings.Instance.Automation_Consent_ReadsKeys, pending.Request.Detail);
                Assert.Equal(ApprovalKind.Consent, rig.Host.Approvals.KindOf(pending.Id));

                // A line past Commands waits for the answer, and runs after it.
                client.Send("@1 devices");
                await Task.Delay(200);
                Assert.True(rig.Host.Approvals.Answer(pending.Id, true));
                string line = client.Next();
                while (line != null && !line.StartsWith("@1 ok", StringComparison.Ordinal) && !line.StartsWith("@1 err", StringComparison.Ordinal))
                    line = client.Next();
                Assert.StartsWith("@1 ok", line);
                string level = client.Event();
                while (level != null && !level.StartsWith("e level", StringComparison.Ordinal)) level = client.Event();
                Assert.Equal("e level read", level);

                // Kept: the program's key with the level it asked for.
                var allowed = Assert.Single(consent.Programs);
                Assert.Equal(AutomationLevel.Read, allowed.Level);
                Assert.Equal(Script, allowed.Script);
                Assert.Equal("script", allowed.Kind);
                var saved = consent.Save();
                var loaded = new AutomationConsent();
                loaded.Load(saved);
                Assert.Equal(allowed.Key, Assert.Single(loaded.Programs).Key);

                // It asks once: the next connection works at Read at once.
                var again = rig.Connect();
                string second = again.Final("hello name=\"Telemetry Bridge\" kind=script level=read");
                Assert.Contains(" level=read ", second);
                Assert.DoesNotContain("consent=", second);
                Assert.Empty(rig.Host.Approvals.Pending);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public async Task DontAllow_KeepsItAtCommands_AndAReconnectLoop_RaisesNoSecondCard()
        {
            var (rig, consent) = RigWithScript();
            using var _ = rig;
            try
            {
                var client = rig.Connect();
                client.Final("hello name=\"Looper\" kind=script level=send-inputs");
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, false));
                // A line past Commands is refused, and a command still runs.
                Assert.StartsWith("@1 err not-allowed", client.Final("@1 devices"));
                Assert.StartsWith("@2 ok", client.Final("@2 state"));
                for (int i = 0; i < 3; i++)
                {
                    var loop = rig.Connect();
                    Assert.Contains(" level=commands ", loop.Final("hello name=\"Looper\" kind=script level=send-inputs"));
                }
                await Task.Delay(100);
                Assert.Empty(rig.Host.Approvals.Pending);
                Assert.Empty(consent.Programs);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public async Task ACardNobodyAnswers_EndsAtCommands_AndAsksNoMoreForAWhile()
        {
            // A short wait for the card, in a host of its own.
            string pipeName = "PadForge.Tests.ConsentWaitPipe." + Guid.NewGuid().ToString("N");
            using var host = new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(), "PadForge.Tests.ConsentWait." + Guid.NewGuid().ToString("N"),
                approvalWait: TimeSpan.FromMilliseconds(300), automationPipeName: pipeName);
            var consent = TestConsent.Empty(host);
            host.Apply(true, AutomationLevel.Read);
            using var client = new AutomationTestClient(pipeName);
            client.Final("hello name=\"Quiet\" kind=script");
            await AutomationPipeRig.Until(() => host.Approvals.Pending.Count == 1);
            await AutomationPipeRig.Until(() => host.Approvals.Pending.Count == 0, 3000);
            Assert.StartsWith("@1 err not-allowed", client.Final("@1 devices"));
            using var again = new AutomationTestClient(pipeName);
            Assert.Contains(" level=commands ", again.Final("hello name=\"Quiet\" kind=script"));
            await Task.Delay(100);
            Assert.Empty(host.Approvals.Pending);
            Assert.Empty(consent.Programs);
        }

        [Fact]
        public void CommandsAsksNothing_AndAPlainCommandsHelloStaysThere()
        {
            var (rig, consent) = RigWithScript();
            using var _ = rig;
            try
            {
                var client = rig.Connect();
                string hello = client.Final("hello name=\"Launcher\" kind=script level=commands");
                Assert.Contains(" level=commands ", hello);
                Assert.DoesNotContain("consent=", hello);
                Assert.Empty(rig.Host.Approvals.Pending);
                Assert.StartsWith("@1 err bad-argument", rig.Connect().Final("@1 hello name=\"x\" kind=script level=everything"));
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public async Task TheUsersLevel_CapsWhatAProgramWasAllowed_AndRemovingIt_DropsItToCommands()
        {
            var (rig, consent) = RigWithScript(AutomationLevel.Read);
            using var _ = rig;
            try
            {
                var client = rig.Connect();
                client.Final("hello name=\"Capped\" kind=script level=change-settings");
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, true));
                await AutomationPipeRig.Until(() => consent.Programs.Count == 1);
                // Allowed Change Settings, capped at the user's Read.
                var session = rig.Host.Sessions.Snapshot().Single();
                Assert.Equal(AutomationLevel.Read, rig.Host.Sessions.LevelOf(session));
                Assert.Equal(AutomationLevel.ChangeSettings, consent.Programs[0].Level);

                rig.Host.Sessions.Revoke(consent.Programs[0].Key);
                Assert.Empty(consent.Programs);
                Assert.Equal(AutomationLevel.Commands, rig.Host.Sessions.LevelOf(session));
                var levels = client.Drain(300).Where(e => e.StartsWith("e level", StringComparison.Ordinal)).ToList();
                Assert.Equal("e level commands", levels.Last());
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public void TwoConnectionsOfOneResumeSecret_ShareOneCallerKey()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var first = rig.Connect();
            first.Final("hello name=\"Helper\" kind=script resume=0123456789abcdef0123");
            var second = rig.Connect();
            second.Final("hello name=\"Helper\" kind=script resume=0123456789abcdef0123");
            var other = rig.Connect();
            other.Final("hello name=\"Other\" kind=script resume=fedcba9876543210fedc");
            var sessions = rig.Host.Sessions.Snapshot().OrderBy(s => s.Number).ToList();
            Assert.Equal(sessions[0].CallerKey, sessions[1].CallerKey);
            Assert.NotEqual(sessions[0].CallerKey, sessions[2].CallerKey);
            Assert.StartsWith("err bad-argument", rig.Connect().Final("hello name=\"x\" kind=script resume=short"));
        }

        [Fact]
        public void AScriptsFile_IsReadFromItsInterpretersCommandLine()
        {
            try
            {
                AutomationConsent.CommandLineSeam = _ => "\"C:\\Python\\python.exe\" -u -X utf8 C:\\a\\b.py one";
                Assert.Equal(@"C:\a\b.py", AutomationConsent.ScriptOf(1, "python.exe"));
                AutomationConsent.CommandLineSeam = _ => "pwsh.exe -NoProfile -File \"C:\\s\\run me.ps1\" -x";
                Assert.Equal(@"C:\s\run me.ps1", AutomationConsent.ScriptOf(1, "pwsh.exe"));
                AutomationConsent.CommandLineSeam = _ => "python.exe -m padforge_tool";
                Assert.Equal("-m padforge_tool", AutomationConsent.ScriptOf(1, "python.exe"));
                AutomationConsent.CommandLineSeam = _ => "python.exe -c \"print(1)\"";
                Assert.Equal("-c print(1)", AutomationConsent.ScriptOf(1, "python.exe"));
                AutomationConsent.CommandLineSeam = _ => "python.exe";
                Assert.Null(AutomationConsent.ScriptOf(1, "python.exe"));
                AutomationConsent.CommandLineSeam = _ => "AutoHotkey64.exe /ErrorStdOut C:\\ahk\\steer.ahk";
                Assert.Equal(@"C:\ahk\steer.ahk", AutomationConsent.ScriptOf(1, "autohotkey64.exe"));
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public void AddingAnAssistantOnItsRow_IsItsConsent_AndRemovingTakesItAway()
        {
            var consent = new AutomationConsent();
            int changed = 0;
            consent.Changed += (s, e) => changed++;
            consent.AllowAssistant(@"C:\Users\x\AppData\Local\AnthropicClaude\claude.exe", "Claude");
            string key = AutomationConsent.KeyOf("assistant", @"c:\users\x\appdata\local\anthropicclaude\CLAUDE.EXE", null);
            Assert.NotNull(consent.Find(key));
            Assert.Equal(AutomationLevel.ChangeSettings, consent.Find(key).Level);
            Assert.True(consent.RemoveAssistant(@"C:\Users\x\AppData\Local\AnthropicClaude\claude.exe"));
            Assert.Null(consent.Find(key));
            Assert.Equal(2, changed);
            // The hello's level words.
            Assert.True(AutomationConsent.TryLevel("send_inputs", out var send));
            Assert.Equal(AutomationLevel.SendInputs, send);
            Assert.False(AutomationConsent.TryLevel("all", out _));
        }

        [Fact]
        public void AnAssistantAnInterpreterRuns_IsKeyedByItsScript_SoOneAnswerNeverCoversAnother()
        {
            try
            {
                const string node = @"C:\Program Files\nodejs\node.exe";
                const string gemini = @"C:\Users\x\AppData\Roaming\npm\node_modules\@google\gemini-cli\dist\index.js";
                const string other = @"C:\Users\x\AppData\Roaming\npm\node_modules\other-cli\cli.js";
                AutomationConsent.ParentSeam = _ => 4242;
                AutomationConsent.ParentPathSeam = _ => node;
                var helper = AutomationClientProcess.ForTest(424300, Environment.ProcessPath);
                AutomationConsent.CommandLineSeam = _ => "\"" + node + "\" --no-warnings " + gemini + " --experimental-acp";
                var first = AutomationConsent.Identify(helper, "assistant", "gemini-cli (node.exe)");
                Assert.Equal(node, first.Path);
                Assert.Equal(gemini, first.Script);
                // Keyed by its script alone, as its row keys the npm shim
                // that runs it, whichever node.exe runs it (#600).
                Assert.Equal(AutomationConsent.KeyOf("assistant", null, gemini), first.Key);
                AutomationConsent.CommandLineSeam = _ => "\"" + node + "\" " + other;
                var second = AutomationConsent.Identify(helper, "assistant", "other (node.exe)");
                Assert.NotEqual(first.Key, second.Key);
                // A program that is no interpreter is its own path alone.
                AutomationConsent.ParentPathSeam = _ => @"C:\Users\x\.local\bin\claude.exe";
                var claude = AutomationConsent.Identify(helper, "assistant", "claude-code (claude.exe)");
                Assert.Null(claude.Script);
                Assert.Equal(AutomationConsent.KeyOf("assistant", @"C:\Users\x\.local\bin\claude.exe", null), claude.Key);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
                AutomationConsent.ParentSeam = null;
                AutomationConsent.ParentPathSeam = null;
            }
        }

        [Fact]
        public async Task TheAutomationPage_ListsTheStore_WithWhoAndConnectedNow_AndItsRemoveDropsTheSession()
        {
            var (rig, consent) = RigWithScript();
            using var _ = rig;
            try
            {
                var page = new AutomationConsentForPage(consent, rig.Host.Sessions, rig.Host.Clients);
                int changed = 0;
                page.Changed += (s, e) => Interlocked.Increment(ref changed);
                var client = rig.Connect();
                client.Final("hello name=\"Telemetry Bridge\" kind=script level=read");
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, true));
                await AutomationPipeRig.Until(() => page.Allowed.Count == 1);
                var row = page.Allowed.Single();
                Assert.Equal("Telemetry Bridge", row.Name);
                Assert.Equal(AutomationLevel.Read, row.Level);
                Assert.True(row.ConnectedNow);
                Assert.StartsWith(Python + " running " + Script + " · ", row.Who);
                Assert.True(changed >= 2, "a connection and the answer each raise Changed");
                // A second connection of the program is its last use, read
                // with the change its connection raises.
                var firstUse = row.LastUsed;
                await Task.Delay(50);
                DateTimeOffset? seenAtChange = null;
                EventHandler look = (s, e) => seenAtChange ??= page.Allowed.Single().LastUsed;
                page.Changed += look;
                using (var again = rig.Connect())
                    again.Final("hello name=\"Telemetry Bridge\" kind=script");
                page.Changed -= look;
                Assert.NotNull(seenAtChange);
                Assert.True(seenAtChange > firstUse, "the page reads the new last use with the connection's change");
                await AutomationPipeRig.Until(() => rig.Host.Sessions.Snapshot().Count == 1);

                var session = rig.Host.Sessions.Snapshot().Single();
                page.Remove(row.Key);
                Assert.Empty(page.Allowed);
                Assert.Equal(AutomationLevel.Commands, rig.Host.Sessions.LevelOf(session));

                // The row's Add and Remove, by the full path of the program
                // that starts PadForge.exe --mcp.
                const string claude = @"C:\Users\x\.local\bin\claude.exe";
                page.AllowAssistant("claude-code", "Claude Code", claude);
                var added = page.Allowed.Single();
                Assert.Equal(AutomationConsent.KeyOf("assistant", claude, null), added.Key);
                Assert.Equal("Claude Code", added.Name);
                Assert.False(added.ConnectedNow);
                page.AllowAssistant("claude-code", "Claude Code", null);
                Assert.Single(page.Allowed);
                page.ForgetAssistant("claude-code", claude);
                Assert.Empty(page.Allowed);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public async Task AConnectedClient_CarriesItsHellosOwnName_AndTheFullPathOfTheProgramBehindIt()
        {
            var (rig, _) = RigWithScript(AutomationLevel.Commands);
            using var __ = rig;
            try
            {
                using var script = rig.Connect();
                script.Final("hello name=\"Telemetry Bridge\" kind=script");
                var first = rig.Host.Clients.Snapshot().Single();
                Assert.Equal("Telemetry Bridge", first.ClientName);
                Assert.Equal(Python, first.HostPath);

                // An assistant's copy of PadForge.exe: the program that
                // started it is behind the connection, and the hello's name
                // is the MCP client's alone.
                rig.Host.AutomationPipe.ProcessOf = _ => AutomationClientProcess.ForTest(424299, Environment.ProcessPath);
                AutomationConsent.ParentSeam = _ => Environment.ProcessId;
                var opened = new TaskCompletionSource<bool>();
                rig.Host.McpSession = async (peer, stream, stopping) =>
                {
                    opened.TrySetResult(true);
                    await Task.Delay(500, stopping);
                };
                using var assistant = rig.Connect();
                Assert.StartsWith("ok padforge=", assistant.Final("hello kind=mcp name=claude-code host=claude.exe"));
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var helper = rig.Host.Clients.Snapshot().Single(c => c.Kind == AutomationClientKind.Assistant);
                Assert.Equal("claude-code (claude.exe)", helper.Name);
                Assert.Equal("claude-code", helper.ClientName);
                Assert.Equal(Environment.ProcessPath, helper.HostPath, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
                AutomationConsent.ParentSeam = null;
            }
        }
    }
}
