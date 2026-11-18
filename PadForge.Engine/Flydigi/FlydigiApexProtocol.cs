using System;

namespace PadForge.Engine.Flydigi
{
    /// <summary>The Flydigi Apex pads PadForge drives beyond SDL's input
    /// and rumble (#586).</summary>
    public enum FlydigiApexModel
    {
        None,
        /// <summary>k2, 04B4:2412 in DInput mode.</summary>
        Apex4,
        /// <summary>k5, 37D7:2501.</summary>
        Apex5,
        /// <summary>k6 Pro, 37D7:2502, four voice coils.</summary>
        Apex6Pro,
    }

    /// <summary>A trigger as the pads number them. 3 is in Flydigi's enum
    /// and the pads acknowledge and ignore it, so one command goes to each
    /// trigger (openflydigi PROTOCOL.md section 3a).</summary>
    public enum FlydigiTriggerSide : byte
    {
        Left = 1,
        Right = 2,
    }

    /// <summary>The force-trigger modes of the Apex 4 and Apex 5, Flydigi's
    /// AdapterTriggerType. Space Station's labels for 2 and 3 are crossed
    /// against the enum's names, and the pad follows the labels, so these
    /// names say what the trigger does (openflydigi flydigi/effects.py,
    /// ApexSenseBridge core/TriggerEffect.h).</summary>
    public enum FlydigiTriggerMode : byte
    {
        /// <summary>No effect. Clears the one before.</summary>
        Normal = 0,
        /// <summary>Constant resistance past a point: start, force (1 at
        /// the least), match.</summary>
        Race = 1,
        /// <summary>A vibration past a point: start, pressure, strength,
        /// frequency (each 1 at the least), match.</summary>
        Rattle = 2,
        /// <summary>A band of resistance that gives way: start, length,
        /// force (1 at the least), 0, match.</summary>
        Break = 3,
        /// <summary>A hard stop: start, 255, match.</summary>
        Lock = 4,
        /// <summary>In the enum and built by nothing of Flydigi's.</summary>
        Vibration = 5,
    }

    /// <summary>One trigger's effect as the pad takes it: the side, the
    /// mode and the five parameter bytes behind it.</summary>
    public readonly record struct FlydigiTriggerCommand(
        FlydigiTriggerSide Side, FlydigiTriggerMode Mode,
        byte P0 = 0, byte P1 = 0, byte P2 = 0, byte P3 = 0, byte P4 = 0)
    {
        public static FlydigiTriggerCommand Off(FlydigiTriggerSide side) => new(side, FlydigiTriggerMode.Normal);

        public bool IsOff => Mode == FlydigiTriggerMode.Normal;
    }

    /// <summary>What a pad answered to the identity command.</summary>
    public sealed record FlydigiApexIdentity(FlydigiApexModel Model, byte DeviceType, bool Wired)
    {
        /// <summary>The Apex 6 Pro's feature byte, 0 for the others.</summary>
        public byte Features { get; init; }

        /// <summary>The Apex 6 Pro's four voice coils, grips and triggers
        /// both (ApexSenseBridge Apex5Identity::supportsRealtimeHaptics).</summary>
        public bool HasVoiceCoils => Model == FlydigiApexModel.Apex6Pro
            && (Features & FlydigiApexProtocol.Apex6FeatureGripHaptic) != 0
            && (Features & FlydigiApexProtocol.Apex6FeatureTriggerHaptic) != 0;

        /// <summary>Force triggers that resist: the Apex 4 and 5.</summary>
        public bool HasForceTriggers => Model == FlydigiApexModel.Apex4 || Model == FlydigiApexModel.Apex5;

        /// <summary>A screen: every Apex 4 and Apex 5, and an Apex 6 Pro
        /// whose feature byte says so (openflydigi gpa6.py FEATURES,
        /// gpa6_screen.upload_picture).</summary>
        public bool HasScreen => Model is FlydigiApexModel.Apex4 or FlydigiApexModel.Apex5
            || (Model == FlydigiApexModel.Apex6Pro && (Features & FlydigiApexProtocol.Apex6FeatureScreen) != 0);
    }

    /// <summary>
    /// The three vendor dialects of the Flydigi Apex 4, Apex 5 and Apex 6
    /// Pro as bytes (#586). All three answer on a HID collection with usage
    /// page 0xFFA0, which SDL's Flydigi driver opens too.
    ///
    /// <para>Every layout is copied from a tool whose author ran it on the
    /// pad: ApexSenseBridge (src/flydigi/Apex4Protocol.cpp,
    /// Apex5Protocol.cpp, Apex5Identity.cpp, Apex6Protocol.cpp,
    /// Apex6Lighting.cpp), openflydigi (PROTOCOL.md, docs/findings-apex6.md),
    /// flydigi-space-station-mac (docs/protocol.md, LEDConfig.swift,
    /// Framing.swift) and flydigi-apex4-linux (docs/TRIGGERS.md). Pure byte
    /// assembly. The handle, the pacing and the order live in the app's
    /// FlydigiApexSession.</para>
    /// </summary>
    public static class FlydigiApexProtocol
    {
        public const ushort UsagePage = 0xFFA0;

        // ── IDs ──────────────────────────────────────────────────────

