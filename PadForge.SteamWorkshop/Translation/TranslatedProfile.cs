using System.Collections.Generic;
using PadForge.Engine.Data;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// Neutral translation output. Everything the engine already models is
    /// carried as the real Engine type (the two per-slot
    /// <see cref="MappingSet"/>s, rows, sources, shift activators); only the
    /// App-owned shapes (ProfileData's bag, MacroData/ActionData) stay out.
    /// PadForge.App's <c>WorkshopProfileMaterializer</c> turns this into a
    /// real <c>ProfileData</c>. Split layering because ProfileData lives in
    /// the WPF exe project and this library must not reference it.
    /// </summary>
    public sealed class TranslatedProfile
    {
        /// <summary>Profile display name (config title, override applied).</summary>
        public string Name { get; set; } = "";

        /// <summary>Config description (localized fallback applied).</summary>
        public string Description { get; set; } = "";

        /// <summary>Rows + shift activators for the pre-allocated Xbox VC
        /// slot. Sources use abstract descriptors with empty DeviceGuid.</summary>
        public MappingSet XboxMappingSet { get; set; } = new();

        /// <summary>Rows + shift activators for the pre-allocated
        /// keyboard/mouse VC slot.</summary>
        public MappingSet KbmMappingSet { get; set; } = new();

        /// <summary>Macro-backed bindings (cursor warp, key autofire,
        /// key-on-release). The materializer builds MacroData from these.</summary>
        public List<TranslatedMacro> Macros { get; set; } = new();

        /// <summary>Radial / touch menus (#9 B-17), carried as the real
        /// Engine menu model (same policy as the two MappingSets above:
        /// everything the engine already models rides the Engine type).
        /// Cell BINDINGS are not in here; they lower through the normal
        /// row / macro / activator paths keyed on the menu-item source
        /// descriptor ("Menu {id} Item {k}"), so the menus list carries
        /// only structure: kind, host surface, layer, fire type, cell
        /// geometry, labels, and overlay hints. DeviceGuid stays empty
        /// (the "any device on the slot" form).</summary>
        public List<PadForge.Engine.Menus.MenuDefinitionEntry> Menus { get; set; } = new();

        /// <summary>True when the config binds the Xbox side at all: explicit
        /// rows (identity bindings included) or activators, or macros (their
        /// triggers read the Xbox slot's combined output). A pure
        /// keyboard/mouse config leaves this false and the materializer
        /// creates no Xbox slot for it.</summary>
        public bool NeedsXboxSlot { get; set; }

        /// <summary>True when the keyboard/mouse side received rows or
        /// activators.</summary>
        public bool NeedsKbmSlot { get; set; }

        // ── Slot-level workshop stamps (v18): values with no per-source
        // channel, carried to the materializer which stamps them onto the
        // authoritative MappingSet for the runtime overlays. ──

        /// <summary>Steam deadzone_shape for the LEFT output thumb pair as
        /// a DeadZoneShape ordinal string ("0" Axial, "2" ScaledRadial).
        /// Empty = no stamp.</summary>
        public string LeftStickDeadZoneShape { get; set; } = "";

        /// <summary>Right-pair twin of
        /// <see cref="LeftStickDeadZoneShape"/>.</summary>
        public string RightStickDeadZoneShape { get; set; } = "";

        /// <summary>Steam gyro_button as a device-free engage descriptor
        /// (v18, the full k_eGamepadButtonBitMask index space since v23):
        /// gyro rows fire only while it is held. Empty = none.</summary>
        public string GyroEngageDescriptor { get; set; } = "";

        /// <summary>Steam gyro_button_invert: engage while NOT held.</summary>
        public bool GyroEngageInvert { get; set; }

        /// <summary>Steam gyro_ratchet_button_mask (v22) as device-free
        /// descriptors, one per grounded mask bit: while ANY is held the
        /// slot's gyro reads are clutched, Steam's ratchet ("while held,
        /// gyro input is ignored so the user can re-center, like lifting
        /// a mouse"). Lowered onto its own AND-NOT lane beside the engage
        /// gate rather than SetGyroEngaged macros or the engage-invert
        /// channel, because (a) the engage read ORs the button bit with
        /// the macro bit and Hold mode's empty descriptor reads
        /// always-engaged, so a SetGyroEngaged Off could never clutch and
        /// would fight a configured engage button, and (b) the invert
        /// flag occupies the single engage-descriptor channel, so it
        /// cannot compose with an authored gyro_button and cannot carry a
        /// multi-bit mask. Empty = no ratchet. Ordered and deduped for
        /// deterministic goldens.</summary>
        public List<string> GyroRatchetDescriptors { get; set; } = new();

        public TranslationReport Report { get; set; } = new();
    }

    /// <summary>What a translated macro does when its trigger fires.</summary>
    public enum TranslatedMacroAction
    {
        /// <summary>Warp the cursor to a fixed screen position
        /// (<c>controller_action MOUSE_POSITION</c>). Coordinates are kept in
        /// Steam's normalized 0..65535 space; the materializer converts to
        /// primary-monitor pixels at import time (the translator has no
        /// screen).</summary>
        MoveMouseToScreenPosition = 0,

        /// <summary>Key autofire while the trigger is held
        /// (<c>key_press</c> with <c>hold_repeats</c>).</summary>
        RepeatKeyWhileHeld = 1,

        /// <summary>One key tap when the trigger releases
        /// (<c>release</c> activator on a key binding).</summary>
        KeyTap = 2,

        /// <summary>Set the pad's LED (<c>controller_action set_led
        /// r g b brightness saturation setting</c>). The materializer maps
        /// setting 1 to a Sticky lightbar-color hold, setting 0 to a
        /// lightbar clear, setting 2 to a clear (approximation, reported
        /// Partial at translate time), and the whole family to a Guide-LED
        /// brightness write for Steam Controller configs. Arg order and
        /// both saturation scales verified against the corpus: 1451857916
        /// (2018, saturation 0-255) and 3353604014 (2024, saturation
        /// 0-100); brightness is 0-100 in both eras. Saturation and
        /// brightness are normalized to percent here so the materializer
        /// never sees the vintage scale.</summary>
        SetLightbarColor = 3,

        /// <summary>Turbo / autofire for a virtual controller button
        /// (<c>xinput_button</c> with <c>hold_repeats</c>, wave 2A).
        /// <see cref="TranslatedMacro.TargetXboxButtons"/> names the pulsed
        /// button, <see cref="TranslatedMacro.IntervalMs"/> the pulse period
        /// (Steam's <c>repeat_rate</c> is a millisecond interval: the shipped
        /// configurator labels it "Repeat Interval" with a #Unit_Milliseconds
        /// suffix in chunk~2dcc5aaf7.js). The materializer lowers this to a
        /// MacroActionType.RepeatVcButtonWhileHeld action with
        /// RepeatMode=UntilRelease so pulsing stops at release.</summary>
        RepeatVcButtonWhileHeld = 4,

        /// <summary>Latch / unlatch a virtual controller button (the
        /// activator-level <c>toggle</c> setting on an xinput binding, wave
        /// 2A). Grounded on Valve's shipped strings: "Toggle will make this
        /// activator continue to be active after releasing it until it is
        /// pressed again." Lowers to MacroActionType.ToggleVcButton.</summary>
        ToggleVcButton = 5,

        /// <summary>Latch / unlatch a keyboard key (the <c>toggle</c>
        /// setting on a key binding, wave 2A). Lowers to
        /// MacroActionType.ToggleKey.</summary>
        ToggleKey = 6,

        /// <summary>Re-reference the slot's gyro-aim state
        /// (<c>controller_action camera_reset</c>, wave 2A). Steam's action
        /// re-levels the in-game camera through calibrated mouse motion
        /// ("Reset the camera to the Horizon ... requires the Dots Per 360°
        /// setting", shipped client strings); PadForge approximates with its
        /// gyro-recenter primitive, reported Partial at translate time.
        /// Lowers to MacroActionType.GyroRecenter.</summary>
        GyroRecenter = 7,

        /// <summary>Press a virtual controller button and hold it until the
        /// physical input releases (a Long_Press xinput binding, wave 2A).
        /// Grounded on Valve's shipped Long Press description: "Once the
        /// long press time has passed, it will activate stay on until you
        /// release it." Rides <see cref="TranslatedMacro.TriggerHoldMs"/>;
        /// the materializer lowers it to a ButtonPress action with
        /// RepeatMode=UntilRelease and RepeatDelayMs=0 (the button is
        /// re-written every frame from the threshold until release).</summary>
        HoldVcButton = 8,

        /// <summary>Confine the desktop cursor to a screen region while the
        /// hosting input is held (a <c>mouse_region</c> group, wave 2A).
        /// Geometry rides <see cref="TranslatedMacro.RegionXPercent"/> /
        /// <see cref="TranslatedMacro.RegionYPercent"/> /
        /// <see cref="TranslatedMacro.RegionScalePercent"/> (Steam stores
        /// position_x/position_y as screen percent and scale as region size;
        /// shipped configurator ids PositionXMouse / PositionYMouse /
        /// ScaleMouseRegion). TriggerMode is the SEMANTIC "WhileHeld"; the
        /// materializer lowers it to an OnPress + OnRelease pair of
        /// MouseLimitRegion clamp toggles because the engine clamp is a
        /// toggle primitive (#110).</summary>
        MouseLimitRegion = 9,

        /// <summary>One reactive rumble pulse when the hosting activator
        /// fires (an activator-level <c>haptic_intensity</c>, v10 G1).
        /// Steam plays a haptic tick through the pad actuator; PadForge's
        /// nearest primitive is the macro rumble override's Reactive
        /// one-shot (MacroActionType.Rumble), strength scaled by the
        /// stored level via <see cref="TranslatedMacro.RumbleStrengthPercent"/>.</summary>
        RumblePulse = 10,

        /// <summary>One mouse-button click when the trigger fires (a
        /// <c>release</c> activator on a mouse_button binding, v10 G6).
        /// Lowers to MacroActionType.MouseButtonPress, whose executor is
        /// down + DurationMs + up: one click.</summary>
        MouseButtonTap = 11,

        /// <summary>One virtual-controller button tap when the trigger
        /// fires (a <c>release</c> activator on an xinput binding, v10 G6).
        /// Lowers to MacroActionType.ButtonPress for one DurationMs.</summary>
        VcButtonTap = 12,

        /// <summary>Hold a keyboard key down from the trigger fire until
        /// the physical input releases (v10 G10/G11: Long_Press keys at
        /// full fidelity, and any-VK keys outside the KbM row engine's
        /// closed set). The materializer lowers it to a PAIR: a KeyPress
        /// with a long duration riding RepeatMode=UntilRelease (release
        /// stops the macro with the key still down), plus an OnRelease
        /// KeyRelease twin that sends the key-up. Releasing an unpressed
        /// key is a SendInput no-op, so short taps are harmless.</summary>
        HoldKey = 13,

        /// <summary>Hold a mouse button down from the trigger fire until
        /// the physical input releases (a Long_Press mouse_button binding,
        /// v10 G10). Same pair lowering as <see cref="HoldKey"/> with
        /// MouseButtonPress / MouseButtonRelease.</summary>
        HoldMouseButton = 14,

        /// <summary>Launch the Windows on-screen keyboard
        /// (<c>controller_action SHOW_KEYBOARD</c>, v10 G7). Lowers to a
        /// RunProgram action; the materializer resolves TabTip.exe and
        /// falls back to osk.exe.</summary>
        ShowOnScreenKeyboard = 15,

        /// <summary>One timed assert of a virtual-controller axis (v15):
        /// a swipe flick or release activator on a trigger-pull / stick-
        /// direction target. <see cref="TranslatedMacro.TargetAxis"/> names
        /// the axis and <see cref="TranslatedMacro.TargetAxisNegative"/>
        /// the direction. Lowers to MacroActionType.AxisHold at the
        /// default tap duration.</summary>
        VcAxisTap = 16,

        /// <summary>Assert a virtual-controller axis from the trigger fire
        /// until the physical input releases (v15): a Long_Press binding
        /// on a trigger-pull / stick-direction target. Lowers to
        /// MacroActionType.AxisHold riding RepeatMode=UntilRelease +
        /// RepeatDelayMs=0, the HoldVcButton shape.</summary>
        HoldVcAxis = 17,

        /// <summary>One discrete mouse-wheel detent per fire (v15): a
        /// <c>mouse_wheel</c> binding hosted on a one-shot context (swipe
        /// flick, release activator, Long_Press threshold).
        /// <see cref="TranslatedMacro.WheelTicks"/> is the signed count
        /// (positive = up / right) and
        /// <see cref="TranslatedMacro.WheelHorizontal"/> picks the lane.
        /// Lowers to MacroActionType.MouseWheelTap.</summary>
        MouseWheelTap = 18,

        /// <summary>One fixed-pixel cursor nudge per fire (v16):
        /// <c>controller_action mouse_delta dx dy</c>, Steam's "Move by
        /// Amount" ("Each time this command fires the mouse will move by a
        /// set number of pixels", shipped configurator
        /// ControllerBinding_MouseDeltaModal_Desc, and corpus 3456927474
        /// carries "mouse_delta 100 0"). <see cref="TranslatedMacro.DeltaX"/> /
        /// <see cref="TranslatedMacro.DeltaY"/> are signed screen-space
        /// pixels (+x right, +y down, the SendInput MOUSEEVENTF_MOVE
        /// frame). Lowers to MacroActionType.MouseNudge, which enqueues
        /// the delta once into the engine's accumulate-and-flush mouse
        /// lane.</summary>
        MouseNudge = 19,

        /// <summary>Steam's Scroll Wheel List (v16): the wheel steps
        /// through <c>scroll_wheel_list_0..N</c>, firing the reached
        /// item's binding per detent ("You can assign a button or key to
        /// be sent to the game when the Nth item is reached", shipped
        /// configurator ControllerBinding_ScrollWheelListN_Description).
        /// <see cref="TranslatedMacro.CycleSteps"/> carries the items in
        /// VDF order and <see cref="TranslatedMacro.CycleWrap"/> Steam's
        /// scroll_wrap ("Wrap List"). Lowers to
        /// MacroActionType.CycleTapList, whose per-action index advances
        /// one step per fire.</summary>
        CycleList = 20,

        /// <summary>Latch / unlatch a mouse button (v18: the activator
        /// toggle on a mouse_button binding). Lowers to
        /// MacroActionType.ToggleMouseButton via the engine's
        /// mouse-button reconcile (the ToggleKey pattern).</summary>
        ToggleMouseButton = 21,

        /// <summary>Latch / unlatch an axis-natured VC target (v18: the
        /// toggle on a trigger-pull / stick-direction binding).
        /// <see cref="TranslatedMacro.TargetAxis"/> /
        /// <see cref="TranslatedMacro.TargetAxisNegative"/> name the
        /// target. Lowers to MacroActionType.ToggleVcAxis.</summary>
        ToggleVcAxis = 22,

        /// <summary>Turbo for an axis-natured VC target (v18: hold_repeats
        /// on a trigger-pull / stick-direction binding). Pulses the axis
        /// assert on the turbo square wave while held. Lowers to
        /// MacroActionType.RepeatVcAxisWhileHeld.</summary>
        RepeatVcAxisWhileHeld = 23,

        /// <summary>Latch / unlatch a continuous wheel scroll (v18: the
        /// toggle on a mouse_wheel binding): while latched, one detent per
        /// <see cref="TranslatedMacro.IntervalMs"/>, reproducing the held
        /// KbmScroll row. Lowers to MacroActionType.ToggleWheel.</summary>
        ToggleWheel = 24,

        /// <summary>Turbo for a mouse wheel (v19, T1: hold_repeats on a
        /// mouse_wheel binding): one discrete detent per
        /// <see cref="TranslatedMacro.IntervalMs"/> while the physical
        /// input is held, Steam's authored repeat_rate cadence. Lowers to
        /// MacroActionType.MouseWheelTap riding RepeatMode=UntilRelease
        /// with the interval as the repeat gap.</summary>
        RepeatWheelWhileHeld = 25,
    }

    /// <summary>Which one-shot form a <see cref="TranslatedCycleStep"/>
    /// fires. Mirrors the tap macro family: the reached list item is
    /// exactly one of the existing one-shot lowerings.</summary>
    public enum TranslatedCycleStepKind
    {
        /// <summary>Key tap via SendInput (<c>key_press</c>).
        /// <see cref="TranslatedCycleStep.VirtualKey"/>.</summary>
        KeyTap = 0,

        /// <summary>Mouse-button click (<c>mouse_button</c>).
        /// <see cref="TranslatedCycleStep.MouseButtonIndex"/>.</summary>
        MouseButtonTap = 1,

        /// <summary>Wheel detent (<c>mouse_wheel</c>).
        /// <see cref="TranslatedCycleStep.WheelTicks"/> /
        /// <see cref="TranslatedCycleStep.WheelHorizontal"/>.</summary>
        WheelTap = 2,

        /// <summary>Virtual-controller button tap (<c>xinput_button</c>).
        /// <see cref="TranslatedCycleStep.TargetXboxButtons"/>.</summary>
        VcButtonTap = 3,

        /// <summary>Axis-natured VC target tap (<c>xinput_button</c> onto
        /// a trigger pull or stick direction).
        /// <see cref="TranslatedCycleStep.TargetAxis"/> /
        /// <see cref="TranslatedCycleStep.TargetAxisNegative"/>.</summary>
        VcAxisTap = 4,
    }

    /// <summary>One Scroll Wheel List item (v16), device-independent. The
    /// materializer encodes the ordered list into the engine's
    /// CycleTapList step string.</summary>
    public sealed class TranslatedCycleStep
    {
        public TranslatedCycleStepKind Kind { get; set; }

        /// <summary>The scroll_wheel_list_N index this step came from. An
        /// item slot carrying several bindings contributes several steps
        /// with the same index. The materializer folds them into one
        /// detent stop so they fire together, Steam's reached-item
        /// semantics.</summary>
        public int ItemIndex { get; set; }

        /// <summary>Win32 VK for <see cref="TranslatedCycleStepKind.KeyTap"/>.</summary>
        public int VirtualKey { get; set; }

        /// <summary>0=Left 1=Right 2=Middle 3=X1 4=X2 for
        /// <see cref="TranslatedCycleStepKind.MouseButtonTap"/>.</summary>
        public int MouseButtonIndex { get; set; }

        /// <summary>Signed tick count for
        /// <see cref="TranslatedCycleStepKind.WheelTap"/> (positive = up /
        /// right).</summary>
        public int WheelTicks { get; set; } = 1;

        /// <summary>Horizontal-lane selector for
        /// <see cref="TranslatedCycleStepKind.WheelTap"/>.</summary>
        public bool WheelHorizontal { get; set; }

        /// <summary>Xbox button bitmask for
        /// <see cref="TranslatedCycleStepKind.VcButtonTap"/>.</summary>
        public ushort TargetXboxButtons { get; set; }

        /// <summary>XInputTarget name ("LeftTrigger", "LeftThumbAxisY",
        /// ...) for <see cref="TranslatedCycleStepKind.VcAxisTap"/>.</summary>
        public string TargetAxis { get; set; } = "";

        /// <summary>Direction for stick-axis targets, translator SDL row
        /// frame (true = up / left), the TranslatedMacro.TargetAxisNegative
        /// contract.</summary>
        public bool TargetAxisNegative { get; set; }
    }

    /// <summary>
    /// A device-independent macro description. Triggers come in two shapes:
    /// inputs with an Xbox output representation ride the Xbox VC slot's
    /// combined output (MacroTriggerSource.OutputController, cheaper and
    /// consume-capable), and everything else (paddles, touchpads, gyro)
    /// rides a device-free InputDevice descriptor trigger since wave 3
    /// (<see cref="TriggerInputDescriptors"/>): empty-guid trigger entries
    /// resolved against whichever device the user maps into the slot,
    /// exactly like the mapping rows' empty DeviceGuid contract.
    /// </summary>
    public sealed class TranslatedMacro
    {
        public string Name { get; set; } = "";

        public TranslatedMacroAction Action { get; set; }

        /// <summary>Device-free InputDevice trigger descriptors (wave 3).
        /// Non-empty means the macro triggers on the hosting physical
        /// input read directly through the engine's descriptor / gesture /
        /// raw-button entries with an empty device guid ("the device on
        /// the slot"); the combined-output fields below stay zero. Multiple
        /// entries AND together (a single-pad click plus its half's touch
        /// spot). The materializer converts each descriptor through the
        /// same picker path (MacroItem.TryBuildTriggerEntry) so imported
        /// macros round-trip the macro editor.</summary>
        public List<string> TriggerInputDescriptors { get; set; } = new();

        /// <summary>"OnPress" / "WhileHeld" / "OnRelease" / "HoldForMs"
        /// (MacroTriggerMode names). For <see cref="TranslatedMacroAction.MouseLimitRegion"/>
        /// "WhileHeld" is semantic (clamp engaged while the trigger holds);
        /// the materializer lowers it to an OnPress + OnRelease toggle pair.</summary>
        public string TriggerMode { get; set; } = "OnPress";

        /// <summary>Continuous-hold threshold in milliseconds for the
        /// "HoldForMs" trigger mode (a Long_Press activator's
        /// long_press_time; Steam's UI default is 500). 0 = not a hold
        /// trigger.</summary>
        public int TriggerHoldMs { get; set; }

        /// <summary>Double-press window in milliseconds for the
        /// "DoublePress" trigger mode (v17: a Double_Press activator's
        /// double_tap_time, the serializer's own key beside repeat_rate
        /// and long_press_time in steamclient.dll's token table). Absent
        /// in the corpus; Valve's shipped controller_base templates
        /// author 442, the default here. 0 = not a double-press
        /// trigger.</summary>
        public int TriggerDoublePressMs { get; set; }

        /// <summary>Xbox output button bitmask (Gamepad.* constants), or 0
        /// when the trigger is an axis (<see cref="TriggerAxisTarget"/>).</summary>
        public ushort TriggerXboxButtons { get; set; }

        /// <summary>The hosting physical input's own descriptor, stashed when
        /// the trigger was pointed at the combined Xbox output
        /// (<see cref="TriggerXboxButtons"/>). Not part of the emitted macro:
        /// it is the fallback the translator's finalize pass swaps in when it
        /// turns out NO emitted row feeds that output bit, which would leave
        /// the trigger permanently unreachable. Empty when the trigger never
        /// took the combined-output shape.</summary>
        public string TriggerFallbackDescriptor { get; set; } = "";

        /// <summary>The hosting input's AND-gate companion descriptor, if any,
        /// carried alongside <see cref="TriggerFallbackDescriptor"/> for the
        /// same finalize swap.</summary>
        public string TriggerFallbackGateDescriptor { get; set; } = "";

        /// <summary>Half-axis shape of the hosting input's own read (v12),
        /// carried beside the descriptor triggers. An axis-class descriptor
        /// converts to a trigger entry that reads the FULL axis by default,
        /// so a wedge-hosted macro (a stick-as-dpad member) would fire on
        /// any deflection of the whole axis. When true, the materializer
        /// stamps the axis-shaped entry as a half-axis read with
        /// <see cref="TriggerDescriptorInvert"/> selecting the half, the
        /// same MappingSource.HalfAxis/Invert contract the wedge row uses.</summary>
        public bool TriggerDescriptorHalfAxis { get; set; }

        /// <summary>Half selector for <see cref="TriggerDescriptorHalfAxis"/>:
        /// false reads the upper half (SDL positive: south and east wedges),
        /// true the lower half (north and west wedges).</summary>
        public bool TriggerDescriptorInvert { get; set; }

        /// <summary>Deadzone percent for the axis-shaped trigger entry
        /// (1..100), from the hosting source's own threshold (a group inner
        /// deadzone, the trigger click's 75). 0 keeps the entry default.</summary>
        public int TriggerDescriptorDeadZonePercent { get; set; }

        /// <summary>"LeftTrigger" / "RightTrigger" when the trigger input is
        /// an analog trigger; null/empty otherwise.</summary>
        public string TriggerAxisTarget { get; set; } = "";

        /// <summary>Axis trigger threshold percent (1..100).</summary>
        public int TriggerAxisThresholdPercent { get; set; } = 50;

        /// <summary>When true the trigger buttons are consumed (removed from
        /// the VC output while the macro fires), reproducing Steam Input's
        /// "this input is keyboard-natured, not pad-natured" behavior.</summary>
        public bool ConsumeTrigger { get; set; }

        /// <summary>MOUSE_POSITION x in Steam's normalized 0..65535 space.</summary>
        public int NormalizedX { get; set; }

        /// <summary>MOUSE_POSITION y in Steam's normalized 0..65535 space.</summary>
        public int NormalizedY { get; set; }

        /// <summary>Win32 virtual-key code for the key actions.</summary>
        public int VirtualKey { get; set; }

        /// <summary>Autofire interval, ms (RepeatKeyWhileHeld and
        /// RepeatVcButtonWhileHeld; Steam's repeat_rate is stored in ms).</summary>
        public int IntervalMs { get; set; } = 100;

        /// <summary>Xbox output button bitmask the VC-button ACTIONS write
        /// (RepeatVcButtonWhileHeld / ToggleVcButton / HoldVcButton).
        /// Distinct from <see cref="TriggerXboxButtons"/>, which is the
        /// physical input's identity on the combined output.</summary>
        public ushort TargetXboxButtons { get; set; }

        // ── MouseLimitRegion payload (mouse_region) ──

        /// <summary>Region center X as percent of the primary screen
        /// (Steam position_x; shipped configurator: "the on screen
        /// horizontal position that the region will be centered around",
        /// #Unit_Percent). Default 50 = centered.</summary>
        public int RegionXPercent { get; set; } = 50;

        /// <summary>Region center Y as percent of the primary screen
        /// (Steam position_y). Default 50 = centered.</summary>
        public int RegionYPercent { get; set; } = 50;

        /// <summary>Region size as percent of the screen per axis (Steam
        /// scale; corpus: 10 = a small corner minimap region, 100 = the
        /// whole screen). Default 100.</summary>
        public int RegionScalePercent { get; set; } = 100;

        // ── SetLightbarColor payload (set_led) ──

        /// <summary>LED red 0..255.</summary>
        public int LedR { get; set; }

        /// <summary>LED green 0..255.</summary>
        public int LedG { get; set; }

        /// <summary>LED blue 0..255.</summary>
        public int LedB { get; set; }

        /// <summary>LED brightness percent 0..100.</summary>
        public int LedBrightnessPercent { get; set; } = 100;

        /// <summary>LED saturation percent 0..100 (already normalized from
        /// the vintage 0..255 scale when needed).</summary>
        public int LedSaturationPercent { get; set; } = 100;

        /// <summary>set_led mode argument: 1 = set the user color,
        /// 0 = restore, 2 = restore-to-default (approximated as restore).</summary>
        public int LedSetting { get; set; } = 1;

        // ── v10 payloads ──

        /// <summary>Mouse button index for <see cref="TranslatedMacroAction.MouseButtonTap"/>
        /// and <see cref="TranslatedMacroAction.HoldMouseButton"/>:
        /// 0=Left 1=Right 2=Middle 3=X1 4=X2 (the MacroMouseButton order,
        /// same index the KbmMBtn{n} targets use).</summary>
        public int MouseButtonIndex { get; set; }

        /// <summary>Rumble strength percent for
        /// <see cref="TranslatedMacroAction.RumblePulse"/>, both motors.
        /// Steam stores haptic_intensity 1..3 (Low/Medium/High); the
        /// translator maps 1=33, 2=66, 3=100.</summary>
        public int RumbleStrengthPercent { get; set; } = 100;

        /// <summary>Activator delay_start in ms (v10 G5): a Delay step the
        /// materializer prepends before the PRESS-leg action. Steam's
        /// shipped strings: "The activator will wait for this period of
        /// time after the button has been pressed before activating."
        /// 0 = none. Rides one-shot macro shapes only; continuous shapes
        /// (autofire, VC holds, region clamps) keep the dropped note.</summary>
        public int DelayStartMs { get; set; }

        /// <summary>Activator delay_end in ms (v10 G5): a Delay step before
        /// the RELEASE-leg action ("wait ... after the button has been
        /// released before deactivating"). On single OnRelease-triggered
        /// macros the whole action IS the release leg; on the Hold* pairs
        /// it lands on the release twin.</summary>
        public int DelayEndMs { get; set; }

        // ── v15 payloads ──

        /// <summary>Virtual-controller axis the
        /// <see cref="TranslatedMacroAction.VcAxisTap"/> /
        /// <see cref="TranslatedMacroAction.HoldVcAxis"/> actions write:
        /// an <see cref="XInputTargetTable.XInputTarget.Target"/> name
        /// ("LeftTrigger", "LeftThumbAxisY", ...).</summary>
        public string TargetAxis { get; set; } = "";

        /// <summary>Direction for stick-axis targets, in the translator's
        /// SDL row frame (+X right, +Y down): true = the negative end
        /// (up / left), matching XInputTarget.StickAxisNegative. The
        /// materializer converts to the XInput thumb frame. Ignored for
        /// trigger targets (a pull has one direction).</summary>
        public bool TargetAxisNegative { get; set; }

        /// <summary>Signed wheel tick count for
        /// <see cref="TranslatedMacroAction.MouseWheelTap"/>
        /// (positive = up / right). One Steam detent = 1.</summary>
        public int WheelTicks { get; set; } = 1;

        /// <summary>MouseWheelTap lane selector: true = the horizontal
        /// (MOUSEEVENTF_HWHEEL) wheel.</summary>
        public bool WheelHorizontal { get; set; }

        // ── v16 payloads ──

        /// <summary>MOUSE_DELTA dx for
        /// <see cref="TranslatedMacroAction.MouseNudge"/>, signed pixels,
        /// +x right (screen space).</summary>
        public int DeltaX { get; set; }

        /// <summary>MOUSE_DELTA dy for
        /// <see cref="TranslatedMacroAction.MouseNudge"/>, signed pixels,
        /// +y down (screen space).</summary>
        public int DeltaY { get; set; }

        /// <summary>Ordered Scroll Wheel List items for
        /// <see cref="TranslatedMacroAction.CycleList"/> (VDF
        /// scroll_wheel_list_N order).</summary>
        public List<TranslatedCycleStep> CycleSteps { get; set; } = new();

        /// <summary>Steam's scroll_wrap for
        /// <see cref="TranslatedMacroAction.CycleList"/>: true wraps past
        /// the last item back to the first ("Wrap List - On"). False stops
        /// producing output past the end ("scrolling past an end won't
        /// generate any command").</summary>
        public bool CycleWrap { get; set; } = true;

        // ── v18 payloads ──

        /// <summary>Composes Steam's toggle + hold_repeats (v18): the
        /// latched contribution pulses on the turbo square wave (period
        /// <see cref="IntervalMs"/>) instead of holding solid. Read by the
        /// Toggle* latch actions.</summary>
        public bool PulseWhileLatched { get; set; }

        /// <summary>Explicit assert duration for the tap shapes
        /// (VcButtonTap / VcAxisTap), in ms. 0 keeps the engine's default
        /// tap length. Used by the delay_end release-extension twins: the
        /// target re-asserts for exactly this long on the release edge.</summary>
        public int TapDurationMs { get; set; }
    }
}
