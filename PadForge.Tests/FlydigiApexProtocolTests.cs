using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Flydigi;
using Xunit;
using Ota = PadForge.Engine.Flydigi.FlydigiScreenOta;
using Proto = PadForge.Engine.Flydigi.FlydigiApexProtocol;
using X = PadForge.Engine.Flydigi.FlydigiApex4XinputScreen;

namespace PadForge.Tests
{
    /// <summary>
    /// The bytes PadForge sends a Flydigi Apex 4, Apex 5 and Apex 6 Pro
    /// (#586), held to the references' own test vectors: ApexSenseBridge
    /// tests/test_protocol.cpp, test_apex6_protocol.cpp and
    /// test_apex6_lighting.cpp, flydigi-space-station-mac
    /// FlydigiKitTests/FramingTests.swift, and values openflydigi's
    /// flydigi/screen.py and screen_ota.py compute.
    /// </summary>
    public class FlydigiApexProtocolTests
    {
        private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

        private static byte[] FromHex(string hex) => Convert.FromHexString(hex);

        // ── Apex 5 ──────────────────────────────────────────────────

        [Fact]
        public void Apex5Commands_AreTheReferences()
        {
            var identity = Proto.Apex5IdentityRequest();
            Assert.Equal(32, identity.Length);
            Assert.Equal("035AA5010203", Hex(identity.AsSpan(0, 6)));
            Assert.All(identity.Skip(6), b => Assert.Equal(0, b));

            // buildRumble(0x34, 0x12) (test_protocol.cpp).
            var rumble = Proto.Apex5Rumble(0x34, 0x12);
            Assert.Equal("035AA512063412", Hex(rumble.AsSpan(0, 7)));
            Assert.All(rumble.Skip(7), b => Assert.Equal(0, b));

            // buildReadRgbConfig(0, 20), buildWriteRgbStart(0, 0, 19, 20) and
            // buildWriteRgbPack(0, {1, 2, 3}).
            var read = Proto.Apex5ReadRgb(0);
            Assert.Equal(new byte[] { 0x03, 0x5A, 0xA5, 0xA7, 4, 0, 20, (byte)(0xA7 + 4 + 0 + 20) }, read.Take(8));
            var start = Proto.Apex5WriteRgbStart(0, 0, 19);
            Assert.Equal(new byte[] { 0x03, 0x5A, 0xA5, 0xA8, 6, 0, 0, 19, 20, (byte)(0xA8 + 6 + 19 + 20) }, start.Take(10));
            var data = new byte[20];
            data[0] = 1; data[1] = 2; data[2] = 3;
            var pack = Proto.Apex5WriteRgbPack(0, data);
            Assert.Equal(new byte[] { 0x03, 0x5A, 0xA5, 0xA9, 23, 0, 1, 2, 3 }, pack.Take(9));

            // Command 31 for the screen chip: [4]=3, [5]=4, then the
            // checksum (screen_ota.enter_upgrade_mode).
            Assert.Equal("035AA51F030426", Hex(Proto.Apex5ScreenUpgrade().AsSpan(0, 7)));
            Assert.Equal("035AA5A102A3", Hex(Proto.Apex5ProfileStatus().AsSpan(0, 6)));
            Assert.Equal("035AA5A20302A7", Hex(Proto.Apex5ApplyProfile(2).AsSpan(0, 7)));
        }

        [Fact]
        public void Apex5Trigger_IsTheRawCommand_WithTheRaceRewrite()
        {
            // buildForceTriggerRaw: length 10, apply 1, the side, the mode and
            // the five parameters, and a Race at 0 with match on goes out with
            // match off.
            var race = Proto.Apex5Trigger(new FlydigiTriggerCommand(FlydigiTriggerSide.Right, FlydigiTriggerMode.Race, 0, 30, 1));
            Assert.Equal(new byte[] { 0x03, 0x5A, 0xA5, 81, 10, 1, 2, 1, 0, 30, 0, 0, 0 }, race.Take(13));
            var kept = Proto.Apex5Trigger(new FlydigiTriggerCommand(FlydigiTriggerSide.Left, FlydigiTriggerMode.Race, 70, 30, 1));
            Assert.Equal(1, kept[10]);
        }

