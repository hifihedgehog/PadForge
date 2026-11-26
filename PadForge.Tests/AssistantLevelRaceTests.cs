using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The user's answer to a program's first connection reaches the
    /// program's tools whenever it comes, the moment the server reads the
    /// session's first list among them.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AssistantLevelRaceTests
    {
        [Fact]
        public async Task AnAnswerAsTheFirstListIsRead_StillGrowsTheList()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            TestConsent.Empty(rig.Host);
            bool raised = false;
            rig.Server.AfterFirstList = session =>
            {
                // The user allows the program right after the server read
                // its first list, and its level is raised before the server
                // goes on.
                var card = rig.Host.Approvals.Pending.Single(p => p.Request.Kind == ApprovalKind.Consent);
                if (!rig.Host.Approvals.Answer(card.Id, true)) return;
                var clock = Stopwatch.StartNew();
                while (session.Granted < AutomationLevel.ChangeSettings && clock.ElapsedMilliseconds < 5000) Thread.Sleep(10);
                raised = session.Granted == AutomationLevel.ChangeSettings;
            };
            var client = await rig.ConnectAsync();
            IList<McpClientTool> tools = null;
            var wait = Stopwatch.StartNew();
            while (!(tools = await client.ListToolsAsync()).Any(t => t.Name == "set") && wait.ElapsedMilliseconds < 5000) await Task.Delay(25);
            Assert.True(raised, "the answer did not raise the session's level");
            Assert.True(tools.Any(t => t.Name == "set"), "the list stayed " + string.Join(",", tools.Select(t => t.Name)));
        }

        /// <summary>Two Follows that cross: the first list's Follow has read
        /// the level and is held before it writes the list, the user answers,
        /// and the answer's Follow runs meanwhile. The list ends at the new
        /// level, whichever writes last.</summary>
        [Fact]
        public async Task TwoFollowsThatCross_LeaveTheListAtTheNewLevel()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            TestConsent.Empty(rig.Host);
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int follows = 0;
            rig.Server.AfterLevelRead = caller =>
            {
                // The first Follow is the first list's, on the session's
                // thread: it has read Commands and waits here.
                if (Interlocked.Increment(ref follows) != 1) return;
                held.Set();
                release.Wait(10000);
            };
            var connecting = rig.ConnectAsync();
            Assert.True(held.Wait(5000), "the first list was never read");
            var card = rig.Host.Approvals.Pending.Single(p => p.Request.Kind == ApprovalKind.Consent);
            Assert.True(rig.Host.Approvals.Answer(card.Id, true));
            var session = rig.Host.Sessions.Snapshot().Single(s => s.Kind == AutomationClientKind.Assistant);
            var clock = Stopwatch.StartNew();
            while (session.Granted < AutomationLevel.ChangeSettings && clock.ElapsedMilliseconds < 5000) await Task.Delay(10);
            Assert.Equal(AutomationLevel.ChangeSettings, session.Granted);
            // The answer's Follow runs now, on the thread that raised it.
            await Task.Delay(300);
            release.Set();
            var client = await connecting;
            var tools = await client.ListToolsAsync();
            Assert.True(Volatile.Read(ref follows) >= 2, "the answer ran no Follow");
            Assert.True(tools.Any(t => t.Name == "set"), "the list stayed " + string.Join(",", tools.Select(t => t.Name)));
        }
    }
}
