using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using W = PadForge.Common.Input.Peripherals.WootingKeyboardLighting;

namespace PadForge.Tests
{
    /// <summary>The Wooting family against OpenRGB's WootingKeyboardController
    /// at the pinned commit and Wooting's own wooting-rgb-sdk (#557). Every
    /// expected byte is written out from those sources, never from the
    /// builder under test.</summary>
    [Collection("PeripheralOutputStatics")]
    public class WootingKeyboardLightingTests
    {
        private static string Hex(byte[] bytes, int count) => ReportLog.Hex(bytes, count);
        private static string Hex(byte[] bytes, int from, int count) => ReportLog.Hex(bytes, from, count);

        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { W.Instance };

        private static HidCollectionInfo Config(ushort vid, ushort pid, ushort page, ushort output = 257)
            => KeyboardFamilyRig.Collection(vid, pid, page, 0x0001, iface: 2, feature: 8, output: output);

        // ─────────────────────────────────────────────
        //  The tables
        // ─────────────────────────────────────────────

        [Fact]
        public void TheTables_AreOpenRgbsAtThePinnedCommit()
        {
            Assert.Equal("c6fc8016d215cca17d660c47746a08d7ea6a9175", WootingLayouts.OpenRgbCommit);
            // The cells of each matrix map that are not NA
            // (RGBController_WootingKeyboard.cpp:23-30, 179-186, 311-318,
            // 443-449 and 539-545).
            Assert.Equal(110, WootingLayouts.Full.Length);
            Assert.Equal(89, WootingLayouts.Tkl.Length);
            Assert.Equal(94, WootingLayouts.EightyHe.Length);
            Assert.Equal(67, WootingLayouts.Sixty.Length);
            Assert.Equal(17, WootingLayouts.ThreePad.Length);
            // Full: Escape at 0, F1 at 2 (row 0), the number pad's Enter at
            // 104 (row 4, column 20), Number Pad . at 124 (row 5, column 19).
            Assert.Equal(new WootingLed(0, 0, 0, "Key: Escape"), WootingLayouts.Full[0]);
            Assert.Equal(new WootingLed(0, 2, 2, "Key: F1"), WootingLayouts.Full[1]);
            Assert.Contains(new WootingLed(4, 20, 104, "Key: Number Pad Enter"), WootingLayouts.Full);
            Assert.Equal(new WootingLed(5, 19, 124, "Key: Number Pad ."), WootingLayouts.Full[^1]);
            // The 60 percent board starts at buffer row 1 (line 444).
            Assert.Equal(new WootingLed(0, 0, 21, "Key: Escape"), WootingLayouts.Sixty[0]);
            // The UwU's first light (lines 540 and 551).
            Assert.Equal(new WootingLed(0, 0, 0, "Lightbar 14"), WootingLayouts.ThreePad[0]);
            // WootingV1KeyboardController.cpp:23-31: row 0 is 0, NA, 11, 12 ...
            // and row 5 ends NA, 111, 112, NA.
            Assert.Equal(new byte[] { 0, 255, 11, 12, 23, 24 }, WootingLayouts.V1LedIndex.Take(6));
            Assert.Equal(new byte[] { 255, 111, 112, 255 }, WootingLayouts.V1LedIndex.Skip(122));
            // pwm_mem_map (lines 87-93).
            Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x08, 0x09 }, WootingLayouts.V1PwmMap.Take(8));
            Assert.Equal(0x9D, WootingLayouts.V1PwmMap[47]);
        }

        [Fact]
        public void TheLights_NameEachKeyByItsUsage()
        {
            var full = new LightMap(W.Lights(W.KeyboardType.Full), "t");
            // "Key: +" sits between - and Backspace: the = key.
            Assert.Equal(0x2E, full.Lights.Single(l => l.Address == 33).Usage);
            Assert.Equal(0x29, full.Lights.Single(l => l.Address == 0).Usage);
            // "Key: #" (ISO) at 75 and "Key: \ (ISO)" at 85 are both lit.
            Assert.Equal(0x32, full.Lights.Single(l => l.Address == 75).Usage);
            Assert.Equal(0x64, full.Lights.Single(l => l.Address == 85).Usage);
            // The Two's three keys over the number pad and its Mode key send
            // no usage: other lights.
            foreach (int address in new[] { 17, 18, 19, 20 })
                Assert.Equal(LightKind.Other, full.Lights.Single(l => l.Address == address).Kind);
            // The right Fn is labeled.
            Assert.Equal(LightLabel.Fn, full.Lights.Single(l => l.Address == 117).Label);
            // Positions are the grid cells.
            var f1 = full.Lights.Single(l => l.Address == 2);
            Assert.Equal((2f, 0f, true), (f1.X, f1.Y, f1.HasPosition));

            // The 80HE: four space bar lights beside the space bar's own, and
            // four JIS places no source names.
            var eighty = new LightMap(W.Lights(W.KeyboardType.Eighty), "t");
            Assert.Equal(5, eighty.IndexesOf(0x2C).Count);
            Assert.Equal(4, eighty.UnnamedKeyCount);

            // The UwU: 14 lightbar lights, OpenRGB calling the first
            // "Lighbar 1", and three keys no source names.
            var uwu = new LightMap(W.Lights(W.KeyboardType.ThreePad), "t");
            Assert.Equal(14, uwu.EdgeCount);
            Assert.Equal(3, uwu.UnnamedKeyCount);
            Assert.Equal(LightKind.Edge, uwu.Lights.Single(l => l.Address == 21).Kind);
        }

        // ─────────────────────────────────────────────
        //  The collections
        // ─────────────────────────────────────────────

        [Fact]
        public void EveryRegistration_IsMatchedOnItsConfigurationPage()
        {
            var registrations = OpenRgbRegistrations.Of(W.FamilyName).ToList();
            Assert.Equal(35, registrations.Count);
            foreach (var r in registrations)
            {
                ushort vid = (ushort)r.VendorId, pid = (ushort)r.ProductId;
                // The legacy keyboards on page 0x1337 only, the others on
                // 0x1337 or 0xFF55 (WootingKeyboardControllerDetect.cpp:121-171).
                var model = W.Instance.Match(Config(vid, pid, W.PageV2));
                Assert.NotNull(model);
                Assert.Same(W.Instance, model.Family);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(Config(vid, pid, W.PageV2)));
                if (vid == W.AtmelVendorId)
                    Assert.Null(W.Instance.Match(Config(vid, pid, W.PageV3)));
                else
                    Assert.NotNull(W.Instance.Match(Config(vid, pid, W.PageV3)));
                // Another collection of the keyboard makes no controller.
                Assert.Null(W.Instance.Match(Config(vid, pid, 0x0001)));
            }
            // A product no registration names is not matched.
            Assert.Null(W.Instance.Match(Config(W.WootingVendorId, 0x1350, W.PageV3)));
        }

        [Fact]
        public void TheProtocol_IsTheDetectorsByVendorAndPage()
        {
            Assert.Equal(W.Protocol.V1, W.ProtocolOf(0x03EB, 0x1337));
            Assert.Equal(W.Protocol.V2, W.ProtocolOf(0x31E3, 0x1337));
            Assert.Equal(W.Protocol.V3, W.ProtocolOf(0x31E3, 0xFF55));
            Assert.Null(W.ProtocolOf(0x31E3, 0xFF00));
            // The detector reads the third protocol off the page alone. The
            // legacy registrations name page 0x1337, so no such collection
            // reaches it (the match test).
            Assert.Equal(W.Protocol.V3, W.ProtocolOf(0x03EB, 0xFF55));
        }

        [Theory]
        [InlineData(0xFF01, (int)W.KeyboardType.Tkl)]
        [InlineData(0xFF02, (int)W.KeyboardType.Full)]
        [InlineData(0x1100, (int)W.KeyboardType.Tkl)]
        [InlineData(0x1102, (int)W.KeyboardType.Tkl)]
        [InlineData(0x1201, (int)W.KeyboardType.Full)]
        [InlineData(0x1212, (int)W.KeyboardType.Full)]
        [InlineData(0x1220, (int)W.KeyboardType.Full)]
        [InlineData(0x1231, (int)W.KeyboardType.Full)]
        [InlineData(0x1300, (int)W.KeyboardType.Sixty)]
        [InlineData(0x1312, (int)W.KeyboardType.Sixty)]
        [InlineData(0x1321, (int)W.KeyboardType.Sixty)]
        [InlineData(0x1342, (int)W.KeyboardType.Sixty)]
        [InlineData(0x1401, (int)W.KeyboardType.Eighty)]
        [InlineData(0x1510, (int)W.KeyboardType.ThreePad)]
        [InlineData(0x1999, (int)W.KeyboardType.Full)]
        public void TheType_IsTheDetectorsByMaskedProduct(int pid, int type)
        {
            // WootingKeyboardControllerDetect.cpp:71-117.
            Assert.Equal((W.KeyboardType)type, W.TypeOf((ushort)pid));
        }

        [Fact]
        public void TheThreeGamepadModes_ShareOneMapProduct()
        {
            Assert.Equal("wooting:31E3:1220", W.Product(0x31E3, 0x1222));
            Assert.Equal("wooting:31E3:1220", W.Product(0x31E3, 0x1220));
            Assert.Equal("wooting:03EB:FF01", W.Product(0x03EB, 0xFF01));
            Assert.Equal("wooting:31E3:1220", W.Instance.Match(Config(0x31E3, 0x1221, W.PageV3)).Map.Product);
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        [Fact]
        public void TheCommands_AreOpenRgbsAndTheSdks()
        {
            // wooting_usb_send_feature: 00 D0 DA, the command, the
            // parameters (WootingKeyboardController.cpp:73-82). Color init
            // is 33 (WootingKeyboardController.h:25).
            Assert.Equal("00 D0 DA 21 00 00 00 00", Hex(W.Command(W.ColorInitCommand), 8));
            // The SDK's reset, 32 (wooting-usb.h:96), report 0 and D0 for
            // the first two protocols, report 1 and D1 for multi-report
            // keyboards (wooting-usb.c:684-701).
            Assert.Equal("00 D0 DA 20 00 00 00 00", Hex(W.SdkCommand(W.Protocol.V1, 32), 8));
            Assert.Equal("00 D0 DA 20 00 00 00 00", Hex(W.SdkCommand(W.Protocol.V2, 32), 8));
            Assert.Equal("01 D1 DA 20 00 00 00 00", Hex(W.SdkCommand(W.Protocol.V3, 32), 8));
            // The SDK's color init on a multi-report keyboard, 33 through
            // wooting_usb_send_feature (wooting-usb.c:488-490 and 684-701).
            Assert.Equal("01 D1 DA 21 00 00 00 00", Hex(W.SdkCommand(W.Protocol.V3, W.ColorInitCommand), 8));
        }

        [Fact]
        public void AColor_Is565_RedHigh()
        {
            // RGB32ToRGBcolor16 on OpenRGB's 0x00BBGGRR: red & F8 << 8,
            // green & FC << 3, blue & F8 >> 3 (WootingV2KeyboardController.cpp:27).
            Assert.Equal(0xF800, W.Color16(0xFF0000));
            Assert.Equal(0x07E0, W.Color16(0x00FF00));
            Assert.Equal(0x001F, W.Color16(0x0000FF));
            // 0x123456: 0x10 << 8 | 0x34 << 3 | 0x50 >> 3 = 0x1000 | 0x1A0 | 0x0A.
            Assert.Equal(0x11AA, W.Color16(0x123456));
        }

        private static (IReadOnlyList<LightInfo> Lights, int[] Colors) Picture(W.KeyboardType type,
            params (int Address, int Color)[] colors)
        {
            var lights = W.Lights(type);
            var out_ = new int[lights.Count];
            foreach (var (address, color) in colors)
                for (int n = 0; n < lights.Count; n++)
                    if (lights[n].Address == address) out_[n] = color;
            return (lights, out_);
        }

        [Fact]
        public void TheSecondProtocol_IsOneReportOf257()
        {
            var (lights, colors) = Picture(W.KeyboardType.Full, (0, 0xFF0000), (2, 0x123456), (124, 0x0000FF));
            var frame = Assert.Single(W.Frames(W.Protocol.V2, 257, lights, colors));
            Assert.Equal(257, frame.Length);
            // 00 D0 DA 0B, then index 0, 1 (none), 2 (WootingV2KeyboardController.cpp:59-68).
            Assert.Equal("00 D0 DA 0B 00 F8 00 00 AA 11", Hex(frame, 10));
            // Index 124 at 4 + 248.
            Assert.Equal("1F 00", Hex(frame, 252, 2));
            Assert.Equal(0, frame[256]);
        }

        [Fact]
        public void TheSecondProtocol_GoesInFourOn64ByteReports()
        {
            var (lights, colors) = Picture(W.KeyboardType.Sixty, (21, 0xFF0000));
            var whole = W.ColorFrame(W.Protocol.V2, W.FrameBuffer(lights, colors));
            var packets = W.Frames(W.Protocol.V2, 65, lights, colors);
            // wooting-usb.c:620-640: four reports 0 of 65 bytes, each the
            // next 64 bytes after the frame's ID byte.
            Assert.Equal(4, packets.Count);
            Assert.All(packets, p => Assert.Equal(65, p.Length));
            Assert.All(packets, p => Assert.Equal(0, p[0]));
            Assert.Equal("00 D0 DA 0B", Hex(packets[0], 4));
            // Index 21 is at 4 + 42 = 46 of the frame, 46 of the first packet.
            Assert.Equal("00 F8", Hex(packets[0], 46, 2));
            for (int i = 0; i < 4; i++)
                Assert.Equal(whole.Skip(1 + i * 64).Take(64), packets[i].Skip(1));
        }

        [Fact]
        public void TheThirdProtocol_IsTheSdksReport5Of2047()
        {
            var (lights, colors) = Picture(W.KeyboardType.Eighty, (0, 0x00FF00));
            // A collection with short output reports changes nothing here.
            var frame = Assert.Single(W.Frames(W.Protocol.V3, 65, lights, colors));
            Assert.Equal(2047, frame.Length);
            // wooting_usb_send_buffer_v3: report 5, D1 DA, the colors
            // report 0x0B, then the colors (wooting-usb.c:24 and 656-670).
            Assert.Equal("05 D1 DA 0B E0 07", Hex(frame, 6));
        }

        /// <summary>The direct family and the SDK route
        /// (<see cref="WootingRgbSdkRoute"/>) write one frame for one picture
        /// on the second and third protocols, the last light's color
        /// included, so a keyboard gets the same bytes whichever path lights
        /// it.</summary>
        [Fact]
        public void TheFamilyAndTheSdkRoute_WriteOneFrame()
        {
            var lights = W.Lights(W.KeyboardType.Full);
            var colors = lights.Select((_, n) => 0x010203 * (n + 1) & 0xFFFFFF).ToArray();
            foreach (var protocol in new[] { W.Protocol.V2, W.Protocol.V3 })
                Assert.Equal(WootingRgbSdkRoute.ColorReport(protocol, WootingRgbSdkRoute.Matrix(lights, colors)),
                    W.ColorFrame(protocol, W.FrameBuffer(lights, colors)));
        }

        [Fact]
        public void TheCrc_IsCcittWithNoInitialValue()
        {
            // getCrc16ccitt: polynomial 0x1021, 0 to start, no final XOR,
            // CRC-16/XMODEM, whose check value over "123456789" is 0x31C3.
            Assert.Equal(0x31C3, W.Crc16Ccitt(System.Text.Encoding.ASCII.GetBytes("123456789"), 9));
        }

        [Fact]
        public void TheFirstProtocol_IsFiveDriverBuffers()
        {
            // F1 at buffer index 2 is LED 11 (rgb_led_index row 0), buffer 0,
            // place pwm_mem_map[11] = 0x0D. Buffer index 17 is LED 113,
            // buffer 4, place pwm_mem_map[113 % 24 = 17] = 0x35.
            var (lights, colors) = Picture(W.KeyboardType.Full, (2, 0x102030), (17, 0x405060));
            var frames = W.Frames(W.Protocol.V1, 129, lights, colors);
            Assert.Equal(5, frames.Count);
            Assert.All(frames, f => Assert.Equal(129, f.Length));
            // Driver and register per part (WootingV1KeyboardController.cpp:163-193).
            Assert.Equal("00 D0 DA 0B 00 00", Hex(frames[0], 6));
            Assert.Equal("00 D0 DA 0B 00 60", Hex(frames[1], 6));
            Assert.Equal("00 D0 DA 0B 01 00", Hex(frames[2], 6));
            Assert.Equal("00 D0 DA 0B 01 60", Hex(frames[3], 6));
            Assert.Equal("00 D0 DA 0B 02 00", Hex(frames[4], 6));
            // Red, green and blue 0x10 apart (lines 135-138).
            Assert.Equal(0x10, frames[0][6 + 0x0D]);
            Assert.Equal(0x20, frames[0][6 + 0x1D]);
            Assert.Equal(0x30, frames[0][6 + 0x2D]);
            Assert.Equal(0x40, frames[4][6 + 0x35]);
            Assert.Equal(0x50, frames[4][6 + 0x45]);
            Assert.Equal(0x60, frames[4][6 + 0x55]);
            // The CRC of the first 127 bytes, low byte first (lines 203-205).
            foreach (var f in frames)
            {
                ushort crc = W.Crc16Ccitt(f, 127);
                Assert.Equal((byte)crc, f[127]);
                Assert.Equal((byte)(crc >> 8), f[128]);
            }
            // A light whose LED is past the limit goes nowhere: none is in
            // the Full table, and every byte outside the lit places is 0.
            Assert.Equal(6, frames.Sum(f => f.Skip(6).Take(96).Count(b => b != 0)));
        }

        // ─────────────────────────────────────────────
        //  The session on the worker
        // ─────────────────────────────────────────────

        [Fact]
        public void AClaim_InitsWaits_SendsTheFrame_AndHandsBackWithTheReset()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 257, FeatureLength = 8 };
            var collection = Config(0x31E3, 0x1200, W.PageV2);
            using var backend = rig.Lit(Families, collection, 0xFF0000);
            var sent = rig.Log.Sent();
            // The init, then the frame (WootingKeyboardController.cpp:64-69).
            Assert.Equal('F', sent[0].Kind);
            Assert.Equal("00 D0 DA 21 00 00 00 00", Hex(sent[0].Report, 8));
            Assert.Contains(W.InitWaitMs, rig.Waits);
            Assert.Equal('O', sent[1].Kind);
            Assert.Equal("00 D0 DA 0B 00 F8", Hex(sent[1].Report, 6));
            // The crash path holds the reset from the start.
            var record = Assert.Single(backend.Recorded);
            Assert.True(record.Feature);
            Assert.Equal("00 D0 DA 20 00 00 00 00", Hex(Assert.Single(record.Frames), 8));
            Assert.Same(VendorPrograms.Wooting, record.Programs);
            // The hand-back.
            rig.Log.Clear();
            rig.Release(backend);
            var back = Assert.Single(rig.Log.Sent());
            Assert.Equal('F', back.Kind);
            Assert.Equal("00 D0 DA 20 00 00 00 00", Hex(back.Report, 8));
            Assert.Empty(backend.Recorded);
        }

        [Fact]
        public void AThirdProtocolClaim_InitsPaintsAndHandsBackInTheMultiReportForm()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 2047, FeatureLength = 8 };
            var collection = Config(0x31E3, 0x1402, W.PageV3, output: 2047);
            using var backend = rig.Lit(Families, collection, 0x00FF00);
            // The SDK's init and frame on a multi-report keyboard
            // (wooting-usb.c:488-490, 656-682 and 684-701).
            Assert.Equal("01 D1 DA 21 00 00 00 00", Hex(rig.Log.Of('F')[0], 8));
            Assert.Equal("05 D1 DA 0B", Hex(rig.Log.Of('O')[0], 4));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Equal("01 D1 DA 20 00 00 00 00", Hex(Assert.Single(rig.Log.Of('F')), 8));
        }

        [Fact]
        public void AFailedWrite_FailsThePicture()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 257, FeatureLength = 8 };
            rig.Log.OutputFails = _ => true;
            var collection = Config(0x31E3, 0x1200, W.PageV2);
            using var backend = rig.Lit(Families, collection);
            Assert.True(backend.Snapshot.Units.Single().Asleep);
        }

        [Fact]
        public void Wootility_IsTheVendorProgram()
        {
            Assert.Contains(VendorPrograms.WootingWootility, VendorPrograms.All);
            Assert.Equal(new[] { VendorPrograms.WootingWootility }, VendorPrograms.Wooting);
            Assert.True(VendorPrograms.WootingWootility.Owns("Wootility"));
            Assert.Equal("Wootility", VendorPrograms.WootingWootility.Name);
            Assert.Same(VendorPrograms.Wooting, W.Instance.Match(Config(0x31E3, 0x1300, W.PageV3)).Programs);
        }
    }
}