        [Fact]
        public void Apex5RgbBlock_TakesTheReferenceHeader_OverTheBlockItRead()
        {
            var read = new byte[Proto.Apex5RgbSize];
            read[0] = 0x00; read[1] = 0x03; read[6] = 77;
            var block = Proto.Apex5RgbBlock(read, Enumerable.Repeat(0x102030, 12).ToArray(), 80, latched: true);
            // writeWorkingColor: [2]=1 latched, [3]=0, [4]=9, [5]=1, [6] the
            // brightness, [7]=12, [8]=4, [9]=0, [10..19]=FF, the version kept.
            Assert.Equal(new byte[] { 0x00, 0x03, 1, 0, 9, 1, 80, 12, 4, 0 }, block.Take(10));
            Assert.All(block.Skip(10).Take(10), b => Assert.Equal(0xFF, b));
            for (int at = Proto.RgbHeader; at < Proto.Apex5RgbSize; at += 3)
                Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, block.Skip(at).Take(3));
            Assert.Equal(0, Proto.Apex5RgbBlock(read, new[] { 1 }, 80, latched: false)[2]);
        }

        [Fact]
        public void Apex5RgbPacket_IsFoundAlongTheReport()
        {
            // 04 5A A5 A7, the count, the index, the slot, then twenty bytes
            // (readRgbConfig, read_blob).
            var report = new byte[32];
            report[0] = 0x04; report[1] = 0x5A; report[2] = 0xA5; report[3] = 0xA7;
            report[4] = 19; report[5] = 7; report[6] = 0;
            for (int i = 0; i < 20; i++) report[7 + i] = (byte)(i + 1);
            var data = new byte[20];
            Assert.Equal(7, Proto.ParseApex5RgbPacket(report, data));
            Assert.Equal(Enumerable.Range(1, 20).Select(i => (byte)i), data);
            Assert.Equal(-1, Proto.ParseApex5RgbPacket(new byte[32], data));
        }

        [Fact]
        public void Apex5RawSlot_KeepsTheSwitchBank()
        {
            var reply = new byte[32];
            reply[0] = 0x04; reply[1] = 0x5A; reply[2] = 0xA5; reply[3] = 0xA1; reply[6] = 5;
            Assert.Equal(5, Proto.ParseApex5RawSlot(reply));
            reply[6] = 9;
            Assert.Equal(-1, Proto.ParseApex5RawSlot(reply));
        }

        // ── Apex 4 ──────────────────────────────────────────────────

        [Fact]
        public void Apex4Commands_AreTheReferences()
        {
            // buildApex4IdentityRequest: twelve bytes, 05 EC and zeros.
            Assert.Equal("05EC00000000000000000000", Hex(Proto.Apex4IdentityRequest()));
            // buildApex4ForceTriggerRaw with Race {0, 30, 1}: the apply flag 0
            // and the live Race rewrite.
            var raw = Proto.Apex4Trigger(new FlydigiTriggerCommand(FlydigiTriggerSide.Right, FlydigiTriggerMode.Race, 0, 30, 1));
            Assert.Equal(15, raw.Length);
            Assert.Equal(new byte[] { 0x05, 0xA0, 1, 0, 2, 1, 0, 30, 0 }, raw.Take(9));
            Assert.Equal("050F1020", Hex(Proto.Apex4Rumble(0x10, 0x20).AsSpan(0, 4)));
            Assert.Equal(12, Proto.Apex4Rumble(1, 2).Length);
            // Light writes: 05 E7 slot count, and 05 33 ten bytes A0 index
            // (DInput.writeParcels).
            Assert.Equal("05E70232000000000000000000", Hex(Proto.Apex4WriteLedStart(2, 50).AsSpan(0, 13)));
            var parcel = Proto.Apex4WriteLedParcel(9, Enumerable.Range(1, 10).Select(i => (byte)i).ToArray());
            Assert.Equal("05330102030405060708090AA009", Hex(parcel));
            Assert.Equal("05E502", Hex(Proto.Apex4ReadLed(2).AsSpan(0, 3)));
            Assert.Equal("05EBA0", Hex(Proto.Apex4CurrentSlotRequest().AsSpan(0, 3)));
        }

