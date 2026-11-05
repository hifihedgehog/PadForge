using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Lenovo Legion K510 Mini Pro keyboard (#557), as OpenRGB's
    /// LenovoK510Controller drives it: 64-byte output reports with ID 4 on
    /// interface 1, usage page 0xFF1C and usage 0x92
    /// (LenovoK510ControllerDetect.cpp:19-23 and 43). One light: OpenRGB
    /// colors the keyboard as one zone (RGBController_LenovoK510.cpp:283-297).
    ///
    /// <para>The model of the device. A mode report sets the mode, the
    /// brightness, the speed, the direction and one color, and the keyboard
    /// keeps it ("@save :robot:", RGBController_LenovoK510.cpp:18), so every
    /// picture lands in its memory. A state request, 05 38 in bytes 3 and
    /// 4, makes the keyboard answer with the stored mode in bytes 9 to 16
    /// of an input report (GetCurrentState, LenovoK510Controller.cpp:77-101),
    /// and changes nothing. So a session reads the stored mode first, saves
    /// it in the settings file (<see cref="KeyboardProfileBackups"/>,
    /// profile 0) before its first picture, and its hand-back writes the
    /// stored mode back as a mode report. A run that ends without a
    /// hand-back leaves the backup, and the next run writes it back.</para>
    ///
    /// <para>The picture: the Static mode, 0x06, OpenRGB's "Normal", with
    /// its color in bytes 14 to 16 and color byte 13 at 0, brightness 2,
    /// speed 0, and byte 12 at 1 for OpenRGB's default direction, left
    /// (SetMode, LenovoK510Controller.cpp:40-75; K510_MODE_NORMAL and
    /// K510_BRIGHTNESS_DEFAULT, LenovoK510Controller.h:20-36;
    /// MODE_DIRECTION_LEFT 0, RGBControllerInterface.h:84). A picture goes
    /// out once it has held for 150 ms and no sooner than 500 ms after the
    /// one before, the spacing PadForge keeps for every write that lands in
    /// a keyboard's memory (<see cref="LenovoLegionLighting.HoldMs"/>).</para>
    ///
    /// <para>Lenovo lights its Legion accessories through Legion Accessory
    /// Central (support.lenovo.com acc500148), but its installer 2.0.6.06161
    /// carries modules for the K300, K500, M300, M500 and M600 and none for
    /// the K510 (legion_hid.exe, Lenny.Module.*), and no other source names
    /// a program that lights the K510, so the model names none
    /// (<see cref="HidLightingModel.Programs"/>).</para>
    /// </summary>
    internal sealed class LenovoK510Lighting : IHidLightingFamily
    {
        public static readonly LenovoK510Lighting Instance = new();

        internal const ushort VendorId = 0x17EF;
        internal const ushort ProductId = 0x619A;
        internal const ushort UsagePage = 0xFF1C;
        internal const ushort Usage = 0x92;
        internal const int Interface = 1;

        /// <summary>K510_DATA_SIZE (LenovoK510Controller.h:18).</summary>
        internal const int PacketSize = 64;
        internal const byte ReportId = 0x04;

        /// <summary>The state bytes: mode, brightness, speed, direction,
        /// color flag, red, green, blue (LenovoK510Controller.cpp:50-66 and
        /// 93-98).</summary>
        internal const int StateAt = 0x09;
        internal const int StateLength = 8;

        internal const byte StaticMode = 0x06;
        internal const byte Brightness = 0x02;

        /// <summary>How long OpenRGB waits for the state answer
        /// (LenovoK510Controller.cpp:90), and how many input reports a
        /// session reads for it.</summary>
        internal const int AnswerMs = 50;
        internal const int AnswerReads = 3;

        internal const int RepairTries = 3;
        internal const int SavingMs = 100;

        public string Name => "Lenovo Legion K510";

        private static readonly Lazy<HidLightingModel> s_model = new(() =>
        {
            string product = $"lenovo:{VendorId:X4}:{ProductId:X4}";
            return new HidLightingModel
            {
                Family = Instance,
                Name = "Lenovo Legion K510 Mini Pro",
                Map = new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } },
                    "OpenRGB RGBController_LenovoK510.cpp") { Product = product },
                HoldMs = LenovoLegionLighting.HoldMs,
                RepaintMs = LenovoLegionLighting.RepaintMs,
                Begin = (io, wait) => Begin(io, KeyboardProfileBackups.SessionKey(product, io?.Path)),
                Owed = path => KeyboardProfileBackups.Get(KeyboardProfileBackups.DeviceKey(product, path)) != null
                               && KeyboardProfileBackups.Failures(KeyboardProfileBackups.DeviceKey(product, path)) < RepairTries,
                Repair = (io, _) => Repair(io, KeyboardProfileBackups.DeviceKey(product, io?.Path)),
            };
        });

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId || collection.ProductId != ProductId
                || collection.UsagePage != UsagePage || collection.Usage != Usage)
                return null;
            if (collection.InterfaceNumber >= 0 && collection.InterfaceNumber != Interface) return null;
            if (collection.OutputReportLength < PacketSize) return null;
            return s_model.Value;
        }

        /// <summary>A report: ID 4, then 06 38 in bytes 3 and 4 for a mode
        /// or 05 38 for the state request (LenovoK510Controller.cpp:44-48 and
        /// 81-85).</summary>
        internal static byte[] Report(byte command)
        {
            var report = new byte[PacketSize];
            report[0] = ReportId;
            report[3] = command;
            report[4] = 0x38;
            return report;
        }

        internal static byte[] StateRequest() => Report(0x05);

        /// <summary>The mode report for one static color.</summary>
        internal static byte[] StaticReport(int color)
        {
            var report = Report(0x06);
            report[StateAt] = StaticMode;
            report[StateAt + 1] = Brightness;
            report[StateAt + 2] = 0x00;
            report[StateAt + 3] = 0x01;
            report[StateAt + 4] = 0x00;
            report[StateAt + 5] = (byte)(color >> 16);
            report[StateAt + 6] = (byte)(color >> 8);
            report[StateAt + 7] = (byte)color;
            return report;
        }

        /// <summary>The mode report that writes a stored state back.</summary>
        internal static byte[] RestoreReport(byte[] state)
        {
            var report = Report(0x06);
            state.AsSpan(0, StateLength).CopyTo(report.AsSpan(StateAt));
            return report;
        }

        /// <summary>Whether <paramref name="state"/> is a picture of
        /// PadForge's: <see cref="StaticReport"/>'s bytes in any color.</summary>
        internal static bool IsOwnPicture(byte[] state)
            => state != null && state.Length == StateLength && state[0] == StaticMode && state[1] == Brightness
               && state[2] == 0x00 && state[3] == 0x01 && state[4] == 0x00;

        /// <summary>The stored state, bytes 9 to 16 of the answer, or null
        /// when no input report with ID 4 came.</summary>
        internal static byte[] ReadState(IHidReportChannel io)
        {
            if (!io.WriteOutput(StateRequest())) return null;
            var answer = new byte[Math.Max(PacketSize, io.InputReportLength)];
            for (int reads = 0; reads < AnswerReads; reads++)
            {
                int length = io.ReadInput(answer, AnswerMs);
                if (length < 0) return null;
                if (length >= StateAt + StateLength && answer[0] == ReportId)
                    return answer.AsSpan(StateAt, StateLength).ToArray();
            }
            return null;
        }

        private static HidLightingSession Begin(IHidReportChannel io, string product)
        {
            var state = ReadState(io);
            if (state == null) return null;
            var original = state;
            bool ours = false;
            if (KeyboardProfileBackups.Get(product) is { } backup)
            {
                // A backup of another length holds no state of this keyboard.
                if (backup.Contents.Length != StateLength)
                    KeyboardProfileBackups.Drop(product);
                else if (backup.Contents.AsSpan().SequenceEqual(state) || IsOwnPicture(state))
                {
                    // An earlier run's picture, never handed back.
                    original = backup.Contents;
                    ours = IsOwnPicture(state);
                }
                else
                {
                    KeyboardProfileBackups.Drop(product);
                }
            }
            return new Session(product, original, ours);
        }

        private static bool Repair(IHidReportChannel io, string product)
        {
            if (KeyboardProfileBackups.Get(product) is not { } backup) return true;
            var state = ReadState(io);
            if ((state != null && !IsOwnPicture(state)) || backup.Contents.Length != StateLength)
            {
                KeyboardProfileBackups.Drop(product);
                return true;
            }
            if (state != null && backup.Contents.Length == StateLength && io.WriteOutput(RestoreReport(backup.Contents)))
            {
                KeyboardProfileBackups.Drop(product);
                return true;
            }
            KeyboardProfileBackups.NoteFailure(product);
            return false;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly string _product;
            private readonly byte[] _original;
            private bool _ours;
            private byte[][] _crash = Array.Empty<byte[]>();

            public Session(string product, byte[] original, bool ours)
            {
                _product = product;
                _original = original;
                _ours = ours;
                Track();
            }

            public byte[] Original => _original;

            public override bool Feature => false;

            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (KeyboardProfileBackups.Get(_product) is not { } held
                    || !held.Contents.AsSpan().SequenceEqual(_original))
                    KeyboardProfileBackups.Take(_product, 0, _original);
                if (!KeyboardProfileBackups.IsPersisted(_product))
                {
                    LaterMs = SavingMs;
                    return HidLightingPaint.Later;
                }
                _ours = true;
                Track();
                int color = colors != null && colors.Count > 0 ? colors[0] : 0;
                return io.WriteOutput(StaticReport(color)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_ours && !io.WriteOutput(RestoreReport(_original))) return false;
                KeyboardProfileBackups.Drop(_product);
                _ours = false;
                Track();
                return true;
            }

            private void Track()
            {
                if (_ours == _crash.Length > 0) return;
                _crash = _ours ? new[] { RestoreReport(_original) } : Array.Empty<byte[]>();
            }
        }
    }

    /// <summary>
    /// The Lenovo Legion M300 mouse (#557), as OpenRGB's
    /// LenovoM300Controller drives it: a 65-byte output report with no
    /// report ID on usage page 0xFF01 and usage 0x01
    /// (LenovoM300ControllerDetect.cpp:19-22 and 42, M300_DATA_SIZE in
    /// LenovoM300Controller.h:18). One light (RGBController_LenovoM300.cpp:81-95).
    ///
    /// <para>The model of the device. The mode report sets the mode, the
    /// brightness and the color, with a checksum of the bytes before it in
    /// byte 0x40, and the mouse keeps it ("@save :robot:",
    /// RGBController_LenovoM300.cpp:18). OpenRGB is the only public source
    /// for this mouse, and it reads nothing back, so what the mouse showed
    /// before PadForge cannot be put back: a session hands back nothing,
    /// and the mouse keeps PadForge's last color until Legion Accessory
    /// Central or another program sets it. A picture goes out once it has
    /// held for 150 ms and no sooner than 500 ms after the one before, the
    /// spacing PadForge keeps for every write that lands in a device's
    /// memory (<see cref="LenovoLegionLighting.HoldMs"/>).</para>
    ///
    /// <para>The picture: 00 25 00 01 01 0C, the static mode 0x03,
    /// brightness 0x64, 0, the color, and the checksum of bytes 0 to 0x0B
    /// (SetMode and CalculateFinalByte, LenovoM300Controller.cpp:39-94,
    /// M300_MAX_BRIGTH 0x64 in LenovoM300Controller.h:19, the Static mode's
    /// default in RGBController_LenovoM300.cpp:41).</para>
    ///
    /// <para>Lenovo lights its Legion accessories through Legion Accessory
    /// Central (support.lenovo.com acc500148), whose device module for this
    /// one ships in its installer, and the device is left to that program
    /// while it runs (<see cref="VendorPrograms.LegionAccessoryCentral"/>).</para>
    /// </summary>
    internal sealed class LenovoM300Lighting : IHidLightingFamily
    {
        public static readonly LenovoM300Lighting Instance = new();

        internal const ushort VendorId = 0x17EF;
        internal const ushort ProductId = 0x60E4;
        internal const ushort UsagePage = 0xFF01;
        internal const ushort Usage = 0x01;
        internal const int PacketSize = 0x41;
        internal const byte StaticMode = 0x03;
        internal const byte Brightness = 0x64;

        public string Name => "Lenovo Legion M300";

        private static readonly Lazy<HidLightingModel> s_model = new(() => new HidLightingModel
        {
            Family = Instance,
            Name = "Lenovo Legion M300",
            Map = new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } },
                "OpenRGB RGBController_LenovoM300.cpp") { Product = $"lenovo:{VendorId:X4}:{ProductId:X4}" },
            HoldMs = LenovoLegionLighting.HoldMs,
            RepaintMs = LenovoLegionLighting.RepaintMs,
            Programs = VendorPrograms.LenovoAccessories,
            Begin = (_, _) => new Session(),
        });

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId || collection.ProductId != ProductId
                || collection.UsagePage != UsagePage || collection.Usage != Usage)
                return null;
            if (collection.OutputReportLength < PacketSize) return null;
            return s_model.Value;
        }

        internal static byte[] StaticReport(int color)
        {
            var report = new byte[PacketSize];
            report[0x01] = 0x25;
            report[0x03] = 0x01;
            report[0x04] = 0x01;
            report[0x05] = 0x0C;
            report[0x06] = StaticMode;
            report[0x07] = Brightness;
            report[0x09] = (byte)(color >> 16);
            report[0x0A] = (byte)(color >> 8);
            report[0x0B] = (byte)color;
            byte sum = 0;
            for (int i = 0; i < 0x0C; i++) sum += report[i];
            report[0x40] = sum;
            return report;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => io.WriteOutput(StaticReport(colors != null && colors.Count > 0 ? colors[0] : 0))
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }

    /// <summary>
    /// The Lenovo Legion K500 keyboard (#557), as OpenRGB's
    /// LenovoK500Controller drives it on interface 3
    /// (LenovoK500ControllerDetect.cpp:16-17 and 37): a 9-byte feature
    /// report, 00 14 00 5A, then a 65-byte output report with no report ID
    /// (SetMode, LenovoK500Controller.cpp:163-239). OpenRGB opens whichever
    /// collection of interface 3 the walk gives it, and the one that takes
    /// a 65-byte output report is the one that can carry the mode, so that
    /// is the one taken here.
    ///
    /// <para>The model of the device. The keyboard keeps every mode it is
    /// sent ("@save :robot:", RGBController_LenovoK500.cpp:19) and has no
    /// per-key colors: its custom mode lights a set of keys, one bit each
    /// in bytes 0x31 on, in one color, and its static mode lights every key
    /// in one color (lines 351-393). OpenRGB is the only public source for
    /// it and reads nothing back, so a session hands back nothing, and the
    /// keyboard keeps PadForge's last picture until Legion Accessory
    /// Central or another program sets it.</para>
    ///
    /// <para>The picture. One color on every key goes out as the static
    /// mode, 0x01. Any other picture goes out as the custom mode, 0x06:
    /// every key that is not black lit, in the color of the first key
    /// that is not black, OpenRGB's choice when no key changed last
    /// (DeviceUpdateLEDs, RGBController_LenovoK500.cpp:355-386). Brightness
    /// 2 and speed 3 are the defaults (LenovoK500Controller.h:20-26), and
    /// bytes 0x11 to 0x2E carry OpenRGB's default palette under the color
    /// (LenovoK500Controller.cpp:133-139 and 184). A picture goes out once
    /// it has held for 150 ms and no sooner than 500 ms after the one
    /// before (<see cref="LenovoLegionLighting.HoldMs"/>).</para>
    ///
    /// <para>Lenovo lights its Legion accessories through Legion Accessory
    /// Central (support.lenovo.com acc500148), whose device module for this
    /// one ships in its installer, and the device is left to that program
    /// while it runs (<see cref="VendorPrograms.LegionAccessoryCentral"/>).</para>
    /// </summary>
    internal sealed class LenovoK500Lighting : IHidLightingFamily
    {
        public static readonly LenovoK500Lighting Instance = new();

        internal const ushort VendorId = 0x17EF;
        internal const ushort ProductId = 0x60D5;
        internal const int Interface = 3;
        internal const int FeatureSize = 9;
        internal const int PacketSize = 65;
        internal const byte StaticMode = 0x01;
        internal const byte CustomMode = 0x06;
        internal const byte Brightness = 2;
        internal const byte Speed = 3;
        internal const int PaletteAt = 0x11;
        internal const int MaskAt = 0x31;

        /// <summary>default_palette (LenovoK500Controller.cpp:133-139).</summary>
        internal static ReadOnlySpan<byte> Palette => new byte[]
        {
            0xff, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0x00,
            0x00, 0xff, 0x00, 0x00, 0x00, 0x00, 0xff, 0x00,
            0xff, 0x00, 0xff, 0x00, 0xff, 0x00, 0xff, 0xff,
            0x00, 0x00, 0x00, 0xff, 0xff, 0x00,
        };

        public string Name => "Lenovo Legion K500";

        private static readonly Lazy<HidLightingModel> s_model = new(() => new HidLightingModel
        {
            Family = Instance,
            Name = "Lenovo Legion K500",
            Map = new LightMap(Lights(), "OpenRGB RGBController_LenovoK500.cpp")
            {
                Product = $"lenovo:{VendorId:X4}:{ProductId:X4}",
            },
            HoldMs = LenovoLegionLighting.HoldMs,
            RepaintMs = LenovoLegionLighting.RepaintMs,
            Programs = VendorPrograms.LenovoAccessories,
            Begin = (_, _) => new Session(),
        });

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId || collection.ProductId != ProductId
                || collection.InterfaceNumber != Interface || collection.OutputReportLength < PacketSize)
                return null;
            return s_model.Value;
        }

        /// <summary>The 104 keys, addressed by their place in OpenRGB's
        /// order (RGBController_LenovoK500.cpp:38-144 and 335-341).</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new List<LightInfo>(LenovoLights.K500LedNames.Length);
            for (int i = 0; i < LenovoLights.K500LedNames.Length; i++)
                lights.Add(LenovoLights.Light(i, LenovoLights.K500LedNames[i], LightKind.Other));
            return lights;
        }

        /// <summary>The feature report that comes ahead of every mode
        /// (LenovoK500Controller.cpp:167-173).</summary>
        internal static byte[] Prelude()
        {
            var report = new byte[FeatureSize];
            report[1] = 0x14;
            report[3] = 0x5A;
            return report;
        }

        /// <summary>The mode report for a picture.</summary>
        internal static byte[] ModeReport(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            bool one = count > 0;
            for (int n = 1; n < count && one; n++)
                one = (colors[n] & 0xFFFFFF) == (colors[0] & 0xFFFFFF);
            var report = new byte[PacketSize];
            report[1] = 0x01;
            report[2] = one ? StaticMode : CustomMode;
            report[3] = Brightness;
            report[4] = Speed;
            Palette.CopyTo(report.AsSpan(PaletteAt));
            int color = 0;
            if (one)
            {
                color = colors[0];
            }
            else
            {
                for (int n = 0; n < count; n++)
                {
                    int c = colors[n] & 0xFFFFFF;
                    int index = lights[n].Address;
                    if (c == 0 || index < 0 || index >= LenovoLights.K500Mask.Length) continue;
                    if (color == 0) color = c;
                    var (b, bit) = LenovoLights.K500Mask[index];
                    report[MaskAt + b] |= (byte)(1 << bit);
                }
            }
            report[PaletteAt] = (byte)(color >> 16);
            report[PaletteAt + 1] = (byte)(color >> 8);
            report[PaletteAt + 2] = (byte)color;
            return report;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                // OpenRGB sends the mode report whatever the feature report
                // answered (LenovoK500Controller.cpp:167-173).
                io.SetFeature(Prelude());
                return io.WriteOutput(ModeReport(lights, colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
