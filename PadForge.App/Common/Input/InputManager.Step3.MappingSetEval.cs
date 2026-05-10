using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
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
            ref Gamepad gp)
        {
            if (state == null || mappingSet == null) return;
            if (mappingSet.Rows == null || mappingSet.Rows.Count == 0) return;

            // Reusable per-row buffers. Step 3 runs in a single thread
            // (the polling thread), so static reuse is safe and zero-alloc.
            var axisContribs = _msAxisBuf ??= new List<float>(8);
            var boolContribs = _msBoolBuf ??= new List<bool>(8);

            foreach (var row in mappingSet.Rows)
            {
                if (row == null) continue;
                if (!string.Equals(row.LayerMask, "Base", System.StringComparison.Ordinal))
                    continue; // Shift-layer evaluation lands in the Shift recipe.
                if (string.IsNullOrEmpty(row.Target)) continue;

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

                if (kind == TargetKind.Button || kind == TargetKind.PovDirection)
                {
                    foreach (var src in row.Sources)
                    {
                        if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
                        boolContribs.Add(SourceCoercion.EvaluateForButtonTarget(
                            state, src, globalAxisToButtonThreshold));
                    }
                    if (boolContribs.Count == 0) continue;

                    bool combined = row.CombineMode == "Custom"
                        ? EvaluateCustomBoolean(row, BoolListToFloats(boolContribs))
                        : CombineHelper.CombineButton(row.CombineMode, boolContribs);

                    WriteBoolTarget(row.Target, combined, ref gp);
                }
                else if (kind == TargetKind.BipolarAxis)
                {
                    foreach (var src in row.Sources)
                    {
                        if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
                        axisContribs.Add(SourceCoercion.EvaluateForBipolarAxisTarget(state, src));
                    }
                    if (axisContribs.Count == 0) continue;

                    float combined = row.CombineMode == "Custom"
                        ? ClampBipolar(EvaluateCustomFloat(row, axisContribs))
                        : ClampBipolar(CombineHelper.CombineAxis(row.CombineMode, axisContribs));

                    WriteBipolarAxisTarget(row.Target, combined, ref gp);
                }
                else if (kind == TargetKind.Trigger)
                {
                    foreach (var src in row.Sources)
                    {
                        if (!SourceMatchesDevice(src, thisDeviceGuid)) continue;
                        axisContribs.Add(SourceCoercion.EvaluateForTriggerTarget(state, src));
                    }
                    if (axisContribs.Count == 0) continue;

                    float combined = row.CombineMode == "Custom"
                        ? ClampUnipolar(EvaluateCustomFloat(row, axisContribs))
                        : ClampUnipolar(CombineHelper.CombineAxis(row.CombineMode, axisContribs));

                    WriteTriggerTarget(row.Target, combined, ref gp);
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