        [Fact]
        public void Apex4Replies_AreReadAtTheirPlaces()
        {
            // blobParcel: 229 at 15, the index at 3, ten bytes from 5.
            var reply = new byte[32];
            reply[15] = 229; reply[3] = 4;
            for (int i = 0; i < 10; i++) reply[5 + i] = (byte)(0x40 + i);
            var data = new byte[10];
            Assert.Equal(4, Proto.ParseApex4LedParcel(reply, data));
            Assert.Equal(0x49, data[9]);
            // writeAck: 231 or 51 at 15, the index at 3.
            var ack = new byte[32];
            ack[15] = 51; ack[3] = 12;
            Assert.Equal(12, Proto.ParseApex4LedAck(ack));
            ack[15] = 9;
            Assert.Equal(-1, Proto.ParseApex4LedAck(ack));
            // currentConfigId: EB A0 and the slot at 3.
            Assert.Equal(2, Proto.ParseApex4CurrentSlot(new byte[] { 0x04, 0xEB, 0xA0, 2 }));
        }

        [Fact]
        public void Apex4SteadyLed_IsSetSteady_GroupByGroup()
        {
            var block = new byte[Proto.Apex4LedSize];
            block[0] = 0x00; block[1] = 0x02; block[2] = 1; block[4] = 5; block[6] = 50; block[7] = 4; block[8] = 3;
            Assert.True(Proto.IsApex4Led(block));
            Proto.Apex4SteadyLed(block, new[] { 0xFF0000, 0x00FF00, 0x0000FF, 0x808080 });
            // setSteady: mode 5, loop 0 to 0, the type flag clear, every unit
            // the color in percent, the brightness kept.
            Assert.Equal(new byte[] { 0x00, 0x02, 0, 0, 0, 0, 50, 4, 5 }, block.Take(9));
            Assert.Equal(new byte[] { 100, 0, 0 }, block.Skip(Proto.RgbHeader).Take(3));
            Assert.Equal(new byte[] { 0, 100, 0 }, block.Skip(Proto.RgbHeader + 30).Take(3));
            Assert.Equal(new byte[] { 0, 0, 100 }, block.Skip(Proto.RgbHeader + 60 + 27).Take(3));
            // Unit(rgb8:) rounds down: 128 is 50 percent.
            Assert.Equal(new byte[] { 50, 50, 50 }, block.Skip(Proto.RgbHeader + 90).Take(3));
        }

        // ── Apex 6 ──────────────────────────────────────────────────

        [Fact]
        public void Apex6Frames_AreTheReferences()
        {
            // buildGetInfo.
            var info = Proto.Apex6IdentityRequest();
            Assert.Equal(33, info.Length);
            Assert.Equal("005AA50102", Hex(info.AsSpan(0, 5)));
            Assert.Equal(0x03, info[32]);
            // buildMotorEnable and buildMotorDisable.
            var triggers = Proto.Apex6TriggerStream(Proto.Apex6Route.Both);
            Assert.Equal(0x07, triggers[4]);
            Assert.Equal(0xA0, triggers[32]);
            var grips = Proto.Apex6GripStream(true);
            Assert.Equal("0112024000", Hex(grips.AsSpan(5, 5)));
            Assert.Equal(0xAF, grips[32]);
            var off = Proto.Apex6TriggerOff(Proto.Apex6Route.Both);
            Assert.Equal(0x05, off[4]);
            Assert.Equal(0x5B, off[32]);
            Assert.Equal(0x6B, Proto.Apex6GripStream(false)[32]);
            var upgrade = Proto.Apex6ScreenUpgrade();
            Assert.Equal("005AA51F0304", Hex(upgrade.AsSpan(0, 6)));
            Assert.Equal(0x1F + 0x03 + 0x04, upgrade[32]);
        }