        public const ushort Apex4VendorId = 0x04B4;
        public const ushort Apex4ProductId = 0x2412;
        public const ushort NewVendorId = 0x37D7;
        public const ushort Apex5ProductId = 0x2501;
        public const ushort Apex6ProductId = 0x2502;

        /// <summary>The model a collection's IDs allow, before the pad is
        /// asked. The Vader 3 and Vader 4 share the Apex 4's IDs, and the
        /// plain Apex 6 shares the Pro's, so the identity answer decides
        /// (<see cref="ParseIdentity"/>).</summary>
        public static FlydigiApexModel CandidateModel(ushort vendorId, ushort productId)
        {
            if (vendorId == Apex4VendorId && productId == Apex4ProductId) return FlydigiApexModel.Apex4;
            if (vendorId == NewVendorId && productId == Apex5ProductId) return FlydigiApexModel.Apex5;
            if (vendorId == NewVendorId && productId == Apex6ProductId) return FlydigiApexModel.Apex6Pro;
            return FlydigiApexModel.None;
        }

        /// <summary>Flydigi's k2 device types (Apex5Identity.cpp
        /// kApex4DeviceTypes).</summary>
        public static bool IsApex4DeviceType(byte type)
            => type is 84 or 86 or 87 or 92 or 93 or 102 or 103 or 104;

        /// <summary>Flydigi's k5 device types (kApex5DeviceTypes).</summary>
        public static bool IsApex5DeviceType(byte type)
            => type is 128 or 129 or 133 or 134 or 135 or 136;

        /// <summary>The Apex 6 Pro and its Phantom Blade Zero edition. 0x95
        /// is the plain Apex 6 (Apex6Protocol.h isProDeviceType).</summary>
        public static bool IsApex6ProDeviceType(byte type) => type is 0x96 or 0x98;

        public const byte Apex6FeatureGripHaptic = 0x80;
        public const byte Apex6FeatureScreen = 0x08;
        public const byte Apex6FeatureTriggerHaptic = 0x10;

        // ── Framing ──────────────────────────────────────────────────

        public const byte Magic0 = 0x5A;
        public const byte Magic1 = 0xA5;

        /// <summary>Apex 5: output report 0x03, 32 bytes with the ID.</summary>
        public const byte Apex5ReportOut = 0x03;
        public const byte ReportIn = 0x04;
        public const int Apex5ReportSize = 32;

        /// <summary>Apex 4: output report 0x05. A command is 12 bytes at
        /// the least: a shorter one is ignored (flydigi-space-station-mac
        /// docs/protocol.md section 2).</summary>
        public const byte Apex4ReportOut = 0x05;
        public const int Apex4MinimumCommand = 12;

        /// <summary>Apex 6: a 32-byte frame behind a zero report ID.</summary>
        public const int Apex6FrameSize = 32;
        public const int Apex6ReportSize = 33;

        public const byte CmdGetInfo = 0x01;
        public const byte CmdRumble = 0x12;
        public const byte CmdForceTrigger = 81;
        public const byte CmdGripBind = 82;
        public const byte CmdScreenUpgrade = 31;
        public const byte CmdProfileStatus = 0xA1;
        public const byte CmdApplyProfile = 0xA2;
        public const byte CmdReadRgb = 0xA7;
        public const byte CmdWriteRgbStart = 0xA8;
        public const byte CmdWriteRgbPack = 0xA9;

        public const byte Apex4CmdGetInfo = 0xEC;
        public const byte Apex4CmdRumble = 0x0F;
        public const byte Apex4CmdForceTrigger = 0xA0;
        public const byte Apex4CmdReadLed = 229;
        public const byte Apex4CmdWriteLedStart = 231;
        public const byte Apex4CmdWriteLedData = 51;
        public const byte Apex4CmdReadConfig = 235;
        public const byte Apex4SpecifyConfig = 0xA0;

        public const byte Apex6CmdMotorRoute = 0x53;
        public const byte Apex6CmdRealtime = 0x57;
        public const byte Apex6CmdRamRead = 0xA3;
        public const byte Apex6CmdRamWrite = 0xA4;
        public const byte Apex6RamLighting = 4;
        public const int Apex6RamChunk = 16;
        public const int Apex6RamMaximum = 4096;

        // ── Apex 5 ───────────────────────────────────────────────────

        /// <summary>An Apex 5 command with a checksum: the length byte is
        /// the payload plus 2, and the 8-bit sum of the bytes from the
        /// command through the payload follows the payload
        /// (Apex5Protocol.cpp buildChecksummedCommand).</summary>
        public static byte[] Apex5Checksummed(byte command, ReadOnlySpan<byte> payload)
        {
            var report = new byte[Apex5ReportSize];
            report[0] = Apex5ReportOut;
            report[1] = Magic0;
            report[2] = Magic1;
            report[3] = command;
            int count = Math.Min(payload.Length, Apex5ReportSize - 6);
            report[4] = (byte)(count + 2);
            payload.Slice(0, count).CopyTo(report.AsSpan(5));
            byte sum = 0;
            for (int i = 3; i < 3 + report[4]; i++) sum += report[i];
            report[3 + report[4]] = sum;
            return report;
        }

