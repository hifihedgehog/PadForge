using System;
using System.Collections.Generic;
using PadForge.Engine.Data;

namespace PadForge.Engine.Common.Mapping
{
    /// <summary>
    /// Per-VC runtime state for stateful source kinds (Incremental).
    /// Lives on the polling-thread side of Step 3; cleared on profile
    /// switch and on app restart.
    ///
    /// <para>
    /// Stateless source kinds (Direct, InvertOnHold) do not allocate
    /// state here — their per-frame value is a pure function of the
    /// inputs and the modifier descriptor.
    /// </para>
    /// </summary>
    public sealed class SourceKindRuntime
    {
        // Keyed by (slotIndex, target, sourceIndex). MappingRow is a DTO
        // with no stable identity, so we key by row position. On row
        // reorder the user's Incremental accumulator survives because
        // Target+sourceIndex is what most users edit incrementally; on
        // wholesale row removal the state lingers harmlessly until the
        // dictionary is cleared (profile switch / engine stop).
        private readonly Dictionary<(int slot, string target, int srcIdx), double> _incrementalAccum
            = new();

        // ── Steering kinds (v3.4 #94) ──
        /// <summary>Lock-edge transition reported to the lock-feedback layer.</summary>
        public enum LockEdge : byte { None, Enter, Exit }
        private enum LockSide : byte { None, Left, Right }
        private struct WindingState { public double AngleDeg; public double LastX, LastY; }
        private struct LockState { public LockSide Side; public LockEdge PendingEdge; public bool PendingLeft; }

        // Signed winding accumulator per row (deg, NOT clamped — overshoot is intentional).
        private readonly Dictionary<(int slot, string target, int srcIdx), WindingState> _windingState = new();
        // At-lock edge tracking per row, drives the haptic feedback layer.
        private readonly Dictionary<(int slot, string target, int srcIdx), LockState> _lockState = new();

        // Saturation band for the lock state machine — avoids float-edge thrash at ±1.
        private const double LockEpsilon = 1e-3;

        /// <summary>Drops all state. Called on profile switch and engine
        /// stop. Cruise control snaps to neutral on next read.</summary>
        public void Clear()
        {
            _incrementalAccum.Clear();
            _windingState.Clear();
            _lockState.Clear();
        }

        /// <summary>Drops all steering state for a slot. Called on profile switch.</summary>
        public void ResetForSlot(int slot)
        {
            RemoveWhere(_windingState, slot, null);
            RemoveWhere(_lockState, slot, null);
        }

        /// <summary>Drops steering state for one (slot, target). Called on row reorder
        /// so a winding accumulator does not survive a structural mapping change.</summary>
        public void ResetForRow(int slot, string target)
        {
            RemoveWhere(_windingState, slot, target ?? "");
            RemoveWhere(_lockState, slot, target ?? "");
        }

        private static void RemoveWhere<TVal>(
            Dictionary<(int slot, string target, int srcIdx), TVal> dict, int slot, string target)
        {
            List<(int, string, int)> dead = null;
            foreach (var k in dict.Keys)
                if (k.slot == slot && (target == null || k.target == target))
                    (dead ??= new()).Add(k);
            if (dead != null)
                foreach (var k in dead) dict.Remove(k);
        }

