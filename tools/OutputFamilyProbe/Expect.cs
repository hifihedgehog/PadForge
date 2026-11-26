using System.Globalization;
using System.Text;
using HIDMaestro;
using HIDMaestro.Internal;
using PadForge.Engine;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>The outcome of one comparison: whether the report carries the
    /// known state, the fields it compared, and the first field that did not
    /// match.</summary>
    internal sealed class Verdict
    {
        public bool Pass;
        public string Compared = string.Empty;
        public string Failure = string.Empty;
        public override string ToString() => Pass ? $"PASS ({Compared})" : $"FAIL ({Failure}; compared {Compared})";
    }

    /// <summary>Compares what Windows delivered with the known state
    /// (<see cref="KnownState"/>). Every offset comes from the consumer's
    /// side or from HIDMaestro's own definition of the report, never from a
    /// PadForge packer, so the self-check can catch a packer and the probe
    /// disagreeing.</summary>
    internal static class Expect
    {
        private sealed class Builder
        {
            private readonly List<string> _compared = new();
            private string _failure;
            public void Check(string field, bool ok, string detail)
            {
                _compared.Add(field);
                if (!ok && _failure == null) _failure = $"{field}: {detail}";
            }
            public Verdict Done(string none)
            {
                if (_compared.Count == 0) return new Verdict { Pass = false, Failure = none, Compared = "nothing" };
                return new Verdict { Pass = _failure == null, Failure = _failure ?? string.Empty, Compared = string.Join(", ", _compared) };
            }
        }

        private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        private static void Axis(Builder b, string field, double got, double want)
            => b.Check(field, Math.Abs(got - want) <= KnownState.Tolerance, $"read {F(got)}, want {F(want)}");

        private static double ExpectedFor(string semantic) => semantic switch
        {
            "leftStickX" => KnownState.LeftX,
            "leftStickY" => KnownState.LeftY,
            "rightStickX" => KnownState.RightX,
            "rightStickY" => KnownState.RightY,
            "leftTrigger" => KnownState.LeftTrigger,
            "rightTrigger" => KnownState.RightTrigger,
            _ => double.NaN,
        };

        /// <summary>WireArith.Linear read backwards
        /// (sdk/HIDMaestro.Core/Internal/WireArith.cs:36-46): the wire value's
        /// place between min and max, turned around for an inverted
        /// field.</summary>
        private static double Unlinear(long raw, int min, int max, bool invert)
        {
            double c = (raw - (double)min) / ((double)max - min);
            return invert ? 1.0 - c : c;
        }

        private static (int lo, int hi) Bits(string bits, int count)
        {
            if (string.IsNullOrEmpty(bits)) return (0, Math.Max(0, count - 1));
            var parts = bits.Split('-');
            int lo = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int hi = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : lo;
            return (lo, hi);
        }

        private static bool Bit(byte[] wire, int at, int bit)
        {
            int index = at + bit / 8;
            return index < wire.Length && (wire[index] & (1 << (bit % 8))) != 0;
        }

        /// <summary>The value a button-mask position names, or null for a
        /// position the known state says nothing about. The names are
        /// HIDMaestro's (VendorBlobProgram.cs:560-600 and VendorBlobCodec.cs
        /// ButtonMask): an HMButton name, "#n" for HMButton bit n, the two
        /// digital trigger bits, the four D-pad directions, and the two pad
        /// touches, which the known state does not drive.</summary>
        private static bool? ButtonExpected(string name, HMButton expected, TriggerThresholdSpec threshold, double leftPull)
        {
            if (string.IsNullOrEmpty(name) || name == "_") return null;
            if (name == "LT_DIGITAL") return TriggerOn(leftPull, threshold);
            if (name == "RT_DIGITAL") return TriggerOn(KnownState.RightTrigger, threshold);
            if (name.StartsWith("DPAD_", StringComparison.Ordinal)) return false;
            if (name is "LEFTPAD_TOUCH" or "RIGHTPAD_TOUCH") return null;
            if (name.StartsWith('#') && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n < 32)
                return ((uint)expected & (1u << n)) != 0;
            if (Enum.TryParse(name, ignoreCase: false, out HMButton flag) && flag != HMButton.None)
                return (expected & flag) != 0;
            return null;
        }

        /// <summary>A digital trigger bit: at a trigger threshold's count
        /// where the mask carries one, else above zero
        /// (VendorBlobCodec.cs ButtonMask, TriggerBit).</summary>
        private static bool TriggerOn(double pull, TriggerThresholdSpec threshold)
        {
            if (threshold?.Max is int max && threshold.At is int at && max > 0)
                return pull * max >= at;
            return pull > 0;
        }

        /// <summary>The bytes Windows hands a reader of an id-less report
        /// start with a zero id. The field offsets of such a spec count from
        /// the data. A spec with an id counts its id as byte 0
        /// (ControllerProfile.cs ExtendedReportSpec.Size).</summary>
        private static byte[] Wire(byte[] delivered, byte reportId)
            => reportId == 0 ? delivered.AsSpan(1).ToArray() : delivered;

        /// <summary>The report against HIDMaestro's own definition of it, the
        /// profile's extendedReport fields: the stick, trigger, hat and button
        /// fields, each decoded as VendorBlobCodec.EncodeInput encodes it
        /// (sdk/HIDMaestro.Core/Internal/VendorBlobCodec.cs:205-640), with the
        /// default ranges of VendorBlobProgram.DefaultRange (:742-749).
        /// <paramref name="leftPull"/> is the left trigger's pull the slot's
        /// state carries: full on a layout with triggers, none on a Nintendo
        /// layout, whose ZL is a button (NintendoPreviewMap.TriggerCount).
        /// <paramref name="stickYUp"/> reads a 12-bit pair's Y with up high, as
        /// the pad sends it and SDL's Switch 2 driver reads it
        /// (SDL_hidapi_switch2.c HandleSwitchProState, 1299-1370, maps Y
        /// inverted), for a persona whose report its own state block builds
        /// with Y inverted, where the spec's field does not say so.</summary>
        internal static Verdict BySpec(ExtendedReportSpec spec, byte[] delivered, HMButton expected, double leftPull, bool stickYUp = false)
        {
            var b = new Builder();
            byte id = spec.ReportIdByte;
            if (delivered.Length == 0 || delivered[0] != id)
                return new Verdict { Failure = $"report id {(delivered.Length > 0 ? delivered[0] : -1):X2}, want {id:X2}", Compared = "report id" };
            b.Check("report id", true, string.Empty);
            var wire = Wire(delivered, id);
            foreach (var f in spec.Fields)
            {
                if (f.Byte is not int at || at < 0) continue;
                switch (f.Type)
                {
                    case "uint8-axis":
                    {
                        double want = ExpectedFor(f.Semantic);
                        if (double.IsNaN(want) || at >= wire.Length) break;
                        int center = f.Center ?? 128;
                        int min = center == 128 ? (f.Min ?? 0) : center - 127;
                        int max = center == 128 ? (f.Max ?? 255) : center + 127;
                        Axis(b, $"{f.Semantic}@{at}", Unlinear(wire[at], min, max, f.Invert == true), want);
                        break;
                    }
                    case "uint8-trigger":
                    {
                        double want = ExpectedFor(f.Semantic);
                        if (double.IsNaN(want) || at >= wire.Length) break;
                        Axis(b, $"{f.Semantic}@{at}", Unlinear(wire[at], f.Min ?? 0, f.Max ?? 255, f.Invert == true), want);
                        break;
                    }
                    case "int16-axis":
                    {
                        double want = ExpectedFor(f.Semantic);
                        if (double.IsNaN(want) || at + 1 >= wire.Length) break;
                        short v = (short)(wire[at] | (wire[at + 1] << 8));
                        Axis(b, $"{f.Semantic}@{at}", Unlinear(v, f.Min ?? -32767, f.Max ?? 32767, f.Invert == true), want);
                        break;
                    }
                    case "uint16-trigger":
                    {
                        double want = ExpectedFor(f.Semantic);
                        if (double.IsNaN(want) || at + 1 >= wire.Length) break;
                        int v = wire[at] | (wire[at + 1] << 8);
                        Axis(b, $"{f.Semantic}@{at}", Unlinear(v, f.Min ?? 0, f.Max ?? 65535, f.Invert == true), want);
                        break;
                    }
                    case "stick12-pair":
                    {
                        if (at + 2 >= wire.Length) break;
                        bool right = f.Semantic == "rightStick";
                        if (!right && f.Semantic != "leftStick") break;
                        int x = wire[at] | ((wire[at + 1] & 0x0F) << 8);
                        int y = (wire[at + 1] >> 4) | (wire[at + 2] << 4);
                        int min = f.Min ?? 0, max = f.Max ?? 4095;
                        Axis(b, $"{f.Semantic}X@{at}", Unlinear(x, min, max, false), right ? KnownState.RightX : KnownState.LeftX);
                        Axis(b, $"{f.Semantic}Y@{at}", Unlinear(y, min, max, f.Invert == true || stickYUp), right ? KnownState.RightY : KnownState.LeftY);
                        break;
                    }
                    case "hat-octant":
                    {
                        if (at >= wire.Length) break;
                        var (lo, hi) = Bits(f.Bits, 8);
                        int width = hi - lo + 1;
                        int v = (wire[at] >> lo) & ((1 << width) - 1);
                        int neutral = f.NeutralValue ?? 8;
                        b.Check($"hat@{at}", v == neutral, $"read {v}, want {neutral} (centered)");
                        break;
                    }
                    case "button-mask":
                    {
                        if (f.Buttons == null) break;
                        var (lo, _) = Bits(f.Bits, f.Buttons.Count);
                        for (int i = 0; i < f.Buttons.Count; i++)
                        {
                            bool? want = ButtonExpected(f.Buttons[i], expected, f.TriggerThreshold, leftPull);
                            if (want == null) continue;
                            bool got = Bit(wire, at, lo + i);
                            b.Check($"{f.Buttons[i]}@{at}.{lo + i}", got == want.Value, $"read {(got ? 1 : 0)}, want {(want.Value ? 1 : 0)}");
                        }
                        break;
                    }
                }
            }
            return b.Done("the spec names no field the known state sets");
        }

        /// <summary>The first Pro Controller's full report 0x30. HIDMaestro's
        /// switch-pro profile builds it from its stateBlock, bytes 2 to 10 of
        /// which land at report bytes 3 to 11 (profiles/nintendo/switch-pro.json
        /// inputReports "full": copy "2-10" to 3), with the stateBlock fields
        /// byte 2 ["#2","#3","#0","#1",_,_,"#5","#7"] and the left stick as a
        /// 12-bit pair at stateBlock byte 5, min 512, max 3584, Y inverted.
        /// SDL reads the same bytes as SwitchStatePacket_full_t
        /// (SDL_hidapi_switch.c, rgucButtons[0] at report byte 3, A = 0x08,
        /// rgucJoystickLeft at byte 6).</summary>
        internal static Verdict SwitchProFull(byte[] delivered, HMButton expected)
        {
            var b = new Builder();
            if (delivered.Length < 12 || delivered[0] != 0x30)
                return new Verdict { Failure = "not report 0x30", Compared = "report id" };
            b.Check("report id", true, string.Empty);
            string[] byte3 = { "#2", "#3", "#0", "#1", "_", "_", "#5", "#7" };
            for (int i = 0; i < byte3.Length; i++)
            {
                bool? want = ButtonExpected(byte3[i], expected, null, 0);
                if (want == null) continue;
                bool got = (delivered[3] & (1 << i)) != 0;
                b.Check($"{byte3[i]}@3.{i}", got == want.Value, $"read {(got ? 1 : 0)}, want {(want.Value ? 1 : 0)}");
            }
            int x = delivered[6] | ((delivered[7] & 0x0F) << 8);
            int y = (delivered[7] >> 4) | (delivered[8] << 4);
            Axis(b, "leftStickX@6", Unlinear(x, 512, 3584, false), KnownState.LeftX);
            Axis(b, "leftStickY@6", Unlinear(y, 512, 3584, true), KnownState.LeftY);
            return b.Done("nothing");
        }

        /// <summary>The profile that sends <see cref="SixaxisJoystick"/>'s
        /// report.</summary>
        internal const string SixaxisProfileId = "dualshock-3-full";

        /// <summary>The 12-byte joystick report of a DualShock 3 in the form
        /// Sony's sixaxis.sys and DsHidMini's SXS mode present, the report
        /// dualshock-3-full sends with no report id (its descriptor declares
        /// none, so Windows heads the data with a zero id). Every offset is
        /// DsHidMini's DS3_RAW_TO_SIXAXIS_HID_INPUT_REPORT (driver/DsHid.c
        /// 585-676): data byte 0 has Triangle, Circle, Cross and Square in
        /// bits 0 to 3 and L2, R2, L1 and R1 in bits 4 to 7; byte 1 has Start,
        /// Select, L3, R3 and PS in bits 0 to 4; the low nibble of byte 3 is
        /// the hat, 8 at rest; bytes 4 to 7 are the sticks; bytes 10 and 11
        /// are L2 and R2, inverted ("Trigger axes (inverted)", 0xFF less the
        /// pressure).</summary>
        internal static Verdict SixaxisJoystick(byte[] delivered, HMButton expected)
        {
            var b = new Builder();
            if (delivered.Length < 13 || delivered[0] != 0x00)
                return new Verdict { Failure = $"not the 12-byte joystick report after a zero id ({delivered.Length} bytes)", Compared = "report id" };
            b.Check("report id", true, string.Empty);
            const int d = 1;
            (string Name, HMButton Button)[] byte0 =
            {
                ("Triangle", HMButton.Y), ("Circle", HMButton.B), ("Cross", HMButton.A), ("Square", HMButton.X),
            };
            for (int i = 0; i < byte0.Length; i++)
            {
                bool want = (expected & byte0[i].Button) != 0;
                bool got = (delivered[d] & (1 << i)) != 0;
                b.Check($"{byte0[i].Name}@0.{i}", got == want, $"read {(got ? 1 : 0)}, want {(want ? 1 : 0)}");
            }
            bool l2 = (delivered[d] & 0x10) != 0, r2 = (delivered[d] & 0x20) != 0;
            b.Check("L2@0.4", l2 == KnownState.LeftTrigger > 0, $"read {(l2 ? 1 : 0)}");
            b.Check("R2@0.5", r2 == KnownState.RightTrigger > 0, $"read {(r2 ? 1 : 0)}");
            (string Name, int Byte, int Bit, HMButton Button)[] rest =
            {
                ("L1", 0, 6, HMButton.LeftBumper), ("R1", 0, 7, HMButton.RightBumper),
                ("Start", 1, 0, HMButton.Start), ("Select", 1, 1, HMButton.Back), ("L3", 1, 2, HMButton.LeftStick),
                ("R3", 1, 3, HMButton.RightStick), ("PS", 1, 4, HMButton.Guide),
            };
            foreach (var (name, at, bit, button) in rest)
            {
                bool want = (expected & button) != 0;
                bool got = (delivered[d + at] & (1 << bit)) != 0;
                b.Check($"{name}@{at}.{bit}", got == want, $"read {(got ? 1 : 0)}, want {(want ? 1 : 0)}");
            }
            b.Check("hat@3", (delivered[d + 3] & 0x0F) == 8, $"read {delivered[d + 3] & 0x0F}, want 8");
            Axis(b, "leftStickX@4", delivered[d + 4] / 255.0, KnownState.LeftX);
            Axis(b, "leftStickY@5", delivered[d + 5] / 255.0, KnownState.LeftY);
            Axis(b, "rightStickX@6", delivered[d + 6] / 255.0, KnownState.RightX);
            Axis(b, "rightStickY@7", delivered[d + 7] / 255.0, KnownState.RightY);
            Axis(b, "leftTrigger@10 (inverted)", (0xFF - delivered[d + 10]) / 255.0, KnownState.LeftTrigger);
            Axis(b, "rightTrigger@11 (inverted)", (0xFF - delivered[d + 11]) / 255.0, KnownState.RightTrigger);
            return b.Done("nothing");
        }

        /// <summary>SInput's report 0x01 as the device builds it, south at bit 0
        /// of byte 3 (SINPUT-LIB-HID include/sinput_lib_types.h:226), the two
        /// trigger bits at byte 4 bits 4 and 5 (sinput_lib_types.h:242-249),
        /// and the sticks and triggers signed 16-bit at 7, 9, 11, 13, 15 and
        /// 17 (SDL_hidapi_sinput.c:69-74), the sticks Y down positive and a
        /// trigger from -32768 at rest (SDL_hidapi_sinput.c:889-930). The SDL
        /// fork names bit 0 EAST (SDL_hidapi_sinput.c:90) and reads the bits in
        /// order with no name (:848-870), so the device side's name is the
        /// one used here.</summary>
        internal static Verdict SInput(byte[] delivered)
        {
            var b = new Builder();
            if (delivered.Length < 19 || delivered[0] != 0x01)
                return new Verdict { Failure = $"report id {(delivered.Length > 0 ? delivered[0] : -1):X2}, want 01", Compared = "report id" };
            b.Check("report id", true, string.Empty);
            b.Check("south@3.0", (delivered[3] & 0x01) != 0, "A not set");
            b.Check("east,west,north@3.1-3", (delivered[3] & 0x0E) == 0, $"byte 3 is {delivered[3]:X2}");
            b.Check("LT_DIGITAL@4.4", (delivered[4] & 0x10) != 0, "left trigger bit clear");
            b.Check("RT_DIGITAL@4.5", (delivered[4] & 0x20) == 0, "right trigger bit set");
            double S(int at) => ((short)(delivered[at] | (delivered[at + 1] << 8)) + 32768.0) / 65535.0;
            Axis(b, "leftStickX@7", S(7), KnownState.LeftX);
            Axis(b, "leftStickY@9", S(9), KnownState.LeftY);
            Axis(b, "rightStickX@11", S(11), KnownState.RightX);
            Axis(b, "rightStickY@13", S(13), KnownState.RightY);
            Axis(b, "leftTrigger@15", S(15), KnownState.LeftTrigger);
            Axis(b, "rightTrigger@17", S(17), KnownState.RightTrigger);
            return b.Done("nothing");
        }

        /// <summary>The report of a descriptor PadForge built for an Extended
        /// slot (Step 5's CreateHMaestroController, its HidDescriptorBuilder
        /// block): one
        /// AddStick per stick, two 16-bit unsigned axes, then one 16-bit axis
        /// per trigger, one 8-bit hat per POV, then the buttons, a bit each, in
        /// declaration order, little-endian, the order ExtendedReportPacker
        /// documents and states the SDK's writer shares
        /// (ExtendedReportPacker.cs:16-25). Report id 0x01 heads it when the
        /// force feedback block is on: AddPidFfbBlock injects that id
        /// (Step 5's CreateHMaestroController, its AddPidFfbBlock call).</summary>
        internal static Verdict ExtendedLayout(byte[] delivered, in CustomControllerLayout layout, bool reportIdOne)
        {
            var b = new Builder();
            byte want = reportIdOne ? (byte)0x01 : (byte)0x00;
            if (delivered.Length == 0 || delivered[0] != want)
                return new Verdict { Failure = $"report id {(delivered.Length > 0 ? delivered[0] : -1):X2}, want {want:X2}", Compared = "report id" };
            b.Check("report id", true, string.Empty);
            int off = 1;
            double U(int at) => at + 1 < delivered.Length ? (delivered[at] | (delivered[at + 1] << 8)) / 65535.0 : double.NaN;
            double[] stickWant = { KnownState.LeftX, KnownState.LeftY, KnownState.RightX, KnownState.RightY };
            for (int s = 0; s < layout.Sticks; s++)
            {
                if (s < 2)
                {
                    Axis(b, $"stick{s}X@{off}", U(off), stickWant[s * 2]);
                    Axis(b, $"stick{s}Y@{off + 2}", U(off + 2), stickWant[s * 2 + 1]);
                }
                off += 4;
            }
            for (int t = 0; t < layout.Triggers; t++)
            {
                if (t < 2) Axis(b, $"trigger{t}@{off}", U(off), t == 0 ? KnownState.LeftTrigger : KnownState.RightTrigger);
                off += 2;
            }
            for (int h = 0; h < layout.Povs; h++)
            {
                if (off < delivered.Length) b.Check($"hat{h}@{off}", delivered[off] == 8, $"read {delivered[off]}, want 8 (centered)");
                off++;
            }
            for (int i = 0; i < Math.Min(layout.Buttons, 40); i++)
            {
                int at = off + i / 8;
                if (at >= delivered.Length) break;
                bool got = (delivered[at] & (1 << (i % 8))) != 0;
                bool wantOn = i == 0;
                if (i < 8 || got != wantOn)
                    b.Check($"button{i + 1}@{at}.{i % 8}", got == wantOn, $"read {(got ? 1 : 0)}, want {(wantOn ? 1 : 0)}");
            }
            return b.Done("nothing");
        }

        /// <summary>The report through Windows' own HID parser on the
        /// virtual device's preparsed data: the profile's stick and trigger
        /// usages (HMProfile.Sticks and Triggers, the keys PadForge writes,
        /// the axis keys HMaestroVirtualController's constructor caches and
        /// BuildRawHidState writes) and its button
        /// usages (by the layout's face roles on a fixed-layout slot, else
        /// HMProfile.ButtonMap: HMButton bit to descriptor button, identity
        /// when null), for a report whose layout is the descriptor's
        /// alone.</summary>
        internal static Verdict ByHidParser(OpenHid hid, byte[] delivered, HMProfile profile, HMButton expected, bool byRole)
        {
            var b = new Builder();
            var caps = ValueCaps(hid.Preparsed);
            void Value(string name, HMAxis axis, double want)
            {
                if (axis == HMAxis.None) return;
                ushort page = (ushort)((ushort)axis >> 8), usage = (ushort)((ushort)axis & 0xFF);
                var cap = caps.FirstOrDefault(c => c.UsagePage == page && c.ReportID == delivered[0]
                    && (c.IsRange != 0 ? usage >= c.UsageMin && usage <= c.UsageMax : c.UsageMin == usage));
                if (cap.BitSize == 0) return;
                if (Native.HidP_GetUsageValue(Native.HidP_Input, page, 0, usage, out uint raw, hid.Preparsed, delivered, delivered.Length)
                    != Native.HIDP_STATUS_SUCCESS) return;
                long v = raw;
                if (cap.LogicalMin < 0 && cap.BitSize < 32 && (raw & (1u << (cap.BitSize - 1))) != 0)
                    v = raw - (1L << cap.BitSize);
                long max = cap.LogicalMax, min = cap.LogicalMin;
                if (max <= min) max = (1L << cap.BitSize) - 1;
                Axis(b, $"{name} {page:X2}:{usage:X2}", (v - (double)min) / (max - min), want);
            }
            var sticks = profile.Sticks;
            var triggers = profile.Triggers;
            if (sticks.Count > 0) { Value("leftStickX", sticks[0].XAxis, KnownState.LeftX); Value("leftStickY", sticks[0].YAxis, KnownState.LeftY); }
            if (sticks.Count > 1) { Value("rightStickX", sticks[1].XAxis, KnownState.RightX); Value("rightStickY", sticks[1].YAxis, KnownState.RightY); }
            if (triggers.Count > 0) Value("leftTrigger", triggers[0].Axis, KnownState.LeftTrigger);
            if (triggers.Count > 1) Value("rightTrigger", triggers[1].Axis, KnownState.RightTrigger);

            var usages = new ushort[256];
            int n = usages.Length;
            if (Native.HidP_GetUsages(Native.HidP_Input, 0x09, 0, usages, ref n, hid.Preparsed, delivered, delivered.Length)
                == Native.HIDP_STATUS_SUCCESS)
            {
                var pressed = new HashSet<int>(usages.Take(n).Select(u => (int)u));
                var map = profile.ButtonMap;
                // A fixed-layout slot means a role by each HMButton bit, so
                // its A is read where the profile's layout puts the face A
                // button (HMProfile.AsGamepad, faceButtons), which is what a
                // game reads it as. HIDMaestro's encoder places the bit by the
                // button map alone (HidReportBuilder.cs:1165-1190), so a
                // profile whose map and layout disagree fails here. A raw slot
                // numbers its rows by descriptor button, so its bits are read
                // where the encoder puts them.
                var face = profile.AsGamepad()?.FaceButtons;
                int ByRole(int bit)
                {
                    if (!byRole || face == null) return -1;
                    var roles = bit switch
                    {
                        0 => new[] { HMButtonRole.FaceA, HMButtonRole.FaceCross },
                        1 => new[] { HMButtonRole.FaceB, HMButtonRole.FaceCircle },
                        2 => new[] { HMButtonRole.FaceX, HMButtonRole.FaceSquare },
                        _ => new[] { HMButtonRole.FaceY, HMButtonRole.FaceTriangle },
                    };
                    foreach (var binding in face)
                        if (binding != null && Array.IndexOf(roles, binding.Role) >= 0) return binding.ButtonIndex + 1;
                    return -1;
                }
                int UsageOf(int bit) => ByRole(bit) is int u && u > 0 ? u : (map != null && bit < map.Length ? map[bit] : bit) + 1;
                for (int bit = 0; bit < 4; bit++)
                {
                    bool want = ((uint)expected & (1u << bit)) != 0;
                    int usage = UsageOf(bit);
                    bool got = pressed.Contains(usage);
                    b.Check($"HMButton bit {bit} as button {usage}", got == want, $"read {(got ? 1 : 0)}, want {(want ? 1 : 0)}");
                }
            }
            return b.Done("the descriptor declares none of the profile's sticks, triggers or buttons in this report");
        }

        private static Native.HIDP_VALUE_CAPS[] ValueCaps(IntPtr preparsed)
        {
            var caps = new Native.HIDP_CAPS { Reserved = new ushort[17] };
            if (Native.HidP_GetCaps(preparsed, ref caps) != Native.HIDP_STATUS_SUCCESS) return Array.Empty<Native.HIDP_VALUE_CAPS>();
            ushort count = caps.NumberInputValueCaps;
            if (count == 0) return Array.Empty<Native.HIDP_VALUE_CAPS>();
            var list = new Native.HIDP_VALUE_CAPS[count];
            return Native.HidP_GetValueCaps(Native.HidP_Input, list, ref count, preparsed) == Native.HIDP_STATUS_SUCCESS
                ? list.Take(count).ToArray() : Array.Empty<Native.HIDP_VALUE_CAPS>();
        }

        /// <summary>XInput's state against the known state, the fields
        /// HIDMaestro's test/Latency.cs reads (wButtons, the thumbs and the
        /// triggers). Gamepad.A is XINPUT_GAMEPAD_A 0x1000, and the thumbs keep
        /// XInput's convention, Y up positive.</summary>
        internal static Verdict XInput(in Native.XINPUT_STATE s)
        {
            var b = new Builder();
            var g = s.Gamepad;
            b.Check("wButtons", g.wButtons == 0x1000, $"read {g.wButtons:X4}, want 1000");
            b.Check("sThumbLX", g.sThumbLX >= 32000, $"read {g.sThumbLX}, want 32767");
            b.Check("sThumbLY", Math.Abs(g.sThumbLY - 16384) <= 600, $"read {g.sThumbLY}, want 16384");
            b.Check("sThumbRX", Math.Abs(g.sThumbRX + 8192) <= 600, $"read {g.sThumbRX}, want -8192");
            b.Check("sThumbRY", Math.Abs(g.sThumbRY) <= 600, $"read {g.sThumbRY}, want 0");
            b.Check("bLeftTrigger", g.bLeftTrigger >= 250, $"read {g.bLeftTrigger}, want 255");
            b.Check("bRightTrigger", g.bRightTrigger <= 5, $"read {g.bRightTrigger}, want 0");
            return b.Done("nothing");
        }

        internal static string Hex(byte[] bytes, int max = 64)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(bytes.Length, max); i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (bytes.Length > max) sb.Append(" ...");
            return sb.ToString();
        }
    }
}