        public static byte[] Apex5IdentityRequest() => Apex5Checksummed(CmdGetInfo, ReadOnlySpan<byte>.Empty);

        /// <summary>Command 81, no checksum: length 10, the apply flag,
        /// the side, the mode and the five parameters. A Race that starts
        /// at 0 with match on goes out with match off, the rewrite
        /// Flydigi's own live path makes (Apex5Protocol.cpp
        /// buildForceTriggerRaw).</summary>
        public static byte[] Apex5Trigger(FlydigiTriggerCommand c)
        {
            var report = new byte[Apex5ReportSize];
            report[0] = Apex5ReportOut;
            report[1] = Magic0;
            report[2] = Magic1;
            report[3] = CmdForceTrigger;
            report[4] = 10;
            report[5] = 1;
            report[6] = (byte)c.Side;
            report[7] = (byte)c.Mode;
            report[8] = c.P0;
            report[9] = c.P1;
            report[10] = c.P2;
            report[11] = c.P3;
            report[12] = c.P4;
            if (c.Mode == FlydigiTriggerMode.Race && report[8] == 0 && report[10] == 1) report[10] = 0;
            return report;
        }

        /// <summary>Command 18: the low motor, then the high
        /// (Apex5Protocol.cpp buildRumble, SDL_hidapi_flydigi.c).</summary>
        public static byte[] Apex5Rumble(byte low, byte high)
        {
            var report = new byte[Apex5ReportSize];
            report[0] = Apex5ReportOut;
            report[1] = Magic0;
            report[2] = Magic1;
            report[3] = CmdRumble;
            report[4] = 6;
            report[5] = low;
            report[6] = high;
            return report;
        }

        public const int Apex5RgbSize = 380;
        public const int Apex5RgbPacket = 20;
        public const int Apex5RgbPackets = 19;
        public const int Apex5Leds = 12;
        public const int Apex5Frames = 10;
        public const int RgbHeader = 20;

        public static byte[] Apex5ReadRgb(byte slot) => Apex5Checksummed(CmdReadRgb, stackalloc byte[] { slot, Apex5RgbPacket });

        public static byte[] Apex5WriteRgbStart(byte slot, byte firstPacket, byte count)
            => Apex5Checksummed(CmdWriteRgbStart, stackalloc byte[] { slot, firstPacket, count, Apex5RgbPacket });

        /// <summary>One 20-byte packet of the block. The index counts from
        /// the write's first packet (Apex5Device.cpp writeRgbConfigRange).</summary>
        public static byte[] Apex5WriteRgbPack(byte index, ReadOnlySpan<byte> data)
        {
            Span<byte> payload = stackalloc byte[1 + Apex5RgbPacket];
            payload[0] = index;
            int count = Math.Min(data.Length, Apex5RgbPacket);
            data.Slice(0, count).CopyTo(payload.Slice(1));
            return Apex5Checksummed(CmdWriteRgbPack, payload.Slice(0, 1 + count));
        }

        public static byte[] Apex5ProfileStatus() => Apex5Checksummed(CmdProfileStatus, ReadOnlySpan<byte>.Empty);

        public static byte[] Apex5ApplyProfile(byte slot) => Apex5Checksummed(CmdApplyProfile, stackalloc byte[] { slot });

        /// <summary>The chip module of the screen, which command 31 switches
        /// to its serial bootloader and nothing else
        /// (screen_ota.CHIP_SCREEN).</summary>
        public const byte ChipScreen = 4;

        /// <summary>Command 31 for the screen chip: [4]=3, [5]=4 and the
        /// checksum (screen_ota.enter_upgrade_mode, PROTOCOL.md section
        /// 8d).</summary>
        public static byte[] Apex5ScreenUpgrade() => Apex5Checksummed(CmdScreenUpgrade, stackalloc byte[] { ChipScreen });

        /// <summary>The same for an Apex 6 Pro, in its own frame
        /// (gpa6_screen.upload_picture, gpa6.frame).</summary>
        public static byte[] Apex6ScreenUpgrade() => Apex6Command(CmdScreenUpgrade, stackalloc byte[] { ChipScreen });

        /// <summary>The block that shows <paramref name="rgb"/>, one color
        /// per light, on every frame: the frames are the effect, so the same
        /// picture on all ten stands still. It starts from
        /// <paramref name="read"/>, the block the pad gave, and takes the
        /// header ApexSenseBridge writes over it (LightbarBridge.cpp
        /// writeWorkingColor): the latch as given, loop 0 to 9, time 1,
        /// brightness, twelve lights, mode 4, no grip sync, ten FF. The
        /// version bytes stay the pad's own.
        ///
        /// <para>Byte 2 latches the lights on the frame they show
        /// (openflydigi docs/device-settings.md, measured on a k5): while it
        /// is set, frames written do not appear. So a picture goes out
        /// latched, and its first packet goes out once more with the latch
        /// clear, which shows the whole picture at once.</para></summary>
        public static byte[] Apex5RgbBlock(ReadOnlySpan<byte> read, ReadOnlySpan<int> rgb, byte brightness, bool latched)
        {
            var block = new byte[Apex5RgbSize];
            read.Slice(0, Math.Min(read.Length, Apex5RgbSize)).CopyTo(block);
            block[2] = (byte)(latched ? 1 : 0);
            block[3] = 0;
            block[4] = Apex5Frames - 1;
            block[5] = 1;
            block[6] = Math.Min(brightness, (byte)100);
            block[7] = Apex5Leds;
            block[8] = 4;
            block[9] = 0;
            block.AsSpan(10, 10).Fill(0xFF);
            for (int frame = 0; frame < Apex5Frames; frame++)
            {
                for (int led = 0; led < Apex5Leds; led++)
                {
                    int color = rgb.Length == 0 ? 0 : rgb[Math.Min(led, rgb.Length - 1)];
                    int at = RgbHeader + (frame * Apex5Leds + led) * 3;
                    block[at] = (byte)(color >> 16);
                    block[at + 1] = (byte)(color >> 8);
                    block[at + 2] = (byte)color;
                }
            }
            return block;
        }

