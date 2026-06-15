using System;
using System.Globalization;
using PadForge.Engine.Data;
using PadForge.Engine.Common.Mapping;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    // Steering at-lock feedback layer (#94). When a steering source (winding /
    // 2D angle-to-axis / motion-lean) saturates at full lock, fire opt-in haptic
    // feedback so the wheel "hits a wall." Every channel is per-slot and off by
    // default; physical writes honor the per-slot test target. Runs after the
    // MappingSet eval has updated this frame's lock state in SourceKindRuntime.
    public partial class InputManager
    {
        private static bool IsSteeringTarget(string t)
            => t == "LeftThumbAxisX" || t == "LeftThumbAxisY"
            || t == "RightThumbAxisX" || t == "RightThumbAxisY";

        private static bool IsSteeringKind(string k)
            => k == "WindingStick" || k == "AngleToAxisX" || k == "AngleToAxisY" || k == "MotionLeanX";

        /// <summary>Per-slot continuous steering AT-resistance (0..1), set by the
        /// lock-feedback pass and read by UserEffectsDispatcher to ramp DualSense
        /// trigger resistance as a steering source approaches lock. 0 = inactive.</summary>
        public readonly float[] SteeringAtResistance = new float[MaxPads];

        /// <summary>Per-slot PaletteStep cursor for the steering-lock lightbar's dedicated
        /// palette, advancing one entry per lock entry. Not persisted — restarts at 0.</summary>
        private readonly int[] _steeringLightbarCycleIndex = new int[MaxPads];

        /// <summary>Current steering trigger-vibration pulse strength (0..1) for the slot,
        /// from the channel-2 lock-feedback override. Read by both the Xbox impulse-trigger
        /// combine in ApplyForceFeedback and the DualSense AT-vibration block in the
        /// dispatcher. 0 when no pulse is active.</summary>
        public float GetSteeringTrigVib(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= MaxPads) return 0f;
            var ovr = SteeringTrigVibOverrides[slotIndex];
            if (ovr == null || !ovr.IsActive) return 0f;
            ovr.ComputeMotors(out ushort l, out ushort r);
            ushort v = l >= r ? l : r;
            return v / 65535f;
        }

        /// <summary>Fires the steering at-lock feedback channels for one device's
        /// frame. Rumble + impulse pulse on lock entry; the lightbar pulses to the
        /// configured color and fades on exit; a continuous AT-resistance value
        /// tracks how close the source is to lock. No-op (and clears the AT value)
        /// when every channel is off.</summary>
        private void ApplySteeringLockFeedback(MappingSet ms, int slotIndex, PadSetting ps, UserDevice ud)
        {
            if (ms?.Rows == null || ps == null || slotIndex < 0 || slotIndex >= MaxPads) return;

            bool rumble   = ps.SteeringLockRumbleEnabled == "1";
            bool trigVib  = ps.SteeringLockTriggerVibEnabled == "1";
            bool lightbar = ps.SteeringLockLightbarEnabled == "1";
            bool atRes    = ps.SteeringLockATResistanceEnabled == "1";
            if (!rumble && !trigVib && !lightbar && !atRes)
            {
                SteeringAtResistance[slotIndex] = 0f;
                return;
            }

            var runtime = GetSlotSourceKindRuntime(slotIndex);
            if (runtime == null) return;

            // Test-target scoping: when a device is the slot's test target, only it acts.
            Guid testTarget = TestRumbleTargetGuid[slotIndex];
            bool deviceAllowed = testTarget == Guid.Empty || (ud != null && ud.InstanceGuid == testTarget);

            int pulseMs        = ParsePositiveInt(ps.SteeringLockPulseMs, 80);          // rumble / trigger pulse
            int lightbarHoldMs = ParsePositiveInt(ps.SteeringLockLightbarHoldMs, 80);   // lightbar hold (its own)
            int fadeMs         = ParsePositiveInt(ps.SteeringLockLightbarFadeMs, 250);  // lightbar decay

            float maxApproach = 0f;
            foreach (var row in ms.Rows)
            {
                if (row?.Sources == null || !IsSteeringTarget(row.Target)) continue;
                for (int si = 0; si < row.Sources.Count; si++)
                {
                    var src = row.Sources[si];
                    // Steering Kinds (winding / angle) plus the descriptor-driven
                    // "Motion Lean" input, which steers with Kind=Direct — its
                    // lock state lives in the same SourceKindRuntime machine.
                    if (src == null
                        || !(IsSteeringKind(src.Kind) || SourceCoercion.IsMotionLeanDescriptor(src.Descriptor)))
                        continue;

                    if (atRes)
                    {
                        float a = (float)runtime.GetLockApproach(slotIndex, row.Target, si);
                        if (a > maxApproach) maxApproach = a;
                    }

                    var edge = runtime.TryGetLockEdgeTransition(slotIndex, row.Target, si, out _);
                    if (!deviceAllowed) continue;
                    if (edge == SourceKindRuntime.LockEdge.Enter)
                    {
                        // Channel 1: grip-motor rumble pulse.
                        if (rumble)
                            MacroRumbleOverrides[slotIndex]?.FireReactive(80, 80, pulseMs, 30);
                        // Channel 2: trigger-actuator pulse. MacroRumbleOverride only drives
                        // the grip motors, so this needs its own override whose scalar is
                        // routed to the triggers: Xbox impulse triggers in ApplyForceFeedback
                        // and DualSense trigger haptics via the dispatcher. Firing the grip
                        // override here would just duplicate channel 1 and never reach the
                        // trigger actuators.
                        if (trigVib)
                            SteeringTrigVibOverrides[slotIndex]?.FireReactive(80, 80, pulseMs, 30);
                        // Channel 3: lightbar pulse to the lock color (its own hold + decay).
                        if (lightbar)
                            FireSteeringLightbar(slotIndex, ps, lightbarHoldMs, fadeMs);
                    }
                    // Exit edge: the reactive rumble / lightbar overrides expire on
                    // their own hold+fade windows, so nothing to do here.
                }
            }

            // Channel 4: continuous AT resistance, the max approach across the slot's
            // steering rows. UserEffectsDispatcher reads this when the toggle is on.
            SteeringAtResistance[slotIndex] = (atRes && deviceAllowed) ? maxApproach : 0f;
        }

        // Pulses every per-device lightbar on the slot to the lock color through the
        // same reactive macro-override channel the macro system uses.
        private void FireSteeringLightbar(int slotIndex, PadSetting ps, int holdMs, int fadeMs)
        {
            // Base / Fixed color.
            if (!TryParseHexColor(ps.SteeringLockLightbarColor, out byte fr, out byte fg, out byte fb))
            { fr = 0xFF; fg = 0; fb = 0; }
            // Resolve by the steering lock's own color source. Its palette is dedicated to the
            // steering lock (slotPaletteFallback: false), so PaletteStep with an empty palette
            // resolves to off — it never borrows another section's colors.
            var source = ParseSteeringColorSource(ps.SteeringLockLightbarColorSource);
            int cycle = _steeringLightbarCycleIndex[slotIndex];
            var (r, g, b) = ResolveOverrideLightbarColor(slotIndex, source, fr, fg, fb,
                ps.SteeringLockLightbarPaletteCsv, ref cycle, slotPaletteFallback: false);
            _steeringLightbarCycleIndex[slotIndex] = cycle;

            DateTime now = DateTime.UtcNow;
            DateTime holdEnd = now.AddMilliseconds(holdMs);
            DateTime expires = holdEnd.AddMilliseconds(fadeMs);
            foreach (var psCfg in EnumerateSlotPlayStationConfigs(slotIndex))
            {
                if (psCfg == null) continue;
                psCfg.MacroOverrideR = r;
                psCfg.MacroOverrideG = g;
                psCfg.MacroOverrideB = b;
                psCfg.MacroOverrideHoldMode = MacroLightbarHoldMode.Reactive;
                psCfg.MacroOverrideStartUtc = now;
                psCfg.MacroOverrideHoldEndUtc = holdEnd;
                psCfg.MacroOverrideExpiresAtUtc = expires;
            }
        }

        // Channel 5 (controller speaker tone on lock entry) waits on the speaker
        // output pipeline (#83). Hook left in place so the dispatch shape is ready;
        // do not block v3.4.0 on it.
        private void FireSteeringSpeakerTone(int slotIndex) { /* #83 */ }

        private static int ParsePositiveInt(string s, int dflt)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0 ? v : dflt;

        private static MacroLightbarColorSource ParseSteeringColorSource(string s)
            => Enum.TryParse<MacroLightbarColorSource>(s, out var v) ? v : MacroLightbarColorSource.Fixed;

        private static bool TryParseHexColor(string hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string s = hex.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            return byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                && byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                && byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }
    }
}
