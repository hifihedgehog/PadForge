using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// OpenRGB's CorsairPeripheralV2Controller family (#557). Every expected
    /// byte is written out from OpenRGB's CorsairPeripheralV2Controller.cpp,
    /// its two controllers and CorsairPeripheralV2Devices.cpp, never from
    /// the builder under test.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class CorsairPeripheralV2LightingTests : IDisposable
    {
        private static string Hex(byte[] bytes, int count) => CorsairPeripheralLightingTests.Hex(bytes, count);

        private static string Hex(byte[] bytes, int from, int count) => BitConverter.ToString(bytes, from, count).Replace('-', ' ');

        public CorsairPeripheralV2LightingTests() => KeyboardProfileBackups.ResetForTest(persistAtOnce: true);

        public void Dispose() => KeyboardProfileBackups.ResetForTest();

        private static CorsairPeripheralV2Lighting Family() => new();

        private static HidCollectionInfo Collection(ushort pid, ushort page = 0xFF42, int iface = 1)
            => CorsairPeripheralLightingTests.Collection(pid, page, iface);

        /// <summary>An answer as Windows reads it: report ID 0, then
        /// hidapi's bytes: the write command, the command, the error byte
        /// and a 32-bit value from byte 3 (GetAddress, lines 291-318).</summary>
        private static byte[] Answer(byte cmd, byte op, byte error = 0, uint value = 0)
        {
            var answer = new byte[65];
            answer[1] = cmd;
            answer[2] = op;
            answer[3] = error;
            answer[4] = (byte)value;
            answer[5] = (byte)(value >> 8);
            answer[6] = (byte)(value >> 16);
            answer[7] = (byte)(value >> 24);
            return answer;
        }

        /// <summary>A device that answers its product ID, its layout and a
        /// transaction's error byte, acknowledges the other writes, and
        /// answers no block write.</summary>
        private static FakeCorsairChannel Device(uint pid, uint layout = 1, byte startError = 0,
            Func<byte, uint?> pidByCmd = null, string path = null)
            => new()
            {
                Path = path,
                Answer = q =>
                {
                    byte cmd = q[1], op = q[2], prop = q[3];
                    if (op == 0x02 && prop == 0x12) return Answer(cmd, op, 0, pidByCmd?.Invoke(cmd) ?? pid);
                    if (op == 0x02 && prop == 0x11) return Answer(cmd, op, 0, 0x1B1C);
                    if (op == 0x02 && prop == 0x41) return Answer(cmd, op, 0, layout);
                    if (op == 0x0D) return Answer(cmd, op, startError);
                    if (op is 0x06 or 0x07) return null;
                    return Answer(cmd, op);
                },
            };

        // ─────────────────────────────────────────────
        //  The tables
        // ─────────────────────────────────────────────

        [Fact]
        public void TheDeviceList_IsOpenRgbs()
        {
            // corsair_v2_device_list_data (CorsairPeripheralV2Devices.cpp at b9d903b5).
            Assert.Equal(34, CorsairLights.V2Devices.Length);
            Assert.Equal("k55_rgb_pro_device", CorsairLights.V2Devices[0].Name);
            Assert.Equal("mm700_3xl_device", CorsairLights.V2Devices[^1].Name);
            var k100 = CorsairPeripheralV2Lighting.DeviceOf(0x1B7C);
            Assert.Equal(("corsair_k100_layout", 12, 24), (k100.Layout, k100.Rows, k100.Cols));
            // Each device's lighting register and packet size (light_ctrl_reg and pkt_size,
            // CorsairPeripheralV2Devices.h:66-67): the K65 Mini's own (lines 968-985) and
            // the K70 RGB PRO V2's 1025-byte reports (OpenRGB dbb97ccb), 5F and 65 elsewhere.
            // Every device of the list paints on handle 1 (SetLEDs, CorsairPeripheralV2Controller.cpp:390-398).
            var k65 = CorsairPeripheralV2Lighting.DeviceOf(0x1BAF);
            Assert.Equal(("k65_mini_device", "corsair_K65_mini_layout", 6, 14), (k65.Name, k65.Layout, k65.Rows, k65.Cols));
            Assert.Equal((0x6E, 1025, 1), (k65.LightCtrlReg, k65.PacketSize, k65.PictureHandle));
            var proV2 = CorsairPeripheralV2Lighting.DeviceOf(0x1BB3);
            Assert.Equal((0x5F, 1025, 1), (proV2.LightCtrlReg, proV2.PacketSize, proV2.PictureHandle));
            Assert.Equal((0x5F, 65, 1), (k100.LightCtrlReg, k100.PacketSize, k100.PictureHandle));
            Assert.All(CorsairLights.V2Devices.Where(d => d.ProductId is not (0x1BAF or 0x1BB3)),
                d => Assert.Equal((0x5F, 65, 1), (d.LightCtrlReg, d.PacketSize, d.PictureHandle)));
            // The devices only SignalRGB's plugin lists paint on handle 0, as the plugin
            // opens it (Corsair_Bragi_Device.js:1705-1706 and 2458) and ckb-next does (bragi_proto.h:70).
            Assert.All(CorsairPeripheralV2Lighting.PluginDevices.Value,
                d => Assert.Equal((0x5F, 65, 0), (d.LightCtrlReg, d.PacketSize, d.PictureHandle)));
            var dark = CorsairPeripheralV2Lighting.DeviceOf(0x1B4B);
            Assert.Equal(new[] { "Scroll Wheel", "Side Buttons", "Rear Left", "Logo", "Rear Right", "DPI & Indicator Zone" },
                dark.Zones.Select(z => z.Name));
            Assert.Equal(CorsairZoneType.Linear, dark.Zones[1].Type);
            Assert.Equal(4, dark.Zones[1].Cols);
            // The receivers are not in the list (lines 38-47).
            foreach (ushort receiver in new ushort[] { 0x1BA6, 0x1B66, 0x1B65, 0x1BDC, 0x2B00 })
            {
                Assert.Null(CorsairPeripheralV2Lighting.DeviceOf(receiver));
                Assert.True(CorsairPeripheralV2Lighting.IsReceiver(receiver));
            }
        }

        [Fact]
        public void EveryRegistration_HasItsModel_OnItsOwnCollection()
        {
            var family = Family();
            var registrations = OpenRgbRegistrations.Of(CorsairPeripheralV2Lighting.OpenRgbFamily).ToList();
            Assert.Equal(39, registrations.Count);
            foreach (var r in registrations)
            {
                var model = family.Match(Collection((ushort)r.ProductId));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Equal(new[] { VendorPrograms.CorsairIcue }, model.Programs);
                // CORSAIR_V2_UPDATE_PERIOD, a resend timed as a picture
                // (CorsairPeripheralV2Controller.h:34, RGBController_CorsairV2Software.cpp:205 and 225-239).
                Assert.Equal(30000, model.KeepAliveMs);
                Assert.Equal(30000, model.KeepAliveIdleMs);
                Assert.Equal(0, model.RecheckMs);
                Assert.Null(family.Match(Collection((ushort)r.ProductId, iface: 0)));
                Assert.Null(family.Match(Collection((ushort)r.ProductId, page: 0xFFC2)));
                // A device in OpenRGB's list has its lights before a session;
                // a receiver's are asked.
                if (CorsairPeripheralV2Lighting.DeviceOf((uint)r.ProductId) is CorsairV2Device device)
                    Assert.NotEmpty(model.Map.Lights);
                else
                    Assert.True(CorsairPeripheralV2Lighting.IsReceiver((uint)r.ProductId));
            }
            Assert.Null(family.Match(Collection(0x1B2D, page: 0xFFC2)));
        }

        [Fact]
        public void TheSharedTable_HoldsTheFamily()
        {
            Assert.Contains(CorsairPeripheralV2Lighting.Instance, HidLightingFamilies.All);
            Assert.True(HidLightingFamilies.All.ToList().IndexOf(CorsairPeripheralV2Lighting.Instance)
                == HidLightingFamilies.All.ToList().IndexOf(CorsairPeripheralLighting.Instance) + 1);
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        [Fact]
        public void TheK100_TakesItsLayoutsValues()
        {
            var lights = CorsairPeripheralV2Lighting.Lights(CorsairPeripheralV2Lighting.DeviceOf(0x1B7C),
                KeyboardLayout.AnsiQwerty, out int length);
            // The highest value, "Logo R" 192 (CorsairPeripheralV2Devices.cpp:319), plus one.
            Assert.Equal(193, length);
            Assert.Equal((ushort)0x29, lights.Single(l => l.Address == 41).Usage);
            Assert.Equal(new[] { 190, 191, 192 }, lights.Where(l => l.Kind == LightKind.Logo).Select(l => l.Address).OrderBy(a => a));
            Assert.Equal(44, lights.Count(l => l.Kind == LightKind.Edge));
            Assert.Equal(Enumerable.Range(131, 6), lights.Where(l => l.Label == LightLabel.GKey).Select(l => l.Address).OrderBy(a => a));
            Assert.Equal(LightKind.Other, lights.Single(l => l.Address == 137).Kind);   // "iCue"
            Assert.Equal(LightKind.Other, lights.Single(l => l.Address == 182).Kind);   // "Wheel 1"
            Assert.All(lights, l => Assert.True(l.HasPosition));
        }

        [Fact]
        public void AKeyboardsLayout_FollowsTheBoardItReports()
        {
            var device = CorsairPeripheralV2Lighting.DeviceOf(0x1BC4);
            var ansi = CorsairPeripheralV2Lighting.Lights(device, KeyboardLayout.AnsiQwerty, out _);
            var iso = CorsairPeripheralV2Lighting.Lights(device, KeyboardLayout.IsoQwerty, out _);
            // corsair_full_size_values: 100 is ISO \ and 50 is ISO # (lines 25-27).
            Assert.DoesNotContain(ansi, l => l.Usage == 0x64);
            Assert.Equal(100, iso.Single(l => l.Usage == 0x64).Address);
            Assert.Equal(KeyboardLayout.IsoQwerty, CorsairPeripheralV2Lighting.LayoutOf(2));
            Assert.Equal(KeyboardLayout.Jis, CorsairPeripheralV2Lighting.LayoutOf(4));
            Assert.Equal(KeyboardLayout.AnsiQwerty, CorsairPeripheralV2Lighting.LayoutOf(3));
            Assert.Equal(KeyboardLayout.AnsiQwerty, CorsairPeripheralV2Lighting.LayoutOf(CorsairPeripheralV2Lighting.NoValue));
            // The K70 RGB PRO's media and indicator keys (lines 175-183).
            Assert.Equal(LightLabel.Brightness, ansi.Single(l => l.Address == 113).Label);
            Assert.Equal(LightKind.Logo, ansi.Single(l => l.Address == 138).Kind);
            Assert.Equal(LightLabel.Mute, ansi.Single(l => l.Address == 102).Label);
            // The K57's indicators (lines 79-80) and the K95 XT's top bar.
            var k57 = CorsairPeripheralV2Lighting.Lights(CorsairPeripheralV2Lighting.DeviceOf(0x1B6E), KeyboardLayout.AnsiQwerty, out _);
            Assert.Equal(new[] { 0, 1 }, k57.Where(l => l.Kind == LightKind.Status).Select(l => l.Address).OrderBy(a => a));
            var xt = CorsairPeripheralV2Lighting.Lights(CorsairPeripheralV2Lighting.DeviceOf(0x1B89), KeyboardLayout.AnsiQwerty, out _);
            Assert.Equal(19, xt.Count(l => l.Kind == LightKind.Edge));
        }

        [Fact]
        public void AMouse_NumbersItsLightsAfterEachOther()
        {
            var lights = CorsairPeripheralV2Lighting.Lights(CorsairPeripheralV2Lighting.DeviceOf(0x1B4B), KeyboardLayout.AnsiQwerty,
                out int length);
            // led.value = leds.size() for each light (lines 173-184).
            Assert.Equal(12, length);
            Assert.Equal(Enumerable.Range(0, 12), lights.Select(l => l.Address));
            Assert.Equal(new[]
            {
                LightKind.Zone, LightKind.Zone, LightKind.Zone, LightKind.Zone, LightKind.Zone, LightKind.Zone,
                LightKind.Logo, LightKind.Zone, LightKind.Status, LightKind.Status, LightKind.Status, LightKind.Status,
            }, lights.Select(l => l.Kind));
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 0, 7, 0, 0, 0, 0 }, lights.Select(l => l.Zone));
            var mat = CorsairPeripheralV2Lighting.Lights(CorsairPeripheralV2Lighting.DeviceOf(0x1B9B), KeyboardLayout.AnsiQwerty, out _);
            Assert.Equal(new[] { LightKind.Edge, LightKind.Edge, LightKind.Logo }, mat.Select(l => l.Kind));
            var k55 = CorsairPeripheralV2Lighting.Lights(CorsairPeripheralV2Lighting.DeviceOf(0x1BA4), KeyboardLayout.AnsiQwerty, out _);
            Assert.Equal(Enumerable.Range(1, 6), k55.Select(l => l.Zone));
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        [Fact]
        public void APicturesData_IsBlocksOrTriplets()
        {
            var lights = new[]
            {
                new LightInfo(1, LightKind.Zone, 0, LightKeySource.None),
                new LightInfo(0, LightKind.Zone, 0, LightKeySource.None),
                new LightInfo(9, LightKind.Zone, 0, LightKeySource.None),
            };
            var colors = new[] { 0x112233, 0x445566, 0x778899 };
            // SetLedsDirect: reds, greens, blues (CorsairPeripheralV2SoftwareController.cpp:26-47).
            Assert.Equal("44 11 55 22 66 33", Hex(CorsairPeripheralV2Lighting.PictureData(false, 2, lights, colors), 6));
            // SetLedsDirectTriplets: 12 00, then each address (CorsairPeripheralV2HardwareController.cpp:66-88).
            Assert.Equal("12 00 44 55 66 11 22 33", Hex(CorsairPeripheralV2Lighting.PictureData(true, 2, lights, colors), 8));
            Assert.Equal(8, CorsairPeripheralV2Lighting.PictureData(true, 2, lights, colors).Length);
        }

        [Fact]
        public void TheBlockWrite_Is57BytesThen61()
        {
            var data = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
            var frames = CorsairPeripheralV2Lighting.DataFrames(0x08, 1, 65, data);
            // 57 + 61 + 61 + 21 on handle 1, whose number stays in byte 3 of every
            // report (SetLEDs, CorsairPeripheralV2Controller.cpp:392-457 at b9d903b5).
            Assert.Equal(4, frames.Count);
            Assert.Equal("00 08 06 01 C8 00 00 00 00 01", Hex(frames[0], 10));
            Assert.Equal(56, frames[0][64]);
            Assert.Equal("00 08 07 01 39 3A", Hex(frames[1], 6));
            Assert.Equal("00 08 07 01 B3", Hex(frames[3], 5));
            Assert.Equal(199, frames[3][4 + 20]);
            Assert.All(frames[3].Skip(25), b => Assert.Equal(0, b));
            Assert.All(frames, f => Assert.Equal(65, f.Length));
            Assert.Single(CorsairPeripheralV2Lighting.DataFrames(0x09, 1, 65, new byte[57]));
            // Handle 0, as the plugin's devices take it.
            var plugin = CorsairPeripheralV2Lighting.DataFrames(0x08, 0, 65, data);
            Assert.Equal("00 08 06 00 C8 00", Hex(plugin[0], 6));
            Assert.Equal("00 08 07 00 39 3A", Hex(plugin[1], 6));
            // A 1025-byte device takes 1017 bytes in its first report and 1021 in each
            // after (pkt_size - offset1 and pkt_size - offset2).
            var big = CorsairPeripheralV2Lighting.DataFrames(0x08, 1, 1025, new byte[2000]);
            Assert.Equal(2, big.Count);
            Assert.All(big, f => Assert.Equal(1025, f.Length));
            Assert.Equal("00 08 06 01 D0 07", Hex(big[0], 6));
            Assert.Equal("00 08 07 01", Hex(big[1], 4));
        }

        // ─────────────────────────────────────────────
        //  Sessions
        // ─────────────────────────────────────────────

        [Fact]
        public void AWiredKeyboard_OpensAsOpenRgbsController()
        {
            var model = Family().Match(Collection(0x1BC4));
            var io = Device(0x1BC4, layout: 2);
            var session = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(io, io.Wait));
            // The product ID is the collection's and no ID is asked of a wired device
            // (CorsairPeripheralV2Controller.cpp:20-54 at b9d903b5).
            Assert.Equal(new[]
            {
                "out 00 08 0D 00 22 00", "in 50",      // resource 22 opened
                "out 00 08 05 01 00 00", "in 50",      // and closed
                "out 00 08 01 03 00 02", "in 50",      // software render mode
                "out 00 08 02 5F 00 00", "in 50",      // LightingControl(0x5F)
                "out 00 08 02 41 00 00", "in 50",      // the board layout
            }, io.Log);
            Assert.True(session.Triplets);
            Assert.Equal(CorsairPeripheralV2Lighting.LightCtrl2, session.LightCtrl);
            Assert.Contains(session.Map.Lights, l => l.Usage == 0x64);
            Assert.Equal("corsair:1B1C:1BC4", session.Map.Product);
            Assert.Equal("00 08 01 03 00 01", Hex(Assert.Single(session.CrashFrames), 6));
        }

        [Fact]
        public void ARefusedResource22_FallsBackToResource1()
        {
            var model = Family().Match(Collection(0x1BC4));
            var io = Device(0x1BC4, startError: 5);
            var session = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(io, io.Wait));
            Assert.Equal(new[] { "out 00 08 0D 00 22 00", "out 00 08 0D 00 01 00", "out 00 08 05 01 00 00" },
                io.Log.Where(l => l.StartsWith("out 00 08 0D") || l.StartsWith("out 00 08 05")));
            Assert.Equal(CorsairPeripheralV2Lighting.LightCtrl1, session.LightCtrl);
            // The hardware controller sends color blocks on resource 1
            // (CorsairPeripheralV2HardwareController.cpp:28-35).
            Assert.False(session.Triplets);
            // The software controller sends blocks on resource 22 too
            // (CorsairPeripheralV2SoftwareController.cpp:26-47).
            var m55 = Family().Match(Collection(0x1B70));
            var sw = Device(0x1B70);
            var swSession = Assert.IsType<CorsairPeripheralV2Lighting.Session>(m55.Begin(sw, sw.Wait));
            Assert.Equal(CorsairPeripheralV2Lighting.LightCtrl2, swSession.LightCtrl);
            Assert.False(swSession.Triplets);
        }

        [Fact]
        public void AnUnansweredTransaction_FallsBackToo()
        {
            // StartTransaction returns buffer[2], the 0D it wrote, when no
            // answer came (lines 320-339).
            var model = Family().Match(Collection(0x1BC4));
            var io = Device(0x1BC4);
            var answer = io.Answer;
            io.Answer = q => q[2] == 0x0D ? null : answer(q);
            var session = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(io, io.Wait));
            Assert.Equal(CorsairPeripheralV2Lighting.LightCtrl1, session.LightCtrl);
        }

        [Fact]
        public void TheK100_StaysOnResource22()
        {
            var model = Family().Match(Collection(0x1B7C));
            var io = Device(0x1B7C, startError: 5);
            var session = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(io, io.Wait));
            Assert.DoesNotContain(io.Log, l => l.StartsWith("out 00 08 0D") || l.StartsWith("out 00 08 05"));
            Assert.True(session.Triplets);
        }

        [Fact]
        public void TheK57_WritesWith80_AndReadsNoAnswers()
        {
            var model = Family().Match(Collection(0x1B6E));
            var io = Device(0x1B6E);
            var session = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(io, io.Wait));
            // Known by the collection's product ID (lines 49-53), so nothing is asked first.
            Assert.Equal(new[]
            {
                "out 00 80 01 03 00 02",
                "out 00 80 02 5F 00 00",
                "out 00 80 02 41 00 00", "in 50",      // GetAddress always reads
            }, io.Log);
            Assert.True(session.SkipReads);
            Assert.False(session.Triplets);
            io.Log.Clear();
            session.Paint(io, io.Wait, session.Map.Lights, new int[session.Map.Lights.Count]);
            Assert.DoesNotContain(io.Log, l => l.StartsWith("in"));
            io.Log.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(new[] { "out 00 80 01 03 00 01" }, io.Log);
        }

        [Fact]
        public void AReceiver_AsksTheDeviceBehindItWith09()
        {
            var model = Family().Match(Collection(0x1BA6));
            Assert.Empty(model.Map.Lights);
            var io = Device(0x1BA6, pidByCmd: cmd => cmd == 0x09 ? 0x1BB5 : null);
            var session = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(io, io.Wait));
            // The collection's ID names a receiver, so the device behind it is asked with
            // 09, and no vendor ID follows (lines 38-47 at b9d903b5).
            Assert.Equal(new[] { "out 00 09 02 12 00 00", "in 50", "out 00 09 0D 00 22 00" }, io.Log.Take(3));
            Assert.Equal(CorsairPeripheralV2Lighting.WriteWireless, session.WriteCmd);
            Assert.Equal("m65_ultra_rgb_device", session.Device.Name);
            Assert.Equal(2, session.Map.Lights.Count);
            Assert.Equal("00 09 01 03 00 01", Hex(Assert.Single(session.CrashFrames), 6));
        }

        /// <summary>A row of the plugins' list can be a dongle too: the wireless command
        /// is asked first, and a device it does not name leaves the wired one.</summary>
        [Fact]
        public void ARowOfThePluginsList_IsAskedAsADongle_ThenAsItself()
        {
            var model = Family().Match(Collection(0x2B0A));
            var dongle = Device(0x2B0A, pidByCmd: cmd => cmd == 0x09 ? 0x2B02u : 0x2B0Au);
            var behind = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(dongle, dongle.Wait));
            Assert.Equal(CorsairPeripheralV2Lighting.WriteWireless, behind.WriteCmd);
            Assert.Equal(0x2B02, behind.Device.ProductId);

            var wired = Device(0x2B0A, pidByCmd: cmd => cmd == 0x09 ? 0xFFFFu : 0x2B0Au);
            var itself = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(wired, wired.Wait));
            Assert.Equal(CorsairPeripheralV2Lighting.WriteWired, itself.WriteCmd);
            Assert.Equal(0x2B0A, itself.Device.ProductId);
        }

        [Fact]
        public void AProductIdThatIsSlowToCome_IsAskedAgain120MsApart()
        {
            // A receiver's device that has not woken answers zero (GetAddressRetry, lines 263-289).
            int asked = 0;
            var model = Family().Match(Collection(0x1BA6));
            var io = Device(0x1BA6, pidByCmd: _ => ++asked < 3 ? 0u : 0x1BB5u);
            Assert.NotNull(model.Begin(io, io.Wait));
            Assert.Equal(new[] { "out 00 09 02 12 00 00", "in 50", "wait 120", "out 00 09 02 12 00 00", "in 50", "wait 120",
                "out 00 09 02 12 00 00", "in 50", "out 00 09 0D 00 22 00" }, io.Log.Take(9));
            // An error answer is no value either (lines 310-316), and eight of them end the open.
            var refusing = new FakeCorsairChannel { Answer = q => Answer(q[1], q[2], error: 3, value: 0x1BB5) };
            Assert.Null(model.Begin(refusing, refusing.Wait));
            Assert.Equal(8, refusing.Outputs.Count);
        }

        [Fact]
        public void ADeviceNotInTheList_GetsNothingMore()
        {
            // The device behind a receiver that the list lacks is dropped (lines 92-97).
            var model = Family().Match(Collection(0x1BA6));
            var io = Device(0x1BA6, pidByCmd: _ => 0x1234u);
            Assert.Null(model.Begin(io, io.Wait));
            Assert.Equal(new[] { "out 00 09 02 12 00 00", "in 50" }, io.Log);
        }

        /// <summary>The K60 PRO's one-color model: its own resource (ckb-next device_bragi.c:83-86),
        /// one channel (led_bragi.c:92-99), and the color made one brightness first (ckb-next
        /// kblight.cpp:296-307).</summary>
        [Fact]
        public void TheK60ProMono_OpensItsResource_AndSendsOneChannel()
        {
            var model = Family().Match(Collection(0x1B83));
            var io = Device(0x1B83);
            var session = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(io, io.Wait));
            Assert.Equal(CorsairPeripheralV2Lighting.LightCtrlMonochrome, session.LightCtrl);
            io.Outputs.Clear();
            var lights = session.Map.Lights;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, lights.Select(_ => 0x0000FF).ToList()));
            var block = io.Outputs.First(f => f[2] == 0x06);
            Assert.Equal(session.BufferLength, block[4] | block[5] << 8 | block[6] << 16 | block[7] << 24);
            Assert.Equal(0x9C, block[8 + lights[0].Address]);
            // A device of the plugin's list paints on handle 0, the transaction opened on
            // it, as the plugin and ckb-next do.
            Assert.Equal(0, block[3]);
            Assert.Equal(0, io.Outputs.First(f => f[2] == 0x0D)[3]);
        }

        [Fact]
        public void APicture_IsOneTransaction()
        {
            var model = Family().Match(Collection(0x1BF0));
            var io = Device(0x1BF0);
            var session = model.Begin(io, io.Wait);
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, new[] { 0x112233, 0x445566 }));
            // StartTransaction(1), the block write on handle 1, its answer or a 2 ms
            // wait, StopTransaction(0), with no ClearPacketBuffer first (SetLEDs,
            // CorsairPeripheralV2Controller.cpp:377-460 at b9d903b5).
            Assert.Equal(new[]
            {
                "out 00 08 0D 01 22 00", "in 50",
                "out 00 08 06 01 08 00", "in 3", "wait 2",
                "out 00 08 05 01 00 00", "in 50",
            }, io.Log);
            var block = io.Outputs.Single(f => f[2] == 0x06);
            Assert.Equal("00 08 06 01 08 00 00 00 12 00 11 22 33 44 55 66 00", Hex(block, 17));
            Assert.Equal("00 08 01 03 00 01", Hex(KeyboardProfileBackups.Get("corsair:1B1C:1BF0").Value.Contents, 6));
        }

        /// <summary>The K65 Mini is a device of the list now (OpenRGB 4f2d8920): no
        /// resource probe, its own lighting register 6E, and 1025-byte reports, so a
        /// picture fits in one, as its own controller had it (08 06 01 73 01 00 00 12 00).
        /// SignalRGB's plugin names three more IDs and drives them the same way
        /// (Corsair_K65_Mini_Keyboard.js:3, 65-81 and 95-146).</summary>
        [Theory]
        [InlineData(0x1BAF)]
        [InlineData(0x1BCF)]
        [InlineData(0x1BC3)]
        [InlineData(0x1BBD)]
        public void TheK65Mini_OpensAsADeviceOfTheList_AndPaintsInOneReport(int product)
        {
            var model = Family().Match(Collection((ushort)product));
            Assert.Equal("Corsair K65 Mini", model.Name);
            Assert.Null(Family().Match(Collection((ushort)product, page: 0xFF00)));
            Assert.Null(new CorsairPeripheralLighting().Match(Collection((ushort)product, page: 0xFF42)));
            var io = Device((uint)product);
            var session = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(io, io.Wait));
            Assert.Equal(new[]
            {
                "out 00 08 01 03 00 02", "in 50",      // software render mode
                "out 00 08 02 6E 00 00", "in 50",      // LightingControl(light_ctrl_reg)
                "out 00 08 02 41 00 00", "in 50",      // the board layout
            }, io.Log);
            Assert.All(io.Outputs, f => Assert.Equal(1025, f.Length));
            Assert.True(session.Triplets);
            Assert.Equal(123, session.BufferLength);
            // The hand-back record fits a backup (KeyboardProfileBackups.MaxContents), so the
            // picture is not held back waiting for it. The channel pads it on the way out.
            Assert.Equal("00 08 01 03 00 01", Hex(Assert.Single(session.CrashFrames), 6));
            Assert.True(Assert.Single(session.CrashFrames).Length <= KeyboardProfileBackups.MaxContents);
            Assert.Equal(new[] { 0, 1, 44 }, session.Map.Lights.Where(l => l.Usage == 0x2C).Select(l => l.Address).OrderBy(a => a));
            io.Log.Clear();
            io.Outputs.Clear();
            var lights = session.Map.Lights;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights,
                lights.Select(l => l.Address == 41 ? 0x112233 : 0).ToList()));
            Assert.Equal(new[] { "out 00 08 0D 01 22 00", "in 50", "out 00 08 06 01 73 01", "in 3", "wait 2",
                "out 00 08 05 01 00 00", "in 50" }, io.Log);
            var block = io.Outputs.Single(f => f[2] == 0x06);
            Assert.Equal("00 08 06 01 73 01 00 00 12 00", Hex(block, 10));
            Assert.Equal("11 22 33", Hex(block, 10 + 41 * 3, 3));
            Assert.Equal(1025, block.Length);
        }

        /// <summary>The K65 Mini's other IDs are lit as OpenRGB's K65 Mini and are no rows
        /// of the plugins' list, where a second table stood for the same keyboard.</summary>
        [Fact]
        public void AnIdOfADeviceOfTheList_IsNoRowOfThePluginsList()
        {
            Assert.Equal(new ushort[] { 0x1BBD, 0x1BC3, 0x1BCF }, CorsairPeripheralV2Lighting.OtherIds.Keys.OrderBy(k => k));
            foreach (ushort other in CorsairPeripheralV2Lighting.OtherIds.Keys)
            {
                // The plugins' tables do list it, and the family's list leaves it out.
                Assert.Contains(PluginCorsairTables.Devices, d => d.Product == other);
                Assert.DoesNotContain(CorsairPeripheralV2Lighting.PluginDevices.Value, d => d.ProductId == other);
                Assert.Null(CorsairPeripheralV2Lighting.DeviceOf(other));
                Assert.False(CorsairPeripheralV2Lighting.MayBeReceiver(other));
                Assert.Null(CorsairPeripheralV2Lighting.PluginRegistration(Collection(other)));
                Assert.Equal("Corsair K65 Mini", Family().Match(Collection(other)).Name);
            }
            // The rows that are no other device's are all still there.
            Assert.Equal(PluginCorsairTables.Devices.Count(d => !CorsairPeripheralV2Lighting.IsReceiver(d.Product)
                    && !CorsairPeripheralV2Lighting.OtherIds.ContainsKey((ushort)d.Product)
                    && CorsairLights.V2Devices.All(v => v.ProductId != d.Product)) + 1,
                CorsairPeripheralV2Lighting.PluginDevices.Value.Length);
        }

        /// <summary>A device only the plugins list writes at its collection's write length
        /// where that is longer than 65, as the plugin does (Corsair_Bragi_Device.js at
        /// 95031635, lines 1862-1864 and 2446-2474): its picture goes out in 1025-byte reports
        /// with 1017 bytes of it in the first, and in 65-byte reports on a collection of 65,
        /// 57 bytes and then 61. A device of OpenRGB's list keeps its table's packet size.</summary>
        [Fact]
        public void ARowOfThePluginsList_WritesAtItsCollectionsLength()
        {
            var row = CorsairPeripheralV2Lighting.PluginDevices.Value
                .First(d => d.Type == CorsairDeviceType.Keyboard && d.OwnLights != null && d.OwnLights.Count > 60);
            var model = Family().Match(Collection(row.ProductId, iface: row.OwnInterface));
            Assert.NotNull(model);
            byte[] Painted(int outputLength, out List<byte[]> reports)
            {
                var answers = Device(row.ProductId, pidByCmd: cmd => cmd == 0x09 ? 0xFFFFu : row.ProductId);
                var io = new FakeCorsairChannel { OutputReportLength = outputLength, InputReportLength = outputLength, Answer = answers.Answer };
                var session = Assert.IsType<CorsairPeripheralV2Lighting.Session>(model.Begin(io, io.Wait));
                Assert.Equal(0, session.Device.PictureHandle);
                io.Outputs.Clear();
                var lights = session.Map.Lights;
                var colors = lights.Select((_, n) => 0x100000 + n * 0x000301).ToList();
                Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, colors));
                reports = io.Outputs.Where(f => f[2] is 0x06 or 0x07).ToList();
                return CorsairPeripheralV2Lighting.PictureData(session.Triplets, session.BufferLength, lights, colors);
            }

            byte[] data = Painted(1025, out var longReports);
            Assert.True(data.Length > 57 && data.Length <= 1017, $"the picture is {data.Length} bytes");
            var only = Assert.Single(longReports);
            Assert.Equal(1025, only.Length);
            Assert.Equal(0x06, only[2]);
            Assert.Equal(data.Length, only[4] | only[5] << 8);
            Assert.Equal(data, only.Skip(8).Take(data.Length));
            Assert.All(only.Skip(8 + data.Length), b => Assert.Equal(0, b));

            byte[] same = Painted(65, out var shortReports);
            Assert.Equal(data, same);
            Assert.All(shortReports, f => Assert.Equal(65, f.Length));
            Assert.Equal(1 + (data.Length - 57 + 60) / 61, shortReports.Count);
            Assert.Equal(data, shortReports[0].Skip(8).Concat(shortReports.Skip(1).SelectMany(f => f.Skip(4))).Take(data.Length));

            // A device of OpenRGB's list on a collection that takes longer reports
            // still writes its table's 65, and the channel pads it.
            var k100 = Family().Match(Collection(0x1B7C));
            var wide = new FakeCorsairChannel { OutputReportLength = 1025, InputReportLength = 1025, Answer = Device(0x1B7C).Answer };
            var listed = Assert.IsType<CorsairPeripheralV2Lighting.Session>(k100.Begin(wide, wide.Wait));
            Assert.Equal(1, listed.Device.PictureHandle);
            wide.Outputs.Clear();
            listed.Paint(wide, wide.Wait, listed.Map.Lights, new int[listed.Map.Lights.Count]);
            Assert.All(wide.Outputs, f => Assert.Equal(65, f.Length));
            Assert.Contains(wide.Outputs, f => f[2] == 0x07);
        }

        /// <summary>The K70 RGB PRO V2 takes 1025-byte reports (OpenRGB dbb97ccb), so
        /// its whole picture goes in one.</summary>
        [Fact]
        public void TheK70RgbProV2_PaintsIn1025ByteReports()
        {
            var model = Family().Match(Collection(0x1BB3));
            var io = Device(0x1BB3);
            var session = model.Begin(io, io.Wait);
            io.Log.Clear();
            session.Paint(io, io.Wait, session.Map.Lights, new int[session.Map.Lights.Count]);
            Assert.DoesNotContain(io.Log, l => l.StartsWith("out 00 08 07"));
            Assert.Equal(1025, io.Outputs.Single(f => f[2] == 0x06).Length);
        }

        [Fact]
        public void ALongPicture_PacesEachContinuation()
        {
            var model = Family().Match(Collection(0x1B7C));
            var io = Device(0x1B7C);
            var session = model.Begin(io, io.Wait);
            io.Log.Clear();
            session.Paint(io, io.Wait, session.Map.Lights, new int[session.Map.Lights.Count]);
            // 2 + 193 * 3 = 581 bytes: 57, then nine of 61.
            Assert.Equal(9, io.Log.Count(l => l.StartsWith("out 00 08 07 01")));
            Assert.Equal(10, io.Log.Count(l => l == "wait 2"));
            Assert.Equal("00 08 06 01 45 02", Hex(io.Outputs.Single(f => f[2] == 0x06), 6));
        }

        [Fact]
        public void TheKeepAlive_SendsTheLastPictureAfter30Seconds()
        {
            var model = Family().Match(Collection(0x1BF0));
            var io = Device(0x1BF0);
            var session = model.Begin(io, io.Wait);
            session.Paint(io, io.Wait, session.Map.Lights, new[] { 0x112233, 0x445566 });
            int sent = io.Outputs.Count(f => f[2] == 0x06);
            Assert.True(session.KeepAlive(io, io.Wait, session.Map.Lights, new[] { 0x112233, 0x445566 }));
            Assert.Equal(sent + 1, io.Outputs.Count(f => f[2] == 0x06));
            Assert.Equal(io.Outputs.Where(f => f[2] == 0x06).First(), io.Outputs.Where(f => f[2] == 0x06).Last());
            io.FailWrites = true;
            Assert.False(session.KeepAlive(io, io.Wait, session.Map.Lights, new[] { 0x112233, 0x445566 }));
        }

        [Fact]
        public void TheHandBack_IsHardwareRenderMode_AndReadsItsAnswer()
        {
            var model = Family().Match(Collection(0x1BF0));
            var io = Device(0x1BF0);
            var session = model.Begin(io, io.Wait);
            session.Paint(io, io.Wait, session.Map.Lights, new int[2]);
            io.Log.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(new[] { "out 00 08 01 03 00 01", "in 50" }, io.Log);
            Assert.Null(KeyboardProfileBackups.Get("corsair:1B1C:1BF0"));
            // A record left by an earlier run goes back once.
            KeyboardProfileBackups.Take("corsair:1B1C:1BF0", 1, session.CrashFrames[0]);
            var repair = new FakeCorsairChannel();
            Assert.True(model.Owed(null));
            Assert.True(model.Repair(repair, repair.Wait));
            Assert.Equal(new[] { "out 00 08 01 03 00 01" }, repair.Log);
        }

        /// <summary>A session records its hand-back under its device's path,
        /// where the worker asks after it and the next run's repair reads it.
        /// Another device of the model owes nothing.</summary>
        [Fact]
        public void ARecordLeftAtADevicesPath_IsOwedAndRepaired_AtThatPath()
        {
            const string path = @"\\?\hid#vid_1b1c&pid_1bf0&mi_01#first";
            const string other = @"\\?\hid#vid_1b1c&pid_1bf0&mi_01#second";
            var model = Family().Match(Collection(0x1BF0));
            var io = Device(0x1BF0, path: path);
            var session = model.Begin(io, io.Wait);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, new int[2]));
            // Killed: no hand-back.
            Assert.True(model.Owed(path));
            Assert.False(model.Owed(other));
            var repair = new FakeCorsairChannel { Path = path };
            Assert.True(model.Repair(repair, repair.Wait));
            Assert.Equal(new[] { "out 00 08 01 03 00 01" }, repair.Log);
            Assert.False(model.Owed(path));
        }
    }
}
