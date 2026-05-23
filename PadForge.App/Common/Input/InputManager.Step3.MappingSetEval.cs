using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Per-slot runtime state for stateful source kinds
        //  (Incremental accumulator; InvertOnHold is stateless).
        //  Cleared on profile switch and on engine stop.
        // ─────────────────────────────────────────────
        private static readonly SourceKindRuntime[] _slotSourceKindRuntime = InitRuntime();
        private static SourceKindRuntime[] InitRuntime()
        {
            var arr = new SourceKindRuntime[MaxPads];
            for (int i = 0; i < MaxPads; i++) arr[i] = new SourceKindRuntime();
            return arr;
        }

        /// <summary>Drops Incremental accumulator state on every slot.
        /// Called by InputService on profile switch and engine stop so
        /// cruise-control / ramp throttle always starts neutral.</summary>
        public static void ClearSourceKindRuntime()
        {
            for (int i = 0; i < _slotSourceKindRuntime.Length; i++)
                _slotSourceKindRuntime[i]?.Clear();
        }

        // Frame delta tracked per slot. Set by ApplyMappingSetToGamepad
        // each frame from the engine's polling-loop timestamp.
        private static readonly double[] _lastEvalTime = new double[MaxPads];

        // ─────────────────────────────────────────────
        //  Multi-source cross-device evaluation tracking
        //
        //  Every multi-source row (regardless of CombineMode) must
        //  evaluate row.Sources once per frame, cross-device, so the
        //  user's chosen combine actually operates on the full
        //  contributions list. The per-device-pass model used by
        //  single-source rows filters by current device, which makes
        //  Sum / Average / AND / XOR / Custom degrade to either OR or
        //  MaxAbs depending on Step 4's recombine when sources span
        //  multiple devices (each pass sees a one-element list).
        //
        //  _multiSourceEvaluatedTargetsBySlot tracks which row targets
        //  have already been evaluated this frame for each slot, so
        //  the second, third … device pass for the slot skips the row
        //  entirely instead of zero-overwriting it.
        //  BeginFrameMultiSourceTracking() at the top of
        //  UpdateOutputStates clears every slot's set.
        // ─────────────────────────────────────────────
        private static readonly HashSet<string>[] _multiSourceEvaluatedTargetsBySlot = InitMultiSourceTracking();
        private static HashSet<string>[] InitMultiSourceTracking()
        {
            var arr = new HashSet<string>[MaxPads];
            for (int i = 0; i < MaxPads; i++) arr[i] = new HashSet<string>(System.StringComparer.Ordinal);
            return arr;
        }

        /// <summary>Called once per polling frame at the top of
        /// <see cref="UpdateOutputStates"/>. Resets the per-slot
        /// multi-source tracking so the new frame's first device pass
        /// triggers fresh cross-device evaluation, and stamps the
        /// per-slot frame dt so every per-target evaluator the cycle
        /// runs (ApplyMappingSetToGamepad, TryEvaluateMappingSet* for
        /// Extended/MIDI/KBM/Touchpad targets) reads the SAME dt.
        /// Without this, the second caller in a cycle was seeing a
        /// near-zero microsecond dt and the Incremental accumulator
        /// crawled at ~1000x slower than configured rate.</summary>
        private static void BeginFrameMultiSourceTracking()
        {
            for (int i = 0; i < _multiSourceEvaluatedTargetsBySlot.Length; i++)
                _multiSourceEvaluatedTargetsBySlot[i].Clear();
            StampFrameDelta();
        }

        // One frame dt captured at frame start, valid for every evaluator
        // call in the same UpdateOutputStates pass. Per-slot to preserve
        // the "first frame on this slot returns 0" guard.
        private static readonly double[] _currentFrameDelta = new double[MaxPads];

        private static void StampFrameDelta()
        {
            double now = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
            for (int slot = 0; slot < _lastEvalTime.Length; slot++)
            {
                double last = _lastEvalTime[slot];
                _lastEvalTime[slot] = now;
                if (last <= 0)
                {
                    _currentFrameDelta[slot] = 0; // first frame on this slot
                    continue;
                }
                double dt = now - last;
                if (dt > 0.25) dt = 0.25; // resume-from-sleep guard
                _currentFrameDelta[slot] = dt;
            }
        }

        // Reads the frame dt captured by BeginFrameMultiSourceTracking.
        // Idempotent — does NOT advance _lastEvalTime, so every evaluator
        // in the same cycle reads the same value regardless of call order.
        private static double ComputeAndAdvanceDelta(int slot)
        {
            if (slot < 0 || slot >= _currentFrameDelta.Length) return 0;
            return _currentFrameDelta[slot];
        }

        // ─────────────────────────────────────────────
        //  Shift layer activator state (v1 — multi-activator)
        //
        //  Each slot carries a ShiftRuntime instance that tracks every
        //  activator's was-down latch and toggle-engaged flag, plus an
        //  ordered engagement stack for last-engaged-wins resolution.
        //  Per-slot, per-activator. Cleared on profile switch and on
        //  slot-index compaction.
        // ─────────────────────────────────────────────

        /// <summary>Per-slot runtime state for shift activators. One instance
        /// per slot; resized when the slot's <see cref="MappingSet.ShiftActivators"/>
        /// list grows or shrinks. The <see cref="Stack"/> records the order
        /// activators engaged in; the most recently engaged sits at the tail
        /// (last-engaged-wins).</summary>
        private sealed class StickyBaseline
        {
            public bool[] Buttons;
            public int[]  Axis;
            public int[]  Sliders;
            public int[]  Povs;
            /// <summary>Per-pad-per-slot finger-down snapshot, flattened
            /// across every touchpad surface on the device. Layout:
            /// <c>[pad0.f0, pad0.f1, ..., pad1.f0, pad1.f1, ...]</c>.
            /// Used only for cross-pad rising-edge detection in
            /// <see cref="ComputeStickyConsumerHeld"/>; order is stable
            /// across captures within a single device session because
            /// <see cref="CustomInputState.Touchpads"/> shape is fixed
            /// at device-open time.</summary>
            public bool[] TouchpadFingerDown;
        }

        /// <summary>Per-activator snapshot of every slot-assigned device's
        /// state at the moment Sticky engaged. Cross-device consumer
        /// detection walks this dictionary on every poll so a button
        /// pressed on Device B disengages a Sticky activator anchored to
        /// Device A.</summary>
        private sealed class StickyEngagementSnapshot
        {
            public readonly Dictionary<string, StickyBaseline> ByDevice =
                new Dictionary<string, StickyBaseline>(System.StringComparer.OrdinalIgnoreCase);
        }

        // Threshold for axis/slider deviation from baseline (raw 0..65535
        // unsigned). 8192 ≈ 12.5% of full range — pulled-trigger
        // territory or a clearly-deliberate stick push, not idle drift.
        private const int StickyAxisDeltaThreshold = 8192;

        /// <summary>Walks every UserSetting assigned to the given slot
        /// and snapshots that device's input state into the returned
        /// engagement snapshot. Used at Sticky-engage time so the
        /// subsequent per-frame scan can spot consumer activity on ANY
        /// of the slot's devices, not just the activator's own.
        ///
        /// <para>Lock ordering: take a snapshot of the slot's device
        /// GUIDs under <c>UserSettings.SyncRoot</c>, release that lock,
        /// then call <see cref="LookupDeviceState"/> (which itself
        /// acquires <c>UserDevices.SyncRoot</c>) for each guid. Never
        /// hold both locks at once — the rest of the codebase nests
        /// <c>UserDevices → UserSettings</c> and inverting that order
        /// here would deadlock against other code paths.</para></summary>
        private static StickyEngagementSnapshot CaptureStickyEngagementSnapshot(int slotIndex)
        {
            var snap = new StickyEngagementSnapshot();
            var settings = SettingsManager.UserSettings;
            if (settings == null) return snap;

            // Step 1: gather slot-assigned device GUIDs under the
            // UserSettings lock only.
            var guids = new List<string>();
            lock (settings.SyncRoot)
            {
                for (int i = 0; i < settings.Items.Count; i++)
                {
                    var us = settings.Items[i];
                    if (us == null || us.MapTo != slotIndex) continue;
                    var guidStr = us.InstanceGuid.ToString();
                    if (!snap.ByDevice.ContainsKey(guidStr))
                    {
                        // Reserve the slot now so duplicates are dropped
                        // without another contains check below.
                        snap.ByDevice[guidStr] = null;
                        guids.Add(guidStr);
                    }
                }
            }

            // Step 2: outside the UserSettings lock, look up each device
            // state (LookupDeviceState takes UserDevices.SyncRoot
            // internally). Drop entries whose device is offline.
            for (int i = 0; i < guids.Count; i++)
            {
                var guidStr = guids[i];
                var devState = LookupDeviceState(guidStr);
                if (devState == null)
                {
                    snap.ByDevice.Remove(guidStr);
                    continue;
                }
                snap.ByDevice[guidStr] = CaptureStickyBaseline(devState);
            }
            return snap;
        }

        /// <summary>True when any device in the engagement snapshot has
        /// consumer activity not present in its baseline. Iterates every
        /// snapshotted device and OR's the per-device check.</summary>
        private static bool ComputeStickyConsumerHeldAcrossSlot(StickyEngagementSnapshot snap)
        {
            if (snap == null) return false;
            foreach (var kv in snap.ByDevice)
            {
                var current = LookupDeviceState(kv.Key);
                if (current == null) continue;
                if (ComputeStickyConsumerHeld(kv.Value, current)) return true;
            }
            return false;
        }

        /// <summary>Snapshots every consumer-input channel on the device
        /// state passed in so a later <see cref="ComputeStickyConsumerHeld"/>
        /// call can spot any "new" activity. Returns null when state is
        /// null.</summary>
        private static StickyBaseline CaptureStickyBaseline(CustomInputState state)
        {
            if (state == null) return null;
            var b = new StickyBaseline();
            if (state.Buttons != null)
            {
                b.Buttons = new bool[state.Buttons.Length];
                System.Array.Copy(state.Buttons, b.Buttons, state.Buttons.Length);
            }
            if (state.Axis != null)
            {
                b.Axis = new int[state.Axis.Length];
                System.Array.Copy(state.Axis, b.Axis, state.Axis.Length);
            }
            if (state.Sliders != null)
            {
                b.Sliders = new int[state.Sliders.Length];
                System.Array.Copy(state.Sliders, b.Sliders, state.Sliders.Length);
            }
            if (state.Povs != null)
            {
                b.Povs = new int[state.Povs.Length];
                System.Array.Copy(state.Povs, b.Povs, state.Povs.Length);
            }
            if (state.Touchpads != null)
            {
                int total = 0;
                for (int p = 0; p < state.Touchpads.Length; p++)
                    total += state.Touchpads[p]?.MaxFingers ?? 0;
                if (total > 0)
                {
                    b.TouchpadFingerDown = new bool[total];
                    int o = 0;
                    for (int p = 0; p < state.Touchpads.Length; p++)
                    {
                        var pad = state.Touchpads[p];
                        if (pad == null) continue;
                        for (int f = 0; f < pad.MaxFingers; f++)
                            b.TouchpadFingerDown[o++] = pad.FingerDown[f];
                    }
                }
            }
            // TouchpadClick lives in Buttons[16] now and is already captured
            // by the Buttons[] copy above.
            return b;
        }

        /// <summary>True when any consumer-input channel has activity not
        /// present in the baseline: a newly-pressed button, an axis/slider
        /// that moved past the deviation threshold, a POV direction that
        /// transitioned away from centered (or to a different direction),
        /// a touchpad-finger contact rising edge, or a touchpad-click
        /// rising edge. Gyro/accel are deliberately ignored — idle hand
        /// movement would constantly release Sticky.</summary>
        private static bool ComputeStickyConsumerHeld(StickyBaseline baseline, CustomInputState current)
        {
            if (baseline == null || current == null) return false;

            if (baseline.Buttons != null && current.Buttons != null)
            {
                int n = System.Math.Min(baseline.Buttons.Length, current.Buttons.Length);
                for (int k = 0; k < n; k++)
                    if (current.Buttons[k] && !baseline.Buttons[k]) return true;
            }
            if (baseline.Axis != null && current.Axis != null)
            {
                int n = System.Math.Min(baseline.Axis.Length, current.Axis.Length);
                for (int k = 0; k < n; k++)
                    if (System.Math.Abs(current.Axis[k] - baseline.Axis[k]) > StickyAxisDeltaThreshold)
                        return true;
            }
            if (baseline.Sliders != null && current.Sliders != null)
            {
                int n = System.Math.Min(baseline.Sliders.Length, current.Sliders.Length);
                for (int k = 0; k < n; k++)
                    if (System.Math.Abs(current.Sliders[k] - baseline.Sliders[k]) > StickyAxisDeltaThreshold)
                        return true;
            }
            if (baseline.Povs != null && current.Povs != null)
            {
                int n = System.Math.Min(baseline.Povs.Length, current.Povs.Length);
                for (int k = 0; k < n; k++)
                    if (current.Povs[k] != -1 && current.Povs[k] != baseline.Povs[k])
                        return true;
            }
            if (baseline.TouchpadFingerDown != null && current.Touchpads != null)
            {
                // Walk the flattened per-pad-per-slot snapshot in the same
                // order CaptureBaseline laid it down. Rising edge on any
                // slot = consumer-input activity.
                int o = 0;
                for (int p = 0; p < current.Touchpads.Length; p++)
                {
                    var pad = current.Touchpads[p];
                    if (pad == null) continue;
                    for (int f = 0; f < pad.MaxFingers; f++)
                    {
                        if (o >= baseline.TouchpadFingerDown.Length) break;
                        if (pad.FingerDown[f] && !baseline.TouchpadFingerDown[o]) return true;
                        o++;
                    }
                }
            }
            // Touchpad-click rising edge is covered by the Buttons[] loop
            // above (Buttons[16] = SDL_GAMEPAD_BUTTON_TOUCHPAD).
            return false;
        }

        private sealed class ShiftRuntime
        {
            public bool[] WasDown = System.Array.Empty<bool>();
            public bool[] ToggleOn = System.Array.Empty<bool>();
            public int[]  CycleIndex   = System.Array.Empty<int>();      // v3 Cycle mode position
            public long[] EngageStartTicks = System.Array.Empty<long>(); // v2 Delay debounce
            // Cycle mode caches the split CycleLayers string per-activator
            // so the polling-thread tick doesn't reallocate every frame.
            // Recomputed when the source string changes.
            public string[][] CycleLayersSplit = System.Array.Empty<string[]>();
            public string[] CycleLayersSource = System.Array.Empty<string>();
            // v3 Sticky mode: per-activator engaged flag, falling-edge
            // tracking latch, and a per-slot engagement snapshot of every
            // assigned device's state at engage time. Cross-device aware:
            // consumer detection walks all snapshotted devices' current
            // states so a Sticky activator on a keyboard releases when
            // the user moves a stick on a gamepad on the same slot.
            public bool[] StickyEngaged = System.Array.Empty<bool>();
            // Tracks whether at least one non-baseline consumer input was
            // active last frame. Sticky releases on the falling edge —
            // i.e. when this was true and is now false — so the layer
            // stays engaged for the full duration the consumer is held.
            public bool[] StickyConsumerActive = System.Array.Empty<bool>();
            // Per-activator engagement snapshot covering EVERY device
            // assigned to the slot at the moment Sticky engaged. Consumer
            // detection walks each snapshotted device's current state
            // against its baseline so a Sticky activator on Device A
            // still releases when the user presses something on Device B.
            // Gyro/accel are intentionally excluded (idle hand movement
            // would constantly release the layer).
            public StickyEngagementSnapshot[] StickyBaselines = System.Array.Empty<StickyEngagementSnapshot>();
            public readonly List<int> Stack = new();
            public string CustomLayer = "";   // v2 Custom mode current layer (overrides stack when non-empty)

            /// <summary>Sync lock guarding <see cref="Stack"/> and
            /// <see cref="CustomLayer"/> against cross-thread reads (UI
            /// thread <see cref="GetEngagedLayerMask"/> + Clear) versus
            /// polling-thread writes (<see cref="UpdateActivatorState"/>
            /// → <see cref="UpdateStack"/>). Per-instance so different
            /// slots don't contend.</summary>
            public readonly object SyncRoot = new();

            public void EnsureSize(int count)
            {
                if (WasDown.Length >= count) return;
                int newSize = count;
                WasDown = ResizeBool(WasDown, newSize);
                ToggleOn = ResizeBool(ToggleOn, newSize);
                CycleIndex = ResizeInt(CycleIndex, newSize);
                EngageStartTicks = ResizeLong(EngageStartTicks, newSize);
                CycleLayersSplit = ResizeStringArrays(CycleLayersSplit, newSize);
                CycleLayersSource = ResizeStringArr(CycleLayersSource, newSize);
                StickyEngaged = ResizeBool(StickyEngaged, newSize);
                StickyConsumerActive = ResizeBool(StickyConsumerActive, newSize);
                StickyBaselines = ResizeStickyBaselines(StickyBaselines, newSize);
            }

            public void Clear()
            {
                System.Array.Clear(WasDown, 0, WasDown.Length);
                System.Array.Clear(ToggleOn, 0, ToggleOn.Length);
                System.Array.Clear(CycleIndex, 0, CycleIndex.Length);
                System.Array.Clear(EngageStartTicks, 0, EngageStartTicks.Length);
                System.Array.Clear(CycleLayersSplit, 0, CycleLayersSplit.Length);
                System.Array.Clear(CycleLayersSource, 0, CycleLayersSource.Length);
                System.Array.Clear(StickyEngaged, 0, StickyEngaged.Length);
                System.Array.Clear(StickyConsumerActive, 0, StickyConsumerActive.Length);
                System.Array.Clear(StickyBaselines, 0, StickyBaselines.Length);
                lock (SyncRoot)
                {
                    Stack.Clear();
                    CustomLayer = "";
                }
            }

            private static StickyEngagementSnapshot[] ResizeStickyBaselines(StickyEngagementSnapshot[] arr, int n)
            { var r = new StickyEngagementSnapshot[n]; System.Array.Copy(arr, r, System.Math.Min(arr.Length, n)); return r; }

            private static string[][] ResizeStringArrays(string[][] arr, int n)
            { var r = new string[n][]; System.Array.Copy(arr, r, System.Math.Min(arr.Length, n)); return r; }
            private static string[] ResizeStringArr(string[] arr, int n)
            { var r = new string[n]; System.Array.Copy(arr, r, System.Math.Min(arr.Length, n)); return r; }

            private static bool[] ResizeBool(bool[] arr, int n)
            { var r = new bool[n]; System.Array.Copy(arr, r, System.Math.Min(arr.Length, n)); return r; }
            private static int[]  ResizeInt(int[] arr, int n)
            { var r = new int[n];  System.Array.Copy(arr, r, System.Math.Min(arr.Length, n)); return r; }
            private static long[] ResizeLong(long[] arr, int n)
            { var r = new long[n]; System.Array.Copy(arr, r, System.Math.Min(arr.Length, n)); return r; }
        }

        private static readonly ShiftRuntime[] _shiftRuntime = new ShiftRuntime[MaxPads];

        // v2 Postpone-the-mapping suppression set. For each slot, a
        // HashSet of "deviceGuid|descriptor" keys identifying source
        // bindings that should be treated as zero/false during this
        // frame's row eval because their owning activator is currently
        // exerting itself and PostponeMapping=false on that activator.
        // Recomputed at the bottom of each ResolveActiveLayerMask call.
        // Read by IsSourceSuppressedPostpone from the row evaluator
        // loops in Step 3.
        private static readonly System.Collections.Generic.HashSet<string>[]
            _suppressedSourcesBySlot = new System.Collections.Generic.HashSet<string>[MaxPads];

        /// <summary>Returns true when the (slot, deviceGuid, descriptor)
        /// tuple matches a currently-engaging activator that has
        /// PostponeMapping=false. Row evaluators short-circuit such
        /// sources so the activator press doesn't ALSO fire that
        /// source's normal mapping — reWASD parity for the
        /// "Postpone the mapping" option (here surfaced as
        /// "Also fire activator's own mapping").</summary>
        internal static bool IsSourceSuppressedPostpone(int slotIndex, string deviceGuid, string descriptor)
        {
            if (slotIndex < 0 || slotIndex >= _suppressedSourcesBySlot.Length) return false;
            var set = _suppressedSourcesBySlot[slotIndex];
            if (set == null || set.Count == 0) return false;
            // Key shape mirrors the population loop below.
            string key = (deviceGuid ?? "") + "|" + (descriptor ?? "");
            return set.Contains(key);
        }

        /// <summary>Clears every slot's shift runtime state. Called from
        /// InputService.ApplyProfile and from CompactSlotsForGaps so a
        /// profile / topology transition starts every activator un-engaged.</summary>
        public static void ClearAllShiftRuntime()
        {
            for (int i = 0; i < _shiftRuntime.Length; i++)
                _shiftRuntime[i]?.Clear();
        }

        /// <summary>Clears one slot's shift runtime state. Use when a single
        /// slot changes shift-activator topology (e.g. activator added/
        /// removed/edited) and we want the new shape to start clean.</summary>
        public static void ClearShiftRuntime(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _shiftRuntime.Length) return;
            _shiftRuntime[slotIndex]?.Clear();
        }

        /// <summary>Inspect-only snapshot of the active engaged layer for a
        /// slot. Returns <c>"Base"</c> when nothing is engaged; otherwise
        /// the LayerMask of the activator at the top of the engagement
        /// stack (or the Custom-mode layer override when set). Used by
        /// the v3 visual overlay to display the live layer state without
        /// having to thread state across the polling-thread boundary.</summary>
        public static string GetEngagedLayerMask(int slotIndex, MappingSet mappingSet)
        {
            if (slotIndex < 0 || slotIndex >= _shiftRuntime.Length) return "Base";
            var rt = _shiftRuntime[slotIndex];
            if (rt == null) return "Base";

            // Snapshot under the runtime's lock so the polling thread's
            // concurrent Stack / CustomLayer mutations can't trip the
            // indexer with a stale Count.
            string customLayer;
            int idx;
            lock (rt.SyncRoot)
            {
                customLayer = rt.CustomLayer;
                idx = rt.Stack.Count > 0 ? rt.Stack[rt.Stack.Count - 1] : -1;
            }

            if (!string.IsNullOrEmpty(customLayer)) return customLayer;
            var activators = mappingSet?.ShiftActivators;
            if (activators == null) return "Base";
            if (idx < 0 || idx >= activators.Count) return "Base";
            return activators[idx]?.LayerMask ?? "Base";
        }

        /// <summary>Resolves the active shift-layer mask for a slot.
        /// Walks <see cref="MappingSet.ShiftActivators"/>, updates engaged
        /// state for activators owned by <paramref name="thisDeviceGuid"/>,
        /// and returns the last-engaged layer's <see cref="ShiftActivator.LayerMask"/>.
        /// Returns <c>"Base"</c> when nothing is engaged.</summary>
        private static string ResolveActiveLayerMask(
            int slotIndex,
            MappingSet mappingSet,
            CustomInputState thisDeviceState,
            string thisDeviceGuid)
        {
            if (mappingSet == null) return "Base";
            var activators = mappingSet.ShiftActivators;
            if (activators == null || activators.Count == 0) return "Base";
            if (slotIndex < 0 || slotIndex >= MaxPads) return "Base";

            var rt = _shiftRuntime[slotIndex] ??= new ShiftRuntime();
            rt.EnsureSize(activators.Count);

            for (int i = 0; i < activators.Count; i++)
            {
                var act = activators[i];
                if (act == null) continue;

                // State updates ONLY happen on the activator's owning-device
                // pass. Other devices' passes still read the resolved
                // active mask below — that's how cross-device activation
                // gates this slot's sources on every device pass.
                if (!string.Equals(act.DeviceGuid ?? "", thisDeviceGuid ?? "", System.StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(act.DeviceGuid))
                    continue;

                UpdateActivatorState(rt, i, act, thisDeviceState, slotIndex);
            }

            // v2 Postpone-the-mapping: rebuild the per-slot suppression
            // set from the just-updated activator exertion state. An
            // activator is "exerting" when its input read (after Delay
            // gating) was true this frame, captured into WasDown[i] at
            // the tail of UpdateActivatorState. PostponeMapping=true on
            // an activator opts OUT of suppression — its own source row
            // fires alongside the layer change.
            var suppressed = _suppressedSourcesBySlot[slotIndex];
            if (suppressed == null)
                _suppressedSourcesBySlot[slotIndex] = suppressed =
                    new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            suppressed.Clear();
            for (int i = 0; i < activators.Count; i++)
            {
                var a = activators[i];
                if (a == null) continue;
                if (a.PostponeMapping) continue;
                if (!rt.WasDown[i]) continue;
                if (!string.IsNullOrEmpty(a.Descriptor))
                    suppressed.Add((a.DeviceGuid ?? "") + "|" + a.Descriptor);
                if (string.Equals(a.Kind, "Chord", System.StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(a.ChordSecondDescriptor))
                {
                    suppressed.Add((a.ChordSecondDeviceGuid ?? "") + "|" + a.ChordSecondDescriptor);
                }
            }

            // Snapshot the cross-thread fields under the runtime's lock,
            // then resolve outside the lock to keep contention short.
            string customLayer;
            int winnerIdx;
            lock (rt.SyncRoot)
            {
                customLayer = rt.CustomLayer;
                winnerIdx = rt.Stack.Count > 0 ? rt.Stack[rt.Stack.Count - 1] : -1;
            }

            // v2 Custom mode: explicit jump-to-layer overrides the stack.
            if (!string.IsNullOrEmpty(customLayer))
                return customLayer;

            // Last-engaged-wins: tail of stack.
            if (winnerIdx < 0 || winnerIdx >= activators.Count) return "Base";
            var winner = activators[winnerIdx];
            return string.IsNullOrEmpty(winner?.LayerMask) ? "Base" : winner.LayerMask;
        }

        /// <summary>Reads the input for a single activator, updates its
        /// per-mode latch state on <paramref name="rt"/>, and maintains the
        /// engagement stack. Supports Hold / Toggle / Custom / Cycle /
        /// Sticky modes plus the v2 Delay debounce + Chord/Axis kinds.</summary>
        private static void UpdateActivatorState(
            ShiftRuntime rt,
            int actIdx,
            ShiftActivator act,
            CustomInputState state,
            int slotIndex)
        {
            // ── Read the activator's current input ──
            bool inputDown = ReadActivatorInput(act, state);

            // ── v2 Delay before Jump: gate transitions until the input
            //    has been continuously down for DelayMs ──
            long nowTicks = System.DateTime.UtcNow.Ticks;
            if (inputDown && !rt.WasDown[actIdx])
                rt.EngageStartTicks[actIdx] = nowTicks;
            long heldMs = inputDown
                ? (nowTicks - rt.EngageStartTicks[actIdx]) / System.TimeSpan.TicksPerMillisecond
                : 0;
            bool delayMet = act.DelayMs <= 0 || !inputDown || heldMs >= act.DelayMs;

            string mode = act.Mode ?? "Hold";
            switch (mode)
            {
                case "Toggle":
                {
                    bool risingEdge = inputDown && !rt.WasDown[actIdx] && delayMet;
                    if (risingEdge)
                        rt.ToggleOn[actIdx] = !rt.ToggleOn[actIdx];
                    UpdateStack(rt, actIdx, rt.ToggleOn[actIdx]);
                    break;
                }
                case "Custom":
                {
                    // v2: press transitions to JumpToLayer; release does
                    // nothing (layer persists until another Custom activator
                    // fires or all Hold/Toggle activators in the stack lose
                    // engagement). Empty JumpToLayer means "back to Base."
                    bool risingEdge = inputDown && !rt.WasDown[actIdx] && delayMet;
                    if (risingEdge)
                    {
                        string newLayer = act.JumpToLayer ?? "";
                        lock (rt.SyncRoot) rt.CustomLayer = newLayer;
                    }
                    break;
                }
                case "Cycle":
                {
                    // v3: each press advances through CycleLayers. Wraps
                    // past the last entry back to Base (empty string).
                    // Split result is cached per-activator so the polling
                    // thread doesn't allocate every frame; recomputed when
                    // the activator's CycleLayers string changes.
                    bool risingEdge = inputDown && !rt.WasDown[actIdx] && delayMet;
                    if (risingEdge)
                    {
                        string src = act.CycleLayers ?? "";
                        if (!string.Equals(rt.CycleLayersSource[actIdx], src, System.StringComparison.Ordinal))
                        {
                            rt.CycleLayersSplit[actIdx] = src.Split('|', System.StringSplitOptions.RemoveEmptyEntries);
                            rt.CycleLayersSource[actIdx] = src;
                        }
                        var layers = rt.CycleLayersSplit[actIdx];
                        if (layers != null && layers.Length > 0)
                        {
                            rt.CycleIndex[actIdx] = (rt.CycleIndex[actIdx] + 1) % (layers.Length + 1);
                            string newLayer = rt.CycleIndex[actIdx] == 0 ? "" : layers[rt.CycleIndex[actIdx] - 1];
                            lock (rt.SyncRoot) rt.CustomLayer = newLayer;
                        }
                    }
                    break;
                }
                case "Sticky":
                {
                    // v3 Sticky: typewriter-shift behavior. Press the
                    // activator (no need to hold) → layer engages. Any
                    // consumer input on ANY device assigned to this slot
                    // — button, stick, trigger, slider, POV direction,
                    // touchpad contact, touchpad click — fires the
                    // shifted mapping AND keeps firing while the consumer
                    // input is held. Releasing the consumer (everything
                    // back to baseline) disengages the layer.
                    //
                    // Cross-device aware: at engage time we snapshot every
                    // slot-assigned device's state, so a Sticky activator
                    // on a keyboard releases when the user moves a stick
                    // on a gamepad assigned to the same slot.
                    if (rt.StickyEngaged[actIdx] && rt.StickyBaselines[actIdx] != null)
                    {
                        bool consumerHeld = ComputeStickyConsumerHeldAcrossSlot(rt.StickyBaselines[actIdx]);

                        if (rt.StickyConsumerActive[actIdx] && !consumerHeld)
                        {
                            // Falling edge — consumer released. Disengage now.
                            UpdateStack(rt, actIdx, false);
                            rt.StickyEngaged[actIdx] = false;
                            rt.StickyConsumerActive[actIdx] = false;
                            rt.StickyBaselines[actIdx] = null;
                        }
                        else
                        {
                            rt.StickyConsumerActive[actIdx] = consumerHeld;
                        }
                    }
                    else
                    {
                        bool risingEdge = inputDown && !rt.WasDown[actIdx] && delayMet;
                        if (risingEdge)
                        {
                            UpdateStack(rt, actIdx, true);
                            rt.StickyEngaged[actIdx] = true;
                            rt.StickyConsumerActive[actIdx] = false;
                            rt.StickyBaselines[actIdx] = CaptureStickyEngagementSnapshot(slotIndex);
                        }
                    }
                    break;
                }
                case "Hold":
                default:
                {
                    bool engaged = inputDown && delayMet;
                    UpdateStack(rt, actIdx, engaged);
                    break;
                }
            }

            rt.WasDown[actIdx] = inputDown;
        }

        /// <summary>Reads the input for an activator according to its
        /// <see cref="ShiftActivator.Kind"/>. Returns <c>true</c> when the
        /// activator should be considered "down" for engagement purposes.</summary>
        private static bool ReadActivatorInput(ShiftActivator act, CustomInputState state)
        {
            string kind = act.Kind ?? "Button";
            switch (kind)
            {
                case "Chord":
                {
                    // Both halves must be down. Cross-device chord supported:
                    // first half is read against the activator's own device
                    // state (the pass we're in); second half is looked up
                    // through LookupDeviceState when ChordSecondDeviceGuid is
                    // set and points to a different device. Falls back to
                    // the activator's own state when no second-device GUID is
                    // recorded (same-device chord — legacy / common case).
                    bool a = SourceKindRuntimeReadButtonLikeBool(state, act.Descriptor);
                    CustomInputState secondState = state;
                    if (!string.IsNullOrEmpty(act.ChordSecondDeviceGuid)
                        && !string.Equals(act.ChordSecondDeviceGuid, act.DeviceGuid,
                            System.StringComparison.OrdinalIgnoreCase))
                    {
                        secondState = LookupDeviceState(act.ChordSecondDeviceGuid) ?? state;
                    }
                    bool b = SourceKindRuntimeReadButtonLikeBool(secondState, act.ChordSecondDescriptor);
                    return a && b;
                }
                case "Axis":
                {
                    // v2: axis past threshold. ReadAxisLike returns [-1..+1].
                    float axisVal = SourceKindRuntimeReadAxisLikeFloat(state, act.Descriptor);
                    return System.Math.Abs(axisVal) >= act.AxisThreshold;
                }
                case "Button":
                default:
                    return SourceKindRuntimeReadButtonLikeBool(state, act.Descriptor);
            }
        }

        /// <summary>Maintains <see cref="ShiftRuntime.Stack"/> so the tail is
        /// always the most recently engaged activator. Adds on rising edge
        /// (engaged transition from absent), removes when engagement clears.
        /// Locked against the UI thread's <see cref="GetEngagedLayerMask"/>
        /// reads and <see cref="ClearAllShiftRuntime"/> writes.</summary>
        private static void UpdateStack(ShiftRuntime rt, int actIdx, bool engaged)
        {
            lock (rt.SyncRoot)
            {
                int existing = rt.Stack.IndexOf(actIdx);
                if (engaged)
                {
                    // Move-to-tail semantics: re-engaging a held activator
                    // doesn't churn the stack, but a release+re-press moves
                    // it to the top so the most-recently-engaged wins.
                    if (existing < 0)
                        rt.Stack.Add(actIdx);
                }
                else if (existing >= 0)
                {
                    rt.Stack.RemoveAt(existing);
                }
            }
        }

        /// <summary>Axis read used by the Axis activator kind. Mirrors
        /// <see cref="SourceKindRuntimeReadButtonLikeBool"/> but returns the
        /// signed [-1..+1] bipolar axis value without thresholding.</summary>
        private static float SourceKindRuntimeReadAxisLikeFloat(CustomInputState state, string descriptor)
            => SourceEvaluator.EvaluateForBipolarAxisTarget(
                state,
                new MappingSource { Kind = "Direct", Descriptor = descriptor ?? "" },
                0, "", 0, null, 0);

        // Reuses the Engine's button-like reader without going through the
        // managed-cast SourceCoercion wrapper (we already know the activator
        // is button-class).
        private static bool SourceKindRuntimeReadButtonLikeBool(CustomInputState state, string descriptor)
            => SourceEvaluator.EvaluateForButtonTarget(
                state,
                new MappingSource { Kind = "Direct", Descriptor = descriptor ?? "" },
                50, 0, "", 0, null, 0);

        // ─────────────────────────────────────────────
        //  Issue #61 multi-source / shift Phase 1c-2
        //  MappingSet-based descriptor reader
        //
        //  Replaces the per-PadSetting-field descriptor reads in the
        //  legacy <see cref="MapInputToGamepad"/>. Operates per device:
        //  for a given device GUID, walks every Base-layer row in the
        //  slot's <see cref="MappingSet"/> and evaluates only the
        //  sources that point to this device. Step 4's per-slot OR /
        //  MaxAbs combine across devices is preserved, so single-device
        //  rows produce bit-identical output to the legacy path.
        //
        //  Cross-device sources within a single row land correctly only
        //  when this method runs on the device that contributes the
        //  source — e.g. a `Sum` row pulling from Wheel + Pedal will
        //  see the wheel's sum on the wheel's pass and the pedal's sum
        //  on the pedal's pass; Step 4 then MaxAbs combines, which is
        //  not equal to a true cross-device Sum. Phase 2's UI prevents
        //  cross-device multi-source rows until a per-VC evaluator
        //  lands; today's migration only emits same-device sources, so
        //  this Phase 1c-2 path is bit-identical to the legacy path on
        //  every existing config.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Walks <see cref="MappingSet.Rows"/> and writes the per-row
        /// combined output into the appropriate field of <paramref name="gp"/>.
        /// Only sources matching <paramref name="thisDeviceGuid"/> (or whose
        /// <see cref="MappingSource.DeviceGuid"/> is empty, meaning "first
        /// available device") contribute on this pass.
        /// </summary>
        private static void ApplyMappingSetToGamepad(
            CustomInputState state,
            MappingSet mappingSet,
            string thisDeviceGuid,
            int globalAxisToButtonThreshold,
            int slotIndex,
            ref Gamepad gp)
        {
            if (state == null || mappingSet == null) return;
            // Snapshot Rows once — the save path mutates the live list on
            // the UI thread (Rows.Add + Sources.Clear/Add inside
            // PushUiExtraSourcesIntoSlotMappingSets), which previously
            // produced spurious "Error mapping device {guid}" errors when
            // a save raced the polling-thread iteration here. The snapshot
            // is an array of MappingRow references, so per-row Sources
            // still need SnapshotSources to handle the inner-list race.
            var rowsSnapshot = SnapshotRows(mappingSet, out int rowsSnapshotCount);
            if (rowsSnapshotCount == 0) return;

            var runtime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex]
                : null;
            double dt = (slotIndex >= 0 && slotIndex < _lastEvalTime.Length)
                ? ComputeAndAdvanceDelta(slotIndex)
                : 0;

            // Reusable per-row buffers. Step 3 runs in a single thread
            // (the polling thread), so static reuse is safe and zero-alloc.
            var axisContribs = _msAxisBuf ??= new List<float>(8);
            var boolContribs = _msBoolBuf ??= new List<bool>(8);

            // Resolve the active shift layer for this slot/device.
            //
            // Default semantics: REPLACE. When a non-Base layer is active,
            // ONLY rows on that layer fire — Base rows are entirely
            // suppressed. Targets the layer doesn't map output zero/false.
            //
            // Opt-in fallthrough: the engaged activator's
            // <see cref="ShiftActivator.InheritUnmapped"/> = true switches
            // to overlay-with-fallthrough — Base rows fall through for any
            // target the active layer doesn't cover. The per-row
            // NoInherit flag then selectively blocks fallthrough for
            // individual targets on inheritance-enabled layers.
            string activeMask = ResolveActiveLayerMask(slotIndex, mappingSet, state, thisDeviceGuid);
            bool inheritUnmapped = false;
            if (activeMask != "Base" && mappingSet?.ShiftActivators != null)
            {
                foreach (var a in mappingSet.ShiftActivators)
                {
                    if (a == null) continue;
                    if (!string.Equals(a.LayerMask, activeMask, System.StringComparison.Ordinal)) continue;
                    inheritUnmapped = a.InheritUnmapped;
                    break;
                }
            }
            HashSet<string> shiftCoveredTargets = null;
            if (activeMask != "Base" && inheritUnmapped)
            {
                shiftCoveredTargets = _shiftCoveredTargetsBuf ??= new HashSet<string>(System.StringComparer.Ordinal);
                shiftCoveredTargets.Clear();
            }
            if (shiftCoveredTargets != null)
            {
                for (int i = 0; i < rowsSnapshotCount; i++)
                {
                    var r = rowsSnapshot[i];
                    if (r == null) continue;
                    if (!string.Equals(r.LayerMask, activeMask, System.StringComparison.Ordinal))
                        continue;
                    // A matching-layer row blocks Base fallthrough when it
                    // has at least one source (real override) OR when it's
                    // an explicit NoInherit declaration. Rows with zero
                    // sources and NoInherit=false are transparent so the
                    // user can author an "intentionally inherit" row
                    // without writing source data.
                    bool hasSources = r.Sources != null && r.Sources.Count > 0;
                    if (hasSources || r.NoInherit)
                        shiftCoveredTargets.Add(r.Target ?? "");
                }
            }

            for (int rowIdx = 0; rowIdx < rowsSnapshotCount; rowIdx++)
            {
                var row = rowsSnapshot[rowIdx];
                if (row == null) continue;
                if (string.IsNullOrEmpty(row.Target)) continue;

                // Layer-row picking. Default = replace: when active mask is
                // non-Base, only that layer's rows fire (Base entirely
                // suppressed). Opt-in = inheritUnmapped: Base rows fall
                // through for targets the active layer doesn't cover.
                string rowLayer = row.LayerMask ?? "Base";
                if (activeMask == "Base")
                {
                    // Base layer active: only Base rows fire.
                    if (rowLayer != "Base") continue;
                }
                else
                {
                    if (rowLayer == "Base")
                    {
                        // Non-Base active.  Default = replace, so Base is
                        // dropped entirely. With inheritUnmapped, Base falls
                        // through for any target the active layer doesn't
                        // cover (cover = matching-mask row with sources or
                        // explicit NoInherit).
                        if (!inheritUnmapped) continue;
                        if (shiftCoveredTargets.Contains(row.Target)) continue;
                    }
                    else if (rowLayer != activeMask)
                    {
                        // Some other shift layer (Shift1 vs Shift2 etc).
                        // Skip on this pass.
                        continue;
                    }
                    else
                    {
                        // Matching-mask row with zero sources is transparent
                        // — when inheritance is on, Base falls through;
                        // when off (replace), the target stays zero/false.
                        bool hasSources = row.Sources != null && row.Sources.Count > 0;
                        if (!hasSources) continue;
                    }
                }

                var kind = TargetKindResolver.Resolve(row.Target);

                // Combined-DPad legacy target: one POV descriptor that
                // expands to all four DPad directions. Evaluated specially
                // because the gamepad write touches four bits, not one.
                if (string.Equals(row.Target, "DPad", System.StringComparison.Ordinal))
                {
                    EvaluateCombinedDpad(state, row, thisDeviceGuid, ref gp);
                    continue;
                }

                axisContribs.Clear();
                boolContribs.Clear();

                // Multi-source rows take the cross-device single-eval
                // path regardless of CombineMode. Per-device-pass mode
                // would only see this device's filtered sources and
                // CombineHelper would apply the user's combine to that
                // one-element list — which makes Sum / Average / AND /
                // XOR degenerate to the single value, with Step 4's
                // OR / MaxAbs re-merge taking over and silently
                // overriding the user's choice. Single-source rows
                // are fine on the per-device-pass path because Step
                // 4's re-merge is a no-op for a value that only one
                // pass produced.
                // Count of contributing (non-row-modifier) sources. Row
                // modifiers (InvertOnHold) don't enter the combine and
                // don't count toward "multi-source" since they're
                // transparent to the user's chosen combine mode.
                int contribCount = 0;
                if (row.Sources != null)
                {
                    for (int si = 0; si < row.Sources.Count; si++)
                        if (!IsRowModifierSource(row.Sources[si])) contribCount++;
                }
                bool isMultiSource = contribCount > 1;
                HashSet<string> multiDone = (isMultiSource && slotIndex >= 0
                    && slotIndex < _multiSourceEvaluatedTargetsBySlot.Length)
                    ? _multiSourceEvaluatedTargetsBySlot[slotIndex] : null;
                if (isMultiSource && multiDone != null && multiDone.Contains(row.Target))
                    continue;
                bool isCustom = row.CombineMode == "Custom";

                if (kind == TargetKind.Button || kind == TargetKind.PovDirection)
                {
                    if (isMultiSource)
                    {
                        var positional = BuildCustomContribsForButton(
                            row, slotIndex, globalAxisToButtonThreshold, dt);
                        if (positional.Count == 0) continue;
                        bool combined;
                        if (isCustom)
                        {
                            combined = EvaluateCustomBoolean(row, positional);
                        }
                        else
                        {
                            var bools = _contribBoolBuf ??= new List<bool>(8);
                            bools.Clear();
                            for (int bi = 0; bi < positional.Count; bi++) bools.Add(positional[bi] > 0.5f);
                            combined = CombineHelper.CombineButton(row.CombineMode, bools);
                        }
                        WriteBoolTarget(row.Target, combined, ref gp);
                        multiDone?.Add(row.Target);
                        continue;
                    }
                    for (int i = 0; i < row.Sources.Count; i++)
                    {
                        var src = row.Sources[i];
                        if (IsRowModifierSource(src)) continue;
                        if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
                        if (IsSourceSuppressedPostpone(slotIndex, src.DeviceGuid, src.Descriptor)) continue;
                        boolContribs.Add(SourceEvaluator.EvaluateForButtonTarget(
                            state, src, globalAxisToButtonThreshold,
                            slotIndex, row.Target, i, runtime, dt));
                    }
                    if (boolContribs.Count == 0) continue;
                    WriteBoolTarget(row.Target,
                        CombineHelper.CombineButton(row.CombineMode, boolContribs), ref gp);
                }
                else if (kind == TargetKind.BipolarAxis)
                {
                    if (isMultiSource)
                    {
                        var positional = BuildCustomContribsForBipolarAxis(row, slotIndex, dt);
                        if (positional.Count == 0) continue;
                        float combined = isCustom
                            ? ClampBipolar(EvaluateCustomFloat(row, positional))
                            : ClampBipolar(CombineHelper.CombineAxis(row.CombineMode, positional));
                        if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combined = -combined;
                        WriteBipolarAxisTarget(row.Target, combined, ref gp);
                        multiDone?.Add(row.Target);
                        continue;
                    }
                    for (int i = 0; i < row.Sources.Count; i++)
                    {
                        var src = row.Sources[i];
                        if (IsRowModifierSource(src)) continue;
                        if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
                        if (IsSourceSuppressedPostpone(slotIndex, src.DeviceGuid, src.Descriptor)) continue;
                        axisContribs.Add(SourceEvaluator.EvaluateForBipolarAxisTarget(
                            state, src, slotIndex, row.Target, i, runtime, dt));
                    }
                    if (axisContribs.Count == 0) continue;
                    float combinedSingle = ClampBipolar(CombineHelper.CombineAxis(row.CombineMode, axisContribs));
                    if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combinedSingle = -combinedSingle;
                    WriteBipolarAxisTarget(row.Target, combinedSingle, ref gp);
                }
                else if (kind == TargetKind.Trigger)
                {
                    if (isMultiSource)
                    {
                        var positional = BuildCustomContribsForTrigger(row, slotIndex, dt);
                        if (positional.Count == 0) continue;
                        float combined = isCustom
                            ? ClampUnipolar(EvaluateCustomFloat(row, positional))
                            : ClampUnipolar(CombineHelper.CombineAxis(row.CombineMode, positional));
                        if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combined = 1f - combined;
                        WriteTriggerTarget(row.Target, combined, ref gp);
                        multiDone?.Add(row.Target);
                        continue;
                    }
                    for (int i = 0; i < row.Sources.Count; i++)
                    {
                        var src = row.Sources[i];
                        if (IsRowModifierSource(src)) continue;
                        if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
                        if (IsSourceSuppressedPostpone(slotIndex, src.DeviceGuid, src.Descriptor)) continue;
                        axisContribs.Add(SourceEvaluator.EvaluateForTriggerTarget(
                            state, src, slotIndex, row.Target, i, runtime, dt));
                    }
                    if (axisContribs.Count == 0) continue;
                    float combinedTrig = ClampUnipolar(CombineHelper.CombineAxis(row.CombineMode, axisContribs));
                    if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combinedTrig = 1f - combinedTrig;
                    WriteTriggerTarget(row.Target, combinedTrig, ref gp);
                }
            }
        }

        /// <summary>True when any source on this row has Kind=InvertOnHold
        /// and its ParamModifier button is currently pressed. The
        /// InvertOnHold source kind acts as a row-level modifier — the
        /// source itself contributes nothing to the combine (see
        /// <see cref="IsRowModifierSource"/>); only its ParamModifier
        /// affects the row by sign-flipping the post-combine output.
        ///
        /// <para>The modifier is read against each source's own DeviceGuid
        /// when set; otherwise against the device currently processing
        /// the row. Multiple InvertOnHold sources on one row OR together:
        /// any held modifier triggers the flip.</para></summary>
        private static bool IsInvertOnHoldActive(MappingRow row, CustomInputState fallbackState, string fallbackDeviceGuid, int slotIndex)
        {
            if (row == null || row.Sources == null) return false;
            for (int i = 0; i < row.Sources.Count; i++)
            {
                var src = row.Sources[i];
                if (src == null) continue;
                if (!string.Equals(src.Kind ?? "Direct", "InvertOnHold", System.StringComparison.Ordinal))
                    continue;
                if (string.IsNullOrEmpty(src.ParamModifier)) continue;
                // PostponeMapping suppression — when an activator with
                // PostponeMapping=false names this same modifier descriptor,
                // its press is "consumed" by the layer change and shouldn't
                // also flip the row sign. Consistent with the source-eval
                // suppression check in BuildCustomContribsFor*.
                string modifierDeviceGuid = string.IsNullOrEmpty(src.DeviceGuid) ? fallbackDeviceGuid : src.DeviceGuid;
                if (IsSourceSuppressedPostpone(slotIndex, modifierDeviceGuid, src.ParamModifier))
                    continue;
                CustomInputState s = string.IsNullOrEmpty(src.DeviceGuid)
                    ? fallbackState
                    : (LookupDeviceState(src.DeviceGuid) ?? fallbackState);
                if (SourceKindRuntimeReadButtonLikeBool(s, src.ParamModifier))
                    return true;
            }
            return false;
        }

        /// <summary>True for sources that act as row-level modifiers
        /// (currently only Kind=InvertOnHold). Skipped by the per-row
        /// contribution-building loops so they don't enter the combine,
        /// and they don't count toward the "is multi-source" check.</summary>
        private static bool IsRowModifierSource(MappingSource src)
            => src != null
            && string.Equals(src.Kind ?? "Direct", "InvertOnHold", System.StringComparison.Ordinal);

        /// <summary>Number of non-modifier (combine-contributing) sources
        /// on a row. Used by the per-target evaluators to drive the
        /// single-vs-multi-source dispatch.</summary>
        private static int CountContributingSources(MappingRow row)
        {
            if (row?.Sources == null) return 0;
            int n = 0;
            for (int i = 0; i < row.Sources.Count; i++)
                if (!IsRowModifierSource(row.Sources[i])) n++;
            return n;
        }

        /// <summary>First non-modifier source on a row, or <c>null</c>
        /// when every source is a row modifier (which makes the row
        /// effectively unmapped — the modifier has nothing to flip).</summary>
        private static MappingSource FirstContributingSource(MappingRow row)
        {
            if (row?.Sources == null) return null;
            for (int i = 0; i < row.Sources.Count; i++)
                if (!IsRowModifierSource(row.Sources[i])) return row.Sources[i];
            return null;
        }

        // ─── Per-row buffer reuse (single polling thread; static is safe) ──
        [System.ThreadStatic] private static List<float> _msAxisBuf;
        [System.ThreadStatic] private static List<bool> _msBoolBuf;

        // Per-row scratch for the Build*/multi-source paths. Reused across
        // the three Build helpers (each clears at entry); used sequentially
        // on the polling thread so one buffer per type is sufficient.
        [System.ThreadStatic] private static List<float> _contribFloatBuf;
        [System.ThreadStatic] private static List<bool> _contribBoolBuf;
        [System.ThreadStatic] private static List<float> _contribFlagsBuf;
        [System.ThreadStatic] private static List<float> _contribActiveBuf;
        [System.ThreadStatic] private static HashSet<string> _shiftCoveredTargetsBuf;
        // Pooled snapshot buffers + cached Base-row index. The KBM eval
        // calls FindBaseRowForTarget ~104×/slot/cycle; before caching, each
        // call allocated a MappingRow[] and linearly scanned. The dict
        // cache rebuilds on (mappingSet, Count) change — same race
        // tolerance as the prior SnapshotRows + scan.
        [System.ThreadStatic] private static MappingRow[] _rowsSnapshotBuf;
        [System.ThreadStatic] private static MappingSource[] _sourcesSnapshotBuf;
        [System.ThreadStatic] private static Dictionary<string, MappingRow> _baseRowIndex;
        [System.ThreadStatic] private static MappingSet _baseRowIndexFor;
        [System.ThreadStatic] private static int _baseRowIndexCount;

        private static bool SourceMatchesDevice(MappingSource src, string thisDeviceGuid)
        {
            if (src == null) return false;
            if (string.IsNullOrEmpty(src.DeviceGuid)) return true; // "any device"
            return string.Equals(src.DeviceGuid, thisDeviceGuid, System.StringComparison.OrdinalIgnoreCase);
        }

        private static float ClampBipolar(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
            if (v < -1f) return -1f;
            if (v > 1f) return 1f;
            return v;
        }
        private static float ClampUnipolar(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        // ─── Custom expression dispatch (compiled lazily, cached on row) ──

        private static float EvaluateCustomFloat(MappingRow row, IList<float> contribs)
        {
            var compiled = GetOrCompileExpression(row);
            return compiled.Evaluate(contribs);
        }

        private static bool EvaluateCustomBoolean(MappingRow row, IList<float> contribs)
        {
            var compiled = GetOrCompileExpression(row);
            return compiled.Evaluate(contribs) > 0.5f;
        }

        // ─────────────────────────────────────────────
        //  Cross-device Custom-row evaluation
        //
        //  Builds the row's contributions in positional order:
        //    a = primary (Sources[0])  — with the bipolar Neg-pair
        //        merged in via sum if present (Neg pair = same
        //        DeviceGuid as primary AND Invert flipped)
        //    b = first ExtraSource (Sources[1] or [2] depending on
        //        whether Neg pair is at index 1)
        //    c = second ExtraSource
        //    …
        //  Each source is evaluated against ITS OWN device's live
        //  InputState (looked up via UserDevices), not the current
        //  pass's state. Missing-device sources contribute 0.
        // ─────────────────────────────────────────────

        private static CustomInputState LookupDeviceState(string deviceGuid)
        {
            if (string.IsNullOrEmpty(deviceGuid)) return null;
            if (!System.Guid.TryParse(deviceGuid, out var g)) return null;
            var devs = SettingsManager.UserDevices?.Items;
            if (devs == null) return null;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                for (int i = 0; i < devs.Count; i++)
                {
                    var d = devs[i];
                    if (d == null) continue;
                    if (d.InstanceGuid == g && d.IsOnline)
                        return d.InputState;
                }
            }
            return null;
        }

        /// <summary>True when sources[i] looks like the bipolar Neg
        /// pair of sources[0] — same device, descriptor matches the
        /// pair encoding (post-prefix-stripped), Invert flipped. Used
        /// only for bipolar axis targets where the migrator and the
        /// save path emit the Neg as Sources[1].</summary>
        private static bool IsBipolarNegPair(MappingSource primary, MappingSource candidate)
        {
            if (primary == null || candidate == null) return false;
            if (!string.Equals(primary.DeviceGuid ?? "", candidate.DeviceGuid ?? "",
                System.StringComparison.OrdinalIgnoreCase)) return false;
            return primary.Invert != candidate.Invert;
        }

        /// <summary>True when the row's target is a bipolar-axis kind
        /// where a Neg-pair encoding is meaningful.</summary>
        private static bool TargetIsBipolarAxis(string target)
            => target == "LeftThumbAxisX" || target == "LeftThumbAxisY"
            || target == "RightThumbAxisX" || target == "RightThumbAxisY"
            || (target != null && target.StartsWith("ExtendedAxis", System.StringComparison.Ordinal));

        /// <summary>Snapshots row.Sources into the thread-local pooled
        /// buffer. The save path mutates row.Sources without locking;
        /// a polling-thread Clear+Add race would otherwise throw. Returns
        /// the shared buffer and writes the populated count to
        /// <paramref name="count"/>. Iterate to <c>count</c>, not buf.Length.</summary>
        private static MappingSource[] SnapshotSources(MappingRow row, out int count)
        {
            var src = row?.Sources;
            if (src == null) { count = 0; return System.Array.Empty<MappingSource>(); }
            int n = src.Count;
            var buf = _sourcesSnapshotBuf;
            if (buf == null || buf.Length < n)
                _sourcesSnapshotBuf = buf = new MappingSource[System.Math.Max(n, 8)];
            for (int i = 0; i < n && i < src.Count; i++) buf[i] = src[i];
            count = n;
            return buf;
        }

        /// <summary>Race-safe snapshot of <c>mappingSet.Rows</c> for the
        /// polling-thread eval. Reuses <see cref="_rowsSnapshotBuf"/>
        /// across calls — Step 3 is single-threaded so the pool is
        /// zero-alloc in steady state. Iterate to <paramref name="count"/>,
        /// not buf.Length.</summary>
        internal static MappingRow[] SnapshotRows(MappingSet mappingSet, out int count)
        {
            var rows = mappingSet?.Rows;
            if (rows == null) { count = 0; return System.Array.Empty<MappingRow>(); }
            int n = rows.Count;
            var buf = _rowsSnapshotBuf;
            if (buf == null || buf.Length < n)
                _rowsSnapshotBuf = buf = new MappingRow[System.Math.Max(n, 16)];
            for (int i = 0; i < n && i < rows.Count; i++) buf[i] = rows[i];
            count = n;
            return buf;
        }

        /// <summary>Builds the row's positional contributions list for
        /// the multi-source cross-device path. Variable order
        /// a..z mirrors the row's UI. Each entry is the source's
        /// coerced value against ITS OWN device's state. Returns an
        /// empty list if no source could be evaluated against any
        /// online device.</summary>
        private static List<float> BuildCustomContribsForBipolarAxis(
            MappingRow row, int slotIndex, double dt)
        {
            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            var list = _contribFloatBuf ??= new List<float>(8);
            list.Clear();
            var srcs = SnapshotSources(row, out int srcsCount);
            if (srcsCount == 0) return list;

            int negPairIndex = -1;
            if (TargetIsBipolarAxis(row.Target) && srcsCount >= 2
                && IsBipolarNegPair(srcs[0], srcs[1]))
            {
                negPairIndex = 1;
            }

            for (int i = 0; i < srcsCount; i++)
            {
                if (i == negPairIndex) continue;
                var src = srcs[i];
                if (IsRowModifierSource(src)) continue;
                if (src == null) { list.Add(0f); continue; }
                // PostponeMapping=false on an activator suppresses its own
                // descriptor; substitute 0 so Custom-formula positions stay
                // stable (sN references are positional).
                if (IsSourceSuppressedPostpone(slotIndex, src.DeviceGuid, src.Descriptor))
                { list.Add(0f); continue; }
                var devState = LookupDeviceState(src.DeviceGuid);
                if (devState == null) { list.Add(0f); continue; }
                float v = SourceEvaluator.EvaluateForBipolarAxisTarget(
                    devState, src, slotIndex, row.Target, i, slotRuntime, dt);
                if (i == 0 && negPairIndex == 1)
                {
                    var negSrc = srcs[1];
                    var negState = negSrc != null ? LookupDeviceState(negSrc.DeviceGuid) : null;
                    if (negState != null
                        && !IsSourceSuppressedPostpone(slotIndex, negSrc.DeviceGuid, negSrc.Descriptor))
                    {
                        v += SourceEvaluator.EvaluateForBipolarAxisTarget(
                            negState, negSrc, slotIndex, row.Target, 1, slotRuntime, dt);
                    }
                }
                list.Add(v);
            }
            return list;
        }

        private static List<float> BuildCustomContribsForTrigger(
            MappingRow row, int slotIndex, double dt)
        {
            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            var list = _contribFloatBuf ??= new List<float>(8);
            list.Clear();
            var srcs = SnapshotSources(row, out int srcsCount);
            for (int i = 0; i < srcsCount; i++)
            {
                var src = srcs[i];
                if (IsRowModifierSource(src)) continue;
                if (src == null) { list.Add(0f); continue; }
                if (IsSourceSuppressedPostpone(slotIndex, src.DeviceGuid, src.Descriptor))
                { list.Add(0f); continue; }
                var devState = LookupDeviceState(src.DeviceGuid);
                if (devState == null) { list.Add(0f); continue; }
                list.Add(SourceEvaluator.EvaluateForTriggerTarget(
                    devState, src, slotIndex, row.Target, i, slotRuntime, dt));
            }
            return list;
        }

        private static List<float> BuildCustomContribsForButton(
            MappingRow row, int slotIndex, int globalAxisToButtonThreshold, double dt)
        {
            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            var list = _contribFloatBuf ??= new List<float>(8);
            list.Clear();
            var srcs = SnapshotSources(row, out int srcsCount);
            for (int i = 0; i < srcsCount; i++)
            {
                var src = srcs[i];
                if (IsRowModifierSource(src)) continue;
                if (src == null) { list.Add(0f); continue; }
                if (IsSourceSuppressedPostpone(slotIndex, src.DeviceGuid, src.Descriptor))
                { list.Add(0f); continue; }
                var devState = LookupDeviceState(src.DeviceGuid);
                if (devState == null) { list.Add(0f); continue; }
                list.Add(SourceEvaluator.EvaluateForButtonTarget(
                    devState, src, globalAxisToButtonThreshold,
                    slotIndex, row.Target, i, slotRuntime, dt) ? 1f : 0f);
            }
            return list;
        }

        // ─────────────────────────────────────────────
        //  Per-target MappingSet evaluators for non-gamepad VC outputs
        //  (MIDI / KBM / Extended / Touchpad). Each looks up the Base-layer
        //  row by target name, evaluates the row's sources (cross-device,
        //  multi-source, combine-mode, Custom-formula aware), and returns
        //  the final value. Returns false when no row exists for the
        //  target so the caller can fall back to legacy single-source
        //  reading (covers configs that haven't been resaved since the
        //  multi-source UI shipped).
        //
        //  These mirror the per-target dispatch inside ApplyMappingSetToGamepad
        //  but are exposed as small, return-by-value helpers because the
        //  non-gamepad output structs (MidiRawState, KbmRawState, etc.)
        //  don't fit the row-iteration-with-WriteXTarget pattern: each
        //  legacy method has its own per-VC-type indexing and post-
        //  processing (deadzones, scrolling, contact bools, ...) we'd
        //  have to duplicate verbatim.
        // ─────────────────────────────────────────────

        /// <summary>Looks up the Base-layer <see cref="MappingRow"/> for a
        /// target by name via a polling-thread-local dictionary cache.
        /// Mirrors the row filter used by
        /// <see cref="ApplyMappingSetToGamepad"/>. KBM mapping calls this
        /// ~104×/slot/cycle (one per keyboard key + mouse button + axis);
        /// without the cache each call was O(rows) with a per-call array
        /// allocation. The cache rebuilds when MappingSet identity OR
        /// Rows.Count changes — same race tolerance as the prior path.</summary>
        private static MappingRow FindBaseRowForTarget(MappingSet mappingSet, string targetName)
        {
            if (mappingSet == null || string.IsNullOrEmpty(targetName)) return null;
            var rows = mappingSet.Rows;
            if (rows == null) return null;

            int currentCount = rows.Count;
            if (_baseRowIndex == null
                || _baseRowIndexFor != mappingSet
                || _baseRowIndexCount != currentCount)
            {
                if (_baseRowIndex == null)
                    _baseRowIndex = new Dictionary<string, MappingRow>(64, System.StringComparer.Ordinal);
                else
                    _baseRowIndex.Clear();

                // Defensive read: rows.Count can shrink mid-iteration if the
                // save path is mutating. Bound by the captured count AND
                // re-check rows.Count on each step — same pattern SnapshotRows
                // uses. Stale read in the rare race self-heals next cycle.
                for (int i = 0; i < currentCount && i < rows.Count; i++)
                {
                    var r = rows[i];
                    if (r == null) continue;
                    if (!string.Equals(r.LayerMask ?? "Base", "Base", System.StringComparison.Ordinal)) continue;
                    string target = r.Target;
                    if (string.IsNullOrEmpty(target)) continue;
                    _baseRowIndex[target] = r;
                }

                _baseRowIndexFor = mappingSet;
                _baseRowIndexCount = currentCount;
            }

            return _baseRowIndex.TryGetValue(targetName, out var row) ? row : null;
        }

        /// <summary>Evaluates a button-class target through the per-VC
        /// MappingSet. <paramref name="value"/> = final combined bool;
        /// returns <c>false</c> when no row exists for the target (caller
        /// should fall back to legacy per-device descriptor lookup).</summary>
        public static bool TryEvaluateMappingSetButton(
            CustomInputState state, MappingSet mappingSet, string thisDeviceGuid,
            int slotIndex, string targetName, int globalAxisToButtonThreshold,
            out bool value)
        {
            value = false;
            var row = FindBaseRowForTarget(mappingSet, targetName);
            if (row == null || row.Sources == null || row.Sources.Count == 0)
                return false;

            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            double dt = (slotIndex >= 0 && slotIndex < _lastEvalTime.Length)
                ? ComputeAndAdvanceDelta(slotIndex) : 0;

            int contribCount = CountContributingSources(row);
            if (contribCount == 0) return false;
            bool isMultiSource = contribCount > 1;
            bool isCustom = row.CombineMode == "Custom";

            if (isMultiSource)
            {
                var positional = BuildCustomContribsForButton(row, slotIndex, globalAxisToButtonThreshold, dt);
                if (positional.Count == 0) return false;
                if (isCustom)
                {
                    value = EvaluateCustomBoolean(row, positional);
                }
                else
                {
                    var bools = _contribBoolBuf ??= new List<bool>(8);
                    bools.Clear();
                    for (int bi = 0; bi < positional.Count; bi++) bools.Add(positional[bi] > 0.5f);
                    value = CombineHelper.CombineButton(row.CombineMode, bools);
                }
                return true;
            }

            // Single source — evaluate cross-device (the source's own DeviceGuid
            // wins, not necessarily the device we're currently processing).
            var src = FirstContributingSource(row);
            if (src == null) return false;
            var devState = string.IsNullOrEmpty(src.DeviceGuid)
                ? state
                : (LookupDeviceState(src.DeviceGuid) ?? state);
            value = SourceEvaluator.EvaluateForButtonTarget(
                devState, src, globalAxisToButtonThreshold,
                slotIndex, targetName, 0, slotRuntime, dt);
            return true;
        }

        /// <summary>Evaluates a bipolar-axis target through the per-VC
        /// MappingSet. <paramref name="value"/> = combined float clamped to
        /// [-1, +1] converted to signed short (-32768..32767); returns
        /// <c>false</c> when no row exists.</summary>
        public static bool TryEvaluateMappingSetBipolarAxis(
            CustomInputState state, MappingSet mappingSet, string thisDeviceGuid,
            int slotIndex, string targetName,
            out short value)
        {
            value = 0;
            var row = FindBaseRowForTarget(mappingSet, targetName);
            if (row == null || row.Sources == null || row.Sources.Count == 0)
                return false;

            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            double dt = (slotIndex >= 0 && slotIndex < _lastEvalTime.Length)
                ? ComputeAndAdvanceDelta(slotIndex) : 0;

            int contribCount = CountContributingSources(row);
            if (contribCount == 0) return false;
            bool isMultiSource = contribCount > 1;
            bool isCustom = row.CombineMode == "Custom";
            float combined;

            if (isMultiSource)
            {
                var positional = BuildCustomContribsForBipolarAxis(row, slotIndex, dt);
                if (positional.Count == 0) return false;
                combined = isCustom
                    ? ClampBipolar(EvaluateCustomFloat(row, positional))
                    : ClampBipolar(CombineHelper.CombineAxis(row.CombineMode, positional));
            }
            else
            {
                var src = FirstContributingSource(row);
                if (src == null) return false;
                var devState = string.IsNullOrEmpty(src.DeviceGuid)
                    ? state
                    : (LookupDeviceState(src.DeviceGuid) ?? state);
                combined = ClampBipolar(SourceEvaluator.EvaluateForBipolarAxisTarget(
                    devState, src, slotIndex, targetName, 0, slotRuntime, dt));
            }

            if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combined = -combined;

            // Map [-1..+1] → signed short with the same convention legacy
            // MapToThumbAxisWithNeg uses: -1 → short.MinValue, +1 → short.MaxValue.
            if (combined <= -1f) value = short.MinValue;
            else if (combined >= 1f) value = short.MaxValue;
            else value = (short)(combined * 32767f);
            return true;
        }

        /// <summary>Evaluates a touchpad-passthrough X/Y target with
        /// per-source gating: a touchpad-class source (descriptor starts
        /// with "Touchpad ") contributes ONLY while its paired
        /// TouchpadDown is true on its device's state. An idle touchpad's
        /// stale finger position can't pollute the combine — Average,
        /// MaxAbs, Sum, etc. all see active sources only. Non-touchpad
        /// sources (sticks / buttons / POVs mapped to a touchpad target)
        /// are always considered active (sticks at rest read 0 and don't
        /// "win" the combine the way a stale touchpad position would).
        ///
        /// <para>Custom mode receives every source plus a parallel
        /// active-flag channel (<c>aD..zD</c> in the formula language)
        /// so the formula can implement its own gating
        /// (e.g. <c>aD ? a : (bD ? b : 0.5)</c>).</para>
        ///
        /// <para>Returns <c>false</c> when zero sources are active, so
        /// the touchpad output path can hold the last position — matches
        /// the natural sticky behavior of a single physical touchpad.</para>
        ///
        /// <para><paramref name="defaultFingerIdx"/> is the finger slot
        /// the OUTPUT target represents (0 for X1/Y1, 1 for X2/Y2). It's
        /// used as the fallback finger index when a source's descriptor
        /// is the bare "Touchpad" prefix form without an explicit Finger
        /// number — current callers always pass parseable descriptors,
        /// so this is purely defensive.</para></summary>
        public static bool TryEvaluateMappingSetTouchpadAxis(
            CustomInputState state, MappingSet mappingSet, string thisDeviceGuid,
            int slotIndex, string targetName, int defaultFingerIdx,
            out short value)
        {
            value = 0;
            var row = FindBaseRowForTarget(mappingSet, targetName);
            if (row == null || row.Sources == null || row.Sources.Count == 0) return false;

            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            double dt = (slotIndex >= 0 && slotIndex < _lastEvalTime.Length)
                ? ComputeAndAdvanceDelta(slotIndex) : 0;

            var sources = SnapshotSources(row, out int sourcesCount);
            var values = _contribFloatBuf ??= new List<float>(8);
            values.Clear();
            var flags = _contribFlagsBuf ??= new List<float>(8);
            flags.Clear();
            int activeCount = 0;

            for (int i = 0; i < sourcesCount; i++)
            {
                var src = sources[i];
                if (IsRowModifierSource(src)) continue;
                if (src == null) { values.Add(0f); flags.Add(0f); continue; }

                var devState = string.IsNullOrEmpty(src.DeviceGuid)
                    ? state
                    : (LookupDeviceState(src.DeviceGuid) ?? state);

                // "Active" = currently contributing useful data.
                bool isActive;
                bool isTouchpadSrc = !string.IsNullOrEmpty(src.Descriptor)
                    && src.Descriptor.StartsWith("Touchpad ", System.StringComparison.Ordinal);
                if (isTouchpadSrc)
                {
                    // Parse pad + finger index from "Touchpad N Finger M X|Y".
                    int padIdx = 0;
                    int fingerIdx = defaultFingerIdx;
                    var parts = src.Descriptor.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int parsedPad))
                        padIdx = parsedPad;
                    if (parts.Length == 5
                        && string.Equals(parts[2], "Finger", System.StringComparison.Ordinal)
                        && int.TryParse(parts[3], out int parsedFinger))
                    {
                        fingerIdx = parsedFinger;
                    }
                    var pad = devState?.Touchpads != null
                        && padIdx >= 0 && padIdx < devState.Touchpads.Length
                        ? devState.Touchpads[padIdx] : null;
                    isActive = pad != null
                        && fingerIdx >= 0
                        && fingerIdx < pad.MaxFingers
                        && pad.FingerDown[fingerIdx];
                }
                else
                {
                    // Non-touchpad sources (sticks, buttons, POVs) are
                    // always live — their natural rest value is 0 so they
                    // can't "win" a combine by being stale.
                    isActive = true;
                }

                float v = SourceEvaluator.EvaluateForBipolarAxisTarget(
                    devState ?? state, src, slotIndex, targetName, i, slotRuntime, dt);

                values.Add(v);
                flags.Add(isActive ? 1f : 0f);
                if (isActive) activeCount++;
            }

            // No active source → caller holds the previous position.
            // Sticky-touchpad semantic, regardless of combine mode.
            if (activeCount == 0) return false;

            bool isCustom = row.CombineMode == "Custom";
            float combined;

            if (isCustom)
            {
                // Custom gets ALL sources + the aD..zD active-flag channel.
                // The formula author handles gating explicitly so positional
                // indices stay stable across frames (gating-by-skip would
                // shift a/b/c as fingers come and go, which is unusable).
                var compiled = GetOrCompileExpression(row);
                combined = ClampBipolar(compiled.Evaluate(values, flags));
            }
            else if (sourcesCount > 1)
            {
                // Built-in modes: filter to active sources only, then combine.
                var activeValues = _contribActiveBuf ??= new List<float>(8);
                activeValues.Clear();
                for (int i = 0; i < values.Count; i++)
                    if (flags[i] > 0.5f) activeValues.Add(values[i]);
                combined = ClampBipolar(CombineHelper.CombineAxis(row.CombineMode, activeValues));
            }
            else
            {
                // Single active source (guaranteed by activeCount>0 + Count==1).
                combined = ClampBipolar(values[0]);
            }

            if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combined = -combined;

            if (combined <= -1f) value = short.MinValue;
            else if (combined >= 1f) value = short.MaxValue;
            else value = (short)(combined * 32767f);
            return true;
        }

        /// <summary>Evaluates a unipolar trigger-class target (Extended
        /// trigger slot) through the per-VC MappingSet. Returned value is
        /// in the same signed-short representation the Extended raw path
        /// uses: short.MinValue = released (0%), short.MaxValue = fully
        /// pressed (100%). Returns <c>false</c> when no row exists.</summary>
        public static bool TryEvaluateMappingSetExtendedTrigger(
            CustomInputState state, MappingSet mappingSet, string thisDeviceGuid,
            int slotIndex, string targetName,
            out short value)
        {
            value = short.MinValue;
            var row = FindBaseRowForTarget(mappingSet, targetName);
            if (row == null || row.Sources == null || row.Sources.Count == 0)
                return false;

            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            double dt = (slotIndex >= 0 && slotIndex < _lastEvalTime.Length)
                ? ComputeAndAdvanceDelta(slotIndex) : 0;

            int contribCount = CountContributingSources(row);
            if (contribCount == 0) return false;
            bool isMultiSource = contribCount > 1;
            bool isCustom = row.CombineMode == "Custom";
            float combined;

            if (isMultiSource)
            {
                var positional = BuildCustomContribsForTrigger(row, slotIndex, dt);
                if (positional.Count == 0) return false;
                combined = isCustom
                    ? ClampUnipolar(EvaluateCustomFloat(row, positional))
                    : ClampUnipolar(CombineHelper.CombineAxis(row.CombineMode, positional));
            }
            else
            {
                var src = FirstContributingSource(row);
                if (src == null) return false;
                var devState = string.IsNullOrEmpty(src.DeviceGuid)
                    ? state
                    : (LookupDeviceState(src.DeviceGuid) ?? state);
                combined = ClampUnipolar(SourceEvaluator.EvaluateForTriggerTarget(
                    devState, src, slotIndex, targetName, 0, slotRuntime, dt));
            }

            if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combined = 1f - combined;

            // [0..+1] → signed short with short.MinValue = 0% (matches the
            // legacy MapToExtendedTriggerAxis convention).
            int ushortVal = (int)(combined * 65535f);
            if (ushortVal < 0) ushortVal = 0;
            if (ushortVal > 65535) ushortVal = 65535;
            value = (short)(ushortVal + short.MinValue);
            return true;
        }

        private static MappingExpression.Compiled GetOrCompileExpression(MappingRow row)
        {
            // The compiled AST is cached in a side dictionary keyed by
            // expression string so MappingRow stays a plain DTO. A typical
            // user has tens of rows with at most a handful of distinct
            // Custom expressions; the dictionary stays tiny.
            var key = row.CombineExpression ?? "";
            if (_compiledExpressions.TryGetValue(key, out var cached))
                return cached;

            var compiled = MappingExpression.Compile(key);
            _compiledExpressions[key] = compiled;
            return compiled;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MappingExpression.Compiled>
            _compiledExpressions = new();

        // ─── Combined-DPad target ─────────────────────────────────────────

        private static void EvaluateCombinedDpad(
            CustomInputState state, MappingRow row, string thisDeviceGuid, ref Gamepad gp)
        {
            // Per the migrator, combined-DPad target only emits when no
            // individual DPadUp/Down/Left/Right rows exist. Sources are
            // POV descriptors. Multi-source on combined DPad is not
            // exposed in the UI, but we tolerate it here by OR'ing each
            // direction across sources.
            bool up = false, down = false, left = false, right = false;
            foreach (var src in row.Sources)
            {
                if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
                if (string.IsNullOrEmpty(src.Descriptor)) continue;
                // Construct synthetic POV-direction sources to reuse the coercion path.
                up    |= EvalPovBool(state, src, "Up");
                down  |= EvalPovBool(state, src, "Down");
                left  |= EvalPovBool(state, src, "Left");
                right |= EvalPovBool(state, src, "Right");
            }
            if (up)    gp.SetButton(Gamepad.DPAD_UP, true);
            if (down)  gp.SetButton(Gamepad.DPAD_DOWN, true);
            if (left)  gp.SetButton(Gamepad.DPAD_LEFT, true);
            if (right) gp.SetButton(Gamepad.DPAD_RIGHT, true);
        }

        private static bool EvalPovBool(CustomInputState state, MappingSource src, string direction)
        {
            // Build a POV-direction descriptor on the fly: original
            // descriptor is "POV N" (no direction); we tack on the
            // direction we're testing.
            var s = (src.Descriptor ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("POV", System.StringComparison.OrdinalIgnoreCase))
                return false;
            var synth = new MappingSource
            {
                Kind = "Direct",
                DeviceGuid = src.DeviceGuid,
                Descriptor = $"POV {parts[1]} {direction}",
                Invert = src.Invert,
                HalfAxis = src.HalfAxis,
                DeadZone = src.DeadZone,
            };
            return SourceCoercion.EvaluateForButtonTarget(state, synth, 50);
        }

        // ─── Target → Gamepad-field dispatch ─────────────────────────────

        private static void WriteBoolTarget(string target, bool value, ref Gamepad gp)
        {
            switch (target)
            {
                case "ButtonA":         if (value) gp.SetButton(Gamepad.A, true); break;
                case "ButtonB":         if (value) gp.SetButton(Gamepad.B, true); break;
                case "ButtonX":         if (value) gp.SetButton(Gamepad.X, true); break;
                case "ButtonY":         if (value) gp.SetButton(Gamepad.Y, true); break;
                case "LeftShoulder":    if (value) gp.SetButton(Gamepad.LEFT_SHOULDER,  true); break;
                case "RightShoulder":   if (value) gp.SetButton(Gamepad.RIGHT_SHOULDER, true); break;
                case "ButtonBack":      if (value) gp.SetButton(Gamepad.BACK,  true); break;
                case "ButtonStart":     if (value) gp.SetButton(Gamepad.START, true); break;
                case "LeftThumbButton": if (value) gp.SetButton(Gamepad.LEFT_THUMB,  true); break;
                case "RightThumbButton":if (value) gp.SetButton(Gamepad.RIGHT_THUMB, true); break;
                case "ButtonGuide":     if (value) gp.SetButton(Gamepad.GUIDE, true); break;
                case "ButtonShare":     if (value) gp.Share = true; break;
                case "DPadUp":          if (value) gp.SetButton(Gamepad.DPAD_UP,    true); break;
                case "DPadDown":        if (value) gp.SetButton(Gamepad.DPAD_DOWN,  true); break;
                case "DPadLeft":        if (value) gp.SetButton(Gamepad.DPAD_LEFT,  true); break;
                case "DPadRight":       if (value) gp.SetButton(Gamepad.DPAD_RIGHT, true); break;
            }
        }

        private static void WriteBipolarAxisTarget(string target, float value, ref Gamepad gp)
        {
            // Gamepad axes are SDL/XInput-style int16 in [-32768, 32767];
            // multiply the [-1, +1] float by 32767 (matching the engine's
            // existing scaling). Negate Y to match legacy "+Y down → -axis"
            // convention used in MapToThumbAxisWithNeg.
            short scaled = (short)System.Math.Clamp((int)(value * 32767f), -32768, 32767);
            switch (target)
            {
                case "LeftThumbAxisX":  gp.ThumbLX = scaled; break;
                case "LeftThumbAxisY":  gp.ThumbLY = (short)System.Math.Clamp((int)(-value * 32767f), -32768, 32767); break;
                case "RightThumbAxisX": gp.ThumbRX = scaled; break;
                case "RightThumbAxisY": gp.ThumbRY = (short)System.Math.Clamp((int)(-value * 32767f), -32768, 32767); break;
            }
        }

        private static void WriteTriggerTarget(string target, float value, ref Gamepad gp)
        {
            // Triggers are uint16 in [0, 65535]; legacy MapToTrigger uses
            // the same scaling.
            ushort scaled = (ushort)System.Math.Clamp((int)(value * 65535f), 0, 65535);
            switch (target)
            {
                case "LeftTrigger":  gp.LeftTrigger  = scaled; break;
                case "RightTrigger": gp.RightTrigger = scaled; break;
            }
        }
    }
}
