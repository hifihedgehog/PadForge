using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What a call keeps and how long: a script session's rate and requests
    /// go when it ends, a helper's last until the grace after its last drop,
    /// a call the program canceled withdraws its question, a first
    /// connection's wait is bounded, a broken pipe gives the command line's
    /// exit code, a copy that is still starting is waited for, the file's
    /// Automation is read once while it stays the same, and a walk's step
    /// whose test throws still shows.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AssistantCallLifetimeTests
    {
        private static async Task<NamedPipeClientStream> ScriptAsync(AssistantRig rig, string name)
        {
            var pipe = new NamedPipeClientStream(".", rig.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await SendAsync(pipe, "hello name=\"" + name + "\" kind=script level=read");
            Assert.StartsWith("ok ", await LineAsync(pipe, null));
            return pipe;
        }

        private static async Task SendAsync(Stream pipe, string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();
        }

        private static async Task<string> LineAsync(Stream pipe, string tag)
        {
            using var clock = new CancellationTokenSource(10000);
            while (true)
            {
                var line = new List<byte>();
                var one = new byte[1];
                while (await pipe.ReadAsync(one, clock.Token) == 1 && one[0] != (byte)'\n') line.Add(one[0]);
                string text = Encoding.UTF8.GetString(line.ToArray());
                if (text.StartsWith("e ", StringComparison.Ordinal)) continue;
                if (tag == null || text.StartsWith(tag, StringComparison.Ordinal)) return text;
            }
        }

        /// <summary>A script's call lines take a rate under the session's
        /// key, which goes when the session ends.</summary>
        [Fact]
        public async Task AScriptSession_LeavesNothingKept_WhenItEnds()
        {
            using var rig = new AssistantRig(AutomationLevel.Read);
            for (int i = 0; i < 3; i++)
            {
                using var pipe = await ScriptAsync(rig, "Loop Script " + i);
                await SendAsync(pipe, "@1 call list_controllers {}");
                Assert.StartsWith("@1 ok", await LineAsync(pipe, "@1 "));
            }
            await AssistantRig.Until(() => rig.Calls.KeptInAll() == 0, 5000);
        }

        /// <summary>A helper that comes back and drops again keeps what it
        /// has until the grace after its last drop.</summary>
        [Fact]
        public async Task AHelperThatDropsAgain_KeepsItsEntries_UntilTheGraceAfterTheLastDrop()
        {
            using var rig = new AssistantRig(AutomationLevel.Read);
            rig.Calls.Grace = TimeSpan.FromMilliseconds(2000);
            const int key = 987654;
            Assert.True(rig.Calls.TakeRate(key));
            rig.Calls.ForgetLater(key);
            await Task.Delay(800);
            // Back under the same key, and gone again.
            rig.Calls.ForgetLater(key);
            await Task.Delay(1500);
            // The first drop's grace has ended, the second's has not.
            Assert.Equal(1, rig.Calls.KeptFor(key));
            await AssistantRig.Until(() => rig.Calls.KeptFor(key) == 0, 4000);
        }

        /// <summary>A call the program cancels takes its question off the
        /// card, and nothing changes.</summary>
        [Fact]
        public async Task ACanceledCall_WithdrawsItsQuestion_AndNothingChanges()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings, approvalWait: TimeSpan.FromSeconds(30), answerWait: TimeSpan.FromSeconds(20));
            var client = await rig.ConnectAsync();
            rig.Settings.AskForSwitch = true;
            using var cancel = new CancellationTokenSource();
            var call = client.CallToolAsync("set", AssistantRig.Args(("id", "settings/Settings_Switch"), ("value", true)), cancellationToken: cancel.Token).AsTask();
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1 || call.IsCompleted);
            var pending = Assert.Single(rig.Host.Approvals.Pending);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 0, 3000);
            Assert.False(rig.Host.Approvals.Answer(pending.Id, true));
            await Task.Delay(300);
            Assert.False(rig.Settings.Switch);
        }

        /// <summary>A first call that waits for the user's answer to the
        /// connection waits no longer than a call's answer wait, and a call
        /// canceled meanwhile is no refusal.</summary>
        [Fact]
        public async Task AConsentWait_IsBoundedByTheAnswerWait_AndRecordsNoRefusal()
        {
            using var rig = new AssistantRig(AutomationLevel.Read, approvalWait: TimeSpan.FromSeconds(60), answerWait: TimeSpan.FromMilliseconds(500));
            TestConsent.Empty(rig.Host);
            using var pipe = await ScriptAsync(rig, "Consent Script");
            var session = rig.Host.Sessions.Snapshot().Single(s => s.ClaimedName == "Consent Script");
            Assert.True(rig.Host.Sessions.ConsentWaits(session));
            var caller = new AssistantCaller(null, session);
            var args = JsonDocument.Parse("{}").RootElement;

            using (var cancel = new CancellationTokenSource(200))
            {
                var canceled = await rig.Calls.CallAsync(caller, "list_profiles", args, cancel.Token);
                Assert.False(canceled.Ok);
                Assert.NotEqual("not-allowed", canceled.Code);
            }

            var call = rig.Calls.CallAsync(caller, "list_profiles", args, CancellationToken.None);
            Assert.Same(call, await Task.WhenAny(call, Task.Delay(5000)));
            Assert.NotEqual("not-allowed", (await call).Code);
            Assert.True(rig.Host.Sessions.ConsentWaits(session));
            Assert.DoesNotContain(rig.Host.Activity.Snapshot(), e => e.Asked.Argument == "list_profiles" && e.Result.Contains("not-allowed", StringComparison.Ordinal));
        }

        /// <summary>A call that ends while the user has not answered the
        /// connection is in Activity, in the words for it.</summary>
        [Fact]
        public async Task ACallThatEndsBeforeTheConnectionIsAnswered_IsInActivity()
        {
            using var rig = new AssistantRig(AutomationLevel.Read, approvalWait: TimeSpan.FromSeconds(60), answerWait: TimeSpan.FromMilliseconds(300));
            TestConsent.Empty(rig.Host);
            using var pipe = await ScriptAsync(rig, "Unanswered Script");
            var session = rig.Host.Sessions.Snapshot().Single(s => s.ClaimedName == "Unanswered Script");
            var outcome = await rig.Calls.CallAsync(new AssistantCaller(null, session), "list_profiles", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
            Assert.Equal("not-answered", outcome.Code);
            var entry = Assert.Single(rig.Host.Activity.Snapshot(), e => e.Asked.Argument == "list_profiles");
            Assert.False(entry.Succeeded);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Automation_Result_NotAnswered, PadForge.ViewModels.AutomationActivityItem.RefusalWords(entry.Result));
        }

        /// <summary>A pipe PadForge dropped right after it connected answers
        /// nothing, which the command line gives as exit code 1.</summary>
        [Fact]
        public async Task APipeThatBroke_AnswersNothing_InPlaceOfThrowing()
        {
            string name = "PadForge.Tests.Broken." + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var waiting = server.WaitForConnectionAsync();
            var (client, notPadForge) = AutomationPipeClient.Open(name);
            Assert.False(notPadForge);
            Assert.NotNull(client);
            await waiting;
            server.Disconnect();
            server.Dispose();
            using (client)
            {
                Assert.Null(client.Ask("hello name=x kind=script level=read", 2000));
                Assert.Null(client.Ask("@1 call status {}", 2000, "@1 "));
            }
        }

        /// <summary>A PadForge that runs and is not ready yet is starting: a
        /// call waits for its pipe and goes through.</summary>
        [Fact]
        public async Task ACopyThatIsStillStarting_IsWaitedFor()
        {
            string pipe = "PadForge.Tests.Assistant." + Guid.NewGuid().ToString("N");
            AssistantRig started = null;
            var mirror = new AssistantMirror(pipe, () => true, _ => true, "host.exe", () => new AutomationFileSettings(true, AutomationLevel.Read), padForgeReady: () => started != null);
            mirror.StartPadForge = () => true;
            var toMirror = new System.IO.Pipelines.Pipe();
            var fromMirror = new System.IO.Pipelines.Pipe();
            using var stop = new CancellationTokenSource();
            var serving = mirror.ServeAsync(new ModelContextProtocol.Server.StreamServerTransport(toMirror.Reader.AsStream(), fromMirror.Writer.AsStream(), "mirror"), stop.Token);
            McpClient client = null;
            try
            {
                client = await McpClient.CreateAsync(new StreamClientTransport(toMirror.Writer.AsStream(), fromMirror.Reader.AsStream()),
                    new McpClientOptions { ProtocolVersion = "2025-11-25", ClientInfo = new Implementation { Name = "Starting Test", Version = "1" } });
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    started = new AssistantRig(AutomationLevel.Read, pipeName: pipe);
                });
                var call = await client.CallToolAsync("list_controllers");
                Assert.False(call.IsError == true, AssistantRig.Text(call));
                Assert.Equal(MirrorState.Connected, mirror.State);
            }
            finally
            {
                try { if (client != null) await client.DisposeAsync(); } catch { }
                stop.Cancel();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                await mirror.DisposeAsync();
                started?.Dispose();
            }
        }

        /// <summary>The file's Automation is read once while the file stays
        /// the same, and again once it changes.</summary>
        [Fact]
        public void TheFilesAutomation_IsReadAgain_OnlyWhenTheFileChanged()
        {
            string file = Path.Combine(Path.GetTempPath(), "PadForge-kept-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(file, "<PadForgeSettings><AppSettings><EnableAutomation>true</EnableAutomation><AutomationLevel>1</AutomationLevel></AppSettings></PadForgeSettings>");
            int reads = 0;
            AutomationFileSettings Count(string path) { reads++; return AutomationFileSettings.Read(path); }
            try
            {
                for (int i = 0; i < 5; i++)
                    Assert.Equal(new AutomationFileSettings(true, AutomationLevel.Read), AutomationFileSettings.Kept(file, Count));
                Assert.Equal(1, reads);
                File.WriteAllText(file, "<PadForgeSettings><AppSettings><EnableAutomation>false</EnableAutomation><AutomationLevel>1</AutomationLevel></AppSettings></PadForgeSettings>");
                Assert.Equal(new AutomationFileSettings(false, AutomationLevel.Read), AutomationFileSettings.Kept(file, Count));
                Assert.Equal(2, reads);
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>A walk's next step whose test throws counts as not done,
        /// and shows.</summary>
        [Fact]
        public void AStepWhoseTestThrows_Shows()
        {
            Exception failed = null;
            bool first = false, second = false;
            var thread = new Thread(() =>
            {
                try
                {
                    var pointer = new AssistantPointer(() => null);
                    pointer.Play("Test Assistant", new[]
                    {
                        new AssistantPointer.Step { Words = "One", Advance = AssistantAdvance.Next },
                        new AssistantPointer.Step { Words = "Two", Advance = AssistantAdvance.Next, Done = () => throw new InvalidOperationException("the place is gone") },
                    }, owner: 5);
                    first = pointer.NextCall(5);
                    // The second step showed, so the next call ends the walk.
                    second = pointer.NextCall(5);
                    pointer.Clear();
                }
                catch (Exception error)
                {
                    failed = error;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(10000));
            Assert.Null(failed);
            Assert.True(first);
            Assert.True(second);
        }
    }
}
