using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Recording;

namespace PadForge.Common.Recording
{
    /// <summary>A DirectInput object of a device as EnumObjects lists it with
    /// SDL's flags (SDL_dinputjoystick.c:1185-1187), and whether the two
    /// properties SDL sets on an axis took (552-577).</summary>
    internal readonly record struct DiObject(Guid Type, uint Kind, ushort UsagePage, ushort Usage, ushort ReportId,
        ushort Collection, bool AxisPropertiesTaken);

    /// <summary>One usage of a HID collection's input reports, as HidP's
    /// button and value caps give it, a range opened into one entry per
    /// usage.</summary>
    internal readonly record struct HidControl(ushort UsagePage, ushort Usage, byte ReportId, ushort Collection,
        bool IsButton, ushort DataIndex, int LogicalMin, int LogicalMax, int BitSize, bool Absolute);

    /// <summary>The kind of an input of SDL's DirectInput joystick.</summary>
    internal enum SdlInputKind : byte { Axis, Button, Hat }

    /// <summary>An input of SDL's DirectInput joystick: its kind, the
    /// DIJOYSTATE2 offset SDL reads it from, and the object whose data
    /// DirectInput puts at that offset, or -1 when no single object
    /// does.</summary>
    internal readonly record struct SdlInput(SdlInputKind Kind, uint Offset, int Source);

    /// <summary>SDL's inputs of a DirectInput device, each kind in SDL's
    /// numbering.</summary>
    internal sealed class SdlNumbering
    {
        public SdlInput[] Axes = Array.Empty<SdlInput>();
        public SdlInput[] Buttons = Array.Empty<SdlInput>();
        public SdlInput[] Hats = Array.Empty<SdlInput>();
    }

    /// <summary>What a binding of SDL's mapping reads or sets.</summary>
    internal enum BindKind : byte { None, Axis, Button, Hat }

    /// <summary>One binding of SDL's gamepad mapping, as
    /// SDL_PrivateParseGamepadElement makes it (SDL_gamepad.c:1899-2017).</summary>
    internal readonly record struct GamepadBinding(BindKind InputKind, int Input, int InputMin, int InputMax, int HatMask,
        BindKind OutputKind, int Output, int OutputMin, int OutputMax);

    /// <summary>A joystick's state in SDL's terms: axes as SDL's signed
    /// values, buttons, hats as SDL's hat bits, each with whether it is
    /// known. An input SDL has and the comparison cannot rebuild is
    /// unknown.</summary>
    internal sealed class SdlJoystickState
    {
        public readonly short[] Axes;
        public readonly bool[] AxisKnown;
        public readonly bool[] Buttons;
        public readonly bool[] ButtonKnown;
        public readonly byte[] Hats;
        public readonly bool[] HatKnown;

        public SdlJoystickState(int axes, int buttons, int hats)
        {
            Axes = new short[axes];
            AxisKnown = new bool[axes];
            Buttons = new bool[buttons];
            ButtonKnown = new bool[buttons];
            Hats = new byte[hats];
            HatKnown = new bool[hats];
        }
    }

    /// <summary>One control of PadForge's reading the comparison names a
    /// value for.</summary>
    internal readonly record struct ExpectedControl(RecordingField Field, int Index, int Value);

    /// <summary>
    /// How Raw Input's copy of a device's reports becomes the values
    /// PadForge's reader would read (#600 item 32): SDL's DirectInput
    /// numbering replayed on DirectInput's own object list, DirectInput's
    /// scaling, SDL's hat rounding, SDL's mapping and PadForge's own
    /// conversion. Pure: no device, no Windows call.
    ///
    /// <para>Every replayed rule was read from SDL at the revision
    /// <see cref="SdlRevision"/> names, and a test pins that revision to the
    /// bundled SDL3.dll, so the next SDL delivery is read again before this
    /// is trusted with it.</para>
    /// </summary>
    internal static class RecorderReaderMatch
    {
        /// <summary>The fork commit of SDL the rules here were read from.</summary>
        internal const string SdlRevision = "68f6b71aef";

        // ─────────────────────────────────────────────
        //  DirectInput, as dinput.h lays it out
        // ─────────────────────────────────────────────

        internal const uint DidftAxis = 0x3, DidftButton = 0xC, DidftPov = 0x10;

        internal static readonly Guid XAxis = new("A36D02E0-C9F3-11CF-BFC7-444553540000");
        internal static readonly Guid YAxis = new("A36D02E1-C9F3-11CF-BFC7-444553540000");
        internal static readonly Guid ZAxis = new("A36D02E2-C9F3-11CF-BFC7-444553540000");
        internal static readonly Guid RxAxis = new("A36D02F4-C9F3-11CF-BFC7-444553540000");
        internal static readonly Guid RyAxis = new("A36D02F5-C9F3-11CF-BFC7-444553540000");
        internal static readonly Guid RzAxis = new("A36D02E3-C9F3-11CF-BFC7-444553540000");
        internal static readonly Guid Slider = new("A36D02E4-C9F3-11CF-BFC7-444553540000");
        internal static readonly Guid Button = new("A36D02F0-C9F3-11CF-BFC7-444553540000");
        internal static readonly Guid Key = new("55728220-D33C-11CF-BFC7-444553540000");
        internal static readonly Guid Pov = new("A36D02F2-C9F3-11CF-BFC7-444553540000");
        internal static readonly Guid Unknown = new("A36D02F3-C9F3-11CF-BFC7-444553540000");

