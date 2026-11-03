using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lighting controller of a Lenovo Legion laptop's keyboard, the
    /// ITE controller OpenRGB drives as its Gen 7 and 8 controller (#557).
    /// Everything here is OpenRGB's at the pinned commit: the collection
    /// (Controllers/LenovoControllers/LenovoUSBControllerDetect.cpp:26-30
    /// and 79), the frames (LenovoUSBController_Gen7_8/LenovoUSBController_Gen7_8.cpp
    /// with the opcodes of LenovoUSBController_Gen7_8.h:24-34) and the
    /// lights (LenovoDevices.h). On the owner's Legion Pro 7 16AFR10H, with
    /// Dynamic Lighting off in the BIOS, the collection is 048D:C197 COL02,
    /// usage page 0xFF89 and usage 0x07 with 960-byte feature reports, and
    /// it answered GET_ACTIVE_PROFILE with 07 CA 01 00 05: profile 5, at
    /// byte 4, behind the opcode and the Gen 10 length.
    ///
    /// <para>Only the Gen 10 boards OpenRGB gives a Direct mode are here:
    /// the Legion 7 Gen 10, 0xC197 (LenovoDevices.h:40 and
    /// RGBController_Lenovo_Gen7_8.cpp:319-331, where 0xC195 has none).
    /// Their Direct mode is three frames and writes nothing the device
    /// keeps: D0 01 with the active profile to enter it, A1 with the colors,
    /// and D0 02 with the same profile to leave it
    /// (LenovoUSBController_Gen7_8.cpp:134-199). OpenRGB sends D0 01 ahead
    /// of every A1 on these boards, which "may ignore A1 updates unless D0
    /// is reasserted" (RGBController_Lenovo_Gen7_8.cpp:535-549). OpenRGB
    /// sends on a change alone, though. Direct mode lasts only while frames
    /// keep coming: on the owner's Legion Pro 7 16AFR10H a picture sent
    /// every five seconds showed for a moment and gave way to the stored
    /// effect each time, and D0 01 ahead of every frame of a 60 ms stream
    /// left the keys flickering between the colors and the stored white.
    /// Lenovo Legion Toolkit is the reference that streams. Its Aurora sync,
    /// the same D0 and A1, sends D0 01 once, then the colors every 60 ms for
    /// as long as it lights the keyboard, and D0 02 on the profile active
    /// at that moment to stop, in its original
    /// (SpectrumKeyboardBacklightController.cs:41 and 470-568) and in its
    /// maintained fork that drives Gen 10 (LenovoLegionToolkit-Team, the
    /// same file, lines 28 and 552-633). So here: D0 01 ahead of a
    /// session's first picture, then A1 alone every 60 ms
    /// (<see cref="ReassertMs"/>), and D0 01 again, as OpenRGB's note asks,
    /// only where the keyboard may have left Direct mode: a stream that
    /// paused (<see cref="ReclaimAfterMs"/>), Windows resuming, or the
    /// active profile changed under it (<see cref="RecheckMs"/>).
    /// The boards before Gen 10 enter Direct mode by
    /// writing a profile group into the device's stored profile as well
    /// (RGBController_Lenovo_Gen7_8.cpp:507-515), which a hand-back would
    /// have to restore.</para>
    ///
    /// <para>Windows Dynamic Lighting. The same keyboards also offer a
    /// LampArray interface (048D:C193 on the owner's Legion). With Dynamic
    /// Lighting off in the BIOS that interface takes no colors, while the
    /// signed-in user's switch for it still reads on, and this controller
    /// lights the keys. OpenRGB drives the controller with no regard to
    /// Windows, and Lenovo Legion Toolkit's maintained fork drives it
    /// while Windows Dynamic Lighting is on, warning only that Windows
    /// "might prevent" it (LenovoLegionToolkit-Team
    /// KeyboardBacklightPage.xaml.cs CheckDynamicLightingWarningAsync), so
    /// this path is chosen over that interface
    /// (<see cref="HidLightingModel.LitBesideWindows"/>).</para>
    /// </summary>
    internal sealed class LenovoLegionLighting : IHidLightingFamily
    {
        public static readonly LenovoLegionLighting Instance = new();

        internal const ushort VendorId = 0x048D;
        internal const ushort UsagePage = 0xFF89;
        internal const ushort Usage = 0x07;

        /// <summary>OpenRGB's PACKET_SIZE, the report ID included, and the
        /// feature length the owner's controller reports.</summary>
        internal const int PacketSize = 960;
        internal const byte ReportId = 0x07;
        internal const byte DirectMode = 0xA1;
        internal const byte GetActiveProfile = 0xCA;
        internal const byte SetDirectMode = 0xD0;
        internal const byte DirectOn = 0x01;
        internal const byte DirectOff = 0x02;

        /// <summary>The Legion 7 Gen 10 (LenovoDevices.h:40).</summary>
        internal const ushort Legion7Gen10 = 0xC197;

        /// <summary>The most lights one A1 frame carries: five bytes each
        /// behind the four-byte header, while i + 5 fits the packet
        /// (LenovoUSBController_Gen7_8.cpp:186).</summary>
        internal const int MaxLightsPerFrame = (PacketSize - 4) / 5;

        /// <summary>Lenovo Legion Toolkit repaints these keyboards every
        /// 60 ms while it mirrors the screen
        /// (SpectrumKeyboardBacklightController.cs:41). OpenRGB has no rate
        /// and sends on every change.</summary>
        internal const int RepaintMs = 60;

        /// <summary>How often an unchanged picture goes out again: Lenovo
        /// Legion Toolkit's 60 ms, since Direct mode gives way to the stored
        /// effect soon after the last frame (see the class notes).</summary>
        internal const int ReassertMs = 60;

        /// <summary>A stream that skipped four frames has stopped as far as
        /// the keyboard can tell, so D0 01 goes ahead of the next picture.</summary>
        internal const int ReclaimAfterMs = 4 * ReassertMs;

        /// <summary>How often the active profile is read again. Lenovo
        /// Legion Toolkit learns of a profile key from the keyboard's key
        /// events and restarts its stream on the new profile
        /// (SpectrumKeyboardBacklightController.cs:71-92). PadForge reads
        /// the profile instead, which changes nothing on the keyboard.</summary>
        internal const int RecheckMs = 2000;

        public string Name => "Lenovo Legion";

        private static readonly Lazy<HidLightingModel> s_gen10 = new(() => Model(
            "Lenovo Legion 7 Gen 10", Legion7Gen10, Gen10Lights()));

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId
                || collection.UsagePage != UsagePage || collection.Usage != Usage)
                return null;
            // A collection with shorter feature reports is not the one
            // OpenRGB opens.
            if (collection.FeatureReportLength != 0 && collection.FeatureReportLength < PacketSize) return null;
            return collection.ProductId == Legion7Gen10 ? s_gen10.Value : null;
        }

        private static HidLightingModel Model(string name, ushort productId, IReadOnlyList<LightInfo> lights) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(lights, "OpenRGB LenovoDevices.h")
            {
                Product = $"lenovo:{VendorId:X4}:{productId:X4}",
            },
            LitBesideWindows = true,
            RepaintMs = RepaintMs,
            ReassertMs = ReassertMs,
            ReclaimAfterMs = ReclaimAfterMs,
            RecheckMs = RecheckMs,
            Begin = Begin,
        };

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>The zones OpenRGB gives the Legion 7 Gen 10, in its
        /// order: the keyboard, the neon, the logo and the vents
        /// (RGBController_Lenovo_Gen7_8.cpp:401-463). A light's address is
        /// its zone's ID in the high byte and its own number in the low one
        /// (line 455).</summary>
        internal static IReadOnlyList<LightInfo> Gen10Lights()
        {
            var lights = new List<LightInfo>(Keys.Length + 10 + 1 + 18);
            foreach (var (number, name) in Keys)
            {
                int address = KeyboardZone << 8 | number;
                if (OpenRgbKeys.TryGet(name, out ushort usage, out var label) && usage != 0)
                    lights.Add(new LightInfo(address, LightKind.Key, usage, LightKeySource.Generated));
                else
                    lights.Add(new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = label });
            }
            // "Neon group 1" to 10 (LenovoDevices.h:1857-1869, zone 1974-1985).
            for (int number = 0xF5; number <= 0xFE; number++)
                lights.Add(new LightInfo(NeonZone << 8 | number, LightKind.Edge, 0, LightKeySource.None));
            // "Logo" (LenovoDevices.h:1905-1908, zone 1929-1940).
            lights.Add(new LightInfo(LogoZone << 8 | 0xDD, LightKind.Logo, 0, LightKeySource.None));
            // "Vent group 1" to 18 (LenovoDevices.h:1883-1903, zone 1958-1969).
            for (int number = 0xE9; number <= 0xFA; number++)
                lights.Add(new LightInfo(VentZone << 8 | number, LightKind.Edge, 0, LightKeySource.None));
            return lights;
        }

        /// <summary>The zone IDs (LenovoDevices.h:1913-1985).</summary>
        internal const int KeyboardZone = 0;
        internal const int NeonZone = 1;
        internal const int VentZone = 3;
        internal const int LogoZone = 5;

        /// <summary>The keys of legion7_gen7and8_ansi_leds, in its order,
        /// copied from LenovoDevices.h:1739-1855 with each KEY_EN_ name
        /// written as its string from RGBControllerKeyNames.cpp.</summary>
        internal static readonly (byte Number, string Name)[] Keys =
        {
            (0x01, "Key: Escape"),
            (0x02, "Key: F1"),
            (0x03, "Key: F2"),
            (0x04, "Key: F3"),
            (0x05, "Key: F4"),
            (0x06, "Key: F5"),
            (0x07, "Key: F6"),
            (0x08, "Key: F7"),
            (0x09, "Key: F8"),
            (0x0A, "Key: F9"),
            (0x0B, "Key: F10"),
            (0x0C, "Key: F11"),
            (0x0D, "Key: F12"),
            (0x0E, "Key: Insert"),
            (0x0F, "Key: Print Screen"),
            (0x10, "Key: Delete"),
            (0x11, "Key: Home"),
            (0x12, "Key: End"),
            (0x13, "Key: Page Up"),
            (0x14, "Key: Page Down"),
            (0x16, "Key: `"),
            (0x17, "Key: 1"),
            (0x18, "Key: 2"),
            (0x19, "Key: 3"),
            (0x1A, "Key: 4"),
            (0x1B, "Key: 5"),
            (0x1C, "Key: 6"),
            (0x1D, "Key: 7"),
            (0x1E, "Key: 8"),
            (0x1F, "Key: 9"),
            (0x20, "Key: 0"),
            (0x21, "Key: -"),
            (0x22, "Key: ="),
            (0x38, "Key: Backspace"),
            (0x26, "Key: Num Lock"),
            (0x27, "Key: Number Pad /"),
            (0x28, "Key: Number Pad *"),
            (0x29, "Key: Number Pad -"),
            (0x40, "Key: Tab"),
            (0x42, "Key: Q"),
            (0x43, "Key: W"),
            (0x44, "Key: E"),
            (0x45, "Key: R"),
            (0x46, "Key: T"),
            (0x47, "Key: Y"),
            (0x48, "Key: U"),
            (0x49, "Key: I"),
            (0x4A, "Key: O"),
            (0x4B, "Key: P"),
            (0x4C, "Key: ["),
            (0x4D, "Key: ]"),
            (0x4E, "Key: \\ (ANSI)"),
            (0x4F, "Key: Number Pad 7"),
            (0x50, "Key: Number Pad 8"),
            (0x51, "Key: Number Pad 9"),
            (0x55, "Key: Caps Lock"),
            (0x6D, "Key: A"),
            (0x6E, "Key: S"),
            (0x58, "Key: D"),
            (0x59, "Key: F"),
            (0x5A, "Key: G"),
            (0x71, "Key: H"),
            (0x72, "Key: J"),
            (0x5B, "Key: K"),
            (0x5C, "Key: L"),
            (0x5D, "Key: ;"),
            (0x5F, "Key: '"),
            (0x77, "Key: Enter"),
            (0x79, "Key: Number Pad 4"),
            (0x7B, "Key: Number Pad 5"),
            (0x7C, "Key: Number Pad 6"),
            (0x68, "Key: Number Pad +"),
            (0x6A, "Key: Left Shift"),
            (0x82, "Key: Z"),
            (0x83, "Key: X"),
            (0x6F, "Key: C"),
            (0x70, "Key: V"),
            (0x87, "Key: B"),
            (0x88, "Key: N"),
            (0x73, "Key: M"),
            (0x74, "Key: ,"),
            (0x75, "Key: ."),
            (0x76, "Key: /"),
            (0x8D, "Key: Right Shift"),
            (0x8E, "Key: Number Pad 1"),
            (0x90, "Key: Number Pad 2"),
            (0x92, "Key: Number Pad 3"),
            (0x7F, "Key: Left Control"),
            (0x80, "Key: Left Fn"),
            (0x96, "Key: Left Windows"),
            (0x97, "Key: Left Alt"),
            (0x98, "Key: Space"),
            (0x9A, "Key: Right Alt"),
            (0x9B, "Key: Right Control"),
            (0x9D, "Key: Up Arrow"),
            (0xA3, "Key: Number Pad 0"),
            (0xA5, "Key: Number Pad ."),
            (0xA7, "Key: Number Pad Enter"),
            (0x9C, "Key: Left Arrow"),
            (0x9F, "Key: Down Arrow"),
            (0xA1, "Key: Right Arrow"),
        };

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>A Gen 10 frame: the report ID, the opcode, the payload
        /// length in bytes 2 and 3, then the payload, padded to the packet
        /// (SetGen10PayloadLength, LenovoUSBController_Gen7_8.cpp:18-27).</summary>
        internal static byte[] Frame(byte opcode, ReadOnlySpan<byte> payload)
        {
            var frame = new byte[PacketSize];
            frame[0] = ReportId;
            frame[1] = opcode;
            frame[2] = (byte)(payload.Length & 0xFF);
            frame[3] = (byte)(payload.Length >> 8);
            payload.CopyTo(frame.AsSpan(4));
            return frame;
        }

        /// <summary>GET_ACTIVE_PROFILE, whose answer carries the profile in
        /// byte 4 (LenovoUSBController_Gen7_8.cpp:230-238).</summary>
        internal static byte[] ActiveProfileQuery() => Frame(GetActiveProfile, stackalloc byte[1]);

        /// <summary>SET_DIRECT_MODE on or off for a profile
        /// (LenovoUSBController_Gen7_8.cpp:134-166).</summary>
        internal static byte[] Direct(bool on, byte profile)
            => Frame(SetDirectMode, stackalloc byte[] { on ? DirectOn : DirectOff, profile });

        /// <summary>DIRECT_MODE with each light's address, low byte first,
        /// and its color (LenovoUSBController_Gen7_8.cpp:168-199).</summary>
        internal static byte[] Colors(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            int count = Math.Min(Math.Min(lights.Count, colors.Count), MaxLightsPerFrame);
            var payload = new byte[count * 5];
            for (int i = 0; i < count; i++)
            {
                int address = lights[i].Address;
                int color = colors[i];
                int at = i * 5;
                payload[at] = (byte)(address & 0xFF);
                payload[at + 1] = (byte)(address >> 8);
                payload[at + 2] = (byte)(color >> 16);
                payload[at + 3] = (byte)(color >> 8);
                payload[at + 4] = (byte)color;
            }
            return Frame(DirectMode, payload);
        }

        /// <summary>The profile in an answer to <see cref="ActiveProfileQuery"/>,
        /// or null when the answer is not one: OpenRGB reads byte 4 of any
        /// answer longer than four bytes, and here the answer must also
        /// carry the query's opcode, since Direct mode is entered and left
        /// on the profile read, and a profile read wrong would leave the
        /// keyboard on another one.</summary>
        internal static byte? ProfileOf(byte[] answer)
            => answer != null && answer.Length > 4 && answer[0] == ReportId && answer[1] == GetActiveProfile
                ? answer[4]
                : null;

        private static HidLightingSession Begin(IHidReportChannel io)
            => ReadProfile(io, out byte? profile) && profile is byte read ? new Session(read) : null;

        /// <summary>Asks for the active profile. False when the exchange
        /// failed. <paramref name="profile"/> is null for an answer that is
        /// not one (<see cref="ProfileOf"/>).</summary>
        private static bool ReadProfile(IHidReportChannel io, out byte? profile)
        {
            profile = null;
            if (io == null) return false;
            var answer = new byte[Math.Max(PacketSize, io.FeatureReportLength)];
            if (!io.SetFeature(ActiveProfileQuery()) || !io.GetFeature(ReportId, answer)) return false;
            profile = ProfileOf(answer);
            return true;
        }

        /// <summary>A session on the keyboard's active profile, which it
        /// follows when the profile changes.</summary>
        internal sealed class Session : HidLightingSession
        {
            private byte _profile;

            public Session(byte profile) => _profile = profile;

            public byte Profile => _profile;

            public override bool Feature => true;

            public override byte[][] Claim() => new[] { Direct(on: true, _profile) };

            public override byte[][] Paint(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => new[] { Colors(lights, colors) };

            public override byte[][] HandBack() => new[] { Direct(on: false, _profile) };

            /// <summary>A profile key moved the keyboard to another profile:
            /// Direct mode is entered again on it, and the hand-back leaves
            /// it there. An answer that is not one keeps the profile.</summary>
            public override bool Recheck(IHidReportChannel io, out bool reclaim)
            {
                reclaim = false;
                if (!ReadProfile(io, out byte? profile)) return false;
                if (profile is byte read && read != _profile)
                {
                    _profile = read;
                    reclaim = true;
                }
                return true;
            }
        }
    }
}
