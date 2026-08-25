using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
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
    [Collection("HandheldRegistry")]
    public class HandheldButtonRegistryTests
    {
        private static void Reset() => HandheldButtonRegistry.LoadRegistry(null, "");

        private static HandheldButtonRegistry.Entry Chord(string name, params int[] keys) =>
            new HandheldButtonRegistry.Entry { Name = name, Keys = keys };

        private static HandheldButtonRegistry.Entry Report(string name, int byteIndex, byte mask) =>
            new HandheldButtonRegistry.Entry
            {
                Name = name, Collection = "17EF:6182:FFA0:0001", ReportId = 1,
                ByteIndex = byteIndex, Mask = mask, ValueKind = VendorButtonKind.Bit,
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
            both.Collection = "17EF:6182:FFA0:0001"; both.ReportId = 1; both.ByteIndex = 21; both.Mask = 0x40;
            HandheldButtonRegistry.Register(both);
            var ally = new HandheldButtonRegistry.Entry
            {
                Name = "M1", Collection = "0B05:1ABE:FF31:0076", ReportId = 0x5A,
                ByteIndex = 1, Value = 166, ValueKind = VendorButtonKind.Value,
            };
            HandheldButtonRegistry.Register(ally);

            var dto = HandheldButtonRegistry.SaveRegistry().Select(HandheldButtonData.From).ToArray();
            Reset();
            HandheldButtonRegistry.LoadRegistry(dto.Select(d => d.ToEntry()), "X|Y");

            var e = HandheldButtonRegistry.Entries;
            Assert.Equal(2, e.Count);
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
    [Collection("HandheldRegistry")]
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
            { Name = "Y1", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x80, ValueKind = VendorButtonKind.Bit };
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
            { Name = "Y1", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x80, ValueKind = VendorButtonKind.Bit };
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
            { Name = "Y1", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x80, ValueKind = VendorButtonKind.Bit };
            var y2 = new HandheldButtonRegistry.Entry
            { Name = "Y2", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x40, ValueKind = VendorButtonKind.Bit };
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
            { Name = "Y1", Collection = Legion, ReportId = 1, ByteIndex = 20, Mask = 0x80, ValueKind = VendorButtonKind.Bit };
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
            Assert.Equal(VendorButtonKind.Bit, c.Kind);
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
        public void Silence_PastTheStaleWindow_ReadsOffline()
        {
            long now = 0;
            var dev = new SystemMotionDevice(new MachineIdentity { Manufacturer = "T", ProductName = "T" }, () => now);
            dev.AttachForTest(false);
            Assert.NotNull(dev.GetCurrentState()); // before any sample: zero baseline
            dev.InjectSample(new[] { 1.0, 0.0, 0.0 }, null);
            now += System.Diagnostics.Stopwatch.Frequency * (SystemMotionDevice.StaleWindowMs + 500) / 1000;
            Assert.Null(dev.GetCurrentState());
            Assert.Equal(InputDeviceType.SystemMotion, dev.GetInputDeviceType());
            Assert.False(dev.HasAccel);
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