        private const uint SliderBase = 24, PovBase = 32, ButtonBase = 48;
        private const int StateButtons = 128;

        /// <summary>SDL keeps at most this many inputs of a device
        /// (SDL_dinputjoystick_c.h:27, SDL_dinputjoystick.c:587-589).</summary>
        private const int MaxInputs = 256;

        private static uint AxisOffset(Guid type) =>
            type == XAxis ? 0u : type == YAxis ? 4u : type == ZAxis ? 8u : type == RxAxis ? 12u : type == RyAxis ? 16u : type == RzAxis ? 20u : uint.MaxValue;

        /// <summary>
        /// SDL's inputs of a DirectInput device, from DirectInput's own object
        /// list in its order: EnumDevObjectsCallback (SDL_dinputjoystick.c:
        /// 507-592), then SortDevObjects, which sorts by offset and numbers each
        /// kind in that order (594-640). Each input's source is the object
        /// DirectInput writes at its offset under SDL's format (<see cref="Slots"/>).
        /// </summary>
        internal static SdlNumbering Number(IReadOnlyList<DiObject> objects)
        {
            var inputs = new List<(SdlInputKind Kind, uint Offset, int Order)>();
            int buttons = 0, hats = 0, sliders = 0;
            for (int i = 0; i < objects.Count && inputs.Count < MaxInputs; i++)
            {
                var o = objects[i];
                if ((o.Kind & DidftButton) != 0)
                {
                    // in->num is a Uint8 of the running count (515).
                    inputs.Add((SdlInputKind.Button, ButtonBase + (byte)buttons, i));
                    buttons++;
                }
                else if ((o.Kind & DidftPov) != 0)
                {
                    if (hats >= 4) continue;
                    inputs.Add((SdlInputKind.Hat, PovBase + 4u * (uint)hats, i));
                    hats++;
                }
                else if ((o.Kind & DidftAxis) != 0)
                {
                    uint offset;
                    if (o.Type == Slider)
                    {
                        // The slider count rises before the properties are
                        // asked, so a refused slider still uses its slot (545-547).
                        offset = SliderBase + 4u * (uint)sliders;
                        sliders++;
                    }
                    else
                    {
                        offset = AxisOffset(o.Type);
                        if (offset == uint.MaxValue) continue;
                    }
                    if (!o.AxisPropertiesTaken) continue;
                    inputs.Add((SdlInputKind.Axis, offset, i));
                }
            }

            // SDL's qsort is not stable, but inputs of one offset read one
            // field, so their order among themselves changes no value.
            inputs.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Order.CompareTo(b.Order));
            var slots = Slots(objects);
            var axes = new List<SdlInput>();
            var buttonList = new List<SdlInput>();
            var hatList = new List<SdlInput>();
            foreach (var (kind, offset, _) in inputs)
            {
                int source = slots.TryGetValue(offset, out int s) && s >= 0 && KindOf(objects[s]) == kind ? s : -1;
                var input = new SdlInput(kind, offset, source);
                if (kind == SdlInputKind.Axis) axes.Add(input);
                else if (kind == SdlInputKind.Button) buttonList.Add(input);
                else hatList.Add(input);
            }
            return new SdlNumbering { Axes = axes.ToArray(), Buttons = buttonList.ToArray(), Hats = hatList.ToArray() };
        }

        private static SdlInputKind? KindOf(DiObject o)
            => (o.Kind & DidftButton) != 0 ? SdlInputKind.Button
             : (o.Kind & DidftPov) != 0 ? SdlInputKind.Hat
             : (o.Kind & DidftAxis) != 0 ? SdlInputKind.Axis : null;

        /// <summary>The GUID DirectInput keeps for an object in its format,
        /// any other type read as Unknown (Wine device.c:2163-2176).</summary>
        private static Guid FormatGuid(Guid type)
            => type == XAxis || type == YAxis || type == ZAxis || type == RxAxis || type == RyAxis || type == RzAxis
               || type == Slider || type == Button || type == Key || type == Pov ? type : Unknown;

