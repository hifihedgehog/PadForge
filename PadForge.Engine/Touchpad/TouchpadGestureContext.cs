using System.Collections.Generic;
using System.Numerics;

namespace PadForge.Engine.Touchpad
{
    /// <summary>Lifecycle state of one touchpad's gesture detection.</summary>
    public enum GestureState
    {
        /// <summary>No fingers in contact. Waiting for a finger-down.</summary>
        Idle,
        /// <summary>At least one finger in contact. Path is accumulating;
        /// Tier 1 / Tier 2 detectors may fire mid-gesture (radial-zone,
        /// longpress, pinch threshold crossing).</summary>
        Accumulating,
        /// <summary>All fingers just lifted; ran the end-of-gesture
        /// recognition pass (Tier 1 swipe direction, Tier 3 shape match).
        /// Transitions immediately to Cooldown.</summary>
        Recognizing,
        /// <summary>Post-gesture quiet period to prevent bounce-fire.
        /// <see cref="TouchpadGestureSettings.CooldownMs"/>.</summary>
        Cooldown,
    }

    /// <summary>
    /// Per-(device, touchpad-index) runtime context for the gesture
    /// recognizer. Holds the in-progress path, the per-frame fire set,
    /// continuous-axis state, and tap-counter scratch. One context per
    /// physical touchpad surface; multi-pad devices have one context
    /// per pad, walked independently each polling tick.
    /// </summary>
    public sealed class TouchpadGestureContext
    {
        public GestureState State;

        /// <summary>One path per finger that was in contact at any point
        /// during this gesture. Indexed by the order fingers touched
        /// down, not by hardware slot index — a finger lifting and a
        /// new one landing in the same slot opens a new path. Cleared
        /// at the end of every gesture (when the cooldown expires).</summary>
        public List<List<Vector2>> FingerPaths = new List<List<Vector2>>();

        /// <summary>Timestamps (ms since some epoch) of each finger's
        /// initial contact. Paired with <see cref="FingerPaths"/>.</summary>
        public List<long> FingerStartTimestampsMs = new List<long>();

        /// <summary>Contact IDs at the moment each path opened. Used to
        /// detect "finger N is still down in the same slot vs a new
        /// contact landed there" across polling ticks.</summary>
        public List<int> FingerContactIds = new List<int>();

        /// <summary>Hardware slot index each path is attached to. Lets
        /// the per-tick update find each path's current position by
        /// reading <c>pad.FingerX[slot]</c>.</summary>
        public List<int> FingerSlotIndices = new List<int>();

        /// <summary>Number of fingers currently in contact across all
        /// paths. Equal to the count of paths whose contact ID is
        /// still active.</summary>
        public int ActiveFingerCount;

        public long GestureStartTimestampMs;
        public long CooldownUntilTimestampMs;

        // ─── Tap-counter scratch ─────────────────────────────────────

        /// <summary>Number of consecutive Taps that have fired within
        /// <see cref="TouchpadGestureSettings.MultiTapGapMs"/> of each
        /// other; resets after the gap elapses. Drives DoubleTap /
        /// TripleTap classification.</summary>
        public int RecentTapCount;
        public long LastTapEndTimestampMs;
        public Vector2 LastTapPosition;

        // ─── Continuous gesture state (Tier 2 axes) ──────────────────

        /// <summary>Bipolar -1..+1 pinch progress when 2 fingers are
        /// active and the pinch-axis source descriptor is mapped.
        /// Negative = pinching closed, positive = spreading open. Held
        /// at last value when the 2-finger session ends until reset
        /// by the next 2-finger gesture.</summary>
        public float CurrentPinchAxis;

        /// <summary>Bipolar -1..+1 rotation angle delta in normalized
        /// units (full -π..+π folded into -1..+1). Held at last value
        /// across 2-finger sessions.</summary>
        public float CurrentRotateAxis;

        /// <summary>True from the first frame both fingers are in
        /// contact until either lifts. Drives Tier 2 baseline capture
        /// (<see cref="TwoFingerInitialDistance"/> +
        /// <see cref="TwoFingerInitialAngle"/>) on entry.</summary>
        public bool TwoFingerSessionActive;

        /// <summary>Initial distance between fingers when the 2-finger
        /// session opened. Pinch/spread compare against this.</summary>
        public float TwoFingerInitialDistance;

        /// <summary>Initial angle of the inter-finger line when the
        /// 2-finger session opened. Rotate compares against this.</summary>
        public float TwoFingerInitialAngle;

        /// <summary>Whether Pinch / Spread / RotateCW / RotateCCW have
        /// already fired in this 2-finger session. One-shot per
        /// session per direction; resets when the session ends.</summary>
        public bool FiredPinchThisSession;
        public bool FiredSpreadThisSession;
        public bool FiredRotateCWThisSession;
        public bool FiredRotateCCWThisSession;

        // ─── Radial zone state ────────────────────────────────────────

        /// <summary>Most recently fired radial zone index. -1 if none
        /// is currently fired. Subsequent re-entries to the same zone
        /// don't re-fire; crossing to a different zone fires that one.</summary>
        public int CurrentRadialZone = -1;

        // ─── Per-frame fire set ──────────────────────────────────────

        /// <summary>Gesture names that fired on this tick. Read by
        /// <see cref="PadForge.Engine.Common.Mapping.SourceCoercion"/>
        /// to drive the gesture-source descriptor reads. Cleared at
        /// the top of each <see cref="GestureRecognizer.Update"/> call.</summary>
        public HashSet<string> FiredGesturesThisFrame = new HashSet<string>();

        public void Reset()
        {
            State = GestureState.Idle;
            FingerPaths.Clear();
            FingerStartTimestampsMs.Clear();
            FingerContactIds.Clear();
            FingerSlotIndices.Clear();
            ActiveFingerCount = 0;
            GestureStartTimestampMs = 0;
            CooldownUntilTimestampMs = 0;
            RecentTapCount = 0;
            LastTapEndTimestampMs = 0;
            LastTapPosition = Vector2.Zero;
            CurrentPinchAxis = 0f;
            CurrentRotateAxis = 0f;
            TwoFingerSessionActive = false;
            TwoFingerInitialDistance = 0f;
            TwoFingerInitialAngle = 0f;
            FiredPinchThisSession = false;
            FiredSpreadThisSession = false;
            FiredRotateCWThisSession = false;
            FiredRotateCCWThisSession = false;
            CurrentRadialZone = -1;
            FiredGesturesThisFrame.Clear();
        }
    }
}
