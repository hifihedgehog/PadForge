using System;
using System.Collections.Generic;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// Reference-armed gesture gating for imported profiles (#9, translator
    /// v14). A Steam Workshop import materializes an Authoritative
    /// <see cref="MappingSet"/> whose rows, shift activators, and macro
    /// triggers can read gated touchpad gesture descriptors
    /// ("Touchpad 0 SwipeUp", "Touchpad 0 TouchLeft", "Touchpad 0 DPadUp",
    /// "Touchpad 0 StickX", "Touchpad 1 DoubleTap"). An imported profile
    /// carries no devices, so at import time there is no per-device
    /// Touchpad tab where the user could turn those gesture families on.
    /// For authoritative sets the gate is therefore
    /// "user toggle OR referenced-by-mapping": any gated descriptor the
    /// slot's active set references arms its own feature family on the
    /// resolved per-(slot, device) settings.
    ///
    /// <para>Manual (non-authoritative) sets keep the plain toggle gate.
    /// That preserves the Touchpad tab as the single switch for
    /// hand-authored rows and the documented slot fan-out contract, where
    /// one slot's toggle OFF stops that slot's rows even while another
    /// slot has the same physical pad's toggle ON.</para>
    ///
    /// <para>Unknown gesture names (custom shape gestures and future
    /// families) never arm anything. Custom gestures carry their own
    /// per-gesture enable in the profile's gesture library, and arming the
    /// master switch for a name this table cannot classify would turn on
    /// recognition nobody asked for.</para>
    /// </summary>
    public static class TouchpadGestureAutoArm
    {
        /// <summary>Feature demands referenced by a mapping surface.</summary>
        private struct Needs
        {
            public bool Master;          // any in-box gesture family below
            public bool Taps;
            public bool FourWaySwipes;
            public bool EightWaySwipes;
            public bool TwoFingerSwipes;
            public bool ThreeFinger;
            public bool FourFinger;
            public bool FiveFinger;
            public bool TouchSpots;
            public bool LongPress;
            public bool RadialZones;
            public int RadialZoneCount;  // 0 = no referenced zone count
            // Two descriptors on the same slot naming DIFFERENT zone counts
            // ("RadialZone8_3" beside "RadialZone4_1"). One setting cannot
            // serve both, so the merge records the clash instead of letting
            // whichever descriptor happened to be enumerated last decide.
            public bool RadialZoneCountConflict;
            public bool PinchSpread;
            public bool Rotate;
            public bool Joystick;        // StickX/StickY and the D-pad wedges
            public bool JoystickDPad;    // the wedge bools specifically

            public void Merge(in Needs other)
            {
                Master |= other.Master;
                Taps |= other.Taps;
                FourWaySwipes |= other.FourWaySwipes;
                EightWaySwipes |= other.EightWaySwipes;
                TwoFingerSwipes |= other.TwoFingerSwipes;
                ThreeFinger |= other.ThreeFinger;
                FourFinger |= other.FourFinger;
                FiveFinger |= other.FiveFinger;
                TouchSpots |= other.TouchSpots;
                LongPress |= other.LongPress;
                RadialZones |= other.RadialZones;
                if (other.RadialZoneCount > 0)
                {
                    if (RadialZoneCount > 0 && RadialZoneCount != other.RadialZoneCount)
                    {
                        RadialZoneCountConflict = true;
                        // Keep the lower count so the outcome is the same
                        // whichever order the descriptors arrive in.
                        if (other.RadialZoneCount < RadialZoneCount)
                            RadialZoneCount = other.RadialZoneCount;
                    }
                    else RadialZoneCount = other.RadialZoneCount;
                }
                RadialZoneCountConflict |= other.RadialZoneCountConflict;
                PinchSpread |= other.PinchSpread;
                Rotate |= other.Rotate;
                Joystick |= other.Joystick;
                JoystickDPad |= other.JoystickDPad;
            }
        }

        /// <summary>
        /// Returns <paramref name="resolved"/> with every gesture feature
        /// referenced by <paramref name="set"/> (and by
        /// <paramref name="extraDescriptors"/>, the slot's device-free macro
        /// trigger descriptors) forced on. Returns the input instance
        /// unchanged when the set is null, not authoritative, references no
        /// gated descriptor, or every referenced feature is already enabled.
        /// </summary>
        public static TouchpadGestureSettings Apply(TouchpadGestureSettings resolved,
            MappingSet set, IEnumerable<string> extraDescriptors = null)
        {
            resolved ??= TouchpadGestureSettings.Default();
            if (set == null || !set.Authoritative) return resolved;

            var need = new Needs();
            bool any = false;

            if (set.Rows != null)
            {
                foreach (var row in set.Rows)
                {
                    if (row?.Sources == null) continue;
                    // The combined D-pad row reads "POV N" descriptors alone,
                    // and no gates (EvaluateCombinedDpad).
                    if (string.Equals(row.Target, "DPad", StringComparison.Ordinal)) continue;
                    bool stickRow = TargetKindResolver.Resolve(row.Target) == TargetKind.BipolarAxis;
                    foreach (var src in row.Sources)
                    {
                        if (src == null) continue;
                        // A field arms its family only where the engine reads
                        // it as a gesture (SourceEvaluator's dispatch). A row
                        // modifier (Invert on Hold) reads only its modifier
                        // input, through the full button read: the descriptor
                        // and gates it kept from its source arm nothing. The
                        // input arms on every row. Button rows ignore a
                        // modifier, but the Keyboard + Mouse, Extended, MIDI
                        // and touchpad output lanes apply it on rows the
                        // target resolver files as buttons, and nothing lists
                        // those rows to tell the two apart.
                        if (string.Equals(src.Kind, "InvertOnHold", StringComparison.Ordinal))
                        {
                            any |= Classify(src.ParamModifier, ref need);
                            continue;
                        }
                        switch (src.Kind ?? "Direct")
                        {
                            // Incremental and Ramped read no descriptor. Their
                            // up and down keys read the gesture families
                            // (SourceKindRuntime.ReadButtonLikeBool).
                            case "Incremental":
                            case "Ramped":
                                any |= Classify(src.ParamUp, ref need);
                                any |= Classify(src.ParamDown, ref need);
                                break;
                            // On a stick row the steering kinds read axes
                            // (SourceKindRuntime.SteeringAxisRead) and the
                            // motion kinds read gravity or the shake envelope.
                            // On any other row they fall to the Direct read of
                            // the descriptor.
                            case "WindingStick":
                            case "AngleToAxisX":
                            case "AngleToAxisY":
                            case "MotionLeanX":
                            case "MotionLeanAuxX":
                            case "MotionShake":
                            case "MotionShakeAux":
                                if (!stickRow) any |= Classify(src.Descriptor, ref need);
                                break;
                            default:
                                any |= Classify(src.Descriptor, ref need);
                                break;
                        }
                        // v18: the per-source AND gate reads through the
                        // same gated families (a click gated on a touch
                        // spot), so it arms exactly like a descriptor.
                        // v26's second AND companion arms identically. A
                        // blank Direct source reads neither.
                        if (SourceEvaluator.IsUnmappedDirect(src)) continue;
                        any |= Classify(src.GateDescriptor, ref need);
                        any |= Classify(src.Gate2Descriptor, ref need);
                    }
                }
            }
            if (set.ShiftActivators != null)
            {
                foreach (var act in set.ShiftActivators)
                {
                    if (act == null) continue;
                    // What the activator read takes (ReadActivatorInput in
                    // Step 3): nothing for a Passive layer or one without an
                    // input of its own, else the input and its second AND
                    // companion, plus the second input for a Chord and the
                    // AND gate for an Axis activator. A kind or mode change
                    // keeps the old fields, which arm nothing.
                    if (!string.IsNullOrEmpty(act.Descriptor)
                        && !string.Equals(act.Mode, "Passive", StringComparison.Ordinal))
                    {
                        any |= Classify(act.Descriptor, ref need);
                        any |= Classify(act.Gate2Descriptor, ref need);
                        switch (act.Kind ?? "Button")
                        {
                            case "Chord":
                                any |= Classify(act.ChordSecondDescriptor, ref need);
                                break;
                            case "Axis":
                                any |= Classify(act.GateDescriptor, ref need);
                                break;
                        }
                    }
                    // The Cycle mode's Previous button, read through the
                    // same button read on its own.
                    if (string.Equals(act.Mode, "Cycle", StringComparison.Ordinal))
                        any |= Classify(act.CyclePrevDescriptor, ref need);
                }
            }
            if (extraDescriptors != null)
            {
                foreach (var d in extraDescriptors)
                    any |= Classify(d, ref need);
            }

            if (!any || Satisfied(resolved, in need)) return resolved;

            var armed = resolved.Clone();
            if (need.Master)
            {
                armed.Enabled = true;
                // The Mode selector can block in-box fires wholesale. A
                // referenced in-box family must fire, so a CustomOnly mode
                // widens to Both, which keeps custom shapes working too.
                if (string.Equals(armed.Mode, "CustomOnly", StringComparison.OrdinalIgnoreCase))
                    armed.Mode = "Both";
            }
            if (need.Taps) armed.EnableTaps = true;
            if (need.FourWaySwipes) armed.EnableFourWaySwipes = true;
            if (need.EightWaySwipes) armed.EnableEightWaySwipes = true;
            if (need.TwoFingerSwipes) armed.EnableTwoFingerSwipes = true;
            if (need.ThreeFinger) armed.EnableThreeFingerGestures = true;
            if (need.FourFinger) armed.EnableFourFingerGestures = true;
            if (need.FiveFinger) armed.EnableFiveFingerGestures = true;
            if (need.TouchSpots) armed.EnableTouchSpots = true;
            if (need.LongPress) armed.EnableLongPress = true;
            if (need.RadialZones)
            {
                armed.EnableRadialZones = true;
                // With a conflict, no count satisfies every descriptor, so
                // an existing explicit setting is left alone: the user's own
                // pick keeps working instead of being overwritten by
                // whichever descriptor the walk reached first.
                if (need.RadialZoneCount > 0
                    && (!need.RadialZoneCountConflict || armed.RadialZoneCount <= 0))
                    armed.RadialZoneCount = need.RadialZoneCount;
            }
            if (need.PinchSpread) armed.EnablePinchSpread = true;
            if (need.Rotate) armed.EnableRotate = true;
            if (need.Joystick) armed.EnableJoystickOutput = true;
            if (need.JoystickDPad
                && string.Equals(armed.JoystickDPadMode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                armed.JoystickDPadMode = "FourWay";
            }
            return armed;
        }

        /// <summary>True when every referenced feature already reads live on
        /// <paramref name="s"/>, so no clone is needed.</summary>
        private static bool Satisfied(TouchpadGestureSettings s, in Needs need)
        {
            if (need.Master)
            {
                if (!s.Enabled) return false;
                if (string.Equals(s.Mode, "CustomOnly", StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (need.Taps && !s.EnableTaps) return false;
            if (need.FourWaySwipes && !s.EnableFourWaySwipes) return false;
            if (need.EightWaySwipes && !s.EnableEightWaySwipes) return false;
            if (need.TwoFingerSwipes && !s.EnableTwoFingerSwipes) return false;
            if (need.ThreeFinger && !s.EnableThreeFingerGestures) return false;
            if (need.FourFinger && !s.EnableFourFingerGestures) return false;
            if (need.FiveFinger && !s.EnableFiveFingerGestures) return false;
            if (need.TouchSpots && !s.EnableTouchSpots) return false;
            if (need.LongPress && !s.EnableLongPress) return false;
            if (need.RadialZones)
            {
                if (!s.EnableRadialZones) return false;
                // An unsatisfiable count demand must not report "not
                // satisfied" forever, or every call clones a fresh settings
                // object to chase a target no single value can hit.
                if (need.RadialZoneCount > 0 && !need.RadialZoneCountConflict
                    && s.RadialZoneCount != need.RadialZoneCount) return false;
                if (need.RadialZoneCountConflict && s.RadialZoneCount <= 0) return false;
            }
            if (need.PinchSpread && !s.EnablePinchSpread) return false;
            if (need.Rotate && !s.EnableRotate) return false;
            if (need.Joystick && !s.EnableJoystickOutput) return false;
            if (need.JoystickDPad
                && string.Equals(s.JoystickDPadMode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        /// <summary>Classifies one descriptor into the feature demands it
        /// needs and merges them into <paramref name="need"/> on success.
        /// Returns true when the descriptor is a gated gesture read this
        /// table recognizes. Name vocabulary mirrors
        /// <see cref="GestureRecognizer"/>'s fired-set keys plus the
        /// joystick output channels read by
        /// <see cref="GestureRecognizer.ComputeJoystickAxis"/> and
        /// <see cref="GestureRecognizer.ComputeJoystickDPad"/>.</summary>
        private static bool Classify(string descriptor, ref Needs need)
        {
            if (string.IsNullOrEmpty(descriptor)) return false;
            if (!SourceCoercion.TryParseTouchpadGesture(descriptor, out _, out string name))
                return false;
            if (!TryClassifyName(name, out Needs local)) return false;
            need.Merge(in local);
            return true;
        }

        /// <summary>The gesture-name table. Fills <paramref name="need"/>
        /// (a fresh local, merged by the caller only on success) and
        /// returns false for names outside the recognized vocabulary.</summary>
        private static bool TryClassifyName(string name, out Needs need)
        {
            need = new Needs();
            // A half-hosted trackpad qualifies its gestures with the half they
            // started on ("SwipeUp Left"). The half decides WHICH binding
            // answers, never which family has to be armed, so it comes off
            // before the table below sees the name.
            if (name.EndsWith(" Left", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 5);
            else if (name.EndsWith(" Right", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 6);
            switch (name)
            {
                case "StickX":
                case "StickY":
                    need.Joystick = true;
                    return true;
                case "DPadUp":
                case "DPadDown":
                case "DPadLeft":
                case "DPadRight":
                    need.Joystick = true;
                    need.JoystickDPad = true;
                    return true;
                case "TouchLeft":
                case "TouchRight":
                case "TouchTop":
                case "TouchMulti":
                    need.Master = true;
                    need.TouchSpots = true;
                    return true;
                case "LongPress":
                    need.Master = true;
                    need.LongPress = true;
                    return true;
                case "Pinch":
                case "Spread":
                case "PinchAxis":
                    need.Master = true;
                    need.PinchSpread = true;
                    return true;
                case "RotateCW":
                case "RotateCCW":
                case "RotateAxis":
                    need.Master = true;
                    need.Rotate = true;
                    return true;
            }

            // Multi-finger prefixes compose with the tap and swipe
            // families. In the recognizer, 3+ finger fires gate on the
            // count toggle AND the family toggle, 2-finger taps gate on
            // EnableTaps, and 2-finger swipes on EnableTwoFingerSwipes.
            string rest = name;
            bool multiSwipeFamily = false;
            if (rest.StartsWith("TwoFinger", StringComparison.Ordinal))
            {
                rest = rest.Substring("TwoFinger".Length);
                multiSwipeFamily = true;
            }
            else if (rest.StartsWith("ThreeFinger", StringComparison.Ordinal))
            {
                rest = rest.Substring("ThreeFinger".Length);
                need.ThreeFinger = true;
                multiSwipeFamily = true;
            }
            else if (rest.StartsWith("FourFinger", StringComparison.Ordinal))
            {
                rest = rest.Substring("FourFinger".Length);
                need.FourFinger = true;
                multiSwipeFamily = true;
            }
            else if (rest.StartsWith("FiveFinger", StringComparison.Ordinal))
            {
                rest = rest.Substring("FiveFinger".Length);
                need.FiveFinger = true;
                multiSwipeFamily = true;
            }

            if (rest == "Tap" || rest == "DoubleTap" || rest == "TripleTap")
            {
                need.Master = true;
                need.Taps = true;
                return true;
            }
            if (rest.StartsWith("Swipe", StringComparison.Ordinal))
            {
                string dir = rest.Substring("Swipe".Length);
                bool diagonal = dir is "NE" or "NW" or "SE" or "SW";
                bool cardinal = dir is "Up" or "Down" or "Left" or "Right";
                if (!diagonal && !cardinal) return false;
                need.Master = true;
                if (multiSwipeFamily) need.TwoFingerSwipes = true;
                else if (cardinal) need.FourWaySwipes = true;
                if (diagonal) need.EightWaySwipes = true;
                return true;
            }
            if (!multiSwipeFamily && name.StartsWith("RadialZone", StringComparison.Ordinal))
            {
                // "RadialZone{count}_{index}": the count is part of the
                // descriptor, so arming carries it onto the settings.
                int us = name.IndexOf('_');
                if (us > "RadialZone".Length
                    && int.TryParse(name.Substring("RadialZone".Length, us - "RadialZone".Length),
                        out int zones)
                    && zones > 0)
                {
                    need.Master = true;
                    need.RadialZones = true;
                    need.RadialZoneCount = zones;
                    return true;
                }
            }
            return false;
        }
    }
}