        [Fact]
        public void Apex6Stream_IsBuildRealtimeMotor()
        {
            var trigger = new sbyte[8];
            var left = new sbyte[8];
            var right = new sbyte[8];
            trigger[0] = -128; left[0] = 0; right[0] = 127;
            trigger[7] = 1; left[7] = -1; right[7] = 0;
            var report = Proto.Apex6Stream(trigger, left, right, Proto.Apex6Route.Right, true, true, true);
            Assert.Equal(0x57, report[3]);
            Assert.Equal(0x1B, report[4]);
            Assert.Equal(0x9D, report[5]);
            Assert.Equal(new byte[] { 0x00, 0x80, 0xFF }, report.Skip(6).Take(3));
            Assert.Equal(new byte[] { 0x81, 0x7F, 0x80 }, report.Skip(27).Take(3));
            Assert.Equal(Proto.Apex6Checksum(report.AsSpan(1)), report[32]);
            var gripsOnly = Proto.Apex6Stream(trigger, left, right, Proto.Apex6Route.Mute, false, true, true);
            Assert.Equal(0x9B, gripsOnly[5]);
            Assert.Equal(0x80, gripsOnly[6]);
        }

        [Fact]
        public void Apex6Identity_ReadsTheTypeAndTheFeatures_WithOrWithoutTheReportId()
        {
            var reply = new byte[33];
            reply[1] = 0x5A; reply[2] = 0xA5; reply[3] = 0x01; reply[4] = 1; reply[5] = 0;
            reply[6] = 0x96; reply[7] = 0; reply[30] = 0x9F;
            reply[32] = Proto.Apex6Checksum(reply.AsSpan(1));
            var identity = Proto.ParseIdentity(FlydigiApexModel.Apex6Pro, reply);
            Assert.NotNull(identity);
            Assert.True(identity.Wired);
            Assert.True(identity.HasVoiceCoils);
            Assert.True(identity.HasScreen);
            Assert.NotNull(Proto.ParseIdentity(FlydigiApexModel.Apex6Pro, reply.AsSpan(1)));
            reply[30] ^= 1;
            Assert.Null(Proto.ParseIdentity(FlydigiApexModel.Apex6Pro, reply));
            // The plain Apex 6 takes no stream.
            reply[30] ^= 1;
            reply[6] = 0x95;
            reply[32] = Proto.Apex6Checksum(reply.AsSpan(1));
            Assert.Null(Proto.ParseIdentity(FlydigiApexModel.Apex6Pro, reply));
        }

        [Fact]
        public void Apex4AndApex5Identities_AreReadAtTheirPlaces()
        {
            var apex5 = new byte[32];
            apex5[0] = 0x04; apex5[1] = 0x5A; apex5[2] = 0xA5; apex5[3] = 0x01; apex5[6] = 128; apex5[7] = 1;
            var five = Proto.ParseIdentity(FlydigiApexModel.Apex5, apex5);
            Assert.Equal(FlydigiApexModel.Apex5, five.Model);
            Assert.True(five.Wired);
            Assert.True(five.HasScreen);
            apex5[6] = 200;
            Assert.Null(Proto.ParseIdentity(FlydigiApexModel.Apex5, apex5));

            var apex4 = new byte[32];
            apex4[15] = 0xEC; apex4[3] = 84; apex4[13] = 0;
            var four = Proto.ParseIdentity(FlydigiApexModel.Apex4, apex4);
            Assert.Equal(FlydigiApexModel.Apex4, four.Model);
            Assert.False(four.Wired);
            // Exactly 32 bytes, the reference's rule.
            Assert.Null(Proto.ParseIdentity(FlydigiApexModel.Apex4, apex4.Concat(new byte[1]).ToArray()));
            // A Vader's type on the shared IDs is no Apex 4.
            apex4[3] = 19;
            Assert.Null(Proto.ParseIdentity(FlydigiApexModel.Apex4, apex4));
        }