        /// <summary>The packet of the light block an input report carries
        /// in answer to a read, or -1: 5A A5 A7, then the packet count,
        /// the packet's index, the slot and the packet's twenty bytes
        /// (Apex5Device.cpp readRgbConfig, openflydigi flydigi/blobs.py
        /// read_blob). The mark is looked for along the report, as the
        /// reference looks for it.</summary>
        public static int ParseApex5RgbPacket(ReadOnlySpan<byte> report, Span<byte> data)
        {
            for (int i = 0; i + 3 < report.Length; i++)
            {
                if (report[i] != Magic0 || report[i + 1] != Magic1 || report[i + 2] != CmdReadRgb) continue;
                if (i + 6 + Apex5RgbPacket > report.Length) return -1;
                report.Slice(i + 6, Apex5RgbPacket).CopyTo(data);
                return report[i + 4];
            }
            return -1;
        }

        /// <summary>The raw slot a command 0xA1 reply names, 0 to 7, or
        /// -1. 4 to 7 say the pad runs its Switch bank
        /// (Apex5Protocol.cpp parseProfileStatus).</summary>
        public static int ParseApex5RawSlot(ReadOnlySpan<byte> report)
            => report.Length >= 7 && IsApex5Reply(report, CmdProfileStatus) && report[6] < 8 ? report[6] : -1;

        /// <summary>Whether an input report is the pad's reply to
        /// <paramref name="command"/>: 04 5A A5 and the command echoed
        /// (Apex5Protocol.cpp isProfileCommandReply).</summary>
        public static bool IsApex5Reply(ReadOnlySpan<byte> report, byte command)
            => report.Length >= 4 && report[0] == ReportIn && report[1] == Magic0
            && report[2] == Magic1 && report[3] == command;

        /// <summary>The active profile slot from a command 0xA1 reply, or
        /// -1. Slots 4 to 7 are the Switch bank of the same four
        /// (Apex5Protocol.cpp parseProfileStatus).</summary>
        public static int ParseApex5ProfileSlot(ReadOnlySpan<byte> report)
        {
            if (report.Length < 7 || !IsApex5Reply(report, CmdProfileStatus)) return -1;
            int raw = report[6];
            if (raw >= 8) return -1;
            return raw >= 4 ? raw - 4 : raw;
        }

        // ── Apex 4 ───────────────────────────────────────────────────

        public static byte[] Apex4Command(byte command, params byte[] args)
        {
            var report = new byte[Math.Max(Apex4MinimumCommand, 2 + (args?.Length ?? 0))];
            report[0] = Apex4ReportOut;
            report[1] = command;
            if (args != null) Array.Copy(args, 0, report, 2, args.Length);
            return report;
        }

        public static byte[] Apex4IdentityRequest() => Apex4Command(Apex4CmdGetInfo);

        public static byte[] Apex4Rumble(byte low, byte high) => Apex4Command(Apex4CmdRumble, low, high);

        /// <summary>The DInput force-trigger command, 15 bytes: the
        /// effect family 1, the apply flag, the side, the mode and the
        /// five parameters. The apply flag stays 0: set, the pad also
        /// writes the effect into its stored profile and stalls for about
        /// a second (Apex4Protocol.h kApex4ApplyFlag, flydigi-apex4-linux
        /// docs/TRIGGERS.md). The Race rewrite is the Apex 5's.</summary>
        public static byte[] Apex4Trigger(FlydigiTriggerCommand c)
        {
            var report = new byte[15];
            report[0] = Apex4ReportOut;
            report[1] = Apex4CmdForceTrigger;
            report[2] = 1;
            report[3] = 0;
            report[4] = (byte)c.Side;
            report[5] = (byte)c.Mode;
            report[6] = c.P0;
            report[7] = c.P1;
            report[8] = c.P2;
            report[9] = c.P3;
            report[10] = c.P4;
            if (c.Mode == FlydigiTriggerMode.Race && report[6] == 0 && report[8] == 1) report[8] = 0;
            return report;
        }

        public const int Apex4LedSize = 500;
        public const int Apex4LedParcel = 10;
        public const int Apex4LedParcels = 50;
        public const int Apex4LedGroups = 16;
        public const int Apex4LedUnits = 10;
        public const int Apex4LitGroups = 4;
        public const byte Apex4LedModeSteady = 5;

