using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The ASUS Aura keyboards of OpenRGB's AsusAuraKeyboardController
    /// (#557): the ROG Strix, Azoth, Claymore and Falchion lines and the TUF
    /// Gaming K1, K3, K5 and K7.
    ///
    /// <para>Lights. Each line keeps a map of layouts by region in
    /// AsusAuraKeyboardLayouts.h (<see cref="AsusAuraTables.KeyboardSets"/>),
    /// picked by product ID (RGBController_AsusAuraKeyboard.cpp:421-501).
    /// The region is the keyboard's answer to 12 12, byte 4 times 100 plus
    /// byte 5 (AsusAuraKeyboardController.cpp:155-173), taken as a byte
    /// (RGBController_AsusAuraKeyboard.cpp:505), and its last two digits pick
    /// the layout. A region the map lacks falls back to UK where the
    /// hundreds digit is 2 and to US otherwise (lines 507-517), and a map
    /// that lacks that one too gives the keyboard no lights, as std::map's
    /// operator[] does there (lines 519-534). A Claymore tells its numpad's
    /// place instead, from 40 60 (AsusAuraKeyboardController.cpp:195-211),
    /// which picks one of its three maps, and its region comes from the
    /// device's release number (lines 174-189). Each light's address is its
    /// LED value, and its place is its matrix cell.</para>
    ///
    /// <para>Frames. OpenRGB's Direct mode sends no mode frame on these
    /// keyboards (RGBController_AsusAuraKeyboard.cpp:592-596) and lights
    /// them with UpdateLeds: C0 81, the count, 00, then each LED's value and
    /// color, fifteen to a frame, the input drained before each frame and its
    /// answer awaited for 20 ms after (AsusAuraKeyboardController.cpp:277-311).
    /// The TUF K1 and K5 light five zones and take 00 in place of each value
    /// (lines 32 and 301). The Claymore first takes AllowRemoteControl(1) and
    /// then (3), the frames OpenRGB's Direct mode sends it
    /// (RGBController_AsusAuraKeyboard.cpp:583-596).</para>
    ///
    /// <para>Hand-back. The Claymore takes AllowRemoteControl(0), which
    /// OpenRGB sends after every mode it sets on it (lines 657-662). No
    /// reference returns the other keyboards to their own lighting, so they
    /// show PadForge's last picture until replugged or lit by Armoury
    /// Crate.</para>
    /// </summary>
    internal sealed partial class AsusAuraUsbLighting
    {
        /// <summary>The keyboard PIDs (AsusAuraKeyboardController.h:35-66).</summary>
        internal const int AzothUsb = 0x1A83, Azoth24 = 0x1A85, Claymore = 0x184D, FalchionWired = 0x193C,
            FalchionWireless = 0x193E, StrixFlare = 0x1875, StrixFlarePnk = 0x18CF, StrixFlareCod = 0x18AF,
            StrixFlareII = 0x19FE, StrixFlareIIAnimate = 0x19FC, StrixScope = 0x18F8, StrixScopeTkl = 0x190C,
            StrixScopeTklPnk = 0x1954, StrixScopeRx = 0x1951, StrixScopeRxTklDeluxe = 0x1A05, StrixScopeRxEva = 0x1B12,
            StrixScopeNxTklWhite = 0x19D0, StrixScopeNxWirelessUsb = 0x19F6, StrixScopeNxWireless24 = 0x19F8,
            StrixScopeII = 0x1AB3, StrixScopeIIRx = 0x1AB5, StrixScopeII96Wireless = 0x1AAE,
            StrixScopeII96RxWireless = 0x1B78, TufK1 = 0x1945, TufK3 = 0x194B, TufK3GenII = 0x1B30, TufK5 = 0x1899,
            TufK7 = 0x18AA, TufK3GenIIMiku = 0x1C5E;

        /// <summary>The Claymore's numpad places, GetNumpadLocation's answer
        /// (RGBController_AsusAuraKeyboard.cpp:476-489).</summary>
        internal const int NumpadNone = 0, NumpadRight = 2, NumpadLeft = 3;

        /// <summary>The LEDs of one per-key frame (AsusAuraKeyboardController.cpp:282-291).</summary>
        internal const int LedsPerFrame = 15;

        /// <summary>AwaitResponse's wait after each keyboard frame.</summary>
        internal const int KeyboardAwaitMs = 20;

        /// <summary>The map of layouts a keyboard's product ID picks
        /// (RGBController_AsusAuraKeyboard.cpp:425-501), with the Claymore's
        /// by its numpad.</summary>
        internal static string KeyboardSetOf(int productId, int numpad = NumpadNone) => productId switch
        {
            StrixFlare or StrixFlarePnk or StrixFlareCod => "AsusROGStrixFlareLayouts",
            TufK3 or TufK7 => "AsusTUFK7Layouts",
            StrixScope or StrixScopeRx or StrixScopeRxEva or StrixScopeNxWirelessUsb or StrixScopeNxWireless24
                => "AsusROGStrixScopeLayouts",
            StrixScopeTkl or StrixScopeTklPnk or StrixScopeRxTklDeluxe or StrixScopeNxTklWhite => "AsusROGStrixScopeTKLLayouts",
            StrixScopeII or StrixScopeIIRx => "AsusROGStrixScopeIILayouts",
            StrixScopeII96Wireless => "AsusROGStrixScopeII96WirelessLayouts",
            StrixScopeII96RxWireless => "AsusROGStrixScopeII96RxWirelessLayouts",
            StrixFlareII => "AsusROGStrixFlareIILayouts",
            StrixFlareIIAnimate => "AsusROGStrixFlareIIAnimateLayouts",
            AzothUsb or Azoth24 => "AsusROGAzothLayouts",
            FalchionWired or FalchionWireless => "AsusFalchionLayouts",
            Claymore => numpad switch
            {
                NumpadRight => "AsusClaymoreNumpadRightLayouts",
                NumpadLeft => "AsusClaymoreNumpadLeftLayouts",
                _ => "AsusClaymoreNoNumpadLayouts",
            },
            TufK1 or TufK5 => "AsusTufK1Layouts",
            TufK3GenII or TufK3GenIIMiku => "AsusTUFK3GamingGen2Layouts",
            _ => "AsusTUFK7Layouts",
        };

        /// <summary>Whether a keyboard takes each LED's value in its frame:
        /// all but the TUF K1 and K5 (AsusAuraKeyboardController.cpp:32).</summary>
        internal static bool PerLedKeyboard(int productId) => productId != TufK1 && productId != TufK5;

        /// <summary>The region key a keyboard's answer picks: the answer as a
        /// byte, its last two digits where the map has them, else UK for a
        /// hundreds digit of 2 and US otherwise
        /// (RGBController_AsusAuraKeyboard.cpp:505-517).</summary>
        internal static int RegionOf(AsusKeyboardLayoutSet set, int answer)
        {
            byte layout = (byte)answer;
            if (set?.Find(layout % 100) == null)
                return layout / 100 == 2 ? AsusAuraTables.RegionUk : AsusAuraTables.RegionUs;
            return layout % 100;
        }

        /// <summary>The region a Claymore's release number gives
        /// (AsusAuraKeyboardController.cpp:174-189).</summary>
        internal static int ClaymoreRegion(int release) => (release >> 12) switch
        {
            2 => 204,
            3 => 221,
            _ => 117,
        };

        /// <summary>A keyboard's lights in a region: the layout's LEDs in
        /// OpenRGB's order, each at its LED value and matrix cell. A national
        /// legend OpenRgbKeys does not hold takes the key of the UK layout's
        /// LED of the same value, since the frame addresses an LED by its
        /// value (AsusAuraKeyboardController.cpp:301). Empty where the map
        /// has no layout for the region.</summary>
        internal static LightMap KeyboardMap(int productId, string setName, int region)
        {
            var set = AsusAuraTables.KeyboardSet(setName);
            var layout = set?.Find(region);
            string product = ProductKey(productId, setName + ":" + region);
            if (layout == null) return new LightMap(Array.Empty<LightInfo>(), "OpenRGB " + setName) { Product = product };
            var uk = set.Find(AsusAuraTables.RegionUk);
            var cells = AsusAuraTables.Cells(layout.Matrix, layout.Rows, layout.Cols, layout.Leds.Length);
            // OpenRGB's grid for the Strix Scope TKL numbers another order than its list of LEDs, so
            // the 1 would show on the bottom row. Its names and LEDs agree, and the key picker draws
            // its keys from their usages.
            bool placed = setName != "AsusROGStrixScopeTKLLayouts";
            var lights = new LightInfo[layout.Leds.Length];
            for (int i = 0; i < lights.Length; i++)
            {
                var (name, id) = layout.Leds[i];
                ushort legend = 0;
                if (uk != null && !OpenRgbKeys.TryGet(name, out _, out _))
                    foreach (var (ukName, ukId) in uk.Leds)
                        if (ukId == id && OpenRgbKeys.TryGet(ukName, out ushort usage, out _) && usage != 0)
                        {
                            legend = usage;
                            break;
                        }
                var light = AsusAuraTables.LightOf(name, id, legend);
                var (row, col) = cells[i];
                lights[i] = row < 0 || !placed ? light : light with { X = col, Y = row, Width = 1, Height = 1, HasPosition = true };
            }
            return new LightMap(lights, "OpenRGB " + set.Source) { Product = product };
        }

        private HidLightingModel Keyboard(string name, int productId, int release)
        {
            string set = KeyboardSetOf(productId);
            int region = productId == Claymore
                ? RegionOf(AsusAuraTables.KeyboardSet(set), ClaymoreRegion(release))
                : AsusAuraTables.RegionUs;
            return Model(name, productId, KeyboardMap(productId, set, region),
                (io, wait) => BeginKeyboard(io, productId, release));
        }

        /// <summary>Reads what the keyboard's map needs and puts it in
        /// Direct mode. Null when it did not answer. A Claymore's region
        /// comes from <paramref name="release"/>, the device's release
        /// number (<see cref="HidCollectionInfo.ReleaseNumber"/>).</summary>
        internal static HidLightingSession BeginKeyboard(IHidReportChannel io, int productId, int release = 0)
        {
            if (io == null) return null;
            var scratch = new byte[Math.Max(PacketSize, io.InputReportLength)];
            LightMap map;
            if (productId == Claymore)
            {
                // GetNumpadLocation (AsusAuraKeyboardController.cpp:195-211).
                Clear(io, scratch);
                var numpad = Ask(io, Frame(PacketSize, 0x00, 0x40, 0x60));
                if (numpad == null) return null;
                string set = KeyboardSetOf(productId, (byte)(numpad[5] * 2 + numpad[4]));
                // GetLayout by the release number (lines 174-189).
                map = KeyboardMap(productId, set, RegionOf(AsusAuraTables.KeyboardSet(set), ClaymoreRegion(release)));
                // AllowRemoteControl(1), then (3) for Direct
                // (RGBController_AsusAuraKeyboard.cpp:583-596).
                if (!RemoteControl(io, scratch, 1) || !RemoteControl(io, scratch, 3)) return null;
            }
            else
            {
                string set = KeyboardSetOf(productId);
                Clear(io, scratch);
                var answer = Ask(io, Frame(PacketSize, 0x00, 0x12, 0x12));
                if (answer == null) return null;
                map = KeyboardMap(productId, set, RegionOf(AsusAuraTables.KeyboardSet(set), answer[4] * 100 + answer[5]));
            }
            return new KeyboardSession(map, PerLedKeyboard(productId), productId == Claymore);
        }

        /// <summary>AllowRemoteControl (AsusAuraKeyboardController.cpp:230-242).</summary>
        internal static byte[] RemoteControlFrame(byte type) => Frame(PacketSize, 0x00, 0x41, type);

        private static bool RemoteControl(IHidReportChannel io, byte[] scratch, byte type)
        {
            Clear(io, scratch);
            if (!io.WriteOutput(RemoteControlFrame(type))) return false;
            Await(io, scratch, KeyboardAwaitMs);
            return true;
        }

        /// <summary>UpdateLeds' frames for the LEDs at their values and
        /// colors (AsusAuraKeyboardController.cpp:277-311).</summary>
        internal static List<byte[]> KeyboardFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors, bool perLed)
        {
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            var frames = new List<byte[]>();
            for (int start = 0; start < count; start += LedsPerFrame)
            {
                int leds = Math.Min(LedsPerFrame, count - start);
                var frame = Frame(PacketSize, 0x00, 0xC0, 0x81, (byte)leds, 0x00);
                for (int j = 0; j < leds; j++)
                {
                    frame[j * 4 + 5] = perLed ? (byte)lights[start + j].Address : (byte)0;
                    Put(frame, j * 4 + 6, colors[start + j]);
                }
                frames.Add(frame);
            }
            return frames;
        }

        internal sealed class KeyboardSession : HidLightingSession
        {
            private readonly LightMap _map;
            private readonly bool _perLed;
            private readonly bool _claymore;
            private readonly byte[][] _crash;
            private byte[] _scratch;

            public KeyboardSession(LightMap map, bool perLed, bool claymore)
            {
                _map = map;
                _perLed = perLed;
                _claymore = claymore;
                _crash = claymore ? new[] { RemoteControlFrame(0) } : Array.Empty<byte[]>();
            }

            public override bool Feature => false;

            public override LightMap Map => _map;

            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                _scratch ??= new byte[Math.Max(PacketSize, io.InputReportLength)];
                foreach (var frame in KeyboardFrames(lights, colors, _perLed))
                {
                    Clear(io, _scratch);
                    if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                    Await(io, _scratch, KeyboardAwaitMs);
                }
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_claymore) return true;
                _scratch ??= new byte[Math.Max(PacketSize, io.InputReportLength)];
                return RemoteControl(io, _scratch, 0);
            }
        }
    }
}
