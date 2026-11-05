using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using F = PadForge.Common.Input.Peripherals.PluginDeviceLighting;

namespace PadForge.Tests
{
    /// <summary>What a row of <see cref="PluginDeviceLighting"/> reaches in the whole family
    /// list, and what it sends when a picture stays the same or the session ends with no hand-back.
    /// Expected bytes are written out from the plugins and libratbag.</summary>
    internal static class PluginRowReach
    {
        /// <summary>The collection a row names, with a page and usage no row asks for where it
        /// names none. The board of a QMK row has its raw collection on QMK's page and usage
        /// (qmk_firmware tmk_core/protocol/usb_descriptor_common.h:44-50).</summary>
        public static HidCollectionInfo CollectionOf(PluginDevice d)
        {
            bool raw = PluginQmkTables.Boards.Any(b => b.Plugin == d.Plugin && b.Vendor == d.VendorId && b.Products.Contains(d.ProductId));
            int number = d.Interfaces?[0] ?? (d.Interface != OpenRgbRegistration.Any ? d.Interface : 0);
            ushort page = raw ? (ushort)0xFF60 : (ushort)(d.UsagePage == OpenRgbRegistration.Any ? 0xFFAA : d.UsagePage);
            ushort usage = raw ? (ushort)0x61 : (ushort)(d.Usage == OpenRgbRegistration.Any ? 0xAA : d.Usage);
            return new HidCollectionInfo
            {
                Path = $@"\\?\hid#vid_{d.VendorId:x4}&pid_{d.ProductId:x4}&mi_{number:x2}#{page:x4}{usage:x4}",
                VendorId = d.VendorId,
                ProductId = d.ProductId,
                InterfaceNumber = number,
                UsagePage = page,
                Usage = usage,
                OutputReportLength = (ushort)d.OutputLength,
                FeatureReportLength = (ushort)d.FeatureLength,
                InputReportLength = 64,
            };
        }

        /// <summary>A board that answers the firmware type as SignalRGB's QMK firmware does.</summary>
        public static FakeReportChannel FlashedBoard(string path = null)
        {
            var answer = new byte[33];
            answer[1] = 0x28;
            answer[2] = 0x01;
            return new FakeReportChannel
            {
                Path = path ?? @"\\?\hid#fake", OutputReportLength = 33, InputReportLength = 33, Input = () => answer,
            };
        }

        /// <summary>Why the whole family list does not bring a device to its row, or null when
        /// it does. A family ahead of the plugin table may hold the collection only by handing a
        /// board flashed with the plugin's firmware to the row's session.</summary>
        public static string Unreached(PluginDevice row)
        {
            var collection = CollectionOf(row);
            var model = HidLightingFamilies.Match(collection);
            if (model == null) return "no family matches";
            if (ReferenceEquals(model.Family, F.Instance))
                return model.Name == row.Name ? null : "the row of " + model.Name + " comes first";
            if (row.Answers == null) return model.Family.Name + " comes first";
            var io = FlashedBoard(collection.Path);
            var session = model.Begin?.Invoke(io, _ => { });
            return session is F.Session && session.Map?.Product == $"plugin:{row.VendorId:X4}:{row.ProductId:X4}"
                ? null
                : model.Family.Name + " comes first and keeps the flashed board from the row";
        }
    }

    [Collection("PeripheralOutputStatics")]
    public class PluginRowBehaviorTests
    {
        private static PluginDevice Device(ushort vid, ushort pid)
            => F.Devices.First(d => d.VendorId == vid && d.ProductId == pid);

        private static string Hex(byte[] bytes, int count) => ReportLog.Hex(bytes, count);

        private static byte[][] Written(FakeReportChannel io)
            => io.Calls.Where(c => c.Kind is "out" or "set").Select(c => c.Bytes).ToArray();

        // ── Reaching the row ──

