using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Pure decoder for 3Dconnexion SpaceMouse HID input reports (#288).
    ///
    /// The wire contract, grounded against three references:
    ///
    /// - spacemouse-AndunHH/SpaceNavigator.md:38-44 and :70-104 (protocol notes +
    ///   full descriptor dump): report ID 1 carries TRANSLATION (three int16 LE at
    ///   bytes 1-6, logical max 350), report ID 2 carries ROTATION (same layout),
    ///   report ID 3 carries buttons. Reports alternate on an 8 ms cadence, and
    ///   after the last non-zero motion each axis triplet is sent as zeros three
    ///   times (the return-to-centre signal).
    /// - hid.spacemouse/index.js:3-12 (joinInt16 sign extension), :16-19
    ///   (PERSISTENT translate/rotate fields; each report updates only its own
    ///   three axes), :26-47 (byte layout, buttons as 6 bytes x 8 bits).
    /// - pyspacemouse/pyspacemouse/devices.toml + device.py:211-232: modern
    ///   0x256F devices (SpaceMouse Wireless C62E/C63A, Pro Wireless C632/C638,
    ///   Enterprise C633, Universal Receiver C652) COMBINE all six axes into
    ///   report ID 1: translation at bytes 1-6, rotation at bytes 7-12. Legacy
    ///   0x046D devices and the wired 0x256F units (C635 Compact, C641) split
    ///   across reports 1/2. device.py's decode confirms byte indexes are
    ///   absolute into the buffer with data[0] = report ID.
    ///
    /// THE TRAP this class exists to encode (recipe plan-281): alternating
    /// reports are a frame-assembly problem. A reader that treats each report as
    /// a complete device state publishes translation with stale rotation and vice
    /// versa at 125 Hz. So the six axes are persistent fields; each report
    /// updates only the axes it carries, and the consumer reads the assembly.
    /// The trailing zero triplets are genuine centre returns, never coalesced.
    ///
    /// Whether report 1 carries six axes (combined) or three (split) is decided
    /// ONCE per device from the HID descriptor (does report 1 define usages
    /// Rx/Ry/Rz), never from per-report byte counts: Windows HIDClass pads every
    /// read to the interface's InputReportByteLength, so a split device whose
    /// longest report exceeds 7 bytes would present a translation report with
    /// zero-padded tail bytes that a length heuristic would misread as rotation.
    /// </summary>
    public sealed class SpaceMouseDecoder
    {
        /// <summary>Descriptor logical maximum for every axis, shared across the
        /// whole family (AndunHH descriptor dump; pyspacemouse axis_scale = 350.0
        /// for all thirteen devices in devices.toml).</summary>
        public const int LogicalMax = 350;

        /// <summary>Buttons arrive as up to 6 bytes x 8 bits on report 3
        /// (hid.spacemouse/index.js:39-47).</summary>
        public const int MaxButtons = 48;

        /// <summary>True when report ID 1 carries all six axes (rotation at bytes
        /// 7-12). Set from the descriptor at open time.</summary>
        public bool CombinedReport { get; }

        // Persistent assembled state (hid.spacemouse/index.js:16-19 pattern).
        public short TranslateX { get; private set; }
        public short TranslateY { get; private set; }
        public short TranslateZ { get; private set; }
        public short RotateX { get; private set; }   // pitch
        public short RotateY { get; private set; }   // roll
        public short RotateZ { get; private set; }   // yaw

        private readonly bool[] _buttons = new bool[MaxButtons];

        public SpaceMouseDecoder(bool combinedReport) => CombinedReport = combinedReport;

        public bool GetButton(int index)
            => index >= 0 && index < MaxButtons && _buttons[index];

        /// <summary>Little-endian int16 with sign extension
        /// (hid.spacemouse/index.js:3-12 joinInt16; pyspacemouse device.py:30-35
        /// _to_int16).</summary>
        private static short Int16At(byte[] b, int lo) => (short)(b[lo] | (b[lo + 1] << 8));

        /// <summary>
        /// Feed one HID input report (report ID at index 0). Updates only the
        /// state the report carries; everything else persists. Returns true when
        /// the report was recognized and applied.
        /// </summary>
        public bool Process(byte[] report, int length)
        {
            if (report == null || length < 2) return false;
            switch (report[0])
            {
                case 1:
                    if (length < 7) return false;
                    TranslateX = Int16At(report, 1);
                    TranslateY = Int16At(report, 3);
                    TranslateZ = Int16At(report, 5);
                    if (CombinedReport && length >= 13)
                    {
                        RotateX = Int16At(report, 7);
                        RotateY = Int16At(report, 9);
                        RotateZ = Int16At(report, 11);
                    }
                    return true;

                case 2:
                    // Rotation rides report 2 on split-shape devices. Combined
                    // devices never define one in their descriptors (pyspacemouse
                    // maps no axis to channel 2 on any combined device), but if a
                    // firmware emitted it anyway the family contract still says
                    // "three rotation int16s", so applying it is the honest read.
                    if (length < 7) return false;
                    RotateX = Int16At(report, 1);
                    RotateY = Int16At(report, 3);
                    RotateZ = Int16At(report, 5);
                    return true;

                case 3:
                    // 8 buttons per payload byte, LSB first, up to 6 bytes
                    // (hid.spacemouse/index.js:39-47). Bytes beyond the report's
                    // actual payload keep their prior state: Windows pads reads
                    // to InputReportByteLength with zeros, and clearing buttons
                    // from padding would release held keys, so only the bytes the
                    // device really defines are trusted. The device re-sends
                    // report 3 on every button change, so a shorter-than-6-byte
                    // device clears its own buttons through real payload bytes.
                    {
                        int payload = Math.Min(length - 1, 6);
                        for (int i = 0; i < payload; i++)
                        {
                            byte bits = report[1 + i];
                            for (int bit = 0; bit < 8; bit++)
                                _buttons[i * 8 + bit] = (bits & (1 << bit)) != 0;
                        }
                        return true;
                    }

                default:
                    return false;
            }
        }

        /// <summary>Scale a raw axis value (logical range -350..350) to the full
        /// SDL axis range, clamped. 0 maps to exactly 0, preserving the sprung
        /// puck's true centre for downstream deadzones.</summary>
        public static short ToSdlAxis(short raw)
        {
            int v = raw * 32767 / LogicalMax;
            if (v > 32767) v = 32767;
            if (v < -32768) v = -32768;
            return (short)v;
        }
    }
}
