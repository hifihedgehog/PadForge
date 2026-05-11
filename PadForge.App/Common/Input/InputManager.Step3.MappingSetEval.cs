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
        /// triggers fresh cross-device evaluation.</summary>
        private static void BeginFrameMultiSourceTracking()
        {
            for (int i = 0; i < _multiSourceEvaluatedTargetsBySlot.Length; i++)
                _multiSourceEvaluatedTargetsBySlot[i].Clear();
        }

        private static double ComputeAndAdvanceDelta(int slot)
        {
            double now = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
            double last = _lastEvalTime[slot];
            _lastEvalTime[slot] = now;
            if (last <= 0) return 0; // first frame on this slot
            double dt = now - last;
            // Cap pathologically large deltas (e.g. resume from sleep) so a
            // sticky Incremental accumulator doesn't fly to a clamp.
            if (dt > 0.25) dt = 0.25;
            return dt;
        }

        // ─────────────────────────────────────────────
        //  Shift layer activator state
        //
        //  Per-slot Toggle engagement + previous-frame button-down latch
        //  for edge detection. State does not persist across app restart
        //  or profile switch (cleared with the source-kind runtime).
        // ─────────────────────────────────────────────
        private static readonly bool[] _shiftToggleEngaged = new bool[MaxPads];
        private static readonly bool[] _shiftButtonWasDown = new bool[MaxPads];

        /// <summary>Resolves the active shift layer mask for a slot based
        /// on its <see cref="MappingSet.ShiftButton"/> activator (Hold or
        /// Toggle). Returns <c>"Base"</c> when no activator is configured
        /// or its button isn't held / toggled on.</summary>
        private static string ResolveActiveLayerMask(
            int slotIndex,
            MappingSet mappingSet,
            CustomInputState thisDeviceState,
            string thisDeviceGuid)
        {
            var act = mappingSet?.ShiftButton;
            if (act == null) return "Base";

            // Only the device that owns the shift button reads it; other
            // devices return Base on this pass and the activator's owning
            // device handles the engagement transition. This keeps cross-
            // device shift cleanly scoped: the user binds the activator
            // to one physical device, and that device drives the layer
            // transition for the slot.
            if (!string.IsNullOrEmpty(act.DeviceGuid) &&
                !string.Equals(act.DeviceGuid, thisDeviceGuid, System.StringComparison.OrdinalIgnoreCase))
                return "Base";

            bool buttonDown = SourceKindRuntimeReadButtonLikeBool(thisDeviceState, act.Descriptor);

            string mode = act.Mode ?? "Hold";
            switch (mode)
            {
                case "Toggle":
                {
                    // Rising-edge detection: engagement flips on press.
                    if (slotIndex >= 0 && slotIndex < _shiftToggleEngaged.Length)
                    {
                        bool prev = _shiftButtonWasDown[slotIndex];
                        if (buttonDown && !prev)
                            _shiftToggleEngaged[slotIndex] = !_shiftToggleEngaged[slotIndex];
                        _shiftButtonWasDown[slotIndex] = buttonDown;
                        return _shiftToggleEngaged[slotIndex] ? "Shift" : "Base";
                    }
                    return "Base";
                }
                case "Hold":
                default:
                    return buttonDown ? "Shift" : "Base";
            }
        }

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
            var rowsSnapshot = SnapshotRows(mappingSet);
            if (rowsSnapshot.Length == 0) return;

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

            // Phase 5 — resolve the shift activator's current state for
            // this slot/device. Overlay-with-fallthrough: rows with the
            // active layer mask override matching Targets; targets without
            // an active-mask row fall through to the Base row.
            string activeMask = ResolveActiveLayerMask(slotIndex, mappingSet, state, thisDeviceGuid);
            HashSet<string> shiftCoveredTargets = activeMask != "Base" ? new HashSet<string>() : null;
            if (shiftCoveredTargets != null)
            {
                for (int i = 0; i < rowsSnapshot.Length; i++)
                {
                    var r = rowsSnapshot[i];
                    if (r == null) continue;
                    if (string.Equals(r.LayerMask, activeMask, System.StringComparison.Ordinal))
                        shiftCoveredTargets.Add(r.Target ?? "");
                }
            }

            for (int rowIdx = 0; rowIdx < rowsSnapshot.Length; rowIdx++)
            {
                var row = rowsSnapshot[rowIdx];
                if (row == null) continue;
                if (string.IsNullOrEmpty(row.Target)) continue;

                // Layer-row picking with overlay-with-fallthrough.
                string rowLayer = row.LayerMask ?? "Base";
                if (activeMask == "Base")
                {
                    // Base layer active: only Base rows fire.
                    if (rowLayer != "Base") continue;
                }
                else
                {
                    // Non-Base active: matching-mask rows fire; Base rows
                    // fall through only when no matching-mask row exists
                    // for this Target.
                    if (rowLayer == "Base")
                    {
                        if (shiftCoveredTargets.Contains(row.Target)) continue;
                    }
                    else if (rowLayer != activeMask)
                    {
                        // Some other shift layer (Shift1 vs Shift2 in the
                        // forward-compatible schema). Skip on this pass.
                        continue;
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
                bool isMultiSource = row.Sources != null && row.Sources.Count > 1;
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
                            var bools = new List<bool>(positional.Count);
                            foreach (var v in positional) bools.Add(v > 0.5f);
                            combined = CombineHelper.CombineButton(row.CombineMode, bools);
                        }
                        WriteBoolTarget(row.Target, combined, ref gp);
                        multiDone?.Add(row.Target);
                        continue;
                    }
                    for (int i = 0; i < row.Sources.Count; i++)
                    {
                        var src = row.Sources[i];
                        if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
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
                        WriteBipolarAxisTarget(row.Target, combined, ref gp);
                        multiDone?.Add(row.Target);
                        continue;
                    }
                    for (int i = 0; i < row.Sources.Count; i++)
                    {
                        var src = row.Sources[i];
                        if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
                        axisContribs.Add(SourceEvaluator.EvaluateForBipolarAxisTarget(
                            state, src, slotIndex, row.Target, i, runtime, dt));
                    }
                    if (axisContribs.Count == 0) continue;
                    WriteBipolarAxisTarget(row.Target,
                        ClampBipolar(CombineHelper.CombineAxis(row.CombineMode, axisContribs)),
                        ref gp);
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
                        WriteTriggerTarget(row.Target, combined, ref gp);
                        multiDone?.Add(row.Target);
                        continue;
                    }
                    for (int i = 0; i < row.Sources.Count; i++)
                    {
                        var src = row.Sources[i];
                        if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
                        axisContribs.Add(SourceEvaluator.EvaluateForTriggerTarget(
                            state, src, slotIndex, row.Target, i, runtime, dt));
                    }
                    if (axisContribs.Count == 0) continue;
                    WriteTriggerTarget(row.Target,
                        ClampUnipolar(CombineHelper.CombineAxis(row.CombineMode, axisContribs)),
                        ref gp);
                }
            }
        }

        // ─── Per-row buffer reuse (single polling thread; static is safe) ──
        [System.ThreadStatic] private static List<float> _msAxisBuf;
        [System.ThreadStatic] private static List<bool> _msBoolBuf;

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

        /// <summary>Snapshots row.Sources into a thread-local buffer to
        /// give the cross-device evaluation a stable view. The save
        /// path (PushUiExtraSourcesIntoSlotMappingSets) mutates
        /// row.Sources without taking a lock — a polling-thread
        /// iteration during a Clear+Add would otherwise throw an
        /// IndexOutOfRangeException as the row briefly empties.</summary>
        private static MappingSource[] SnapshotSources(MappingRow row)
        {
            var src = row?.Sources;
            if (src == null) return System.Array.Empty<MappingSource>();
            int n = src.Count;
            var arr = new MappingSource[n];
            for (int i = 0; i < n && i < src.Count; i++) arr[i] = src[i];
            return arr;
        }

        /// <summary>Race-safe snapshot of <c>mappingSet.Rows</c> for the
        /// polling-thread eval. The save path
        /// (<c>PushUiExtraSourcesIntoSlotMappingSets</c>) mutates the same
        /// list on the UI thread — calling <c>Rows.Add</c> for new targets
        /// and <c>Sources.Clear/Add</c> on each row. A direct <c>foreach</c>
        /// over the live list could throw <c>InvalidOperationException</c>
        /// (collection modified during enumeration) or an indexed access
        /// could read past <c>Count</c>; the result is the user-visible
        /// "Error: Error mapping device {guid}" status when the polling
        /// thread's try/catch around <c>UpdateOutputStates</c> swallows the
        /// throw. Snapshotting once at the start of evaluation hands the
        /// polling thread a stable array even mid-save. Sources are
        /// snapshotted separately by <see cref="SnapshotSources"/>.</summary>
        internal static MappingRow[] SnapshotRows(MappingSet mappingSet)
        {
            var rows = mappingSet?.Rows;
            if (rows == null) return System.Array.Empty<MappingRow>();
            int n = rows.Count;
            var arr = new MappingRow[n];
            for (int i = 0; i < n && i < rows.Count; i++) arr[i] = rows[i];
            return arr;
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
            var list = new List<float>();
            var srcs = SnapshotSources(row);
            if (srcs.Length == 0) return list;

            int negPairIndex = -1;
            if (TargetIsBipolarAxis(row.Target) && srcs.Length >= 2
                && IsBipolarNegPair(srcs[0], srcs[1]))
            {
                negPairIndex = 1;
            }

            for (int i = 0; i < srcs.Length; i++)
            {
                if (i == negPairIndex) continue;
                var src = srcs[i];
                if (src == null) { list.Add(0f); continue; }
                var devState = LookupDeviceState(src.DeviceGuid);
                if (devState == null) { list.Add(0f); continue; }
                float v = SourceEvaluator.EvaluateForBipolarAxisTarget(
                    devState, src, slotIndex, row.Target, i, slotRuntime, dt);
                if (i == 0 && negPairIndex == 1)
                {
                    var negSrc = srcs[1];
                    var negState = negSrc != null ? LookupDeviceState(negSrc.DeviceGuid) : null;
                    if (negState != null)
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
            var list = new List<float>();
            var srcs = SnapshotSources(row);
            for (int i = 0; i < srcs.Length; i++)
            {
                var src = srcs[i];
                if (src == null) { list.Add(0f); continue; }
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
            var list = new List<float>();
            var srcs = SnapshotSources(row);
            for (int i = 0; i < srcs.Length; i++)
            {
                var src = srcs[i];
                if (src == null) { list.Add(0f); continue; }
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
        /// target by name. Mirrors the row filter used by
        /// <see cref="ApplyMappingSetToGamepad"/>.</summary>
        private static MappingRow FindBaseRowForTarget(MappingSet mappingSet, string targetName)
        {
            // Snapshot to avoid racing the save path's Rows.Add/Sources mutations.
            // See SnapshotRows for the race details.
            var rows = SnapshotRows(mappingSet);
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                if (r == null) continue;
                if (!string.Equals(r.LayerMask ?? "Base", "Base", System.StringComparison.Ordinal))
                    continue;
                if (string.Equals(r.Target, targetName, System.StringComparison.Ordinal))
                    return r;
            }
            return null;
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

            bool isMultiSource = row.Sources.Count > 1;
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
                    var bools = new List<bool>(positional.Count);
                    foreach (var v in positional) bools.Add(v > 0.5f);
                    value = CombineHelper.CombineButton(row.CombineMode, bools);
                }
                return true;
            }

            // Single source — evaluate cross-device (the source's own DeviceGuid
            // wins, not necessarily the device we're currently processing).
            var src = row.Sources[0];
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

            bool isMultiSource = row.Sources.Count > 1;
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
                var src = row.Sources[0];
                if (src == null) return false;
                var devState = string.IsNullOrEmpty(src.DeviceGuid)
                    ? state
                    : (LookupDeviceState(src.DeviceGuid) ?? state);
                combined = ClampBipolar(SourceEvaluator.EvaluateForBipolarAxisTarget(
                    devState, src, slotIndex, targetName, 0, slotRuntime, dt));
            }

            // Map [-1..+1] → signed short with the same convention legacy
            // MapToThumbAxisWithNeg uses: -1 → short.MinValue, +1 → short.MaxValue.
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

            bool isMultiSource = row.Sources.Count > 1;
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
                var src = row.Sources[0];
                if (src == null) return false;
                var devState = string.IsNullOrEmpty(src.DeviceGuid)
                    ? state
                    : (LookupDeviceState(src.DeviceGuid) ?? state);
                combined = ClampUnipolar(SourceEvaluator.EvaluateForTriggerTarget(
                    devState, src, slotIndex, targetName, 0, slotRuntime, dt));
            }

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

        private static IList<float> BoolListToFloats(List<bool> bools)
        {
            // Tiny temporary list (button rows rarely exceed 4 sources).
            var floats = new float[bools.Count];
            for (int i = 0; i < bools.Count; i++) floats[i] = bools[i] ? 1f : 0f;
            return floats;
        }

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
