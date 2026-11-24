using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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
    /// An assistant's devices and inputs (#597): a device of its own through
    /// the scripts' own line verbs, a held control of any device, the next
    /// input the user makes, and files opened by path with the user's rights
    /// by the copy an assistant starts.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationAssistantDeviceTests
    {
        private static UserDevice AddPad(string name = "Test Pad")
        {
            var state = new CustomInputState();
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), ProductName = name, IsOnline = true };
            ud.LoadCapabilities(6, 16, 1, InputDeviceType.Gamepad);
            ud.InputState = state;
            ud.ReadInputState = state;
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        [Fact]
        public async Task ADeviceOfItsOwn_IsAddedDrivenAndRemoved_ThroughTheScriptsVerbs()
        {
            using var rig = new AssistantRig(AutomationLevel.SendInputs);
            var client = await rig.ConnectAsync();
            var added = await client.CallToolAsync("add_device", new Dictionary<string, object>
            {
                ["key"] = "wheel",
                ["name"] = "Assistant Wheel",
                ["buttons"] = new[] { "Shift Up", "Shift Down" },
                ["axes"] = new[] { new Dictionary<string, object> { ["name"] = "Steering" } },
                ["feedback"] = new[] { "rumble" },
            });
            Assert.False(added.IsError == true, AssistantRig.Text(added));
            string id = added.StructuredContent.Value.GetProperty("device").GetString();
            var entry = rig.Host.Sessions.Devices.Find(id);
            Assert.NotNull(entry);
            Assert.Equal(2, entry.Device.Shape.Buttons.Count);

            var set = await client.CallToolAsync("set_device_inputs", new Dictionary<string, object>
            {
                ["device"] = id,
                ["buttons"] = new Dictionary<string, object> { ["1"] = true },
                ["axes"] = new Dictionary<string, object> { ["0"] = 50000 },
            });
            Assert.False(set.IsError == true, AssistantRig.Text(set));
            var state = entry.Device.PeekState();
            Assert.True(state.Buttons[1]);
            Assert.Equal(50000, state.Axis[0]);

            // Another connection does not drive it.
            var other = await rig.ConnectAsync();
            var theirs = await other.CallToolAsync("set_device_inputs", new Dictionary<string, object> { ["device"] = id, ["buttons"] = new Dictionary<string, object> { ["0"] = true } });
            Assert.True(theirs.IsError);
            Assert.Contains("(not-owner)", AssistantRig.Text(theirs));

            var gone = await client.CallToolAsync("remove_device", AssistantRig.Args(("device", id)));
            Assert.False(gone.IsError == true, AssistantRig.Text(gone));
            // The verbs the tools ran wrote no line of their own into
            // Activity: each call is one entry, the tool's.
            Assert.DoesNotContain(rig.Host.Activity.Snapshot(), e => e.Asked.Kind is AutomationAskKind.Declare or AutomationAskKind.Set or AutomationAskKind.Gone);
            Assert.Equal(3, rig.Host.Activity.Snapshot().Count(e => e.Asked.Kind == AutomationAskKind.Tool && e.Succeeded
                && e.Asked.Argument is "add_device" or "set_device_inputs" or "remove_device"));
        }

        [Fact]
        public async Task AControlOfAnyDevice_IsHeld_AndReleased_AndTheHoldGoesWithTheConnection()
        {
            using var rig = new AssistantRig(AutomationLevel.SendInputs);
            var pad = AddPad();
            string id = AutomationDevices.IdOf(pad.InstanceGuid);
            var client = await rig.ConnectAsync();
            var held = await client.CallToolAsync("hold_control", new Dictionary<string, object> { ["device"] = id, ["control"] = "b3", ["value"] = 1 });
            Assert.False(held.IsError == true, AssistantRig.Text(held));
            var session = rig.Host.Sessions.Snapshot().Single(s => s.Kind == AutomationClientKind.Assistant);
            Assert.Equal(1, rig.Host.Sessions.HoldsOf(session));
            // The lease is kept while the protocol runs, so the hold stays.
            await Task.Delay(AutomationSession.DefaultLeaseMs + 600);
            Assert.Equal(1, rig.Host.Sessions.HoldsOf(session));
            var released = await client.CallToolAsync("hold_control", new Dictionary<string, object> { ["device"] = id, ["control"] = "b3", ["release"] = true });
            Assert.False(released.IsError == true, AssistantRig.Text(released));
            Assert.Equal(0, rig.Host.Sessions.HoldsOf(session));
            var bad = await client.CallToolAsync("hold_control", new Dictionary<string, object> { ["device"] = id, ["control"] = "x9", ["value"] = 1 });
            Assert.Contains("(bad-argument)", AssistantRig.Text(bad));
            // Below Send Inputs the tool is gone.
            rig.Host.Apply(true, AutomationLevel.Read);
            await AssistantRig.Until(() => !client.ListToolsAsync().AsTask().GetAwaiter().GetResult().Any(t => t.Name == "hold_control"));
        }

        [Fact]
        public async Task WaitForInput_GivesTheNextButtonTheUserPresses_WithTheInputSetMappingsTakes()
        {
            using var rig = new AssistantRig(AutomationLevel.Read);
            var pad = AddPad("Arcade Stick");
            pad.InputState.Buttons[2] = true;   // held before the wait: no press
            var client = await rig.ConnectAsync();
            var waiting = client.CallToolAsync("wait_for_input", new Dictionary<string, object> { ["seconds"] = 5 }).AsTask();
            await Task.Delay(300);
            pad.InputState.Buttons[5] = true;
            Interlocked.Increment(ref pad.InputStateSeq);
            var found = await waiting;
            var r = found.StructuredContent.Value;
            Assert.True(r.GetProperty("found").GetBoolean());
            Assert.Equal("button", r.GetProperty("kind").GetString());
            Assert.Equal(5, r.GetProperty("index").GetInt32());
            Assert.Equal("Button 5", r.GetProperty("input").GetString());
            Assert.Equal(AutomationDevices.IdOf(pad.InstanceGuid), r.GetProperty("device").GetString());
            Assert.Equal("Arcade Stick", r.GetProperty("device_name").GetProperty("outside_text").GetString());

            var none = await client.CallToolAsync("wait_for_input", new Dictionary<string, object> { ["seconds"] = 1 });
            Assert.False(none.StructuredContent.Value.GetProperty("found").GetBoolean());
            Assert.Equal("Up", AssistantTools.HatWord(0));
            Assert.Equal("Right", AssistantTools.HatWord(9000));
            Assert.Equal("Down", AssistantTools.HatWord(18000));
            Assert.Equal("Left", AssistantTools.HatWord(27000));
        }

        [Fact]
        public async Task ThroughTheMirror_AFileByPath_IsReadAndWrittenWithTheUsersRights_AndTheImportAsks()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings, approvalWait: TimeSpan.FromSeconds(20), answerWait: TimeSpan.FromSeconds(10));
            var mirror = new AssistantMirror(rig.PipeName, () => false, _ => true, "host.exe");
            var toMirror = new System.IO.Pipelines.Pipe();
            var fromMirror = new System.IO.Pipelines.Pipe();
            using var stop = new CancellationTokenSource();
            var serving = mirror.ServeAsync(new ModelContextProtocol.Server.StreamServerTransport(toMirror.Reader.AsStream(), fromMirror.Writer.AsStream(), "m"), stop.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(toMirror.Writer.AsStream(), fromMirror.Reader.AsStream()));
            await AssistantRig.Until(() => mirror.State == MirrorState.Connected);
            string folder = Path.Combine(Path.GetTempPath(), "PadForge-assistant-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string file = Path.Combine(folder, "racing.pfprofile");
                File.WriteAllBytes(file, new byte[] { 80, 75, 5, 6 });
                var import = client.CallToolAsync("import_profile", AssistantRig.Args(("path", file))).AsTask();
                await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1 || import.IsCompleted);
                var question = rig.Host.Approvals.Pending.Single();
                Assert.Contains(question.Request.Detail, d => d.Contains("racing.pfprofile", StringComparison.Ordinal));
                Assert.Contains("Runs x.exe", question.Request.Detail);
                rig.Host.Approvals.Answer(question.Id, true);
                var imported = await import;
                Assert.False(imported.IsError == true, AssistantRig.Text(imported));
                Assert.Contains("import", rig.App.Calls);

                string target = Path.Combine(folder, "out");
                var exported = await client.CallToolAsync("export_profile", AssistantRig.Args(("profile", "p1"), ("path", target)));
                Assert.False(exported.IsError == true, AssistantRig.Text(exported));
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target + ".pfprofile"));
                Assert.False(exported.StructuredContent.Value.TryGetProperty("content_base64", out _));

                var missing = await client.CallToolAsync("import_profile", AssistantRig.Args(("path", Path.Combine(folder, "none.pfprofile"))));
                Assert.True(missing.IsError);
                Assert.Contains("(no-file)", AssistantRig.Text(missing));
            }
            finally
            {
                stop.Cancel();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                await mirror.DisposeAsync();
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }

    /// <summary>
    /// Undo on the real services (#597): a program's run of changes, a
    /// profile's and an app setting's, put back in one step by the calls a
    /// profile switch makes.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationAssistantUndoTests : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
        private readonly string _active = SettingsManager.ActiveProfileId;
        private readonly ProfileData _pending = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly List<int> _xbox = SettingsManager.XboxSlotOrder;

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.Profiles = _profiles;
            SettingsManager.ActiveProfileId = _active;
            SettingsManager.PendingDefaultSnapshot = _pending;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
            SettingsManager.SlotMappingSets = _sets;
            SettingsManager.XboxSlotOrder = _xbox;
        }

        [Fact]
        public void AProfileChangeAndAnAppSettingChange_ComeBackAsOneStep()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData> { new ProfileData { Id = "p1", Name = "Racing" } };
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int> { 0 };
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            var input = new InputService(vm) { SettingsService = settings };
            vm.Settings.ProfileItems.Add(new ProfileListItem { Id = "p1", Name = "Racing" });
            int rate = vm.Settings.PollingRateHz;

            var undo = new AssistantUndo(() => AssistantUndoStore.Take(input, settings),
                state => AssistantUndoStore.Restore(state, input, settings, vm, null));
            int run = undo.BeginChange(7, "rename_profile", "Racing");
            SettingsManager.Profiles[0].Name = "Rally";
            vm.Settings.ProfileItems.Single(p => p.Id == "p1").Name = "Rally";
            Assert.Equal(run, undo.BeginChange(7, "set_setting", "Polling Rate"));
            vm.Settings.PollingRateHz = rate == 500 ? 250 : 500;
            SettingsManager.SlotEnabled[0] = false;

            var entry = new AutomationActivityEntry(DateTimeOffset.Now, AutomationProgram.Unknown, new AutomationAsk(AutomationAskKind.Tool, "set_setting"), "ok", true) { Spell = run };
            Assert.True(undo.CanUndo(entry));
            string back = undo.Undo(entry);
            Assert.Contains("Put back", back);
            Assert.Equal("Racing", SettingsManager.Profiles.Single().Name);
            Assert.Equal("Racing", vm.Settings.ProfileItems.Single(p => p.Id == "p1").Name);
            Assert.Equal(rate, vm.Settings.PollingRateHz);
            Assert.True(SettingsManager.SlotEnabled[0]);
            Assert.False(undo.CanUndo(entry));

            // Another program's change after a run leaves the first run's
            // program nothing it can undo alone.
            undo.BeginChange(7, "create_profile", "A");
            undo.BeginChange(8, "create_profile", "B");
            Assert.Equal("nothing-to-undo", Assert.Throws<AssistantRefusal>(() => undo.UndoNewestOf(7)).Code);
        }
    }
}
