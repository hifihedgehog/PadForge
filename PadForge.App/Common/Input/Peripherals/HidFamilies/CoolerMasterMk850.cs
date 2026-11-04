using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Cooler Master MK850 (#557). OpenRGB registers it and lists its keys,
    /// taken from SignalRGB's plugin ("[signal]", CMKeyboardDevices.h:108), and
    /// its V2 detector makes no controller for it, so no OpenRGB frame has
    /// reached this keyboard. The frames here are those of the plugin
    /// (signal-plugins CoolerMaster_Mk850_Keyboard.js), the one source that drives it.
    ///
    /// <para>They differ from OpenRGB's V2 frames in where the colors start.
    /// OpenRGB's first report (56 83 00) carries the first 40 bytes of colors
    /// and report 1 starts at byte 40. The plugin leaves its first report out
    /// (commented out on lines 96-99), and report 1 starts at LED 0.</para>
    ///
    /// <para>A session starts as the plugin's Initialize does, on the first
    /// picture: profile 5 (51 00 00 00 05) and the commit (51 28 00 00 FF). A
    /// picture is reports 56 83 n 00 of 60 color bytes, each followed by a
    /// read, and the commit followed by 3 ms. The plugin stops at report 8,
    /// which leaves the keys at LEDs 160 to 164 dark, the Aim + key and the
    /// number pad's minus, plus and Enter. PadForge sends report 9 as well, which
    /// the family's other keyboards take (CMKeyboardV2Controller.cpp:258) and
    /// which the plugin's SK650 sends. No source has sent it to an MK850.</para>
    ///
    /// <para>The keys. OpenRGB's layout for the MK850 holds the plugin's LED
    /// numbers. Two differ from the plugin, which PadForge follows since it
    /// drives the keyboard: Num Lock is LED 140 (the plugin's note on line 1 of
    /// its table), where OpenRGB holds a placeholder 254, and the macro keys count
    /// M1 to M5 from the top, where OpenRGB counts from M5. No source knows the
    /// LEDs of the two ISO keys, so an ISO board shows its ANSI keys.</para>
    ///
    /// <para>The hand-back. The plugin's Shutdown takes direct control (41 80)
    /// and sets effect 1. It reads nothing back, so a keyboard on any other effect
    /// comes back on that one. A session here first reads the active profile
    /// (52 00) and effect (52 28) the family's firmware answers
    /// (CMKeyboardAbstractController.cpp:148-158 and GetModeStatus, libcmmk.c:498-509
    /// and 613-625). It hands back the profile, 41 80, the effect (the plugin's
    /// effect 1 when the read gave nothing, or gave the Direct effect FF an earlier
    /// run left), and 41 00, firmware control, which ends the family's other
    /// hand-backs. Nothing is saved.</para>
    /// </summary>
    internal sealed partial class CoolerMasterLighting
    {
        /// <summary>The Direct effect the commit selects, and the profile the
        /// plugin selects first.</summary>
        internal const byte Mk850DirectEffect = 0xFF;
        internal const byte Mk850Profile = 0x05;

        /// <summary>The effect the plugin's Shutdown sets.</summary>
        internal const byte Mk850OwnEffect = 0x01;

        /// <summary>LEDs of the plugin's Num Lock note and OpenRGB's placeholder.</summary>
        internal const int Mk850NumLockLed = 140;
        private const int Mk850NumLockPlaceholder = 254;

        /// <summary>Reports 1 to 9 of 60 color bytes.</summary>
        internal const int Mk850Reports = 9;
        internal const int Mk850Slots = Mk850Reports * 60 / 3;

        /// <summary>The wait after the commit (SendCommits, line 133).</summary>
        internal const int Mk850CommitMs = 3;

        private static HidLightingModel Mk850Model(string name)
        {
            var lights = Mk850Lights();
            return lights == null ? null : NewModel(name, Mk850, lights, "OpenRGB CMKeyboardDevices.cpp, SignalRGB",
                (io, _) => BeginMk850(io));
        }

        /// <summary>OpenRGB's ANSI layout, with the plugin's Num Lock LED and
        /// macro key numbers. A macro key's LED is its place from the top, 0 to 4.</summary>
        internal static IReadOnlyList<LightInfo> Mk850Lights()
        {
            var lights = KeyboardLights(Mk850, KeyboardLayout.AnsiQwerty);
            return lights?.Select(light => light switch
            {
                { Address: Mk850NumLockPlaceholder } => light with { Address = Mk850NumLockLed },
                { Label: LightLabel.MacroKey } => light with { LabelNumber = light.Address + 1 },
                _ => light,
            }).ToArray();
        }

        /// <summary>A read of the profile or effect, the value at byte 4 when
        /// the answer carries the request's second byte at byte 1.</summary>
        internal static byte? Mk850ValueOf(byte[] answer, byte request)
            => answer != null && answer.Length > 4 && answer[1] == request ? answer[4] : null;

        private static HidLightingSession BeginMk850(IHidReportChannel io)
        {
            if (io == null) return null;
            byte? profile = Mk850ValueOf(Exchange(io, ActiveProfileQuery(), required: false), 0x00);
            byte? effect = Mk850ValueOf(Exchange(io, Packet(stackalloc byte[] { 0x52, 0x28 }), required: false), 0x28);
            return new Mk850Session(profile, effect);
        }

        internal sealed class Mk850Session : Session
        {
            private bool _started;

            public Mk850Session(byte? profile, byte? effect)
            {
                var handBack = new List<byte[]>(4);
                if (profile is byte p) handBack.Add(ActiveProfile(p));
                handBack.Add(ControlMode(0x80));
                byte own = effect is byte e && e != Mk850DirectEffect ? e : Mk850OwnEffect;
                handBack.Add(Packet(stackalloc byte[] { 0x51, 0x28, 0x00, 0x00, own }));
                handBack.Add(ControlMode(FirmwareControl));
                SetHandBack(handBack.ToArray());
            }

            /// <summary>Reports 1 to 9 of a picture, colors by LED.</summary>
            internal static List<byte[]> ColorReports(int[] map)
            {
                var reports = new List<byte[]>(Mk850Reports);
                for (int number = 1; number <= Mk850Reports; number++)
                {
                    var frame = Packet(new byte[] { 0x56, 0x83, (byte)number, 0x00 });
                    int at = 5;
                    for (int led = (number - 1) * 20; led < number * 20; led++)
                    {
                        frame[at++] = R(map[led]);
                        frame[at++] = G(map[led]);
                        frame[at++] = B(map[led]);
                    }
                    reports.Add(frame);
                }
                return reports;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                Owe();
                if (!_started)
                {
                    _started = true;
                    if (Exchange(io, ActiveProfile(Mk850Profile), required: false, Mk850CommitMs) == null) return HidLightingPaint.Failed;
                    if (Exchange(io, V2Apply(), required: false, Mk850CommitMs) == null) return HidLightingPaint.Failed;
                }
                if (!ExchangeAll(io, ColorReports(ByAddress(lights, colors, Mk850Slots)), required: false)) return HidLightingPaint.Failed;
                return Exchange(io, V2Apply(), required: false, Mk850CommitMs) == null ? HidLightingPaint.Failed : HidLightingPaint.Sent;
            }
        }
    }
}
