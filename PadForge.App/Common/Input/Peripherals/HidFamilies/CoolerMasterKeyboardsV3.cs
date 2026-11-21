using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Cooler Master's V3 keyboards, as OpenRGB's CMKeyboardV3Controller lights
    /// them (e4af57cd): the MK721, the CK721 and the CK721 LINE, each wired and
    /// behind its dongle, with the CK721's layout (CMKeyboardDevices.cpp:271-283
    /// and 724-749). The wired CK721 keeps the live mode of SignalRGB's plugin
    /// (CoolerMaster_CK721_Keyboard.js, PluginDevicesBeta.cs), which
    /// OpenRGB's V3 controller lacks: "Direct is not supported, uses Custom
    /// instead" (RGBController_CMKeyboardController.cpp:26). The plugin names no
    /// other V3 product.
    ///
    /// <para>The lights. OpenRGB's layout places the keys of a 60% board and
    /// moves the rest in with swaps, and a swap only takes a place a key holds,
    /// so its KeyboardLayoutManager builds no Up, Down, Left or Right Arrow and no
    /// Page Down, and puts Escape where Tab is (ChangeKeys with
    /// KEYBOARD_OPCODE_SWAP_ONLY, the edits at lines 738-747, as klm_dump.tsv
    /// holds them). The plugin's lists place every key at the same values
    /// OpenRGB's keymap gives them, Tab at 15 and the arrows at 56, 64, 67 and 70
    /// (vLedNames and vLeds, the edits' own values), so a V3 keyboard takes the
    /// plugin's keys and, at the one value they leave out, OpenRGB's wheel light,
    /// 14 (line 739).</para>
    ///
    /// <para>Every command is a run of output reports, report ID 0, the command,
    /// 80 and the count on the first and the report's number on the rest, then
    /// the payload: 62 bytes in 65 on a wired keyboard, 20 in 33 behind a dongle,
    /// 3 ms after each report wired and 20 ms behind a dongle, which drops reports
    /// sent closer (_SendCommand, CMKeyboardV3Controller.cpp:166-201, the sizes at
    /// CMKeyboardV3Controller.h:18-22). Nothing is read after a write.</para>
    ///
    /// <para>A session opens as OpenRGB's SetMode takes the Custom mode
    /// (lines 208-327): it reads away what input waits and reads the active
    /// profile, register 0324, from the answer's byte 6 when the answer is a
    /// read's and names one of the four profiles, else profile 0
    /// (_ReadActiveProfile, lines 118-139). Then it writes the profile's effect
    /// register, 0xC8 past the start of the profile's 0xC9 bytes, to 04, Custom,
    /// and Custom's ten bytes at 4 times 10 past that start: brightness FF twice,
    /// speed 2 with direction 1 in the high half, as OpenRGB makes them of
    /// Custom's speed 3 and direction left, no colors, and color index 06
    /// (lines 105-108 and 315-326, Custom's row at line 335). OpenRGB reads the
    /// profile in its constructor and again in SetMode, and keeps the first when
    /// the second goes unanswered. A session reads it once.</para>
    ///
    /// <para>A picture is WRITE_CUSTOM_RGB, 0A, at register 0: the address, the
    /// length 284, then red, green, blue and FF for each of the 71 LEDs by its
    /// value (SetLeds, lines 66-99). OpenRGB's page says the family saves on
    /// every update ("@save :robot:", RGBController_CMKeyboardController.cpp:18),
    /// the profile's effects are stored (line 111), and no reference sends this
    /// write as a stream, so each picture is taken to land in the keyboard's
    /// memory and pictures are held and spaced as the Lenovo Gen 10 keyboard's
    /// stored writes are (<see cref="LenovoLegionLighting"/>).</para>
    ///
    /// <para>Hand-back. OpenRGB's Shutdown writes nothing (lines 57-60), and no
    /// reference gives these keyboards back their own effect: the hand-back sends
    /// nothing and the keyboard keeps the Custom mode and the last picture.</para>
    /// </summary>
    internal sealed partial class CoolerMasterLighting
    {
        // The product IDs of CMKeyboardDevices.h:98-107 at 6b681e3c.
        internal const ushort Ck721Dongle = 0x0183;
        internal const ushort Ck721Line = 0x01EE;
        internal const ushort Ck721LineDongle = 0x01F2;
        internal const ushort Mk721 = 0x016F;
        internal const ushort Mk721Dongle = 0x01C3;

        /// <summary>The write and payload sizes, the dongle's wait and the read's
        /// bound (CMKeyboardV3Controller.h:18-23), and the wired wait,
        /// CM_KEYBOARD_TIMEOUT_SHORT (CMKeyboardAbstractController.h:30).</summary>
        internal const int V3WiredWriteSize = 65, V3WiredPayload = 62;
        internal const int V3DongleWriteSize = 33, V3DonglePayload = 20;
        internal const int V3DongleWaitMs = 20, V3WiredWaitMs = 3, V3ReadMs = 200;

        /// <summary>CM_KEYBOARD_V3_LED_COUNT, the profiles' size, their count, the
        /// active profile's register, an effect's size and the effect register
        /// (lines 24-29).</summary>
        internal const int V3LedCount = 71;
        internal const int V3ProfileSize = 0xC9, V3ProfileCount = 4, V3ProfileRegister = 0x324;
        internal const int V3EffectSize = 10, V3EffectModeRegister = 0xC8;

        /// <summary>The commands and the Custom effect (lines 31-44).</summary>
        internal const byte V3WriteRegister = 0x01, V3ReadRegister = 0x02, V3WriteCustomRgb = 0x0A, V3Custom = 0x04;

        /// <summary>The most reports a session reads away before its profile read.
        /// OpenRGB reads until none waits (line 122), so the bound is PadForge's.</summary>
        internal const int V3MaxClearReads = 64;

        /// <summary>The keyboards DetectCoolerMasterV3Keyboards makes a controller
        /// for (CoolerMasterControllerDetect.cpp:231-263).</summary>
        internal static readonly ushort[] V3Keyboards = { Ck721, Ck721Dongle, Ck721Line, Ck721LineDongle, Mk721, Mk721Dongle };

        internal static bool IsV3Dongle(ushort productId) => productId is Ck721Dongle or Ck721LineDongle or Mk721Dongle;

        private static HidLightingModel V3KeyboardModel(string name, ushort productId)
        {
            if (productId == Ck721 || Array.IndexOf(V3Keyboards, productId) < 0) return null;
            var lights = V3KeyboardLights(productId);
            if (lights == null) return null;
            bool dongle = IsV3Dongle(productId);
            return NewModel(name, productId, lights, "SignalRGB CoolerMaster_CK721_Keyboard.js and OpenRGB CMKeyboardDevices.cpp",
                (io, wait) => BeginV3Keyboard(io, wait, dongle),
                repaintMs: LenovoLegionLighting.RepaintMs, holdMs: LenovoLegionLighting.HoldMs);
        }

        /// <summary>The plugin's keys, then each light of OpenRGB's layout at a value
        /// the plugin leaves out. Null for a product with no layout.</summary>
        internal static IReadOnlyList<LightInfo> V3KeyboardLights(ushort productId)
        {
            var openRgb = KeyboardLights(productId, KeyboardLayout.AnsiQwerty);
            if (openRgb == null) return null;
            var lights = new List<LightInfo>(PluginKeyNames.Lights(PluginKeyTables.Ck721));
            var taken = new HashSet<int>();
            foreach (var light in lights) taken.Add(light.Address);
            foreach (var light in openRgb)
                if (taken.Add(light.Address)) lights.Add(light);
            return lights;
        }

        /// <summary>_SendCommand's reports for a command and its data.</summary>
        internal static List<byte[]> V3Frames(bool dongle, byte command, IReadOnlyList<byte> data)
        {
            int size = dongle ? V3DongleWriteSize : V3WiredWriteSize;
            int payload = dongle ? V3DonglePayload : V3WiredPayload;
            int count = (data.Count + payload - 1) / payload;
            var frames = new List<byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                var frame = new byte[size];
                frame[1] = command;
                frame[2] = i == 0 ? (byte)(0x80 | count) : (byte)i;
                for (int j = 0; j < payload && i * payload + j < data.Count; j++)
                    frame[3 + j] = data[i * payload + j];
                frames.Add(frame);
            }
            return frames;
        }

        /// <summary>_WriteRegister (lines 146-157): the address and the length, low
        /// byte first, then the data.</summary>
        internal static List<byte[]> V3Register(bool dongle, int address, params byte[] data)
        {
            var buf = new List<byte>(4 + data.Length)
            {
                (byte)address, (byte)(address >> 8), (byte)data.Length, (byte)(data.Length >> 8),
            };
            buf.AddRange(data);
            return V3Frames(dongle, V3WriteRegister, buf);
        }

        /// <summary>The active profile's read (line 126).</summary>
        internal static byte[] V3ProfileQuery(bool dongle)
            => V3Frames(dongle, V3ReadRegister, new byte[] { V3ProfileRegister & 0xFF, V3ProfileRegister >> 8, 0x01, 0x00 })[0];

        /// <summary>SetMode's writes for the Custom mode on a profile
        /// (lines 303-326).</summary>
        internal static List<byte[]> V3CustomMode(bool dongle, int profile)
        {
            int start = profile * V3ProfileSize;
            var frames = V3Register(dongle, start + V3EffectModeRegister, V3Custom);
            frames.AddRange(V3Register(dongle, start + V3Custom * V3EffectSize,
                0xFF, 0xFF, 0x02 | 0x01 << 4, 0, 0, 0, 0, 0, 0, 0x06));
            return frames;
        }

        /// <summary>SetLeds's reports (lines 66-99).</summary>
        internal static List<byte[]> V3PictureFrames(bool dongle, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var map = ByAddress(lights, colors, V3LedCount);
            const int length = V3LedCount * 4;
            var data = new byte[4 + length];
            data[2] = length & 0xFF;
            data[3] = length >> 8;
            for (int i = 0; i < V3LedCount; i++)
            {
                data[4 + i * 4] = R(map[i]);
                data[5 + i * 4] = G(map[i]);
                data[6 + i * 4] = B(map[i]);
                data[7 + i * 4] = 0xFF;
            }
            return V3Frames(dongle, V3WriteCustomRgb, data);
        }

        /// <summary>The profile in an answer to <see cref="V3ProfileQuery"/>, as
        /// hidapi hands it over, or null for no read's answer or a profile past the
        /// four (lines 128-131).</summary>
        internal static byte? V3ProfileOf(byte[] answer)
            => answer != null && answer.Length > 6 && answer[0] == V3ReadRegister && answer[6] < V3ProfileCount ? answer[6] : null;

        /// <summary>Writes each report with the wait after it. False when one failed.</summary>
        private static bool V3Send(IHidReportChannel io, Action<int> wait, IEnumerable<byte[]> frames, bool dongle)
        {
            foreach (var frame in frames)
            {
                if (!io.WriteOutput(frame)) return false;
                wait?.Invoke(dongle ? V3DongleWaitMs : V3WiredWaitMs);
            }
            return true;
        }

        private static HidLightingSession BeginV3Keyboard(IHidReportChannel io, Action<int> wait, bool dongle)
        {
            if (io == null) return null;
            var scratch = new byte[Math.Max(V3WiredWriteSize, io.InputReportLength)];
            for (int i = 0; i < V3MaxClearReads && io.ReadInput(scratch, 0) > 0; i++) { }
            if (!V3Send(io, wait, new[] { V3ProfileQuery(dongle) }, dongle)) return null;
            int profile = V3ProfileOf(Read(io, V3ReadMs)) ?? 0;
            if (!V3Send(io, wait, V3CustomMode(dongle, profile), dongle)) return null;
            return new V3KeyboardSession(dongle);
        }

        internal sealed class V3KeyboardSession : Session
        {
            private readonly bool _dongle;

            public V3KeyboardSession(bool dongle) => _dongle = dongle;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => V3Send(io, wait, V3PictureFrames(_dongle, lights, colors), _dongle)
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
        }
    }
}