        /// <summary>
        /// Updates the Incremental accumulator for this source and returns
        /// the per-frame contribution (already in the source kind's
        /// configured range — unipolar [ParamMin, ParamMax]).
        /// </summary>
        public double TickIncremental(
            int slotIndex,
            string target,
            int sourceIndex,
            MappingSource src,
            CustomInputState state,
            double frameDeltaSeconds)
        {
            if (src == null || state == null) return 0;
            var key = (slotIndex, target ?? "", sourceIndex);
            _incrementalAccum.TryGetValue(key, out double current);

            // Clamp to declared range (handles user re-narrowing the range
            // mid-session).
            if (current < src.ParamMin) current = src.ParamMin;
            if (current > src.ParamMax) current = src.ParamMax;

            bool up = ReadButtonLikeBool(state, src.ParamUp);
            bool down = ReadButtonLikeBool(state, src.ParamDown);

            double rate = src.ParamRate;
            if (rate < 0) rate = 0;

            double range = src.ParamMax - src.ParamMin;
            if (range <= 0) range = 1.0;

            // Step in units-of-output-range per second; e.g. rate=0.5
            // sweeps the full range in 2 s.
            double step = rate * range * frameDeltaSeconds;

            if (up && !down)
            {
                current += step;
                if (current > src.ParamMax) current = src.ParamMax;
            }
            else if (down && !up)
            {
                current -= step;
                if (current < src.ParamMin) current = src.ParamMin;
            }
            else if (!up && !down && !src.ParamSticky)
            {
                // Non-sticky: snap to ParamMin when neither held.
                current = src.ParamMin;
            }
            // else: held opposite or both held / both released sticky →
            // hold last value.

            _incrementalAccum[key] = current;
            return current;
        }

        // ── Steering kind ticks (v3.4 #94) ──
        // Math sourced from JoyShockMapper src/JoyShock.cpp:1179-1323 (winding,
        // angle-to-axis) and src/main.cpp:939-986 (motion lean), Electronicks fork
        // bb69784: read for the geometry, written original here. The per-stick
        // output-undeadzone / unpower terms JSM applies all default to identity, so
        // they are omitted. STICK_POWER defaults to 1, so its pow() collapses too
        // (the angle-to-axis magnitude keeps its linear-in-deflection factor).

        /// <summary>Accumulating winding-stick steering. Stick rotation winds a
        /// virtual wheel; full lock at <c>ParamWindRangeDeg</c> of accrued travel.
        /// Returns the X-channel value in [-1, +1].</summary>
        public double TickWindingStick(int slotIndex, string target, int sourceIndex,
            MappingSource src, CustomInputState state, double deltaSeconds)
        {
            if (src == null || state == null) return 0;
            var key = (slotIndex, target ?? "", sourceIndex);
            var (x, y, len) = ReadStick2D(state, src.Descriptor, src.ParamYDescriptor, Clamp01(src.ParamStickDeadzone));

            _windingState.TryGetValue(key, out var ws);

            // Accrue the signed angular travel since last frame, scaled by deflection.
            // atan2 is scale-invariant, so the deadzone-scaled stick gives the true angle.
            if (len > 0 && ws.LastX != 0 && ws.LastY != 0)
            {
                double cur = Math.Atan2(-x, y);
                double last = Math.Atan2(-ws.LastX, ws.LastY);
                double delta = ((cur - last) + Math.PI) % (2.0 * Math.PI);
                if (delta < 0) delta += 2.0 * Math.PI;
                delta -= Math.PI;                                   // wrap to (-PI, PI]
                ws.AngleDeg -= delta * len * (180.0 / Math.PI);
            }

            // Unwind whenever below full deflection, proportional to how far released.
            if (len < 1.0)
            {
                double unwind = (src.ParamWindUnwindRate <= 0 ? 0 : src.ParamWindUnwindRate) * (1.0 - len) * deltaSeconds;
                double absA = Math.Abs(ws.AngleDeg);
                if (absA <= unwind) ws.AngleDeg = 0;
                else ws.AngleDeg -= unwind * Math.Sign(ws.AngleDeg);
            }

            ws.LastX = x; ws.LastY = y;
            _windingState[key] = ws;

            // Remap accrued angle to output. The accumulator is NOT clamped at the
            // range; only the output saturates, so an overwind holds lock until it
            // unwinds back through the overshoot (intentional, matches JSM).
            double range = src.ParamWindRangeDeg <= 0 ? 900 : src.ParamWindRangeDeg;
            double power = src.ParamWindPower == 0 ? 1 : src.ParamWindPower;
            double remapped = Math.Min(Math.Pow(Math.Abs(ws.AngleDeg) / range * 2.0, power), 1.0);
            double output = Math.Sign(ws.AngleDeg) * remapped;
            UpdateLock(key, output);
            return output;
        }

