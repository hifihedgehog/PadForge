using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A program's reach (#600 DESIGN-2 N3): held to its own devices and the
    /// ones the user picked, it drives and changes only those, runs no
    /// command but the ones that read, sets no named value, and still reads
    /// every device. A program with all of PadForge is unchanged. The pipe's
    /// lines and the assistant tools apply the same check.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ProgramReachTests
    {
        private const string Pad = "{\"buttons\":[\"A\",\"B\"],\"axes\":[{\"name\":\"Left X\"}],\"hats\":[\"View\"]}";

        [Fact]
        public void AScopedScript_DrivesItsOwnDevice_IsRefusedAnotherWithWords_RunsNoCommand_AndStillReadsEveryDevice()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            rig.EachConnectionItsOwnProcess();
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Xbox Wireless Controller");
            var mouse = rig.AddDevice(InputDeviceType.Mouse, "Mouse");
            string padId = AutomationPipeRig.IdOf(pad), mouseId = AutomationPipeRig.IdOf(mouse);

            using var scoped = rig.Open("Steer Assist", " reach=own");
            string declared = scoped.Final("declare key=steer name=\"Steer\" " + Pad);
            Assert.StartsWith("ok id=", declared);
            string own = declared.Substring(6).Split(' ')[0];
            Assert.Equal("ok", scoped.Final("own " + own + " A=1"));
            string refused = scoped.Final("own " + padId + " B=1");
            Assert.StartsWith("err outside-reach ", refused);
            Assert.Contains("its own devices and the ones the user picked", refused);
            Assert.Contains("still reads every device", refused);
            Assert.StartsWith("err outside-reach", scoped.Final("var speed=4"));
            Assert.StartsWith("err outside-reach", scoped.Final("engine stop"));
            Assert.StartsWith("err outside-reach", scoped.Final("activate default"));
            Assert.StartsWith("err outside-reach", scoped.Final("exit"));
            // Reads stay whole, keyboards and mice included.
            var devices = scoped.Ask("devices");
            Assert.Contains(devices, d => d.Contains(padId, StringComparison.Ordinal));
            Assert.Contains(devices, d => d.Contains(mouseId, StringComparison.Ordinal));
            Assert.StartsWith("ok", scoped.Final("read " + padId));
            Assert.StartsWith("ok", scoped.Final("state"));
            Assert.StartsWith("err ", scoped.Final("sub " + padId + " A claim"));

            // A program with all of PadForge is unchanged.
            using var whole = rig.Open("Pedal Curve");
            Assert.Equal("ok", whole.Final("own " + padId + " B=1"));
            Assert.Equal("ok", whole.Final("var speed=4"));
        }

        [Fact]
        public async Task TheUserNarrowsAReachOnItsRow_ThePickedDeviceWorks_AndAHoldOutsideItIsLetGo()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            rig.EachConnectionItsOwnProcess();
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Xbox Wireless Controller");
            var wheel = rig.AddDevice(InputDeviceType.Gamepad, "Wheel");
            using var client = rig.Open("Steer Assist");
            Assert.Equal("ok", client.Final("own " + AutomationPipeRig.IdOf(wheel) + " A=1"));
            Assert.True(ControlHolds.Current.ContainsKey(wheel.InstanceGuid));
            string key = rig.Host.Sessions.Snapshot().Single(s => s.ClaimedName == "Steer Assist").Who.Key;

            rig.Host.Sessions.SetReach(key, new ProgramReach { Devices = new[] { new ReachDevice(pad.InstanceGuid, "Xbox Wireless Controller") } });
            await AutomationPipeRig.Until(() => ControlHolds.Current == null || !ControlHolds.Current.TryGetValue(wheel.InstanceGuid, out var held)
                || held.Buttons == null || held.Buttons.Length == 0);
            Assert.Equal("ok", client.Final("own " + AutomationPipeRig.IdOf(pad) + " A=1"));
            Assert.StartsWith("err outside-reach", client.Final("own " + AutomationPipeRig.IdOf(wheel) + " A=1"));

            // Back to all of PadForge, and the wheel is reached again.
            rig.Host.Sessions.SetReach(key, null);
            Assert.Equal("ok", client.Final("own " + AutomationPipeRig.IdOf(wheel) + " A=1"));
        }

        [Fact]
        public void AScopedScript_ThatClaimsAnotherProgramsName_GetsNoneOfItsDevices()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            int next = 0;
            // Each connection its own program: another image path each time.
            rig.Host.AutomationPipe.ProcessOf = _ => AutomationClientProcess.ForTest(700000 + ++next, @"C:\Tools\program" + next + ".exe");
            using var first = rig.Open("Steer Assist");
            string declared = first.Final("declare key=steer name=\"Steer\" " + Pad);
            Assert.StartsWith("ok id=", declared);
            string device = declared.Substring(6).Split(' ')[0];

            using var other = rig.Open("Steer Assist", " reach=own");
            Assert.StartsWith("err outside-reach", other.Final("own " + device + " A=1"));
            Assert.Equal("ok", first.Final("own " + device + " A=1"));
        }

        [Fact]
        public async Task TheUserNarrowsAReach_AndAClaimOutsideItIsLetGo()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            rig.EachConnectionItsOwnProcess();
            var pad = rig.AddDevice(InputDeviceType.Gamepad, "Xbox Wireless Controller");
            var wheel = rig.AddDevice(InputDeviceType.Gamepad, "Wheel");
            using var client = rig.Open("Steer Assist");
            Assert.StartsWith("ok", client.Final("sub " + AutomationPipeRig.IdOf(wheel) + " A claim"));
            await AutomationPipeRig.Until(() => ControlHolds.Current != null && ControlHolds.Current.TryGetValue(wheel.InstanceGuid, out var claimed)
                && (claimed.ClaimedButtons?.Length ?? 0) > 0);
            var session = rig.Host.Sessions.Snapshot().Single(s => s.ClaimedName == "Steer Assist");
            Assert.Equal(1, rig.Host.Sessions.HeldAndClaimedBy(session));
            rig.Host.Sessions.SetReach(session.Who.Key, new ProgramReach { Devices = new[] { new ReachDevice(pad.InstanceGuid, "Xbox Wireless Controller") } });
            Assert.Equal(0, rig.Host.Sessions.HeldAndClaimedBy(session));
        }

        [Fact]
        public async Task AHelloThatAsksForItsOwnDevices_IsNamedOnTheCard_AndKeptWithItsConsent()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.ChangeSettings);
            var consent = TestConsent.Empty(rig.Host);
            int next = 524200;
            rig.Host.AutomationPipe.ProcessOf = _ => AutomationClientProcess.ForTest(Interlocked.Increment(ref next), @"C:\AutoHotkey\AutoHotkey64.exe");
            AutomationConsent.CommandLineSeam = _ => "\"C:\\AutoHotkey\\AutoHotkey64.exe\" C:\\Scripts\\steer.ahk";
            try
            {
                var client = rig.Connect();
                client.Final("hello name=\"Steer Assist\" kind=script level=send-inputs reach=own");
                await AutomationPipeRig.Until(() => rig.Host.Approvals.Pending.Count == 1);
                var detail = rig.Host.Approvals.Pending[0].Request.Detail;
                var s = PadForge.Resources.Strings.Strings.Instance;
                Assert.Equal("It asks to drive only its own devices.", s.Automation_Consent_OwnDevices);
                Assert.Contains(s.Automation_Consent_OwnDevices, detail);
                Assert.Contains(s.Automation_Consent_ReadsKeys, detail);
                Assert.Contains(s.Automation_Consent_AsksOnceReach, detail);
                Assert.True(rig.Host.Approvals.Answer(rig.Host.Approvals.Pending[0].Id, true));
                await AutomationPipeRig.Until(() => consent.Programs.Count == 1);
                Assert.NotNull(consent.Programs[0].Reach);

                // A bad word is refused.
                Assert.StartsWith("err bad-argument", rig.Connect().Final("hello name=\"X\" kind=script reach=some"));
            }
            finally
            {
                AutomationConsent.CommandLineSeam = null;
            }
        }

        [Fact]
        public void TheReach_RoundTripsThroughTheFile_AndAHelloNarrowsButNeverWidens()
        {
            var store = new AutomationConsent();
            var picked = new ProgramReach { Devices = new[] { new ReachDevice(Guid.NewGuid(), "Xbox Wireless Controller") } };
            store.Allow(new AllowedProgram("script|X", "Steer Assist", "script", @"C:\x.exe", null, null, AutomationLevel.SendInputs) { Reach = picked });
            var xml = new XmlSerializer(typeof(AllowedProgramData[]));
            var text = new StringWriter();
            xml.Serialize(text, store.Save());
            var back = new AutomationConsent();
            back.Load((AllowedProgramData[])xml.Deserialize(new StringReader(text.ToString())));
            Assert.Equal(picked, back.Programs.Single().Reach);
            Assert.Equal("Xbox Wireless Controller", back.Programs.Single().Reach.Devices.Single().Name);
            // A program allowed again keeps the reach its row has.
            back.Allow(back.Programs.Single() with { Reach = null, Level = AutomationLevel.ChangeSettings });
            Assert.Equal(picked, back.Programs.Single().Reach);
            Assert.True(back.SetReach("script|X", null));
            Assert.Null(back.Programs.Single().Reach);

            Assert.Null(ProgramReach.Narrowest(null, null));
            Assert.Equal(ProgramReach.OwnDevices, ProgramReach.Narrowest(null, ProgramReach.OwnDevices));
            Assert.Equal(picked, ProgramReach.Narrowest(picked, null));
            Assert.Empty(ProgramReach.Narrowest(picked, ProgramReach.OwnDevices).Devices);
        }

        /// <summary>Every tool has its verdict under a reach. A tool added
        /// later is refused until it is named, and this list fails until it
        /// is classed on purpose.</summary>
        [Fact]
        public void EveryTool_HasItsVerdictUnderAReach_ReadsWhole_AndTheCommandsToolsRefused()
        {
            var verdicts = AssistantTools.All.ToDictionary(t => t.Name, AssistantTools.UnderReach);
            Assert.Equal(new[] { "add_device", "hold_control", "remove_device", "set_device_inputs" },
                verdicts.Where(v => v.Value == ReachVerdict.ItsDevices).Select(v => v.Key).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(new[] { "press", "set" },
                verdicts.Where(v => v.Value == ReachVerdict.EntriesOfItsDevices).Select(v => v.Key).OrderBy(n => n, StringComparer.Ordinal));
            foreach (var tool in AssistantTools.All.Where(t => t.ReadOnly)) Assert.Equal(ReachVerdict.Whole, verdicts[tool.Name]);
            foreach (string name in new[] { "switch_profile", "set_engine", "set_controllers_power", "record_problem", "set_value" })
                Assert.Equal(ReachVerdict.Refused, verdicts[name]);
            foreach (string name in new[] { "status", "list_devices", "get_device", "wait_for_input", "read", "find", "where" })
                Assert.Equal(ReachVerdict.Whole, verdicts[name]);
            Assert.Equal(AssistantTools.All.Count(t => !t.ReadOnly) - 6, verdicts.Count(v => v.Value == ReachVerdict.Refused));
        }

        [Fact]
        public async Task AScopedAssistant_IsRefusedACommandTool_AndAnEntryOfAnotherDevice_ButSetsOneOfAPickedDevice()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            var client = await rig.ConnectAsync();
            Assert.NotEqual(true, (await client.CallToolAsync("status")).IsError);
            var session = rig.Host.Sessions.Snapshot().Single(s => s.Kind == AutomationClientKind.Assistant);
            var picked = Guid.NewGuid();
            rig.Host.Sessions.SetReach(session.Who.Key, new ProgramReach { Devices = new[] { new ReachDevice(picked, "Pad") } });

            var profile = await client.CallToolAsync("switch_profile", AssistantRig.Args(("profile", "x")));
            Assert.True(profile.IsError);
            Assert.Contains("reach", AssistantRig.Text(profile));
            Assert.NotEqual(true, (await client.CallToolAsync("status")).IsError);

            rig.Settings.Holders["pad/Pad_Deadzone@c1"] = Guid.NewGuid();
            var other = await client.CallToolAsync("set", AssistantRig.Args(("id", "pad/Pad_Deadzone@c1"), ("value", 5)));
            Assert.True(other.IsError);
            Assert.Contains("belongs to none of them", AssistantRig.Text(other));
            var notHeld = await client.CallToolAsync("press", AssistantRig.Args(("id", "settings.action/Settings_Press")));
            Assert.True(notHeld.IsError);

            rig.Settings.Holders["pad/Pad_Deadzone@c1"] = picked;
            var mine = await client.CallToolAsync("set", AssistantRig.Args(("id", "pad/Pad_Deadzone@c1"), ("value", 5)));
            Assert.NotEqual(true, mine.IsError);

            // All of PadForge again: every tool works as before.
            rig.Host.Sessions.SetReach(session.Who.Key, null);
            Assert.NotEqual(true, (await client.CallToolAsync("set", AssistantRig.Args(("id", "pad/Pad_Deadzone@c1"), ("value", 6)))).IsError);
        }

        [Fact]
        public void TheAllowedRow_SaysTheReach_AndItsChoiceSetsIt()
        {
            var picked = new ProgramReach { Devices = new[] { new ReachDevice(Guid.NewGuid(), "Xbox Wireless Controller") } };
            var set = new List<(string, ProgramReach)>();
            var row = new AllowedProgramItem(new AllowedProgramView("k", "Steer Assist", @"AutoHotkey64.exe running C:\Scripts\steer.ahk",
                AutomationLevel.SendInputs, null, true, picked), _ => { }, (k, r) => set.Add((k, r)), (n, r) => r);
            Assert.EndsWith("Drives its own devices and Xbox Wireless Controller. It still reads every device."
                + " Commands from the command line still reach PadForge, as they do for every program.", row.Line);
            Assert.Equal(1, row.SelectedReach);
            Assert.True(row.IsLimited);
            row.SelectedReach = 0;
            Assert.Equal(("k", (ProgramReach)null), set.Single());
            row.PickCommand.Execute(null);
            Assert.Equal(picked, set.Last().Item2);

            var whole = new AllowedProgramItem(new AllowedProgramView("w", "Pedal Curve", "python.exe", AutomationLevel.Read, null, false), _ => { }, (k, r) => set.Add((k, r)));
            Assert.Equal(0, whole.SelectedReach);
            Assert.False(whole.IsLimited);
            Assert.DoesNotContain("Drives", whole.Line);
            whole.SelectedReach = 1;
            Assert.Equal(ProgramReach.OwnDevices, set.Last().Item2);
        }
    }
}