        /// <summary>
        /// The object DirectInput writes at each offset SDL reads, under SDL's
        /// format SDL_c_dfDIJoystick2 (SDL_dinputjoystick.c:79-256): each entry
        /// takes the first object not yet taken whose GUID is the entry's and
        /// whose type the entry's type includes, a button entry any button
        /// (Wine device.c:229-291). The velocity, acceleration and force
        /// entries of the sliders point at the two slider slots again (226-228,
        /// 235-237, 244-246), so a third and fourth slider object lands on the
        /// first two slots: a slot two objects write is -2.
        /// </summary>
        internal static Dictionary<uint, int> Slots(IReadOnlyList<DiObject> objects)
        {
            var taken = new bool[objects.Count];
            var slots = new Dictionary<uint, int>();
            void Entry(Guid? type, uint kind, uint offset)
            {
                for (int i = 0; i < objects.Count; i++)
                {
                    if (taken[i]) continue;
                    var o = objects[i];
                    if (type is Guid t && FormatGuid(o.Type) != t) continue;
                    if ((kind & o.Kind & 0xFF) == 0) continue;
                    taken[i] = true;
                    slots[offset] = slots.ContainsKey(offset) ? -2 : i;
                    return;
                }
            }
            Guid[] axes = { XAxis, YAxis, ZAxis, RxAxis, RyAxis, RzAxis };
            for (int a = 0; a < axes.Length; a++) Entry(axes[a], DidftAxis, (uint)(4 * a));
            Entry(Slider, DidftAxis, SliderBase);
            Entry(Slider, DidftAxis, SliderBase + 4);
            for (int p = 0; p < 4; p++) Entry(Pov, DidftPov, PovBase + 4u * (uint)p);
            for (int b = 0; b < StateButtons; b++) Entry(null, DidftButton, ButtonBase + (uint)b);
            // Velocity, acceleration and force: the six axes go to fields SDL
            // never reads, the sliders to the slider slots again.
            uint far = 1000;
            for (int aspect = 0; aspect < 3; aspect++)
            {
                for (int a = 0; a < axes.Length; a++) Entry(axes[a], DidftAxis, far++);
                Entry(Slider, DidftAxis, SliderBase);
                Entry(Slider, DidftAxis, SliderBase + 4);
            }
            return slots;
        }