        /// <summary>The slot the pad has active: 05 EB A0, answered with
        /// EB A0 and the slot (flydigi-space-station-mac DeviceSession.swift
        /// currentConfigId).</summary>
        public static byte[] Apex4CurrentSlotRequest() => Apex4Command(Apex4CmdReadConfig, Apex4SpecifyConfig);

        public static int ParseApex4CurrentSlot(ReadOnlySpan<byte> report)
            => report.Length > 3 && report[1] == Apex4CmdReadConfig && report[2] == Apex4SpecifyConfig && report[3] < 8
                ? report[3] : -1;

        public static byte[] Apex4ReadLed(byte slot) => Apex4Command(Apex4CmdReadLed, slot);

        /// <summary>The 14-byte start of a light write: the slot and the
        /// parcel count (Framing.swift DInput.writeParcels).</summary>
        public static byte[] Apex4WriteLedStart(byte slot, byte parcels)
        {
            var report = new byte[14];
            report[0] = Apex4ReportOut;
            report[1] = Apex4CmdWriteLedStart;
            report[2] = slot;
            report[3] = parcels;
            return report;
        }

        /// <summary>One 14-byte parcel: ten bytes of the block, A0 and the
        /// parcel's index from 0.</summary>
        public static byte[] Apex4WriteLedParcel(byte index, ReadOnlySpan<byte> data)
        {
            var report = new byte[14];
            report[0] = Apex4ReportOut;
            report[1] = Apex4CmdWriteLedData;
            data.Slice(0, Math.Min(data.Length, Apex4LedParcel)).CopyTo(report.AsSpan(2));
            report[12] = Apex4SpecifyConfig;
            report[13] = index;
            return report;
        }

        /// <summary>The parcel a light read answer carries, or -1: the
        /// read command at byte 15, the index at 3 and ten bytes at 5
        /// (Replies.swift DInputReply.blobParcel).</summary>
        public static int ParseApex4LedParcel(ReadOnlySpan<byte> report, Span<byte> data)
        {
            if (report.Length <= 15 || report[15] != Apex4CmdReadLed) return -1;
            report.Slice(5, Apex4LedParcel).CopyTo(data);
            return report[3];
        }

        /// <summary>The index a light write acknowledgement names, or
        /// -1: 231 or 51 at byte 15 and the index at 3, from 1 for a
        /// parcel and FF for the start (Replies.swift DInputReply.writeAck,
        /// run on the pad 2026-09-01).</summary>
        public static int ParseApex4LedAck(ReadOnlySpan<byte> report)
        {
            if (report.Length <= 15) return -1;
            return report[15] == Apex4CmdWriteLedStart || report[15] == Apex4CmdWriteLedData ? report[3] : -1;
        }

        /// <summary>Whether <paramref name="block"/> is a light block this
        /// code can change: 500 bytes, a mode Space Station knows and 1 to
        /// 16 groups in use (LEDConfig.swift init).</summary>
        public static bool IsApex4Led(ReadOnlySpan<byte> block)
            => block.Length == Apex4LedSize && block[8] <= 7 && block[7] >= 1 && block[7] <= Apex4LedGroups;

        /// <summary>Changes <paramref name="block"/>, a light block read
        /// from the pad, to show one steady color per lit group, and
        /// leaves the rest as the pad had it, the brightness included.
        /// Steady is mode 5 with the loop at 0 to 0 and every unit of a
        /// group its color, the channels in percent (LEDConfig.swift
        /// setSteady, docs/protocol.md section 12). The reference gives
        /// every group the one color. Here each group takes its own, which
        /// is the block's own layout: sixteen groups of ten units.</summary>
        public static void Apex4SteadyLed(Span<byte> block, ReadOnlySpan<int> rgb)
        {
            if (block.Length < Apex4LedSize) return;
            block[2] = 0;
            block[3] = 0;
            block[4] = 0;
            block[8] = Apex4LedModeSteady;
            int groups = block[7];
            if (groups < 1 || groups > Apex4LedGroups) groups = Apex4LitGroups;
            for (int group = 0; group < groups; group++)
            {
                int color = rgb.Length == 0 ? 0 : rgb[Math.Min(group, rgb.Length - 1)];
                byte r = Percent(color >> 16), g = Percent(color >> 8), b = Percent(color);
                for (int unit = 0; unit < Apex4LedUnits; unit++)
                {
                    int at = RgbHeader + group * Apex4LedUnits * 3 + unit * 3;
                    block[at] = r;
                    block[at + 1] = g;
                    block[at + 2] = b;
                }
            }
        }

        /// <summary>A channel of 0 to 255 as the pad's percent, rounded
        /// down (LEDConfig.swift Unit.init rgb8).</summary>
        private static byte Percent(int channel) => (byte)((channel & 0xFF) * 100 / 255);

        // ── Apex 6 ───────────────────────────────────────────────────

        /// <summary>The sum of a frame's bytes 2 to 30
        /// (Apex6Protocol.cpp checksum).</summary>
        public static byte Apex6Checksum(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < Apex6FrameSize) return 0;
            byte sum = 0;
            for (int i = 2; i < Apex6FrameSize - 1; i++) sum += frame[i];
            return sum;
        }

