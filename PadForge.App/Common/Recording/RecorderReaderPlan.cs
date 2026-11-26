using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Recording;

namespace PadForge.Common.Recording
{
    /// <summary>Which second reader a device has (#600 item 32).</summary>
    internal enum ReaderClass : byte
    {
        /// <summary>None, for the reason the classification gives.</summary>
        None,
        /// <summary>A pad at an XInput number, compared with the read of that number through the same library.</summary>
        XInput,
        /// <summary>SDL reads it through DirectInput: Raw Input's copy through
        /// DirectInput's own objects and SDL's numbering.</summary>
        DirectInput,
        /// <summary>SDL reads it through a HIDAPI driver whose meaning is
        /// proven: Raw Input's copy by the proven usage of each control.</summary>
        Hidapi,
        /// <summary>PadForge's SpaceMouse reader: Raw Input's copy by usage.</summary>
        SpaceMouse,
    }

    /// <summary>How a gamepad or joystick control is rebuilt from one
    /// report usage, for a reader whose meaning is fixed.</summary>
    internal enum PlanKind : byte { Stick, Trigger, Hat, Button, SpaceAxis }

    /// <summary>A control of PadForge's reading and the report usage that
    /// carries it, with the digital button a HIDAPI driver reads as a full
    /// trigger when the analog value is 0.</summary>
    internal readonly record struct ProvenControl(RecordingField Field, int Index, PlanKind Kind, ushort UsagePage, ushort Usage,
        ushort FallbackButton = 0);

    /// <summary>How the comparison rebuilds PadForge's reading of one
    /// device from Raw Input's copy of its reports.</summary>
    internal sealed class ReaderPlan
    {
        public ReaderClass Class;
        public HidControl[] Controls = Array.Empty<HidControl>();
        public RecordingControlKind Kind;

        // DirectInput: SDL's numbering, the control behind each object, the
        // mapping when PadForge reads the gamepad layout, and what the
        // reader reads.
        public SdlNumbering Numbering;
        public int[] ObjectControl = Array.Empty<int>();
        public List<GamepadBinding> Bindings;
        public string MappingText;
        public bool GamepadLayout, ExtraAxes, Touchpad;
        public int RawAxes, RawButtons, AxesRead, HatsRead, ButtonsRead;

        // SDL's own count of the joystick's hats: its count when PadForge
        // reads the joystick, DirectInput's when SDL's mapping hides it.
        public int JoystickHats;

        // HIDAPI and SpaceMouse: each control by its usage.
        public ProvenControl[] Proven = Array.Empty<ProvenControl>();
        public int[] ProvenControlIndex = Array.Empty<int>();
        public int[] ProvenFallbackIndex = Array.Empty<int>();

        /// <summary>The kinds the count check left out, one line's words each.</summary>
        public readonly List<string> LeftOut = new();
    }

    /// <summary>
    /// The pure half of the reader comparison (#600 item 32): which second
    /// reader each device has or why it has none, the plans that rebuild
    /// PadForge's reading from Raw Input's copy of the reports, and the
    /// lines' words.
    /// </summary>
    internal static class RecorderReaderPlan
    {
        // ─────────────────────────────────────────────
        //  Which second reader
        // ─────────────────────────────────────────────

