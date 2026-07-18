using System;
using PadForge.Engine;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>The Steam Input physical slots a group can attach to
    /// (the first token of a <c>group_source_bindings</c> value).</summary>
    public enum SteamSlot
    {
        Unknown = 0,
        ButtonDiamond,
        Switch,
        Dpad,
        Joystick,
        RightJoystick,
        LeftTrackpad,
        RightTrackpad,
        CenterTrackpad,
        LeftTrigger,
        RightTrigger,
        Gyro,
    }

    /// <summary>Which horizontal half of a physical touchpad a Steam
    /// trackpad token addresses (#9 B-1). Single-pad controllers (DS4 /
    /// DualSense: SDL registers exactly one touchpad) split their pad into
    /// left_trackpad / right_trackpad halves; multi-pad controllers map
    /// each token to its own physical pad and stay <see cref="Whole"/>.</summary>
    public enum TrackpadHalf
    {
        Whole = 0,
        Left = 1,
        Right = 2,
    }

    /// <summary>
    /// One resolved physical input: the abstract source descriptor to emit
    /// (empty DeviceGuid, "first device on the slot") plus everything the
    /// translator needs to reason about it.
    /// </summary>
    public sealed class ResolvedSource
    {
        /// <summary>Abstract source descriptor ("Gamepad ButtonA",
        /// "Touchpad 1 Click", "Gyro Yaw", ...).</summary>
        public string Descriptor { get; init; } = "";

        public bool HalfAxis { get; init; }

        public bool Invert { get; init; }

        /// <summary>Axis-to-button deadzone percent; 0 = engine default.</summary>
        public int DeadZone { get; init; }

        /// <summary>Target this source's device automap already feeds
        /// ("ButtonA" for Gamepad ButtonA), or null when automap never maps
        /// it (paddles, touchpads, gyro).</summary>
        public string AutomapTarget { get; init; }

        /// <summary>Combined-Xbox-output bit for device-free macro triggers,
        /// or 0 when the input has no Xbox button representation.</summary>
        public ushort XboxButtonBit { get; init; }

        /// <summary>"LeftTrigger"/"RightTrigger" when the input is an analog
        /// trigger pull (device-free macro axis trigger); else null.</summary>
        public string MacroAxisTarget { get; init; }

        /// <summary>Non-null when the source is a gesture-gated read. The
        /// value names the Touchpad-tab feature family. Since v14 this is
        /// no longer a report concern (imported sets are Authoritative and
        /// the engine self-arms referenced gesture families at apply, so
        /// emitted rows are Clean). It still marks reads that cannot drive
        /// shift activators (see the translator's IsActivatorCapable).</summary>
        public string TrackpadFeature { get; init; }

        /// <summary>True for analog trigger pulls, where Soft_Press can be
        /// approximated by a lower activation deadzone.</summary>
        public bool IsAnalogTriggerPull { get; init; }

        /// <summary>Optional AND-companion descriptor: the emitted row (or
        /// device-free macro trigger) fires only while this second source is
        /// also active. Carries Steam's click-on-half semantics for
        /// single-pad controllers (#9 B-1): primary = the pad click, gate =
        /// the half's touch spot, so the left_trackpad click fires only
        /// while the finger sits on the left half.</summary>
        public string GateDescriptor { get; init; }

        /// <summary>Second AND companion (v26): a chord partner on a host
        /// whose primary gate slot is already spent (the single-pad wedge
        /// chord). Rides MappingSource.Gate2Descriptor on rows and a
        /// second ANDed trigger entry on device-free macros.</summary>
        public string Gate2Descriptor { get; init; }

        /// <summary>Reason key forcing Partial status on emitted rows for
        /// named approximations that are not feature-gated (B-19's quadrant
        /// collapse: four_buttons cells hosted on a touch surface share one
        /// contact bool). Null = no forced Partial.</summary>
        public string PartialReasonKey { get; init; }
    }

    /// <summary>
    /// Physical-slot atlas: (Steam slot, input name) to abstract PadForge
    /// source descriptors. Grounded on SourceCoercion.GamepadAliasTable for
    /// the Gamepad family (paddle order: RIGHT_PADDLE1=Paddle1,
    /// LEFT_PADDLE1=Paddle2, RIGHT_PADDLE2=Paddle3, LEFT_PADDLE2=Paddle4),
    /// the "Touchpad {p} Click / Finger {f} Down / Finger {f} X|Y" families,
    /// the touchpad gesture families ("Touchpad {p} DPadUp.." anchor D-pad,
    /// "Touchpad {p} TouchLeft/TouchRight" spots, "Touchpad {p} StickX/Y"),
    /// and the bare "Gyro Pitch/Yaw/Roll" descriptors.
    /// </summary>
    public static class PhysicalSlotResolver
    {
        public static SteamSlot ParseSlot(string token) => (token ?? "").Trim().ToLowerInvariant() switch
        {
            "button_diamond" => SteamSlot.ButtonDiamond,
            "switch" => SteamSlot.Switch,
            "dpad" => SteamSlot.Dpad,
            "joystick" => SteamSlot.Joystick,
            "left_joystick" => SteamSlot.Joystick,
            "right_joystick" => SteamSlot.RightJoystick,
            "left_trackpad" => SteamSlot.LeftTrackpad,
            "right_trackpad" => SteamSlot.RightTrackpad,
            "center_trackpad" => SteamSlot.CenterTrackpad,
            "left_trigger" => SteamSlot.LeftTrigger,
            "right_trigger" => SteamSlot.RightTrigger,
            "gyro" => SteamSlot.Gyro,
            _ => SteamSlot.Unknown,
        };

        /// <summary>PadForge touchpad index for a trackpad slot: left=0,
        /// right=1, center=2 (Steam Controller). -1 for non-trackpads.</summary>
        public static int TrackpadIndex(SteamSlot slot) => slot switch
        {
            SteamSlot.LeftTrackpad => 0,
            SteamSlot.RightTrackpad => 1,
            SteamSlot.CenterTrackpad => 2,
            _ => -1,
        };

        /// <summary>Per-controller-type touchpad index (#9 B-1): on
        /// single-pad controllers every trackpad token addresses the one
        /// physical pad (index 0, halves selected via
        /// <see cref="HalfFor"/>); multi-pad types keep the classic
        /// left=0 / right=1 mapping. center_trackpad always reads pad 0
        /// whole: the token means "the single central pad" in every
        /// grammar vintage (25 of 30 wild authors are controller_ps4;
        /// census 2026-07-18), no SDL device registers a third pad, and
        /// the non-PS authors are type-converted leftovers whose center
        /// sections should still drive whichever pad-bearing device the
        /// user maps, not skip.</summary>
        public static int TrackpadIndex(SteamSlot slot, bool singlePadTrackpads)
            => !IsTrackpad(slot) ? -1
             : singlePadTrackpads || slot == SteamSlot.CenterTrackpad ? 0
             : TrackpadIndex(slot);

        /// <summary>Which half of the physical pad a trackpad token
        /// addresses (#9 B-1). Only single-pad controllers split:
        /// left_trackpad = the left half, right_trackpad = the right half,
        /// center_trackpad = the whole pad. Multi-pad types (and
        /// non-trackpad slots) always read Whole.</summary>
        public static TrackpadHalf HalfFor(SteamSlot slot, bool singlePadTrackpads)
            => !singlePadTrackpads ? TrackpadHalf.Whole
             : slot switch
             {
                 SteamSlot.LeftTrackpad => TrackpadHalf.Left,
                 SteamSlot.RightTrackpad => TrackpadHalf.Right,
                 _ => TrackpadHalf.Whole,
             };

        /// <summary>Descriptor suffix selecting the engine's region-windowed
        /// finger reads ("Touchpad 0 Finger 0 X Left", #9 B-1).</summary>
        internal static string HalfSuffix(TrackpadHalf half) => half switch
        {
            TrackpadHalf.Left => " Left",
            TrackpadHalf.Right => " Right",
            _ => "",
        };

        /// <summary>The held-state touch spot covering a half
        /// (<c>"Touchpad {p} TouchLeft"</c> / <c>TouchRight</c>). Callers
        /// pass a real half, never Whole.</summary>
        internal static string HalfSpot(int padIdx, TrackpadHalf half)
            => $"Touchpad {padIdx} " + (half == TrackpadHalf.Left ? "TouchLeft" : "TouchRight");

        /// <summary>True for controller types whose SDL driver registers
        /// exactly ONE touchpad, so Steam's left_trackpad / right_trackpad
        /// tokens address halves of the same physical pad (#9 B-1). Ground
        /// truth in the SDL fork clone: SDL_hidapi_ps4.c:732 and
        /// SDL_hidapi_ps5.c:846 each add a single touchpad, while the
        /// multi-pad family registers two (SDL_hidapi_steam.c:1273-1274 for
        /// gordon, SDL_hidapi_steamdeck.c:420-421 for neptune,
        /// SDL_hidapi_steam_triton.c:584-585 for the SC 2026 family, whose
        /// configs carry controller_type "controller_triton" in the
        /// corpus). Typeless configs predate controller_type and are Steam
        /// Controller authored: multi-pad.</summary>
        public static bool UsesSinglePadTrackpads(string controllerType)
            => (controllerType ?? "").Trim().ToLowerInvariant() switch
            {
                "controller_ps4" => true,
                "controller_ps5" => true,
                // DualSense Edge: same SDL_hidapi_ps5.c driver, one
                // touchpad. The wild corpus authors the type
                // (controller_ps5_edge, e.g. 3485653841) and its ratchet
                // masks carry the center-pad bits, which only ground
                // through the single-pad routing.
                "controller_ps5_edge" => true,
                _ => false,
            };

        public static bool IsTrackpad(SteamSlot slot) => TrackpadIndex(slot) >= 0;

        public static bool IsStick(SteamSlot slot)
            => slot == SteamSlot.Joystick || slot == SteamSlot.RightJoystick;

        /// <summary>Feature-family names for gesture-gated sources
        /// (<see cref="ResolvedSource.TrackpadFeature"/> markers).</summary>
        public const string FeatureJoystickOutput = "Touchpad joystick output";
        public const string FeatureTouchSpots = "Touchpad touch spots";
        public const string FeatureSwipes = "Touchpad swipe gestures";
        public const string FeatureTaps = "Touchpad tap gestures";

        /// <summary>True for the Switch family, whose configs serialize the
        /// button diamond by NINTENDO LABEL (button_a = the A-labeled cap,
        /// physical EAST), not by position. Ground truth: Valve's shipped
        /// controller_switch_pro gamepad template is the label-identity
        /// diamond (button_a -> xinput A), while every positional-feel
        /// community Switch config in the corpus carries the crossed diamond
        /// (button_b -> xinput A: physical south emits A). PadForge's
        /// "Gamepad Button*" family is positional (SDL: RemapButton in
        /// SDL_hidapi_switch.c is identity for Pro/Joy-Con, so ButtonA =
        /// Button 0 = SOUTH), so these types need the label->position swap
        /// in <see cref="Resolve"/>. Family list mirrors Steam's
        /// controller_type vocabulary (switch2_pro included: same labels).</summary>
        public static bool UsesNintendoLabels(string controllerType)
            => (controllerType ?? "").Trim().ToLowerInvariant() switch
            {
                "controller_switch_pro" => true,
                "controller_switch2_pro" => true,
                "controller_switch_joycon_left" => true,
                "controller_switch_joycon_right" => true,
                "controller_switch_joycon_pair" => true,
                _ => false,
            };

        /// <summary>Resolves one named input within a group hosted on
        /// <paramref name="slot"/>. <paramref name="nintendoLabels"/>
        /// (from <see cref="UsesNintendoLabels"/> on the config's
        /// controller_type) folds label-named diamond members onto their
        /// physical positions before resolving, so every downstream
        /// consumer (rows, identity detection, activators, macro trigger
        /// bits) sees the positional source.
        /// <paramref name="singlePadTrackpads"/> (from
        /// <see cref="UsesSinglePadTrackpads"/>) routes trackpad tokens
        /// onto pad 0's halves (#9 B-1). Returns null when PadForge has
        /// no source for it (caller reports Skipped/UnknownPhysicalInput or
        /// a more specific reason).</summary>
        public static ResolvedSource Resolve(SteamSlot slot, string inputName, bool nintendoLabels,
            bool singlePadTrackpads = false)
        {
            string name = (inputName ?? "").Trim().ToLowerInvariant();
            switch (slot)
            {
                case SteamSlot.ButtonDiamond:
                    // Nintendo labels sit crossed against position:
                    // A=east, B=south, X=north, Y=west. Fold the label onto
                    // the position, then resolve positionally. Everything
                    // else on the diamond is already positional.
                    if (nintendoLabels)
                    {
                        name = name switch
                        {
                            "button_a" => "button_b",
                            "button_b" => "button_a",
                            "button_x" => "button_y",
                            "button_y" => "button_x",
                            _ => name,
                        };
                    }
                    return name switch
                    {
                        "button_a" => Btn("Gamepad ButtonA", "ButtonA", Gamepad.A),
                        "button_b" => Btn("Gamepad ButtonB", "ButtonB", Gamepad.B),
                        "button_x" => Btn("Gamepad ButtonX", "ButtonX", Gamepad.X),
                        "button_y" => Btn("Gamepad ButtonY", "ButtonY", Gamepad.Y),
                        // dpad mode hosted on the diamond (census
                        // 2026-07-18): the dpad_* members are positional
                        // seats, so each lands on the button at that seat.
                        // No label fold: dpad_* names are already
                        // positional in Steam's own serialization.
                        "dpad_north" => Btn("Gamepad ButtonY", "ButtonY", Gamepad.Y),
                        "dpad_south" => Btn("Gamepad ButtonA", "ButtonA", Gamepad.A),
                        "dpad_east" => Btn("Gamepad ButtonB", "ButtonB", Gamepad.B),
                        "dpad_west" => Btn("Gamepad ButtonX", "ButtonX", Gamepad.X),
                        _ => null,
                    };

                case SteamSlot.Switch:
                    // Diamond members ride switches groups in wild configs
                    // (mode_shift carriers and hand-merged sets; census
                    // 2026-07-18: button_a/b/x/y across 25+ configs). The
                    // serializer accepts them anywhere, so they resolve
                    // exactly as on the button_diamond slot, Nintendo label
                    // fold included.
                    if (nintendoLabels)
                    {
                        name = name switch
                        {
                            "button_a" => "button_b",
                            "button_b" => "button_a",
                            "button_x" => "button_y",
                            "button_y" => "button_x",
                            _ => name,
                        };
                    }
                    return name switch
                    {
                        "button_a" => Btn("Gamepad ButtonA", "ButtonA", Gamepad.A),
                        "button_b" => Btn("Gamepad ButtonB", "ButtonB", Gamepad.B),
                        "button_x" => Btn("Gamepad ButtonX", "ButtonX", Gamepad.X),
                        "button_y" => Btn("Gamepad ButtonY", "ButtonY", Gamepad.Y),
                        // Physical dpad directions as switch members (same
                        // hand-merged shape as the diamond members above).
                        "dpad_north" => Btn("Gamepad DPadUp", "DPadUp", Gamepad.DPAD_UP),
                        "dpad_south" => Btn("Gamepad DPadDown", "DPadDown", Gamepad.DPAD_DOWN),
                        "dpad_east" => Btn("Gamepad DPadRight", "DPadRight", Gamepad.DPAD_RIGHT),
                        "dpad_west" => Btn("Gamepad DPadLeft", "DPadLeft", Gamepad.DPAD_LEFT),
                        // Steam Input's names are template-era: button_escape
                        // is the Start/Menu button, button_menu is Back/View
                        // (ground truth: fixture switches groups bind them to
                        // "xinput_button start" / "xinput_button select").
                        "button_escape" => Btn("Gamepad ButtonStart", "ButtonStart", Gamepad.START),
                        "button_menu" => Btn("Gamepad ButtonBack", "ButtonBack", Gamepad.BACK),
                        "left_bumper" => Btn("Gamepad LeftShoulder", "LeftShoulder", Gamepad.LEFT_SHOULDER),
                        "right_bumper" => Btn("Gamepad RightShoulder", "RightShoulder", Gamepad.RIGHT_SHOULDER),
                        // Paddles: primary pair = SDL *_PADDLE1, upper pair =
                        // *_PADDLE2. No automap target and no Xbox output bit.
                        "button_back_left" => new ResolvedSource { Descriptor = "Gamepad Paddle2" },
                        "button_back_right" => new ResolvedSource { Descriptor = "Gamepad Paddle1" },
                        "button_back_left_upper" => new ResolvedSource { Descriptor = "Gamepad Paddle4" },
                        "button_back_right_upper" => new ResolvedSource { Descriptor = "Gamepad Paddle3" },
                        // Steam Controller pad clicks appear as switch
                        // members in SC-era configs (mode_shift carriers).
                        // Single-pad controllers (#9 B-1) have ONE physical
                        // click: the side is the finger's half, so the
                        // click gates on the half's touch spot.
                        // button_lpad / button_rpad are the same pad clicks
                        // under the serializer's older token pair (both in
                        // steamclient64.dll's own string table).
                        "left_click" => PadClick(TrackpadHalf.Left, singlePadTrackpads),
                        "right_click" => PadClick(TrackpadHalf.Right, singlePadTrackpads),
                        "button_lpad" => PadClick(TrackpadHalf.Left, singlePadTrackpads),
                        "button_rpad" => PadClick(TrackpadHalf.Right, singlePadTrackpads),
                        // Pad touch contact as a switch member (SC-era
                        // mode_shift carriers; serializer tokens
                        // left/right_trackpad_touch). Multi-pad types read
                        // the side's own contact; single-pad types (#9 B-1)
                        // read the half's touch spot.
                        "left_trackpad_touch" => PadTouch(TrackpadHalf.Left, singlePadTrackpads),
                        "right_trackpad_touch" => PadTouch(TrackpadHalf.Right, singlePadTrackpads),
                        // Stick clicks as switch members (serializer tokens
                        // left/right_stick_click, present in the shipped
                        // configurator JS and steamclient64's table).
                        "left_stick_click" => Btn("Gamepad LeftStick", "LeftThumbButton", Gamepad.LEFT_THUMB),
                        "right_stick_click" => Btn("Gamepad RightStick", "RightThumbButton", Gamepad.RIGHT_THUMB),
                        // Grip capsense as switch members (v26): Steam's
                        // CapSenseLeftAux / CapSenseRightAux
                        // (k_eGamepadButtonBitMask bits 44 / 45 in the
                        // shipped configurator JS), the SC 2026 grip
                        // handles, delivered by the fork's
                        // SDL_GetGamepadCapSense LEFT_GRIP / RIGHT_GRIP
                        // channels. Wild witness 3722524382 hosts layer
                        // verbs on button_leftauxcapsense.
                        "button_leftauxcapsense" => new ResolvedSource
                        {
                            Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.CapSenseLeftGripDescriptor,
                        },
                        "button_rightauxcapsense" => new ResolvedSource
                        {
                            Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.CapSenseRightGripDescriptor,
                        },
                        // Steam's "Always On Command" (the serializer's
                        // always_on_action member; shipped string
                        // ControllerBinding_SwitchesActionSetAlwaysOn, and
                        // Valve's controller_neptune_webbrowser template
                        // authors one): a virtual input held the whole time
                        // the hosting action set is active. Lowered onto
                        // the engine's constant-true read; set scoping
                        // rides the row's LayerMask / the macro layer gate.
                        "always_on_action" => new ResolvedSource
                        {
                            Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.AlwaysOnDescriptor,
                        },
                        // Soft-pull threshold crossings as switch members.
                        // Steam's "Soft Pull" binding point
                        // (ControllerBinding_TriggerAnalogThresholdBinding
                        // = "Soft Pull" in the shipped strings): same
                        // analog read as the trigger slot's "edge" member,
                        // the lowest useful threshold on the upper-half
                        // pull window.
                        "left_trigger_threshold" => TriggerSoftPull(left: true),
                        "right_trigger_threshold" => TriggerSoftPull(left: false),
                        // Steam Controller digital trigger pulls also ride
                        // switches groups (full-pull clicks, incl. mode_shift
                        // carriers). Ground truth: 1172518660 (Valve's TF2
                        // config) binds left_trigger/right_trigger to
                        // mouse buttons inside a switches group, and
                        // 770509247 carries a left_trigger mode_shift.
                        // Same shape as the trigger slot's "click" member.
                        "left_trigger" => TriggerClick(left: true),
                        "right_trigger" => TriggerClick(left: false),
                        // Share / Capture button (Steam Deck, Xbox Series,
                        // DualSense mic): SDL posts it as MISC1, which the
                        // wrapper fills into Buttons[11]
                        // (SdlDeviceWrapper.GetGamepadState). Raw descriptor
                        // on purpose: index 11 has no Gamepad alias, no
                        // automap target, and no Xbox output bit, the same
                        // paddle shape as button_back_*.
                        "button_capture" => new ResolvedSource { Descriptor = "Button 11" },
                        // Steam's assignable macro buttons (v24). The
                        // serializer tokens button_macro0..7 (steamclient64
                        // string table) are k_eGamepadButtonBitMask
                        // ButtonMacro0..7 = bits 32-39 in the shipped
                        // configurator (steamui/chunk~2dcc5aaf7.js), which
                        // gates each one on the matching device capability
                        // 1:1 (unCapabilities & ATTRIBCAP_MISCn_BUTTON ->
                        // ButtonMacroN): they are the device's Nth EXTRA
                        // button beyond the standard set and beyond the
                        // capture-shaped button, which Steam models
                        // separately (Ancillary1 / ATTRIBCAP_CAPTUREBUTTON)
                        // exactly as SDL reserves MISC1 for it. So Steam
                        // macro N is SDL misc N+2. Corpus witness: every
                        // physical button_macro author is a Flydigi Vader 5
                        // Pro (controller_caps 66578042879 = MISC0..4 set,
                        // five extra buttons), and SDL maps those same five
                        // to misc2..misc6 (SDL_gamepad.c Flydigi branch:
                        // "misc2:b15,misc3:b16" for C/Z plus the Vader-5
                        // "misc4:b17,misc5:b18,misc6:b19"); the mobile-touch
                        // overlay's on-screen macro buttons carry the same
                        // enum identity. The wrapper reads MISC2..MISC6 at
                        // raw Buttons[17..21] (SdlDeviceWrapper), the
                        // paddle shape: no automap target, no Xbox bit.
                        // macro5..7 and the mobile multi-finger taps
                        // (button_macro1finger/2finger, enum bits 48/49)
                        // exceed SDL's misc space: null keeps the named
                        // skip.
                        "button_macro0" => new ResolvedSource { Descriptor = "Button 17" },
                        "button_macro1" => new ResolvedSource { Descriptor = "Button 18" },
                        "button_macro2" => new ResolvedSource { Descriptor = "Button 19" },
                        "button_macro3" => new ResolvedSource { Descriptor = "Button 20" },
                        "button_macro4" => new ResolvedSource { Descriptor = "Button 21" },
                        _ => null, // touchpads-as-switch and friends: no family member
                    };

                case SteamSlot.Dpad:
                    // four_buttons hosted on the physical dpad (census
                    // 2026-07-18): diamond cells fold onto the direction at
                    // their seat (A=south, B=east, X=west, Y=north), the
                    // same seat fold the stick hosts use. Nintendo-labeled
                    // configs address cells by label, crossed against
                    // position, so the label fold runs first.
                    if (nintendoLabels)
                    {
                        name = name switch
                        {
                            "button_a" => "button_b",
                            "button_b" => "button_a",
                            "button_x" => "button_y",
                            "button_y" => "button_x",
                            _ => name,
                        };
                    }
                    name = name switch
                    {
                        "button_a" => "dpad_south",
                        "button_b" => "dpad_east",
                        "button_x" => "dpad_west",
                        "button_y" => "dpad_north",
                        _ => name,
                    };
                    return name switch
                    {
                        "dpad_north" => Btn("Gamepad DPadUp", "DPadUp", Gamepad.DPAD_UP),
                        "dpad_south" => Btn("Gamepad DPadDown", "DPadDown", Gamepad.DPAD_DOWN),
                        "dpad_east" => Btn("Gamepad DPadRight", "DPadRight", Gamepad.DPAD_RIGHT),
                        "dpad_west" => Btn("Gamepad DPadLeft", "DPadLeft", Gamepad.DPAD_LEFT),
                        // edge / click on a PHYSICAL dpad (v26): a dpad
                        // direction is a switch, always at full deflection
                        // when pressed and physically clicky, so both
                        // members ARE "any direction held": the POV
                        // any-direction read. scroll_clockwise /
                        // scroll_counterclockwise (a hand-authored
                        // scrollwheel on the dpad, one wild sighting) fold
                        // onto the rotation-side directions of the circular
                        // surface: the right side of a wheel moves down
                        // when it turns clockwise, so east = clockwise,
                        // west = counterclockwise.
                        "edge" => new ResolvedSource { Descriptor = "POV 0 Any" },
                        "click" => new ResolvedSource { Descriptor = "POV 0 Any" },
                        "scroll_clockwise" => new ResolvedSource { Descriptor = "POV 0 Right" },
                        "scroll_counterclockwise" => new ResolvedSource { Descriptor = "POV 0 Left" },
                        _ => null,
                    };

                case SteamSlot.Joystick:
                case SteamSlot.RightJoystick:
                {
                    bool left = slot == SteamSlot.Joystick;
                    string stick = left ? "LeftStick" : "RightStick";
                    // Stick-hosted diamond cells (four_buttons on a stick)
                    // fire on deflection toward the cell's seat, so each
                    // folds onto the matching wedge read below (A=south,
                    // B=east, X=west, Y=north). Nintendo-labeled configs
                    // address cells by label, crossed against position,
                    // same as the button_diamond slot.
                    if (nintendoLabels)
                    {
                        name = name switch
                        {
                            "button_a" => "button_b",
                            "button_b" => "button_a",
                            "button_x" => "button_y",
                            "button_y" => "button_x",
                            _ => name,
                        };
                    }
                    name = name switch
                    {
                        "button_a" => "dpad_south",
                        "button_b" => "dpad_east",
                        "button_x" => "dpad_west",
                        "button_y" => "dpad_north",
                        _ => name,
                    };
                    switch (name)
                    {
                        case "click":
                            return Btn($"Gamepad {stick}",
                                left ? "LeftThumbButton" : "RightThumbButton",
                                left ? Gamepad.LEFT_THUMB : Gamepad.RIGHT_THUMB);
                        // Stick-top capsense as a stick member (v26): the
                        // "touch" member on a joystick source is Steam's
                        // CapSenseLeftStick / CapSenseRightStick (enum bits
                        // 46 / 47), the fork's SDL_GetGamepadCapSense
                        // LEFT_STICK / RIGHT_STICK channels. Wild
                        // witnesses: 3705891700 (joystick_mouse touch),
                        // 3145222926 (radial_menu touch).
                        case "touch":
                            return new ResolvedSource
                            {
                                Descriptor = left
                                    ? PadForge.Engine.Common.Mapping.SourceCoercion.CapSenseLeftStickDescriptor
                                    : PadForge.Engine.Common.Mapping.SourceCoercion.CapSenseRightStickDescriptor,
                            };
                        // Stick-as-dpad: half-axis reads. SDL convention is
                        // +X right, +Y down, so north = Y lower half
                        // (HalfAxis+Invert), south = Y upper half, east = X
                        // upper half, west = X lower half.
                        case "dpad_north":
                            return new ResolvedSource { Descriptor = $"Gamepad {stick}Y", HalfAxis = true, Invert = true };
                        case "dpad_south":
                            return new ResolvedSource { Descriptor = $"Gamepad {stick}Y", HalfAxis = true };
                        case "dpad_east":
                            return new ResolvedSource { Descriptor = $"Gamepad {stick}X", HalfAxis = true };
                        case "dpad_west":
                            return new ResolvedSource { Descriptor = $"Gamepad {stick}X", HalfAxis = true, Invert = true };
                        default:
                            return null; // "edge" resolves in the member walk (v17 stick ring)
                    }
                }

                case SteamSlot.Gyro:
                {
                    // Gyro-hosted dpad / diamond members (v26): a
                    // gyro-hosted dpad reads sustained controller TILT
                    // (the gravity direction from the accelerometer), not
                    // the rotation rate, so the members lower onto the
                    // engine's gravity-lean pair with EXACTLY the
                    // stick-as-dpad wedge table: the lean signs are the
                    // physical stick's own frame by construction (Lean X
                    // positive = tilt right, Lean Y positive = nose up =
                    // stick pulled back), so north = tilt the top edge
                    // away (push the "stick" forward), south = tilt it
                    // back, east / west = tilt right / left. Diamond
                    // cells fold onto their seats first, the stick hosts'
                    // rule.
                    if (nintendoLabels)
                    {
                        name = name switch
                        {
                            "button_a" => "button_b",
                            "button_b" => "button_a",
                            "button_x" => "button_y",
                            "button_y" => "button_x",
                            _ => name,
                        };
                    }
                    name = name switch
                    {
                        "button_a" => "dpad_south",
                        "button_b" => "dpad_east",
                        "button_x" => "dpad_west",
                        "button_y" => "dpad_north",
                        _ => name,
                    };
                    string leanX = PadForge.Engine.Common.Mapping.SourceCoercion.GyroLeanXDescriptor;
                    string leanY = PadForge.Engine.Common.Mapping.SourceCoercion.GyroLeanYDescriptor;
                    return name switch
                    {
                        "dpad_north" => new ResolvedSource { Descriptor = leanY, HalfAxis = true, Invert = true },
                        "dpad_south" => new ResolvedSource { Descriptor = leanY, HalfAxis = true },
                        "dpad_east" => new ResolvedSource { Descriptor = leanX, HalfAxis = true },
                        "dpad_west" => new ResolvedSource { Descriptor = leanX, HalfAxis = true, Invert = true },
                        _ => null,
                    };
                }

                case SteamSlot.LeftTrackpad:
                case SteamSlot.RightTrackpad:
                case SteamSlot.CenterTrackpad:
                {
                    int p = TrackpadIndex(slot, singlePadTrackpads);
                    var half = HalfFor(slot, singlePadTrackpads);
                    switch (name)
                    {
                        case "click":
                            // Half-hosted click (#9 B-1): the pad's single
                            // physical click, gated on the half's touch
                            // spot so only the hosting side fires.
                            if (half != TrackpadHalf.Whole)
                            {
                                return new ResolvedSource
                                {
                                    Descriptor = $"Touchpad {p} Click",
                                    GateDescriptor = HalfSpot(p, half),
                                    TrackpadFeature = FeatureTouchSpots,
                                };
                            }
                            return new ResolvedSource { Descriptor = $"Touchpad {p} Click" };
                        case "touch":
                            // Half-hosted touch rides the held-state touch
                            // spot (the existing left/right split source).
                            if (half != TrackpadHalf.Whole)
                            {
                                return new ResolvedSource
                                {
                                    Descriptor = HalfSpot(p, half),
                                    TrackpadFeature = FeatureTouchSpots,
                                };
                            }
                            return new ResolvedSource { Descriptor = $"Touchpad {p} Finger 0 Down" };
                        // Trackpad-as-dpad rides the anchor-relative D-pad
                        // gesture channel ("Touchpad {p} DPadUp" etc., read
                        // through TouchpadGestureFiredProvider). The
                        // joystick-output feature self-arms at apply since
                        // v14, so emitted rows are Clean. On a half-hosted
                        // group the wedge is whole-pad (the anchor gesture
                        // has no half window); the translator adds one
                        // TrackpadHalfApproximated note per group.
                        case "dpad_north":
                            return TrackpadDpad(p, "DPadUp");
                        case "dpad_south":
                            return TrackpadDpad(p, "DPadDown");
                        case "dpad_east":
                            return TrackpadDpad(p, "DPadRight");
                        case "dpad_west":
                            return TrackpadDpad(p, "DPadLeft");
                        // B-19 (built in v18): four_buttons cells hosted on
                        // a touch surface read the diamond-quadrant contact
                        // windows ("Finger 0 Down North" and friends, the
                        // engine's |dy| vs |dx| check around the region
                        // center), exactly Steam's ABXY zone geometry.
                        // Half-hosted groups compose the quadrant with the
                        // half ("Down North Left": the quadrant test runs
                        // against the half's own center).
                        case "button_a":
                        case "button_b":
                        case "button_x":
                        case "button_y":
                        {
                            string quad = name switch
                            {
                                "button_y" => "North",
                                "button_a" => "South",
                                "button_b" => "East",
                                _ => "West",
                            };
                            return new ResolvedSource
                            {
                                Descriptor = $"Touchpad {p} Finger 0 Down {quad}{HalfSuffix(half)}",
                            };
                        }
                        // Mouse-mode "Double Tap" member ("A button can be
                        // sent when double tapping on the pad", shipped
                        // ControllerBinding_DoubleTap strings; census
                        // 2026-07-18: 70 wild sites on absolute_mouse
                        // groups). Same gesture read the Double_Press
                        // activator lowering uses: the pad's DoubleTap
                        // fire, self-armed at apply (v14).
                        case "doubletap":
                            return new ResolvedSource
                            {
                                Descriptor = $"Touchpad {p} DoubleTap",
                                TrackpadFeature = FeatureTaps,
                            };
                        default:
                            return null; // "edge" and menu cells resolve elsewhere
                    }
                }

                case SteamSlot.LeftTrigger:
                case SteamSlot.RightTrigger:
                {
                    bool left = slot == SteamSlot.LeftTrigger;
                    string axis = left ? "Gamepad LeftTrigger" : "Gamepad RightTrigger";
                    string target = left ? "LeftTrigger" : "RightTrigger";
                    switch (name)
                    {
                        case "click":
                            return TriggerClick(left);
                        case "edge":
                            // Soft-pull edge: same read at the lowest useful
                            // threshold (~57% physical pull; 50% is the floor
                            // of the upper-half read).
                            return new ResolvedSource
                            {
                                Descriptor = axis,
                                HalfAxis = true,
                                DeadZone = 15,
                                MacroAxisTarget = target,
                                IsAnalogTriggerPull = true,
                            };
                        default:
                            return null;
                    }
                }

                default:
                    return null;
            }
        }

        private static ResolvedSource TrackpadDpad(int p, string dir) => new()
        {
            Descriptor = $"Touchpad {p} {dir}",
            TrackpadFeature = FeatureJoystickOutput,
        };

        /// <summary>A switch-slot pad touch contact ("left_trackpad_touch" /
        /// "right_trackpad_touch"). Multi-pad controllers read the side's
        /// own contact bool; single-pad controllers (#9 B-1) read the
        /// half's held-state touch spot, the same source the trackpad
        /// slots' half-hosted "touch" member resolves to.</summary>
        private static ResolvedSource PadTouch(TrackpadHalf side, bool singlePadTrackpads)
        {
            if (singlePadTrackpads)
            {
                return new ResolvedSource
                {
                    Descriptor = HalfSpot(0, side),
                    TrackpadFeature = FeatureTouchSpots,
                };
            }
            return new ResolvedSource
            {
                Descriptor = side == TrackpadHalf.Left
                    ? "Touchpad 0 Finger 0 Down" : "Touchpad 1 Finger 0 Down",
            };
        }

        /// <summary>The soft-pull threshold crossing ("Soft Pull" in the
        /// shipped configurator strings), shared by the trigger slot's
        /// <c>edge</c> member and the <c>left/right_trigger_threshold</c>
        /// members of switches groups: the same analog read at the lowest
        /// useful threshold (~57% physical pull; 50% is the floor of the
        /// upper-half read).</summary>
        private static ResolvedSource TriggerSoftPull(bool left)
        {
            string target = left ? "LeftTrigger" : "RightTrigger";
            return new ResolvedSource
            {
                Descriptor = left ? "Gamepad LeftTrigger" : "Gamepad RightTrigger",
                HalfAxis = true,
                DeadZone = 15,
                MacroAxisTarget = target,
                IsAnalogTriggerPull = true,
            };
        }

        /// <summary>A switch-slot pad click ("left_click" / "right_click").
        /// Multi-pad controllers own one click per pad; single-pad
        /// controllers (#9 B-1) have ONE physical click whose side is the
        /// finger's half, so the click gates on that half's touch spot.</summary>
        private static ResolvedSource PadClick(TrackpadHalf side, bool singlePadTrackpads)
        {
            if (singlePadTrackpads)
            {
                return new ResolvedSource
                {
                    Descriptor = "Touchpad 0 Click",
                    GateDescriptor = HalfSpot(0, side),
                    TrackpadFeature = FeatureTouchSpots,
                };
            }
            return new ResolvedSource
            {
                Descriptor = side == TrackpadHalf.Left ? "Touchpad 0 Click" : "Touchpad 1 Click",
            };
        }

        /// <summary>Full-pull trigger switch, shared by the trigger slot's
        /// <c>click</c> member and the <c>left_trigger</c>/<c>right_trigger</c>
        /// members of switches groups. The button coercion of an "Axis N"
        /// source reads the UPPER half of the 0..65535 range, so the
        /// reachable physical-pull window is 50..100%; DeadZone 75 fires at
        /// roughly 87% pull (the end-of-travel click).</summary>
        private static ResolvedSource TriggerClick(bool left)
        {
            string target = left ? "LeftTrigger" : "RightTrigger";
            return new ResolvedSource
            {
                Descriptor = left ? "Gamepad LeftTrigger" : "Gamepad RightTrigger",
                HalfAxis = true,
                DeadZone = 75,
                AutomapTarget = target,
                MacroAxisTarget = target,
                IsAnalogTriggerPull = true,
            };
        }

        private static ResolvedSource Btn(string descriptor, string automapTarget, ushort bit) => new()
        {
            Descriptor = descriptor,
            AutomapTarget = automapTarget,
            XboxButtonBit = bit,
        };

        /// <summary>The surface press that engages a mouse_region group
        /// hosted on <paramref name="slot"/> (wave 2A): Steam activates the
        /// region while the hosting surface is touched. Trackpads engage on
        /// touch (whole pad: "Touchpad {p} Finger 0 Down"; a single-pad
        /// half: the half's touch spot, both device-free InputDevice macro
        /// triggers since wave 3) and trigger slots engage on the pull (the
        /// full-pull click read, whose MacroAxisTarget IS a device-free
        /// axis trigger). Sticks and gyro have no press-shaped engage
        /// surface: null.</summary>
        public static ResolvedSource RegionEngageSource(SteamSlot slot, bool singlePadTrackpads = false)
        {
            if (IsTrackpad(slot))
                return Resolve(slot, "touch", nintendoLabels: false, singlePadTrackpads);
            if (slot == SteamSlot.LeftTrigger || slot == SteamSlot.RightTrigger)
                return TriggerClick(left: slot == SteamSlot.LeftTrigger);
            // Stick hosts (v26): a stick mouse_region engages while the
            // stick is deflected, which is the v17 deflection-ring bool
            // (any deflection past the engage radius). 25 percent is the
            // engine's stick-engage neighborhood (the menu machinery's
            // EngageDeadzonePercent default, grounded there on
            // sc-controller's stick-menu thresholds).
            if (IsStick(slot))
            {
                return new ResolvedSource
                {
                    Descriptor = slot == SteamSlot.Joystick
                        ? "Gamepad LeftStickRing"
                        : "Gamepad RightStickRing",
                    HalfAxis = true,
                    DeadZone = 25,
                };
            }
            return null;
        }

        /// <summary>The Steam Link on-screen touch controls (v26): switch
        /// members that exist only on the mobile client's touch overlay.
        /// Macro Buttons 6-8 (serializer button_macro5..7, enum bits
        /// 37-39, capability-gated on ATTRIBCAP_MISC5..7) name buttons
        /// past SDL's whole gamepad surface (SDL_GAMEPAD_BUTTON_MISC6 =
        /// Steam macro 4 is the last misc slot, SDL_gamepad.h), and the
        /// One / Two Finger Tap pair (button_macro1finger / 2finger, enum
        /// bits 48 / 49, "One Finger Tap" / "Two Finger Tap" in the
        /// shipped strings, filed under the glyph map's eIgnore group)
        /// are touch-screen tap gestures. No physical controller PadForge
        /// can drive exposes any of them, so they get the precise
        /// mobile-only skip instead of the generic unknown-input net.</summary>
        public static bool IsMobileTouchOnlyToken(string name) => name switch
        {
            "button_macro5" or "button_macro6" or "button_macro7"
                or "button_macro1finger" or "button_macro2finger" => true,
            _ => false,
        };

        /// <summary>Mouse-delta axis pair for a mouse-mode group hosted on
        /// <paramref name="slot"/>: (x descriptor, y descriptor, family).
        /// Family: 0 = generic stick axes (per-source Sensitivity), 1 =
        /// touchpad finger axes (per-source Sensitivity since B-13 widened
        /// the predicate to them), 2 = gyro (per-source GyroSensitivity).
        /// Single-pad halves (#9 B-1) ride the region-windowed finger reads
        /// ("Touchpad 0 Finger 0 X Right"). Null when the slot has no
        /// analog surface.</summary>
        public static (string X, string Y, int Family)? MouseAxisPair(SteamSlot slot,
            bool singlePadTrackpads = false)
        {
            if (IsStick(slot))
            {
                string stick = slot == SteamSlot.Joystick ? "LeftStick" : "RightStick";
                return ($"Gamepad {stick}X", $"Gamepad {stick}Y", 0);
            }
            if (IsTrackpad(slot))
            {
                int p = TrackpadIndex(slot, singlePadTrackpads);
                string sfx = HalfSuffix(HalfFor(slot, singlePadTrackpads));
                return ($"Touchpad {p} Finger 0 X{sfx}", $"Touchpad {p} Finger 0 Y{sfx}", 1);
            }
            if (slot == SteamSlot.Gyro)
                return ("Gyro Yaw", "Gyro Pitch", 2);
            return null;
        }

        /// <summary>Absolute-pointer axis pair for a trackpad-hosted
        /// mouse_region group (#9 B-15): ("Touchpad {p} Pointer X{sfx}",
        /// "... Y{sfx}"). Single-pad halves ride the same region-window
        /// suffix the finger reads use, so a DualSense right_trackpad
        /// region maps the right half of the physical pad. Null for
        /// non-trackpad slots (a stick's deflection is not a position;
        /// those keep the wave-2A clamp approximation).</summary>
        public static (string X, string Y)? PointerAxisPair(SteamSlot slot,
            bool singlePadTrackpads = false)
        {
            if (!IsTrackpad(slot)) return null;
            int p = TrackpadIndex(slot, singlePadTrackpads);
            string sfx = HalfSuffix(HalfFor(slot, singlePadTrackpads));
            return ($"Touchpad {p} Pointer X{sfx}", $"Touchpad {p} Pointer Y{sfx}");
        }
    }
}
