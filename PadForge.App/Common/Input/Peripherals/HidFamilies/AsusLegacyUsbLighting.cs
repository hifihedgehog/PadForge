using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's AsusLegacyUSBController family (#557): the Cerberus Mech and
    /// Sagaris GK1100 keyboards and the ROG Strix Claw mouse, all under
    /// Controllers/AsusLegacyUSBController at the pinned commit. Every mode
    /// OpenRGB gives these three saves itself on the device
    /// (MODE_FLAG_AUTOMATIC_SAVE throughout their RGBControllers), so every
    /// color PadForge sends is written into the device's memory. A picture
    /// goes out once it has held for 150 ms and no sooner than 500 ms after
    /// the one before, the bounds of <see cref="LenovoLegionLighting"/>, whose
    /// writes land in the keyboard's memory too.
    ///
    /// <para>Cerberus Mech (195D:2047). Feature report 07 of eight bytes. The
    /// controller switches the keyboard to its first profile, which OpenRGB
    /// always writes since "there is no way to fetch them from the device"
    /// (RGBController_AsusCerberusKeyboard.cpp:181-186,
    /// AsusCerberusKeyboardController.cpp:67-81). OpenRGB's Custom mode sets
    /// every key static (0E, lines 113-149) and the profile to its custom
    /// mode, 07, with direction 1 and the brightness at its maximum, 4
    /// (0A, 0B and 0C, lines 151-203, RGBController_AsusCerberusKeyboard.cpp:196-204
    /// and 345-386). A picture is one 0D frame per key with its code and
    /// color, then 0D with FF (lines 83-111, RGBController_AsusCerberusKeyboard.cpp:316-327).
    /// The keys and their codes are OpenRGB's table
    /// (<see cref="AsusAuraTables.CerberusLeds"/>). Nothing reads the profile
    /// back, so nothing is put back: the keyboard keeps PadForge's last
    /// picture in its first profile.</para>
    ///
    /// <para>Sagaris GK1100 (0B05:1835). Output report 06 of 65 bytes. Its
    /// lighting is a mode with a brightness, a speed and a color index into a
    /// palette of seven colors of four bits each, and OpenRGB gives it no
    /// LED, only modes (RGBController_AsusSagarisKeyboard.cpp:173-183 and
    /// 200-246). PadForge lights it as one zone in its Static mode: the
    /// palette's first color, then the mode with the brightness at its
    /// default, 3, and color index 0 (AsusSagarisKeyboardController.cpp:137-185).
    /// The session first reads the mode and the palette, 0B 03 and 0B 05,
    /// as OpenRGB does when it starts (lines 64-135,
    /// RGBController_AsusSagarisKeyboard.cpp:151-163), and its hand-back
    /// writes them back. A keyboard that does not answer gets no picture.
    /// What it read goes into the settings file before the first picture, as
    /// <see cref="LenovoLegionLighting"/> keeps its profile, so a run that
    /// ends with no hand-back, as a forced kill does, leaves the backup, and
    /// the next run writes it back while the keyboard still shows the Static
    /// mode PadForge leaves, and drops it once something else set the
    /// keyboard.</para>
    ///
    /// <para>ROG Strix Claw (195D:1016). Feature report 07 of nine bytes. Its
    /// wheel light can only be on or off and its logo light only dimmed, both
    /// in their own color (AsusStrixClawController.cpp:77-102), so the wheel
    /// is on for any color but black and the logo takes the color's
    /// brightest channel. Nothing reads them back.</para>
    ///
    /// <para>Armoury Crate is ASUS's program for its peripherals, and while it
    /// runs each is left to it (<see cref="VendorPrograms.ArmouryCrate"/>).</para>
    /// </summary>
    internal sealed class AsusLegacyUsbLighting : IHidLightingFamily
    {
        public static readonly AsusLegacyUsbLighting Instance = new();

        internal const string OpenRgbFamily = "AsusLegacyUSBController";

        internal const byte CerberusReport = 0x07, SagarisReport = 0x06, ClawReport = 0x07;

        /// <summary>ASUS_CERBERUS_KB_PACKET_SIZE, ASUS_SAGARIS_KB_PACKET_SIZE
        /// and the Strix Claw's nine bytes.</summary>
        internal const int CerberusLength = 8, SagarisLength = 65, ClawLength = 9;

        /// <summary>CERBERUS_KEYBOARD_MODE_CUSTOM and the Custom mode's
        /// brightness, CERBERUS_MECH_BRIGHTNESS_DEFAULT
        /// (AsusCerberusKeyboardController.h:19-28, RGBController_AsusCerberusKeyboard.h:19-21).</summary>
        internal const byte CerberusCustom = 7, CerberusBrightness = 4;

        /// <summary>SAGARIS_KEYBOARD_MODE_STATIC and
        /// SAGARIS_KEYBOARD_BRIGHTNESS_DEFAULT (AsusSagarisKeyboardController.h:19-35).</summary>
        internal const byte SagarisStatic = 1, SagarisBrightness = 3;

        /// <summary>The palette's size, the waits of the Sagaris's reads and
        /// of SetMode (AsusSagarisKeyboardController.cpp:79, 116-122 and 184).</summary>
        internal const int SagarisColors = 7, SagarisReadMs = 100, SagarisAwaitMs = 20;

        public string Name => "ASUS Legacy USB";

        private readonly Lazy<HidLightingModel> _cerberus, _sagaris, _claw;

        public AsusLegacyUsbLighting()
        {
            _cerberus = new(() => Model("ASUS Cerberus Mech", CerberusMap(), (io, wait) => BeginCerberus(io)));
            _sagaris = new(() => Model("ASUS Sagaris GK1100",
                new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } },
                    "OpenRGB RGBController_AsusSagarisKeyboard.cpp") { Product = SagarisProduct },
                (io, wait) => BeginSagaris(io),
                () => KeyboardProfileBackups.Get(SagarisProduct) != null
                      && KeyboardProfileBackups.Failures(SagarisProduct) < LenovoLegionLighting.RepairTries,
                (io, wait) => RepairSagaris(io)));
            _claw = new(() => Model("ASUS ROG Strix Claw",
                new LightMap(new[]
                {
                    new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
                    new LightInfo(1, LightKind.Logo, 0, LightKeySource.None),
                }, "OpenRGB RGBController_AsusStrixClaw.cpp") { Product = "asus:195D:1016" },
                (io, wait) => io == null ? null : new ClawSession()));
        }

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            var found = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (found.Count == 0) return null;
            return found[0].Detector switch
            {
                "DetectAsusCerberusMech" => _cerberus.Value,
                "DetectAsusSagarisKeyboard" => _sagaris.Value,
                "DetectAsusStrixClaw" => _claw.Value,
                _ => null,
            };
        }

        private HidLightingModel Model(string name, LightMap map, Func<IHidReportChannel, Action<int>, HidLightingSession> begin,
            Func<bool> owed = null, Func<IHidReportChannel, Action<int>, bool> repair = null)
            => new()
            {
                Family = this,
                Name = name,
                Map = map,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                HoldMs = LenovoLegionLighting.HoldMs,
                RepaintMs = LenovoLegionLighting.RepaintMs,
                Begin = begin,
                Owed = owed,
                Repair = repair,
            };

        // ─────────────────────────────────────────────
        //  Cerberus Mech
        // ─────────────────────────────────────────────

        /// <summary>The Cerberus Mech's 105 keys in OpenRGB's order, each at
        /// its key code and matrix cell (RGBController_AsusCerberusKeyboard.cpp:291-314).</summary>
        internal static LightMap CerberusMap()
        {
            var leds = AsusAuraTables.CerberusLeds;
            var cells = AsusAuraTables.Cells(AsusAuraTables.CerberusMatrix, 6, 24, leds.Length);
            var lights = new LightInfo[leds.Length];
            for (int i = 0; i < leds.Length; i++)
            {
                var light = AsusAuraTables.LightOf(leds[i].Name, leds[i].Id);
                var (row, col) = cells[i];
                lights[i] = row < 0 ? light : light with { X = col, Y = row, Width = 1, Height = 1, HasPosition = true };
            }
            return new LightMap(lights, "OpenRGB RGBController_AsusCerberusKeyboard.cpp") { Product = "asus:195D:2047" };
        }

        internal static byte[] CerberusFrame(params byte[] head)
        {
            var frame = new byte[CerberusLength];
            head.CopyTo(frame, 0);
            return frame;
        }

        /// <summary>SetProfile(1), SetPerLEDMode for static keys and SetMode
        /// for the custom mode (AsusCerberusKeyboardController.cpp:67-81 and
        /// 113-203).</summary>
        internal static List<byte[]> CerberusOpening()
        {
            var frames = new List<byte[]> { CerberusFrame(CerberusReport, 0x03, 0x01) };
            for (byte i = 0; i < 4; i++) frames.Add(CerberusFrame(CerberusReport, 0x0E, 0x01, i, 0x00, 0x00, 0x00, 0x00));
            frames.Add(CerberusFrame(CerberusReport, 0x0A, 0x01, CerberusCustom, 0x01));
            frames.Add(CerberusFrame(CerberusReport, 0x0B, 0x01, CerberusCustom, 0x00, 0x00, 0x00));
            frames.Add(CerberusFrame(CerberusReport, 0x0C, 0x01, CerberusBrightness));
            return frames;
        }

        /// <summary>SetPerLEDColor for each key, then SendPerLEDColorEnd
        /// (AsusCerberusKeyboardController.cpp:83-111).</summary>
        internal static List<byte[]> CerberusFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int rgb = colors[i];
                frames.Add(CerberusFrame(CerberusReport, 0x0D, 0x01, (byte)lights[i].Address,
                    (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            }
            frames.Add(CerberusFrame(CerberusReport, 0x0D, 0x01, 0xFF, 0x00, 0x00, 0x00));
            return frames;
        }

        private static HidLightingSession BeginCerberus(IHidReportChannel io)
        {
            if (io == null) return null;
            foreach (var frame in CerberusOpening())
                if (!io.SetFeature(frame)) return null;
            return new FeatureSession((lights, colors) => CerberusFrames(lights, colors));
        }

        /// <summary>A session whose every picture is a list of feature
        /// reports and which has nothing to hand back.</summary>
        internal sealed class FeatureSession : HidLightingSession
        {
            private readonly Func<IReadOnlyList<LightInfo>, IReadOnlyList<int>, List<byte[]>> _frames;

            public FeatureSession(Func<IReadOnlyList<LightInfo>, IReadOnlyList<int>, List<byte[]>> frames) => _frames = frames;

            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in _frames(lights, colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  Sagaris GK1100
        // ─────────────────────────────────────────────

        internal static byte[] SagarisFrame(params byte[] head)
        {
            var frame = new byte[SagarisLength];
            head.CopyTo(frame, 0);
            return frame;
        }

        /// <summary>SetColor (AsusSagarisKeyboardController.cpp:137-159): a
        /// palette index and its four-bit red, green and blue.</summary>
        internal static byte[] SagarisColorFrame(byte index, byte red, byte green, byte blue)
            => SagarisFrame(SagarisReport, 0x0B, 0x04, 0x00, index, red, green, blue);

        /// <summary>SetMode (AsusSagarisKeyboardController.cpp:161-185).</summary>
        internal static byte[] SagarisModeFrame(byte mode, byte brightness, byte speed, byte colorIndex)
            => SagarisFrame(SagarisReport, 0x0B, 0x01, mode, brightness, speed, colorIndex);

        /// <summary>The frames of one color: the palette's first entry, the
        /// color divided by 16 as OpenRGB writes it, then the Static mode at
        /// the default brightness with speed 0 and color index 0
        /// (RGBController_AsusSagarisKeyboard.cpp:217-245).</summary>
        internal static List<byte[]> SagarisFrames(int rgb)
            => new()
            {
                SagarisColorFrame(0, (byte)((rgb >> 16 & 0xFF) / 16), (byte)((rgb >> 8 & 0xFF) / 16), (byte)((rgb & 0xFF) / 16)),
                SagarisModeFrame(SagarisStatic, SagarisBrightness, 0, 0),
            };

        /// <summary>What the keyboard showed before the session: its mode
        /// bytes and its palette, four bits a channel.</summary>
        internal sealed record SagarisState(byte Mode, byte Brightness, byte Speed, byte ColorIndex, byte[][] Palette)
        {
            /// <summary>The frames that put it back: each palette entry, then
            /// the mode, as DeviceUpdateMode writes a mode with its colors
            /// (RGBController_AsusSagarisKeyboard.cpp:212-245).</summary>
            public byte[][] Restore()
            {
                var frames = new byte[SagarisColors + 1][];
                for (int i = 0; i < SagarisColors; i++)
                    frames[i] = SagarisColorFrame((byte)i, Palette[i][0], Palette[i][1], Palette[i][2]);
                frames[SagarisColors] = SagarisModeFrame(Mode, Brightness, Speed, ColorIndex);
                return frames;
            }

            /// <summary>The state as the settings file keeps it: the four mode
            /// bytes, then each palette entry's three.</summary>
            public byte[] Contents()
            {
                var contents = new byte[4 + 3 * SagarisColors];
                contents[0] = Mode;
                contents[1] = Brightness;
                contents[2] = Speed;
                contents[3] = ColorIndex;
                for (int i = 0; i < SagarisColors; i++) Palette[i].CopyTo(contents, 4 + 3 * i);
                return contents;
            }

            /// <summary>A state from <see cref="Contents"/>, or null for bytes
            /// of another length.</summary>
            public static SagarisState FromContents(byte[] contents)
            {
                if (contents == null || contents.Length != 4 + 3 * SagarisColors) return null;
                var palette = new byte[SagarisColors][];
                for (int i = 0; i < SagarisColors; i++) palette[i] = contents.AsSpan(4 + 3 * i, 3).ToArray();
                return new SagarisState(contents[0], contents[1], contents[2], contents[3], palette);
            }

            public bool SameAs(SagarisState other)
                => other != null && Contents().AsSpan().SequenceEqual(other.Contents());

            /// <summary>Whether the keyboard shows what a picture of PadForge's
            /// leaves: the Static mode at brightness 3, speed 0 and color index 0
            /// (<see cref="SagarisFrames"/>).</summary>
            public bool LooksOurs => Mode == SagarisStatic && Brightness == SagarisBrightness && Speed == 0 && ColorIndex == 0;
        }

        /// <summary>The key the Sagaris's backup is kept under in the settings
        /// file (<see cref="KeyboardProfileBackups"/>).</summary>
        internal const string SagarisProduct = "asus:0B05:1835";

        /// <summary>GetMode and GetColors (AsusSagarisKeyboardController.cpp:64-135),
        /// or null when an answer did not come.</summary>
        internal static SagarisState ReadSagaris(IHidReportChannel io)
        {
            var scratch = new byte[Math.Max(SagarisLength, io.InputReportLength)];
            AsusAuraUsbLighting.Clear(io, scratch);
            if (!io.WriteOutput(SagarisFrame(SagarisReport, 0x0B, 0x03))) return null;
            var mode = new byte[scratch.Length];
            if (io.ReadInput(mode, SagarisReadMs) <= 0) return null;
            mode = AsusAuraUsbLighting.AsHidapiRead(mode);
            AsusAuraUsbLighting.Clear(io, scratch);
            if (!io.WriteOutput(SagarisFrame(SagarisReport, 0x0B, 0x05))) return null;
            var palette = new byte[SagarisColors][];
            for (int i = 0; i < SagarisColors; i++)
            {
                var answer = new byte[scratch.Length];
                if (io.ReadInput(answer, SagarisReadMs) <= 0) return null;
                answer = AsusAuraUsbLighting.AsHidapiRead(answer);
                palette[i] = new[] { answer[4], answer[5], answer[6] };
            }
            return new SagarisState(mode[3], mode[4], mode[5], mode[6], palette);
        }

        /// <summary>Starts a session on what the keyboard shows. A backup an
        /// earlier run left stands in for it when the keyboard still shows
        /// that run's picture, and is dropped when something else set it
        /// since, as <see cref="LenovoLegionLighting"/> treats its profile
        /// backup.</summary>
        internal static HidLightingSession BeginSagaris(IHidReportChannel io)
        {
            if (io == null) return null;
            var state = ReadSagaris(io);
            if (state == null) return null;
            bool ours = false;
            if (KeyboardProfileBackups.Get(SagarisProduct) is { } backup)
            {
                var saved = SagarisState.FromContents(backup.Contents);
                if (saved != null && state.SameAs(saved))
                {
                    state = saved;
                }
                else if (saved != null && state.LooksOurs)
                {
                    // An earlier run's picture, never handed back.
                    state = saved;
                    ours = true;
                }
                else
                {
                    KeyboardProfileBackups.Drop(SagarisProduct);
                }
            }
            return new SagarisSession(state, ours);
        }

        /// <summary>Puts back what an earlier run left, with no claim on the
        /// keyboard. True when nothing is owed any more.</summary>
        internal static bool RepairSagaris(IHidReportChannel io)
        {
            if (KeyboardProfileBackups.Get(SagarisProduct) is not { } backup) return true;
            var saved = SagarisState.FromContents(backup.Contents);
            var now = saved == null ? null : ReadSagaris(io);
            bool settled = saved == null
                           || (now != null && (now.SameAs(saved) || !now.LooksOurs || SendSagaris(io, saved.Restore())));
            if (settled) KeyboardProfileBackups.Drop(SagarisProduct);
            else KeyboardProfileBackups.NoteFailure(SagarisProduct);
            return settled;
        }

        /// <summary>Sends output frames as OpenRGB does, each SetMode with the
        /// input drained before it and its answer awaited after it.</summary>
        private static bool SendSagaris(IHidReportChannel io, IEnumerable<byte[]> frames)
        {
            var scratch = new byte[Math.Max(SagarisLength, io.InputReportLength)];
            foreach (var frame in frames)
            {
                bool mode = frame[2] == 0x01;
                if (mode) AsusAuraUsbLighting.Clear(io, scratch);
                if (!io.WriteOutput(frame)) return false;
                if (mode) AsusAuraUsbLighting.Await(io, scratch, SagarisAwaitMs);
            }
            return true;
        }

        /// <summary>A session on the Sagaris. Its first picture waits until the
        /// state it replaces is in the settings file, so a run that ends with
        /// no hand-back leaves the backup, and the next run puts it back
        /// (<see cref="HidLightingModel.Owed"/>).</summary>
        internal sealed class SagarisSession : HidLightingSession
        {
            private readonly SagarisState _state;
            private readonly byte[] _contents;
            private readonly byte[][] _restore;
            private bool _ours;
            private byte[][] _crash = Array.Empty<byte[]>();

            public SagarisSession(SagarisState state, bool ours = false)
            {
                _state = state;
                _contents = state.Contents();
                _restore = state.Restore();
                _ours = ours;
                if (ours) _crash = _restore;
            }

            public SagarisState State => _state;

            public override bool Feature => false;

            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (colors == null || colors.Count == 0) return HidLightingPaint.Sent;
                if (KeyboardProfileBackups.Get(SagarisProduct) is not { } held
                    || !held.Contents.AsSpan().SequenceEqual(_contents))
                    KeyboardProfileBackups.Take(SagarisProduct, 0, _contents);
                if (!KeyboardProfileBackups.IsPersisted(SagarisProduct))
                {
                    LaterMs = LenovoLegionLighting.SavingMs;
                    return HidLightingPaint.Later;
                }
                // Marked first: a write that reports a failure may still have
                // landed.
                _ours = true;
                _crash = _restore;
                return SendSagaris(io, SagarisFrames(colors[0])) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_ours && !SendSagaris(io, _restore)) return false;
                if (KeyboardProfileBackups.Get(SagarisProduct) is { } held && held.Contents.AsSpan().SequenceEqual(_contents))
                    KeyboardProfileBackups.Drop(SagarisProduct);
                _ours = false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }

        // ─────────────────────────────────────────────
        //  ROG Strix Claw
        // ─────────────────────────────────────────────

        /// <summary>SetScrollWheelLED and SetLogoLED
        /// (AsusStrixClawController.cpp:77-102): the wheel on for any color
        /// but black, the logo at the color's brightest channel.</summary>
        internal static List<byte[]> ClawFrames(IReadOnlyList<int> colors)
        {
            int wheel = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
            int logo = colors != null && colors.Count > 1 ? colors[1] & 0xFFFFFF : 0;
            int level = Math.Max(logo >> 16 & 0xFF, Math.Max(logo >> 8 & 0xFF, logo & 0xFF));
            var on = new byte[ClawLength];
            on[0] = ClawReport;
            on[1] = 0x07;
            on[2] = 0x01;
            on[3] = (byte)(wheel != 0 ? 1 : 0);
            var dim = new byte[ClawLength];
            dim[0] = ClawReport;
            dim[1] = 0x0A;
            dim[2] = 0x01;
            dim[3] = 0x00;
            dim[4] = (byte)level;
            return new List<byte[]> { on, dim };
        }

        internal sealed class ClawSession : HidLightingSession
        {
            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in ClawFrames(colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
