using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #343 registry: the NFC stability rules applied to learned
    /// handheld buttons. Stable index per entry, lowest free on add, no
    /// renumbering on remove, deduped names, persisted button honored.
    /// Shares the static registry with the device tests, so both classes
    /// sit in one collection and each test resets first.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HandheldButtonRegistryTests
    {
        private static void Reset() => HandheldButtonRegistry.LoadRegistry(null, "");

        private static HandheldButtonRegistry.Entry Chord(string name, params int[] keys) =>
            new HandheldButtonRegistry.Entry { Name = name, Keys = keys };

        private static HandheldButtonRegistry.Entry Report(string name, int byteIndex, byte mask) =>
            new HandheldButtonRegistry.Entry
            {
                Name = name, Collection = "17EF:6182:FFA0:0001", ReportId = 1,
                ByteIndex = byteIndex, Mask = mask, Value = mask, ValueKind = VendorButtonKind.Bit,
            };

        [Fact]
        public void Register_AssignsSequentialButtonsFromZero()
        {
            Reset();
            Assert.Equal(0, HandheldButtonRegistry.Register(Chord("A", 0xA2, 0x5B, 0x80)).Button);
            Assert.Equal(1, HandheldButtonRegistry.Register(Chord("B", 0x5B, 0x44)).Button);
            Assert.Equal(2, HandheldButtonRegistry.Register(Report("C", 20, 0x80)).Button);
        }

        [Fact]
        public void Remove_DoesNotRenumberSurvivors_AndFreesTheSlot()
        {
            Reset();
            HandheldButtonRegistry.Register(Chord("A", 0x80));
            HandheldButtonRegistry.Register(Chord("B", 0x81));
            HandheldButtonRegistry.Register(Chord("C", 0x82));
            HandheldButtonRegistry.Remove(0);
            var e = HandheldButtonRegistry.Entries;
            Assert.Equal(new[] { 1, 2 }, e.Select(x => x.Button).ToArray());
            Assert.Equal(0, HandheldButtonRegistry.Register(Chord("D", 0x83)).Button);
        }

        [Fact]
        public void Register_RejectsAnEntryWithNoDeliveryPath()
        {
            Reset();
            Assert.Null(HandheldButtonRegistry.Register(new HandheldButtonRegistry.Entry { Name = "Empty" }));
            Assert.Equal(0, HandheldButtonRegistry.Count);
        }

        [Fact]
        public void Register_DedupesNames()
        {
            Reset();
            HandheldButtonRegistry.Register(Chord("Paddle", 0x80));
            var second = HandheldButtonRegistry.Register(Chord("paddle", 0x81));
            Assert.Equal("paddle (2)", second.Name);
        }

        [Fact]
        public void Load_HonorsStoredButtons_ReassignsCollisions()
        {
            Reset();
            var a = Chord("A", 0x80); a.Button = 5;
            var b = Chord("B", 0x81); b.Button = 5;   // collides with A
            var c = Chord("C", 0x82); c.Button = 999; // out of range
            HandheldButtonRegistry.LoadRegistry(new[] { a, b, c }, "LENOVO|83E1");
            var e = HandheldButtonRegistry.Entries.OrderBy(x => x.Name).ToList();
            Assert.Equal(5, e[0].Button);
            Assert.Equal(0, e[1].Button);
            Assert.Equal(1, e[2].Button);
            Assert.Equal("LENOVO|83E1", HandheldButtonRegistry.MachineKey);
        }

        [Fact]
        public void SaveRegistry_RoundTripsThroughTheSettingsDto()
        {
            Reset();
            var both = Chord("Desktop", 0x5B, 0x44);
            both.Collection = "17EF:6182:FFA0:0001"; both.ReportId = 1; both.ByteIndex = 21; both.Mask = 0x40; both.Value = 0x40;
            HandheldButtonRegistry.Register(both);
            var ally = new HandheldButtonRegistry.Entry
            {
                Name = "M1", Collection = "0B05:1ABE:FF31:0076", ReportId = 0x5A,
                ByteIndex = 1, Value = 166, ValueKind = VendorButtonKind.Value,
            };
            HandheldButtonRegistry.Register(ally);
            HandheldButtonRegistry.Register(new HandheldButtonRegistry.Entry
            { Name = "Vantage", WmiClass = "LENOVO_UTILITY_EVENT", WmiProperty = "PressTypeDataVal", WmiValue = "72" });

            var dto = HandheldButtonRegistry.SaveRegistry().Select(HandheldButtonData.From).ToArray();
            Reset();
            HandheldButtonRegistry.LoadRegistry(dto.Select(d => d.ToEntry()), "X|Y");

            var e = HandheldButtonRegistry.Entries;
            Assert.Equal(3, e.Count);
            Assert.True(e[2].HasWmi);
            Assert.False(e[2].HasChord);
            Assert.False(e[2].HasReport);
            Assert.Equal("72", e[2].WmiValue);
            Assert.Contains("LENOVO_UTILITY_EVENT", HandheldButtonRegistry.RequiredWmiClasses);
            Assert.Equal(new[] { 0x5B, 0x44 }, e[0].Keys);
            Assert.True(e[0].HasReport);
            Assert.Equal(21, e[0].ByteIndex);
            Assert.Equal(0x40, e[0].Mask);
            Assert.Equal(VendorButtonKind.Bit, e[0].ValueKind);
            Assert.False(e[1].HasChord);
            Assert.Equal(VendorButtonKind.Value, e[1].ValueKind);
            Assert.Equal(166, e[1].Value);
            Assert.Equal(0x5A, e[1].ReportId);
        }

        [Fact]
        public void RequiredCollections_NamesOnlyReportEntries()
        {
            Reset();
            HandheldButtonRegistry.Register(Chord("A", 0x80));
            HandheldButtonRegistry.Register(Report("B", 20, 0x80));
            var req = HandheldButtonRegistry.RequiredCollections;
            Assert.Single(req);
            Assert.Contains("17EF:6182:FFA0:0001", req);
            Assert.True(HandheldButtonRegistry.HasChords);
        }

        [Fact]
        public void ActivityChanged_FiresOnCaptureAndFeatureFlips_OncePerChange()
        {
            int fired = 0;
            EventHandler h = (s, e) => fired++;
            HandheldButtonRegistry.ActivityChanged += h;
            try
            {
                bool cap = HandheldButtonRegistry.LearnCaptureActive;
                HandheldButtonRegistry.LearnCaptureActive = !cap;
                HandheldButtonRegistry.LearnCaptureActive = !cap; // same value: no event
                HandheldButtonRegistry.LearnCaptureActive = cap;
                Assert.Equal(2, fired);
            }
            finally { HandheldButtonRegistry.ActivityChanged -= h; }
        }
    }

    /// <summary>
    /// Issue #343 device row: report definitions evaluate against the
    /// bytes the vendor reader delivers, with the NFC edge rule (state is
    /// written every poll), a minimum press for bit buttons, and the hold
    /// window for code-style buttons that never send a release.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HandheldButtonsDeviceTests
    {
        private const string Legion = "17EF:6182:FFA0:0001";

        private static HandheldButtonsDevice Fresh(params HandheldButtonRegistry.Entry[] entries)
        {
            HandheldButtonRegistry.LoadRegistry(entries, "T|T");
            var dev = new HandheldButtonsDevice(new MachineIdentity { Manufacturer = "T", ProductName = "T" });
            dev.AttachForTest();
            return dev;
        }

        private static byte[] LegionReport(byte b20, byte b21 = 0)
        {
            var r = new byte[64];
            r[0] = 0x01; r[1] = 0x3C; r[20] = b20; r[21] = b21;
            return r;
        }

        [Fact]
        public void BitButton_FollowsTheReport_WithAMinimumPress()
        {
            var y1 = new HandheldButtonRegistry.Entry
            { Name = "Y1", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x80, Value = 0x80, ValueKind = VendorButtonKind.Bit };
            using var dev = Fresh(y1);

            Assert.False(dev.GetCurrentState().Buttons[0]);
            dev.InjectReportForTest(Legion, LegionReport(0x80));
            Assert.True(dev.GetCurrentState().Buttons[0]);
            dev.InjectReportForTest(Legion, LegionReport(0x00));
            // Released at once by the report, but the 175 ms minimum press
            // keeps the button visible to a slow poll.
            Assert.True(dev.GetCurrentState().Buttons[0]);
            Thread.Sleep(230);
            Assert.False(dev.GetCurrentState().Buttons[0]);
        }

        [Fact]
        public void ValueButton_HoldsAfterItsLastReport_ThenReleases()
        {
            const string ally = "0B05:1ABE:FF31:0076";
            var m1 = new HandheldButtonRegistry.Entry
            { Name = "M1", Collection = ally, ReportId = 0x5A, ByteIndex = 1, Value = 166, ValueKind = VendorButtonKind.Value };
            using var dev = Fresh(m1);
            var report = new byte[64]; report[0] = 0x5A; report[1] = 166;
            dev.InjectReportForTest(ally, report);
            Assert.True(dev.GetCurrentState().Buttons[0]);
            Thread.Sleep(VendorReportLearner.ValueHoldMs + 60);
            Assert.False(dev.GetCurrentState().Buttons[0]);
        }

        [Fact]
        public void OtherReportIds_AndOtherCollections_LeaveTheButtonAlone()
        {
            var y1 = new HandheldButtonRegistry.Entry
            { Name = "Y1", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x80, Value = 0x80, ValueKind = VendorButtonKind.Bit };
            using var dev = Fresh(y1);
            var other = LegionReport(0x80); other[0] = 0x04;
            dev.InjectReportForTest(Legion, other);
            Assert.False(dev.GetCurrentState().Buttons[0]);
            dev.InjectReportForTest("1A86:FE00:FF00:0001", LegionReport(0x80));
            Assert.False(dev.GetCurrentState().Buttons[0]);
        }

        [Fact]
        public void TwoBitsInOneByte_AreTwoIndependentButtons()
        {
            var y1 = new HandheldButtonRegistry.Entry
            { Name = "Y1", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x80, Value = 0x80, ValueKind = VendorButtonKind.Bit };
            var y2 = new HandheldButtonRegistry.Entry
            { Name = "Y2", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x40, Value = 0x40, ValueKind = VendorButtonKind.Bit };
            using var dev = Fresh(y1, y2);
            dev.InjectReportForTest(Legion, LegionReport(0x40));
            var s = dev.GetCurrentState();
            Assert.False(s.Buttons[0]);
            Assert.True(s.Buttons[1]);
        }

        [Fact]
        public void RegistryChange_DropsAStaleButtonState_AndGrowsTheSpan()
        {
            var y1 = new HandheldButtonRegistry.Entry
            { Name = "Y1", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x80, Value = 0x80, ValueKind = VendorButtonKind.Bit };
            using var dev = Fresh(y1);
            dev.InjectReportForTest(Legion, LegionReport(0x80));
            Assert.True(dev.GetCurrentState().Buttons[0]);
            Assert.Equal(1, dev.RawButtonCount);

            HandheldButtonRegistry.Remove(0);
            Assert.False(dev.GetCurrentState().Buttons[0]);
            Assert.Equal(0, dev.RawButtonCount);

            HandheldButtonRegistry.Register(new HandheldButtonRegistry.Entry { Name = "K", Keys = new[] { 0x80 } });
            HandheldButtonRegistry.Register(new HandheldButtonRegistry.Entry { Name = "L", Keys = new[] { 0x81 } });
            Assert.Equal(2, dev.RawButtonCount);
            Assert.Equal(new[] { 0, 1 }, dev.SupportedButtonIndices);
            Assert.Equal(new[] { "K", "L" }, dev.GetDeviceObjects().Select(o => o.Name).ToArray());
        }

        [Fact]
        public void WmiButton_PulsesOnItsEvent_IgnoresOtherValues()
        {
            // Legion Pro 7 (hardware-captured 2026-08-25): the Vantage key is
            // LENOVO_UTILITY_EVENT PressTypeDataVal=72 and Smart Connect is
            // 1, and nothing arrives on any HID collection or as a key.
            var vantage = new HandheldButtonRegistry.Entry
            { Name = "Vantage", WmiClass = "LENOVO_UTILITY_EVENT", WmiProperty = "PressTypeDataVal", WmiValue = "72" };
            using var dev = Fresh(vantage);
            WmiEventRuntime.RaiseForTest(new WmiEventRuntime.Event
            { ClassName = "LENOVO_UTILITY_EVENT", Props = { ("PressTypeDataVal", "1") } });
            Assert.False(dev.GetCurrentState().Buttons[0]);
            WmiEventRuntime.RaiseForTest(new WmiEventRuntime.Event
            { ClassName = "LENOVO_UTILITY_EVENT", Props = { ("PressTypeDataVal", "72") } });
            Assert.True(dev.GetCurrentState().Buttons[0]);
            Thread.Sleep(230);
            Assert.False(dev.GetCurrentState().Buttons[0]);
        }

        [Fact]
        public void PinnedWmiButton_FiresThroughStep3_FromAnotherDevicesPass()
        {
            // Bench 2026-08-25: the Vantage key recorded onto a virtual
            // controller button but never pressed it. This drives the exact
            // engine path: the row's source is pinned to the Hidden Buttons
            // device, the pass belongs to another device on the slot, and
            // the WMI event has just pulsed the learned button.
            var savedDevices = SettingsManager.UserDevices;
            var savedSettings = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var vantage = new HandheldButtonRegistry.Entry
                { Name = "Vantage", WmiClass = "LENOVO_UTILITY_EVENT", WmiProperty = "PressTypeDataVal", WmiValue = "72" };
                using var dev = Fresh(vantage);
                var ud = new UserDevice { CapType = InputDeviceType.HandheldButtons };
                ud.LoadFromExternalDevice(dev);
                ud.IsOnline = true;
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
                lock (SettingsManager.UserSettings.SyncRoot)
                    SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = dev.InstanceGuid, MapTo = 0 });
                Assert.Equal(1, ud.RawButtonCount);

                var ms = new MappingSet();
                var row = new MappingRow { Target = "ButtonX", LayerMask = "Base", CombineMode = "OR" };
                row.Sources.Add(new MappingSource { Kind = "Direct", DeviceGuid = dev.InstanceGuid.ToString().ToLowerInvariant(), Descriptor = "Button 0" });
                ms.Rows.Add(row);

                // Before the press: the row owns the target and reads released.
                ud.InputState = dev.GetCurrentState();
                var otherState = new CustomInputState();
                Assert.True(InputManager.TryEvaluateMappingSetButton(otherState, ms, "99999999-9999-9999-9999-999999999999", 0, "ButtonX", 50, out bool v0));
                Assert.False(v0);

                WmiEventRuntime.RaiseForTest(new WmiEventRuntime.Event
                { ClassName = "LENOVO_UTILITY_EVENT", Props = { ("PressTypeDataVal", "72") } });
                ud.InputState = dev.GetCurrentState();
                Assert.True(ud.InputState.Buttons[0]);
                Assert.True(InputManager.TryEvaluateMappingSetButton(otherState, ms, "99999999-9999-9999-9999-999999999999", 0, "ButtonX", 50, out bool v1));
                Assert.True(v1, "the pinned Hidden Buttons source must fire the target");
            }
            finally
            {
                SettingsManager.UserDevices = savedDevices;
                SettingsManager.UserSettings = savedSettings;
            }
        }

        [Fact]
        public void Identity_IsStableForTheMachineKey_AndSyntheticPath()
        {
            var id = new MachineIdentity { Manufacturer = "LENOVO", ProductName = "83E1", Family = "Legion Go" };
            var a = new HandheldButtonsDevice(id);
            var b = new HandheldButtonsDevice(id);
            Assert.Equal(a.InstanceGuid, b.InstanceGuid);
            Assert.StartsWith("handheld://", a.DevicePath);
            Assert.Equal("Legion Go Hidden Buttons", a.Name);
            Assert.Equal(InputDeviceType.HandheldButtons, a.GetInputDeviceType());
        }
    }

    /// <summary>
    /// Issue #343 learn session: the timed-phase buckets feed the pure
    /// learner. Replay fixtures shaped like the two report families the
    /// references document: a state-bit report with IMU noise (Legion Go
    /// byte 20 under bytes 35 to 59 of motion), and an event-style report
    /// that exists only while a key is down (ROG Ally 0x5A).
    /// </summary>
    public class HandheldLearnSessionTests
    {
        private const string Legion = "17EF:6182:FFA0:0001";
        private const string Ally = "0B05:1ABE:FF31:0076";

        private static byte[] LegionFrame(Random rng, byte b20)
        {
            var r = new byte[64];
            r[0] = 0x01; r[1] = 0x3C; r[20] = b20;
            for (int i = 35; i < 60; i++) r[i] = (byte)rng.Next(256); // IMU words
            return r;
        }

        [Fact]
        public void LegionStyle_BitUnderImuNoise_LearnsOneBitCandidate()
        {
            var rng = new Random(7);
            var s = new HandheldLearnSession();
            s.SetPhase(HandheldLearnSession.Phase.Idle);
            for (int i = 0; i < 40; i++) s.OnReport(Legion, "Legion", LegionFrame(rng, 0), 64);
            s.SetPhase(HandheldLearnSession.Phase.Press);
            for (int i = 0; i < 40; i++) s.OnReport(Legion, "Legion", LegionFrame(rng, 0x80), 64);
            s.SetPhase(HandheldLearnSession.Phase.Release);
            for (int i = 0; i < 20; i++) s.OnReport(Legion, "Legion", LegionFrame(rng, 0), 64);

            var found = s.Finish();
            var c = Assert.Single(found);
            Assert.Equal(Legion, c.Collection);
            Assert.Equal(1, c.ReportId);
            Assert.Equal(20, c.ByteIndex);
            Assert.Equal(0x80, c.Mask);
            Assert.Equal(0x80, c.Value); // pressed pattern: active-high
            Assert.Equal(VendorButtonKind.Bit, c.Kind);
        }

        [Fact]
        public void ActiveLowBit_LearnsItsPressedPattern_AndEvaluatesPressedWhenClear()
        {
            var rng = new Random(11);
            var s = new HandheldLearnSession();
            s.SetPhase(HandheldLearnSession.Phase.Idle);
            for (int i = 0; i < 20; i++) s.OnReport(Legion, "Legion", LegionFrame(rng, 0x80), 64);
            s.SetPhase(HandheldLearnSession.Phase.Press);
            for (int i = 0; i < 20; i++) s.OnReport(Legion, "Legion", LegionFrame(rng, 0x00), 64);
            s.SetPhase(HandheldLearnSession.Phase.Release);
            for (int i = 0; i < 10; i++) s.OnReport(Legion, "Legion", LegionFrame(rng, 0x80), 64);

            var c = Assert.Single(s.Finish());
            Assert.Equal(0x80, c.Mask);
            Assert.Equal(0, c.Value);
            var def = new VendorButtonDefinition { ReportId = 1, ByteIndex = 20, Mask = c.Mask, Value = c.Value, Kind = c.Kind };
            Assert.True(def.Evaluate(LegionFrame(rng, 0x00)));
            Assert.False(def.Evaluate(LegionFrame(rng, 0x80)));
        }

        [Fact]
        public void AllyStyle_SilentWhileIdle_LearnsAValueCandidate()
        {
            var s = new HandheldLearnSession();
            s.SetPhase(HandheldLearnSession.Phase.Idle);
            // nothing arrives while idle
            s.SetPhase(HandheldLearnSession.Phase.Press);
            var press = new byte[64]; press[0] = 0x5A; press[1] = 166;
            s.OnReport(Ally, "Ally", press, 64);
            s.SetPhase(HandheldLearnSession.Phase.Release);

            var found = s.Finish();
            var c = Assert.Single(found);
            Assert.Equal(0x5A, c.ReportId);
            Assert.Equal(1, c.ByteIndex);
            Assert.Equal(166, c.Value);
            Assert.Equal(VendorButtonKind.Value, c.Kind);
        }

        [Fact]
        public void WmiEvent_DuringThePress_IsACandidate_InArrivalOrder()
        {
            // The captured Legion Pro 7 sequence: the utility event that
            // names the key, then the lighting side event both keys share.
            var s = new HandheldLearnSession();
            s.SetPhase(HandheldLearnSession.Phase.Idle);
            s.SetPhase(HandheldLearnSession.Phase.Press);
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_UTILITY_EVENT", Props = { ("PressTypeDataVal", "72") } });
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_LIGHTING_EVENT", Props = { ("Key_ID", "3") } });
            s.SetPhase(HandheldLearnSession.Phase.Release);

            var found = s.Finish();
            Assert.Equal(2, found.Count);
            Assert.True(found[0].IsWmi);
            Assert.Equal("LENOVO_UTILITY_EVENT", found[0].Collection);
            Assert.Equal("PressTypeDataVal", found[0].WmiProperty);
            Assert.Equal("72", found[0].WmiValue);
            Assert.Equal("LENOVO_LIGHTING_EVENT", found[1].Collection);
        }

        [Fact]
        public void WmiEvent_PressedOnceEarly_StillLearns()
        {
            // The user taps a moment before the press phase, then again on
            // cue: one idle copy must not cancel the press (bench 08-25).
            var s = new HandheldLearnSession();
            s.SetPhase(HandheldLearnSession.Phase.Idle);
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_UTILITY_EVENT", Props = { ("PressTypeDataVal", "72") } });
            s.SetPhase(HandheldLearnSession.Phase.Press);
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_UTILITY_EVENT", Props = { ("PressTypeDataVal", "72") } });
            s.SetPhase(HandheldLearnSession.Phase.Release);
            var c = Assert.Single(s.Finish());
            Assert.Equal("72", c.WmiValue);
            var n = s.Counts;
            Assert.Equal((0, 0, 0, 1, 1, 0), n);
        }

        [Fact]
        public void WmiEvent_RepeatingWhileIdle_IsNoise()
        {
            var s = new HandheldLearnSession();
            s.SetPhase(HandheldLearnSession.Phase.Idle);
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_AC_PD_EVENT", Props = { ("State", "1") } });
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_AC_PD_EVENT", Props = { ("State", "1") } });
            s.SetPhase(HandheldLearnSession.Phase.Press);
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_AC_PD_EVENT", Props = { ("State", "1") } });
            s.SetPhase(HandheldLearnSession.Phase.Release);
            Assert.Empty(s.Finish());
        }

        [Fact]
        public void WmiEvent_ThatAlsoFiresAtRest_IsNoise()
        {
            var s = new HandheldLearnSession();
            s.SetPhase(HandheldLearnSession.Phase.Idle);
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_AC_PD_EVENT", Props = { ("State", "1") } });
            s.SetPhase(HandheldLearnSession.Phase.Press);
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_AC_PD_EVENT", Props = { ("State", "1") } });
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_UTILITY_EVENT", Props = { ("PressTypeDataVal", "1") } });
            s.SetPhase(HandheldLearnSession.Phase.Release);
            // A periodic status keeps firing after release too; one idle copy
            // alone is treated as an early press (see the test above).
            s.OnWmiEvent(new WmiEventRuntime.Event { ClassName = "LENOVO_AC_PD_EVENT", Props = { ("State", "1") } });
            var c = Assert.Single(s.Finish());
            Assert.Equal("LENOVO_UTILITY_EVENT", c.Collection);
            Assert.Equal("1", c.WmiValue);
        }

        [Fact]
        public void NothingPressed_FindsNothing()
        {
            var rng = new Random(3);
            var s = new HandheldLearnSession();
            s.SetPhase(HandheldLearnSession.Phase.Idle);
            for (int i = 0; i < 20; i++) s.OnReport(Legion, "Legion", LegionFrame(rng, 0), 64);
            s.SetPhase(HandheldLearnSession.Phase.Press);
            for (int i = 0; i < 20; i++) s.OnReport(Legion, "Legion", LegionFrame(rng, 0), 64);
            s.SetPhase(HandheldLearnSession.Phase.Release);
            Assert.Empty(s.Finish());
            Assert.Null(s.ChordKeys);
        }

        [Fact]
        public void ChordCapture_RidesTheSameSession()
        {
            var s = new HandheldLearnSession();
            s.OnChordCaptured(new[] { 0xA2, 0x5B, 0x80 });
            Assert.Equal(new[] { 0xA2, 0x5B, 0x80 }, s.ChordKeys);
            s.OnChordCaptured(Array.Empty<int>()); // an empty capture never clears a real one
            Assert.Equal(3, s.ChordKeys.Length);
        }

        [Fact]
        public void ReportsAfterDone_AreIgnored()
        {
            var s = new HandheldLearnSession();
            s.SetPhase(HandheldLearnSession.Phase.Press);
            s.Finish();
            var press = new byte[8]; press[0] = 1; press[3] = 0x10;
            s.OnReport(Legion, "Legion", press, 8);
            Assert.Empty(s.Finish());
        }
    }

    /// <summary>
    /// Issue #343 system motion: Windows sensor units and frame into the
    /// SDL native frame. Gyrometer deg/s becomes rad/s on the same axes;
    /// accelerometer g in the gravity direction becomes m/s² of reaction,
    /// so a face-up device at rest (Windows Z = -1) reads +9.8 on SDL Z.
    /// </summary>
    public class SystemMotionDeviceTests
    {
        [Fact]
        public void Gyro_DegreesPerSecond_BecomeRadiansPerSecond_SameAxes()
        {
            var dev = new SystemMotionDevice(new MachineIdentity { Manufacturer = "T", ProductName = "T" });
            dev.AttachForTest(true);
            dev.InjectSample(new[] { 90.0, -45.0, 180.0 }, null);
            var s = dev.GetCurrentState();
            Assert.Equal(Math.PI / 2, s.Gyro[0], 4);
            Assert.Equal(-Math.PI / 4, s.Gyro[1], 4);
            Assert.Equal(Math.PI, s.Gyro[2], 4);
        }

        [Fact]
        public void Accel_FaceUpAtRest_ReadsPositiveGravityOnZ()
        {
            var dev = new SystemMotionDevice(new MachineIdentity { Manufacturer = "T", ProductName = "T" });
            dev.AttachForTest(true);
            dev.InjectSample(null, new[] { 0.0, 0.0, -1.0 });
            var s = dev.GetCurrentState();
            Assert.Equal(0f, s.Accel[0], 3);
            Assert.Equal(0f, s.Accel[1], 3);
            Assert.Equal(9.80665f, s.Accel[2], 3);
        }

        [Fact]
        public void Silence_KeepsTheLastSample_NeverFlapsOffline()
        {
            // A built-in sensor has no link to lose, and a driver under its
            // report threshold goes quiet at rest: the row must not flap.
            var dev = new SystemMotionDevice(new MachineIdentity { Manufacturer = "T", ProductName = "T" });
            dev.AttachForTest(false);
            Assert.NotNull(dev.GetCurrentState()); // before any sample: zero baseline
            dev.InjectSample(new[] { 90.0, 0.0, 0.0 }, null);
            var s = dev.GetCurrentState();
            Assert.NotNull(s);
            Assert.Equal(Math.PI / 2, s.Gyro[0], 4);
            Assert.Equal(InputDeviceType.SystemMotion, dev.GetInputDeviceType());
            Assert.False(dev.HasAccel);
            dev.Dispose();
            Assert.Null(dev.GetCurrentState());
        }
    }

    /// <summary>The device type ordinals are append-only (CapType is
    /// serialized as an int in PadForge.xml); the two #343 rows sit past
    /// Microphone and never move.</summary>
    public class HandheldDeviceTypePinTests
    {
        [Fact]
        public void HandheldOrdinals_ArePinnedPastMicrophone()
        {
            Assert.Equal(31, InputDeviceType.Microphone);
            Assert.Equal(32, InputDeviceType.HandheldButtons);
            Assert.Equal(33, InputDeviceType.SystemMotion);
        }
    }
}

