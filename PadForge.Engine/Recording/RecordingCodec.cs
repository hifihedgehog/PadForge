using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PadForge.Engine.Recording
{
    /// <summary>
    /// The words of a problem recording (#600): how a change becomes a token
    /// of a line, how a token becomes the change again, and how a change is
    /// applied to a state. The writer and the reader both go through here, so
    /// what one writes the other reads back to the last bit.
    ///
    /// <para>A line of the events part is the time in seconds from the start
    /// with six decimals, a layer letter, a subject (D1 for a device, C1 for
    /// a virtual controller, a hyphen for neither) and the payload. An input
    /// or output payload is tokens divided by spaces: a name, an index where
    /// the field has one, and a value. Integers are written as they are.
    /// A fraction is written in the shortest form that reads back to the
    /// same bits.</para>
    /// </summary>
    public static class RecordingCodec
    {
        /// <summary>Format 2 (#600) names controls (D1 A=1, D1 LeftX=32768)
        /// where format 1 wrote index tokens (a0=, b3+). A reader takes
        /// both.</summary>
        public const string FirstLine = "PadForge problem recording, format 2";

        /// <summary>What the first line of every format starts with.</summary>
        public const string FirstLineStart = "PadForge problem recording";

        /// <summary>The line under the status that says which parts the file
        /// holds (#600).</summary>
        public const string HoldsPrefix = "Holds: ";
        public const string StatusPrefix = "Status: ";
        public const string StatusRecording = "Status: recording";
        public const string StatusFinishedStart = "Status: finished";
        public const string SummaryHeader = "== Summary ==";
        public const string ScrubHeader = "== Scrub check ==";
        public const string SystemHeader = "== System ==";
        public const string DevicesHeader = "== Devices ==";
        public const string ControllersHeader = "== Virtual controllers ==";
        public const string SettingsHeader = "== Settings ==";
        public const string EventsHeader = "== Events ==";
        public const string EndHeader = "== End ==";
        public const string HashPrefix = "sha256 ";

        public const char LayerInput = 'I', LayerOutput = 'O', LayerSent = 'S', LayerFeedback = 'F',
            LayerTiming = 'T', LayerWindow = 'W', LayerError = 'E', LayerLine = 'L', LayerMarker = 'M';

        /// <summary>Layers of format 2 (#600): what PadForge's readers say of
        /// a device (how it is held, a failed read, a reader that lost it,
        /// two readers that disagree), what PadForge's own XInput library reads,
        /// a program's calls on Activity, and a setting changed while
        /// recording.</summary>
        public const char LayerReader = 'R', LayerReadBack = 'X', LayerActivity = 'A', LayerSettingChange = 'C';

        /// <summary>The token that opens a full frame: every field of the
        /// subject follows, and what is not named is at rest.</summary>
        public const string FullToken = "full";

        /// <summary>How often a full frame is written for each device and
        /// each virtual controller, so a reader can start anywhere.</summary>
        public const int KeyframeSeconds = 5;

        /// <summary>The most touchpads and fingers a recording follows on
        /// one device.</summary>
        public const int MaxTouchpads = 4, MaxFingers = 10;

        /// <summary>The most capacitive channels and NFC tags a recording
        /// follows on one device.</summary>
        public const int MaxCapSense = 8, MaxNfcTags = 64;

        /// <summary>Which part of a state's shape a <see cref="RecordingField.Shape"/>
        /// change names.</summary>
        public static class Shape
        {
            public const int Touchpads = 0;
            /// <summary>The fingers of touchpad 0. The other touchpads
            /// follow, one index each.</summary>
            public const int FirstTouchpadFingers = 1;
            public const int CapSense = FirstTouchpadFingers + MaxTouchpads;
            public const int Nfc = CapSense + 1;
            public const int Midi = Nfc + 1;
            public const int AnalogKeys = Midi + 1;
        }

        /// <summary>Why a device went offline.</summary>
        public static class OfflineReason
        {
            public const int Unknown = 0, ReadFailed = 1, HandleLost = 2, Gone = 3;

            public static string Word(int reason) => reason switch
            {
                ReadFailed => "read-failed",
                HandleLost => "handle-lost",
                Gone => "gone",
                _ => "unknown",
            };

            public static int Parse(ReadOnlySpan<char> word)
                => word.SequenceEqual("read-failed") ? ReadFailed
                 : word.SequenceEqual("handle-lost") ? HandleLost
                 : word.SequenceEqual("gone") ? Gone : Unknown;
        }

        /// <summary>What became of a virtual controller's frame on a pass.</summary>
        public static class SentState
        {
            /// <summary>The slot is not created or is switched off.</summary>
            public const int Off = 0;
            /// <summary>Handed to a connected virtual controller.</summary>
            public const int Ok = 1;
            /// <summary>The slot has devices online and no virtual controller.</summary>
            public const int Absent = 2;
            /// <summary>The virtual controller exists and is not connected.</summary>
            public const int Disconnected = 3;
            /// <summary>The hand-over threw.</summary>
            public const int Failed = 4;
            /// <summary>Nothing is sent: none of the slot's devices is online.</summary>
            public const int Resting = 5;

            public static string Word(int state) => state switch
            {
                Ok => "ok",
                Absent => "absent",
                Disconnected => "disconnected",
                Failed => "failed",
                Resting => "resting",
                _ => "off",
            };

            public static int Parse(ReadOnlySpan<char> word)
                => word.SequenceEqual("ok") ? Ok
                 : word.SequenceEqual("absent") ? Absent
                 : word.SequenceEqual("disconnected") ? Disconnected
                 : word.SequenceEqual("failed") ? Failed
                 : word.SequenceEqual("resting") ? Resting : Off;
        }

        /// <summary>The steps of a pass a timing line tells apart, in the
        /// order a pass runs them.</summary>
        public static readonly string[] StepNames =
        {
            "sdl", "sweep", "read", "engage", "map", "macros", "motion", "virtual", "output",
        };

        // ─────────────────────────────────────────────
        //  Fields
        // ─────────────────────────────────────────────

        /// <summary>The layer a field's lines carry.</summary>
        public static char LayerOf(RecordingField field)
        {
            if (field <= RecordingField.Shape) return LayerInput;
            if (field <= RecordingField.OutAccel) return LayerOutput;
            if (field == RecordingField.Sent) return LayerSent;
            if (field <= RecordingField.DeviceRumble) return LayerFeedback;
            if (field == RecordingField.ForceCounts) return LayerFeedback;
            return field == RecordingField.Marker ? LayerMarker : LayerTiming;
        }

        /// <summary>True for a field whose subject is a device though its
        /// layer is no input layer: rumble written to it, a call to it that
        /// took long, its force counts.</summary>
        public static bool NamesDevice(RecordingField field)
            => field is RecordingField.DeviceRumble or RecordingField.TimingCall or RecordingField.ForceCounts;

        /// <summary>What a device call was (#600).</summary>
        public static class CallKind
        {
            public const int Read = 1, Rumble = 2, Force = 3;

            public static string Word(int kind) => kind switch
            {
                Read => "read",
                Rumble => "rumble",
                Force => "force",
                _ => "call",
            };

            /// <summary>The words a line uses for a call that hangs: its
            /// read, its rumble write, its force write.</summary>
            public static string Phrase(int kind) => kind switch
            {
                Read => "read",
                Rumble => "rumble write",
                Force => "force write",
                _ => "call",
            };
        }

        /// <summary>True for a field that holds the counts of one pass: each
        /// pass that has any is its own line, a full frame leaves it out, and
        /// a state holds the last one written.</summary>
        public static bool IsPerPass(RecordingField field)
            => field is RecordingField.JoyCon2Mouse or RecordingField.MouseRaw or RecordingField.MouseWheel
                or RecordingField.KbmFlick or RecordingField.KbmGyro
                or RecordingField.KbmTouch or RecordingField.KbmCoast;

        private enum Form
        {
            /// <summary>name[index]=int</summary>
            Int,
            /// <summary>name[index]+ or name[index]-</summary>
            Flag,
            /// <summary>name[index]=f,f,f from three sets of bits.</summary>
            Float3,
            /// <summary>name=f,f from two sets of bits.</summary>
            Float2,
            /// <summary>name=f from one set of bits.</summary>
            Float1,
            /// <summary>name[index]=int,int</summary>
            Int2,
            /// <summary>name[index]=int,int,int</summary>
            Int3,
            /// <summary>name[index]=0x hex of A.</summary>
            Hex32,
            /// <summary>name[index]=0x hex of B then A.</summary>
            Hex64,
            /// <summary>name[index]=f,f,int</summary>
            Float2Int,
            /// <summary>name[index]=int,f,f</summary>
            IntFloat2,
            /// <summary>Written and read by hand.</summary>
            Special,
        }

        private readonly record struct Spec(string Name, Form Form, bool Indexed);

        private static readonly Spec[] Specs = BuildSpecs();
        private static readonly Dictionary<string, RecordingField> ByName = BuildNames();

        private static Spec[] BuildSpecs()
        {
            var specs = new Spec[Enum.GetValues<RecordingField>().Length];
            void Set(RecordingField field, string name, Form form, bool indexed = false) => specs[(int)field] = new Spec(name, form, indexed);

            Set(RecordingField.Axis, "a", Form.Int, true);
            Set(RecordingField.Slider, "s", Form.Int, true);
            Set(RecordingField.Hat, "h", Form.Int, true);
            Set(RecordingField.Button, "b", Form.Flag, true);
            Set(RecordingField.Gyro, "gy", Form.Float3);
            Set(RecordingField.Accel, "ac", Form.Float3);
            Set(RecordingField.GyroAux, "gyl", Form.Float3);
            Set(RecordingField.AccelAux, "acl", Form.Float3);
            Set(RecordingField.TouchContact, "tc", Form.Int2, true);
            Set(RecordingField.TouchPosition, "tp", Form.Float3, true);
            Set(RecordingField.TouchClick, "tk", Form.Flag, true);
            Set(RecordingField.MidiNote, "mn", Form.Flag, true);
            Set(RecordingField.MidiCc, "mc", Form.Int, true);
            Set(RecordingField.MidiCcUp, "mu", Form.Flag, true);
            Set(RecordingField.MidiCcDown, "md", Form.Flag, true);
            Set(RecordingField.MidiPitchBend, "mb", Form.Int);
            Set(RecordingField.AnalogKey, "ak", Form.Float1, true);
            Set(RecordingField.IrPointer, "ir", Form.Float2Int);
            Set(RecordingField.JoyConIr, "ji", Form.Float1);
            Set(RecordingField.RingCon, "rc", Form.Float1);
            Set(RecordingField.JoyCon2Mouse, "jm", Form.Float2);
            Set(RecordingField.MouseRaw, "mr", Form.Int2);
            Set(RecordingField.MouseWheel, "mw", Form.Int2);
            Set(RecordingField.CapSense, "cs", Form.Flag, true);
            Set(RecordingField.NfcTag, "nf", Form.Flag, true);
            Set(RecordingField.Battery, "bat", Form.Int2);
            Set(RecordingField.Online, "online", Form.Special);
            Set(RecordingField.Shape, "shape", Form.Special);

            Set(RecordingField.OutType, "type", Form.Special);
            Set(RecordingField.GpButtons, "bt", Form.Hex32);
            Set(RecordingField.GpLeftTrigger, "lt", Form.Int);
            Set(RecordingField.GpRightTrigger, "rt", Form.Int);
            Set(RecordingField.GpLeftX, "lx", Form.Int);
            Set(RecordingField.GpLeftY, "ly", Form.Int);
            Set(RecordingField.GpRightX, "rx", Form.Int);
            Set(RecordingField.GpRightY, "ry", Form.Int);
            Set(RecordingField.GpExtra, "ex", Form.Int);
            Set(RecordingField.RawAxis, "ra", Form.Int, true);
            Set(RecordingField.RawButtons, "rb", Form.Hex32, true);
            Set(RecordingField.RawHat, "rh", Form.Int, true);
            Set(RecordingField.KbmKeys, "kk", Form.Hex64, true);
            Set(RecordingField.KbmMouseButtons, "kb", Form.Int);
            Set(RecordingField.KbmMove, "km", Form.Int2);
            Set(RecordingField.KbmScroll, "ks", Form.Int2);
            Set(RecordingField.KbmAbsolute, "ka", Form.Float2Int);
            Set(RecordingField.KbmFlick, "kf", Form.Int);
            Set(RecordingField.KbmGyro, "kg", Form.Float2);
            Set(RecordingField.KbmTouch, "kt", Form.Float2);
            Set(RecordingField.KbmCoast, "kc", Form.Float2);
            Set(RecordingField.MidiOutCc, "oc", Form.Int, true);
            Set(RecordingField.MidiOutNote, "on", Form.Flag, true);
            Set(RecordingField.VrHand, "vh", Form.Int3, true);
            Set(RecordingField.VrStick, "vs", Form.Int2, true);
            Set(RecordingField.OutTouch, "ot", Form.IntFloat2, true);
            Set(RecordingField.OutTouchClick, "ok", Form.Flag);
            Set(RecordingField.OutGyro, "og", Form.Float3);
            Set(RecordingField.OutAccel, "oa", Form.Float3);

            Set(RecordingField.Sent, "sent", Form.Special);
            Set(RecordingField.GameRumble, "rumble", Form.Special);
            Set(RecordingField.DeviceRumble, "wrote", Form.Special);
            Set(RecordingField.TimingPasses, "passes", Form.Special);
            Set(RecordingField.TimingWork, "work", Form.Special);
            Set(RecordingField.TimingLate, "late", Form.Special);
            Set(RecordingField.TimingStep, "step", Form.Special);
            Set(RecordingField.TimingStall, "stall", Form.Special);
            Set(RecordingField.TimingGap, "gap", Form.Special);
            Set(RecordingField.TimingRest, "rest", Form.Special);
            Set(RecordingField.TimingCall, "took", Form.Special);
            Set(RecordingField.ForceCounts, "force", Form.Special);
            Set(RecordingField.Marker, "mark", Form.Special);
            return specs;
        }

        private static Dictionary<string, RecordingField> BuildNames()
        {
            var names = new Dictionary<string, RecordingField>(StringComparer.Ordinal);
            for (int i = 1; i < Specs.Length; i++)
            {
                var field = (RecordingField)i;
                // Only the fields a reader turns back into a state.
                if (LayerOf(field) is not (LayerInput or LayerOutput)) continue;
                names.Add(Specs[i].Name, field);
            }
            names.Add("offline", RecordingField.Online);
            return names;
        }

        // ─────────────────────────────────────────────
        //  Time
        // ─────────────────────────────────────────────

        /// <summary>A time in microseconds as seconds with six decimals.</summary>
        public static void AppendTime(StringBuilder sb, long microseconds)
        {
            if (microseconds < 0) { sb.Append('-'); microseconds = -microseconds; }
            AppendInt(sb, microseconds / 1_000_000);
            sb.Append('.');
            long fraction = microseconds % 1_000_000;
            for (long place = 100_000; place > 0; place /= 10)
            {
                sb.Append((char)('0' + fraction / place));
                fraction %= place;
            }
        }

        /// <summary>Reads what <see cref="AppendTime"/> wrote.</summary>
        public static bool TryParseTime(ReadOnlySpan<char> text, out long microseconds)
        {
            microseconds = 0;
            bool negative = text.Length > 0 && text[0] == '-';
            if (negative) text = text.Slice(1);
            int dot = text.IndexOf('.');
            if (dot <= 0 || text.Length - dot - 1 != 6) return false;
            if (!long.TryParse(text.Slice(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)) return false;
            if (!int.TryParse(text.Slice(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int fraction)) return false;
            microseconds = seconds * 1_000_000 + fraction;
            if (negative) microseconds = -microseconds;
            return true;
        }

        // ─────────────────────────────────────────────
        //  Numbers
        // ─────────────────────────────────────────────

        private static void AppendInt(StringBuilder sb, long value)
        {
            Span<char> buffer = stackalloc char[24];
            value.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
            sb.Append(buffer.Slice(0, written));
        }

        /// <summary>A fraction from its bits, in the shortest decimal that
        /// reads back to the same bits. A value that is no number, or is
        /// infinite, is written as its bits, since its text does not hold
        /// them all. Writing a long decimal as its eight hex digits instead
        /// made the text about 5% shorter and its zip larger (a minute of a
        /// moving DualSense, 2.93 MB against 2.88 MB at deflate's optimal
        /// level), and the file leaves PadForge zipped.</summary>
        public static void AppendFloat(StringBuilder sb, int bits)
        {
            float value = BitConverter.Int32BitsToSingle(bits);
            if (!float.IsFinite(value))
            {
                sb.Append('x');
                Span<char> hex = stackalloc char[8];
                ((uint)bits).TryFormat(hex, out int n, "X8", CultureInfo.InvariantCulture);
                sb.Append(hex.Slice(0, n));
                return;
            }
            Span<char> buffer = stackalloc char[32];
            value.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
            sb.Append(buffer.Slice(0, written));
        }

        private static bool TryParseFloat(ReadOnlySpan<char> text, out int bits)
        {
            bits = 0;
            if (text.Length == 9 && text[0] == 'x')
            {
                if (!uint.TryParse(text.Slice(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint raw)) return false;
                bits = (int)raw;
                return true;
            }
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) return false;
            bits = BitConverter.SingleToInt32Bits(value);
            return true;
        }

        private static bool TryParseInt(ReadOnlySpan<char> text, out int value)
            => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

        private static bool TryParseHex(ReadOnlySpan<char> text, out ulong value)
        {
            value = 0;
            return text.StartsWith("0x") && ulong.TryParse(text.Slice(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        private static void AppendHex(StringBuilder sb, ulong value, int digits)
        {
            sb.Append("0x");
            Span<char> buffer = stackalloc char[16];
            value.TryFormat(buffer, out int written, digits == 16 ? "X16" : digits == 8 ? "X8" : "X4", CultureInfo.InvariantCulture);
            sb.Append(buffer.Slice(0, written));
        }

        // ─────────────────────────────────────────────
        //  Tokens
        // ─────────────────────────────────────────────

        /// <summary>Appends one change as its token, or as its few tokens
        /// for a timing or status field. Nothing is written before it.</summary>
        public static void AppendToken(StringBuilder sb, RecordingField field, int index, int a, int b, int c)
        {
            var spec = Specs[(int)field];
            if (spec.Name == null) return;
            if (spec.Form == Form.Special)
            {
                AppendSpecial(sb, field, index, a, b, c);
                return;
            }

            sb.Append(spec.Name);
            if (spec.Indexed)
            {
                if (field is RecordingField.TouchContact or RecordingField.TouchPosition)
                {
                    AppendInt(sb, index >> 4);
                    sb.Append('.');
                    AppendInt(sb, index & 15);
                }
                else AppendInt(sb, index);
            }
            switch (spec.Form)
            {
                case Form.Flag:
                    sb.Append(a != 0 ? '+' : '-');
                    break;
                case Form.Int:
                    sb.Append('='); AppendInt(sb, a);
                    break;
                case Form.Int2:
                    sb.Append('='); AppendInt(sb, a); sb.Append(','); AppendInt(sb, b);
                    break;
                case Form.Int3:
                    sb.Append('='); AppendInt(sb, a); sb.Append(','); AppendInt(sb, b); sb.Append(','); AppendInt(sb, c);
                    break;
                case Form.Float1:
                    sb.Append('='); AppendFloat(sb, a);
                    break;
                case Form.Float2:
                    sb.Append('='); AppendFloat(sb, a); sb.Append(','); AppendFloat(sb, b);
                    break;
                case Form.Float3:
                    sb.Append('='); AppendFloat(sb, a); sb.Append(','); AppendFloat(sb, b); sb.Append(','); AppendFloat(sb, c);
                    break;
                case Form.Float2Int:
                    sb.Append('='); AppendFloat(sb, a); sb.Append(','); AppendFloat(sb, b); sb.Append(','); AppendInt(sb, c);
                    break;
                case Form.IntFloat2:
                    sb.Append('='); AppendInt(sb, a); sb.Append(','); AppendFloat(sb, b); sb.Append(','); AppendFloat(sb, c);
                    break;
                case Form.Hex32:
                    sb.Append('='); AppendHex(sb, (uint)a, field == RecordingField.GpButtons ? 4 : 8);
                    break;
                case Form.Hex64:
                    sb.Append('='); AppendHex(sb, ((ulong)(uint)b << 32) | (uint)a, 16);
                    break;
            }
        }

        /// <summary>Tenths of a microsecond as microseconds with one
        /// decimal.</summary>
        private static void AppendTenths(StringBuilder sb, int tenths)
        {
            if (tenths < 0) { sb.Append('-'); tenths = -tenths; }
            AppendInt(sb, tenths / 10);
            sb.Append('.').Append((char)('0' + tenths % 10));
        }

        private static void AppendSpecial(StringBuilder sb, RecordingField field, int index, int a, int b, int c)
        {
            switch (field)
            {
                case RecordingField.Online:
                    if (a != 0) sb.Append("online");
                    else sb.Append("offline=").Append(OfflineReason.Word(b));
                    break;
                case RecordingField.Shape:
                    sb.Append("shape.").Append(ShapeName(index, out int pad));
                    if (pad >= 0) AppendInt(sb, pad);
                    sb.Append('='); AppendInt(sb, a);
                    break;
                case RecordingField.OutType:
                    sb.Append("type=").Append(TypeWord(a));
                    break;
                case RecordingField.Sent:
                    sb.Append("sent=").Append(SentState.Word(a)).Append(" type=").Append(TypeWord(b));
                    break;
                case RecordingField.GameRumble:
                case RecordingField.DeviceRumble:
                    sb.Append(field == RecordingField.GameRumble ? "rumble=" : "wrote=");
                    AppendInt(sb, a & 0xFFFF); sb.Append(',');
                    AppendInt(sb, (a >> 16) & 0xFFFF); sb.Append(',');
                    AppendInt(sb, b & 0xFFFF); sb.Append(',');
                    AppendInt(sb, (b >> 16) & 0xFFFF);
                    break;
                case RecordingField.TimingPasses:
                    sb.Append("passes="); AppendInt(sb, a);
                    sb.Append(" set="); AppendInt(sb, b);
                    sb.Append(" rate="); AppendInt(sb, c / 1000); sb.Append('.');
                    sb.Append((char)('0' + c % 1000 / 100)).Append((char)('0' + c % 100 / 10)).Append((char)('0' + c % 10));
                    break;
                case RecordingField.TimingWork:
                    sb.Append("work="); AppendTenths(sb, a);
                    sb.Append(" longest="); AppendInt(sb, b);
                    sb.Append(" capture="); AppendTenths(sb, c);
                    break;
                case RecordingField.TimingLate:
                    sb.Append("late="); AppendTenths(sb, a);
                    sb.Append(" latest="); AppendInt(sb, b);
                    sb.Append(" sleeps="); AppendInt(sb, c);
                    break;
                case RecordingField.TimingStep:
                    sb.Append((uint)index < (uint)StepNames.Length ? StepNames[index] : "step").Append('=');
                    AppendTenths(sb, a);
                    break;
                case RecordingField.TimingStall:
                    sb.Append("stall sdl="); AppendInt(sb, a);
                    sb.Append(" sweep="); AppendInt(sb, b);
                    sb.Append(" pass="); AppendInt(sb, c);
                    break;
                case RecordingField.TimingGap:
                    sb.Append("gap="); AppendInt(sb, a);
                    sb.Append(" due="); AppendInt(sb, b);
                    break;
                case RecordingField.TimingRest:
                    sb.Append("rest=").Append(a == 1 ? "idle" : a == 2 ? "suspended" : "none");
                    break;
                case RecordingField.TimingCall:
                    sb.Append("took ms="); AppendInt(sb, a);
                    sb.Append(" call=").Append(CallKind.Word(b));
                    break;
                case RecordingField.ForceCounts:
                    sb.Append("force updates="); AppendInt(sb, a);
                    sb.Append(" made="); AppendInt(sb, b);
                    sb.Append(" started="); AppendInt(sb, c);
                    break;
                case RecordingField.Marker:
                    sb.Append("mark");
                    break;
            }
        }

        private static string ShapeName(int index, out int pad)
        {
            pad = -1;
            if (index == Shape.Touchpads) return "tp";
            if (index >= Shape.FirstTouchpadFingers && index < Shape.CapSense)
            {
                pad = index - Shape.FirstTouchpadFingers;
                return "tf";
            }
            if (index == Shape.CapSense) return "cs";
            if (index == Shape.Nfc) return "nfc";
            return index == Shape.Midi ? "midi" : "ak";
        }

        private static readonly string[] TypeWords = Enum.GetNames<VirtualControllerType>();
        private static readonly VirtualControllerType[] TypeValues = Enum.GetValues<VirtualControllerType>();

        /// <summary>A virtual controller type by its name, or "none".</summary>
        public static string TypeWord(int type)
        {
            for (int i = 0; i < TypeValues.Length; i++)
                if ((int)TypeValues[i] == type) return TypeWords[i];
            return "none";
        }

        /// <summary>Reads what <see cref="TypeWord"/> wrote: the type's
        /// number, or -1.</summary>
        public static int ParseType(ReadOnlySpan<char> word)
        {
            for (int i = 0; i < TypeWords.Length; i++)
                if (word.SequenceEqual(TypeWords[i])) return (int)TypeValues[i];
            return -1;
        }

        /// <summary>Reads one token of an input or output line back into the
        /// change that wrote it. False for a token this version does not
        /// know, which a reader steps over.</summary>
        public static bool TryParseToken(ReadOnlySpan<char> token, out RecordingField field, out int index, out int a, out int b, out int c)
        {
            field = RecordingField.None;
            index = a = b = c = 0;
            if (token.IsEmpty) return false;

            // The name runs to the first digit, sign or equals. A shape
            // token's name has a dot in it.
            int nameEnd = 0;
            while (nameEnd < token.Length && (char.IsAsciiLetter(token[nameEnd]) || token[nameEnd] == '.')) nameEnd++;
            if (nameEnd == 0) return false;
            var name = token.Slice(0, nameEnd);
            var rest = token.Slice(nameEnd);

            if (name.StartsWith("shape."))
                return TryParseShape(name.Slice(6), rest, out field, out index, out a);
            if (name.Contains('.')) return false;

            if (!ByName.TryGetValue(name.ToString(), out field)) return false;
            var spec = Specs[(int)field];

            if (field == RecordingField.Online)
            {
                if (name.SequenceEqual("online")) { a = 1; return rest.IsEmpty; }
                if (rest.Length < 1 || rest[0] != '=') return false;
                b = OfflineReason.Parse(rest.Slice(1));
                return true;
            }
            if (field == RecordingField.OutType)
            {
                if (rest.Length < 1 || rest[0] != '=') return false;
                a = ParseType(rest.Slice(1));
                return true;
            }

            // The index: digits, and for a touch field a dot and digits.
            int valueStart = 0;
            while (valueStart < rest.Length && (char.IsAsciiDigit(rest[valueStart]) || rest[valueStart] == '.')) valueStart++;
            var indexText = rest.Slice(0, valueStart);
            var value = rest.Slice(valueStart);
            if (spec.Indexed)
            {
                if (indexText.IsEmpty) return false;
                int dot = indexText.IndexOf('.');
                if (field is RecordingField.TouchContact or RecordingField.TouchPosition)
                {
                    if (dot <= 0 || !TryParseInt(indexText.Slice(0, dot), out int pad) || !TryParseInt(indexText.Slice(dot + 1), out int finger)) return false;
                    if ((uint)pad >= MaxTouchpads || (uint)finger > 15) return false;
                    index = (pad << 4) | finger;
                }
                else if (dot >= 0 || !TryParseInt(indexText, out index) || index < 0) return false;
            }
            else if (!indexText.IsEmpty) return false;

            if (spec.Form == Form.Flag)
            {
                if (value.Length != 1 || value[0] is not ('+' or '-')) return false;
                a = value[0] == '+' ? 1 : 0;
                return true;
            }
            if (value.Length < 2 || value[0] != '=') return false;
            value = value.Slice(1);

            Span<Range> parts = stackalloc Range[4];
            int count = value.Split(parts, ',');
            ReadOnlySpan<char> Part(ReadOnlySpan<char> v, Span<Range> p, int i) => v[p[i]];
            switch (spec.Form)
            {
                case Form.Int:
                    return count == 1 && TryParseInt(value, out a);
                case Form.Int2:
                    return count == 2 && TryParseInt(Part(value, parts, 0), out a) && TryParseInt(Part(value, parts, 1), out b);
                case Form.Int3:
                    return count == 3 && TryParseInt(Part(value, parts, 0), out a) && TryParseInt(Part(value, parts, 1), out b)
                        && TryParseInt(Part(value, parts, 2), out c);
                case Form.Float1:
                    return count == 1 && TryParseFloat(value, out a);
                case Form.Float2:
                    return count == 2 && TryParseFloat(Part(value, parts, 0), out a) && TryParseFloat(Part(value, parts, 1), out b);
                case Form.Float3:
                    return count == 3 && TryParseFloat(Part(value, parts, 0), out a) && TryParseFloat(Part(value, parts, 1), out b)
                        && TryParseFloat(Part(value, parts, 2), out c);
                case Form.Float2Int:
                    return count == 3 && TryParseFloat(Part(value, parts, 0), out a) && TryParseFloat(Part(value, parts, 1), out b)
                        && TryParseInt(Part(value, parts, 2), out c);
                case Form.IntFloat2:
                    return count == 3 && TryParseInt(Part(value, parts, 0), out a) && TryParseFloat(Part(value, parts, 1), out b)
                        && TryParseFloat(Part(value, parts, 2), out c);
                case Form.Hex32:
                    if (count != 1 || !TryParseHex(value, out ulong h32) || h32 > uint.MaxValue) return false;
                    a = (int)(uint)h32;
                    return true;
                case Form.Hex64:
                    if (count != 1 || !TryParseHex(value, out ulong h64)) return false;
                    a = (int)(uint)h64;
                    b = (int)(uint)(h64 >> 32);
                    return true;
            }
            return false;
        }

        private static bool TryParseShape(ReadOnlySpan<char> name, ReadOnlySpan<char> rest, out RecordingField field, out int index, out int a)
        {
            field = RecordingField.Shape;
            index = a = 0;
            int eq = rest.IndexOf('=');
            if (eq < 0 || !TryParseInt(rest.Slice(eq + 1), out a)) return false;
            var pad = rest.Slice(0, eq);
            if (name.SequenceEqual("tf"))
            {
                if (!TryParseInt(pad, out int p) || (uint)p >= MaxTouchpads) return false;
                index = Shape.FirstTouchpadFingers + p;
                return true;
            }
            if (!pad.IsEmpty) return false;
            if (name.SequenceEqual("tp")) index = Shape.Touchpads;
            else if (name.SequenceEqual("cs")) index = Shape.CapSense;
            else if (name.SequenceEqual("nfc")) index = Shape.Nfc;
            else if (name.SequenceEqual("midi")) index = Shape.Midi;
            else if (name.SequenceEqual("ak")) index = Shape.AnalogKeys;
            else return false;
            return true;
        }

        // ─────────────────────────────────────────────
        //  Named tokens (format 2)
        // ─────────────────────────────────────────────

        /// <summary>Appends one input change as format 2 writes it: a
        /// control by its name (A=1, LeftX=32768, DPad=9000), a sensor by its
        /// own word, and anything else as its format 1 token.</summary>
        public static void AppendInputToken(StringBuilder sb, RecordingControlKind kind, RecordingField field, int index, int a, int b, int c)
        {
            switch (field)
            {
                case RecordingField.Axis:
                case RecordingField.Slider:
                case RecordingField.Hat:
                    sb.Append(RecordingControlNames.Input(kind, field, index)).Append('=');
                    AppendInt(sb, a);
                    return;
                case RecordingField.Button:
                    sb.Append(RecordingControlNames.Input(kind, field, index)).Append('=').Append(a != 0 ? '1' : '0');
                    return;
                case RecordingField.Gyro: Named3(sb, "Gyro", a, b, c); return;
                case RecordingField.Accel: Named3(sb, "Accel", a, b, c); return;
                case RecordingField.GyroAux: Named3(sb, "GyroAux", a, b, c); return;
                case RecordingField.AccelAux: Named3(sb, "AccelAux", a, b, c); return;
                case RecordingField.Battery:
                    sb.Append("Battery=");
                    AppendInt(sb, a);
                    if (b != 0) sb.Append(",charging");
                    return;
                default:
                    AppendToken(sb, field, index, a, b, c);
                    return;
            }
        }

        private static void Named3(StringBuilder sb, string name, int a, int b, int c)
        {
            sb.Append(name).Append('=');
            AppendFloat(sb, a); sb.Append(','); AppendFloat(sb, b); sb.Append(','); AppendFloat(sb, c);
        }

        private static readonly (string Name, RecordingField Field)[] OutputNames =
        {
            ("Buttons", RecordingField.GpButtons), ("LeftTrigger", RecordingField.GpLeftTrigger),
            ("RightTrigger", RecordingField.GpRightTrigger), ("LeftX", RecordingField.GpLeftX),
            ("LeftY", RecordingField.GpLeftY), ("RightX", RecordingField.GpRightX), ("RightY", RecordingField.GpRightY),
            ("Extra", RecordingField.GpExtra),
        };

        /// <summary>Appends one output change as format 2 writes it: an
        /// XInput frame's fields by their names (Buttons=A+Start,
        /// LeftX=-1200), anything else as its format 1 token.</summary>
        public static void AppendOutputToken(StringBuilder sb, RecordingField field, int index, int a, int b, int c)
        {
            foreach (var (name, named) in OutputNames)
            {
                if (named != field) continue;
                sb.Append(name).Append('=');
                if (field == RecordingField.GpButtons) RecordingControlNames.AppendXInputButtons(sb, a);
                else AppendInt(sb, a);
                return;
            }
            AppendToken(sb, field, index, a, b, c);
        }

        /// <summary>Appends a change of any layer: an input change named for
        /// <paramref name="kind"/>, an output change by its name, and the
        /// rest as their tokens.</summary>
        public static void AppendNamedToken(StringBuilder sb, RecordingControlKind kind, RecordingField field, int index, int a, int b, int c)
        {
            char layer = LayerOf(field);
            if (layer == LayerInput) AppendInputToken(sb, kind, field, index, a, b, c);
            else if (layer == LayerOutput) AppendOutputToken(sb, field, index, a, b, c);
            else AppendToken(sb, field, index, a, b, c);
        }

        /// <summary>A token of format 2's own: a word that starts with a
        /// capital letter, then an equals sign and a value.</summary>
        private static bool SplitNamed(ReadOnlySpan<char> token, out ReadOnlySpan<char> name, out ReadOnlySpan<char> value)
        {
            name = value = default;
            if (token.IsEmpty || !char.IsAsciiLetterUpper(token[0])) return false;
            int eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1) return false;
            name = token.Slice(0, eq);
            value = token.Slice(eq + 1);
            return true;
        }

        /// <summary>Reads one token of an input line, of either format.
        /// False for a token this version does not know.</summary>
        public static bool TryParseInputToken(ReadOnlySpan<char> token, out RecordingField field, out int index, out int a, out int b, out int c)
        {
            field = RecordingField.None;
            index = a = b = c = 0;
            if (!SplitNamed(token, out var name, out var value))
                return TryParseToken(token, out field, out index, out a, out b, out c) && LayerOf(field) == LayerInput;
            Span<Range> parts = stackalloc Range[4];
            int count = value.Split(parts, ',');
            if (name.SequenceEqual("Gyro")) { field = RecordingField.Gyro; return Floats3(value, parts, count, out a, out b, out c); }
            if (name.SequenceEqual("Accel")) { field = RecordingField.Accel; return Floats3(value, parts, count, out a, out b, out c); }
            if (name.SequenceEqual("GyroAux")) { field = RecordingField.GyroAux; return Floats3(value, parts, count, out a, out b, out c); }
            if (name.SequenceEqual("AccelAux")) { field = RecordingField.AccelAux; return Floats3(value, parts, count, out a, out b, out c); }
            if (name.SequenceEqual("Battery"))
            {
                field = RecordingField.Battery;
                if (!TryParseInt(value[parts[0]], out a)) return false;
                if (count == 1) return true;
                b = 1;
                return count == 2 && value[parts[1]].SequenceEqual("charging");
            }
            if (!RecordingControlNames.TryInput(name, out field, out index)) return false;
            if (count != 1 || !TryParseInt(value, out a)) return false;
            return field != RecordingField.Button || a is 0 or 1;
        }

        private static bool Floats3(ReadOnlySpan<char> value, Span<Range> parts, int count, out int a, out int b, out int c)
        {
            a = b = c = 0;
            return count == 3 && TryParseFloat(value[parts[0]], out a) && TryParseFloat(value[parts[1]], out b) && TryParseFloat(value[parts[2]], out c);
        }

        /// <summary>Reads one token of an output line, of either format.</summary>
        public static bool TryParseOutputToken(ReadOnlySpan<char> token, out RecordingField field, out int index, out int a, out int b, out int c)
        {
            field = RecordingField.None;
            index = a = b = c = 0;
            if (!SplitNamed(token, out var name, out var value))
                return TryParseToken(token, out field, out index, out a, out b, out c) && LayerOf(field) == LayerOutput;
            foreach (var (known, named) in OutputNames)
            {
                if (!name.SequenceEqual(known)) continue;
                field = named;
                return named == RecordingField.GpButtons ? RecordingControlNames.TryXInputButtons(value, out a) : TryParseInt(value, out a);
            }
            return false;
        }

        // ─────────────────────────────────────────────
        //  A device's state
        // ─────────────────────────────────────────────

        private static float F(int bits) => BitConverter.Int32BitsToSingle(bits);
        private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

        /// <summary>A state with nothing in it and no optional part, which a
        /// full frame starts from.</summary>
        public static void Reset(CustomInputState state)
        {
            state.ResetForReuse();
            state.Touchpads = null;
            state.CapSense = null;
            state.NfcTag = null;
            state.Midi = null;
            state.AnalogKeys = null;
        }

        /// <summary>Applies one change to a device's state. A change this
        /// state has no room for is left out.</summary>
        public static void Apply(CustomInputState s, RecordingField field, int index, int a, int b, int c)
        {
            switch (field)
            {
                case RecordingField.Axis:
                    if ((uint)index < CustomInputState.MaxAxis) s.Axis[index] = a;
                    break;
                case RecordingField.Slider:
                    if ((uint)index < CustomInputState.MaxSliders) s.Sliders[index] = a;
                    break;
                case RecordingField.Hat:
                    if ((uint)index < CustomInputState.MaxPovs) s.Povs[index] = a;
                    break;
                case RecordingField.Button:
                    if ((uint)index < CustomInputState.MaxButtons) s.Buttons[index] = a != 0;
                    break;
                case RecordingField.Gyro: Set3(s.Gyro, a, b, c); break;
                case RecordingField.Accel: Set3(s.Accel, a, b, c); break;
                case RecordingField.GyroAux: Set3(s.GyroAux, a, b, c); break;
                case RecordingField.AccelAux: Set3(s.AccelAux, a, b, c); break;
                case RecordingField.TouchContact:
                    if (Finger(s, index, out var contactPad, out int contactFinger))
                    {
                        contactPad.FingerDown[contactFinger] = a != 0;
                        contactPad.FingerContactId[contactFinger] = b;
                    }
                    break;
                case RecordingField.TouchPosition:
                    if (Finger(s, index, out var placePad, out int placeFinger))
                    {
                        placePad.FingerX[placeFinger] = F(a);
                        placePad.FingerY[placeFinger] = F(b);
                        placePad.FingerPressure[placeFinger] = F(c);
                    }
                    break;
                case RecordingField.TouchClick:
                    if (s.Touchpads != null && (uint)index < (uint)s.Touchpads.Length && s.Touchpads[index] != null)
                        s.Touchpads[index].Clicked = a != 0;
                    break;
                case RecordingField.MidiNote:
                    if (s.Midi != null && (uint)index < MidiInputState.NoteCount) s.Midi.Notes[index] = a != 0;
                    break;
                case RecordingField.MidiCc:
                    if (s.Midi != null && (uint)index < MidiInputState.CcCount) s.Midi.Cc[index] = (byte)a;
                    break;
                case RecordingField.MidiCcUp:
                    if (s.Midi != null && (uint)index < MidiInputState.CcCount) s.Midi.CcUp[index] = a != 0;
                    break;
                case RecordingField.MidiCcDown:
                    if (s.Midi != null && (uint)index < MidiInputState.CcCount) s.Midi.CcDown[index] = a != 0;
                    break;
                case RecordingField.MidiPitchBend:
                    if (s.Midi != null) s.Midi.PitchBend = a;
                    break;
                case RecordingField.AnalogKey:
                    s.AnalogKeys?.Set(index, F(a));
                    break;
                case RecordingField.IrPointer:
                    s.Ir = new WiiIrState { X = F(a), Y = F(b), Detected = (c & 1) != 0, Calibrated = (c & 2) != 0 };
                    break;
                case RecordingField.JoyConIr: s.JoyConIrIntensity = F(a); break;
                case RecordingField.RingCon: s.RingConStrain = F(a); break;
                case RecordingField.JoyCon2Mouse:
                    s.JoyCon2MouseDX = F(a);
                    s.JoyCon2MouseDY = F(b);
                    break;
                case RecordingField.MouseRaw:
                    s.MouseRawDX = a;
                    s.MouseRawDY = b;
                    break;
                case RecordingField.MouseWheel:
                    s.MouseRawWheel = a;
                    s.MouseRawWheelH = b;
                    break;
                case RecordingField.CapSense:
                    if (s.CapSense != null && (uint)index < (uint)s.CapSense.Length) s.CapSense[index] = a != 0;
                    break;
                case RecordingField.NfcTag:
                    if (s.NfcTag != null && (uint)index < (uint)s.NfcTag.Length) s.NfcTag[index] = a != 0;
                    break;
                case RecordingField.Battery:
                    s.BatteryPercent = a;
                    s.BatteryCharging = b != 0;
                    break;
                case RecordingField.Shape:
                    ApplyShape(s, index, a);
                    break;
            }
        }

        private static void Set3(float[] target, int a, int b, int c)
        {
            target[0] = F(a);
            target[1] = F(b);
            target[2] = F(c);
        }

        private static bool Finger(CustomInputState s, int index, out TouchpadInputState pad, out int finger)
        {
            int p = index >> 4;
            finger = index & 15;
            pad = s.Touchpads != null && (uint)p < (uint)s.Touchpads.Length ? s.Touchpads[p] : null;
            return pad != null && finger < pad.MaxFingers;
        }

        private static void ApplyShape(CustomInputState s, int index, int value)
        {
            if (index == Shape.Touchpads)
            {
                int count = Math.Clamp(value, 0, MaxTouchpads);
                if (count == 0) { s.Touchpads = null; return; }
                if (s.Touchpads == null || s.Touchpads.Length != count)
                {
                    var pads = new TouchpadInputState[count];
                    if (s.Touchpads != null) Array.Copy(s.Touchpads, pads, Math.Min(count, s.Touchpads.Length));
                    s.Touchpads = pads;
                }
            }
            else if (index >= Shape.FirstTouchpadFingers && index < Shape.CapSense)
            {
                int pad = index - Shape.FirstTouchpadFingers;
                if (s.Touchpads == null || pad >= s.Touchpads.Length) return;
                // Under zero is a touchpad the device lists and has not made.
                if (value < 0) { s.Touchpads[pad] = null; return; }
                int fingers = Math.Min(value, MaxFingers);
                if (s.Touchpads[pad] == null || s.Touchpads[pad].MaxFingers != fingers)
                    s.Touchpads[pad] = new TouchpadInputState(fingers);
            }
            else if (index == Shape.CapSense)
            {
                int count = Math.Clamp(value, 0, MaxCapSense);
                s.CapSense = count == 0 ? null : Resized(s.CapSense, count);
            }
            else if (index == Shape.Nfc)
            {
                int count = Math.Clamp(value, 0, MaxNfcTags);
                s.NfcTag = count == 0 ? null : Resized(s.NfcTag, count);
            }
            else if (index == Shape.Midi)
            {
                if (value == 0) s.Midi = null;
                else s.Midi ??= new MidiInputState();
            }
            else if (index == Shape.AnalogKeys)
            {
                if (value == 0) s.AnalogKeys = null;
                else s.AnalogKeys ??= new AnalogKeyInputState();
            }
        }

        private static bool[] Resized(bool[] old, int count)
        {
            if (old != null && old.Length == count) return old;
            var made = new bool[count];
            if (old != null) Array.Copy(old, made, Math.Min(count, old.Length));
            return made;
        }

        /// <summary>The shape tokens of a state: what a reader makes before
        /// it applies anything else. A part the state lacks writes nothing.</summary>
        public static void AppendShape(StringBuilder sb, CustomInputState s)
        {
            if (s.Touchpads != null && s.Touchpads.Length > 0)
            {
                int count = Math.Min(s.Touchpads.Length, MaxTouchpads);
                Token(sb, RecordingField.Shape, Shape.Touchpads, count);
                for (int p = 0; p < count; p++)
                    Token(sb, RecordingField.Shape, Shape.FirstTouchpadFingers + p,
                        s.Touchpads[p] == null ? -1 : Math.Min(s.Touchpads[p].MaxFingers, MaxFingers));
            }
            if (s.CapSense != null && s.CapSense.Length > 0)
                Token(sb, RecordingField.Shape, Shape.CapSense, Math.Min(s.CapSense.Length, MaxCapSense));
            if (s.NfcTag != null && s.NfcTag.Length > 0)
                Token(sb, RecordingField.Shape, Shape.Nfc, Math.Min(s.NfcTag.Length, MaxNfcTags));
            if (s.Midi != null) Token(sb, RecordingField.Shape, Shape.Midi, 1);
            if (s.AnalogKeys != null) Token(sb, RecordingField.Shape, Shape.AnalogKeys, 1);
        }

        private static void Token(StringBuilder sb, RecordingField field, int index, int a, int b = 0, int c = 0)
        {
            sb.Append(' ');
            AppendToken(sb, field, index, a, b, c);
        }

        /// <summary>A device's whole state as tokens, each with a space
        /// before it: its shape, then every field that is not at rest. The
        /// counts of one pass are left out.</summary>
        public static void AppendFull(StringBuilder sb, CustomInputState s)
            => AppendFull(sb, s, RecordingControlKind.Generic);

        /// <summary>A device's whole state, its controls named for
        /// <paramref name="kind"/>.</summary>
        public static void AppendFull(StringBuilder sb, CustomInputState s, RecordingControlKind kind)
        {
            AppendShape(sb, s);
            for (int i = 0; i < CustomInputState.MaxAxis; i++)
                if (s.Axis[i] != 0) InputToken(sb, kind, RecordingField.Axis, i, s.Axis[i]);
            for (int i = 0; i < CustomInputState.MaxSliders; i++)
                if (s.Sliders[i] != 0) InputToken(sb, kind, RecordingField.Slider, i, s.Sliders[i]);
            for (int i = 0; i < CustomInputState.MaxPovs; i++)
                if (s.Povs[i] != -1) InputToken(sb, kind, RecordingField.Hat, i, s.Povs[i]);
            for (int i = 0; i < CustomInputState.MaxButtons; i++)
                if (s.Buttons[i]) InputToken(sb, kind, RecordingField.Button, i, 1);
            Full3Named(sb, kind, RecordingField.Gyro, s.Gyro);
            Full3Named(sb, kind, RecordingField.Accel, s.Accel);
            Full3Named(sb, kind, RecordingField.GyroAux, s.GyroAux);
            Full3Named(sb, kind, RecordingField.AccelAux, s.AccelAux);

            if (s.Touchpads != null)
            {
                for (int p = 0; p < s.Touchpads.Length && p < MaxTouchpads; p++)
                {
                    var pad = s.Touchpads[p];
                    if (pad == null) continue;
                    if (pad.Clicked) Token(sb, RecordingField.TouchClick, p, 1);
                    for (int f = 0; f < pad.MaxFingers && f < MaxFingers; f++)
                    {
                        int at = (p << 4) | f;
                        if (pad.FingerDown[f] || pad.FingerContactId[f] != -1)
                            Token(sb, RecordingField.TouchContact, at, pad.FingerDown[f] ? 1 : 0, pad.FingerContactId[f]);
                        int x = Bits(pad.FingerX[f]), y = Bits(pad.FingerY[f]), pressure = Bits(pad.FingerPressure[f]);
                        if ((x | y | pressure) != 0) Token(sb, RecordingField.TouchPosition, at, x, y, pressure);
                    }
                }
            }
            if (s.Midi != null)
            {
                for (int i = 0; i < MidiInputState.NoteCount; i++)
                    if (s.Midi.Notes[i]) Token(sb, RecordingField.MidiNote, i, 1);
                for (int i = 0; i < MidiInputState.CcCount; i++)
                {
                    if (s.Midi.Cc[i] != 0) Token(sb, RecordingField.MidiCc, i, s.Midi.Cc[i]);
                    if (s.Midi.CcUp[i]) Token(sb, RecordingField.MidiCcUp, i, 1);
                    if (s.Midi.CcDown[i]) Token(sb, RecordingField.MidiCcDown, i, 1);
                }
                if (s.Midi.PitchBend != MidiInputState.PitchBendCenter)
                    Token(sb, RecordingField.MidiPitchBend, 0, s.Midi.PitchBend);
            }
            if (s.AnalogKeys != null)
                for (int i = 0; i < s.AnalogKeys.Count; i++)
                    Token(sb, RecordingField.AnalogKey, s.AnalogKeys.Codes[i], Bits(s.AnalogKeys.Depths[i]));

            int irFlags = (s.Ir.Detected ? 1 : 0) | (s.Ir.Calibrated ? 2 : 0);
            if ((Bits(s.Ir.X) | Bits(s.Ir.Y) | irFlags) != 0)
                Token(sb, RecordingField.IrPointer, 0, Bits(s.Ir.X), Bits(s.Ir.Y), irFlags);
            if (Bits(s.JoyConIrIntensity) != 0) Token(sb, RecordingField.JoyConIr, 0, Bits(s.JoyConIrIntensity));
            if (Bits(s.RingConStrain) != 0) Token(sb, RecordingField.RingCon, 0, Bits(s.RingConStrain));
            if (s.CapSense != null)
                for (int i = 0; i < s.CapSense.Length && i < MaxCapSense; i++)
                    if (s.CapSense[i]) Token(sb, RecordingField.CapSense, i, 1);
            if (s.NfcTag != null)
                for (int i = 0; i < s.NfcTag.Length && i < MaxNfcTags; i++)
                    if (s.NfcTag[i]) Token(sb, RecordingField.NfcTag, i, 1);
            if (s.BatteryPercent != -1 || s.BatteryCharging)
                InputToken(sb, kind, RecordingField.Battery, 0, s.BatteryPercent, s.BatteryCharging ? 1 : 0);
        }

        private static void InputToken(StringBuilder sb, RecordingControlKind kind, RecordingField field, int index, int a, int b = 0, int c = 0)
        {
            sb.Append(' ');
            AppendInputToken(sb, kind, field, index, a, b, c);
        }

        private static void Full3Named(StringBuilder sb, RecordingControlKind kind, RecordingField field, float[] values)
        {
            int a = Bits(values[0]), b = Bits(values[1]), c = Bits(values[2]);
            if ((a | b | c) != 0) InputToken(sb, kind, field, 0, a, b, c);
        }

        private static void OutputToken(StringBuilder sb, RecordingField field, int a)
        {
            sb.Append(' ');
            AppendOutputToken(sb, field, 0, a, 0, 0);
        }

        private static void Full3(StringBuilder sb, RecordingField field, float[] values)
        {
            int a = Bits(values[0]), b = Bits(values[1]), c = Bits(values[2]);
            if ((a | b | c) != 0) Token(sb, field, 0, a, b, c);
        }

        // ─────────────────────────────────────────────
        //  A virtual controller's frame
        // ─────────────────────────────────────────────

        /// <summary>Applies one change to a virtual controller's frame.</summary>
        public static void Apply(RecordedOutputState s, RecordingField field, int index, int a, int b, int c)
        {
            switch (field)
            {
                case RecordingField.OutType: s.Type = a; break;
                case RecordingField.GpButtons: s.Buttons = (ushort)a; break;
                case RecordingField.GpLeftTrigger: s.LeftTrigger = (ushort)a; break;
                case RecordingField.GpRightTrigger: s.RightTrigger = (ushort)a; break;
                case RecordingField.GpLeftX: s.LeftX = (short)a; break;
                case RecordingField.GpLeftY: s.LeftY = (short)a; break;
                case RecordingField.GpRightX: s.RightX = (short)a; break;
                case RecordingField.GpRightY: s.RightY = (short)a; break;
                case RecordingField.GpExtra: s.Extra = a; break;
                case RecordingField.RawAxis:
                    if ((uint)index < RecordedOutputState.RawAxisCount) s.RawAxes[index] = (short)a;
                    break;
                case RecordingField.RawButtons:
                    if ((uint)index < RecordedOutputState.RawButtonWords) s.RawButtons[index] = (uint)a;
                    break;
                case RecordingField.RawHat:
                    if ((uint)index < RecordedOutputState.RawHatCount) s.RawHats[index] = a;
                    break;
                case RecordingField.KbmKeys:
                    if ((uint)index < RecordedOutputState.KeyWords) s.Keys[index] = ((ulong)(uint)b << 32) | (uint)a;
                    break;
                case RecordingField.KbmMouseButtons: s.MouseButtons = a; break;
                case RecordingField.KbmMove: s.MoveX = (short)a; s.MoveY = (short)b; break;
                case RecordingField.KbmScroll: s.Scroll = (short)a; s.ScrollH = (short)b; break;
                case RecordingField.KbmAbsolute: s.AbsoluteX = F(a); s.AbsoluteY = F(b); s.AbsoluteFlags = c; break;
                case RecordingField.KbmFlick: s.FlickX = a; break;
                case RecordingField.KbmGyro: s.GyroX = F(a); s.GyroY = F(b); break;
                case RecordingField.KbmTouch: s.TouchX = F(a); s.TouchY = F(b); break;
                case RecordingField.KbmCoast: s.CoastX = F(a); s.CoastY = F(b); break;
                case RecordingField.MidiOutCc:
                    if ((uint)index < RecordedOutputState.MidiCount) s.MidiCc[index] = (byte)a;
                    break;
                case RecordingField.MidiOutNote:
                    if ((uint)index < RecordedOutputState.MidiCount) s.MidiNotes[index] = a != 0;
                    break;
                // Hand 0 is the left and 1 the right, touch 0 the first and 1
                // the second. Any other index has no place in the frame.
                case RecordingField.VrHand:
                    if (index == 0) { s.Left.Buttons = (byte)a; s.Left.Trigger = (short)b; s.Left.Grip = (short)c; }
                    else if (index == 1) { s.Right.Buttons = (byte)a; s.Right.Trigger = (short)b; s.Right.Grip = (short)c; }
                    break;
                case RecordingField.VrStick:
                    if (index == 0) { s.Left.StickX = (short)a; s.Left.StickY = (short)b; }
                    else if (index == 1) { s.Right.StickX = (short)a; s.Right.StickY = (short)b; }
                    break;
                case RecordingField.OutTouch:
                    if (index == 0) { s.TouchDown0 = a != 0; s.TouchX0 = F(b); s.TouchY0 = F(c); }
                    else if (index == 1) { s.TouchDown1 = a != 0; s.TouchX1 = F(b); s.TouchY1 = F(c); }
                    break;
                case RecordingField.OutTouchClick: s.TouchClick = a != 0; break;
                case RecordingField.OutGyro: Set3(s.Gyro, a, b, c); break;
                case RecordingField.OutAccel: Set3(s.Accel, a, b, c); break;
            }
        }

        /// <summary>A virtual controller's whole frame as tokens, each with a
        /// space before it: its type, then every field that is not at rest.
        /// The counts of one pass are left out.</summary>
        public static void AppendFull(StringBuilder sb, RecordedOutputState s)
        {
            Token(sb, RecordingField.OutType, 0, s.Type);
            if (s.Buttons != 0) OutputToken(sb, RecordingField.GpButtons, s.Buttons);
            if (s.LeftTrigger != 0) OutputToken(sb, RecordingField.GpLeftTrigger, s.LeftTrigger);
            if (s.RightTrigger != 0) OutputToken(sb, RecordingField.GpRightTrigger, s.RightTrigger);
            if (s.LeftX != 0) OutputToken(sb, RecordingField.GpLeftX, s.LeftX);
            if (s.LeftY != 0) OutputToken(sb, RecordingField.GpLeftY, s.LeftY);
            if (s.RightX != 0) OutputToken(sb, RecordingField.GpRightX, s.RightX);
            if (s.RightY != 0) OutputToken(sb, RecordingField.GpRightY, s.RightY);
            if (s.Extra != 0) OutputToken(sb, RecordingField.GpExtra, s.Extra);
            for (int i = 0; i < RecordedOutputState.RawAxisCount; i++)
                if (s.RawAxes[i] != 0) Token(sb, RecordingField.RawAxis, i, s.RawAxes[i]);
            for (int i = 0; i < RecordedOutputState.RawButtonWords; i++)
                if (s.RawButtons[i] != 0) Token(sb, RecordingField.RawButtons, i, (int)s.RawButtons[i]);
            for (int i = 0; i < RecordedOutputState.RawHatCount; i++)
                if (s.RawHats[i] != -1) Token(sb, RecordingField.RawHat, i, s.RawHats[i]);
            for (int i = 0; i < RecordedOutputState.KeyWords; i++)
                if (s.Keys[i] != 0) Token(sb, RecordingField.KbmKeys, i, (int)(uint)s.Keys[i], (int)(uint)(s.Keys[i] >> 32));
            if (s.MouseButtons != 0) Token(sb, RecordingField.KbmMouseButtons, 0, s.MouseButtons);
            if ((s.MoveX | s.MoveY) != 0) Token(sb, RecordingField.KbmMove, 0, s.MoveX, s.MoveY);
            if ((s.Scroll | s.ScrollH) != 0) Token(sb, RecordingField.KbmScroll, 0, s.Scroll, s.ScrollH);
            if ((Bits(s.AbsoluteX) | Bits(s.AbsoluteY) | s.AbsoluteFlags) != 0)
                Token(sb, RecordingField.KbmAbsolute, 0, Bits(s.AbsoluteX), Bits(s.AbsoluteY), s.AbsoluteFlags);
            for (int i = 0; i < RecordedOutputState.MidiCount; i++)
            {
                if (s.MidiCc[i] != 0) Token(sb, RecordingField.MidiOutCc, i, s.MidiCc[i]);
                if (s.MidiNotes[i]) Token(sb, RecordingField.MidiOutNote, i, 1);
            }
            FullHand(sb, 0, in s.Left);
            FullHand(sb, 1, in s.Right);
            if (s.TouchDown0 || (Bits(s.TouchX0) | Bits(s.TouchY0)) != 0)
                Token(sb, RecordingField.OutTouch, 0, s.TouchDown0 ? 1 : 0, Bits(s.TouchX0), Bits(s.TouchY0));
            if (s.TouchDown1 || (Bits(s.TouchX1) | Bits(s.TouchY1)) != 0)
                Token(sb, RecordingField.OutTouch, 1, s.TouchDown1 ? 1 : 0, Bits(s.TouchX1), Bits(s.TouchY1));
            if (s.TouchClick) Token(sb, RecordingField.OutTouchClick, 0, 1);
            Full3(sb, RecordingField.OutGyro, s.Gyro);
            Full3(sb, RecordingField.OutAccel, s.Accel);
        }

        private static void FullHand(StringBuilder sb, int hand, in VrHandRaw raw)
        {
            if ((raw.Buttons | (ushort)raw.Trigger | (ushort)raw.Grip) != 0)
                Token(sb, RecordingField.VrHand, hand, raw.Buttons, raw.Trigger, raw.Grip);
            if ((raw.StickX | raw.StickY) != 0)
                Token(sb, RecordingField.VrStick, hand, raw.StickX, raw.StickY);
        }

        // ─────────────────────────────────────────────
        //  Lines
        // ─────────────────────────────────────────────

        /// <summary>Cuts an event line into its time, layer, subject and
        /// payload. False for a line that is none.</summary>
        public static bool TrySplitEvent(ReadOnlySpan<char> line, out long microseconds, out char layer,
            out ReadOnlySpan<char> subject, out ReadOnlySpan<char> payload)
        {
            microseconds = 0;
            layer = '\0';
            subject = payload = default;
            int first = line.IndexOf(' ');
            if (first <= 0 || !TryParseTime(line.Slice(0, first), out microseconds)) return false;
            var rest = line.Slice(first + 1);
            if (rest.Length < 3 || rest[1] != ' ') return false;
            layer = rest[0];
            rest = rest.Slice(2);
            int second = rest.IndexOf(' ');
            subject = second < 0 ? rest : rest.Slice(0, second);
            payload = second < 0 ? default : rest.Slice(second + 1);
            return subject.Length > 0;
        }

        /// <summary>The value of a named token in a payload of tokens, such
        /// as the "sent" of a sent line. False when the payload has none.</summary>
        public static bool TryGetValue(ReadOnlySpan<char> payload, ReadOnlySpan<char> name, out ReadOnlySpan<char> value)
        {
            value = default;
            while (!payload.IsEmpty)
            {
                int space = payload.IndexOf(' ');
                var token = space < 0 ? payload : payload.Slice(0, space);
                payload = space < 0 ? default : payload.Slice(space + 1);
                if (token.Length > name.Length && token[name.Length] == '=' && token.StartsWith(name))
                {
                    value = token.Slice(name.Length + 1);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Appends " key=value" to a line of facts, such as a
        /// device's line in the devices part. A value with a space, a quote
        /// or nothing in it goes in quotes.</summary>
        public static void AppendFact(StringBuilder sb, string key, string value)
        {
            value ??= string.Empty;
            sb.Append(' ').Append(key).Append('=');
            bool plain = value.Length > 0;
            foreach (char ch in value)
                if (ch is ' ' or '"' or '\\' or '\t' or '\r' or '\n') { plain = false; break; }
            if (plain) { sb.Append(value); return; }
            sb.Append('"');
            foreach (char ch in value)
            {
                if (ch is '"' or '\\') sb.Append('\\');
                sb.Append(ch is '\r' or '\n' or '\t' ? ' ' : ch);
            }
            sb.Append('"');
        }

        /// <summary>Appends " key=number" to a line of facts.</summary>
        public static void AppendFact(StringBuilder sb, string key, long value)
        {
            sb.Append(' ').Append(key).Append('=');
            AppendInt(sb, value);
        }

        /// <summary>Reads a line of facts back: what
        /// <see cref="AppendFact(StringBuilder, string, string)"/> wrote,
        /// each key once. Text that is no key and value is stepped over.</summary>
        public static Dictionary<string, string> ParseFacts(ReadOnlySpan<char> text)
        {
            var facts = new Dictionary<string, string>(StringComparer.Ordinal);
            var value = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && text[i] == ' ') i++;
                int keyStart = i;
                while (i < text.Length && text[i] != '=' && text[i] != ' ') i++;
                if (i >= text.Length || text[i] != '=') continue;
                string key = text.Slice(keyStart, i - keyStart).ToString();
                i++;
                value.Clear();
                if (i < text.Length && text[i] == '"')
                {
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) i++;
                        value.Append(text[i++]);
                    }
                    i++;
                }
                else
                {
                    while (i < text.Length && text[i] != ' ') value.Append(text[i++]);
                }
                if (key.Length > 0) facts[key] = value.ToString();
            }
            return facts;
        }

        /// <summary>A subject as its number: 3 for D3 or C3, and 0 for a
        /// subject that is no <paramref name="letter"/> and a number.</summary>
        public static int SubjectNumber(ReadOnlySpan<char> subject, char letter)
            => subject.Length > 1 && subject[0] == letter
               && int.TryParse(subject.Slice(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }
}