        [Fact]
        public void Apex6Lighting_IsTheReferences()
        {
            // test_apex6_lighting.cpp's block read from a pad, its checks, and
            // the solid green it builds.
            var original = FromHex("200052404C4748540200" + "0E0000003F0103503203" + "6D00FF3080FF0900FF00" + "AB04");
            Assert.Equal(0x9164741Cu, Proto.Crc32(original));
            Assert.Equal(0x4052, Proto.Crc16Ccitt(original.AsSpan(4)));
            Assert.True(Proto.ParseApex6Lighting(original, out bool vibration, out byte brightness, out byte mode));
            Assert.Equal(3, mode);
            Assert.Equal(80, brightness);
            Assert.False(vibration);

            var green = Proto.Apex6SolidLighting(0, 255, 0, 80, false);
            Assert.Equal("1800339D4C4748540200060000003701" + "0100FF0050005001", Hex(green));
            Assert.Equal(0x7EE3282Cu, Proto.Crc32(green));
            Assert.True(Proto.ParseApex6Lighting(Proto.Apex6SolidLighting(1, 2, 3, 40, true), out vibration, out brightness, out mode));
            Assert.True(vibration);
            Assert.Equal(40, brightness);
            Assert.Equal(1, mode);

            var broken = (byte[])original.Clone();
            broken[20] ^= 1;
            Assert.False(Proto.ParseApex6Lighting(broken, out _, out _));
            Assert.False(Proto.ParseApex6Lighting(original.AsSpan(0, original.Length - 1), out _, out _));
        }

        [Fact]
        public void Apex6Ram_RequestsAndReplies_AreTheReferences()
        {
            var info = Proto.Apex6RamInfo(4);
            Assert.Equal(new byte[] { 0xA3, 4, 1, 4 }, info.Skip(3).Take(4));
            var block = Proto.Apex6RamRead(4, 3);
            Assert.Equal(new byte[] { 0, 4, 3, 16 }, block.Skip(5).Take(4));
            var begin = Proto.Apex6RamWriteBegin(4, 24, 0x7EE3282Cu);
            Assert.Equal(new byte[] { 0, 4, 24, 0, 0x2C, 0x28, 0xE3, 0x7E }, begin.Skip(5).Take(8));
            var chunk = Proto.Apex6RamWriteChunk(16, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Equal(new byte[] { 1, 16, 0, 8, 1 }, chunk.Skip(5).Take(5));
            Assert.Equal(8, chunk[16]);
            var commit = Proto.Apex6RamWriteCommit(24, 0x7EE3282Cu);
            Assert.Equal(new byte[] { 2, 24, 0, 0x2C }, commit.Skip(5).Take(4));
            Assert.Equal(0x7E, commit[11]);
            var abort = Proto.Apex6RamWriteAbort();
            Assert.Equal(3, abort[4]);
            Assert.Equal(3, abort[5]);

            var infoReply = new byte[32];
            FromHex("5AA5A30100040020001C746491").CopyTo(infoReply, 0);
            infoReply[31] = 0x4D;
            var payload = Proto.Apex6RamReply(infoReply, Proto.Apex6CmdRamRead, 1);
            Assert.False(payload.IsEmpty);
            Assert.True(Proto.Apex6RamReply(infoReply, Proto.Apex6CmdRamRead, 0).IsEmpty);
            Assert.True(Proto.ParseApex6RamInfo(payload, 4, out int length, out uint crc));
            Assert.Equal(32, length);
            Assert.Equal(0x9164741Cu, crc);

            var blockReply = FromHex("5AA5A30000040020000010200052404C4748540200" + "0E0000003F010000000008");
            var blockPayload = Proto.Apex6RamReply(blockReply, Proto.Apex6CmdRamRead, 0);
            var data = Proto.ParseApex6RamBlock(blockPayload, 4, 0);
            Assert.Equal(16, data.Length);
            Assert.Equal(0x20, data[0]);
            Assert.Equal(0x01, data[15]);

            Assert.True(Proto.Apex6RamCommitted(new byte[] { 2, 0, 4, 0, 24, 0, 0x2C, 0x28, 0xE3, 0x7E }, 24, 0x7EE3282Cu));
            Assert.False(Proto.Apex6RamCommitted(new byte[] { 2, 0 }, 24, 0x7EE3282Cu));
            Assert.True(Proto.Apex6RamAccepted(new byte[] { 0, 0 }));
            Assert.False(Proto.Apex6RamAccepted(new byte[] { 0, 1 }));
        }

        [Fact]
        public void Crcs_MatchTheirCheckValues()
        {
            var check = System.Text.Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, Proto.Crc32(check));
            Assert.Equal(0x29B1, Proto.Crc16Ccitt(check));
        }

