using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>Which devices are sending input now (#574, the world-class
    /// pass): the small light beside a device's name on a controller's Home
    /// card and in its page's header, and the light on a device's row on
    /// the Devices page. A device is sending while a button or a direction
    /// is held, and for a moment after an axis moved, so a short tap can
    /// still be seen.
    ///
    /// <para>Every kind of input the idle test counts is counted here. A
    /// finger on a touchpad, a pen on a tablet and a pad pressed in are
    /// held, as are a note on a MIDI device and a key above rest on an
    /// analog keyboard, and a MIDI knob or pitch wheel that turns has
    /// moved. A touchpad's row stayed dark under a finger while the light
    /// read buttons, directions, axes and the mouse's counts alone (owner,
    /// 2026-11-14).</para>
    ///
    /// <para>It is asked at the interface's own pace, about thirty times a
    /// second, and reads the state the engine last wrote. It keeps a copy
    /// of each device's axes to tell movement by, so it adds nothing to the
    /// engine's own loop. Motion sensors are left out, as the idle test
    /// leaves them out: a gyro never settles.</para></summary>
    internal sealed class InputActivity
    {
        /// <summary>How long after the last input a device still counts as
        /// sending, in milliseconds.</summary>
        internal const int HoldMs = 350;

        private sealed class Seen
        {
            internal int[] Axis = Array.Empty<int>();
            internal int[] Sliders = Array.Empty<int>();
            internal int MouseX, MouseY;
            internal byte[] Knobs = Array.Empty<byte>();
            internal int PitchBend;
            internal long LastActive = long.MinValue;
            internal bool Any;
        }

        private readonly Dictionary<Guid, Seen> _seen = new();

        /// <summary>Whether the device is sending input, by its state now
        /// and the state at the last look. The first look at a device only
        /// notes where its axes rest.</summary>
        internal bool Look(Guid device, CustomInputState state, bool gamepad, long nowMs)
        {
            if (state == null)
            {
                _seen.Remove(device);
                return false;
            }
            if (!_seen.TryGetValue(device, out var seen))
                _seen[device] = seen = new Seen();

            bool active = Held(state) || (gamepad && !IdleInputDetector.IsGamepadIdle(state));
            bool moved = Moved(ref seen.Axis, state.Axis) | Moved(ref seen.Sliders, state.Sliders)
                | state.MouseRawDX != seen.MouseX | state.MouseRawDY != seen.MouseY
                | MidiMoved(seen, state.Midi);
            seen.MouseX = state.MouseRawDX;
            seen.MouseY = state.MouseRawDY;
            if (seen.Any && moved) active = true;
            seen.Any = true;

            if (active) seen.LastActive = nowMs;
            return seen.LastActive != long.MinValue && nowMs - seen.LastActive <= HoldMs;
        }

        /// <summary>Forgets a device, so its next look starts over.</summary>
        internal void Forget(Guid device) => _seen.Remove(device);

        /// <summary>Forgets every device but these.</summary>
        internal void KeepOnly(ICollection<Guid> devices)
        {
            if (_seen.Count == 0) return;
            List<Guid> gone = null;
            foreach (var key in _seen.Keys)
                if (!devices.Contains(key)) (gone ??= new List<Guid>()).Add(key);
            if (gone != null)
                foreach (var key in gone) _seen.Remove(key);
        }

        private static bool Held(CustomInputState state)
        {
            var buttons = state.Buttons;
            if (buttons != null)
                for (int i = 0; i < buttons.Length; i++)
                    if (buttons[i]) return true;
            var povs = state.Povs;
            if (povs != null)
                for (int i = 0; i < povs.Length; i++)
                    if (povs[i] >= 0) return true;

            // A finger on a touchpad or a pen on a tablet, and a pad
            // pressed in.
            var pads = state.Touchpads;
            if (pads != null)
                for (int p = 0; p < pads.Length; p++)
                {
                    var pad = pads[p];
                    if (pad == null) continue;
                    if (pad.Clicked) return true;
                    var down = pad.FingerDown;
                    if (down == null) continue;
                    for (int f = 0; f < down.Length; f++)
                        if (down[f]) return true;
                }

            // A note held on a MIDI device.
            var notes = state.Midi?.Notes;
            if (notes != null)
                for (int i = 0; i < notes.Length; i++)
                    if (notes[i]) return true;

            // A key above rest on an analog keyboard: an entry exists only
            // while its key is.
            return state.AnalogKeys != null && state.AnalogKeys.Count > 0;
        }

        /// <summary>Whether a MIDI knob or the pitch wheel has turned since
        /// the copy was taken. A knob's value is copied as it changes, so a
        /// knob left where it was turned to is at rest. The pitch wheel
        /// springs back, and counts past the idle test's slop.</summary>
        private static bool MidiMoved(Seen seen, MidiInputState midi)
        {
            if (midi == null) return false;
            bool moved = false;
            var knobs = midi.Cc;
            if (knobs != null)
            {
                if (seen.Knobs.Length != knobs.Length)
                    seen.Knobs = (byte[])knobs.Clone();
                else
                    for (int i = 0; i < knobs.Length; i++)
                    {
                        if (knobs[i] == seen.Knobs[i]) continue;
                        moved = true;
                        seen.Knobs[i] = knobs[i];
                    }
            }
            if (Math.Abs(midi.PitchBend - seen.PitchBend) > IdleInputDetector.DeltaSlop)
            {
                moved = true;
                seen.PitchBend = midi.PitchBend;
            }
            return moved;
        }

        /// <summary>Whether any value stands more than the idle test's
        /// slop from where the copy has it. A value that does is copied, so
        /// a slow sweep counts each time it has gone that far, and a value
        /// that only shakes inside the slop never counts.</summary>
        private static bool Moved(ref int[] copy, int[] now)
        {
            if (now == null) return false;
            if (copy.Length != now.Length)
            {
                copy = (int[])now.Clone();
                return false;
            }
            bool moved = false;
            for (int i = 0; i < now.Length; i++)
            {
                if (Math.Abs(now[i] - copy[i]) <= IdleInputDetector.DeltaSlop) continue;
                moved = true;
                copy[i] = now[i];
            }
            return moved;
        }
    }
}