        /// <summary>Positional 2D-steering: the stick's angle relative to one axis
        /// maps to that axis, magnitude scaled by deflection. No accumulator.
        /// <paramref name="isX"/> picks the X half-plane projection, else Y.</summary>
        public double TickAngleToAxis(int slotIndex, string target, int sourceIndex,
            MappingSource src, CustomInputState state, bool isX)
        {
            if (src == null || state == null) return 0;
            var key = (slotIndex, target ?? "", sourceIndex);
            var (x, y, len) = ReadStick2D(state, src.Descriptor, src.ParamYDescriptor, Clamp01(src.ParamStickDeadzone));
            if (len <= 0) { UpdateLock(key, 0); return 0; }

            // Force the off-axis component positive: the mode is a half-plane
            // projection, so up-and-right and down-and-right report the same angle.
            double angle = isX ? Math.Atan2(x, Math.Abs(y)) : Math.Atan2(y, Math.Abs(x));
            double absDeg = Math.Abs(angle * 180.0 / Math.PI);     // [0, 90]
            double sign = angle < 0 ? -1 : 1;

            double denom = 90.0 - src.ParamAngleOuterDz - src.ParamAngleInnerDz;
            double remapped = denom > 0
                ? Clamp01((absDeg - src.ParamAngleInnerDz) / denom)
                : (absDeg >= src.ParamAngleInnerDz ? 1 : 0);
            remapped *= len;                                       // pow(deflection, STICK_POWER=1)
            double output = sign * remapped;
            UpdateLock(key, output);
            return output;
        }

        /// <summary>Gravity-derived lean steering. Tilt the controller like a wheel;
        /// tilt angle maps to the X channel. Routed-channel stick deflection is
        /// ignored. Returns 0 when no gravity is available.</summary>
        public double TickMotionLean(int slotIndex, string target, int sourceIndex,
            MappingSource src, CustomInputState state, string deviceGuid)
        {
            if (src == null) return 0;
            var key = (slotIndex, target ?? "", sourceIndex);
            var grav = SourceCoercion.GravityProvider?.Invoke(deviceGuid ?? "") ?? (0f, 0f, -1f);
            double gx = grav.gx, gy = grav.gy, gz = grav.gz;
            double gLen = Math.Sqrt(gx * gx + gy * gy + gz * gz);
            if (gLen <= 0) { UpdateLock(key, 0); return 0; }

            double side = (src.ParamControllerOrientation ?? "Forward") switch
            {
                "Left"     => gz,
                "Right"    => -gz,
                "Backward" => -gx,
                _          => gx,   // Forward
            };
            double gravDirX = side / gLen;
            double leanDeg = Math.Asin(Math.Clamp(gravDirX, -1.0, 1.0)) * 180.0 / Math.PI;
            double sign = leanDeg < 0 ? -1 : 1;
            double absLean = Math.Abs(leanDeg);
            if (gy > 0) absLean = 180.0 - absLean;                 // past upright — keep mapping to the same side

            double denom = 180.0 - src.ParamMotionOuterDz - src.ParamMotionInnerDz;
            double remapped = denom > 0
                ? Clamp01((absLean - src.ParamMotionInnerDz) / denom)
                : (absLean >= src.ParamMotionInnerDz ? 1 : 0);     // pow(., STICK_POWER=1)
            double output = sign * remapped;
            UpdateLock(key, output);
            return output;
        }

        /// <summary>Returns and clears any pending at-lock edge for this row, for the
        /// lock-feedback layer. <paramref name="isLeft"/> is set on an Enter edge.</summary>
        public LockEdge TryGetLockEdgeTransition(int slotIndex, string target, int sourceIndex, out bool isLeft)
        {
            isLeft = false;
            var key = (slotIndex, target ?? "", sourceIndex);
            if (_lockState.TryGetValue(key, out var ls) && ls.PendingEdge != LockEdge.None)
            {
                LockEdge edge = ls.PendingEdge;
                isLeft = ls.PendingLeft;
                ls.PendingEdge = LockEdge.None;
                _lockState[key] = ls;
                return edge;
            }
            return LockEdge.None;
        }