        // ── The screen ──────────────────────────────────────────────

        [Fact]
        public void ScreenFrame_IsLvglTrueColor_HighByteFirst()
        {
            var frame = FlydigiScreenFormat.EncodeFrame(new byte[160 * 80 * 3]);
            Assert.Equal(25604, frame.Length);
            Assert.Equal("0480020A", Hex(frame.AsSpan(0, 4)));
            // pixelQuantisationIsBigEndian565: (40, 88, 248) is 0x2ADF.
            var rgb = new byte[160 * 80 * 3];
            rgb[0] = 40; rgb[1] = 88; rgb[2] = 248;
            rgb[3] = 255; rgb[4] = 255; rgb[5] = 255;
            var encoded = FlydigiScreenFormat.EncodeFrame(rgb);
            Assert.Equal(0x2A, encoded[4]);
            Assert.Equal(0xDF, encoded[5]);
            Assert.Equal(0xFF, encoded[6]);
            Assert.Equal(0xFF, encoded[7]);
            // White comes back white.
            var decoded = FlydigiScreenFormat.DecodeFrame(encoded);
            Assert.Equal(new byte[] { 255, 255, 255 }, decoded.Skip(3).Take(3));
            Assert.True(FlydigiScreenFormat.IsFrame(encoded));
            Assert.False(FlydigiScreenFormat.IsFrame(encoded.AsSpan(1)));
        }

