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
            _stickTrimStates.Clear();
            // The gravity-lean pair's captured resting grip lives beside
            // the per-slot motion neutrals and follows the same profile
            // switch / engine-stop hygiene.
            SourceCoercion.ResetGyroLeanNeutral();
        }

        // ─────────────────────────────────────────────
        //  Stick-trim combine state (#155)
        //
        //  One stored level per (slot, target, layer): the same keying
        //  family as SourceKindRuntime's (slot, target, srcIdx)
        //  accumulators, with the layer added because a Base row and a
        //  Shift row on the same target are distinct rows with distinct
        //  levels. Rows are DTOs (the compiled-expression cache keeps
        //  state off them deliberately), so the level lives here.
        //  Cleared with the other accumulators above.
        // ─────────────────────────────────────────────
        private sealed class StickTrimState
        {
            public float Level = 1f;
            public bool WasHeld;
            // Frame idempotence: the Extended/KBM/MIDI evaluators run
            // once per assigned DEVICE per frame (per-UserSetting pass),
            // so without this gate a two-device slot would advance the
            // level twice per tick and double-process the release edge.
            // The gamepad path is already once-per-frame via multiDone;
            // this covers every path uniformly.
            public long LastSeq = -1;
            public float LastOutput;
        }

        // Incremented once per polling frame in
        // BeginFrameMultiSourceTracking, read by EvaluateStickTrim.
        private static long _stickTrimFrameSeq;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            (int Slot, string Target, string Layer), StickTrimState> _stickTrimStates = new();

        /// <summary>Stick-trim combine (#155): the row's LAST contributing
        /// source is the trim stick, every earlier contributing source
        /// gates. While the gate is held, the trim axis's signed
        /// deflection past <see cref="MappingRow.TrimDeadzone"/> slides a
        /// stored level (stick up raises, down lowers, at
        /// <see cref="MappingRow.TrimRate"/> percent per second at full
        /// deflection), and the row outputs gate × level. Released, the
        /// row outputs 0 and <see cref="MappingRow.TrimResetOnRelease"/>
        /// decides whether the level snaps back to 100%.
        ///
        /// <para>The trim source is side-evaluated as a bipolar axis
        /// (the SourceKindRuntimeReadAxisLikeFloat pattern) because a
        /// trigger contribution folds the sign away (rest reads 0.5).
        /// SDL's stick convention reaches this read unflipped (the Y
        /// negation seams live on the stick-target writes only), so a
        /// raw stick-up is NEGATIVE and negative-raises here. A user
        /// who wants the opposite feel inverts the trim source.</para></summary>
        private static float EvaluateStickTrim(MappingRow row, int slotIndex, double dt,
            CustomInputState currentState, string currentDeviceGuid)
        {
            var srcs = SnapshotSources(row, out int srcsCount);

            // Last contributing source = trim; the rest gate.
            int trimIdx = -1;
            for (int i = srcsCount - 1; i >= 0; i--)
            {
                var s = srcs[i];
                if (s == null || IsRowModifierSource(s)) continue;
                trimIdx = i;
                break;
            }

            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;

            float gate = 0f;
            for (int i = 0; i < srcsCount; i++)
            {
                if (i == trimIdx) continue;
                var src = srcs[i];
                if (src == null || IsRowModifierSource(src)) continue;
                if (IsSourceSuppressedPostpone(slotIndex, src.DeviceGuid, src.Descriptor)) continue;
                // Empty DeviceGuid = "the device currently being evaluated"
                // (the documented MappingSource.DeviceGuid contract, same
                // resolution the single-source per-target evaluators use).
                var devState = string.IsNullOrEmpty(src.DeviceGuid)
                    ? currentState : LookupDeviceState(src.DeviceGuid);
                if (devState == null) continue;
                float v = SourceEvaluator.EvaluateForTriggerTarget(
                    devState, src, slotIndex, row.Target, i, slotRuntime, dt,
                    evaluatedDeviceGuid: currentDeviceGuid);
                if (v > gate) gate = v;
            }

            var key = (slotIndex, row.Target ?? "", row.LayerMask ?? "Base");
            var st = _stickTrimStates.GetOrAdd(key, _ => new StickTrimState());

            // Second per-device pass in the same frame: replay the
            // frame's output instead of advancing the level again.
            long seq = _stickTrimFrameSeq;
            if (st.LastSeq == seq) return st.LastOutput;

            bool held = gate > 0.05f;
            if (held && trimIdx >= 0)
            {
                var trimSrc = srcs[trimIdx];
                var trimState = string.IsNullOrEmpty(trimSrc.DeviceGuid)
                    ? currentState : LookupDeviceState(trimSrc.DeviceGuid);
                if (trimState != null
                    && !IsSourceSuppressedPostpone(slotIndex, trimSrc.DeviceGuid, trimSrc.Descriptor))
                {
                    float v = SourceEvaluator.EvaluateForBipolarAxisTarget(
                        trimState, trimSrc, slotIndex, row.Target, trimIdx, slotRuntime, dt,
                        evaluatedDeviceGuid: currentDeviceGuid);
                    st.Level = AdvanceStickTrimLevel(
                        st.Level, v, row.TrimDeadzone, row.TrimRate, dt);
                }
            }
            else if (!held && st.WasHeld && row.TrimResetOnRelease)
            {
                st.Level = 1f;
            }
            st.WasHeld = held;

            float output = held ? gate * st.Level : 0f;
            st.LastSeq = seq;
            st.LastOutput = output;
            return output;
        }

        /// <summary>Pure stick-trim level step (#155). Deflection at or
        /// below the deadzone leaves the level alone; past it, speed
        /// rescales from zero at the deadzone edge to the full
        /// <paramref name="ratePct"/> (percent of range per second) at
        /// full deflection. Negative trim (a raw SDL stick pushed up)
        /// raises the level. Clamped to [0, 1].</summary>
        internal static float AdvanceStickTrimLevel(
            float level, float trimValue, int deadzonePct, int ratePct, double dt)
        {
            float dz = System.Math.Clamp(deadzonePct, 0, 95) / 100f;
            float mag = System.Math.Abs(trimValue);
            if (mag <= dz) return level;
            float eff = (mag - dz) / (1f - dz) * System.Math.Sign(trimValue);
            float rate = System.Math.Max(1, ratePct) / 100f;
            return System.Math.Clamp(level + (-eff) * rate * (float)dt, 0f, 1f);
        }

        /// <summary>The per-slot source-kind runtime, for the steering lock-feedback
        /// pass (reads the at-lock edge / approach the steering ticks recorded).</summary>
        internal static SourceKindRuntime GetSlotSourceKindRuntime(int slot)
            => (slot >= 0 && slot < _slotSourceKindRuntime.Length) ? _slotSourceKindRuntime[slot] : null;

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
            _stickTrimFrameSeq++;
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
            public long[] EngageStartTicks = System.Array.Empty<long>(); // v2 Delay debounce
            // v3 Cycle (#119): per-activator cursor (0 = Base, 1..N index the
            // queued layers) and the Previous-button down latch. The Next button
            // uses the shared WasDown latch; Previous gets its own.
            public int[] CycleIndex = System.Array.Empty<int>();
            public bool[] CyclePrevWasDown = System.Array.Empty<bool>();
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
            // #206 long-press: per-activator once-per-hold latch. With
            // DelayMs > 0 the edge modes (Toggle / Latch / Sticky) fire
            // when the hold crosses the threshold, and this keeps the
            // continued hold from re-firing every frame. Cleared on
            // release.
            public bool[] LongPressFired = System.Array.Empty<bool>();
            // v7 double-press gate (translator v25): raw-input latch for
            // rising-edge detection (WasDown stores the GATED read for
            // DoublePressMs activators, so the raw edge needs its own
            // latch), the first press's anchor timestamp, and whether the
            // second press of a qualifying pair is currently held.
            public bool[] DoublePressRawWasDown = System.Array.Empty<bool>();
            public long[] DoublePressAnchorTicks = System.Array.Empty<long>();
            public bool[] DoublePressActive = System.Array.Empty<bool>();
            // #206 auto-cancel: last tick the engaged layer showed
            // activity (stamped at engage, refreshed from
            // LayerOutputTicks). Only meaningful while ToggleOn and
            // AutoCancelMs > 0.
            public long[] AutoCancelLastActivityTicks = System.Array.Empty<long>();
            // v6 release linger (translator v22): Hold mode's pending
            // disengage deadline (UTC ticks). While the input is engaged
            // the deadline is pushed forward; after release the layer
            // stays engaged until the deadline passes, and a re-press
            // pushes it forward again (cancel-on-re-press). Only
            // meaningful when ReleaseDelayMs > 0.
            public long[] HoldLingerUntilTicks = System.Array.Empty<long>();
            // #206 auto-cancel: per-layer last-output-activity ticks,
            // written by StampLayerActivity from the row write sites on
            // the polling thread and read by the Toggle auto-cancel
            // block on the same thread.
            // ConcurrentDictionary because Clear() runs on the UI thread
            // (ApplyProfile / the 30 Hz per-app auto-switch) while the
            // polling thread stamps, the same reason _stickTrimStates is
            // concurrent.
            public readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> LayerOutputTicks =
                new(System.StringComparer.Ordinal);
            public readonly List<int> Stack = new();
            public string CustomLayer = "";   // v2 Custom mode current layer (overrides stack when non-empty)
            // Bumped (under SyncRoot) on every Stack/CustomLayer mutation.
            // GetEngagedLayerMask's memo keys on it so the ~110 per-target
            // row lookups per KBM device pass skip the lock while the
            // engaged state is unchanged, with EXACT invalidation (no
            // one-tick staleness on activator edges).
            public int Version;

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
                EngageStartTicks = ResizeLong(EngageStartTicks, newSize);
                CycleIndex = ResizeInt(CycleIndex, newSize);
                CyclePrevWasDown = ResizeBool(CyclePrevWasDown, newSize);
                CycleLayersSplit = ResizeStringArrays(CycleLayersSplit, newSize);
                CycleLayersSource = ResizeStringArr(CycleLayersSource, newSize);
                StickyEngaged = ResizeBool(StickyEngaged, newSize);
                StickyConsumerActive = ResizeBool(StickyConsumerActive, newSize);
                StickyBaselines = ResizeStickyBaselines(StickyBaselines, newSize);
                LongPressFired = ResizeBool(LongPressFired, newSize);
                AutoCancelLastActivityTicks = ResizeLong(AutoCancelLastActivityTicks, newSize);
                HoldLingerUntilTicks = ResizeLong(HoldLingerUntilTicks, newSize);
                DoublePressRawWasDown = ResizeBool(DoublePressRawWasDown, newSize);
                DoublePressAnchorTicks = ResizeLong(DoublePressAnchorTicks, newSize);
                DoublePressActive = ResizeBool(DoublePressActive, newSize);
            }

            public void Clear()
            {
                System.Array.Clear(WasDown, 0, WasDown.Length);
                System.Array.Clear(ToggleOn, 0, ToggleOn.Length);
                System.Array.Clear(EngageStartTicks, 0, EngageStartTicks.Length);
                System.Array.Clear(CyclePrevWasDown, 0, CyclePrevWasDown.Length);
                System.Array.Clear(CycleLayersSplit, 0, CycleLayersSplit.Length);
                System.Array.Clear(CycleLayersSource, 0, CycleLayersSource.Length);
                System.Array.Clear(StickyEngaged, 0, StickyEngaged.Length);
                System.Array.Clear(StickyConsumerActive, 0, StickyConsumerActive.Length);
                System.Array.Clear(StickyBaselines, 0, StickyBaselines.Length);
                System.Array.Clear(LongPressFired, 0, LongPressFired.Length);
                System.Array.Clear(AutoCancelLastActivityTicks, 0, AutoCancelLastActivityTicks.Length);
                System.Array.Clear(HoldLingerUntilTicks, 0, HoldLingerUntilTicks.Length);
                System.Array.Clear(DoublePressRawWasDown, 0, DoublePressRawWasDown.Length);
                System.Array.Clear(DoublePressAnchorTicks, 0, DoublePressAnchorTicks.Length);
                System.Array.Clear(DoublePressActive, 0, DoublePressActive.Length);
                LayerOutputTicks.Clear();
                lock (SyncRoot)
                {
                    Stack.Clear();
                    CustomLayer = "";
                    // CycleIndex is read/written under SyncRoot in the Cycle
                    // case (alongside CustomLayer), so clear it under the lock.
                    System.Array.Clear(CycleIndex, 0, CycleIndex.Length);
                    Version++;
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
        private static readonly System.Collections.Generic.HashSet<(string Guid, string Desc)>[]
            _suppressedSourcesBySlot = new System.Collections.Generic.HashSet<(string Guid, string Desc)>[MaxPads];

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
            string canon = null;
            var set = _suppressedSourcesBySlot[slotIndex];
            if (set != null && set.Count != 0)
            {
                // Key shape mirrors the population loop below.
                // Tuple key: no per-call string concat on the 1 kHz path
                // (this runs per source per row per tick while an activator
                // is held). Both member strings already exist.
                canon = CanonicalPostponeDescriptor(descriptor);
                if (set.Contains((deviceGuid ?? "", canon))) return true;
            }
            // Consume-armed macro triggers (2026-07-25) ride the same
            // seam: a raw-button / descriptor trigger source that a
            // consume-on macro is currently eating reads as released in
            // every row evaluator, exactly like a postponed activator.
            var consumed = _consumedTriggerSourcesBySlot[slotIndex];
            if (consumed != null && consumed.Count != 0)
            {
                canon ??= CanonicalPostponeDescriptor(descriptor);
                if (consumed.Contains((deviceGuid ?? "", canon))) return true;
            }
            return false;
        }

        /// <summary>Folds a "Gamepad ..." alias to the canonical per-device
        /// descriptor (the public shape of Engine's internal
        /// CanonicalDescriptor) so the suppression key and its lookup agree
        /// when the activator and a source row spell the same control
        /// differently ("Gamepad ButtonBack" vs "Button 6"). Non-alias
        /// descriptors pass through unchanged, with no allocation, because
        /// this runs per row per frame on the poll thread.</summary>
        private sealed class PostponeKeyComparer
            : System.Collections.Generic.IEqualityComparer<(string Guid, string Desc)>
        {
            public static readonly PostponeKeyComparer Instance = new();
            public bool Equals((string Guid, string Desc) x, (string Guid, string Desc) y) =>
                string.Equals(x.Guid, y.Guid, System.StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Desc, y.Desc, System.StringComparison.OrdinalIgnoreCase);
            public int GetHashCode((string Guid, string Desc) k) =>
                System.StringComparer.OrdinalIgnoreCase.GetHashCode(k.Guid ?? "")
                ^ (System.StringComparer.OrdinalIgnoreCase.GetHashCode(k.Desc ?? "") * 397);
        }

        private static string CanonicalPostponeDescriptor(string descriptor)
        {
            if (SourceCoercion.IsGamepadAliasDescriptor(descriptor))
            {
                string resolved = SourceCoercion.ResolveGamepadAlias(descriptor);
                if (!string.IsNullOrEmpty(resolved)) return resolved;
            }
            return descriptor ?? "";
        }

        // ── Consume for raw-button / descriptor macro triggers ──
        //
        // "Consume Trigger Buttons" historically worked only for
        // virtual-button (Xbox bitmask) triggers: Step 4b strips those
        // from the combined Gamepad after the macro fires. Raw
        // device-button and descriptor triggers had nothing to strip
        // there (the press lives on the DEVICE; its effect on the output
        // goes through the mapping rows), so the checkbox was silently
        // inert for them from the day raw triggers shipped (ad77addb,
        // owner report 2026-07-25). The fix suppresses at the SOURCE
        // READ: while a consume-on macro's raw/descriptor trigger is
        // physically active (and its layer gate open), the matching
        // mapping sources on the macro's slot read as released. Step 3
        // runs before Step 4b in the same tick, so the first pressed
        // tick is already suppressed at any poll rate, and layers,
        // combine modes, and curves all see an ordinary released
        // control. The macro's own trigger read is unaffected:
        // CheckRawButtonTrigger / CheckDescriptorTrigger read the
        // device's raw InputState, not the mapping rows. The empty-guid
        // key is added alongside the concrete one so "(Any device)" rows
        // are eaten too. The cost on a multi-device slot: the same
        // control's any-device row pauses for the OTHER pads while the
        // trigger is held. Consume-means-consume beats leaking the
        // mapped output. Axis-threshold, POV, and gesture triggers stay
        // unconsumed
        // (the option is named for buttons; the virtual path never
        // consumed axes either).
        private static readonly System.Collections.Generic.HashSet<(string Guid, string Desc)>[]
            _consumedTriggerSourcesBySlot =
                new System.Collections.Generic.HashSet<(string Guid, string Desc)>[MaxPads];

        // "Button N" descriptor strings, lazily interned so the per-tick
        // rebuild allocates nothing after first use. Poll thread only.
        private static string[] _consumeButtonDescCache = new string[64];

        private static string ConsumeButtonDesc(int n)
        {
            var arr = _consumeButtonDescCache;
            if (n >= arr.Length)
            {
                var na = new string[System.Math.Max(n + 1, arr.Length * 2)];
                System.Array.Copy(arr, na, arr.Length);
                _consumeButtonDescCache = na;
                arr = na;
            }
            return arr[n] ??= "Button " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Rebuilds the per-slot consumed-trigger-source keys for
        /// this tick. Runs at the top of Step 3, before any row
        /// evaluation, on the poll thread. Internal for the
        /// PadForge.Tests consume pins.</summary>
        internal void RebuildConsumedTriggerSources()
        {
            for (int slot = 0; slot < MaxPads; slot++)
            {
                var macros = MacroSnapshots[slot];
                _consumedTriggerSourcesBySlot[slot]?.Clear();
                if (macros == null || macros.Length == 0) continue;
                for (int m = 0; m < macros.Length; m++)
                {
                    var macro = macros[m];
                    if (macro == null || !macro.IsEnabled || !macro.ConsumeTriggerButtons) continue;
                    if (!macro.UsesRawTrigger && !macro.UsesDescriptorTrigger) continue;
                    if (!MacroLayerGateOpen(macro)) continue;
                    var entries = macro.GetTriggerInputEntries();
                    if (entries.Count > 0)
                    {
                        for (int i = 0; i < entries.Count; i++)
                        {
                            var e = entries[i];
                            if (e.RawButton >= 0)
                                AddConsumedRawButton(slot, e.DeviceGuid, e.RawButton);
                            else if (e.DescriptorSource != null)
                                AddConsumedDescriptor(slot, e.DeviceGuid, e.DescriptorSource);
                        }
                    }
                    else if (macro.UsesRawTrigger && macro.TriggerDeviceGuid != System.Guid.Empty)
                    {
                        // Legacy single-device raw-button macros
                        // (pre-multi-device saves).
                        var raws = macro.TriggerRawButtons;
                        if (raws == null) continue;
                        for (int i = 0; i < raws.Length; i++)
                            if (raws[i] >= 0)
                                AddConsumedRawButton(slot, macro.TriggerDeviceGuid, raws[i]);
                    }
                }
            }
        }

        private void AddConsumedRawButton(int slot, System.Guid deviceGuid, int rawButton)
        {
            string desc = ConsumeButtonDesc(rawButton);
            if (deviceGuid == System.Guid.Empty)
            {
                // Device-free entry: any online slot device holding the button.
                int n = EnsureSlotTriggerDevices(slot);
                for (int d = 0; d < n; d++)
                {
                    var ud = _slotTriggerDeviceScratch[d];
                    var btns = ud?.InputState?.Buttons;
                    if (btns == null || rawButton >= btns.Length || !btns[rawButton]) continue;
                    AddConsumedKey(slot, ud.InstanceGuidString, desc);
                    AddConsumedKey(slot, "", desc);
                }
                return;
            }
            var udc = FindSlotDeviceByInstanceGuid(deviceGuid, slot);
            var b = udc?.InputState?.Buttons;
            if (udc == null || !udc.IsOnline || b == null) return;
            if (rawButton < b.Length && b[rawButton])
            {
                AddConsumedKey(slot, udc.InstanceGuidString, desc);
                AddConsumedKey(slot, "", desc);
            }
        }

        private void AddConsumedDescriptor(int slot, System.Guid deviceGuid, PadForge.Engine.Data.MappingSource src)
        {
            string desc = CanonicalPostponeDescriptor(src.Descriptor);
            if (string.IsNullOrEmpty(desc)) return;
            if (deviceGuid == System.Guid.Empty)
            {
                int n = EnsureSlotTriggerDevices(slot);
                for (int d = 0; d < n; d++)
                {
                    var ud = _slotTriggerDeviceScratch[d];
                    if (ud?.InputState == null) continue;
                    if (!SourceCoercion.EvaluateForButtonTarget(
                            ud.InputState, src, DescriptorTriggerThresholdPercent,
                            slot, ud.InstanceGuidString))
                        continue;
                    AddConsumedKey(slot, ud.InstanceGuidString, desc);
                    AddConsumedKey(slot, "", desc);
                }
                return;
            }
            var udc = FindSlotDeviceByInstanceGuid(deviceGuid, slot);
            if (udc == null || !udc.IsOnline || udc.InputState == null) return;
            if (SourceCoercion.EvaluateForButtonTarget(
                    udc.InputState, src, DescriptorTriggerThresholdPercent,
                    slot, udc.InstanceGuidString))
            {
                AddConsumedKey(slot, udc.InstanceGuidString, desc);
                AddConsumedKey(slot, "", desc);
            }
        }

        private static void AddConsumedKey(int slot, string deviceGuid, string desc)
        {
            (_consumedTriggerSourcesBySlot[slot]
                ??= new System.Collections.Generic.HashSet<(string Guid, string Desc)>(PostponeKeyComparer.Instance))
                .Add((deviceGuid ?? "", desc));
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
        /// <summary>Immutable memo of one resolved mask. The KBM/Extended
        /// path resolves the mask once per TARGET (~110 lock round-trips
        /// per device pass at 1 kHz); the engaged state changes at human
        /// cadence. Validity = same MappingSet identity + same runtime
        /// <see cref="ShiftRuntime.Version"/>, and (for stack outcomes)
        /// the winning activator object and its LayerMask string are
        /// reference-unchanged, so an in-place activator edit in the UI
        /// invalidates without needing a Version bump.</summary>
        private sealed class EngagedMaskMemo
        {
            public MappingSet Set;
            public int Version;
            public int ActivatorIdx = -1;     // -1: CustomLayer/Base outcome
            public ShiftActivator Activator;
            public string MaskSource;         // Activator.LayerMask ref at compute
            public string Mask;
        }
        private static readonly EngagedMaskMemo[] _engagedMaskMemos = new EngagedMaskMemo[MaxPads];

        public static string GetEngagedLayerMask(int slotIndex, MappingSet mappingSet)
        {
            if (slotIndex < 0 || slotIndex >= _shiftRuntime.Length) return "Base";
            var rt = _shiftRuntime[slotIndex];
            if (rt == null) return "Base";

            // Lock-free fast path while nothing changed. Version is only
            // written under SyncRoot; a torn read is impossible for an int
            // and a stale read merely falls through to the locked path.
            var memo = System.Threading.Volatile.Read(ref _engagedMaskMemos[slotIndex]);
            if (memo != null
                && ReferenceEquals(memo.Set, mappingSet)
                && memo.Version == System.Threading.Volatile.Read(ref rt.Version))
            {
                if (memo.ActivatorIdx < 0) return memo.Mask;
                var acts = mappingSet?.ShiftActivators;
                if (acts != null && memo.ActivatorIdx < acts.Count
                    && ReferenceEquals(acts[memo.ActivatorIdx], memo.Activator)
                    && ReferenceEquals(memo.Activator?.LayerMask, memo.MaskSource))
                    return memo.Mask;
            }

            // Snapshot under the runtime's lock so the polling thread's
            // concurrent Stack / CustomLayer mutations can't trip the
            // indexer with a stale Count.
            string customLayer;
            int idx;
            int version;
            lock (rt.SyncRoot)
            {
                customLayer = rt.CustomLayer;
                idx = rt.Stack.Count > 0 ? rt.Stack[rt.Stack.Count - 1] : -1;
                version = rt.Version;
            }

            string mask;
            var fresh = new EngagedMaskMemo { Set = mappingSet, Version = version };
            if (!string.IsNullOrEmpty(customLayer))
            {
                mask = customLayer;
            }
            else
            {
                var activators = mappingSet?.ShiftActivators;
                if (activators == null || idx < 0 || idx >= activators.Count)
                {
                    mask = "Base";
                }
                else
                {
                    var act = activators[idx];
                    mask = act?.LayerMask ?? "Base";
                    fresh.ActivatorIdx = idx;
                    fresh.Activator = act;
                    fresh.MaskSource = act?.LayerMask;
                }
            }
            fresh.Mask = mask;
            System.Threading.Volatile.Write(ref _engagedMaskMemos[slotIndex], fresh);
            return mask;
        }

        /// <summary>Resolves the active shift-layer mask for a slot.
        /// Walks <see cref="MappingSet.ShiftActivators"/>, updates engaged
        /// state for activators owned by <paramref name="thisDeviceGuid"/>,
        /// and returns the last-engaged layer's <see cref="ShiftActivator.LayerMask"/>.
        /// Returns <c>"Base"</c> when nothing is engaged.</summary>
        internal static string ResolveActiveLayerMask(
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
                    new System.Collections.Generic.HashSet<(string Guid, string Desc)>(PostponeKeyComparer.Instance);
            suppressed.Clear();
            for (int i = 0; i < activators.Count; i++)
            {
                var a = activators[i];
                if (a == null) continue;
                if (a.PostponeMapping) continue;
                // Cycle suppresses each of its two buttons by its own latch
                // (Next via WasDown, Previous via CyclePrevWasDown), so a press
                // that steps the queue doesn't also fire the button's mapping.
                if (string.Equals(a.Mode, "Cycle", System.StringComparison.Ordinal))
                {
                    if (rt.WasDown[i] && !string.IsNullOrEmpty(a.Descriptor))
                        suppressed.Add((a.DeviceGuid ?? "", CanonicalPostponeDescriptor(a.Descriptor)));
                    if (rt.CyclePrevWasDown[i] && !string.IsNullOrEmpty(a.CyclePrevDescriptor))
                        suppressed.Add((a.CyclePrevDeviceGuid ?? "", CanonicalPostponeDescriptor(a.CyclePrevDescriptor)));
                    continue;
                }
                if (!rt.WasDown[i]) continue;
                if (!string.IsNullOrEmpty(a.Descriptor))
                    suppressed.Add((a.DeviceGuid ?? "", CanonicalPostponeDescriptor(a.Descriptor)));
                if (string.Equals(a.Kind, "Chord", System.StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(a.ChordSecondDescriptor))
                {
                    suppressed.Add((a.ChordSecondDeviceGuid ?? "", CanonicalPostponeDescriptor(a.ChordSecondDescriptor)));
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
            bool inputDown = ReadActivatorInput(act, state, slotIndex);

            // ── v7 double-press gate (translator v25): when DoublePressMs
            //    is set, the input counts as engaged only during the
            //    SECOND press of a press-release-press pair inside the
            //    window (the macro engine's DoublePress contract:
            //    press, release, press; a slow second press re-arms as a
            //    fresh first; a completed pair is consumed). Every mode
            //    below then sees the gated read: Hold holds through the
            //    second press, the edge modes fire on its rising edge. ──
            if (act.DoublePressMs > 0)
            {
                long dpNow = System.DateTime.UtcNow.Ticks;
                bool rawRising = inputDown && !rt.DoublePressRawWasDown[actIdx];
                if (rawRising)
                {
                    long anchor = rt.DoublePressAnchorTicks[actIdx];
                    long windowTicks = act.DoublePressMs * System.TimeSpan.TicksPerMillisecond;
                    if (anchor != 0 && dpNow - anchor <= windowTicks)
                    {
                        rt.DoublePressActive[actIdx] = true;
                        rt.DoublePressAnchorTicks[actIdx] = 0; // consume the pair
                    }
                    else
                    {
                        rt.DoublePressAnchorTicks[actIdx] = dpNow; // fresh first press
                    }
                }
                rt.DoublePressRawWasDown[actIdx] = inputDown;
                if (!inputDown) rt.DoublePressActive[actIdx] = false;
                inputDown = rt.DoublePressActive[actIdx];
            }

            // ── v2 Delay before Jump: gate transitions until the input
            //    has been continuously down for DelayMs ──
            long nowTicks = System.DateTime.UtcNow.Ticks;
            if (inputDown && !rt.WasDown[actIdx])
                rt.EngageStartTicks[actIdx] = nowTicks;
            long heldMs = inputDown
                ? (nowTicks - rt.EngageStartTicks[actIdx]) / System.TimeSpan.TicksPerMillisecond
                : 0;
            bool delayMet = act.DelayMs <= 0 || !inputDown || heldMs >= act.DelayMs;

            // #206 long-press: the edge modes fire once when the hold
            // crosses DelayMs. The old rising-edge && delayMet gate was
            // dead code for DelayMs > 0 (the edge frame can never satisfy
            // the delay, and later frames are no longer edges), so Toggle
            // / Latch / Sticky with a delay simply never fired.
            bool fireEdge = ComputeActivatorFire(
                inputDown, rt.WasDown[actIdx], heldMs, act.DelayMs,
                ref rt.LongPressFired[actIdx]);

            string mode = act.Mode ?? "Hold";
            switch (mode)
            {
                case "Toggle":
                {
                    bool justEngaged = false;
                    if (fireEdge)
                    {
                        rt.ToggleOn[actIdx] = !rt.ToggleOn[actIdx];
                        justEngaged = rt.ToggleOn[actIdx];
                    }

                    // #206 auto-cancel: while engaged, disengage after
                    // AutoCancelMs with none of the layer's own rows
                    // producing output. Output stamps (StampLayerActivity
                    // at the row write sites) carry every source kind and
                    // flag already applied, where a descriptor-level
                    // re-probe misread resting triggers as active and
                    // missed any-device and Param-driven sources. The
                    // timer starts at engage.
                    if (rt.ToggleOn[actIdx] && act.AutoCancelMs > 0)
                    {
                        // Seed at engage, and also when unseeded (the
                        // layer was engaged before auto-cancel was turned
                        // on, or before a runtime clear): without the
                        // zero check, the stale epoch read as an ancient
                        // last-activity and disengaged instantly.
                        if (justEngaged || rt.AutoCancelLastActivityTicks[actIdx] == 0)
                            rt.AutoCancelLastActivityTicks[actIdx] = nowTicks;

                        long last = rt.AutoCancelLastActivityTicks[actIdx];
                        if (rt.LayerOutputTicks.TryGetValue(act.LayerMask ?? "", out long stamped)
                            && stamped > last)
                        {
                            last = stamped;
                            rt.AutoCancelLastActivityTicks[actIdx] = stamped;
                        }
                        if ((nowTicks - last) / System.TimeSpan.TicksPerMillisecond >= act.AutoCancelMs)
                            rt.ToggleOn[actIdx] = false;
                    }

                    UpdateStack(rt, actIdx, rt.ToggleOn[actIdx]);
                    break;
                }
                case "Custom":   // displayed as "Latch" (#119)
                {
                    // Latch: press engages THIS activator's own layer as the
                    // active override and holds it. Press again releases back to
                    // Base. Pressing a different Latch switches the active layer
                    // (single-valued override). The own layer's mappings fire
                    // while latched. (Legacy value "Custom"; the old jump-to-a-
                    // separate-target behavior is gone, its own layer was unused.)
                    // No auto-cancel here on purpose (#206): the requester
                    // flagged auto-unlatching as surprising.
                    bool risingEdge = fireEdge;
                    if (risingEdge)
                    {
                        string own = act.LayerMask ?? "";
                        if (!string.IsNullOrEmpty(own))
                        {
                            lock (rt.SyncRoot)
                            {
                                rt.CustomLayer = string.Equals(rt.CustomLayer, own, System.StringComparison.Ordinal)
                                    ? "" : own;
                                rt.Version++;
                            }
                        }
                    }
                    break;
                }
                case "Cycle":
                {
                    // v3 (#119): one Cycle control holds the queue plus a Next
                    // button (the activator's own input -> inputDown) and a
                    // Previous button (CyclePrev*). Next steps the cursor forward
                    // through CycleLayers, Previous backward. Stepping past an end
                    // follows CycleWrap; whether Base is a stop follows
                    // CycleIncludeBase. The split list is cached per-activator
                    // (recomputed when it changes) so the tick doesn't allocate.
                    string src = act.CycleLayers ?? "";
                    if (!string.Equals(rt.CycleLayersSource[actIdx], src, System.StringComparison.Ordinal))
                    {
                        rt.CycleLayersSplit[actIdx] = src.Split('|', System.StringSplitOptions.RemoveEmptyEntries);
                        rt.CycleLayersSource[actIdx] = src;
                    }
                    var layers = rt.CycleLayersSplit[actIdx];

                    // Previous button. Read against its own device when it lives
                    // on a different controller than Next (mirrors cross-device
                    // chord via LookupDeviceState).
                    bool prevDown = false;
                    if (!string.IsNullOrEmpty(act.CyclePrevDescriptor))
                    {
                        CustomInputState prevState = state;
                        string prevGuid = act.DeviceGuid;
                        if (!string.IsNullOrEmpty(act.CyclePrevDeviceGuid)
                            && !string.Equals(act.CyclePrevDeviceGuid, act.DeviceGuid, System.StringComparison.OrdinalIgnoreCase))
                        {
                            // Offline prev device reads rest (false), never
                            // the current pass's state (wrong device).
                            prevState = LookupDeviceState(act.CyclePrevDeviceGuid) ?? OfflinePinnedRestState;
                            prevGuid = act.CyclePrevDeviceGuid;
                        }
                        // Device guid + slot ride along like the chord second
                        // read at ReadActivatorInput, so the slot-keyed source
                        // families (menu items, touchpad gestures) evaluate
                        // against THIS slot instead of slot 0.
                        prevDown = SourceKindRuntimeReadButtonLikeBool(prevState, act.CyclePrevDescriptor, prevGuid, slotIndex);
                    }

                    // Cycle steps on a press edge; Delay (a hold-to-engage
                    // debounce) doesn't apply to a press-to-step control.
                    bool nextRising = inputDown && !rt.WasDown[actIdx];
                    bool prevRising = prevDown && !rt.CyclePrevWasDown[actIdx];

                    if ((nextRising || prevRising) && layers != null && layers.Length > 0)
                    {
                        int n = layers.Length;
                        bool wrap = act.CycleWrap;
                        bool includeBase = act.CycleIncludeBase;
                        lock (rt.SyncRoot)
                        {
                            int pos = rt.CycleIndex[actIdx];
                            if (nextRising)
                                pos = PadForge.Engine.Common.ShiftCycleStepper.Step(pos, n, previous: false, wrap, includeBase);
                            if (prevRising)
                                pos = PadForge.Engine.Common.ShiftCycleStepper.Step(pos, n, previous: true, wrap, includeBase);
                            rt.CycleIndex[actIdx] = pos;
                            rt.CustomLayer = pos == 0 ? "" : layers[pos - 1];
                            rt.Version++;
                        }
                    }

                    rt.CyclePrevWasDown[actIdx] = prevDown;
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
                        if (fireEdge)
                        {
                            UpdateStack(rt, actIdx, true);
                            rt.StickyEngaged[actIdx] = true;
                            rt.StickyConsumerActive[actIdx] = false;
                            rt.StickyBaselines[actIdx] = CaptureStickyEngagementSnapshot(slotIndex);
                        }
                    }
                    break;
                }
                case "Passive":
                    // #119: a No-Button layer never self-engages. It owns a tab
                    // and mappings, reached only via a Cycle queue or Custom jump.
                    break;
                case "Hold":
                default:
                {
                    bool engaged = inputDown && delayMet;
                    // v6 release linger (translator v22, Steam delay_end on
                    // a layer switch): an engaged hold keeps pushing its
                    // disengage deadline forward; after release the layer
                    // stays engaged until the deadline passes. A re-press
                    // inside the window re-engages the plain read first, so
                    // the pending disengage is cancelled by the press (the
                    // M6 cancel-on-re-press shape on the layer machinery).
                    if (act.ReleaseDelayMs > 0)
                    {
                        if (engaged)
                        {
                            rt.HoldLingerUntilTicks[actIdx] = nowTicks
                                + act.ReleaseDelayMs * System.TimeSpan.TicksPerMillisecond;
                        }
                        else if (nowTicks < rt.HoldLingerUntilTicks[actIdx])
                        {
                            engaged = true;
                        }
                    }
                    UpdateStack(rt, actIdx, engaged);
                    break;
                }
            }

            rt.WasDown[actIdx] = inputDown;
        }

        /// <summary>Reads the input for an activator according to its
        /// <see cref="ShiftActivator.Kind"/>. Returns <c>true</c> when the
        /// activator should be considered "down" for engagement purposes.
        /// <paramref name="slotIndex"/> keys the slot-scoped source
        /// families (menu-item fires, per-(device, slot) tuning) so an
        /// activator descriptor reads the same state a mapping row on the
        /// same slot would (#9 B-17).</summary>
        private static bool ReadActivatorInput(ShiftActivator act, CustomInputState state,
            int slotIndex)
        {
            // Input-less layers (#119) are passive targets: no own button, so
            // they never self-engage and are reached only via Cycle / Custom jump.
            if (string.IsNullOrEmpty(act.Descriptor)) return false;

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
                    // recorded (same-device chord, the legacy / common case).
                    bool a = SourceKindRuntimeReadButtonLikeBool(state, act.Descriptor, act.DeviceGuid, slotIndex);
                    CustomInputState secondState = state;
                    string secondGuid = act.DeviceGuid;
                    if (!string.IsNullOrEmpty(act.ChordSecondDeviceGuid)
                        && !string.Equals(act.ChordSecondDeviceGuid, act.DeviceGuid,
                            System.StringComparison.OrdinalIgnoreCase))
                    {
                        // Offline second device reads rest (false), never
                        // the current pass's state (wrong device).
                        secondState = LookupDeviceState(act.ChordSecondDeviceGuid) ?? OfflinePinnedRestState;
                        secondGuid = act.ChordSecondDeviceGuid;
                    }
                    bool b = SourceKindRuntimeReadButtonLikeBool(secondState, act.ChordSecondDescriptor, secondGuid, slotIndex);
                    return a && b;
                }
                case "Axis":
                {
                    // v8 gate companion (translator v26): a trackpad D-pad
                    // wedge is an axis half PLUS its contact / click gate;
                    // the gate must hold or the wedge never engages.
                    if (!string.IsNullOrEmpty(act.GateDescriptor)
                        && !SourceKindRuntimeReadButtonLikeBool(state, act.GateDescriptor, act.DeviceGuid, slotIndex))
                        return false;
                    // v2: axis past threshold. ReadAxisLike returns [-1..+1].
                    float axisVal = SourceKindRuntimeReadAxisLikeFloat(state, act.Descriptor, act.DeviceGuid, slotIndex);
                    // v5 half stamp (translator v15): one signed direction
                    // engages instead of the direction-blind |axis| test, so
                    // a wedge- or gyro-hosted flick drives only its own layer.
                    if (act.AxisHalf)
                    {
                        return act.AxisInvert
                            ? axisVal <= -act.AxisThreshold
                            : axisVal >= act.AxisThreshold;
                    }
                    return System.Math.Abs(axisVal) >= act.AxisThreshold;
                }
                case "Button":
                default:
                    return SourceKindRuntimeReadButtonLikeBool(state, act.Descriptor, act.DeviceGuid, slotIndex);
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
                    {
                        rt.Stack.Add(actIdx);
                        rt.Version++;
                    }
                }
                else if (existing >= 0)
                {
                    rt.Stack.RemoveAt(existing);
                    rt.Version++;
                }
            }
        }

        /// <summary>#206 edge-mode fire decision, one place for Toggle /
        /// Latch / Sticky. DelayMs 0 keeps the classic rising edge.
        /// DelayMs &gt; 0 is a long press: fires exactly once when the hold
        /// crosses the threshold; <paramref name="longPressFired"/> is
        /// the once-per-hold latch, cleared on release. Pure so the
        /// state machine is testable frame by frame.</summary>
        internal static bool ComputeActivatorFire(
            bool inputDown, bool wasDown, long heldMs, int delayMs, ref bool longPressFired)
        {
            if (!inputDown)
            {
                longPressFired = false;
                return false;
            }
            if (delayMs <= 0)
                return !wasDown;
            if (longPressFired || heldMs < delayMs)
                return false;
            longPressFired = true;
            return true;
        }

        /// <summary>#206 auto-cancel activity stamp, called from the
        /// gamepad row write sites when a non-Base row produces output
        /// (button pressed, |axis| past 10%, trigger past 5%). Output
        /// carries every source kind and flag already applied, so this
        /// is the layer's "targets are pressed" signal with no
        /// re-derivation. Same-thread with the reader (both on the
        /// polling tick).</summary>
        private static void StampLayerActivity(int slotIndex, MappingRow row)
        {
            string layer = row?.LayerMask;
            if (string.IsNullOrEmpty(layer)
                || string.Equals(layer, "Base", System.StringComparison.Ordinal))
                return;
            if (slotIndex < 0 || slotIndex >= _shiftRuntime.Length) return;
            var rt = _shiftRuntime[slotIndex];
            if (rt == null) return;
            rt.LayerOutputTicks[layer] = System.DateTime.UtcNow.Ticks;
        }

        /// <summary>Axis read used by the Axis activator kind. Mirrors
        /// <see cref="SourceKindRuntimeReadButtonLikeBool"/> but returns the
        /// signed [-1..+1] bipolar axis value without thresholding.</summary>
        private static float SourceKindRuntimeReadAxisLikeFloat(CustomInputState state, string descriptor,
            string deviceGuid = null, int slotIndex = 0)
            => SourceEvaluator.EvaluateForBipolarAxisTarget(
                state,
                // DeviceGuid rides along so per-device engine families
                // ("IR Offscreen"'s debounce store, the IR EMA keys) never
                // collapse onto a shared empty-string key (#203 review).
                new MappingSource { Kind = "Direct", Descriptor = descriptor ?? "", DeviceGuid = deviceGuid ?? "" },
                slotIndex, "", 0, null, 0);

        // Reuses the Engine's button-like reader without going through the
        // managed-cast SourceCoercion wrapper (we already know the activator
        // is button-class). slotIndex keys the slot-scoped families (menu
        // fires, per-(device, slot) tuning) for callers that know their
        // slot (#9 B-17); legacy utility callers keep the 0 default.
        private static bool SourceKindRuntimeReadButtonLikeBool(CustomInputState state, string descriptor,
            string deviceGuid = null, int slotIndex = 0)
            => SourceEvaluator.EvaluateForButtonTarget(
                state,
                new MappingSource { Kind = "Direct", Descriptor = descriptor ?? "", DeviceGuid = deviceGuid ?? "" },
                50, slotIndex, "", 0, null, 0);

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
                    EvaluateCombinedDpad(state, row, thisDeviceGuid, slotIndex, ref gp);
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
                            row, slotIndex, globalAxisToButtonThreshold, dt, state, thisDeviceGuid);
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
                        if (combined) StampLayerActivity(slotIndex, row);
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
                            slotIndex, row.Target, i, runtime, dt,
                            evaluatedDeviceGuid: thisDeviceGuid));
                    }
                    if (boolContribs.Count == 0) continue;
                    bool singlePressed = CombineHelper.CombineButton(row.CombineMode, boolContribs);
                    if (singlePressed) StampLayerActivity(slotIndex, row);
                    WriteBoolTarget(row.Target, singlePressed, ref gp);
                }
                else if (kind == TargetKind.BipolarAxis)
                {
                    if (isMultiSource)
                    {
                        var positional = BuildCustomContribsForBipolarAxis(row, slotIndex, dt, state, thisDeviceGuid);
                        if (positional.Count == 0) continue;
                        float combined = isCustom
                            ? ClampBipolar(EvaluateCustomFloat(row, positional))
                            : ClampBipolar(CombineHelper.CombineAxis(row.CombineMode, positional));
                        if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combined = -combined;
                        if (System.Math.Abs(combined) > 0.10f) StampLayerActivity(slotIndex, row);
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
                            state, src, slotIndex, row.Target, i, runtime, dt,
                            evaluatedDeviceGuid: thisDeviceGuid));
                    }
                    if (axisContribs.Count == 0) continue;
                    float combinedSingle = ClampBipolar(CombineHelper.CombineAxis(row.CombineMode, axisContribs));
                    if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combinedSingle = -combinedSingle;
                    if (System.Math.Abs(combinedSingle) > 0.10f) StampLayerActivity(slotIndex, row);
                    WriteBipolarAxisTarget(row.Target, combinedSingle, ref gp);
                }
                else if (kind == TargetKind.Trigger)
                {
                    if (isMultiSource)
                    {
                        float combined;
                        if (row.CombineMode == "StickTrim")
                        {
                            // Stateful combine (#155): walks row.Sources
                            // itself (the trim axis needs its signed value,
                            // which positional trigger contribs fold away).
                            combined = ClampUnipolar(EvaluateStickTrim(row, slotIndex, dt, state, thisDeviceGuid));
                        }
                        else
                        {
                            var positional = BuildCustomContribsForTrigger(row, slotIndex, dt, state, thisDeviceGuid);
                            if (positional.Count == 0) continue;
                            combined = isCustom
                                ? ClampUnipolar(EvaluateCustomFloat(row, positional))
                                : ClampUnipolar(CombineHelper.CombineAxis(row.CombineMode, positional));
                        }
                        if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combined = 1f - combined;
                        if (combined > 0.05f) StampLayerActivity(slotIndex, row);
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
                            state, src, slotIndex, row.Target, i, runtime, dt,
                            evaluatedDeviceGuid: thisDeviceGuid));
                    }
                    if (axisContribs.Count == 0) continue;
                    float combinedTrig = ClampUnipolar(CombineHelper.CombineAxis(row.CombineMode, axisContribs));
                    if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combinedTrig = 1f - combinedTrig;
                    if (combinedTrig > 0.05f) StampLayerActivity(slotIndex, row);
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
        [System.ThreadStatic] private static List<CustomInputState> _slotDeviceStatesBuf;
        [System.ThreadStatic] private static List<string> _slotDeviceGuidsBuf;

        /// <summary>Online input states of every device assigned to a slot.
        /// A multi-source row is evaluated ONCE per slot (the
        /// <see cref="_multiSourceEvaluatedTargetsBySlot"/> de-dup), so an
        /// empty-guid ("any device") source inside it must be read across ALL
        /// of the slot's devices, not only the one device that happened to be
        /// evaluated first. Without this, a slot shared by two controllers
        /// dropped every multi-source row's input from whichever device was
        /// not first in the loop (owner report 2026-07-14: an imported profile
        /// OR-ing a paddle / touchpad-click with a face-button passthrough on a
        /// slot holding both a Steam Controller and an Xbox pad: the Xbox pad
        /// claimed the rows and read them from itself, so the Steam Controller's
        /// presses never fired). The single-source path never hit this because
        /// it evaluates per-device and Step 4 OR-merges across the slot.
        /// <para>Lock order: GUIDs are collected under UserSettings.SyncRoot,
        /// which is released before LookupDeviceState takes UserDevices.SyncRoot,
        /// so the documented UserDevices-before-UserSettings order is never
        /// inverted.</para>
        /// <para>Returns the shared ThreadStatic buffer; callers must consume it
        /// before the next call. Falls back to [currentState] when the slot has
        /// no enumerable devices (utility / preview callers pass slotIndex -1).</para></summary>
        private static List<CustomInputState> GetSlotDeviceStates(
            int slotIndex, CustomInputState currentState, string currentDeviceGuid)
        {
            var result = _slotDeviceStatesBuf ??= new List<CustomInputState>(4);
            result.Clear();
            var guids = _slotDeviceGuidsBuf ??= new List<string>(4);
            guids.Clear();
            var settings = SettingsManager.UserSettings;
            if (slotIndex >= 0 && settings?.Items != null)
            {
                lock (settings.SyncRoot)
                {
                    for (int i = 0; i < settings.Items.Count; i++)
                    {
                        var us = settings.Items[i];
                        if (us == null || us.MapTo != slotIndex) continue;
                        // Cached string (UserSetting memoizes it): the previous
                        // Guid.ToString() here allocated once per any-device row
                        // per assigned device per 1 kHz tick.
                        guids.Add(us.InstanceGuidString);
                    }
                }
            }
            for (int i = 0; i < guids.Count; i++)
            {
                var g = guids[i];
                var st = (currentDeviceGuid != null
                          && string.Equals(g, currentDeviceGuid, System.StringComparison.OrdinalIgnoreCase))
                    ? currentState : LookupDeviceState(g);
                if (st != null && !result.Contains(st)) result.Add(st);
            }
            if (result.Count == 0 && currentState != null) result.Add(currentState);
            return result;
        }
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
        /// <summary>Per-MappingSet base-row index (weak-keyed so replaced
        /// sets collect). The prior single-entry thread-static cache
        /// thrashed a full rebuild per device pass whenever two or more
        /// non-gamepad slots interleaved in UserSettings order.</summary>
        private sealed class BaseRowCache
        {
            public int Count = -1;
            public readonly Dictionary<string, MappingRow> Rows =
                new(64, System.StringComparer.Ordinal);
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MappingSet, BaseRowCache>
            s_baseRowCaches = new();

        private static bool SourceMatchesDevice(MappingSource src, string thisDeviceGuid)
        {
            if (src == null) return false;
            if (string.IsNullOrEmpty(src.DeviceGuid)) return true; // "any device"
            return string.Equals(src.DeviceGuid, thisDeviceGuid, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Per-device trigger value (0-65535) for the Triggers-tab live preview.
        /// Evaluates only <paramref name="deviceGuid"/>'s own sources on the
        /// target row, so the preview reflects the selected device's own
        /// contribution even when that source is the secondary entry on a
        /// multi-source row.
        ///
        /// <para>Why this is separate from <c>RawMappedState</c>: a multi-source
        /// row is evaluated once per slot in <see cref="ApplyMappingSetToGamepad"/>
        /// (the <c>_multiSourceEvaluatedTargetsBySlot</c> de-dup), so its combined
        /// value lands on only the first-evaluated device's <c>RawMappedState</c>.
        /// Every other device's snapshot reads 0, which blanked the Triggers-tab
        /// preview whenever the selected device was a secondary source on a
        /// LeftTrigger/RightTrigger row. This re-evaluates per device instead.</para>
        ///
        /// <para>Runtime is passed null and dt 0 on purpose: Direct sources (the
        /// axis triggers this previews) evaluate statelessly, while Incremental /
        /// Ramped sources short-circuit to 0 (see
        /// <see cref="SourceEvaluator.EvaluateForTriggerTarget"/>) without ticking
        /// the live slot accumulators the polling thread owns, so calling this from
        /// the UI refresh can never double-advance them. No shared static buffers
        /// are touched and the row walk is mutation-guarded, so it is safe to call
        /// off the polling thread.</para>
        /// </summary>
        internal static ushort EvaluatePerDeviceTriggerPreview(
            CustomInputState state, MappingSet mappingSet, string deviceGuid, string target, int slotIndex)
        {
            if (state == null || mappingSet == null) return 0;
            try
            {
                var rows = mappingSet.Rows;
                if (rows == null) return 0;
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    if (row == null || row.Sources == null) continue;
                    if (!string.Equals(row.Target, target, System.StringComparison.Ordinal)) continue;

                    if (string.Equals(row.CombineMode, "StickTrim", System.StringComparison.Ordinal))
                    {
                        // Stateful mode (#155): mirror the live output the
                        // polling thread computed this frame instead of
                        // re-deriving. A re-derive here would fold the trim
                        // stick's sign away and preview ~50% pull at rest.
                        // Read-only on the state entry, so still safe off
                        // the polling thread. Only while the row is truly
                        // multi-source: degraded to one source, the engine
                        // takes the single-source path and the state entry
                        // freezes, so the normal preview below is the
                        // accurate one.
                        int contributing = 0;
                        for (int ci = 0; ci < row.Sources.Count; ci++)
                            if (row.Sources[ci] != null && !IsRowModifierSource(row.Sources[ci]))
                                contributing++;
                        if (contributing >= 2)
                        {
                            return _stickTrimStates.TryGetValue(
                                (slotIndex, row.Target ?? "", row.LayerMask ?? "Base"), out var trimSt)
                                ? (ushort)System.Math.Clamp((int)(trimSt.LastOutput * 65535f), 0, 65535)
                                : (ushort)0;
                        }
                    }

                    List<float> contribs = null;
                    for (int i = 0; i < row.Sources.Count; i++)
                    {
                        var src = row.Sources[i];
                        if (IsRowModifierSource(src)) continue;
                        if (!SourceMatchesDevice(src, deviceGuid)) continue;
                        (contribs ??= new List<float>(row.Sources.Count)).Add(
                            SourceEvaluator.EvaluateForTriggerTarget(state, src, slotIndex, target, i, null, 0,
                                evaluatedDeviceGuid: deviceGuid));
                    }
                    if (contribs == null || contribs.Count == 0) continue; // no source for this device on this row

                    float combined = ClampUnipolar(CombineHelper.CombineAxis(row.CombineMode, contribs));
                    return (ushort)System.Math.Clamp((int)(combined * 65535f), 0, 65535);
                }
            }
            catch
            {
                // Best-effort preview. A concurrent save-path mutation of Rows /
                // Sources resolves on the next frame; never throw into the UI update.
            }
            return 0;
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

        // Step-3-pass-scoped memo for LookupDeviceState. Multi-source rows
        // resolve the same device GUIDs once per source per tick, and at the
        // ~1 kHz poll rate that meant hundreds of UserDevices.SyncRoot
        // acquisitions per second CONTENDING with the 33 ms UI publisher's
        // long dashboard critical section (profiled: Monitor.Enter_Slowpath
        // inside this function on the poll thread).
        //
        // Scope rule: the memo participates ONLY between BeginDeviceStateMemo
        // and EndDeviceStateMemo, which UpdateOutputStates brackets around one
        // Step-3 pass. Every OTHER caller (tests, UI preview at 30 Hz,
        // utility evaluators) takes the always-live locked scan exactly as
        // before, so no semantic changes exist off the poll path. Wall-clock
        // and generation scopes were both tried first and failed a
        // multi-source test: they leaked one pass's states into the next
        // caller's evaluation.
        //
        // The memo stores the UserDevice REFERENCE, not its InputState:
        // IsOnline and InputState are re-read live on every hit. Web/Remote
        // disconnect callbacks run MarkDeviceOffline off the poll thread
        // (clears IsOnline + InputState, neutralizes OutputState), and a
        // memoized snapshot could resurrect a held button for the rest of
        // the pass on a shared slot (Codex audit 2026-07-16). The per-hit
        // re-read restores the baseline scan's per-row freshness while
        // still taking UserDevices.SyncRoot only once per GUID per pass.
        // Negative results (no such device) are memoed too, which is the
        // offline-contributes-zero contract; a device REGISTERED mid-pass
        // publishes its first InputState at the next Step 2 anyway.
        [System.ThreadStatic] private static Dictionary<string, UserDevice> _devStateMemo;
        [System.ThreadStatic] private static bool _devStateMemoActive;

        /// <summary>Arms (and clears) the per-pass device-state memo on this
        /// thread. Only the poll thread calls this, once per Step-3 pass; the
        /// clear-on-arm makes a pass that aborted mid-way harmless, because
        /// the next pass never sees its entries, and no other thread ever
        /// arms its own flag.</summary>
        internal static void BeginDeviceStateMemo()
        {
            var memo = _devStateMemo ??= new Dictionary<string, UserDevice>(System.StringComparer.OrdinalIgnoreCase);
            memo.Clear();
            _devStateMemoActive = true;
        }

        /// <summary>Disarms the memo at the end of a Step-3 pass so any code
        /// running later on the same thread (Step 4 macros, SOCD, tests
        /// calling eval helpers directly) takes the always-live locked scan
        /// instead of finished-pass entries.</summary>
        internal static void EndDeviceStateMemo()
        {
            _devStateMemoActive = false;
        }

        // All-rest state for offline-pinned BOOL-LIKE reads (buttons read
        // false). Never hand this to an AXIS read: zeroed unsigned axes
        // decode as full-negative deflection, which is the exact poison
        // the offline-contributes-zero fixes remove. Read-only by contract.
        private static readonly CustomInputState OfflinePinnedRestState = new();

        private static CustomInputState LookupDeviceState(string deviceGuid)
        {
            if (string.IsNullOrEmpty(deviceGuid)) return null;
            bool useMemo = _devStateMemoActive;
            if (useMemo && _devStateMemo.TryGetValue(deviceGuid, out var cachedDev))
                return (cachedDev != null && cachedDev.IsOnline) ? cachedDev.InputState : null;

            UserDevice found = null;
            if (System.Guid.TryParse(deviceGuid, out var g))
            {
                var devs = SettingsManager.UserDevices?.Items;
                if (devs != null)
                {
                    lock (SettingsManager.UserDevices.SyncRoot)
                    {
                        for (int i = 0; i < devs.Count; i++)
                        {
                            var d = devs[i];
                            if (d == null || d.InstanceGuid != g) continue;
                            if (d.IsOnline) { found = d; break; }
                            // Remember the first offline match too: the
                            // per-hit IsOnline re-read picks it up if it
                            // comes back within the pass, matching what a
                            // fresh baseline scan would have seen.
                            found ??= d;
                        }
                    }
                }
            }
            if (useMemo) _devStateMemo[deviceGuid] = found;
            return (found != null && found.IsOnline) ? found.InputState : null;
        }

        /// <summary>Memoized lookup for concrete-source sites. The captured
        /// current-state shortcut this once had was removed: it returned a
        /// state snapshot with no IsOnline re-read, so a device that went
        /// offline mid-pass kept contributing its held state (the resurrect
        /// race above). The memo hit is lock-free and re-checks liveness, so
        /// the shortcut bought nothing worth that hole.</summary>
        private static CustomInputState LookupDeviceStateFast(
            string deviceGuid, CustomInputState currentState, string currentDeviceGuid)
        {
            return LookupDeviceState(deviceGuid);
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
            || (target != null && target.StartsWith("RawAxis", System.StringComparison.Ordinal));

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
            // Count what was actually copied: a UI-thread shrink mid-copy
            // must yield a truncated one-tick snapshot, never stale pooled
            // references past the stop point (the buffer is not cleared
            // between calls) or an indexer throw on the poll thread.
            int copied = 0;
            try
            {
                for (int i = 0; i < n && i < src.Count; i++) { buf[i] = src[i]; copied = i + 1; }
            }
            catch (System.ArgumentOutOfRangeException) { }
            count = copied;
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
            // Same copied-count containment as SnapshotSources: a shrink
            // mid-copy truncates this tick's snapshot instead of exposing
            // stale pooled rows or throwing on the poll thread.
            int copied = 0;
            try
            {
                for (int i = 0; i < n && i < rows.Count; i++) { buf[i] = rows[i]; copied = i + 1; }
            }
            catch (System.ArgumentOutOfRangeException) { }
            count = copied;
            return buf;
        }

        /// <summary>Builds the row's positional contributions list for
        /// the multi-source cross-device path. Variable order
        /// a..z mirrors the row's UI. Each entry is the source's
        /// coerced value against ITS OWN device's state. Returns an
        /// empty list if no source could be evaluated against any
        /// online device.</summary>
        // The three positional contribution builders below resolve each
        // source against ITS OWN device (LookupDeviceState). One seam:
        // an EMPTY MappingSource.DeviceGuid means "the device currently
        // being evaluated" (the documented contract, and the resolution
        // the single-source per-target paths already apply).
        // currentState / currentDeviceGuid carry that device's state and
        // guid from the caller. Explicitly-pinned sources whose device is
        // offline still contribute 0, exactly as before.
        private static List<float> BuildCustomContribsForBipolarAxis(
            MappingRow row, int slotIndex, double dt,
            CustomInputState currentState, string currentDeviceGuid)
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

            List<CustomInputState> slotStates = null; // lazily filled on the first empty-guid side
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

                bool hasNegPair = (i == 0 && negPairIndex == 1);
                var negSrc = hasNegPair ? srcs[1] : null;
                bool useNeg = hasNegPair && negSrc != null
                    && !IsSourceSuppressedPostpone(slotIndex, negSrc.DeviceGuid, negSrc.Descriptor);
                bool posAny = string.IsNullOrEmpty(src.DeviceGuid);
                bool negAny = useNeg && string.IsNullOrEmpty(negSrc.DeviceGuid);

                if (posAny || negAny)
                {
                    // Any "any device" side spans the slot's devices (the row is
                    // evaluated once). The empty-guid side reads per device; a
                    // concrete side reads its own fixed device. The neg pair's
                    // (pos + neg) is formed per device, then max-abs selects the
                    // device with the strongest deflection.
                    slotStates ??= GetSlotDeviceStates(slotIndex, currentState, currentDeviceGuid);
                    var posFixed = posAny ? null : LookupDeviceStateFast(src.DeviceGuid, currentState, currentDeviceGuid);
                    var negFixed = (useNeg && !negAny) ? LookupDeviceStateFast(negSrc.DeviceGuid, currentState, currentDeviceGuid) : null;
                    float best = 0f;
                    for (int d = 0; d < slotStates.Count; d++)
                    {
                        var pState = posAny ? slotStates[d] : posFixed;
                        if (pState == null) continue;
                        float v = SourceEvaluator.EvaluateForBipolarAxisTarget(
                            pState, src, slotIndex, row.Target, i, slotRuntime, dt,
                            evaluatedDeviceGuid: currentDeviceGuid);
                        if (useNeg)
                        {
                            var nState = negAny ? slotStates[d] : negFixed;
                            if (nState != null)
                                v += SourceEvaluator.EvaluateForBipolarAxisTarget(
                                    nState, negSrc, slotIndex, row.Target, 1, slotRuntime, dt,
                                    evaluatedDeviceGuid: currentDeviceGuid);
                        }
                        if (System.Math.Abs(v) > System.Math.Abs(best)) best = v;
                    }
                    list.Add(best);
                    continue;
                }

                // Both sides concrete (original single-device behavior).
                var devState = LookupDeviceStateFast(src.DeviceGuid, currentState, currentDeviceGuid);
                if (devState == null) { list.Add(0f); continue; }
                float val = SourceEvaluator.EvaluateForBipolarAxisTarget(
                    devState, src, slotIndex, row.Target, i, slotRuntime, dt,
                    evaluatedDeviceGuid: currentDeviceGuid);
                if (useNeg)
                {
                    var negState = LookupDeviceStateFast(negSrc.DeviceGuid, currentState, currentDeviceGuid);
                    if (negState != null)
                        val += SourceEvaluator.EvaluateForBipolarAxisTarget(
                            negState, negSrc, slotIndex, row.Target, 1, slotRuntime, dt,
                            evaluatedDeviceGuid: currentDeviceGuid);
                }
                list.Add(val);
            }
            return list;
        }

        private static List<float> BuildCustomContribsForTrigger(
            MappingRow row, int slotIndex, double dt,
            CustomInputState currentState, string currentDeviceGuid)
        {
            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            var list = _contribFloatBuf ??= new List<float>(8);
            list.Clear();
            var srcs = SnapshotSources(row, out int srcsCount);
            List<CustomInputState> slotStates = null; // lazily filled on the first empty-guid source
            for (int i = 0; i < srcsCount; i++)
            {
                var src = srcs[i];
                if (IsRowModifierSource(src)) continue;
                if (src == null) { list.Add(0f); continue; }
                if (IsSourceSuppressedPostpone(slotIndex, src.DeviceGuid, src.Descriptor))
                { list.Add(0f); continue; }
                if (string.IsNullOrEmpty(src.DeviceGuid))
                {
                    // "any device": take the strongest pull across the slot's
                    // devices (this row is evaluated once, so it must span all
                    // devices, not just the first-evaluated one).
                    slotStates ??= GetSlotDeviceStates(slotIndex, currentState, currentDeviceGuid);
                    float mx = 0f;
                    for (int d = 0; d < slotStates.Count; d++)
                    {
                        float t = SourceEvaluator.EvaluateForTriggerTarget(
                            slotStates[d], src, slotIndex, row.Target, i, slotRuntime, dt,
                            evaluatedDeviceGuid: currentDeviceGuid);
                        if (t > mx) mx = t;
                    }
                    list.Add(mx);
                    continue;
                }
                var devState = LookupDeviceStateFast(src.DeviceGuid, currentState, currentDeviceGuid);
                if (devState == null) { list.Add(0f); continue; }
                list.Add(SourceEvaluator.EvaluateForTriggerTarget(
                    devState, src, slotIndex, row.Target, i, slotRuntime, dt,
                    evaluatedDeviceGuid: currentDeviceGuid));
            }
            return list;
        }

        private static List<float> BuildCustomContribsForButton(
            MappingRow row, int slotIndex, int globalAxisToButtonThreshold, double dt,
            CustomInputState currentState, string currentDeviceGuid)
        {
            var slotRuntime = (slotIndex >= 0 && slotIndex < _slotSourceKindRuntime.Length)
                ? _slotSourceKindRuntime[slotIndex] : null;
            var list = _contribFloatBuf ??= new List<float>(8);
            list.Clear();
            var srcs = SnapshotSources(row, out int srcsCount);
            List<CustomInputState> slotStates = null; // lazily filled on the first empty-guid source
            for (int i = 0; i < srcsCount; i++)
            {
                var src = srcs[i];
                if (IsRowModifierSource(src)) continue;
                if (src == null) { list.Add(0f); continue; }
                if (IsSourceSuppressedPostpone(slotIndex, src.DeviceGuid, src.Descriptor))
                { list.Add(0f); continue; }
                if (string.IsNullOrEmpty(src.DeviceGuid))
                {
                    // "any device": OR the button read across every device on
                    // the slot (this row is evaluated once, so it must span all
                    // devices, not just the first-evaluated one).
                    slotStates ??= GetSlotDeviceStates(slotIndex, currentState, currentDeviceGuid);
                    bool any = false;
                    for (int d = 0; d < slotStates.Count; d++)
                    {
                        if (SourceEvaluator.EvaluateForButtonTarget(
                            slotStates[d], src, globalAxisToButtonThreshold,
                            slotIndex, row.Target, i, slotRuntime, dt,
                            evaluatedDeviceGuid: currentDeviceGuid)) { any = true; break; }
                    }
                    list.Add(any ? 1f : 0f);
                    continue;
                }
                var devState = LookupDeviceStateFast(src.DeviceGuid, currentState, currentDeviceGuid);
                if (devState == null) { list.Add(0f); continue; }
                list.Add(SourceEvaluator.EvaluateForButtonTarget(
                    devState, src, globalAxisToButtonThreshold,
                    slotIndex, row.Target, i, slotRuntime, dt,
                    evaluatedDeviceGuid: currentDeviceGuid) ? 1f : 0f);
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
            var cache = s_baseRowCaches.GetOrCreateValue(mappingSet);
            if (cache.Count != currentCount)
            {
                var baseRows = cache.Rows;
                baseRows.Clear();

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
                    baseRows[target] = r;
                }

                cache.Count = currentCount;
            }

            return cache.Rows.TryGetValue(targetName, out var row) ? row : null;
        }

        /// <summary>Resolves the mapping row that should drive
        /// <paramref name="targetName"/> for the slot's currently-engaged
        /// shift layer, so the non-gamepad per-VC evaluators (Extended /
        /// MIDI / KBM / Touchpad) pick the SAME row
        /// <see cref="ApplyMappingSetToGamepad"/> would. That gamepad path
        /// was the only output dispatch that consulted the active layer;
        /// these per-target evaluators hard-filtered to Base via
        /// <see cref="FindBaseRowForTarget"/>, so a layer that remapped a
        /// physical input to a different Extended/MIDI/KBM/Touchpad target
        /// never fired that target and the Base target it was meant to
        /// replace stayed live regardless of the active layer
        /// (issue #221).
        ///
        /// <para>Base active → the cached Base row (unchanged fast path).
        /// A layer engaged → the layer's own row wins when it has sources;
        /// otherwise the target inherits the Base row (InheritUnmapped with
        /// no NoInherit block) or is suppressed (replace mode / NoInherit).
        /// <paramref name="suppressed"/> is true when a shift layer
        /// deliberately forces the target off: the caller must NOT fall
        /// back to the legacy per-target descriptor, or replace-mode
        /// suppression would leak the Base mapping back in.</para>
        ///
        /// <para>The engaged mask is read via the pure
        /// <see cref="GetEngagedLayerMask"/>. The per-frame activator tick
        /// (<see cref="ResolveActiveLayerMask"/>) already ran on this
        /// slot's gamepad pass in <c>UpdateOutputStates</c> before any
        /// non-gamepad path, so reading here never re-ticks the activator
        /// state machine.</para></summary>
        private static MappingRow FindActiveRowForTarget(
            MappingSet mappingSet, string targetName, int slotIndex, out bool suppressed)
        {
            suppressed = false;
            if (mappingSet == null || string.IsNullOrEmpty(targetName)) return null;

            string activeMask = GetEngagedLayerMask(slotIndex, mappingSet);
            if (string.IsNullOrEmpty(activeMask)
                || string.Equals(activeMask, "Base", System.StringComparison.Ordinal))
                return FindBaseRowForTarget(mappingSet, targetName);

            // A non-Base layer is engaged. Weigh the same two candidates
            // ApplyMappingSetToGamepad does: the layer's own row for the
            // target and the Base row. One walk, race-guarded the same way
            // FindBaseRowForTarget's rebuild is (bound by the captured
            // count AND re-check rows.Count each step).
            MappingRow layerRow = null, baseRow = null;
            var rows = mappingSet.Rows;
            if (rows != null)
            {
                int count = rows.Count;
                for (int i = 0; i < count && i < rows.Count; i++)
                {
                    var r = rows[i];
                    if (r == null) continue;
                    if (!string.Equals(r.Target, targetName, System.StringComparison.Ordinal)) continue;
                    string rl = r.LayerMask ?? "Base";
                    if (string.Equals(rl, activeMask, System.StringComparison.Ordinal)) layerRow = r;
                    else if (string.Equals(rl, "Base", System.StringComparison.Ordinal)) baseRow = r;
                }
            }

            // Layer overrides the target when its own row carries sources.
            if (layerRow?.Sources != null && layerRow.Sources.Count > 0) return layerRow;

            // The layer doesn't map the target with sources. A zero-source
            // layer row still BLOCKS Base fallthrough when it's an explicit
            // NoInherit declaration; otherwise it's transparent.
            bool layerBlocks = layerRow != null && layerRow.NoInherit;
            if (LayerInheritsUnmapped(mappingSet, activeMask) && !layerBlocks)
                return baseRow; // overlay-with-fallthrough: Base drives the target

            // Replace mode, or a NoInherit block: force the target off and
            // tell the caller to skip the legacy descriptor fallback.
            suppressed = true;
            return null;
        }

        /// <summary>True when the activator engaging
        /// <paramref name="activeMask"/> has
        /// <see cref="ShiftActivator.InheritUnmapped"/> set. Mirrors the
        /// per-layer inherit lookup <see cref="ApplyMappingSetToGamepad"/>
        /// does inline (first activator whose LayerMask matches wins).</summary>
        private static bool LayerInheritsUnmapped(MappingSet mappingSet, string activeMask)
        {
            var activators = mappingSet?.ShiftActivators;
            if (activators == null) return false;
            for (int i = 0; i < activators.Count; i++)
            {
                var a = activators[i];
                if (a == null) continue;
                if (string.Equals(a.LayerMask, activeMask, System.StringComparison.Ordinal))
                    return a.InheritUnmapped;
            }
            return false;
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
            var row = FindActiveRowForTarget(mappingSet, targetName, slotIndex, out bool shiftSuppressed);
            if (shiftSuppressed) return true; // shift layer forces this target off; skip legacy fallback
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
                var positional = BuildCustomContribsForButton(row, slotIndex, globalAxisToButtonThreshold, dt, state, thisDeviceGuid);
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
            CustomInputState devState;
            if (string.IsNullOrEmpty(src.DeviceGuid))
                devState = state;
            else
            {
                devState = LookupDeviceState(src.DeviceGuid);
                // Offline-contributes-zero contract: a source PINNED to a
                // device that is not online contributes REST. Falling back
                // to the CURRENT pass's state evaluated another device's
                // row against this device's input (a keyboard's zeroed
                // unsigned axes read as full-negative deflection, the
                // off-center-at-rest report). The row still owns the
                // target, so return true with the rest value.
                if (devState == null) return true;
            }
            value = SourceEvaluator.EvaluateForButtonTarget(
                devState, src, globalAxisToButtonThreshold,
                slotIndex, targetName, 0, slotRuntime, dt,
                evaluatedDeviceGuid: thisDeviceGuid);
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
            var row = FindActiveRowForTarget(mappingSet, targetName, slotIndex, out bool shiftSuppressed);
            if (shiftSuppressed) return true; // shift layer forces this target off; skip legacy fallback
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
                var positional = BuildCustomContribsForBipolarAxis(row, slotIndex, dt, state, thisDeviceGuid);
                if (positional.Count == 0) return false;
                combined = isCustom
                    ? ClampBipolar(EvaluateCustomFloat(row, positional))
                    : ClampBipolar(CombineHelper.CombineAxis(row.CombineMode, positional));
            }
            else
            {
                var src = FirstContributingSource(row);
                if (src == null) return false;
                CustomInputState devState;
                if (string.IsNullOrEmpty(src.DeviceGuid))
                    devState = state;
                else
                {
                    devState = LookupDeviceState(src.DeviceGuid);
                    // Offline-contributes-zero (see the button branch):
                    // rest for a bipolar axis is centered 0, which the
                    // caller's pre-initialized value already holds.
                    if (devState == null) return true;
                }
                combined = ClampBipolar(SourceEvaluator.EvaluateForBipolarAxisTarget(
                    devState, src, slotIndex, targetName, 0, slotRuntime, dt,
                    evaluatedDeviceGuid: thisDeviceGuid));
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
            var row = FindActiveRowForTarget(mappingSet, targetName, slotIndex, out bool shiftSuppressed);
            if (shiftSuppressed) return true; // shift layer forces this target off; skip legacy fallback
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

                CustomInputState devState;
                if (string.IsNullOrEmpty(src.DeviceGuid))
                    devState = state;
                else
                {
                    devState = LookupDeviceState(src.DeviceGuid);
                    // Offline-contributes-zero: a pinned source whose
                    // device is offline contributes centered/inactive,
                    // never an evaluation against the wrong device.
                    if (devState == null) { values.Add(0f); flags.Add(0f); continue; }
                }

                // "Active" = currently contributing useful data.
                bool isActive;
                bool isTouchpadSrc = !string.IsNullOrEmpty(src.Descriptor)
                    && src.Descriptor.StartsWith("Touchpad ", System.StringComparison.Ordinal);
                if (isTouchpadSrc)
                {
                    // Parse pad + finger index from "Touchpad N Finger M X|Y".
                    var (padIdx, parsedFingerIdx) = ParseTouchpadPadFinger(src.Descriptor);
                    int fingerIdx = parsedFingerIdx >= 0 ? parsedFingerIdx : defaultFingerIdx;
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
                    devState ?? state, src, slotIndex, targetName, i, slotRuntime, dt,
                    evaluatedDeviceGuid: thisDeviceGuid);

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
        public static bool TryEvaluateMappingSetRawTrigger(
            CustomInputState state, MappingSet mappingSet, string thisDeviceGuid,
            int slotIndex, string targetName,
            out short value)
        {
            value = short.MinValue;
            var row = FindActiveRowForTarget(mappingSet, targetName, slotIndex, out bool shiftSuppressed);
            if (shiftSuppressed) return true; // shift layer forces this target off; skip legacy fallback
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
                if (row.CombineMode == "StickTrim")
                {
                    // Stateful combine (#155), same intercept as the
                    // gamepad trigger site.
                    combined = ClampUnipolar(EvaluateStickTrim(row, slotIndex, dt, state, thisDeviceGuid));
                }
                else
                {
                    var positional = BuildCustomContribsForTrigger(row, slotIndex, dt, state, thisDeviceGuid);
                    if (positional.Count == 0) return false;
                    combined = isCustom
                        ? ClampUnipolar(EvaluateCustomFloat(row, positional))
                        : ClampUnipolar(CombineHelper.CombineAxis(row.CombineMode, positional));
                }
            }
            else
            {
                var src = FirstContributingSource(row);
                if (src == null) return false;
                CustomInputState devState;
                if (string.IsNullOrEmpty(src.DeviceGuid))
                    devState = state;
                else
                {
                    devState = LookupDeviceState(src.DeviceGuid);
                    // Offline-contributes-zero: trigger rest is released
                    // (the caller's pre-initialized value).
                    if (devState == null) return true;
                }
                combined = ClampUnipolar(SourceEvaluator.EvaluateForTriggerTarget(
                    devState, src, slotIndex, targetName, 0, slotRuntime, dt,
                    evaluatedDeviceGuid: thisDeviceGuid));
            }

            if (IsInvertOnHoldActive(row, state, thisDeviceGuid, slotIndex)) combined = 1f - combined;

            // [0..+1] → signed short with short.MinValue = 0% (matches the
            // legacy MapToRawTriggerAxis convention).
            int ushortVal = (int)(combined * 65535f);
            if (ushortVal < 0) ushortVal = 0;
            if (ushortVal > 65535) ushortVal = 65535;
            value = (short)(ushortVal + short.MinValue);
            return true;
        }

        /// <summary>Memoized pad/finger parse for "Touchpad N Finger M X|Y"
        /// descriptors. Descriptors are immutable config vocabulary and this
        /// parse ran per touchpad source per row per poll, allocating a
        /// Split array each time (audit 1n). Finger is -1 when the
        /// descriptor carries no explicit Finger clause, so the caller can
        /// substitute its own default. Capped like the Step 3 descriptor
        /// cache so pathological configs stay bounded.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string, (int Pad, int Finger)> _touchpadPadFingerCache = new();

        private static (int Pad, int Finger) ParseTouchpadPadFinger(string descriptor)
        {
            if (_touchpadPadFingerCache.TryGetValue(descriptor, out var cached))
                return cached;

            int padIdx = 0;
            int fingerIdx = -1;
            var parts = descriptor.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int parsedPad))
                padIdx = parsedPad;
            if (parts.Length == 5
                && string.Equals(parts[2], "Finger", System.StringComparison.Ordinal)
                && int.TryParse(parts[3], out int parsedFinger))
            {
                fingerIdx = parsedFinger;
            }
            var result = (padIdx, fingerIdx);
            if (_touchpadPadFingerCache.Count < 4096)
                _touchpadPadFingerCache[descriptor] = result;
            return result;
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
            CustomInputState state, MappingRow row, string thisDeviceGuid, int slotIndex, ref Gamepad gp)
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
            if (up || down || left || right) StampLayerActivity(slotIndex, row);
            if (up)    gp.SetButton(Gamepad.DPAD_UP, true);
            if (down)  gp.SetButton(Gamepad.DPAD_DOWN, true);
            if (left)  gp.SetButton(Gamepad.DPAD_LEFT, true);
            if (right) gp.SetButton(Gamepad.DPAD_RIGHT, true);
        }

        /// <summary>Per-source memo of the four synthetic POV-direction
        /// sources. The Split + interpolated descriptor + MappingSource
        /// allocations otherwise run 4x per combined-DPad source per 1 kHz
        /// tick. Keyed on the source instance; revalidates the fields the
        /// synths were built from so an in-place edit rebuilds them.</summary>
        private sealed class PovSynthCache
        {
            public string Descriptor;
            public string DeviceGuid;
            public bool Invert;
            public bool HalfAxis;
            public int DeadZone;
            public MappingSource Up, Down, Left, Right;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MappingSource, PovSynthCache>
            s_povSynthCache = new();

        private static bool EvalPovBool(CustomInputState state, MappingSource src, string direction)
        {
            // Build (once) a POV-direction descriptor: original descriptor
            // is "POV N" (no direction); we tack on the direction under test.
            var s = (src.Descriptor ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return false;

            var cache = s_povSynthCache.GetOrCreateValue(src);
            if (!string.Equals(cache.Descriptor, s, System.StringComparison.Ordinal)
                || !string.Equals(cache.DeviceGuid, src.DeviceGuid, System.StringComparison.Ordinal)
                || cache.Invert != src.Invert
                || cache.HalfAxis != src.HalfAxis
                || cache.DeadZone != src.DeadZone)
            {
                var parts = s.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                bool isPov = parts.Length >= 2
                    && parts[0].Equals("POV", System.StringComparison.OrdinalIgnoreCase);
                cache.Descriptor = s;
                cache.DeviceGuid = src.DeviceGuid;
                cache.Invert = src.Invert;
                cache.HalfAxis = src.HalfAxis;
                cache.DeadZone = src.DeadZone;
                if (!isPov)
                {
                    cache.Up = cache.Down = cache.Left = cache.Right = null;
                }
                else
                {
                    MappingSource Synth(string dir) => new()
                    {
                        Kind = "Direct",
                        DeviceGuid = src.DeviceGuid,
                        Descriptor = $"POV {parts[1]} {dir}",
                        Invert = src.Invert,
                        HalfAxis = src.HalfAxis,
                        DeadZone = src.DeadZone,
                    };
                    cache.Up = Synth("Up");
                    cache.Down = Synth("Down");
                    cache.Left = Synth("Left");
                    cache.Right = Synth("Right");
                }
            }

            var synth = direction switch
            {
                "Up" => cache.Up,
                "Down" => cache.Down,
                "Left" => cache.Left,
                _ => cache.Right,
            };
            if (synth == null) return false;
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
