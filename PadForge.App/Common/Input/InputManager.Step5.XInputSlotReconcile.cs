using System;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 5 (packet-count based xinputhid slot reconciliation)
        //
        //  Purpose: detect when xinputhid has reshuffled our virtual to a
        //  different XInput slot so we can move the hook mask to match.
        //
        //  Signal: dwPacketNumber on an XInput slot increments whenever the
        //  kernel-side device state changes. For our virtual, HIDMaestro's
        //  SubmitState pipeline drives continuous changes at the polling
        //  rate (~1 kHz); the counter grows roughly 1 per poll. For the
        //  real Xbox controller, the counter only grows when the user
        //  actually changes input.
        //
        //  Observation window: 250 ms. Over that span:
        //   - The virtual's slot accumulates ~250 packet-counter ticks
        //     (user-independent, driven by our poll rate).
        //   - The real's slot accumulates 0..N ticks depending on what
        //     the user is pressing. Rarely as high as our submission rate.
        //
        //  The slot with the largest growth in that window is the virtual.
        //  If it does not match `_hiddenXInputSlot[padIndex]`, xinputhid
        //  reshuffled; move the hook mask and force SDL to re-enumerate.
        //
        //  Fallback: if no slot grows substantially (e.g. virtual idle
        //  because PadForge just started or paused, real idle because user
        //  is not playing), do nothing. Reshuffles only matter when input
        //  is flowing, at which point the signal is strong.
        //
        //  This replaces an earlier PnP-based approach that parsed IG_XX
        //  from HIDMaestro's root enumerator name. That name is a static
        //  `IG_00` marker in every HIDMaestro virtual regardless of the
        //  actual xinputhid slot, so PnP-based detection produced the
        //  correct answer only when the virtual happened to land on slot 0.
        // ─────────────────────────────────────────────

        private const int MsSlotSampleIntervalCycles = 250;
        /// <summary>Counter for the monitoring cadence. Incremented each
        /// `UpdateVirtualDevices` call; every `MsSlotSampleIntervalCycles`
        /// we sample packet counters and reconcile.</summary>
        private int _msSlotMonitorCycles;

        /// <summary>Last packet counter observed per XInput slot, from the
        /// previous sample. `uint.MaxValue` marks "no prior sample" (e.g.
        /// empty slot, failed read).</summary>
        private readonly uint[] _lastPacketCount = { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue };

        /// <summary>
        /// Threshold (packet-counter ticks over the sample window) above
        /// which a slot is considered "virtual-driven." Tuned for 250 ms
        /// at 1 kHz SubmitState: expect ~250 ticks on the virtual. Real
        /// slot rarely exceeds 50 ticks over 250 ms even under vigorous
        /// stick movement.
        /// </summary>
        private const uint MsVirtualSlotMinDelta = 120;

        /// <summary>
        /// Run packet-count slot reconcile if it is time. Call once per
        /// `UpdateVirtualDevices`. Returns true if any mask changed.
        /// </summary>
        private bool ReconcileMicrosoftVirtualSlots()
        {
            if (++_msSlotMonitorCycles < MsSlotSampleIntervalCycles) return false;
            _msSlotMonitorCycles = 0;

            if (!XInputHook.IsInstalled) return false;

            // Skip the reconcile entirely if no Microsoft virtual is active.
            // Nothing to reshuffle-track.
            int anyMsPad = -1;
            for (int i = 0; i < MaxPads; i++)
            {
                if (_virtualControllers[i] is HMaestroVirtualController hm
                    && hm.Type == VirtualControllerType.Microsoft)
                { anyMsPad = i; break; }
            }
            if (anyMsPad < 0)
            {
                for (int s = 0; s < 4; s++) _lastPacketCount[s] = uint.MaxValue;
                return false;
            }

            // Sample current packet counters on all 4 XInput slots.
            // GetStateOriginal bypasses our own hook so we see ground truth.
            var current = new uint[4];
            var present = new bool[4];
            for (int s = 0; s < 4; s++)
            {
                if (XInputHook.GetStateOriginal(s, out var st) == 0)
                {
                    current[s] = st.dwPacketNumber;
                    present[s] = true;
                }
                else
                {
                    current[s] = uint.MaxValue;
                    present[s] = false;
                }
            }

            // Compute deltas against the last sample. Slots that were empty
            // last time or empty now are excluded from the "virtual-driven"
            // candidate pool.
            int virtualSlot = -1;
            long bestDelta = 0;
            for (int s = 0; s < 4; s++)
            {
                if (!present[s]) continue;
                if (_lastPacketCount[s] == uint.MaxValue) continue;

                uint now = current[s];
                uint prev = _lastPacketCount[s];
                long delta = (long)(now - prev);
                if (delta < 0) delta += uint.MaxValue; // wrap-around guard
                if (delta > bestDelta && delta >= MsVirtualSlotMinDelta)
                {
                    bestDelta = delta;
                    virtualSlot = s;
                }
            }

            // Persist current readings as the next sample's baseline.
            for (int s = 0; s < 4; s++) _lastPacketCount[s] = current[s];

            if (virtualSlot < 0) return false;

            // Apply the detected slot to the Microsoft pad that currently
            // has its mask set (or the first Microsoft pad if none). For
            // multi-virtual setups this falls back to the first Microsoft
            // pad, which is imperfect but matches existing behavior in
            // other areas of this class.
            int targetPad = anyMsPad;

            int recordedSlot = _hiddenXInputSlot[targetPad];
            if (recordedSlot == virtualSlot) return false;

            // Avoid stealing a slot that a sibling Microsoft pad owns.
            for (int p = 0; p < MaxPads; p++)
            {
                if (p != targetPad && _hiddenXInputSlot[p] == virtualSlot) return false;
            }

            int mask = XInputHook.IgnoreSlotMask;
            if (recordedSlot >= 0) mask &= ~(1 << recordedSlot);
            mask |= (1 << virtualSlot);
            XInputHook.SetIgnoreSlotMask(mask);
            _hiddenXInputSlot[targetPad] = virtualSlot;
            _sdlJoysticksNeedReopen = true;

            XInputHook.Log(
                $"Slot reconcile (pkt-count): pad{targetPad} mask slot " +
                $"{recordedSlot} -> {virtualSlot} (delta={bestDelta}), " +
                $"mask=0x{mask:X}");

            return true;
        }
    }
}