        /// <summary>Frames that openflydigi's own fit makes from the same
        /// pixels, by the first eight bytes of their SHA-256.</summary>
        [Theory]
        [InlineData(7, 5, FlydigiScreenFit.Fill, "76ab4299341ce4cf")]
        [InlineData(7, 5, FlydigiScreenFit.Fit, "5d2407c78b9d6bc5")]
        [InlineData(7, 5, FlydigiScreenFit.Stretch, "1c3356b12ccd5c6b")]
        [InlineData(400, 123, FlydigiScreenFit.Fill, "bb26dc27352beb10")]
        [InlineData(400, 123, FlydigiScreenFit.Fit, "ef6ff4290ca6d2d7")]
        [InlineData(400, 123, FlydigiScreenFit.Stretch, "9c49ccfe9aa39f72")]
        [InlineData(90, 200, FlydigiScreenFit.Fill, "3d22266528dd9789")]
        [InlineData(90, 200, FlydigiScreenFit.Fit, "001c61bd4c8c49d7")]
        [InlineData(90, 200, FlydigiScreenFit.Stretch, "87a6ea505940333b")]
        [InlineData(160, 80, FlydigiScreenFit.Fill, "ab9acae8f0e69768")]
        public void ScreenFit_IsOpenflydigis(int width, int height, FlydigiScreenFit fit, string sha)
        {
            var rgb = new byte[width * height * 3];
            for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)((i * 37 + 11) % 256);
            var fitted = FlydigiScreenFormat.Fit(rgb, width, height, fit);
            var hash = System.Security.Cryptography.SHA256.HashData(fitted);
            Assert.Equal(sha, Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 16));
        }

        [Fact]
        public void OtaChecksum_IsAppOtasCrcCal()
        {
            // screen_ota.checksum on the same data.
            Assert.Equal(0x6A8517BDu, Ota.Checksum(System.Text.Encoding.ASCII.GetBytes("123456789")));
            var ramp = Enumerable.Range(0, 25600).Select(i => (byte)(i % 256)).ToArray();
            Assert.Equal(0x3BED77DDu, Ota.Checksum(ramp));
            Assert.Equal(0xAD447C41u, Ota.Checksum(Enumerable.Repeat((byte)0xFF, 55).ToArray()));
        }

        [Fact]
        public void OtaPackets_CarryTheCopiedLengthFields()
        {
            Assert.Equal("030600" + "00F02F00", Hex(Ota.Packet(Ota.OpErase, Ota.AddressPayload(0x002FF000), strict: false)));
            Assert.Equal("0A0600", Hex(Ota.Packet(Ota.OpGetVersion, ReadOnlySpan<byte>.Empty, strict: false)));
            Assert.Equal("0B0400" + "02010A00", Hex(Ota.Packet(Ota.OpGetBase, Ota.BasePayload(1, 100, strict: false), strict: false)));
            Assert.Equal("0B0400" + "0123" + "0B00", Hex(Ota.Packet(Ota.OpGetBase, Ota.BasePayload(35, 105, strict: true), strict: true)));
            // The older writer declares 55 and says 64 for the packet. The
            // newer one declares the chunk's length and three more.
            var chunk = new byte[] { 9, 8, 7 };
            Assert.Equal("054000" + "10000000" + "3700" + "090807", Hex(Ota.Packet(Ota.OpWrite, Ota.WritePayload(0x10, chunk, false), false)));
            Assert.Equal("050C00" + "10000000" + "0300" + "090807", Hex(Ota.Packet(Ota.OpWrite, Ota.WritePayload(0x10, chunk, true), true)));
            var data = new byte[] { 1, 2, 3 };
            uint crc = Ota.Checksum(data);
            var reset = Ota.Packet(Ota.OpReset, Ota.ResetPayload(data), false);
            Assert.Equal("0C0800" + "03000000", Hex(reset.AsSpan(0, 7)));
            Assert.Equal(crc, BitConverter.ToUInt32(reset, 7));
            // One frame is 7 erases and 466 writes (PROTOCOL.md section 8d).
            Assert.Equal(7, Ota.Erases(FlydigiScreenFormat.FrameLength));
            Assert.Equal(466, Ota.Writes(FlydigiScreenFormat.FrameLength));
            // round() is to the nearest even, the newer engine rounds half up.
            Assert.Equal(10, Ota.FrameRate(105, strict: false));
            Assert.Equal(11, Ota.FrameRate(105, strict: true));
            Assert.Equal(Ota.PicturePng, Ota.PictureType(1));
            Assert.Equal(Ota.PictureGif, Ota.PictureType(2));
        }

        [Fact]
        public void StrictReplies_SkipStaleBytes_AndOtherPackets()
        {
            var payload = Ota.AddressPayload(0x2000);
            // A stray byte, a reply to the version, an erase reply for another
            // address, then the one that answers.
            var buffered = new List<byte> { 0x77 };
            buffered.AddRange(new byte[] { 0, Ota.OpGetVersion, 4, 0, 1, 2, 3, 4 });
            buffered.AddRange(new byte[] { 0, Ota.OpErase, 4, 0, 0x00, 0x10, 0, 0 });
            Assert.Equal(Ota.StrictReading.More, Ota.ReadStrict(buffered, Ota.OpErase, payload, out _, out _));
            buffered.AddRange(new byte[] { 0, Ota.OpErase, 4, 0, 0x00, 0x20, 0, 0 });
            Assert.Equal(Ota.StrictReading.Accepted, Ota.ReadStrict(buffered, Ota.OpErase, payload, out _, out var body));
            Assert.Equal(payload, body.Take(4));
            // A nonzero result for the opcode sent is a refusal.
            var refused = new List<byte> { 3, Ota.OpWrite };
            Assert.Equal(Ota.StrictReading.Refused, Ota.ReadStrict(refused, Ota.OpWrite, new byte[6], out byte result, out _));
            Assert.Equal(3, result);
        }

        [Fact]
        public void Apex4XinputPackets_MatchTheCaptures()
        {
            // FramingTests.swift: the device-info command, and the start of
            // frame 1 of 3 at period 2.
            Assert.Equal("A510000000000000000000000000B5", Hex(X.DeviceInfoRequest()));
            Assert.Equal("A5D009010103010264044900000000", Hex(X.Start(1, 3, 2, 25604)));
            var frame = Enumerable.Range(0, 60).Select(i => (byte)i).ToArray();
            var data = X.Data(0, frame.AsSpan(0, 26));
            Assert.Equal(31, data.Length);
            Assert.Equal(frame.Take(26), data.Skip(4).Take(26));
            byte sum = 0;
            for (int i = 1; i < 30; i++) sum += data[i];
            Assert.Equal(sum, data[30]);
            var last = X.Data(52, frame.AsSpan(52));
            Assert.All(last.Skip(12).Take(18), b => Assert.Equal(0xFF, b));
            Assert.Equal("A5D207010164040043", Hex(X.End(1, 25604).AsSpan(0, 9)));
            Assert.Equal("A5D3070103000000DE", Hex(X.EndAll(3).AsSpan(0, 9)));
            Assert.Equal("A517000000000000000000000000BC", Hex(X.SwitchToDInput()));
        }

        [Fact]
        public void Apex4XinputReplies_AreReadAtByte14()
        {
            var report = new byte[64];
            FromHex("5AA5D1050063D60F0000").CopyTo(report, 14);
            Assert.True(X.ParseAck(report, out byte command, out byte result));
            Assert.Equal(0xD1, command);
            Assert.Equal(0, result);
            var info = new byte[64];
            info[14] = 0xA5; info[15] = 0x10; info[16] = 84; info[25] = 1;
            Assert.Equal(84, X.ParseDeviceType(info, out byte connection));
            Assert.Equal(1, connection);
            Assert.Equal(-1, X.ParseDeviceType(new byte[64], out _));
            // One frame is 985 data packets (docs/protocol.md section 6).
            Assert.Equal(985, (FlydigiScreenFormat.FrameLength + X.Chunk - 1) / X.Chunk);
        }

        // ── The grips' samples ──────────────────────────────────────

        [Fact]
        public void GripSamples_AreEachMillisecondsMean_AtFullScale127()
        {
            var frames = new float[16];
            for (int i = 0; i < 8; i++)
            {
                frames[i * 2] = 1f;
                frames[i * 2 + 1] = i < 4 ? -1f : 0f;
            }
            Assert.Equal(127, HapticToneService.FlydigiSample(frames, 0));
            Assert.Equal(-63, HapticToneService.FlydigiSample(frames, 1));
            Assert.Equal(8, HapticToneService.FlydigiFramesPerSample);
        }
    }
}
