using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The assistant tools' words and counts (#600): one word for a
    /// controller's type wherever a tool names it, every feedback word a
    /// device declares, a device's own buttons in its state, and the
    /// command set's refusals in their own words.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationAssistantToolWordsTests
    {
        private static JsonElement S(CallToolResult r) => r.StructuredContent ?? throw new Xunit.Sdk.XunitException("no result: " + AssistantRig.Text(r));

        /// <summary>A keyboard and mouse controller is keyboard_mouse in
        /// status, in list_controllers and in its outputs, the word
        /// add_controller takes, never the pipe's keyboard-mouse.</summary>
        [Fact]
        public async Task AKeyboardAndMouseController_IsOneWord_InStatus_AndInItsOutputs()
        {
            using var rig = new AssistantRig(AutomationLevel.Read);
            rig.Target.State = new AutomationState
            {
                Controllers = new[] { new AutomationControllerState(1, "Keyboard", VirtualControllerType.KeyboardMouse, true, 0, 0) },
            };
            rig.App.Controllers[0].Type = "keyboard_mouse";
            rig.App.OutputsOf = _ => new Dictionary<string, string> { ["type"] = "keyboard-mouse", ["k"] = "0" };
            var client = await rig.ConnectAsync();
            var status = S(await client.CallToolAsync("status"));
            Assert.Equal("keyboard_mouse", status.GetProperty("controllers")[0].GetProperty("type").GetString());
            var listed = S(await client.CallToolAsync("list_controllers", AssistantRig.Args(("outputs", true))));
            var controller = listed.GetProperty("controllers")[0];
            Assert.Equal("keyboard_mouse", controller.GetProperty("type").GetString());
            Assert.Equal("keyboard_mouse", controller.GetProperty("outputs").GetProperty("type").GetString());
            Assert.Equal("0", controller.GetProperty("outputs").GetProperty("k").GetString());
        }

        /// <summary>add_device declares trigger effects, the fifth feedback
        /// word the pipe takes, and get_device reads it back.</summary>
        [Fact]
        public async Task ADeviceWithTriggerEffects_IsDeclared_AndReadsSo()
        {
            using var rig = new AssistantRig(AutomationLevel.SendInputs);
            var client = await rig.ConnectAsync();
            var added = await client.CallToolAsync("add_device", new Dictionary<string, object>
            {
                ["key"] = "pad",
                ["name"] = "Effects Pad",
                ["buttons"] = new[] { "A" },
                ["feedback"] = new[] { "rumble", "trigger_effects" },
            });
            Assert.False(added.IsError == true, AssistantRig.Text(added));
            string id = S(added).GetProperty("device").GetString();
            Assert.True(rig.Host.Sessions.Devices.Find(id).Device.Shape.TriggerEffects);
            var read = S(await client.CallToolAsync("get_device", AssistantRig.Args(("device", id))));
            var feedback = read.GetProperty("feedback").EnumerateArray().Select(f => f.GetString()).ToList();
            Assert.Contains("rumble", feedback);
            Assert.Contains("trigger effects", feedback);
        }

        /// <summary>get_device's state lists the buttons the device has,
        /// as the pipe's read of it does: a button set past them is not
        /// its own.</summary>
        [Fact]
        public async Task ADevicesState_ListsOnlyItsOwnButtons()
        {
            using var rig = new AssistantRig(AutomationLevel.Read);
            var state = new CustomInputState();
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), ProductName = "Four Buttons", IsOnline = true };
            ud.LoadCapabilities(2, 4, 0, InputDeviceType.Gamepad);
            ud.InputState = state;
            ud.ReadInputState = state;
            state.Buttons[1] = true;
            state.Buttons[10] = true;
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            var client = await rig.ConnectAsync();
            var read = S(await client.CallToolAsync("get_device", AssistantRig.Args(("device", AutomationDevices.IdOf(ud.InstanceGuid)), ("live", true))));
            Assert.Equal(new[] { 1 }, read.GetProperty("state").GetProperty("buttons_down").EnumerateArray().Select(b => b.GetInt32()));
        }

        /// <summary>record_problem's snapshot is refused in the command
        /// set's own words: a second inside a minute is too soon, and while
        /// the 20 that programs took wait for the user's review the limit is
        /// reached.</summary>
        [Fact]
        public async Task ASnapshotTooSoon_OrPastTheWaitingLimit_IsRefusedInItsOwnWords()
        {
            using var rig = new AssistantRig(AutomationLevel.Commands);
            long now = 1_000_000;
            rig.Host.Commands.NowMs = () => now;
            // Every snapshot taken here still waits for the user's review.
            rig.Host.Commands.StillWaits = _ => true;
            var client = await rig.ConnectAsync();
            var first = await client.CallToolAsync("record_problem", AssistantRig.Args(("seconds", 0)));
            Assert.False(first.IsError == true, AssistantRig.Text(first));
            var soon = await client.CallToolAsync("record_problem", AssistantRig.Args(("seconds", 0)));
            Assert.True(soon.IsError);
            Assert.Contains("(too-soon)", AssistantRig.Text(soon));
            Assert.Contains("minute", AssistantRig.Text(soon));
            for (int i = 1; i < AutomationCommands.MostWaitingFromPrograms; i++)
            {
                now += AutomationCommands.SnapshotEveryMs + 1;
                await Task.Delay(60);
                var taken = await client.CallToolAsync("record_problem", AssistantRig.Args(("seconds", 0)));
                Assert.False(taken.IsError == true, AssistantRig.Text(taken));
            }
            now += AutomationCommands.SnapshotEveryMs + 1;
            await Task.Delay(60);
            var past = await client.CallToolAsync("record_problem", AssistantRig.Args(("seconds", 0)));
            Assert.True(past.IsError);
            Assert.Contains("(limit-reached)", AssistantRig.Text(past));
            Assert.Contains("review on Home", AssistantRig.Text(past));
            // The cap counts snapshots alone: recordings have their own,
            // which record start meets, and the user's own are not counted.
            Assert.Contains(AutomationCommands.MostWaitingFromPrograms + " snapshots that programs took", AssistantRig.Text(past));
            Assert.DoesNotContain("recording", AssistantRig.Text(past));
        }

        /// <summary>set_value's release lets go of the name: no program's
        /// value is held and no session is its setter, so the gate the held
        /// value kept in force goes with it.</summary>
        [Fact]
        public async Task ReleasingAValue_LetsGoOfIt()
        {
            ScriptValues.ResetForTest();
            try
            {
                using var rig = new AssistantRig(AutomationLevel.SendInputs);
                var client = await rig.ConnectAsync();
                var set = await client.CallToolAsync("set_value", AssistantRig.Args(("name", "boost"), ("value", 0.5)));
                Assert.False(set.IsError == true, AssistantRig.Text(set));
                Assert.True(ScriptValues.AnySet);
                Assert.NotEqual(0, ScriptValues.SetterOf("boost"));
                var released = await client.CallToolAsync("set_value", AssistantRig.Args(("name", "boost"), ("release", true)));
                Assert.False(released.IsError == true, AssistantRig.Text(released));
                Assert.Equal(0, ScriptValues.SetterOf("boost"));
                Assert.False(ScriptValues.AnySet);
                Assert.Equal(0f, ScriptValues.Read("boost"));
            }
            finally
            {
                ScriptValues.ResetForTest();
            }
        }

        /// <summary>A keyboard and mouse controller with one row whose key
        /// the page names Mouse X, for set_mappings' card.</summary>
        private static AssistantRig KeyboardRig()
        {
            var rig = new AssistantRig(AutomationLevel.ChangeSettings, approvalWait: TimeSpan.FromSeconds(20), answerWait: TimeSpan.FromSeconds(10));
            rig.App.Controllers.Add(new AssistantController { Number = 2, Type = "keyboard_mouse" });
            rig.App.RowsOf[2] = new List<AssistantMappingRow> { new AssistantMappingRow { Target = "KbmMouseX", Label = "Mouse X", Source = string.Empty } };
            return rig;
        }

        /// <summary>set_mappings' card for one row, answered Not Now, or
        /// null when the call asked nothing.</summary>
        private static async Task<IReadOnlyList<string>> AskedFor(AssistantRig rig, McpClient client, Dictionary<string, object> row)
        {
            var call = client.CallToolAsync("set_mappings", AssistantRig.Args(("controller", 2), ("rows", new object[] { row }))).AsTask();
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1 || call.IsCompleted);
            if (call.IsCompleted) return null;
            var pending = Assert.Single(rig.Host.Approvals.Pending);
            var detail = pending.Request.Detail.ToList();
            Assert.True(rig.Host.Approvals.Answer(pending.Id, allowed: false));
            Assert.Contains("(declined)", AssistantRig.Text(await call));
            return detail;
        }

        /// <summary>A row given only a negative source on a keyboard and
        /// mouse controller presses a key too: the user is asked first, and
        /// the card names it.</summary>
        [Fact]
        public async Task SetMappingsOnAKeyboardAndMouseController_AsksForANegativeSourceAlone()
        {
            using var rig = KeyboardRig();
            var client = await rig.ConnectAsync();
            var asked = await AskedFor(rig, client, new Dictionary<string, object> { ["target"] = "KbmMouseX", ["negative_source"] = "Button 3" });
            Assert.True(asked != null, "set_mappings asked nothing, and wrote: " + string.Join("; ", rig.App.Calls));
            Assert.Contains(AssistantToolWords.KeyLine("Mouse X", "Button 3"), asked);
            Assert.DoesNotContain(rig.App.Calls, c => c.StartsWith("map 2", StringComparison.Ordinal));
        }

        /// <summary>set_mappings' card names each key by its name on the
        /// controller's page, as get_mappings' label gives it, never the
        /// setting's name the program sent.</summary>
        [Fact]
        public async Task SetMappingsCard_NamesTheKeyAsThePageDoes()
        {
            using var rig = KeyboardRig();
            var client = await rig.ConnectAsync();
            var asked = await AskedFor(rig, client, new Dictionary<string, object> { ["target"] = "kbmmousex", ["source"] = "Button 1" });
            Assert.NotNull(asked);
            Assert.Contains(AssistantToolWords.KeyLine("Mouse X", "Button 1"), asked);
            Assert.DoesNotContain(asked, line => line.Contains("KbmMouseX", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A stream that says it is short and gives more: a file a
        /// program keeps writing to after its length was read.</summary>
        private sealed class GrowingStream : System.IO.Stream
        {
            private long _given;
            private readonly long _total;
            public GrowingStream(long total) => _total = total;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 10;
            public override long Position { get => _given; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = (int)Math.Min(count, _total - _given);
                _given += n;
                return n;
            }
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>A profile file is read once, and never past the most a
        /// profile file holds, whatever length it gave when it was
        /// opened.</summary>
        [Fact]
        public void AProfileFileThatGrowsPastTheLimit_IsRefused()
        {
            Assert.Throws<System.IO.IOException>(() => AssistantTools.ReadProfileBytes(new GrowingStream(AssistantTools.LongestProfileFile + 100)));
            Assert.Equal(5000, AssistantTools.ReadProfileBytes(new GrowingStream(5000)).Length);
        }

        /// <summary>import_profile reads the bytes it was given and nothing
        /// else: no copy of them waits in the user's temp folder while the
        /// card asks, which another program could rewrite before the
        /// import.</summary>
        [Fact]
        public async Task AnImport_LeavesNoCopyInTheTempFolder_WhileTheCardAsks()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings, approvalWait: TimeSpan.FromSeconds(20), answerWait: TimeSpan.FromSeconds(10));
            var client = await rig.ConnectAsync();
            string temp = System.IO.Path.GetTempPath();
            var before = System.IO.Directory.GetFiles(temp, "PadForge-import-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var call = client.CallToolAsync("import_profile", AssistantRig.Args(("file_name", "racing.pfprofile"),
                ("content_base64", Convert.ToBase64String(new byte[] { 80, 75, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })))).AsTask();
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1 || call.IsCompleted);
            var pending = Assert.Single(rig.Host.Approvals.Pending);
            var made = System.IO.Directory.GetFiles(temp, "PadForge-import-*").Where(f => !before.Contains(f)).ToList();
            Assert.True(rig.Host.Approvals.Answer(pending.Id, allowed: false));
            await call;
            Assert.Empty(made);
        }

        /// <summary>A refusal goes out scrubbed as a result does: a private
        /// value in its words, such as a headset's name a picker lists, is
        /// taken out, and the IDs in it stay.</summary>
        [Fact]
        public async Task ARefusalsWords_AreScrubbed_AndKeepTheirIds()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var client = await rig.ConnectAsync();
            var refused = await client.CallToolAsync("set", AssistantRig.Args(("id", "settings/Settings_Choice"), ("value", "Nope")));
            Assert.True(refused.IsError);
            string text = AssistantRig.Text(refused);
            Assert.Contains("(out-of-range)", text);
            Assert.Contains("settings/Settings_Choice", text);
            Assert.DoesNotContain("zelda", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