        // Saturation → lock state machine. Fires Enter on leaving neutral, Exit on
        // returning to it. A direct side-flip (left lock straight to right lock in one
        // frame) records no edge, matching the JSM-derived recipe's machine.
        private void UpdateLock((int slot, string target, int srcIdx) key, double output)
        {
            LockSide cur = output >= 1.0 - LockEpsilon ? LockSide.Right
                         : output <= -1.0 + LockEpsilon ? LockSide.Left
                         : LockSide.None;
            _lockState.TryGetValue(key, out var ls);
            if (cur == ls.Side) return;
            if (ls.Side == LockSide.None) { ls.PendingEdge = LockEdge.Enter; ls.PendingLeft = cur == LockSide.Left; }
            else if (cur == LockSide.None) { ls.PendingEdge = LockEdge.Exit; }
            ls.Side = cur;
            _lockState[key] = ls;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // Deadzone-processed 2D stick read: raw normalized X/Y, radial inner deadzone,
        // direction preserved, magnitude rescaled to [0, 1]. Inside the deadzone the
        // stick reads fully centered (0, 0, 0).
        private static (double x, double y, double len) ReadStick2D(
            CustomInputState state, string xDesc, string yDesc, double innerDz)
        {
            double rx = ReadNormAxis(state, xDesc);
            double ry = ReadNormAxis(state, yDesc);
            double rawLen = Math.Sqrt(rx * rx + ry * ry);
            if (rawLen <= innerDz || rawLen <= 0) return (0, 0, 0);
            double denom = 1.0 - innerDz;
            double scaled = denom > 0 ? Math.Min((rawLen - innerDz) / denom, 1.0) : Math.Min(rawLen, 1.0);
            return (rx / rawLen * scaled, ry / rawLen * scaled, scaled);
        }

        // Reads an "Axis N" descriptor to a bipolar [-1, +1] value (center 32768),
        // matching SourceCoercion's axis normalization. Non-axis descriptors read 0.
        private static double ReadNormAxis(CustomInputState state, string descriptor)
        {
            if (state == null || string.IsNullOrWhiteSpace(descriptor)) return 0;
            string s = descriptor.Trim();
            if (!s.StartsWith("Axis", StringComparison.Ordinal)) return 0;
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int idx)) return 0;
            if (idx < 0 || idx >= CustomInputState.MaxAxis) return 0;
            double v = (state.Axis[idx] - 32768) / 32767.0;
            return v < -1 ? -1 : (v > 1 ? 1 : v);
        }

        // Reads a button-like descriptor (Button N or POV N Dir) from a
        // CustomInputState. No deadzone handling here — Incremental's up
        // and down inputs are bool intent buttons; analog inputs aren't a
        // sensible up/down trigger for an accumulator.
        private static bool ReadButtonLikeBool(CustomInputState state, string descriptor)
        {
            if (state == null || string.IsNullOrWhiteSpace(descriptor)) return false;
            string s = descriptor.Trim();

            if (s.StartsWith("Button ", StringComparison.Ordinal))
            {
                if (int.TryParse(s.Substring(7), out int idx) &&
                    idx >= 0 && idx < state.Buttons.Length)
                    return state.Buttons[idx];
                return false;
            }

            if (s.StartsWith("POV ", StringComparison.Ordinal))
            {
                var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[1], out int povIdx) &&
                    povIdx >= 0 && povIdx < state.Povs.Length)
                {
                    int v = state.Povs[povIdx];
                    if (v < 0) return false;
                    int n = ((v % 36000) + 36000) % 36000;
                    return parts[2].ToLowerInvariant() switch
                    {
                        "up"    => n >= 31500 || n <= 4500,
                        "right" => n >= 4500 && n <= 13500,
                        "down"  => n >= 13500 && n <= 22500,
                        "left"  => n >= 22500 && n <= 31500,
                        _       => false,
                    };
                }
                return false;
            }

            return false;
        }
    }
}