        /// <summary>The driver signature in byte 14 of SDL's joystick GUID
        /// (SDL_joystick.c:4088), from PadForge's GUID string, or -1.</summary>
        internal static int Signature(string sdlGuid)
        {
            if (string.IsNullOrEmpty(sdlGuid) || sdlGuid.Length != 32) return -1;
            return int.TryParse(sdlGuid.AsSpan(28, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) ? value : -1;
        }

        /// <summary>The bus in bytes 0 and 1 of SDL's joystick GUID, little
        /// endian (SDL_joystick.c:4079), or -1.</summary>
        internal static int Bus(string sdlGuid)
        {
            if (string.IsNullOrEmpty(sdlGuid) || sdlGuid.Length != 32) return -1;
            return int.TryParse(sdlGuid.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int low)
                && int.TryParse(sdlGuid.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int high)
                ? low | high << 8 : -1;
        }

        internal const int BusUsb = 0x03;

        /// <summary>
        /// Which second reader an SDL device has, from its GUID's driver
        /// signature and its path, or none and why, in the line's words. The
        /// signatures are SDL's: 0 DirectInput (SDL_dinputjoystick.c:1086-1090),
        /// h HIDAPI (SDL_hidapijoystick.c:852, 1159) and the Bluetooth LE Switch 2
        /// driver (SDL_ble_switch2joystick.c:1582), x XInput (SDL_xinputjoystick.c:
        /// 186), g GameInput (SDL_gameinputjoystick.cpp:441), v virtual
        /// (SDL_virtualjoystick.c:314), l BLE GATT (SDL_blegattjoystick.c:1296),
        /// s serial (SDL_serialjoystick.c:1447-1450), d DJI TCP
        /// (SDL_djitcpjoystick.c:733), b RFCOMM (SDL_rfcommjoystick.c:931), i iCade
        /// (SDL_icadejoystick.c:1011).
        /// </summary>
        internal static (ReaderClass Class, string Reason) Classify(string sdlGuid, bool xinputPlace, bool spaceMouse,
            ushort vendor, ushort product, bool gamepadLayout)
        {
            if (xinputPlace) return (ReaderClass.XInput, null);
            int signature = Signature(sdlGuid);
            switch (signature)
            {
                case 0:
                    return (ReaderClass.DirectInput, null);
                case 'h':
                    // A driver with no proof, or a proven one read in the
                    // joystick layout, numbers the controls PadForge reads
                    // its own way. A proven one off USB numbers them as the
                    // proof does, but the proof is of the USB report layout.
                    if (ProvenFor(vendor, product) == null || !gamepadLayout) return (ReaderClass.None, "SDL's driver numbers its controls its own way");
                    if (Bus(sdlGuid) != BusUsb) return (ReaderClass.None, "the comparison is proven on its USB reports only");
                    return (ReaderClass.Hidapi, null);
                case 'x':
                    return (ReaderClass.None, "SDL reads it through XInput at no place PadForge can name");
                case 'g':
                    return (ReaderClass.None, "SDL reads it through GameInput");
                case 'l':
                    return (ReaderClass.None, "SDL reads it over Bluetooth LE");
                case 's':
                    return (ReaderClass.None, "SDL reads it from a COM port");
                case 'd':
                    return (ReaderClass.None, "SDL reads it over the network");
                case 'b':
                    return (ReaderClass.None, "SDL reads it over Bluetooth serial");
                case 'i':
                    return (ReaderClass.None, "SDL reads it from keyboard keys");
                case 'v':
                    return spaceMouse ? (ReaderClass.SpaceMouse, null) : (ReaderClass.None, "PadForge reads it with no HID collection");
                default:
                    return (ReaderClass.None, "SDL names no reader of it");
            }
        }

        // ─────────────────────────────────────────────
        //  HIDAPI drivers whose meaning is proven
        // ─────────────────────────────────────────────

        /// <summary>
        /// The controls of a USB DualShock 4 (054C:05C4, 054C:09CC) and a USB
        /// DualSense (054C:0CE6) whose meaning SDL's HIDAPI driver and SDL's
        /// DirectInput row share, each by the report usage that carries it. The
        /// proof, control by control, is RecorderReaderMatchTests.
        /// TheSonyDriversMeaning_IsTheDirectInputRowsThroughTheDescriptor: the
        /// byte or bit each driver reads (SDL_hidapi_ps4.c:80-105, 1064-1148,
        /// SDL_hidapi_ps5.c:83-102, 1282-1373) holds the usage the descriptor
        /// gives that place (ControllersInfo, dualshock4 and dualsense report
        /// descriptors), and that usage is the one SDL's DirectInput row names
        /// for the control (SDL_gamepad_db.h:238-240) through SDL's numbering.
        /// Both drivers read a digital trigger with no analog value as a full
        /// trigger (SDL_hidapi_ps4.c:1124-1128, SDL_hidapi_ps5.c:1351-1361), so
        /// the comparison reads it so too. The touchpad click and the
        /// DualSense's mute are left out: the DualSense driver calls mute Misc1
        /// (SDL_gamepad.c:1520-1522) where the row calls the touchpad click
        /// Misc1, and PadForge's touchpad block decides the touchpad position
        /// (SdlDeviceWrapper.cs:2232-2260).
        /// </summary>
        internal static readonly ProvenControl[] SonyPad =
        {
            new(RecordingField.Axis, 0, PlanKind.Stick, 0x01, 0x30),
            new(RecordingField.Axis, 1, PlanKind.Stick, 0x01, 0x31),
            new(RecordingField.Axis, 2, PlanKind.Trigger, 0x01, 0x33, 7),
            new(RecordingField.Axis, 3, PlanKind.Stick, 0x01, 0x32),
            new(RecordingField.Axis, 4, PlanKind.Stick, 0x01, 0x35),
            new(RecordingField.Axis, 5, PlanKind.Trigger, 0x01, 0x34, 8),
            new(RecordingField.Button, 0, PlanKind.Button, 0x09, 2),
            new(RecordingField.Button, 1, PlanKind.Button, 0x09, 3),
            new(RecordingField.Button, 2, PlanKind.Button, 0x09, 1),
            new(RecordingField.Button, 3, PlanKind.Button, 0x09, 4),
            new(RecordingField.Button, 4, PlanKind.Button, 0x09, 5),
            new(RecordingField.Button, 5, PlanKind.Button, 0x09, 6),
            new(RecordingField.Button, 6, PlanKind.Button, 0x09, 9),
            new(RecordingField.Button, 7, PlanKind.Button, 0x09, 10),
            new(RecordingField.Button, 8, PlanKind.Button, 0x09, 11),
            new(RecordingField.Button, 9, PlanKind.Button, 0x09, 12),
            new(RecordingField.Button, 10, PlanKind.Button, 0x09, 13),
            new(RecordingField.Hat, 0, PlanKind.Hat, 0x01, 0x39),
        };

        /// <summary>The proven controls of a product SDL reads through HIDAPI,
        /// or null for a driver with no proof.</summary>
        internal static ProvenControl[] ProvenFor(ushort vendor, ushort product)
            => vendor == 0x054C && product is 0x05C4 or 0x09CC or 0x0CE6 ? SonyPad : null;

        /// <summary>PadForge's SpaceMouse reader's controls: virtual axis i is
        /// usage 0x30 + i (SpaceMouseService.cs:555-560), virtual button k the
        /// Button page's usage k + 1, bit k of report 3 (SpaceMouseDecoder.cs:
        /// 111-130), 32 of them (SpaceMouseService.cs:343, 392-401).</summary>
        internal static ProvenControl[] SpaceMouse()
        {
            var controls = new List<ProvenControl>();
            for (int i = 0; i < 6; i++) controls.Add(new(RecordingField.Axis, i, PlanKind.SpaceAxis, 0x01, (ushort)(0x30 + i)));
            for (int k = 0; k < 32; k++) controls.Add(new(RecordingField.Button, k, PlanKind.Button, 0x09, (ushort)(k + 1)));
            return controls.ToArray();
        }

        // ─────────────────────────────────────────────
        //  Plans
        // ─────────────────────────────────────────────

        /// <summary>
        /// The plan of a device SDL reads through DirectInput: SDL's numbering
        /// of DirectInput's own objects, the report control behind each, and
        /// SDL's mapping when PadForge reads the gamepad layout. The counts
        /// SDL keeps must match the replay's, a kind that does not is left out
        /// (a device read through a mapping keeps no raw hat count,
        /// SdlDeviceWrapper.cs:650, so its hats go unchecked).
        /// </summary>
        internal static ReaderPlan DirectInputPlan(IReadOnlyList<DiObject> objects, IReadOnlyList<HidControl> controls,
            string mappingText, bool gamepadLayout, bool extraAxes, bool touchpad, int rawAxes, int rawButtons,
            int numAxes, int numHats, int numButtons, bool gamepadOpened)
        {
            var numbering = RecorderReaderMatch.Number(objects);
            var plan = new ReaderPlan
            {
                Class = ReaderClass.DirectInput,
                Controls = new List<HidControl>(controls).ToArray(),
                Kind = gamepadLayout ? RecordingControlKind.Gamepad : RecordingControlKind.Generic,
                Numbering = numbering,
                ObjectControl = RecorderReaderMatch.MatchControls(objects, controls),
                GamepadLayout = gamepadLayout,
                MappingText = gamepadLayout ? mappingText : null,
                Bindings = gamepadLayout ? RecorderReaderMatch.ParseMapping(mappingText) : null,
                ExtraAxes = extraAxes,
                Touchpad = touchpad,
                RawAxes = rawAxes,
                RawButtons = rawButtons,
                AxesRead = extraAxes ? rawAxes : numAxes,
                HatsRead = numHats,
                ButtonsRead = rawButtons > 0 ? rawButtons : numButtons,
                JoystickHats = gamepadOpened ? numbering.Hats.Length : numHats,
            };
            if (numbering.Axes.Length != rawAxes)
            {
                plan.LeftOut.Add("axes: SDL keeps " + rawAxes.ToString(CultureInfo.InvariantCulture) + " and DirectInput lists "
                    + numbering.Axes.Length.ToString(CultureInfo.InvariantCulture));
                numbering.Axes = Array.Empty<SdlInput>();
            }
            if (numbering.Buttons.Length != rawButtons)
            {
                plan.LeftOut.Add("buttons: SDL keeps " + rawButtons.ToString(CultureInfo.InvariantCulture) + " and DirectInput lists "
                    + numbering.Buttons.Length.ToString(CultureInfo.InvariantCulture));
                numbering.Buttons = Array.Empty<SdlInput>();
            }
            if (!gamepadOpened && numbering.Hats.Length != numHats)
            {
                plan.LeftOut.Add("hats: SDL keeps " + numHats.ToString(CultureInfo.InvariantCulture) + " and DirectInput lists "
                    + numbering.Hats.Length.ToString(CultureInfo.InvariantCulture));
                numbering.Hats = Array.Empty<SdlInput>();
            }
            return plan;
        }

        /// <summary>The plan of a device whose controls are fixed by usage:
        /// a proven HIDAPI driver's, or PadForge's SpaceMouse reader's. A
        /// control whose usage the descriptor does not declare is left out.</summary>
        internal static ReaderPlan FixedPlan(ReaderClass readerClass, ProvenControl[] proven, IReadOnlyList<HidControl> controls)
        {
            var kept = new List<ProvenControl>();
            var index = new List<int>();
            var fallback = new List<int>();
            foreach (var p in proven)
            {
                int c = Find(controls, p.UsagePage, p.Usage, p.Kind == PlanKind.Button);
                if (c < 0) continue;
                kept.Add(p);
                index.Add(c);
                fallback.Add(p.FallbackButton == 0 ? -1 : Find(controls, 0x09, p.FallbackButton, button: true));
            }
            return new ReaderPlan
            {
                Class = readerClass,
                Controls = new List<HidControl>(controls).ToArray(),
                Kind = readerClass == ReaderClass.Hidapi ? RecordingControlKind.Gamepad : RecordingControlKind.Generic,
                Proven = kept.ToArray(),
                ProvenControlIndex = index.ToArray(),
                ProvenFallbackIndex = fallback.ToArray(),
            };
        }

        private static int Find(IReadOnlyList<HidControl> controls, ushort page, ushort usage, bool button)
        {
            for (int i = 0; i < controls.Count; i++)
                if (controls[i].UsagePage == page && controls[i].Usage == usage && controls[i].IsButton == button) return i;
            return -1;
        }

        // ─────────────────────────────────────────────
        //  What PadForge's reader should read
        // ─────────────────────────────────────────────

        /// <summary>
        /// The values PadForge's reader would write, rebuilt from the newest
        /// report values: <paramref name="values"/> by control, a button's
        /// value 1 when down, and whether each control's report has come. A
        /// control whose report has not come is left out.
        /// </summary>
        internal static List<ExpectedControl> Expected(ReaderPlan plan, uint[] values, bool[] known)
        {
            if (plan.Class == ReaderClass.DirectInput) return ExpectedDirectInput(plan, values, known);
            var expected = new List<ExpectedControl>();
            bool? back = null, start = null;
            int guide = -1;
            for (int i = 0; i < plan.Proven.Length; i++)
            {
                var p = plan.Proven[i];
                int c = plan.ProvenControlIndex[i];
                if (!known[c]) continue;
                var control = plan.Controls[c];
                switch (p.Kind)
                {
                    case PlanKind.Stick:
                        expected.Add(new(p.Field, p.Index, Linear(values[c], control)));
                        break;
                    case PlanKind.Trigger:
                    {
                        int f = plan.ProvenFallbackIndex[i];
                        if (f >= 0 && !known[f]) continue;
                        long full = Linear(values[c], control);
                        if (SignedValue(values[c], control) == control.LogicalMin && f >= 0 && values[f] != 0) full = 65535;
                        // SDL_GetGamepadAxis takes the joystick axis's whole
                        // range to 0..32767 (SDL_gamepad.c:3817-3821), and
                        // PadForge spreads that to 0..65535 (SdlDeviceWrapper.cs:2063).
                        short sdl = (short)(full - 32768);
                        int trigger = (int)((float)(sdl - RecorderReaderMatch.AxisMin) / (RecorderReaderMatch.AxisMax - RecorderReaderMatch.AxisMin) * RecorderReaderMatch.AxisMax);
                        expected.Add(new(p.Field, p.Index, (int)(trigger * 65535L / 32767)));
                        break;
                    }
                    case PlanKind.Hat:
                        expected.Add(new(p.Field, p.Index, RecorderReaderMatch.Angle(RecorderReaderMatch.HatOf(values[c], control))));
                        break;
                    case PlanKind.Button:
                        bool down = values[c] != 0;
                        if (plan.Class == ReaderClass.Hidapi && p.Field == RecordingField.Button)
                        {
                            if (p.Index == 6) back = down;
                            else if (p.Index == 7) start = down;
                            else if (p.Index == 10) { guide = expected.Count; }
                        }
                        expected.Add(new(p.Field, p.Index, down ? 1 : 0));
                        break;
                    case PlanKind.SpaceAxis:
                        expected.Add(new(p.Field, p.Index, RecorderReaderMatch.Unsigned(SpaceAxis(values[c], control))));
                        break;
                }
            }
            // Windows makes Guide of Back and Start held together, and
            // PadForge lets that Guide go (SdlDeviceWrapper.cs:2098-2101).
            if (guide >= 0 && back == true && start == true && expected[guide].Value == 1)
                expected[guide] = expected[guide] with { Value = 0 };
            return expected;
        }

        private static long SignedValue(uint raw, in HidControl c) => RecorderReaderMatch.SignExtend(raw, c.BitSize, c.LogicalMin);

        /// <summary>A value spread over 0..65535 by its logical range, as a
        /// HIDAPI driver spreads a byte (value times 257, SDL_hidapi_ps4.c:1137-1148).</summary>
        private static int Linear(uint raw, in HidControl c)
        {
            long value = SignedValue(raw, c);
            long span = (long)c.LogicalMax - c.LogicalMin;
            if (span <= 0) return 0;
            long scaled = (value - c.LogicalMin) * 65535 / span;
            return (int)Math.Clamp(scaled, 0, 65535);
        }

        /// <summary>The SpaceMouse reader's axis: the value times 32767 over the
        /// logical maximum, clamped (SpaceMouseDecoder.ToSdlAxis), with the
        /// maximum the puck's descriptor declares.</summary>
        private static short SpaceAxis(uint raw, in HidControl c)
        {
            long value = SignedValue(raw, c);
            long max = Math.Max(1, c.LogicalMax);
            long v = value * 32767 / max;
            return (short)Math.Clamp(v, -32768, 32767);
        }

        /// <summary>
        /// The DirectInput replay. SDL's joystick state has SDL's own counts,
        /// so a binding past them is passed over as SDL passes it over, and an
        /// input of a kind the count check left out stays unknown: the
        /// bindings that read it are left out, never read as centered.
        /// </summary>
        private static List<ExpectedControl> ExpectedDirectInput(ReaderPlan plan, uint[] values, bool[] known)
        {
            var n = plan.Numbering;
            var s = new SdlJoystickState(Math.Max(0, plan.RawAxes), Math.Max(0, plan.RawButtons), Math.Max(0, plan.JoystickHats));
            for (int i = 0; i < n.Axes.Length && i < s.Axes.Length; i++)
            {
                int c = ControlOf(plan, n.Axes[i]);
                if (c < 0 || !known[c] || !plan.Controls[c].Absolute) continue;
                s.Axes[i] = RecorderReaderMatch.ScaleAxis(values[c], plan.Controls[c]);
                s.AxisKnown[i] = true;
            }
            for (int i = 0; i < n.Buttons.Length && i < s.Buttons.Length; i++)
            {
                int c = ControlOf(plan, n.Buttons[i]);
                if (c < 0 || !known[c]) continue;
                s.Buttons[i] = values[c] != 0;
                s.ButtonKnown[i] = true;
            }
            for (int i = 0; i < n.Hats.Length && i < s.Hats.Length; i++)
            {
                int c = ControlOf(plan, n.Hats[i]);
                if (c < 0 || !known[c]) continue;
                s.Hats[i] = RecorderReaderMatch.HatOf(values[c], plan.Controls[c]);
                s.HatKnown[i] = true;
            }
            return plan.GamepadLayout
                ? RecorderReaderMatch.GamepadLayout(plan.Bindings, s, plan.MappingText, plan.ExtraAxes, plan.RawAxes, plan.RawButtons, plan.Touchpad)
                : RecorderReaderMatch.JoystickLayout(s, plan.AxesRead, plan.HatsRead, plan.ButtonsRead);
        }

        private static int ControlOf(ReaderPlan plan, SdlInput input)
            => input.Source >= 0 && input.Source < plan.ObjectControl.Length ? plan.ObjectControl[input.Source] : -1;

        /// <summary>The report IDs whose reports carry a control the plan
        /// compares, in order.</summary>
        internal static SortedSet<byte> UsedReportIds(ReaderPlan plan)
        {
            var ids = new SortedSet<byte>();
            void Add(int c)
            {
                if (c >= 0 && c < plan.Controls.Length) ids.Add(plan.Controls[c].ReportId);
            }
            if (plan.Class == ReaderClass.DirectInput)
            {
                foreach (var input in plan.Numbering.Axes) Add(ControlOf(plan, input));
                foreach (var input in plan.Numbering.Buttons) Add(ControlOf(plan, input));
                foreach (var input in plan.Numbering.Hats) Add(ControlOf(plan, input));
            }
            else
            {
                foreach (int c in plan.ProvenControlIndex) Add(c);
                foreach (int c in plan.ProvenFallbackIndex) Add(c);
            }
            return ids;
        }

        // ─────────────────────────────────────────────
        //  The lines
        // ─────────────────────────────────────────────

        /// <summary>
        /// The match a plan makes, for the line written once per device and
        /// file: each control PadForge's reading has, by the recording's name
        /// for it, and the report usage behind it. A device read through
        /// SDL's mapping names SDL's joystick inputs as the mapping writes
        /// them (a0, b1, h0). A run of buttons in step is one range. A pad
        /// read through a proven HIDAPI driver is matched by SDL's
        /// DirectInput row for it, and its line says so.
        /// </summary>
        internal static string MatchWords(ReaderPlan plan)
        {
            var parts = new List<(string Name, string Usage, int Left, int Right, bool Button)>();
            if (plan.Class == ReaderClass.DirectInput)
            {
                var n = plan.Numbering;
                void Add(SdlInput[] inputs, string prefix, RecordingField field, bool button)
                {
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        int c = ControlOf(plan, inputs[i]);
                        if (c < 0) continue;
                        var control = plan.Controls[c];
                        if (field == RecordingField.Axis && !control.Absolute) continue;
                        string name = plan.GamepadLayout
                            ? prefix + i.ToString(CultureInfo.InvariantCulture)
                            : RecordingControlNames.Input(RecordingControlKind.Generic,
                                field == RecordingField.Axis && i >= CustomInputState.MaxAxis ? RecordingField.Slider : field,
                                field == RecordingField.Axis && i >= CustomInputState.MaxAxis ? i - CustomInputState.MaxAxis : i);
                        parts.Add((name, RecorderReaderMatch.UsageName(control.UsagePage, control.Usage), i, control.Usage, button && control.UsagePage == 0x09));
                    }
                }
                Add(n.Axes, "a", RecordingField.Axis, false);
                Add(n.Hats, "h", RecordingField.Hat, false);
                Add(n.Buttons, "b", RecordingField.Button, true);
            }
            else
            {
                for (int i = 0; i < plan.Proven.Length; i++)
                {
                    var p = plan.Proven[i];
                    var control = plan.Controls[plan.ProvenControlIndex[i]];
                    parts.Add((RecordingControlNames.Input(plan.Kind, p.Field, p.Index), RecorderReaderMatch.UsageName(control.UsagePage, control.Usage),
                        p.Index, control.Usage, p.Field == RecordingField.Button && plan.Class == ReaderClass.SpaceMouse && control.UsagePage == 0x09));
                }
            }

            var sb = new StringBuilder();
            bool first = true;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                int j = i;
                if (part.Button)
                    while (j + 1 < parts.Count && parts[j + 1].Button && parts[j + 1].Left == parts[j].Left + 1 && parts[j + 1].Right == parts[j].Right + 1) j++;
                if (!first) sb.Append(", ");
                if (j > i)
                {
                    sb.Append(part.Name).Append(" to ").Append(parts[j].Name).Append(first ? " are its Buttons " : " its Buttons ")
                      .Append(part.Right.ToString(CultureInfo.InvariantCulture)).Append(" to ").Append(parts[j].Right.ToString(CultureInfo.InvariantCulture));
                    i = j;
                }
                else
                {
                    sb.Append(part.Name).Append(first ? " is its " : " its ").Append(part.Usage);
                }
                first = false;
            }
            if (plan.Class == ReaderClass.Hidapi) sb.Append(" by SDL's DirectInput mapping for it");
            return sb.ToString();
        }
    }
}
