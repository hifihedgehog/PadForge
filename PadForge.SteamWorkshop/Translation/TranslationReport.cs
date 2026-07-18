using System.Collections.Generic;
using System.Text;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// One line of the translation preview: what a binding (or an aggregate,
    /// like a preset's passthrough recognition) became, or why it didn't.
    /// Reason text is NOT localized here. <see cref="ReasonKey"/> names a
    /// <c>Workshop_Tr_*</c> string resource (added with the Phase C/D resx
    /// pass) and <see cref="ReasonArgs"/> carries its format arguments, so
    /// the UI renders in the active culture while this DTO stays culture-free
    /// and serializable.
    /// </summary>
    public sealed class TranslationEntry
    {
        public TranslationStatus Status { get; set; }

        /// <summary>Localization key, e.g. <c>Workshop_Tr_UnknownKey</c>.</summary>
        public string ReasonKey { get; set; } = "";

        /// <summary>Format arguments for the localized reason string.</summary>
        public List<string> ReasonArgs { get; set; } = new();

        /// <summary>Where in the config the binding lives, e.g.
        /// <c>"Default/left_trackpad/group 1 (dpad)/dpad_north/Full_Press"</c>.</summary>
        public string SourcePath { get; set; } = "";

        /// <summary>The raw <c>binding</c> value string, when the entry is
        /// about a single binding. Empty for aggregates.</summary>
        public string Binding { get; set; } = "";

        /// <summary>Diagnostic trace of what was emitted, e.g.
        /// <c>"KbmKey57 &lt;- Touchpad 0 DPadUp"</c>. Not localized, not shown
        /// as primary UI text.</summary>
        public string Emitted { get; set; } = "";
    }

    /// <summary>
    /// Serializable result summary of one <see cref="ConfigTranslator"/> run.
    /// The Workshop preview UI binds to <see cref="Entries"/>; provenance
    /// (Phase D) stores <see cref="ToSummaryString"/>.
    /// </summary>
    public sealed class TranslationReport
    {
        /// <summary>Bumped when translation output changes shape, so
        /// update detection can flag "translator improved since import".
        /// The value rides provenance inside
        /// <see cref="ToSummaryString"/> ("v2 rows:..."), which the
        /// materializer stamps on SteamWorkshopSource.TranslationSummary.
        /// v2: Wave 1a (#9 follow-up). Adds single_button / gyro_to_mouse
        /// modes, Steam Controller digital-trigger switch members, #token
        /// titles, inner deadzone, set_led macros, Long_Press layer
        /// carries, and the named-skip vocabulary below.
        /// v3: Wave 2A. Long_Press key/button macros (HoldForMs trigger),
        /// xinput hold_repeats turbo, the activator toggle setting
        /// (ToggleVcButton / ToggleKey latches, Toggle-mode layer carries),
        /// camera_reset as a gyro recenter, and mouse_region cursor-clamp
        /// macros with region geometry.
        /// v4: Wave 3. Device-free InputDevice macro triggers replace the
        /// NoDeviceFreeTrigger skips (touchpad / paddle / gyro hosted
        /// macros, mouse_region engage on trackpads), single-pad trackpad
        /// halves (DS4 / DualSense left_/right_trackpad onto pad 0's
        /// region-windowed sources), four_buttons-on-trackpad quadrant
        /// collapse, and per-row touchpad mouse sensitivity replacing the
        /// TouchpadTuningNotPerRow drop.
        /// v5: Wave 4a (#225). The flickstick joystick mode emits a
        /// "Flick Stick Right"/"Flick Stick Left" KbmMouseX source with
        /// the group's "sensitivity" (Steam's Dots Per 360) carried on
        /// ParamFlickCountsPer360; trackpad-hosted flickstick and the
        /// ungrounded flick tuning keys get named entries.
        /// v6: Wave 4b (#9 B-15). Trackpad mouse_region groups emit Clean
        /// absolute "Touchpad {p} Pointer X/Y" rows (region geometry on the
        /// per-source ParamPointerCenter/Extent, sensitivity scales
        /// consumed as extent) instead of the wave-2A clamp macro;
        /// teleport/edge keys get a named entry. The absolute_mouse
        /// AbsoluteMouseApproximated Partial is retired as a false alarm
        /// (the mode is relative on trackpads and gyro; the relative rows
        /// are faithful and Clean).
        /// v7: Wave 4c (#9 B-17). radial_menu / touch_menu groups become
        /// first-class overlay-backed menus: the group emits an engine
        /// MenuDefinitionEntry (kind, host stick/trackpad, layer, Steam's
        /// touchmenu_button_fire_type commit semantics, cell geometry,
        /// labels, overlay position/scale/opacity) and every bound cell's
        /// bindings translate through the normal walk against a
        /// "Menu {id} Item {k}" source the menu runtime fires on
        /// hover-commit. Retires the TouchMenuNeedsOverlay /
        /// RadialMenuNeedsOverlay skips and the two-cell touch-spot
        /// approximation; per-cell icons get a named Partial.
        /// v8: Group axis inversion (finding 1g). The Steam group settings
        /// invert_x / invert_y now flip the emitted mouse-axis source's
        /// Invert flag on every mode whose engine read honors it (stick /
        /// touchpad-finger / gyro mouse modes and the trackpad mouse_region
        /// pointer), so the imported trackpad axes track the config instead
        /// of running reversed under a Clean label; invert_z (no third
        /// mouse-delta axis) and flick-stick inversion (the angle read
        /// ignores Invert) get the named AxisInversionNotApplied Partial.
        /// rotation / friction / mouse_smoothing / trackball on the mouse
        /// modes get the named MouseModeTuningDropped Partial, and group-level
        /// haptic_intensity now feeds the per-config haptic aggregate
        /// alongside haptic_intensity_override.
        /// v9: Audit 2026-07-16. mouse_joystick emits right-stick (or
        /// output_joystick-redirected) axis rows instead of KbM mouse;
        /// unmerged CHANGE_PRESET lowers to a Latch of the target layer
        /// (Base jumps to a single-stop Cycle) instead of latching a
        /// rowless Jump_* mask; stick-hosted button_a/b/x/y resolve onto
        /// the positional wedge reads; multi-pad center_trackpad groups
        /// get a reasoned skip instead of dead Touchpad 2 output; the
        /// clamp-macro mouse_region branch names its dropped teleport /
        /// edge keys; single-pad half clicks drive mode shifts and layer
        /// switches as Kind=Chord activators (click + half touch spot).
        /// v10: Gap closure 2026-07-17 (G1-G15). Activator haptic_intensity
        /// becomes a RumblePulse macro per activator (levels 1..3 scale
        /// strength); button_capture resolves to the raw "Button 11"
        /// (SDL MISC1) source; 2dscroll lowers onto the one-shot swipe
        /// gestures and scrollwheel onto a KbmScroll finger drag on
        /// trackpad hosts; activator delay_start / delay_end become Delay
        /// steps on one-shot macros; release activators on mouse_button /
        /// xinput emit MouseButtonTap / VcButtonTap macros; SCREENSHOT
        /// taps PrintScreen and SHOW_KEYBOARD launches the on-screen
        /// keyboard (named Partials); self-hosted REMOVE_LAYER lowers to
        /// the single-stop return Cycle; Long_Press gains mouse-button
        /// holds, CHANGE_PRESET debounce, set_led thresholds, and the
        /// HoldKey pair replacing the tap-at-threshold approximation;
        /// VKs outside the KbM row list ride SendInput HoldKey macros;
        /// trackpad-hosted Double_Press reads the DoubleTap gesture;
        /// identity turbo pulses through a descriptor trigger with the
        /// identity row dropped; hotbar lowers as a grid menu and
        /// empty_binding placeholders go silent.
        /// v11: Response-curve channel. Stick-hosted joystick groups
        /// (joystick_move / joystick_mouse / joystick_camera /
        /// mouse_joystick) carry Steam's curve cluster on the emitted axis
        /// pair as per-source params: the curve_exponent preset selector
        /// (and the x100 Custom slider) lands on ParamCurveExponent,
        /// deadzone_outer_radius / 32767 on ParamRangeOuter, and
        /// sensitivity_horiz/vert_scale fold into the X / Y row Sensitivity,
        /// so ResponseCurveNotSupported names only genuinely dropped keys
        /// (deadzone_shape; trigger, trackpad, and gyro hosts unchanged).
        /// v12: Stick-hosted swipe and wheel. 2dscroll on a stick lowers
        /// each dpad_* member's one-shot-able bindings onto tap macros
        /// (KeyTap / MouseButtonTap / VcButtonTap plus the one-shot
        /// controller_action verbs) triggered on the member's own wedge
        /// read, one fire per deflection entry; scrollwheel on a stick
        /// lowers scroll_clockwise / scroll_counterclockwise onto
        /// KbmScroll rows fed by the stick's Y deflection drag (the G4
        /// shape: composed invert, deduped symmetric twins, group
        /// deadzone honored); 2dscroll non-swipe members (click) translate
        /// as normal members on both hosts. Device-free descriptor macro
        /// triggers now carry the hosting read's half-axis shape (half,
        /// direction, deadzone) end to end, so wedge-hosted macros from
        /// earlier waves (haptic pulses and key latches on stick dpad
        /// members, trigger-pull hosts after the finalize swap) fire on
        /// their own wedge instead of any full-axis deflection.
        /// v13: Vocabulary census against Steam's own serializer (the
        /// steamclient.dll token table, Valve's controller_base configs,
        /// and the fixture corpus). The LSTICK_/RSTICK_ direction params
        /// lower to bipolar thumb-axis rows with the direction as the
        /// output polarity (LSTICK_UP pushes the virtual left stick up
        /// while the input is held, and wedge / trigger hosts keep their
        /// half-axis read with the polarity on InvertOutput).
        /// "STEAM" resolves as the Guide button and the axis-natured
        /// release / toggle / turbo / long-press variants keep the
        /// trigger-axis notes. The key table gains the serializer's
        /// alias spellings (ENTER, ESC, BACKSLASH, BREAK), the Windows
        /// keys, and the media row (MUTE, PLAY, STOP, VOLUME_*,
        /// NEXT_/PREV_TRACK), all riding the SendInput macro channel.
        /// controller_action gains mouse_delta as a named skip (no
        /// one-shot cursor-nudge primitive), toggle_lizard as the
        /// serializer spelling of TOGGLE_LIZARD_MODE, and the
        /// Steam-client verb families (SR_/GR_/TS_/STEAMMUSIC_/
        /// BIGPICTURE_/HOST_/CHORD_HINT_ plus brightness, poweroff,
        /// magnifier, quit, hud, rumble/haptics toggles and friends) as
        /// named SteamSystemAction skips. Scroll Wheel List members get
        /// the wheel mode's named skip per binding instead of silence,
        /// gyro_button_invert joins the named gyro-mask note, and the
        /// census-grounded feel keys (acceleration, friction_vert_scale,
        /// mouse_dampening_trigger, mouse_move_threshold, anti_deadzone)
        /// join their named drop lists.
        /// v14: Self-arming gesture reads and per-arm swipe skips. The
        /// TrackpadFeatureRequired vocabulary retires whole: imported sets
        /// are Authoritative, and the engine's gesture gate now treats a
        /// descriptor as enabled when an authoritative slot's mapping set
        /// (rows, activator legs, device-free macro triggers) references
        /// it (TouchpadGestureAutoArm, gate = user toggle OR
        /// referenced-by-mapping), so gesture-hosted rows, macros, and
        /// chord legs report Clean with no user action required. The
        /// generic directional-swipe skip splits into precise per-arm
        /// facts (all four arms built and retired again in v15).
        /// v15: The swipe / flick skip family closes. Gyro-hosted 2dscroll
        /// lowers each dpad_* member onto one-shot tap macros triggered by
        /// the SIGNED gyro rate descriptor with the matching half (up =
        /// pitch upper half, west = yaw upper half; the engine's gyro bool
        /// read honors the half stamp now), re-arming below the 30 deg/s
        /// engine rate threshold. Non-2D hosts route through the same
        /// member walk (a dpad-hosted swipe taps once per press;
        /// unresolvable members keep UnknownPhysicalInput), retiring
        /// GyroSwipeNotSupported and SwipeSurfaceNotSupported. The macro
        /// channel gains the timed-axis assert (AxisHold: VcAxisTap /
        /// HoldVcAxis) and the discrete wheel detent (MouseWheelTap), so
        /// flick / release / Long_Press bindings onto trigger pulls, stick
        /// directions, and mouse_wheel detents all lower instead of
        /// skipping (FlickAxisTargetNotSupported retired; the release /
        /// Long_Press axis arms close with it). Flick-hosted mode shifts
        /// and layer verbs lower to Toggle / Latch / Cycle shift
        /// activators riding the new Kind=Axis half stamp (ShiftActivator
        /// AxisHalf / AxisInvert; wedge-hosted held shifts pick up the
        /// same direction fix), and flick controller_actions route through
        /// the canonical verb lowering, retiring FlickBindingNotOneShot.
        /// Scrollwheel detent members with non-wheel bindings ride the
        /// one-shot tap walk (stick drag wedge / trackpad swipe gesture).
        /// The two macro-trigger plumbing notes
        /// (MacroTriggerViaXboxOutput, MacroTriggerRetargetedToInput)
        /// retire: they narrated HOW a working trigger is wired, not a
        /// fidelity loss, so those emissions are silent Clean now.
        /// v16: Terminal gap round. controller_action mouse_delta builds:
        /// the new one-shot MouseNudge macro enqueues the authored pixel
        /// delta once into the engine's accumulate-and-flush mouse lane
        /// (MouseDeltaNotSupported retired, key and locale strings
        /// deleted). Scroll Wheel List builds: the ordered
        /// scroll_wheel_list_0..N items lower to one CycleTapList macro on
        /// the clockwise detent read (stick drag wedge / trackpad
        /// SwipeDown), each detent firing the next item's one-shot form
        /// (key / mouse / wheel / VC button / VC axis taps), scroll_wrap
        /// consumed as the wrap flag. The surfaceless-scrollwheel skip arm
        /// retires with the census proof that no non-drag host exists in
        /// Steam's grammar (corpus + Valve's shipped controller_base
        /// templates host the mode on trackpads and joysticks only, and
        /// the shipped strings say "click the pad/stick"). A hand-edited
        /// host outside that grammar routes through the member walk's
        /// UnknownPhysicalInput safety net
        /// (ScrollWheelModeNotSupported retired, key and locale strings
        /// deleted). The degenerate whole-pad Outer Ring closes: a
        /// trackpad-hosted edge member whose zone covers the whole pad
        /// (edge_binding_invert 1 with radius at the 32767 ceiling, or
        /// invert 0 with radius 0) IS the touch read, so its bindings
        /// translate on "Touchpad {p} Finger 0 Down" and the two consumed
        /// geometry keys drop out of the region tuning note.
        /// v17: The last two gaps build. Double_Press activators on ANY
        /// host lower to macros on the engine's new DoublePress trigger
        /// (press, release, press within the window; trackpad hosts keep
        /// the shipped DoubleTap gesture read): keys / mouse buttons / VC
        /// targets take the Hold* shapes (Valve's "if held on the second
        /// press, it will remain pressed"), toggle / hold_repeats compose
        /// the latch and turbo variants, wheel detents tap, and
        /// camera_reset rides the canonical verb walk. The window is the
        /// activator's double_tap_time (steamclient.dll's own token,
        /// beside repeat_rate and long_press_time), default 442 ms, the
        /// value Valve's own controller_base templates author
        /// (DoublePressNotSupported retired, key and locale strings
        /// deleted; layer verbs and mode shifts have no double-press
        /// activator construct, are absent from every Double_Press in the
        /// corpus and Valve's 54 templates, and land on the existing
        /// ActivatorInputNotSupported net). Stick-hosted edge members
        /// build on the new deflection-magnitude family "Gamepad
        /// LeftStickRing" / "Gamepad RightStickRing" (sqrt(x*x + y*y) of
        /// the flick-stick axis pair): the authored edge_binding_radius
        /// (0..32767, the v11 scale) rides the source DeadZone as the
        /// ring radius and edge_binding_invert rides Invert as the inner
        /// ring, gated on the engine's 5 percent rest floor so a centered
        /// stick never fires. Partial trackpad rings approximate onto the
        /// touch read with the ring geometry named through the existing
        /// region tuning note (EdgeInputNotSupported retired, key and
        /// locale strings deleted; the census guard pins edge members to
        /// trigger / trackpad / stick hosts, and anything hand-edited
        /// outside that grammar routes through UnknownPhysicalInput).
        /// The expected-behavior notes retire with it: SCREENSHOT and
        /// SHOW_KEYBOARD keep their macros but report nothing, because
        /// "approximated as a PrintScreen key tap" (and the on-screen
        /// keyboard sibling) is exactly what a user expects the action to
        /// do (ScreenshotApproximated / ShowKeyboardApproximated retired,
        /// keys and locale strings deleted). Soft_Press rows report Clean
        /// because a soft press IS a press threshold, with the
        /// trigger-pull lowering kept (SoftPressApproximated retired),
        /// and set_led restore-default reports Clean because clearing the
        /// override IS restoring the default (SetLedDefaultApproximated
        /// retired).
        /// v18: the response-cluster and gate/latch waves. The engine's
        /// curve/range shaping seam widened from the stick tail to EVERY
        /// analog lane (unipolar trigger pulls, touchpad finger delta /
        /// absolute / gesture-stick reads, the gyro rate), so
        /// curve_exponent / custom_curve_exponent / deadzone_outer_radius
        /// / the sensitivity scales / the new anti_deadzone floor stamp
        /// on every analog host and ResponseCurveNotSupported names only
        /// deadzone_shape (mouse-output hosts) plus the defensive
        /// output_curve. deadzone_shape on thumb-pair outputs consumes
        /// into the slot-level DeadZoneShape stamp the runtime overlays
        /// onto the VC stick processing (Steam Cross / Square = Axial,
        /// Circle = ScaledRadial). The mouse-feel family builds:
        /// rotation as two-source Sum rows with trigonometric
        /// coefficients, mouse_smoothing as the per-source EMA,
        /// acceleration as the rate gain, mouse_move_threshold as the
        /// delta gate, and trackball + friction (+ vert scale) as the
        /// momentum decay, leaving MouseModeTuningDropped only
        /// mouse_dampening_trigger (cross-input modulation has no
        /// per-source channel). Click gates ride the per-source AND
        /// companion (MappingSource.GateDescriptor, evaluated like the
        /// chord second leg), so multi-source rows keep every gate and
        /// ClickGateDropped retires whole. four_buttons trackpad cells
        /// read the diamond-quadrant contact windows (North / South /
        /// East / West, half-composed on single-pad hosts) and
        /// half-hosted D-pad wedges gate on their half's contact or
        /// windowed-click read, retiring TouchQuadrantApproximated and
        /// TrackpadHalfApproximated. The latch family completes:
        /// ToggleMouseButton / ToggleWheel / ToggleVcAxis latch macros
        /// plus the RepeatVcAxisWhileHeld axis turbo and the
        /// pulse-while-latched composite (toggle + hold_repeats), so
        /// ToggleDropped and RepeatDropped retire whole (keys and locale
        /// strings deleted). Activator delays widen to every carrier:
        /// autofire takes a one-shot Delay step, VC holds compose
        /// delay_start into HoldForMs and grow a delay_end
        /// release-extension twin, the region clamp pair takes per-leg
        /// steps, rows reroute onto the delayed hold pairs, and layer
        /// switches take delay_start as the engage debounce, leaving
        /// ActivatorDelayDropped only layer delay_end, autofire
        /// delay_end, and wheel-row delays. gyro_button 0 (the pad-touch
        /// engage) stamps the slot-level device-free aim-engage overlay
        /// with gyro_button_invert 1 as the inverted hold; non-zero
        /// indices and ratchet masks keep the named note (the index
        /// table beyond 0 has no public grounding). flickstick
        /// transition_time consumes into ParamFlickTime.
        /// InterruptibleDropped retires as factually wrong: stored
        /// interruptable 0 matches PadForge's native never-cancel
        /// evaluation exactly (keys and locale strings deleted).
        /// v19: the audit-2 semantics wave. The gyro-hosted
        /// mouse_joystick rotation flips its pitch-to-X cross leg so the
        /// realized family-2 matrix stays orthogonal against the engine's
        /// yaw-frame flip (finding 1i). mouse_wheel hold_repeats lowers
        /// to a RepeatWheelWhileHeld turbo (one detent per repeat_rate)
        /// instead of the continuous full-scale row. The bare "deadzone"
        /// key parses as the group inner radius beside
        /// deadzone_inner_radius. Rotated groups withhold the nonlinear
        /// per-leg stamps (curve exponent, anti-deadzone, accel) under
        /// the named RotationNonlinearWithheld Partial, since nonlinear
        /// shaping does not commute with the rotation Sum. Analog pair
        /// hosts name the radial deadzone residual
        /// (DeadZoneRadialResidual: inner never reaches the analog read,
        /// outer applies per axis) pending the companion-axis pair-read
        /// channel. Release-hosted layer and preset verbs name their
        /// press-edge approximation (LayerReleaseEdgeApproximated)
        /// instead of lowering under silent Clean.
        /// v20: CHANGE_PRESET sentinel ids lower as commands, not preset
        /// references. 32766 (change to next action set) and 32765
        /// (previous) become one Cycle activator through every action set
        /// in authored order, Base riding the ring's include-Base stop,
        /// previous walking the ring in reverse, and a single-set config
        /// keeping an empty-queue ring that never steps. MissingPreset now
        /// names only genuinely dangling numeric references. system_key_1
        /// lowers as the SCREENSHOT PrintScreen tap: every censused
        /// occurrence (corpus and Valve's controller_base configs) rides
        /// button_capture Release to restore the Capture button's native
        /// screenshot, so the SteamSystemAction note no longer fires for
        /// it. system_key_0 keeps the named note.
        /// v21: menu cell icons carry. Each touch_menu_button binding's
        /// authored icon name (the third comma field, e.g.
        /// "ghost_050_menu_0030.png") rides MenuItemDefinition.Icon and
        /// the overlay resolves it against the local Steam client's
        /// binding-icon art at display time, so MenuIconsDropped retires
        /// (key and locale strings deleted). Only a reference outside the
        /// client's bare-filename shape stays behind, named per cell by
        /// MenuIconUnresolved.
        /// v22: the three Skyrim notes build. gyro_ratchet_button_mask
        /// lowers onto a new slot-level clutch lane (grounded per bit
        /// against Steam's own k_eGamepadButtonBitMask enum in the
        /// shipped configurator JS): while any stamped descriptor is
        /// held, the slot's gyro reads disengage, and only genuinely
        /// ungrounded bits keep GyroButtonMaskDropped (args carry the
        /// residual mask). The delay family closes whole: layer switches
        /// take delay_end as the Hold-mode release linger
        /// (ShiftActivator.ReleaseDelayMs, re-press cancels the pending
        /// disengage), autofire and the wheel turbo take it as the
        /// UntilRelease release linger (MacroData.ReleaseLingerMs, the
        /// pulse train runs past the release), wheel rows with delays
        /// reroute onto the wheel turbo (delay_start composing into the
        /// HoldForMs threshold), press-leg taps grow their assert to
        /// delay_end, and the edge-fired one-shots consume it silently
        /// (Steam's own deactivation edge emits nothing for them), so
        /// ActivatorDelayDropped retires (key and locale strings
        /// deleted). Group-level haptic_intensity / _override becomes the
        /// EmitHapticPulse fallback, so every member activation ticks at
        /// the group's level and the per-config HapticIntensityDropped
        /// aggregate retires (key and locale strings deleted; a
        /// member-less group's continuous surface tick has no channel
        /// and lowers silently).
        /// v23: the gyro_button engage arm gains the full enum the v22
        /// ratchet grounding established. gyro_button=N indexes
        /// k_eGamepadButtonBitMask (the configurator renders the engage
        /// picker and the ratchet mask through the one GyroButtonPicker
        /// glyph map), so every grounded index lowers through
        /// RatchetBitDescriptor onto the slot-level engage stamp; 0
        /// keeps the v18 pad-touch default sentinel and only genuinely
        /// out-of-enum indices keep GyroButtonMaskDropped. The lockdown
        /// re-audit also builds the bare half-touch activator host: a
        /// single-pad "touch" member (the held-state TouchLeft /
        /// TouchRight spot) drives layer verbs through the same
        /// button-like read a chord leg gets, closing the two corpus
        /// ActivatorInputNotSupported sites (the key survives as the
        /// safety net for genuinely un-hostable gesture fires).
        /// v24: the mass-sweep top four. button_macro0..4 resolve as the
        /// device's extra buttons (k_eGamepadButtonBitMask ButtonMacro0..7
        /// = bits 32-39, capability-gated 1:1 by ATTRIBCAP_MISCn_BUTTON in
        /// the shipped configurator; Steam macro N = SDL misc N+2 since
        /// both count the extras past the capture-shaped button, exact on
        /// the corpus's Flydigi Vader 5 Pro) onto raw "Button 17..21"
        /// paddle-shaped sources; macro5..7 and the mobile finger taps
        /// keep the named skip. The chord activator lowers: chord_button
        /// indexes the same enum (the gyro_button value space; the
        /// configurator's Chord_ChordButton picker renders through the
        /// same glyph map) and the partner rides the host source's
        /// AND-gate leg, so rows gate, macros grow the gate trigger
        /// entry (combined-output identity deliberately stripped: the
        /// output bit fires partner-or-not), and layer verbs lower as
        /// Kind=Chord activators; single-pad half clicks fold into the
        /// windowed click read to free the gate slot, and only
        /// out-of-enum chord_buttons or un-foldable double gates keep the
        /// named skip. Long_Press impossible bindings reroute into their
        /// binding's OWN class: game_action / game_action_analog feed the
        /// per-preset GameActionsNotSupported aggregate and every
        /// controller_action verb routes through the canonical walk
        /// (mouse_position / screenshot / show_keyboard fire at the hold
        /// threshold now, the SET_LED shape), so LongPressNotSupported
        /// names only genuinely unknown vocabulary. "@"-prefixed menu
        /// icons are the configurator's APP-PROVIDED set
        /// (GetTouchMenuIconsForApp, client-internal /appcontrollericons):
        /// unreachable locally by construction, so those cells degrade
        /// silently to their text labels and MenuIconUnresolved names
        /// only genuinely malformed references.
        /// v25: wild-corpus round 2. Serializer-vocabulary switch members
        /// resolve (left/right_stick_click, left/right_trigger_threshold
        /// = the Soft Pull read, diamond and dpad members in switches
        /// groups, left/right_trackpad_touch, button_lpad/rpad); the
        /// mouse modes' doubletap member reads the pad's DoubleTap
        /// gesture; four_buttons cells fold onto dpad seats and dpad
        /// members onto diamond seats; center_trackpad reads pad 0 whole
        /// on every type. always_on_action ("Always On Command") lowers
        /// onto the engine's constant-true source with rows scoped by
        /// LayerMask and macro-shaped bindings gated by the new macro
        /// layer gate. Double_Press layer verbs / preset jumps / mode
        /// shifts ride ShiftActivator.DoublePressMs. Radial menus host on
        /// the physical dpad / face diamond (button-pair menu hosts).
        /// deadzone_shape on stick-hosted mouse pairs lands on the
        /// per-source geometry stamp (ParamStickDeadZoneShape: Circle =
        /// radial over the pair, Cross / Square = per-axis), retiring the
        /// radial residual for stick mouse hosts. gyro_button_invert 2 =
        /// the enum's Toggle arm onto the slot engage machinery's Toggle
        /// mode; ratchet/engage bits 27/28 ground on single-pad center
        /// reads (controller_ps5_edge joins the single-pad set) and bits
        /// 32-36 on the v24 macro buttons.
        /// v26: wild-corpus round 3. The gravity-lean channel lands
        /// ("Gyro Lean X/Y": sustained tilt from the low-passed
        /// accelerometer, physical-stick signs, resting-grip neutral):
        /// gyro-hosted dpad members lower onto lean wedges with the
        /// stick-dpad table, gyro_to_joystick_deflection emits the lean
        /// pair onto the thumb axes, gyro_to_joystick emits the rate
        /// pair through the mouse_joystick shape, and gyro-hosted
        /// touch_menus hover by tilting (the Custom menu opener on the
        /// lean pair). Capsense lands (the fork's SDL_GetGamepadCapSense
        /// through CustomInputState.CapSense): stick "touch" members and
        /// button_left/rightauxcapsense read the four "Gamepad ...Touch"
        /// descriptors and ratchet/engage/chord bits 44-47 ground on
        /// them. The Steam Link on-screen controls (button_macro5..7,
        /// the One / Two Finger Taps) get the precise
        /// MobileTouchSurfaceOnly class: they exceed SDL's whole gamepad
        /// surface and exist only on the mobile touch overlay. Physical
        /// dpad edge / click members read the POV any-direction bool
        /// (a pressed dpad IS at the edge and IS clicked). Trackpad edge
        /// members build on the finger-ring read ("Touchpad {p} Finger 0
        /// Ring", radius on DeadZone / inner on Invert), consuming
        /// edge_binding_radius / _invert everywhere. Trackpad-hosted
        /// flickstick builds on the touch-surface flick family, and the
        /// flick tuning keys consume whole (rotation, mouse_smoothing,
        /// transition_time incl. 0). Stick mouse_region engages on the
        /// v17 deflection ring (the clamp macro's WhileHeld trigger).
        /// hotbar grids host on the dpad / diamond (stepped selection,
        /// Steam's remember-the-selection contract) and In-Menu
        /// Sensitivity consumes into MenuDefinitionEntry
        /// .SensitivityPercent. Gated trackpad D-pad wedges and
        /// pulse-gesture hosts (double taps, swipes) carry layer verbs
        /// (ShiftActivator.GateDescriptor on the Axis kind; pulse hosts
        /// latch, the flick-host rule). The chord activator with no
        /// chord_button gets the precise ChordWithoutPartner
        /// config-error class, and the "analog" activator type lowers as
        /// the live-magnitude press it is (shipped Analog description).
        /// FlickStickTuningDropped and MenuTuningDropped retired (keys
        /// and locale strings deleted).</summary>
        public const int CurrentTranslatorVersion = 26;

        public int TranslatorVersion { get; set; } = CurrentTranslatorVersion;

        public string ConfigTitle { get; set; } = "";

        public string ControllerType { get; set; } = "";

        public int SchemaVersion { get; set; }

        public List<TranslationEntry> Entries { get; set; } = new();

        public int XboxRowCount { get; set; }

        public int KbmRowCount { get; set; }

        public int MacroCount { get; set; }

        public int MenuCount { get; set; }

        public int ShiftActivatorCount { get; set; }

        public int CleanCount { get; set; }

        public int PartialCount { get; set; }

        public int SkippedCount { get; set; }

        public int ErrorCount { get; set; }

        public TranslationEntry Add(TranslationStatus status, string reasonKey,
            string sourcePath, string binding = "", string emitted = "", params string[] args)
        {
            var e = new TranslationEntry
            {
                Status = status,
                ReasonKey = reasonKey ?? "",
                SourcePath = sourcePath ?? "",
                Binding = binding ?? "",
                Emitted = emitted ?? "",
            };
            if (args != null && args.Length > 0)
                e.ReasonArgs.AddRange(args);
            Entries.Add(e);
            switch (status)
            {
                case TranslationStatus.Clean: CleanCount++; break;
                case TranslationStatus.Partial: PartialCount++; break;
                case TranslationStatus.Skipped: SkippedCount++; break;
                case TranslationStatus.Error: ErrorCount++; break;
            }
            return e;
        }

        /// <summary>One-line, culture-free digest for the provenance record.</summary>
        public string ToSummaryString()
        {
            var sb = new StringBuilder();
            sb.Append("v").Append(TranslatorVersion)
              .Append(" rows:x").Append(XboxRowCount).Append("+k").Append(KbmRowCount)
              .Append(" macros:").Append(MacroCount)
              .Append(" menus:").Append(MenuCount)
              .Append(" layers:").Append(ShiftActivatorCount)
              .Append(" clean:").Append(CleanCount)
              .Append(" partial:").Append(PartialCount)
              .Append(" skipped:").Append(SkippedCount)
              .Append(" errors:").Append(ErrorCount);
            return sb.ToString();
        }
    }

    /// <summary>
    /// The <c>Workshop_Tr_*</c> reason-key vocabulary. English resx strings
    /// land with the Phase C/D localization pass; the keys are fixed now so
    /// reports serialized before that pass stay renderable.
    /// </summary>
    public static class TranslationReasons
    {
        // Legacy-render-only: no longer emitted (identity bindings now
        // materialize as explicit rows) but kept, with its resx strings,
        // because the vocabulary is fixed and reports serialized by older
        // translator versions must stay renderable.
        public const string DefaultAutomapPassthrough = "Workshop_Tr_DefaultAutomapPassthrough"; // {0} bindings
        public const string RowEmitted = "Workshop_Tr_RowEmitted";
        public const string MacroEmitted = "Workshop_Tr_MacroEmitted";
        public const string ShiftLayerEmitted = "Workshop_Tr_ShiftLayerEmitted";                 // {0} layer name
        // TrackpadFeatureRequired retired in v14: imported sets are
        // Authoritative and the engine auto-arms every referenced gesture
        // family at apply (TouchpadGestureAutoArm), so no note remains and
        // the key plus its locale strings were deleted.
        // Legacy-render-only since translator v4: touchpad mouse rows carry
        // the per-row Sensitivity now (#9 B-13 widened the knob to the
        // finger reads), so nothing is dropped. Kept, with its resx
        // strings, for reports serialized by older translator versions.
        public const string TouchpadTuningNotPerRow = "Workshop_Tr_TouchpadTuningNotPerRow";
        // SoftPressApproximated retired in v17: "Soft_Press approximated
        // as a press threshold" described exactly what a soft press is,
        // so the note was noise, not a fidelity loss. Soft rows report
        // Clean now (the trigger-pull threshold lowering stays in
        // BuildSource) and the key plus its locale strings were deleted.
        // Legacy-render-only since translator v6: the v2-v5 Partial was a
        // false alarm. Trackpad/gyro absolute_mouse IS relative cursor
        // movement in Steam (trackball/friction settings vocabulary, the
        // Steam Input API's delta delivery, sc-controller's importer, and
        // Valve's mobile-touch template naming all agree; see the
        // absolute_mouse case in ConfigTranslator), so the relative rows
        // are faithful and Clean. Kept, with its resx strings, for reports
        // serialized by older translator versions.
        public const string AbsoluteMouseApproximated = "Workshop_Tr_AbsoluteMouseApproximated";
        public const string TriggerThresholdApproximated = "Workshop_Tr_TriggerThresholdApproximated";
        // MacroTriggerViaXboxOutput retired in v15: it narrated the trigger's
        // combined-output plumbing on a WORKING macro, not a fidelity loss,
        // so those emissions are silent Clean now and the key plus its
        // locale strings were deleted.
        public const string NoDeviceFreeTrigger = "Workshop_Tr_NoDeviceFreeTrigger";
        public const string GameActionsNotSupported = "Workshop_Tr_GameActionsNotSupported";     // {0} count
        public const string SteamSystemAction = "Workshop_Tr_SteamSystemAction";                 // {0} action
        public const string UnsupportedControllerAction = "Workshop_Tr_UnsupportedControllerAction"; // {0} action
        public const string UnknownBindingType = "Workshop_Tr_UnknownBindingType";               // {0} type
        public const string UnknownKey = "Workshop_Tr_UnknownKey";                               // {0} key
        // Legacy-render-only since translator v10 (G11): VKs outside the
        // KbM row engine's closed list ride SendInput HoldKey macros now,
        // so nothing is unsupported. Kept, with its resx strings, for
        // reports serialized by older translator versions.
        public const string UnsupportedKey = "Workshop_Tr_UnsupportedKey";                       // {0} key
        public const string UnknownMouseButton = "Workshop_Tr_UnknownMouseButton";               // {0} name
        public const string UnknownXInputButton = "Workshop_Tr_UnknownXInputButton";             // {0} name
        public const string UnknownPhysicalInput = "Workshop_Tr_UnknownPhysicalInput";           // {0} slot {1} input
        /// <summary>A Steam Link on-screen touch control (v26): Macro
        /// Buttons 6-8 (button_macro5..7, enum bits 37-39) name buttons
        /// past SDL's whole gamepad surface (SDL_GAMEPAD_BUTTON_MISC6 =
        /// Steam macro 4 is the last misc slot), and the One / Two Finger
        /// Tap pair (bits 48/49) are touch-screen tap gestures the shipped
        /// glyph map files under eIgnore. They exist only on the mobile
        /// client's touch overlay; no physical controller PadForge can
        /// drive exposes them. {0} = the slot, {1} = the input token.</summary>
        public const string MobileTouchSurfaceOnly = "Workshop_Tr_MobileTouchSurfaceOnly";       // {0} slot {1} input
        public const string UnknownGroupMode = "Workshop_Tr_UnknownGroupMode";                   // {0} mode
        // Legacy-render-only since translator v7 (#9 B-17): menus are
        // first-class now, so nothing needs an overlay it doesn't have.
        // Kept, with their resx strings, for reports serialized by older
        // translator versions.
        public const string TouchMenuNeedsOverlay = "Workshop_Tr_TouchMenuNeedsOverlay";         // {0} cell count
        public const string RadialMenuNeedsOverlay = "Workshop_Tr_RadialMenuNeedsOverlay";       // {0} cell count
        public const string MouseRegionNotSupported = "Workshop_Tr_MouseRegionNotSupported";
        // ScrollWheelModeNotSupported retired in v16: scroll_wheel_list
        // items lower to the CycleTapList macro, and the surfaceless-host
        // arm is unreachable in Steam's grammar (the mode hosts on
        // trackpads and joysticks only, census-guarded). The key plus its
        // locale strings were deleted.
        // EdgeInputNotSupported retired in v17: stick-hosted edge members
        // build on the "Gamepad Left/RightStickRing" deflection-magnitude
        // family, partial trackpad rings approximate onto the touch read
        // (geometry named via MouseRegionTuningDropped), and the census
        // guard pins edge members to trigger / trackpad / stick hosts.
        // The key plus its locale strings were deleted.
        public const string ReleaseActivatorNotSupported = "Workshop_Tr_ReleaseActivatorNotSupported";
        public const string LongPressNotSupported = "Workshop_Tr_LongPressNotSupported";
        // DoublePressNotSupported retired in v17: button-hosted (and every
        // other non-trackpad) Double_Press lowers to macros on the
        // engine's DoublePress trigger. The double-press-hostable
        // vocabulary is census-guarded, and the two arms with no construct
        // (layer verbs, mode shifts) land on ActivatorInputNotSupported.
        // The key plus its locale strings were deleted.
        public const string UnknownActivatorType = "Workshop_Tr_UnknownActivatorType";           // {0} type
        /// <summary>A chord activator whose settings carry no chord_button
        /// (v26): the partner picker was never set (Steam's serializer
        /// omits defaults, and the shared enum's 0 is the none sentinel),
        /// so not even Steam can fire the chord. Config-error reporting of
        /// the config's own unset picker; there is nothing to gate on.</summary>
        public const string ChordWithoutPartner = "Workshop_Tr_ChordWithoutPartner";
        // RepeatDropped retired in v18: every surviving arm BUILT. Axis
        // targets pulse via the RepeatVcAxisWhileHeld turbo and the
        // toggle + hold_repeats composite rides the pulse-while-latched
        // flag on the latch macros. The key plus its locale strings were
        // deleted.
        // MacroTriggerRetargetedToInput retired in v15: the finalize pass's
        // swap onto the hosting input's own descriptor is normal working
        // plumbing (the macro fires exactly as the config asks), so the
        // rescue is silent now and the key plus its locale strings were
        // deleted.
        public const string MissingGroup = "Workshop_Tr_MissingGroup";                           // {0} group id
        public const string MissingModeShiftGroup = "Workshop_Tr_MissingModeShiftGroup";         // {0} slot {1} group id
        /// <summary>A CHANGE_PRESET / layer verb names a preset index the
        /// config does not define. Sentinel set-cycle ids (32766 next,
        /// 32765 previous) never land here since v20: they are commands
        /// and lower to a Cycle activator through the config's sets.</summary>
        public const string MissingPreset = "Workshop_Tr_MissingPreset";                         // {0} preset id
        public const string ReferenceCycle = "Workshop_Tr_ReferenceCycle";                       // {0} group id
        public const string RemoveLayerApproximated = "Workshop_Tr_RemoveLayerApproximated";
        /// <summary>A layer / preset verb hosted on a release activator
        /// (v19, T6). Every ShiftActivator mode keys on the press edge
        /// (Hold while held, Toggle / Cycle / Custom on press), so the
        /// change lowers one edge early; this names the shifted edge
        /// instead of leaving the arm silent Clean. {0} = the verb.</summary>
        public const string LayerReleaseEdgeApproximated = "Workshop_Tr_LayerReleaseEdgeApproximated"; // {0} verb
        public const string ActivatorInputNotSupported = "Workshop_Tr_ActivatorInputNotSupported";
        // ClickGateDropped retired in v18: the AND companion rides each
        // source itself (MappingSource.GateDescriptor, evaluated like the
        // chord second leg), so a second feed on the same target never
        // drops anybody's gate. The key plus its locale strings were
        // deleted.
        public const string RowCapExceeded = "Workshop_Tr_RowCapExceeded";                       // {0} slot class
        public const string PresetHasNoActivator = "Workshop_Tr_PresetHasNoActivator";           // {0} preset name
        public const string ShiftLayerEmpty = "Workshop_Tr_ShiftLayerEmpty";                     // {0} layer name
        // Legacy-render-only: no longer emitted (authoritative imported sets
        // stop the automap from asserting, so there is nothing to warn
        // about) but kept, with its resx strings, for old serialized reports.
        public const string AutomapAlsoActive = "Workshop_Tr_AutomapAlsoActive";                 // {0} source {1} target

        // ── Translator v2 (Wave 1a) vocabulary ──
        // ScrollGestureModeNotSupported retired in v14 (split into per-arm
        // reasons) and the per-arm family itself retired in v15 when every
        // arm was built; the keys plus their locale strings were deleted.
        // HapticIntensityDropped retired in v22: activator-level haptics
        // became RumblePulse macros in v10 (G1) and the group-level
        // intensity now rides every member activation through the
        // EmitHapticPulse fallback, so the per-config aggregate had
        // nothing left to count. The key plus its locale strings were
        // deleted.
        // Since v18 every analog host consumes the exponent / range /
        // sensitivity / anti-deadzone cluster into the per-source channel,
        // so the args name only deadzone_shape (mouse-output hosts, whose
        // X / Y rows have no pair read) and the defensively-listed
        // output_curve.
        public const string ResponseCurveNotSupported = "Workshop_Tr_ResponseCurveNotSupported"; // {0} setting keys
        /// <summary>The pair-host deadzone radial residual (v19, T2): the
        /// inner radius lands on the digital per-source DeadZone only (the
        /// analog pair read has no per-source inner channel) and the outer
        /// radius applies per axis, not radially, so diagonals overshoot.
        /// Retires when the per-source companion-axis pair read (a
        /// ParamYDescriptor-style channel on MappingSource) lands in the
        /// engine. {0} = the authored keys.</summary>
        public const string DeadZoneRadialResidual = "Workshop_Tr_DeadZoneRadialResidual";       // {0} setting keys
        /// <summary>Nonlinear response stamps withheld on a rotated group
        /// (v19, T5): curve exponent / anti-deadzone / accel do not
        /// commute with the rotation's two-source Sum (per-leg shaping
        /// bends the summed vector by a speed-dependent angle), so the
        /// rotated legs carry only the linear knobs and the withheld keys
        /// are named. {0} = the withheld keys.</summary>
        public const string RotationNonlinearWithheld = "Workshop_Tr_RotationNonlinearWithheld"; // {0} setting keys
        // Since v18 the note is the out-of-census net only: gyro_button 0
        // (the none/default sentinel, pad touch) stamps the slot-level
        // engage overlay and gyro_button_invert 1 the inverted hold.
        // Since v22 the ratchet mask builds bit-by-bit against Steam's
        // own k_eGamepadButtonBitMask enum (the shipped configurator
        // JS), and since v23 gyro_button indices lower through the same
        // enum onto the engage stamp, so the note names only genuinely
        // out-of-enum gyro_button indices, non-boolean
        // gyro_button_invert values, and the residual mask of
        // ungrounded ratchet bits.
        public const string GyroButtonMaskDropped = "Workshop_Tr_GyroButtonMaskDropped";         // {0} setting key {1} value
        // ActivatorDelayDropped retired in v22: every surviving arm BUILT
        // or was proved unobservable. Layer delay_end rides the Hold-mode
        // release linger (ShiftActivator.ReleaseDelayMs), autofire and
        // wheel-turbo delay_end ride the UntilRelease release linger
        // (MacroData.ReleaseLingerMs), wheel rows with delays reroute
        // onto the wheel turbo with delay_start on the HoldForMs
        // threshold, press-leg taps grow their assert to delay_end, and
        // the remaining press-leg one-shots (edge-fired commands, latch
        // flips) emit nothing on Steam's own deactivation edge, so the
        // shifted edge is unobservable. The key plus its locale strings
        // were deleted.
        // InterruptibleDropped retired in v18: stored interruptable 0
        // matches PadForge's native never-cancel evaluation exactly
        // (sibling activators on one input all fire), so the note
        // reported the MATCHING case as a divergence. The key plus its
        // locale strings were deleted.
        public const string PlayerNumberActionNotSupported = "Workshop_Tr_PlayerNumberActionNotSupported";
        public const string LizardModeActionNotSupported = "Workshop_Tr_LizardModeActionNotSupported";
        // SetLedDefaultApproximated retired in v17: "restore-default
        // lighting approximated as clearing the override" described
        // exactly what restoring is (clearing the override IS the
        // restore), so the note was noise. The macro still emits and
        // reports Clean; the key plus its locale strings were deleted.

        // ── Translator v3 (Wave 2A) vocabulary ──
        /// <summary>The activator toggle setting became a latch macro
        /// (ToggleVcButton / ToggleKey). Clean for the macro-only structure;
        /// Partial when the momentary identity row is kept alongside the
        /// latch so the macro's trigger stays fed.</summary>
        public const string ToggleLatchEmitted = "Workshop_Tr_ToggleLatchEmitted";                 // {0} target
        // ToggleDropped retired in v18: every binding kind latches now
        // (ToggleMouseButton / ToggleWheel / ToggleVcAxis beside the
        // wave-2A key and VC-button latches). The key plus its locale
        // strings were deleted.
        /// <summary>Legacy-render-only since translator v10 (G10): a
        /// Long_Press key rides the HoldKey pair now (down at threshold,
        /// up on release), so nothing taps. Kept, with its resx strings,
        /// for reports serialized by older translator versions.</summary>
        public const string LongPressKeyTap = "Workshop_Tr_LongPressKeyTap";                       // {0} key
        /// <summary>camera_reset re-levels the camera by unwinding STEAM'S
        /// OWN ledger of emitted camera motion (the numeric args bound the
        /// yaw / pitch sweep and speed), a best-effort even there: no
        /// input remapper knows the game camera's true state. PadForge
        /// delivers the same user-facing construct by re-referencing its
        /// gyro aim integration state (the GyroRecenter macro), the
        /// equivalent state it actually owns; the args' sweep shaping has
        /// no counterpart on a re-reference and is named by this Partial.
        /// Re-attacked v26: building Steam's ledger unwind would need a
        /// per-slot accumulated-counts ledger across every mouse lane
        /// with an arbitrary reference heading, reproducing the same
        /// approximation with more state, not more fidelity.</summary>
        public const string CameraResetApproximated = "Workshop_Tr_CameraResetApproximated";
        /// <summary>mouse_region approximated as a centered cursor clamp
        /// engaged while the hosting input is held. Stick/gyro hosts only
        /// since translator v6; trackpad hosts translate to Clean absolute
        /// pointer rows (#9 B-15). Stick hosts ENGAGE since v26 (the v17
        /// deflection-ring bool drives the clamp macro's WhileHeld
        /// trigger, retiring their NoDeviceFreeTrigger skip); the clamp
        /// remains an approximation because a stick has no absolute
        /// position surface for the 1:1 region map.</summary>
        public const string MouseRegionApproximated = "Workshop_Tr_MouseRegionApproximated";       // {0} scale {1} x {2} y
        /// <summary>Trackpad mouse_region keys with no pointer-row channel
        /// (teleport start/stop snap, edge-binding radius/invert): the
        /// region itself translates, these shape its engage/release
        /// behavior.</summary>
        public const string MouseRegionTuningDropped = "Workshop_Tr_MouseRegionTuningDropped";     // {0} setting keys

        // ── Translator v4 (Wave 3) vocabulary ──
        // TouchQuadrantApproximated retired in v18: four_buttons trackpad
        // cells read the diamond-quadrant contact windows ("Finger 0 Down
        // North" and friends, the |dy| vs |dx| check around the region
        // center), exactly Steam's ABXY zone geometry, half-composed on
        // single-pad hosts. The key plus its locale strings were deleted.
        // TrackpadHalfApproximated retired in v18: half-hosted D-pad
        // wedges gate on the half's contact window (or the windowed pad
        // click when the group requires one) through the per-source AND
        // companion, so only the hosting half fires the group. The key
        // plus its locale strings were deleted.

        // ── Translator v7 (Wave 4c, #9 B-17) vocabulary ──
        /// <summary>A radial_menu / touch_menu group became an
        /// overlay-backed menu with working hover-commit.</summary>
        public const string MenuEmitted = "Workshop_Tr_MenuEmitted";                             // {0} bound cell count
        /// <summary>A menu group with no bound cells: nothing to show or
        /// fire (corpus carries several placeholder menus).</summary>
        public const string MenuEmpty = "Workshop_Tr_MenuEmpty";
        /// <summary>A menu group hosted on a surface with no direction /
        /// position read (not a stick or trackpad).</summary>
        public const string MenuSurfaceNotSupported = "Workshop_Tr_MenuSurfaceNotSupported";     // {0} slot
        // MenuIconsDropped retired in v21: authored icon names carry on
        // the items and resolve against the local Steam client's art at
        // display time. The key plus its locale strings were deleted.
        /// <summary>A cell icon reference outside the Steam client's
        /// bare-filename art shape (v21): a malformed reference, named per
        /// cell with the exact file. Since v24 the "@"-prefixed names are
        /// NOT malformed and never land here: they are the configurator's
        /// app-provided icon namespace (client-internal), so those cells
        /// degrade silently to their text labels.</summary>
        public const string MenuIconUnresolved = "Workshop_Tr_MenuIconUnresolved";               // {0} icon reference
        // MenuTuningDropped retired in v26: "sensitivity" (In-Menu
        // Sensitivity), the only key it ever named, BUILT as
        // MenuDefinitionEntry.SensitivityPercent (the hover-vector scale
        // the menu runtime applies before selection). The key plus its
        // locale strings were deleted.

        // ── Translator v5 (Wave 4a) vocabulary ──
        /// <summary>A flickstick group hosted on a surface with no analog
        /// pair at all (hand-edited grammar). Since v26 trackpad hosts
        /// BUILD on the touch-surface flick family ("Flick Stick
        /// Touchpad {p}"), so this is the residual net only.</summary>
        public const string FlickStickSurfaceNotSupported = "Workshop_Tr_FlickStickSurfaceNotSupported";
        // FlickStickTuningDropped retired in v26: every key it ever
        // named BUILT. rotation -> ParamFlickRotationOffsetDeg (degrees,
        // the v18 rotation scale on the same key), mouse_smoothing ->
        // ParamFlickSmooth (percent strength onto JSM's rad-per-tick
        // band), transition_time -> ParamFlickTime at any authored value
        // including 0 (clamp floor = the near-instant flick), and
        // edge_binding_radius belongs to the edge MEMBER's ring read
        // (v17 stick / v26 finger). The key plus its locale strings were
        // deleted.

        // ── Translator v8 (finding 1g) vocabulary ──
        /// <summary>Mouse/region-mode feel settings with no PadForge channel:
        /// rotation (a geometric rotation of the pad-to-cursor map),
        /// friction, mouse_smoothing, trackball. The mouse rows translate;
        /// these shape their response.</summary>
        public const string MouseModeTuningDropped = "Workshop_Tr_MouseModeTuningDropped";         // {0} setting keys
        /// <summary>A Steam axis inversion the emitter could not apply to a
        /// source the engine honors: invert_z (no third mouse-delta axis) or
        /// flick-stick inversion (the angle read ignores Invert). The row is
        /// emitted un-inverted; this names the dropped flip.</summary>
        public const string AxisInversionNotApplied = "Workshop_Tr_AxisInversionNotApplied";       // {0} invert keys

        // ── Translator v10 (gap closure) vocabulary ──
        // Activator-level haptic_intensity lowers to a RumblePulse macro
        // silently. Steam Input treats rumble and haptics interchangeably,
        // so the lowering is clean, not an approximation (v13 ruling).
        /// <summary>A trackpad scrollwheel group lowered onto the vertical
        /// finger drag: circular scratch geometry approximated as a linear
        /// drag (up = counterclockwise, down = clockwise).</summary>
        public const string ScrollWheelApproximated = "Workshop_Tr_ScrollWheelApproximated";
        // ScreenshotApproximated / ShowKeyboardApproximated retired in v17:
        // "SCREENSHOT approximated as a PrintScreen key tap" and the
        // on-screen-keyboard sibling describe exactly what a user expects
        // those actions to do, so the notes were noise, not fidelity
        // losses. The macros still emit, silently now, and the keys plus
        // their locale strings were deleted.

        // ── Translator v13 (vocabulary census) vocabulary ──
        // MouseDeltaNotSupported retired in v16, three translator versions
        // after it landed, because the arm BUILT instead of skipping: the
        // one-shot MouseNudge macro carries the authored pixel delta into
        // the engine's accumulate-and-flush mouse lane. The key plus its
        // locale strings were deleted.

        // ── Translator v14 (per-arm swipe skips) vocabulary ──
        // The whole family retired in v15, one translator version after it
        // landed, because every arm BUILT instead of skipping: gyro hosts
        // ride the signed-rate half read (GyroSwipeNotSupported), non-2D
        // hosts route through the member walk (SwipeSurfaceNotSupported),
        // axis-natured flick targets ride the AxisHold channel
        // (FlickAxisTargetNotSupported), and held-state flick bindings
        // lower to half-stamped shift activators, wheel taps, and the
        // canonical controller_action walk (FlickBindingNotOneShot). The
        // keys plus their locale strings were deleted.
    }
}