namespace PadForge.Tests
{
    /// <summary>The ACPI-WMI _WDG parser (issue #343 follow-up): the
    /// firmware's own list of event GUIDs is the only thing that decides
    /// which WMI event classes the learner may subscribe to.</summary>
    public class AcpiWmiParserTests
    {
        private static byte[] Wdg(params (Guid Guid, byte Notify, byte Flags)[] entries)
        {
            var body = new System.Collections.Generic.List<byte>();
            foreach (var (g, notify, flags) in entries)
            {
                body.AddRange(g.ToByteArray());
                body.Add(notify); body.Add(0); body.Add(1); body.Add(flags);
            }
            // Name(_WDG, Buffer(size) { ... }) with a two-byte PkgLength
            // and a WordConst buffer size.
            var buf = new System.Collections.Generic.List<byte> { 0x08, (byte)'_', (byte)'W', (byte)'D', (byte)'G', 0x11 };
            int inner = 3 + body.Count; // WordConst prefix + 2 bytes + data
            int pkg = inner + 2;        // plus the two PkgLength bytes themselves
            buf.Add((byte)(0x40 | (pkg & 0x0F)));
            buf.Add((byte)(pkg >> 4));
            buf.Add(0x0B); buf.Add((byte)(body.Count & 0xFF)); buf.Add((byte)(body.Count >> 8));
            buf.AddRange(body);
            var padded = new System.Collections.Generic.List<byte> { 0x10, 0x20, 0x30 }; // leading AML noise
            padded.AddRange(buf);
            padded.AddRange(new byte[] { 0x5B, 0x82, 0x00 });
            return padded.ToArray();
        }

