using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Recording;

namespace PadForge.Services.Automation
{
    /// <summary>A control of a device as scripts name it (#600 item 21): its
    /// name, its kind (<c>b</c> a button, <c>a</c> an axis or a slider,
    /// <c>h</c> a hat) and its index on the pipe (axes 0 to 23, sliders 24
    /// to 31).</summary>
    internal readonly record struct ScriptControl(string Name, char Kind, int Index);

    /// <summary>
    /// The names scripts use for a device's controls (#600 item 21), one
    /// naming everywhere: the libraries' properties and hotkeys, the pipe's
    /// <c>s</c>, <c>own</c>, <c>set</c>, <c>rel</c> and <c>sub</c> lines,
    /// <c>caps</c>, and the device panel's Script ID row.
    ///
    /// <para>They are the stable English names a problem recording uses
    /// (RecordingControlNames, picked by RecordingKinds.Of): A, LeftX and
    /// DPad for a device read in SDL's gamepad layout, MouseX and MouseLeft
    /// for a mouse, KeyA for a keyboard, and Axis0, Slider0, Hat0 and Button0
    /// for any other. A program's own device has the names it declared, made
    /// into one word ("Left Pedal" is LeftPedal), unless a name would be no
    /// word, would read as another control (a hat's direction among them)
    /// or a word of the pipe, or is taken: that control keeps its numbered
    /// name. The hats are named first. Every control also
    /// answers to its numbered name and to the pipe's short form (b3, a0,
    /// h0). Names are read without regard to case.</para>
    ///
    /// <para>A hat's directions are its name and Up, Right, Down or Left
    /// (DPadUp, Hat1Left), and the first hat's also answer to HatUp and the
    /// rest.</para>
    /// </summary>
    internal static class ScriptNames
    {
        /// <summary>The words of the pipe a declared name may not take.</summary>
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "gyro", "accel", "bat", "charging", "rest", "after", "in", "claim", "above", "below", "hyst", "line", "all",
            "state", "buttons", "hats", "axes", "axis", "hz", "devices", "profile", "engine",
        };

        private static readonly string[] DirectionWords = { "Up", "Right", "Down", "Left" };

        /// <summary>The controls of a device, buttons first, then axes and
        /// sliders, then hats. Read under the device list's lock by the
        /// caller for a row.</summary>
        internal static List<ScriptControl> Of(UserDevice ud)
        {
            var list = new List<ScriptControl>();
            if (ud == null) return list;
            if (ud.Device is ProgramDevice program && ud.CapType != InputDeviceType.Gamepad) return Declared(program.Shape);
            var kind = RecordingKinds.Of(ud);

            var buttons = new SortedSet<int>();
            if (ud.CapButtonIndices != null) foreach (int i in ud.CapButtonIndices) buttons.Add(i);
            else for (int i = 0; i < Math.Min(ud.CapButtonCount, CustomInputState.MaxButtons); i++) buttons.Add(i);
            foreach (int i in buttons)
                if ((uint)i < CustomInputState.MaxButtons)
                    list.Add(new ScriptControl(RecordingControlNames.Input(kind, RecordingField.Button, i), 'b', i));

            var axes = new SortedSet<int>();
            if (ud.CapAxisIndices != null) foreach (int i in ud.CapAxisIndices) axes.Add(i);
            else for (int i = 0; i < Math.Min(ud.CapAxeCount, CustomInputState.MaxAxis); i++) axes.Add(i);
            if (ud.DeviceObjects != null)
                foreach (var o in ud.DeviceObjects)
                    if (o.IsAxis) axes.Add(o.IsSlider ? ProgramAxis.FirstSlider + o.InputIndex : o.InputIndex);
            foreach (int i in axes)
            {
                if ((uint)i >= ProgramAxis.End) continue;
                string name = i >= ProgramAxis.FirstSlider
                    ? RecordingControlNames.Input(kind, RecordingField.Slider, i - ProgramAxis.FirstSlider)
                    : RecordingControlNames.Input(kind, RecordingField.Axis, i);
                list.Add(new ScriptControl(name, 'a', i));
            }

            for (int i = 0; i < Math.Min(ud.CapPovCount, CustomInputState.MaxPovs); i++)
                list.Add(new ScriptControl(RecordingControlNames.Input(kind, RecordingField.Hat, i), 'h', i));
            return list;
        }

        /// <summary>A program device's controls by the names it declared, as
        /// one word each where that word is free. The hats are named first,
        /// and each hat's name with Up, Right, Down and Left is taken with
        /// it, so a button or an axis declared "DPad Up" beside a hat
        /// "DPad" keeps its numbered name: that word is the hat's up. The
        /// list keeps its order, buttons first.</summary>
        internal static List<ScriptControl> Declared(ProgramDeviceShape shape)
        {
            var list = new List<ScriptControl>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string Pick(string declared, string numbered)
            {
                string word = Word(declared);
                if (word == null || taken.Contains(word) || Reserved.Contains(word) || LooksNumbered(word)) word = numbered;
                taken.Add(word);
                return word;
            }
            var hats = new string[shape.Hats.Count];
            for (int i = 0; i < hats.Length; i++)
            {
                hats[i] = Pick(shape.Hats[i], "Hat" + Text(i));
                foreach (var d in DirectionWords) taken.Add(hats[i] + d);
            }
            foreach (var (index, name) in shape.Buttons)
                list.Add(new ScriptControl(Pick(name, "Button" + Text(index)), 'b', index));
            foreach (var axis in shape.Axes)
                list.Add(new ScriptControl(Pick(axis.Name, axis.IsSlider ? "Slider" + Text(axis.Index - ProgramAxis.FirstSlider) : "Axis" + Text(axis.Index)), 'a', axis.Index));
            for (int i = 0; i < hats.Length; i++)
                list.Add(new ScriptControl(hats[i], 'h', i));
            return list;
        }

