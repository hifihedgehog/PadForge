using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// How the Automation pipe names and describes devices (#597). Every kind
    /// of device PadForge reads is listed and read alike, keyboards and mice
    /// included: the owner's ruling is that every input device is readable
    /// at the Read level, with no grant or exclusion for any kind.
    ///
    /// <para>What a program reads is scrubbed: a device is named by its
    /// product and a short ID PadForge makes up, never its serial number,
    /// path, Bluetooth address or a name the user typed, and every name a
    /// read gives goes through the scrubber (AutomationReadScrub).</para>
    /// </summary>
    internal static class AutomationDevices
    {
        private static readonly ConcurrentDictionary<Guid, string> Ids = new();
        private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

        /// <summary>
        /// A device's short ID: eight letters and digits from a hash of its
        /// instance GUID, so it is the same for that device across runs and
        /// says nothing of its serial number or path.
        /// </summary>
        internal static string IdOf(Guid instanceGuid)
            => Ids.GetOrAdd(instanceGuid, static g =>
            {
                byte[] hash = SHA256.HashData(g.ToByteArray());
                // Forty bits, five at a time.
                ulong bits = 0;
                for (int i = 0; i < 5; i++) bits = (bits << 8) | hash[i];
                var id = new char[8];
                for (int i = 7; i >= 0; i--)
                {
                    id[i] = Base32[(int)(bits & 31)];
                    bits >>= 5;
                }
                return new string(id);
            });

        /// <summary>A device kind's word on the pipe. Every
        /// <see cref="InputDeviceType"/> has its own. Fixed ASCII, apart from
        /// the names in code, which a rename would change.</summary>
        internal static string TypeWord(int capType) => capType switch
        {
            InputDeviceType.Mouse => "mouse",
            InputDeviceType.Keyboard => "keyboard",
            InputDeviceType.Joystick => "joystick",
            InputDeviceType.Gamepad => "gamepad",
            InputDeviceType.Driving => "wheel",
            InputDeviceType.Flight => "flight-stick",
            InputDeviceType.FirstPerson => "first-person",
            InputDeviceType.Supplemental => "supplemental",
            InputDeviceType.Touchpad => "touchpad",
            InputDeviceType.Midi => "midi",
            InputDeviceType.Nfc => "nfc",
            InputDeviceType.ConsumerControl => "media-keys",
            InputDeviceType.HeadsetMotion => "headset-motion",
            InputDeviceType.Microphone => "microphone",
            InputDeviceType.HandheldButtons => "handheld-buttons",
            InputDeviceType.SystemMotion => "system-motion",
            InputDeviceType.HeadTracker => "head-tracker",
            InputDeviceType.Tablet => "tablet",
            InputDeviceType.VrController => "vr-controller",
            InputDeviceType.LogitechGKeys => "g-keys",
            InputDeviceType.AnalogKeyboard => "analog-keyboard",
            InputDeviceType.WebMenus => "web-menus",
            InputDeviceType.PeripheralLighting => "lighting",
            InputDeviceType.PeripheralHaptics => "haptics",
            InputDeviceType.KeyboardScreenKeys => "screen-keys",
            InputDeviceType.NovintFalcon => "falcon",
            InputDeviceType.GameTelemetry => "telemetry",
            InputDeviceType.TwitchChat => "twitch-chat",
            InputDeviceType.Program => "program",
            _ => "device",
        };

        /// <summary>The name a device is shown by to a program: the name its
        /// product gives, scrubbed, never one the user typed or the name a
        /// Bluetooth device carries for its owner. A device whose product
        /// names nothing is "Device".</summary>
        internal static string NameOf(UserDevice ud)
        {
            string name = string.IsNullOrWhiteSpace(ud.ProductName) ? null : AutomationReadScrub.Text(ud.ProductName.Trim());
            return string.IsNullOrWhiteSpace(name) ? "Device" : name;
        }

        /// <summary>A control's name as a program reads it: scrubbed, or the
        /// name PadForge gives the control by its index.</summary>
        private static string ControlName(string name, string fallback)
        {
            string scrubbed = string.IsNullOrWhiteSpace(name) ? null : AutomationReadScrub.Text(name.Trim());
            return string.IsNullOrWhiteSpace(scrubbed) ? fallback : scrubbed;
        }

        private static int SliderCount(DeviceObjectItem[] objects)
        {
            int n = 0;
            if (objects != null) foreach (var o in objects) if (o.IsSlider) n++;
            return n;
        }

        /// <summary>One row of <c>devices</c>. Read under the device list's
        /// lock by the caller.</summary>
        internal static string Row(UserDevice ud)
        {
            var sb = new StringBuilder(160);
            sb.Append("device id=").Append(IdOf(ud.InstanceGuid))
              .Append(" type=").Append(TypeWord(ud.CapType))
              .Append(" online=").Append(ud.IsOnline ? '1' : '0')
              .Append(" name=").Append(AutomationLine.Quote(NameOf(ud)))
              .Append(" buttons=").Append(ud.CapButtonCount)
              .Append(" axes=").Append(ud.CapAxeCount)
              .Append(" sliders=").Append(SliderCount(ud.DeviceObjects))
              .Append(" hats=").Append(ud.CapPovCount)
              .Append(" gyro=").Append(ud.HasGyro ? '1' : '0')
              .Append(" accel=").Append(ud.HasAccel ? '1' : '0')
              .Append(" touchpads=").Append(ud.HasTouchpad ? Math.Max(1, ud.CapTouchpadCount) : 0);
            return sb.ToString();
        }

        /// <summary>The rest a program can count on for an axis or slider of
        /// a device, or null where PadForge cannot name one: a flight
        /// stick's throttle rests wherever the hand left it.</summary>
        internal static int? RestOf(UserDevice ud, int index)
        {
            if (ud.Device is ProgramDevice program)
            {
                foreach (var axis in program.Shape.Axes)
                    if (axis.Index == index) return axis.Rest;
                return null;
            }
            if (index >= CustomInputState.MaxAxis) return null;
            bool gamepad = ud.Device is SdlDeviceWrapper w
                ? w.GamepadHandle != IntPtr.Zero && !ud.ForceRawJoystickMode
                : ud.CapType == InputDeviceType.Gamepad;
            if (gamepad && index < 6) return index == 2 || index == 5 ? 0 : 32768;
            if (ud.CapType == InputDeviceType.Mouse && index < 3) return SdlMouseWrapper.AxisCenter;
            return null;
        }

        /// <summary>The rows of <c>caps</c>: each control with its index and
        /// the name the interface shows, an axis's rest, the sensors, the
        /// touchpads, the battery and the feedback the device takes. With
        /// <paramref name="script"/>, each control's row also carries
        /// <c>script=</c>, the name scripts use for it (#600 item 21). Read
        /// under the device list's lock by the caller.</summary>
        internal static List<string> Caps(UserDevice ud, IReadOnlyList<ScriptControl> script = null)
        {
            string Script(char kind, int index) => script == null ? string.Empty : " script=" + ScriptNames.NameOf(script, kind, index);
            var rows = new List<string>();
            var objects = ud.DeviceObjects ?? Array.Empty<DeviceObjectItem>();

            // Axes, from the record's own list, then the sliders.
            var axisIndices = ud.CapAxisIndices;
            var axes = new SortedDictionary<int, string>();
            if (axisIndices != null) foreach (int i in axisIndices) axes[i] = null;
            else for (int i = 0; i < Math.Min(ud.CapAxeCount, CustomInputState.MaxAxis); i++) axes[i] = null;
            foreach (var o in objects)
            {
                if (!o.IsAxis) continue;
                int index = o.IsSlider ? ProgramAxis.FirstSlider + o.InputIndex : o.InputIndex;
                if (index < 0 || index >= ProgramAxis.End) continue;
                axes[index] = o.Name;
            }
            foreach (var (index, name) in axes)
            {
                var row = new StringBuilder("axis index=").Append(index)
                    .Append(" name=").Append(AutomationLine.Quote(ControlName(name,
                        index >= ProgramAxis.FirstSlider ? "Slider " + (index - ProgramAxis.FirstSlider) : "Axis " + index)));
                int? rest = RestOf(ud, index);
                if (rest.HasValue) row.Append(" rest=").Append(rest.Value);
                row.Append(Script('a', index));
                rows.Add(row.ToString());
            }

            // Buttons: the record's positions, named where an object names
            // them.
            var buttons = new SortedDictionary<int, string>();
            var buttonIndices = ud.CapButtonIndices;
            if (buttonIndices != null) foreach (int i in buttonIndices) buttons[i] = null;
            else for (int i = 0; i < Math.Min(ud.CapButtonCount, CustomInputState.MaxButtons); i++) buttons[i] = null;
            foreach (var o in objects)
                if (o.IsButton && o.InputIndex >= 0 && o.InputIndex < CustomInputState.MaxButtons)
                    buttons[o.InputIndex] = o.Name;
            foreach (var (index, name) in buttons)
                rows.Add("button index=" + index + " name=" + AutomationLine.Quote(ControlName(name, "Button " + index)) + Script('b', index));

            var hats = new SortedDictionary<int, string>();
            for (int i = 0; i < Math.Min(ud.CapPovCount, CustomInputState.MaxPovs); i++) hats[i] = null;
            foreach (var o in objects)
                if (o.IsPov && o.InputIndex >= 0 && o.InputIndex < CustomInputState.MaxPovs) hats[o.InputIndex] = o.Name;
            foreach (var (index, name) in hats)
                rows.Add("hat index=" + index + " name=" + AutomationLine.Quote(ControlName(name, "Hat " + index)) + Script('h', index));

            if (ud.HasGyro) rows.Add("gyro");
            if (ud.HasAccel) rows.Add("accel");
            if (ud.HasTouchpad)
            {
                int pads = Math.Max(1, ud.CapTouchpadCount);
                var fingers = ud.CapTouchpadFingerCounts;
                for (int p = 0; p < pads; p++)
                    rows.Add("touchpad index=" + p + " fingers=" + (fingers != null && p < fingers.Length ? fingers[p] : 2));
            }
            bool battery = ud.Device is ProgramDevice pd ? pd.Shape.Battery : (ud.InputState?.BatteryPercent ?? -1) >= 0;
            if (battery) rows.Add("battery");

            var relayed = ud.Device as IRelayedFeedbackDevice;
            bool player = ud.Device is ProgramDevice p2 ? p2.Shape.Player : relayed != null;
            bool trigfx = ud.Device is ProgramDevice p3 && p3.Shape.TriggerEffects;
            rows.Add("feedback rumble=" + (ud.HasForceFeedback ? 1 : 0)
                + " trigrumble=" + (ud.HasRumbleTriggers ? 1 : 0)
                + " light=" + (relayed?.HasLightbar == true ? 1 : 0)
                + " player=" + (player ? 1 : 0)
                + " trigfx=" + (trigfx ? 1 : 0));
            return rows;
        }

        /// <summary>
        /// A device's whole state on one line, after <c>ok</c>:
        /// <c>b=</c> the buttons as hex, two digits a byte with button 0 in
        /// the lowest bit of the first, <c>a=</c> the 24 axes and 8 sliders,
        /// <c>h=</c> the 4 hats, <c>gyro=</c> and <c>accel=</c>, <c>t=</c>
        /// the touchpads apart by a slash, each <c>c</c> or <c>u</c> for its
        /// click and then <c>:x;y;down</c> for each finger, or <c>-</c> for
        /// none, and <c>bat=</c> the battery and whether it charges.
        /// </summary>
        /// <summary>The state line with axes and hats in plain units
        /// (#600): each axis read against its rest in
        /// <paramref name="rests"/>, hats in degrees or -1.</summary>
        internal static string StateLine(CustomInputState s, int buttonCount, int[] rests) => StateLine(s, buttonCount, rests, plain: true);

        internal static string StateLine(CustomInputState s, int buttonCount) => StateLine(s, buttonCount, null, plain: false);

        private static string StateLine(CustomInputState s, int buttonCount, int[] rests, bool plain)
        {
            string Axis(int raw, int index)
                => plain ? PlainUnits.Format(PlainUnits.AxisToPlain(raw, rests?[index] ?? PlainUnits.DefaultRest(index)))
                    : raw.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sb = new StringBuilder(512);
            sb.Append("ok b=");
            int bytes = Math.Max(1, (Math.Clamp(buttonCount, 0, CustomInputState.MaxButtons) + 7) / 8);
            for (int b = 0; b < bytes; b++)
            {
                int v = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int i = b * 8 + bit;
                    if (i < s.Buttons.Length && s.Buttons[i]) v |= 1 << bit;
                }
                sb.Append(v.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append(" a=");
            for (int i = 0; i < CustomInputState.MaxAxis; i++) sb.Append(Axis(s.Axis[i], i)).Append(',');
            for (int i = 0; i < CustomInputState.MaxSliders; i++)
            {
                sb.Append(Axis(s.Sliders[i], ProgramAxis.FirstSlider + i));
                if (i < CustomInputState.MaxSliders - 1) sb.Append(',');
            }
            sb.Append(" h=");
            for (int i = 0; i < CustomInputState.MaxPovs; i++)
            {
                if (plain) sb.Append(PlainUnits.Format(PlainUnits.HatToPlain(s.Povs[i])));
                else sb.Append(s.Povs[i]);
                if (i < CustomInputState.MaxPovs - 1) sb.Append(',');
            }
            sb.Append(" gyro=").Append(AutomationLine.Number(s.Gyro[0])).Append(',')
              .Append(AutomationLine.Number(s.Gyro[1])).Append(',').Append(AutomationLine.Number(s.Gyro[2]));
            sb.Append(" accel=").Append(AutomationLine.Number(s.Accel[0])).Append(',')
              .Append(AutomationLine.Number(s.Accel[1])).Append(',').Append(AutomationLine.Number(s.Accel[2]));
            sb.Append(" t=");
            if (s.Touchpads == null || s.Touchpads.Length == 0) sb.Append('-');
            else
            {
                for (int p = 0; p < s.Touchpads.Length; p++)
                {
                    var pad = s.Touchpads[p];
                    if (p > 0) sb.Append('/');
                    if (pad == null) { sb.Append('-'); continue; }
                    sb.Append(pad.Clicked ? 'c' : 'u');
                    for (int f = 0; f < pad.MaxFingers; f++)
                        sb.Append(':').Append(AutomationLine.Number(pad.FingerX[f])).Append(';')
                          .Append(AutomationLine.Number(pad.FingerY[f])).Append(';').Append(pad.FingerDown[f] ? 1 : 0);
                }
            }
            sb.Append(" bat=").Append(s.BatteryPercent).Append(',').Append(s.BatteryCharging ? 1 : 0);
            return sb.ToString();
        }

        /// <summary>Copies a device's own state off the poll thread: the
        /// copy is taken again when Step 2 published a new state while it
        /// ran, since the buffers are pooled. Null when the device has not
        /// been read.</summary>
        internal static CustomInputState CopyState(UserDevice ud)
        {
            if (ud.Device is ProgramDevice program) return program.PeekState().Clone();
            var copy = new CustomInputState();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                long before = System.Threading.Volatile.Read(ref ud.InputStateSeq);
                var source = ud.ReadInputState ?? ud.InputState;
                if (source == null) return null;
                source.CopyInto(copy);
                if (System.Threading.Volatile.Read(ref ud.InputStateSeq) == before) return copy;
            }
            return copy;
        }
    }
}