        [Fact]
        public void ParsesEventAndDataEntries_FromASyntheticTable()
        {
            var utility = Guid.Parse("8fc0de0c-b4e4-43fd-b0f3-8871711c1294"); // LENOVO_UTILITY_EVENT on the bench
            var lighting = Guid.Parse("1e3391a1-2c89-464d-95d9-3028b72e7a33"); // LENOVO_LIGHTING_EVENT
            var data = Guid.NewGuid();
            var aml = Wdg((utility, 0xD0, AcpiWmi.FlagEvent), (data, (byte)'A', 0x00), (lighting, 0xD1, (byte)(AcpiWmi.FlagEvent | AcpiWmi.FlagExpensive)));
            var blocks = new System.Collections.Generic.List<AcpiWmi.Block>();
            AcpiWmi.ParseWdg(aml, blocks);
            Assert.Equal(3, blocks.Count);
            Assert.Equal(utility, blocks[0].Guid);
            Assert.True(blocks[0].IsEvent);
            Assert.Equal(0xD0, blocks[0].NotifyId);
            Assert.False(blocks[1].IsEvent);
            Assert.True(blocks[2].IsEvent);
        }

        [Fact]
        public void IgnoresAnUnderscoreWdgThatIsNotABuffer()
        {
            var aml = new byte[] { (byte)'_', (byte)'W', (byte)'D', (byte)'G', 0x0A, 0x05, 0x00 };
            var blocks = new System.Collections.Generic.List<AcpiWmi.Block>();
            AcpiWmi.ParseWdg(aml, blocks);
            Assert.Empty(blocks);
        }
    }
}
