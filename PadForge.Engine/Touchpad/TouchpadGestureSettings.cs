using System.Xml.Serialization;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// Per-(device, touchpad-index) gesture detection settings. Round-trips
    /// through PadSetting XML alongside the other per-device tuning.
    /// Every feature toggle defaults to off — touchpad mappings are opt-in.
    /// The user enables the master switch and individual gesture / joystick
    /// features as they want them. Numeric thresholds keep calibrated
    /// defaults so a feature works correctly once the user turns it on.
    /// </summary>
    public sealed class TouchpadGestureSettings
    {
        /// <summary>Master enable for this touchpad's gesture detection.</summary>
        [XmlAttribute] public bool Enabled { get; set; }

        /// <summary>Detection mode: <c>InBoxOnly</c>, <c>CustomOnly</c>, <c>Both</c>.</summary>
        [XmlAttribute] public string Mode { get; set; } = "Both";

        /// <summary>Cooldown (ms) between gesture fires. Prevents bounce.</summary>
        [XmlAttribute] public int CooldownMs { get; set; } = 100;

        // ─── Tier 1: swipes / radial / taps / longpress ───────────────

        /// <summary>Minimum motion (normalized pad units 0..1) for a
        /// finger lift to register as a swipe. 0.15 = 15% of pad span.</summary>
        [XmlAttribute] public float SwipeDistanceThreshold { get; set; } = 0.15f;

        /// <summary>Maximum elapsed time (ms) between finger-down and
        /// finger-up for a swipe to fire. Longer gestures are dwells
        /// rather than swipes.</summary>
        [XmlAttribute] public int SwipeTimeWindowMs { get; set; } = 500;

        /// <summary>4-way swipe gestures enabled (Up/Down/Left/Right).</summary>
        [XmlAttribute] public bool EnableFourWaySwipes { get; set; }

        /// <summary>8-way diagonals enabled (NE/NW/SE/SW). Composes with
        /// <see cref="EnableFourWaySwipes"/>: with both on, swipes
        /// classify into 8 buckets; with only 4-way on, diagonals fold
        /// to the nearest axial.</summary>
        [XmlAttribute] public bool EnableEightWaySwipes { get; set; }

        /// <summary>Radial-zone fire on a finger held in an angular
        /// sector past <see cref="RadialCenterDeadzone"/>.</summary>
        [XmlAttribute] public bool EnableRadialZones { get; set; }

        /// <summary>Number of radial zones: 4 / 6 / 8 / 12.</summary>
        [XmlAttribute] public int RadialZoneCount { get; set; } = 8;

        /// <summary>Center dead zone (normalized pad units 0..1). A
        /// finger within this radius of the starting point doesn't
        /// register a zone.</summary>
        [XmlAttribute] public float RadialCenterDeadzone { get; set; } = 0.30f;

        /// <summary>Tap / DoubleTap / TripleTap gestures enabled.</summary>
        [XmlAttribute] public bool EnableTaps { get; set; }

        /// <summary>Maximum elapsed time (ms) for a tap gesture (down→up).
        /// 350 ms is a forgiving default — many users naturally hold a
        /// finger past 200 ms before lifting, especially on small
        /// touchpads. Tighten to 200-250 ms if you want crisp rapid
        /// tap-vs-longpress separation, loosen to 500 ms for slower
        /// taps. Long-press fires at 500 ms by default so 350 leaves
        /// a 150 ms band between tap and long-press.</summary>
        [XmlAttribute] public int TapTimeWindowMs { get; set; } = 350;

        /// <summary>Maximum motion (normalized pad units) for a tap.
        /// Larger movements register as swipes or longpresses. 0.04 is
        /// looser than the prior 0.02 default — a finger landing on a
        /// touchpad rarely stays inside a 2% radius without sliding a
        /// few pixels mid-tap, so 4% catches genuine taps that drift
        /// slightly without firing on intentional small swipes
        /// (SwipeDistanceThreshold defaults to 15%).</summary>
        [XmlAttribute] public float TapMaxMotion { get; set; } = 0.04f;

        /// <summary>Maximum gap (ms) between successive taps for the
        /// later one to count as DoubleTap / TripleTap rather than
        /// a fresh single tap.</summary>
        [XmlAttribute] public int MultiTapGapMs { get; set; } = 300;

        /// <summary>LongPress gestures enabled (single finger held).</summary>
        [XmlAttribute] public bool EnableLongPress { get; set; }

        /// <summary>Minimum duration (ms) of finger contact for a
        /// LongPress to fire.</summary>
        [XmlAttribute] public int LongPressTimeWindowMs { get; set; } = 500;

        /// <summary>Maximum motion (normalized pad units) for a
        /// LongPress to fire. Movement past this turns the gesture
        /// into a swipe candidate instead. 0.05 (5%) accommodates the
        /// finger drift that naturally accumulates over a 500 ms hold —
        /// the prior 2% default was tighter than realistic human
        /// finger jitter and caused long-press to fire unreliably even
        /// when the user clearly meant a hold (SwipeDistanceThreshold
        /// defaults to 15%, so 5% stays well clear of the swipe band).
        /// Tap uses 4% on its shorter 350 ms window for the same
        /// reason; long-press needs a touch more headroom.</summary>
        [XmlAttribute] public float LongPressMaxMotion { get; set; } = 0.05f;

        // ─── Tier 2: multi-finger ─────────────────────────────────────

        /// <summary>Two-finger swipe gestures enabled.</summary>
        [XmlAttribute] public bool EnableTwoFingerSwipes { get; set; }

        /// <summary>Maximum angular tolerance (degrees) between the two
        /// fingers' motion vectors for the gesture to count as a
        /// two-finger swipe (vs a pinch/spread).</summary>
        [XmlAttribute] public float TwoFingerSwipeAngularTolerance { get; set; } = 25f;

        /// <summary>Pinch / Spread gestures enabled.</summary>
        [XmlAttribute] public bool EnablePinchSpread { get; set; }

        /// <summary>Fractional distance change (relative to initial
        /// 2-finger distance) past which a pinch fires.</summary>
        [XmlAttribute] public float PinchThreshold { get; set; } = 0.25f;

        /// <summary>Rotate gestures enabled.</summary>
        [XmlAttribute] public bool EnableRotate { get; set; }

        /// <summary>Angular change (degrees) past which a rotate fires.</summary>
        [XmlAttribute] public float RotateThresholdDegrees { get; set; } = 20f;

        /// <summary>Three-finger gestures enabled (devices with 3+ fingers).</summary>
        [XmlAttribute] public bool EnableThreeFingerGestures { get; set; }

        /// <summary>Four-finger gestures enabled (devices with 4+ fingers).</summary>
        [XmlAttribute] public bool EnableFourFingerGestures { get; set; }

        /// <summary>Five-finger gestures enabled (Windows PTP only).</summary>
        [XmlAttribute] public bool EnableFiveFingerGestures { get; set; }

        // ─── Tier 3: shape templates ──────────────────────────────────

        /// <summary>In-box shape gestures (Circle/Square/Triangle/Z/
        /// Checkmark) enabled. Custom user gestures have their own
        /// per-gesture toggles in the profile's gesture library.</summary>
        [XmlAttribute] public bool EnableShapeGestures { get; set; }

        /// <summary>Point-cloud shape-recognizer match threshold (the
        /// engine uses <see cref="ShapeRecognizer"/>'s $Q implementation).
        /// Lower = stricter (fewer false-positives), higher = looser
        /// (more matches). 3.0 keeps the same scale the prior $P
        /// implementation used so user-tuned values transfer across
        /// the $P → $Q migration without re-tuning. The angular-margin
        /// matcher that runs alongside the point-cloud recognizer uses
        /// its own per-axis tolerance internally.</summary>
        [XmlAttribute] public float GestureMatchThreshold { get; set; } = 3.0f;

        // ─── Joystick / D-pad output (anchor-relative continuous) ───────
        //
        // Separate feature from gesture recognition. Treats the touchpad
        // as a virtual analog stick (and/or D-pad) where the finger's
        // landing position becomes the centre and current-minus-anchor
        // delta drives stick X/Y or wedge-thresholded D-pad output. Lives
        // in the same per-(device, pad) settings shape because the data
        // model already supports it and the user picks both gesture
        // toggles and joystick toggles from the same Touchpad tab.

        /// <summary>Master enable for the touchpad's joystick / D-pad
        /// output channel. Off by default so existing users keep their
        /// behavior; on enables the Touchpad N JoystickX/Y and
        /// JoystickDPad* descriptors in the mapping picker.</summary>
        [XmlAttribute] public bool EnableJoystickOutput { get; set; }

        /// <summary>Distance from anchor (normalized 0..1 touchpad units)
        /// at which stick output saturates to ±1. Smaller = twitchier,
        /// larger = more travel. 0.30 = half the pad in either direction
        /// gives full stick deflection.</summary>
        [XmlAttribute] public float JoystickMaxRadius { get; set; } = 0.30f;

        /// <summary>Magnitude below this maps stick output to (0, 0).
        /// Prevents sub-millimeter finger drift from registering as
        /// slow stick input.</summary>
        [XmlAttribute] public float JoystickInnerDeadzone { get; set; } = 0.02f;

        /// <summary>D-pad output mode: "Off", "FourWay", "EightWay".
        /// 4-way emits one cardinal at a time; 8-way emits two cardinals
        /// for diagonals (matches physical D-pads on Xbox / PS pads).</summary>
        [XmlAttribute] public string JoystickDPadMode { get; set; } = "FourWay";

        /// <summary>Minimum distance from anchor (normalized 0..1) for
        /// any D-pad direction to fire. Independent of stick output's
        /// inner deadzone so users can dial in tactile-D-pad-style
        /// snap separately from analog feel.</summary>
        [XmlAttribute] public float JoystickDPadActivationThreshold { get; set; } = 0.15f;

        // ─── Mouse output (touchpad-as-mouse tuning) ───────────────────
        //
        // Applied by SourceCoercion.TryReadTouchpadAxis on the per-frame
        // delta when a touchpad finger X/Y descriptor feeds a KBM mouse
        // target. The base delta scale (TouchpadDeltaScale = 128) gives
        // a 1:1 sweep across a 1920-wide screen at sensitivity 1.0; the
        // multipliers below let the user dial in slower/faster cursor
        // feel and flip either axis without leaving the touchpad tab.

        /// <summary>Multiplier on horizontal touchpad-to-mouse delta. 1.0
        /// is the calibrated baseline (a full horizontal pad sweep
        /// moves the cursor ~1920 pixels). Below 1.0 = slower cursor,
        /// above 1.0 = faster.</summary>
        [XmlAttribute] public float MouseSensitivityX { get; set; } = 1.0f;

        /// <summary>Multiplier on vertical touchpad-to-mouse delta.</summary>
        [XmlAttribute] public float MouseSensitivityY { get; set; } = 1.0f;

        /// <summary>Flip horizontal touchpad-to-mouse delta. Finger right
        /// → cursor left when on.</summary>
        [XmlAttribute] public bool MouseInvertX { get; set; }

        /// <summary>Flip vertical touchpad-to-mouse delta. Finger down →
        /// cursor up when on.</summary>
        [XmlAttribute] public bool MouseInvertY { get; set; }

        public TouchpadGestureSettings Clone()
        {
            return new TouchpadGestureSettings
            {
                Enabled = Enabled,
                Mode = Mode,
                CooldownMs = CooldownMs,
                SwipeDistanceThreshold = SwipeDistanceThreshold,
                SwipeTimeWindowMs = SwipeTimeWindowMs,
                EnableFourWaySwipes = EnableFourWaySwipes,
                EnableEightWaySwipes = EnableEightWaySwipes,
                EnableRadialZones = EnableRadialZones,
                RadialZoneCount = RadialZoneCount,
                RadialCenterDeadzone = RadialCenterDeadzone,
                EnableTaps = EnableTaps,
                TapTimeWindowMs = TapTimeWindowMs,
                TapMaxMotion = TapMaxMotion,
                MultiTapGapMs = MultiTapGapMs,
                EnableLongPress = EnableLongPress,
                LongPressTimeWindowMs = LongPressTimeWindowMs,
                LongPressMaxMotion = LongPressMaxMotion,
                EnableTwoFingerSwipes = EnableTwoFingerSwipes,
                TwoFingerSwipeAngularTolerance = TwoFingerSwipeAngularTolerance,
                EnablePinchSpread = EnablePinchSpread,
                PinchThreshold = PinchThreshold,
                EnableRotate = EnableRotate,
                RotateThresholdDegrees = RotateThresholdDegrees,
                EnableThreeFingerGestures = EnableThreeFingerGestures,
                EnableFourFingerGestures = EnableFourFingerGestures,
                EnableFiveFingerGestures = EnableFiveFingerGestures,
                EnableShapeGestures = EnableShapeGestures,
                GestureMatchThreshold = GestureMatchThreshold,
                EnableJoystickOutput = EnableJoystickOutput,
                JoystickMaxRadius = JoystickMaxRadius,
                JoystickInnerDeadzone = JoystickInnerDeadzone,
                JoystickDPadMode = JoystickDPadMode,
                JoystickDPadActivationThreshold = JoystickDPadActivationThreshold,
                MouseSensitivityX = MouseSensitivityX,
                MouseSensitivityY = MouseSensitivityY,
                MouseInvertX = MouseInvertX,
                MouseInvertY = MouseInvertY,
            };
        }

        public static TouchpadGestureSettings Default() => new TouchpadGestureSettings();
    }
}