        /// <summary>
        /// The report control behind each object: same usage page, usage,
        /// report ID and link collection and the same kind, the first not yet
        /// taken, then by page, usage and kind alone, as the reference matches
        /// its twin's controls (RawInputLayout.cs:113-130). DirectInput fills an
        /// object's report ID and collection from the HID caps (Wine
        /// joystick_hid.c:855-859, 892-896). -1 for none.
        /// </summary>
        internal static int[] MatchControls(IReadOnlyList<DiObject> objects, IReadOnlyList<HidControl> controls)
        {
            var result = new int[objects.Count];
            var taken = new bool[controls.Count];
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < objects.Count; i++)
                {
                    if (pass == 0) result[i] = -1;
                    else if (result[i] >= 0) continue;
                    var o = objects[i];
                    var kind = KindOf(o);
                    if (kind == null) continue;
                    bool button = kind == SdlInputKind.Button;
                    for (int c = 0; c < controls.Count; c++)
                    {
                        if (taken[c]) continue;
                        var h = controls[c];
                        if (h.UsagePage != o.UsagePage || h.Usage != o.Usage || h.IsButton != button) continue;
                        if (pass == 0 && (h.ReportId != o.ReportId || h.Collection != o.Collection)) continue;
                        taken[c] = true;
                        result[i] = c;
                        break;
                    }
                }
            return result;
        }

        // ─────────────────────────────────────────────
        //  Values
        // ─────────────────────────────────────────────

        /// <summary>A report's raw value read as the signed number it is when
        /// its logical minimum is below zero (Wine joystick_hid.c:1395-1400).</summary>
        internal static long SignExtend(uint raw, int bitSize, int logicalMin)
        {
            if (logicalMin >= 0 || bitSize <= 1) return raw;
            if (bitSize >= 32) return unchecked((int)raw);
            uint sign = 1u << (bitSize - 1);
            raw &= (1u << bitSize) - 1;
            return (raw & sign) != 0 ? (long)raw - (1L << bitSize) : raw;
        }

        /// <summary>Windows' MulDiv: the product divided, rounded to the nearest
        /// integer, a half away from zero.</summary>
        internal static long MulDiv(long a, long b, long c)
        {
            if (c == 0) return -1;
            if (c < 0) { a = -a; c = -c; }
            long product = a * b;
            return product >= 0 ? (product + c / 2) / c : (product - c / 2) / c;
        }

        /// <summary>
        /// DirectInput's value of an axis in the range SDL sets, -32768 to 32767,
        /// with dead zone 0 and saturation 10000 (SDL_dinputjoystick.c:552-577):
        /// Wine's scale_axis_value (joystick_hid.c:1414-1448), the two halves
        /// scaled from the logical center, a logical range of 0 to -1 read as
        /// the field's bit range. SDL takes it as a Sint16 (791-816).
        /// </summary>
        internal static short ScaleAxis(uint raw, in HidControl c)
        {
            long tmp = SignExtend(raw, c.BitSize, c.LogicalMin);
            long logMin = c.LogicalMin, logMax = c.LogicalMax, phyMin = -32768, phyMax = 32767;
            if (logMin == 0 && logMax == -1) logMax = c.BitSize >= 32 ? uint.MaxValue : (1L << c.BitSize) - 1;
            long phyCtr = phyMin == 0 ? phyMax >> 1 : (long)Math.Round((phyMin + phyMax) / 2.0, MidpointRounding.AwayFromZero);
            long logCtr = logMin == 0 ? logMax >> 1 : (long)Math.Round((logMin + logMax) / 2.0, MidpointRounding.AwayFromZero);
            tmp -= logCtr;
            if (tmp <= 0)
            {
                logMax = 0;
                logMin -= logCtr;
                phyMax = phyCtr;
            }
            else
            {
                logMin = 0;
                logMax -= logCtr;
                phyMin = phyCtr;
            }
            long value;
            if (tmp <= logMin) value = phyMin;
            else if (tmp >= logMax) value = phyMax;
            else value = phyMin + MulDiv(tmp - logMin, phyMax - phyMin, logMax - logMin);
            return unchecked((short)value);
        }

        internal const byte HatCentered = 0, HatUp = 1, HatRight = 2, HatDown = 4, HatLeft = 8;

        /// <summary>
        /// The SDL hat of a hat switch: DirectInput spreads the logical range
        /// over 0 to 36000 less one step and makes a value outside it centered
        /// (Wine joystick_hid.c:1954-1959, scale_value 1402-1412), and SDL rounds
        /// the angle to eight directions (SDL_dinputjoystick.c:730-757).
        /// </summary>
        internal static byte HatOf(uint raw, in HidControl c)
        {
            long tmp = SignExtend(raw, c.BitSize, c.LogicalMin);
            long steps = (long)c.LogicalMax - c.LogicalMin;
            long range = 36000;
            if (steps > 0) range -= 36000 / (steps + 1);
            if (tmp < c.LogicalMin || tmp > c.LogicalMax) return HatCentered;
            // MulDiv by zero gives -1, which SDL reads as centered.
            long angle = MulDiv(tmp - c.LogicalMin, range, steps);
            return TranslatePov(unchecked((uint)angle));
        }

        private static readonly byte[] HatValues =
        {
            HatUp, HatUp | HatRight, HatRight, HatDown | HatRight, HatDown, HatDown | HatLeft, HatLeft, HatUp | HatLeft,
        };

        /// <summary>SDL's TranslatePOV (SDL_dinputjoystick.c:730-757).</summary>
        internal static byte TranslatePov(uint value)
        {
            if ((value & 0xFFFF) == 0xFFFF) return HatCentered;
            value += 4500 / 2;
            value %= 36000;
            value /= 4500;
            return value >= 8 ? HatCentered : HatValues[value];
        }

        /// <summary>A hat's angle as PadForge writes it
        /// (SdlDeviceWrapper.HatToCentidegrees).</summary>
        internal static int Angle(byte hat) => SdlDeviceWrapper.HatToCentidegrees(hat);

        /// <summary>An SDL axis value as PadForge writes it in the joystick
        /// layout (SdlDeviceWrapper.cs:2438-2440).</summary>
        internal static int Unsigned(short value) => (ushort)(value - short.MinValue);

        // ─────────────────────────────────────────────
        //  SDL's mapping
        // ─────────────────────────────────────────────

        internal const int AxisMin = -32768, AxisMax = 32767;

        private static readonly string[] AxisNames = { "leftx", "lefty", "rightx", "righty", "lefttrigger", "righttrigger" };

        private static readonly string[] ButtonNames =
        {
            "a", "b", "x", "y", "back", "guide", "start", "leftstick", "rightstick", "leftshoulder", "rightshoulder",
            "dpup", "dpdown", "dpleft", "dpright", "misc1", "paddle1", "paddle2", "paddle3", "paddle4", "touchpad",
            "misc2", "misc3", "misc4", "misc5", "misc6",
        };

        internal const int LeftTrigger = 4, RightTrigger = 5;
        internal const int South = 0, East = 1, West = 2, North = 3, Back = 4, Guide = 5, Start = 6, LeftStick = 7,
            RightStick = 8, LeftShoulder = 9, RightShoulder = 10, DpadUp = 11, DpadDown = 12, DpadLeft = 13, DpadRight = 14;

        /// <summary>SDL_GetGamepadAxisFromString (SDL_gamepad.c:1776-1796).</summary>
        private static int AxisFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            if (name[0] == '+' || name[0] == '-') name = name.Substring(1);
            for (int i = 0; i < AxisNames.Length; i++)
                if (string.Equals(name, AxisNames[i], StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>SDL_PrivateGetGamepadButtonFromString with its face swaps
        /// (SDL_gamepad.c:1840-1879).</summary>
        private static int ButtonFromName(string name, bool axby, bool baxy)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < ButtonNames.Length; i++)
            {
                if (!string.Equals(name, ButtonNames[i], StringComparison.OrdinalIgnoreCase)) continue;
                if (axby)
                {
                    if (i == East) return West;
                    if (i == West) return East;
                }
                else if (baxy)
                {
                    switch (i)
                    {
                        case South: return East;
                        case East: return South;
                        case West: return North;
                        case North: return West;
                    }
                }
                return i;
            }
            return -1;
        }

        /// <summary>
        /// The bindings of a mapping SDL handed back. Its text is
        /// "GUID,name,body" (CreateMappingString, SDL_gamepad.c:3086-3121), and
        /// the body is read as SDL_PrivateParseGamepadConfigString reads it,
        /// into its two fixed buffers of 20 and 128 characters: fields split on
        /// commas, spaces dropped, each colon writing the input from its start
        /// again over what it held, a name too long ending the parse
        /// (1979-2025), each field made a binding by
        /// SDL_PrivateParseGamepadElement (1856-1974), an equal binding kept
        /// once. A DirectInput GUID gets no fixup after the parse (2300-2305).
        /// </summary>
        internal static List<GamepadBinding> ParseMapping(string text)
        {
            var bindings = new List<GamepadBinding>();
            if (string.IsNullOrEmpty(text)) return bindings;
            int first = text.IndexOf(',');
            int second = first < 0 ? -1 : text.IndexOf(',', first + 1);
            if (second < 0) return bindings;
            string body = text.Substring(second + 1);
            bool axby = body.Contains(",hint:SDL_GAMECONTROLLER_USE_GAMECUBE_LABELS:=1", StringComparison.Ordinal);
            bool baxy = body.Contains(",hint:SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1", StringComparison.Ordinal);
            var game = new char[20];
            var joystick = new char[128];
            int i = 0;
            bool inGame = true;
            foreach (char ch in body)
            {
                if (ch == ':')
                {
                    i = 0;
                    inGame = false;
                }
                else if (ch == ' ')
                {
                }
                else if (ch == ',')
                {
                    i = 0;
                    inGame = true;
                    Element(bindings, Field(game), Field(joystick), axby, baxy);
                    Array.Clear(game);
                    Array.Clear(joystick);
                }
                else if (inGame)
                {
                    if (i >= game.Length) return bindings;
                    game[i++] = ch;
                }
                else
                {
                    if (i >= joystick.Length) return bindings;
                    joystick[i++] = ch;
                }
            }
            if (game[0] != '\0' || joystick[0] != '\0') Element(bindings, Field(game), Field(joystick), axby, baxy);
            return bindings;
        }

        /// <summary>A buffer's text up to its first null, as C reads it.</summary>
        private static string Field(char[] buffer)
        {
            int end = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, end < 0 ? buffer.Length : end);
        }

        private static void Element(List<GamepadBinding> bindings, string game, string joystick, bool axby, bool baxy)
        {
            char halfOutput = '\0', halfInput = '\0';
            if (game.Length > 0 && (game[0] == '+' || game[0] == '-'))
            {
                halfOutput = game[0];
                game = game.Substring(1);
            }
            BindKind outputKind;
            int output, outMin, outMax;
            int axis = AxisFromName(game);
            int button = ButtonFromName(game, axby, baxy);
            if (axis >= 0)
            {
                outputKind = BindKind.Axis;
                output = axis;
                if (axis == LeftTrigger || axis == RightTrigger) { outMin = 0; outMax = AxisMax; }
                else if (halfOutput == '+') { outMin = 0; outMax = AxisMax; }
                else if (halfOutput == '-') { outMin = 0; outMax = AxisMin; }
                else { outMin = AxisMin; outMax = AxisMax; }
            }
            else if (button >= 0)
            {
                outputKind = BindKind.Button;
                output = button;
                outMin = outMax = 0;
            }
            else
            {
                return;
            }

            if (joystick.Length > 0 && (joystick[0] == '+' || joystick[0] == '-'))
            {
                halfInput = joystick[0];
                joystick = joystick.Substring(1);
            }
            if (joystick.Length == 0) return;
            bool invert = joystick[joystick.Length - 1] == '~';

            BindKind inputKind;
            int input, inMin = 0, inMax = 0, mask = 0;
            if (joystick.Length > 1 && joystick[0] == 'a' && char.IsAsciiDigit(joystick[1]))
            {
                inputKind = BindKind.Axis;
                input = LeadingNumber(joystick, 1);
                if (halfInput == '+') { inMin = 0; inMax = AxisMax; }
                else if (halfInput == '-') { inMin = 0; inMax = AxisMin; }
                else { inMin = AxisMin; inMax = AxisMax; }
                if (invert) (inMin, inMax) = (inMax, inMin);
            }
            else if (joystick.Length > 1 && joystick[0] == 'b' && char.IsAsciiDigit(joystick[1]))
            {
                inputKind = BindKind.Button;
                input = LeadingNumber(joystick, 1);
            }
            else if (joystick.Length > 3 && joystick[0] == 'h' && char.IsAsciiDigit(joystick[1]) && joystick[2] == '.'
                     && char.IsAsciiDigit(joystick[3]))
            {
                inputKind = BindKind.Hat;
                input = LeadingNumber(joystick, 1);
                mask = LeadingNumber(joystick, 3);
            }
            else
            {
                return;
            }

            var binding = new GamepadBinding(inputKind, input, inMin, inMax, mask, outputKind, output, outMin, outMax);
            if (!bindings.Contains(binding)) bindings.Add(binding);
        }

        /// <summary>SDL_atoi of the digits from <paramref name="start"/>.</summary>
        private static int LeadingNumber(string text, int start)
        {
            long value = 0;
            for (int i = start; i < text.Length && char.IsAsciiDigit(text[i]); i++)
            {
                value = value * 10 + (text[i] - '0');
                if (value > int.MaxValue) return int.MaxValue;
            }
            return (int)value;
        }

        /// <summary>Whether every input a binding reads is known. False when a
        /// binding of the output reads an input the comparison cannot rebuild.</summary>
        private static bool Known(in GamepadBinding b, SdlJoystickState s) => b.InputKind switch
        {
            BindKind.Axis => b.Input < 0 || b.Input >= s.Axes.Length || s.AxisKnown[b.Input],
            BindKind.Button => b.Input < 0 || b.Input >= s.Buttons.Length || s.ButtonKnown[b.Input],
            BindKind.Hat => b.Input < 0 || b.Input >= s.Hats.Length || s.HatKnown[b.Input],
            _ => true,
        };

        /// <summary>
        /// SDL_GetGamepadAxis over the bindings (SDL_gamepad.c:3839-3903), or
        /// null when a binding of the axis reads an input that is not known or
        /// no binding sets it.
        /// </summary>
        internal static short? GamepadAxis(IReadOnlyList<GamepadBinding> bindings, int axis, SdlJoystickState s)
        {
            bool any = false;
            foreach (var b in bindings)
            {
                if (b.OutputKind != BindKind.Axis || b.Output != axis) continue;
                any = true;
                if (!Known(b, s)) return null;
            }
            if (!any) return null;
            foreach (var b in bindings)
            {
                if (b.OutputKind != BindKind.Axis || b.Output != axis) continue;
                int value = 0;
                if (b.InputKind == BindKind.Axis)
                {
                    // A missing axis is passed over (3806-3810).
                    if (b.Input < 0 || b.Input >= s.Axes.Length) continue;
                    value = s.Axes[b.Input];
                    bool validInput = b.InputMin < b.InputMax
                        ? value >= b.InputMin && value <= b.InputMax
                        : value >= b.InputMax && value <= b.InputMin;
                    if (validInput)
                    {
                        if (b.InputMin != b.OutputMin || b.InputMax != b.OutputMax)
                        {
                            float normalized = (float)(value - b.InputMin) / (b.InputMax - b.InputMin);
                            value = b.OutputMin + (int)(normalized * (b.OutputMax - b.OutputMin));
                        }
                    }
                    else
                    {
                        value = 0;
                    }
                }
                else if (b.InputKind == BindKind.Button)
                {
                    if (b.Input >= 0 && b.Input < s.Buttons.Length && s.Buttons[b.Input]) value = b.OutputMax;
                }
                else if (b.InputKind == BindKind.Hat)
                {
                    int hat = b.Input >= 0 && b.Input < s.Hats.Length ? s.Hats[b.Input] : 0;
                    if ((hat & b.HatMask) != 0) value = b.OutputMax;
                }
                bool validOutput = b.OutputMin < b.OutputMax
                    ? value >= b.OutputMin && value <= b.OutputMax
                    : value >= b.OutputMax && value <= b.OutputMin;
                if (value != 0 && validOutput) return (short)value;
            }
            return 0;
        }

        /// <summary>
        /// SDL_GetGamepadButton over the bindings (SDL_gamepad.c:3934-3975), or
        /// null when a binding of the button reads an input that is not known or
        /// no binding sets it.
        /// </summary>
        internal static bool? GamepadButton(IReadOnlyList<GamepadBinding> bindings, int button, SdlJoystickState s)
        {
            bool any = false;
            foreach (var b in bindings)
            {
                if (b.OutputKind != BindKind.Button || b.Output != button) continue;
                any = true;
                if (!Known(b, s)) return null;
            }
            if (!any) return null;
            bool result = false;
            foreach (var b in bindings)
            {
                if (b.OutputKind != BindKind.Button || b.Output != button) continue;
                if (b.InputKind == BindKind.Axis)
                {
                    int value = b.Input >= 0 && b.Input < s.Axes.Length ? s.Axes[b.Input] : 0;
                    int threshold = b.InputMin + (b.InputMax - b.InputMin) / 2;
                    if (b.InputMin < b.InputMax)
                    {
                        if (value >= b.InputMin && value <= b.InputMax) result |= value >= threshold;
                    }
                    else if (value >= b.InputMax && value <= b.InputMin)
                    {
                        result |= value <= threshold;
                    }
                }
                else if (b.InputKind == BindKind.Button)
                {
                    result |= b.Input >= 0 && b.Input < s.Buttons.Length && s.Buttons[b.Input];
                }
                else if (b.InputKind == BindKind.Hat)
                {
                    int hat = b.Input >= 0 && b.Input < s.Hats.Length ? s.Hats[b.Input] : 0;
                    result |= (hat & b.HatMask) != 0;
                }
            }
            return result;
        }

        /// <summary>The joystick buttons a mapping's text names, as PadForge
        /// counts them to leave out of its raw buttons past 22
        /// (SdlDeviceWrapper.ParseMappedButtonIndices).</summary>
        internal static HashSet<int> MappedButtons(string text)
        {
            var indices = new HashSet<int>();
            if (string.IsNullOrEmpty(text)) return indices;
            foreach (var segment in text.Split(','))
            {
                int colon = segment.IndexOf(':');
                if (colon < 0) continue;
                string value = segment.Substring(colon + 1);
                if (value.Length > 1 && value[0] == 'b' && int.TryParse(value.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    indices.Add(index);
            }
            return indices;
        }

        /// <summary>PadForge's positions 11 to 21 in SDL's button numbers
        /// (SdlDeviceWrapper.GamepadButtonForPosition).</summary>
        private static readonly int[] ExtendedButtons = { 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25 };

        /// <summary>PadForge's positions 0 to 10 in SDL's button numbers
        /// (SdlDeviceWrapper.cs:2086-2096).</summary>
        private static readonly int[] StandardButtons = { South, East, West, North, LeftShoulder, RightShoulder, Back, Start, LeftStick, RightStick, Guide };

        /// <summary>
        /// What PadForge's reader writes for a device read through SDL's
        /// mapping, from SDL's joystick state and the mapping
        /// (SdlDeviceWrapper.GetGamepadState, SdlDeviceWrapper.cs:2039-2153):
        /// sticks plus 32768, triggers to 0..65535, the eleven buttons with the
        /// Back, Start and Guide rule, positions 11 to 21 a binding sets, the
        /// D-pad's angle, raw axes past 6 and raw buttons past 22 the mapping
        /// leaves free. A control is left out when a binding of it reads what
        /// the comparison cannot rebuild, and position 16 when the device has
        /// a touchpad, since the touchpad block decides it (2232-2276).
        /// </summary>
        internal static List<ExpectedControl> GamepadLayout(IReadOnlyList<GamepadBinding> bindings, SdlJoystickState s,
            string mappingText, bool extraAxes, int rawAxes, int rawButtons, bool touchpad)
        {
            var expected = new List<ExpectedControl>();
            void Stick(int position, int axis)
            {
                if (GamepadAxis(bindings, axis, s) is short v) expected.Add(new(RecordingField.Axis, position, (ushort)(v - short.MinValue)));
            }
            void Trigger(int position, int axis)
            {
                if (GamepadAxis(bindings, axis, s) is short v) expected.Add(new(RecordingField.Axis, position, (int)(v * 65535L / 32767)));
            }
            Stick(0, 0);
            Stick(1, 1);
            Trigger(2, LeftTrigger);
            Stick(3, 2);
            Stick(4, 3);
            Trigger(5, RightTrigger);

            var standard = new bool?[StandardButtons.Length];
            for (int p = 0; p < StandardButtons.Length; p++) standard[p] = GamepadButton(bindings, StandardButtons[p], s);
            // Windows makes Guide of Back and Start held together (2098-2101).
            if (standard[6] == true && standard[7] == true && standard[10] == true) standard[10] = false;
            for (int p = 0; p < standard.Length; p++)
                if (standard[p] is bool down) expected.Add(new(RecordingField.Button, p, down ? 1 : 0));
            for (int p = 0; p < ExtendedButtons.Length; p++)
            {
                int position = 11 + p;
                if (touchpad && position == 16) continue;
                if (GamepadButton(bindings, ExtendedButtons[p], s) is bool down) expected.Add(new(RecordingField.Button, position, down ? 1 : 0));
            }

            var up = GamepadButton(bindings, DpadUp, s);
            var downPad = GamepadButton(bindings, DpadDown, s);
            var left = GamepadButton(bindings, DpadLeft, s);
            var right = GamepadButton(bindings, DpadRight, s);
            bool dpadBound = up != null || downPad != null || left != null || right != null;
            if (dpadBound && Bound(bindings, DpadUp, s) && Bound(bindings, DpadDown, s) && Bound(bindings, DpadLeft, s) && Bound(bindings, DpadRight, s))
                expected.Add(new(RecordingField.Hat, 0, SdlDeviceWrapper.DpadToCentidegrees(up == true, downPad == true, left == true, right == true)));

            if (extraAxes)
                for (int i = 6; i < Math.Min(rawAxes, CustomInputState.MaxAxis) && i < s.Axes.Length; i++)
                    if (s.AxisKnown[i]) expected.Add(new(RecordingField.Axis, i, Unsigned(s.Axes[i])));
            var mapped = MappedButtons(mappingText);
            for (int i = 22; i < rawButtons && i < CustomInputState.MaxButtons && i < s.Buttons.Length; i++)
                if (!mapped.Contains(i) && s.ButtonKnown[i]) expected.Add(new(RecordingField.Button, i, s.Buttons[i] ? 1 : 0));
            return expected;
        }

        /// <summary>Whether a D-pad button is unbound or bound to known inputs:
        /// the angle is written only when none of its four reads what the
        /// comparison cannot rebuild.</summary>
        private static bool Bound(IReadOnlyList<GamepadBinding> bindings, int button, SdlJoystickState s)
        {
            foreach (var b in bindings)
                if (b.OutputKind == BindKind.Button && b.Output == button && !Known(b, s)) return false;
            return true;
        }

        /// <summary>
        /// What PadForge's reader writes for a device read in the joystick
        /// layout (SdlDeviceWrapper.GetJoystickState, SdlDeviceWrapper.cs:
        /// 2418-2474): axis i in Axis[i] and past 24 in the sliders, as many as
        /// it reads, hats as angles, buttons by number.
        /// </summary>
        internal static List<ExpectedControl> JoystickLayout(SdlJoystickState s, int axesRead, int hatsRead, int buttonsRead)
        {
            var expected = new List<ExpectedControl>();
            int axes = Math.Min(Math.Min(axesRead, s.Axes.Length), CustomInputState.MaxAxis + CustomInputState.MaxSliders);
            for (int i = 0; i < axes; i++)
            {
                if (!s.AxisKnown[i]) continue;
                if (i < CustomInputState.MaxAxis) expected.Add(new(RecordingField.Axis, i, Unsigned(s.Axes[i])));
                else expected.Add(new(RecordingField.Slider, i - CustomInputState.MaxAxis, Unsigned(s.Axes[i])));
            }
            int hats = Math.Min(Math.Min(hatsRead, s.Hats.Length), CustomInputState.MaxPovs);
            for (int i = 0; i < hats; i++)
                if (s.HatKnown[i]) expected.Add(new(RecordingField.Hat, i, Angle(s.Hats[i])));
            int buttons = Math.Min(Math.Min(buttonsRead, s.Buttons.Length), CustomInputState.MaxButtons);
            for (int i = 0; i < buttons; i++)
                if (s.ButtonKnown[i]) expected.Add(new(RecordingField.Button, i, s.Buttons[i] ? 1 : 0));
            return expected;
        }

        // ─────────────────────────────────────────────
        //  The difference
        // ─────────────────────────────────────────────

        /// <summary>How far two readers of one axis or slider may differ,
        /// a sixteenth of the travel, the XInput comparison's own gap.</summary>
        internal const int Gap = 4096;

        /// <summary>
        /// How Raw Input's reading differs from PadForge's reader's, or null
        /// when they agree: an axis or slider more than <see cref="Gap"/>
        /// apart, a hat at another angle, a button down on one side only. Each
        /// control by the recording's own name for it.
        /// </summary>
        internal static string Difference(IReadOnlyList<ExpectedControl> expected, CustomInputState state, RecordingControlKind kind)
        {
            if (state == null) return null;
            StringBuilder sb = null;
            foreach (var e in expected)
            {
                int actual;
                switch (e.Field)
                {
                    case RecordingField.Axis:
                        if ((uint)e.Index >= (uint)state.Axis.Length) continue;
                        actual = state.Axis[e.Index];
                        if (Math.Abs(actual - e.Value) <= Gap) continue;
                        break;
                    case RecordingField.Slider:
                        if ((uint)e.Index >= (uint)state.Sliders.Length) continue;
                        actual = state.Sliders[e.Index];
                        if (Math.Abs(actual - e.Value) <= Gap) continue;
                        break;
                    case RecordingField.Hat:
                        if ((uint)e.Index >= (uint)state.Povs.Length) continue;
                        actual = state.Povs[e.Index];
                        if (actual == e.Value) continue;
                        break;
                    case RecordingField.Button:
                        if ((uint)e.Index >= (uint)state.Buttons.Length) continue;
                        actual = state.Buttons[e.Index] ? 1 : 0;
                        if (actual == e.Value) continue;
                        break;
                    default:
                        continue;
                }
                sb ??= new StringBuilder();
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(RecordingControlNames.Input(kind, e.Field, e.Index)).Append(' ');
                if (e.Field == RecordingField.Button)
                    sb.Append(e.Value != 0 ? "down" : "up").Append(" while PadForge's reader reads it ").Append(actual != 0 ? "down" : "up");
                else
                    sb.Append(e.Value.ToString(CultureInfo.InvariantCulture)).Append(" while PadForge's reader reads ").Append(actual.ToString(CultureInfo.InvariantCulture));
            }
            return sb?.ToString();
        }

        // ─────────────────────────────────────────────
        //  Names
        // ─────────────────────────────────────────────

        /// <summary>A report control's name in fixed English, never the
        /// device's own words: the axis names ForceActuators gives DirectInput's
        /// axes, the HID names of the other usages, Button N on the Button page.</summary>
        internal static string UsageName(ushort page, ushort usage)
        {
            if (page == 0x09) return "Button " + usage.ToString(CultureInfo.InvariantCulture);
            if (page == 0x01)
                switch (usage)
                {
                    case 0x30: return "X Axis";
                    case 0x31: return "Y Axis";
                    case 0x32: return "Z Axis";
                    case 0x33: return "X Rotation";
                    case 0x34: return "Y Rotation";
                    case 0x35: return "Z Rotation";
                    case 0x36: return "Slider";
                    case 0x37: return "Dial";
                    case 0x38: return "Wheel";
                    case 0x39: return "Hat Switch";
                }
            if (page == 0x02)
                switch (usage)
                {
                    case 0xBA: return "Rudder";
                    case 0xBB: return "Throttle";
                    case 0xC4: return "Accelerator";
                    case 0xC5: return "Brake";
                    case 0xC6: return "Clutch";
                    case 0xC8: return "Steering";
                }
            return "Usage " + page.ToString("X4", CultureInfo.InvariantCulture) + ":" + usage.ToString("X4", CultureInfo.InvariantCulture);
        }
    }
}
