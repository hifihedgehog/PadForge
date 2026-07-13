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

        /// <summary>Non-null when the source only fires after the user
        /// enables a Touchpad-tab feature on their device; value is the
        /// reason-arg naming the feature. Emitted rows get Partial status.</summary>
        public string TrackpadFeature { get; init; }

        /// <summary>True for analog trigger pulls, where Soft_Press can be
        /// approximated by a lower activation deadzone.</summary>
        public bool IsAnalogTriggerPull { get; init; }
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

        public static bool IsTrackpad(SteamSlot slot) => TrackpadIndex(slot) >= 0;

        public static bool IsStick(SteamSlot slot)
            => slot == SteamSlot.Joystick || slot == SteamSlot.RightJoystick;

        /// <summary>Feature-name reason args for gesture-gated sources.</summary>
        public const string FeatureJoystickOutput = "Touchpad joystick output";
        public const string FeatureTouchSpots = "Touchpad touch spots";

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
        /// bits) sees the positional source. Returns null when PadForge has
        /// no source for it (caller reports Skipped/UnknownPhysicalInput or
        /// a more specific reason).</summary>
        public static ResolvedSource Resolve(SteamSlot slot, string inputName, bool nintendoLabels)
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
                        _ => null,
                    };

                case SteamSlot.Switch:
                    return name switch
                    {
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
                        "left_click" => new ResolvedSource { Descriptor = "Touchpad 0 Click" },
                        "right_click" => new ResolvedSource { Descriptor = "Touchpad 1 Click" },
                        _ => null, // button_capture and friends: no family member
                    };

                case SteamSlot.Dpad:
                    return name switch
                    {
                        "dpad_north" => Btn("Gamepad DPadUp", "DPadUp", Gamepad.DPAD_UP),
                        "dpad_south" => Btn("Gamepad DPadDown", "DPadDown", Gamepad.DPAD_DOWN),
                        "dpad_east" => Btn("Gamepad DPadRight", "DPadRight", Gamepad.DPAD_RIGHT),
                        "dpad_west" => Btn("Gamepad DPadLeft", "DPadLeft", Gamepad.DPAD_LEFT),
                        _ => null,
                    };

                case SteamSlot.Joystick:
                case SteamSlot.RightJoystick:
                {
                    bool left = slot == SteamSlot.Joystick;
                    string stick = left ? "LeftStick" : "RightStick";
                    switch (name)
                    {
                        case "click":
                            return Btn($"Gamepad {stick}",
                                left ? "LeftThumbButton" : "RightThumbButton",
                                left ? Gamepad.LEFT_THUMB : Gamepad.RIGHT_THUMB);
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
                            return null; // "edge" has no PadForge primitive
                    }
                }

                case SteamSlot.LeftTrackpad:
                case SteamSlot.RightTrackpad:
                case SteamSlot.CenterTrackpad:
                {
                    int p = TrackpadIndex(slot);
                    switch (name)
                    {
                        case "click":
                            return new ResolvedSource { Descriptor = $"Touchpad {p} Click" };
                        case "touch":
                            return new ResolvedSource { Descriptor = $"Touchpad {p} Finger 0 Down" };
                        // Trackpad-as-dpad rides the anchor-relative D-pad
                        // gesture channel ("Touchpad {p} DPadUp" etc., read
                        // through TouchpadGestureFiredProvider). It needs the
                        // per-device "joystick output" Touchpad-tab toggle,
                        // so emitted rows are Partial.
                        case "dpad_north":
                            return TrackpadDpad(p, "DPadUp");
                        case "dpad_south":
                            return TrackpadDpad(p, "DPadDown");
                        case "dpad_east":
                            return TrackpadDpad(p, "DPadRight");
                        case "dpad_west":
                            return TrackpadDpad(p, "DPadLeft");
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
                            // Full-pull switch. The button coercion of an
                            // "Axis N" source reads the UPPER half of the
                            // 0..65535 range, so the reachable physical-pull
                            // window is 50..100%; DeadZone 75 fires at
                            // roughly 87% pull (the end-of-travel click).
                            return new ResolvedSource
                            {
                                Descriptor = axis,
                                HalfAxis = true,
                                DeadZone = 75,
                                AutomapTarget = target,
                                MacroAxisTarget = target,
                                IsAnalogTriggerPull = true,
                            };
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

                case SteamSlot.Gyro:
                default:
                    return null;
            }
        }

        /// <summary>Two-cell touch menus map onto the held-state touch
        /// spots (left/right split). Cell 0 = left, cell 1 = right.</summary>
        public static ResolvedSource TouchMenuSpot(int trackpadIndex, int cellIndex)
            => cellIndex switch
            {
                0 => new ResolvedSource
                {
                    Descriptor = $"Touchpad {trackpadIndex} TouchLeft",
                    TrackpadFeature = FeatureTouchSpots,
                },
                1 => new ResolvedSource
                {
                    Descriptor = $"Touchpad {trackpadIndex} TouchRight",
                    TrackpadFeature = FeatureTouchSpots,
                },
                _ => null,
            };

        private static ResolvedSource TrackpadDpad(int p, string dir) => new()
        {
            Descriptor = $"Touchpad {p} {dir}",
            TrackpadFeature = FeatureJoystickOutput,
        };

        private static ResolvedSource Btn(string descriptor, string automapTarget, ushort bit) => new()
        {
            Descriptor = descriptor,
            AutomapTarget = automapTarget,
            XboxButtonBit = bit,
        };

        /// <summary>Mouse-delta axis pair for a mouse-mode group hosted on
        /// <paramref name="slot"/>: (x descriptor, y descriptor, family).
        /// Family: 0 = generic stick axes (per-source Sensitivity), 1 =
        /// touchpad finger axes (tuning lives on the Touchpad tab, not the
        /// row), 2 = gyro (per-source GyroSensitivity). Null when the slot
        /// has no analog surface.</summary>
        public static (string X, string Y, int Family)? MouseAxisPair(SteamSlot slot)
        {
            if (IsStick(slot))
            {
                string stick = slot == SteamSlot.Joystick ? "LeftStick" : "RightStick";
                return ($"Gamepad {stick}X", $"Gamepad {stick}Y", 0);
            }
            if (IsTrackpad(slot))
            {
                int p = TrackpadIndex(slot);
                return ($"Touchpad {p} Finger 0 X", $"Touchpad {p} Finger 0 Y", 1);
            }
            if (slot == SteamSlot.Gyro)
                return ("Gyro Yaw", "Gyro Pitch", 2);
            return null;
        }
    }
}
