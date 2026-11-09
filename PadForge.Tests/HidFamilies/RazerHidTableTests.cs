using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>A channel that records what a session sends and answers
    /// input reads and feature reads from queues the test fills.</summary>
    internal sealed class RazerScriptChannel : IHidReportChannel
    {
        public readonly List<string> Log = new();
        public readonly List<byte[]> Writes = new();

        /// <summary>The answer to each input read, in turn, after a write
        /// whose bytes the key matches. A read with none answers 0.</summary>
        public Func<byte[], byte[]> AnswerTo = _ => null;
        private byte[] _pending;

        public Func<byte[], byte[]> FeatureReply = _ => null;
        public Func<byte[], bool> Fails = _ => false;

        public string Path { get; init; } = @"\\?\hid#vid_1532&pid_0000&mi_00#test";
        public bool FeatureOnly => false;
        public int InputReportLength { get; init; } = 37;
        public int OutputReportLength { get; init; } = 65;
        public int FeatureReportLength { get; init; } = 91;

        public bool SetFeature(byte[] report)
        {
            if (Fails(report)) return false;
            Log.Add("F " + Hex(report, 16));
            Writes.Add((byte[])report.Clone());
            return true;
        }

        public bool GetFeature(byte reportId, byte[] buffer)
        {
            Log.Add("G " + reportId.ToString("X2"));
            var answer = FeatureReply(Writes.LastOrDefault());
            if (answer == null) return false;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return true;
        }

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (Fails(report)) return false;
            Log.Add("O " + Hex(report, 16));
            Writes.Add((byte[])report.Clone());
            _pending = AnswerTo(report);
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            Log.Add("R " + timeoutMs);
            var answer = _pending;
            _pending = null;
            if (answer == null) return 0;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return Math.Min(answer.Length, buffer.Length);
        }

        public void Wait(int ms) => Log.Add("W " + ms);

        public void Dispose() { }
    }
    /// <summary>The 90-byte reports as OpenRGB builds them, the checksum the
    /// XOR of bytes 3 to 88 (RazerController.cpp:602-623 and 629-651).</summary>
    internal static class RazerReport90
    {
        public static string Of(byte transaction, byte cls, byte cmd, byte size, params byte[] arguments)
        {
            var b = new byte[91];
            b[2] = transaction;
            b[6] = size;
            b[7] = cls;
            b[8] = cmd;
            Array.Copy(arguments, 0, b, 9, arguments.Length);
            byte crc = 0;
            for (int i = 3; i < 89; i++) crc ^= b[i];
            b[89] = crc;
            return Hex(b);
        }

        public static byte[] Reply(byte[] request, params (int At, byte Value)[] arguments)
        {
            var reply = (byte[])request.Clone();
            reply[1] = 0x02;
            foreach (var (at, value) in arguments) reply[9 + at] = value;
            return reply;
        }
    }
    /// <summary>The registrations of the Razer devices OpenRGB lights
    /// through controllers of their own (#557), read from
    /// RazerControllerDetect.cpp at b9d903b5 into RazerHidTable.</summary>
    public class RazerHidRegistrationTests
    {
        /// <summary>The families, by OpenRGB's detector function.</summary>
        private static IHidLightingFamily FamilyOf(string detector) => detector switch
        {
            "DetectRazerKrakenControllers" => RazerKrakenLighting.Instance,
            "DetectRazerKrakenV3Controllers" => RazerKrakenV3Lighting.Instance,
            "DetectRazerKrakenV4Controllers" => RazerKrakenV4Lighting.Instance,
            "DetectRazerHanboControllers" => RazerHanboLighting.Instance,
            "DetectRazerARGBControllers" => RazerArgbLighting.Instance,
            "DetectRazerControllers" => RazerChargingPadLighting.Instance,
            _ => null,
        };

        /// <summary>RazerControllerDetect.cpp:430-445, 465, 466 and 483:
        /// seventeen lines, Kraken Kitty Edition and Kitty Black Edition
        /// (434-435) left to the Razer worker, whose detector is
        /// DetectRazerControllers.</summary>
        [Fact]
        public void TheTable_HoldsTheSeventeenRegistrations()
        {
            var lines = RazerHidTable.Registrations.Select(r => r.Source).ToArray();
            Assert.Equal(new[]
            {
                "RazerControllerDetect.cpp:430", "RazerControllerDetect.cpp:431", "RazerControllerDetect.cpp:432",
                "RazerControllerDetect.cpp:433", "RazerControllerDetect.cpp:436", "RazerControllerDetect.cpp:437",
                "RazerControllerDetect.cpp:438", "RazerControllerDetect.cpp:439", "RazerControllerDetect.cpp:440",
                "RazerControllerDetect.cpp:441", "RazerControllerDetect.cpp:442", "RazerControllerDetect.cpp:443",
                "RazerControllerDetect.cpp:444", "RazerControllerDetect.cpp:445", "RazerControllerDetect.cpp:465",
                "RazerControllerDetect.cpp:466", "RazerControllerDetect.cpp:483",
            }, lines);
            Assert.Equal("b9d903b544a40486709c4ca097d0b234b617db9c", RazerHidTable.OpenRgbCommit);
            Assert.All(RazerHidTable.Registrations, r => Assert.Equal(0x1532, r.VendorId));
            // The registration generator leaves the folder out, so no other
            // family's table holds these.
            Assert.DoesNotContain(OpenRgbRegistrations.All, r => r.Family == "RazerController");
            // Interfaces and pages from the lines.
            var v4 = RazerHidTable.Registrations.Single(r => r.ProductId == 0x056B);
            Assert.Equal((5, 0xFF14, 0x01), (v4.Interface, v4.UsagePage, v4.Usage));
            var argb = RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F1F);
            Assert.Equal((0, -1, -1, "REGISTER_HID_DETECTOR_I"), (argb.Interface, argb.UsagePage, argb.Usage, argb.Macro));
            var hanbo = RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F35);
            Assert.Equal((0, 0xFF00, 0x01), (hanbo.Interface, hanbo.UsagePage, hanbo.Usage));
            var pad = RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F26);
            Assert.Equal((0, 0x000C, 0x01, "Razer Charging Pad Chroma"), (pad.Interface, pad.UsagePage, pad.Usage, pad.Name));
        }

        [Fact]
        public void EveryRegistration_AnswersWithAModelOfItsFamily()
        {
            foreach (var r in RazerHidTable.Registrations)
            {
                var family = FamilyOf(r.Detector);
                var collection = CollectionFor(r, output: 512, feature: 512, input: 64);
                var model = family.Match(collection);
                Assert.True(model != null, $"{r.Name} {r.ProductId:X4}");
                Assert.Same(family, model.Family);
                Assert.Equal(r.Name, model.Name);
                Assert.True(model.Map.Lights.Count > 0);
                Assert.Equal($"razer:1532:{r.ProductId:X4}", model.Map.Product);
                Assert.True(model.Begin != null || model.BeginWith != null);
                Assert.Same(model, family.Match(collection));
                Assert.Same(model, HidLightingFamilies.Match(collection));
                Assert.Contains(family, HidLightingFamilies.All);
                // No other family takes it, and another vendor's collection
                // with the same fields is not it.
                foreach (var other in HidLightingFamilies.All.Where(f => !ReferenceEquals(f, family)))
                    Assert.Null(other.Match(collection));
                Assert.Null(family.Match(collection with { VendorId = 0x1533 }));
                Assert.Null(family.Match(collection with { ProductId = 0x0001 }));
                // The Razer worker's table does not list it.
                Assert.Null(RazerDirectTable.Find((ushort)r.ProductId));
            }
        }

        /// <summary>A collection on another interface or page than the line
        /// names is not the device's, and one whose reports are too short for
        /// the family's frames is not lit.</summary>
        [Fact]
        public void AnotherInterfacePageOrShortReport_IsNotTheModel()
        {
            var kraken = CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0510), output: 37);
            Assert.NotNull(RazerKrakenLighting.Instance.Match(kraken));
            Assert.Null(RazerKrakenLighting.Instance.Match(kraken with { OutputReportLength = 36 }));
            Assert.Null(RazerKrakenLighting.Instance.Match(kraken with { UsagePage = 0x0001 }));
            Assert.Null(RazerKrakenLighting.Instance.Match(kraken with { InterfaceNumber = 2, Path = kraken.Path.Replace("mi_03", "mi_02") }));

            var v3 = CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0549), output: 15);
            Assert.NotNull(RazerKrakenV3Lighting.Instance.Match(v3));
            Assert.Null(RazerKrakenV3Lighting.Instance.Match(v3 with { OutputReportLength = 14 }));

            var v4 = CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x056B), output: 64);
            Assert.NotNull(RazerKrakenV4Lighting.Instance.Match(v4));
            Assert.Null(RazerKrakenV4Lighting.Instance.Match(v4 with { OutputReportLength = 63 }));
            Assert.Null(RazerKrakenV4Lighting.Instance.Match(v4 with { Usage = 0x02 }));

            var pad = CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F26), feature: 91);
            Assert.NotNull(RazerChargingPadLighting.Instance.Match(pad));
            Assert.Null(RazerChargingPadLighting.Instance.Match(pad with { FeatureReportLength = 90 }));

            var argb = CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F1F), feature: 91);
            Assert.NotNull(RazerArgbLighting.Instance.Match(argb));
            Assert.Null(RazerArgbLighting.Instance.Match(argb with { FeatureReportLength = 90 }));

            var hanbo = CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F35), output: 65);
            Assert.NotNull(RazerHanboLighting.Instance.Match(hanbo));
            Assert.Null(RazerHanboLighting.Instance.Match(hanbo with { UsagePage = 0xFF01 }));
        }

        /// <summary>The razer_device entries the generator read, against
        /// RazerDevices.cpp at b9d903b5.</summary>
        [Fact]
        public void TheDeviceEntries_AreOpenRgbs()
        {
            var v4 = RazerHidTable.Device(0x056B);
            Assert.Equal(("Razer Kraken V4 (Wired)", 0x60, 1, 9), (v4.Name, (int)v4.TransactionId, v4.Rows, v4.Columns));
            var kitty = RazerHidTable.Device(0x0587);
            Assert.Equal(10, kitty.Columns);
            Assert.Equal(new[] { ("Headset", 9), ("Ears", 1) }, kitty.Zones.Select(z => (z.Name, z.Count)));
            var argb = RazerHidTable.Device(0x0F1F);
            Assert.Equal((0x3F, 0x05, 6, 80), ((int)argb.TransactionId, (int)argb.LedId, argb.Rows, argb.Columns));
            var hanbo = RazerHidTable.Device(0x0F35);
            Assert.Equal(new[] { 16, 18, 18, 18 }, hanbo.Zones.Select(z => z.Count));
            var pad = RazerHidTable.Device(0x0F26);
            Assert.Equal((0x1F, 1, 10), ((int)pad.TransactionId, pad.Rows, pad.Columns));
            var kittyV2 = RazerHidTable.Device(0x0554);
            Assert.Equal(new[] { "Left Cat Ear", "Right Cat Ear", "Left Logo", "Right Logo" }, kittyV2.Zones.Select(z => z.Name));
            // The Kraken 7.1 has no entry (RazerDevices.cpp).
            Assert.Null(RazerHidTable.Device(0x0501));
            Assert.Null(RazerHidTable.Device(0x0506));
            Assert.Equal(15, RazerHidTable.Devices.Length);
        }

        /// <summary>Each map against OpenRGB's zones, in its LED order.</summary>
        [Fact]
        public void TheLightMaps_FollowOpenRgbsZones()
        {
            static LightMap MapOf(ushort pid) => HidLightingFamilies.Match(
                CollectionFor(RazerHidTable.Registrations.First(r => r.ProductId == pid), output: 512, feature: 512, input: 64)).Map;

            // The Kraken controller sends one color.
            foreach (ushort pid in new ushort[] { 0x0501, 0x0506, 0x0504, 0x0510, 0x0560, 0x0527 })
            {
                var light = Assert.Single(MapOf(pid).Lights);
                Assert.Equal((0, LightKind.Zone, 1), (light.Address, light.Kind, light.Zone));
            }
            Assert.Single(MapOf(0x0549).Lights);
            var kittyV2 = MapOf(0x0554).Lights;
            Assert.Equal(new[] { LightKind.Zone, LightKind.Zone, LightKind.Logo, LightKind.Logo }, kittyV2.Select(l => l.Kind));
            Assert.Equal(new[] { 0, 1, 2, 3 }, kittyV2.Select(l => l.Address));
            var v4 = MapOf(0x056C).Lights;
            Assert.Equal(9, v4.Count);
            Assert.All(v4, l => Assert.Equal((LightKind.Zone, 1), (l.Kind, l.Zone)));
            var kittyV3 = MapOf(0x0588).Lights;
            Assert.Equal(10, kittyV3.Count);
            Assert.Equal(2, kittyV3[9].Zone);
            var hanbo = MapOf(0x0F35).Lights;
            Assert.Equal(70, hanbo.Count);
            Assert.Equal(new[] { 16, 18, 18, 18 }, hanbo.GroupBy(l => l.Zone).Select(g => g.Count()));
            var argb = MapOf(0x0F1F).Lights;
            Assert.Equal(480, argb.Count);
            Assert.All(argb, l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(Enumerable.Range(1, 6), argb.GroupBy(l => l.Zone).Select(g => g.Key));
            var pad = MapOf(0x0F26).Lights;
            Assert.Equal(10, pad.Count);
            Assert.All(pad, l => Assert.Equal(LightKind.Edge, l.Kind));
        }

        /// <summary>The devices are Razer's: each model names Synapse, so the
        /// worker sends nothing while it runs, and the linker closes their path
        /// while Synapse runs by the Razer vendor ID
        /// (PeripheralLinker.VendorProgramRuns), for a Razer row that reaches
        /// the unit and for the unit's own row.</summary>
        [Fact]
        public void WhileSynapseRuns_TheLinkerLeavesEveryUnitToIt()
        {
            var a = new Guid("00000000-0000-0000-0000-0000000000a1");
            var box = new Guid("11111111-2222-3333-4444-555555555555");
            var running = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { "RazerAppEngine.exe" });
            Assert.True(running.RazerSoftwareRunning);
            foreach (var r in RazerHidTable.Registrations)
            {
                var model = HidLightingFamilies.Match(CollectionFor(r, output: 512, feature: 512, input: 64));
                Assert.Equal(VendorPrograms.Razer, model.Programs);
                // As HidLightingBackend.Publish lists the unit.
                var unit = new DirectUnit(OutputFamily.HidLightingUnit, "u" + r.Source, box, (ushort)r.VendorId, (ushort)r.ProductId)
                {
                    Name = model.Name, Programs = model.Programs,
                };
                var path = new OutputPath(OutputFamily.HidLightingUnit, unit.Key);
                Assert.True(PeripheralLinker.UnitProgramRuns(unit, running));
                Assert.False(PeripheralLinker.UnitProgramRuns(unit, PeripheralPresence.None));

                var row = new LinkRow(a, InputDeviceType.Mouse, (ushort)r.VendorId, (ushort)r.ProductId, box, null);
                var free = PeripheralLinker.Build(new[] { row }, new LinkInputs(HidppSnapshot.Empty, PeripheralPresence.None) { Units = new[] { unit } }).For(a);
                Assert.Equal(new[] { path }, free.Lighting);
                var held = PeripheralLinker.Build(new[] { row }, new LinkInputs(HidppSnapshot.Empty, running) { Units = new[] { unit } }).For(a);
                // No path at all: the row's links are none, or none to the unit.
                Assert.True(held == null || !held.Lighting.Contains(path));

                // With no input row, a row of its own while Synapse stops.
                var none = Array.Empty<LinkRow>();
                Assert.Equal(new[] { unit }, PeripheralLinker.Orphans(none, new LinkInputs(HidppSnapshot.Empty, PeripheralPresence.None) { Units = new[] { unit } }));
                Assert.Empty(PeripheralLinker.Orphans(none, new LinkInputs(HidppSnapshot.Empty, running) { Units = new[] { unit } }));
            }
        }
    }
}