        [Fact]
        public void EveryRow_IsReachedThroughTheWholeFamilyList()
        {
            var shadowed = F.Devices.Where(d => d.OnlyWhen == null)
                .Select(d => (Row: d, Why: PluginRowReach.Unreached(d)))
                .Where(r => r.Why != null)
                .Select(r => $"{r.Row.VendorId:X4}:{r.Row.ProductId:X4} {r.Row.Plugin}: {r.Why}")
                .ToList();
            Assert.True(shadowed.Count == 0, shadowed.Count + " rows no device reaches:\n" + string.Join("\n", shadowed));
        }

        /// <summary>A Keychron flashed with SignalRGB's firmware (SRGBmods/KeychronQMK) takes its
        /// plugin's frames. Effect 23 there is the firmware's SignalRGB effect, which reads no
        /// per-key color, so the session asks the firmware type ahead of Keychron's commands.</summary>
        [Fact]
        public void AKeychronOnSignalRgbFirmware_TakesItsPluginsFrames_AndNoKeychronCommand()
        {
            var row = Device(0x3434, 0x0107);
            var collection = PluginRowReach.CollectionOf(row);
            var model = HidLightingFamilies.Match(collection);
            var io = PluginRowReach.FlashedBoard(collection.Path);
            var session = model.Begin(io, _ => { });
            Assert.IsType<F.Session>(session);
            Assert.Equal("00 28", Hex(Assert.Single(Written(io)), 2));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, session.Map.Lights, new int[session.Map.Lights.Count]));
            var sent = Written(io).Skip(1).ToArray();
            Assert.Equal("00 25", Hex(sent[0], 2));
            Assert.Equal("00 24 00", Hex(sent[1], 3));
            Assert.DoesNotContain(sent, s => s[1] is 0xA2 or 0xA8 or 0x07);
            // The model repeats the picture as the row's does, since the firmware returns to its
            // own lighting when pictures stop.
            Assert.Equal(F.PictureAgainMs, model.KeepAliveMs);
            int before = Written(io).Length;
            Assert.True(session.KeepAlive(io, _ => { }, session.Map.Lights, new int[session.Map.Lights.Count]));
            Assert.True(Written(io).Length > before);
        }

        /// <summary>Keychron's firmware keeps its per-key picture in memory, so a session on it
        /// sends nothing more when the picture stays the same, under the model that repeats a
        /// flashed board's.</summary>
        [Fact]
        public void AStockKeychronOnARowsId_TakesKeychronsSession_AndRepeatsNothing()
        {
            var row = Device(0x3434, 0x0107);
            var collection = PluginRowReach.CollectionOf(row);
            var model = HidLightingFamilies.Match(collection);
            FakeReportChannel io = null;
            io = new FakeReportChannel
            {
                OutputReportLength = 33, InputReportLength = 33,
                Input = () =>
                {
                    var last = io.Calls.Where(c => c.Kind == "out").Last().Bytes;
                    var answer = new byte[33];
                    Array.Copy(last, answer, Math.Min(33, last.Length));
                    if (last[1] == 0x28) answer[1] = 0xFF;
                    if (last[1] == 0xA2) { answer[2] = 0x00; answer[3] = 0x80; }
                    if (last[1] == 0xA8 && last[2] == 0x01) answer[4] = 0x01;
                    if (last[1] == 0xA8 && last[2] == 0x05) { answer[3] = 0x00; answer[4] = 0x01; }
                    if (last[1] == 0x08) { answer[4] = 0x05; answer[5] = 0x06; }
                    if (last[1] == 0xA8 && last[2] == 0x07) answer[4] = 0x00;
                    return answer;
                },
            };
            var session = model.Begin(io, _ => { });
            Assert.IsType<QmkLighting.KeychronSession>(session);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, session.Map.Lights, new[] { 0x112233 }));
            int before = Written(io).Length;
            Assert.True(session.KeepAlive(io, _ => { }, session.Map.Lights, new[] { 0x112233 }));
            Assert.Equal(before, Written(io).Length);
        }

        // ── The collection of a QMK row ──

        /// <summary>RAW_USAGE_PAGE and RAW_USAGE_ID (qmk_firmware
        /// tmk_core/protocol/usb_descriptor_common.h:44-50). Stock firmware on these IDs has other
        /// collections on interface 1, and they are another row's or none.</summary>
        [Fact]
        public void AQmkRow_TakesTheRawCollectionOnly()
        {
            foreach (var row in F.Devices.Where(d => d.Plugin == "GMMK/GMMK_Numpad_QMK.js"))
            {
                Assert.Equal((0xFF60, 0x61), (row.UsagePage, row.Usage));
                Assert.Equal(row.Name, F.Instance.Match(PluginRowReach.CollectionOf(row)).Name);
            }
            var stock = new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_320f&pid_5088&mi_01&col05#ff1c0092",
                VendorId = 0x320F, ProductId = 0x5088, InterfaceNumber = 1,
                UsagePage = 0xFF1C, Usage = 0x0092, OutputReportLength = 64, InputReportLength = 64,
            };
            Assert.Equal("EVision keyboard", F.Instance.Match(stock).Name);
            Assert.Null(F.Instance.Match(stock with { ProductId = 0x5044, Path = @"\\?\hid#vid_320f&pid_5044&mi_01&col05#ff1c0092" }));
        }

        // ── Taking a device again ──

        /// <summary>ASUS_Omni_Device.js:96-104 and :153-166, ASUS_Mouse_Protocol.js:119-121: a
        /// mouse that comes back on its dongle takes direct mode again.</summary>
        [Fact]
        public void TheOmniMouse_TakesDirectModeAgain()
        {
            var row = F.Devices.Single(d => d.Name == "ASUS Omni mouse");
            Assert.Equal(5000, row.KeepAliveMs);
            FakeReportChannel io = null;
            io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            var session = new F.Session(row, row.Picture, null, new PluginVariant(row.Lights, 1, F.OmniMousePicture));
            session.Paint(io, _ => { }, row.Lights, new[] { 0x010203 });
            while (io.Calls.TryDequeue(out _)) { }
            Assert.True(session.KeepAlive(io, _ => { }, row.Lights, new[] { 0x010203 }));
            Assert.Equal("03 51 28 03 00 02 64 02", Hex(Written(io)[0], 8));
        }

        /// <summary>NZXT_Function_Keyboard.js:338-342 sends the new firmware's report only when
        /// a color changed, so that firmware holds a picture with nothing sent.</summary>
        [Theory]
        [InlineData(new byte[] { 1, 3, 71 }, 0)]
        [InlineData(new byte[] { 1, 3, 70 }, 4)]
        public void TheNzxtFunction_RepeatsThePictureOnlyOnTheFirmwareWhosePluginDoes(byte[] version, int repeated)
        {
            var row = F.Devices.First(d => d.Plugin.EndsWith("NZXT_Function_Keyboard.js"));
            var answer = new byte[64];
            version.CopyTo(answer, 3);
            var io = new FakeReportChannel { OutputReportLength = 65, InputReportLength = 64, Input = () => answer };
            var session = F.StartForTest(row, io, _ => { });
            session.Paint(io, _ => { }, session.Map.Lights, new int[10]);
            while (io.Calls.TryDequeue(out _)) { }
            Assert.True(session.KeepAlive(io, _ => { }, session.Map.Lights, new int[10]));
            Assert.Equal(repeated, Written(io).Length);
        }

        /// <summary>Corsair_Headset_Controller.js:165-173: the brightness is read first, and
        /// written only when it is not 1000. A keep-alive takes the same path as the start.</summary>
        [Theory]
        [InlineData(0xE8, 0x03, 0)]
        [InlineData(0x64, 0x00, 1)]
        public void ACorsairHeadset_SetsItsBrightnessOnlyWhenItReadsAnother(int low, int high, int writes)
        {
            var row = Device(0x1B1C, 0x0A42);
            FakeReportChannel io = null;
            io = new FakeReportChannel
            {
                OutputReportLength = 64, InputReportLength = 64,
                Input = () =>
                {
                    var last = io.Calls.Where(c => c.Kind == "out").LastOrDefault().Bytes;
                    if (last == null || last[2] != 0x02 || last[3] != 0x02) return null;
                    var answer = new byte[64];
                    answer[4] = (byte)low;
                    answer[5] = (byte)high;
                    return answer;
                },
            };
            var session = new F.Session(row, row.Picture);
            session.Paint(io, _ => { }, row.Lights, new int[3]);
            string set = "02 09 01 02 00 E8 03", ask = "02 09 02 02 00";
            Assert.Equal(writes, Written(io).Count(w => Hex(w, 7) == set));
            Assert.Equal(1, Written(io).Count(w => Hex(w, 5) == ask));
            while (io.Calls.TryDequeue(out _)) { }
            Assert.True(session.KeepAlive(io, _ => { }, row.Lights, new int[3]));
            Assert.Equal("02 09 01 03 00 02", Hex(Written(io)[0], 6));
            Assert.Equal(writes, Written(io).Count(w => Hex(w, 7) == set));
        }

        /// <summary>A headset that gives no answer gets the write, as the plugin's comparison of
        /// an empty read does.</summary>
        [Fact]
        public void ACorsairHeadsetThatDoesNotAnswer_StillGetsItsBrightness()
        {
            var row = Device(0x1B1C, 0x0A42);
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            new F.Session(row, row.Picture).Paint(io, _ => { }, row.Lights, new int[3]);
            Assert.Single(Written(io), w => Hex(w, 7) == "02 09 01 02 00 E8 03");
        }

        /// <summary>A whole screen is 121 reports of 1,024 bytes, about 15 ms. It repeats as
        /// every SignalRGB row's picture does, and takes no start: the plugin's Initialize runs
        /// once.</summary>
        [Fact]
        public void TheNexus_RepeatsItsScreen_AndNotItsStart()
        {
            var row = Device(0x1B1C, 0x1B8E);
            Assert.Equal(F.PictureAgainMs, F.PictureAgainMsOf(row));
            var model = F.Instance.Match(PluginRowReach.CollectionOf(row));
            Assert.Equal(F.PictureAgainMsOf(row), model.KeepAliveMs);
            Assert.Equal(F.PictureAgainMsOf(row), model.KeepAliveIdleMs);
            var io = new FakeReportChannel { OutputReportLength = 1024, FeatureReportLength = 32 };
            var session = model.Begin(io, _ => { });
            session.Paint(io, _ => { }, row.Lights, new int[1]);
            while (io.Calls.TryDequeue(out _)) { }
            Assert.True(session.KeepAlive(io, _ => { }, row.Lights, new int[1]));
            Assert.Equal(121, Written(io).Length);
            Assert.DoesNotContain(io.Calls, c => c.Kind == "set");
        }

        // ── The collection a row takes ──

        /// <summary>Corsair_Void_Pro_HeadSet_0x0A14.js:53, :99 and :125 write the lights on FFC5.
        /// FF00 carries the sidetone alone (:74-92).</summary>
        [Fact]
        public void TheVoidPro0A14_TakesTheLightingPageOnly()
        {
            var row = Device(0x1B1C, 0x0A14);
            var collection = PluginRowReach.CollectionOf(row);
            Assert.Equal(0xFFC5, collection.UsagePage);
            Assert.NotNull(F.Instance.Match(collection));
            Assert.Null(F.Instance.Match(collection with { UsagePage = 0xFF00 }));
        }

        /// <summary>Roccat_Kain_200_Mouse_Wireless.js:90: the frames go to interface 2, usage 1,
        /// page FF01, as the wired row's.</summary>
        [Fact]
        public void TheWirelessKain200_TakesTheCollectionItsPluginWritesTo()
        {
            var row = Device(0x1E7D, 0x2D60);
            Assert.Equal((2, 0xFF01, 0x0001), (row.Interface, row.UsagePage, row.Usage));
            var collection = PluginRowReach.CollectionOf(row);
            Assert.NotNull(F.Instance.Match(collection));
            Assert.Null(F.Instance.Match(collection with { UsagePage = 0xFF00, Usage = 0x0000 }));
        }

        // ── Two listed endpoints ──

        private static HidCollectionInfo Headset(ushort pid, int iface, int col, ushort page = 0xFF42) => new()
        {
            Path = $@"\\?\hid#vid_1b1c&pid_{pid:x4}&mi_{iface:x2}&col{col:x2}#8&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            VendorId = 0x1B1C, ProductId = pid, InterfaceNumber = iface,
            UsagePage = page, Usage = 0x0001, OutputReportLength = 64, InputReportLength = 64,
        };

        /// <summary>detectDeviceEndpoint (Corsair_Headset_Controller.js:386-417) takes the first
        /// endpoint of its list that the device has, so a device with two is one unit.</summary>
        [Theory]
        // :461-464: interface 3, then 4.
        [InlineData(0x0A42, 3, 5, 4, 1)]
        // :561-564: interface 4, then 3.
        [InlineData(0x0A3E, 4, 1, 3, 4)]
        // :589-590: both on interface 3, the lower collection first.
        [InlineData(0x0A64, 3, 4, 3, 5)]
        public void AHeadsetWithTwoListedEndpoints_IsOneUnit_AtTheFirstItsPluginLists(int pid, int firstIface, int firstCol, int otherIface, int otherCol)
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 64, InputLength = 64 };
            var first = Headset((ushort)pid, firstIface, firstCol);
            var other = Headset((ushort)pid, otherIface, otherCol);
            using var backend = rig.Started(new IHidLightingFamily[] { F.Instance }, new[] { other, first });
            Assert.Equal(first.Path, Assert.Single(backend.Snapshot.Units).Key);
        }

        /// <summary>Two headsets of one model on one PC are two devices, each its own unit.</summary>
        [Fact]
        public void TwoHeadsetsOfOneModel_AreTwoUnits()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 64, InputLength = 64 };
            var one = Headset(0x0A42, 3, 5);
            var two = Headset(0x0A42, 4, 1) with { Path = @"\\?\hid#vid_1b1c&pid_0a42&mi_04&col01#9&2&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}" };
            var boxOne = Guid.NewGuid();
            var boxTwo = Guid.NewGuid();
            using var backend = new HidLightingBackend(new IHidLightingFamily[] { F.Instance },
                path => new ReportLogChannel { Path = path, Device = rig.Log, OutputReportLength = 64, InputReportLength = 64 },
                path => path.Contains("#9&") ? boxTwo : boxOne, rig.Tick,
                (_, _, _) => true, (_, _, _) => true, ms => rig.Advance(ms), idleTickMs: 5);
            backend.Start();
            backend.Rescan(new[] { one, two });
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(new[] { one.Path, two.Path }.OrderBy(p => p), backend.Snapshot.Units.Select(u => u.Key).OrderBy(p => p));
        }

        /// <summary>Each is the unit when the device has only that one.</summary>
        [Fact]
        public void AHeadsetWithOneOfItsListedEndpoints_IsAUnitThere()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 64, InputLength = 64 };
            var only = Headset(0x0A42, 4, 1);
            using var backend = rig.Started(new IHidLightingFamily[] { F.Instance }, new[] { only });
            Assert.Equal(only.Path, Assert.Single(backend.Snapshot.Units).Key);
        }

        /// <summary>A collection on an interface the model's list does not name is not the
        /// device: 0x0A40 lists interface 4 alone (:439).</summary>
        [Fact]
        public void AHeadsetCollectionItsPluginDoesNotList_IsNoUnit()
        {
            Assert.Null(F.Instance.Match(Headset(0x0A40, 3, 4)));
            Assert.NotNull(F.Instance.Match(Headset(0x0A40, 4, 4)));
        }

        // ── The hand-back with no worker to send it ──

        /// <summary>Corsair_Nexus_Screen.js:37-40: the screen returns through two feature
        /// reports, on a session whose pictures are output reports.</summary>
        [Fact]
        public void TheNexus_IsHandedBack_ThroughFeatureReports_WhenTheChannelFails()
        {
            var row = Device(0x1B1C, 0x1B8E);
            using var rig = new KeyboardFamilyRig { OutputLength = 1024, FeatureLength = 32, InputLength = 64 };
            var collection = PluginRowReach.CollectionOf(row);
            var backend = rig.Lit(new IHidLightingFamily[] { F.Instance }, collection);
            rig.Log.FeatureFails = _ => true;
            rig.Log.OutputFails = _ => true;
            backend.Dispose();
            Assert.Equal(new[] { (collection.Path, "03 0D 01 01 78", true), (collection.Path, "03 01 64 01 78", true) },
                rig.OneShots.Select(o => (o.Path, Hex(o.Frame, 5), o.Feature)).ToArray());
        }

        /// <summary>A hand-back owed after a failed write goes out through the same collection, on
        /// the crash record and once the unit answers.</summary>
        [Fact]
        public void TheChromaStrip_OwesItsHandBack_ToItsOtherCollection()
        {
            var row = Device(0x1532, 0x0F2C);
            using var rig = new KeyboardFamilyRig { FeatureLength = 381, InputLength = 64 };
            var collection = PluginRowReach.CollectionOf(row);
            var other = new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_1532&pid_0f2c&mi_00#000c0001",
                VendorId = 0x1532, ProductId = 0x0F2C, InterfaceNumber = 0, UsagePage = 0x000C, Usage = 0x0001, FeatureReportLength = 91,
            };
            using var backend = rig.Started(new IHidLightingFamily[] { F.Instance }, new[] { collection, other });
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(collection));
            KeyboardFamilyRig.Light(0x112233);
            KeyboardFamilyRig.Passes(backend, 4);
            rig.Log.FeatureFails = _ => true;
            KeyboardFamilyRig.Light(0x445566);
            rig.Advance(F.DefaultRepaintMs);
            KeyboardFamilyRig.Passes(backend, 4);
            Assert.True(backend.Snapshot.Units.Single(u => u.Key == collection.Path).Asleep);
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(collection.Path, record.Path);
            Assert.Equal(other.Path, record.WritePath);
            Assert.Empty(rig.OneShots);
            rig.Log.FeatureFails = _ => false;
            KeyboardFamilyRig.Unlight();
            rig.Advance(HidLightingBackend.RetryMs);
            KeyboardFamilyRig.Passes(backend, 4);
            var shot = Assert.Single(rig.OneShots);
            Assert.Equal(other.Path, shot.Path);
            Assert.True(shot.Feature);
            Assert.Empty(backend.Recorded);
        }

        /// <summary>Razer_Chroma_Light_Strip_Set.js hands back through its other interface, and
        /// a frame on the unit's own collection would never reach it.</summary>
        [Fact]
        public void TheChromaStrip_IsHandedBack_ThroughItsOtherCollection_WhenTheChannelFails()
        {
            var row = Device(0x1532, 0x0F2C);
            using var rig = new KeyboardFamilyRig { FeatureLength = 381, InputLength = 64 };
            var collection = PluginRowReach.CollectionOf(row);
            var other = new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_1532&pid_0f2c&mi_00#000c0001",
                VendorId = 0x1532, ProductId = 0x0F2C, InterfaceNumber = 0, UsagePage = 0x000C, Usage = 0x0001, FeatureReportLength = 91,
            };
            var backend = rig.Started(new IHidLightingFamily[] { F.Instance }, new[] { collection, other });
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(collection));
            KeyboardFamilyRig.Light(0x112233);
            KeyboardFamilyRig.Passes(backend, 4);
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(collection.Path, record.Path);
            Assert.Equal(other.Path, record.WritePath);
            Assert.True(record.Feature);
            rig.Log.FeatureFails = _ => true;
            backend.Dispose();
            var shot = Assert.Single(rig.OneShots);
            Assert.Equal(other.Path, shot.Path);
            Assert.True(shot.Feature);
            Assert.Equal("00 00 1F 00 00 00 01 00 37 00", Hex(shot.Frame, 10));
        }
    }
}
