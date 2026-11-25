using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PadForge.Common.Input;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The assistant tools of #600, reached as an assistant reaches them: the
    /// budget of the list, the folds (status with the system and what each
    /// higher level adds, record_problem with the snapshot, list_profiles,
    /// get_mappings and set_mappings), the general tools (find, read, set,
    /// press, where and show), set_value, a refused argument that names
    /// what the tool takes, and a call that moves a step of the pointer.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationAssistantToolsTests
    {
        private static JsonElement S(CallToolResult r) => r.StructuredContent ?? throw new Xunit.Sdk.XunitException("no result: " + AssistantRig.Text(r));

        [Fact]
        public async Task TheList_HoldsAtMost39Tools_InAFixedSize_WithTheRetiredNamesGone()
        {
            Assert.InRange(AssistantTools.All.Count, 30, AssistantTools.MostTools);
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var client = await rig.ConnectAsync();
            var tools = await client.ListToolsAsync();
            Assert.Equal(AssistantTools.All.Count, tools.Count);
            int bytes = JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool), McpJsonUtilities.DefaultOptions).Length;
            Assert.True(bytes <= AssistantTools.MostListBytes, "tools/list is " + bytes + " bytes, past the budget of " + AssistantTools.MostListBytes);
            var names = tools.Select(t => t.Name).ToHashSet();
            foreach (string gone in new[] { "find_settings", "get_setting", "set_setting", "save_snapshot", "get_system", "get_profile", "list_macros", "clear_mappings" })
                Assert.DoesNotContain(gone, names);
            foreach (string added in new[] { "find", "read", "set", "press", "show", "where", "set_value" })
                Assert.Contains(added, names);
            // Every tool has its line in Activity.
            Assert.All(AssistantTools.All, t => Assert.True(AssistantToolWords.HasPhrase(t.Name), t.Name));
        }

        [Fact]
        public async Task Status_CarriesTheSystemAtRead_AndWhatEachHigherLevelWouldAdd_WithItsArguments()
        {
            using (var rig = new AssistantRig(AutomationLevel.Commands))
            {
                var client = await rig.ConnectAsync();
                var status = S(await client.CallToolAsync("status"));
                Assert.Equal("commands", status.GetProperty("automation_level").GetString());
                Assert.False(status.TryGetProperty("system", out _));
                var higher = status.GetProperty("higher_levels").EnumerateArray().ToList();
                Assert.Equal(new[] { "read", "send_inputs", "change_settings" }, higher.Select(l => l.GetProperty("level").GetString()));
                var find = higher[0].GetProperty("tools").EnumerateArray().First(t => t.GetProperty("name").GetString() == "find");
                Assert.Contains("words", find.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()));
                var read = higher[0].GetProperty("tools").EnumerateArray().First(t => t.GetProperty("name").GetString() == "read");
                Assert.Contains("id*", read.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()));
            }
            using (var rig = new AssistantRig(AutomationLevel.Read))
            {
                var client = await rig.ConnectAsync();
                var status = S(await client.CallToolAsync("status"));
                Assert.Equal(1000, status.GetProperty("system").GetProperty("polling_rate_hz").GetInt32());
                Assert.Equal(new[] { "send_inputs", "change_settings" },
                    status.GetProperty("higher_levels").EnumerateArray().Select(l => l.GetProperty("level").GetString()));
            }
        }

        [Fact]
        public async Task RecordProblem_SavesASnapshotAtCommands_AndRecordsOnlyAtRead()
        {
            using var rig = new AssistantRig(AutomationLevel.Commands);
            var client = await rig.ConnectAsync();
            var snapshot = await client.CallToolAsync("record_problem", AssistantRig.Args(("seconds", 0)));
            Assert.False(snapshot.IsError == true, AssistantRig.Text(snapshot));
            Assert.Contains("snapshot", S(snapshot).GetProperty("message").GetString());
            var recording = await client.CallToolAsync("record_problem", AssistantRig.Args(("seconds", 3)));
            Assert.True(recording.IsError);
            Assert.Contains("(not-allowed)", AssistantRig.Text(recording));
            Assert.Contains("the Read level", AssistantRig.Text(recording));
        }

        [Fact]
        public async Task TheFolds_ListProfilesWithOne_GetMappingsWithMacros_AndSetMappingsClearAll()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var client = await rig.ConnectAsync();
            var profiles = S(await client.CallToolAsync("list_profiles", AssistantRig.Args(("profile", "Racing"))));
            Assert.Equal("p1", profiles.GetProperty("profile").GetProperty("id").GetString());
            var plain = S(await client.CallToolAsync("list_profiles"));
            Assert.False(plain.TryGetProperty("profile", out _));
            var mappings = S(await client.CallToolAsync("get_mappings", AssistantRig.Args(("controller", 1), ("macros", true))));
            Assert.Equal(JsonValueKind.Array, mappings.GetProperty("macros").ValueKind);
            Assert.Equal(JsonValueKind.Array, mappings.GetProperty("menus").ValueKind);
            Assert.False(S(await client.CallToolAsync("get_mappings", AssistantRig.Args(("controller", 1)))).TryGetProperty("macros", out _));
            var cleared = await client.CallToolAsync("set_mappings", AssistantRig.Args(("controller", 1), ("clear_all", true)));
            Assert.False(cleared.IsError == true, AssistantRig.Text(cleared));
            Assert.Contains("clear 1", rig.App.Calls);
            var none = await client.CallToolAsync("set_mappings", AssistantRig.Args(("controller", 1)));
            Assert.Contains("(bad-argument)", AssistantRig.Text(none));
        }

        [Fact]
        public async Task ARefusedArgument_NamesWhatTheToolTakes()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var client = await rig.ConnectAsync();
            var bad = await client.CallToolAsync("set_engine", AssistantRig.Args(("onn", true)));
            Assert.True(bad.IsError);
            Assert.Contains("set_engine takes on*", AssistantRig.Text(bad));
            Assert.Equal("set_engine takes on*, a name with * being needed.", AssistantJson.Takes("set_engine", typeof(OnOffArgs)));
            Assert.Equal("status takes no arguments.", AssistantJson.Takes("status", typeof(NoArguments)));
        }

        [Fact]
        public async Task FindReadSetAndPress_ReachTheCatalog_ByClass_AndRefuseATurnedOffControl()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var client = await rig.ConnectAsync();
            var found = S(await client.CallToolAsync("find", AssistantRig.Args(("kind", "setting"), ("most", 2))));
            Assert.Equal(2, found.GetProperty("places").GetArrayLength());
            Assert.Equal(2, found.GetProperty("left_out").GetInt32());
            var read = S(await client.CallToolAsync("read", AssistantRig.Args(("id", "pad/Pad_Deadzone@c1"))));
            Assert.Equal("number", read.GetProperty("place").GetProperty("takes").GetString());
            // A read that names its documentation page points at it with a
            // resource link.
            var linked = await client.CallToolAsync("read", AssistantRig.Args(("id", "settings/Settings_Switch")));
            var link = Assert.Single(linked.Content.OfType<ResourceLinkBlock>());
            Assert.True(link.Uri == "https://padforge.org/docs/features/settings/", "the link is " + link.Uri);
            Assert.Empty((await client.CallToolAsync("read", AssistantRig.Args(("id", "pad/Pad_Deadzone@c1")))).Content.OfType<ResourceLinkBlock>());

            // Several values in one call, in order.
            var set = await client.CallToolAsync("set", AssistantRig.Args(("values", new Dictionary<string, object> { ["pad/Pad_Deadzone@c1"] = 30, ["settings/Settings_Choice"] = "High" })));
            Assert.False(set.IsError == true, AssistantRig.Text(set));
            Assert.Equal(30, rig.Settings.Number);
            Assert.Equal("High", rig.Settings.Choice);
            Assert.Equal(2, S(set).GetProperty("places").GetArrayLength());

            // A control the page has turned off is refused, a number too.
            rig.Settings.NumberTurnedOff = true;
            var off = await client.CallToolAsync("set", AssistantRig.Args(("id", "pad/Pad_Deadzone@c1"), ("value", 40)));
            Assert.Contains("(not-available)", AssistantRig.Text(off));
            Assert.Equal(30, rig.Settings.Number);
            // The user's own is refused.
            var own = await client.CallToolAsync("set", AssistantRig.Args(("id", "extras/Automation/Extras_Automation_Level"), ("value", "Read")));
            Assert.Contains("(users-own)", AssistantRig.Text(own));

            // A press is posted to the interface's thread, never run inside
            // the call, so a dialog it opens holds nothing.
            int posted = rig.Ui.Posted.Count;
            var pressed = await client.CallToolAsync("press", AssistantRig.Args(("id", "settings.action/Settings_Press")));
            Assert.False(pressed.IsError == true, AssistantRig.Text(pressed));
            Assert.True(S(pressed).GetProperty("done").GetBoolean());
            Assert.Equal(0, rig.Settings.Pressed);
            List<Action> run;
            lock (rig.Ui.Posted) run = rig.Ui.Posted.Skip(posted).ToList();
            foreach (var work in run) work();
            Assert.Equal(1, rig.Settings.Pressed);
            var notPressed = await client.CallToolAsync("press", AssistantRig.Args(("id", "settings/Settings_Switch")));
            Assert.Contains("(not-available)", AssistantRig.Text(notPressed));

            var where = S(await client.CallToolAsync("where"));
            Assert.Equal("page/Home", where.GetProperty("page").GetString());
            Assert.Equal("behind", where.GetProperty("window").GetString());
        }

        [Fact]
        public async Task Show_WhileTheWindowIsBehind_AsksOnTheCard_AndPlaysOnlyWhenTheUserPicksShow()
        {
            using var rig = new AssistantRig(AutomationLevel.Read);
            var client = await rig.ConnectAsync();
            var shown = S(await client.CallToolAsync("show", AssistantRig.Args(("place", "settings/Settings_Switch"), ("words", "Turn this on."))));
            Assert.True(shown.GetProperty("asked").GetBoolean());
            Assert.False(shown.GetProperty("shown").GetBoolean());
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
            var pending = rig.Host.Approvals.Pending[0];
            Assert.Equal(ApprovalKind.Show, pending.Request.Kind);
            Assert.Contains("Switch", pending.Request.Title);
            Assert.Empty(rig.Settings.Shows);
            Assert.Equal(0, rig.App.BroughtForward);

            // Not Now: nothing plays, nothing comes forward.
            Assert.True(rig.Host.Approvals.Answer(pending.Id, false));
            await Task.Delay(100);
            lock (rig.Ui.Posted) foreach (var work in rig.Ui.Posted.ToList()) work();
            Assert.Empty(rig.Settings.Shows);
            Assert.Equal(0, rig.App.BroughtForward);

            // Show: the user's click brings the window forward and plays.
            await client.CallToolAsync("show", AssistantRig.Args(("place", "settings/Settings_Switch"), ("words", "Turn this on.")));
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
            int before;
            lock (rig.Ui.Posted) before = rig.Ui.Posted.Count;
            Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, true));
            await AssistantRig.Until(() => { lock (rig.Ui.Posted) return rig.Ui.Posted.Count > before; });
            List<Action> run;
            lock (rig.Ui.Posted) run = rig.Ui.Posted.Skip(before).ToList();
            foreach (var work in run) work();
            Assert.Equal(1, rig.App.BroughtForward);
            var played = Assert.Single(rig.Settings.Shows);
            Assert.Equal("settings/Settings_Switch", played.Steps[0].Place);
            Assert.Contains("Test Assistant", played.Program);

            // In front, it plays at once and asks nothing.
            rig.App.WindowIs = AssistantWindowPlace.InFront;
            var now = S(await client.CallToolAsync("show", AssistantRig.Args(("steps", new object[]
            {
                new Dictionary<string, object> { ["place"] = "settings/Settings_Switch", ["words"] = "One.", ["advance"] = "next" },
                new Dictionary<string, object> { ["place"] = "settings/Settings_Choice", ["words"] = "Two.", ["advance"] = "seconds", ["seconds"] = 3 },
            }))));
            Assert.True(now.GetProperty("shown").GetBoolean());
            Assert.Empty(rig.Host.Approvals.Pending);
            var two = rig.Settings.Shows.Last();
            Assert.Equal(AssistantAdvance.Next, two.Steps[0].Advance);
            Assert.Equal(3, two.Steps[1].Seconds);
            // The next call moves a step that waits for it.
            int calls = rig.Settings.NextCalls;
            await client.CallToolAsync("where");
            Assert.True(rig.Settings.NextCalls > calls);
            // Show with no place takes the callout away.
            await client.CallToolAsync("show");
            Assert.Empty(rig.Settings.Shows.Last().Steps);
        }

        [Fact]
        public async Task SetValue_HandsANamedValueToMacroTriggers_AtSendInputs()
        {
            ScriptValues.ResetForTest();
            try
            {
                using var rig = new AssistantRig(AutomationLevel.SendInputs);
                var client = await rig.ConnectAsync();
                var set = await client.CallToolAsync("set_value", AssistantRig.Args(("name", "boost"), ("value", 0.75)));
                Assert.False(set.IsError == true, AssistantRig.Text(set));
                Assert.Equal(0.75f, ScriptValues.Read("boost"));
                var bad = await client.CallToolAsync("set_value", AssistantRig.Args(("name", "9lives"), ("value", 1)));
                Assert.Contains("(bad-argument)", AssistantRig.Text(bad));
            }
            finally
            {
                ScriptValues.ResetForTest();
            }
        }
    }
}
