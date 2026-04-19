using System;
using System.Collections.Generic;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 5 (packet-count based xinputhid slot reconciliation)
        //
        //  Purpose: detect when xinputhid has reshuffled a Microsoft virtual
        //  to a different XInput slot so we can move the hook mask to match.
        //  Multi-pad aware: handles any number of active Microsoft virtuals
        //  (1..MaxPads) sharing the 4-slot XInput pool with real Xbox
        //  controllers.
        //
        //  Signal: dwPacketNumber on an XInput slot increments whenever the
        //  kernel-side device state changes. HIDMaestro's SubmitState
        //  pipeline drives continuous changes at the polling rate (~1 kHz);
        //  the counter grows roughly 1 per poll on every active virtual's
        //  slot. A real Xbox controller only bumps its counter when the
        //  user actually changes input, rarely above 50 ticks per 250 ms.
        //
        //  Observation window: 250 ms. Over that span:
        //   - Each virtual's slot accumulates ~250 packet-counter ticks
        //     (user-independent, driven by our SubmitState cadence).
        //   - A real's slot accumulates 0..N ticks depending on user input.
        //   - Empty slots accumulate nothing.
        //
        //  Multi-pad selection: N = number of active Microsoft virtuals.
        //  Pick the top N slots by growth (above MsVirtualSlotMinDelta) as
        //  the virtual set. Map pads to slots with preference for
        //  preserving existing assignments, so xinputhid reshuffling one
        //  pad's virtual doesn't disrupt another pad that stayed in place.
        //
        //  Fallback: if fewer than N slots exceed the threshold (e.g. a
        //  virtual is freshly created and has not yet received SubmitState
        //  in Pass 3), only the detectable ones are reconciled. Pads whose
        //  virtuals are not yet detectable keep their current (possibly
        //  -1) assignment and are revisited next cycle.
        //
        //  Earlier approach (single-pad, "lowest pkt only") was replaced
        //  because multi-Microsoft-virtual setups produced mask drift on
        //  the non-first pad that the single-pad reconcile never corrected.
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
        /// at 1 kHz SubmitState: expect ~250 ticks on each virtual. Real
        /// slots rarely exceed 50 ticks over 250 ms even under vigorous
        /// stick movement.
        /// </summary>
        private const long MsVirtualSlotMinDelta = 120;

        /// <summary>
        /// Run packet-count slot reconcile if it is time. Call once per
        /// `UpdateVirtualDevices`. Returns true if any mask changed.
        /// </summary>
        private bool ReconcileMicrosoftVirtualSlots()
        {
            if (++_msSlotMonitorCycles < MsSlotSampleIntervalCycles) return false;
            _msSlotMonitorCycles = 0;

            if (!XInputHook.IsInstalled) return false;

            // Collect all active Microsoft pads.
            var msPads = new List<int>(MaxPads);
            for (int i = 0; i < MaxPads; i++)
            {
                if (_virtualControllers[i] is HMaestroVirtualController hm
                    && hm.Type == VirtualControllerType.Microsoft)
                {
                    msPads.Add(i);
                }
            }
            if (msPads.Count == 0)
            {
                // No Microsoft virtuals. Reset baseline so the next virtual
                // we spin up gets a clean growth measurement.
                for (int s = 0; s < 4; s++) _lastPacketCount[s] = uint.MaxValue;
                return false;
            }

            // Sample current packet counters on all 4 XInput slots.
            // GetStateOriginal bypasses our hook so we see ground truth.
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

            // Compute per-slot growth since last sample. -1 marks "no data"
            // (first sample, slot empty now, or slot was empty last time).
            var growth = new long[4];
            for (int s = 0; s < 4; s++)
            {
                if (!present[s] || _lastPacketCount[s] == uint.MaxValue)
                {
                    growth[s] = -1;
                    continue;
                }
                long d = (long)current[s] - (long)_lastPacketCount[s];
                if (d < 0) d += uint.MaxValue; // wrap-around guard
                growth[s] = d;
            }

            // Persist current readings as next sample's baseline.
            for (int s = 0; s < 4; s++) _lastPacketCount[s] = current[s];

            // Identify candidate virtual slots: those whose packet counter
            // grew by at least MsVirtualSlotMinDelta in the last window.
            // An idle virtual (pad's assigned physical is not being touched,
            // its mapped keyboards aren't being typed) won't make the cut,
            // and that's fine. We only use this set to assign *new* pads.
            var slotRanking = new[] { 0, 1, 2, 3 };
            Array.Sort(slotRanking, (a, b) => growth[b].CompareTo(growth[a]));

            var highGrowthSlots = new List<int>(4);
            foreach (int s in slotRanking)
            {
                if (growth[s] >= MsVirtualSlotMinDelta) highGrowthSlots.Add(s);
            }

            // Build new pad -> slot assignment. Sticky where possible:
            // existing assignments are only cleared if the slot went empty
            // (virtual definitely gone from there). An idle virtual
            // produces low growth that is indistinguishable from an idle
            // real via this signal alone; if we cleared on low growth we
            // would unmask idle virtuals and leak them into the Devices
            // list. So the rule is conservative: present+assigned is
            // trusted, and only unassigned pads pull from the high-growth
            // candidate pool.
            var newAssignments = new int[MaxPads];
            for (int i = 0; i < MaxPads; i++) newAssignments[i] = _hiddenXInputSlot[i];

            var claimedSlots = new HashSet<int>();

            // Pass 1: sticky. Keep every assignment whose slot still has a
            // device connected. Drop only if the slot is empty now.
            foreach (int padIdx in msPads)
            {
                int cur = _hiddenXInputSlot[padIdx];
                if (cur < 0 || cur >= 4) continue;
                if (present[cur])
                {
                    claimedSlots.Add(cur);
                }
                else
                {
                    newAssignments[padIdx] = -1;
                }
            }

            // Pass 2: assign high-growth unclaimed slots to pads without
            // an assignment (newly-created pads, or pads whose prior slot
            // went empty). Pads that had a high-growth slot stolen by
            // xinputhid reshuffle fall back to this path once Pass 1
            // clears them on the "slot went empty" rule.
            foreach (int padIdx in msPads)
            {
                if (newAssignments[padIdx] >= 0) continue;
                int pick = -1;
                foreach (int s in highGrowthSlots)
                {
                    if (!claimedSlots.Contains(s))
                    {
                        pick = s;
                        break;
                    }
                }
                if (pick >= 0)
                {
                    newAssignments[padIdx] = pick;
                    claimedSlots.Add(pick);
                }
                // else: no high-growth unclaimed slot. Pad stays at -1
                // until either the create-time single-shot detection
                // populates it, or a later reconcile window sees the
                // virtual's slot come alive.
            }

            // Apply. If the slot set for the hook changed, rebuild the
            // mask and force SDL re-enumeration.
            bool changed = false;
            var log = new System.Text.StringBuilder();
            for (int i = 0; i < MaxPads; i++)
            {
                if (newAssignments[i] != _hiddenXInputSlot[i])
                {
                    log.Append($"pad{i} slot {_hiddenXInputSlot[i]}->{newAssignments[i]}; ");
                    _hiddenXInputSlot[i] = newAssignments[i];
                    changed = true;
                }
            }

            if (changed)
            {
                int mask = 0;
                for (int i = 0; i < MaxPads; i++)
                {
                    if (_hiddenXInputSlot[i] >= 0) mask |= (1 << _hiddenXInputSlot[i]);
                }
                XInputHook.SetIgnoreSlotMask(mask);
                _sdlJoysticksNeedReopen = true;

                XInputHook.Log(
                    $"Multi-pad reconcile: {log} " +
                    $"growth=[{growth[0]},{growth[1]},{growth[2]},{growth[3]}] " +
                    $"mask=0x{mask:X}");
            }

            return changed;
        }
    }
}
