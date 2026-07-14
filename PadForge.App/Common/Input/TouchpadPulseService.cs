using System;
using System.Collections.Concurrent;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Sony-side delivery for touchpad swipe-haptic ticks (discussion
    /// #219). A tick raises a short-lived pulse level per (slot, device);
    /// <c>InputService.SlotRumbleForDeviceProvider</c> mixes the level
    /// into the DS5/DS4 rumble bytes via max(), the same idiom
    /// <c>ScaleRumbleForDevice</c> uses for audio-bass rumble, so the
    /// dispatcher stays the sole writer (memory:
    /// sony-rumble-sole-writer-architecture.md) and a pulse coexists with
    /// live game rumble instead of replacing it.
    ///
    /// <para>Burst shape: hold the intensity for
    /// <see cref="PulseDurationMs"/> = 80 ms, then drop to zero. That is
    /// DS4MapperTest's DS4 haptic burst (HAPTICS_DURATION_DEFAULT = 80,
    /// DS4Device.cs:202, enforced in DS4Reader.CheckFeedbackStatus
    /// :441-456). Repeated ticks re-arm the window; overlapping ticks
    /// max-combine (DS4Mapper.CheckLeftHapticSide's
    /// "pending &lt; ratio" merge, :984-1022). 80 ms also guarantees the
    /// dispatcher's 33 ms tick samples the pulse at least twice (on,
    /// then off).</para>
    ///
    /// <para>The Steam Controller family does NOT come through here; its
    /// ticks ride <see cref="HapticToneService.QueueTouchpadPulse"/>'s
    /// per-side actuator commands.</para>
    /// </summary>
    internal static class TouchpadPulseService
    {
        /// <summary>Pulse hold time in ms (DS4MapperTest
        /// DS4Device.HAPTICS_DURATION_DEFAULT).</summary>
        public const int PulseDurationMs = 80;

        private sealed class Cell
        {
            public long UntilMs;
            public float Amp;
        }

        private static readonly ConcurrentDictionary<(int Slot, Guid Device), Cell> _cells = new();

        /// <summary>Sony pads the effects dispatcher is the sole rumble
        /// writer for. Mirrors the exact PID set of the SDL-rumble skip
        /// in <c>InputManager.Step2.ApplyForceFeedback</c>.</summary>
        public static bool IsSonyRumblePad(Engine.Data.UserDevice ud)
        {
            if (ud == null || ud.VendorId != 0x054C) return false;
            return ud.ProdId is 0x0CE6   // DualSense
                or 0x0DF2                // DualSense Edge
                or 0x05C4                // DS4 v1
                or 0x09CC                // DS4 v1 alt
                or 0x0BA0;               // DS4 v2
        }

        /// <summary>True when the device both has a touchpad and has a
        /// haptic lane PadForge drives for swipe ticks (Steam Controller
        /// family via HapticToneService, or a dispatcher-driven Sony
        /// pad). Gates the Swipe Haptics card on the Touchpad tab.</summary>
        public static bool DeviceHasSwipePulse(Engine.Data.UserDevice ud)
        {
            if (ud == null || !ud.HasTouchpad) return false;
            return HapticToneService.DeviceHasHaptics(ud) || IsSonyRumblePad(ud);
        }

        /// <summary>Raises the pulse level for (slot, device). Called
        /// from the polling thread on a swipe tick.</summary>
        public static void Pulse(int slot, Guid device, float amp)
            => Pulse(slot, device, amp, Environment.TickCount64);

        internal static void Pulse(int slot, Guid device, float amp, long nowMs)
        {
            if (amp <= 0f) return;
            if (amp > 1f) amp = 1f;
            var cell = _cells.GetOrAdd((slot, device), _ => new Cell());
            // Max-combine while a burst is live; a fresh burst replaces a
            // dead one outright.
            if (nowMs < cell.UntilMs && cell.Amp > amp) amp = cell.Amp;
            cell.Amp = amp;
            cell.UntilMs = nowMs + PulseDurationMs;
        }

        /// <summary>Current pulse level 0..1 for (slot, device), read by
        /// the dispatcher's per-device rumble provider. 0 once the burst
        /// expires.</summary>
        public static float CurrentLevel(int slot, Guid device)
            => CurrentLevel(slot, device, Environment.TickCount64);

        internal static float CurrentLevel(int slot, Guid device, long nowMs)
        {
            if (_cells.IsEmpty) return 0f;
            if (!_cells.TryGetValue((slot, device), out var cell)) return 0f;
            return nowMs < cell.UntilMs ? cell.Amp : 0f;
        }

        /// <summary>True while any device on the slot has a live pulse.
        /// Keepalive input for Step 2's dispatcher poke: without it the
        /// dispatcher's 33 ms timer parks on an otherwise idle slot and
        /// the pulse never reaches the motors (memory:
        /// sony-dispatcher-timer-keepalive-sources.md).</summary>
        public static bool IsSlotActive(int slot)
            => IsSlotActive(slot, Environment.TickCount64);

        internal static bool IsSlotActive(int slot, long nowMs)
        {
            if (_cells.IsEmpty) return false;
            foreach (var kv in _cells)
            {
                if (kv.Key.Slot != slot) continue;
                if (nowMs < kv.Value.UntilMs) return true;
            }
            return false;
        }

        /// <summary>Mixes a pulse level into the scaled motor pair via
        /// max(), the audio-bass idiom (<c>ScaleRumbleForDevice</c>'s
        /// "if (audioL &gt; baseL)" merge). The DS4/DS5 touchpad sits
        /// center, so the tick drives both motors.</summary>
        public static void MixIntoMotors(ref ushort scaledLeft, ref ushort scaledRight, float level)
        {
            if (level <= 0f) return;
            if (level > 1f) level = 1f;
            ushort pv = (ushort)(level * 65535f);
            if (pv > scaledLeft) scaledLeft = pv;
            if (pv > scaledRight) scaledRight = pv;
        }

        /// <summary>Drops every live pulse. Called from
        /// <c>InputManager.ResetGestureContexts</c> (profile switch /
        /// engine stop) so a burst never outlives its source.</summary>
        public static void Clear() => _cells.Clear();
    }
}
