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

        /// <summary>Touch-spot zones enabled (Left / Right / Top /
        /// Multitouch). Held-state buttons over where the pad is being
        /// touched: 2+ fingers asserts Multitouch; a single finger in
        /// the top quarter asserts Top, otherwise Left / Right split at
        /// 2/5 of the width (the boundary DS4Windows uses). Exactly one
        /// spot asserts at a time and releases on lift.</summary>
        [XmlAttribute] public bool EnableTouchSpots { get; set; }

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

        /// <summary><para>Momentum: the cursor keeps travelling after the
        /// finger lifts, coasting to a stop instead of halting dead. This is
        /// the trackball feel the Steam Controller's own lizard mode has, and
        /// it is most of why flicking across a pad there covers ground a
        /// finger-length swipe cannot.</para>
        /// <para>Off by default. A cursor that keeps moving after release is
        /// a deliberate choice, not something to surprise anyone with.</para>
        /// </summary>
        [XmlAttribute] public bool MouseMomentum { get; set; }

        /// <summary><para>How long the coast lasts: the fraction of speed
        /// kept after each 10 ms of travel. The band is 0.80 to 1.00 and the
        /// default 0.90 sits at its midpoint, which glides about twice as far
        /// as the first cut did.</para>
        /// <para>1.00 is FRICTIONLESS. The cursor keeps its speed until you
        /// touch the pad again, which is what a real trackball does when you
        /// spin it and let go, and touching down stops it. That is the only
        /// stop at 1.00, by design.</para>
        /// <para>Per unit TIME rather than per poll, so the glide lasts the
        /// same wall-clock duration at any polling rate.</para></summary>
        [XmlAttribute] public float MouseMomentumDecay { get; set; } = 0.90f;

        /// <summary><para>Jitter reduction: bends motion below the threshold
        /// down a power curve instead of cutting it off, so resting-hand
        /// tremor is damped while fine movement stays continuous. Same shape
        /// the gyro lane uses, which came from DS4Windows'
        /// jitterCompensation.</para>
        /// <para>A deadzone would delete the small motion outright, which is
        /// what makes fine cursor work feel dead. This keeps it, just
        /// smaller.</para></summary>
        [XmlAttribute] public bool MouseJitterReduction { get; set; } = true;

        // ─── Absolute pointer output (#9 B-15) ─────────────────────────
        //
        // Applied by SourceCoercion.ReadTunedTouchpadPointer when a
        // "Touchpad N Pointer X/Y" descriptor feeds a KBM mouse target
        // (the absolute cursor lane). The margin stretch is the Wii aim
        // map's concept (SourceCoercion.IrMarginStretchX/Y, the Touchmote
        // pointer_margins lineage): values above 1.0 let a thumb that
        // stops short of the pad bezel still reach the screen edges.
        // Default 1.0 = Steam's 1:1 mouse_region map ("touching a
        // particular place on the pad will always put the cursor in the
        // same place on the screen"), which needs no stretch because a
        // finger CAN reach the pad edges.

        /// <summary><para>Width of the screen rectangle this pad maps onto,
        /// as a fraction of screen width. 1.0 is the full-screen 1:1 map;
        /// 0.5 confines the cursor to the middle half; 1.2 runs the region
        /// wider than the screen so the edges are reached before the pad
        /// bezel.</para>
        /// <para>SUPERSEDES PointerStretchX, which was the same quantity
        /// under a worse name and a floor of 1.0. The two are algebraically
        /// identical at the default center: with u = 2*raw-1, a stretch S
        /// gives clamp(u*S) and an extent S gives clamp(u*S). The floor was
        /// the live defect, since it could not express any region SMALLER
        /// than the screen, which is what most Steam mouse_region configs
        /// author (5 of the 6 extents in the translation corpus are below
        /// 1.0). Old profiles migrate in SettingsService.</para></summary>
        [XmlAttribute] public float PointerRegionSizeX { get; set; } = 1.0f;

        /// <summary>Height of the screen rectangle, as a fraction of screen
        /// height. Supersedes PointerStretchY.</summary>
        [XmlAttribute] public float PointerRegionSizeY { get; set; } = 1.0f;

        /// <summary>Horizontal center of the screen rectangle, 0 = left
        /// edge, 1 = right edge. Steam's mouse_region position_x. Had no
        /// representation at all before, so an imported corner region (AOE
        /// II maps a pad to the bottom-left menu at 0.09/0.90) could be
        /// imported but never seen or edited.</summary>
        [XmlAttribute] public float PointerRegionCenterX { get; set; } = 0.5f;

        /// <summary>Vertical center, 0 = TOP edge, 1 = bottom. Note the
        /// origin: Steam's position_y is bottom-origin and the translator
        /// flips it (1 - y/100) on the way in, matching sc-controller's
        /// importer (scc/foreign/vdf.py "y = 1.0 - (y/100.0)").</summary>
        [XmlAttribute] public float PointerRegionCenterY { get; set; } = 0.5f;

        /// <summary><para>True once the user has touched the Absolute Pointer
        /// card, which hands this pad's region to the pad settings for good.
        /// </para>
        /// <para>It exists to keep a RESET honest. An imported Steam
        /// mouse_region carries its geometry on the mapping source (import
        /// runs before any device is assigned, so per-device settings cannot
        /// be written yet), and the engine reads that source geometry until
        /// the card is used. Were the handover keyed on "the pad's region is
        /// still 0.5/1.0" instead of this flag, a user who deliberately set
        /// the region back to full screen would land on exactly that
        /// condition and silently get the imported rectangle back, with no
        /// way to ever undo it.</para></summary>
        [XmlAttribute] public bool PointerRegionAuthored { get; set; }

        // ── Legacy read path for the superseded stretch pair ──
        //
        // A profile saved before the region rename carries PointerStretchX/Y.
        // Without these shims XmlSerializer drops the unknown attributes and
        // the user's tuning vanishes on first load. Deserialize-only: both
        // ShouldSerialize hooks return false, so nothing ever writes them
        // back and the file converges to the region names after one save.
        // Safe because stretch S and region size S are the same quantity.

        // Both setters claim authorship. A profile that carried a stretch had
        // a region the user chose, so it must keep winning over whatever an
        // imported mapping source says; without this the old value would
        // deserialize correctly and then be ignored by the read.
        [XmlAttribute] public float PointerStretchX
        {
            get => PointerRegionSizeX;
            set { PointerRegionSizeX = value; PointerRegionAuthored = true; }
        }

        [XmlAttribute] public float PointerStretchY
        {
            get => PointerRegionSizeY;
            set { PointerRegionSizeY = value; PointerRegionAuthored = true; }
        }

        public bool ShouldSerializePointerStretchX() => false;

        public bool ShouldSerializePointerStretchY() => false;

        // ─── Swipe haptics (discussion #219) ───────────────────────────
        //
        // Steam-Input-style trackpad feel: a short haptic tick fires each
        // time the finger travels a fixed distance across the pad
        // (SwipeHapticsEvaluator accumulates travel per finger and emits
        // detents). Delivery is per device family. The Steam Controller
        // family pulses the pad-side actuator, Sony pads pulse the rumble
        // motors through the effects dispatcher.

        /// <summary>Master enable for swipe-haptic ticks on this pad.
        /// Off by default. The feature is opt-in per (device, pad).</summary>
        [XmlAttribute] public bool EnableSwipeHaptics { get; set; }

        /// <summary>Tick strength 0..1. 0.5 mirrors the Medium step of
        /// DS4MapperTest's HapticsIntensity ladder (Light 0.3 / Medium
        /// 0.5 / Heavy 0.8 / Full 1.0, MapAction.cs GetHapticsIntensityRatio).</summary>
        [XmlAttribute] public float SwipeHapticsIntensity { get; set; } = 0.5f;

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
                EnableTouchSpots = EnableTouchSpots,
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
                MouseMomentum = MouseMomentum,
                MouseMomentumDecay = MouseMomentumDecay,
                MouseJitterReduction = MouseJitterReduction,
                PointerRegionSizeX = PointerRegionSizeX,
                PointerRegionSizeY = PointerRegionSizeY,
                PointerRegionAuthored = PointerRegionAuthored,
                PointerRegionCenterX = PointerRegionCenterX,
                PointerRegionCenterY = PointerRegionCenterY,
                EnableSwipeHaptics = EnableSwipeHaptics,
                SwipeHapticsIntensity = SwipeHapticsIntensity,
            };
        }

        public static TouchpadGestureSettings Default() => new TouchpadGestureSettings();

        // ─── Per-device resolution ─────────────────────────────────────
        //
        // Touchpad gesture / gating settings are per-DEVICE: enabling a
        // setting applies to every touchpad the device enumerates (a Steam
        // Controller has 2 pads). The per-pad distinction survives only in
        // the output descriptor strings ("Touchpad 0 StickX" vs "Touchpad 1
        // StickX") the user picks in the mapping grid. The on-disk shape
        // keeps the TouchpadIndex attribute (no schema break); these
        // selectors collapse the array to one winner per device so every
        // read seam agrees mid-migration with legacy per-pad arrays.

        /// <summary>Winner-selection shared by every read seam. Among the
        /// entries whose DeviceGuid matches <paramref name="guidStr"/>, a
        /// user-configured entry beats a fresh <see cref="Default"/> one.
        /// Ties break to the lowest <c>TouchpadIndex</c>. Owner-accepted
        /// merge policy: two pads configured differently collapse to the
        /// lowest-index tuning, and the higher pad's tuning is dropped.
        /// Returns null when no entry matches.</summary>
        public static TouchpadSettingsEntry ResolveEntryForDevice(TouchpadSettingsEntry[] entries, string guidStr)
        {
            if (entries == null || string.IsNullOrEmpty(guidStr)) return null;
            TouchpadSettingsEntry best = null;
            foreach (var e in entries)
            {
                if (e?.Settings == null) continue;
                if (!string.Equals(e.DeviceGuid, guidStr, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null) { best = e; continue; }
                bool eCfg = IsConfigured(e.Settings), bCfg = IsConfigured(best.Settings);
                if (eCfg != bCfg) { if (eCfg) best = e; }
                else if (e.TouchpadIndex < best.TouchpadIndex) best = e;
            }
            return best;
        }

        /// <summary>The resolved settings bundle for a device, or
        /// <see cref="Default"/> when no entry matches. Every runtime read
        /// seam (gesture provider, mouse provider, VM loader, picker)
        /// funnels through this so they agree on which pad's tuning a
        /// device uses.</summary>
        public static TouchpadGestureSettings ResolveForDevice(TouchpadSettingsEntry[] entries, string guidStr)
            => ResolveEntryForDevice(entries, guidStr)?.Settings ?? Default();

        /// <summary>True when <paramref name="s"/> differs from
        /// <see cref="Default"/> in any user-facing way: any enable toggle
        /// on, any mouse / pointer / haptic tuning moved off its default,
        /// any threshold retuned. A configured entry outranks a pristine
        /// Default entry when the resolver picks a winner, so a pad the user
        /// set up mouse-only (masters off) still beats an untouched sibling.</summary>
        internal static bool IsConfigured(TouchpadGestureSettings s)
        {
            if (s == null) return false;
            var d = Default();
            return s.Enabled != d.Enabled
                || !string.Equals(s.Mode, d.Mode, System.StringComparison.Ordinal)
                || s.CooldownMs != d.CooldownMs
                || s.SwipeDistanceThreshold != d.SwipeDistanceThreshold
                || s.SwipeTimeWindowMs != d.SwipeTimeWindowMs
                || s.EnableFourWaySwipes != d.EnableFourWaySwipes
                || s.EnableEightWaySwipes != d.EnableEightWaySwipes
                || s.EnableRadialZones != d.EnableRadialZones
                || s.RadialZoneCount != d.RadialZoneCount
                || s.RadialCenterDeadzone != d.RadialCenterDeadzone
                || s.EnableTouchSpots != d.EnableTouchSpots
                || s.EnableTaps != d.EnableTaps
                || s.TapTimeWindowMs != d.TapTimeWindowMs
                || s.TapMaxMotion != d.TapMaxMotion
                || s.MultiTapGapMs != d.MultiTapGapMs
                || s.EnableLongPress != d.EnableLongPress
                || s.LongPressTimeWindowMs != d.LongPressTimeWindowMs
                || s.LongPressMaxMotion != d.LongPressMaxMotion
                || s.EnableTwoFingerSwipes != d.EnableTwoFingerSwipes
                || s.TwoFingerSwipeAngularTolerance != d.TwoFingerSwipeAngularTolerance
                || s.EnablePinchSpread != d.EnablePinchSpread
                || s.PinchThreshold != d.PinchThreshold
                || s.EnableRotate != d.EnableRotate
                || s.RotateThresholdDegrees != d.RotateThresholdDegrees
                || s.EnableThreeFingerGestures != d.EnableThreeFingerGestures
                || s.EnableFourFingerGestures != d.EnableFourFingerGestures
                || s.EnableFiveFingerGestures != d.EnableFiveFingerGestures
                || s.EnableShapeGestures != d.EnableShapeGestures
                || s.GestureMatchThreshold != d.GestureMatchThreshold
                || s.EnableJoystickOutput != d.EnableJoystickOutput
                || s.JoystickMaxRadius != d.JoystickMaxRadius
                || s.JoystickInnerDeadzone != d.JoystickInnerDeadzone
                || !string.Equals(s.JoystickDPadMode, d.JoystickDPadMode, System.StringComparison.Ordinal)
                || s.JoystickDPadActivationThreshold != d.JoystickDPadActivationThreshold
                || s.MouseSensitivityX != d.MouseSensitivityX
                || s.MouseSensitivityY != d.MouseSensitivityY
                || s.MouseInvertX != d.MouseInvertX
                || s.MouseInvertY != d.MouseInvertY
                || s.PointerRegionSizeX != d.PointerRegionSizeX
                || s.PointerRegionSizeY != d.PointerRegionSizeY
                || s.PointerRegionAuthored != d.PointerRegionAuthored
                || s.PointerRegionCenterX != d.PointerRegionCenterX
                || s.PointerRegionCenterY != d.PointerRegionCenterY
                || s.EnableSwipeHaptics != d.EnableSwipeHaptics
                || s.SwipeHapticsIntensity != d.SwipeHapticsIntensity;
        }
    }
}