        /// <summary>A report for the Apex 6: the zero ID, 5A A5, the
        /// command, the payload length plus 2, the payload, and the
        /// checksum in the frame's last byte (Apex6Protocol.cpp
        /// buildCommand).</summary>
        public static byte[] Apex6Command(byte command, ReadOnlySpan<byte> payload)
        {
            var report = new byte[Apex6ReportSize];
            report[1] = Magic0;
            report[2] = Magic1;
            report[3] = command;
            int count = Math.Min(payload.Length, 27);
            report[4] = (byte)(count + 2);
            payload.Slice(0, count).CopyTo(report.AsSpan(5));
            report[32] = Apex6Checksum(report.AsSpan(1));
            return report;
        }

        public static byte[] Apex6IdentityRequest() => Apex6Command(CmdGetInfo, ReadOnlySpan<byte>.Empty);

        /// <summary>The 32-byte frame inside a report read from the pad,
        /// with or without a report ID byte ahead of it, or empty.</summary>
        public static ReadOnlySpan<byte> Apex6Frame(ReadOnlySpan<byte> report)
        {
            if (report.Length >= Apex6ReportSize && report[0] == 0 && report[1] == Magic0 && report[2] == Magic1)
                return report.Slice(1, Apex6FrameSize);
            if (report.Length >= Apex6FrameSize && report[0] == Magic0 && report[1] == Magic1)
                return report.Slice(0, Apex6FrameSize);
            return ReadOnlySpan<byte>.Empty;
        }

        public static bool IsApex6Reply(ReadOnlySpan<byte> report, byte command)
        {
            var frame = Apex6Frame(report);
            return frame.Length == Apex6FrameSize && frame[2] == command && frame[31] == Apex6Checksum(frame);
        }

        /// <summary>The trigger a motor command names.</summary>
        public enum Apex6Route : byte
        {
            Left = 0,
            Right = 1,
            Both = 2,
            Mute = 3,
        }

        /// <summary>Puts a trigger, or both, on the stream: 01, the
        /// target, 03 00 40 (Apex6Protocol.cpp buildTriggerEnable).</summary>
        public static byte[] Apex6TriggerStream(Apex6Route side)
            => Apex6Command(Apex6CmdMotorRoute, stackalloc byte[] { 0x01, TriggerTarget(side), 0x03, 0x00, 0x40 });

        /// <summary>Takes a trigger, or both, off the stream.</summary>
        public static byte[] Apex6TriggerOff(Apex6Route side)
            => Apex6Command(Apex6CmdMotorRoute, stackalloc byte[] { 0x01, TriggerTarget(side), 0x00 });

        /// <summary>Puts both grips on the stream (true) or gives them back
        /// to the pad's own rumble (buildMotorEnable, buildMotorDisable).</summary>
        public static byte[] Apex6GripStream(bool streamed)
            => streamed
                ? Apex6Command(Apex6CmdMotorRoute, stackalloc byte[] { 0x01, 0x12, 0x02, 0x40, 0x00 })
                : Apex6Command(Apex6CmdMotorRoute, stackalloc byte[] { 0x01, 0x12, 0x00 });

        private static byte TriggerTarget(Apex6Route side)
            => side == Apex6Route.Left ? (byte)0 : side == Apex6Route.Right ? (byte)1 : (byte)2;

        public const int Apex6SamplesPerReport = 8;

        /// <summary>One 8 ms stream report: the control byte, then eight
        /// triples of the trigger, the left grip and the right grip, each
        /// sample -127 to 127 around the pad's zero of 128. A motor whose
        /// flag is off gets 128 (Apex6Protocol.cpp buildRealtimeMotor).</summary>
        public static byte[] Apex6Stream(ReadOnlySpan<sbyte> trigger, ReadOnlySpan<sbyte> leftGrip,
            ReadOnlySpan<sbyte> rightGrip, Apex6Route route, bool triggerOn, bool gripsOn, bool interpolate)
        {
            Span<byte> payload = stackalloc byte[25];
            payload[0] = (byte)((interpolate ? 0x80 : 0) | (gripsOn ? 0x18 : 0) | (triggerOn ? 0x04 : 0) | (byte)route);
            for (int i = 0; i < Apex6SamplesPerReport; i++)
            {
                int at = 1 + i * 3;
                payload[at] = triggerOn && i < trigger.Length ? Sample(trigger[i]) : (byte)0x80;
                payload[at + 1] = gripsOn && i < leftGrip.Length ? Sample(leftGrip[i]) : (byte)0x80;
                payload[at + 2] = gripsOn && i < rightGrip.Length ? Sample(rightGrip[i]) : (byte)0x80;
            }
            return Apex6Command(Apex6CmdRealtime, payload);
        }

        private static byte Sample(sbyte value) => (byte)(value + 128);

        // ── Apex 6 lighting, RAM 4 ───────────────────────────────────

