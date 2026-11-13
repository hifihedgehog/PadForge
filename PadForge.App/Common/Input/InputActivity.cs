using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>Which devices are sending input now (#574, the world-class
    /// pass): the small light beside a device's name on a controller's Home
    /// card and in its page's header. A device is sending while a button or
    /// a direction is held, and for a moment after an axis moved, so a
    /// short tap can still be seen.
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
                | state.MouseRawDX != seen.MouseX | state.MouseRawDY != seen.MouseY;
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
            return false;
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