        private static string Text(int i) => i.ToString(CultureInfo.InvariantCulture);

        /// <summary>A name as one word: its letters and digits, each word's
        /// first letter upper case. Null when nothing is left or it starts
        /// with a digit.</summary>
        internal static string Word(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var sb = new StringBuilder(name.Length);
            bool start = true;
            foreach (char c in name)
            {
                if (c < 128 && char.IsLetterOrDigit(c))
                {
                    sb.Append(start ? char.ToUpperInvariant(c) : c);
                    start = false;
                }
                else start = true;
            }
            if (sb.Length == 0 || char.IsDigit(sb[0]) || sb.Length > 32) return null;
            return sb.ToString();
        }

        /// <summary>Whether a word reads as a numbered control (Button3,
        /// Axis0), a pipe's short form (b3), a touchpad's word (t0.1, tc0)
        /// or a hat's direction (HatUp).</summary>
        private static bool LooksNumbered(string word)
        {
            if (TryShort(word.ToLowerInvariant(), out _, out _)) return true;
            if (Numbered(word, "Button", int.MaxValue, out _) || Numbered(word, "Axis", int.MaxValue, out _)
                || Numbered(word, "Slider", int.MaxValue, out _) || Numbered(word, "Hat", int.MaxValue, out _)) return true;
            string lower = word.ToLowerInvariant();
            if (lower.Length > 1 && lower[0] == 't' && (char.IsDigit(lower[1]) || lower.StartsWith("tc", StringComparison.Ordinal))) return true;
            foreach (var d in DirectionWords)
                if (word.EndsWith(d, StringComparison.OrdinalIgnoreCase) && word.StartsWith("Hat", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary><c>b&lt;i&gt;</c>, <c>a&lt;i&gt;</c>, <c>h&lt;i&gt;</c>,
        /// lower case.</summary>
        private static bool TryShort(string key, out char kind, out int index)
        {
            kind = '\0';
            index = -1;
            if (key.Length < 2 || key[0] is not ('b' or 'a' or 'h')) return false;
            if (!int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
            kind = key[0];
            int end = kind switch { 'b' => CustomInputState.MaxButtons, 'a' => ProgramAxis.End, _ => CustomInputState.MaxPovs };
            return index >= 0 && index < end;
        }

        /// <summary>The control a word names on a device with
        /// <paramref name="controls"/>: a name, a numbered name or a short
        /// form. False for a word that names none.</summary>
        internal static bool TryResolve(string word, IReadOnlyList<ScriptControl> controls, out char kind, out int index)
        {
            kind = '\0';
            index = -1;
            if (string.IsNullOrEmpty(word)) return false;
            if (TryShort(word.ToLowerInvariant(), out kind, out index)) return true;
            if (controls != null)
                foreach (var c in controls)
                    if (string.Equals(c.Name, word, StringComparison.OrdinalIgnoreCase))
                    {
                        kind = c.Kind;
                        index = c.Index;
                        return true;
                    }
            // The numbered names answer on every device.
            if (Numbered(word, "Button", CustomInputState.MaxButtons, out index)) { kind = 'b'; return true; }
            if (Numbered(word, "Axis", CustomInputState.MaxAxis, out index)) { kind = 'a'; return true; }
            if (Numbered(word, "Slider", CustomInputState.MaxSliders, out index)) { kind = 'a'; index += ProgramAxis.FirstSlider; return true; }
            if (Numbered(word, "Hat", CustomInputState.MaxPovs, out index)) { kind = 'h'; return true; }
            kind = '\0';
            index = -1;
            return false;
        }

        private static bool Numbered(string word, string stem, int count, out int index)
        {
            index = -1;
            if (word.Length <= stem.Length || !word.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(word.AsSpan(stem.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index) && index < count;
        }

        /// <summary>A hat direction a word names: a hat's name and Up, Right,
        /// Down or Left, or HatUp and the rest for the first hat.</summary>
        internal static bool TryDirection(string word, IReadOnlyList<ScriptControl> controls, out int hat, out HatDirections direction)
        {
            hat = -1;
            direction = HatDirections.None;
            if (string.IsNullOrEmpty(word)) return false;
            for (int d = 0; d < DirectionWords.Length; d++)
            {
                string end = DirectionWords[d];
                if (word.Length <= end.Length || !word.EndsWith(end, StringComparison.OrdinalIgnoreCase)) continue;
                string head = word.Substring(0, word.Length - end.Length);
                if (string.Equals(head, "Hat", StringComparison.OrdinalIgnoreCase)) hat = 0;
                else if (!TryResolve(head, controls, out char kind, out hat) || kind != 'h') { hat = -1; continue; }
                direction = (HatDirections)(1 << d);
                return true;
            }
            return false;
        }

        /// <summary>The name a control has on a device, or its numbered name
        /// when the list names none.</summary>
        internal static string NameOf(IReadOnlyList<ScriptControl> controls, char kind, int index)
        {
            if (controls != null)
                foreach (var c in controls)
                    if (c.Kind == kind && c.Index == index) return c.Name;
            return kind switch
            {
                'b' => "Button" + Text(index),
                'h' => "Hat" + Text(index),
                _ => index >= ProgramAxis.FirstSlider ? "Slider" + Text(index - ProgramAxis.FirstSlider) : "Axis" + Text(index),
            };
        }

        /// <summary>The controls of a device as the device panel lists them:
        /// their names, apart by commas.</summary>
        internal static string List(UserDevice ud)
        {
            var controls = Of(ud);
            var names = new List<string>(controls.Count);
            foreach (var c in controls) names.Add(c.Name);
            return string.Join(", ", names);
        }
    }
}