        /// <summary>IEEE CRC-32, reflected, over a whole RAM block.</summary>
        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }

        /// <summary>CRC-16-CCITT, start FFFF, over a block's body.</summary>
        public static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int bit = 0; bit < 8; bit++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        private static ushort Sum16(ReadOnlySpan<byte> data)
        {
            uint total = 0;
            foreach (byte b in data) total += b;
            return (ushort)total;
        }

        /// <summary>The lighting block for one steady color: length and
        /// CRC-16, then LGHT, version 2, the payload's length, the header's
        /// sum, the payload (mode 1, red, green, blue, brightness, the
        /// vibration flag) and its sum (Apex6Lighting.cpp
        /// buildSolidLightingRam).</summary>
        public static byte[] Apex6SolidLighting(byte r, byte g, byte b, byte brightness, bool vibration)
        {
            Span<byte> payload = stackalloc byte[] { 1, r, g, b, brightness, (byte)(vibration ? 1 : 0) };
            var body = new byte[12 + payload.Length + 2];
            body[0] = (byte)'L'; body[1] = (byte)'G'; body[2] = (byte)'H'; body[3] = (byte)'T';
            body[4] = 2; body[5] = 0;
            body[6] = (byte)payload.Length; body[7] = 0; body[8] = 0; body[9] = 0;
            ushort headSum = Sum16(body.AsSpan(0, 10));
            body[10] = (byte)headSum; body[11] = (byte)(headSum >> 8);
            payload.CopyTo(body.AsSpan(12));
            ushort paySum = Sum16(payload);
            body[12 + payload.Length] = (byte)paySum; body[13 + payload.Length] = (byte)(paySum >> 8);

            var ram = new byte[4 + body.Length];
            ram[0] = (byte)ram.Length; ram[1] = (byte)(ram.Length >> 8);
            ushort crc = Crc16Ccitt(body);
            ram[2] = (byte)crc; ram[3] = (byte)(crc >> 8);
            body.CopyTo(ram, 4);
            return ram;
        }

        /// <summary>Whether <paramref name="ram"/> is a lighting block
        /// this code can read: its length, CRC-16, magic and both sums hold,
        /// and its mode is one of the six. On true,
        /// <paramref name="vibration"/> is the block's vibration flag and
        /// <paramref name="brightness"/> its brightness, 100 for a mode
        /// with none (Apex6Lighting.cpp parseLightingRam).</summary>
        public static bool ParseApex6Lighting(ReadOnlySpan<byte> ram, out bool vibration, out byte brightness)
            => ParseApex6Lighting(ram, out vibration, out brightness, out _);

        /// <summary>The same, with the block's mode: 0 off, 1 one steady
        /// color, 2 breathing, 3 gradient, 4 streaming, 5 the pad's
        /// default.</summary>
        public static bool ParseApex6Lighting(ReadOnlySpan<byte> ram, out bool vibration, out byte brightness, out byte mode)
        {
            vibration = false;
            brightness = 100;
            mode = 0;
            if (ram.Length < 4 + 12 + 2) return false;
            if ((ram[0] | (ram[1] << 8)) != ram.Length) return false;
            var body = ram.Slice(4);
            if ((ram[2] | (ram[3] << 8)) != Crc16Ccitt(body)) return false;
            if (body[0] != 'L' || body[1] != 'G' || body[2] != 'H' || body[3] != 'T') return false;
            if ((body[10] | (body[11] << 8)) != Sum16(body.Slice(0, 10))) return false;
            long size = (uint)(body[6] | (body[7] << 8) | (body[8] << 16) | (body[9] << 24));
            if (size == 0 || 12 + size + 2 > body.Length) return false;
            var data = body.Slice(12, (int)size);
            if ((body[12 + (int)size] | (body[13 + (int)size] << 8)) != Sum16(data)) return false;
            mode = data[0];
            int next;
            switch (data[0])
            {
                case 0: next = 1; break;
                case 1:
                    if (data.Length < 5) return false;
                    brightness = data[4];
                    next = 5;
                    break;
                case 2:
                case 3:
                    if (data.Length < 4) return false;
                    brightness = data[1];
                    next = 4 + data[3] * 3;
                    if (next > data.Length) return false;
                    break;
                case 4:
                case 5:
                    if (data.Length < 3) return false;
                    brightness = data[1];
                    next = 3;
                    break;
                default: return false;
            }
            vibration = next < data.Length && (data[next] & 1) != 0;
            return true;
        }

        public static byte[] Apex6RamInfo(byte ram) => Apex6Command(Apex6CmdRamRead, stackalloc byte[] { 1, ram });

        public static byte[] Apex6RamRead(byte ram, byte index)
            => Apex6Command(Apex6CmdRamRead, stackalloc byte[] { 0, ram, index, Apex6RamChunk });

        public static byte[] Apex6RamWriteBegin(byte ram, ushort length, uint crc)
            => Apex6Command(Apex6CmdRamWrite, stackalloc byte[]
            {
                0, ram, (byte)length, (byte)(length >> 8),
                (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24),
            });

        public static byte[] Apex6RamWriteChunk(ushort offset, ReadOnlySpan<byte> data)
        {
            int count = Math.Min(data.Length, Apex6RamChunk);
            Span<byte> payload = stackalloc byte[4 + count];
            payload[0] = 1;
            payload[1] = (byte)offset;
            payload[2] = (byte)(offset >> 8);
            payload[3] = (byte)count;
            data.Slice(0, count).CopyTo(payload.Slice(4));
            return Apex6Command(Apex6CmdRamWrite, payload);
        }

        public static byte[] Apex6RamWriteCommit(ushort length, uint crc)
            => Apex6Command(Apex6CmdRamWrite, stackalloc byte[]
            {
                2, (byte)length, (byte)(length >> 8),
                (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24),
            });

        public static byte[] Apex6RamWriteAbort() => Apex6Command(Apex6CmdRamWrite, stackalloc byte[] { 3 });

        /// <summary>A RAM block's length and CRC-32 from the reply to
        /// <see cref="Apex6RamInfo"/>, false when the pad refused or named
        /// another block (Apex6Lighting.cpp parseRamInfo, Apex5Device.cpp
        /// readApex6Ram).</summary>
        public static bool ParseApex6RamInfo(ReadOnlySpan<byte> reply, byte ram, out int length, out uint crc)
        {
            length = 0;
            crc = 0;
            if (reply.Length < 10 || reply[1] != 0 || reply[2] != ram) return false;
            length = reply[4] | (reply[5] << 8);
            crc = (uint)(reply[6] | (reply[7] << 8) | (reply[8] << 16) | (reply[9] << 24));
            return length > 0 && length <= Apex6RamMaximum;
        }

        /// <summary>The bytes a reply to <see cref="Apex6RamRead"/> carries
        /// for chunk <paramref name="index"/>, or empty when the pad refused
        /// or answered for another chunk (parseRamBlock).</summary>
        public static ReadOnlySpan<byte> ParseApex6RamBlock(ReadOnlySpan<byte> reply, byte ram, int index)
        {
            if (reply.Length < 8 || reply[1] != 0 || reply[2] != ram || reply[6] != index)
                return ReadOnlySpan<byte>.Empty;
            return reply.Slice(8, Math.Min(reply[7], reply.Length - 8));
        }

        /// <summary>Whether a reply to a write's begin or chunk says the
        /// pad took it: a zero behind the mode (Apex5Device.cpp
        /// writeApex6Ram accepted).</summary>
        public static bool Apex6RamAccepted(ReadOnlySpan<byte> reply) => reply.Length > 1 && reply[1] == 0;

        /// <summary>Whether a commit's reply names the length and the
        /// CRC-32 that were sent (parseRamCommit).</summary>
        public static bool Apex6RamCommitted(ReadOnlySpan<byte> reply, ushort length, uint crc)
            => reply.Length >= 10 && (reply[4] | (reply[5] << 8)) == length
            && (uint)(reply[6] | (reply[7] << 8) | (reply[8] << 16) | (reply[9] << 24)) == crc;

        /// <summary>The 28 bytes behind 5A A5 and the command of a RAM
        /// reply whose first byte is <paramref name="mode"/>, or empty
        /// (Apex6Lighting.cpp ramReplyPayload).</summary>
        public static ReadOnlySpan<byte> Apex6RamReply(ReadOnlySpan<byte> report, byte command, byte mode)
        {
            var frame = Apex6Frame(report);
            if (frame.Length != Apex6FrameSize || frame[2] != command
                || frame[31] != Apex6Checksum(frame) || frame[3] != mode)
                return ReadOnlySpan<byte>.Empty;
            return frame.Slice(3, 28);
        }

        // ── Identity ─────────────────────────────────────────────────

        /// <summary>The identity in an input report read after the request
        /// for <paramref name="candidate"/>, or null when the report is no
        /// such reply or names a pad this code does not drive.
        ///
        /// <para>Apex 5 (Apex5Identity.cpp parseReply): 04 5A A5 01, the
        /// device type at 6, the connection at 7, 1 for the cable. Apex 4
        /// (parseApex4Reply): 32 bytes with EC at 15, the type at 3, the
        /// connection at 13, 1 for the cable. Apex 6 (Apex6Protocol.cpp
        /// parseDeviceInfo): a checksummed frame with command 01, the type
        /// at payload 0, the connection at 1, 0 for the cable, the features
        /// at 24.</para></summary>
        public static FlydigiApexIdentity ParseIdentity(FlydigiApexModel candidate, ReadOnlySpan<byte> report)
        {
            switch (candidate)
            {
                case FlydigiApexModel.Apex5:
                    if (report.Length < 14 || !IsApex5Reply(report, CmdGetInfo)) return null;
                    return IsApex5DeviceType(report[6])
                        ? new FlydigiApexIdentity(FlydigiApexModel.Apex5, report[6], report[7] == 1)
                        : null;

                case FlydigiApexModel.Apex4:
                    if (report.Length != 32 || report[15] != Apex4CmdGetInfo) return null;
                    return IsApex4DeviceType(report[3])
                        ? new FlydigiApexIdentity(FlydigiApexModel.Apex4, report[3], report[13] == 1)
                        : null;

                case FlydigiApexModel.Apex6Pro:
                {
                    var frame = Apex6Frame(report);
                    if (frame.Length != Apex6FrameSize || frame[2] != CmdGetInfo || frame[31] != Apex6Checksum(frame))
                        return null;
                    const int payload = 5;
                    byte type = frame[payload];
                    if (!IsApex6ProDeviceType(type)) return null;
                    return new FlydigiApexIdentity(FlydigiApexModel.Apex6Pro, type, frame[payload + 1] == 0)
                    {
                        Features = frame[payload + 24],
                    };
                }
            }
            return null;
        }
    }
}
