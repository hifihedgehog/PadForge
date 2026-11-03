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

        private long _tick = 1_000_000;

        public CorsairPeripheralV2LightingTests() => KeyboardProfileBackups.ResetForTest(persistAtOnce: true);

        public void Dispose() => KeyboardProfileBackups.ResetForTest();

        private CorsairPeripheralV2Lighting Family() => new(() => _tick);

        private static HidCollectionInfo Collection(ushort pid, ushort page = 0xFF42, int iface = 1)
            => CorsairPeripheralLightingTests.Collection(pid, page, iface);

        /// <summary>An answer as Windows reads it: report ID 0, then
        /// hidapi's bytes: the write command, the command, the error byte
        /// and a 32-bit value from byte 3 (GetAddress, lines 292-319).</summary>
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
            Func<byte, uint?> pidByCmd = null)
            => new()
            {
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
            // corsair_v2_device_list_data (CorsairPeripheralV2Devices.cpp:1720-1766).
            Assert.Equal(33, CorsairLights.V2Devices.Length);
            Assert.Equal("k55_rgb_pro_device", CorsairLights.V2Devices[0].Name);
            Assert.Equal("mm700_3xl_device", CorsairLights.V2Devices[^1].Name);
            var k100 = CorsairPeripheralV2Lighting.DeviceOf(0x1B7C);
            Assert.Equal(("corsair_k100_layout", 12, 24), (k100.Layout, k100.Rows, k100.Cols));
            var dark = CorsairPeripheralV2Lighting.DeviceOf(0x1B4B);
            Assert.Equal(new[] { "Scroll Wheel", "Side Buttons", "Rear Left", "Logo", "Rear Right", "DPI & Indicator Zone" },
                dark.Zones.Select(z => z.Name));
            Assert.Equal(CorsairZoneType.Linear, dark.Zones[1].Type);
            Assert.Equal(4, dark.Zones[1].Cols);
            // The receivers are not in the list (lines 36-43).
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
            Assert.Equal(38, registrations.Count);
            foreach (var r in registrations)
            {
                var model = family.Match(Collection((ushort)r.ProductId));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Equal(new[] { VendorPrograms.CorsairIcue }, model.Programs);
                Assert.Equal(CorsairPeripheralV2Lighting.KeepAliveCheckMs, model.RecheckMs);
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
            // The highest value, "Logo R" 192 (CorsairPeripheralV2Devices.cpp:285), plus one.
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
            // The K70 RGB PRO's media and indicator keys (lines 141-151).
            Assert.Equal(LightLabel.Brightness, ansi.Single(l => l.Address == 113).Label);
            Assert.Equal(LightKind.Logo, ansi.Single(l => l.Address == 138).Kind);
            Assert.Equal(LightLabel.Mute, ansi.Single(l => l.Address == 102).Label);
            // The K57's indicators (lines 65-66) and the K95 XT's top bar.
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
            var frames = CorsairPeripheralV2Lighting.DataFrames(0x08, data);
            // 57 + 61 + 61 + 21 (SetLEDs, lines 395-454).
            Assert.Equal(4, frames.Count);
            Assert.Equal("00 08 06 00 C8 00 00 00 00 01", Hex(frames[0], 10));
            Assert.Equal(56, frames[0][64]);
            Assert.Equal("00 08 07 00 39 3A", Hex(frames[1], 6));
            Assert.Equal("00 08 07 00 B3", Hex(frames[3], 5));
            Assert.Equal(199, frames[3][4 + 20]);
            Assert.All(frames[3].Skip(25), b => Assert.Equal(0, b));
            Assert.All(frames, f => Assert.Equal(65, f.Length));
            Assert.Single(CorsairPeripheralV2Lighting.DataFrames(0x09, new byte[57]));
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
            Assert.Equal(new[]
            {
                "out 00 08 02 12 00 00", "in 50",      // the product ID
                "out 00 08 02 11 00 00", "in 50",      // the vendor ID
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
        }

        [Fact]
        public void AnUnansweredTransaction_FallsBackToo()
        {
            // StartTransaction returns buffer[2], the 0D it wrote, when no
            // answer came (lines 321-340).
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
            Assert.Equal(new[]
            {
                "out 00 08 02 12 00 00", "in 50",
                "out 00 80 02 11 00 00", "in 50",      // read whatever skip_reads says
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
            Assert.Equal(new[] { "out 00 08 02 12 00 00", "in 50", "out 00 09 02 12 00 00", "in 50", "out 00 09 02 11 00 00" },
                io.Log.Take(5));
            Assert.Equal(CorsairPeripheralV2Lighting.WriteWireless, session.WriteCmd);
            Assert.Equal("m65_ultra_rgb_device", session.Device.Name);
            Assert.Equal(2, session.Map.Lights.Count);
            Assert.Equal("00 09 01 03 00 01", Hex(Assert.Single(session.CrashFrames), 6));
        }

        [Fact]
        public void AProductIdThatIsSlowToCome_IsAskedAgain120MsApart()
        {
            int asked = 0;
            var model = Family().Match(Collection(0x1BF0));
            var io = Device(0x1BF0, pidByCmd: _ => ++asked < 3 ? 0u : null);
            Assert.NotNull(model.Begin(io, io.Wait));
            Assert.Equal(new[] { "out 00 08 02 12 00 00", "in 50", "wait 120", "out 00 08 02 12 00 00", "in 50", "wait 120",
                "out 00 08 02 12 00 00", "in 50", "out 00 08 02 11 00 00" }, io.Log.Take(9));
            // An error answer is no value either (lines 311-317).
            var refusing = new FakeCorsairChannel { Answer = q => Answer(q[1], q[2], error: 3, value: 0x1BF0) };
            Assert.Null(model.Begin(refusing, refusing.Wait));
            Assert.Equal(8 + 1, refusing.Outputs.Count);
        }

        [Fact]
        public void ADeviceNotInTheList_GetsNothingMore()
        {
            var model = Family().Match(Collection(0x1BF0));
            var io = Device(0x1234);
            Assert.Null(model.Begin(io, io.Wait));
            Assert.Equal(new[] { "out 00 08 02 12 00 00", "in 50", "out 00 08 02 11 00 00", "in 50" }, io.Log);
        }

        [Fact]
        public void APicture_IsOneTransaction()
        {
            var model = Family().Match(Collection(0x1BF0));
            var io = Device(0x1BF0);
            var session = model.Begin(io, io.Wait);
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, new[] { 0x112233, 0x445566 }));
            // ClearPacketBuffer, StartTransaction, the block write, its
            // answer or a 2 ms wait, StopTransaction (lines 378-457).
            Assert.Equal(new[]
            {
                "in 3",
                "out 00 08 0D 00 22 00", "in 50",
                "out 00 08 06 00 08 00", "in 3", "wait 2",
                "out 00 08 05 01 00 00", "in 50",
            }, io.Log);
            var block = io.Outputs.Single(f => f[2] == 0x06);
            Assert.Equal("00 08 06 00 08 00 00 00 12 00 11 22 33 44 55 66 00", Hex(block, 17));
            Assert.Equal("00 08 01 03 00 01", Hex(KeyboardProfileBackups.Get("corsair:1B1C:1BF0").Value.Contents, 6));
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
            Assert.Equal(9, io.Log.Count(l => l.StartsWith("out 00 08 07")));
            Assert.Equal(10, io.Log.Count(l => l == "wait 2"));
            Assert.Equal("00 08 06 00 45 02", Hex(io.Outputs.Single(f => f[2] == 0x06), 6));
        }

        [Fact]
        public void TheKeepAlive_SendsTheLastPictureAfter30Seconds()
        {
            var model = Family().Match(Collection(0x1BF0));
            var io = Device(0x1BF0);
            var session = model.Begin(io, io.Wait);
            session.Paint(io, io.Wait, session.Map.Lights, new[] { 0x112233, 0x445566 });
            int sent = io.Outputs.Count(f => f[2] == 0x06);
            _tick += 30000;
            Assert.True(session.Recheck(io, io.Wait));
            Assert.Equal(sent, io.Outputs.Count(f => f[2] == 0x06));
            _tick += 1;
            Assert.True(session.Recheck(io, io.Wait));
            Assert.Equal(sent + 1, io.Outputs.Count(f => f[2] == 0x06));
            Assert.Equal(io.Outputs.Where(f => f[2] == 0x06).First(), io.Outputs.Where(f => f[2] == 0x06).Last());
            io.FailWrites = true;
            _tick += 30001;
            Assert.False(session.Recheck(io, io.Wait));
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
            Assert.True(model.Owed());
            Assert.True(model.Repair(repair, repair.Wait));
            Assert.Equal(new[] { "out 00 08 01 03 00 01" }, repair.Log);
        }
    }
}
